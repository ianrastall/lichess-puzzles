//! Command-line shell over the Lichess Puzzles core. Shares its data folder with the desktop app.

use std::path::{Path, PathBuf};
use std::process::ExitCode;
use std::sync::atomic::AtomicBool;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use clap::{Args, Parser, Subcommand, ValueEnum};
use lichess_puzzles_core::build::{BuildOptions, BuildProgress, build_store};
use lichess_puzzles_core::filter::{self, Filter};
use lichess_puzzles_core::source::{DownloadOutcome, PUZZLE_URL, Progress, Source, UpdateCheck};
use lichess_puzzles_core::store::Store;
use lichess_puzzles_core::{STORE_FILE, Side, chess, default_data_dir, engine};

#[derive(Parser)]
#[command(name = "lichess-puzzles", version, about = "Downloads, filters and inspects the Lichess puzzle database.")]
struct Cli {
    /// Data folder holding the downloaded file and the puzzle store.
    #[arg(long, global = true)]
    data_dir: Option<PathBuf>,
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Download the puzzle database, resuming an interrupted download. Skipped when current.
    Download {
        /// Download even if the local copy matches the server.
        #[arg(long)]
        force: bool,
        #[arg(long, default_value = PUZZLE_URL)]
        url: String,
    },
    /// Compare the downloaded file with the server's (one HEAD request).
    CheckUpdate {
        #[arg(long, default_value = PUZZLE_URL)]
        url: String,
    },
    /// Build the puzzle store from the downloaded file or another .csv or .csv.zst.
    Build {
        #[arg(long)]
        input: Option<PathBuf>,
        /// zstd level for puzzle details.
        #[arg(long, default_value_t = 9)]
        level: i32,
        /// Puzzles per compressed detail block.
        #[arg(long, default_value_t = 1024)]
        block_size: u32,
    },
    /// Describe the puzzle store.
    Status,
    /// Count the puzzles matching a filter.
    Count(FilterArgs),
    /// List random puzzles matching a filter.
    Sample {
        #[command(flatten)]
        filter: FilterArgs,
        #[arg(short, default_value_t = 10)]
        n: usize,
        #[arg(long)]
        seed: Option<u64>,
    },
    /// Show one puzzle with its solution in SAN.
    Show { id: String },
    /// Matching puzzles per theme.
    Themes(FilterArgs),
    /// Matching puzzles per opening family.
    Openings(FilterArgs),
    /// Run the evaluation engine on one puzzle, or on a random sample matching a filter, and
    /// report how often its first move is the solution's.
    Analyse {
        id: Option<String>,
        #[command(flatten)]
        filter: FilterArgs,
        #[arg(short, default_value_t = 20)]
        n: usize,
        /// Thinking time per puzzle.
        #[arg(long, default_value_t = 1000)]
        millis: u64,
        #[arg(long)]
        seed: Option<u64>,
    },
}

#[derive(Args, Default)]
struct FilterArgs {
    /// Rating range: 1500-1800, 1500- or -1800.
    #[arg(long, value_parser = parse_range::<u16>)]
    rating: Option<(Option<u16>, Option<u16>)>,
    #[arg(long)]
    max_rd: Option<u16>,
    #[arg(long, allow_negative_numbers = true)]
    min_popularity: Option<i8>,
    #[arg(long)]
    min_plays: Option<u32>,
    /// Theme key; puzzles need at least one of those given.
    #[arg(long = "theme")]
    themes_any: Vec<String>,
    /// Theme key; puzzles need every one given.
    #[arg(long = "all-themes")]
    themes_all: Vec<String>,
    /// Theme key to exclude.
    #[arg(long = "without-theme")]
    themes_none: Vec<String>,
    /// Opening family or variation tag, such as Sicilian_Defense.
    #[arg(long = "opening")]
    openings: Vec<String>,
    #[arg(long)]
    solver: Option<SideArg>,
    /// Moves the solver plays: 2-3, 2- or -3.
    #[arg(long, value_parser = parse_range::<u8>)]
    length: Option<(Option<u8>, Option<u8>)>,
}

#[derive(Clone, Copy, ValueEnum)]
enum SideArg {
    White,
    Black,
}

