//! Players, their Glicko-2 puzzle ratings and the history of puzzles they played, kept in
//! `players.sqlite`.
//!
//! Each rated puzzle is one game between the player and the puzzle, using the puzzle's rating and
//! deviation from the database, settled as a rating period of its own. The deviation does not
//! grow while a player is away.

use std::path::Path;

use rusqlite::{Connection, OptionalExtension, params};

const SCALE: f64 = 173.7178;
const TAU: f64 = 0.5;
pub const DEFAULT_RATING: f64 = 1500.0;
pub const DEFAULT_DEVIATION: f64 = 350.0;
const DEFAULT_VOLATILITY: f64 = 0.06;
/// Keeps ratings responsive after many games and bounded for new players.
const MIN_DEVIATION: f64 = 45.0;
const MAX_DEVIATION: f64 = 350.0;
/// A rating is provisional while its deviation is above this.
pub const PROVISIONAL_DEVIATION: f64 = 110.0;
pub const MAX_NAME_CHARS: usize = 40;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rating {
    pub rating: f64,
    pub deviation: f64,
    pub volatility: f64,
}

impl Default for Rating {
    fn default() -> Self {
        Rating { rating: DEFAULT_RATING, deviation: DEFAULT_DEVIATION, volatility: DEFAULT_VOLATILITY }
    }
}

impl Rating {
    pub fn provisional(&self) -> bool {
        self.deviation > PROVISIONAL_DEVIATION
    }
}

fn g(phi: f64) -> f64 {
    1.0 / (1.0 + 3.0 * phi * phi / (std::f64::consts::PI * std::f64::consts::PI)).sqrt()
}

/// One Glicko-2 game (Glickman, "Example of the Glicko-2 system", steps 2–8). `score` is 1 for a
/// win and 0 for a loss.
pub fn glicko2(player: Rating, opponent_rating: f64, opponent_deviation: f64, score: f64) -> Rating {
    let mu = (player.rating - DEFAULT_RATING) / SCALE;
    let phi = player.deviation / SCALE;
    let sigma = player.volatility;
    let mu_j = (opponent_rating - DEFAULT_RATING) / SCALE;
    let g_j = g(opponent_deviation.max(0.0) / SCALE);
    let e = 1.0 / (1.0 + (-g_j * (mu - mu_j)).exp());
    let v_inv = g_j * g_j * e * (1.0 - e);
    if v_inv <= 0.0 {
        return player;
    }
    let v = 1.0 / v_inv;
    let delta_sum = g_j * (score - e);
    let delta = v * delta_sum;

    // Step 5: the new volatility, by the Illinois variant of regula falsi.
    let a = (sigma * sigma).ln();
    let f = |x: f64| {
        let ex = x.exp();
        let d = phi * phi + v + ex;
        ex * (delta * delta - phi * phi - v - ex) / (2.0 * d * d) - (x - a) / (TAU * TAU)
    };
    let mut big_a = a;
    let mut big_b = if delta * delta > phi * phi + v {
        (delta * delta - phi * phi - v).ln()
    } else {
        let mut k = 1.0;
        while f(a - k * TAU) < 0.0 {
            k += 1.0;
        }
        a - k * TAU
    };
    let (mut f_a, mut f_b) = (f(big_a), f(big_b));
    for _ in 0..100 {
        if (big_b - big_a).abs() <= 1e-6 {
            break;
        }
        let c = big_a + (big_a - big_b) * f_a / (f_b - f_a);
        let f_c = f(c);
        if f_c * f_b <= 0.0 {
            big_a = big_b;
            f_a = f_b;
        } else {
            f_a /= 2.0;
        }
        big_b = c;
        f_b = f_c;
    }
    let new_sigma = (big_a / 2.0).exp();
    let phi_star = (phi * phi + new_sigma * new_sigma).sqrt();
    let new_phi = 1.0 / (1.0 / (phi_star * phi_star) + v_inv).sqrt();
    let new_mu = mu + new_phi * new_phi * delta_sum;
    let next = Rating {
        rating: new_mu * SCALE + DEFAULT_RATING,
        deviation: (new_phi * SCALE).clamp(MIN_DEVIATION, MAX_DEVIATION),
        volatility: new_sigma,
    };
    if next.rating.is_finite() && next.deviation.is_finite() && next.volatility.is_finite() { next } else { player }
}

