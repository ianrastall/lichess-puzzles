//! A second-engine opinion, separate from exact mate proof and human puzzle ratings.
//! Searches without supplying the solution to the engine, then compares every solver turn
//! along the supplied line. The supplied defences are not independently certified optimal.

use std::path::Path;
use std::sync::atomic::AtomicBool;
use std::time::{Duration, Instant};

use cozy_chess::{Board, GameStatus, Move};
use rusqlite::{Connection, OptionalExtension, params};
use serde::{Deserialize, Serialize};

use crate::{chess, engine, store::Puzzle};

// Bump when engine search/evaluation, comparison semantics, or budgets change.
pub const VERSION: u32 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct ReviewLimits {
    pub depth: u32,
    pub nodes: u64,
    pub millis: u64,
}

impl Default for ReviewLimits {
    fn default() -> Self {
        Self { depth: 4, nodes: 50_000, millis: 75 }
    }
}

impl ReviewLimits {
    fn validate(self) -> Result<(), String> {
        if !(1..=12).contains(&self.depth)
            || !(1..=1_000_000).contains(&self.nodes)
            || !(1..=2000).contains(&self.millis)
        {
            return Err("review limits: depth 1–12, nodes 1–1000000, milliseconds 1–2000".into());
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Agreement {
    SameMove,
    /// Both the recorded move and the engine move immediately checkmate.
    EquivalentMate,
    /// A different recommendation is not proof that the recorded solution is wrong.
    DifferentMove,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DepthReview {
    pub depth: u32,
    pub best_move: String,
    pub agreement: Agreement,
    /// Both scores use the solver's point of view. Exactly one is present.
    pub centipawns: Option<i32>,
    pub mate: Option<i32>,
    pub nodes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MoveReview {
    /// One-based ply in the original CSV line (2 is the first solver move).
    pub ply: usize,
    pub expected_move: String,
    pub iterations: Vec<DepthReview>,
    /// First completed depth after which every remaining completed depth agrees. Only
    /// reports a suffix with at least two observations; horizon effects can still remain.
    pub stable_from_depth: Option<u32>,
    pub nodes: u64,
    pub budget_exhausted: bool,
    pub complete: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ReviewStatus {
    /// Every solver turn was searched without exhausting its budget, and the final
    /// recommendation agrees. This says nothing about human difficulty or best defence.
    Agreement,
    /// At least one final completed iteration recommends another move.
    DifferentMove,
    /// There is too little completed work to give an opinion for the whole line.
    Inconclusive,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct EngineReview {
    pub version: u32,
    pub limits: ReviewLimits,
    pub status: ReviewStatus,
    pub moves: Vec<MoveReview>,
    pub nodes: u64,
    pub elapsed_ms: f64,
    /// True only when every solver turn reached the requested depth or an earlier mate
    /// termination, without exhausting its share of the budget. Only these are cached.
    pub complete: bool,
}

impl EngineReview {
    /// A conservative search-effort signal, not a human difficulty judgment. Require a
    /// completed whole-line review and agreement at every observed depth, with at least
    /// two completed observations on every solver turn. Never reject on disagreement.
    pub fn shallow_agreement(&self) -> bool {
        self.complete
            && !self.moves.is_empty()
            && self.status == ReviewStatus::Agreement
            && self.moves.iter().all(|m| m.stable_from_depth == Some(1))
    }
}

fn comparison(board: &Board, expected: Move, best_uci: &str) -> Agreement {
    let Ok(line) = chess::replay(board, &[best_uci]) else { return Agreement::DifferentMove };
    if line[0] == expected {
        return Agreement::SameMove;
    }
    let mut recorded = board.clone();
    recorded.play(expected);
    let mut suggested = board.clone();
    suggested.play(line[0]);
    if recorded.status() == GameStatus::Won && suggested.status() == GameStatus::Won {
        Agreement::EquivalentMate
    } else {
        Agreement::DifferentMove
    }
}

fn stable_depth(iterations: &[DepthReview]) -> Option<u32> {
    let start = iterations.iter().rposition(|i| i.agreement == Agreement::DifferentMove).map_or(0, |i| i + 1);
    (iterations.len() - start >= 2).then(|| iterations[start].depth)
}

/// Reviews all solver moves, with one total node/time budget divided among them. A quick
/// first move alone cannot qualify the whole puzzle. No source, history or rating is changed.
pub fn inspect(puzzle: &Puzzle, limits: ReviewLimits) -> Result<EngineReview, String> {
    limits.validate()?;
    let started = Instant::now();
    let mut board = chess::parse_fen(&puzzle.fen).map_err(|e| e.to_string())?;
    let uci: Vec<&str> = puzzle.moves.iter().map(String::as_str).collect();
    let line = chess::replay(&board, &uci).map_err(|e| e.to_string())?;
    if line.len() < 2 {
        return Err("a review needs a setup move and at least one solver move".into());
    }
    let total = line.len() / 2;
    let mut reviews = Vec::with_capacity(total);
    let mut nodes = 0;
    let stop = AtomicBool::new(false);
    let duration = Duration::from_millis(limits.millis);
    for (i, &mv) in line.iter().enumerate() {
        if i % 2 == 1 {
            let remaining = (total - reviews.len()) as u32;
            let time = duration.saturating_sub(started.elapsed()) / remaining;
            let allowance = limits.nodes.saturating_sub(nodes) / u64::from(remaining);
            let search = engine::probe(&board, allowance, time, limits.depth, &stop);
            let sign = if board.side_to_move() == cozy_chess::Color::White { 1 } else { -1 };
            let iterations: Vec<_> = search
                .iterations
                .iter()
                .map(|info| {
                    let (centipawns, mate) = match info.score {
                        engine::Score::Centipawns(cp) => (Some(sign * cp), None),
                        engine::Score::Mate(m) => (None, Some(sign * m)),
                    };
                    DepthReview {
                        depth: info.depth,
                        best_move: info.pv_uci[0].clone(),
                        agreement: comparison(&board, mv, &info.pv_uci[0]),
                        centipawns,
                        mate,
                        nodes: info.nodes,
                    }
                })
                .collect();
            let stable_from_depth = stable_depth(&iterations);
            reviews.push(MoveReview {
                ply: i + 1,
                expected_move: uci[i].into(),
                iterations,
                stable_from_depth,
                nodes: search.nodes,
                budget_exhausted: search.budget_exhausted,
                complete: search.complete,
            });
            nodes += search.nodes;
        }
        board.play(mv);
    }
    let complete = reviews.iter().all(|m| m.complete);
    let status = if reviews.iter().filter_map(|m| m.iterations.last()).any(|i| i.agreement == Agreement::DifferentMove)
    {
        ReviewStatus::DifferentMove
    } else if complete {
        ReviewStatus::Agreement
    } else {
        ReviewStatus::Inconclusive
    };
    Ok(EngineReview {
        version: VERSION,
        limits,
        status,
        moves: reviews,
        nodes,
        elapsed_ms: started.elapsed().as_secs_f64() * 1000.0,
        complete,
    })
}

pub struct ReviewCache {
    conn: Connection,
}

impl ReviewCache {
    pub fn open(path: &Path) -> Result<Self, String> {
        let conn = Connection::open(path).map_err(|e| e.to_string())?;
        // Disposable derived data: WAL/NORMAL avoids a durable disk flush for every
        // reviewed puzzle. A power loss may discard recent cache entries; they rebuild.
        // This connection must never be used for player history or the source database.
        conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;").map_err(|e| e.to_string())?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS engine_reviews (
             identity TEXT PRIMARY KEY NOT NULL, result TEXT NOT NULL);",
        )
        .map_err(|e| e.to_string())?;
        Ok(Self { conn })
    }

    pub fn inspect(&self, puzzle: &Puzzle, limits: ReviewLimits) -> Result<(EngineReview, bool), String> {
        limits.validate()?;
        // The full FEN, full line, engine version and every budget parameter are identity.
        // Ratings/IDs can change without changing chess; identical positions with different
        // continuations must not share the result of a whole-line comparison.
        let identity =
            serde_json::to_string(&(VERSION, &puzzle.fen, &puzzle.moves, limits)).map_err(|e| e.to_string())?;
        let cached: Option<String> = self
            .conn
            .query_row("SELECT result FROM engine_reviews WHERE identity=?1", [&identity], |r| r.get(0))
            .optional()
            .map_err(|e| e.to_string())?;
        if let Some(cached) = cached {
            return Ok((serde_json::from_str(&cached).map_err(|e| e.to_string())?, true));
        }
        let review = inspect(puzzle, limits)?;
        if review.complete {
            self.conn
                .execute(
                    "INSERT OR REPLACE INTO engine_reviews(identity,result) VALUES(?1,?2)",
                    params![identity, serde_json::to_string(&review).map_err(|e| e.to_string())?],
                )
                .map_err(|e| e.to_string())?;
        }
        Ok((review, false))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn puzzle(fen: &str, moves: &[&str]) -> Puzzle {
        Puzzle {
            index: 0,
            id: "test".into(),
            fen: fen.into(),
            moves: moves.iter().map(|s| (*s).into()).collect(),
            rating: 1500,
            deviation: 80,
            popularity: 100,
            plays: 100,
            themes: Vec::new(),
            opening_family: None,
            opening_variation: None,
            game_url: String::new(),
            daily_date: None,
            solver: crate::Side::White,
        }
    }

    fn mate() -> Puzzle {
        puzzle("6k1/5ppp/8/8/8/8/8/1R2R1K1 b - - 0 1", &["g8h8", "e1e8"])
    }

    fn generous() -> ReviewLimits {
        ReviewLimits { depth: 3, nodes: 1_000_000, millis: 2000 }
    }

    #[test]
    fn mating_alternatives_and_black_score_orientation() {
        let p = mate();
        let board = crate::screening::solver_board(&p).unwrap();
        let expected = chess::replay(&board, &["e1e8"]).unwrap()[0];
        assert_eq!(comparison(&board, expected, "b1b8"), Agreement::EquivalentMate);
        let result = inspect(&p, generous()).unwrap();
        assert_eq!(result.status, ReviewStatus::Agreement);
        assert!(result.complete);
        assert!(result.shallow_agreement());
        assert_eq!(result.moves[0].iterations.last().unwrap().mate, Some(1));

        let black = puzzle("1r2r1k1/8/8/8/8/8/5PPP/6K1 w - - 0 1", &["g1h1", "e8e1"]);
        let result = inspect(&black, generous()).unwrap();
        assert_eq!(result.status, ReviewStatus::Agreement);
        assert_eq!(result.moves[0].iterations.last().unwrap().mate, Some(1), "solver-relative mate");
    }

    #[test]
    fn checks_the_continuation_and_shares_one_node_budget() {
        let p = puzzle("4k3/8/8/3q4/8/8/3R4/3K4 b - - 0 1", &["e8f8", "d2d5", "f8g8", "d5d4"]);
        let result = inspect(&p, generous()).unwrap();
        assert_eq!(result.moves.len(), 2);
        assert_eq!(result.moves[0].ply, 2);
        assert_eq!(result.moves[1].ply, 4);
        assert_eq!(result.moves[0].iterations.last().unwrap().agreement, Agreement::SameMove);
        // A perfectly legal but different supplied continuation is reviewed, not silently
        // accepted because the first move won a queen.
        assert_eq!(result.moves[1].expected_move, "d5d4");
        assert!(!result.moves[1].iterations.is_empty());
        let bounded = inspect(&p, ReviewLimits { nodes: 100, ..generous() }).unwrap();
        assert!(bounded.nodes <= 100, "budget is per puzzle, not per move");
        assert!(!bounded.complete);
        assert!(!bounded.shallow_agreement());
        assert_ne!(bounded.status, ReviewStatus::Agreement);
    }

    #[test]
    fn incomplete_results_are_retried_and_cache_identity_covers_line_and_limits() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("screening.sqlite");
        let p = mate();
        let limits = generous();
        {
            let cache = ReviewCache::open(&path).unwrap();
            let small = ReviewLimits { nodes: 1, ..limits };
            let (unknown, hit) = cache.inspect(&p, small).unwrap();
            assert!(!hit);
            assert!(!unknown.complete);
            assert_eq!(unknown.status, ReviewStatus::Inconclusive);
            assert!(!cache.inspect(&p, small).unwrap().1, "never cache a budget failure");
            assert!(!cache.inspect(&p, limits).unwrap().1);
        }
        let cache = ReviewCache::open(&path).unwrap();
        assert!(cache.inspect(&p, limits).unwrap().1, "completed work survives reopening");
        let mut renamed = p.clone();
        renamed.id = "different id".into();
        renamed.rating = 2200;
        assert!(cache.inspect(&renamed, limits).unwrap().1, "human rating is independent");
        renamed.moves[1] = "b1b8".into();
        assert!(!cache.inspect(&renamed, limits).unwrap().1, "same FEN, different expected line");
        assert!(!cache.inspect(&p, ReviewLimits { depth: 4, ..limits }).unwrap().1);
        renamed = p.clone();
        renamed.fen = renamed.fen.replace("0 1", "0 2");
        assert!(!cache.inspect(&renamed, limits).unwrap().1, "full FEN identity");
        let rows: i64 = cache.conn.query_row("SELECT COUNT(*) FROM engine_reviews", [], |r| r.get(0)).unwrap();
        assert_eq!(rows, 4);
    }

    #[test]
    fn malformed_lines_and_out_of_range_limits_are_rejected() {
        let mut p = mate();
        p.moves[1] = "e1e7".into(); // Legal non-mate: still reviewable, not a data error.
        assert!(inspect(&p, generous()).is_ok());
        p.moves[1] = "e1d2".into();
        assert!(inspect(&p, generous()).is_err());
        p.moves.truncate(1);
        assert!(inspect(&p, generous()).is_err());
        for limits in [ReviewLimits { nodes: 0, ..generous() }, ReviewLimits { depth: 13, ..generous() }] {
            assert!(inspect(&mate(), limits).is_err());
        }
    }

    #[test]
    fn shallow_agreement_does_not_hide_a_deeper_reversal() {
        let observation = |depth, agreement| DepthReview {
            depth,
            best_move: "e2e4".into(),
            agreement,
            centipawns: Some(0),
            mate: None,
            nodes: 1,
        };
        let history = [
            observation(1, Agreement::SameMove),
            observation(2, Agreement::SameMove),
            observation(3, Agreement::DifferentMove),
            observation(4, Agreement::SameMove),
        ];
        assert_eq!(stable_depth(&history), None);
        let mut history = history.to_vec();
        history.push(observation(5, Agreement::SameMove));
        assert_eq!(stable_depth(&history), Some(4));
    }
}
