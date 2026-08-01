# Backlog Run Analysis: BKFK2-401 Through BKFK2-404

Source transcript: `backlog-transcript.txt`

## Executive Summary

This transcript captures a successful autonomous backlog run over four dependent
tickets: BKFK2-401, BKFK2-402, BKFK2-403, and BKFK2-404. The run completed in
`1h 55m 59s`, produced four commits on the integration branch
`bkfk2-401-404-run`, moved all four tickets to Done, tore down all Build-managed
worktree leases, and ended with a clean working tree. No push, deployment, remote
production mutation, or migration application was performed.

The run is long, but the length is mostly the cost of useful safety. The workflow
did not merely rubber-stamp changes: independent strong reviews found real defects
in BKFK2-402 and BKFK2-404 before those changes were committed and integrated. In
both cases, the implementer reworked the same leased tree, a fresh reviewer checked
the result, and the conductor preserved the audited ancestry into the shared run
branch.

The most important observation is that the `build` CLI made the run operationally
boring in the right places. Ticket mutations, gate execution, worktree leasing,
safe teardown, and JSON/readback checks were reduced to repeatable commands with
visible evidence. The remaining overhead is now concentrated in repeated worker
briefs, repeated safety contracts, and manual-looking fingerprint/status command
blocks. Those are good candidates for compaction and deterministic helper commands.

## Run Scope And Outcome

| Ticket | Result | Commit | Notes |
| --- | --- | --- | --- |
| BKFK2-401 | Done | `da61ad5` | Map payload/rendered-scale budgets. Extra evidence recorded: scoped web e2e `162 passed / 6 skipped`, browser checks `17 passed`, named desktop/mobile screenshots. |
| BKFK2-402 | Done | `fad81a2` | Tiled source-snapshot contract. Strong review passed after 2 rework rounds. Focused source-snapshot tests: `16 passed`. |
| BKFK2-403 | Done | `8d890e3` | Bounded tiled Overpass acquisition. Strong review passed. Focused ingest tests: `70 passed`. |
| BKFK2-404 | Done | `d9b18f8` | Atomic source snapshot publication. Strong review passed after 1 rework round. Migration and later manual apply commands were recorded on the ticket. |

The transcript reports these final state guarantees:

- Every ticket had a green baseline gate before claim.
- Every integrated ticket had a green merged-tree `build gate --ticket <ID> --require-checks --json`.
- The final BKFK2-404 gate passed typecheck, build, test, and advisory lint.
- All Build leases were torn down.
- `build worktree list --json` reported no leases.
- The working tree was clean on `bkfk2-401-404-run`.
- No push, deploy, remote mutation, or migration application occurred.

## Timeline And Measured Runtime

The visible transcript reports total wall time of `1h 55m 59s`.

The transcript also includes visible shell command durations. Those visible command
durations total `994 seconds`, or `16.6 minutes`. That means most of the wall clock
was not ordinary shell execution in the conductor process. It was primarily worker
implementation, worker review, rework, and the gate runs performed inside those
worker sessions.

| Category | Visible Time | Count | Interpretation |
| --- | ---: | ---: | --- |
| Primary `build gate` commands | `10.6m` | `8` | Two visible gates per ticket: baseline before claim and merged-tree after integration. |
| Worktree lease commands | `1.3m` | `4` | About 19-20 seconds per ticket to create/install a lease. |
| Worktree teardown/list commands | `0.7m` | `4` | Safe cleanup checks after each ticket. |
| Ticket read/write command time | `3.4m` | `20` | Comments, state transitions, and readbacks. |
| Git/fingerprint command time | `0.6m` | `19` | Scope checks, diff fingerprints, candidate SHAs, ancestry checks. |
| All visible shell time | `16.6m` | n/a | About 14 percent of total wall time. |

The eight visible primary gates were remarkably consistent: each took about
`1m 18s` to `1m 20s`. That consistency is a strong sign that `build gate` is a
stable unit of workflow cost. It is not where most of the total runtime disappeared.

There are also implied gate runs inside workers. The implementer was instructed to
run the exact gate for each ticket, every reviewer was instructed to rerun it, and
each rework message required another exact gate. From the transcript shape, that
suggests roughly:

- 4 implementer gate runs;
- 7 reviewer gate runs;
- 3 rework gate runs;
- 8 visible conductor gate runs.

That is approximately 22 total gate invocations if each worker complied exactly.
Only 8 are timed directly in the visible transcript, so the full gate cost is
materially larger than the visible 10.6 minutes. Still, the gates are doing useful
work: they create a repeatable definition of "green" across implementation, review,
and integration.

