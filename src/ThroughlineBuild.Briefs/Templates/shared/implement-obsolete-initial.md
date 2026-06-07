## Obsolete detection

Before making any changes, check whether the plan's acceptance criteria are already satisfied by a prior commit. If the acceptance criteria's artifacts already exist AND their content meets the acceptance criteria, the ticket is obsolete.

**Detection bar:** "the file exists AND its content meets the acceptance criteria" qualifies. "a file with the same name exists" does not.

Emit `Status=Escalate` with a populated `metadata.escalation` block. Do not make any changes.

WORKER_RESULT
{
  "status": "Escalate",
  "summary": "Ticket obsolete: decompose.md already delivered in commit 80ccafa",
  "files_changed": [],
  "failure_reason": null,
  "metadata": {
    "escalation": {
      "reason": "obsolete",
      "subsumed_by": {
        "commit": "80ccafa",
        "files": ["src/ThroughlineBuild.Briefs/Templates/claude-code/decompose.md"],
        "rationale": "decompose.md delivered in commit 80ccafa; file meets this brief's acceptance criteria"
      }
    }
  }
}