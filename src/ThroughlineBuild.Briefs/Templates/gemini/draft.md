# Draft Ticket Brief

You are drafting a ticket body from a free-form operator description. Your job is to expand the
operator's text into the standard ticket template format without inventing scope the operator did
not state.

## Operator input

{{operator_text}}

## Target template structure

Fill in each section below using the operator input above. Output the completed ticket body in the
WORKER_RESULT envelope defined in the Output format section.

```
# <Title - concise and descriptive, <=80 chars where possible>

**Type:** <task|feature|bug>

## Description

<Expand the operator's intent into one to three factual paragraphs. Do not pad.>

## Acceptance criteria

- [ ] <Observable outcome the operator stated or clearly implied>
- [ ] <Additional observable outcome if applicable>

## Out of scope

- <Only include if the operator explicitly mentioned an exclusion; otherwise omit this section>

## Notes

<Optional: design decisions, references, or ambiguity flags. If the operator text is ambiguous
about scope, note it here: "Operator did not specify X; suggest clarifying or accepting default Y".>
```

## Constraints

1. Preserve operator intent verbatim where possible - do not paraphrase without cause.
2. Do not invent acceptance criteria the operator did not state or clearly imply.
3. Do not invent out-of-scope items unless the operator explicitly mentioned exclusions.
4. When operator text is terse, the description should be terse - do not pad with filler sentences.
5. When operator text is ambiguous about scope, note the ambiguity in the Notes section using the
   pattern: "Operator did not specify X; suggest clarifying or accepting default Y."
6. Infer Type from text: use "bug" for defects or issues; "feature" for new capability; "task" as
   the safe default when the type is unclear.
7. Title should be concise and descriptive. Target <=80 characters; this is a soft limit - do not
   truncate meaning to hit it.
8. Use single hyphens only - no em-dashes or en-dashes anywhere in the output.
9. Acceptance criteria are observable outcomes, not implementation steps. "Widget renders on
   sidebar" is valid; "Call AddWidget() in Sidebar.cs" is not.

## Output format

Before emitting the WORKER_RESULT envelope, emit the drafted ticket body as a named fenced block:

<<<DRAFT_BODY_START
# Title here

**Type:** task|feature|bug

## Description

Your drafted content here. Can include code blocks, backticks, quotes, shell commands - no JSON escaping needed.

## Acceptance criteria

- [ ] Observable outcome

## Notes

Optional notes.
<<<DRAFT_BODY_END

Then emit the WORKER_RESULT envelope:

WORKER_RESULT
{"status":"Ok","summary":"<one-line summary of the draft>","filesChanged":[],"failureReason":null,"metadata":{"body_markdown_ref":"DRAFT_BODY"}}

## Examples

### Example 1 - terse input

Operator input:
```
Add a widget to the sidebar
```

Output:

<<<DRAFT_BODY_START
# Add widget to sidebar

**Type:** feature

## Description

Add a widget to the sidebar.

## Acceptance criteria

- [ ] Widget appears in the sidebar.

## Notes

Operator did not specify widget type or content; suggest clarifying before filing.
<<<DRAFT_BODY_END

WORKER_RESULT
{"status":"Ok","summary":"Drafted ticket: Add widget to sidebar","filesChanged":[],"failureReason":null,"metadata":{"body_markdown_ref":"DRAFT_BODY"}}

### Example 2 - ambiguous input

Operator input:
```
The login sometimes fails when the network is slow - fix it
```

Output:

<<<DRAFT_BODY_START
# Fix intermittent login failure on slow network

**Type:** bug

## Description

Login fails intermittently when the network is slow. The root cause has not been identified; this ticket covers investigation and fix.

## Acceptance criteria

- [ ] Login succeeds reliably under degraded network conditions.

## Notes

Operator did not specify the failure mode (timeout, error response, or UI hang); suggest reproducing the issue before narrowing the fix scope.
<<<DRAFT_BODY_END

WORKER_RESULT
{"status":"Ok","summary":"Drafted ticket: Fix intermittent login failure on slow network","filesChanged":[],"failureReason":null,"metadata":{"body_markdown_ref":"DRAFT_BODY"}}
