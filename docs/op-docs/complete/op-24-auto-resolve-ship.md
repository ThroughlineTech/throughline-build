# Operation: ship-auto-resolve-divergence

Make `build ship`'s gate auto-resolve clean-divergence cases rather than always stopping the chain on `local main and origin/main have diverged`. Adds a no-op probe that classifies the divergence (none, local-ahead, remote-ahead, diverged-no-conflict, diverged-with-conflict), an auto-rebase branch on the diverged-no-conflict subspecies, and an opt-out flag mirroring the obsolete-auto-resolve op-doc. No destructive operations attempted on unsafe states; the existing operator-triage path is unchanged for real conflicts.

## Why this exists

The ship gate today does a binary ancestry check (local main vs origin/main, via `git merge-base --is-ancestor`) and bails on any divergence with `manual resolution required` - even when the divergence is trivially mechanical: a clean rebase of local-only commits onto a moved origin/main. For automated multi-ticket runs (the world that op-auto-resolve-obsolete-escalations enables), this stops the chain on cases that don't need a human. Operator-side reality: a `docs/` commit landed on main from elsewhere during a long run, or the operator made an unrelated local commit between chain invocations.

The recon confirmed the divergence check fires after `git fetch` and before any destructive operation (ShipPhase Step 4a, between Step 4 fetch and Step 5 rebase). That is the ideal point to attempt an auto-rebase: if the rebase predicts and executes clean, ship continues with its existing flow; if it predicts a conflict, nothing is touched and the existing operator-triage path produces the same failure as today. The pre-flight dirty check at `ShipPhase.cs:141-162` runs before all of this and already refuses on uncommitted tracked changes, so untracked-file cruft and dirty-tracked-state are both outside the scope of this op-doc.

This is a sibling of op-auto-resolve-obsolete-escalations: same operator problem (automated chain stops on a thing that did not need a human), same fix-shape (detect category, resolve where safe, log honestly, opt-out flag). Different mechanism: deterministic git operations rather than reviewer ratification, with no model call needed - `git merge-tree` tells us deterministically whether a rebase will conflict.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Ship gate auto-resolve: probe, auto-rebase, event-log + opt-out | - | M |

Single plan; briefs sequential.

## Plan A: Ship gate auto-resolve

### Goal

After this plan, a ship invocation against a clean-divergence state auto-rebases local main onto origin/main and proceeds; against a real-conflict state, ship fails exactly as today with no destructive operations attempted; the auto-rebase is recorded in the event log; an opt-out flag forces the legacy diverged-equals-fail behavior.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | divergence-category-probe | Classify the local-vs-origin main state into a structured DivergenceState without destructive ops | - | src/ThroughlineBuild.Git/, src/ThroughlineBuild.Contracts/Models/, tests/ |
| 02 | auto-rebase-on-clean-divergence | On DivergedNoConflict, rebase local main onto origin/main as part of the ship gate; bail-and-restore on any failure | 01 | src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Git/, tests/ |
| 03 | event-log-and-opt-out | New MainAutoRebased event kind; `--no-auto-merge` flag on ship and chain to force legacy behavior | 02 | src/ThroughlineBuild.Events/, src/ThroughlineBuild.Cli/, docs/event-log-format.md, tests/ |

### Briefs - detail

#### Brief 01: divergence-category-probe

Goal: Replace the binary ancestry pass/fail with a structured category that distinguishes auto-resolvable divergence from genuine conflict, without performing any destructive git operations.

Inputs: the existing IsAncestorAsync helper on IGitClient; the post-fetch ancestry-check site at ShipPhase Step 4a; the `git merge-tree` primitive (or equivalent) for predicting conflict without mutating.

Outputs:
- `DivergenceState` enum in Contracts: `Clean`, `LocalAhead`, `RemoteAhead`, `DivergedNoConflict`, `DivergedWithConflict`.
- A probe method on IGitClient (or a sibling helper) that, given the main-worktree path and the base branch name, fetches if needed and returns the DivergenceState without modifying any working directory or branch.
- The probe uses `merge-tree --write-tree` (or equivalent) to determine the no-conflict-vs-with-conflict subspecies of the Diverged state.
- Probe is side-effect-free with respect to the main worktree's index and working tree.

Acceptance:
- [ ] Probe returns DivergenceState matching the actual ancestor relationship between local and remote main across all five categories
- [ ] DivergedNoConflict is reported when a hypothetical rebase of local onto remote would replay without conflict
- [ ] DivergedWithConflict is reported when the same hypothetical rebase would produce a conflict
- [ ] Probe leaves the main worktree's index and working tree unchanged in every category
- [ ] Tests cover all five DivergenceState categories against fixture repos

Notes: `git merge-tree` (or equivalent dry-run mechanism) gives a clean conflict-prediction without mutating anything; this is the right primitive for the probe. The DivergedWithConflict case continues to produce the existing operator-triage message in B02; only the no-conflict subspecies becomes auto-resolvable. If the probe itself encounters an error (transient network issue during a re-fetch, corrupted index), treat as DivergedWithConflict for safety - never silently proceed.

OOS:
- Performing the rebase itself (B02 owns)
- Event-log emission (B03 owns)
- Changes to the operator-triage error message for unrecoverable cases (current text stands)
- Any check on worker-branch state versus main (only the local-main vs origin-main relationship is in scope)

#### Brief 02: auto-rebase-on-clean-divergence

