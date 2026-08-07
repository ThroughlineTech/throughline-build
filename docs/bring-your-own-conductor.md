# Bring your own conductor

`build chain` is the batteries-included topology: Build owns the deterministic
workflow and starts a fresh configured worker for each judgment phase.

An operator can instead keep one long-lived agent session as the conductor. The
conductor uses the ticket CRUD verbs (`list`, `get`, `comments`, `comment`,
`evidence`, `transition`, and `amend`) and asks Build to own deterministic resources. The
`worktree` verb is the isolated-workspace primitive for that topology. The
`gate` verb runs the repository's configured review checks in that workspace.
The `waves` verb plans which selected tickets can safely run concurrently.
The `candidate status` command fingerprints the current candidate worktree so a
conductor can prove the reviewed tree is the tree it is about to commit.
The `sop` command family lists binary-hosted SOPs, validates machine-local
conductor data, and emits the brief envelope that host stubs consume.
`profile prompt` emits the canonical repository-interrogation rules and
`profile apply` persists a supplied PROJECT_PROFILE without starting a worker.
`profile verify-canaries` is a separate explicit opt-in operation that proves
the proposed gating checks in a temporary worktree without changing config.
`conductor prompt` and `conductor apply` are the same shape for review
invariants, the one conductor.toml fact left entirely to human judgment:
`conductor prompt` emits invariant-authoring rules and `conductor apply` reads
back TOML containing 2-5 `[[conductor.review.invariants]]` blocks and splices
that run in place, preserving every other section byte-for-byte.
`conductor apply` rejects the scaffold's placeholder sentence, prose, Markdown
fences, and any TOML section other than the invariants it owns.
They load only the repository configuration sections they consume and do not
require `[ticketing]`, `[workers]`, or `[events]`, resolve ticketing secrets, or
construct a Plane client. Missing ticketing credentials therefore do not block
`worktree`, `gate`, `waves`, `candidate status`, `sop`, `profile prompt`,
`profile apply`, `conductor prompt`, or `conductor apply`. None of these start
a worker or execute a canary. Other commands still require the full ticketing,
worker, and event configuration.

The `worker brief` command materializes a role-specific Markdown artifact from
ticket and worktree evidence for an agent to inspect. It is the exception in
this conductor group: it reads the source ticket, so it requires the full
configuration and ticketing credentials.

## Configuration

Binary-hosted SOPs read machine-local conductor data:

```toml
[conductor]
min_build_version = "0.1.0"
branch_prefix = "ticket"
ticket_prefix = "TLB"
source_roots = ["src", "tests", "docs"]
architecture_map = "docs/throughline-build-architecture.md"
rework_cap = 3

[[conductor.review.invariants]]
id = "aot-json"
statement = "CLI JSON output uses source-generated JsonSerializerContext."
paths = ["src/ThroughlineBuild.Cli/**"]
blocks_done = true

[[conductor.review.invariants]]
id = "semantic-ticket-contract"
statement = "Semantic-risk tickets record a binding execution contract before code edits."
blocks_done = true

[conductor.review.escalation]
model_size = "large"
paths = ["src/ThroughlineBuild.Cli/**"]

[constellation]
platform = "dotnet-cli"
contract_authority = "src/ThroughlineBuild.Contracts"
```

`.build/conductor.toml` is ignored, machine-local, and contains no secrets;
recreate it in each clone through `build install` or `build sop install`. It
carries the minimum Build version, branch and ticket prefixes, source roots,
architecture map, review invariants, review escalation rule, rework cap, and
constellation.

