# Project History

Why this project exists, what it replaced, and the decisions whose reasoning is not
visible in the code. This is a narrative record, not a specification: where it disagrees
with the source, the source wins. For current behavior see
[throughline-build-architecture.md](throughline-build-architecture.md) and
[throughline_build_userguide.md](throughline_build_userguide.md).

A note on names: the product is **Throughline Build**, the binary on disk is `build`, the
C# namespace root is `ThroughlineBuild.*`, and the repository is `throughline-build`.
Early historical material may use the retired codename `latticeflow`.

---

## 1. Where it came from

The predecessor was a slash-command workflow: six markdown command files, roughly 1,326
lines and 26-28k tokens, loaded into every invocation of a Claude Code chat session. That
session was simultaneously the orchestrator runtime, the persistent context, the model
dispatcher, and the tool gateway. The same prompt corpus was mirrored to GitHub Copilot
Chat and to Codex by generator scripts with parity guards.

It worked, and it was ruinously expensive. One measured chain of 7 tickets, running on an
Opus orchestrator for about 4 hours of wall clock, consumed roughly 190M cache_read
tokens, 45k input tokens, and 627k output tokens, at a cost of about $258. Around 76% of
that was the persistent session re-reading the static 26k-token prompt corpus on every
single action, because a chat loop was being used as a state machine.

That number is the entire origin of this project. The bet: move the state machine into
deterministic code and call an LLM only where an LLM adds value. The target was a ~10x
cost reduction. It was framed as preparation rather than optimization, on the assumption
that the era of free or subsidized tokens had a limited shelf life.

A secondary driver was vendor neutrality. The old system was bound to whichever agent host
loaded the markdown prompts, and parity was maintained by copying prompt text around. The
replacement abstracts at two seams instead: the LLM API client (`ILlmClient`) and the
worker spawn (`IWorkerAgent`). Plan with one vendor, implement with another, review with a
third.

## 2. The founding principles

These were settled early and have held.

**Three-tier LLM contact.** Every interaction belongs to exactly one tier. *Deterministic
phases* (state transitions, gates, drift checks, conflict scans, ticket writes) live in
code and make no LLM contact at all. *Judgment slots* (is this conflict mergeable, score
this verdict) make a small scoped API call. *Agentic work* (plan, implement, review the
substance of a change) spawns an agent CLI as a subprocess in a worktree and lets it use
its full tool loop.

**Right layer.** State machines belong in code, agentic work belongs in agent CLIs,
judgment belongs in discrete API calls. The workflow shape (Backlog -> Planning -> Ready
-> InProgress -> InReview -> Done) is the interface humans deal with; everything else is
implementation detail.

**Lessons as fixtures, not prose.** Every operational lesson becomes a test fixture or a
typed gate, never a paragraph appended to a prompt. Prose lessons grow forever and cost
tokens forever. Fixture lessons are evaluable, refactorable, and deletable. This principle
is the direct antidote to the 26k-token corpus described above.

**Dogfooding over benchmarking.** A phase ships when it handles five real tickets without
surprising anyone, not when it clears an A/B corpus.

**No daemon.** A single native-AOT binary, invoked, which then exits. Any state that must
survive lives on disk and self-validates.

## 3. Rough timeline

Approximate; reconstructed from doc headers and ticket numbers.

| When | What |
|---|---|
| 2026-05-21 | Architecture written. Plan, implement, review, ship phases specified. |
| late May 2026 | Cross-CLI contract research (TLB-201). Copilot WORKER_RESULT spike (TLB-233). |
| June 2026 | Multi-agent foundation and the Codex, Copilot, and Gemini workers. Decompose. Tree-aware commands. The lifecycle verbs (list, close, defer, reopen, amend). Scaffold. |
| 2026-06-04 | The dirty-main-checkout chain failure that produced the preflight gate. |
| 2026-06-16 | The claude-config predecessor formally retired (TLB-541). |
| July 2026 | Claude Code interactive-hook transport cutover. Ticket relations and metadata work. |

## 4. Decisions worth remembering

Each of these cost something to learn.

**The first cost comparison was invalid and had to be run again.** The CLI required
`ANTHROPIC_API_KEY` at startup, and the worker subprocess inherited it, which flipped Claude
Code out of its OAuth/subscription path into per-token API billing. The old system was
measured on subscription and the new one on API, so the first numbers compared two different
meters. The fix was to stop requiring a key the plan phase never used and to strip it from
the child environment. Every cost figure in this document postdates that fix. The general
form: a subprocess inherits your environment, and an inherited credential silently changes
which meter runs.

