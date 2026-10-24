//! Puzzle filters, compiled against a store's theme and opening tables and run as a parallel scan
//! over its records.

use serde::{Deserialize, Serialize};

use crate::Side;
use crate::store::{FLAG_SOLVER_WHITE, RECORD_SIZE, Record, Store};

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct Filter {
    pub rating_min: Option<u16>,
    pub rating_max: Option<u16>,
    pub max_deviation: Option<u16>,
    pub min_popularity: Option<i8>,
    pub min_plays: Option<u32>,
    /// Theme keys; a puzzle needs at least one.
    pub themes_any: Vec<String>,
    /// Theme keys; a puzzle needs every one.
    pub themes_all: Vec<String>,
    /// Theme keys; a puzzle may have none of them.
    pub themes_none: Vec<String>,
    /// Opening family or variation tags; a puzzle needs one of them.
    pub openings: Vec<String>,
    pub solver: Option<Side>,
    /// Moves the solver plays.
    pub min_solution_moves: Option<u8>,
    pub max_solution_moves: Option<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FilterError {
    UnknownTheme(String),
    UnknownOpening(String),
}

impl std::fmt::Display for FilterError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            FilterError::UnknownTheme(name) => write!(f, "no theme `{name}` in this database"),
            FilterError::UnknownOpening(name) => write!(f, "no opening `{name}` in this database"),
        }
    }
}

impl std::error::Error for FilterError {}

#[derive(Debug, Clone)]
pub struct CompiledFilter {
    rating: (u16, u16),
    max_deviation: u16,
    min_popularity: i8,
    min_plays: u32,
    themes_any: u128,
    themes_all: u128,
    themes_none: u128,
    /// Allowed family and variation ids (index 0 unused), or `None` for any opening.
    openings: Option<(Vec<bool>, Vec<bool>)>,
    solver_white: Option<bool>,
    moves: (u8, u8),
}

impl Filter {
    pub fn compile(&self, store: &Store) -> Result<CompiledFilter, FilterError> {
        let info = store.info();
        let mask = |names: &[String]| -> Result<u128, FilterError> {
            names.iter().try_fold(0u128, |mask, name| {
                let bit = info
                    .themes
                    .iter()
                    .position(|t| t == name)
                    .ok_or_else(|| FilterError::UnknownTheme(name.clone()))?;
                Ok(mask | 1u128 << bit)
            })
        };
        let openings = if self.openings.is_empty() {
            None
        } else {
            let mut families = vec![false; info.opening_families.len() + 1];
            let mut variations = vec![false; info.opening_variations.len() + 1];
            for tag in &self.openings {
                if let Some(i) = info.opening_families.iter().position(|f| f == tag) {
                    families[i + 1] = true;
                } else if let Some(i) = info.opening_variations.iter().position(|v| &v.tag == tag) {
                    variations[i + 1] = true;
                } else {
                    return Err(FilterError::UnknownOpening(tag.clone()));
                }
            }
            Some((families, variations))
        };
        Ok(CompiledFilter {
            rating: (self.rating_min.unwrap_or(0), self.rating_max.unwrap_or(u16::MAX)),
            max_deviation: self.max_deviation.unwrap_or(u16::MAX),
            min_popularity: self.min_popularity.unwrap_or(i8::MIN),
            min_plays: self.min_plays.unwrap_or(0),
            themes_any: mask(&self.themes_any)?,
            themes_all: mask(&self.themes_all)?,
            themes_none: mask(&self.themes_none)?,
            openings,
            solver_white: self.solver.map(|s| s == Side::White),
            moves: (
                self.min_solution_moves.unwrap_or(0).saturating_mul(2),
                self.max_solution_moves.unwrap_or(u8::MAX / 2).saturating_mul(2),
            ),
        })
    }
}

