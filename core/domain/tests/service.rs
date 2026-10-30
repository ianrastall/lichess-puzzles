//! The service end to end: download, build, install, update and play, against a local server;
//! ratings, history, played puzzles, export and analysis.

mod support;

use std::path::Path;
use std::time::{Duration, Instant};

use lichess_puzzles_core::engine::Score;
use lichess_puzzles_core::export::ExportFormat;
use lichess_puzzles_core::filter::Filter;
use lichess_puzzles_core::players::PuzzleResult;
use lichess_puzzles_core::service::{
    EventAction, EventLevel, ExportSource, Service, ServiceConfig, ServiceError, SessionCommand, SessionResult,
};
use lichess_puzzles_core::session::{MoveOutcome, SessionState};
use support::Server;

const SAMPLE: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures/sample_puzzles.csv");

fn open(dir: &Path, url: &str) -> Service {
    Service::open(ServiceConfig {
        data_dir: dir.to_path_buf(),
        source_url: url.to_owned(),
        default_player: "Tester".into(),
    })
    .unwrap()
}

/// Waits for the running operation to finish and returns the events it produced.
fn settle(service: &Service) -> Vec<(EventLevel, String, EventAction)> {
    let deadline = Instant::now() + Duration::from_secs(30);
    while service.status().operation.is_some() {
        assert!(Instant::now() < deadline, "operation did not finish");
        std::thread::sleep(Duration::from_millis(20));
    }
    service.poll_events(100).into_iter().map(|e| (e.level, e.message, e.action)).collect()
}

fn sample_zst() -> Vec<u8> {
    zstd::encode_all(std::fs::read(SAMPLE).unwrap().as_slice(), 3).unwrap()
}

fn installed(dir: &Path) -> Service {
    let service = open(dir, "http://127.0.0.1:9/unused");
    service.start_build(Some(SAMPLE.into()), true).unwrap();
    assert!(settle(&service)[0].1.starts_with("Installed 13 puzzles"));
    service
}

/// Plays the rest of the open puzzle's solution, from the opponent's first move if needed.
fn solve(service: &Service) -> SessionResult {
    let mut view = service.session(SessionCommand::Get).unwrap().view;
    if view.state == SessionState::OpponentToMove {
        view = service.session(SessionCommand::PlayOpponent).unwrap().view;
    }
    let line = view.puzzle.moves.clone();
    let mut ply = view.played;
    loop {
        let result = service.session(SessionCommand::MakeMove(line[ply].clone())).unwrap();
        ply += 2;
        match result.outcome.unwrap() {
            MoveOutcome::Solved => return result,
            MoveOutcome::Correct => {
                service.session(SessionCommand::PlayOpponent).unwrap();
            }
            other => panic!("{other:?}"),
        }
    }
}

