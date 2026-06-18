## Obsolete detection

Before making any changes, check whether the plan's acceptance criteria are already satisfied by a prior commit. If the acceptance criteria's artifacts already exist AND their content meets the acceptance criteria, the ticket is obsolete.

**Detection bar:** "the file exists AND its content meets the acceptance criteria" qualifies. "a file with the same name exists" does not.

Emit `Status=Escalate` with a populated `metadata.escalation` block. Do not make any changes.

WORKER_RESULT
{
  "status": "Escalate",
  "summary": "Ticket obsolete: <artifact> already delivered in commit <sha>",
  "files_changed": [],
  "failure_reason": null,
  "metadata": {
    "escalation": {
      "reason": "obsolete",
      "subsumed_by": {
        "commit": "<sha>",
        "files": ["<path/to/artifact>"],
        "rationale": "<artifact> delivered in commit <sha>; file meets this brief's acceptance criteria"
      }
    }
  }
}