# TLB-606: Risk-tiered gate policy

## Scope and recommendation

This is a docs-only investigation. It analyzes the run-backlog procedure from
`ticket/TLB-617-migrate-consumers` at commit `e415ebffdf910fc0e4bfa0a664c335ee60339035`,
the gate implementation on the TLB-606 branch, and the BKFK2 transcript evidence
stored under `.agent-instruction-compaction/`.

Recommendation: keep the strong gate path at all five run-backlog locations:
baseline, implementer, independent reviewer, rework, and merged tree. Do not
introduce a lighter default for any ticket class in this ticket.

The reason is not that every ticket has the same risk. The reason is that the
current workflow has no reliable risk-to-gate policy, and the five gates prove
different facts about different trees and failure stages. The repository's
`CheckSpec` has execution, timeout, role, canary, and required-path data, but no
risk tier or ticket-class field (`src/ThroughlineBuild.Contracts/Verifier/CheckResult.cs:17-26`).
An implicit lighter path would therefore be judgment hidden in the conductor or
worker, which would be neither deterministic nor auditable.

The current repository `.build/config.toml` has no `[[review.checks]]` entries.
That is itself a safety issue: `build gate --require-checks` must fail when no
checks are configured, while a gate without `--require-checks` can report a
decorative green result. The standalone gate implements that distinction in
`src/ThroughlineBuild.Cli/GateCommand.cs:29-64`; the chain gate also treats the
absence of a gating check as a vacuity/integrity failure in
`src/ThroughlineBuild.Phases/GatePhase.cs:104-117`. A passing empty gate is not
evidence for this analysis or for a future risk profile.

## The five gate locations

The procedure is embedded on the SOP branch, not recreated on `main`. Its five
locations are intentionally distinct.

### 1. Baseline gate before claim

The baseline stage is defined at
`src/ThroughlineBuild.Cli/Sops/run-backlog/ticket-transaction.md:161-182`.
The conductor runs:

```sh
build gate --ticket <ID> --require-checks --json
```

The lease must be green before the ticket is claimed. `checksConfigured == false`
aborts the whole run, and a red base is treated as a base defect rather than
something for the implementer to repair. This preserves attribution: later
failures are changes introduced after a known-green base.

The CLI gate selects checks by role and treats a selected check as passing only
when it is non-blocking or passed without being inconclusive or skipped
(`GateCommand.cs:68-82`). Required paths missing from a check produce an
inconclusive result through `AutomatedChecksRunner` rather than an unexplained
green result (`AutomatedChecksRunner.cs:112-139`).

### 2. Implementer gate

The implement stage requires the implementer to run the exact configured gate
before returning, as specified at
`ticket-transaction.md:207-220`. This catches straightforward build, test, and
typecheck regressions while the implementer still has the working context needed
to fix them. It is not permission to commit or mutate the ticket.

The built-in chain wires the same check set into `GatePhase` after implementation
(`src/ThroughlineBuild.Cli/CliApplication.cs:2360-2388` and
`src/ThroughlineBuild.Phases/ImplementReviewLoop.cs:337-463`). `GatePhase` runs
the configured checks against the warm implementation worktree, hard-fails only
gating-role failures, and preserves advisory results for review. A green
implementer gate makes a ticket eligible for review; it does not prove the
integrated tree or authorize Done.

### 3. Independent reviewer gate

The independent-review stage is defined at
`ticket-transaction.md:243-279`. A fresh reviewer receives the actual diff,
cannot edit it, reruns the exact gate, and makes a separate judgment. The
candidate fingerprint is checked before and after review, so a reviewer-side
write or git mutation cannot silently change what is committed.

The normal chain's `ReviewPhase` runs the configured checks and then invokes the
verifier (`src/ThroughlineBuild.Phases/ReviewPhase.cs:207-238`). The composition
can pass the already-collected gate results to avoid executing identical checks a
second time in that built-in chain (`src/ThroughlineBuild.Cli/CliApplication.cs:2401-2409`),
but that optimization does not weaken the separate reviewer judgment in the
run-backlog procedure. The procedure's reviewer gate remains a required,
read-only, independently observed stage.

### 4. Rework gate and recheck

The rework contract requires one exact gate command for every actor and stage,
limits rework to numbered blocking findings, closes the first-round finding list,
and caps the loop at three rounds (`ticket-transaction.md:450-477`). This prevents
both unchecked rework and a reviewer from continually expanding scope.