#[derive(Debug, Clone, PartialEq)]
pub struct Player {
    pub id: i64,
    pub name: String,
    pub rating: Rating,
    /// Rated puzzles played.
    pub games: u32,
    pub wins: u32,
    pub created_unix_ms: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PuzzleResult {
    /// Solved without a mistake, hint or the solution.
    Solved,
    SolvedWithHelp,
    /// The solution was shown, or the puzzle was left unfinished after an attempt.
    Failed,
}

impl PuzzleResult {
    fn code(self) -> i64 {
        match self {
            PuzzleResult::Solved => 0,
            PuzzleResult::SolvedWithHelp => 1,
            PuzzleResult::Failed => 2,
        }
    }

    fn from_code(code: i64) -> PuzzleResult {
        match code {
            0 => PuzzleResult::Solved,
            1 => PuzzleResult::SolvedWithHelp,
            _ => PuzzleResult::Failed,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct HistoryEntry {
    pub id: i64,
    pub player_id: i64,
    pub puzzle_id: String,
    pub played_unix_ms: i64,
    pub puzzle_rating: u16,
    pub result: PuzzleResult,
    pub mistakes: u32,
    pub hints: u32,
    pub seconds: f64,
    /// Rating before and after, when the attempt was rated.
    pub rating_change: Option<(f64, f64)>,
}

/// Totals for a player's history.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct HistoryStats {
    pub played: u32,
    pub solved_clean: u32,
    pub solved_with_help: u32,
    pub failed: u32,
    /// Clean solves in a row, ending with the latest puzzle.
    pub streak: u32,
    pub best_streak: u32,
    pub today_played: u32,
    pub today_solved: u32,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlayerError {
    Database(String),
    InvalidName(&'static str),
    NameTaken,
    NotFound,
    LastPlayer,
}

impl std::fmt::Display for PlayerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            PlayerError::Database(message) => write!(f, "player database error: {message}"),
            PlayerError::InvalidName(reason) => f.write_str(reason),
            PlayerError::NameTaken => f.write_str("another player already has that name"),
            PlayerError::NotFound => f.write_str("no such player"),
            PlayerError::LastPlayer => f.write_str("the last player cannot be deleted"),
        }
    }
}

impl std::error::Error for PlayerError {}

impl From<rusqlite::Error> for PlayerError {
    fn from(e: rusqlite::Error) -> Self {
        PlayerError::Database(e.to_string())
    }
}

/// Trims a name and checks its length and characters.
pub fn clean_name(name: &str) -> Result<String, PlayerError> {
    let name: String = name.chars().map(|c| if c.is_control() { ' ' } else { c }).collect();
    let name = name.split_whitespace().collect::<Vec<_>>().join(" ");
    if name.is_empty() {
        return Err(PlayerError::InvalidName("a player needs a name"));
    }
    if name.chars().count() > MAX_NAME_CHARS {
        return Err(PlayerError::InvalidName("names are at most 40 characters"));
    }
    Ok(name)
}

pub struct PlayerStore {
    conn: Connection,
}

const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS players (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    rating REAL NOT NULL,
    deviation REAL NOT NULL,
    volatility REAL NOT NULL,
    games INTEGER NOT NULL DEFAULT 0,
    wins INTEGER NOT NULL DEFAULT 0,
    created_ms INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS history (
    id INTEGER PRIMARY KEY,
    player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
    puzzle_id TEXT NOT NULL,
    played_ms INTEGER NOT NULL,
    puzzle_rating INTEGER NOT NULL,
    result INTEGER NOT NULL,
    mistakes INTEGER NOT NULL,
    hints INTEGER NOT NULL,
    seconds REAL NOT NULL,
    rating_before REAL,
    rating_after REAL
);
CREATE INDEX IF NOT EXISTS history_player ON history(player_id, played_ms);
CREATE INDEX IF NOT EXISTS history_puzzle ON history(player_id, puzzle_id);
";

const SCHEMA_VERSION: i64 = 1;

fn now_ms() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_millis() as i64)
}

fn player_row(row: &rusqlite::Row) -> rusqlite::Result<Player> {
    Ok(Player {
        id: row.get(0)?,
        name: row.get(1)?,
        rating: Rating { rating: row.get(2)?, deviation: row.get(3)?, volatility: row.get(4)? },
        games: row.get(5)?,
        wins: row.get(6)?,
        created_unix_ms: row.get(7)?,
    })
}

const PLAYER_COLUMNS: &str = "id, name, rating, deviation, volatility, games, wins, created_ms";

impl PlayerStore {
    /// Opens or creates the database. A new database gets one player named `default_name`.
    pub fn open(path: &Path, default_name: &str) -> Result<PlayerStore, PlayerError> {
        let conn = Connection::open(path)?;
        conn.execute_batch("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;")?;
        conn.execute_batch(SCHEMA)?;
        conn.execute(
            "INSERT OR IGNORE INTO meta (key, value) VALUES ('schema', ?1)",
            params![SCHEMA_VERSION.to_string()],
        )?;
        let store = PlayerStore { conn };
        if store.players()?.is_empty() {
            let name = clean_name(default_name).unwrap_or_else(|_| "Player".to_owned());
            let player = store.add(&name)?;
            store.set_current(player.id)?;
        }
        Ok(store)
    }