`.build/config.toml`, by contrast, is tracked: it carries repository facts -
`[[review.checks]]`, `[[ship.regression_checks]]`, `[waves]`, `[worktree]` -
that must travel with a clone rather than be re-derived per machine. It never
holds a literal Plane token by default; `plane_api_token_env` (or
`plane_api_token_file`) is the template's active key, and `sop doctor`/`setup
--check` both flag a tracked file that has a literal `plane_api_token` set.
See `docs/new-repository-plane-run-backlog-setup.md` for the full setup flow.

`.build/` belongs to the clone, not to a worktree. A linked worktree, including
every worktree the conductor cuts under `.worktrees/`, holds no copy of it, so
`sop doctor`, `sop brief`, and config loading resolve conductor and config data
from the clone's main worktree and report the same result there as in the
worktree. Install the SOP once per clone; nothing seeds `.build/` per worktree.
Emitted host stubs are the exception: they are tracked repository content, so
they are validated in the tree the verb runs in. Resolution is bounded by the
repository, so a tree whose repository holds no `.build/config.toml` reports it
missing rather than adopting an unrelated ancestor's config.
Run `build sop list [--json]` to report available embedded SOPs and their binary
versions. Run `build sop install [--sop <name>] [--host claude|codex] [--json]`
to emit host stubs, scaffold a missing `.build/conductor.toml`, and write
`.build/sop-manifest.json`. By default install emits every known host stub;
`--host` narrows emitted stubs to Claude or Codex while still including shared
scaffolded paths.

A scaffolded `conductor.toml` is not a template filled with generic values -
install derives what it can from the repository itself, deterministically, with
no worker and no engine code that special-cases a language or framework:
`ticket_prefix` from the configured Plane project identifier, `source_roots`
from `git ls-files`'s tracked top-level directories excluding the ones Build
itself owns (`.build`, `.claude`, `.agents` - tracked since the gate started
travelling with the clone, but Build's output rather than the repository's
source), `branch_prefix` from the most common `<prefix>/...` segment among
distinct local and remote-tracking branch names (remotes are read too, so a
fresh clone - which has exactly one local branch - still derives what its origin
derived; falling back to Build's own `"ticket"` convention when no branch
reveals a convention), and `architecture_map`
from the first tracked file matching a known architecture/contributor-doc name
(`docs/architecture.md`, `ARCHITECTURE.md`, `docs/contributing.md`,
`CONTRIBUTING.md`, `AGENTS.md`, `README.md`, checked in that order). Facts that
need real judgment instead of file-system inspection - `constellation.platform`
and `constellation.contract_authority` - are resolved from the same
repository-interrogation pass as the toolchain profile, but only inside the
`build install --profile PATH` orchestrator, which runs `sop install` before
that resolution step: it reads the agent-supplied `framework`, `language`, and
`contract_authority` fields already on hand from the profile and splices them
into `conductor.toml`, but only while those keys still hold the scaffold's
placeholder - an already-edited value is always preserved. The standalone
`profile apply` verb has no conductor.toml to write into if it runs before
`sop install` and never touches these two keys either way; set them by hand
when driving the individual verbs instead of `build install`. A value nothing can
derive is never written as a plausible-looking guess; it is written as a
sentinel (`"UNRESOLVED_ARCHITECTURE_MAP"`, `"UNRESOLVED_CONTRACT_AUTHORITY"`,
the pre-existing `"TICKET"` and `"unknown"`) that doctor always rejects, so an
untouched scaffold cannot read as ready. `[[conductor.review.invariants]]` is
the one fact left entirely to human judgment; author it with `build conductor
prompt` and persist it with `build conductor apply`, described below. Run
`build sop status [--json]` to report catalog drift,
including missing installed paths. Run
`build sop upgrade [--sop <name>] [--host claude|codex] [--json]` after
replacing the binary; it rewrites only emitted files that still match trusted
previous catalog hashes embedded in the current binary. Run
`build sop uninstall [--sop <name>] [--host claude|codex] [--json]` to remove
only emitted regular files that still match the current catalog.

The embedded catalog is the authority. `.build/sop-manifest.json` is a cache of
prior writes, not permission to touch arbitrary paths. Emitted files are stubs
and are validated byte-for-byte against the catalog. Scaffolded files are owned
as paths, not content: install never overwrites an existing scaffolded file, and
status validates `.build/conductor.toml` as structured conductor data instead of
comparing it with a template. A locally modified emitted stub is intentionally
preserved by install, upgrade, and uninstall; delete the local stub and rerun
`build sop install` to restore catalog content. Before any write or delete,
every target path and the manifest path must resolve strictly below the
repository root and must not cross a symlink or reparse point.

Run `build sop doctor [--json]` to validate conductor.toml, review checks, and
manifest-recorded or present emitted host stubs. Doctor validates those stubs
byte-for-byte against the catalog and reports missing, modified, non-regular, or
unsafe stub paths as drift. Review invariants are structured prose: doctor
validates id uniqueness, non-empty statements, optional paths, and optional
`blocks_done` shape only. It does not judge whether a statement is true, but it
does reject an unedited scaffold: `conductor.review.invariants.statement.placeholder`
when a statement still matches the scaffold sentence, and
`conductor.review.invariants.id.placeholder` when every invariant still carries
the scaffold's default id even if the statement text changed - editing only the
sentence is not enough to prove an invariant is repository-specific. Doctor also
resolves `architecture_map` and every `source_roots` entry against the
repository, reporting `conductor.architecture_map.not_found` or
`conductor.source_roots.not_found` when a named path or directory does not
exist - this catches both a hand-typed wrong path and an unresolved install-time
sentinel. `conductor.ticket_prefix.placeholder`,
`constellation.platform.placeholder`, and
`constellation.contract_authority.placeholder` report the corresponding
scaffold sentinels left untouched. Unknown keys in conductor.toml are findings.
Doctor also requires local `[[review.checks]]` to include at least one setup or
gating check with a non-empty executable; advisory-only checks cannot make a
gate block Done.

Run `build sop brief <name>` to emit one JSON envelope containing SOP text,
resolved conductor data, the SOP schema version, SOP version, binary version,
doctor result, the SOP's owned catalog paths, and run mode. `--json` is accepted
for consistency. Standard briefs run doctor first and fail closed: if
conductor.toml is invalid, review checks are missing, or `min_build_version` is
newer than the running binary, the command exits 1 and omits SOP text. Admission
briefs validate inspection inputs before doctor reads conductor data, then run
doctor. There is no override flag. Unknown SOP names exit 9 with the JSON error
code `unknown_sop`.

Admission-only inspection enters through the brief mode syntax:
`build sop brief <name> admission <absolute-inspection-root> <inspection-sha>`.
The root must be the absolute git worktree root for the invoking repository;
subdirectories and unrelated repositories are refused. The SHA must be a full
40-character commit SHA that resolves in that worktree; relative roots, short
SHAs, and unresolvable SHAs are refused before conductor data is read. The
emitted `runMode` carries the resolved inspection root, normalized inspection
SHA, inherited `BUILD_SOP_*` environment values, and an explicit verb policy.
With `BUILD_SOP_RUN_MODE=admission` active, mutating verbs refuse with JSON error
code `sop_admission_refused`; read-only inspection verbs remain available.
Admission forbids worktree lease and teardown, ticket comments and transitions,
commits, branches, pushes, and parent or epic expansion.

Add an optional section to `.build/config.toml`:

```toml
[worktree]
root = ".worktrees/conductor"
seed_files = [".dev.vars", ".npmrc"]
```

`root` resolves relative to the primary worktree unless it is absolute. The
default is `.worktrees/conductor`.

`seed_files` is an allowlist of untracked local files that may be copied from the
primary worktree into a lease. The default is empty. Paths must be relative and
must not contain `.` or `..` segments. Tracked paths are refused. Build never
copies all untracked files.

The lease runs `[project].install_command` in the new worktree. A blank install
command is recorded as `skipped`.

Wave planning has its own optional configuration:

```toml
[waves]
cap = 2

