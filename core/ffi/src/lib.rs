//! C ABI facade over [`lichess_puzzles_core::service::Service`] for the WinUI shell.
//!
//! Contract (ABI version 1):
//!
//! - Commands and queries are protobuf `Request` messages passed to `lpz_call`; the reply is a
//!   `Response` held in a Rust-owned result handle. Bytes from `lpz_result_bytes` stay valid
//!   until `lpz_result_free`.
//! - Status codes report transport outcomes only. Domain failures are `Response.error`.
//! - `lpz_create` sets `*out_core` to null on failure and, when possible, sets `*out_error` to a
//!   result holding a `Response` with an `Error`. Every other output is null on failure.
//! - A core handle may be used from any thread, including concurrently. Filter and puzzle queries
//!   scan the store (about 15 ms); everything else returns at once. Downloads and builds run on a
//!   core thread and report through `GetStatus` and `lpz_poll_events`.
//! - After a contained panic the handle is unusable: further calls return `LPZ_UNUSABLE`;
//!   `lpz_destroy` is still valid.
//! - `lpz_destroy` stops a running operation (a download stays resumable) and waits for it.
//! - Memory is freed only by the side that allocated it.

pub mod convert;

#[allow(clippy::all)]
pub mod messages {
    include!(concat!(env!("OUT_DIR"), "/jupiter.lichess_puzzles.v1.rs"));
}

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};

use lichess_puzzles_core::export::ExportFormat;
use lichess_puzzles_core::service::{ExportSource, Service, ServiceConfig, ServiceError, SessionCommand};
use lichess_puzzles_core::source::PUZZLE_URL;
use prost::Message;

use messages::{ErrorKind, request, response, session_command};

pub const ABI_VERSION: u32 = 1;

pub const LPZ_OK: i32 = 0;
pub const LPZ_INVALID_ARGUMENT: i32 = 1;
pub const LPZ_MALFORMED_REQUEST: i32 = 2;
pub const LPZ_PANIC: i32 = 3;
pub const LPZ_UNUSABLE: i32 = 4;
pub const LPZ_CREATE_FAILED: i32 = 5;

pub struct CoreHandle {
    service: Service,
    poisoned: AtomicBool,
}

pub struct ResultHandle {
    bytes: Vec<u8>,
}

fn result(message: &impl Message) -> *mut ResultHandle {
    Box::into_raw(Box::new(ResultHandle { bytes: message.encode_to_vec() }))
}

fn error_response(request_id: u64, kind: ErrorKind, message: impl Into<String>) -> messages::Response {
    messages::Response {
        request_id,
        result: Some(response::Result::Error(messages::Error { kind: kind as i32, message: message.into() })),
    }
}

fn service_error(request_id: u64, e: ServiceError) -> messages::Response {
    let kind = match &e {
        ServiceError::NoStore => ErrorKind::NoStore,
        ServiceError::Busy => ErrorKind::Busy,
        ServiceError::NotFound(_) => ErrorKind::NotFound,
        ServiceError::NoMatches => ErrorKind::NoMatches,
        ServiceError::Filter(_) => ErrorKind::InvalidFilter,
        ServiceError::Session(_) => ErrorKind::State,
        ServiceError::Store(_) | ServiceError::Io(_) => ErrorKind::Io,
        ServiceError::Player(_) => ErrorKind::Player,
    };
    error_response(request_id, kind, e.to_string())
}

/// Reads `len` bytes at `ptr`; a null pointer is valid only with length zero.
///
/// # Safety
/// `ptr` must be valid for reads of `len` bytes for the duration of the call.
unsafe fn input<'a>(ptr: *const u8, len: usize) -> Option<&'a [u8]> {
    if len == 0 {
        return Some(&[]);
    }
    if ptr.is_null() {
        return None;
    }
    // SAFETY: the caller guarantees `ptr` is readable for `len` bytes during this call.
    Some(unsafe { std::slice::from_raw_parts(ptr, len) })
}

