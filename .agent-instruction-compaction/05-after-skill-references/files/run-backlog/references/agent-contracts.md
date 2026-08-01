# Worker task contracts

Use these contracts whenever work is delegated. The conductor remains responsible for detecting
accidental state changes after every worker returns.

## Implementer contract

Give the implementer exactly one ticket, its full body and acceptance criteria, the absolute
workspace/worktree path, repository instructions, and exact gate command. Prefer
`build gate --ticket <ID> --require-checks --json` when the repo has configured
`[[review.checks]]`.

Require the implementer to:

1. Work only in the supplied path and implement exactly one ticket.
2. Read cited files before editing and preserve unrelated changes.
3. Run the exact gate command and report exact command and result.
4. Return changed files and a criterion-by-criterion self-check.
5. Avoid commits, pushes, branch operations, ticket mutations, deployment, integration, and
   worktree teardown.

Its final response is a structured report to the conductor, not a user handoff.

Never allow two agents to write the same working tree concurrently.

## Reviewer contract

Use a fresh reviewer after every implementation or rework round. Give it the ticket body,
acceptance criteria, absolute workspace path, actual diff or instructions to inspect it, repository
review invariants, and exact gate command. Do not provide the intended verdict or the implementer's
self-assessment.

Require the reviewer to:

1. Make no edits and perform no ticket, Git-history, branch, push, deploy, integration, or teardown
   mutations.
2. Inspect the actual diff and surrounding code.
3. Rerun the exact gate command.
4. Check every acceptance criterion, regression risk, scope creep, and repository invariant.
5. Return exactly `VERDICT: PASS` or `VERDICT: REWORK`, followed by observed gate results and
   actionable `file:line` findings.

After the reviewer returns, inspect status/diff to detect accidental edits before acting on its
verdict.

## Rework

Send the original ticket plus review findings to the same implementer in the same worktree. Then use
a new reviewer. Stop after three failed review rounds and report the impasse.

## Integration

The conductor normally integrates directly:

1. Commit the reviewed diff inside the passing ticket worktree.
2. Rebase the helper branch onto the current shared branch.
3. Fast-forward the shared branch to the rebased helper branch.
4. Stop on conflicts without guessing at semantic resolution.
5. Run the configured gate on the integrated tree.
6. Mutate ticket state and tear down only after the integrated gate passes.

An optional integration reviewer may inspect and run gates, but it does not own Git history or
ticket state.

## Adapter: Codex

Spawn implementers/reviewers with the collaboration mechanism and paste the contracts above into
their tasks. Codex workers normally have the same tools as the conductor, so read-only review and
conductor-only ownership are task contracts, not reduced tool grants. Inspect state after each
worker. Do not claim that Claude Code hooks enforce Codex operations.

## Adapter: Claude Code

Claude role files may restrict advertised tools and a Claude `PreToolUse` hook may add defense in
depth. Keep the contracts above authoritative. Label the hook Claude-only and do not claim it
enforces another agent. A retained shell hook must parse every chained invocation, quoted paths,
absolute executable paths, and Git global options such as `-C` and `-c`.

## Adapter: other agents

Use the universal contracts above. Add enforcement only when that agent's own documented mechanism
is installed and tested; otherwise rely on conductor inspection.
