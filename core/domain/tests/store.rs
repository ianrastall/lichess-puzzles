//! Building a store from the sample CSV and querying it.

use std::io::Write;
use std::path::Path;
use std::sync::atomic::AtomicBool;

use lichess_puzzles_core::Side;
use lichess_puzzles_core::build::{BuildError, BuildOptions, build_store};
use lichess_puzzles_core::filter::{self, Filter, FilterError};
use lichess_puzzles_core::store::{Store, StoreError};

const SAMPLE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/sample_puzzles.csv");

fn build(input: &Path, dir: &Path, options: &BuildOptions) -> Store {
    let output = dir.join("puzzles.store");
    build_store(input, &output, None, options, &mut |_| {}, &AtomicBool::new(false)).unwrap();
    Store::open(&output).unwrap()
}

/// Small blocks so the sample spans several detail blocks.
fn small_blocks() -> BuildOptions {
    BuildOptions { block_size: 4, level: 3, threads: 3 }
}

#[test]
fn builds_and_reads_every_puzzle() {
    let dir = tempfile::tempdir().unwrap();
    let store = build(Path::new(SAMPLE), dir.path(), &small_blocks());
    assert_eq!(store.len(), 13);
    assert_eq!(store.info().skipped_count, 0);
    assert!(store.info().sections.id_order.is_none());

    let csv = std::fs::read_to_string(SAMPLE).unwrap();
    for (index, line) in csv.lines().skip(1).enumerate() {
        let cols: Vec<&str> = line.split(',').collect();
        let puzzle = store.puzzle(index).unwrap();
        assert_eq!(puzzle.id, cols[0]);
        assert_eq!(puzzle.fen, cols[1]);
        assert_eq!(puzzle.moves.join(" "), cols[2]);
        assert_eq!(puzzle.rating.to_string(), cols[3]);
        assert_eq!(puzzle.deviation.to_string(), cols[4]);
        assert_eq!(puzzle.popularity.to_string(), cols[5]);
        assert_eq!(puzzle.plays.to_string(), cols[6]);
        let mut themes: Vec<&str> = cols[7].split(' ').collect();
        themes.sort();
        assert_eq!(puzzle.themes, themes);
        assert_eq!(puzzle.game_url, cols[8]);
        let tags: Vec<&str> = cols[9].split(' ').filter(|t| !t.is_empty()).collect();
        assert_eq!(puzzle.opening_family.as_deref(), tags.first().copied());
        assert_eq!(puzzle.opening_variation.as_deref(), tags.get(1).copied());
        let solver = if cols[1].split(' ').nth(1) == Some("w") { Side::Black } else { Side::White };
        assert_eq!(puzzle.solver, solver);
        assert_eq!(store.find(cols[0]), Some(index));
    }
    assert_eq!(store.find("zzzzz"), None);
    assert!(matches!(store.puzzle(13), Err(StoreError::OutOfRange(13))));

    let mut themes = store.info().themes.clone();
    themes.sort();
    assert_eq!(store.info().themes, themes);
}

#[test]
fn compressed_input_gives_the_same_store_contents() {
    let dir = tempfile::tempdir().unwrap();
    let zst = dir.path().join("sample.csv.zst");
    std::fs::write(&zst, zstd::encode_all(std::fs::read(SAMPLE).unwrap().as_slice(), 3).unwrap()).unwrap();
    let plain = build(Path::new(SAMPLE), &dir.path().join("a"), &small_blocks());
    let compressed = build(&zst, &dir.path().join("b"), &BuildOptions::default());
    assert_eq!(compressed.len(), plain.len());
    for i in 0..plain.len() {
        assert_eq!(compressed.puzzle(i).unwrap(), plain.puzzle(i).unwrap());
    }
}