/// Dispatches one decoded request against the service.
pub fn dispatch(service: &Service, req: messages::Request) -> messages::Response {
    let id = req.request_id;
    let ok = |r: response::Result| messages::Response { request_id: id, result: Some(r) };
    let done = || ok(response::Result::Done(messages::Done {}));
    let finish = |r: Result<(), ServiceError>| match r {
        Ok(()) => done(),
        Err(e) => service_error(id, e),
    };
    let session = |r: Result<_, ServiceError>| match r {
        Ok(result) => ok(response::Result::Session(convert::session_to_message(&result))),
        Err(e) => service_error(id, e),
    };
    let player = |r: Result<_, ServiceError>| match r {
        Ok(p) => ok(response::Result::Player(convert::player_to_message(&p))),
        Err(e) => service_error(id, e),
    };
    use request::Command as C;
    match req.command {
        Some(C::GetStatus(_)) => ok(response::Result::Status(convert::status_to_message(&service.status()))),
        Some(C::GetCatalogue(_)) => match service.catalogue() {
            Ok(c) => ok(response::Result::Catalogue(convert::catalogue_to_message(&c))),
            Err(e) => service_error(id, e),
        },
        Some(C::SetFilter(f)) => {
            let filter = convert::filter_from_message(&f.filter.unwrap_or_default());
            match service.set_filter(filter, f.skip_played) {
                Ok(counts) => ok(response::Result::Counts(convert::counts_to_message(&counts))),
                Err(e) => service_error(id, e),
            }
        }
        Some(C::NextPuzzle(n)) => session(service.next_puzzle(n.near_rating.then_some(n.offset))),
        Some(C::LoadPuzzle(p)) => session(service.load_puzzle(&p.id, p.replay)),
        Some(C::Session(s)) => {
            let command = match s.command {
                Some(session_command::Command::Get(_)) => SessionCommand::Get,
                Some(session_command::Command::PlayOpponent(_)) => SessionCommand::PlayOpponent,
                Some(session_command::Command::MakeMove(uci)) => SessionCommand::MakeMove(uci),
                Some(session_command::Command::Hint(_)) => SessionCommand::Hint,
                Some(session_command::Command::ShowSolution(_)) => SessionCommand::ShowSolution,
                Some(session_command::Command::Retry(_)) => SessionCommand::Retry,
                Some(session_command::Command::Navigate(ply)) => SessionCommand::Navigate(ply as usize),
                None => return error_response(id, ErrorKind::UnknownCommand, "Unknown or missing session command."),
            };
            session(service.session(command))
        }
        Some(C::StartDownload(d)) => finish(service.start_download(d.keep_download)),
        Some(C::StartBuild(b)) => {
            let input = (!b.input.is_empty()).then(|| PathBuf::from(b.input));
            finish(service.start_build(input, b.keep_download))
        }
        Some(C::CheckForUpdate(_)) => finish(service.check_for_update()),
        Some(C::PauseDownload(_)) => {
            service.pause_download();
            done()
        }
        Some(C::CancelOperation(_)) => {
            service.cancel_operation();
            done()
        }
        Some(C::SwitchStore(_)) => finish(service.switch_store()),
        Some(C::GetPlayers(g)) => match service.players(g.day_start_ms) {
            Ok(info) => ok(response::Result::Players(convert::players_to_message(&info))),
            Err(e) => service_error(id, e),
        },
        Some(C::SetCurrentPlayer(p)) => finish(service.set_current_player(p.id)),
        Some(C::AddPlayer(p)) => player(service.add_player(&p.name)),
        Some(C::RenamePlayer(p)) => player(service.rename_player(p.id, &p.name)),
        Some(C::DeletePlayer(p)) => finish(service.delete_player(p.id)),
        Some(C::ClearHistory(p)) => finish(service.clear_history(p.id).map(|_| ())),
        Some(C::SetRated(r)) => {
            service.set_rated(r.rated);
            done()
        }
        Some(C::GetHistory(h)) => match service.history(if h.limit == 0 { 100_000 } else { h.limit as usize }) {
            Ok(entries) => ok(response::Result::History(convert::history_to_message(&entries))),
            Err(e) => service_error(id, e),
        },
        Some(C::GetPuzzlePgn(p)) => match service.puzzle_pgn(p.player_start) {
            Ok(text) => ok(response::Result::Text(messages::Text { text })),
            Err(e) => service_error(id, e),
        },
        Some(C::StartExport(x)) => {
            let source = match messages::ExportSource::try_from(x.source) {
                Ok(messages::ExportSource::Played) => ExportSource::Played,
                _ => ExportSource::Matching,
            };
            let format = match messages::ExportFormat::try_from(x.format) {
                Ok(messages::ExportFormat::Csv) => ExportFormat::Csv,
                Ok(messages::ExportFormat::Epd) => ExportFormat::Epd,
                _ => ExportFormat::Pgn,
            };
            if x.path.is_empty() {
                return error_response(id, ErrorKind::Io, "No export file was given.");
            }
            match service.start_export(source, format, x.player_start, PathBuf::from(x.path)) {
                Ok(count) => ok(response::Result::Started(messages::Started { count })),
                Err(e) => service_error(id, e),
            }
        }
        Some(C::StartAnalysis(a)) => {
            let millis = u64::from(a.millis.clamp(100, 60_000));
            finish(service.start_analysis(&a.fen, std::time::Duration::from_millis(millis)))
        }
        Some(C::GetAnalysis(_)) => ok(response::Result::Analysis(convert::analysis_to_message(&service.analysis()))),
        Some(C::StopAnalysis(_)) => {
            service.stop_analysis();
            done()
        }
        None => error_response(id, ErrorKind::UnknownCommand, "Unknown or missing command."),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn lpz_abi_version() -> u32 {
    ABI_VERSION
}

/// Creates a core from a serialized `CreateConfig`.
///
/// # Safety
/// `config_ptr` must be readable for `config_len` bytes. `out_core` must be a valid pointer;
/// `out_error` may be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_create(
    config_ptr: *const u8,
    config_len: usize,
    out_core: *mut *mut CoreHandle,
    out_error: *mut *mut ResultHandle,
) -> i32 {
    if out_core.is_null() {
        return LPZ_INVALID_ARGUMENT;
    }
    // SAFETY: checked non-null; the caller provides a writable pointer.
    unsafe { *out_core = std::ptr::null_mut() };
    if !out_error.is_null() {
        // SAFETY: non-null and writable per the contract.
        unsafe { *out_error = std::ptr::null_mut() };
    }
    let outcome = catch_unwind(|| {
        // SAFETY: forwarded caller guarantee.
        let bytes = unsafe { input(config_ptr, config_len) }.ok_or((LPZ_INVALID_ARGUMENT, None))?;
        let config = messages::CreateConfig::decode(bytes).map_err(|_| (LPZ_MALFORMED_REQUEST, None))?;
        if config.data_dir.is_empty() {
            return Err((
                LPZ_INVALID_ARGUMENT,
                Some(Box::new(error_response(0, ErrorKind::Io, "No data folder was given."))),
            ));
        }
        let source_url = if config.source_url.is_empty() { PUZZLE_URL.to_owned() } else { config.source_url };
        let default_player = if config.default_player.is_empty() { "Player".to_owned() } else { config.default_player };
        let service =
            Service::open(ServiceConfig { data_dir: PathBuf::from(config.data_dir), source_url, default_player })
                .map_err(|e| (LPZ_CREATE_FAILED, Some(Box::new(service_error(0, e)))))?;
        Ok(Box::into_raw(Box::new(CoreHandle { service, poisoned: AtomicBool::new(false) })))
    });
    match outcome {
        Ok(Ok(handle)) => {
            // SAFETY: checked non-null above.
            unsafe { *out_core = handle };
            LPZ_OK
        }
        Ok(Err((status, error))) => {
            if let (Some(e), false) = (error, out_error.is_null()) {
                // SAFETY: checked non-null.
                unsafe { *out_error = result(e.as_ref()) };
            }
            status
        }
        Err(_) => LPZ_PANIC,
    }
}

/// Sends a serialized `Request`; on `LPZ_OK`, `*out_result` holds a `Response`.
///
/// # Safety
/// `core` must come from `lpz_create` and not be destroyed. `request_ptr` must be readable for
/// `request_len` bytes. `out_result` must be a valid pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_call(
    core: *const CoreHandle,
    request_ptr: *const u8,
    request_len: usize,
    out_result: *mut *mut ResultHandle,
) -> i32 {
    if core.is_null() || out_result.is_null() {
        return LPZ_INVALID_ARGUMENT;
    }
    // SAFETY: checked non-null; writable per the contract.
    unsafe { *out_result = std::ptr::null_mut() };
    // SAFETY: the caller guarantees a live handle from lpz_create.
    let handle = unsafe { &*core };
    if handle.poisoned.load(Ordering::Acquire) {
        return LPZ_UNUSABLE;
    }
    let outcome = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: forwarded caller guarantee.
        let bytes = unsafe { input(request_ptr, request_len) }.ok_or(LPZ_INVALID_ARGUMENT)?;
        let req = messages::Request::decode(bytes).map_err(|_| LPZ_MALFORMED_REQUEST)?;
        Ok(result(&dispatch(&handle.service, req)))
    }));
    match outcome {
        Ok(Ok(r)) => {
            // SAFETY: checked non-null above.
            unsafe { *out_result = r };
            LPZ_OK
        }
        Ok(Err(status)) => status,
        Err(_) => {
            handle.poisoned.store(true, Ordering::Release);
            LPZ_PANIC
        }
    }
}

