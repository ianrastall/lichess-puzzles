//! The puzzle store: one file built from the Lichess CSV and memory-mapped for reading.
//!
//! Layout:
//!
//! ```text
//! "LPSTORE1"
//! detail blocks        zstd frames of `FEN \t Moves \t GameUrl \t DailyDate \n`, block_size puzzles each
//! records              puzzle_count × 32 bytes, the fields filters read (see `Record`)
//! ids                  puzzle_count × u64, packed puzzle ids (see `puzzle_id`)
//! id order             puzzle_count × u32, record indices sorted by id; present only if the ids are unsorted
//! block offsets        (block_count + 1) × u64, absolute offsets of the detail blocks
//! info                 JSON `StoreInfo`
//! info offset          u64
//! "LPSTEND1"
//! ```
//!
//! All integers are little-endian.

use std::fs::File;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

use memmap2::Mmap;
use serde::{Deserialize, Serialize};

use crate::Side;
use crate::puzzle_id;
use crate::source::RemoteVersion;

pub const MAGIC: &[u8; 8] = b"LPSTORE1";
pub const END_MAGIC: &[u8; 8] = b"LPSTEND1";
pub const FORMAT_VERSION: u32 = 1;
pub const RECORD_SIZE: usize = 32;
pub const MAX_THEMES: usize = 128;

pub const FLAG_SOLVER_WHITE: u8 = 1;
pub const FLAG_DAILY: u8 = 2;

/// The fixed-size part of a puzzle. Opening ids index `StoreInfo` tables from 1; 0 means none.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Record {
    pub themes: u128,
    pub plays: u32,
    pub rating: u16,
    pub deviation: u16,
    pub family: u16,
    pub variation: u16,
    pub popularity: i8,
    /// Moves in the line, including the opponent's first move.
    pub moves: u8,
    pub flags: u8,
}

impl Record {
    pub fn encode(&self) -> [u8; RECORD_SIZE] {
        let mut out = [0u8; RECORD_SIZE];
        out[0..16].copy_from_slice(&self.themes.to_le_bytes());
        out[16..20].copy_from_slice(&self.plays.to_le_bytes());
        out[20..22].copy_from_slice(&self.rating.to_le_bytes());
        out[22..24].copy_from_slice(&self.deviation.to_le_bytes());
        out[24..26].copy_from_slice(&self.family.to_le_bytes());
        out[26..28].copy_from_slice(&self.variation.to_le_bytes());
        out[28] = self.popularity as u8;
        out[29] = self.moves;
        out[30] = self.flags;
        out
    }

    pub fn decode(bytes: &[u8; RECORD_SIZE]) -> Record {
        let u16_at = |i: usize| u16::from_le_bytes([bytes[i], bytes[i + 1]]);
        Record {
            themes: u128::from_le_bytes(bytes[0..16].try_into().unwrap()),
            plays: u32::from_le_bytes(bytes[16..20].try_into().unwrap()),
            rating: u16_at(20),
            deviation: u16_at(22),
            family: u16_at(24),
            variation: u16_at(26),
            popularity: bytes[28] as i8,
            moves: bytes[29],
            flags: bytes[30],
        }
    }

    pub fn solver(&self) -> Side {
        if self.flags & FLAG_SOLVER_WHITE != 0 { Side::White } else { Side::Black }
    }

