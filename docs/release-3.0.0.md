# Release 3.0.0 validation

Release scope: stabilize the existing offline training workflow, adopt shared theme selection, verify desktop delivery and publish the Jupiter rebuild in the existing public repository without rewriting the predecessor's history.

## Automated checks

Rust formatting and Clippy with warnings rejected pass. All **40 Rust tests** and **38 checks across the real C#–Rust DLL boundary** pass. The WinUI build runs the shared Jupiter style check with Error severity and passes.

Regression coverage includes player switching during a partially solved rated puzzle, invalid player switches, deleting the player who owns the board, replaying without duplicate history, replacing an export, cancellation preserving the old file and cleanup after a failed export. Terminal evaluation covers checkmate for both sides, stalemate, superseding a running search and clearing terminal results for a new search; all three outcomes also cross the real DLL boundary.

## Packaging

The normal build produces the self-contained Windows installer, portable zip, source zip (including the exact shared Jupiter.UI source snapshot) and SHA256 checksums. Merida SVG source, attribution and GPL v2 text ship together. Generated notices include Rust and NuGet package metadata and licence files.

## Compatibility

- Windows 11 24H2 or later, x64; runtimes included; unsigned per-user installer.
- Existing Jupiter Lichess Puzzles settings, puzzle store, players and history stay in `%LOCALAPPDATA%\Jupiter\LichessPuzzles` during upgrade and uninstall.
- The older Lichess Puzzle Desktop installation uses a separate directory and data store; automatic migration is not included.
- Full download, pause/resume, import, store replacement, export and ratings are exercised against deterministic local fixtures by core and interop tests. A production database is used for desktop smoke testing where available.

## Desktop verification

Checked on 25 October 2026 with a separate 13-puzzle fixture folder, leaving the production rating and history untouched:

- Per-user installer upgrade exits with code 0 and requires no restart. Installed app, core DLL, shared UI DLL, artwork licence and dependency notices match the packaged files by SHA256.
- Windows registers **Lichess Puzzles** as `Jupiter.LichessPuzzles` in Start.
- Dark and light appearance are visually checked. Light appearance persists across app restart and installer upgrade; dialogs follow it.
- A clean solve of `0000H` with `Qg2#` increases the test player's rating from 1500 to 1695 and creates one History entry. Reopening the puzzle is a replay: hints and showing its solution leave the rating and History total unchanged.
- The Hint control remains enabled after a hint; navigation, solution and History shortcuts work.
- A named preset saves to settings and appears in the preset menu. Export matching puzzles writes 13 PGN games and reports success with a Show file action.
- Production puzzle store and player database hashes are identical before and after upgrade testing.
- Source archive hashes verify for all 136 files. Extracted source repackages using its manifest without requiring Git metadata.

Final package checks also pass: the completed puzzle displays **Checkmate — Black wins** with a terminal evaluation bar; adding and selecting a second player restarts the board at 1500 with no played puzzles; the Players menu has exactly one New player command after repeated refreshes and switching players.

Launching the installed **Lichess Puzzles.lnk** Start shortcut opens the production 6,157,341-puzzle store, restores the user's open puzzle and retains the existing player rating and five recorded attempts. The app is left open on that production workspace after verification.
