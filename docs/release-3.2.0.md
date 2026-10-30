# Lichess Puzzles 3.2.0 validation

30 October 2026. The release includes optional short-mate screening, fixed rating ranges, the two selection-state review fixes and a prominent player-rating section.

## Required checks

The normal `pwsh build/build.ps1` passed formatting, Clippy with warnings denied, **50 Rust tests**, **55 C# to native integration checks**, and the shared Jupiter style check. It produced the per-user installer, portable ZIP, CLI, complete source ZIP with Jupiter.UI, and SHA256 checksums. Binaries were compiled from code commit `668c986`; subsequent source changes record validation only.

The four new adversarial regressions cover selection using a projected abandonment loss, preservation of the unfinished attempt/rating/history when no candidate matches, changing filters or the current player at the selection/commit boundary, and replacing the mapped store before commit. State changes invalidate both candidate successes and failures; selection retries under the new state. Long screening work stays outside the state gate.

## Installed desktop verification

- The installer completed with exit code 0 over the existing per-user installation. The installed executable reports 3.2.0.0 and its SHA256 matches the published executable. The Start Menu shortcut launches the installed app.
- Verification used a separate profile and a copy of the full 6,157,341-puzzle store. The new labeled player rating, visible provisional status/deviation/count, existing screening selection and preserved preset were inspected in the installed light-theme GUI. A solve updated the displayed rating and rated-puzzle count live. The rating summary is outside both tab-specific views, so it remains present in Puzzle and History.
- The isolated profile's progress was retained. The normal user's `players.sqlite`, `puzzles.store`, `settings.json` and `ui.json` SHA256 values were unchanged after installation and verification. No cleanup removes either profile.
- The screening preview was previously inspected in both dark and light themes, including preset saving/restart persistence. The revised layout was visually inspected in light; native shared theme resources and the style checker remain in use. A Windows contrast theme was not manually exercised.

See [selection design](puzzle-selection.md) for proof-search budgets, cache identity and limitations. Short-mate screening does not estimate human difficulty or replace the original Lichess ratings.
