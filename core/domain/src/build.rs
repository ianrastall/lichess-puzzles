//! Building the puzzle store from the Lichess CSV, compressed (`.zst`) or plain.
//!
//! One pass reads the CSV, checks each puzzle with the chess rules and appends its record; detail
//! blocks are compressed on worker threads and written in order. Theme bits and opening ids are
//! assigned as they are first seen and renumbered in name order at the end.

use std::collections::{BTreeMap, HashMap};
use std::fs::{self, File};
use std::io::{BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, sync_channel};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use crate::chess;
use crate::puzzle_id;
use crate::source::RemoteVersion;
use crate::store::{
    END_MAGIC, FLAG_DAILY, FLAG_SOLVER_WHITE, FORMAT_VERSION, MAGIC, MAX_THEMES, RECORD_SIZE, Record, Section,
    Sections, SkippedRow, SourceInfo, StoreInfo, Variation,
};

const SKIPPED_EXAMPLES: usize = 20;
const PROGRESS_ROWS: u64 = 50_000;

#[derive(Debug, Clone)]
pub struct BuildOptions {
    /// Puzzles per detail block.
    pub block_size: u32,
    /// zstd level for detail blocks.
    pub level: i32,
    /// Compression threads.
    pub threads: usize,
}

impl Default for BuildOptions {
    fn default() -> Self {
        let threads = std::thread::available_parallelism().map_or(2, |n| n.get()).clamp(1, 8);
        BuildOptions { block_size: 1024, level: 9, threads }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BuildProgress {
    pub rows: u64,
    /// Bytes of the input file read so far.
    pub input_read: u64,
    pub input_total: u64,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuildReport {
    pub puzzles: u64,
    pub skipped: u64,
    pub duplicate_ids: u64,
    pub ids_sorted: bool,
    pub store_bytes: u64,
    pub elapsed: Duration,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BuildError {
    Io(String),
    Csv(String),
    MissingColumn(&'static str),
    TooManyThemes,
    Cancelled,
}

impl std::fmt::Display for BuildError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            BuildError::Io(message) => write!(f, "file error: {message}"),
            BuildError::Csv(message) => write!(f, "CSV error: {message}"),
            BuildError::MissingColumn(name) => write!(f, "the CSV has no `{name}` column"),
            BuildError::TooManyThemes => write!(f, "the CSV uses more than {MAX_THEMES} themes"),
            BuildError::Cancelled => write!(f, "cancelled"),
        }
    }
}

impl std::error::Error for BuildError {}

fn io_error(error: std::io::Error) -> BuildError {
    BuildError::Io(error.to_string())
}

struct CountingReader<R> {
    inner: R,
    count: Arc<AtomicU64>,
}

impl<R: Read> Read for CountingReader<R> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let read = self.inner.read(buf)?;
        self.count.fetch_add(read as u64, Ordering::Relaxed);
        Ok(read)
    }
}

struct Columns {
    id: usize,
    fen: usize,
    moves: usize,
    rating: usize,
    deviation: usize,
    popularity: usize,
    plays: usize,
    themes: usize,
    game_url: usize,
    openings: Option<usize>,
    daily: Option<usize>,
}

impl Columns {
    fn from_headers(headers: &csv::ByteRecord) -> Result<Columns, BuildError> {
        let find = |name: &str| headers.iter().position(|h| h == name.as_bytes());
        let need = |name: &'static str| find(name).ok_or(BuildError::MissingColumn(name));
        Ok(Columns {
            id: need("PuzzleId")?,
            fen: need("FEN")?,
            moves: need("Moves")?,
            rating: need("Rating")?,
            deviation: need("RatingDeviation")?,
            popularity: need("Popularity")?,
            plays: need("NbPlays")?,
            themes: need("Themes")?,
            game_url: need("GameUrl")?,
            openings: find("OpeningTags"),
            daily: find("DailyDate"),
        })
    }
}

/// Names numbered in first-seen order, renumbered by name at the end.
#[derive(Default)]
struct Vocabulary {
    ids: HashMap<String, usize>,
    names: Vec<String>,
}

