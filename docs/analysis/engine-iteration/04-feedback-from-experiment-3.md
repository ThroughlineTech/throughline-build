# Plan - exp-4: flatten the per-turn context ramp (session context hygiene)

Deliberation-side plan; the implementing/investigation agent grounds every claim at a SHA and may override
(the established workflow). This targets the cost that front-loading could never touch: the dominant bill is
`cache_read`, and per the exp-3 transcripts `cache_read`/turn **grows 1.8-2.7x within a single session**
(35k/turn early -> 60-102k/turn late). Total `cache_read = sum_turns context_size(turn)`. Front-loading
attacked *turns* (discovery collapsed; banked). exp-4 attacks *context_size per turn*.

Same hard constraints as always: stack-agnostic (no language/extension branching in engine C#); never weaken
review/rework; one variable per experiment; the metric is behavioral, cost ($) is n=1 noise.

---

## 0. What the data says the target IS and ISN'T (read before scoping)

Attribution of per-turn context growth (cache_creation by the turn's tool class, exp-3 transcripts; the
attribution lags the tool_use by ~1 turn, so treat the split as approximate, not exact):

| source | B05 | B08 | reducible? |
|---|---|---|---|
| Read/Grep/Glob (file contents pulled in) | 19% | 52% | partly - preload already cut redundant reads; residual is non-preloaded reads (test exemplars) |
| **Todo/Task (the worker's own planning churn)** | **38%** | 22% | **yes, and some is information-free to drop** (TodoWrite has replace semantics - every prior list version is dead weight) |
| Write/Edit (code the worker produces) | 38% | 21% | barely - this is the actual work |
| Bash / command output | 4% | 4% | irrelevant - quiet-reporter flags would save nothing |

Headline correction: the ramp is fed by accumulated **work product** (files read to understand, code
written) plus **planning overhead** (TodoWrite/Task). It is NOT fed by verbose test/build output. So the
quiet-command idea is dead; the levers are planning-overhead and supersession, not output-trimming.

---

## 1. The gating investigation (decides which levers are even reachable)

Same architectural reality that killed warm batching: **`build` does not own the worker's turn loop.** It
pipes one stdin prompt to a `claude -p` subprocess and reads the result; the Read/Edit/Bash/TodoWrite cycle
and its accumulating context live inside claude-code. So before designing anything, the agent must establish
**what context control claude-code exposes to a spawning parent.** Concretely, find and document:

- Does claude-code honor a **compaction threshold** setting that could fire well below the 200k limit?
  (These sessions never approach the limit, so default auto-compact never triggers - the ramp is entirely
  sub-limit. A low threshold would force mid-session compaction.)
- Do **PostToolUse / PreCompact hooks** exist that can rewrite or drop a tool_result before it enters
  context (e.g., drop a superseded TodoWrite result, cap an oversized output)?
- Can the spawn **restrict the tool set** (`--allowedTools` / disallow TodoWrite or Task) per invocation?
- Are these settable per-spawn by `build` (so they can key on brief effort), or only global?

The answer partitions the levers below into reachable vs not. If claude-code exposes none of these, exp-4
collapses to prompt-side only (L2's instruction half) - say so and stop, rather than inventing a mechanism.

---

## 2. Candidate levers, ranked by confidence and information-safety

- **L1 - supersession pruning (information-free, highest value if reachable).** TodoWrite has *replace*
  semantics: only the latest list is live; every earlier version in context is pure dead weight. Likewise a
  file Read and later re-Read or Edited makes the earlier copy stale. Dropping superseded content removes
  zero live information. This is the clean win - but it depends entirely on section 1 (a hook or compaction that can
  drop superseded results). If reachable, it cuts a chunk of the Todo/Task slice and some Read slice with no
  behavioral risk.
- **L2 - planning-overhead constraint, scoped to focused briefs.** For S-effort, single-area briefs, an
  elaborate todo list and subagent Task are overkill. Lever = (a) a prompt instruction to keep planning
  lightweight for focused briefs, and (b) if section 1 allows, restrict TodoWrite/Task in the spawn for S briefs.
  Keyed on the op-doc's existing S/M/L effort sizing - derived, not a new field. **Do NOT apply to L briefs**
  (B08 is the one that legitimately needs to plan; stripping it will backfire - see section 5).
- **L3 - generic oversized-tool_result cap, failure-preserving (cheap backstop, low ceiling).** Cap any
  single tool_result to head + tail + any lines matching generic failure markers (error/fail/exception),
  so a rare huge output can't dominate. Directly honors the prior `tail`-evicts-failure-detail finding:
  trim must preserve failure lines, never blindly tail. Low ceiling (Bash is 4%), but it's a no-downside
  guardrail and it's where the `tail` lesson lives.

---

## 3. What changes, by layer

### op-doc - minimal to none
No new field. Lean-mode is gated on the **existing** `Effort` (S/M/L) column; the engine reads it to decide
whether a brief gets the constrained planning profile. The op-doc stays a valid control input unchanged.
(Resist adding a `hygiene:` or `lean:` brief label - derive the decision from effort, the same way cohesion
was derived from plan-membership rather than a new field.)

### code / process - the real work, all behind a flag
- **Telemetry first (build this regardless of the levers): per-turn context attribution.** Emit, per ticket,
  the `cache_read`/turn series and a by-tool-class byte attribution (Read/Write/Todo/Task/Bash), so the ramp
  is event-derivable instead of requiring transcript spelunking. This is the instrument that makes the
  dominant cost legible for the first time; it also *measures* exp-4. Stack-agnostic (byte/usage arithmetic).
- **The lever(s) from section 2 that section 1 says are reachable**, all behind a `[project].context_hygiene` flag (off =
  byte-identical to today, for the ablation): the spawn config (compaction threshold / hook / tool
  restriction), keyed on effort for L2.
- Review/rework path untouched. ASCII+LF, no language branching - trims key on byte size and generic failure
  markers, never on extension or tool name.

### prompts build sends to its spawned agents - one instruction, effort-gated
Add a context-hygiene line to the implement template, applied for S/focused briefs: keep planning
lightweight (don't maintain an elaborate evolving todo list for a single-area change); don't re-Read files
already in the Pre-loaded context; prefer targeted reads over reading whole large files. This is prompt
data, stack-agnostic. Do NOT add it for L briefs.

---

## 4. How we measure it

Ablation: same binary, `context_hygiene` off (control) vs on (treatment), **same exp-3 op-doc** (held
constant). Primary metric from the new telemetry:
- per-turn `cache_read` **slope** (last-5-turn avg / first-3-turn avg) and **total `cache_read`/brief** -
  treatment should flatten the slope and lower the total.
Guard metrics (the backfire watch - see section 5), all must hold flat or improve:
- **turn count per brief** (must NOT rise),
- **rework_rounds** (must stay 0) and review pass (no quality regression).

Predictions: slope and total `cache_read` down on the briefs that get lean-mode (the S/focused ones);
**B08-class L briefs roughly unchanged** (their context is mostly intrinsic work product - read to
understand, code to write - and they keep full planning). Report per-brief, not just aggregate.

---

## 5. The backfire risk (this is the kill condition, not a footnote)

`cache_read = context_per_turn x turns`. Every lever here cuts the first term; the danger is it inflates the
second. Strip a worker's todo list or a tool and it may flail - re-deriving state it would have tracked,
re-running to re-see output a cap hid, taking more turns and possibly more rework. That trades a context
win for a turn loss and can net negative, exactly the trap to avoid. Mitigations baked in: scope lean-mode
to S briefs only (never the complex ones that need to plan); failure-preserving caps (never blind tail);
and the guard metrics above. **KILL exp-4 if** turns rise, rework appears, or review quality drops on any
brief - even if `cache_read`/turn fell.

---

## 6. Honest ceiling (so nobody oversells this)

The ramp is mostly **intrinsic**: files read to understand the contract and code written to fulfill it.
That floor can't be pruned without losing the work. The clearly-reducible surface is (a) superseded content
- information-free to drop, but reachability gated by section 1 - and (b) planning overhead on focused briefs,
which is real but is largest on the *cheap* briefs and smallest on the *expensive* L briefs where the money
actually is (B08 at $3.44 is 52% Read content + genuine work). So expect a **modest, brief-class-dependent**
win concentrated on the many small briefs, not a step-change on the few costly ones. The telemetry in section 3 is
worth shipping even if every lever turns out small - it's the first time intra-session cost is legible, and
it's what tells you whether front-loading has hit the irreducible floor (in which case the next axis is
fewer/cheaper briefs or wall-clock via parallelism, not token cost).

---

## 7. Scope discipline
- Stack-agnostic on every change: trims key on bytes/usage/generic failure markers; lean-mode keys on the
  effort column (data); no C# branch on language.
- One variable: the `context_hygiene` flag, op-doc held constant. If L2's prompt half and the engine half
  are tested together, note the two sub-variables and prefer toggling the engine lever first.
- Don't touch review/rework, the gate, preload (exp-3, folding), or `Brief` shape.
- `topic:` commits, no AI branding, don't merge/push; re-verify cited lines before editing.

---
prereqs: contamination cleanup done; setup.ts convention edit (cleanup a) + deriver path-resolution (cleanup b) landed before any exp-4 run so they don't confound the read-content slice.