impl FilterArgs {
    fn filter(&self) -> Filter {
        let (rating_min, rating_max) = self.rating.unwrap_or_default();
        let (min_solution_moves, max_solution_moves) = self.length.unwrap_or_default();
        Filter {
            rating_min,
            rating_max,
            max_deviation: self.max_rd,
            min_popularity: self.min_popularity,
            min_plays: self.min_plays,
            themes_any: self.themes_any.clone(),
            themes_all: self.themes_all.clone(),
            themes_none: self.themes_none.clone(),
            openings: self.openings.clone(),
            solver: self.solver.map(|s| match s {
                SideArg::White => Side::White,
                SideArg::Black => Side::Black,
            }),
            min_solution_moves,
            max_solution_moves,
        }
    }
}

type Range<T> = (Option<T>, Option<T>);

fn parse_range<T: std::str::FromStr + Copy>(text: &str) -> Result<Range<T>, String> {
    let bound = |s: &str| -> Result<Option<T>, String> {
        if s.is_empty() { Ok(None) } else { s.parse().map(Some).map_err(|_| format!("`{s}` is not a number")) }
    };
    match text.split_once('-') {
        Some((low, high)) => Ok((bound(low)?, bound(high)?)),
        None => bound(text).map(|v| (v, v)),
    }
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    let data_dir = cli.data_dir.unwrap_or_else(default_data_dir);
    match run(cli.command, &data_dir) {
        Ok(()) => ExitCode::SUCCESS,
        Err(message) => {
            eprintln!("error: {message}");
            ExitCode::FAILURE
        }
    }
}

