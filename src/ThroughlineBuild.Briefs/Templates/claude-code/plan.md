# Plan: {{ticket_id}} - {{title}}

You are investigating a ticket and writing an implementation plan. Investigation only. No code changes, no branches. You do not need to build, run tests, or invoke other verification commands. Investigation is read-only. The plan you produce will be appended to the ticket's description and serve as the contract for the implement phase.

## Ticket

**Title:** {{title}}
**Type:** {{type}}
**Description:**

{{description_html}}

## Repository context

**Main branch SHA at planning time:** {{main_sha}}
**Top-level repo entries:** {{top_level_entries}}

{{project_notes_section}}

## Your job

Produce a plan an implementing agent can execute without guessing. The plan must justify its choices, name what should NOT be done, and document the discipline passes you ran on it.

### Investigation behaviors

1. **Command syntax note.** When referencing commands, use the syntax of the active workflow tool. For this project, that is: {{workflow_tool}}. If "build", use `build <phase> <TKT-ID>` when mentioning commands; if "claude-config", use `/ticket-<phase>` slash commands. This project uses the {{workflow_tool}} tool.

2. **Read project context.** `CLAUDE.md` if present, any project notes referenced above, parent ticket if any. The parent provides framing, not work - do not expand scope.

3. **Deep-dive the code.** Use Grep, Glob, and Read aggressively from key source locations. Read relevant files end-to-end; skimming produces vague plans. Map call chains entry -> business logic -> data layer. Identify the interfaces, types, and tests that will be touched.

4. **By ticket type:**
   - **Bug:** trace the path that produces the wrong behavior; identify the actual line or contract that is wrong. "Probably something in foo module" is not a root cause.
   - **Feature:** identify where new code fits; which interfaces extend; which new files; which patterns to follow vs deliberately diverge from.
   - **Refactor:** map call sites that depend on the surface being refactored; assess whether the change is mechanical or has semantic impact.

5. **Verify environment.** If the project context names build/test/install commands, run one to confirm the environment is workable. Environment gaps (broken shims, stale lockfiles) are noted in Investigation but NOT fixed as part of this plan unless they directly block the ticket's work.

6. **Identify regression risks.** Which tests cover the affected code? Which user-facing flows touch it? What could break? Which downstream callers are affected?

## Output structure

Produce the plan as markdown inside a PLAN_BODY fenced block. Emit the fenced block BEFORE the WORKER_RESULT envelope. All sections, in this order:

```
# Investigation

What you found, with specific file paths and line numbers. No vague descriptions.
Root cause (for bugs) or architectural fit (for features) - specific.
Regression risk: low | medium | high - with rationale.
Subtract pass: {what was cut, deferred, or "nothing to cut"}.
Rubber-duck pass: {clean, or defects caught and fixed}.

# Proposed Solution

Approach, tradeoffs, why this approach over alternatives.
If multiple paths exist, name them and explain the recommendation with reasoning.

# Implementation Plan

One paragraph: what, why, approach.

**Relevant files**
- `path/to/file.ts` - what changes

**Steps**
1. Concrete actions an implementer takes; numbered.

**Verification**
- Specific checks. Include at least one manual verification step for UI work. Name a human ship gate.

**Design decisions**
- Decisions made during this investigation the implementer should treat as settled. Include rationale for non-obvious choices.

**Escalation rules** (raise a ticket, do not work around)
- Name the adjacent fixes, refactors, or temptations the implementing agent must not take. Being explicit here preempts scope creep.

**Out of scope**
- Explicit deferrals - work for a different ticket or future phase.

**Agent size:** {S|M|L} - {inference rationale, e.g. "M - 5 files, 8 steps, one new file"}
```

**Length target:** under ~90 lines, but completeness wins. Relevant files is load-bearing - do not omit.

### Complete output example

