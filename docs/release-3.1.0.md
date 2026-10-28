# Release 3.1.0 validation

Validated on 27 October 2026. This minor release fixes discovery of user-downloaded Lichess databases and puts desktop downloads and updates under the user's control.

## Automated checks

The full `pwsh build/build.ps1` pipeline passes: Rust formatting, Clippy with warnings rejected, all 40 Rust tests, and all 46 checks across the real C#–Rust DLL boundary. The WinUI Release build and shared Jupiter style check pass.

New regression checks cover missing files, ignoring partial downloads, discovery without download metadata, executable-directory precedence, plain and compressed CSV filenames, importing a discovered CSV with an unreachable source URL, and preserving the original database bytes. Existing Rust coverage verifies that compressed and plain CSV inputs produce equivalent stores.

## Packaged desktop smoke test

The self-contained published app was copied to an isolated smoke-test folder and launched with a temporary data directory, using a different working directory from the executable directory.

- With no database, the setup screen displays **Import puzzle file…** as the main action and **Get database…** as the browser-link action. The setup layout was visually checked.
- After placing the 13-puzzle CSV fixture beside the executable as `lichess_db_puzzle.csv`, launching the app automatically installs all 13 puzzles and opens a playable puzzle without a download prompt or download metadata.
- The imported CSV's SHA256 matches the original fixture after import.
- The File menu exposes the Lichess browser link and local import, with no in-app download or update-check command.
- The packaged CLI reports version 3.1.0. Desktop smoke testing uses the computer-use skill; production player data is not used.

## Packaging and compatibility

The normal pipeline generates the unsigned, self-contained Windows x64 installer, portable ZIP, source ZIP including the exact shared Jupiter.UI working-source snapshot, dependency notices, and SHA256 checksums. The source archive is refreshed after committing this validation record.

Windows 11 24H2 or later remains required. Existing puzzle stores, settings, players and history retain their existing data location. Existing stores are not automatically replaced by newer CSV files; users explicitly import updates. Desktop import always preserves the source file. CLI download commands remain available.
