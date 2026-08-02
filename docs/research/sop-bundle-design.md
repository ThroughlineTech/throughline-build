# SOP bundle: binary-hosted agent workflows

Status: proposal. Written 2026-08-01. Not a shipped commitment.

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
branch_prefix = "bkfk2"
ticket_prefix = "BKFK2"
source_roots = ["apps/api/src", "apps/web/src", "packages/contracts"]
architecture_map = "docs/state-of-the-system/00-index.md"

[conductor.review]
invariants = [
  "packages/contracts/openapi.yaml is the single wire authority",
  "a D1 migration ships with db/types.ts in lockstep",
]

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

## Manifest semantics

`build sop install` writes `.build/sop-manifest.json` recording, for every emitted
path: the path, a content hash, the SOP that owns it, and the binary version that
wrote it.

- **install** is idempotent. A path whose hash matches its manifest entry is left
  alone. Re-running install never changes a correctly installed repository.
- **upgrade** rewrites only paths whose embedded content changed since the recorded
  version. A path whose on-disk hash does not match its manifest entry was edited
  locally; upgrade reports it and does not overwrite it.
- **uninstall** removes only manifested paths whose on-disk hash still matches.
  Anything edited locally is reported and left in place. Nothing unexpected is
  ever deleted.

Deleting the stub directories and `.build/conductor.toml` by hand is equivalent
to uninstall, minus the report.

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

## Rules ported from the existing validator

The JavaScript validator's rules are retained. The file is not. Each rule becomes
C# with tests.

| Rule | Disposition |
| --- | --- |
| Dependency-first topological scheduling; reject cycles | Already in `build waves` (`dependency_cycle`, exit 5). Verify parity. |
| Reject unverified dependencies outside the selected scope | Already in `build waves` (`verifiedExternalDeps`). Verify parity. |
| Conservative serialization when predicted files are uncertain | Already in `build waves` (`uncertain`, empty `files`). Verify parity. |
| Strict containment below a declared worktree root | Already in `build worktree teardown` (exit 8). Verify parity. |
| Manifest-backed teardown; reject unmanifested directories | Already in `build worktree`. Verify parity. |
| Existing branch, path, and manifest collision rejection | Already in `build worktree lease` (exit 6). Verify parity. |
| Required-seed failure before any mutation | Already in `build worktree lease --require-seed` (exit 7). Verify parity. |
| Partial-lease rollback deletes only what the attempt created | Already in `build worktree lease`. Verify parity. |
| Delete helper branches during rollback only at the recorded base SHA | Verify; port if absent. |
| Live availability checks for leased resources such as TCP ports | Port. Not currently in Build. |
| Admission-only inspection: pinned SHA, mutations hard-blocked | Port as a flag with a resolved-SHA precondition, not a scope-string dialect. |
| One ticket per serial invocation; parent and epic expansion disabled | Port into `build sop doctor` or the run-backlog brief. |
| `RUN_BACKLOG_CONTRACT_VERSION` fence-aware markdown scan | Drop. Replaced by a TOML schema Build reads directly. |
| Entry-point symlink escape check | Drop. Build emits the stub; there is no untrusted entry file to resolve. |

The two dropped items are dropped because the design removes the problem they
solved, not because the concern was invalid.

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

## Open questions

1. Should `build sop install` write stubs for every host it knows about, or only
   for hosts named on the command line? Writing both by default is simpler and
   the stubs are inert to the host that ignores them.
2. Does `build sop brief` emit one document per SOP, or a document plus the
   repository's resolved conductor data as a single JSON envelope? The second is
   fewer round trips and is probably correct.
3. Where does the run-backlog rework cap live: embedded procedure, or
   `[conductor]` so a repository can tighten it?
4. Should `build sop doctor` run as a precondition inside `build sop brief`, so an
   invocation cannot begin against an invalid configuration?