**The first honest quality comparison was worse than the cost comparison.** An early dogfood
put the new pipeline at roughly 13x cheaper on planning, and a side-by-side read of the
output showed part of why. The old `/ticket-investigate` produced an investigation with
file-and-line specifics, a proposed solution with rationale, escalation rules, out-of-scope
boundaries, and self-applied subtract and rubber-duck passes. The new pipeline produced a
checklist restating the ticket's acceptance criteria as steps. Some of the saving was doing
less work. The root cause was the brief, not the architecture: `PlanBriefBuilder` asked for
"an implementation plan plus risk and size assessment" and nothing more, where the old prompt
corpus asked for all of it by default. This is why brief templates were moved out of C#
string interpolation into embedded markdown - a prompt that must be recompiled to change is a
prompt that does not get changed. Any cost ratio quoted for this system is conditional on the
briefs being as demanding as the thing being replaced.

**Cold starts have a measured price, and it is most of the bill.** A four-ticket chain over
one conceptual feature (model and registry, two sibling renderers, then wiring) ran a fresh
implement worker per ticket. Each cold worker re-read the same growing implementation context
from scratch: the last child alone read 2.4M cache tokens on top of three prior commits, the
run logged about 7.9M cache-read tokens in total with roughly 91% of cost in implement, and it
paid for five separate review passes over one feature. Stateless workers are what make the
architecture cheap in general and expensive in exactly this case. The isolation that earns its
keep is the per-ticket commit boundary, state transition, and reviewable history; the
expensive part is repeatedly re-priming a worker that needs the same design in memory. Hence
batch-implement as an opt-in path for cohesive groups rather than a change to the default.

**Advisory checks are not gating checks.** `build`, `test`, and `typecheck` hard-fail the
gate; `lint` and `format` never do; anything ambiguous defaults to advisory. The asymmetry
is the whole argument: a false advisory is nearly free, while a false gating check burns a
cold rework loop on a cosmetic auto-fixable violation. This was found by root-causing a
Swift/iOS project whose chains kept dying in the gate on style nits while the
implementer's own build and tests passed.

**The chain refuses a dirty main checkout before doing any work.** A four-ticket chain
once spent about 42 minutes, got every ticket to a passing review verdict, and then had
all four stop at ship, because the main checkout had tracked modifications from the very
first second. The gate now emits `chain_preflight_dirty` and refuses before any ticket
transitions state or spawns a worker. It blocks on tracked changes only and deliberately
ignores untracked files, because that mirrors the actual downstream blocker in `ShipPhase`
rather than inventing a stricter policy of its own.

**WORKER_RESULT is a bare marker, not a fenced code block.** The original op-doc specified
a fenced block; the implementation chose a bare `WORKER_RESULT` line followed by JSON, and
the implementation became canonical. The parser is deliberately more lenient than the
contract: fences are stripped if present, the last valid envelope wins, and content after
the closing brace is ignored, because some models narrate a sign-off after emitting the
envelope and that must not void an otherwise valid result.

**Large payloads move in named fenced blocks.** The `<<<NAME_START` / `<<<NAME_END`
protocol exists because large content with unescaped quotes (shell snippets, embedded code
blocks) kept breaking JSON string escaping. `<<<` was chosen because it collides with
neither markdown nor HTML syntax. Decompose is the deliberate exception and keeps its
`child_specs` as a JSON array: its per-child fields are bounded prose with minimal escape
risk, and the phase needs typed per-child access to create tickets in a loop. The general
rule that came out of it: migrate a field to a fenced block only when it carries
multi-paragraph narrative, diffs, or command listings.

**Only Claude Code reports a dollar cost.** Codex and Gemini emit token counts but no USD.
Copilot bills against a premium-request quota and, in the silent mode required for clean
envelope capture, exposes no usage data at all. Hence `cost_usd` is nullable throughout and
the pricing table is token-based per vendor.

**Auth posture splits two ways.** Codex and Gemini follow the Claude Code pattern: strip
the API key from the child process so authentication falls through to the subscription or
OAuth path. Copilot is the inverse and requires a token to be explicitly set. This lives in
per-agent code and was never allowed to leak into the shared contract.

**Per-tool permissions do not port.** Only Copilot has a Claude-like per-tool allowlist
vocabulary. Codex and Gemini gate by sandbox or approval mode instead. `AllowedTools`
therefore stayed a per-agent option rather than becoming part of the worker contract.

**Debug capture is pure observation.** Everything written under `--debug` happens after the
worker exits, reads only the already-captured stream, and writes only to disk. The worker
runs byte-for-byte identically with or without it. This was a hard guarantee from the
start, because the alternative is instrumentation that perturbs the exact thing being
measured. Every writer is best-effort and swallows its own failures for the same reason.

**ASCII only at tool boundaries.** Git Bash plus curl.exe mangles non-ASCII bytes on
round-trip through MSYS code-page translation: a valid UTF-8 em-dash comes back as `0x97`,
which Plane's REST API rejects outright. Inherited as a scar from the predecessor system.

**Reopen does not cascade, but close and defer do.** Closing or deferring a parent cascades
to its non-terminal children. Reopening does not, because children may carry their own
branches, plans, and in-flight work that an automatic reopen would invalidate. The operator
reopens children deliberately.

