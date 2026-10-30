//! Candidate preparation is independent of attempts: no ratings/history changes until a
//! selected puzzle is actually started. Cached candidates are rechecked against current
//! filters, player history and rating bounds before use.

use std::collections::VecDeque;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Weak};
use std::time::{Duration, Instant};

use super::{Service, ServiceError, SessionResult};
use crate::filter::{self, CompiledFilter};
use crate::screening::{self, ScreeningCache, Verdict};
use crate::store::Store;

const BATCH: usize = 128;
const BATCH_TIME: Duration = Duration::from_millis(750);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SelectionOptions {
    pub rating_window: u16,
    pub exclude_short_mates: u8,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::build::{BuildOptions, build_store};
    use crate::filter::Filter;
    use crate::players::{Rating, glicko2};
    use crate::service::{NEXT_STORE_FILE, ServiceConfig, SessionCommand};
    use std::sync::atomic::AtomicBool;

    fn sample() -> String {
        include_str!("../../tests/fixtures/sample_puzzles.csv").to_owned()
    }

    fn setup(csv: &str) -> (tempfile::TempDir, Service) {
        let dir = tempfile::tempdir().unwrap();
        let input = dir.path().join("sample.csv");
        std::fs::write(&input, csv).unwrap();
        build_store(
            &input,
            &dir.path().join(crate::STORE_FILE),
            None,
            &BuildOptions::default(),
            &mut |_| {},
            &AtomicBool::new(false),
        )
        .unwrap();
        let service = Service::open(ServiceConfig {
            data_dir: dir.path().into(),
            source_url: "http://127.0.0.1:9/unused".into(),
            default_player: "Tester".into(),
        })
        .unwrap();
        (dir, service)
    }

    fn exact(rating: u16) -> Filter {
        Filter { rating_min: Some(rating), rating_max: Some(rating), ..Default::default() }
    }

    #[test]
    fn next_uses_projected_abandonment_rating_and_failed_selection_changes_nothing() {
        let projected = glicko2(Rating::default(), 700.0, 76.0, 0.0).rating;
        assert!((projected - 1500.0).abs() > 150.0);
        let csv = sample()
            .lines()
            .map(|row| {
                let mut fields: Vec<String> = row.split(',').map(str::to_owned).collect();
                if fields[0] == "00008" {
                    fields[3] = "700".into();
                }
                if fields[0] == "0009B" {
                    fields[3] = (projected.round() as u16).to_string();
                }
                fields.join(",")
            })
            .collect::<Vec<_>>()
            .join("\n");
        let (_dir, service) = setup(&csv);
        let old = service.load_puzzle("00008", false).unwrap();
        service.session(SessionCommand::PlayOpponent).unwrap();
        service.session(SessionCommand::MakeMove(old.view.puzzle.moves[1].clone())).unwrap();
        let before = service.players(0).unwrap();
        let session = service.session(SessionCommand::Get).unwrap();
        service.set_filter(exact(1500), true).unwrap();
        assert!(matches!(
            service.next_selected(Some(0), SelectionOptions::new(75, 0)),
            Err(ServiceError::Selection(_))
        ));
        assert_eq!(service.players(0).unwrap(), before);
        assert_eq!(service.session(SessionCommand::Get).unwrap(), session);
        assert!(service.history(100).unwrap().is_empty());

        service.set_filter(exact(projected.round() as u16), true).unwrap();
        let next = service.next_selected(Some(0), SelectionOptions::new(75, 0)).unwrap();
        let after = service.players(0).unwrap().players[0].clone();
        assert!((after.rating.rating - projected).abs() < 1e-8);
        assert_eq!(after.games, 1, "abandonment settles once, only after a valid candidate exists");
        assert_eq!(next.view.puzzle.id, "0009B");
        assert!((next.view.puzzle.rating as f64 - after.rating.rating.round()).abs() <= 75.0);
    }

    #[test]
    fn changed_filter_is_retried_at_session_commit() {
        let (_dir, service) = setup(&sample());
        service.set_filter(exact(1529), false).unwrap();
        let mut changed = false;
        let result = service
            .next_selected_checked(None, SelectionOptions::default(), || {
                if !changed {
                    changed = true;
                    service.set_filter(exact(1084), false).unwrap();
                }
            })
            .unwrap();
        assert_eq!(result.view.puzzle.id, "0009B");
        assert!(service.history(100).unwrap().is_empty());
    }

    #[test]
    fn changed_player_is_retried_with_their_rating() {
        let target = glicko2(Rating::default(), 700.0, 76.0, 0.0).rating.round() as u16;
        let csv = sample()
            .lines()
            .map(|row| {
                let mut fields: Vec<String> = row.split(',').map(str::to_owned).collect();
                if fields[0] == "0009B" {
                    fields[3] = target.to_string();
                }
                fields.join(",")
            })
            .collect::<Vec<_>>()
            .join("\n");
        let (_dir, service) = setup(&csv);
        let guest = service.add_player("Guest").unwrap();
        {
            let _state = service.inner.change_state();
            service.inner.players.lock().unwrap().apply_rating(guest.id, 700.0, 76.0, false).unwrap();
        }
        let mut changed = false;
        let result = service
            .next_selected_checked(Some(0), SelectionOptions::new(75, 2), || {
                if !changed {
                    changed = true;
                    service.set_current_player(guest.id).unwrap();
                }
            })
            .unwrap();
        assert_eq!(result.view.puzzle.id, "0009B");
        assert!((i32::from(result.view.puzzle.rating) - i32::from(target)).abs() <= 75);
        assert_eq!(service.players(0).unwrap().current, guest.id);
    }

    #[test]
    fn store_replacement_before_commit_drops_readers_and_retries() {
        let (dir, service) = setup(&sample());
        let csv = sample();
        let next =
            format!("{}\n{}\n", csv.lines().next().unwrap(), csv.lines().find(|r| r.starts_with("0009B,")).unwrap());
        let input = dir.path().join("replacement.csv");
        std::fs::write(&input, next).unwrap();
        build_store(
            &input,
            &dir.path().join(NEXT_STORE_FILE),
            None,
            &BuildOptions::default(),
            &mut |_| {},
            &AtomicBool::new(false),
        )
        .unwrap();
        let mut changed = false;
        let result = service
            .next_selected_checked(None, SelectionOptions::default(), || {
                if !changed {
                    changed = true;
                    service.switch_store().unwrap();
                }
            })
            .unwrap();
        assert_eq!(result.view.puzzle.id, "0009B");
        assert_eq!(service.status().store.unwrap().puzzle_count, 1);
    }
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

/// Inputs captured together under state_gate. Only the snapshot owns a strong store reference.
struct SelectionSnapshot {
    store: Arc<Store>,
    revision: u64,
    compiled: CompiledFilter,
    current: Option<usize>,
    key: String,
    near_rating: bool,
    options: SelectionOptions,
}

impl Service {
    /// Selects, screens, then starts a puzzle. Metadata counts remain counts before screening.
    pub fn next_selected(
        &self,
        near_offset: Option<i32>,
        options: SelectionOptions,
    ) -> Result<SessionResult, ServiceError> {
        self.next_selected_checked(near_offset, options, || {})
    }

    fn next_selected_checked(
        &self,
        near_offset: Option<i32>,
        options: SelectionOptions,
        mut before_commit: impl FnMut(),
    ) -> Result<SessionResult, ServiceError> {
        for _ in 0..5 {
            let snapshot = self.selection_snapshot(near_offset, options)?;
            let revision = snapshot.revision;
            let candidate = self.select_index(&snapshot, true);
            // Store replacement waits for mapped readers. Drop ours before acquiring the
            // commit gate, otherwise replacement could hold that gate waiting on this reader.
            drop(snapshot);
            before_commit();
            let _state = self.inner.state_gate.lock().unwrap();
            if revision != self.inner.state_revision.load(Ordering::Relaxed) {
                continue;
            }
            let index = candidate?;
            // No eligibility input can change between this check and session creation.
            let store = self.store()?;
            self.inner.state_revision.fetch_add(1, Ordering::Relaxed);
            return self.start_session(&store, index, false);
        }
        Err(ServiceError::Selection(
            "The player or puzzle selection changed while preparing a puzzle. Try Next puzzle again.".into(),
        ))
    }

    /// Prepares without settling the active attempt. Next revalidates every input at commit.
    pub fn prepare_next(&self, near_offset: Option<i32>, options: SelectionOptions) -> Result<(), ServiceError> {
        if options.exclude_short_mates > 0 {
            self.select_index(&self.selection_snapshot(near_offset, options)?, false)?;
        }
        Ok(())
    }

    fn selection_snapshot(
        &self,
        near_offset: Option<i32>,
        options: SelectionOptions,
    ) -> Result<SelectionSnapshot, ServiceError> {
        let _state = self.inner.state_gate.lock().unwrap();
        let store = self.store()?;
        let options = SelectionOptions::new(options.rating_window.into(), options.exclude_short_mates.into());
        let (player, current_id, pending_attempt) = self.inner.selection_player()?;
        let current = current_id.and_then(|id| store.find(&id));
        let filter = self.inner.filter.lock().unwrap().clone();
        let skip_played = self.inner.skip_played.load(Ordering::Relaxed);
        let mut compiled = filter.compile(&store).map_err(ServiceError::Filter)?;
        if skip_played {
            let played = self.played_set(&store)?;
            if let Some(index) = current.filter(|_| pending_attempt) {
                // Abandonment will add this attempt to history at commit.
                let mut bits = (*played).clone();
                bits[index / 64] |= 1 << (index % 64);
                compiled = compiled.excluding(Arc::new(bits));
            } else {
                compiled = compiled.excluding(played);
            }
        }
        if let Some(offset) = near_offset {
            let target = (player.rating.rating.round() as i32).saturating_add(offset.clamp(-600, 600));
            let window = i32::from(options.rating_window);
            compiled = compiled.within_ratings(
                target.saturating_sub(window).clamp(0, u16::MAX as i32) as u16,
                target.saturating_add(window).clamp(0, u16::MAX as i32) as u16,
            );
        }
        let key = format!(
            "{:p}:{}:{:?}:{}:{}:{}:{}",
            Arc::as_ptr(&store),
            player.id,
            near_offset,
            options.rating_window,
            options.exclude_short_mates,
            skip_played,
            serde_json::to_string(&filter).expect("filter serialization")
        );
        Ok(SelectionSnapshot {
            store,
            compiled,
            current,
            key,
            near_rating: near_offset.is_some(),
            options,
            revision: self.inner.state_revision.load(Ordering::Relaxed),
        })
    }

    fn select_index(&self, snapshot: &SelectionSnapshot, consume: bool) -> Result<usize, ServiceError> {
        let SelectionSnapshot { store, compiled, current, key, options, .. } = snapshot;
        let seed = self.inner.seed.fetch_add(0x9E37_79B9_7F4A_7C15, Ordering::Relaxed);
        let empty = || {
            if snapshot.near_rating {
                ServiceError::Selection(format!(
                    "No puzzles match within ±{} of the target rating. Change the rating range or filters; the app will not widen them automatically.",
                    options.rating_window
                ))
            } else {
                ServiceError::NoMatches
            }
        };
        if options.exclude_short_mates == 0 {
            return filter::pick(store, compiled, seed).ok_or_else(empty);
        }

        let mut state = self.inner.selection.lock().unwrap();
        let identity = Arc::downgrade(store);
        if state.key != *key || !state.store.ptr_eq(&identity) {
            state.store = identity;
            state.key = key.clone();
            state.candidates.clear();
            state.ready = None;
        }
        let acceptable = |i: usize| *current != Some(i) && compiled.accepts(i, &store.record(i));
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
                state.candidates = filter::sample(store, compiled, BATCH, seed).into();
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