#[test]
fn downloads_installs_updates_and_switches() {
    let server = Server::start(sample_zst(), "\"v1\"");
    let dir = tempfile::tempdir().unwrap();
    let service = open(dir.path(), &server.url);

    let status = service.status();
    assert!(status.store.is_none());
    assert_eq!(status.store_problem.as_deref(), Some("No puzzles are installed."));
    assert_eq!(service.next_puzzle(None).unwrap_err(), ServiceError::NoStore);

    service.start_download(true).unwrap();
    assert_eq!(service.start_download(true).unwrap_err(), ServiceError::Busy);
    let events = settle(&service);
    assert_eq!(events.len(), 1, "{events:?}");
    assert_eq!(events[0].0, EventLevel::Success);
    assert!(events[0].1.starts_with("Installed 13 puzzles"), "{events:?}");

    let status = service.status();
    let store = status.store.expect("store installed");
    assert_eq!((store.puzzle_count, store.rating_min, store.rating_max), (13, 998, 2873));
    assert_eq!(store.source.version.as_ref().and_then(|v| v.etag.as_deref()), Some("\"v1\""));
    assert!(status.download.is_some());
    assert!(!status.update_ready);

    service.check_for_update().unwrap();
    assert_eq!(settle(&service)[0].1, "The installed puzzles are up to date.");
    service.start_download(true).unwrap();
    assert_eq!(settle(&service)[0].1, "The installed puzzles are up to date.");

    // A new version on the server: check, download and build it beside the open store.
    server.state.lock().unwrap().etag = "\"v2\"".into();
    service.check_for_update().unwrap();
    assert_eq!(settle(&service)[0].1, "A puzzle update is available.");
    assert!(service.status().update_available.is_some());
    let view = service.next_puzzle(None).unwrap().view;
    service.start_download(false).unwrap();
    let events = settle(&service);
    assert_eq!(events[0].2, EventAction::SwitchStore, "{events:?}");
    let status = service.status();
    assert!(status.update_ready && status.update_available.is_none());
    assert!(status.download.is_none(), "the download is deleted when not kept");

    service.switch_store().unwrap();
    let status = service.status();
    assert!(!status.update_ready);
    assert_eq!(status.store.unwrap().source.version.unwrap().etag.as_deref(), Some("\"v2\""));
    // The session survives the switch.
    let same = service.session(SessionCommand::Get).unwrap().view;
    assert_eq!(same.puzzle.id, view.puzzle.id);
}

#[test]
fn imports_filters_and_plays() {
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());

    let catalogue = service.catalogue().unwrap();
    assert_eq!(catalogue.themes.len(), 19);
    assert_eq!(catalogue.themes[0].group, "Goal");
    assert!(catalogue.groups.starts_with(&["Goal".to_owned(), "Length".to_owned(), "Phase".to_owned()]));
    let mate = catalogue.themes.iter().find(|t| t.key == "mate").unwrap();
    assert_eq!((mate.name.as_str(), mate.count), ("Checkmate", 2));
    let french = catalogue.variations.iter().find(|v| v.tag == "French_Defense_Exchange_Variation").unwrap();
    assert_eq!(french.name, "Exchange Variation");
    assert_eq!(catalogue.families.iter().find(|f| f.id == french.family).unwrap().name, "French Defense");

    let counts = service.set_filter(Filter { themes_any: vec!["mate".into()], ..Default::default() }, false).unwrap();
    assert_eq!(counts.total, 2);
    assert!(service.set_filter(Filter { themes_any: vec!["nothing".into()], ..Default::default() }, false).is_err());

    // Play whichever mate puzzle comes up, using its own solution.
    let view = service.next_puzzle(None).unwrap().view;
    assert!(["0000H", "00sHx"].contains(&view.puzzle.id.as_str()));
    assert_eq!(view.state, SessionState::OpponentToMove);
    let solved = solve(&service).view;
    assert_eq!(solved.state, SessionState::Solved);
    assert_eq!(solved.moves.len(), view.puzzle.moves.len());

    let view = service.session(SessionCommand::Retry).unwrap().view;
    assert!(view.replay);
    let view = service.load_puzzle("https://lichess.org/training/00sJ9", false).unwrap().view;
    assert_eq!(view.puzzle.opening_variation.as_deref(), Some("French_Defense_Exchange_Variation"));
    assert_eq!(service.load_puzzle("zzzzz", false).unwrap_err(), ServiceError::NotFound("zzzzz".into()));

    service.set_filter(Filter { rating_min: Some(4000), ..Default::default() }, false).unwrap();
    assert_eq!(service.next_puzzle(None).unwrap_err(), ServiceError::NoMatches);
}

