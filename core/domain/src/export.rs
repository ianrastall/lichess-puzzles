//! Writing puzzles as PGN, CSV or EPD.
//!
//! PGN: one game per puzzle with `SetUp`/`FEN` tags and the solution as movetext. By default the
//! game starts from the database FEN, so the opponent's move comes first, followed by a comment
//! marking where the puzzle begins; with `player_start` it starts from the position the solver
//! faces. Puzzle data is kept in `Puzzle*` tags and the source game in `Source`.
//! CSV: the Lichess column layout. EPD: the solver's position with `bm` (the first solution move)
//! and `id`.

use std::io::{BufWriter, Write};
use std::path::Path;
use std::sync::atomic::{AtomicBool, Ordering};

use cozy_chess::Color;
use cozy_chess::util::display_san_move;

use crate::chess::{self, LineError};
use crate::store::{Puzzle, Store};
use crate::{Side, catalogue};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExportFormat {
    Pgn,
    Csv,
    Epd,
}

impl ExportFormat {
    pub fn extension(self) -> &'static str {
        match self {
            ExportFormat::Pgn => "pgn",
            ExportFormat::Csv => "csv",
            ExportFormat::Epd => "epd",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ExportError {
    Io(String),
    Cancelled,
}

impl std::fmt::Display for ExportError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            ExportError::Io(message) => write!(f, "file error: {message}"),
            ExportError::Cancelled => f.write_str("cancelled"),
        }
    }
}

impl std::error::Error for ExportError {}

pub const CSV_HEADER: &str =
    "PuzzleId,FEN,Moves,Rating,RatingDeviation,Popularity,NbPlays,Themes,GameUrl,OpeningTags,DailyDate";

/// `YYYY.MM.DD` (UTC) from milliseconds since the Unix epoch.
pub fn pgn_date(unix_ms: i64) -> String {
    let days = unix_ms.div_euclid(86_400_000);
    // Civil date from days since 1970-01-01 (Howard Hinnant's algorithm).
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = if mp < 10 { mp + 3 } else { mp - 9 };
    let year = yoe + era * 400 + i64::from(month <= 2);
    format!("{year:04}.{month:02}.{day:02}")
}

fn tag(name: &str, value: &str) -> String {
    format!("[{name} \"{}\"]\n", value.replace('\\', "\\\\").replace('"', "\\\""))
}

