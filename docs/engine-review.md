# Second-engine puzzle screening

Version 3.3.0 adds **Skip shallow engine solutions** to the filter pane. It is off by default, saved with presets, and applies only to selecting the next puzzle. Direct ID/history opens and exports retain the full original database. It can be combined with exact short-mate screening and fixed near-rating ranges.

Lichess generates/checks puzzles using Stockfish NNUE; its published puzzle ratings come from player solving attempts via Glicko-2, not from a Stockfish Elo estimate ([Lichess database documentation](https://database.lichess.org/#puzzles)). The new screen supplies an independent engine opinion about search effort. It does not replace either the published puzzle rating or the player's local rating.

## Engine and decision

Reuse the existing native Rust engine over `cozy-chess`: iterative-deepening alpha-beta/PVS, capture/check quiescence, material and piece-square evaluation. No new crate, subprocess, model download or network service is required. This is a small classical engine, not a claim of Stockfish strength or of being the fastest available engine.

The engine receives a board without the recorded solution as a hint. After applying the opponent's setup move, independently search **each solver turn** along the supplied continuation. Compare recommendations only after each completed iteration. The default maximum depth is four; check and quiescence extensions may examine deeper positions. Early mate termination is allowed. At least two completed observations on every solver turn must agree with the recorded move, and all observations must agree, before the puzzle is skipped. If two different moves both immediately checkmate, accept them as equivalent for this comparison.

The review must finish every solver turn's requested search or early mate termination. A different recommendation, a later reversal or insufficient work prevents rejection. Different recommendations do not prove that Lichess is wrong: the small engine may be weaker, and alternative winning moves may exist. This checks the recorded line's solver turns; it does not certify that the recorded opponent defences are optimal. Fast engine solutions can still be difficult or useful for people.

Exact short-mate proof remains a separate algorithm. When both controls are enabled, it runs first. Its existing treatment of inconclusive proof checks is unchanged; the second-engine screen alone keeps inconclusive reviews available.

## Work limits and cache

- 50,000 visited search nodes per whole puzzle, divided among remaining solver turns. The hard limit includes incomplete work. A retry within PVS cannot increment nodes after the search has stopped.
- Soft 75 ms total review time, also divided among remaining solver turns. The clock is checked before iterations and every 256 nodes. Allocation, move generation, serialization, cache I/O and store lookup add overhead; this is not a hard real-time guarantee.
- A 32,768-entry transposition table per solver-turn search, instead of the evaluation pane's 524,288 entries. Searches remain single-threaded and run through the shell's existing worker path. Selection retains its 128-candidate/750 ms batch budget plus individual completion/scan overhead and one-candidate preparation.
- Only fully completed reviews are cached. Key: algorithm version, complete original FEN, complete recorded move line and every limit parameter. Neither puzzle ID nor rating is a chess identity. Bump review version when search/evaluation/comparison behavior changes.
- `engine-review.sqlite` is independent of `players.sqlite`, `screening.sqlite` and the read-only puzzle store. It uses WAL with synchronous NORMAL because it contains disposable derived results. A power loss may discard recent cache entries; they can be recomputed. Player history durability is unchanged. See [SQLite's synchronous documentation](https://www.sqlite.org/pragma.html#pragma_synchronous).

Selection settings are part of the candidate identity. Existing state-revision checks still protect filter/player/rating/history/store changes before committing a session. Rejecting or preparing a candidate creates no attempt and changes no rating.

## CLI

```text
lichess-puzzles review 0000D
lichess-puzzles review --rating 1400-1799 -n 100 --seed 20261030 --json
lichess-puzzles review --rating 1400-1799 --length 2-2 --without-theme mate --max-rd 100 --min-plays 100 -n 100 --seed 20261030 --json
```

`--depth` (1–12), `--nodes` (1–1,000,000) and `--millis` (1–2000) override the diagnostic limits; desktop selection uses the fixed defaults. `--data-dir` selects a separate profile. The CLI only reports; it never culls, starts an attempt or changes player ratings. JSON is one record per puzzle, including its current published rating, depth observations, completeness, cached status and separate lookup/review timings. For cache hits the stored engine elapsed time describes the original search; `review_ms` measures the current cache access. Summary output goes to stderr.

## Measurements, 30 October 2026

Windows x64 release build; installed Lichess store with 6,157,341 records (SHA256 `5a5ba737d09307832fbe508acfa60b7cc1b63891bafe1f20b993a7f4728b6668`). Use a separate copy and cache; the store hash remained unchanged. Sample 100 puzzles per band, seed 20261030, depth four, 50,000 nodes, 75 ms. These are stratified pilot samples, not estimates of the database's overall proportions or a held-out human difficulty predictor. The two cohorts may overlap.

| Published rating | Broad sample: whole-line agreement | Broad: would skip | Matched sample: whole-line agreement | Matched: would skip |
| --- | ---: | ---: | ---: | ---: |
| 0–999 | 96/100 | 94/100 | 85/100 | 82/100 |
| 1000–1399 | 82/100 | 62/100 | 83/100 | 66/100 |
| 1400–1799 | 52/100 | 30/100 | 74/100 | 53/100 |
| 1800–2199 | 36/100 | 20/100 | 50/100 | 18/100 |
| 2200–2599 | 22/100 | 7/100 | 30/100 | 10/100 |
| 2600–4000 | 3/100 | 0/100 | 14/100 | 2/100 |

The matched cohort excludes puzzles tagged `mate`, requires exactly two solver moves, rating deviation at most 100 and at least 100 plays. Tags cannot guarantee the absence of every mating line. Controlling these factors preserves a useful association with published difficulty, but does not establish causation or individual accuracy. In the broad highest band the final recommendation agreed on the **first** solver move in 46/100 puzzles, while only 3/100 had complete whole-line agreement: checking just the first move would substantially overstate coverage.

Broad-sample uncached engine review averaged 9.25 ms (median 8.73, p95 19.95, max 23.03). No review exceeded 50,000 nodes. With a new engine cache and a warm operating-system file cache, all 600 records took 6.31 seconds including lookup, cache writes and reporting; repeating took 3.97 seconds, with 384 completed cache hits and incomplete cases searched again. Times are machine/load dependent. Switching this disposable cache from durable per-record commits to WAL/NORMAL removed an avoidable I/O bottleneck; no player database settings changed.

## Validation

Regression checks cover interrupted first iterations, strict node accounting through retries, cancellation, both colors' score orientation, immediate mating alternatives, every solver turn, later recommendation reversals, invalid input, cache persistence and identity changes, incomplete-result retries, non-mating shallow rejection, candidate invalidation when the option changes, source preservation and unchanged history/rating on preparation or failure. Native C# integration checks cover the new option across the real DLL boundary. Installer and desktop verification are recorded in the release validation notes.