    pub fn players(&self) -> Result<Vec<Player>, PlayerError> {
        let mut stmt =
            self.conn.prepare(&format!("SELECT {PLAYER_COLUMNS} FROM players ORDER BY name COLLATE NOCASE"))?;
        let rows = stmt.query_map([], player_row)?;
        Ok(rows.collect::<Result<_, _>>()?)
    }

    pub fn player(&self, id: i64) -> Result<Player, PlayerError> {
        self.conn
            .query_row(&format!("SELECT {PLAYER_COLUMNS} FROM players WHERE id = ?1"), params![id], player_row)
            .optional()?
            .ok_or(PlayerError::NotFound)
    }

    /// The current player; falls back to the first player if the stored one is gone.
    pub fn current(&self) -> Result<Player, PlayerError> {
        let id: Option<i64> = self
            .conn
            .query_row("SELECT value FROM meta WHERE key = 'current_player'", [], |r| r.get::<_, String>(0))
            .optional()?
            .and_then(|v| v.parse().ok());
        if let Some(player) = id.and_then(|id| self.player(id).ok()) {
            return Ok(player);
        }
        let first = self.players()?.into_iter().next().ok_or(PlayerError::NotFound)?;
        self.set_current(first.id)?;
        Ok(first)
    }

    pub fn set_current(&self, id: i64) -> Result<(), PlayerError> {
        self.player(id)?;
        self.conn.execute(
            "INSERT INTO meta (key, value) VALUES ('current_player', ?1) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            params![id.to_string()],
        )?;
        Ok(())
    }

    fn name_taken(&self, name: &str, except: i64) -> Result<bool, PlayerError> {
        let n: i64 = self.conn.query_row(
            "SELECT COUNT(*) FROM players WHERE name = ?1 COLLATE NOCASE AND id <> ?2",
            params![name, except],
            |r| r.get(0),
        )?;
        Ok(n > 0)
    }

