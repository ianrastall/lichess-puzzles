# Release notes

## 3.2.0 — 30 October 2026

- Add optional pre-presentation screening for forced mate in one or two, using a bounded legal-move proof search independent of theme tags. Searches run off the UI thread, with a prepared candidate and a reusable position/version cache in `screening.sqlite`.
- Preserve the original CSV, derived puzzle store, Lichess ratings and user history. Screening settings are saved with presets; switching screening off restores the original selection. Filter counts and exports remain before screening.
- Keep near-rating selection within an explicit ±75, ±150 or ±300 range. Empty selections explain how to adjust it, without a silent fallback to distant puzzles. New settings default to near-rating selection at ±150 and skipping played puzzles; existing saved choices are retained.
- Revalidate prepared candidates after changes to filters, player, rating, history or database. Screening failures preserve the active puzzle and leave Next available to retry.
- Clarify the local rating's Glicko-2 deviation, provisional status and dependence on the selected puzzle pool. Keep first-attempt rating settlement unchanged.
- Add the `screen` CLI command for reproducible sample checks and cache/performance measurements.

## 3.1.0 — 27 October 2026

- Import an existing Lichess CSV or compressed CSV beside the executable when no readable puzzle store is installed, including files downloaded outside the app without metadata.
- Make local import the primary setup action. Desktop downloads and automatic update checks are replaced with a browser link to Lichess; users choose when to download and import updates. Original files are always retained. CLI download commands remain available.

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