impl Vocabulary {
    fn id(&mut self, name: &str) -> usize {
        if let Some(&id) = self.ids.get(name) {
            return id;
        }
        let id = self.names.len();
        self.ids.insert(name.to_owned(), id);
        self.names.push(name.to_owned());
        id
    }

    /// Sorted names and the new position of each first-seen id.
    fn sorted(&self) -> (Vec<String>, Vec<usize>) {
        let mut order: Vec<usize> = (0..self.names.len()).collect();
        order.sort_by(|&a, &b| self.names[a].cmp(&self.names[b]));
        let mut new_of_old = vec![0; order.len()];
        for (new, &old) in order.iter().enumerate() {
            new_of_old[old] = new;
        }
        (order.into_iter().map(|old| self.names[old].clone()).collect(), new_of_old)
    }
}

struct Parsed {
    record: Record,
    id: u64,
    fen: String,
    moves: String,
    game_url: String,
    daily: String,
}

struct Builder {
    columns: Columns,
    themes: Vocabulary,
    families: Vocabulary,
    variations: Vocabulary,
    /// First-seen family id of each first-seen variation.
    variation_family: Vec<usize>,
}

impl Builder {
    /// The puzzle in `row`, or the reason it is skipped.
    fn parse(&mut self, row: &csv::ByteRecord) -> Result<Result<Parsed, String>, BuildError> {
        let field = |i: usize| -> Result<&str, String> {
            std::str::from_utf8(row.get(i).unwrap_or_default()).map_err(|_| "text is not UTF-8".to_owned())
        };
        let number = |i: usize, name: &str| -> Result<i64, String> {
            field(i)?.trim().parse::<i64>().map_err(|_| format!("invalid {name}"))
        };
        let columns = &self.columns;
        let fields = (|| {
            let id = puzzle_id::pack(field(columns.id)?).ok_or("invalid puzzle id")?;
            let daily = match columns.daily {
                Some(i) => field(i)?,
                None => "",
            };
            let text = [field(columns.fen)?, field(columns.moves)?, field(columns.game_url)?, daily];
            if text.iter().any(|s| s.contains(['\t', '\n', '\r'])) {
                return Err("field contains a tab or line break".to_owned());
            }
            let numbers = [
                number(columns.rating, "rating")?,
                number(columns.deviation, "rating deviation")?,
                number(columns.popularity, "popularity")?,
                number(columns.plays, "play count")?,
            ];
            let openings = match columns.openings {
                Some(i) => field(i)?,
                None => "",
            };
            Ok((id, text, numbers, field(columns.themes)?, openings))
        })();
        let (id, [fen, moves, game_url, daily], [rating, deviation, popularity, plays], themes, openings) = match fields
        {
            Ok(fields) => fields,
            Err(reason) => return Ok(Err(reason)),
        };

        let uci: Vec<&str> = moves.split(' ').filter(|m| !m.is_empty()).collect();
        if uci.len() < 2 || !uci.len().is_multiple_of(2) || uci.len() > u8::MAX as usize {
            return Ok(Err(format!("{} moves; a puzzle needs an even number from 2", uci.len())));
        }
        let board = match chess::parse_fen(fen) {
            Ok(board) => board,
            Err(e) => return Ok(Err(e.to_string())),
        };
        if let Err(e) = chess::replay(&board, &uci) {
            return Ok(Err(e.to_string()));
        }

        let mut theme_mask = 0u128;
        for theme in themes.split(' ').filter(|t| !t.is_empty()) {
            let bit = self.themes.id(theme);
            if bit >= MAX_THEMES {
                return Err(BuildError::TooManyThemes);
            }
            theme_mask |= 1u128 << bit;
        }
        let (mut family, mut variation) = (0u16, 0u16);
        let mut tags = openings.split(' ').filter(|t| !t.is_empty());
        if let Some(tag) = tags.next() {
            let f = self.families.id(tag);
            family = f as u16 + 1;
            if let Some(tag) = tags.next() {
                let v = self.variations.id(tag);
                if v == self.variation_family.len() {
                    self.variation_family.push(f);
                }
                variation = v as u16 + 1;
            }
        }

        let solver_white = chess::solver(board.side_to_move()) == cozy_chess::Color::White;
        let flags = if solver_white { FLAG_SOLVER_WHITE } else { 0 } | if daily.is_empty() { 0 } else { FLAG_DAILY };
        let record = Record {
            themes: theme_mask,
            plays: plays.clamp(0, u32::MAX as i64) as u32,
            rating: rating.clamp(0, u16::MAX as i64) as u16,
            deviation: deviation.clamp(0, u16::MAX as i64) as u16,
            family,
            variation,
            popularity: popularity.clamp(i8::MIN as i64, i8::MAX as i64) as i8,
            moves: uci.len() as u8,
            flags,
        };
        Ok(Ok(Parsed {
            record,
            id,
            fen: fen.to_owned(),
            moves: uci.join(" "),
            game_url: game_url.to_owned(),
            daily: daily.to_owned(),
        }))
    }
}

