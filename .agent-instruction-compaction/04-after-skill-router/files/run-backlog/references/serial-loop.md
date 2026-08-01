# Serial backlog loop

Inherit the shell contract and conductor invariants from `SKILL.md`. Use serial mode by default and
whenever tickets overlap, dependencies are unclear, the working tree is unsafe, or independent
workers are unavailable.

## 1. Inventory and order

- Discover only the authorized scope.
- Expand ticket shorthand using repository instructions.
- Order prerequisites first; use numeric ticket order only among equally ready tickets.
- Reject dependency cycles and unverified dependencies outside the selected scope.

## 2. Handle one ticket completely

- Read the ticket and comments with the repository ticket client.
- Classify the ticket as implementation, investigation, or hygiene.
- For hygiene, inspect code, history, and docs, then run focused gates; do not manufacture a change.
- For implementation, enter the active state only when work starts, make the smallest complete
  change, and run risk-proportionate gates.

## 3. Review independently

- Use one implementer and then a fresh read-only reviewer when independent workers are available.
- Give the reviewer the ticket body and actual diff, not the implementer's conclusions.
- Require the reviewer to rerun relevant gates.
- On `REWORK`, return findings to the same implementer and worktree. Use a new reviewer for every
  review round. Stop after three failed rounds and report the concrete blocker.
- If independent workers are unavailable, perform a distinct adversarial review pass and disclose the
  limitation.

## 4. Finalize safely

- Commit only after review passes and only when the repository workflow calls for a commit.
- Attach concise evidence: inspected implementation, commit if relevant, exact gates/counts, and
  whether code changed.
- Perform ticket transitions as separate mutations, inspect each response, and read the ticket back.
- Never use a cascading close when only one ticket should change.

## 5. Continue until terminal

- Start the next ticket only after the current ticket is complete or concretely blocked.
- "All," "finish," or "do not stop" requires persistence through the authorized queue; it does not
  authorize unrelated mutations.
