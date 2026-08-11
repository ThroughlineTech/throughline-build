# Fan-out: the scheduling layer

Fan-out decides HOW MANY ticket transactions may be in flight at once. It does not change what a
transaction is. Every state, proof, and failure path in
[ticket-transaction.md](ticket-transaction.md) applies identically whether the run is serial or
parallel. If you find yourself writing a rule that only holds in one mode, the rule is wrong.

Serial mode is this file with cap = 1 and no wave planning. That is a real and often correct
answer: fan-out only pays on genuinely file-disjoint tickets.

## 1. Verify dependencies before planning

`build waves` accepts `verifiedExternalDeps` as an ASSERTION. It does not read your ticket system
and it cannot prove anything: the planner only checks that every dependency outside the selected
ticket set appears in that list, and then proceeds. Supplying an identifier is not evidence.

So the conductor verifies each one first, through the repo's ticket CLI:

```sh
build get <DEP-ID> --json      # or the repo's own ticket CLI
```

A dependency counts as satisfied only when its state is in the repo's declared satisfied set
(normally Done, sometimes Done-or-Deployed). Record the id and the observed state in the plan
printout. If a dependency cannot be read, or is not satisfied, the dependent ticket does not enter
the wave - it is not started merely because its identifier was typed into the planner input.

In-scope dependencies (a prerequisite that is itself in the selected set) do not need this: the
planner levels them topologically so the prerequisite lands in an earlier wave. But note what
"earlier wave" buys you - see "Dependent tickets" below.

## 2. Predict surfaces and plan the waves

Predict each ticket's files from its body. That prediction is the ticket's DECLARED SURFACE, and
it is reused as the scope fence in the transaction - do not invent a second artifact.

```sh
build waves --input tickets.json --json
```

Input is `[{id, files, deps}]`, or an object with `cap`, `verifiedExternalDeps`, and `tickets`. Set
`uncertain: true` when a prediction is unreliable; an uncertain ticket, or one with an empty
`files` array, serializes with every peer. Exact-file overlap always serializes without
configuration. The repo's own conflict classes - `global`, `cohesive-module`, `pairwise` - live in
`[waves]` in `.build/config.toml`, not here.

**Print the plan for the human before leasing anything.** The output names the rule and the path
behind every serialization decision, so an unexpected serial wave is explainable. Never silently
serialize or silently parallelize.

## 3. Run one wave at a time

For each ticket in the wave, at the configured cap (2, sometimes 3 - the bound is disk, per-lease
install, and gate load, not the model), run the full transaction: lease, baseline, claim,
implement, scope-check, independent-review.

The conductor owns the workspace lifecycle. Do NOT use any per-agent "fresh workspace" isolation
feature: the reviewer must see the implementer's tree, and rework round 2 must resume round 1's
tree. Per-call isolation destroys both.

## 4. Commit and integrate serially

Commit, integrate, merged-gate, and finalize run ONE TICKET AT A TIME, in ticket order, even
though implementation ran in parallel. There is no integrator subagent - spawning one to run three
git commands only adds a context boundary where evidence gets lost.

The serial section is where the wave's parallelism is cashed in, and it is where a bad wave plan
surfaces as a merge conflict. Treat every cross-ticket conflict as a finding about the file
predictions, not just an obstacle.

## 5. When one ticket in a wave fails

The other tickets are not automatically doomed, but they are not automatically fine either:

- A ticket that fails BEFORE `commit` (rework exhausted, scope conflict, red baseline) does not
  block its peers. Its peers continue through commit and integration normally.
- A ticket that fails AT `integrate` (merge conflict) does not block peers whose surfaces are
  genuinely disjoint - integrate them, then stop and report the conflict. The conflicted ticket
  goes to BLOCKED-INTEGRATION with its lease intact. Re-planning can prevent the same scheduling
  mistake later, but it cannot repair the existing commit; a human chooses rebase-and-re-review,
  replacement implementation on the current base, or leaving it blocked.
- A ticket that fails at `merged-gate` STOPS THE WAVE. The run branch is red; integrating another
  ticket on top of a red branch makes the failure harder to attribute and harder to unwind. Every
  remaining ticket in the wave stays In Review with its lease preserved, and nothing is torn down.
- Passed-but-unintegrated peers stay In Review. That state is the whole point: their commits exist
  on their lease branches, they are recorded on their tickets, and no one has claimed they are
  Done.

## 6. Dependent tickets

A dependent ticket becomes eligible only when its prerequisite reaches Done - which under this
lifecycle means integrated, gated on the merged tree, and finalized. A per-worktree PASS does not
unblock anything. This is stricter than dependency LEVELING, which only guarantees an earlier
wave: if the prerequisite ends the wave escalated or blocked, its dependents do not start, even
though the planner put them in the next level.

Re-check this at the start of each wave, from the ticket system, not from the plan.

## 7. Scope expansion mid-wave

A surface expansion is a scheduling decision, not implementer rework, because the peers already in
flight were scheduled against the OLD surface. The check and the replan rule are in
[ticket-transaction.md](ticket-transaction.md), section "Surface expansion". The scheduling
consequence: a replanned ticket re-enters `build waves` with its widened `files` list, which may
legitimately move it behind tickets that were originally its peers.

## 8. Honest expectations

Fan-out pays only on the genuinely disjoint subset - roughly 2x net on a realistic batch once
per-lease setup and serial integration are counted, not across the board. If a scope's tickets
mostly overlap, the planner collapses them back to serial. That is the planner protecting the
integration path, not a failure.

Treat the speedup number as a planning signal, not a promise. Concurrency also stresses shared
machine resources; keep the cap small.

Deploy, merge-to-main, and ship are NEVER inside the loop. They are separate, explicit,
human-approved steps.
