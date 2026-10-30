# Puzzle selection and short-mate screening

Direction agreed with Ian on 30 October 2026: retain the original Lichess database and make personal culling simple. Many unwanted puzzles are short mates. Select near the player's developing rating; don't confuse short mate length, engine effort and human difficulty.

## Behavior

The filter pane offers Short-mate screening: Off, Exclude mate in one, and Exclude mate in one or two. It is opt-in, stored in settings and in named presets. Original puzzle data, CSV exports and Lichess ratings are unchanged. Direct ID/history opens bypass selection screening. Counts are explicitly before screening and near-rating matching.

The solver starts after the opponent's first move, using the existing cozy-chess UCI conversion for castling and en passant. A separate exact proof search first checks all legal mating moves; then checks whether any first move guarantees mate on the solver's next turn against **every** legal defence. Promotions and underpromotions come from legal move generation. Stalemate cannot satisfy the proof. No static material score or fast engine solution is converted into a human difficulty estimate.

Each check has 50,000 move visits and 50 ms. The outcomes are mate in one, mate in two, no forced mate within two, or inconclusive. Inconclusive checks are not cached or treated as evidence of difficulty. A candidate batch is capped at 128 sampled records and a 750 ms processing budget (plus individual check completion/store scan overhead). No accepted result produces a retryable selection message, not a claim that the full database is exhausted.

`screening.sqlite` is an independent, disposable SQLite sidecar. Its key is algorithm version plus the full solver FEN, including side, castling, en passant and move counters. Puzzle ID alone is not an identity. Version 1 caches complete checks through two moves. All reads of the source store remain read-only.

The shell's core calls run on worker threads. After selection it prepares one accepted candidate without starting a session. Before using that candidate the core rechecks current filters, player, played status and rating bounds. Candidate queues are invalidated on changed selection settings, player or store identity; a weak reference distinguishes store instances without keeping the old mapped store open during replacement. Rating changes are handled by rechecking current eligibility, rather than presenting a stale out-of-range puzzle.

## Rating

Near-rating selection uses an explicit ±75, ±150 or ±300 band around the player's current rounded local rating plus the existing offset. No automatic widening remains. Bounds intersect the manual rating filter; nothing overrides theme/quality/side/length choices. New settings use ±150 and skip played; old JSON choices are retained. Reset filters restores these new training defaults with screening off.

Existing per-player Glicko-2 settlement remains: first wrong move/hint/solution is a loss; a clean solve is a win; only the first attempt counts. Preparing/rejecting a candidate changes no ratings/history. Personal filtering can narrow the skills represented by the local estimate. It is not an official Lichess account, FIDE or other playing-strength rating.

Before selecting, the core forecasts any pending abandonment loss with the same Glicko-2 calculation used at settlement. It does not write that loss unless an eligible next puzzle can start. A shared state gate captures player/filter/played-history/session/store inputs coherently, while a revision counter invalidates work when any of them changes. Long scans and mate checks run outside the gate. Candidate success **and failure** are checked against the revision under the session-commit lock; stale work retries, up to five times. Store readers are dropped before acquiring the commit gate to avoid holding a mapped reader while store replacement waits for it. These changes address both Copilot findings on PR #2.

The player summary is at the top of the inspector, above both tabs. The rating uses the shared section-heading style and is explicitly labeled Player rating; provisional status, Glicko-2 deviation and rated-puzzle count are visible below it.

## Validation

Rust regressions cover exact mate-in-one/two recognition, a nonmate, stalemate, an exhausted budget, cache persistence and changed-position identity. Service tests prove untagged mates are screened, source/store bytes stay unchanged, rejected candidates don't change ratings/history or replace the active board, changed filters invalidate preparation, mate-in-one-only exclusion still allows mate in two, and near-rating selection respects its explicit bounds. Existing solve/rating/replay/export/import tests remain in the required suite.

Four additional adversarial regressions exercise abandonment projection with failed-selection rollback, changing filters at the selection/commit boundary, switching to a differently rated player at that boundary, and replacing the store before commit without a mapped-reader deadlock. Candidate selection is retried against the changed state.

The C# to native DLL checks exercise these same user-visible contracts over generated protobuf messages, including preparation, retryable selection errors and disabling screening. The normal installer build runs format, Clippy, Rust tests, native integration checks and the shared Jupiter style check.

Measured with the 6,157,341-puzzle installed store on 30 October 2026, release build, `screen --rating 1350-1650 -n 200 --seed 42`: 9 mates in one, 36 mates in two, 155 without a forced short mate, zero inconclusive. First run 1,188.1 ms, repeat 174.5 ms with 200 cache hits. This sample is not a quality assessment of the entire dataset and does not establish that the short mates were easy for people.

Final delivery checks on 30 October 2026:

- The screening preview passed the normal `pwsh build/build.ps1`: format, Clippy with warnings denied, all 46 Rust tests, all 55 C# to native integration checks, Jupiter style validation, app/CLI publish, complete source ZIP and installer. The review fixes add four Rust regressions, for a total of 50. Final release validation is recorded in `release-3.2.0.md`.
- Installed 3.2.0 over the existing per-user installation with installer exit code 0. The installed executable reports 3.2.0.0 and its SHA256 matches the published executable. The existing Start Menu shortcut launches it by the application name.
- Desktop verification used an isolated test data directory with a copy of the complete store, not Ian's profile. Fresh near-rating ±150/skip-played defaults, opt-in screening, a screened puzzle within range, saving a screening preset, restart persistence and both dark/light themes were inspected in the installed GUI. A Windows contrast theme was not manually exercised.
- Ian's existing `players.sqlite`, `puzzles.store` and `settings.json` SHA256 values were unchanged after installation and verification. The isolated GUI checks created no rated attempts. Original source/store byte preservation is separately asserted by the service and integration regressions.
- The source ZIP contains both new screening/selection modules and this design document, along with the existing exact Jupiter.UI source snapshot. Publication follows final installer and desktop verification.

## Deferred work

This release does not infer human difficulty from engine search speed, change the original ratings, distribute a culled CSV, introduce personal per-puzzle dismissals/favorites, or impose a new rated-session skip policy. Broader curation and calibration against human feedback can follow real use of the first screening controls.
