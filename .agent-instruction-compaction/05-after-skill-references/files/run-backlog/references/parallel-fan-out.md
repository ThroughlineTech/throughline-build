# Parallel fan-out

## Contents

1. Preconditions
2. Wave planning
3. Worktree leasing
4. Implement, review, and rework
5. Serial integration
6. Teardown and failures
7. Planner input

## Preconditions

Inherit the shell contract, conductor ownership, worker boundaries, and authorization bans from
`SKILL.md`.

Fan-out is opt-in. Use it only when the user requests it or the repository declares it and the
current `.build/config.toml` provides Build-backed fan-out config:

- ticket read/mutation commands;
- source roots used to predict changed files;
- `[[review.checks]]` for `build gate`;
- `[worktree]` root plus explicit ignored-file `seed_files`;
- `[waves]` cap and `[[waves.serialize]]` always-serial paths/modules.

If config is absent, incomplete, or contradicted by stale repo-local script instructions, stay
serial and report the stale declaration. Never copy ignored directories or secrets wholesale.

## Wave planning

1. Read every ticket and dependency in the authorized scope.
2. Predict files conservatively. Set the boolean `uncertain: true` when prediction is incomplete.
3. Separately verify every dependency outside the selected scope is satisfied, then list its ID in
   `verifiedExternalDeps`.
4. Run `build waves --input <path|-> --json` and show its output before leasing.
5. Stop on nonzero exit.

Build topologically levels dependencies first and applies numeric ID order only among equally ready
tickets. It rejects cycles and unverified external dependencies. Conflict-free packing occurs only
within one dependency level.

Serialize uncertain/empty predictions, migration-like changes, contract/codegen authorities,
lockfiles, global configuration, and same-cohesive-module changes according to repository rules in
`.build/config.toml`.

## Worktree leasing

The conductor owns lifecycle. Before leasing the first wave, use `SKILL.md` run branch bootstrap to
create or switch to the local shared run branch and record its full ref (`refs/heads/<branch>`). Do
not require the human to provide `INTEGRATION_TARGET` when this can be derived safely.

For each ticket in one wave:

1. Resolve the primary repository root and current shared branch/base SHA.
2. Run `build worktree lease --ticket <ID> --slug <slug> --base <base-ref> --json`.
3. Add `--require-seed <path>` only when that ticket truly requires a seed allowlisted by
   `[worktree].seed_files`.
4. Inspect the JSON result and record the worktree path, helper branch, and install status.

Treat any lease/install failure as a failed lease. Build owns manifest, containment, collision,
seed, rollback, and helper-branch safety checks; do not recreate them in repo-local scripts.

## Implement, review, and rework

Spawn one implementer per leased worktree up to the cap. Pass its absolute worktree path and one
ticket. After it finishes, inspect state and use a fresh read-only reviewer in the same worktree.
Keep that worktree for rework. Never allow concurrent writers in one tree.

Use `agent-contracts.md`. Adapter hooks may add defense in depth only for the agent that owns them;
the universal baseline is the task contract plus conductor inspection.

Run `build gate --ticket <ID> --require-checks --json` in the leased worktree for the implementer
and reviewer gate. Do not integrate until review returns `PASS` and the conductor verifies the diff
and gate result.

## Serial integration

For each passing ticket in deterministic order:

1. The conductor commits the reviewed diff inside its ticket worktree.
2. The conductor rebases the helper branch onto the current shared branch.
3. The conductor fast-forwards the shared branch to the rebased helper branch.
4. Stop on conflicts; do not guess at semantic resolution.
5. After the wave applies, run `build gate --ticket <scope-or-wave> --require-checks --json` on the
   integrated tree.
6. Only after the integrated gate passes, post evidence and mutate tickets one operation at a time.

## Teardown and failures

- Tear down only safely integrated tickets or abandoned worktrees whose removal is authorized.
- Preserve failed/conflicted worktrees unless safe recovery is proven.
- Use `build worktree teardown --ticket <ID> --require-merged-into <shared-branch> --json` for
  integrated worktrees, where `<shared-branch>` is the full local ref of the conductor-created run
  branch unless the caller supplied a validated override.
- Use `--force` only with explicit human approval.
- The `SKILL.md` deploy/push/merge/ship authorization ban applies to fan-out.

## Planner input

`build waves` accepts a bare ticket array or this object:

```json
{
  "cap": 3,
  "verifiedExternalDeps": ["APP-1"],
  "tickets": [
    {
      "id": "APP-10",
      "files": ["src/users/service.cs"],
      "deps": ["APP-1"],
      "uncertain": false
    },
    {
      "id": "APP-11",
      "files": ["src/billing/service.cs"],
      "deps": []
    }
  ]
}
```

Rules:

- Shared exact files always conflict.
- Configured `kind = "global"` paths conflict with every peer in their level.
- Configured `kind = "pairwise"` paths conflict when both tickets touch that shared surface.
- Configured `kind = "cohesive-module"` paths conflict when both tickets touch the same module.
- `uncertain: true` or an empty file list conflicts with every peer in its level.
- Dependencies determine levels; conflicts determine waves within each level.

Use `--json` for machine-readable output. Nonzero exit means the plan is unsafe.
