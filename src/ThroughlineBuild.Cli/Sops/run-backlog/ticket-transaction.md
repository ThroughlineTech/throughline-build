# The ticket transaction (universal)

The ONE lifecycle a conductor follows for a ticket. Serial and parallel runs use it unchanged;
[fan-out-scheduling.md](fan-out-scheduling.md) only decides how many transactions are in flight at
once. There are no separate correctness rules for parallel mode.

Host-agnostic and repo-agnostic. Per-repo values live in that repo's `Conductor inputs` block;
per-host mechanics (spawning a subagent, registering a pre-tool hook) live in that host's adapter.

| Concern | Owner |
| --- | --- |
| workspace lifecycle | `build worktree lease \| list \| teardown`, called by the conductor |
| which tickets may run together | `build waves --input` |
| what "green" means | `build gate --ticket <ID> --require-checks --json` (reads `[[review.checks]]`) |
| every commit, every branch, every ticket mutation | the CONDUCTOR, in the PRIMARY worktree |
| implement / review judgment | the conductor's subagents, read-implement-gate only |

Everything below serves two invariants: **a ticket is Done only when the run branch provably
contains its verified change**, and **nothing is deleted while it might still hold unintegrated
work**.

`build chain` / `plan` / `implement` / `review` is the deliberately-rejected alternative: same
pipeline, a fresh worker CLI per phase, a cold context load every time. Do not adopt it.

## States

```
  [run preflight] -> [recovery triage] -> plan
                                           |
        per ticket:  lease -> baseline -> claim -> semantic-checkpoint -> implement -> scope-check
                                                                            ^            |
                                                                            |            v
                                                             (rework, max 3) +---- independent-review
                                                                    |
                                            commit -> integrate -> merged-gate
                                                                    |
                                              finalize-ticket -> teardown -> DONE
```

Holding states, reachable from many places, all non-destructive:

- **ESCALATED** - hand to the human. Lease, branch, diff, gate output, finding history preserved.
  The ticket keeps its current non-final state.
- **BLOCKED-REPLAN** - cannot proceed in this wave. Lease preserved; ticket returns to the planner.
- **BLOCKED-INTEGRATION** - a committed ticket conflicts with the current run branch. Re-ordering
  waves cannot change an existing commit; lease and branch stay preserved until a human chooses
  rebase-and-re-review, new implementation on the current base, or abandonment.
- **ABORTED-PRECLAIM** - failed before `claim`, so no ticket mutation ever happened.

