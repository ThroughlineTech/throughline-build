# SOP bundle: binary-hosted agent workflows

Status: proposal, revision 2. Written 2026-08-01, revised the same day after an
adversarial review. Not a shipped commitment.

Revision 2 changed the design in five places: a version contract in
`.build/conductor.toml`, a fail-closed stub protocol, the embedded catalog rather
than the manifest as install authority, admission as a run mode rather than a
flag, and a third column in the rules table separating rules Build enforces from
rules the brief states. The review that produced these is summarized under
[Review outcomes](#review-outcomes).

This proposes moving the agent-facing workflow bundle (`run-backlog`, `cross-impact`)
out of loose markdown and JavaScript on disk and into Build as embedded resources
plus deterministic install verbs.

## Problem

The workflow bundle today is a directory of markdown, a bash installer, a
JavaScript validator, and a set of zip archives. A survey of the two development
machines on 2026-08-01 found:

| Artifact | Machine A (fubar) | Machine B (plympton) |
| --- | --- | --- |
| `run-backlog-fanout-kit` (24 files) | present | present, byte-identical |
| `~/src/AGENTS-run-backlog-instructions.md` | present | present, byte-identical |
| Codex skill installed | yes, matches kit | yes |
| Claude global install | none | none |
| Shared consumer repo (bike parking) | fan-out generation: repo-local worktree scripts, integrator agent, deny hook | serial generation: contract version marker, installed procedure copy |
| Contract validator (`run-backlog-candidate.mjs`, 1113 lines + 1226 lines of tests) | absent | present, untracked |
| Snapshot archives | 2 tarballs | 4 zips plus one `.before-` backup |
| Bundle under version control | no | no |

Three findings matter.

1. The shared substrate is already consistent. The kit and the global procedure
   file are byte-identical across machines. The kit is not the source of drift.
2. Drift lives entirely in what was installed into repositories, and when. The
   same repository runs two incompatible architectures depending on the machine,
   because the procedure was copied into repositories rather than referenced.
3. There has never been a Claude-side install. `install-run-backlog-skill.sh`
   writes only to `$CODEX_HOME/skills`, and `verify-kit.sh` checks only the Codex
   validator. Claude reaches the workflow through per-repository command files
   that point at a separate, older procedure document. Claude and Codex are not
   near parity; they run different generations.

A secondary finding: the JavaScript validator spends roughly a third of its
length on a fence-aware scan of a markdown file's first thirty lines to confirm
a `RUN_BACKLOG_CONTRACT_VERSION: 3` string, plus symlink-escape checks on the
entry path. The rules it encodes are sound. The parsing machinery exists only
because the contract is stored as prose. A typed configuration file read by Build
does not need a bespoke parser to prove it exists.

## Design

Three layers. The boundary between them is the whole proposal.

### Layer 0: Build

Build gains deterministic SOP verbs and a typed configuration section. No verb in
this layer starts a worker agent, so all of it stays callable from inside an agent
session and does not trip the nested-session guard described in
[AGENTS.md](../../AGENTS.md).

The SOP content itself ships as embedded resources, following the pattern already
used by `ThroughlineBuild.Briefs`, `ThroughlineBuild.Commands`, and
`ThroughlineBuild.Scaffold`. Build does not read the bundle from disk; it emits
the bundle from itself.

The consequences are the point of the design:

- The bundle version is the binary version. There is no second artifact that can
  be at a different version, which is the precise failure mode observed above.
- Portability is copying one binary. No kit directory, no archive, no sync step.
- Upgrading is replacing the binary and running one verb.

### What embedding does not fix

Embedding removes kit-versus-archive drift. It does not remove binary-versus-repo
drift. Two machines with different Build versions can still run different
procedures against the same repository, because the committed stub invokes
whatever `build` is on `PATH` at call time.

The bundle therefore needs an explicit version contract that fails closed.
`.build/conductor.toml` declares the minimum Build version the repository's SOPs
require:

```toml
[conductor]
min_build_version = "0.9.0"
```

`build sop brief` refuses to emit when the running binary is older than the
declared minimum. The declaration lives in the tracked conductor file rather than
in the stub for two reasons: the stub stays inert and never needs upgrading, which
is the property that makes upgrade equal to replacing the binary; and the version
contract is then typed data that `build sop doctor` validates alongside everything
else, rather than a string in prose that has to be maintained by hand.

The accepted cost: an urgent procedure fix requires publishing a binary. There is
no override path, because an override is a second artifact and a second artifact is
the failure mode this design exists to remove.

### Layer 1: per-host stubs

Claude and Codex discover capabilities differently and cannot share one file.
Build writes a small stub for each host. The stub does not contain the procedure.
It names the SOP and defers to the binary:

```
.claude/commands/run-backlog.md      Claude, slash-invoked
.agents/skills/run-backlog/SKILL.md  Codex
```

Claude uses a command rather than a skill deliberately: invocation is explicit,
so an SOP cannot be triggered by description match during unrelated work.

A stub body is approximately ten lines and reduces to "run
`build sop brief run-backlog --json` and follow what it returns." Because the
procedure arrives from the binary at call time, stubs do not change when the
procedure changes. Upgrade is therefore genuinely just the binary.

Stubs are committed to each repository. They contain no machine-specific paths,
so a fresh clone on any machine works without an install step.

The stub must fail closed. Its instruction to the agent is: run the brief, and if
`build` is missing, not executable, exits nonzero, does not recognize the SOP, or
reports that the repository's `min_build_version` is unsatisfied, then stop and
report the failure. There is no fallback to cached prose and no degraded mode. An
agent that cannot obtain the current procedure must not improvise one.

A committed stub is repository content, so after installation it is editable by
anyone with commit access. `build sop doctor` and `build sop status` therefore
validate every emitted stub byte-for-byte against the embedded catalog and report
any difference as drift. Stubs are deliberately not customizable; the whole point
of a stub is that it carries no decisions.

### Layer 2: per-repository data

The values that differ per repository become typed data rather than prose.

Today those values live in a `## Conductor inputs` markdown section that an agent
can only interpret. Moving them to TOML means Build can validate them, fail loudly
on a missing gate list, and hand the agent a JSON structure instead of a document
to read.

This layer is what makes one generic procedure work across an iOS repository, an
Android repository, a .NET repository, and a TypeScript repository. The procedure
is identical; the data is not.

`.build/config.toml` is gitignored because it holds the Plane API token. Conductor
data has no secrets and must travel with the repository, so it needs its own file:

- `.build/conductor.toml`, tracked. Gates, source roots, review invariants,
  branch prefix, review-model escalation rule, constellation.
- `.build/config.toml`, gitignored, unchanged. Backend, workspace, credentials.

## Command surface

```
build sop list                    catalog of embedded SOPs and versions
build sop install [--sop <name>]  emit stubs, scaffold conductor.toml, write manifest
build sop upgrade                 re-emit changed stubs, preserve local edits
build sop uninstall [--sop <name>] remove manifested files still matching their hash
build sop status                  installed SOPs, versions, drift against embedded content
build sop doctor                  validate conductor.toml; fail on empty or unrunnable gates
build sop brief <name> [--json]   emit the procedure the stubs defer to
build sop adapt <name>            scaffold conductor.toml for an agent to fill in
```

`install`, `upgrade`, and `uninstall` are the only mutating verbs. `brief` is the
hot path: every SOP invocation calls it.

## Configuration schema

Sketch, not final. `build sop doctor` is the authority once implemented.

```toml
[conductor]
min_build_version = "0.9.0"
branch_prefix = "bkfk2"
ticket_prefix = "BKFK2"
source_roots = ["apps/api/src", "apps/web/src", "packages/contracts"]
architecture_map = "docs/state-of-the-system/00-index.md"

[[conductor.review.invariants]]
id = "wire-contract-authority"
statement = "packages/contracts/openapi.yaml is the single wire authority; a shape change updates the YAML, regenerates generated.ts, and stays back-compatible"
paths = ["packages/contracts/**"]
blocks_done = true

[[conductor.review.invariants]]
id = "migration-types-lockstep"
statement = "a D1 migration ships with apps/api/src/db/types.ts in lockstep"
paths = ["migrations/**", "apps/api/src/db/types.ts"]
blocks_done = true

[conductor.review.escalation]
model_size = "large"
paths = ["packages/contracts/**", "apps/api/src/auth/**", "migrations/**"]

[constellation]
platform = "web"
contract_authority = "packages/contracts/openapi.yaml"

[[constellation.siblings]]
platform = "ios"
path = "../rejog-lending-ios"
ticket_prefix = "RJLI"
```

Gates are not duplicated here. They remain `[[review.checks]]`, which
`build gate` already consumes and which
[bring-your-own-conductor.md](../bring-your-own-conductor.md) documents.
`build sop doctor` asserts that the list is non-empty and that each check has a
runnable command, which closes the empty-gate hazard by construction rather than
by vigilance.

Review invariants are structured prose, not typed values. The `statement` is a
sentence of judgment and no schema can evaluate its truth. What the schema buys is
real but bounded: `doctor` can require that invariants exist, that each has an id
and a non-empty statement, and that `blocks_done` entries are surfaced in the
brief; and `paths` lets the brief tell a reviewer which invariants a given diff
actually implicates. The design should not claim more than that. Pretending
judgment is data is how the current prose-based system fails; over-schematizing it
would fail the same way with more ceremony.

## Install semantics

**The embedded catalog is the authority. The manifest is a cache.** The catalog is
compiled into the binary and lists, per SOP, every path the SOP owns, its class,
and for emitted files the expected content hash. `.build/sop-manifest.json` records
what a past install actually wrote and which binary version wrote it. A manifest is
mutable repository content and can be edited to bless modified files, so no
decision is ever taken on the manifest alone. Every comparison is against the
catalog; the manifest supplies history, not permission.

Installed paths come in two classes, and conflating them was a defect in the first
revision of this design.

- **Emitted.** Stubs. Catalog-owned, content-validated byte-for-byte, never
  customizable. A difference from the catalog is drift, not a customization.
- **Scaffolded.** `.build/conductor.toml`. Catalog-owned as a path, schema-validated
  as content, expected to be hand-edited. Never content-compared against the catalog
  after the initial scaffold.

Given that split:

- **install** is idempotent. An emitted path whose hash matches the catalog is left
  alone; a scaffolded path that already exists is never overwritten. A second run on
  an unchanged repository writes nothing and reports no change.
- **upgrade** rewrites only emitted paths whose catalog content changed since the
  recorded version. An emitted path that differs from both the old and new catalog
  content was edited locally: upgrade reports it and does not overwrite it. Scaffolded
  paths are schema-checked and never rewritten.
- **uninstall** removes only catalog-owned paths that are regular files, are not
  symlinks or reparse points, and whose content still matches the catalog. Anything
  else is reported and left in place.
- **status** reports missing catalog paths as drift rather than treating absence as
  clean, so a hand-deleted stub is visible instead of silently absent.

Before writing or removing any path, `build sop` resolves it and refuses symlinks,
reparse points, and any target not strictly below the repository root. The worktree
code already implements this shape of containment check and should be the model:
see `IsStrictlyBelow` and the manifest validation in
[WorktreeLeaseManager.cs](../../src/ThroughlineBuild.Helpers/WorktreeLeaseManager.cs).

Deleting the stub directories and `.build/conductor.toml` by hand still works as a
removal, and leaves a stale manifest; `status` reports the result as drift and
`install` restores from the catalog.

## Initial SOP catalog

**run-backlog.** Drive a repository's backlog one ticket at a time: claim,
implement, independently review, rework up to a cap, integrate, gate the merged
tree, finalize. The lifecycle authority is the existing `ticket-transaction.md`,
which becomes an embedded resource. Its per-ticket sequence is
commit, integrate, merged-gate, finalize, with In Review meaning a candidate
commit exists and is not yet proven integrated.

The current global procedure file states a shorter rule in its step D (pass means
commit and move to Done). That contradiction is resolved by deleting the global
file, not by reconciling two documents.

**cross-impact.** In a multi-repository constellation sharing one wire contract,
determine whether a change touches a sibling platform, answer from freshly pulled
sibling code read-only, and draft rather than silently create follow-up tickets in
the sibling's project. This SOP is the reason the design is generic: its entire
per-repository surface is the `[constellation]` table above. It has already been
run in production across a web, iOS, and Android trio.

Adding a third SOP is an embedded resource plus a schema section, not a new
distribution mechanism.

## Rules and their enforcement

The rules are retained. The loose files are not. The third column matters as much
as the second: a rule Build can mechanically enforce should not live only in
embedded prose, because prose is advice to an agent and code is a refusal.

### From the JavaScript validator

| Rule | Disposition | Enforced by |
| --- | --- | --- |
| Dependency-first topological scheduling; reject cycles | Already in `build waves` (`dependency_cycle`, exit 5) | binary |
| Reject unverified deps outside the selected scope | Present but weaker than assumed; see below | binary + brief |
| Conservative serialization when predicted files are uncertain | Already in `build waves` (`uncertain`, empty `files`) | binary |
| Strict containment below a declared worktree root | Already in `build worktree teardown` (exit 8) | binary |
| Manifest-backed teardown; reject unmanifested directories | Already in `build worktree` | binary |
| Existing branch, path, and manifest collision rejection | Already in `build worktree lease` (exit 6) | binary |
| Required-seed failure before any mutation | Already in `build worktree lease --require-seed` (exit 7) | binary |
| Partial-lease rollback deletes only what the attempt created | Already in `build worktree lease` | binary |
| Rollback deletes helper branches only at the recorded base SHA | Was absent; fixed under TLB-611 | binary |
| Live availability checks for leased resources such as TCP ports | Port; not currently in Build | binary |
| Admission-only inspection | Port as a run mode, not a flag; see below | binary |
| One ticket per serial invocation; no parent or epic expansion | Port | binary |
| `RUN_BACKLOG_CONTRACT_VERSION` fence-aware markdown scan | Replaced by the `min_build_version` schema check | binary |
| Entry-point symlink escape check | Retained in a different form; see below | binary |

Two rows changed on review and are worth stating plainly.

**Rollback branch deletion was a real defect, not a parity question.**
`RollBackCreatedLeaseAsync` deleted the helper branch guarded only by a
`branchCreated` flag and passed `force: true`, which defeats Git's unmerged-branch
protection. It never re-read the branch tip, so a branch that moved between creation
and rollback lost that work silently. Fixed under TLB-611: rollback now resolves the
branch tip and deletes only when it still equals the attempt's recorded base SHA,
using a non-force delete as a second barrier, and otherwise preserves the branch and
names it in the failure message.

`ChainWorktreeSweeper` uses the same `force: true` shape in two places. It was left
out of TLB-611's scope deliberately and still needs its own assessment of whether a
sweeper is entitled to discard unmerged work.

**The symlink check is not dropped, it moves.** The original concern was an
untrusted markdown entry file, and Build emitting the stub does remove that
specific input. But a committed stub is repository content that a later commit can
replace, including with a symlink, and install and uninstall both write and delete
paths. The containment and link refusal described under
[Install semantics](#install-semantics) is the same concern relocated to where it
still applies.

**Admission is a run mode, not a flag.** A flag qualifies one invocation. It is not
part of the scope identity, does not propagate into a spawned subagent, is not
carried in the ticket audit trail, and can simply be omitted from the next call.
Admission must instead appear in the SOP brief envelope as a mode carrying the
resolved SHA and an explicit verb policy: read-only verbs allowed; no parent or
epic expansion; no worktree lease or teardown; no ticket transition or comment; no
commit, branch, or push. Every mutating stage refuses while the mode is active.
Until cross-repository policy is designed, admission inspection roots must belong
to the invoking repository so one repository's tree is not judged against another
repository's conductor rules.

**`build waves` does not verify dependencies.** `verifiedExternalDeps` is asserted
by the caller. Build never reads the ticket system to prove a dependency is Done,
and supplying an id is not evidence. Conductor-side dependency verification, and
re-checking dependents before each wave rather than once at plan time, stay rules
in the brief. This row is not fully subsumed by the binary and the table should not
imply it is.

### From the lifecycle document

These are enforced today only as prose in `ticket-transaction.md`, which this design
embeds verbatim. Embedding preserves them, but several are mechanizable and should
migrate into the binary rather than remain advice.

| Rule | Enforced by |
| --- | --- |
| Clean primary worktree, no protected branch, no interrupted merge or rebase before any ticket mutation | binary (`sop doctor` precondition) |
| Baseline gate green and `checksConfigured` true before claim; claim only after lease and baseline | binary (`gate --require-checks`) |
| Candidate immutability across review, over tracked, cached, and untracked hashes | binary (`candidate status` exists as the primitive) |
| Explicit-path commit only; never `git add -A`, `git add .`, or `git commit -a` | binary (worker deny contract) |
| Worker deny contract: workers do not commit, branch, mutate tickets, or manage worktrees | binary where the host allows, brief otherwise |
| Serial integration by rebase plus fast-forward, never cherry-pick; merged-tree gate before Done | brief |
| Finalization invariants block Done exactly like a red gate | binary (`blocks_done` invariants) |
| Transaction-keyed ledger comments; comments from a prior run are not evidence for this one | brief |
| Declared surface is the scope fence; expansion is a scheduling decision | brief |
| Rework contract: numbered blocking findings, closed first-round list, cap of three rounds | binary (cap) + brief |

### Not yet audited

The validator's own rules remain unaudited. The review that produced this revision
ran on a machine where `run-backlog-candidate.mjs` is absent, so the reviewer
substituted rules from the lifecycle document. The list above is therefore a floor,
not a ceiling. A second pass with the validator present is required before the
rules table can be called complete, and TLB-610 should not be closed on the current
list.

## Retired on adoption

- `~/src/run-backlog-fanout-kit` and every packaged archive of it.
- `~/src/run-backlog-skill-source` (already drifted, no consumer).
- `~/src/AGENTS-run-backlog-instructions.md` and
  `~/src/AGENTS-cross-impact-instructions.md`, which become embedded resources.
- `run-backlog-candidate.mjs` and its test file, after the rules table above is
  discharged.
- Per-repository copies of the procedure under `docs/run-backlog/`.
- Repository-local fan-out scripts such as `scripts/backlog-worktree.mjs`.

`~/src/AGENTS.md` is unaffected. Notification policy, ASCII discipline, and the
do-not-merge rule are personal global conventions, not bundle content, and the
SOPs should not restate them.

## Interaction with TLB-599

TLB-599 has acceptance criteria requiring that adoption work update the portable
kit source, run the kit verifier, produce a fresh archive, and verify parity among
Build help, repository instructions, installed skill source, kit source, and the
packaged archive. That five-way parity obligation is a direct cost of the current
distribution model.

This proposal removes four of the five artifacts. If adopted, those criteria
should be replaced rather than satisfied.

## Resolved questions

The four open questions from revision 1 were settled by the review.

1. **Host stubs.** `build sop install` writes stubs for every host it knows about
   by default, with `--host` to narrow. A stub is inert to the host that ignores it.
2. **Brief output.** `build sop brief` emits a single JSON envelope: the SOP text,
   the repository's resolved conductor data, the schema version, the SOP and binary
   versions, the doctor result, owned catalog paths, and `runMode`. Admission
   `runMode` carries the resolved inspection root, inspection SHA, inherited
   environment values, and explicit verb policy. Admission roots are constrained
   to the invoking repository until cross-repository conductor policy is designed.
   One round trip, and the agent cannot act on the procedure without also
   receiving the configuration and mode it applies to.
3. **Rework cap.** The binary default is three rounds. A repository may tighten it
   in `[conductor]` and may not loosen it.
4. **Doctor as precondition.** Yes. `build sop brief` fails closed. Standard
   briefs run doctor first; admission briefs validate their pinned inspection
   root and SHA before doctor reads conductor data. An invocation cannot begin
   against invalid configuration or invalid admission identity.

## Review outcomes

Revision 1 was reviewed adversarially on 2026-08-01 with a mandate to disagree. The
review was substantially correct and changed the design. Recorded here so the same
ground is not re-argued.

Accepted and incorporated:

- Embedding does not eliminate binary-versus-repo drift; a version contract is
  required and must fail closed.
- Review invariants are structured prose, not typed values, and the schema must
  not pretend to validate judgment.
- Stub behavior on a missing, old, or SOP-unaware binary was unspecified, and the
  correct specification is fail-closed with no cached fallback.
- The manifest cannot be the install authority; the embedded catalog is.
- The symlink and containment concern survives, relocated to install and uninstall.
- Admission must be a run mode carried in the brief envelope, not a per-invocation
  flag.
- `build waves` does not verify dependencies against the ticket system, so
  conductor-side verification and per-wave recheck remain brief rules.
- Rollback branch deletion does not prove the recorded base SHA. Verified against
  the source and confirmed; it also passes `force: true`.

Modified rather than accepted: the version contract lives in
`.build/conductor.toml` as `min_build_version` rather than in the stub. Putting a
version string in a committed markdown stub reintroduces a hand-maintained artifact
and forfeits the property that stubs never need upgrading.

Settled and not to be relitigated: embedded resources as the distribution
mechanism, thin stubs, separate Claude and Codex adapters, gates staying in
`[[review.checks]]`, and retiring the loose archive kit once the contracts above
are ported.
