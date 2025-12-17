# Lichess Puzzles

Windows desktop app (WPF/.NET 8) for practicing Lichess puzzles locally.

## Features
- Downloads the official Lichess puzzle dump (~250 MB .zst) and builds a local SQLite database of ~5.6M puzzles.
- Play puzzles on a drag-and-drop board that flips to the side to move; click any move in the list to jump to that position.
- Filter by rating range, get a hint, retry the current puzzle, or load the next one with a single click.
- Shows puzzle themes and status; copy a PGN of the puzzle (includes source game metadata when available).
- Refresh the puzzle database at any time from inside the app.

## Requirements
- Windows 10/11
- .NET 8 SDK
- Internet access for the initial/refresh puzzle download

## Run the app
1) Restore/build/run: `dotnet run --project "Lichess Puzzles.csproj"` (or open the solution in Visual Studio 2022 and press F5).
2) On first launch, the app prompts to download the puzzle database (~250 MB). It is unpacked to SQLite at `%LOCALAPPDATA%\LichessPuzzles\chess_puzzles.db`.
3) After the download completes, a puzzle loads automatically. Use the rating boxes if you want to narrow the range before clicking `Next`.

## Using it
- Move pieces by click-then-click or drag-and-drop.
- `Hint` highlights the from-square of the next move; `Retry` restarts the current puzzle; `Next` loads another random puzzle within the selected rating range.
- Click moves in the move list to rewind/inspect; `Copy PGN` copies a PGN built from the puzzle (or its source game, if fetched).
- `Update Puzzle Database` re-downloads and rebuilds the local database to get the latest puzzles.

## Notes
- The puzzle database lives at `%LOCALAPPDATA%\LichessPuzzles\chess_puzzles.db`. Delete it if you want to force a fresh download.
- NuGet packages restore automatically on build (ChessDotNet, Microsoft.Data.Sqlite, SharpVectors.Wpf, WPF-UI, ZstdSharp.Port).

## License
MIT License. See `LICENSE`.
