//! The service's puzzle play: filters with played puzzles left out, puzzle choice near the
//! player's rating, the session with rating settlement and history, players, export and the
//! evaluation engine.
//!
//! Rating follows Lichess: a rated puzzle is settled as a loss by the first wrong move, hint or
//! the solution, and as a win by a solve without any of these. Leaving a puzzle unfinished after
//! playing in it is a loss. Only a player's first attempt at a puzzle is rated; replays are
//! neither rated nor recorded.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use super::{EventAction, EventLevel, Inner, OperationKind, Service, ServiceError, group_digits, puzzle_id_from};
use crate::engine::{self, SearchInfo};
use crate::export::{self, ExportFormat};
use crate::filter::{self, CompiledFilter, Counts, Filter};
use crate::players::{HistoryEntry, HistoryStats, Player, PlayerError, PuzzleResult, glicko2};
use crate::session::{MoveOutcome, Session, SessionError, SessionView};
use crate::store::Store;

/// The open session and what it means for the player's rating and history.
pub(super) struct Active {
    pub(super) session: Session,
    tracker: Tracker,
}

struct Tracker {
    player: i64,
    /// Counts toward the rating: rated play, and the player's first attempt.
    rated: bool,
    /// Recorded in history (not a replay).
    recorded: bool,
    entry_id: i64,
    rating_change: Option<(f64, f64)>,
    settled: bool,
    attempted: bool,
    finished: bool,
    started: Instant,
    played_ms: i64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct SessionResult {
    /// Set when the command was a move.
    pub outcome: Option<MoveOutcome>,
    pub view: SessionView,
    /// The attempt counts toward the player's rating.
    pub rated: bool,
    /// Rating before and after, once the rated result is settled.
    pub rating_change: Option<(f64, f64)>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PlayersInfo {
    pub players: Vec<Player>,
    pub current: i64,
    pub stats: HistoryStats,
    pub rated: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExportSource {
    /// Every puzzle matching the current filter.
    Matching,
    /// The current player's played puzzles, newest first.
    Played,
}

#[derive(Default)]
pub(super) struct AnalysisState {
    generation: u64,
    fen: String,
    running: bool,
    info: Option<SearchInfo>,
    terminal_outcome: Option<AnalysisOutcome>,
    stop: Arc<AtomicBool>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AnalysisOutcome {
    WhiteWins,
    BlackWins,
    Draw,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AnalysisSnapshot {
    pub fen: String,
    pub running: bool,
    pub info: Option<SearchInfo>,
    pub terminal_outcome: Option<AnalysisOutcome>,
}

fn now_ms() -> i64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

fn player_error(e: PlayerError) -> ServiceError {
    ServiceError::Player(e)
}

impl Service {
    /// The current filter compiled for `store`, leaving out played puzzles when asked to.
    pub(super) fn compiled(&self, store: &Store) -> Result<CompiledFilter, ServiceError> {
        let filter = self.inner.filter.lock().unwrap().clone();
        let compiled = filter.compile(store).map_err(ServiceError::Filter)?;
        Ok(if self.inner.skip_played.load(Ordering::Relaxed) {
            compiled.excluding(self.played_set(store)?)
        } else {
            compiled
        })
    }

    /// The current player's played puzzles as record indices, cached until they change.
    pub(super) fn played_set(&self, store: &Store) -> Result<Arc<Vec<u64>>, ServiceError> {
        let players = self.inner.players.lock().unwrap();
        let player = players.current().map_err(player_error)?.id;
        let mut cache = self.inner.played.lock().unwrap();
        if let Some((id, set)) = cache.as_ref()
            && *id == player
        {
            return Ok(set.clone());
        }
        let ids = players.played_ids(player).map_err(player_error)?;
        let set = filter::index_set(store.len(), ids.iter().filter_map(|id| store.find(id)));
        *cache = Some((player, set.clone()));
        Ok(set)
    }

    /// Replaces the current filter and returns its counts. An invalid filter is not kept.
    pub fn set_filter(&self, filter: Filter, skip_played: bool) -> Result<Counts, ServiceError> {
        let _state = self.inner.change_state();
        let store = self.store()?;
        let mut compiled = filter.compile(&store).map_err(ServiceError::Filter)?;
        if skip_played {
            compiled = compiled.excluding(self.played_set(&store)?);
        }
        let counts = filter::counts(&store, &compiled);
        *self.inner.filter.lock().unwrap() = filter;
        self.inner.skip_played.store(skip_played, Ordering::Relaxed);
        Ok(counts)
    }

    /// Starts a random puzzle, within 150 points when near-rating selection is requested.
    pub fn next_puzzle(&self, near_offset: Option<i32>) -> Result<SessionResult, ServiceError> {
        self.next_selected(near_offset, super::SelectionOptions::default())
    }

    /// Starts a puzzle by id or lichess.org training link.
    pub fn load_puzzle(&self, id_or_link: &str, replay: bool) -> Result<SessionResult, ServiceError> {
        let _state = self.inner.change_state();
        let store = self.store()?;
        let id = puzzle_id_from(id_or_link);
        let index = store.find(&id).ok_or_else(|| ServiceError::NotFound(id.clone()))?;
        self.start_session(&store, index, replay)
    }

    pub(super) fn start_session(
        &self,
        store: &Store,
        index: usize,
        replay: bool,
    ) -> Result<SessionResult, ServiceError> {
        let puzzle = store.puzzle(index).map_err(ServiceError::Store)?;
        let mut guard = self.inner.session.lock().unwrap();
        if let Some(previous) = guard.as_mut() {
            self.inner.abandon(previous);
        }
        let replay = replay || {
            let players = self.inner.players.lock().unwrap();
            let player = players.current().map_err(player_error)?.id;
            players.has_played(player, &puzzle.id).map_err(player_error)?
        };
        let mut session = Session::new(puzzle).map_err(ServiceError::Session)?;
        session.set_replay(replay);
        let tracker = self.inner.new_tracker(&session, replay)?;
        let active = Active { session, tracker };
        let result = active.result(None);
        *guard = Some(active);
        Ok(result)
    }

    /// Applies a command to the current session, settling the rating and recording history.
    pub fn session(&self, command: super::SessionCommand) -> Result<SessionResult, ServiceError> {
        use super::SessionCommand as C;
        let _state = self.inner.state_gate.lock().unwrap();
        if !matches!(command, C::Get) {
            self.inner.state_revision.fetch_add(1, Ordering::Relaxed);
        }
        let mut guard = self.inner.session.lock().unwrap();
        let active = guard.as_mut().ok_or(ServiceError::Session(SessionError::State("no puzzle is open")))?;
        let session = &mut active.session;
        let outcome = match &command {
            C::Get => None,
            C::PlayOpponent => {
                session.play_opponent().map_err(ServiceError::Session)?;
                None
            }
            C::MakeMove(uci) => Some(session.make_move(uci).map_err(ServiceError::Session)?),
            C::Hint => {
                session.hint().map_err(ServiceError::Session)?;
                None
            }
            C::ShowSolution => {
                session.show_solution().map_err(ServiceError::Session)?;
                None
            }
            C::Retry => {
                self.inner.abandon(active);
                active.session.retry();
                active.tracker = self.inner.new_tracker(&active.session, true)?;
                None
            }
            C::Navigate(ply) => {
                session.navigate(*ply);
                None
            }
        };
        match (&command, outcome) {
            (C::MakeMove(_), Some(MoveOutcome::Wrong)) | (C::Hint, _) => {
                active.tracker.attempted = true;
                self.inner.settle(active, false);
                self.inner.save(active, PuzzleResult::Failed);
            }
            (C::MakeMove(_), Some(MoveOutcome::Correct)) => active.tracker.attempted = true,
            (C::MakeMove(_), Some(MoveOutcome::Solved)) => {
                let clean = active.session.mistakes() == 0 && active.session.hints() == 0;
                active.tracker.attempted = true;
                active.tracker.finished = true;
                self.inner.settle(active, clean);
                self.inner.save(active, if clean { PuzzleResult::Solved } else { PuzzleResult::SolvedWithHelp });
            }
            (C::ShowSolution, _) => {
                active.tracker.attempted = true;
                active.tracker.finished = true;
                self.inner.settle(active, false);
                self.inner.save(active, PuzzleResult::Failed);
            }
            _ => {}
        }
        Ok(active.result(outcome))
    }

    /// Whether new puzzles are rated.
    pub fn set_rated(&self, rated: bool) {
        let _state = self.inner.change_state();
        self.inner.rated.store(rated, Ordering::Relaxed);
    }

    /// Players, the current player and their totals; `day_start_ms` is the start of today.
    pub fn players(&self, day_start_ms: i64) -> Result<PlayersInfo, ServiceError> {
        let players = self.inner.players.lock().unwrap();
        let current = players.current().map_err(player_error)?;
        Ok(PlayersInfo {
            players: players.players().map_err(player_error)?,
            current: current.id,
            stats: players.stats(current.id, day_start_ms).map_err(player_error)?,
            rated: self.inner.rated.load(Ordering::Relaxed),
        })
    }

    pub fn set_current_player(&self, id: i64) -> Result<(), ServiceError> {
        let _state = self.inner.change_state();
        let mut guard = self.inner.session.lock().unwrap();
        {
            let players = self.inner.players.lock().unwrap();
            if players.current().map_err(player_error)?.id == id {
                return Ok(());
            }
            players.set_current(id).map_err(player_error)?;
        }
        if let Some(active) = guard.as_mut() {
            self.inner.abandon(active);
            let mut session = Session::new(active.session.puzzle().clone()).map_err(ServiceError::Session)?;
            let tracker = self.inner.new_tracker(&session, false)?;
            session.set_replay(!tracker.recorded);
            *active = Active { session, tracker };
        }
        *self.inner.played.lock().unwrap() = None;
        Ok(())
    }

    pub fn add_player(&self, name: &str) -> Result<Player, ServiceError> {
        let _state = self.inner.change_state();
        self.inner.players.lock().unwrap().add(name).map_err(player_error)
    }

    pub fn rename_player(&self, id: i64, name: &str) -> Result<Player, ServiceError> {
        let _state = self.inner.change_state();
        self.inner.players.lock().unwrap().rename(id, name).map_err(player_error)
    }

    pub fn delete_player(&self, id: i64) -> Result<(), ServiceError> {
        let _state = self.inner.change_state();
        let mut guard = self.inner.session.lock().unwrap();
        self.inner.players.lock().unwrap().delete(id).map_err(player_error)?;
        if let Some(active) = guard.as_mut()
            && active.tracker.player == id
        {
            let mut session = Session::new(active.session.puzzle().clone()).map_err(ServiceError::Session)?;
            let tracker = self.inner.new_tracker(&session, false)?;
            session.set_replay(!tracker.recorded);
            *active = Active { session, tracker };
        }
        *self.inner.played.lock().unwrap() = None;
        Ok(())
    }

    pub fn clear_history(&self, id: i64) -> Result<u64, ServiceError> {
        let _state = self.inner.change_state();
        let removed = self.inner.players.lock().unwrap().clear_history(id).map_err(player_error)?;
        *self.inner.played.lock().unwrap() = None;
        Ok(removed)
    }

    /// The current player's history, newest first.
    pub fn history(&self, limit: usize) -> Result<Vec<HistoryEntry>, ServiceError> {
        let players = self.inner.players.lock().unwrap();
        let current = players.current().map_err(player_error)?;
        players.history(current.id, limit).map_err(player_error)
    }

    /// The open puzzle as PGN.
    pub fn puzzle_pgn(&self, player_start: bool) -> Result<String, ServiceError> {
        let guard = self.inner.session.lock().unwrap();
        let active = guard.as_ref().ok_or(ServiceError::Session(SessionError::State("no puzzle is open")))?;
        export::puzzle_pgn(active.session.puzzle(), player_start)
            .map_err(|e| ServiceError::Session(SessionError::Line(e)))
    }

    /// Writes puzzles to `path` on the operation thread. Returns the number to be written.
    pub fn start_export(
        &self,
        source: ExportSource,
        format: ExportFormat,
        player_start: bool,
        path: PathBuf,
    ) -> Result<u64, ServiceError> {
        let store = self.store()?;
        let indices: Vec<u32> = match source {
            ExportSource::Matching => filter::matching(&store, &self.compiled(&store)?),
            ExportSource::Played => {
                let mut seen = std::collections::HashSet::new();
                self.history(usize::MAX >> 1)?
                    .into_iter()
                    .filter(|e| seen.insert(e.puzzle_id.clone()))
                    .filter_map(|e| store.find(&e.puzzle_id).map(|i| i as u32))
                    .collect()
            }
        };
        if indices.is_empty() {
            return Err(ServiceError::NoMatches);
        }
        let total = indices.len() as u64;
        self.start_operation(OperationKind::Export, move |inner, op| {
            op.update(|s| s.total = Some(total));
            let mut progress = |n: u64| op.update(|s| s.done = n);
            match export::export(&store, &indices, format, player_start, &path, &mut progress, &op.cancel) {
                Ok(written) => inner.event_with_path(
                    EventLevel::Success,
                    format!("Exported {} puzzles to {}.", group_digits(written), path.display()),
                    EventAction::ShowFile,
                    path.clone(),
                ),
                Err(export::ExportError::Cancelled) => {
                    inner.event(EventLevel::Info, "The export was cancelled.", EventAction::None)
                }
                Err(e) => inner.event(EventLevel::Error, format!("The export failed: {e}."), EventAction::None),
            }
        })?;
        Ok(total)
    }

    /// Analyses `fen` on a background thread for up to `limit`, replacing any running analysis.
    pub fn start_analysis(&self, fen: &str, limit: Duration) -> Result<(), ServiceError> {
        let board = crate::chess::parse_fen(fen).map_err(|e| ServiceError::Session(SessionError::Line(e)))?;
        let mut state = self.inner.analysis.lock().unwrap();
        state.stop.store(true, Ordering::Relaxed);
        state.generation += 1;
        let generation = state.generation;
        let stop = Arc::new(AtomicBool::new(false));
        state.stop = stop.clone();
        state.fen = fen.to_owned();
        state.running = true;
        state.info = None;
        state.terminal_outcome = match board.status() {
            cozy_chess::GameStatus::Ongoing => None,
            cozy_chess::GameStatus::Drawn => Some(AnalysisOutcome::Draw),
            cozy_chess::GameStatus::Won => Some(if board.side_to_move() == cozy_chess::Color::Black {
                AnalysisOutcome::WhiteWins
            } else {
                AnalysisOutcome::BlackWins
            }),
        };
        if state.terminal_outcome.is_some() {
            state.running = false;
            return Ok(());
        }
        drop(state);
        let inner = self.inner.clone();
        std::thread::Builder::new()
            .name("analysis".into())
            .spawn(move || {
                let mut report = |info: &SearchInfo| {
                    let mut state = inner.analysis.lock().unwrap();
                    if state.generation == generation {
                        state.info = Some(info.clone());
                    }
                };
                let _ = engine::analyse(&board, limit, 64, &stop, &mut report);
                let mut state = inner.analysis.lock().unwrap();
                if state.generation == generation {
                    state.running = false;
                }
            })
            .map_err(|e| ServiceError::Io(e.to_string()))?;
        Ok(())
    }

    pub fn analysis(&self) -> AnalysisSnapshot {
        let state = self.inner.analysis.lock().unwrap();
        AnalysisSnapshot {
            fen: state.fen.clone(),
            running: state.running,
            info: state.info.clone(),
            terminal_outcome: state.terminal_outcome,
        }
    }

    pub fn stop_analysis(&self) {
        let mut state = self.inner.analysis.lock().unwrap();
        state.stop.store(true, Ordering::Relaxed);
        state.running = false;
    }
}

impl Active {
    fn result(&self, outcome: Option<MoveOutcome>) -> SessionResult {
        SessionResult {
            outcome,
            view: self.session.view(),
            rated: self.tracker.rated,
            rating_change: self.tracker.rating_change,
        }
    }
}

impl Inner {
    /// Must be called under state_gate. Forecast the loss that Next would settle, without
    /// changing the active attempt, history or database if no eligible candidate is found.
    pub(super) fn selection_player(&self) -> Result<(Player, Option<String>, bool), ServiceError> {
        let session = self.session.lock().unwrap();
        let mut player = self.players.lock().unwrap().current().map_err(player_error)?;
        let current = session.as_ref().map(|a| a.session.puzzle().id.clone());
        let pending = session
            .as_ref()
            .is_some_and(|a| a.tracker.player == player.id && a.tracker.attempted && !a.tracker.finished);
        if let Some(active) = session.as_ref().filter(|a| pending && a.tracker.rated && !a.tracker.settled) {
            let puzzle = active.session.puzzle();
            player.rating = glicko2(player.rating, puzzle.rating.into(), puzzle.deviation.into(), 0.0);
        }
        Ok((player, current, pending))
    }
    fn new_tracker(&self, session: &Session, replay: bool) -> Result<Tracker, ServiceError> {
        let players = self.players.lock().unwrap();
        let player = players.current().map_err(player_error)?.id;
        let played = players.has_played(player, &session.puzzle().id).map_err(player_error)?;
        Ok(Tracker {
            player,
            rated: !replay && !played && self.rated.load(Ordering::Relaxed),
            recorded: !replay && !played,
            entry_id: 0,
            rating_change: None,
            settled: false,
            attempted: false,
            finished: false,
            started: Instant::now(),
            played_ms: now_ms(),
        })
    }

    /// Settles the rated game once; later calls change nothing.
    fn settle(&self, active: &mut Active, won: bool) {
        let tracker = &mut active.tracker;
        if !tracker.rated || tracker.settled {
            return;
        }
        tracker.settled = true;
        let puzzle = active.session.puzzle();
        let players = self.players.lock().unwrap();
        match players.apply_rating(tracker.player, f64::from(puzzle.rating), f64::from(puzzle.deviation), won) {
            Ok(change) => tracker.rating_change = Some(change),
            Err(e) => self.event(EventLevel::Error, format!("The rating could not be saved: {e}."), EventAction::None),
        }
    }

    /// Writes or updates the history entry for the session.
    fn save(&self, active: &mut Active, result: PuzzleResult) {
        let tracker = &mut active.tracker;
        if !tracker.recorded {
            return;
        }
        let puzzle = active.session.puzzle();
        let entry = HistoryEntry {
            id: tracker.entry_id,
            player_id: tracker.player,
            puzzle_id: puzzle.id.clone(),
            played_unix_ms: tracker.played_ms,
            puzzle_rating: puzzle.rating,
            result,
            mistakes: active.session.mistakes(),
            hints: active.session.hints(),
            seconds: tracker.started.elapsed().as_secs_f64(),
            rating_change: tracker.rating_change,
        };
        let first = tracker.entry_id == 0;
        match self.players.lock().unwrap().save_entry(&entry) {
            Ok(id) => tracker.entry_id = id,
            Err(e) => self.event(EventLevel::Error, format!("The history could not be saved: {e}."), EventAction::None),
        }
        if first {
            *self.played.lock().unwrap() = None;
        }
    }

    /// Records a puzzle left unfinished after an attempt as failed.
    fn abandon(&self, active: &mut Active) {
        if active.tracker.attempted && !active.tracker.finished {
            active.tracker.finished = true;
            self.settle(active, false);
            self.save(active, PuzzleResult::Failed);
        }
    }
}