pub fn puzzle_pgn(p: &Puzzle, player_start: bool) -> Result<String, LineError> {
    let start = chess::parse_fen(&p.fen)?;
    let uci: Vec<&str> = p.moves.iter().map(String::as_str).collect();
    let moves = chess::replay(&start, &uci)?;
    let player = if p.solver == Side::White { "White" } else { "Black" };

    let mut board = start.clone();
    let mut plies = Vec::with_capacity(moves.len());
    for &mv in &moves {
        let color = board.side_to_move();
        let number = board.fullmove_number();
        let san = display_san_move(&board, mv).to_string();
        board.play(mv);
        plies.push((color, number, san));
    }
    let mated = board.status() == cozy_chess::GameStatus::Won;
    let result = match (mated, plies.last().map(|p| p.0)) {
        (true, Some(Color::White)) => "1-0",
        (true, Some(Color::Black)) => "0-1",
        _ => "*",
    };
    let mut after_first = start.clone();
    after_first.play(moves[0]);

    let mut pgn = String::new();
    pgn += &tag("Event", &format!("Lichess Puzzle {}", p.id));
    pgn += &tag("Site", &format!("https://lichess.org/training/{}", p.id));
    pgn += &tag("Date", &p.daily_date.map_or_else(|| "????.??.??".to_owned(), pgn_date));
    pgn += &tag("Round", "-");
    pgn += &tag("White", if p.solver == Side::White { "Player" } else { "Opponent" });
    pgn += &tag("Black", if p.solver == Side::Black { "Player" } else { "Opponent" });
    pgn += &tag("Result", result);
    pgn += &tag("SetUp", "1");
    pgn += &tag("FEN", &if player_start { after_first.to_string() } else { p.fen.clone() });
    pgn += &tag("PuzzleId", &p.id);
    pgn += &tag("PuzzleRating", &p.rating.to_string());
    pgn += &tag("PuzzleRatingDeviation", &p.deviation.to_string());
    pgn += &tag("PuzzlePopularity", &p.popularity.to_string());
    pgn += &tag("PuzzlePlays", &p.plays.to_string());
    pgn += &tag("PuzzleThemes", &p.themes.join(" "));
    if let Some(family) = &p.opening_family {
        let opening = match &p.opening_variation {
            Some(variation) => {
                format!("{}: {}", catalogue::family_name(family), catalogue::variation_name(variation, family))
            }
            None => catalogue::family_name(family),
        };
        pgn += &tag("Opening", &opening);
    }
    if !p.game_url.is_empty() {
        pgn += &tag("Source", &p.game_url);
    }
    pgn += &tag("Annotator", "Lichess Puzzles");
    pgn.push('\n');

    let mut tokens: Vec<String> = Vec::new();
    let mut need_number = true;
    let comment = |tokens: &mut Vec<String>, text: &str| {
        tokens.push(format!("{{ {text} }}"));
    };
    if player_start {
        comment(&mut tokens, &format!("{player} to move."));
    }
    for (i, (color, number, san)) in plies.iter().enumerate() {
        if player_start && i == 0 {
            continue;
        }
        if *color == Color::White {
            tokens.push(format!("{number}."));
        } else if need_number {
            tokens.push(format!("{number}..."));
        }
        need_number = false;
        tokens.push(san.clone());
        if i == 0 {
            comment(&mut tokens, &format!("Puzzle starts here: {player} to move."));
            need_number = true;
        }
        if player_start && i == 1 {
            need_number = false;
        }
    }
    tokens.push(result.to_owned());

    // Wrap movetext at 80 columns, keeping comments whole where they fit.
    let mut column = 0;
    for token in tokens.iter().flat_map(|t| {
        if t.starts_with('{') { t.split(' ').map(str::to_owned).collect::<Vec<_>>() } else { vec![t.clone()] }
    }) {
        if column > 0 && column + 1 + token.len() > 80 {
            pgn.push('\n');
            column = 0;
        } else if column > 0 {
            pgn.push(' ');
            column += 1;
        }
        pgn += &token;
        column += token.len();
    }
    pgn.push('\n');
    Ok(pgn)
}

pub fn puzzle_csv(p: &Puzzle) -> String {
    let openings = [p.opening_family.as_deref(), p.opening_variation.as_deref()]
        .into_iter()
        .flatten()
        .collect::<Vec<_>>()
        .join(" ");
    let daily = p.daily_date.map(|d| d.to_string()).unwrap_or_default();
    [
        p.id.clone(),
        p.fen.clone(),
        p.moves.join(" "),
        p.rating.to_string(),
        p.deviation.to_string(),
        p.popularity.to_string(),
        p.plays.to_string(),
        p.themes.join(" "),
        p.game_url.clone(),
        openings,
        daily,
    ]
    .join(",")
}

/// The position the solver faces, with the first solution move as `bm`.
pub fn puzzle_epd(p: &Puzzle) -> Result<String, LineError> {
    let start = chess::parse_fen(&p.fen)?;
    let uci: Vec<&str> = p.moves.iter().map(String::as_str).collect();
    let moves = chess::replay(&start, &uci)?;
    let mut board = start;
    board.play(moves[0]);
    let best = display_san_move(&board, moves[1]).to_string();
    let fen = board.to_string();
    let position: Vec<&str> = fen.split(' ').take(4).collect();
    Ok(format!("{} bm {best}; id \"{}\";", position.join(" "), p.id))
}

