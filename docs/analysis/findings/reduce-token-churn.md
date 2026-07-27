# Reducing Token Churn

## Turns Aren't Limited by Code-Per-Turn; They're Tool Round-Trips

In the claude-code worker (`claude --print --output-format stream-json`, brief on stdin), a "turn" is one model response, and the count is driven by tool round-trips - every `Read`, `Grep`, `Bash`, and incremental `Edit` forces another turn. The model already writes as much as it wants per turn (up to its output cap).

The `cache_read` increase in `claude-chain2` is consistent with more tool round-trips:
reading files, exploring, and re-verifying. The logs do not expose a direct turn count,
so `cache_read/cache_create` is only an approximate proxy. **The practical hypothesis to
test is reducing unnecessary round-trips, not "verbosity per turn."**

The higher-cache run (`claude-chain2`) passed every ticket first-try; the lower-cache
run (`claud-chain`) needed rework on 9 and 11. That association is compatible with
additional exploration helping quality, but two runs do not establish the tradeoff.

> The goal isn't "fewer turns," it's **"remove turns the agent shouldn't have needed."**

## Levers That Reduce Round-Trips Without Hurting Quality

### 1. Front-load context in the brief

*Biggest, safest lever.*

Today `claude-code/implement.md` hands the agent only the ticket description as the plan (lines 16-19) and the worktree path - no code, no file map. So the agent burns turns discovering the codebase.

Make the plan phase emit (and the brief surface) the exact files/functions to touch and, ideally, the relevant file contents or a focused file map. Every file it doesn't have to `Read` is a turn removed - and it's information-preserving, so first-pass quality doesn't drop.

### 2. Add explicit batching guidance to the template

The template gives zero working-style guidance. Adding something like:

> Batch edits - use parallel tool calls; make all edits to a file in one message; don't re-read files already in context; run the full check suite once after implementing, not after each edit

measurably cuts round-trips (claude-code honors parallel tool calls).

Mind the template gotchas: edit as LF, then `dotnet test` the Briefs snapshots and update them (`Templates/AGENTS.md`).

### 3. Make sure output isn't throttled

`ClaudeCodeOptions.MaxOutputTokens` caps tokens per model response (default `null` = CLI default). If your config sets it low, a large file write gets chunked across turns - that is a literal "less code per turn" throttle. Leave it `null`/high.

### 4. Tighten scope and warm the session

Smaller diffs = fewer files to discover = fewer turns.

And the designed lever for exactly this cost axis is the warm batch session (op-31): prime one worker over a cohesive group instead of cold-booting per ticket. It's the unwired path from earlier - wiring it is the highest-leverage cache win for cohesive chains.

## The Honest Bottom Line

If the real goal is cost, the lever is **front-loaded context + warm batch sessions** (1 and 4), not pressuring the model to be terse.

If the goal is genuinely "more code per model response," only **#3** is a literal knob, and it rarely matters because the cap isn't usually the bottleneck.

I'd reach for **#1** first - it's the only one that cuts turns and helps correctness instead of trading against it.

---

Want me to draft the implement-template change (file map + batching guidance) and check what the plan phase currently emits, so we can see how much context is already available to front-load?