fn run(command: Command, data_dir: &Path) -> Result<(), String> {
    let source_dir = data_dir.join("source");
    let store_path = data_dir.join(STORE_FILE);
    match command {
        Command::Download { force, url } => download(&Source::new(&source_dir, url), force),
        Command::CheckUpdate { url } => {
            let source = Source::new(&source_dir, url);
            match source.check_update().map_err(|e| e.to_string())? {
                UpdateCheck::NotDownloaded => println!("Not downloaded."),
                UpdateCheck::Current(remote) => println!("Current ({}).", describe(&remote)),
                UpdateCheck::Available { local, remote } => {
                    println!("Update available: {}", describe(&remote));
                    println!("Downloaded:       {}", describe(&local));
                }
            }
            Ok(())
        }
        Command::Build { input, level, block_size } => {
            let source = Source::new(&source_dir, PUZZLE_URL);
            let (input, version) = match input {
                Some(input) => (input, None),
                None => (source.file_path(), source.local_version()),
            };
            if !input.is_file() {
                return Err(format!("{} does not exist; run `download` or pass --input", input.display()));
            }
            let options = BuildOptions { level, block_size, ..BuildOptions::default() };
            let mut last = Instant::now();
            let mut show = |p: BuildProgress| {
                if last.elapsed().as_millis() >= 500 {
                    last = Instant::now();
                    let percent = p.input_read as f64 * 100.0 / p.input_total.max(1) as f64;
                    eprint!("\r{} puzzles read, {percent:.0}% of input   ", group(p.rows));
                }
            };
            let report = build_store(&input, &store_path, version, &options, &mut show, &AtomicBool::new(false))
                .map_err(|e| e.to_string())?;
            eprintln!();
            println!(
                "Built {} puzzles in {:.1} s; {} skipped; store {}.",
                group(report.puzzles),
                report.elapsed.as_secs_f64(),
                group(report.skipped),
                megabytes(report.store_bytes)
            );
            if !report.ids_sorted {
                println!("Puzzle ids were not in order; {} duplicates.", report.duplicate_ids);
            }
            let store = Store::open(&store_path).map_err(|e| e.to_string())?;
            for row in &store.info().skipped {
                println!("  skipped line {} ({}): {}", row.line, row.id, row.reason);
            }
            Ok(())
        }
        Command::Status => {
            let started = Instant::now();
            let store = open(&store_path)?;
            let opened = started.elapsed();
            let info = store.info();
            println!("Store:      {} ({})", store.path().display(), megabytes(store.file_len()));
            println!("Puzzles:    {}", group(info.puzzle_count));
            println!("Skipped:    {}", group(info.skipped_count));
            println!("Themes:     {}", info.themes.len());
            println!(
                "Openings:   {} families, {} variations",
                info.opening_families.len(),
                info.opening_variations.len()
            );
            println!("Built:      {} by {}", date(info.built_at_unix), info.builder);
            let version = info.source.version.as_ref().map(describe).unwrap_or_else(|| "local file".into());
            println!("Source:     {} ({}), {}", info.source.file_name, megabytes(info.source.bytes), version);
            println!("Opened in   {:.1} ms", opened.as_secs_f64() * 1000.0);
            Ok(())
        }
        Command::Count(args) => {
            let store = open(&store_path)?;
            let compiled = args.filter().compile(&store).map_err(|e| e.to_string())?;
            let started = Instant::now();
            let count = filter::count(&store, &compiled);
            println!("{} puzzles ({:.1} ms)", group(count as u64), started.elapsed().as_secs_f64() * 1000.0);
            Ok(())
        }
        Command::Sample { filter: args, n, seed } => {
            let store = open(&store_path)?;
            let compiled = args.filter().compile(&store).map_err(|e| e.to_string())?;
            let seed =
                seed.unwrap_or_else(|| SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_nanos() as u64));
            let started = Instant::now();
            let picked = filter::sample(&store, &compiled, n, seed);
            let elapsed = started.elapsed();
            for index in picked {
                let p = store.puzzle(index).map_err(|e| e.to_string())?;
                println!("{:<6} {:>4}  {:<5}  {}", p.id, p.rating, format!("{:?}", p.solver), p.themes.join(" "));
            }
            eprintln!("({:.1} ms to choose)", elapsed.as_secs_f64() * 1000.0);
            Ok(())
        }
        Command::Show { id } => {
            let store = open(&store_path)?;
            let started = Instant::now();
            let index = store.find(&id).ok_or_else(|| format!("no puzzle `{id}`"))?;
            let p = store.puzzle(index).map_err(|e| e.to_string())?;
            let elapsed = started.elapsed();
            let uci: Vec<&str> = p.moves.iter().map(String::as_str).collect();
            let san = chess::san_line(&p.fen, &uci).map_err(|e| e.to_string())?;
            println!("Puzzle      {} (record {})", p.id, p.index);
            println!("Rating      {} ± {}", p.rating, p.deviation);
            println!("Popularity  {}, {} plays", p.popularity, group(p.plays as u64));
            println!("Solver      {:?}", p.solver);
            println!("Themes      {}", p.themes.join(" "));
            let opening = [p.opening_family.as_deref(), p.opening_variation.as_deref()];
            println!("Opening     {}", opening.iter().flatten().copied().collect::<Vec<_>>().join(" / "));
            println!("FEN         {}", p.fen);
            println!("Opponent    {}", san[0]);
            println!("Solution    {}", san[1..].join(" "));
            println!("Game        {}", p.game_url);
            println!("({:.2} ms to look up)", elapsed.as_secs_f64() * 1000.0);
            Ok(())
        }
        Command::Themes(args) => {
            let store = open(&store_path)?;
            let compiled = args.filter().compile(&store).map_err(|e| e.to_string())?;
            let counts = filter::counts(&store, &compiled);
            let total = counts.total;
            let mut rows: Vec<(&String, usize)> = store.info().themes.iter().zip(counts.themes).collect();
            rows.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(b.0)));
            for (theme, count) in rows {
                println!("{:>10}  {theme}", group(count as u64));
            }
            println!("{:>10}  matching puzzles", group(total as u64));
            Ok(())
        }
        Command::Analyse { id, filter: args, n, millis, seed } => {
            let store = open(&store_path)?;
            let indices = match id {
                Some(id) => vec![store.find(&id).ok_or_else(|| format!("no puzzle `{id}`"))?],
                None => {
                    let compiled = args.filter().compile(&store).map_err(|e| e.to_string())?;
                    let seed = seed.unwrap_or_else(|| {
                        SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_nanos() as u64)
                    });
                    filter::sample(&store, &compiled, n, seed)
                }
            };
            analyse(&store, &indices, millis)
        }
        Command::Openings(args) => {
            let store = open(&store_path)?;
            let compiled = args.filter().compile(&store).map_err(|e| e.to_string())?;
            let counts = filter::counts(&store, &compiled).families;
            let mut rows: Vec<(String, u64)> = counts
                .iter()
                .enumerate()
                .map(|(id, &n)| (store.family_tag(id as u16).unwrap_or("(no opening)").to_owned(), n as u64))
                .filter(|(_, n)| *n > 0)
                .collect();
            rows.sort_by(|a, b| b.1.cmp(&a.1).then(a.0.cmp(&b.0)));
            for (family, count) in rows {
                println!("{:>10}  {family}", group(count));
            }
            Ok(())
        }
    }
}

