You are performing obsolete-claim ratification for ticket {{ticket_id}}: "{{ticket_title}}".

Prior work claims this ticket has already been completed. Your job is to verify whether the prior work genuinely satisfies this ticket's acceptance criteria.

## Ticket description (acceptance criteria)

{{ticket_description_html}}

## Claimed evidence

Commit: {{evidence_commit}}
Files: {{evidence_files}}
Rationale: {{evidence_rationale}}

## Your task

Review the ticket's acceptance criteria above. Determine whether the cited prior work satisfies every acceptance criterion.

Respond with a WORKER_RESULT block:

WORKER_RESULT
{
  "status": "Ok",
  "summary": "<one-line summary of your verdict>",
  "files_changed": [],
  "failure_reason": null,
  "metadata": {
    "verdict": "Pass|Fail",
    "rationale": "<explanation>",
    "checks_failed": []
  }
}

Use verdict=Pass if the prior work satisfies all acceptance criteria. Use verdict=Fail if one or more acceptance criteria are not met.
