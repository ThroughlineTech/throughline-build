# Plan - exp-4: per-turn context attribution telemetry + effort-gated planning hygiene

Planner deliverable for the feedback note `04-feedback-from-experiment-3.md`. Every surface below
is cited at the current main SHA; the implementing/investigation agent grounds each claim at its own SHA and
may override a citation that has moved (re-verify before editing - the established workflow). This plan is
detailed enough that the implementer makes only mechanical decisions.

Branch: `exp-4-context-hygiene` (cut from `main`, clean tree). One atomic diff. Decision unit = fold or abandon.

---

## 0. What this experiment is, after the gating investigation resolved

The feedback note's section 1 made one question the gate: **what context control does claude-code expose to a
spawning parent?** That answer partitions the candidate levers (L1 supersession pruning, L2 planning-overhead
constraint, L3 oversized-output cap) into reachable vs not. The investigation is now done, grounded two ways:
(a) the C# spawn path, and (b) an authoritative claude-code capability check. Result:

| Lever (from feedback s2) | Reachable from a spawning parent? | Why |
|---|---|---|
| **Telemetry** (per-turn attribution) | **YES - build regardless** | per-turn `message.usage` and per-turn `tool_use` names are already in the stream we capture; only the `--debug` path parses them today |
| **L1** supersession pruning | **NO - killed** | `build` pipes one stdin prompt and reads the result; it does NOT own the worker's turn loop. claude-code exposes no way for a parent to drop/rewrite already-accumulated context: no sub-limit compaction setting, and hooks (PreCompact/PostToolUse) can only ADD context or BLOCK actions, never retroactively prune the transcript already in the window. |
| **L2** planning-overhead constraint | **YES (both halves)** | (a) prompt instruction is brief text, always reachable; (b) tool restriction is reachable per-spawn via `--disallowedTools` (the spawn already varies args per brief) |
| **L3** oversized tool_result cap | **NO as an engine mechanism** | same root as L1: the parent cannot intercept or truncate the child's tool_results; they live inside claude-code. (Survives only as weak prompt advice, folded into L2a.) |

So exp-4 is exactly two things, no more:

1. **The instrument (always-on, behavior-inert): per-turn context-attribution telemetry.** Emit, per implement
   phase, the `cache_read`/turn series and a by-tool-class byte attribution, so the dominant cost (the
   intra-session `cache_read` ramp) is event-derivable for the first time instead of requiring transcript
   spelunking. This is worth shipping even if every lever turns out small - it is the first time intra-session
   cost is legible, and it is what tells us whether front-loading has hit the irreducible floor.

2. **One behavioral lever (behind a flag, off by default): L2 - effort-gated planning hygiene for S briefs.**
   (a) a prompt-instruction line and (b) a tool-set restriction (`--disallowedTools TodoWrite,Task`), both
   applied ONLY to S-effort briefs, both gated on `[project].context_hygiene`. Off = byte-identical behavior.

This is one notch better than the feedback's anticipated worst case ("if claude-code exposes none of these,
exp-4 collapses to prompt-side only"): L2b (tool restriction) is also reachable, so we get one real engine
lever, not just the prompt line. But the headline ceiling stands (section 7 below): the ramp is mostly
intrinsic work product, and lean-mode touches only the cheap S briefs, so expect a modest, brief-class-dependent
result. The telemetry is the durable deliverable.

---

## 1. The target the data points at (why telemetry is the spine)

Per the exp-3 transcripts the dominant bill is `cache_read`, and `cache_read`/turn grows 1.8-2.7x within a
single session (35k/turn early to 60-102k/turn late). `total cache_read = sum over turns of context_size(turn)`.
Front-loading (exp-2/3) attacked the number of turns; exp-4 makes context_size/turn legible and nibbles at the
reducible part of it. The feedback's attribution split (Read/Grep ~19-52%, Todo/Task ~22-38%, Write/Edit
~21-38%, Bash ~4%) was built by hand from transcripts and is explicitly approximate (the cache_creation
attribution lags the tool_use by ~1 turn). The point of deliverable (1) is to stop spelunking: emit the series
and the per-tool-class split as a structured event so every future run is comparable from `.build/events/*.jsonl`.