    pub fn add(&self, name: &str) -> Result<Player, PlayerError> {
        let name = clean_name(name)?;
        if self.name_taken(&name, 0)? {
            return Err(PlayerError::NameTaken);
        }
        let r = Rating::default();
        self.conn.execute(
            "INSERT INTO players (name, rating, deviation, volatility, created_ms) VALUES (?1, ?2, ?3, ?4, ?5)",
            params![name, r.rating, r.deviation, r.volatility, now_ms()],
        )?;
        self.player(self.conn.last_insert_rowid())
    }

    pub fn rename(&self, id: i64, name: &str) -> Result<Player, PlayerError> {
        let name = clean_name(name)?;
        self.player(id)?;
        if self.name_taken(&name, id)? {
            return Err(PlayerError::NameTaken);
        }
        self.conn.execute("UPDATE players SET name = ?1 WHERE id = ?2", params![name, id])?;
        self.player(id)
    }

    /// Deletes a player with their rating and history. The last player cannot be deleted.
    pub fn delete(&self, id: i64) -> Result<(), PlayerError> {
        self.player(id)?;
        if self.players()?.len() <= 1 {
            return Err(PlayerError::LastPlayer);
        }
        let current = self.current()?.id;
        self.conn.execute("DELETE FROM players WHERE id = ?1", params![id])?;
        if current == id {
            let first = self.players()?.into_iter().next().ok_or(PlayerError::NotFound)?;
            self.set_current(first.id)?;
        }
        Ok(())
    }

    /// Forgets a player's played puzzles. The rating is kept.
    pub fn clear_history(&self, id: i64) -> Result<u64, PlayerError> {
        self.player(id)?;
        Ok(self.conn.execute("DELETE FROM history WHERE player_id = ?1", params![id])? as u64)
    }

    pub fn has_played(&self, player: i64, puzzle_id: &str) -> Result<bool, PlayerError> {
        let n: i64 = self.conn.query_row(
            "SELECT COUNT(*) FROM history WHERE player_id = ?1 AND puzzle_id = ?2",
            params![player, puzzle_id],
            |r| r.get(0),
        )?;
        Ok(n > 0)
    }

    pub fn played_ids(&self, player: i64) -> Result<Vec<String>, PlayerError> {
        let mut stmt = self.conn.prepare("SELECT DISTINCT puzzle_id FROM history WHERE player_id = ?1")?;
        let rows = stmt.query_map(params![player], |r| r.get(0))?;
        Ok(rows.collect::<Result<_, _>>()?)
    }

    /// Settles a rated game against a puzzle and returns the rating before and after.
    pub fn apply_rating(
        &self,
        player: i64,
        puzzle_rating: f64,
        puzzle_deviation: f64,
        won: bool,
    ) -> Result<(f64, f64), PlayerError> {
        let p = self.player(player)?;
        let next = glicko2(p.rating, puzzle_rating, puzzle_deviation, if won { 1.0 } else { 0.0 });
        self.conn.execute(
            "UPDATE players SET rating = ?1, deviation = ?2, volatility = ?3, games = games + 1, wins = wins + ?4 WHERE id = ?5",
            params![next.rating, next.deviation, next.volatility, i64::from(won), player],
        )?;
        Ok((p.rating.rating, next.rating))
    }

    /// Adds a history entry (`entry.id` 0) or updates the one with `entry.id`; returns its id.
    pub fn save_entry(&self, entry: &HistoryEntry) -> Result<i64, PlayerError> {
        let (before, after) = entry.rating_change.map_or((None, None), |(b, a)| (Some(b), Some(a)));
        if entry.id == 0 {
            self.conn.execute(
                "INSERT INTO history (player_id, puzzle_id, played_ms, puzzle_rating, result, mistakes, hints, seconds, rating_before, rating_after)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
                params![
                    entry.player_id,
                    entry.puzzle_id,
                    entry.played_unix_ms,
                    entry.puzzle_rating,
                    entry.result.code(),
                    entry.mistakes,
                    entry.hints,
                    entry.seconds,
                    before,
                    after
                ],
            )?;
            Ok(self.conn.last_insert_rowid())
        } else {
            self.conn.execute(
                "UPDATE history SET result = ?1, mistakes = ?2, hints = ?3, seconds = ?4, rating_before = ?5, rating_after = ?6 WHERE id = ?7",
                params![entry.result.code(), entry.mistakes, entry.hints, entry.seconds, before, after, entry.id],
            )?;
            Ok(entry.id)
        }
    }