/// Builds `output` from `input`. The store is written beside `output` and moved into place only
/// when complete, so an existing store stays usable until then.
pub fn build_store(
    input: &Path,
    output: &Path,
    source_version: Option<RemoteVersion>,
    options: &BuildOptions,
    progress: &mut dyn FnMut(BuildProgress),
    cancel: &AtomicBool,
) -> Result<BuildReport, BuildError> {
    let started = Instant::now();
    let file = File::open(input).map_err(|e| BuildError::Io(format!("{}: {e}", input.display())))?;
    let input_total = file.metadata().map_err(io_error)?.len();
    let input_read = Arc::new(AtomicU64::new(0));
    let counted = CountingReader { inner: BufReader::with_capacity(1 << 20, file), count: input_read.clone() };
    let compressed = input.extension().is_some_and(|e| e.eq_ignore_ascii_case("zst"));
    let reader: Box<dyn Read> =
        if compressed { Box::new(zstd::Decoder::new(counted).map_err(io_error)?) } else { Box::new(counted) };
    let mut csv = csv::ReaderBuilder::new().buffer_capacity(1 << 20).from_reader(reader);
    let headers = csv.byte_headers().map_err(|e| BuildError::Csv(e.to_string()))?.clone();
    let mut builder = Builder {
        columns: Columns::from_headers(&headers)?,
        themes: Vocabulary::default(),
        families: Vocabulary::default(),
        variations: Vocabulary::default(),
        variation_family: Vec::new(),
    };

    if let Some(dir) = output.parent() {
        fs::create_dir_all(dir).map_err(io_error)?;
    }
    let temp = temp_path(output);
    let result = write_store(
        &mut csv,
        &mut builder,
        &temp,
        source_version,
        input,
        input_total,
        options,
        &mut |rows| progress(BuildProgress { rows, input_read: input_read.load(Ordering::Relaxed), input_total }),
        cancel,
    );
    match result {
        Ok(mut report) => {
            fs::rename(&temp, output).map_err(io_error)?;
            report.elapsed = started.elapsed();
            Ok(report)
        }
        Err(error) => {
            let _ = fs::remove_file(&temp);
            Err(error)
        }
    }
}

fn temp_path(output: &Path) -> PathBuf {
    let mut name = output.file_name().unwrap_or_default().to_os_string();
    name.push(".tmp");
    output.with_file_name(name)
}

type Block = (usize, Vec<u8>);