```
<<<PLAN_BODY_START
# Investigation

Traced the bug to `src/Foo.cs` line 42: null-check absent before dereferencing `bar`.
Regression risk: low - isolated to one call site.
Subtract pass: nothing to cut.
Rubber-duck pass: clean.

# Proposed Solution

Add null guard before dereference. No alternative needed.

# Implementation Plan

Add null check at the identified call site.

**Relevant files**
- `src/Foo.cs` - add null guard

**Steps**
1. Open `src/Foo.cs`.
2. At line 42, add: `if (bar is null) return;`
3. Run `dotnet test` to confirm green.

**Verification**
- `dotnet test` passes.

**Design decisions**
- Chosen early return over exception to match surrounding pattern.

**Escalation rules**
- Do not refactor unrelated null checks in the same file.

**Out of scope**
- Full null-safety audit of Foo - separate ticket.

**Agent size:** S - 1 file, 3 steps, no new files
<<<PLAN_BODY_END

WORKER_RESULT
```json
{
  "status": "Ok",
  "summary": "Null dereference in Foo.cs line 42 - add null guard",
  "files_changed": [],
  "failure_reason": null,
  "metadata": {
    "plan_body_ref": "PLAN_BODY",
    "risk_label": "low",
    "size_label": "S",
    "planned_at_sha": "{{main_sha}}"
  }
}
```

## Discipline passes (mandatory, both)

Before returning the result, run both passes. Document the outcome in the Investigation section.

### Subtract pass

- What can be cut (not needed to satisfy acceptance criteria)?
- What can be deferred to a follow-up?
- Does this plan give an implementor enough detail without padding?
- Bundling a delight feature with a bug fix? Recommend splitting.

Document the answer (or a "nothing to cut" one-liner) before returning.

### Rubber-duck pass (load-bearing)

Read every shell, test, and build command in Steps and Verification and ask "what happens when this actually runs":

- **Interpreter match:** does the test command use the right runtime for the script type (`bash` for `.sh`, `python3` for `.py`)?
- **Input feeding:** for piped stdin, do the bytes include the trailing newlines the tested code expects?
- **Path existence:** does every file path mentioned resolve to a real or about-to-be-created location? Grep-verify any referenced function or flag before posting.
- **Reviewer simulation:** if a reviewer looked at the Verification section, what would they flag?

Document "Rubber-duck pass: clean" or list defects caught and fixed.

## Agent size inference

Count Relevant files and Steps. Apply:

- **S:** 1-2 files AND ≤4 steps AND no new files; localized additive change; no load-bearing interface modifications.
- **M:** 4-6 files OR 6-10 steps; moderate cross-cutting; or introduces new files.
- **L:** 7+ files OR 11+ steps; architectural change; modifies load-bearing interfaces; or high blast radius.

The "Agent size:" line in the Implementation Plan must match this inference.

## Obsolete detection

Before starting investigation proper, check whether the work is already done. If the acceptance criteria's artifacts already exist AND their content meets the acceptance criteria, the ticket is obsolete.

**Detection bar:** "the file exists AND its content meets the acceptance criteria" qualifies. "a file with the same name exists" does not.

Emit `Status=Escalate` with a populated `metadata.escalation` block. Do not append a plan.

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

## Invalid-ticket discovery

If investigation reveals the ticket is invalid or already fixed (not obsolete): return `WorkerResult` with `Status = Escalate` and `FailureReason` containing a one-line explanation. Do not append a plan.

## WORKER_RESULT envelope

When investigation, discipline passes, and size inference are complete, emit the PLAN_BODY fenced block followed by the WORKER_RESULT envelope as the LAST output. A bare `WORKER_RESULT` marker on its own line, followed by JSON:

WORKER_RESULT
{
  "status": "Ok",
  "summary": "<one-line root cause or approach>",
  "files_changed": [],
  "failure_reason": null,
  "metadata": {
    "plan_body_ref": "PLAN_BODY",
    "risk_label": "<low|medium|high>",
    "size_label": "<S|M|L>",
    "planned_at_sha": "{{main_sha}}"
  }
}

## Rules

- Investigation only. No code changes. No branches.
- Specific file paths and line numbers. No vague descriptions.
- Every section listed in Output structure must be present in the PLAN_BODY fenced block. Empty sections are not acceptable - either the section has content or its absence is justified.
- Both discipline passes documented before returning.
- Never reference specific model names (haiku, sonnet, opus, claude-*) in the size line or rationale - only S, M, L.
- Do not propose work outside the ticket's scope; surface as "Out of scope" items.
- Do not embed `[planned_at: SHA]` marker comments in the PLAN_BODY block - the orchestrator posts those separately.
- Do not JSON-escape the plan body. Write it as plain markdown inside the PLAN_BODY fenced block. The fenced block requires no escaping.