Goal: When the probe reports DivergedNoConflict, rebase local main onto origin/main as part of the ship gate, then continue ship's existing flow on the updated main. Any failure during the actual rebase leaves the main worktree at its pre-attempt state and falls through to the existing operator-triage path.

Inputs: the DivergenceState probe from B01; the existing post-fetch / pre-rebase position in ship's sequence (between Step 4 and Step 5); existing RebaseAsync helper on IGitClient; the existing operator-triage error path.

Outputs:
- Ship gate logic that, on DivergedNoConflict, performs the rebase of local main onto origin/main in the main worktree, then continues with the existing rebase-and-ff-merge flow as if there had been no divergence.
- On Clean, LocalAhead, or RemoteAhead, ship proceeds through its existing flow unchanged from today.
- On DivergedWithConflict, ship produces the existing operator-triage failure with no attempt to rebase.
- On a race condition (B01 predicted DivergedNoConflict but the rebase surprises with a conflict at execution), the main worktree is restored to its pre-attempt state (`git rebase --abort` plus any necessary reset), and ship produces the existing operator-triage failure.

Acceptance:
- [ ] A ship invocation against a DivergedNoConflict state completes successfully and produces the same end state as a manual rebase of local main onto origin/main
- [ ] A ship invocation against a DivergedWithConflict state produces the same operator-triage failure message as today
- [ ] A race condition leaves the main worktree at its pre-attempt state and produces the existing operator-triage failure
- [ ] Clean, LocalAhead, and RemoteAhead states proceed through ship unchanged from today
- [ ] The feature worktree's state is not modified by the auto-rebase logic; the existing feature-worktree rebase step continues to own that working directory

Notes: Bail-and-restore is critical: if the predicted-clean rebase encounters a conflict at execution time, the main checkout must end up where it started. `git rebase --abort` from inside the rebase is the recovery path; verify it leaves index and working tree clean. This is the only step in this op-doc that performs a destructive git op, so the restore guarantee is the safety contract.

OOS:
- Stashing or otherwise handling uncommitted operator changes (the pre-flight dirty check at ShipPhase.cs:141-162 still catches that case ahead of the auto-resolve)
- Merging origin/main into local main as an alternative to rebasing (rebase keeps history linear and matches existing convention)
- Auto-resolution of conflicts via heuristics or model assistance (the only auto-resolvable subspecies is "no conflict at all")
- Skipping or modifying the existing post-rebase conflict-marker scan at ShipPhase.cs:272-292

#### Brief 03: event-log-and-opt-out

Goal: A new event kind records each auto-rebase attempt and outcome, and an opt-out flag forces the legacy "Diverged = fail" behavior for operators who want every divergence to surface.

Inputs: the existing event-kind set documented in docs/event-log-format.md; the auto-rebase branch from B02; CLI flag parsing in ship's verb dispatcher and the chain verb.

Outputs:
- New event kind `MainAutoRebased` with payload `{ from_sha, onto_sha, local_commits_replayed: [shas], outcome: "clean" | "raced_to_conflict" }`. Registered in the appropriate `JsonSerializerContext`.
- ShipPhase emits the event on every auto-rebase attempt, regardless of outcome (clean replay or race-induced conflict).
- `--no-auto-merge` flag available on `build ship` and `build chain`, recorded in the relevant options record.
- When `--no-auto-merge` is set, the B02 auto-rebase branch is skipped entirely; any divergence (including DivergedNoConflict) falls through to the existing operator-triage path.
- Event-log documentation updated for the new event kind.

Acceptance:
- [ ] The MainAutoRebased event appears in the event log on every auto-rebase attempt, with the documented payload
- [ ] The event kind appears in `docs/event-log-format.md` with its payload documented
- [ ] `--no-auto-merge` on `build ship` causes any divergence to produce the existing operator-triage failure, with no auto-rebase attempted
- [ ] `--no-auto-merge` on `build chain` propagates to every ship invocation in the chain
- [ ] AOT publish succeeds with the new DTO registered in a source-gen context

Notes: The flag name `--no-auto-merge` deliberately mirrors `--no-auto-resolve` from the obsolete-auto-resolve op-doc - same shape of opt-out, same operator mental model. Keep the naming consistent so the two systems read as a family of auto-resolve features. If a future operator wants to disable both, a higher-level convenience flag could be added later; this op-doc keeps the controls independent.

OOS:
- Per-project or per-chain configurability beyond the flag (config-file opt-out is a separate concern if it ever becomes needed)
- Sending a notification on auto-rebase (event-driven notifications belong with the notification infrastructure thread, not here)
- Restructuring the existing ship-gate failure messages for unrecoverable cases
- Backward-compatible reading of pre-MainAutoRebased event logs (additive event kind, no schema break)

## What done looks like

A ship run that previously failed on `local main and origin/main have diverged` now succeeds when the divergence is a clean rebase, recording a `MainAutoRebased` event with the replayed commit SHAs and outcome. When the divergence has real conflicts, ship fails exactly as today with the same operator-triage message - no destructive operations were attempted, the main worktree is untouched. The new `--no-auto-merge` flag is available on both ship and chain for operators who want every divergence to stop the chain, matching the opt-out shape of the obsolete-auto-resolve work. The "docs/ commits landed during a long run" friction in unattended chain runs no longer requires manual intervention when those local-main commits replay cleanly onto origin/main.