#[test]
fn pause_and_cancel_downloads() {
    let server = Server::start((0..4_000_000u32).map(|i| i as u8).collect(), "\"big\"");
    // Each response stops early so the download stays in progress long enough to pause.
    server.state.lock().unwrap().cut_after = vec![200_000; 50];
    let dir = tempfile::tempdir().unwrap();
    let service = open(dir.path(), &server.url);
    service.start_download(true).unwrap();
    std::thread::sleep(Duration::from_millis(300));
    service.pause_download();
    let events = settle(&service);
    assert!(events[0].1.starts_with("Download paused"), "{events:?}");
    let (partial, total) = service.status().partial_download.expect("partial kept");
    assert!(partial > 0);
    assert_eq!(total, Some(4_000_000));

    service.start_download(true).unwrap();
    std::thread::sleep(Duration::from_millis(300));
    service.cancel_operation();
    let events = settle(&service);
    assert_eq!(events[0].1, "Download cancelled.", "{events:?}");
    assert!(service.status().partial_download.is_none());
}

#[test]
fn ratings_history_and_played_puzzles() {
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    let info = service.players(0).unwrap();
    assert_eq!(info.players.len(), 1);
    assert_eq!(info.players[0].name, "Tester");
    assert!(info.rated);

    // A clean solve is a rated win, recorded once.
    let opened = service.load_puzzle("0000H", false).unwrap();
    assert!(opened.rated && opened.rating_change.is_none());
    let solved = solve(&service);
    let (before, after) = solved.rating_change.expect("settled");
    assert!(before == 1500.0 && after > before);
    let history = service.history(10).unwrap();
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].result, PuzzleResult::Solved);

    // Played again: not rated.
    assert!(!service.load_puzzle("0000H", false).unwrap().rated);

    // A wrong move settles a loss at once; finishing afterwards does not change it.
    assert!(service.load_puzzle("00sHx", false).unwrap().rated);
    service.session(SessionCommand::PlayOpponent).unwrap();
    let wrong = service.session(SessionCommand::MakeMove("h2h3".into())).unwrap();
    assert_eq!(wrong.outcome, Some(MoveOutcome::Wrong));
    let (lost_from, lost_to) = wrong.rating_change.expect("loss settled");
    assert!(lost_to < lost_from);
    let finished = solve(&service);
    assert_eq!(finished.rating_change, Some((lost_from, lost_to)));
    let history = service.history(10).unwrap();
    assert_eq!(history[0].puzzle_id, "00sHx");
    assert_eq!(history[0].result, PuzzleResult::SolvedWithHelp);
    assert_eq!(history[0].mistakes, 1);

    // Leaving a puzzle after playing a move in it records a failure.
    let line = service.load_puzzle("00sJ9", false).unwrap().view.puzzle.moves;
    service.session(SessionCommand::PlayOpponent).unwrap();
    service.session(SessionCommand::MakeMove(line[1].clone())).unwrap();
    service.load_puzzle("000Pw", false).unwrap();
    let history = service.history(10).unwrap();
    assert_eq!((history[0].puzzle_id.as_str(), history[0].result), ("00sJ9", PuzzleResult::Failed));
    // Opening a puzzle without playing in it records nothing.
    service.load_puzzle("0008Q", false).unwrap();
    assert_eq!(service.history(10).unwrap().len(), 3);

    // Replays are neither rated nor recorded.
    service.load_puzzle("000Pw", true).unwrap();
    let replay = solve(&service);
    assert!(!replay.rated && replay.rating_change.is_none());
    assert_eq!(service.history(10).unwrap().len(), 3);

    // Skipping played puzzles: 0000H, 00sHx and 00sJ9 are played.
    assert_eq!(service.set_filter(Filter::default(), true).unwrap().total, 10);
    let mates = service.set_filter(Filter { themes_any: vec!["mate".into()], ..Default::default() }, true).unwrap();
    assert_eq!(mates.total, 0);
    assert_eq!(service.next_puzzle(None).unwrap_err(), ServiceError::NoMatches);

    // Another player has their own rating and history.
    let guest = service.add_player("Guest").unwrap();
    service.set_current_player(guest.id).unwrap();
    assert_eq!(service.set_filter(Filter::default(), true).unwrap().total, 13);
    assert!(service.history(10).unwrap().is_empty());
    let near = service.next_puzzle(Some(-500)).unwrap();
    assert!((850..=1150).contains(&near.view.puzzle.rating), "respects the explicit default ±150 range");
    let info = service.players(0).unwrap();
    assert_eq!(info.current, guest.id);
    assert_eq!(info.stats.played, 0);

    service.set_rated(false);
    assert!(!service.load_puzzle("000Vc", false).unwrap().rated);
}