[[waves.serialize]]
kind = "global"
paths = ["package.json", "*.lock", "migrations/**", ".github/**"]

[[waves.serialize]]
kind = "cohesive-module"
paths = ["src/admin", "src/owner", "src/middleware"]

[[waves.serialize]]
kind = "pairwise"
paths = ["src/contract.ts", "share-contract.md"]
```

`cap` defaults to 2 and may be 1 through 16. Configured paths are
repository-relative. `*` and `?` match within one path segment, while `**`
matches across directories. A path with no wildcard matches itself and its
descendants.

A `global` match serializes a ticket with every ready peer.
`cohesive-module` serializes two tickets when both match the same configured
module. `pairwise` serializes two tickets when each matches any path in that
rule. Exact-file overlap always serializes without configuration.

## Plan safe waves

Supply either a ticket array or an object:

```json
{
  "cap": 2,
  "verifiedExternalDeps": ["TLB-1"],
  "tickets": [
    {
      "id": "TLB-2",
      "files": ["src/a.cs"],
      "deps": ["TLB-1"],
      "uncertain": false
    }
  ]
}
```

Run the planner from a file or stdin:

```sh
build waves --input tickets.json
build waves --input - --json
```

The input object's `cap` overrides `[waves].cap`. A dependency outside the
selected ticket set must appear in `verifiedExternalDeps`. Selected
dependencies are topologically leveled, so a prerequisite always runs in an
earlier wave. Tickets within one level are packed by numeric ticket order,
file disjointness, configured rules, and the cap.

Set `uncertain` to true when the predicted file list is unreliable. An
uncertain ticket, or any ticket with an empty `files` array, serializes with
every ready peer.

Human and JSON output include the schedule, every conflict edge, the rule and
path behind each serialization decision, and an estimated speedup over one
ticket per wave. Invalid input or an unverified external dependency exits 2.
A dependency cycle has the distinct `dependency_cycle` error and exits 5.
Planning does not lease worktrees or run tickets.

Treat the estimate as a planning signal, not a promise. Fan-out pays roughly 2x
on a genuinely disjoint batch once per-workspace setup and serial integration
are counted. A mostly overlapping batch should collapse to a serial schedule;
that is the planner protecting the integration path.

## Lease a worktree

```sh
build worktree lease --ticket TLB-582 --slug safe-worktrees
```

The command:

1. Takes an exclusive per-ticket lock and refuses concurrent attempts, even when
   they request different slugs.
2. Refuses existing leases, helper branches, or target directories.
3. Resolves `--base` (default `HEAD`) to a full commit SHA.
4. Creates `lease/tlb-582-safe-worktrees` under the configured root.
5. Copies existing allowlisted seed files.
6. Runs the configured install command.
7. Writes `.build-worktree-lease.json` inside the worktree.
8. Prints the absolute worktree path.

Creation tracks branch and worktree ownership separately. Failure cleanup
removes only artifacts that the current attempt proved it created.

Use `--require-seed <path>` when a specific allowlisted file is mandatory. Build
checks that file before creating the branch or worktree.

Use `--json` to receive the absolute path and full manifest in the standard
versioned success envelope. Collision, missing-seed, and manifest/containment
refusals have distinct exit codes (6, 7, and 8). General git, install, and
filesystem failures exit 1; usage and config errors exit 2.

An install failure remains a manifest-backed lease with its failure and duration
recorded. This makes the partial setup visible and safely removable.

## Inspect leases

```sh
build worktree list
build worktree list --json
```

The command reports valid leases and unmanifested directories directly below the
configured root. It never mutates either Git or the filesystem. A directory with
a missing or invalid manifest is reported as unmanifested.

## Tear down a lease

```sh
build worktree teardown --ticket TLB-582
build worktree teardown --dir .worktrees/conductor/tlb-582-safe-worktrees
build worktree teardown --ticket TLB-582 --require-merged-into main
build worktree teardown --ticket TLB-582 --force
```

Exactly one selector is required. Before removing anything, Build validates the
manifest schema, repository identity, absolute paths, exact manifest location,
and strict containment below the configured root. It then checks the lease
worktree for tracked changes and non-ignored untracked files. The default path
allows only `.build-worktree-lease.json` and files listed in the manifest's
`seededFiles`; tracked work or any other untracked file is refused before
mutation.

When `--require-merged-into <ref>` is supplied, Build also runs an explicit
ancestry proof before any removal: the lease helper branch must be an ancestor
of the named ref. If the branch is not proven merged into that ref, or the proof
cannot be run, teardown refuses while leaving both the worktree and branch in
place. This is the conductor-safe form when the intended merge target is known.

After that proof passes, Build removes the linked worktree and deletes its lease
helper branch with Git's non-force branch deletion. An unmerged helper branch is
preserved and reported as a partial failure after worktree removal. `--force`
skips the user-work proof, may permanently discard work, and force-deletes the
lease helper branch. If `--require-merged-into <ref>` is supplied, that
merge-target proof still runs before any removal.

## Run the configured gate

Run the gate with the leased worktree as the current directory:

```sh
build gate --ticket TLB-583
build gate --ticket TLB-583 --role gating --json
```

The command reads `[[review.checks]]` from the repository configuration and
runs the selected checks in the current directory. Setup checks always run
first and exactly once. A setup or gating failure exits 1. Advisory failures
are reported but exit 0. A declared `required_paths` entry that is absent makes
that check `inconclusive`, distinct from a command failure; an inconclusive
setup or gating check exits 1.

The JSON envelope includes the optional ticket identity, selected role, working
directory, overall result, and each check's name, role, status, exit code,
duration, stdout, stderr, and missing required paths. If no review checks are
configured or selected, the command clearly reports that and exits 0 by default.
Use `--require-checks` when automation should treat an empty gate as a failure.

## Create a worker brief artifact

Create a role-specific brief after leasing or selecting the worktree:

```sh
build worker brief --ticket TLB-583 --role implement --worktree .worktrees/conductor/tlb-583-safe-worktrees --output .build/tlb-583-implement.md
build worker brief --ticket TLB-583 --role review --worktree .worktrees/conductor/tlb-583-safe-worktrees --output .build/tlb-583-review.md --json
build worker brief --ticket TLB-583 --role rework --worktree .worktrees/conductor/tlb-583-safe-worktrees --output .build/tlb-583-rework.md
```

The command reads the ticket body, the supplied worktree branch and status, and
the resolved base diff. Review briefs carry that evidence and instruct the
reviewer to make an independent judgment; they do not treat an implementer
summary as a verdict. Rework briefs read the prior blocking review or gate
finding and keep the same worktree and branch semantics. The artifact includes
the exact `build gate --ticket <id> --role gating --json` command and the
configured check commands, but does not run them.

The brief is rendered from one agent's prompt templates. Pass `--agent <name>`
to override the configured agent, or `--agent-implement` / `--agent-review` to
override only the role's phase; the per-phase flag wins over `--agent`, which
wins over the `[workers.phases]` entry for that phase, which wins over
`default_agent`. The rework role uses the implement phase. Because this command
renders templates and never starts a worker, the value must name a shipped
template set (`claude-code`, `codex`, `copilot`, `gemini`) rather than a
configured `[workers.<name>]` sub-table; an unknown name, or `--agent-plan`,
exits 2 without reading the ticket or writing the artifact.

```sh
build worker brief --ticket TLB-583 --role implement --worktree .worktrees/conductor/tlb-583-safe-worktrees --output .build/tlb-583-codex.md --agent codex
```

Before dispatching a worker for a semantic-risk ticket, the conductor records a
`Ticket execution contract` in the ticket body. It names parent intent,
authority, forbidden shortcuts, required shared surfaces, focused negative tests,
out-of-scope behavior, and the rework fence. The artifact carries that ticket
body into every role and treats the recorded contract as binding. A missing or
conflicting contract stops implementation before code edits; a reviewer reports
that as a plan or contract defect for the conductor rather than inventing a
replacement contract.

`--json` emits the standard versioned envelope with the source ticket ID,
absolute output path, role, workspace, branch, base and HEAD SHAs, changed
paths, and current status paths. The command writes only the requested output
file. It does not spawn a worker or mutate tickets, commits, branches,
worktrees, deployments, or other files.

## Fingerprint a candidate

Run the status command from the candidate worktree:

```sh
build candidate status --ticket TLB-583 --base main --json
```

The JSON envelope includes the ticket, base ref, resolved base SHA, current
HEAD SHA, branch, working directory, tracked diff hash, cached/index diff hash,
untracked-file hash, touched paths, untracked paths, dirty state, and lease
manifest metadata when `.build-worktree-lease.json` is present. The tracked diff
hash fingerprints `git diff --binary --full-index --no-ext-diff --no-textconv
<base> --`; the cached/index hash fingerprints `git diff --cached --binary
--full-index --no-ext-diff --no-textconv <base> --`; the untracked hash is
computed from sorted repository-relative paths, Git-style regular-file modes,
and file content hashes.

Missing base refs, non-git directories, conflicted worktrees, invalid lease
manifests, unreadable paths, untracked directories, and untracked
symlink/reparse-point paths fail with a nonzero JSON error envelope. The command
reads git and filesystem state only. It does not mutate ticket state, branches,
commits, pushes, workers, or worktree lifecycle state.

## Record structured evidence

Use `build evidence add` for one audit entry after a conductor checkpoint:

```sh
build evidence add --ticket TLB-583 --kind claim \
  --claim "implementation matches the ticket" \
  --candidate-sha CANDIDATE_SHA --fingerprint FINGERPRINT --json