impl CompiledFilter {
    pub fn matches(&self, r: &Record) -> bool {
        r.rating >= self.rating.0
            && r.rating <= self.rating.1
            && r.deviation <= self.max_deviation
            && r.popularity >= self.min_popularity
            && r.plays >= self.min_plays
            && (self.themes_any == 0 || r.themes & self.themes_any != 0)
            && r.themes & self.themes_all == self.themes_all
            && r.themes & self.themes_none == 0
            && r.moves >= self.moves.0
            && r.moves <= self.moves.1
            && self.solver_white.is_none_or(|white| (r.flags & FLAG_SOLVER_WHITE != 0) == white)
            && self
                .openings
                .as_ref()
                .is_none_or(|(families, variations)| families[r.family as usize] || variations[r.variation as usize])
    }
}

/// Runs `per_chunk` over contiguous record ranges on several threads, returning results in order.
fn scan<T: Send>(store: &Store, per_chunk: impl Fn(usize, &[u8]) -> T + Sync) -> Vec<T> {
    let bytes = store.record_bytes();
    let n = store.len();
    let threads = std::thread::available_parallelism().map_or(1, |t| t.get()).clamp(1, 16);
    let per_thread = n.div_ceil(threads).max(1 << 16);
    std::thread::scope(|scope| {
        let handles: Vec<_> = (0..n)
            .step_by(per_thread)
            .map(|start| {
                let end = (start + per_thread).min(n);
                let chunk = &bytes[start * RECORD_SIZE..end * RECORD_SIZE];
                let per_chunk = &per_chunk;
                scope.spawn(move || per_chunk(start, chunk))
            })
            .collect();
        handles.into_iter().map(|h| h.join().expect("scan thread")).collect()
    })
}

pub fn count(store: &Store, filter: &CompiledFilter) -> usize {
    scan(store, |_, chunk| {
        chunk.as_chunks::<RECORD_SIZE>().0.iter().filter(|r| filter.matches(&Record::decode(r))).count()
    })
    .into_iter()
    .sum()
}

/// Record indices of every matching puzzle, in store order.
pub fn matching(store: &Store, filter: &CompiledFilter) -> Vec<u32> {
    scan(store, |start, chunk| {
        chunk
            .as_chunks::<RECORD_SIZE>()
            .0
            .iter()
            .enumerate()
            .filter(|(_, r)| filter.matches(&Record::decode(r)))
            .map(|(i, _)| (start + i) as u32)
            .collect::<Vec<_>>()
    })
    .concat()
}

/// Matching puzzles per theme bit, plus the total, for showing counts beside theme choices.
pub fn theme_counts(store: &Store, filter: &CompiledFilter) -> (usize, Vec<usize>) {
    let themes = store.info().themes.len();
    scan(store, |_, chunk| {
        let mut counts = vec![0usize; themes];
        let mut total = 0;
        for r in chunk.as_chunks::<RECORD_SIZE>().0 {
            let record = Record::decode(r);
            if filter.matches(&record) {
                total += 1;
                let mut mask = record.themes;
                while mask != 0 {
                    counts[mask.trailing_zeros() as usize] += 1;
                    mask &= mask - 1;
                }
            }
        }
        (total, counts)
    })
    .into_iter()
    .fold((0, vec![0; themes]), |(total, mut sum), (t, counts)| {
        sum.iter_mut().zip(counts).for_each(|(s, c)| *s += c);
        (total + t, sum)
    })
}

/// Up to `n` distinct matching puzzles chosen at random from `seed`.
pub fn sample(store: &Store, filter: &CompiledFilter, n: usize, seed: u64) -> Vec<usize> {
    let mut indices = matching(store, filter);
    let mut rng = SplitMix64(seed);
    let n = n.min(indices.len());
    for i in 0..n {
        let j = i + (rng.next() % (indices.len() - i) as u64) as usize;
        indices.swap(i, j);
    }
    indices.truncate(n);
    indices.into_iter().map(|i| i as usize).collect()
}

struct SplitMix64(u64);

impl SplitMix64 {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }
}