#[test]
fn switching_players_restarts_the_puzzle_and_keeps_attempts_with_their_owner() {
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    let original = service.players(0).unwrap().current;
    let guest = service.add_player("Guest").unwrap();
    let line = service.load_puzzle("00sJ9", false).unwrap().view.puzzle.moves;
    service.session(SessionCommand::PlayOpponent).unwrap();
    service.session(SessionCommand::MakeMove(line[1].clone())).unwrap();

    // Invalid switches preserve the active attempt.
    assert!(service.set_current_player(-1).is_err());
    assert_eq!(service.session(SessionCommand::Get).unwrap().view.state, SessionState::ReplyPending);
    service.set_current_player(guest.id).unwrap();
    let restarted = service.session(SessionCommand::Get).unwrap();
    assert_eq!(restarted.view.state, SessionState::OpponentToMove);
    assert!(restarted.rated);
    assert!(service.history(10).unwrap().is_empty());
    solve(&service);
    assert_eq!(service.history(10).unwrap()[0].result, PuzzleResult::Solved);
    assert_eq!(service.players(0).unwrap().players.iter().find(|p| p.id == guest.id).unwrap().games, 1);

    service.set_current_player(original).unwrap();
    let history = service.history(10).unwrap();
    assert_eq!(history.len(), 1);
    assert_eq!(history[0].result, PuzzleResult::Failed);
    let replay = service.session(SessionCommand::Get).unwrap();
    assert!(replay.view.replay && !replay.rated);
    solve(&service);
    assert_eq!(service.history(10).unwrap(), history, "replays must not rewrite or duplicate history");
}

#[test]
fn deleting_the_active_player_transfers_the_board_to_the_remaining_player() {
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    let original = service.players(0).unwrap().current;
    let guest = service.add_player("Guest").unwrap();
    service.load_puzzle("0000H", false).unwrap();
    service.session(SessionCommand::PlayOpponent).unwrap();
    service.delete_player(original).unwrap();
    assert_eq!(service.players(0).unwrap().current, guest.id);
    assert_eq!(service.session(SessionCommand::Get).unwrap().view.state, SessionState::OpponentToMove);
    solve(&service);
    assert_eq!(service.history(10).unwrap()[0].player_id, guest.id);
    assert!(service.poll_events(100).is_empty(), "no save should target the deleted player");
}

#[test]
fn export_replaces_the_destination_and_preserves_it_on_cancel_or_failure() {
    use lichess_puzzles_core::export::{self, ExportError};
    use lichess_puzzles_core::store::Store;
    use std::sync::atomic::AtomicBool;

    let dir = tempfile::tempdir().unwrap();
    let _service = installed(dir.path());
    let store = Store::open(dir.path().join("puzzles.store")).unwrap();
    let path = dir.path().join("export.csv");
    let sidecar = dir.path().join("export.csv.partial");
    std::fs::write(&path, "existing export").unwrap();
    std::fs::write(&sidecar, "unrelated file").unwrap();
    let cancelled = AtomicBool::new(true);
    assert_eq!(
        export::export(&store, &[0], ExportFormat::Csv, false, &path, &mut |_| {}, &cancelled),
        Err(ExportError::Cancelled)
    );
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "existing export");
    let running = AtomicBool::new(false);
    assert_eq!(export::export(&store, &[0], ExportFormat::Csv, false, &path, &mut |_| {}, &running).unwrap(), 1);
    assert!(std::fs::read_to_string(&path).unwrap().starts_with(export::CSV_HEADER));
    assert_eq!(std::fs::read_to_string(&sidecar).unwrap(), "unrelated file");
    let folder = dir.path().join("folder");
    std::fs::create_dir(&folder).unwrap();
    assert!(export::export(&store, &[0], ExportFormat::Csv, false, &folder, &mut |_| {}, &running).is_err());
    assert_eq!(
        std::fs::read_dir(dir.path())
            .unwrap()
            .filter_map(Result::ok)
            .filter(|e| e.file_name().to_string_lossy().starts_with(".tmp"))
            .count(),
        0
    );
}

