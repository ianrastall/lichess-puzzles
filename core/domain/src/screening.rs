//! Exact, bounded short-mate screening of the position the solver actually sees.
//! This proves mate in one/two; it does not estimate human difficulty or alter puzzle ratings.

use std::path::Path;
use std::time::{Duration, Instant};

use cozy_chess::{Board, GameStatus, Move};
use rusqlite::{Connection, OptionalExtension, params};

use crate::chess::{self, LineError};
use crate::store::Puzzle;

const VERSION: i64 = 1;
const MAX_NODES: u32 = 50_000;
const MAX_TIME: Duration = Duration::from_millis(50);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    /// The solver can force mate in this many moves, against every legal reply.
    Mate(u8),
    /// No forced mate within two moves was found by a complete search.
    NoShortMate,
    /// The search budget ran out. Never treat this as proof of difficulty.
    Inconclusive,
}

/// Reconstructs the position after the opponent's setup move, including castling/en passant.
pub fn solver_board(puzzle: &Puzzle) -> Result<Board, LineError> {
    let mut board = chess::parse_fen(&puzzle.fen)?;
    let first = puzzle.moves.first().ok_or_else(|| LineError::Move { index: 0, uci: String::new() })?;
    let moves = chess::replay(&board, &[first.as_str()])?;
    board.play(moves[0]);
    Ok(board)
}

struct Budget {
    remaining: u32,
    deadline: Instant,
}

impl Budget {
    fn visit(&mut self) -> Option<()> {
        if self.remaining == 0 || Instant::now() >= self.deadline {
            return None;
        }
        self.remaining -= 1;
        Some(())
    }
}

fn moves(board: &Board) -> Vec<Move> {
    let mut result = Vec::new();
    board.generate_moves(|batch| {
        result.extend(batch);
        false
    });
    result
}

fn mate_in_one(board: &Board, budget: &mut Budget) -> Option<bool> {
    for mv in moves(board) {
        budget.visit()?;
        let mut child = board.clone();
        child.play(mv);
        if child.status() == GameStatus::Won {
            return Some(true);
        }
    }
    Some(false)
}

pub fn inspect(board: &Board) -> Verdict {
    inspect_with_budget(board, MAX_NODES, MAX_TIME)
}

fn inspect_with_budget(board: &Board, nodes: u32, duration: Duration) -> Verdict {
    // A terminal position is not a playable short mating puzzle.
    if board.status() != GameStatus::Ongoing {
        return Verdict::NoShortMate;
    }
    let mut budget = Budget { remaining: nodes, deadline: Instant::now() + duration };
    let Some(one) = mate_in_one(board, &mut budget) else { return Verdict::Inconclusive };
    if one {
        return Verdict::Mate(1);
    }
    for mv in moves(board) {
        if budget.visit().is_none() {
            return Verdict::Inconclusive;
        }
        let mut reply_board = board.clone();
        reply_board.play(mv);
        if reply_board.status() != GameStatus::Ongoing {
            continue; // Stalemate is not mate; avoid a vacuous "every reply" proof.
        }
        let mut forced = true;
        for reply in moves(&reply_board) {
            if budget.visit().is_none() {
                return Verdict::Inconclusive;
            }
            let mut child = reply_board.clone();
            child.play(reply);
            match mate_in_one(&child, &mut budget) {
                Some(true) => {}
                Some(false) => {
                    forced = false;
                    break;
                }
                None => return Verdict::Inconclusive,
            }
        }
        if forced {
            return Verdict::Mate(2);
        }
    }
    Verdict::NoShortMate
}

/// A disposable sidecar. Full solver FEN + algorithm version prevent stale results after
/// database updates, reused puzzle IDs, or changes to the screening algorithm.
pub struct ScreeningCache {
    conn: Connection,
}

impl ScreeningCache {
    pub fn open(path: &Path) -> Result<Self, rusqlite::Error> {
        let conn = Connection::open(path)?;
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS short_mates (
            version INTEGER NOT NULL, fen TEXT NOT NULL, mate INTEGER NOT NULL CHECK(mate BETWEEN 0 AND 2),
            PRIMARY KEY(version, fen));",
        )?;
        Ok(Self { conn })
    }

    /// Returns the verdict and whether it was already cached. Inconclusive results are not saved.
    pub fn inspect(&self, board: &Board) -> Result<(Verdict, bool), rusqlite::Error> {
        let fen = board.to_string();
        let cached: Option<u8> = self
            .conn
            .query_row("SELECT mate FROM short_mates WHERE version=?1 AND fen=?2", params![VERSION, fen], |row| {
                row.get(0)
            })
            .optional()?;
        if let Some(mate) = cached {
            return Ok((if mate == 0 { Verdict::NoShortMate } else { Verdict::Mate(mate) }, true));
        }
        let verdict = inspect(board);
        let value = match verdict {
            Verdict::Mate(n) => Some(n),
            Verdict::NoShortMate => Some(0),
            Verdict::Inconclusive => None,
        };
        if let Some(value) = value {
            self.conn.execute(
                "INSERT OR REPLACE INTO short_mates(version,fen,mate) VALUES(?1,?2,?3)",
                params![VERSION, fen, value],
            )?;
        }
        Ok((verdict, false))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_mates_and_nonmates() {
        let sample = include_str!("../tests/fixtures/sample_puzzles.csv");
        for (id, expected) in
            [("0000H", Verdict::Mate(1)), ("00sHx", Verdict::Mate(2)), ("0000D", Verdict::NoShortMate)]
        {
            let columns: Vec<_> = sample.lines().find(|l| l.starts_with(id)).unwrap().split(',').collect();
            let mut board = chess::parse_fen(columns[1]).unwrap();
            let setup = columns[2].split_whitespace().next().unwrap();
            let mv = chess::replay(&board, &[setup]).unwrap()[0];
            board.play(mv);
            assert_eq!(inspect_with_budget(&board, 1_000_000, Duration::from_secs(5)), expected, "{id}");
        }
        assert_eq!(inspect(&Board::default()), Verdict::NoShortMate);
        let stalemate = chess::parse_fen("7k/5K2/6Q1/8/8/8/8/8 b - - 0 1").unwrap();
        assert_eq!(inspect(&stalemate), Verdict::NoShortMate);
    }

    #[test]
    fn exhausted_search_is_inconclusive() {
        assert_eq!(inspect_with_budget(&Board::default(), 0, Duration::from_secs(1)), Verdict::Inconclusive);
    }

    #[test]
    fn cache_survives_reopen_and_distinguishes_positions() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("screening.sqlite");
        let board = Board::default();
        let cache = ScreeningCache::open(&path).unwrap();
        assert_eq!(cache.inspect(&board).unwrap(), (Verdict::NoShortMate, false));
        drop(cache);
        let cache = ScreeningCache::open(&path).unwrap();
        assert_eq!(cache.inspect(&board).unwrap(), (Verdict::NoShortMate, true));
        let mut changed = board.clone();
        changed.play(chess::replay(&changed, &["e2e4"]).unwrap()[0]);
        assert!(!cache.inspect(&changed).unwrap().1);
    }
}