The current chain has a targeted recheck before the next full gate. It reruns
only the named failed checks and stops if they still fail
(`src/ThroughlineBuild.Phases/ImplementReviewLoop.cs:240-335`). It then runs the
full `GatePhase` again for the reworked tree (`ImplementReviewLoop.cs:337-463`).
The targeted recheck is an early diagnostic, not a lighter approval profile;
the full gate remains the approval boundary for the round.

### 5. Merged-tree gate

After ancestry-preserving integration, the conductor runs the gate in the primary
worktree at `ticket-transaction.md:317-378`, especially lines 359-378. A green
lease gate does not prove that the run branch is green. The merged-tree gate
checks the actual post-rebase, fast-forward tree before finalization. A red result
leaves the integrated work in place, stops further integration, and escalates; it
does not auto-revert or reset a shared branch.

Fan-out preserves this rule: commit, integrate, merged-gate, and finalize are
serial even when implementation was parallel, and a merged-gate failure stops
the wave (`fan-out-scheduling.md:61-87`).

## Risk classes that must keep the strong path

The following classes must retain all five gate locations and the independent
review contract:

| Risk class | Why a lighter path is unsafe | Repository evidence |
| --- | --- | --- |
| Contract, schema, serialization, and shared API changes | A small shape or normalization defect can make dependent tickets implement against the wrong contract. A green local build does not prove consumers agree. | The BKFK2-402 review found malformed duplicate-conflict entries and unresolved-conflict publication, then a digest-format hole. The report records these findings at `.agent-instruction-compaction/backlog-run-analysis-report.md:123-152`. |
| Migration and persistence changes | A migration can compile while violating row ownership, transaction, count, or retirement invariants. The merged tree is the only place to prove the migration change integrates with its consumers. | BKFK2-404's reviewer found pointer advancement even when rows did not stage/promote; the rework added a count precondition and a regression test (`backlog-transcript.txt:1620-1717`). |
| Authentication and authorization | Permission errors and bypasses are behavioral and may not be visible in a basic compile or unit check. Independent review and all configured negative checks must remain. | The procedure's reviewer invariants and the BKFK2 reviewer prompts preserve auth gates as explicit invariants; the transcript records auth among the protected repository surfaces. |
| Publication, deployment, external mutation, and irreversible behavior | A green implementation worktree cannot prove that the integrated path preserves the no-publication/no-deploy boundary or that a one-way action is correctly fenced. | BKFK2-404 was migration/publication work and still used strong review plus merged-tree gating; its transcript explicitly prohibited migration application, remote publication, and deployment (`backlog-transcript.txt:1493-1505`, `1600-1604`). |
| Shared state, derived state, freshness, status, lifecycle, and cache behavior | The defect may be in an interaction between tickets or in a state transition rather than in one changed file. Serial integration and a final gate are required to test the actual run tree. | The SOP requires dependency verification, ancestry proof, finalization invariants, and a merged-tree gate (`ticket-transaction.md:89-107`, `317-399`). |
| Cross-ticket, parent-child, or shared-module changes | Parallel or lighter validation can allow a dependent ticket to consume an unreviewed contract or can hide an integration conflict. | BKFK2-402 defined a contract consumed by BKFK2-403 and BKFK2-404; the analysis says serial execution avoided building later work on unstable assumptions (`backlog-run-analysis-report.md:112-142`). |

These categories include the categories named by TLB-606: contract, migration,
persistence, auth, publication, irreversible behavior, and shared API changes.
They also cover adjacent derived-state and lifecycle changes because their
failure modes are the same: a local green result is not proof of the integrated
behavior or of the coordination obligation.

## Low-risk candidates considered

Pure spelling, formatting, or isolated comment changes are the closest possible
low-risk class. They are not a safe default lighter tier here:

- A documentation or help change can change how a conductor performs a
  destructive or lifecycle operation, so its semantic risk is not determined by
  file extension.
- A supposedly isolated change can touch a shared generated artifact, snapshot,
  instruction contract, or configuration surface.
- The current config and contract have no auditable risk-tier selection. A worker
  or conductor deciding that a ticket is low-risk would bypass the deterministic
  gate contract.