#[test]
fn analysis_reports_finished_positions_without_starting_a_search() {
    use lichess_puzzles_core::service::AnalysisOutcome;

    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    // A terminal request also supersedes any analysis still running for an earlier position.
    service.start_analysis(&service.load_puzzle("0000H", true).unwrap().view.fen, Duration::from_secs(5)).unwrap();
    for (fen, expected) in [
        ("7k/6Q1/6K1/8/8/8/8/8 b - - 0 1", AnalysisOutcome::WhiteWins),
        ("8/8/8/8/8/6k1/6q1/7K w - - 0 1", AnalysisOutcome::BlackWins),
        ("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", AnalysisOutcome::Draw),
    ] {
        service.start_analysis(fen, Duration::from_secs(1)).unwrap();
        let result = service.analysis();
        assert_eq!(result.fen, fen);
        assert!(!result.running);
        assert!(result.info.is_none());
        assert_eq!(result.terminal_outcome, Some(expected));
    }
    service.start_analysis(&service.session(SessionCommand::Get).unwrap().view.fen, Duration::from_millis(50)).unwrap();
    assert!(service.analysis().terminal_outcome.is_none(), "a new search clears the terminal outcome");
}

#[test]
fn exports_and_analysis() {
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    service.set_filter(Filter { themes_any: vec!["mate".into()], ..Default::default() }, false).unwrap();
    let pgn = dir.path().join("mates.pgn");
    assert_eq!(service.start_export(ExportSource::Matching, ExportFormat::Pgn, false, pgn.clone()).unwrap(), 2);
    let events = settle(&service);
    assert_eq!(events[0].2, EventAction::ShowFile, "{events:?}");
    assert_eq!(std::fs::read_to_string(&pgn).unwrap().matches("[Event ").count(), 2);

    service.load_puzzle("0000H", false).unwrap();
    solve(&service);
    let csv = dir.path().join("played.csv");
    assert_eq!(service.start_export(ExportSource::Played, ExportFormat::Csv, false, csv.clone()).unwrap(), 1);
    settle(&service);
    let text = std::fs::read_to_string(&csv).unwrap();
    assert_eq!(text.lines().count(), 2);
    assert!(text.lines().nth(1).unwrap().starts_with("0000H,"));
    assert!(service.puzzle_pgn(true).unwrap().contains("[PuzzleId \"0000H\"]"));

    // Mate in one for Black after the opponent's move in puzzle 0000H.
    let fen = "r4rk1/pp3ppp/8/2bqp2Q/3N4/P2Bn2P/1PPN1P2/R1B2RK1 b - - 0 15";
    service.start_analysis(fen, Duration::from_secs(5)).unwrap();
    let deadline = Instant::now() + Duration::from_secs(10);
    while service.analysis().running {
        assert!(Instant::now() < deadline);
        std::thread::sleep(Duration::from_millis(20));
    }
    let info = service.analysis().info.expect("result");
    assert_eq!(info.score, Score::Mate(-1), "{info:?}");
    assert_eq!(info.pv_san[0], "Qg2#");
    assert!(service.start_analysis("not a fen", Duration::from_secs(1)).is_err());
}