## Workflow Quality

The conductor followed a disciplined sequence for each ticket:

1. Lease a dedicated worktree from the current run branch.
2. Run a baseline gate before claiming the ticket.
3. Record a claim ledger and move the ticket to InProgress.
4. Delegate implementation into the lease.
5. Scope-check the returned diff.
6. Fingerprint the candidate tree, including untracked files.
7. Send the actual diff to an independent reviewer.
8. Rework in the same lease when needed.
9. Use a fresh reviewer for each review round.
10. Commit only after review passed and the fingerprint held.
11. Record the candidate SHA and move the ticket to InReview.
12. Rebase and fast-forward the run branch.
13. Prove ancestry.
14. Run the merged-tree gate.
15. Record final evidence.
16. Move the ticket to Done.
17. Tear down the lease through Build's safe path.

This is a strong workflow. It is slow in the way a good release train is slow:
it forces state transitions and evidence to happen in order, and it makes the
operator prove that each ticket was safe before the next ticket starts.

The workflow also avoided a common failure mode in multi-ticket agent work. BKFK2-402
defined a contract that BKFK2-403 and BKFK2-404 consumed. The conductor recognized
that dependency chain and ran serially. That avoided parallel edits against a shared
contract surface and prevented later tickets from building against unstable or
unreviewed assumptions.

## Review Findings That Paid For The Process

The reviews were not ceremonial. They found defects that would have been easy to
miss in a faster pass.

### BKFK2-402

The first strong review found two contract holes:

- `duplicate_conflicts` entries were only validated as an array, allowing malformed
  entries, invalid resolutions, wrong digest/key types, or extra mutable fields.
- `evaluateSourceSnapshotPublication()` could publish a manifest with unresolved
  duplicate conflicts.

The second review found a remaining digest weakness:

- `winner_digest` and `loser_digest` accepted non-empty strings rather than a real
  digest format.

Those findings matter because BKFK2-402 was the contract ticket. If those holes had
landed, BKFK2-403 and BKFK2-404 would likely have implemented against a leaky or
ambiguous manifest policy. The two rework rounds were not waste; they stabilized the
contract before dependent work consumed it.

### BKFK2-404

The first strong review found a publication atomicity issue:

- The source pointer could advance even when manifest rows did not actually stage
  or promote. A D1 `ON CONFLICT(id)` no-op could still allow pointer advancement.

That is exactly the kind of bug a migration/publication ticket should be reviewed
for. The rework added a count/precondition safeguard, then a fresh reviewer checked
the updated behavior. The transcript indicates the final review passed after one
rework round.

## What The Build CLI Is Saving

This transcript is a good argument for the `build` CLI. It shows the value more
clearly than an abstract feature list.

`build` provided stable primitives for:

- creating ticket-specific leased worktrees;
- running configured gates with `--require-checks`;
- safely tearing down leases only after integration proof;
- writing ticket comments;
- transitioning ticket state;
- reading tickets back after mutation;
- producing JSON envelopes that can be inspected by the conductor.

Without `build`, the conductor would have had to carry a much larger instruction
surface for Plane access, credentials, project mapping, state IDs, relations,
comments, retries, JSON shapes, and failure handling. More importantly, each agent
would have had more chances to perform an unsafe direct mutation.

In this run alone, the transcript shows:

| Build-backed operation | Count |
| --- | ---: |
| Worktree leases | `4` |
| Visible primary gates | `8` |
| Ticket comment commands | `24` |
| Ticket transition commands | `12` |
| Ticket get/readback commands | `16` |
| Worktree teardown/list sequences | `4` |

That is a lot of stateful work. The benefit of `build` is that these operations
became deterministic command calls rather than custom agent reasoning. The CLI did
not make the run short. It made the run auditable.

## Context And Transcript Overhead

The transcript is `98,759` characters, `1,903` lines, and `11,310` words. That is
large, but it is not the same as model context consumed during the run. The transcript
appears to duplicate some worker prompts, showing both an `Input:` block and a
`Created <agent> with the instructions:` block for at least the first worker. That
inflates the transcript.

Even so, the context cost is real. The run spawned 11 worker agents:

| Worker Type | Count |
| --- | ---: |
| Implementers | `4` |
| Reviewers / strong reviewers | `7` |
| Total created agents | `11` |
| Total closed agents | `11` |

Every worker context likely received repeated forms of the same safety contract:

