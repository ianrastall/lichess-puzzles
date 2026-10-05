# Lichess Puzzle Desktop

A dark-mode Windows desktop app for playing puzzles from the
[Lichess puzzle database](https://database.lichess.org/#puzzles) offline.

- **Shell:** C# 14, .NET 10, WinUI 3 (Windows App SDK), MVVM with CommunityToolkit.Mvvm
- **Core:** C++20 behind a plain C ABI (`lpd_core.dll`) — chess rules, the puzzle database,
  solution checking, export and evaluation all live here

## Features

- Board with click-to-move and drag-and-drop, legal-move markers, promotion picker, Merida pieces
- Move notation (SAN) with click and arrow-key navigation
- Hint, show solution, retry, next puzzle; any checkmating move is accepted as a solution
- Filters: rating range, minimum popularity and plays, themes, opening, skip already-played puzzles
- Jump to a puzzle by id
- Evaluation bar and principal variation from a built-in engine (hidden while solving by default)
- History of played puzzles with a clean-solve count and streak
- Export:
  - current puzzle as PGN (copy or save) and its FEN
  - every puzzle matching the filters as PGN or CSV
  - every puzzle you have played as PGN

## Install

Download `LichessPuzzleDesktop-<version>-Setup.exe` from the
[releases page](https://github.com/ianrastall/lichess-puzzles/releases) and run it. It installs
for the current user without an administrator prompt, adds a Start menu entry and can be
removed from **Settings → Apps**. Nothing else is needed: the .NET and Windows App SDK runtimes
are included. The installer is not code-signed, so Windows SmartScreen may ask you to confirm.

## Building from source

Requirements:

- Windows 10 1809 or later, x64
- .NET 10 SDK
- Visual Studio 2022 or later with the **Desktop development with C++** workload
  (MSVC x64 tools, and CMake + Ninja — either Visual Studio's or your own on `PATH`)
- PowerShell 7 (`pwsh`), used by the native build step

Build and run:

```
dotnet run --project app/LichessPuzzleDesktop -c Release
```

The app project builds the native core first by calling `build-core.ps1`, then copies
`lpd_core.dll` next to the executable. The Windows App SDK runtime is bundled, so nothing
else needs installing.

To build the core on its own and run its self-checks (perft, SAN, puzzle replay, PGN, engine):

```
pwsh ./build-core.ps1 -Configuration Release -Test
```

`LichessPuzzleDesktop.slnx` opens in Visual Studio.

### Installer and releases

With [Inno Setup 6](https://jrsoftware.org/isdl.php) installed, this publishes a self-contained
build and compiles `artifacts\installer\LichessPuzzleDesktop-<version>-Setup.exe`:

```
pwsh ./build-installer.ps1
```

The version comes from `<Version>` in the app project. To publish a release, set that version,
commit, then push a matching tag — CI builds the installer and attaches it to a new GitHub release:

```
git tag 2.0.0
git push origin 2.0.0
```

## Getting the puzzles

The app starts with a 13-puzzle sample (`samples/sample_puzzles.csv`). For the full set:

1. Download `lichess_db_puzzle.csv.zst` from <https://database.lichess.org/#puzzles>.
2. Decompress it, for example with `zstd -d lichess_db_puzzle.csv.zst`.
3. Choose **Open database…** (Ctrl+O) and pick the `.csv`.

The file is memory-mapped and indexed in memory at roughly 33 bytes per puzzle; the last
database you opened is reloaded on the next start.

## Keyboard

| Key | Action |
| --- | --- |
| Left / Right | Step back / forward through the moves |
| Home / End | First / latest position |
| Ctrl+N | Next puzzle |
| Ctrl+O | Open database |
| Ctrl+S | Save the current puzzle as PGN |

## Layout

```
core/
  include/lpd_core.h    the C ABI (the only header the app depends on)
  src/chess.*           position, move generation, FEN, UCI, SAN
  src/db.cpp            memory-mapped CSV index, filtering, bulk export
  src/session.cpp       puzzle state machine: move checking, hints, navigation
  src/puzzle.*          CSV row parsing and PGN generation
  src/engine.cpp        alpha-beta search used for the evaluation
  src/history.*         log of played puzzles
  tests/core_tests.cpp  self-checks
app/LichessPuzzleDesktop/
  Interop/              P/Invoke declarations mirroring lpd_core.h
  Core/                 thin managed wrappers over the native handles
  ViewModels/           MainViewModel and item view models
  Controls/ChessBoard   board rendering and pointer input
  Services/             settings, file pickers, clipboard, dialogs
  Assets/pieces/        Merida piece SVGs
samples/                bundled sample puzzles
installer/              Inno Setup script
build-core.ps1          builds the native core with MSVC + CMake + Ninja
build-installer.ps1     publishes the app and compiles the installer
```

Settings and history are stored in `%LOCALAPPDATA%\LichessPuzzleDesktop`.

## PGN format

Each puzzle is one game with `SetUp`/`FEN` tags and the full solution as the movetext.
By default the game starts from the FEN in the database, so the opponent's move comes first
and is followed by a comment marking where the puzzle begins; **Export → Start PGN at the
player's move** starts from the position shown to the player instead. Puzzle metadata is
written as `PuzzleId`, `PuzzleRating`, `PuzzleRatingDeviation`, `PuzzlePopularity`,
`PuzzlePlays` and `PuzzleThemes` tags, with the source game in `Source`.

## Limitations

- The evaluation comes from a small built-in engine, not Stockfish. It finds short mates and
  simple tactics quickly but is only a rough guide on hard positions.
- After a puzzle is finished you can step through the solution, but not play your own
  side lines.
- x64 only. Chess960 castling is not supported (the Lichess puzzle set does not need it).

## Earlier version

Versions 1.0–1.2 of this repository were a WPF/.NET 8 app that imported the puzzles into
SQLite. That code is still available under the `1.0.0`, `1.1.0` and `1.2.0` tags.

## Credits and licences

- Code: MIT, see `LICENSE`.
- Puzzles: the Lichess puzzle database, released under
  [CC0](https://creativecommons.org/publicdomain/zero/1.0/).
- Piece set: Merida by Armando Hernandez Marroquin, taken from
  [lichess-org/lila](https://github.com/lichess-org/lila/tree/master/public/piece/merida),
  licensed [GPLv2+](https://www.gnu.org/licenses/gpl-2.0.txt). Distributing the app with
  these files means complying with that licence.
- Piece-square tables in the engine follow Tomasz Michniewski's "simplified evaluation function".
