//! The core used by the desktop shell: the open store, the current filter, the puzzle session and
//! one background operation at a time (download, store build or update check).
//!
//! Every method returns quickly except the filter and puzzle queries, which scan the store (about
//! 15 ms). Operations run on their own thread and report through `status` and `poll_events`.
//!
//! Stores are built to `puzzles-next.store` and moved to `puzzles.store` by `switch_store`, which
//! first closes the open store (Windows cannot replace a mapped file).
//!
//! Players, ratings, history, export and analysis are in `service/practice.rs`.

mod practice;
mod selection;
pub use selection::SelectionOptions;

pub use practice::{AnalysisOutcome, AnalysisSnapshot, ExportSource, PlayersInfo, SessionResult};

use std::collections::VecDeque;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, RwLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::build::{BuildOptions, build_store};
use crate::catalogue::{self, OTHER_GROUP};
use crate::filter::{self, Filter, FilterError};
use crate::players::{PlayerError, PlayerStore};
use crate::session::SessionError;
use crate::source::{DownloadOutcome, RemoteVersion, Source, UpdateCheck};
use crate::store::{SourceInfo, Store, StoreError};
use crate::{STORE_FILE, puzzle_id};

pub const NEXT_STORE_FILE: &str = "puzzles-next.store";
const MAX_EVENTS: usize = 200;

#[derive(Debug, Clone)]
pub struct ServiceConfig {
    pub data_dir: PathBuf,
    pub source_url: String,
    /// Name of the player created with a new player database.
    pub default_player: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ServiceError {
    /// No puzzle store is installed or it cannot be read.
    NoStore,
    /// Another operation is running.
    Busy,
    NotFound(String),
    NoMatches,
    Selection(String),
    Filter(FilterError),
    Session(SessionError),
    Store(StoreError),
    Player(PlayerError),
    Io(String),
}

impl std::fmt::Display for ServiceError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ServiceError::NoStore => f.write_str("no puzzles are installed"),
            ServiceError::Busy => f.write_str("another download or build is running"),
            ServiceError::NotFound(id) => write!(f, "no puzzle `{id}` in this database"),
            ServiceError::NoMatches => f.write_str("no puzzles match the filters"),
            ServiceError::Selection(message) => f.write_str(message),
            ServiceError::Filter(e) => e.fmt(f),
            ServiceError::Session(e) => e.fmt(f),
            ServiceError::Store(e) => e.fmt(f),
            ServiceError::Player(e) => e.fmt(f),
            ServiceError::Io(message) => f.write_str(message),
        }
    }
}

impl std::error::Error for ServiceError {}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EventLevel {
    Info,
    Success,
    Warning,
    Error,
}

