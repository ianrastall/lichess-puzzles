//! The service end to end: download, build, install, update and play, against a local server.

mod support;

use std::path::Path;
use std::time::{Duration, Instant};

use lichess_puzzles_core::filter::Filter;
use lichess_puzzles_core::service::{EventAction, EventLevel, Service, ServiceConfig, ServiceError, SessionCommand};
use lichess_puzzles_core::session::{MoveOutcome, SessionState};
use support::Server;

const SAMPLE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/sample_puzzles.csv");

fn open(dir: &Path, url: &str) -> Service {
    Service::open(ServiceConfig { data_dir: dir.to_path_buf(), source_url: url.to_owned() }).unwrap()
}

/// Waits for the running operation to finish and returns the events it produced.
fn settle(service: &Service) -> Vec<(EventLevel, String, EventAction)> {
    let deadline = Instant::now() + Duration::from_secs(30);
    while service.status().operation.is_some() {
        assert!(Instant::now() < deadline, "operation did not finish");
        std::thread::sleep(Duration::from_millis(20));
    }
    service.poll_events(100).into_iter().map(|e| (e.level, e.message, e.action)).collect()
}

fn sample_zst() -> Vec<u8> {
    zstd::encode_all(std::fs::read(SAMPLE).unwrap().as_slice(), 3).unwrap()
}

#[test]
fn downloads_installs_updates_and_switches() {
    let server = Server::start(sample_zst(), "\"v1\"");
    let dir = tempfile::tempdir().unwrap();
    let service = open(dir.path(), &server.url);

    let status = service.status();
    assert!(status.store.is_none());
    assert_eq!(status.store_problem.as_deref(), Some("No puzzles are installed."));
    assert_eq!(service.next_puzzle().unwrap_err(), ServiceError::NoStore);

    service.start_download(true).unwrap();
    assert_eq!(service.start_download(true).unwrap_err(), ServiceError::Busy);
    let events = settle(&service);
    assert_eq!(events.len(), 1, "{events:?}");
    assert_eq!(events[0].0, EventLevel::Success);
    assert!(events[0].1.starts_with("Installed 13 puzzles"), "{events:?}");

    let status = service.status();
    let store = status.store.expect("store installed");
    assert_eq!((store.puzzle_count, store.rating_min, store.rating_max), (13, 998, 2873));
    assert_eq!(store.source.version.as_ref().and_then(|v| v.etag.as_deref()), Some("\"v1\""));
    assert!(status.download.is_some());
    assert!(!status.update_ready);

    service.check_for_update().unwrap();
    assert_eq!(settle(&service)[0].1, "The installed puzzles are up to date.");
    service.start_download(true).unwrap();
    assert_eq!(settle(&service)[0].1, "The installed puzzles are up to date.");

    // A new version on the server: check, download and build it beside the open store.
    server.state.lock().unwrap().etag = "\"v2\"".into();
    service.check_for_update().unwrap();
    assert_eq!(settle(&service)[0].1, "A puzzle update is available.");
    assert!(service.status().update_available.is_some());
    let view = service.next_puzzle().unwrap();
    service.start_download(false).unwrap();
    let events = settle(&service);
    assert_eq!(events[0].2, EventAction::SwitchStore, "{events:?}");
    let status = service.status();
    assert!(status.update_ready && status.update_available.is_none());
    assert!(status.download.is_none(), "the download is deleted when not kept");

    service.switch_store().unwrap();
    let status = service.status();
    assert!(!status.update_ready);
    assert_eq!(status.store.unwrap().source.version.unwrap().etag.as_deref(), Some("\"v2\""));
    // The session survives the switch.
    let (_, same) = service.session(SessionCommand::Get).unwrap();
    assert_eq!(same.puzzle.id, view.puzzle.id);
}