**Op-docs are implementation plans, not sketches, because nothing re-plans them.** A
scaffolded brief-ticket that still routed through the plan phase would spawn a worker to
re-investigate work the op-doc already described. That costs tokens and leaks fidelity: the
worker can plan differently than the op-doc deliberately intended. The chain therefore
promotes scaffolded tickets straight to Ready and implements them directly, which is what
makes the format spec's demand for implementation-ready briefs load-bearing rather than
stylistic. A thin brief is not one that gets fleshed out later; it is one that gets
implemented thin.

**The operator's apparent lever was inert.** A chain run logged `model claude-opus-4-7` when
the operator expected Sonnet. `[llm] default_model`, the obvious knob, does not drive worker
model selection at all - it is read only by the close/defer/reopen reason translator. The real
path is ticket size to `WorkerSize` to the `[workers.<agent>.sizes]` map, and that map carried
a stale model string baked into the init template, duplicated across three files, with dead
in-code default maps behind it that silently absorbed a missing block instead of failing. The
fix was structural rather than a string edit: stable tier aliases where the vendor offers them,
a discovery probe where it does not, and no silent fallback. A config key that looks like it
controls something and does not is worse than one that is missing.

**The agents were sequenced easy to hard on purpose.** Codex first, because it is closest in
shape to Claude Code and would prove the foundation cheaply; then Gemini, the cleanest
structured output of the three; then Copilot last, flagged in advance as the awkward fit - no
structured JSON output, inverted auth, weak usage reporting, and unverified envelope survival
in its quiet mode. By the time Copilot was built, two working agents and a shared contract
test base existed to diff against, so every accommodation it needed landed against a proven
pattern instead of setting one. Its op-doc opened with a gating spike for that reason.

## 5. Built, then removed

**In-chain parallelism.** Multi-ticket dispatch was built to run independent tickets
concurrently, with a main-worktree mutex and a divergence probe making the shared main
checkout safe under concurrent ship operations. It worked, and it was then removed on purpose.
For a solo operator paying per token, wall-clock is not the binding constraint, and parallel
siblings duplicate investigation across tickets that share a code region. Sequential dispatch
also pays a dividend the parallel version could not: once each ticket ships before the next
implements, the prior commits are already in the next worker's checkout, so the handoff
between tickets becomes a deterministic pointer to a commit range rather than a worker-authored
prose digest. It is also the assumption the deterministic gate rests on, since a per-ticket
worktree only counts as an integrated tree while chains stack sequentially. `MainWorktreeLock`
and the ship-divergence auto-resolve survive in the source as residue of that period; they
remain load-bearing for concurrent invocations of the binary, not for parallelism inside one
chain.

**Retrospective handoff docs.** After two ops, a convention of reconstructing what actually
shipped against what the op-doc specified - picked versus rejected design decisions,
surprise files, doc drift - was tried and dropped. Both attempts were written after the fact
from diffs rather than captured during the run, which is most of why it did not survive: the
reconstruction cost was real and the output was stale on arrival. The durable version of that
impulse is the state-of-the-system doc set, which is regenerated against a named HEAD rather
than narrated per operation.

## 6. Considered and not built

Each of these was investigated in enough depth to be actionable, then not started. Recorded
so the next person does not redo the analysis.

**Linear as a second ticketing backend.** Judged feasible and architecturally clean but not
a drop-in. `ITicketing` is the only surface the commands and phases touch, so the wiring is
one adapter class plus a backend factory. The real work is four impedance mismatches:
GraphQL instead of REST, Markdown instead of HTML for all descriptions and comments,
hardcoded workflow-state names that would need to become config-driven, and bot identity.
Estimated at under a week.

**Partial test selection.** Run only the tests a change actually touches during the
review/rework loop, and reserve the full suite for ship. Two walls stopped it: mapping a
changed source file to its covering tests is inherently toolchain-specific, and so is
turning a selected file set into a runner invocation (pytest and go take paths, `dotnet
test` needs `--filter`). Neither can live in a stack-agnostic core. The leading design was
an agent-emitted test command validated against an executable allowlist, with the full
suite at ship as an unconditional backstop, so under-selection could never ship a
regression.

**A plan-phase repo index.** Give the planner a deterministic symbol index so it stops
re-discovering the codebase from scratch on every ticket. The useful framing was splitting
"where is symbol X and who calls it" (exact, solvable with Roslyn or any LSP, cheap) from
semantic retrieval (embeddings, heavier, deferrable). The honest expected payoff was
latency and plan consistency rather than tokens, since plan cost is reasoning-dominated.
Never prototyped.

**RTK command-output compression.** Treat it as a worker-runtime treatment applied at the
launch boundary, transparently via Claude Code's `PreToolUse` hook so the brief stays
byte-identical, and measure it with paired A/B runs. Never started.

## 7. Retired

**claude-config.** The `/ticket-*` slash commands, the `.claude/plane-rest` REST layer, the
`.claude/plane-config.md` ID maps, and the Plane MCP server. Removed from this repository on
2026-06-16 under TLB-541. The cutover was deliberately per-command and atomic with the
matching `build` verb, with no dual-truth window in which both paths worked.