    /// Moves the solver plays.
    pub fn solution_moves(&self) -> u8 {
        self.moves / 2
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Section {
    pub offset: u64,
    pub len: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Sections {
    pub records: Section,
    pub ids: Section,
    pub id_order: Option<Section>,
    pub block_offsets: Section,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Variation {
    pub tag: String,
    /// Family id (1-based).
    pub family: u16,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SourceInfo {
    pub file_name: String,
    pub bytes: u64,
    pub version: Option<RemoteVersion>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SkippedRow {
    pub line: u64,
    pub id: String,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StoreInfo {
    pub format_version: u32,
    pub builder: String,
    pub built_at_unix: u64,
    pub puzzle_count: u64,
    pub block_size: u32,
    pub sections: Sections,
    /// Theme keys in bit order (bit 0 first), sorted by name.
    pub themes: Vec<String>,
    /// Opening family tags; family id n is `opening_families[n - 1]`.
    pub opening_families: Vec<String>,
    /// Opening variation tags; variation id n is `opening_variations[n - 1]`.
    pub opening_variations: Vec<Variation>,
    pub source: SourceInfo,
    pub skipped_count: u64,
    /// The first skipped rows, for the report.
    pub skipped: Vec<SkippedRow>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Puzzle {
    pub index: usize,
    pub id: String,
    pub fen: String,
    pub moves: Vec<String>,
    pub rating: u16,
    pub deviation: u16,
    pub popularity: i8,
    pub plays: u32,
    pub themes: Vec<String>,
    pub opening_family: Option<String>,
    pub opening_variation: Option<String>,
    pub game_url: String,
    /// Milliseconds since the Unix epoch, for puzzles that were a Lichess daily puzzle.
    pub daily_date: Option<i64>,
    pub solver: Side,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StoreError {
    Io(String),
    /// The file is not a store, is damaged or uses another format version.
    Format(String),
    OutOfRange(usize),
}

impl std::fmt::Display for StoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StoreError::Io(message) => write!(f, "store file error: {message}"),
            StoreError::Format(message) => write!(f, "not a usable puzzle store: {message}"),
            StoreError::OutOfRange(index) => write!(f, "puzzle index {index} is out of range"),
        }
    }
}

impl std::error::Error for StoreError {}

fn format_error(message: impl Into<String>) -> StoreError {
    StoreError::Format(message.into())
}

/// The most recently decompressed detail block, shared by lookups on any thread.
type BlockCache = Mutex<Option<(usize, Arc<Vec<u8>>)>>;

pub struct Store {
    path: PathBuf,
    map: Mmap,
    info: StoreInfo,
    cache: BlockCache,
}

impl Store {
    pub fn open(path: impl AsRef<Path>) -> Result<Store, StoreError> {
        let path = path.as_ref().to_path_buf();
        let file = File::open(&path).map_err(|e| StoreError::Io(format!("{}: {e}", path.display())))?;
        // SAFETY: the store is written to a temporary file and renamed into place, never modified
        // in place, so the mapped bytes do not change while the map is alive.
        let map = unsafe { Mmap::map(&file) }.map_err(|e| StoreError::Io(e.to_string()))?;
        let info = read_info(&map)?;
        let store = Store { path, map, info, cache: Mutex::new(None) };
        store.validate()?;
        Ok(store)
    }

    fn validate(&self) -> Result<(), StoreError> {
        let info = &self.info;
        let n = info.puzzle_count;
        let len = self.map.len() as u64;
        let fits = |s: &Section| s.offset.checked_add(s.len).is_some_and(|end| end <= len);
        let s = &info.sections;
        let blocks = n.div_ceil(info.block_size.max(1) as u64);
        let ok = info.block_size > 0
            && s.records.len == n * RECORD_SIZE as u64
            && s.ids.len == n * 8
            && s.id_order.as_ref().is_none_or(|o| o.len == n * 4 && fits(o))
            && s.block_offsets.len == (blocks + 1) * 8
            && [&s.records, &s.ids, &s.block_offsets].into_iter().all(fits)
            && info.themes.len() <= MAX_THEMES;
        if ok { Ok(()) } else { Err(format_error("section sizes do not match the puzzle count")) }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn info(&self) -> &StoreInfo {
        &self.info
    }

    pub fn file_len(&self) -> u64 {
        self.map.len() as u64
    }

    pub fn len(&self) -> usize {
        self.info.puzzle_count as usize
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    fn section(&self, section: &Section) -> &[u8] {
        &self.map[section.offset as usize..(section.offset + section.len) as usize]
    }

    /// The raw record bytes, `RECORD_SIZE` per puzzle, for scanning.
    pub fn record_bytes(&self) -> &[u8] {
        self.section(&self.info.sections.records)
    }

    pub fn record(&self, index: usize) -> Record {
        Record::decode(self.record_bytes()[index * RECORD_SIZE..(index + 1) * RECORD_SIZE].try_into().unwrap())
    }

    fn packed_id(&self, index: usize) -> u64 {
        let ids = self.section(&self.info.sections.ids);
        u64::from_le_bytes(ids[index * 8..index * 8 + 8].try_into().unwrap())
    }

    pub fn id(&self, index: usize) -> String {
        puzzle_id::unpack(self.packed_id(index))
    }

    /// Record index of a puzzle id.
    pub fn find(&self, id: &str) -> Option<usize> {
        let key = puzzle_id::pack(id)?;
        let n = self.len();
        let at = |rank: usize| match &self.info.sections.id_order {
            Some(order) => {
                let order = self.section(order);
                u32::from_le_bytes(order[rank * 4..rank * 4 + 4].try_into().unwrap()) as usize
            }
            None => rank,
        };
        let (mut low, mut high) = (0, n);
        while low < high {
            let mid = low + (high - low) / 2;
            match self.packed_id(at(mid)).cmp(&key) {
                std::cmp::Ordering::Less => low = mid + 1,
                std::cmp::Ordering::Greater => high = mid,
                std::cmp::Ordering::Equal => return Some(at(mid)),
            }
        }
        None
    }

    pub fn theme_names(&self, mask: u128) -> Vec<String> {
        (0..self.info.themes.len())
            .filter(|&bit| mask >> bit & 1 == 1)
            .map(|bit| self.info.themes[bit].clone())
            .collect()
    }

    pub fn family_tag(&self, id: u16) -> Option<&str> {
        id.checked_sub(1).and_then(|i| self.info.opening_families.get(i as usize)).map(String::as_str)
    }

    pub fn variation_tag(&self, id: u16) -> Option<&str> {
        id.checked_sub(1).and_then(|i| self.info.opening_variations.get(i as usize)).map(|v| v.tag.as_str())
    }

    fn block(&self, block: usize) -> Result<Arc<Vec<u8>>, StoreError> {
        if let Some((cached, data)) = self.cache.lock().unwrap().as_ref()
            && *cached == block
        {
            return Ok(data.clone());
        }
        let offsets = self.section(&self.info.sections.block_offsets);
        let offset_at = |i: usize| u64::from_le_bytes(offsets[i * 8..i * 8 + 8].try_into().unwrap()) as usize;
        let (start, end) = (offset_at(block), offset_at(block + 1));
        if start > end || end > self.map.len() {
            return Err(format_error("detail block offsets are damaged"));
        }
        let data = zstd::decode_all(&self.map[start..end]).map_err(|e| format_error(format!("detail block: {e}")))?;
        let data = Arc::new(data);
        *self.cache.lock().unwrap() = Some((block, data.clone()));
        Ok(data)
    }

    pub fn puzzle(&self, index: usize) -> Result<Puzzle, StoreError> {
        if index >= self.len() {
            return Err(StoreError::OutOfRange(index));
        }
        let block_size = self.info.block_size as usize;
        let data = self.block(index / block_size)?;
        let line =
            data.split(|&b| b == b'\n').nth(index % block_size).ok_or_else(|| format_error("detail block is short"))?;
        let line = std::str::from_utf8(line).map_err(|_| format_error("detail text is not UTF-8"))?;
        let mut fields = line.split('\t');
        let mut field = || fields.next().unwrap_or("").to_owned();
        let (fen, moves, game_url, daily) = (field(), field(), field(), field());
        let record = self.record(index);
        Ok(Puzzle {
            index,
            id: self.id(index),
            fen,
            moves: moves.split(' ').filter(|m| !m.is_empty()).map(str::to_owned).collect(),
            rating: record.rating,
            deviation: record.deviation,
            popularity: record.popularity,
            plays: record.plays,
            themes: self.theme_names(record.themes),
            opening_family: self.family_tag(record.family).map(str::to_owned),
            opening_variation: self.variation_tag(record.variation).map(str::to_owned),
            game_url,
            daily_date: daily.parse().ok(),
            solver: record.solver(),
        })
    }
}

fn read_info(map: &[u8]) -> Result<StoreInfo, StoreError> {
    let len = map.len();
    if len < 32 || &map[..8] != MAGIC || &map[len - 8..] != END_MAGIC {
        return Err(format_error("missing store markers"));
    }
    let info_offset = u64::from_le_bytes(map[len - 16..len - 8].try_into().unwrap()) as usize;
    if info_offset < 8 || info_offset > len - 16 {
        return Err(format_error("info offset is out of range"));
    }
    let info: StoreInfo =
        serde_json::from_slice(&map[info_offset..len - 16]).map_err(|e| format_error(format!("info: {e}")))?;
    if info.format_version != FORMAT_VERSION {
        return Err(format_error(format!(
            "format version {} (this build reads {FORMAT_VERSION}); rebuild the store",
            info.format_version
        )));
    }
    Ok(info)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_round_trip() {
        let record = Record {
            themes: 1 << 100 | 5,
            plays: 312_665,
            rating: 3323,
            deviation: 500,
            family: 157,
            variation: 1400,
            popularity: -83,
            moves: 34,
            flags: FLAG_SOLVER_WHITE | FLAG_DAILY,
        };
        assert_eq!(Record::decode(&record.encode()), record);
        assert_eq!(record.solver(), Side::White);
        assert_eq!(record.solution_moves(), 17);
    }
}