#[test]
fn screening_is_personal_non_destructive_and_does_not_start_attempts() {
    use lichess_puzzles_core::service::SelectionOptions;
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    let original_csv = std::fs::read(SAMPLE).unwrap();
    let original_store = std::fs::read(dir.path().join("puzzles.store")).unwrap();
    let before = service.players(0).unwrap();
    service.load_puzzle("0000D", false).unwrap();
    service.prepare_next(None, SelectionOptions::new(150, 2)).unwrap();
    assert_eq!(service.session(SessionCommand::Get).unwrap().view.puzzle.id, "0000D");
    assert_eq!(service.players(0).unwrap(), before, "prefetch does not rate or record anything");
    assert!(service.history(100).unwrap().is_empty());

    // A prepared non-mate must not leak through after changing to a mate-only filter.
    service.set_filter(Filter { themes_any: vec!["mate".into()], ..Default::default() }, false).unwrap();
    assert!(matches!(service.next_selected(None, SelectionOptions::new(150, 2)), Err(ServiceError::Selection(_))));
    assert_eq!(
        service.session(SessionCommand::Get).unwrap().view.puzzle.id,
        "0000D",
        "failed selection preserves the board"
    );
    assert_eq!(service.players(0).unwrap(), before);
    // Exclude mate in one, but keep a genuine mate in two.
    assert_eq!(service.next_selected(None, SelectionOptions::new(150, 1)).unwrap().view.puzzle.id, "00sHx");
    let result = solve(&service);
    assert!(result.rating_change.is_some());
    assert_eq!(service.players(0).unwrap().players[0].games, 1);
    // Screening off restores access to the full source, including excluded puzzles.
    service.set_filter(Filter { themes_any: vec!["mateIn1".into()], ..Default::default() }, false).unwrap();
    assert_eq!(service.next_selected(None, SelectionOptions::default()).unwrap().view.puzzle.id, "0000H");
    assert_eq!(std::fs::read(SAMPLE).unwrap(), original_csv);
    assert_eq!(std::fs::read(dir.path().join("puzzles.store")).unwrap(), original_store);
    assert!(dir.path().join("screening.sqlite").exists());
}

#[test]
fn selection_stays_in_the_requested_rating_range() {
    use lichess_puzzles_core::service::SelectionOptions;
    let dir = tempfile::tempdir().unwrap();
    let service = installed(dir.path());
    service.set_filter(Filter { rating_min: Some(2200), ..Default::default() }, true).unwrap();
    let error = service.next_selected(Some(0), SelectionOptions::new(75, 0)).unwrap_err();
    assert!(matches!(error, ServiceError::Selection(_)));
    assert!(error.to_string().contains("±75"));
    assert!(service.history(100).unwrap().is_empty());
    // Explicitly broaden via the caller's offset and window; never as an automatic fallback.
    let result = service.next_selected(Some(600), SelectionOptions::new(150, 2)).unwrap();
    assert_eq!(result.view.puzzle.rating, 2235);
    assert!(service.history(100).unwrap().is_empty(), "opening a puzzle is not a solve");
}

#[test]
fn screening_proves_mates_even_without_mate_tags() {
    use lichess_puzzles_core::service::SelectionOptions;
    let dir = tempfile::tempdir().unwrap();
    let csv = std::fs::read_to_string(SAMPLE).unwrap();
    let row = csv
        .lines()
        .find(|l| l.starts_with("0000H,"))
        .unwrap()
        .replace("mate mateIn1 middlegame oneMove", "advantage middlegame");
    let input = dir.path().join("untagged.csv");
    std::fs::write(&input, format!("{}\n{row}\n", csv.lines().next().unwrap())).unwrap();
    let service = open(dir.path(), "http://127.0.0.1:9/unused");
    service.start_build(Some(input.clone()), true).unwrap();
    settle(&service);
    assert!(matches!(service.next_selected(None, SelectionOptions::new(150, 1)), Err(ServiceError::Selection(_))));
    assert_eq!(service.next_puzzle(None).unwrap().view.puzzle.id, "0000H");
    assert_eq!(std::fs::read_to_string(input).unwrap().lines().nth(1).unwrap(), row);
}
