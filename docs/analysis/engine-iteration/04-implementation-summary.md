# Implementation summary - exp-4: per-turn context-attribution telemetry + effort-gated planning hygiene

Implements `04-plan.md` against
`04-feedback-from-experiment-3.md`. One atomic diff on a throwaway branch off main.

## Branch and commits

Branch: `exp-4-context-hygiene` (cut from `main` at `c95cb22`; planning docs landed on main first).
Decision unit: fold or abandon. Not merged, not pushed.

```
a21be20 hygiene: effort-gated --disallowedTools for S briefs (L2b)
b2053a2 hygiene: effort-gated planning-hygiene prompt line (L2a)
e7ab4da config: add [project].context_hygiene flag (default false)
946570b telemetry: emit context_attribution CostLedger from ImplementPhase
d484d2a telemetry: parse claude-code per-turn usage and tool-class bytes
```

(`c95cb22 docs: land exp-4 plan and update handoff ledger` is on `main`, not on the branch - the
planning docs are the shared record per the harness protocol.)

`git diff --stat main...HEAD`: 20 files, 769 insertions, 10 deletions. No op-doc edit, no live
`.build/config.toml` edit (both confirmed via name-only diff).

## Files changed (what / why)

Telemetry (commits 1-2, always-on, behavior-inert):
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeTurnParser.cs` (new) - parses the already-captured
  NDJSON stdout after the worker exits into a per-turn `ContextTurnSeries` (cache_read / cache_creation /
  output series, turn count, per-tool-class cache_creation byte buckets, total_cache_read, slope_ratio).
  JsonDocument-only (AOT-safe). Stack-agnostic: observes only vendor tool NAMES and token counts.
- `src/ThroughlineBuild.Workers.ClaudeCode/WorkerTranscriptWriter.cs` - its private `ReadUsage` now
  delegates to `ClaudeCodeTurnParser.ReadUsage` so the usage field-name shape lives in one place.
  Output-preserving (the 19 transcript snapshot/structure tests still pass byte-identical).
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs` - `AttachContextTurns` runs the parser
  post-exit on the production path and stashes a flat `Dictionary<string,object>` on
  `WorkerResult.Metadata["context_turns"]`. Best-effort (any parse failure leaves the result untouched);
  attaches nothing when zero turns. No flag, no prompt, no spawn.
- `src/ThroughlineBuild.EventLog/EventLogJsonContext.cs` - registers `List<long>` for AOT source-gen so
  the boxed series serialize in event Data (the only registration the flat payload needs).
- `src/ThroughlineBuild.Phases/ImplementPhase.cs` - Step 13b emits a `CostLedger` event with
  `kind == "context_attribution"` carrying the series + buckets + total + slope + an `attribution_note`,
  whenever `Metadata["context_turns"]` is present. Advisory (emit and proceed). Reads generic dictionary
  values by key; names no tool or stack; fires for every brief regardless of effort or flag. (Also adds
  the L2b `lean` gate + `LeanPlanning` arg in Step 11 - see below.)

Config flag (commit 3, opt-in, default false):
- `src/ThroughlineBuild.Briefs/ProjectContext.cs` - `public bool ContextHygiene { get; init; } = false;`
  (init-only; existing positional ctor call sites compile unchanged; `Empty` defaults it false).
- `src/ThroughlineBuild.Cli/Config.cs` - `OptionalBool(t, "context_hygiene", false)`; added to
  `KnownProjectKeys`; assigned on the returned `ProjectContext`. Threading to the phase reuses the
  existing `config2.Project` path (no Program.cs change).
- `src/ThroughlineBuild.Commands/Templates/config.toml.template` - commented `# context_hygiene = false`
  block with a one-line doc (generated configs stay default-off).

L2a prompt line (commit 4, S-effort + flag-on only):
- `src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md` - new `{{context_hygiene_section}}`
  token at the end of the `## Constraints` block (same newline-ownership convention as
  `{{preloaded_context_section}}`).
- `src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs` - `BuildContextHygieneSection(proj, ticket)`
  returns a stack-agnostic planning-hygiene bullet when `proj.ContextHygiene && ticket.Size == Size.S`,
  else `""`. No language/extension/tool-name words.
- 3 implement snapshots (`implement-original/rework/gate-rework.txt`) - the off-case empty-token render
  adds exactly one blank line at the Constraints/Golden boundary; updated deliberately as LF.

L2b tool restriction (commit 5, S-effort + flag-on only):
- `src/ThroughlineBuild.Contracts/IWorkerAgent.cs` - `WorkerOptions.LeanPlanning` (bool, default false),
  appended LAST in the positional record so every existing call site compiles unchanged. A generic,
  stack-agnostic intent flag; the phase names no vendor tool.
- `src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs` (BuildArgs) - maps `LeanPlanning` to
  `--disallowedTools TodoWrite,Task`. This adapter is the ONE place those tool-name literals live.
- `src/ThroughlineBuild.Phases/ImplementPhase.cs` - computes `lean = _project.ContextHygiene &&
  ticket.Size == Size.S` once and passes `LeanPlanning: lean`.

Tests added: `ClaudeCodeTurnParserTests.cs` (new, 6 tests, stack-free fixture), one `List<long>` AOT
round-trip in `JsonlEventSinkListValueTests.cs`, two phase tests in `ImplementPhaseTests.cs`
(context_attribution emit; LeanPlanning gate across S/M/L x on/off), three `context_hygiene` config
tests, one L2a lean-case brief test, two `BuildArgs` L2b tests.

## Test result

