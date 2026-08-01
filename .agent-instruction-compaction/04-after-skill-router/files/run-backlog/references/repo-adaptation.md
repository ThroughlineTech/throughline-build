# Adapt run-backlog to a repository

## Contents

1. Establish the Bash context
2. Discover repository inputs
3. Configure Build fan-out primitives
4. Declare fan-out inputs
5. Apply agent adapters
6. Validate without real tickets

## Establish the Bash context

Run all commands in Bash. On Windows use Git Bash. Do not translate snippets to PowerShell.

Start from any directory inside the target repository:

```bash
set -euo pipefail
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"
```

Inspect repository instructions and preserve unrelated changes before editing.

## Discover repository inputs

Determine from repository files rather than another project's implementation:

- supported ticket system and exact read/comment/transition commands;
- whether ticket mutation is safe while changing that client;
- configured `build gate` checks and fresh-worktree install/setup;
- source roots and generated/contract/migration/lockfile/global-config conflict classes;
- ignored files workers need, using an explicit seed allowlist;
- shared resources requiring unique leases;
- a safe linked-worktree root, seed allowlist, and concurrency cap of 2 or 3;
- irreversible operations that always require explicit user authorization.

Do not infer permission to copy secrets, create production resources, deploy, or mutate real tickets.
If a seed/resource decision is materially ambiguous, remain serial or ask.

## Configure Build fan-out primitives

Prefer Throughline Build's deterministic primitives over repo-local helper scripts. Add or verify:

```toml
[worktree]
root = ".worktrees/conductor"
seed_files = []

[waves]
cap = 2

[[waves.serialize]]
kind = "global" # or "pairwise" / "cohesive-module"
paths = ["migrations/**", "package-lock.json"]
```

Also configure `[[review.checks]]` so `build gate --ticket <ID> --require-checks --json` is a real
gate. Use `build init --print-template` and `build <command> --help` for current syntax; do not use
`build help config` as a full schema.

## Declare fan-out inputs

Add a concise repository-instructions block only when fan-out is intentionally enabled:

```markdown
## Conductor inputs

- Fan-out inputs (optional): use Build-backed fan-out.
  - Plan waves: `build waves --input <json> --json`; summarize repo-specific serialization rules.
  - Lease/teardown: `build worktree lease ... --json`; `build worktree teardown ... --json`.
  - Gate: `build gate --ticket <ID> --require-checks --json`.
  - Worktree root, seed allowlist, serialization rules, and concurrency cap live in `.build/config.toml`.
  - Ticket reads: `<commands>`; conductor-only mutations: `<commands>`.
  - Source roots: `<paths>`; concurrency cap: `<2 or 3>`.
```

No declaration means serial mode.

Do not implement repo-local worktree/planner helpers unless Build lacks a required primitive and the
user explicitly asks for a temporary bridge. If a repository still documents old helpers, either
migrate it to Build-backed config or keep that repo serial and report the stale declaration.

## Apply agent adapters

The repository instructions must state the universal conductor/worker boundaries. Then add only the
adapter used by that repository:

- **Codex:** reference the global skill and its task contracts. Do not install Claude role files or
  describe Claude hooks as Codex enforcement. Add `.codex` files only when named Codex custom agents
  are intentionally used.
- **Claude Code:** a `.claude` command/role/hook may remain as explicitly Claude-only defense in
  depth. Test chained commands, quotes, executable paths, and Git global options.
- **Other agents:** document their adapter only when its enforcement mechanism is actually present.

Do not copy another repository's ticket prefix, gates, ports, secrets, paths, or agent definitions.

## Validate without real tickets

Use only fake IDs such as `TEST-000`. Before testing, inspect registered worktrees and the declared
root for existing or orphaned directories. Never delete an unverified orphan.

Validate:

1. `build waves` fixtures: reversed-number dependency ordering, numeric order among equally ready tickets,
   cycle rejection, unverified external-dependency rejection, verified external dependencies, exact
   file conflicts, shared contract/codegen conflicts, cohesive-module conflicts, migration/lockfile/
   global-config serialization, uncertain/empty predictions, and a genuinely parallel wave. Also
   reject malformed JSON, non-string file entries, non-boolean uncertainty, absolute/drive-prefixed
   paths, and parent traversal.
2. `build worktree` fixtures: list is clean, missing required seed fails, lease succeeds with a fake
   ID, install/setup status is inspected, teardown refuses unsafe targets, and safe teardown removes
   the lease.
3. `build gate --ticket TEST-000 --require-checks --json` runs the configured checks. If no checks
   are configured, either add them or keep the repo serial/manual-gated and say so.
4. Integration dry-run reasoning: prove the helper branch can be rebased onto the shared branch and
   the shared branch can be fast-forwarded before safe teardown. Do not merge real work during
   validation.
5. Adapter behavior: task-contract ownership plus any adapter-specific hook cases. For a shell hook,
   include quoted paths containing spaces, absolute executable paths, `-C`, `-c`, and later chained
   mutations while proving safe reads remain allowed.
6. One successful lease/setup/gate/teardown after the final config/docs changes.
7. Repository lint, typecheck, tests, syntax checks, build, and relevant smoke tests. Report missing
   scripts as not available rather than inventing commands.

An uncommitted adaptation is absent from a worktree created from `HEAD`. During validation, invoke
the adapted helper/hook by its absolute path in the primary working tree so stale worktree copies are
not mistaken for the final implementation.

After any rework, use a new read-only reviewer and rerun affected gates. Finish by proving no fake
branches, registered worktrees, manifests, leased listeners, or helper processes remain. Report any
preserved unverified directory for human-approved cleanup.
