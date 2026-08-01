---
name: run-backlog
description: Run or adapt a repository backlog through a disciplined inspect, implement, independently review, rework, integrate, test, and ticket-update loop. Use for one or more tickets, verify-and-close work, dependency-safe parallel fan-out, or installing this workflow in a repository.
---

# Run Backlog

Act as the conductor. Repository instructions and the declared ticket client are authoritative.

## Shell contract

Run every documented CLI command with Bash. On Windows use Git Bash. Do not translate commands to
PowerShell, cmd.exe, or another shell. Quote every path variable.

Resolve the repository from any nested directory with:

```bash
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"
```

Use `build --help` and `build <command> --help` for command contracts. Treat
`build help <topic>` as reference prose, not as the complete command or TOML schema.

## Select the mode

- **Serial mode is default.** Read [serial-loop.md](references/serial-loop.md) before running it.
  Require serial mode when tickets overlap, dependencies are unclear, the working tree is unsafe, or
  independent workers are unavailable.
- **Parallel fan-out is opt-in.** Use it only when the user requests it or the repository declares it
  and the current `.build/config.toml` supports the named Build-backed primitives. Read
  [parallel-fan-out.md](references/parallel-fan-out.md) before creating worktrees. If config is absent,
  incomplete, or stale, stay serial and report that.
- **Installation/adaptation is separate.** Read
  [repo-adaptation.md](references/repo-adaptation.md) before adapting a repo. Never run or mutate real
  backlog tickets during installation validation.
- **Delegated workers use contracts.** Read [agent-contracts.md](references/agent-contracts.md) before
  spawning implementers, reviewers, or optional integration reviewers.

## Conductor invariants

- The conductor alone owns ticket mutations, commits, integration, branches, and worktree lifecycle.
- Implementers and reviewers operate under the task contracts in
  [agent-contracts.md](references/agent-contracts.md). They never commit, push, switch/create/delete
  branches, mutate tickets, deploy, integrate, or tear down worktrees.
- Preserve unrelated changes. Inspect the working tree before work and before every integration.
- Read each ticket body and comments. Verify every acceptance criterion against the real tree.
- If a ticket changes its own ticket client or transport, verify reads before any mutation and
  inspect every mutation response before continuing.
- Never deploy, push, merge to the primary branch, or ship without explicit user authorization.

## Run branch bootstrap

Do not stop merely because the primary worktree is on the repository's primary branch. If the
working tree is otherwise safe, create or switch to the run/integration branch yourself before any
ticket mutation:

- Derive the branch name from the repository's declared branch prefix and the authorized scope
  (for example `<prefix>-<ticket>-<slug>` for a single ticket, or `<prefix>-<scope>-run` for a
  batch/epic). Prefer a ticket title slug after reading the ticket when available; otherwise use a
  short stable slug such as `run`.
- If the branch exists locally, switch to it. If it does not exist, create it from the configured
  base branch/ref. Refuse only on dirty tracked changes, unresolved branch ambiguity, or a branch
  that exists with unrelated unsafe state.
- Treat `INTEGRATION_TARGET=refs/heads/<branch>` as an optional caller override. If absent, set it
  internally to the full local ref for the run branch you created or selected.
- Never ask the human to run `git switch -c ...` or to append `INTEGRATION_TARGET=...` when the
  conductor can prove and perform that setup safely.

## Serial backlog loop

Serial procedure lives in [serial-loop.md](references/serial-loop.md). In short: discover only the
authorized scope, read ticket bodies and comments, finish or block one ticket before starting the
next, use independent review when available, cap repeated review failures, and mutate ticket state
only after verified evidence.

## Parallel fan-out utility

When fan-out is enabled, use Throughline Build's deterministic verbs and the full procedure in
[parallel-fan-out.md](references/parallel-fan-out.md):

```bash
build waves --input "$PLAN_JSON" --json
build worktree lease --ticket "$ID" --slug "$SLUG" --base "$BASE_REF" --json
build gate --ticket "$ID" --require-checks --json
build worktree teardown --ticket "$ID" --require-merged-into "$SHARED_BRANCH" --json
```

`build waves` accepts a bare ticket array or an object with `cap`, `verifiedExternalDeps`, and
`tickets`. Repository conflict rules live in `.build/config.toml` under `[waves]` and
`[[waves.serialize]]`; do not pass or invent inline rule schemas. Nonzero exit means the plan is
unsafe. Empty or uncertain file predictions serialize globally.

## Agent adapters

The workflow is universal; enforcement integrations are adapter-specific. Keep detailed adapter
warnings in [agent-contracts.md](references/agent-contracts.md): Codex boundaries are task contracts
inspected by the conductor, Claude hooks are Claude-only defense in depth, and other agents need
their own documented and tested enforcement. `agents/openai.yaml` is Codex UI metadata only.

## Final report

Report each ticket's final state, exact gates and pass counts, files/commits changed, unresolved
blockers, reviewer-independence limitations, worktree cleanup state, and whether the working tree is
clean. For installation/adaptation, also report skill/archive parity and confirm that no real ticket
was mutated.
