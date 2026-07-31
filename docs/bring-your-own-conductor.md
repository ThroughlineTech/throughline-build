# Bring your own conductor

`build chain` is the batteries-included topology: Build owns the deterministic
workflow and starts a fresh configured worker for each judgment phase.

An operator can instead keep one long-lived agent session as the conductor. The
conductor uses the ticket CRUD verbs (`list`, `get`, `comments`, `comment`,
`transition`, and `amend`) and asks Build to own deterministic resources. The
`worktree` verb is the isolated-workspace primitive for that topology. The
`gate` verb runs the repository's configured review checks in that workspace.
The `waves` verb plans which selected tickets can safely run concurrently.
None of these verbs starts a worker agent.

## Configuration

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

1. Refuses existing leases, helper branches, or target directories.
2. Resolves `--base` (default `HEAD`) to a full commit SHA.
3. Creates `lease/tlb-582-safe-worktrees` under the configured root.
4. Copies existing allowlisted seed files.
5. Runs the configured install command.
6. Writes `.build-worktree-lease.json` inside the worktree.
7. Prints the absolute worktree path.

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
```

Exactly one selector is required. Before removing anything, Build validates the
manifest schema, repository identity, absolute paths, exact manifest location,
and strict containment below the configured root. A missing, moved, or tampered
manifest is refused. On success Build removes the linked worktree and deletes its
lease helper branch.

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
configured, the command clearly reports `no checks configured` and exits 0.

## Suggested conductor sequence

For each selected ticket, a caller-owned conductor can:

1. Read the tickets and comments, predict touched files, and run `build waves`.
2. For each ticket in the next wave, lease a worktree and pass the printed path as the implementation agent's
   working directory.
3. Run its own implementation agent in that directory.
4. Run `build gate` in that directory, then run the conductor's own review
   agent with the gate evidence.
5. Record evidence with `build comment` and move workflow state with
   `build transition`.
6. Tear down the lease only after the branch no longer needs to be preserved.

The conductor still owns agent selection, review judgment, execution, and
delivery policy. Build owns wave planning, validated workspace lifecycle, and
configured checks.