    /// A player's history, newest first.
    pub fn history(&self, player: i64, limit: usize) -> Result<Vec<HistoryEntry>, PlayerError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, player_id, puzzle_id, played_ms, puzzle_rating, result, mistakes, hints, seconds, rating_before, rating_after
             FROM history WHERE player_id = ?1 ORDER BY played_ms DESC, id DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![player, limit as i64], |r| {
            let before: Option<f64> = r.get(9)?;
            let after: Option<f64> = r.get(10)?;
            Ok(HistoryEntry {
                id: r.get(0)?,
                player_id: r.get(1)?,
                puzzle_id: r.get(2)?,
                played_unix_ms: r.get(3)?,
                puzzle_rating: r.get(4)?,
                result: PuzzleResult::from_code(r.get(5)?),
                mistakes: r.get(6)?,
                hints: r.get(7)?,
                seconds: r.get(8)?,
                rating_change: before.zip(after),
            })
        })?;
        Ok(rows.collect::<Result<_, _>>()?)
    }

    /// Totals, streaks and the counts since `day_start_ms` (the start of today, local time).
    pub fn stats(&self, player: i64, day_start_ms: i64) -> Result<HistoryStats, PlayerError> {
        let mut stmt =
            self.conn.prepare("SELECT result, played_ms FROM history WHERE player_id = ?1 ORDER BY played_ms, id")?;
        let rows =
            stmt.query_map(params![player], |r| Ok((PuzzleResult::from_code(r.get(0)?), r.get::<_, i64>(1)?)))?;
        let mut stats = HistoryStats::default();
        let mut run = 0;
        for row in rows {
            let (result, played) = row?;
            stats.played += 1;
            match result {
                PuzzleResult::Solved => stats.solved_clean += 1,
                PuzzleResult::SolvedWithHelp => stats.solved_with_help += 1,
                PuzzleResult::Failed => stats.failed += 1,
            }
            run = if result == PuzzleResult::Solved { run + 1 } else { 0 };
            stats.best_streak = stats.best_streak.max(run);
            if played >= day_start_ms {
                stats.today_played += 1;
                if result != PuzzleResult::Failed {
                    stats.today_solved += 1;
                }
            }
        }
        stats.streak = run;
        Ok(stats)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn glicko2_matches_glickmans_example() {
        // Against an equal opponent a win and a loss move the rating by the same amount.
        let start = Rating { rating: 1500.0, deviation: 200.0, volatility: 0.06 };
        let won = glicko2(start, 1500.0, 200.0, 1.0);
        let lost = glicko2(start, 1500.0, 200.0, 0.0);
        assert!(won.rating > 1500.0 && lost.rating < 1500.0);
        assert!(((won.rating - 1500.0) + (lost.rating - 1500.0)).abs() < 1e-6, "symmetric for an equal opponent");
        assert!(won.deviation < 200.0);
        // A new player losing to a weaker puzzle loses a lot; deviation stays within bounds.
        let new = glicko2(Rating::default(), 1200.0, 80.0, 0.0);
        assert!(new.rating < 1350.0, "{new:?}");
        assert!(new.deviation >= MIN_DEVIATION && new.deviation <= MAX_DEVIATION);
        let mut seasoned = Rating::default();
        for _ in 0..500 {
            seasoned = glicko2(seasoned, 1500.0, 80.0, 1.0);
            seasoned = glicko2(seasoned, 1500.0, 80.0, 0.0);
        }
        assert!(seasoned.deviation >= MIN_DEVIATION);
        assert!(!seasoned.provisional());
    }

    #[test]
    fn players_ratings_and_history() {
        let dir = tempfile::tempdir().unwrap();
        let store = PlayerStore::open(&dir.path().join("players.sqlite"), "Ian").unwrap();
        let ian = store.current().unwrap();
        assert_eq!((ian.name.as_str(), ian.rating.rating, ian.games), ("Ian", 1500.0, 0));
        assert!(ian.rating.provisional());

        assert_eq!(store.add("  ian ").unwrap_err(), PlayerError::NameTaken);
        assert!(matches!(store.add("   ").unwrap_err(), PlayerError::InvalidName(_)));
        assert!(matches!(store.add(&"x".repeat(41)).unwrap_err(), PlayerError::InvalidName(_)));
        let guest = store.add("Guest\tplayer").unwrap();
        assert_eq!(guest.name, "Guest player");
        assert_eq!(store.rename(guest.id, "Visitor").unwrap().name, "Visitor");
        assert_eq!(store.players().unwrap().len(), 2);

        let (before, after) = store.apply_rating(ian.id, 1500.0, 80.0, true).unwrap();
        assert!(before == 1500.0 && after > 1500.0);
        let entry = HistoryEntry {
            id: 0,
            player_id: ian.id,
            puzzle_id: "00sHx".into(),
            played_unix_ms: 1_000,
            puzzle_rating: 1500,
            result: PuzzleResult::Failed,
            mistakes: 1,
            hints: 0,
            seconds: 3.5,
            rating_change: Some((before, after)),
        };
        let id = store.save_entry(&entry).unwrap();
        store.save_entry(&HistoryEntry { id, result: PuzzleResult::SolvedWithHelp, ..entry.clone() }).unwrap();
        for (i, result) in [PuzzleResult::Solved, PuzzleResult::Solved, PuzzleResult::Failed, PuzzleResult::Solved]
            .into_iter()
            .enumerate()
        {
            store
                .save_entry(&HistoryEntry {
                    id: 0,
                    puzzle_id: format!("p{i}"),
                    played_unix_ms: 2_000 + i as i64,
                    result,
                    rating_change: None,
                    ..entry.clone()
                })
                .unwrap();
        }
        let history = store.history(ian.id, 10).unwrap();
        assert_eq!(history.len(), 5);
        assert_eq!(history[0].puzzle_id, "p3");
        assert_eq!(history[4].result, PuzzleResult::SolvedWithHelp);
        assert!(history[4].rating_change.is_some() && history[0].rating_change.is_none());
        assert!(store.has_played(ian.id, "00sHx").unwrap());
        assert!(!store.has_played(guest.id, "00sHx").unwrap());
        assert_eq!(store.played_ids(ian.id).unwrap().len(), 5);

        let stats = store.stats(ian.id, 2_001).unwrap();
        assert_eq!((stats.played, stats.solved_clean, stats.solved_with_help, stats.failed), (5, 3, 1, 1));
        assert_eq!((stats.streak, stats.best_streak, stats.today_played, stats.today_solved), (1, 2, 3, 2));
        let ian = store.player(ian.id).unwrap();
        assert_eq!((ian.games, ian.wins), (1, 1));

        store.set_current(guest.id).unwrap();
        store.delete(guest.id).unwrap();
        assert_eq!(store.current().unwrap().id, ian.id);
        assert_eq!(store.delete(ian.id).unwrap_err(), PlayerError::LastPlayer);
        assert_eq!(store.clear_history(ian.id).unwrap(), 5);
        assert_eq!(store.player(ian.id).unwrap().rating.rating, after, "clearing history keeps the rating");
        drop(store);
        let reopened = PlayerStore::open(&dir.path().join("players.sqlite"), "Other").unwrap();
        assert_eq!(reopened.players().unwrap().len(), 1);
    }
}