fn analyse(store: &Store, indices: &[usize], millis: u64) -> Result<(), String> {
    let (mut found, mut total) = (0, 0);
    for &index in indices {
        let p = store.puzzle(index).map_err(|e| e.to_string())?;
        let mut board = chess::parse_fen(&p.fen).map_err(|e| e.to_string())?;
        let uci: Vec<&str> = p.moves.iter().map(String::as_str).collect();
        let line = chess::replay(&board, &uci).map_err(|e| e.to_string())?;
        board.play(line[0]);
        let stop = std::sync::atomic::AtomicBool::new(false);
        let Some(info) = engine::analyse(&board, std::time::Duration::from_millis(millis), 64, &stop, &mut |_| {})
        else {
            continue;
        };
        let solution = chess::san_line(&p.fen, &uci[..2]).map_err(|e| e.to_string())?[1].clone();
        // Any mating move solves a mate puzzle.
        let hit = info.pv_uci.first().map(String::as_str) == Some(uci[1])
            || (matches!(info.score, engine::Score::Mate(_))
                && p.themes.iter().any(|t| t == "mate")
                && info.pv_san[0].ends_with('#'));
        total += 1;
        found += usize::from(hit);
        let score = match info.score {
            engine::Score::Centipawns(cp) => format!("{:+.2}", f64::from(cp) / 100.0),
            engine::Score::Mate(m) => format!("mate {m}"),
        };
        println!(
            "{:<6} {:>4}  {}  depth {:>2}  {:>9}  engine {:<8} solution {:<8} {}",
            p.id,
            p.rating,
            if hit { "found " } else { "missed" },
            info.depth,
            score,
            info.pv_san.first().map(String::as_str).unwrap_or("-"),
            solution,
            info.pv_san.join(" ")
        );
    }
    if total > 1 {
        println!(
            "Found {found} of {total} first moves ({:.0}%) at {millis} ms each.",
            found as f64 * 100.0 / total as f64
        );
    }
    Ok(())
}

fn open(path: &Path) -> Result<Store, String> {
    if !path.is_file() {
        return Err(format!("no puzzle store at {}; run `download` and `build`", path.display()));
    }
    Store::open(path).map_err(|e| e.to_string())
}

fn download(source: &Source, force: bool) -> Result<(), String> {
    if !force && let UpdateCheck::Current(remote) = source.check_update().map_err(|e| e.to_string())? {
        println!("Already current ({}).", describe(&remote));
        return Ok(());
    }
    let started = Instant::now();
    let mut show = |p: Progress| {
        let total = p.total.map(megabytes).unwrap_or_else(|| "?".into());
        let rate = p.rate / 1_048_576.0;
        let remaining = match p.total {
            Some(total) if p.rate > 0.0 => format!(", {:.0} s left", (total - p.downloaded) as f64 / p.rate),
            _ => String::new(),
        };
        eprint!("\r{} of {total}, {rate:.1} MB/s{remaining}      ", megabytes(p.downloaded));
    };
    let outcome = source.download(&mut show, &AtomicBool::new(false)).map_err(|e| e.to_string())?;
    eprintln!();
    match outcome {
        DownloadOutcome::Completed { bytes, resumed_from, version } => {
            let resumed =
                if resumed_from > 0 { format!(", resumed at {}", megabytes(resumed_from)) } else { String::new() };
            println!(
                "Downloaded {} in {:.1} s{resumed}: {}",
                megabytes(bytes),
                started.elapsed().as_secs_f64(),
                source.file_path().display()
            );
            println!("Version: {}", describe(&version));
        }
        DownloadOutcome::Cancelled { downloaded } => println!("Cancelled at {}.", megabytes(downloaded)),
    }
    Ok(())
}

fn describe(version: &lichess_puzzles_core::source::RemoteVersion) -> String {
    let mut parts = Vec::new();
    if let Some(date) = &version.last_modified {
        parts.push(date.clone());
    }
    if let Some(length) = version.length {
        parts.push(megabytes(length));
    }
    if let Some(etag) = &version.etag {
        parts.push(format!("ETag {etag}"));
    }
    parts.join(", ")
}

fn megabytes(bytes: u64) -> String {
    format!("{:.1} MB", bytes as f64 / 1_048_576.0)
}

fn group(n: u64) -> String {
    let digits = n.to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(',');
        }
        out.push(c);
    }
    out
}

/// UTC date and time from Unix seconds.
fn date(seconds: u64) -> String {
    let days = (seconds / 86_400) as i64;
    let (h, m) = ((seconds % 86_400) / 3600, (seconds % 3600) / 60);
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year}-{month:02}-{d:02} {h:02}:{m:02} UTC")
}
