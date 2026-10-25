# Release 3.0.0 validation

Release scope: stabilize the existing offline training workflow, adopt shared theme selection, verify desktop delivery and publish the Jupiter rebuild in the existing public repository without rewriting the predecessor's history.

## Automated checks

The release build runs Rust formatting, Clippy with warnings rejected, workspace tests and checks across the real C#–Rust DLL boundary. The WinUI build runs the shared Jupiter style check with Error severity.

Regression coverage includes player switching during a partially solved rated puzzle, invalid player switches, deleting the player who owns the board, replaying without duplicate history, replacing an export, cancellation preserving the old file and cleanup after a failed export.

## Packaging

The normal build produces the self-contained Windows installer, portable zip, source zip (including the exact shared Jupiter.UI source snapshot) and SHA256 checksums. Merida SVG source, attribution and GPL v2 text ship together. Generated notices include Rust and NuGet package metadata and licence files.

## Compatibility

- Windows 11 24H2 or later, x64; runtimes included; unsigned per-user installer.
- Existing Jupiter Lichess Puzzles settings, puzzle store, players and history stay in `%LOCALAPPDATA%\Jupiter\LichessPuzzles` during upgrade and uninstall.
- The older Lichess Puzzle Desktop installation uses a separate directory and data store; automatic migration is not included.
- Full download, pause/resume, import, store replacement, export and ratings are exercised against deterministic local fixtures by core and interop tests. A production database is used for desktop smoke testing where available.

## Desktop verification

To be completed against the final installer before publication.