/// Writes the puzzles at `indices` to `path` (through a temporary file). `progress` receives the
/// number written so far. Puzzles that cannot be replayed are skipped. Returns the number written.
pub fn export(
    store: &Store,
    indices: &[u32],
    format: ExportFormat,
    player_start: bool,
    path: &Path,
    progress: &mut dyn FnMut(u64),
    cancel: &AtomicBool,
) -> Result<u64, ExportError> {
    let io = |e: std::io::Error| ExportError::Io(e.to_string());
    let parent = path.parent().filter(|p| !p.as_os_str().is_empty()).unwrap_or_else(|| Path::new("."));
    let mut temp = tempfile::NamedTempFile::new_in(parent).map_err(io)?;
    let written = {
        let mut out = BufWriter::with_capacity(1 << 20, temp.as_file_mut());
        if format == ExportFormat::Csv {
            writeln!(out, "{CSV_HEADER}").map_err(io)?;
        }
        let mut written = 0u64;
        for (n, &index) in indices.iter().enumerate() {
            if n % 1000 == 0 {
                if cancel.load(Ordering::Relaxed) {
                    return Err(ExportError::Cancelled);
                }
                progress(n as u64);
            }
            let Ok(puzzle) = store.puzzle(index as usize) else { continue };
            let text = match format {
                ExportFormat::Pgn => puzzle_pgn(&puzzle, player_start).ok().map(|pgn| pgn + "\n"),
                ExportFormat::Csv => Some(puzzle_csv(&puzzle) + "\n"),
                ExportFormat::Epd => puzzle_epd(&puzzle).ok().map(|epd| epd + "\n"),
            };
            if let Some(text) = text {
                out.write_all(text.as_bytes()).map_err(io)?;
                written += 1;
            }
        }
        out.flush().map_err(io)?;
        progress(indices.len() as u64);
        written
    };
    if cancel.load(Ordering::Relaxed) {
        return Err(ExportError::Cancelled);
    }
    temp.persist(path).map_err(|e| io(e.error))?;
    Ok(written)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample() -> Puzzle {
        Puzzle {
            index: 0,
            id: "00008".into(),
            fen: "r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24".into(),
            moves: "f2g3 e6e7 b2b1 b3c1 b1c1 h6c1".split(' ').map(str::to_owned).collect(),
            rating: 1811,
            deviation: 76,
            popularity: 95,
            plays: 10282,
            themes: vec!["crushing".into(), "hangingPiece".into()],
            opening_family: Some("French_Defense".into()),
            opening_variation: Some("French_Defense_Exchange_Variation".into()),
            game_url: "https://lichess.org/787zsVup/black#48".into(),
            daily_date: Some(1_608_386_149_791),
            solver: Side::White,
        }
    }

    #[test]
    fn pgn_from_the_database_position() {
        let pgn = puzzle_pgn(&sample(), false).unwrap();
        assert!(pgn.contains("[Event \"Lichess Puzzle 00008\"]\n"));
        assert!(pgn.contains("[Date \"2020.12.19\"]\n"));
        assert!(pgn.contains("[White \"Player\"]\n[Black \"Opponent\"]\n[Result \"*\"]\n"));
        assert!(pgn.contains("[FEN \"r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24\"]\n"));
        assert!(pgn.contains("[Opening \"French Defense: Exchange Variation\"]\n"));
        assert!(
            pgn.ends_with(
                "24... Bxg3 { Puzzle starts here: White to move. } 25. Rxe7 Qb1+ 26. Nc1 Qxc1+\n27. Qxc1 *\n"
            ),
            "{pgn}"
        );
    }

    #[test]
    fn pgn_from_the_solvers_position_and_other_formats() {
        let pgn = puzzle_pgn(&sample(), true).unwrap();
        assert!(pgn.contains("[FEN \"r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2b1/PqP3PP/7K w - - 0 25\"]\n"), "{pgn}");
        assert!(pgn.contains("{ White to move. } 25. Rxe7 Qb1+ 26. Nc1 Qxc1+ 27. Qxc1 *\n"), "{pgn}");
        assert_eq!(
            puzzle_csv(&sample()),
            "00008,r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2R1/PqP2bPP/7K b - - 0 24,f2g3 e6e7 b2b1 b3c1 b1c1 h6c1,1811,76,95,10282,crushing hangingPiece,https://lichess.org/787zsVup/black#48,French_Defense French_Defense_Exchange_Variation,1608386149791"
        );
        assert_eq!(
            puzzle_epd(&sample()).unwrap(),
            "r6k/pp2r2p/4Rp1Q/3p4/8/1N1P2b1/PqP3PP/7K w - - bm Rxe7; id \"00008\";"
        );
        assert_eq!(pgn_date(0), "1970.01.01");
    }
}