---

## 2. Architecture reality that shapes the fix (read before editing)

- **The spawn is parent-owned and rebuilt per invocation.** `ClaudeCodeAgent.BuildArgs`
  ([ClaudeCodeAgent.cs:437-451](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L437-L451))
  assembles the argv fresh each call: `--print --verbose --output-format stream-json`, then
  `--dangerously-skip-permissions`, then `--allowedTools <list>` (only when `WorkerOptions.AllowedTools` is
  non-empty - [:442-443](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L442-L443)), then
  `--model <m>` selected from `ticket.Size`
  ([:444-447](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L444-L447)). The review phase
  already passes `--allowedTools Read,Grep,Glob`; implement leaves it null. So **per-brief arg variation, keyed
  on size, already exists** - L2b is one more `args.AddRange`, not new plumbing.
- **The brief goes via stdin** ([:126-127](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L126-L127)),
  and the full NDJSON stream is accumulated into `stdoutBuilder`
  ([:77](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L77)). The terminal `type=result`
  envelope is parsed for the final cumulative usage (`BuildLlmUsageMetadata`,
  [:390-419](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L390-L419)). **Per-turn
  `message.usage` and per-turn `tool_use` blocks are present in that same stdout** but parsed only by the
  `--debug` `WorkerTranscriptWriter` (`AccumulateAssistant` / `ReadUsage`, ~`:206-263` / `:494-500`), never on
  the production path.
