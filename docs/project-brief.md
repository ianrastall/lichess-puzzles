# lichess-puzzles: project brief

Working brief · 24 October 2026

## Purpose

Download, filter and play the Lichess puzzle database offline on Windows.

Priority: unranked group `chess-puzzles` in the [portfolio organization](../../_planning/portfolio-organization_draft.md); selected by Ian as the next Jupiter project on 24 October 2026.

## Decisions

Ian's answers, 24 October 2026:

- **Track:** Windows desktop on the [canonical stack](../../_standards/stack-reference_winui3-rust-core.md): WinUI 3/C# shell, Rust domain core, C ABI with protobuf messages. [Chess.com Crawler](../../chesscom-crawler/readme.md) is the working template for the toolchain, boundary and installer.
- **Repository:** a fresh repository with no copied history. [Lichess Puzzle Desktop](<D:/dev/proj/lichess-puzzle-desktop>) is a reference for features and behaviour only, and stays where it is.
- **Interface:** a Jupiter workbench. The arrangement follows the previous app's own layout under Jupiter conventions; it is not modelled on the Lichess website.

`lichess-puzzles` is a working folder name; the product name is not yet fixed.

## Source data

Checked 24 October 2026:

| Item | Value |
| --- | --- |
| Download | `https://database.lichess.org/lichess_db_puzzle.csv.zst` |
| Compressed size | 307,234,795 bytes; `Last-Modified` 2 October 2026; `ETag` present; byte ranges supported |
| Decompressed CSV | about 1.1 GB, 6,157,341 puzzles |
| Columns | `PuzzleId, FEN, Moves, Rating, RatingDeviation, Popularity, NbPlays, Themes, GameUrl, OpeningTags, DailyDate` |
| Vocabulary | about 72 themes and 1,590 opening tags |
| Licence | CC0 |

`Moves` begins with the opponent's move; the player's solution follows. Theme names and descriptions are recorded in [`D:/GitHub/lichess-puzzles/themes.jsonl`](<D:/GitHub/lichess-puzzles/themes.jsonl>). Local copies of the decompressed CSV exist in that folder (April 2026) and in the predecessor checkout (October 2026).

## First workflows

1. **Download and update.** Without a database, the app offers to download the current file. Progress shows bytes, rate and time remaining; a download can be paused, resumed with a range request after an interruption, or cancelled. *Check for update* sends a conditional request using the stored `ETag`/`Last-Modified`. An existing `.csv` or `.csv.zst` can be imported instead.
2. **Build the puzzle store.** The core decompresses the stream and parses it straight into a compact local store: fixed-size records (rating, deviation, popularity, plays, theme bitmask, opening id, move count, side to move) beside the FEN and move text. The CSV is not kept unless chosen. Rebuilding after an update keeps history and ratings, which are keyed by `PuzzleId`.
3. **Filter.** Rating range, maximum rating deviation, minimum popularity and plays, themes (include any, include all, exclude), opening family and variation, side to move, solution length, and skip played puzzles. The matching count updates as filters change. Filters can be saved as named presets.
4. **Play.** Board with click and drag moves, legal-move markers, promotion picker and orientation from the solver's side. Hint, show solution, retry and next. Any checkmating move is accepted. Move list with keyboard navigation; after the puzzle ends the solution can be stepped through.
5. **Ratings and history.** Players with Glicko-2 puzzle ratings, rated/unrated play, puzzles matched to the player's rating with a difficulty offset, history with clean-solve count and streak.
6. **Export.** Current puzzle as PGN or FEN; every filtered puzzle as PGN, CSV or EPD; played puzzles as PGN.

## Predecessor inventory

| Lichess Puzzle Desktop feature | Plan |
| --- | --- |
| Board input, legal markers, promotion, Merida pieces | First version |
| SAN move list, arrow-key navigation | First version |
| Hint, solution, retry, next; alternative mates accepted | First version |
| Rating, popularity, plays, theme and opening filters; skip played | First version, extended as above |
| Jump to puzzle by id | First version |
| Players, Glicko-2, rating-matched puzzles | First version |
| History, clean-solve count, streak | First version |
| PGN, CSV, EPD and FEN export | First version |
| Memory-mapped CSV index (about 33 bytes per puzzle) | Replaced by the built store |
| Manual download and `zstd -d` | Replaced by in-app download and import |
| Built-in alpha-beta evaluation and principal variation | Open: omit, rebuild, or drive an external UCI engine such as Stockfish |
| Inno Setup per-user installer, tag-driven releases | Follow the Chess.com Crawler installer pattern |

Its saved data (`%LOCALAPPDATA%\LichessPuzzleDesktop`) holds a handful of history entries and one player, so no migration is planned.

## Responsibilities

- **Rust core:** chess rules, SAN/UCI/FEN, download with resume and conditional checks, zstd decompression, store build and query, puzzle session state, Glicko-2, history, export.
- **Rust CLI:** `download`, `import`, `status`, `count`/`export` with filter arguments. It exercises the core without the GUI and supports scripted exports.
- **WinUI 3 shell:** menu bar (substantial app), filter pane, board control, puzzle details, move list, player and history panels, download progress, settings including the shared Appearance > Accent color control.

Data lives in `%LOCALAPPDATA%\Jupiter\LichessPuzzles` by default. The store folder can be moved because it is several hundred megabytes.

## Open questions

- Product name.
- Chess-rules implementation: write our own (as the predecessor did) or use a crate; licences differ (`shakmaty` is GPL-3.0, `cozy-chess` is MIT). The Merida pieces are GPLv2+ either way.
- Evaluation: none, a built-in engine, or an external UCI engine.
- Whether a GitHub repository and public releases are wanted, and under which name.

## Staged progression

1. Confirm this brief and the open questions.
2. Workbench layout: regions, menus, empty/downloading/building/ready/error states, keyboard behaviour.
3. Technical proof: Rust workspace and CLI that download, build the store and answer a filtered count against the real database, with measured build time, store size and query time.
4. ABI and WinUI shell over that core: open the store, filter, play a puzzle to completion.
5. Ratings, history, export, installer.