- work only in the supplied lease;
- do not mutate tickets;
- do not commit, push, branch, deploy, stash, reset, or tear down worktrees;
- run the exact gate command;
- report exact command results;
- preserve unrelated changes;
- stay within the declared ticket surface.

Those rules are important. The opportunity is to make them shorter and more canonical.
If 1,500 words of repeated instruction can be removed from the common path, that is
roughly 2,000-2,400 tokens per context. Across 11 worker contexts, that could save
about 22,000-26,000 tokens in a run shaped like this one, before counting the conductor
itself. The savings are not just cost and latency. Shorter, non-duplicated instructions
also reduce instruction fog.

## Where The Run Is Strong

The run shows several practices worth preserving:

- Baseline gates happened before ticket claim, so a red base would not be blamed on
  the ticket implementation.
- Each ticket was isolated in a Build lease.
- Implementation and review were separate roles.
- Strong reviewers were used for contract, ingest, and migration/publication work.
- Rework happened in the same lease, preserving the candidate context.
- Fresh reviewers were used after rework.
- Candidate fingerprints were recorded before and after review.
- Commits were created only after review passed.
- Integration preserved ancestry through rebase and fast-forward.
- Final evidence was written back to the ticket discussion.
- Cleanup was explicit and verified.

The most impressive part is that the workflow did not skip caution when it became
inconvenient. BKFK2-402 needed two rework rounds and BKFK2-404 needed one. The run
absorbed that friction and still completed the batch.

## Where The Run Is Expensive

The expensive areas are visible:

1. Repeated worker briefing

   Each worker prompt repeats the workspace, ticket state, expected files, gates,
   acceptance criteria, repository invariants, and prohibitions. Some of that should
   remain explicit. Some can move into a compact worker contract or generated brief.

2. Manual fingerprint ceremony

   The conductor repeatedly computed diff, cached, and untracked fingerprints with
   shell snippets. Those snippets worked, but they are easy to mistype and noisy in
   the transcript. A deterministic helper such as `build candidate status` or
   `build fingerprint` would reduce both token load and operational risk.

3. Ticket ledger verbosity

   The ledger is valuable, especially for auditability, but every ticket received
   claim, review, commit, integrate, gate, and final comments. A structured evidence
   command could preserve the separate events while producing a smaller, consistent
   comment shape.

4. Repeated gates

   Re-running gates in the baseline, implementer, reviewer, rework, and merged-tree
   phases is conservative. That is appropriate for these tickets, especially the
   contract and migration work. For lower-risk tickets, the same workflow could support
   risk-tiered gate profiles without changing the strong path.

## Recommendations

1. Compact shared instructions first.

   The highest leverage improvement is reducing repeated instruction text in
   `AGENTS.md`, nested `AGENTS.md` files, and the `run-backlog` skill. The plan in
   `agent-instruction-compaction-plan.md` is aimed at exactly this.

2. Add a Build-backed candidate fingerprint command.

   Replace repeated shell blocks with one deterministic command that reports:
   base SHA, current HEAD, tracked diff hash, cached diff hash, untracked file hash,
   touched paths, and lease manifest presence. This would make the conductor safer
   and the transcript smaller.

3. Add a compact worker brief artifact.

   Generate a `.build/worker-brief/<ticket>/<role>.md` file and give the worker a
   short instruction to read it. The brief can include ticket body, acceptance criteria,
   surface fence, exact gate, and role contract. This reduces prompt duplication and
   makes worker instructions inspectable after the fact.

4. Preserve strong review for contract and migration work.

   The strong review cost paid for itself in this run. Do not optimize it away.
   Instead, make the review prompts smaller and the reviewer evidence more structured.

5. Consider a structured evidence command.

   A command such as `build evidence add --ticket <ID> --kind review --sha <SHA> ...`
   could format comments consistently while keeping the conductor's mutation sequence
   explicit and inspectable.

6. Keep serial dependency handling as the default for contract chains.

   This batch had real dependencies: BKFK2-402 defined a contract used by BKFK2-403
   and BKFK2-404. Serial execution was the right choice. Parallel fan-out should remain
   reserved for independent tickets with Build-backed conflict planning.

## Bottom Line

This was a strong autonomous backlog run. It finished four dependent tickets in under
two hours, maintained clean ticket and git state, caught real defects through review,
and left a clear audit trail. The process is not lightweight, but it is doing real
engineering work.

The next gains should come from compression and tooling, not from weakening safety:
shorter shared instructions, deterministic fingerprint helpers, generated worker
briefs, and structured evidence commands. That would preserve the behavior that made
this run successful while reducing context load, transcript size, and operator friction.