/// Removes up to `max` pending events; `*out_result` holds an `EventBatch`.
///
/// # Safety
/// As for `lpz_call`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_poll_events(core: *const CoreHandle, max: u32, out_result: *mut *mut ResultHandle) -> i32 {
    if core.is_null() || out_result.is_null() {
        return LPZ_INVALID_ARGUMENT;
    }
    // SAFETY: checked non-null; writable per the contract.
    unsafe { *out_result = std::ptr::null_mut() };
    // SAFETY: the caller guarantees a live handle from lpz_create.
    let handle = unsafe { &*core };
    if handle.poisoned.load(Ordering::Acquire) {
        return LPZ_UNUSABLE;
    }
    match catch_unwind(AssertUnwindSafe(|| {
        let events = handle.service.poll_events(max as usize);
        result(&messages::EventBatch { events: events.iter().map(convert::event_to_message).collect() })
    })) {
        Ok(r) => {
            // SAFETY: checked non-null above.
            unsafe { *out_result = r };
            LPZ_OK
        }
        Err(_) => {
            handle.poisoned.store(true, Ordering::Release);
            LPZ_PANIC
        }
    }
}

/// Exposes a result's bytes. Valid until `lpz_result_free`.
///
/// # Safety
/// `result` must come from this library and not be freed; `out_ptr` and `out_len` must be valid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_result_bytes(
    result: *const ResultHandle,
    out_ptr: *mut *const u8,
    out_len: *mut usize,
) -> i32 {
    if result.is_null() || out_ptr.is_null() || out_len.is_null() {
        return LPZ_INVALID_ARGUMENT;
    }
    // SAFETY: all pointers checked non-null; the caller guarantees `result` is live.
    unsafe {
        let r = &*result;
        *out_ptr = r.bytes.as_ptr();
        *out_len = r.bytes.len();
    }
    LPZ_OK
}