- The BKFK2 record shows that the expensive gates found defects in tickets that
  initially looked bounded. The measured report estimates eight visible gates and
  approximately 22 total gate invocations, while also recording real defects
  caught by strong review and rework (`backlog-run-analysis-report.md:59-81`,
  `223-243`).

Conclusion: no low-risk class is approved for a lighter profile by this ticket.
The strong path is conservative, but it is the only path whose meaning is
currently explicit and consistent across ticket classes.

## Failure-mode analysis

| Failure | Strong-path catch today | Would a lighter profile preserve the catch? |
| --- | --- | --- |
| Bad base | Baseline runs before claim and refuses implementation on a red base (`ticket-transaction.md:161-182`). If a later gate fails, `GatePhase` can rerun the failed gating checks against the untouched base through `GateControlProber` (`GatePhase.cs:195-289`); a base failure is classified as an environment failure without spending rework rounds. | No. Removing the baseline loses attribution and can send a worker to repair unrelated breakage. Removing the control run makes base/config failure look like ticket failure. |
| Bad implementation | The implementer gate and the full post-implementation gate execute configured checks. Gating failures become numbered rework findings; advisory failures remain visible to the reviewer (`GatePhase.cs:126-183`, `337-463`). | No. A lighter implementation gate can defer a cheap, actionable failure into review or integration and consume more expensive context while obscuring attribution. |
| Bad rework | The targeted recheck reruns named failed checks, the full gate reruns the complete configured set, and the procedure caps rework at three rounds (`ImplementReviewLoop.cs:240-335`, `ticket-transaction.md:450-477`). | No. Skipping either the targeted recheck or the full round gate allows a fix to regress the original failure or introduce a new one without a stage-level result. |
| Bad review judgment or reviewer mutation | The reviewer is independent and read-only, reruns the gate, and is followed by candidate fingerprint and scope checks (`ticket-transaction.md:243-279`). A changed tree or reviewer-side mutation is detectable. A semantic miss with no corresponding negative test is not guaranteed to be caught by any automated gate; this is a known detection limit, not evidence that gates are redundant. | No. The only mitigation for the semantic-miss case is to preserve strong independent review, focused negative tests, and the later merged-tree gate. A lighter profile would reduce the remaining independent chances to detect it. |
| Bad integration | Integration requires rebase, fast-forward, and ancestry proof, then the merged-tree gate runs in the primary tree (`ticket-transaction.md:317-378`). A red merged gate stops the wave and preserves the branch for human triage. | No. A per-worktree green result cannot catch a conflict, post-rebase interaction, or combined-tree failure. The merged-tree gate cannot be removed or inferred from ancestry. |

The bad-review row is intentionally explicit about the limit of testing: no gate
can guarantee detection of a semantic defect that no check or reviewer recognizes.
The correct response is stronger contracts, independent review, negative tests,
and preserved gates, not a weaker tier.

## Explicit and auditable future policy, if revisited

No config, help, or instruction behavior is changed by TLB-606. If a future
ticket revisits profiles, it should satisfy all of these constraints before any
lighter profile is allowed:

1. Profiles must be named configuration data with an explicit conductor choice;
   never infer a profile from ticket labels, file extensions, size, or worker
   opinion.
2. The default must remain the strong profile. A missing, unknown, or empty
   profile must fail closed, as `--require-checks` already does for empty check
   configuration.
3. The selected profile must enumerate its exact checks, roles, canaries, and
   required paths. The gate JSON and ticket evidence must record the profile and
   resolved check names so an auditor can reproduce the decision.
4. Baseline and merged-tree gates must remain mandatory for every profile. A
   profile may not turn a missing check into green or treat an inconclusive check
   as passed.
5. Any proposed reduction must be tested by failure injection for bad base, bad
   implementation, bad rework, bad review mutation, and bad integration. If any
   catch is lost, the proposal fails its own analysis.

This is a design boundary for a separate ticket, not an implementation proposal
for TLB-606.

## Final conclusion

Repeated gates are expensive, as the BKFK2 report demonstrates, but the evidence
shows that the process paid for itself: contract defects were found in BKFK2-402,
publication atomicity was found in BKFK2-404, and both were reworked before
commit/integration. The safe optimization target is prompt and evidence
compression, not gate removal. TLB-606 therefore recommends no lighter gate
class and makes no change that reduces gate execution.
