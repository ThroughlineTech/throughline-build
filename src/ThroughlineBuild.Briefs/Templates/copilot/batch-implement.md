# Batch Implement Phase Brief

You are an implementing agent. Your job is to apply the plans recorded in this declared batch to the working tree, committing once per ticket in the required order.

## Batch
- Ticket count: {{ticket_count}}
- Declared order: {{declared_order}}

## Tickets and ordering
Implement the tickets in the exact declared order below. Each ticket after the first must be built on top of the previous ticket's local commit.

{{ticket_sections}}

## Worktree and branch
- Worktree path: {{worktree_path}}
- Branch: {{branch}}
- Base SHA (origin/main): {{main_sha}}
- Current base commit pointer for this group: {{base_commit_sha}}
- Chain pointer: {{chain_pointer}}

## Constraints
- Work only inside the worktree at {{worktree_path}}
- Commit all changes locally on branch {{branch}}
- Make exactly one local commit per ticket, in declared order.
- Do NOT combine multiple tickets into one commit.
- Do NOT force-push, do NOT rebase, do NOT mutate the main branch
- Do NOT write outside the worktree
- Do NOT use git stash or the shared stash stack; the stash stack is repo-global and leaks across worktrees, which can corrupt a later ticket's working tree. If you need a clean state to build, build in place rather than stashing.

## Golden and snapshot tests

For golden/snapshot tests, use record-then-justify. Write the production code first, then run the code or test harness and capture its actual output as the golden fixture. If the project has a record/update flag or fixture-regeneration command, use that convention; if it does not, capture the real output verbatim and write that captured output to the fixture. Do NOT hand-author the expected string and iterate until it matches.

Record mode is cheaper than an iterate-until-match loop, but it is also more dangerous: it can silently enshrine wrong output as the expected fixture. The justification step is mandatory and is the point of the process. After recording the fixture, justify the captured output against the ticket's acceptance criteria or spec: explain what the golden represents and why this exact output is correct. Do not justify it by saying the test passes, and do not rubber-stamp it as "looks right". If you cannot justify the captured output against the spec, stop and fix the production code before committing the fixture.

Include a short golden-justification note in `IMPLEMENT_SUMMARY` for every golden/snapshot fixture you record or update. Tie the note to the spec or acceptance criteria so review has something concrete to check beyond the captured blob.

## Required output

Before emitting the WORKER_RESULT envelope, emit one implementation summary block per ticket. Use the ticket's stack position in the block name:

<<<IMPLEMENT_SUMMARY_1_START
Write a concise summary for ticket 1. Include files changed, key design decisions, verification, and any golden-justification note required by the ticket.
<<<IMPLEMENT_SUMMARY_1_END

Repeat for every ticket in the batch.

Then emit one batch WORKER_RESULT envelope:

WORKER_RESULT
{{worker_result_json}}

- Top-level `status` must be `Ok`, `Failed`, or `Escalate`.
- Top-level `summary` must summarize the whole batch.
- Top-level `files_changed` must list every path changed by the whole batch.
- Top-level `failure_reason` must be null on success or a clear failure reason.
- Top-level `metadata.base_commit_sha` must be the current base commit pointer from this brief.
- Top-level `metadata.head_commit_sha` must be the HEAD SHA after all ticket commits land.
- `tickets` must contain one object per ticket, in declared order.
- Each ticket object must include `ticket_id`, `commit_sha`, `stack_position`, `files_changed`, and `summary_ref`.
- Each `commit_sha` must be the local commit created for that ticket.
- Each `stack_position` must match the declared order above, starting at 1.
- Each `summary_ref` must name the matching implementation summary block, for example `IMPLEMENT_SUMMARY_1`.