`dotnet test --nologo -v q` from repo root: ALL GREEN, 0 failed, across all 19 test projects. Key
projects: Briefs 147, Cli 629, EventLog 48, Phases 449, Workers.ClaudeCode 136 (full suite ~2300+ tests,
0 failures). `dotnet build throughline-build.sln`: 0 warnings, 0 errors.

Snapshot updates: the 3 implement snapshots gained one blank line each (the off-case empty-token render
of `{{context_hygiene_section}}`); deliberate, LF-verified (`grep -c $'\r'` == 0 on every touched
template/snapshot). The off arm renders byte-identical to these updated baselines, exactly as the
exp-3 preload token did - off == control == snapshot.

## Acceptance mapping (feedback / plan s10)

1. `[project].context_hygiene` parses (default false; true when set; not an unknown-key warning); off
   leaves implement argv + brief byte-identical to today. SATISFIED - three ConfigLoader tests;
   `BuildArgs_LeanPlanningFalse_OmitsDisallowedTools` (off argv unchanged; implement still passes no
   AllowedTools); off-brief renders the blessed empty-token snapshot baseline.
2. A `CostLedger` `kind == "context_attribution"` event per implement phase carrying cache_read_series,
   per-tool-class byte buckets, total_cache_read, slope_ratio, for every brief independent of the flag;
   round-trips under AOT. SATISFIED - `RunAsync_WorkerReturnsContextTurns_EmitsContextAttributionCostLedger`
   (emit, all fields), `EmitAsync_WithListLongValues_RoundTripsForContextAttribution` (reflection OFF =
   the AOT path). Step 13b has no flag gate.
3. Flag on -> S briefs (and only S) get the hygiene prompt line AND `--disallowedTools TodoWrite,Task`;
   M/L untouched; flag off -> none. SATISFIED - `Build_ContextHygiene_BulletAppearsOnlyForSEffortWithFlagOn`
   (brief level, 4 combos), `RunAsync_LeanPlanning_SetTrueOnlyForSEffortWithFlagOn` (phase gate, 4 combos),
   the two BuildArgs tests (argv mapping). L2a and L2b use the identical `ContextHygiene && Size==S`
   predicate.
4. Per-turn parser proven by a stream-only fixture with zero stack tokens. SATISFIED -
   `Parse_SeriesAndBuckets_FromStreamOnlyFixture` uses an NDJSON fixture of vendor tool names + token
   counts only (no extension/language/framework token anywhere).
5. Full `dotnet test` green; Briefs snapshots updated deliberately (LF). SATISFIED (see Test result).
6. L2b's tool-removal effect empirically confirmed on the pinned claude-code, OR L2b dropped with the
   finding recorded; no silent no-op ships. PARTIAL - DEFERRED to the run owner (see below).

## The L2b empirical-gate result (criterion 6) - DEFERRED, not dropped

The plan (s3d) requires running one real `claude` spawn with `--disallowedTools TodoWrite,Task` and
confirming from the stream that no `tool_use` named TodoWrite/Task appears. An agent CANNOT perform this:
spawning `claude` inside a Claude Code session hits the nested-session guard (documented standing
constraint), and `build chain` is likewise blocked. So the empirical confirmation is a manual run-owner
step, not something this implementation could execute.

What IS proven here: the argv wiring is unit-tested (`--disallowedTools TodoWrite,Task` is appended iff
`LeanPlanning`, absent otherwise). This is not a silent no-op of the exp-2 kind - the mechanism is
present and tested up to the process boundary; only claude-code's runtime HONORING of the flag is
unverified. Run-owner action before crediting L2b: on the pinned `claude` version, spawn one implement
with the flag and grep the stream for `"name":"TodoWrite"` / `"name":"Task"`. If they still appear, drop
L2b (keep L2a + telemetry) and record it; the always-on `context_attribution` telemetry now makes that
check event-derivable (todo_bytes / task_bytes go to ~0 and no todo/task turns appear).

## Prerequisites (run gate, not part of this diff)

Plan s9 names run preconditions (contamination cleanup; the setup.ts convention edit "cleanup a"; the
deriver path-resolution fix "cleanup b") that must be on `main` before the arms run so they don't
confound the read-content slice. These are separate cleanup tickets that gate the RUN, not the build; I
did not verify or land them (correctly out of the one-variable atomic diff). The run owner verifies them
before running off/on.

## Recommendation

FOLD the telemetry (commits 1-2 + the `List<long>` registration); it is the durable deliverable and is
pure observation - it makes the intra-session `cache_read` ramp event-legible for the first time
(`context_attribution` in `.build/events/*.jsonl`) with zero behavior change, worth keeping even if every
lever proves small.

HOLD the L2 lever (commits 3-5) behind its default-false flag pending data. It is correct, tested, and
inert when off, so folding it is safe, but do NOT credit it as a win yet:
- Run the empirical L2b gate first (criterion 6 above). If the flag is a no-op, drop L2b.
- Then ablate per plan s7: same binary, `context_hygiene` off (control) vs on (treatment), the exp-4
  op-doc held constant. Primary metrics now event-derived from `context_attribution`: per-brief
  `slope_ratio` and `total_cache_read` (expect down on the two treated S briefs `01 vite-scaffold`,
  `04 my-responses`; M/L unchanged - they are an internal within-run control).
- KILL the L2 branch (keep telemetry) per plan s6 if, on ANY brief, the treatment arm shows turn count
  rising, any `rework_rounds > 0`, or a review-quality drop - even if cache_read/turn fell.
- n=1 caveat carries with force: brief-to-brief variance swamped a single change in runs 1-3; run the
  arms 3-5x and compare distributions before attributing any saving.

Measurement detail to point at: plan s7 (ablation + honest ceiling) and s6 (kill condition).
