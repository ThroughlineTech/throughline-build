---
name: run-backlog
description: Run or adapt a repository backlog through a disciplined inspect, implement, independently review, rework, integrate, test, and ticket-update loop. Use for one or more tickets, verify-and-close work, dependency-safe parallel fan-out, or installing this workflow in a repository.
---

# Run Backlog

Act as the conductor. Treat repository instructions and its declared ticket client as authoritative.

## Shell contract

Run every documented CLI command with Bash. On Windows use Git Bash. Do not translate the commands
to PowerShell, cmd.exe, or another shell. Quote every path variable.

Resolve the repository from any nested directory with:

```bash
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"
```

Use `build --help` and `build <command> --help` for command contracts. Treat `build help <topic>`
as reference prose only, not as the complete command or TOML schema.

## Select the mode

- Use **serial mode** by default. Require it when tickets overlap, dependencies are unclear, the
  working tree is unsafe, or independent workers are unavailable.
- Use **parallel fan-out** only when requested by the user or declared by the repository and the
  repository's current `.build/config.toml` supports the Build-backed primitives it names. Read
  [parallel-fan-out.md](references/parallel-fan-out.md) completely before creating worktrees.
  If a repo still declares obsolete local scripts or lacks Build fan-out config, stay serial and
  report the stale declaration.
- For installation or adaptation, read [repo-adaptation.md](references/repo-adaptation.md)
  completely. Never run or mutate real backlog tickets during installation validation.

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

1. **Inventory and order**
   - Discover only the authorized scope.
   - Expand ticket shorthand using repository instructions.
   - Order prerequisites first. Use numeric ticket order only among equally ready tickets.
   - Reject dependency cycles and unverified dependencies outside the selected scope.
2. **Handle one ticket completely**
   - Read the ticket and comments with the repository ticket client.
   - Classify it as implementation, investigation, or hygiene.
   - For hygiene, inspect code/history/docs and run focused gates; do not manufacture a change.
   - For implementation, enter the active state only when work starts, make the smallest complete
     change, and run risk-proportionate gates.
3. **Review independently**
   - Use one implementer and then a fresh, read-only reviewer when independent workers are available.
   - Give the reviewer the ticket body and actual diff, not the implementer's conclusions.
   - Require the reviewer to rerun relevant gates.
   - On `REWORK`, return findings to the same implementer/worktree. Use a new reviewer for every
     review round. Stop after three failed rounds and report the concrete blocker.
   - If independent workers are unavailable, perform a distinct adversarial review pass and disclose
     the limitation.
4. **Finalize safely**
   - Commit only after review passes and only when the repository workflow calls for a commit.
   - Attach concise evidence: inspected implementation, commit if relevant, exact gates/counts, and
     whether code changed.
   - Perform ticket transitions as separate mutations, inspect each response, and read the ticket back.
   - Never use a cascading close when only one ticket should change.
5. **Continue until terminal**
   - Start the next ticket only after the current ticket is complete or concretely blocked.
   - "All," "finish," or "do not stop" requires persistence through the authorized queue; it does
     not authorize unrelated mutations.

## Parallel fan-out utility

When fan-out is enabled, use Throughline Build's deterministic verbs:

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

The workflow above is universal. Enforcement integrations are adapter-specific:

- **Codex adapter:** worker boundaries are task contracts inspected by the conductor. Codex does not
  consume Claude Code hooks. `agents/openai.yaml` is Codex UI metadata; other agents ignore it.
- **Claude Code adapter:** a repository may retain a Claude `PreToolUse` hook as defense in depth.
  It does not enforce boundaries for other agents and must be labeled Claude-only.
- **Other agents:** use task contracts and state inspection unless that agent's own adapter explicitly
  provides stronger enforcement.

## Final report

Report each ticket's final state, exact gates and pass counts, files/commits changed, unresolved
blockers, reviewer-independence limitations, worktree cleanup state, and whether the working tree is
clean. For installation/adaptation, also report skill/archive parity and confirm that no real ticket
was mutated.