#[test]
fn imports_filters_and_plays() {
    let dir = tempfile::tempdir().unwrap();
    let service = open(dir.path(), "http://127.0.0.1:9/unused");
    service.start_build(Some(SAMPLE.into()), true).unwrap();
    assert!(settle(&service)[0].1.starts_with("Installed 13 puzzles"));

    let catalogue = service.catalogue().unwrap();
    assert_eq!(catalogue.themes.len(), 19);
    assert_eq!(catalogue.themes[0].group, "Goal");
    assert!(catalogue.groups.starts_with(&["Goal".to_owned(), "Length".to_owned(), "Phase".to_owned()]));
    let mate = catalogue.themes.iter().find(|t| t.key == "mate").unwrap();
    assert_eq!((mate.name.as_str(), mate.count), ("Checkmate", 2));
    let french = catalogue.variations.iter().find(|v| v.tag == "French_Defense_Exchange_Variation").unwrap();
    assert_eq!(french.name, "Exchange Variation");
    assert_eq!(catalogue.families.iter().find(|f| f.id == french.family).unwrap().name, "French Defense");

    let counts = service.set_filter(Filter { themes_any: vec!["mate".into()], ..Default::default() }).unwrap();
    assert_eq!(counts.total, 2);
    assert!(service.set_filter(Filter { themes_any: vec!["nothing".into()], ..Default::default() }).is_err());

    // Play whichever mate puzzle comes up, using its own solution.
    let view = service.next_puzzle().unwrap();
    assert!(["0000H", "00sHx"].contains(&view.puzzle.id.as_str()));
    assert_eq!(view.state, SessionState::OpponentToMove);
    let line = view.puzzle.moves.clone();
    let (_, opened) = service.session(SessionCommand::PlayOpponent).unwrap();
    assert_eq!(opened.state, SessionState::Solving);
    let mut ply = 1;
    let view = loop {
        let (outcome, view) = service.session(SessionCommand::MakeMove(line[ply].clone())).unwrap();
        ply += 2;
        match outcome.unwrap() {
            MoveOutcome::Solved => break view,
            MoveOutcome::Correct => {
                service.session(SessionCommand::PlayOpponent).unwrap();
            }
            other => panic!("{other:?}"),
        }
    };
    assert_eq!(view.state, SessionState::Solved);
    assert_eq!(view.moves.len(), line.len());

    let (_, view) = service.session(SessionCommand::Retry).unwrap();
    assert!(view.replay);
    let view = service.load_puzzle("https://lichess.org/training/00sJ9", false).unwrap();
    assert_eq!(view.puzzle.opening_variation.as_deref(), Some("French_Defense_Exchange_Variation"));
    assert_eq!(service.load_puzzle("zzzzz", false).unwrap_err(), ServiceError::NotFound("zzzzz".into()));

    service.set_filter(Filter { rating_min: Some(4000), ..Default::default() }).unwrap();
    assert_eq!(service.next_puzzle().unwrap_err(), ServiceError::NoMatches);
}

#[test]
fn pause_and_cancel_downloads() {
    let server = Server::start((0..4_000_000u32).map(|i| i as u8).collect(), "\"big\"");
    // Each response stops early so the download stays in progress long enough to pause.
    server.state.lock().unwrap().cut_after = vec![200_000; 50];
    let dir = tempfile::tempdir().unwrap();
    let service = open(dir.path(), &server.url);
    service.start_download(true).unwrap();
    std::thread::sleep(Duration::from_millis(300));
    service.pause_download();
    let events = settle(&service);
    assert!(events[0].1.starts_with("Download paused"), "{events:?}");
    let (partial, total) = service.status().partial_download.expect("partial kept");
    assert!(partial > 0);
    assert_eq!(total, Some(4_000_000));

    service.start_download(true).unwrap();
    std::thread::sleep(Duration::from_millis(300));
    service.cancel_operation();
    let events = settle(&service);
    assert_eq!(events[0].1, "Download cancelled.", "{events:?}");
    assert!(service.status().partial_download.is_none());
}