/// What the shell can offer beside an event's message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EventAction {
    None,
    RetryDownload,
    RetryBuild,
    RetryCheck,
    SwitchStore,
    /// Show the file named by the event's path.
    ShowFile,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Event {
    pub level: EventLevel,
    pub message: String,
    pub action: EventAction,
    pub unix_ms: u64,
    pub path: Option<PathBuf>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OperationKind {
    Download,
    Build,
    CheckUpdate,
    Export,
}

#[derive(Debug, Clone, PartialEq)]
pub struct OperationStatus {
    pub kind: OperationKind,
    pub cancelling: bool,
    /// Bytes downloaded, input bytes read or puzzles exported.
    pub done: u64,
    pub total: Option<u64>,
    /// Bytes per second while downloading.
    pub rate: f64,
    /// Puzzles read while building.
    pub rows: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoreSummary {
    pub path: PathBuf,
    pub puzzle_count: u64,
    pub file_bytes: u64,
    pub built_at_unix: u64,
    pub source: SourceInfo,
    pub skipped_count: u64,
    pub rating_min: u16,
    pub rating_max: u16,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Status {
    pub data_dir: PathBuf,
    pub store: Option<StoreSummary>,
    /// Why no store is open: missing, or the reason it cannot be read.
    pub store_problem: Option<String>,
    pub operation: Option<OperationStatus>,
    /// Bytes of a resumable download, and its full length if known.
    pub partial_download: Option<(u64, Option<u64>)>,
    /// The completed download kept on disk, if any.
    pub download: Option<RemoteVersion>,
    pub update_available: Option<RemoteVersion>,
    /// A newer store has been built and waits for `switch_store`.
    pub update_ready: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CatalogueTheme {
    pub key: String,
    pub bit: u32,
    pub group: String,
    pub name: String,
    pub description: String,
    pub count: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CatalogueOpening {
    /// Family or variation id (from 1).
    pub id: u32,
    pub tag: String,
    pub name: String,
    /// For a variation, its family id; 0 for a family.
    pub family: u32,
    pub count: u64,
}

/// Themes and openings of the open store, with unfiltered counts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Catalogue {
    /// In display order: catalogue groups, then any unknown themes under `Other`.
    pub themes: Vec<CatalogueTheme>,
    pub groups: Vec<String>,
    /// Sorted by name.
    pub families: Vec<CatalogueOpening>,
    /// Sorted by family name, then name.
    pub variations: Vec<CatalogueOpening>,
    pub rating_min: u16,
    pub rating_max: u16,
}

/// A session command and its result.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionCommand {
    Get,
    PlayOpponent,
    MakeMove(String),
    Hint,
    ShowSolution,
    Retry,
    Navigate(usize),
}

struct OpenStore {
    store: Arc<Store>,
    rating_min: u16,
    rating_max: u16,
}

struct Operation {
    id: u64,
    cancel: Arc<AtomicBool>,
    /// With `cancel`: delete the partial download instead of keeping it.
    discard: Arc<AtomicBool>,
    progress: Arc<Mutex<OperationStatus>>,
    thread: Option<JoinHandle<()>>,
}

#[derive(Default)]
struct UpdateState {
    available: Option<RemoteVersion>,
    ready: bool,
}

struct Inner {
    /// Serializes state snapshots and session commits, never long screening searches.
    state_gate: Mutex<()>,
    state_revision: AtomicU64,
    data_dir: PathBuf,
    source: Source,
    store: RwLock<Option<OpenStore>>,
    store_problem: Mutex<Option<String>>,
    filter: Mutex<Filter>,
    skip_played: AtomicBool,
    session: Mutex<Option<practice::Active>>,
    players: Mutex<PlayerStore>,
    /// The current player's played puzzles as record indices of the open store.
    played: Mutex<Option<(i64, Arc<Vec<u64>>)>>,
    selection: Mutex<selection::SelectionState>,
    rated: AtomicBool,
    analysis: Mutex<practice::AnalysisState>,
    operation: Mutex<Option<Operation>>,
    next_operation: AtomicU64,
    events: Mutex<VecDeque<Event>>,
    update: Mutex<UpdateState>,
    seed: AtomicU64,
}

pub struct Service {
    inner: Arc<Inner>,
}

impl Service {
    /// Opens the data folder and the store in it, if there is one. A store left in
    /// `puzzles-next.store` by an earlier session is moved into place first.
    pub fn open(config: ServiceConfig) -> Result<Service, ServiceError> {
        fs::create_dir_all(&config.data_dir)
            .map_err(|e| ServiceError::Io(format!("{}: {e}", config.data_dir.display())))?;
        let seed = SystemTime::now().duration_since(UNIX_EPOCH).map_or(1, |d| d.as_nanos() as u64);
        let players = PlayerStore::open(&config.data_dir.join("players.sqlite"), &config.default_player)
            .map_err(ServiceError::Player)?;
        let inner = Arc::new(Inner {
            state_gate: Mutex::new(()),
            state_revision: AtomicU64::new(0),
            source: Source::new(config.data_dir.join("source"), config.source_url),
            data_dir: config.data_dir,
            store: RwLock::new(None),
            store_problem: Mutex::new(None),
            filter: Mutex::new(Filter::default()),
            skip_played: AtomicBool::new(false),
            session: Mutex::new(None),
            players: Mutex::new(players),
            played: Mutex::new(None),
            selection: Mutex::new(selection::SelectionState::default()),
            rated: AtomicBool::new(true),
            analysis: Mutex::new(practice::AnalysisState::default()),
            operation: Mutex::new(None),
            next_operation: AtomicU64::new(1),
            events: Mutex::new(VecDeque::new()),
            update: Mutex::new(UpdateState::default()),
            seed: AtomicU64::new(seed),
        });
        if inner.data_dir.join(NEXT_STORE_FILE).is_file() && Store::open(inner.data_dir.join(NEXT_STORE_FILE)).is_ok() {
            let _ = fs::rename(inner.data_dir.join(NEXT_STORE_FILE), inner.data_dir.join(STORE_FILE));
        }
        inner.open_current();
        Ok(Service { inner })
    }

    pub fn status(&self) -> Status {
        let inner = &self.inner;
        let store = inner.store.read().unwrap().as_ref().map(|open| {
            let info = open.store.info();
            StoreSummary {
                path: open.store.path().to_path_buf(),
                puzzle_count: info.puzzle_count,
                file_bytes: open.store.file_len(),
                built_at_unix: info.built_at_unix,
                source: info.source.clone(),
                skipped_count: info.skipped_count,
                rating_min: open.rating_min,
                rating_max: open.rating_max,
            }
        });
        let operation = inner.operation.lock().unwrap().as_ref().map(|op| op.progress.lock().unwrap().clone());
        let update = inner.update.lock().unwrap();
        Status {
            data_dir: inner.data_dir.clone(),
            store,
            store_problem: inner.store_problem.lock().unwrap().clone(),
            operation,
            partial_download: inner.source.partial(),
            download: inner.source.local_version(),
            update_available: update.available.clone(),
            update_ready: update.ready,
        }
    }

    fn store(&self) -> Result<Arc<Store>, ServiceError> {
        self.inner.current_store().ok_or(ServiceError::NoStore)
    }

    pub fn catalogue(&self) -> Result<Catalogue, ServiceError> {
        let store = self.store()?;
        let (rating_min, rating_max) = {
            let guard = self.inner.store.read().unwrap();
            guard.as_ref().map_or((0, 0), |o| (o.rating_min, o.rating_max))
        };
        let all = Filter::default().compile(&store).map_err(ServiceError::Filter)?;
        let counts = filter::counts(&store, &all);
        let info = store.info();

        let mut themes: Vec<CatalogueTheme> = info
            .themes
            .iter()
            .enumerate()
            .map(|(bit, key)| {
                let known = catalogue::theme_info(key);
                CatalogueTheme {
                    key: key.clone(),
                    bit: bit as u32,
                    group: known.map_or(OTHER_GROUP, |t| t.group).to_owned(),
                    name: known.map_or(key.as_str(), |t| t.name).to_owned(),
                    description: known.map_or("", |t| t.description).to_owned(),
                    count: counts.themes[bit] as u64,
                }
            })
            .collect();
        let order = |key: &str| catalogue::THEMES.iter().position(|t| t.key == key).unwrap_or(usize::MAX);
        themes.sort_by(|a, b| order(&a.key).cmp(&order(&b.key)).then(a.name.cmp(&b.name)));
        let groups = catalogue::GROUPS
            .iter()
            .filter(|g| themes.iter().any(|t| t.group == **g))
            .map(|g| (*g).to_owned())
            .collect();

        let mut families: Vec<CatalogueOpening> = info
            .opening_families
            .iter()
            .enumerate()
            .map(|(i, tag)| CatalogueOpening {
                id: i as u32 + 1,
                tag: tag.clone(),
                name: catalogue::family_name(tag),
                family: 0,
                count: counts.families[i + 1] as u64,
            })
            .collect();
        families.sort_by(|a, b| a.name.cmp(&b.name));
        let mut variations: Vec<CatalogueOpening> = info
            .opening_variations
            .iter()
            .enumerate()
            .map(|(i, v)| {
                let family = store.family_tag(v.family).unwrap_or("");
                CatalogueOpening {
                    id: i as u32 + 1,
                    tag: v.tag.clone(),
                    name: catalogue::variation_name(&v.tag, family),
                    family: u32::from(v.family),
                    count: counts.variations[i + 1] as u64,
                }
            })
            .collect();
        let family_name = |id: u32| store.family_tag(id as u16).map(catalogue::family_name).unwrap_or_default();
        variations.sort_by(|a, b| family_name(a.family).cmp(&family_name(b.family)).then(a.name.cmp(&b.name)));

        Ok(Catalogue { themes, groups, families, variations, rating_min, rating_max })
    }

    /// Downloads the puzzle database (resuming a paused download) and builds a store from it.
    pub fn start_download(&self, keep_download: bool) -> Result<(), ServiceError> {
        self.start_operation(OperationKind::Download, move |inner, op| inner.download_and_build(op, keep_download))
    }

    /// Builds a store from `input`, or from the kept download when `None`.
    pub fn start_build(&self, input: Option<PathBuf>, keep_download: bool) -> Result<(), ServiceError> {
        let input = match input {
            Some(path) => path,
            None => {
                let path = self.inner.source.file_path();
                if !path.is_file() {
                    return Err(ServiceError::Io("the downloaded puzzle file is no longer on disk".into()));
                }
                path
            }
        };
        self.start_operation(OperationKind::Build, move |inner, op| {
            let from_download = input == inner.source.file_path();
            let version = if from_download { inner.source.local_version() } else { None };
            inner.build(op, &input, version, from_download && !keep_download, true);
        })
    }

    pub fn check_for_update(&self) -> Result<(), ServiceError> {
        self.start_operation(OperationKind::CheckUpdate, |inner, _| inner.check_update())
    }

    /// Stops a download, keeping what was downloaded so it can resume.
    pub fn pause_download(&self) {
        self.inner.cancel(false);
    }

    /// Stops the running operation; a partial download is deleted.
    pub fn cancel_operation(&self) {
        self.inner.cancel(true);
    }

    /// Replaces the open store with the newly built one.
    pub fn switch_store(&self) -> Result<(), ServiceError> {
        self.inner.switch_store()
    }

    pub fn poll_events(&self, max: usize) -> Vec<Event> {
        let mut events = self.inner.events.lock().unwrap();
        let n = max.min(events.len());
        events.drain(..n).collect()
    }

    /// Cancels the running operation, keeping a partial download, and waits for it to stop.
    pub fn shutdown(&self) {
        self.stop_analysis();
        let op = self.inner.operation.lock().unwrap().take();
        if let Some(mut op) = op {
            op.cancel.store(true, Ordering::Relaxed);
            if let Some(thread) = op.thread.take() {
                let _ = thread.join();
            }
        }
    }

    fn start_operation(
        &self,
        kind: OperationKind,
        work: impl FnOnce(&Inner, &OperationHandle) + Send + 'static,
    ) -> Result<(), ServiceError> {
        let mut slot = self.inner.operation.lock().unwrap();
        if slot.is_some() {
            return Err(ServiceError::Busy);
        }
        let id = self.inner.next_operation.fetch_add(1, Ordering::Relaxed);
        let handle = OperationHandle {
            cancel: Arc::new(AtomicBool::new(false)),
            discard: Arc::new(AtomicBool::new(false)),
            progress: Arc::new(Mutex::new(OperationStatus {
                kind,
                cancelling: false,
                done: 0,
                total: None,
                rate: 0.0,
                rows: 0,
            })),
        };
        let mut op = Operation {
            id,
            cancel: handle.cancel.clone(),
            discard: handle.discard.clone(),
            progress: handle.progress.clone(),
            thread: None,
        };
        let inner = self.inner.clone();
        let thread = std::thread::Builder::new()
            .name(format!("operation-{id}"))
            .spawn(move || {
                let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| work(&inner, &handle)));
                if outcome.is_err() {
                    inner.event(EventLevel::Error, "The operation stopped unexpectedly.", EventAction::None);
                }
                let mut slot = inner.operation.lock().unwrap();
                if slot.as_ref().is_some_and(|op| op.id == id) {
                    *slot = None;
                }
            })
            .map_err(|e| ServiceError::Io(e.to_string()))?;
        op.thread = Some(thread);
        *slot = Some(op);
        Ok(())
    }
}

impl Drop for Service {
    fn drop(&mut self) {
        self.shutdown();
    }
}

/// The running operation's view of its controls and progress.
struct OperationHandle {
    cancel: Arc<AtomicBool>,
    discard: Arc<AtomicBool>,
    progress: Arc<Mutex<OperationStatus>>,
}

impl OperationHandle {
    fn set_kind(&self, kind: OperationKind) {
        let mut p = self.progress.lock().unwrap();
        *p = OperationStatus { kind, cancelling: p.cancelling, done: 0, total: None, rate: 0.0, rows: 0 };
    }

    fn update(&self, f: impl FnOnce(&mut OperationStatus)) {
        f(&mut self.progress.lock().unwrap());
    }
}

impl Inner {
    /// Mutations invalidate in-flight selections before changing their inputs.
    fn change_state(&self) -> MutexGuard<'_, ()> {
        let guard = self.state_gate.lock().unwrap();
        self.state_revision.fetch_add(1, Ordering::Relaxed);
        guard
    }
    fn current_store(&self) -> Option<Arc<Store>> {
        self.store.read().unwrap().as_ref().map(|o| o.store.clone())
    }

    fn event(&self, level: EventLevel, message: impl Into<String>, action: EventAction) {
        let unix_ms = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as u64);
        let mut events = self.events.lock().unwrap();
        if events.len() == MAX_EVENTS {
            events.pop_front();
        }
        events.push_back(Event { level, message: message.into(), action, unix_ms, path: None });
    }

    fn event_with_path(&self, level: EventLevel, message: impl Into<String>, action: EventAction, path: PathBuf) {
        self.event(level, message, action);
        if let Some(last) = self.events.lock().unwrap().back_mut() {
            last.path = Some(path);
        }
    }

    /// Opens `puzzles.store`, recording why when it cannot be opened.
    fn open_current(&self) {
        let path = self.data_dir.join(STORE_FILE);
        let (open, problem) = if !path.is_file() {
            (None, Some("No puzzles are installed.".to_owned()))
        } else {
            match Store::open(&path) {
                Ok(store) => (Some(open_store(store)), None),
                Err(e) => (None, Some(format!("The puzzle store can't be read: {e}."))),
            }
        };
        *self.store.write().unwrap() = open;
        *self.store_problem.lock().unwrap() = problem;
    }

    fn cancel(&self, discard: bool) {
        if let Some(op) = self.operation.lock().unwrap().as_ref() {
            op.discard.store(discard, Ordering::Relaxed);
            op.cancel.store(true, Ordering::Relaxed);
            op.progress.lock().unwrap().cancelling = true;
        }
    }

    fn installed_version(&self) -> Option<RemoteVersion> {
        let store = self.current_store()?;
        store.info().source.version.clone()
    }

    fn check_update(&self) {
        let remote = match self.source.remote_version() {
            Ok(remote) => remote,
            Err(e) => {
                self.event(EventLevel::Error, format!("Checking for an update failed: {e}."), EventAction::RetryCheck);
                return;
            }
        };
        let installed = self.installed_version();
        let current = installed.as_ref().is_some_and(|v| v.same_file(&remote));
        let mut update = self.update.lock().unwrap();
        if current {
            update.available = None;
            drop(update);
            self.event(EventLevel::Info, "The installed puzzles are up to date.", EventAction::None);
        } else if self.current_store().is_some() {
            update.available = Some(remote);
            drop(update);
            self.event(EventLevel::Info, "A puzzle update is available.", EventAction::None);
        }
    }

    fn download_and_build(&self, op: &OperationHandle, keep_download: bool) {
        let remote = match self.source.remote_version() {
            Ok(remote) => remote,
            Err(e) => {
                self.event(
                    EventLevel::Error,
                    format!("The download could not start: {e}."),
                    EventAction::RetryDownload,
                );
                return;
            }
        };
        if self.installed_version().is_some_and(|v| v.same_file(&remote)) {
            self.update.lock().unwrap().available = None;
            self.event(EventLevel::Info, "The installed puzzles are up to date.", EventAction::None);
            return;
        }
        let have_current_download = matches!(self.source.check_update_against(&remote), UpdateCheck::Current(_));
        if !have_current_download {
            op.update(|p| p.total = remote.length);
            let mut progress = |p: crate::source::Progress| {
                op.update(|s| {
                    s.done = p.downloaded;
                    s.total = p.total;
                    s.rate = p.rate;
                });
            };
            match self.source.download(&mut progress, &op.cancel) {
                Ok(DownloadOutcome::Completed { .. }) => {}
                Ok(DownloadOutcome::Cancelled { downloaded }) => {
                    if op.discard.load(Ordering::Relaxed) {
                        self.source.discard_part();
                        self.event(EventLevel::Info, "Download cancelled.", EventAction::None);
                    } else {
                        let mb = downloaded as f64 / 1_048_576.0;
                        self.event(EventLevel::Info, format!("Download paused at {mb:.1} MB."), EventAction::None);
                    }
                    return;
                }
                Err(e) => {
                    self.event(
                        EventLevel::Error,
                        format!("The download failed: {e}. What was downloaded is kept."),
                        EventAction::RetryDownload,
                    );
                    return;
                }
            }
        }
        op.set_kind(OperationKind::Build);
        let input = self.source.file_path();
        let switch = self.current_store().is_none();
        self.build(op, &input, self.source.local_version(), !keep_download, switch);
    }

    fn build(
        &self,
        op: &OperationHandle,
        input: &Path,
        version: Option<RemoteVersion>,
        delete_input: bool,
        switch_when_done: bool,
    ) {
        let next = self.data_dir.join(NEXT_STORE_FILE);
        let mut progress = |p: crate::build::BuildProgress| {
            op.update(|s| {
                s.done = p.input_read;
                s.total = Some(p.input_total);
                s.rows = p.rows;
            });
        };
        let started = Instant::now();
        let report = build_store(input, &next, version, &BuildOptions::default(), &mut progress, &op.cancel);
        match report {
            Ok(report) => {
                if delete_input {
                    let _ = fs::remove_file(input);
                }
                let skipped = match report.skipped {
                    0 => String::new(),
                    n => format!(", {n} skipped"),
                };
                let seconds = started.elapsed().as_secs();
                if switch_when_done {
                    match self.switch_store() {
                        Ok(()) => self.event(
                            EventLevel::Success,
                            format!("Installed {} puzzles in {seconds} s{skipped}.", group_digits(report.puzzles)),
                            EventAction::None,
                        ),
                        Err(e) => self.event(
                            EventLevel::Error,
                            format!("The new puzzles were built but could not be opened: {e}."),
                            EventAction::SwitchStore,
                        ),
                    }
                } else {
                    let mut update = self.update.lock().unwrap();
                    update.ready = true;
                    update.available = None;
                    drop(update);
                    self.event(
                        EventLevel::Success,
                        format!("The puzzle update is ready ({} puzzles{skipped}).", group_digits(report.puzzles)),
                        EventAction::SwitchStore,
                    );
                }
            }
            Err(crate::build::BuildError::Cancelled) => {
                self.event(EventLevel::Info, "Building the puzzle store was cancelled.", EventAction::None);
            }
            Err(e) => self.event(
                EventLevel::Error,
                format!("Building the puzzle store failed: {e}. The installed puzzles are unchanged."),
                EventAction::RetryBuild,
            ),
        }
    }

    fn switch_store(&self) -> Result<(), ServiceError> {
        let _state = self.change_state();
        let next = self.data_dir.join(NEXT_STORE_FILE);
        if !next.is_file() {
            return Err(ServiceError::Io("no new puzzle store has been built".into()));
        }
        // Check the new store before closing the current one.
        drop(Store::open(&next).map_err(ServiceError::Store)?);
        let mut guard = self.store.write().unwrap();
        if let Some(open) = guard.take() {
            // Queries hold their own reference briefly; wait for them before unmapping.
            let deadline = Instant::now() + Duration::from_secs(10);
            while Arc::strong_count(&open.store) > 1 {
                if Instant::now() > deadline {
                    *guard = Some(open);
                    return Err(ServiceError::Io("the puzzle store is still in use; try again".into()));
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            drop(open);
        }
        let current = self.data_dir.join(STORE_FILE);
        let renamed = fs::rename(&next, &current);
        let (open, problem) = match Store::open(&current) {
            Ok(store) => (Some(open_store(store)), None),
            Err(e) => (None, Some(format!("The puzzle store can't be read: {e}."))),
        };
        let opened = open.is_some();
        *guard = open;
        drop(guard);
        // Played puzzles are kept as record indices, which differ between stores.
        *self.played.lock().unwrap() = None;
        *self.store_problem.lock().unwrap() = problem;
        let mut update = self.update.lock().unwrap();
        update.ready = false;
        update.available = None;
        drop(update);
        renamed.map_err(|e| ServiceError::Io(e.to_string()))?;
        if opened { Ok(()) } else { Err(ServiceError::NoStore) }
    }
}

fn open_store(store: Store) -> OpenStore {
    let (mut low, mut high) = (u16::MAX, 0);
    for index in 0..store.len() {
        let rating = store.record(index).rating;
        low = low.min(rating);
        high = high.max(rating);
    }
    if store.is_empty() {
        low = 0;
    }
    OpenStore { store: Arc::new(store), rating_min: low, rating_max: high }
}

/// `https://lichess.org/training/00sHx` → `00sHx`; other text is trimmed.
fn puzzle_id_from(text: &str) -> String {
    let text = text.trim();
    let id = text.rsplit('/').next().unwrap_or(text);
    let id = id.split(['?', '#']).next().unwrap_or(id);
    if puzzle_id::pack(id).is_some() { id.to_owned() } else { text.to_owned() }
}

fn group_digits(n: u64) -> String {
    let digits = n.to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn puzzle_ids_from_links() {
        assert_eq!(puzzle_id_from(" 00sHx "), "00sHx");
        assert_eq!(puzzle_id_from("https://lichess.org/training/00sHx"), "00sHx");
        assert_eq!(puzzle_id_from("https://lichess.org/training/00sHx?x=1"), "00sHx");
        assert_eq!(group_digits(6157341), "6,157,341");
        assert_eq!(group_digits(12), "12");
    }
}
