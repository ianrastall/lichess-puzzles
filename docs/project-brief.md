# Lichess Puzzles: project brief

Working brief · 24 October 2026

## Purpose

Download, filter and play the Lichess puzzle database offline on Windows.

Priority: unranked group `chess-puzzles` in the [portfolio organization](../../_planning/portfolio-organization_draft.md); selected by Ian as the next Jupiter project on 24 October 2026.

## Decisions

Ian's answers, 24 October 2026:

- **Track:** Windows desktop on the [canonical stack](../../_standards/stack-reference_winui3-rust-core.md): WinUI 3/C# shell, Rust domain core, C ABI with protobuf messages. [Chess.com Crawler](../../chesscom-crawler/readme.md) is the working template for the toolchain, boundary and installer.
- **Repository:** a fresh repository with no copied history. [Lichess Puzzle Desktop](<D:/dev/proj/lichess-puzzle-desktop>) is a reference for features and behaviour only, and stays where it is.
- **Interface:** a Jupiter workbench. The arrangement follows the previous app's own layout under Jupiter conventions; it is not modelled on the Lichess website.

Further answers, 24 October 2026:

- **Name:** Lichess Puzzles; folder and repository `lichess-puzzles`.
- **Licence:** MIT, as for all of Ian's releases.
- **Chess rules:** the [cozy-chess](https://github.com/analog-hors/cozy-chess) crate (MIT, 0.3.4). Its UCI parsing converts castling to its king-takes-rook form and its SAN writer adds disambiguation, check and mate, so the core needs no notation code of its own.
- **Evaluation:** not authoritative; a small engine is enough. See [Evaluation](#evaluation).

## Source data

Checked 24 October 2026:

| Item | Value |
| --- | --- |
| Download | `https://database.lichess.org/lichess_db_puzzle.csv.zst` |
| Compressed size | 307,234,795 bytes; `Last-Modified` 2 October 2026; `ETag` present; byte ranges supported |
| Decompressed CSV | about 1.1 GB, 6,157,341 puzzles |
| Columns | `PuzzleId, FEN, Moves, Rating, RatingDeviation, Popularity, NbPlays, Themes, GameUrl, OpeningTags, DailyDate` |
| Vocabulary | 73 themes; 156 opening families and 1,435 variations |
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
| Built-in alpha-beta evaluation and principal variation | Rebuilt in Rust; see [Evaluation](#evaluation) |
| Inno Setup per-user installer, tag-driven releases | Follow the Chess.com Crawler installer pattern |

Its saved data (`%LOCALAPPDATA%\LichessPuzzleDesktop`) holds a handful of history entries and one player, so no migration is planned.

## Responsibilities

- **Rust core:** chess rules, SAN/UCI/FEN, download with resume and conditional checks, zstd decompression, store build and query, puzzle session state, Glicko-2, history, export.
- **Rust CLI:** `download`, `check-update`, `build`, `status`, `count`, `sample`, `show`, `themes` and `openings`; `export` follows with the export work. It exercises the core without the GUI and supports scripted use.
- **WinUI 3 shell:** menu bar (substantial app), filter pane, board control, puzzle details, move list, player and history panels, download progress, settings including the shared Appearance > Accent color control.

Data lives in `%LOCALAPPDATA%\Jupiter\LichessPuzzles` by default. The store folder can be moved because it is several hundred megabytes.

## Evaluation

Checked 24 October 2026:

- **Lozza** (Colin Jenkins) is now MIT on GitHub (`op12no2/lozza`; older references say GPL-3.0) and actively developed. Using it means either embedding a JavaScript runtime, as its own Windows binaries do, or porting a complete engine to Rust and keeping the port in step with upstream. Either is heavier than this app needs.
- **Embeddable Rust engines:** no maintained, MIT-licensed engine built for use as a library was found; the strong Rust engines are standalone UCI programs.
- **Recommendation:** a small engine in the Rust core on cozy-chess (alpha-beta with quiescence search, a transposition table, material and piece-square evaluation), run on a worker thread with a time limit and hidden while solving, as in the predecessor. A setting for an external UCI engine such as Stockfish can follow later.

The Merida pieces are GPLv2+, as in the predecessor; distributing them with an MIT app requires complying with that licence, or a differently licensed piece set.

## Core proof (stage 3)

Built 24 October 2026: a Rust workspace with the domain core (`core/domain`) and CLI (`cli`), pinned to Rust 1.99.1. Measured on this machine (16 logical processors) against the 2 October 2026 database:

| Step | Result |
| --- | --- |
| Download | 307,234,795 bytes in 141 s (about 2.1 MB/s from Lichess). Interrupted at 53.8 MB and resumed with a `Range` request; the result is byte-identical. |
| Update check | One `HEAD` request; compares `ETag`, `Last-Modified` and length. An interrupted re-download does not disturb the completed file or its record. |
| Store build from `.zst` | 25 s, peak memory 307 MB, 6,157,341 puzzles, none rejected by cozy-chess (every FEN parses and every move is legal) |
| Store build from plain `.csv` | 18 s |
| Store size | 501.5 MB: records 188 MB, ids 47 MB, compressed details about 266 MB |
| Open | under 1 ms (memory-mapped; reads the JSON info only) |
| Filtered count over all puzzles | 13 ms for any combination of rating, deviation, popularity, plays, themes (any/all/none), opening, solver side and length |
| Random sample | 17 ms (collects matching indices, then picks) |
| Puzzle by id | under 1 ms (binary search over ids, one 1,024-puzzle block decompressed) |

Compression settings barely change the size: level 3 gives 513 MB, level 19 with 4,096-puzzle blocks 474 MB (49 s to build). The default is level 9 with 1,024-puzzle blocks. The fixed-size records and ids are about half the store. If size matters more than simplicity, packing positions and moves in binary before compression, or dropping the downloaded `.zst` after a build (307 MB), are the next steps.

Store format, filter fields and CLI commands are described in the [readme](../readme.md).

## Open questions

- Whether a GitHub repository and public releases are wanted now.
- Whether to keep the downloaded `.zst` after a successful build (needed only to rebuild without downloading again).
- Piece set licensing (above).

## Staged progression

1. Confirm this brief and the open questions.
2. Workbench layout: regions, menus, empty/downloading/building/ready/error states, keyboard behaviour.
3. Technical proof: Rust workspace and CLI that download, build the store and answer a filtered count against the real database, with measured build time, store size and query time. **Done 24 October 2026.**
4. ABI and WinUI shell over that core: open the store, filter, play a puzzle to completion.
5. Ratings, history, export, installer.
