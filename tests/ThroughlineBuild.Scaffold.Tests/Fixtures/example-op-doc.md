# Operation: example

Example operation for parser fixture. Covers two plans with multiple briefs.

## Why this exists

This op-doc exists as a known-good parser fixture. It covers the full structural surface of the op-doc format: H1 header, Why section, Dispatch order table, two Plan sections each with Goal/Briefs table/Briefs detail, and a What done looks like section.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Parsing and validation | - | M |
| B    | Integration and CLI | A | S |

Plan A delivers the parser and validator. Plan B integrates with external services and adds the CLI command.

## Plan A: Parsing and validation

### Goal

Parse op-doc markdown files and produce typed records. Validate structure.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | op-doc-types | Typed records for op-doc structure | - | src/OpDocTypes.cs |
| 02 | op-doc-parser | Parser that reads an op-doc file and produces an OpDoc record | 01 | src/OpDocParser.cs |
| 03 | op-doc-validator | Validator checking parsed OpDoc against format spec | 02 | src/OpDocValidator.cs |

### Briefs - detail

#### Brief 01: op-doc-types

Goal: Define immutable records representing the op-doc structure.

Inputs:
- Op-doc format conventions
- Existing record patterns in the codebase

Outputs:
- OpDoc record with all required fields
- Plan, Brief, DispatchEntry, OpDocParseError records

Acceptance:
- [ ] All records defined with correct field shapes
- [ ] Records are immutable
- [ ] Tests pass

Notes: Keep record shape close to visible doc structure.

OOS:
- No parsing or validation logic in this brief
- No external service integration

#### Brief 02: op-doc-parser

Goal: Hand-rolled line-oriented parser that reads an op-doc file and produces a populated OpDoc record with a list of parse errors.

Inputs:
- OpDoc and related types from Brief 01
- The strict op-doc format spec

Outputs:
- OpDocParser class with Parse static method returning ParseResult
- ParseResult record with Parsed and Errors fields

Acceptance:
- [ ] Parse returns populated OpDoc for valid fixture
- [ ] Parse errors include line numbers and section context
- [ ] Multiple plans and briefs are supported
- [ ] Tests pass

Notes: Be lenient about whitespace but strict about required sections.

OOS:
- No format version detection
- No op-doc generation from records
- No section reordering or auto-fix

#### Brief 03: op-doc-validator

Goal: Semantic validator checking a parsed OpDoc against the strict format spec.

Inputs:
- OpDoc from Brief 01
- Format spec rules

Outputs:
- OpDocValidator class with Validate method
- ValidationResult record with errors and warnings

Acceptance:
- [ ] All error rules implemented and tested
- [ ] All warning rules implemented and tested
- [ ] Tests pass

Notes: Validation is the gate before any external API calls.

OOS:
- No auto-fix of any validation issue
- No custom validation rules per project

## Plan B: Integration and CLI

### Goal

Integrate validated OpDoc with external services and add the CLI command.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | service-client | Client for external service integration | - | src/ServiceClient.cs |
| 02 | cli-command | CLI command wiring for scaffold | 01 | src/Cli/ScaffoldCommand.cs |

### Briefs - detail

#### Brief 01: service-client

Goal: Implement the client that creates records in the external service.

Inputs:
- Validated OpDoc from Plan A
- External service API documentation

Outputs:
- ServiceClient class with CreatePlanAsync and CreateBriefAsync methods
- Rollback support on partial failure

Acceptance:
- [ ] Successful creation returns result with ID
- [ ] Partial failure triggers rollback
- [ ] Tests pass against mocked HTTP layer

OOS:
- No direct database access
- No caching of API responses

#### Brief 02: cli-command

Goal: Wire the scaffold CLI command that reads an op-doc file and scaffolds it into the external service.

Inputs:
- ServiceClient from Brief 01
- OpDocParser and OpDocValidator from Plan A

Outputs:
- ScaffoldCommand class
- CLI registration

Acceptance:
- [ ] Command accepts op-doc file path argument
- [ ] Validation errors are reported before any API calls
- [ ] Successful scaffold prints created IDs
- [ ] Tests pass

Notes: Validation-first design is load-bearing.

OOS:
- No interactive mode in v1
- No dry-run support in v1

## What done looks like

- `build scaffold <op-doc-path>` command exists and works end-to-end
- Valid op-doc with two plans and five briefs scaffolds into external service without errors
- Invalid op-doc (missing OOS, malformed dispatch table) is rejected before any API calls with clear error messages
- All tests pass
