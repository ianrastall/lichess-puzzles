# Lichess Puzzles

A Windows desktop app for downloading, filtering and playing the [Lichess puzzle database](https://database.lichess.org/#puzzles) offline. Part of the Jupiter family.

Status: the desktop app plays, filters, downloads and updates the puzzles (stage 4 of the [project brief](docs/project-brief.md)). Players with ratings, history, export, the evaluation engine and an installer follow in stage 5.

- [Project brief](docs/project-brief.md): decisions, source data, workflows, measurements and open questions
- [Workbench layout](docs/workbench-layout.md): regions, menus, states and keyboard behaviour for the desktop shell
- [Source map](docs/source-map.json): related folders and their dispositions

This is a fresh repository. The previous [Lichess Puzzle Desktop](<D:/dev/proj/lichess-puzzle-desktop>) remains in place as a reference.

## Desktop app

Run `LichessPuzzles.exe` from the published folder `dist/LichessPuzzles` (or the zip beside it). Windows 11 24H2 or newer, x64; no separate runtime installation is needed.

On first run the app offers to download the Lichess puzzle database (about 300 MB) and build the store (about 500 MB, half a minute), or to import a `.csv` or `.csv.zst` you already have. Filters apply as they change; **Next puzzle** (Ctrl+N) draws a random matching puzzle. **Help > Keyboard shortcuts** lists the keys. The window layout is described in [docs/workbench-layout.md](docs/workbench-layout.md).

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
puzzles-next.store                      an update built beside the open store, until it replaces it
settings.json, ui.json                  the desktop app's settings, filters, layout and puzzle in progress
```

`LICHESS_PUZZLES_DATA_DIR` points the desktop app at another folder.

## Puzzle store

One file, memory-mapped for reading and replaced only by renaming a completed build into place:

- **Records:** 32 bytes per puzzle, holding everything filters read: theme bitmask (128 bits), plays, rating, deviation, opening family and variation ids, popularity, move count and flags (solver side, daily puzzle).
- **Ids:** packed into a `u64` each, in file order, with a sorted index only if the source is out of order.
- **Details:** FEN, moves, game URL and daily date as text, in zstd-compressed blocks of 1,024 puzzles.
- **Info:** a JSON trailer with section offsets, theme and opening tables, the source file's version and any rows that were skipped.

Every puzzle is checked during the build: the FEN must parse and every move must be legal. Rows that fail are skipped and reported.

## Build

Requirements: Rust 1.99.1 (pinned in `rust-toolchain.toml`), .NET SDK 10.0.1xx (`global.json`), Visual Studio Build Tools with MSVC (including its x64 redistributable files) and the Windows 11 SDK, Windows 11 x64.

```powershell
pwsh build/build.ps1
```

This formats, lints and tests the Rust workspace, runs the C# interop checks and publishes `dist/LichessPuzzles` and `dist/LichessPuzzles-<version>-win-x64.zip`, including the CLI. `-SkipChecks` publishes only.

```text
core/domain                    Rust domain core: download, store, filters, session, service
core/ffi                       C ABI (lpz_*) over the service, protobuf messages from proto/puzzles.proto
cli                            lichess-puzzles command line
src/LichessPuzzles.Interop     C# wrapper over the ABI
src/LichessPuzzles.App         WinUI 3 shell
tests/LichessPuzzles.InteropTests   checks across the real DLL boundary
build                          build script, native build targets, icon script
```

## Credits and licences

- Code: MIT, see `LICENSE`.
- Puzzles: the Lichess puzzle database, released under [CC0](https://creativecommons.org/publicdomain/zero/1.0/).
- Chess rules: [cozy-chess](https://github.com/analog-hors/cozy-chess), MIT.
- Pieces: Merida by Armando Hernandez Marroquin, from [lichess-org/lila](https://github.com/lichess-org/lila/tree/master/public/piece/merida), GPL version 2 or later. Distributing the app with these files means complying with that licence; see the brief's open questions.
- Theme descriptions are written for this app.