- **`WorkerResult.Metadata` is an open `IReadOnlyDictionary<string,object>`**
  ([WorkerResult.cs:3-10](../../../src/ThroughlineBuild.Contracts/Models/WorkerResult.cs#L3-L10)) - the existing
  channel for `llm_usage`. A new `context_turns` key rides the same channel, no contract break.
- **The phase emits advisory CostLedger events from a dict.** `ImplementPhase.BuildAndReportPreloadAsync`
  ([ImplementPhase.cs:612-656](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L612-L656)) emits
  `EventKind.CostLedger` with a `"kind"` string discriminator (`preload_summary`,
  [:622-632](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L622-L632)) and proceeds. This is the exact
  pattern for the new `context_attribution` event. `EmitAsync` hard-codes `Phase.Implement`
  ([:590-598](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L590-L598)).
- **The flag pattern is `preload_context`.** `ProjectContext.PreloadContext { get; init; } = true;`
  ([ProjectContext.cs:25-28](../../../src/ThroughlineBuild.Briefs/ProjectContext.cs#L25-L28)), parsed via
  `OptionalBool(t, "preload_context", true)` ([Config.cs:907](../../../src/ThroughlineBuild.Cli/Config.cs#L907)),
  registered in `KnownProjectKeys` ([Config.cs:266-271](../../../src/ThroughlineBuild.Cli/Config.cs#L266-L271)),
  documented in the config template, threaded to `ImplementPhase` via `config2.Project` (implement verb
  [Program.cs:1450](../../../src/ThroughlineBuild.Cli/Program.cs#L1450); chain factory
  [Program.cs:1753-1754](../../../src/ThroughlineBuild.Cli/Program.cs#L1753-L1754)). `context_hygiene` mirrors
  this exactly, with the one difference that its default is **false** (opt-in, see s3).
- **Effort flows S/M/L -> ticket label -> `Ticket.Size` -> `WorkerSize`.** Op-doc `Effort` column ->
  `size:s/m/l` label (`ScaffoldPhase.EffortToSizeLabel`,
  [:446-458](../../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L446-L458)) -> `Ticket.Size` at fetch ->
  `WorkerSizeMapper.FromTicketSize(ticket.Size)`
  ([WorkerSizeMapper.cs:7-12](../../../src/ThroughlineBuild.Helpers/WorkerSizeMapper.cs#L7-L12)). In
  `ImplementPhase.RunAsync`, `ticket.Size`, `_project`, the brief build, and the worker spawn all coincide at
  [:327-350](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L327-L350). **So an effort-gated decision is
  trivially makeable there.** No new field on `Brief` - the size is already in scope.
- **AOT discipline.** `Cli` is `PublishAot=true`; serialized event payloads use source-gen `EventLogJsonContext`
  ([EventLogJsonContext.cs:6-18](../../../src/ThroughlineBuild.EventLog/EventLogJsonContext.cs#L6-L18)). It
  registers scalars + `List<string>` but **not `List<long>`** - the new series payload needs that one line added.
- **The template has no conditionals** - `{{token}}` substitution only, missing key throws
  (`TemplateExtensions`). Conditional content is done in C# by passing a real string or `""`, exactly like
  `{{preloaded_context_section}}` ([implement.md:20](../../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L20)).
  Templates are snapshot-tested under `tests/ThroughlineBuild.Briefs.Tests/Snapshots/`, pinned `eol=lf`.

---

## 3. The design, by layer (stack-agnostic throughout)

Stack-agnostic compliance is checked on every change: the telemetry is pure byte/usage arithmetic; lean-mode
keys on the `Effort` column (data), never on language/extension; the only tool-name literals
(`TodoWrite`,`Task`) live in the claude-code worker adapter where vendor tool names already live (review's
`Read,Grep,Glob`, Copilot's `--allow-tool`), NOT in engine phase code. No `if (language == ...)` anywhere.

### 3a. op-doc: NO change

Lean-mode gates on the existing `Effort` (S/M/L) column. No `hygiene:`/`lean:` brief label (same discipline that
derived preload from the brief rather than a new engine field). The exp-4 op-doc
(`workloads/survey-experiment-3-and-4.md`, 466 lines, already present) is the held-constant control - it keeps
the exp-3 Preload blocks so preload stays ON and constant and the only variable is `context_hygiene`. Do NOT
edit the op-doc.

### 3b. Config flag: `[project].context_hygiene` (bool, default false)

Mirror `preload_context` mechanically:
- `ProjectContext`: add `public bool ContextHygiene { get; init; } = false;` (init-only, so existing positional
  `new ProjectContext(...)` call sites compile unchanged), beside `PreloadContext`
  ([ProjectContext.cs:25-28](../../../src/ThroughlineBuild.Briefs/ProjectContext.cs#L25-L28)). Default **false**:
  this is an unproven, backfire-risky lever, so it is opt-in; off must equal today, which is what an ablation
  control arm needs and what makes a telemetry-only fold to main safe.
- `Config.cs`: `var contextHygiene = OptionalBool(t, "context_hygiene", false);` near
  [:907](../../../src/ThroughlineBuild.Cli/Config.cs#L907); assign `ContextHygiene = contextHygiene` on the
  returned `ProjectContext`; add `"context_hygiene"` to `KnownProjectKeys`
  ([:266-271](../../../src/ThroughlineBuild.Cli/Config.cs#L266-L271)).
- Config template (`src/ThroughlineBuild.Commands/Templates/config.toml.template`): add a commented
  `# context_hygiene = false` line beside the `# preload_context` line, with a one-line doc.
- Do NOT add it to the live repo `.build/config.toml` (that governs THIS engine's own builds; leave it default-off).

### 3c. Telemetry (always-on, NOT behind the flag) - the spine

**Worker side** (`ClaudeCodeAgent`, where the stream lives):
- Add a single post-exit pass over the accumulated NDJSON (`stdoutBuilder`) that, per `assistant` event:
  - reads `message.usage` once per message (it repeats across the lines of one message - dedup as
    `WorkerTranscriptWriter.AccumulateAssistant` already does), capturing `cache_read_input_tokens`,
    `cache_creation_input_tokens`, `output_tokens`;
  - reads the `content[]` blocks of type `tool_use` for their `name`, mapping each to a tool class via a generic
    classifier: `read` = {Read,Grep,Glob,NotebookRead,LS}, `write` = {Write,Edit,MultiEdit,NotebookEdit},
    `todo` = {TodoWrite}, `task` = {Task}, `bash` = {Bash}, else `other`.
  - **Reuse, do not duplicate, the JSON-shape knowledge.** Extract the per-turn usage/tool parse that
    `WorkerTranscriptWriter` already implements into a small shared internal helper (e.g.
    `ClaudeCodeTurnParser.Parse(stdout) -> ContextTurnSeries`) and call it from BOTH the debug transcript and
    this production pass. One parser, one source of truth for the stream shape.
- Build a compact result and stash it on `WorkerResult.Metadata["context_turns"]` as a `Dictionary<string,object>`
  of FLAT scalar + `List<long>` values (a flat shape so AOT needs only one new registration, no nested dict):
  - `cache_read_series` : `List<long>` (one entry per assistant turn)
  - `cache_creation_series` : `List<long>`
  - `output_series` : `List<long>`
  - `turns` : `int`
  - byte attribution (cache_creation delta of each turn assigned to that turn's tool class(es); the same
    ~1-turn-lagged proxy the feedback's own table used - documented as approximate in the event):
    `read_bytes`, `write_bytes`, `todo_bytes`, `task_bytes`, `bash_bytes`, `other_bytes` (each `long`).
- This is claude-code-stream-specific parsing living in the claude-code adapter - the correct layer (mirrors
  `BuildLlmUsageMetadata`). Other workers can populate `context_turns` later from their own streams or omit it.
- Behavior-inert: it reads already-captured stdout after the process exits; it adds no flag, changes no prompt,
  spawns nothing. The non-debug fast path keeps its zero-overhead live handler
  ([:73-92](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L73-L92)); the parse runs once at
  the end, like `TryParseEnvelopeFromStdout`.

**Phase side** (`ImplementPhase`, after the Step 13 LlmCall emit at
[:358-370](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L358-L370)):
- If `workerResult.Metadata` has `context_turns`, emit one advisory CostLedger via the existing `EmitAsync`
  helper (same pattern as `preload_summary`):
  ```
  EmitAsync(EventKind.CostLedger, ticketId, {
    "kind": "context_attribution",
    "turns": <int>,
    "cache_read_series": <List<long>>,
    "cache_creation_series": <List<long>>,
    "total_cache_read": <sum>,
    "slope_ratio": <last-5-turn avg cache_read / first-3-turn avg cache_read; double; emit -1 if <8 turns>,
    "read_bytes","write_bytes","todo_bytes","task_bytes","bash_bytes","other_bytes": <long each>,
    "attribution_note": "cache_creation lags tool_use ~1 turn; per-class split is approximate"
  })
  ```
  Advisory only (emit and proceed; the chain's hard-fail is a phase return value, never the event stream).
- This makes the ramp slope + total + per-class split derivable straight from `.build/events/*.jsonl`. It runs
  for EVERY brief regardless of effort or flag - so a single run yields per-turn ramps for all 8 briefs.

**AOT** (`EventLogJsonContext`): add `[JsonSerializable(typeof(List<long>))]`
([EventLogJsonContext.cs:6-18](../../../src/ThroughlineBuild.EventLog/EventLogJsonContext.cs#L6-L18)) so a boxed
`List<long>` inside the `Dictionary<string,object>` Data serializes the same way the existing boxed `List<string>`
in `preload_summary` does. Verify with a JsonlEventSink round-trip test (s5).

### 3d. L2 lever (behind `context_hygiene`, S-effort only) - the one behavioral variable

Gate condition everywhere: `lean = (_project.ContextHygiene && ticket.Size == Size.S)`. Never M, never L (B08-class
L briefs legitimately need to plan; stripping them backfires - see s6). The phase computes `lean` once where
`ticket.Size` and `_project` are both in scope (~[:327-350](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L327-L350)).

**L2a - prompt instruction (prompt data, stack-agnostic):**
- Add an optional `{{context_hygiene_section}}` token to `implement.md`, placed at the end of the `## Constraints`
  block ([:26-32](../../../src/ThroughlineBuild.Briefs/Templates/claude-code/implement.md#L26-L32)), rendered by
  the SAME newline-ownership convention as `{{preloaded_context_section}}` so the empty case reproduces an
  already-blessed shape (section string is `""` when off, or a leading-newline block when on).
- Populate it (a non-empty string) only when `lean`; else `""`. Compute the section in `ImplementBriefBuilder.Build`
  (it already receives `ticket` and `_project`, and renders `["size"]` at
  [ImplementBriefBuilder.cs:41](../../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L41) and
  `["preloaded_context_section"]` at
  [:54](../../../src/ThroughlineBuild.Briefs/ImplementBriefBuilder.cs#L54)) - no I/O needed, so no need to lift it
  into the phase like preload.
- Text (ASCII, stack-agnostic - no language/extension/tool-name words; robust whether or not preload ran):
  ```
  - Planning hygiene (this is a small, single-area brief): keep planning lightweight. Do not maintain an
    elaborate, continuously-rewritten task list for a change this focused. Do not re-read files whose contents
    are already provided to you above. Prefer targeted reads of the specific symbols you need over reading whole
    large files.
  ```
- Snapshots: adding the token changes the three implement snapshots (`implement-original.txt`,
  `implement-rework.txt`, `implement-gate-rework.txt`) by the empty-token render - update them deliberately as
  LF, per `Templates/AGENTS.md`. Add ONE new fixture/test for the `lean` case asserting the hygiene bullet
  appears for an S+flag-on ticket and is absent for M, L, and flag-off (extend
  `Build_NoPreloadedSection_InstructionUnchangedFromBaseline` style at
  [ImplementBriefBuilderTests.cs:212](../../../tests/ThroughlineBuild.Briefs.Tests/ImplementBriefBuilderTests.cs#L212)).

**L2b - tool restriction (engine intent -> adapter maps to its tool names):**
- Add a generic intent flag to `WorkerOptions`: `bool LeanPlanning = false` (after `AllowedTools`,
  [IWorkerAgent.cs:47-56](../../../src/ThroughlineBuild.Contracts/IWorkerAgent.cs#L47-L56)). The phase expresses
  intent; it names no tools.
- In `ImplementPhase` Step 11 ([:338-347](../../../src/ThroughlineBuild.Phases/ImplementPhase.cs#L338-L347)), set
  `LeanPlanning: lean` on the `WorkerOptions`.
- In `ClaudeCodeAgent.BuildArgs`
  ([:437-451](../../../src/ThroughlineBuild.Workers.ClaudeCode/ClaudeCodeAgent.cs#L437-L451)), after the
  AllowedTools block, map intent to claude-code tool names (the ONLY place these literals live):
  ```
  if (workerOptions.LeanPlanning)
      args.AddRange(new[] { "--disallowedTools", "TodoWrite,Task" });
  ```
  `--disallowedTools` with bare tool names removes those tools from the child's context (confirmed mechanism;
  `--allowedTools` only auto-approves and would NOT disable, so disallow is the correct flag).
- **Empirical gate (the implementer MUST do this before crediting L2b).** The claude-code capability check got
  two unrelated facts wrong (it claimed TodoWrite/Task "do not exist" - they plainly do; the codebase classifies
  `Task` as a discovery tool and TodoWrite has the replace semantics the feedback measured). Treat its flag
  claims as plausible-not-proven: on the pinned `claude` version, run one implement spawn with
  `--disallowedTools TodoWrite,Task` and confirm from the stream that no `tool_use` with `name` in {TodoWrite,
  Task} appears (and that the brief still completes). If the flag does NOT remove the tools, drop L2b, keep L2a +
  telemetry, and record the finding - do not ship a no-op lever (the exp-2 lesson: a mechanism that silently
  does nothing is the failure mode this program exists to prevent).

---

## 4. Scope boundary (what is deliberately NOT here)

- **No L1, no L3.** Both unreachable from a spawning parent (s0). Do not attempt a hook, a settings file, a
  `--max-turns` cap (it exits non-zero on hit - would fail briefs), or any parent-side context pruning.
- **Lean-mode is S-only.** No M, no L. (The biggest Todo/Task slice in the feedback sat on an M brief, B05; we
  still do not touch M in exp-4 - prove the mechanism is safe on the cheap S briefs first; the always-on
  telemetry will quantify M's headroom for a possible later widening.)
- **Do not touch** review/rework, the gate, preload (exp-3, folding), `Brief` shape, `MaxReworkRounds`, or model
  selection. Telemetry adds a Metadata key and `WorkerOptions.LeanPlanning`; neither changes `Brief`.
- **Do not edit the op-doc** (the control) or the live repo `.build/config.toml`.

---

## 5. Test strategy (must be green before handoff)

- **Config**: mirror `ConfigLoaderTests` preload trio
  ([ConfigLoaderTests.cs:1619-1660](../../../tests/ThroughlineBuild.Cli.Tests/ConfigLoaderTests.cs#L1619-L1660)):
  `context_hygiene` defaults false when absent; parses true; not warned as unknown key.
- **Telemetry parser (stack-agnostic - this is the leak-proof test)**: feed `ClaudeCodeTurnParser` a small fixed
  NDJSON fixture of 3-4 `assistant` events with `message.usage` and mixed `tool_use` names, assert the series
  values, `turns`, and per-class byte buckets. The fixture is pure stream JSON - no language, extension, or
  stack token anywhere - proving no single-stack assumption leaked. Add a `slope_ratio` arithmetic test
  (including the `<8 turns -> -1` guard).
- **AOT round-trip**: serialize a `WorkflowEvent` whose Data carries the `context_attribution` shape (with a
  `List<long>` series) through `JsonlEventSink` / `EventLogJsonContext.Default` and assert it round-trips - this
  is the AOT regression guard for the new `List<long>` registration.
- **Phase emit**: an `ImplementPhase` test asserting that when `workerResult.Metadata["context_turns"]` is
  present, a `CostLedger` event with `kind == "context_attribution"` is emitted; and (separately) that
  `LeanPlanning` is set true only for `Size.S` with `ContextHygiene` on, false otherwise (mirror the preload-off
  test scaffolding at [ImplementPhaseTests.cs:636](../../../tests/ThroughlineBuild.Phases.Tests/ImplementPhaseTests.cs#L636)).
- **BuildArgs**: assert `--disallowedTools TodoWrite,Task` is appended iff `WorkerOptions.LeanPlanning`, and
  absent otherwise (keeps the off-path argv byte-identical).
- **Brief builder snapshots**: update the 3 implement snapshots (LF); add the lean-case assertion (s3d L2a).
- Full suite green: `dotnet test --nologo -v q --logger "console;verbosity=minimal"`. Briefs snapshot updates
  are deliberate.

---

## 6. The kill condition (not a footnote)

`cache_read = context_per_turn x turns`. Every lever cuts the first term; the danger is inflating the second.
Strip a worker's todo list / Task tool and it may need to re-derive state, take more turns, and possibly use more
rework - trading a context win for a turn loss and netting negative. Mitigations are baked in: S-only scope,
telemetry-measured, opt-in flag. **KILL exp-4 (abandon the L2 branch, keep telemetry) if, on any brief, the
treatment arm shows:** turn count per brief rises, any `rework_rounds > 0` appears, or review quality drops -
even if `cache_read`/turn fell. Watch the two treated S briefs specifically (`01 vite-scaffold` creates ~10
files and is the likeliest to want a task list; `04 my-responses`).

---

## 7. Measurement (how we tell it worked) + honest ceiling

Ablation: same exp-4 binary, `context_hygiene` off (control) vs on (treatment), same exp-4 op-doc held constant
(preload stays on in both arms). Primary metrics, now event-derived from `context_attribution`:
- per-brief `slope_ratio` and `total_cache_read` - treatment should flatten/lower them on the treated S briefs.
- **Within-run control (the elegant part):** lean-mode fires only on S briefs, but the telemetry runs on ALL
  briefs - so in a single treatment run the untreated M/L briefs are an internal control for the S briefs, on
  top of the cross-arm off/on comparison.

Guard metrics (the s6 backfire watch - all must hold flat or improve): turn count per brief (must NOT rise),
`rework_rounds` (must stay 0), verifier verdicts (8 Pass, no quality regression on the conditional-logic L brief).

Predictions: slope/total down on the S briefs; M/L roughly unchanged (intrinsic work product they keep planning
for). Report per-brief, not just aggregate. **n=1 caveat (carried, with force):** Runs 1-3 showed brief-to-brief
sampling variance of tens of percent swamps a single intended change. Do NOT credit lean-mode from one run; to
attribute any saving, run the off and on arms 3-5x and compare distributions. The honest near-term claim exp-4
can make is "the telemetry fires and the ramp is now legible; lean-mode changed S-brief slope by X at n=1, within
the variance band."

Honest ceiling: the ramp is mostly intrinsic (files read to understand the contract, code written to fulfill it)
- that floor cannot be pruned without losing the work. The reducible surface is planning overhead on focused
briefs, which is real but largest on the cheap briefs and smallest on the expensive L briefs where the money is.
Expect a modest, brief-class-dependent win concentrated on the small briefs. The telemetry is the win that lasts:
it is what tells us whether front-loading has hit the irreducible floor (in which case the next axis is
fewer/cheaper briefs or wall-clock via parallelism, not token-per-turn).

Two sub-variables noted (per feedback s7): L2a (prompt) and L2b (tool restriction) ride one flag. The primary
exp-4 comparison is flag-off vs flag-on (both halves). If that bundled result is interesting, isolate by a
follow-up arm that enables only L2b (it is the cleaner engine lever; the prompt half is the softer one). Do not
build a second flag now - one variable, noted compromise.

---

## 8. Implementation order and commit plan (atomic, serial)

Each is one `topic:` commit, ASCII + LF, no AI branding, no merge/push. Re-verify cited lines before editing.

1. `telemetry: parse claude-code per-turn usage + tool-class bytes` - extract the shared `ClaudeCodeTurnParser`
   (reuse the `WorkerTranscriptWriter` shape), populate `WorkerResult.Metadata["context_turns"]`. + parser unit
   tests (the stack-agnostic fixture). No behavior change.
2. `telemetry: emit context_attribution CostLedger from ImplementPhase` - phase emit after Step 13; add
   `List<long>` to `EventLogJsonContext`; AOT round-trip test + phase emit test.
3. `config: add [project].context_hygiene flag (default false)` - `ProjectContext`, `Config.cs`, `KnownProjectKeys`,
   config template, config tests. Threading already exists via `config2.Project`.
4. `hygiene: effort-gated planning-hygiene prompt line (L2a)` - `{{context_hygiene_section}}` token, builder gate
   on `lean`, snapshot updates + lean-case test.
5. `hygiene: effort-gated --disallowedTools for S briefs (L2b)` - `WorkerOptions.LeanPlanning`, phase sets it,
   `BuildArgs` maps it to `--disallowedTools TodoWrite,Task`, BuildArgs test. **Run the empirical flag-gate check
   (s3d) and record the result in the implementation summary** before declaring L2b done.

Then the implementation summary (`04-implementation-summary.md`): branch, commits, files, test counts,
snapshot updates, the L2b empirical-gate result, and the acceptance mapping. Recommend fold/abandon and point at
this section 7 for what to measure.

---

## 9. Prerequisites (confound controls - Dan/run-owner gate, NOT part of this diff)

The feedback's closing line names run preconditions that must land on `main` BEFORE any exp-4 run so they do not
confound the read-content slice of the telemetry: contamination cleanup done; the setup.ts convention edit
("cleanup a") and the deriver path-resolution fix ("cleanup b") landed. These are separate cleanup tickets, not
exp-4 levers - keep them OUT of the exp-4 atomic diff (one-variable discipline). The run owner verifies they are
in before running the arms; if the implementer finds them unlanded, note it and proceed with the code change
(they gate the RUN, not the build). An agent cannot run `build chain` (nested-session guard) - the off/on arms
are a manual Dan step (handoff s3).

---

## 10. Acceptance criteria (verify against observable behavior, not self-report)

1. `[project].context_hygiene` parses (default false; true when set; not an unknown-key warning); off leaves the
   implement argv and brief byte-identical to today.
2. A `CostLedger` event with `kind == "context_attribution"` is emitted per implement phase, carrying the
   `cache_read_series`, the per-tool-class byte buckets, `total_cache_read`, and `slope_ratio` - for every brief,
   independent of the flag. Round-trips under AOT.
3. With the flag on, S briefs (and only S) get the hygiene prompt line AND `--disallowedTools TodoWrite,Task`; M
   and L briefs are untouched; with the flag off, none are.
4. The per-turn parser is proven by a stream-only fixture with zero stack-specific tokens (no language leak).
5. Full `dotnet test` green; Briefs snapshots updated deliberately (LF).
6. L2b's tool-removal effect empirically confirmed on the pinned claude-code, or L2b dropped with the finding
   recorded. No silent no-op ships.