build evidence add --ticket TLB-583 --kind review \
  --run-head-sha RUN_HEAD_SHA --verdict Pass \
  --fingerprint FINGERPRINT --json
```

The supported kinds are `claim`, `review`, `commit`, `integrate`, `gate`, and
`final`. Each kind requires the provenance fields for its checkpoint. The
command formats Markdown, posts exactly one comment, and reads that comment
back by the returned id before reporting success. `readBackVerified: true`
means only that the returned id is present in the read-back list; it does not
compare stored comment content with the submitted body. If the post succeeds
but read-back fails, the command reports the created id and does not retry;
inspect `build comments <id>` before trying again.

Evidence is an audit entry only. It never closes, defers, reopens, or transitions
a ticket. Keep lifecycle operations as separate explicit commands; do not use
evidence as a cascading close or transition shortcut.

## Suggested conductor sequence

For each selected ticket, a caller-owned conductor can:

1. Read the tickets and comments, predict touched files, and run `build waves`.
2. For each ticket in the next wave, lease a worktree and pass the printed path as the implementation agent's
   working directory.
3. Run its own implementation agent in that directory.
4. Run `build gate` and `build candidate status` in that directory, then run the
   conductor's own review agent with the gate and fingerprint evidence.
5. Record the checkpoint with `build evidence add`, then move workflow state
   with the separate explicit `build transition` command.
6. Tear down the lease only after the branch no longer needs to be preserved.

The conductor still owns agent selection, review judgment, execution, and
delivery policy. Build owns wave planning, validated workspace lifecycle, and
configured checks.
