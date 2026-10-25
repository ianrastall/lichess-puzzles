# Release notes

## 3.0.0 — 25 October 2026

The Jupiter rebuild of Lichess Puzzle Desktop, with a WinUI 3 shell and Rust core.

- Download the Lichess database with pause, resume and update checks, or import CSV/zstd files. Build a compact offline store with legal-position and move validation.
- Filter by rating, themes, openings, quality, solver side and solution length; save presets and skip played puzzles.
- Solve with click, drag or keyboard input, legal-move markers, hints, promotion, solution navigation and replay.
- Keep separate players, Glicko-2 ratings and attempt history; choose puzzles near the player's rating.
- Export PGN, CSV and EPD, and optionally display the built-in evaluation engine's score and line.
- Choose the shared Jupiter dark, light or Windows theme, accent colour and board appearance.
- Switching or deleting the current player restarts the board for the new player; prior attempts remain with their owner. Reopening a played puzzle is an unrated replay and creates no duplicate history.
- The Players menu refreshes without accumulating duplicate entries.
- Export cancellation and failures preserve existing destination files; temporary files use unique names and are cleaned up automatically.
- Evaluation reports checkmate and drawn positions immediately, and distinguishes an interrupted search from a pending result. Hint and navigation controls remain available after a command finishes.
- Deliver a self-contained x64 Windows installer with Start Menu and uninstall entries, a portable zip, a complete source bundle including Jupiter.UI, licence notices and SHA256 checksums.

Requires Windows 11 24H2 or later, x64. The installer is unsigned. Existing Jupiter installations upgrade in place and retain puzzles, settings, players and history. The predecessor Lichess Puzzle Desktop installation and data remain separate; automatic migration is not included. The built-in engine is a training aid rather than an authoritative engine.
