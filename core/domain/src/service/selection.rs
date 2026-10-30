//! Candidate preparation is independent of attempts: no ratings/history changes until a
//! selected puzzle is actually started. Cached candidates are rechecked against current
//! filters, player history and rating bounds before use.

use std::collections::VecDeque;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Weak};
use std::time::{Duration, Instant};

use super::{Service, ServiceError, SessionResult};
use crate::filter;
use crate::screening::{self, ScreeningCache, Verdict};
use crate::store::Store;

const BATCH: usize = 128;
const BATCH_TIME: Duration = Duration::from_millis(750);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectionOptions {
    pub rating_window: u16,
    pub exclude_short_mates: u8,
}

impl Default for SelectionOptions {
    fn default() -> Self {
        Self { rating_window: 150, exclude_short_mates: 0 }
    }
}

impl SelectionOptions {
    pub fn new(window: u32, mates: u32) -> Self {
        Self {
            rating_window: if window == 0 { 150 } else { window.min(600) as u16 },
            exclude_short_mates: mates.min(2) as u8,
        }
    }
}

#[derive(Default)]
pub(super) struct SelectionState {
    store: Weak<Store>,
    key: String,
    candidates: VecDeque<usize>,
    ready: Option<usize>,
    cache: Option<ScreeningCache>,
}

impl Service {
    /// Selects, screens, then starts a puzzle. Metadata counts remain counts before screening.
    pub fn next_selected(
        &self,
        near_offset: Option<i32>,
        options: SelectionOptions,
    ) -> Result<SessionResult, ServiceError> {
        let store = self.store()?;
        let index = self.select_index(&store, near_offset, options, true)?;
        self.start_session(&store, index, false)
    }

    /// Prepares one candidate on the caller's worker thread, without starting a session.
    pub fn prepare_next(&self, near_offset: Option<i32>, options: SelectionOptions) -> Result<(), ServiceError> {
        if options.exclude_short_mates > 0 {
            self.select_index(&self.store()?, near_offset, options, false)?;
        }
        Ok(())
    }

    fn select_index(
        &self,
        store: &Arc<Store>,
        near_offset: Option<i32>,
        options: SelectionOptions,
        consume: bool,
    ) -> Result<usize, ServiceError> {
        let options = SelectionOptions::new(options.rating_window.into(), options.exclude_short_mates.into());
        let player = self.inner.players.lock().unwrap().current().map_err(ServiceError::Player)?;
        let filter = self.inner.filter.lock().unwrap().clone();
        let skip_played = self.inner.skip_played.load(Ordering::Relaxed);
        let mut compiled = filter.compile(store).map_err(ServiceError::Filter)?;
        if skip_played {
            compiled = compiled.excluding(self.played_set(store)?);
        }
        if let Some(offset) = near_offset {
            let target = (player.rating.rating.round() as i32).saturating_add(offset.clamp(-600, 600));
            let window = i32::from(options.rating_window);
            compiled = compiled.within_ratings(
                target.saturating_sub(window).clamp(0, u16::MAX as i32) as u16,
                target.saturating_add(window).clamp(0, u16::MAX as i32) as u16,
            );
        }
        let seed = self.inner.seed.fetch_add(0x9E37_79B9_7F4A_7C15, Ordering::Relaxed);
        let empty = || {
            if near_offset.is_some() {
                ServiceError::Selection(format!(
                    "No puzzles match within ±{} of the target rating. Change the rating range or filters; the app will not widen them automatically.",
                    options.rating_window
                ))
            } else {
                ServiceError::NoMatches
            }
        };
        if options.exclude_short_mates == 0 {
            return filter::pick(store, &compiled, seed).ok_or_else(empty);
        }

        let current = self
            .inner
            .session
            .lock()
            .unwrap()
            .as_ref()
            .map(|a| a.session.puzzle().id.clone())
            .and_then(|id| store.find(&id));
        let key = format!(
            "{:p}:{}:{:?}:{}:{}:{}:{}",
            Arc::as_ptr(store),
            player.id,
            near_offset,
            options.rating_window,
            options.exclude_short_mates,
            skip_played,
            serde_json::to_string(&filter).expect("filter serialization")
        );
        let mut state = self.inner.selection.lock().unwrap();
        let identity = Arc::downgrade(store);
        if state.key != key || !state.store.ptr_eq(&identity) {
            state.store = identity;
            state.key = key;
            state.candidates.clear();
            state.ready = None;
        }
        let acceptable = |i: usize| current != Some(i) && compiled.accepts(i, &store.record(i));
        if let Some(index) = state.ready.take().filter(|&i| acceptable(i)) {
            if !consume {
                state.ready = Some(index);
            }
            return Ok(index);
        }
        if state.cache.is_none() {
            state.cache = Some(
                ScreeningCache::open(&self.inner.data_dir.join("screening.sqlite"))
                    .map_err(|e| ServiceError::Io(format!("The screening cache could not be opened: {e}")))?,
            );
        }
        let mut refilled = false;
        let deadline = Instant::now() + BATCH_TIME;
        while Instant::now() < deadline {
            if state.candidates.is_empty() && !refilled {
                state.candidates = filter::sample(store, &compiled, BATCH, seed).into();
                refilled = true;
                if state.candidates.is_empty() {
                    return Err(empty());
                }
            }
            let Some(index) = state.candidates.pop_front() else { break };
            if !acceptable(index) {
                continue;
            }
            let puzzle = store.puzzle(index).map_err(ServiceError::Store)?;
            let board = screening::solver_board(&puzzle).map_err(|e| ServiceError::Io(e.to_string()))?;
            let (verdict, _) = state
                .cache
                .as_ref()
                .unwrap()
                .inspect(&board)
                .map_err(|e| ServiceError::Io(format!("The screening result could not be saved: {e}")))?;
            let accepted = match verdict {
                Verdict::Mate(n) => n > options.exclude_short_mates,
                Verdict::NoShortMate => true,
                Verdict::Inconclusive => false,
            };
            if accepted {
                if !consume {
                    state.ready = Some(index);
                }
                return Ok(index);
            }
        }
        Err(ServiceError::Selection("No puzzle passed screening in this batch. Try Next puzzle again, change the filters, or turn off short-mate screening. Unfinished checks are not treated as evidence that a puzzle is difficult.".into()))
    }
}