#[test]
fn filters_count_match_and_sample() {
    let dir = tempfile::tempdir().unwrap();
    let store = build(Path::new(SAMPLE), dir.path(), &small_blocks());
    let count = |f: Filter| filter::count(&store, &f.compile(&store).unwrap());

    assert_eq!(count(Filter::default()), 13);
    assert_eq!(count(Filter { rating_min: Some(1500), rating_max: Some(1800), ..Default::default() }), 5);
    assert_eq!(count(Filter { themes_any: vec!["mate".into()], ..Default::default() }), 2);
    assert_eq!(count(Filter { themes_all: vec!["advantage".into(), "fork".into()], ..Default::default() }), 2);
    assert_eq!(count(Filter { themes_none: vec!["endgame".into()], ..Default::default() }), 7);
    assert_eq!(count(Filter { min_solution_moves: Some(3), ..Default::default() }), 5);
    assert_eq!(count(Filter { max_solution_moves: Some(1), ..Default::default() }), 1);
    assert_eq!(count(Filter { solver: Some(Side::White), ..Default::default() }), 6);
    assert_eq!(count(Filter { min_plays: Some(1000), min_popularity: Some(95), ..Default::default() }), 3);

    let unknown = Filter { themes_any: vec!["nonsense".into()], ..Default::default() };
    assert_eq!(unknown.compile(&store).unwrap_err(), FilterError::UnknownTheme("nonsense".into()));

    let mates = Filter { themes_any: vec!["mate".into()], ..Default::default() }.compile(&store).unwrap();
    let matching = filter::matching(&store, &mates);
    assert_eq!(matching.iter().map(|&i| store.id(i as usize)).collect::<Vec<_>>(), ["0000H", "00sHx"]);
    let (total, per_theme) = filter::theme_counts(&store, &mates);
    assert_eq!(total, 2);
    let mate_bit = store.info().themes.iter().position(|t| t == "mate").unwrap();
    assert_eq!(per_theme[mate_bit], 2);

    let all = Filter::default().compile(&store).unwrap();
    let mut picked = filter::sample(&store, &all, 5, 42);
    assert_eq!(picked, filter::sample(&store, &all, 5, 42));
    picked.sort();
    picked.dedup();
    assert_eq!(picked.len(), 5);
    assert_eq!(filter::sample(&store, &mates, 10, 1).len(), 2);
}

#[test]
fn bad_rows_are_skipped_and_unsorted_ids_still_resolve() {
    let dir = tempfile::tempdir().unwrap();
    let input = dir.path().join("mixed.csv");
    let mut file = std::fs::File::create(&input).unwrap();
    let header = "PuzzleId,FEN,Moves,Rating,RatingDeviation,Popularity,NbPlays,Themes,GameUrl,OpeningTags";
    writeln!(file, "{header}").unwrap();
    let good = "5rk1/1p3ppp/pq3b2/8/8/1P1Q1N2/P4PPP/3R2K1 w - - 2 27,d3d6 f8d8 d6d8 f6d8,1529,74,96,37506,advantage endgame short,https://lichess.org/F8M8OS71#53,";
    writeln!(file, "zzz01,{good}").unwrap();
    writeln!(file, "aaa01,{good}").unwrap();
    writeln!(file, "bad01,5rk1/1p3ppp/pq3b2/8/8/1P1Q1N2/P4PPP/3R2K1 w - - 2 27,d3d6 f8d7,1529,74,96,1,short,u,")
        .unwrap();
    writeln!(file, "bad02,not-a-fen,d3d6 f8d8,1529,74,96,1,short,u,").unwrap();
    writeln!(file, "bad03,5rk1/1p3ppp/pq3b2/8/8/1P1Q1N2/P4PPP/3R2K1 w - - 2 27,d3d6,1529,74,96,1,short,u,").unwrap();
    writeln!(file, "bad04,{}", good.replace("1529", "high")).unwrap();
    writeln!(file, "mmm01,{good}").unwrap();
    drop(file);

    let store = build(&input, dir.path(), &BuildOptions::default());
    assert_eq!(store.len(), 3);
    let info = store.info();
    assert_eq!(info.skipped_count, 4);
    let reasons: Vec<&str> = info.skipped.iter().map(|s| s.reason.as_str()).collect();
    assert_eq!(
        reasons,
        [
            "illegal move 2 `f8d7`",
            "invalid FEN `not-a-fen`",
            "1 moves; a puzzle needs an even number from 2",
            "invalid rating"
        ]
    );
    assert_eq!(info.skipped[0].line, 4);
    assert!(info.sections.id_order.is_some());
    for (index, id) in ["zzz01", "aaa01", "mmm01"].into_iter().enumerate() {
        assert_eq!(store.find(id), Some(index));
    }
    assert_eq!(store.puzzle(2).unwrap().opening_family, None);
}

#[test]
fn missing_columns_and_damaged_stores_are_rejected() {
    let dir = tempfile::tempdir().unwrap();
    let input = dir.path().join("short.csv");
    std::fs::write(&input, "PuzzleId,FEN\n00008,8/8/8/8/8/8/8/8 w - - 0 1\n").unwrap();
    let output = dir.path().join("puzzles.store");
    let result = build_store(&input, &output, None, &BuildOptions::default(), &mut |_| {}, &AtomicBool::new(false));
    assert_eq!(result.unwrap_err(), BuildError::MissingColumn("Moves"));
    assert!(!output.exists());

    std::fs::write(&output, b"not a store at all, just some bytes").unwrap();
    assert!(matches!(Store::open(&output), Err(StoreError::Format(_))));
}
