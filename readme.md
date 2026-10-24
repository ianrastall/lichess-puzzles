# Lichess Puzzles

A Windows desktop app for downloading, filtering and playing the [Lichess puzzle database](https://database.lichess.org/#puzzles) offline. Part of the Jupiter family.

Status: the Rust domain core and command-line shell are built (stage 3 of the [project brief](docs/project-brief.md)). The WinUI 3 shell, ratings, history and export follow.

- [Project brief](docs/project-brief.md): decisions, source data, workflows, measurements and open questions
- [Workbench layout](docs/workbench-layout.md): regions, menus, states and keyboard behaviour for the desktop shell
- [Source map](docs/source-map.json): related folders and their dispositions

This is a fresh repository. The previous [Lichess Puzzle Desktop](<D:/dev/proj/lichess-puzzle-desktop>) remains in place as a reference.

## Command line

```text
lichess-puzzles download            download the database, resuming an interrupted download; skipped when current
lichess-puzzles check-update        compare the downloaded file with the server's (one HEAD request)
lichess-puzzles build               build the puzzle store from the download, or --input <file.csv|file.csv.zst>
lichess-puzzles status              describe the store
lichess-puzzles count [filter]      count matching puzzles
lichess-puzzles sample [filter] -n 10 [--seed N]
lichess-puzzles show <puzzle-id>    one puzzle with its solution in SAN
lichess-puzzles themes [filter]     matching puzzles per theme
lichess-puzzles openings [filter]   matching puzzles per opening family
```

Filters: `--rating 1500-1800` (or `1500-`, `-1800`), `--max-rd N`, `--min-popularity N`, `--min-plays N`, `--theme T` (any of those given), `--all-themes T`, `--without-theme T`, `--opening TAG` (family or variation, such as `Sicilian_Defense`), `--solver white|black`, `--length 2-3` (moves the solver plays). Theme and opening options repeat.

Data lives in `%LOCALAPPDATA%\Jupiter\LichessPuzzles` (`--data-dir` to change):

```text
source\lichess_db_puzzle.csv.zst        the download
source\download.json                    ETag, Last-Modified and length of the completed download
source\lichess_db_puzzle.csv.zst.part   an interrupted download, with its own .part.json record
puzzles.store                           the puzzle store
```

## Puzzle store

One file, memory-mapped for reading and replaced only by renaming a completed build into place:

- **Records:** 32 bytes per puzzle, holding everything filters read: theme bitmask (128 bits), plays, rating, deviation, opening family and variation ids, popularity, move count and flags (solver side, daily puzzle).
- **Ids:** packed into a `u64` each, in file order, with a sorted index only if the source is out of order.
- **Details:** FEN, moves, game URL and daily date as text, in zstd-compressed blocks of 1,024 puzzles.
- **Info:** a JSON trailer with section offsets, theme and opening tables, the source file's version and any rows that were skipped.

Every puzzle is checked during the build: the FEN must parse and every move must be legal. Rows that fail are skipped and reported.

## Build

Requirements: Rust 1.99.1 (pinned in `rust-toolchain.toml`) with the MSVC toolchain, Windows 11 x64.

```powershell
cargo test --workspace
cargo build --release
```

## Credits and licences

- Code: MIT, see `LICENSE`.
- Puzzles: the Lichess puzzle database, released under [CC0](https://creativecommons.org/publicdomain/zero/1.0/).
- Chess rules: [cozy-chess](https://github.com/analog-hors/cozy-chess), MIT.
