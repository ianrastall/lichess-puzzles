//! Conversions between domain types and the protobuf messages.

use lichess_puzzles_core::Side;
use lichess_puzzles_core::filter::{Counts, Filter};
use lichess_puzzles_core::service::{
    Catalogue, CatalogueOpening, Event, EventAction, EventLevel, OperationKind, OperationStatus, Status,
};
use lichess_puzzles_core::session::{MoveOutcome, SessionState, SessionView};
use lichess_puzzles_core::source::RemoteVersion;
use lichess_puzzles_core::store::Puzzle;

use crate::messages as m;

fn side(side: Side) -> i32 {
    match side {
        Side::White => m::Side::White as i32,
        Side::Black => m::Side::Black as i32,
    }
}

fn version(v: &RemoteVersion) -> m::RemoteVersion {
    m::RemoteVersion {
        etag: v.etag.clone().unwrap_or_default(),
        last_modified: v.last_modified.clone().unwrap_or_default(),
        length: v.length.unwrap_or(0),
    }
}

/// Zero bounds mean "no bound"; out-of-range numbers are clamped.
pub fn filter_from_message(f: &m::Filter) -> Filter {
    let bound = |v: u32, max: u32| (v > 0).then(|| v.min(max));
    Filter {
        rating_min: bound(f.rating_min, u16::MAX as u32).map(|v| v as u16),
        rating_max: bound(f.rating_max, u16::MAX as u32).map(|v| v as u16),
        max_deviation: bound(f.max_deviation, u16::MAX as u32).map(|v| v as u16),
        min_popularity: f.has_min_popularity.then(|| f.min_popularity.clamp(i8::MIN as i32, i8::MAX as i32) as i8),
        min_plays: (f.min_plays > 0).then_some(f.min_plays),
        themes_any: f.themes_any.clone(),
        themes_all: f.themes_all.clone(),
        themes_none: f.themes_none.clone(),
        openings: f.openings.clone(),
        solver: match m::Side::try_from(f.solver) {
            Ok(m::Side::White) => Some(Side::White),
            Ok(m::Side::Black) => Some(Side::Black),
            _ => None,
        },
        min_solution_moves: bound(f.min_solution_moves, 127).map(|v| v as u8),
        max_solution_moves: bound(f.max_solution_moves, 127).map(|v| v as u8),
    }
}

pub fn counts_to_message(c: &Counts) -> m::Counts {
    let wide = |v: &[usize]| v.iter().map(|&n| n as u64).collect();
    m::Counts {
        total: c.total as u64,
        themes: wide(&c.themes),
        families: wide(&c.families),
        variations: wide(&c.variations),
    }
}

fn operation(o: &OperationStatus) -> m::Operation {
    m::Operation {
        kind: match o.kind {
            OperationKind::Download => m::OperationKind::Download,
            OperationKind::Build => m::OperationKind::Build,
            OperationKind::CheckUpdate => m::OperationKind::CheckUpdate,
        } as i32,
        cancelling: o.cancelling,
        done: o.done,
        total: o.total.unwrap_or(0),
        rate: o.rate,
        rows: o.rows,
    }
}

pub fn status_to_message(s: &Status) -> m::Status {
    m::Status {
        data_dir: s.data_dir.to_string_lossy().into_owned(),
        store: s.store.as_ref().map(|store| m::StoreSummary {
            path: store.path.to_string_lossy().into_owned(),
            puzzle_count: store.puzzle_count,
            file_bytes: store.file_bytes,
            built_at_unix: store.built_at_unix as i64,
            source_file: store.source.file_name.clone(),
            source_bytes: store.source.bytes,
            source_version: store.source.version.as_ref().map(version),
            skipped_count: store.skipped_count,
            rating_min: u32::from(store.rating_min),
            rating_max: u32::from(store.rating_max),
        }),
        store_problem: s.store_problem.clone().unwrap_or_default(),
        operation: s.operation.as_ref().map(operation),
        partial_bytes: s.partial_download.map_or(0, |p| p.0),
        partial_total: s.partial_download.and_then(|p| p.1).unwrap_or(0),
        download: s.download.as_ref().map(version),
        update_available: s.update_available.as_ref().map(version),
        update_ready: s.update_ready,
    }
}