#[allow(clippy::too_many_arguments)]
fn write_store(
    csv: &mut csv::Reader<Box<dyn Read>>,
    builder: &mut Builder,
    temp: &Path,
    source_version: Option<RemoteVersion>,
    input: &Path,
    input_total: u64,
    options: &BuildOptions,
    progress: &mut dyn FnMut(u64),
    cancel: &AtomicBool,
) -> Result<BuildReport, BuildError> {
    let block_size = options.block_size.max(1) as usize;
    let threads = options.threads.max(1);
    let mut out = BufWriter::with_capacity(1 << 20, File::create(temp).map_err(io_error)?);
    out.write_all(MAGIC).map_err(io_error)?;

    let mut records: Vec<u8> = Vec::new();
    let mut ids: Vec<u64> = Vec::new();
    let mut skipped: Vec<SkippedRow> = Vec::new();
    let mut skipped_count = 0u64;

    let (out, block_offsets, parse_result) = std::thread::scope(|scope| {
        let (work_tx, work_rx) = sync_channel::<Block>(threads * 2);
        let work_rx = Arc::new(Mutex::new(work_rx));
        let (done_tx, done_rx) = sync_channel::<(usize, std::io::Result<Vec<u8>>)>(threads * 2);
        for _ in 0..threads {
            let (work_rx, done_tx) = (work_rx.clone(), done_tx.clone());
            let level = options.level;
            scope.spawn(move || {
                loop {
                    let job = work_rx.lock().unwrap().recv();
                    let Ok((index, text)) = job else { break };
                    if done_tx.send((index, zstd::encode_all(&text[..], level))).is_err() {
                        break;
                    }
                }
            });
        }
        drop(done_tx);
        let writer = scope.spawn(move || write_blocks(out, done_rx));

        let mut parse = || -> Result<(), BuildError> {
            let mut row = csv::ByteRecord::new();
            let mut block = Vec::with_capacity(block_size * 160);
            let (mut in_block, mut next_block, mut rows) = (0usize, 0usize, 0u64);
            loop {
                if !csv.read_byte_record(&mut row).map_err(|e| BuildError::Csv(e.to_string()))? {
                    break;
                }
                rows += 1;
                if rows % PROGRESS_ROWS == 0 {
                    if cancel.load(Ordering::Relaxed) {
                        return Err(BuildError::Cancelled);
                    }
                    progress(rows);
                }
                match builder.parse(&row)? {
                    Ok(p) => {
                        records.extend_from_slice(&p.record.encode());
                        ids.push(p.id);
                        for (i, text) in [&p.fen, &p.moves, &p.game_url, &p.daily].into_iter().enumerate() {
                            if i > 0 {
                                block.push(b'\t');
                            }
                            block.extend_from_slice(text.as_bytes());
                        }
                        block.push(b'\n');
                        in_block += 1;
                        if in_block == block_size {
                            let full = std::mem::replace(&mut block, Vec::with_capacity(block_size * 160));
                            if work_tx.send((next_block, full)).is_err() {
                                return Ok(()); // the writer failed; its error is reported below
                            }
                            next_block += 1;
                            in_block = 0;
                        }
                    }
                    Err(reason) => {
                        skipped_count += 1;
                        if skipped.len() < SKIPPED_EXAMPLES {
                            let id =
                                String::from_utf8_lossy(row.get(builder.columns.id).unwrap_or_default()).into_owned();
                            let line = row.position().map_or(rows + 1, |p| p.line());
                            skipped.push(SkippedRow { line, id, reason });
                        }
                    }
                }
            }
            if in_block > 0 {
                let _ = work_tx.send((next_block, block));
            }
            progress(rows);
            Ok(())
        };
        let parse_result = parse();
        drop(work_tx);
        let (out, offsets) = writer.join().expect("writer thread");
        (out, offsets, parse_result)
    });
    parse_result?;
    let mut out = out?;

    // Renumber themes and openings in name order.
    let (themes, theme_bit) = builder.themes.sorted();
    let (families, family_pos) = builder.families.sorted();
    let (variation_tags, variation_pos) = builder.variations.sorted();
    let mut opening_variations = vec![Variation { tag: String::new(), family: 0 }; variation_tags.len()];
    for (old, &new) in variation_pos.iter().enumerate() {
        let family = family_pos[builder.variation_family[old]] as u16 + 1;
        opening_variations[new] = Variation { tag: variation_tags[new].clone(), family };
    }
    for chunk in records.as_chunks_mut::<RECORD_SIZE>().0 {
        let mut record = Record::decode(chunk);
        let mut mask = 0u128;
        for (old, &new) in theme_bit.iter().enumerate() {
            mask |= (record.themes >> old & 1) << new;
        }
        record.themes = mask;
        if record.family > 0 {
            record.family = family_pos[record.family as usize - 1] as u16 + 1;
        }
        if record.variation > 0 {
            record.variation = variation_pos[record.variation as usize - 1] as u16 + 1;
        }
        chunk.copy_from_slice(&record.encode());
    }

    let ids_sorted = ids.windows(2).all(|w| w[0] < w[1]);
    let mut duplicate_ids = 0;
    let id_order = (!ids_sorted).then(|| {
        let mut order: Vec<u32> = (0..ids.len() as u32).collect();
        order.sort_by_key(|&i| ids[i as usize]);
        duplicate_ids = order.windows(2).filter(|w| ids[w[0] as usize] == ids[w[1] as usize]).count() as u64;
        order
    });

    let mut position = *block_offsets.last().expect("first offset");
    let mut section = |len: u64| {
        let s = Section { offset: position, len };
        position += len;
        s
    };
    let records_section = section(records.len() as u64);
    let ids_section = section(ids.len() as u64 * 8);
    let order_section = id_order.as_ref().map(|o| section(o.len() as u64 * 4));
    let offsets_section = section(block_offsets.len() as u64 * 8);
    let info_offset = position;

    out.write_all(&records).map_err(io_error)?;
    drop(records);
    for id in &ids {
        out.write_all(&id.to_le_bytes()).map_err(io_error)?;
    }
    if let Some(order) = &id_order {
        for index in order {
            out.write_all(&index.to_le_bytes()).map_err(io_error)?;
        }
    }
    for offset in &block_offsets {
        out.write_all(&offset.to_le_bytes()).map_err(io_error)?;
    }

    let info = StoreInfo {
        format_version: FORMAT_VERSION,
        builder: format!("lichess_puzzles_core {}", env!("CARGO_PKG_VERSION")),
        built_at_unix: SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_secs()),
        puzzle_count: ids.len() as u64,
        block_size: block_size as u32,
        sections: Sections {
            records: records_section,
            ids: ids_section,
            id_order: order_section,
            block_offsets: offsets_section,
        },
        themes,
        opening_families: families,
        opening_variations,
        source: SourceInfo {
            file_name: input.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
            bytes: input_total,
            version: source_version,
        },
        skipped_count,
        skipped,
    };
    let json = serde_json::to_vec(&info).expect("info serializes");
    out.write_all(&json).map_err(io_error)?;
    out.write_all(&info_offset.to_le_bytes()).map_err(io_error)?;
    out.write_all(END_MAGIC).map_err(io_error)?;
    let file = out.into_inner().map_err(|e| io_error(e.into_error()))?;
    file.sync_all().map_err(io_error)?;
    let store_bytes = file.metadata().map_err(io_error)?.len();

    Ok(BuildReport {
        puzzles: info.puzzle_count,
        skipped: info.skipped_count,
        duplicate_ids,
        ids_sorted,
        store_bytes,
        elapsed: Duration::ZERO,
    })
}

/// Writes compressed blocks in index order, returning the writer and the block offsets
/// (`block_count + 1` entries, starting after the magic).
fn write_blocks(
    mut out: BufWriter<File>,
    done: Receiver<(usize, std::io::Result<Vec<u8>>)>,
) -> (Result<BufWriter<File>, BuildError>, Vec<u64>) {
    let mut offsets = vec![MAGIC.len() as u64];
    let mut pending = BTreeMap::new();
    let mut next = 0;
    for (index, data) in done {
        let data = match data {
            Ok(data) => data,
            Err(e) => return (Err(BuildError::Io(format!("compression: {e}"))), offsets),
        };
        pending.insert(index, data);
        while let Some(data) = pending.remove(&next) {
            if let Err(e) = out.write_all(&data) {
                return (Err(io_error(e)), offsets);
            }
            offsets.push(offsets.last().unwrap() + data.len() as u64);
            next += 1;
        }
    }
    (Ok(out), offsets)
}