Ticket states (map to the repo's own names in `Conductor inputs`): unchanged before `claim`; **In
Progress** after `claim`; **In Review** once a conductor candidate commit exists and its SHA is
recorded; **Done** only at `finalize-ticket`.

**In Review means "a conductor candidate commit exists and is not yet proven integrated".** That state is
what the redesign hangs on: it is observable, it is where an interrupted run is found, and it is
why a failed integration can never read as Done.

---

## 0. Run preflight (before ANY ticket mutation)

Conductor, primary/integration worktree, read-only.

```sh
git -C <primary> rev-parse --abbrev-ref HEAD                          # the run branch, never protected
git -C <primary> status --porcelain                                   # MUST be empty
git -C <primary> rev-parse -q --verify MERGE_HEAD || true              # must be absent
ls <primary>/.git/rebase-merge <primary>/.git/rebase-apply 2>/dev/null # must be absent
build worktree list --json                                            # leases + unmanifested dirs
```

- **Dirty tree -> STOP.** Print the exact porcelain output and the branch. Do NOT stash, `git add`,
  commit alongside ticket work, or switch branches. Integration commits and the merged gate both
  run here, so absorbing unrelated work would put unreviewed changes inside a ticket's proof.
- **On the protected branch, or an interrupted merge/rebase/cherry-pick -> STOP.**
- **Outstanding leases -> recovery triage.** Never delete a lease because a new run is starting.

Resuming a specific already-active ticket is the only path that may proceed past an inconvenient
tree state, and only for read-only inspection. Integration always requires a clean primary tree.

## 1. Recovery triage

Conductor, read-only until a classification is certain.

```sh
build worktree list --json                                  # manifest: ticket, branch, baseSha, path
git -C <lease> rev-parse HEAD                               # == baseSha iff no commit was made
git -C <lease> status --porcelain                           # uncommitted tracked work
git -C <lease> ls-files --others --exclude-standard         # untracked, non-ignored work
git -C <primary> merge-base --is-ancestor <leaseHead> HEAD  # integrated?
build get <ID> --json && build comments <ID> --json         # ticket state + the conductor's ledger
```

The lease branch IS the ledger: workers cannot commit, so `leaseHead != baseSha` proves the
conductor committed. Ticket comments carry the rest. No extra state file is kept.

| Classification | Observables | Resume at |
| --- | --- | --- |
| leased but untouched | `HEAD == baseSha`, clean, no untracked, no current claim ledger | `baseline` |
| claim recorded but transition pending | current claim ledger present, ticket not In Progress | finish transition, then `implement` |
| implemented but unreviewed | `HEAD == baseSha`, tree has changes, no verdict comment | `scope-check` |
| reviewed but uncommitted | verdict comment PASS present, `HEAD == baseSha` | `commit` |
| committed but In Review transition pending | commit ledger candidateSha matches lease HEAD, ticket not In Review | finish transition, then `integrate` |
| committed but unintegrated | commit/integrate ledger matches lease HEAD, not an ancestor of run HEAD | `integrate` |
| integrated but not gated | matching integrate ledger or ancestry proof, no matching gate ledger | `merged-gate` |
| gated but ticket not finalized | matching gate ledger present, ticket not Done | `finalize-ticket` |
| finalized but not torn down | ticket Done, lease still present | `teardown` |
| ticket In Progress but no lease | In Progress, no lease, no current transaction ledger | STOP - human chooses lease or state correction |
| ambiguous | anything else | **STOP - human** |

Ambiguous includes: a ticket marked Done whose recorded integrated commit is not an ancestor of the run
branch; more than one commit on a lease branch; an unmanifested directory under `[worktree].root`;
a manifest that fails validation; a recorded SHA that does not exist. Report; do not guess.

**Idempotency and correlation.** Before repeating any ticket mutation, read the current state and
comments. Re-post a comment only if that stage's ledger line for THIS transaction is absent;
re-issue a transition only if the ticket is not already in the target state.

Define the transaction key immediately after a successful lease, before the first ticket comment:

`tx=<TICKET-ID>|<leaseBranch>|<baseSha>|<runBranch>`

Every conductor comment starts with a stable, greppable prefix and carries that exact key:
`run-backlog <STAGE> <TICKET-ID> [<tx>]: <one line>`, where `<STAGE>` is one of `claim`, `semantic`,
`scope`, `review`, `commit`, `integrate`, `gate`, `final`, `blocked`, `escalated`. Commit and later stages
carry `candidateSha` or `ticketSha` as appropriate; integrate, gate, and final also carry the exact
`runHeadSha` they observed.

An old comment with the same stage and ticket id is NOT evidence for the current transaction.
Recovery accepts a ledger line only when its transaction key, recorded SHAs, lease manifest, lease
HEAD, run branch, and current run HEAD agree. This prevents a reopened ticket or an earlier run
from being mistaken for the current transaction.

## 2. plan

Conductor. Wave planning, dependency verification, and surface prediction are in
[fan-out-scheduling.md](fan-out-scheduling.md). In a serial run "plan" still happens: the declared
surface is not optional, it is the scope fence. Unverifiable dependency, cycle, or unpredictable
surface -> the ticket is not started (ABORTED-PRECLAIM).

### 2.1 Semantic checkpoint classification

Before implementation, classify every ticket as semantic-risk or not semantic-risk. Treat a ticket as
semantic-risk when it touches or could change derived state, publication, freshness or status, scoring,
authorization, persistence, cache behavior, contracts, shared public or administrative behavior,
lifecycle transitions, or an area with known prior drift. Uncertainty is semantic-risk; do not use a
narrow label to avoid the checkpoint.

For a semantic-risk ticket, derive a ticket execution contract from the ticket body, parent body and
comments, repository instructions, and inspected code. A child inherits its parent's intent; it may
refine the assigned surface but must not silently reinterpret that intent. If the parent and child,
comments, instructions, or inspected authority conflict, stop before implementation and escalate the
conflict to the conductor or human who can amend the contract.

The execution contract records all of the following in the ticket body before code edits:

- Parent intent and the ticket's semantic-risk classification.
- Authoritative source of truth.
- Forbidden shortcuts.
- Required shared helper, query, or surface.
- Required focused negative tests.
- Explicit out-of-scope behavior.
- Rework fence.

For a durable, checkable repository rule in that contract, add a
`[[conductor.review.invariants]]` entry with `id`, `statement`, relevant `paths`, and
`blocks_done = true` where the conductor must treat the rule as a finalization obligation. These are
structured prose: doctor validates their shape only and does not evaluate whether a statement is true.
The conductor, not doctor, judges whether the contract was satisfied.

If the contract cannot be written from a declared authority, do not create one by inference. Stop before
code edits and escalate. A non-semantic classification does not waive the declared surface, independent
review, gate, fingerprint, or finalization rules.

---

## 3. lease

- **Entry:** preflight clean; ticket in the current wave; a declared surface exists.
- **Owner:** conductor, from the primary tree. **Mutations:** the lease worktree and its `lease/*`
  branch. No ticket mutation.

```sh
build worktree lease --ticket <ID> --slug <slug> --base <run-branch-or-HEAD> --json
```

Keep the whole manifest: absolute path, branch, `baseSha`, seeded files, install status.

- **Success ->** `baseline`. **Failure** (collision, seed refused, install failed) -> ABORTED-PRECLAIM.
- A `lease_collision` means a lease for this ticket already exists: that is a recovery case.
  **Never tear down an existing lease to make room for a new one.**
- **Preserve:** whatever the failed lease left behind. `build worktree lease` rolls back its own
  partial creations; a lease that survives a failure is inspected, not deleted.

## 4. baseline

- **Entry:** a lease manifest. **Owner:** conductor. **Mutations:** none.

```sh
build gate --ticket <ID> --require-checks --json      # cwd = the lease worktree
```

`build gate` runs its checks in the invocation directory, so this gates the lease, not the primary
tree. Read two fields:

- `data.checksConfigured == false` -> **ABORT THE WHOLE RUN** and tell the human. With
  `--require-checks`, this is a non-zero gate failure. Without that flag it would be a decorative
  green light that verifies nothing.
- `data.passed == false` -> the base ref is already red. Do NOT dispatch an implementer; one handed
  a red baseline fixes things that are not its ticket, and that is where scope creep starts. A
  broken base is its own ticket.

Keep the baseline JSON: it is the attribution evidence for everything after. Any gate failure not
present in the baseline was caused by this change.

- **Success ->** `claim`. **Failure ->** ABORTED-PRECLAIM, lease preserved.

## 5. claim

- **Entry:** lease installed AND baseline green. **Owner:** conductor, primary tree.
  **Mutations:** one ticket comment, then one transition.

**Claim happens AFTER lease and baseline, never before.** A lease collision, failed install,
missing gate configuration, or red base must not leave a ticket looking like it is being
implemented. The cost is a short window where work has started and the ticket still reads Ready;
the cost of the other ordering is a false claim on every setup failure.

Evidence first, flag second:

```sh
build comment <ID> "run-backlog claim <ID> [<tx>]: baseline gate green"
build transition <ID> InProgress
```

- **Success ->** `semantic-checkpoint`.
- **Comment fails ->** no ticket mutation occurred; ABORTED-PRECLAIM with the lease preserved.
- **Transition fails ->** retry once, then ESCALATED. Recovery finds the existing lease and
  matching claim ledger as "claim recorded but transition pending"; it does not create a new
  lease.

## 5.1 semantic-checkpoint

- **Entry:** ticket In Progress; claim ledger line present. **Owner:** conductor, before dispatching
  an implementer. **Mutations:** the conductor may amend the ticket body to record the semantic
  classification and, for a semantic-risk ticket, its execution contract.

Use conductor-owned `build amend` operations to preserve the source ticket while recording the contract.
Read the ticket and its comments back after the amendment. The worker brief must contain the resulting
ticket body, so the implementer, reviewer, and any rework handoff receive the same contract.

- A non-semantic ticket with its classification recorded -> `implement`.
- A semantic-risk ticket with every contract field recorded and no authority conflict -> `implement`.
- Missing field, unresolved authority conflict, or a child-parent intent conflict -> ESCALATED before
  code edits. Do not substitute a local interpretation or send a partial implementation to review.

## 6. implement

- **Entry:** ticket In Progress; claim ledger line present. **Owner:** ONE implementer subagent,
  working directory = the lease. **Mutations:** file edits inside the lease, within the declared
  surface, plus its own gate run. Nothing else.

Give it the ticket body, the declared surface, the absolute lease path, and the exact gate command:
`build gate --ticket <ID> --require-checks --json`. Never restate the underlying toolchain commands.

For a semantic-risk ticket, the ticket execution contract is binding. An implementer stops before code
edits when it is missing, ambiguous, or conflicts with inspected authority. Before expanding a shared
surface, public contract, or user-facing behavior, add and run the focused negative tests the contract
requires when applicable. A need for a new source of truth, shortcut, shared surface, or test not in the
contract is a conductor decision, not an implementer interpretation.

The implementer does NOT commit, stage, branch, push, stash, reset, tear down a worktree, or touch
a ticket. `HEAD` in the lease must still equal `baseSha` when it returns - `scope-check` verifies
that, and it is what makes the lease branch a trustworthy ledger.

- **Success ->** `scope-check`. **Cannot do the ticket ->** ESCALATED, lease preserved.

## 7. scope-check

- **Entry:** the implementer's report. **Owner:** conductor, deterministic, no agent.
  **Mutations:** none.

```sh
git -C <lease> rev-parse HEAD                        # MUST still equal baseSha
git -C <lease> status --porcelain
git -C <lease> diff --name-only <baseSha>
git -C <lease> ls-files --others --exclude-standard
```

Compare the union of touched and new files against the declared surface, allowing only the lease
manifest file and the manifest's `seededFiles` as expected residue.

- `HEAD != baseSha` -> a worker committed. **ESCALATED immediately**: the enforcement boundary
  failed and the run's assumptions no longer hold.
- Inside the surface -> `independent-review`.
- Outside the surface -> a surface-expansion decision, which is a CONDUCTOR SCHEDULING problem, not
  implementer rework. See "Surface expansion".

## 8. independent-review

- **Entry:** a scope-clean diff and the baseline result. **Owner:** a SEPARATE reviewer subagent,
  fresh context, same lease worktree, no Edit/Write tool. **Mutations:** none; it runs
  `build gate --ticket <ID> --require-checks --json` and reads.

Independence is the invariant: the reviewer did not write the code and must see the implementer's
actual tree. Do NOT use any per-call "fresh workspace" isolation feature and do not spawn the
reviewer into a different worktree - rework round 2 must also resume round 1's tree.

For a semantic-risk ticket, review the execution contract before reaching a verdict. Verify the declared
authority, forbidden shortcuts, required focused negative tests, and required shared surfaces against the
actual diff. A clear contract violation is an implementation defect. A missing, ambiguous, or conflicting
contract is a plan or contract defect: do not invent a replacement contract or send speculative
implementation rework; return it to the conductor for amendment or escalation.

The conductor fingerprints the exact candidate change BEFORE review. The fingerprint consists of:

1. lease `HEAD`;
2. SHA of `git diff --binary --full-index <baseSha>`;
3. SHA of `git diff --cached --binary --full-index <baseSha>`;
4. the sorted `(repository-relative path, git hash-object --no-filters)` pairs for every
   `git ls-files --others --exclude-standard` result.

Ignored build outputs are intentionally absent. The lease manifest and seeded files are included:
they must not change during review either. Store the four values as one canonical candidate
fingerprint in the conductor transcript. Hashes alone are not enough for recovery unless the
associated base SHA and sorted untracked path/blob-hash pairs are retained too.

After the reviewer returns, recompute all four values and repeat `scope-check`. Exact equality is
required. A reviewer has Bash and a gate can generate files; absence of Edit/Write tools is not
proof that the candidate stayed unchanged.

- **Fingerprint or repeated scope-check differs ->** do not commit and do not restore
  automatically. Record the before/after evidence, leave the lease intact, ESCALATED.
- **PASS ->** post and read back
  `run-backlog review <ID> [<tx>]: PASS, candidateFingerprint <value>, <non-blocking summary>`,
  then `commit`.
- **REWORK ->** back to `implement` with only the numbered blocking findings, max three rounds.
- **Three failed rounds ->** ESCALATED.
- **Review ledger comment fails or is absent on read-back ->** retry once, then ESCALATED. The
  commit stage requires a matching PASS ledger and fingerprint; a verdict existing only in an
  agent transcript is not durable recovery evidence.

## 9. commit

The stage that did not exist before. Workers are forbidden to commit and integration assumes a
commit exists; the conductor creates it.

- **Entry:** `VERDICT: PASS`, scope-check clean, `HEAD == baseSha`.
- **Owner:** conductor, **cwd = the primary tree**, addressing the lease with `git -C`. Do not `cd`
  into the lease: the worker deny hook keys on the caller's directory, and a conductor standing
  inside a lease is indistinguishable from a worker.
- **Mutations:** the lease index and one commit on the `lease/*` branch. Nothing on the run branch.

```sh
git -C <lease> diff --cached --quiet || echo "ANOMALY: index not empty"   # must be empty
git -C <lease> add -- <approved path> [<approved path> ...]               # explicit paths ONLY
git -C <lease> diff --cached --name-only                                  # must equal the approved set
git -C <lease> commit -m "<TICKET-ID>: <short description>"
git -C <lease> rev-parse HEAD                                             # candidateSha
```

Never `git add -A`, never `git add .`, never `git commit -a`. Then, evidence before flag:

```sh
build comment <ID> "run-backlog commit <ID> [<tx>]: candidateSha <candidateSha>, review PASS, candidateFingerprint <value>"
build transition <ID> InReview
```

- **Success ->** `integrate`.
- **Pre-staged index, or staged set != approved set ->** do not commit; ESCALATED.
- **Commit fails** (hook, identity, empty commit): do not retry blind. An empty commit means the
  implementer changed nothing - a review failure, not a commit failure. ESCALATED.
- **Comment fails ->** the commit exists but is unrecorded: retry, then ESCALATED. Do not
  transition. Recovery still finds the commit from the lease branch head.
- **Transition fails ->** retry once, then ESCALATED.
- **Preserve:** the lease, the branch, the candidate commit. Nothing is torn down here or in the next three
  stages.

## 10. integrate

- **Entry:** a recorded candidate commit SHA; primary tree still clean; ticket In Review.
- **Owner:** conductor, primary tree, on the run branch. Serial - one integration at a time even
  when the wave implemented several tickets in parallel. **Mutations:** the run branch.

Integrate by preserving helper-branch ancestry, never by cherry-pick. The preferred path is:
rebase the helper branch onto the current run branch, stop on conflicts, then fast-forward the run
branch to the helper branch. If the rebase rewrites the conductor's original commit, the
post-rebase HEAD becomes the `ticketSha` recorded for integration/finalization. A cherry-pick
creates a disconnected SHA and breaks both the ancestry proof and safe teardown.

```sh
git -C <lease> rebase <run-branch>                           # stop on conflicts
git -C <lease> rev-parse HEAD                                # post-rebase TICKET COMMIT SHA
git -C <primary> merge --ff-only <lease-branch>              # fast-forward the run branch
git -C <primary> merge-base --is-ancestor <ticketSha> HEAD   # exit 0 REQUIRED - prove, do not assume
git -C <primary> rev-parse HEAD                              # capture exact runHeadSha
build comment <ID> "run-backlog integrate <ID> [<tx>]: candidateSha <candidateSha>, ticketSha <ticketSha>, runHeadSha <runHeadSha>, ancestry proven"
build comments <ID> --json                                    # matching line MUST read back
```

- **Rebase conflict:** `git -C <lease> rebase --abort` when a rebase is in progress - the only
  automatic git undo this procedure permits, because it reverses an operation the conductor just
  started and touches only the ticket lease. Record the conflicting files and hunks verbatim. Do NOT
  resolve with a semantic guess and do NOT reset or revert. Ticket stays In Review, lease preserved
  -> BLOCKED-INTEGRATION. A cross-ticket conflict also means the wave plan let two overlapping
  tickets fan out: a conductor-level finding about the file predictions, worth fixing before the
  next wave. Re-planning alone cannot alter the already-created commit. A human must choose one
  explicit recovery:
  - resolve deliberately in the lease, rerun the full gate and independent review, then record the
    rewritten `ticketSha`;
  - abandon that commit and start a new implementation lease from the current run HEAD, preserving
    the old lease until the replacement is verified; or
  - leave the ticket blocked.
- **Fast-forward fails without conflicts ->** STOP and inspect; the run branch moved or the helper
  branch ancestry is not what the ledger says. Do not use `--no-ff` or cherry-pick as a shortcut.
- **Ancestry proof fails after an apparently clean merge ->** ESCALATED.
- **Integrate ledger comment fails or is absent on read-back ->** do not gate or finalize. Retry
  once, then ESCALATED; ancestry still makes the integrated state recoverable.
- **Success ->** `merged-gate`.

## 11. merged-gate

- **Entry:** ancestry proven. **Owner:** conductor, primary tree. **Mutations:** none.

```sh
build gate --ticket <ID-or-scope> --require-checks --json          # cwd = primary tree
```

A green per-worktree gate does not prove the merged tree is green. Paste the output verbatim.

- **Green ->** post and read back
  `run-backlog gate <ID> [<tx>]: ticketSha <ticketSha>, runHeadSha <runHeadSha>, merged gate green`
  with the gate summary, then `finalize-ticket`.
- **Gate ledger comment fails or is absent on read-back ->** retry once, then ESCALATED. Recovery
  safely re-runs the merged gate; it never infers green from ancestry alone.
- **Red ->** the run branch carries an integrated but failing change. **Do NOT auto-revert, reset,
  or drop the merge.** Record the failure, leave the ticket In Review, preserve every lease in the
  wave, halt all further integration, ESCALATE. Fix-forward versus roll-back is a human decision,
  and rolling back a branch that may also carry a peer's integrated work is exactly the destruction
  this procedure exists to prevent.

## 12. finalize-ticket

- **Entry:** ALL of - the conductor-created integrated ticket commit exists; it is an ancestor of the run
  branch HEAD; the merged gate is green on that tree; every repo-declared finalization invariant is
  satisfied. **Owner:** conductor. **Mutations:** one final comment, then one transition to Done.

Finalization invariants are the coordination obligations a gate cannot see - a migration whose
apply command must be recorded on the ticket, a contract change whose cross-repo follow-up tickets
must exist, a deploy note. They are listed in the repo's `Conductor inputs`, and an unsatisfied one
blocks Done exactly like a red gate.

```sh
build comment <ID> "run-backlog final <ID> [<tx>]: commit <ticketSha> integrated at <runHeadSha>, merged gate green
<verbatim gate summary>"
build comments <ID> --json      # read back - the comment MUST be present
build transition <ID> Done
```

- **Comment fails or is absent on read-back ->** do NOT transition; ticket stays In Review; retry
  once, then ESCALATED. Done without a recorded reason is unauditable.
- **Transition fails ->** retry once, then ESCALATED.
- **Success ->** `teardown`.

## 13. teardown

Use Build's safe teardown path. `--require-merged-into <run-branch>` makes Build prove the helper
branch is already integrated before deleting the worktree and branch. The conductor still performs
the surrounding proofs immediately before teardown because ticket finalization and unexpected
workspace residue are part of this transaction, not just filesystem cleanup.

All four checks must hold immediately before the call:

```sh
git -C <primary> merge-base --is-ancestor <ticketSha> HEAD   # P1 integrated
git -C <lease> status --porcelain                            # P2 empty
git -C <lease> ls-files --others --exclude-standard          # P3 only the manifest + seededFiles
build get <ID> --json && build comments <ID> --json          # P4 Done, final comment present
build worktree teardown --ticket <ID> --require-merged-into <run-branch> --json
```

- **Any proof fails -> DO NOT TEAR DOWN.** Preserve the lease and its branch, record which proof
  failed on the ticket, report it. A retained lease costs disk; a forced teardown costs work that
  cannot be recovered.
- Escalated and blocked tickets are never torn down. Use `--force` only with explicit human approval.
- Tear down at the end of the wave, never between review and integration.

---

## Surface expansion (scheduling, not rework)

The implementer stops and reports when correct implementation needs a file outside the declared
surface. Expansion is allowed - predictions are imperfect - but it is a conductor DECISION, and
approving it in place is only safe when nobody else is in flight. Check the proposed added paths
against all four:

1. every ACTIVE peer's declared surface in this wave;
2. every PASSED-BUT-UNINTEGRATED peer's actual committed diff (`git diff --name-only <baseSha>..<ticketSha>`);
3. every REMAINING ticket's predicted surface, this wave and later;
4. the repo's `[waves]` serialization rules - a new path can trip a `global`, `cohesive-module`, or
   `pairwise` class with no exact file shared.

- **No conflict:** approve, record the widened surface (`run-backlog scope` ledger line), hand it
  back to the implementer, and re-check the fence against the widened list every round after.
- **Any conflict:** do NOT approve in place. Pause the ticket, preserve its worktree, let the
  conflicting peers finish or stop them at a safe state, and re-plan this ticket into a LATER wave
  with the widened surface as its `files` input to `build waves` -> BLOCKED-REPLAN.

An expansion that is explicit but unreplanned is still unsafe: it silently converts a disjoint wave
into an overlapping one, and the damage surfaces as an integration conflict two stages later.

## The rework contract

Each rule counters an observed failure - rework after rework, each round growing the diff until
scope creep blew the ticket up. They only work as a set.

1. **One gate, defined once, in config.** `build gate --ticket <ID> --require-checks --json` is the
   ONLY gate command in the loop, for every actor at every stage, identical by construction because
   they all read the same `[[review.checks]]`. Never restate toolchain commands in prose, an agent
   definition, or a ticket. Never tell a worker to "scope the gate to what you touched" - that is
   judgment, and two agents make it differently; scope is `--role`, which is configuration. Two
   parallel workers colliding on a fixed resource is a CONFIG problem: make the check derive its
   resource per-worktree, or set `[waves].cap = 1`.
2. **Four distinct meanings of green.** `checksConfigured: false` -> abort the run, not a pass.
   Baseline red -> do not implement. Per-worktree green -> eligible for review then commit, NOT for
   Done. Integrated-tree green -> eligible for finalization.
3. **Findings are numbered and ranked.** Each is `blocking` - it violates a named acceptance
   criterion, fails a gate check that was green in the baseline, or breaks a stated review
   invariant - or `non-blocking`, which is everything else. **Only blocking findings cause
   rework**; non-blocking ones are recorded or filed as follow-ups and the ticket proceeds. A
   reviewer that cannot name the criterion, check, or invariant a finding violates does not have a
   blocking finding.
4. **The first-round list is closed.** In round N the reviewer may only re-check round N-1's
   numbered findings and flag genuine NEW defects introduced by the rework diff. Reviewer drift
   loops as hard as implementer drift.
5. **Semantic rework preserves the contract.** Rework addresses only the numbered reviewer findings;
   it does not reinterpret the execution contract. If a plan or contract defect needs a different
   authority, shortcut, surface, or test, the conductor amends the contract before more implementation.
   If one semantic miss repeats after rework, the conductor inspects the evidence directly or escalates;
   do not start another generic rework round on the same unresolved semantic decision.
6. **Three rework rounds maximum.** Round N addresses ONLY round N-1's blocking findings; scope is
   re-checked after every round. After three failed rounds STOP - hand over the diff, the full
   finding history, and the gate output, and leave the worktree standing. Escalation is a
   successful outcome of this contract.

## Failure behavior, in one place

| Failure | Ticket ends at | Git side | Next |
| --- | --- | --- | --- |
| lease collision / install failure | unchanged | lease preserved if any | ABORTED-PRECLAIM, recovery triage |
| `checksConfigured: false` | unchanged | lease preserved | abort the WHOLE run |
| baseline red | unchanged | lease preserved | ABORTED-PRECLAIM; broken base is its own ticket |
| semantic contract missing / authority or parent-intent conflict | In Progress | lease preserved, no commit | ESCALATED before code edits |
| worker committed (`HEAD != baseSha`) | In Progress | nothing touched | ESCALATED |
| three failed rework rounds | In Progress | lease preserved, no commit | ESCALATED |
| surface expansion conflicts with a peer | In Progress | lease preserved | BLOCKED-REPLAN into a later wave |
| commit creation fails | In Progress | index left as-is, inspected | ESCALATED |
| commit ledger comment fails | In Progress | commit exists on the lease branch | retry, then ESCALATED |
| rebase conflict at integrate | In Review | `rebase --abort`, run branch unchanged | BLOCKED-INTEGRATION; human chooses deliberate rebase/re-review, replacement implementation, or leave blocked |
| ancestry proof fails | In Review | run branch untouched | ESCALATED |
| merged gate red | In Review | merge stays, NOTHING reverted | ESCALATED; halt integration for the wave |
| finalization invariant unsatisfied | In Review | integrated | ESCALATED |
| final comment fails / absent on read-back | In Review | integrated | retry, then ESCALATED - never transition |
| transition to Done fails | In Review | integrated | retry, then ESCALATED |
| a peer fails after others passed | passed peers stay In Review | their commits stay on their lease branches | integrate the peers ONLY while the run branch is green; otherwise all wait |
| a dependent ticket is unblocked | - | - | eligible only after its prerequisite reaches Done, never after a worktree PASS |

Two behaviors are prohibited everywhere in this table: resolving a conflict by guessing, and
reverting or resetting work the conductor did not create in this transaction.

## Enforcement

ALL git-history writes and ALL ticket-state changes stay with the conductor, in the PRIMARY
worktree. Workers in leased worktrees are read-implement-gate only.

Where the host supports a pre-tool hook, back this structurally: key on "is the caller in a linked
worktree" (a worker) versus the primary tree (the conductor), and deny worker commit / branch /
push / stash / reset / ticket-mutation / workspace-lifecycle calls. This is why the conductor
commits with `git -C <lease>` FROM the primary tree rather than by `cd`-ing in.

The hook is defense in depth, not the contract. Hosts without hooks enforce the same rules in
prose. Two limits are worth stating plainly: a worker whose working directory is the primary tree
is not detectable as a worker, so workers MUST be launched with the lease as their working
directory; and the config boundary is not a privilege boundary - see the README's "Configuration
and credential boundary".