fn opening(o: &CatalogueOpening) -> m::CatalogueOpening {
    m::CatalogueOpening { id: o.id, tag: o.tag.clone(), name: o.name.clone(), family: o.family, count: o.count }
}

pub fn catalogue_to_message(c: &Catalogue) -> m::Catalogue {
    m::Catalogue {
        themes: c
            .themes
            .iter()
            .map(|t| m::CatalogueTheme {
                key: t.key.clone(),
                bit: t.bit,
                group: t.group.clone(),
                name: t.name.clone(),
                description: t.description.clone(),
                count: t.count,
            })
            .collect(),
        groups: c.groups.clone(),
        families: c.families.iter().map(opening).collect(),
        variations: c.variations.iter().map(opening).collect(),
        rating_min: u32::from(c.rating_min),
        rating_max: u32::from(c.rating_max),
    }
}

fn puzzle(p: &Puzzle) -> m::Puzzle {
    m::Puzzle {
        index: p.index as u64,
        id: p.id.clone(),
        fen: p.fen.clone(),
        moves: p.moves.clone(),
        rating: u32::from(p.rating),
        deviation: u32::from(p.deviation),
        popularity: i32::from(p.popularity),
        plays: p.plays,
        themes: p.themes.clone(),
        opening_family: p.opening_family.clone().unwrap_or_default(),
        opening_variation: p.opening_variation.clone().unwrap_or_default(),
        game_url: p.game_url.clone(),
        daily_date: p.daily_date.unwrap_or(0),
        solver: side(p.solver),
    }
}

pub fn session_to_message(outcome: Option<MoveOutcome>, v: &SessionView) -> m::SessionResult {
    let square = |s: Option<u8>| s.map_or(-1, i32::from);
    m::SessionResult {
        outcome: match outcome {
            None => m::MoveOutcome::Unspecified,
            Some(MoveOutcome::Illegal) => m::MoveOutcome::Illegal,
            Some(MoveOutcome::Wrong) => m::MoveOutcome::Wrong,
            Some(MoveOutcome::Correct) => m::MoveOutcome::Correct,
            Some(MoveOutcome::Solved) => m::MoveOutcome::Solved,
        } as i32,
        view: Some(m::SessionView {
            puzzle: Some(puzzle(&v.puzzle)),
            state: match v.state {
                SessionState::OpponentToMove => m::SessionState::OpponentToMove,
                SessionState::Solving => m::SessionState::Solving,
                SessionState::ReplyPending => m::SessionState::ReplyPending,
                SessionState::Solved => m::SessionState::Solved,
                SessionState::SolutionShown => m::SessionState::SolutionShown,
            } as i32,
            mistakes: v.mistakes,
            hints: v.hints,
            solution_shown: v.solution_shown,
            replay: v.replay,
            moves: v
                .moves
                .iter()
                .map(|e| m::MoveEntry {
                    san: e.san.clone(),
                    uci: e.uci.clone(),
                    solver: e.solver,
                    white: e.white,
                    number: e.number,
                })
                .collect(),
            played: v.played as u32,
            view: v.view as u32,
            board: v.board.clone(),
            fen: v.fen.clone(),
            side_to_move: side(v.side_to_move),
            last_from: square(v.last_move.map(|m| m.0)),
            last_to: square(v.last_move.map(|m| m.1)),
            check_square: square(v.check_square),
            hint_square: square(v.hint_square),
            legal_moves: v.legal_moves.clone(),
        }),
    }
}

pub fn event_to_message(e: &Event) -> m::Event {
    m::Event {
        level: match e.level {
            EventLevel::Info => m::EventLevel::Info,
            EventLevel::Success => m::EventLevel::Success,
            EventLevel::Warning => m::EventLevel::Warning,
            EventLevel::Error => m::EventLevel::Error,
        } as i32,
        message: e.message.clone(),
        action: match e.action {
            EventAction::None => m::EventAction::None,
            EventAction::RetryDownload => m::EventAction::RetryDownload,
            EventAction::RetryBuild => m::EventAction::RetryBuild,
            EventAction::RetryCheck => m::EventAction::RetryCheck,
            EventAction::SwitchStore => m::EventAction::SwitchStore,
        } as i32,
        unix_ms: e.unix_ms as i64,
    }
}