/// Frees a result. Null is ignored.
///
/// # Safety
/// `result` must come from this library and must not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_result_free(result: *mut ResultHandle) {
    if !result.is_null() {
        // SAFETY: ownership returns to Rust exactly once per the contract.
        drop(unsafe { Box::from_raw(result) });
    }
}

/// Stops a running operation, waits for it and frees the core. Null is ignored.
///
/// # Safety
/// `core` must come from `lpz_create`, must not be in use by another call, and must not be used
/// afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn lpz_destroy(core: *mut CoreHandle) {
    if core.is_null() {
        return;
    }
    // SAFETY: ownership returns to Rust exactly once per the contract.
    let handle = unsafe { Box::from_raw(core) };
    let _ = catch_unwind(AssertUnwindSafe(move || {
        handle.service.shutdown();
        drop(handle);
    }));
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/../domain/tests/fixtures/sample_puzzles.csv");

    fn call(core: *const CoreHandle, command: request::Command) -> messages::Response {
        let req = messages::Request { request_id: 42, command: Some(command) };
        let bytes = req.encode_to_vec();
        let mut out = std::ptr::null_mut();
        let status = unsafe { lpz_call(core, bytes.as_ptr(), bytes.len(), &mut out) };
        assert_eq!(status, LPZ_OK);
        let (mut ptr, mut len) = (std::ptr::null(), 0usize);
        assert_eq!(unsafe { lpz_result_bytes(out, &mut ptr, &mut len) }, LPZ_OK);
        let resp = messages::Response::decode(unsafe { std::slice::from_raw_parts(ptr, len) }).unwrap();
        unsafe { lpz_result_free(out) };
        assert_eq!(resp.request_id, 42);
        resp
    }

    fn status(core: *const CoreHandle) -> messages::Status {
        match call(core, request::Command::GetStatus(Default::default())).result {
            Some(response::Result::Status(s)) => s,
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn builds_filters_and_plays_through_the_abi() {
        assert_eq!(lpz_abi_version(), ABI_VERSION);
        let dir = tempfile::tempdir().unwrap();
        let config = messages::CreateConfig {
            data_dir: dir.path().to_string_lossy().into(),
            source_url: "http://127.0.0.1:9/unused".into(),
            default_player: "Tester".into(),
        }
        .encode_to_vec();
        let mut core = std::ptr::null_mut();
        let mut err = std::ptr::null_mut();
        assert_eq!(unsafe { lpz_create(config.as_ptr(), config.len(), &mut core, &mut err) }, LPZ_OK);
        assert!(err.is_null());
        assert_eq!(status(core).store_problem, "No puzzles are installed.");

        let resp = call(core, request::Command::NextPuzzle(Default::default()));
        let Some(response::Result::Error(e)) = resp.result else { panic!("{resp:?}") };
        assert_eq!(e.kind, ErrorKind::NoStore as i32);

        let build = messages::StartBuild { input: SAMPLE.into(), keep_download: true };
        assert!(matches!(call(core, request::Command::StartBuild(build)).result, Some(response::Result::Done(_))));
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
        while status(core).operation.is_some() {
            assert!(std::time::Instant::now() < deadline);
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        let mut events = std::ptr::null_mut();
        assert_eq!(unsafe { lpz_poll_events(core, 100, &mut events) }, LPZ_OK);
        let (mut ptr, mut len) = (std::ptr::null(), 0usize);
        assert_eq!(unsafe { lpz_result_bytes(events, &mut ptr, &mut len) }, LPZ_OK);
        let batch = messages::EventBatch::decode(unsafe { std::slice::from_raw_parts(ptr, len) }).unwrap();
        unsafe { lpz_result_free(events) };
        assert_eq!(batch.events[0].level, messages::EventLevel::Success as i32);
        assert_eq!(status(core).store.unwrap().puzzle_count, 13);

        let resp = call(core, request::Command::GetCatalogue(Default::default()));
        let Some(response::Result::Catalogue(catalogue)) = resp.result else { panic!("{resp:?}") };
        assert_eq!(catalogue.themes.len(), 19);

        let filter = messages::Filter { themes_any: vec!["mate".into()], ..Default::default() };
        let resp =
            call(core, request::Command::SetFilter(messages::SetFilter { filter: Some(filter), skip_played: false }));
        let Some(response::Result::Counts(counts)) = resp.result else { panic!("{resp:?}") };
        assert_eq!(counts.total, 2);

        let resp = call(core, request::Command::LoadPuzzle(messages::LoadPuzzle { id: "0000H".into(), replay: false }));
        let Some(response::Result::Session(s)) = resp.result else { panic!("{resp:?}") };
        let view = s.view.unwrap();
        assert_eq!(view.state, messages::SessionState::OpponentToMove as i32);
        assert_eq!((view.last_from, view.hint_square), (-1, -1));
        let play = |command| {
            let resp = call(core, request::Command::Session(messages::SessionCommand { command: Some(command) }));
            match resp.result {
                Some(response::Result::Session(s)) => s,
                other => panic!("{other:?}"),
            }
        };
        play(session_command::Command::PlayOpponent(messages::Empty {}));
        let wrong = play(session_command::Command::MakeMove("a7a6".into()));
        assert_eq!(wrong.outcome, messages::MoveOutcome::Wrong as i32);
        let solved = play(session_command::Command::MakeMove("d5g2".into()));
        assert_eq!(solved.outcome, messages::MoveOutcome::Solved as i32);
        let view = solved.view.unwrap();
        assert_eq!(view.state, messages::SessionState::Solved as i32);
        assert_eq!(view.moves.last().unwrap().san, "Qg2#");
        assert_eq!(view.mistakes, 1);

        let garbage = [0xFFu8, 0xFF, 0xFF];
        let mut out = std::ptr::null_mut();
        assert_eq!(unsafe { lpz_call(core, garbage.as_ptr(), garbage.len(), &mut out) }, LPZ_MALFORMED_REQUEST);
        assert!(out.is_null());

        unsafe { lpz_destroy(core) };
    }

    #[test]
    fn create_reports_bad_input() {
        let mut core = std::ptr::null_mut();
        let mut err = std::ptr::null_mut();
        let empty = messages::CreateConfig::default().encode_to_vec();
        assert_eq!(unsafe { lpz_create(empty.as_ptr(), empty.len(), &mut core, &mut err) }, LPZ_INVALID_ARGUMENT);
        assert!(core.is_null() && !err.is_null());
        unsafe { lpz_result_free(err) };
        assert_eq!(unsafe { lpz_create(std::ptr::null(), 3, &mut core, std::ptr::null_mut()) }, LPZ_INVALID_ARGUMENT);
    }
}
