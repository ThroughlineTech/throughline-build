# Operation:build-foundation

Define the typed contracts (data model, interfaces) and pure helper functions that all downstream implementations will compose. Foundation work, no I/O, no external dependencies beyond .NET stdlib.

## Why this exists

The architecture's value depends on clean typed boundaries between components. This op-doc establishes those boundaries as compilable C# records and interfaces, plus the pure-function helpers that the deterministic phases need. With this in place, downstream implementation work proceeds against typed contracts instead of speculative shapes. Two libraries fall out: `ThroughlineBuild.Contracts` and `ThroughlineBuild.Helpers`. Both ship with full unit test coverage and zero external dependencies.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A    | Data model and interfaces | - | M |
| B    | Pure helpers | - | M |

Plans A and B can be implemented in parallel. They produce sibling projects in the solution.

## Plan A: Data model and interfaces

### Goal

A `ThroughlineBuild.Contracts` class library that defines the core record types and interface contracts the rest of the system will compose. No implementations, no I/O, no external dependencies beyond .NET stdlib.

Brief sequence: B01 records (foundation for all the rest). B02-06 interface definitions, each consuming records from B01. B02-06 can be implemented in any order after B01.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | data-records | Define Ticket, Brief, WorkerResult, Verdict, WorkflowEvent and supporting enums | - | src/ThroughlineBuild.Contracts/ThroughlineBuild.Contracts.csproj, src/ThroughlineBuild.Contracts/Models/*.cs |
| 02 | ticketing-contract | ITicketing interface and BackendCapabilities | 01 | src/ThroughlineBuild.Contracts/ITicketing.cs |
| 03 | llm-contract | ILlmClient interface and message/response types | 01 | src/ThroughlineBuild.Contracts/ILlmClient.cs |
| 04 | worker-contract | IWorkerAgent interface and WorkerOptions | 01 | src/ThroughlineBuild.Contracts/IWorkerAgent.cs |
| 05 | verifier-contract | IVerifier interface and GitDiff type | 01 | src/ThroughlineBuild.Contracts/IVerifier.cs |
| 06 | event-sink-contract | IEventSink interface | 01 | src/ThroughlineBuild.Contracts/IEventSink.cs |

### Briefs - detail

#### Brief 01: data-records

Goal: Define the core data records that the workflow operates on. C# 12 records with primary constructors and init-only properties. Immutable, value-equality, AOT-friendly.

Inputs:
- .NET 8 stdlib
- C# 12 record syntax

Outputs:
- `src/ThroughlineBuild.Contracts/ThroughlineBuild.Contracts.csproj` (classlib, `net8.0`, Nullable enabled, LangVersion 12)
- `src/ThroughlineBuild.Contracts/Models/Ticket.cs` with Ticket, TicketState, Size, Risk
- `src/ThroughlineBuild.Contracts/Models/Phase.cs` with the Phase enum
- `src/ThroughlineBuild.Contracts/Models/Brief.cs` with the Brief record
- `src/ThroughlineBuild.Contracts/Models/WorkerResult.cs` with WorkerResult and Status enum
- `src/ThroughlineBuild.Contracts/Models/Verdict.cs` with Verdict and VerdictKind enum
- `src/ThroughlineBuild.Contracts/Models/WorkflowEvent.cs` with WorkflowEvent and EventKind enum
- `src/ThroughlineBuild.Contracts/Models/Relation.cs` with Relation record
- A test project `tests/ThroughlineBuild.Contracts.Tests/` with equality and construction tests

Record shapes:

```csharp
public record Ticket(
    string Id,
    string Title,
    string Type,
    TicketState State,
    Size Size,
    Risk Risk,
    string DescriptionHtml,
    IReadOnlyList<Relation> Relations,
    IReadOnlyList<string> Labels,
    string? ParentId);

public enum TicketState { Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled }
public enum Size { S, M, L }
public enum Risk { Low, Medium, High }
public enum Phase { Plan, Implement, Review, Ship, Chain, New }

public record Brief(
    string TicketId,
    Phase Phase,
    string Instruction,
    IReadOnlyList<string> RelevantFiles,
    IReadOnlyList<string> AllowedWrites,
    IReadOnlyDictionary<string, string> Context);

public record WorkerResult(
    Status Status,
    string Summary,
    IReadOnlyList<string> FilesChanged,
    string? FailureReason,
    IReadOnlyDictionary<string, object> Metadata);

public enum Status { Ok, NeedsRework, Failed, Escalate }

public record Verdict(
    VerdictKind Kind,
    string Rationale,
    IReadOnlyList<string> ChecksFailed);

public enum VerdictKind { Pass, Rework, Fail }

public record WorkflowEvent(
    string SessionId,
    DateTimeOffset Timestamp,
    EventKind Kind,
    string TicketId,
    Phase Phase,
    IReadOnlyDictionary<string, object> Data);

public enum EventKind { StateTransition, LlmCall, WorkerSpawn, VerifierVerdict, GateFailure, TicketWrite }

public record Relation(string Kind, string TargetId);
```

Acceptance:
- [ ] All records compile under .NET 8 with `<Nullable>enable</Nullable>`
- [ ] All record types are immutable (no settable properties)
- [ ] All collection types are `IReadOnlyList<T>` or `IReadOnlyDictionary<TK,TV>`, never `List<T>` or `Dictionary<TK,TV>`
- [ ] xUnit tests verify value equality for each record
- [ ] EventKind exhaustively covers: StateTransition, LlmCall, WorkerSpawn, VerifierVerdict, GateFailure, TicketWrite
- [ ] Phase enum values are exactly: Plan, Implement, Review, Ship, Chain, New
- [ ] TicketState enum values are exactly: Backlog, Planning, Ready, InProgress, InReview, Done, Cancelled

Notes: Records get value-equality for free. Prefer enums over magic strings. The `IReadOnlyDictionary<string, object>` on WorkerResult.Metadata and WorkflowEvent.Data is the typed escape hatch for phase-specific payloads; downstream code is responsible for the shape contract per phase.

OOS:
- Do not add behavior methods to records (data only)
- Do not add JSON serialization attributes (source generators come later in op-doc 3)
- Do not add validation logic
- Do not reference any prior repos or systems

#### Brief 02: ticketing-contract

Goal: The ITicketing interface defining what a ticketing backend must provide. Plus BackendCapabilities for the partial-adapter pattern.

Inputs:
- Records from B01 (Ticket, Relation, TicketState)

Outputs:
- `src/ThroughlineBuild.Contracts/ITicketing.cs` containing:

```csharp
public interface ITicketing
{
    Task<Ticket> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct);
    Task TransitionAsync(string id, TicketState newState, CancellationToken ct);
    Task AppendDescriptionAsync(string id, string html, CancellationToken ct);
    Task<string> CreateCommentAsync(string id, string html, CancellationToken ct);
    Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct);
    Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct);
    BackendCapabilities Capabilities { get; }
}

public record BackendCapabilities(
    bool TypedRelations,
    bool TypedLabels,
    bool RichHtmlComments,
    bool Attachments);
```

Acceptance:
- [ ] Interface compiles
- [ ] All methods accept CancellationToken
- [ ] BackendCapabilities is a record (immutable)
- [ ] xUnit test verifies the interface can be implemented with a stub mock

Notes: CreateCommentAsync returns the new comment ID. ApplyLabelsAsync replaces the label set (not append).

OOS:
- Do not implement this interface (op-doc 3)
- Do not add a Plane-specific or GitHub-specific method
- Do not add convenience overloads

#### Brief 03: llm-contract

Goal: The ILlmClient interface for all LLM vendor implementations. Supports invoke-and-return and streaming modes.

Inputs:
- Records from B01
- `IAsyncEnumerable<T>` pattern

Outputs:
- `src/ThroughlineBuild.Contracts/ILlmClient.cs` containing:

```csharp
public interface ILlmClient
{
    Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);

    IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);
}

public record LlmMessage(string Role, string Content);
public record InvocationOptions(int? MaxTokens, double? Temperature);
public record LlmResponse(string Content, LlmUsage Usage);
public record LlmUsage(int InputTokens, int OutputTokens, int? CacheReadTokens, int? CacheWriteTokens);

public abstract record LlmStreamEvent;
public record LlmStreamTextDelta(string Text) : LlmStreamEvent;
public record LlmStreamUsage(LlmUsage Usage) : LlmStreamEvent;
public record LlmStreamDone : LlmStreamEvent;
```

Acceptance:
- [ ] Interface compiles
- [ ] modelId convention documented: `vendor:model` (e.g., `anthropic:claude-sonnet-4-7`)
- [ ] LlmUsage cache tokens nullable (vendor-specific)
- [ ] LlmStreamEvent as abstract record with derived records

Notes: Streaming uses `IAsyncEnumerable<T>`. Discriminated unions emulated via abstract records and derived records.

OOS:
- Do not implement any vendor client
- Do not add provider-specific options
- Do not add tool use schemas yet

#### Brief 04: worker-contract

Goal: The IWorkerAgent interface wrapping agent CLI subprocess invocation.

Inputs:
- Brief, WorkerResult from B01

Outputs:
- `src/ThroughlineBuild.Contracts/IWorkerAgent.cs` containing:

```csharp
public interface IWorkerAgent
{
    string Name { get; }
    Task<WorkerResult> ExecuteAsync(
        Brief brief,
        string workingDirectory,
        WorkerOptions options,
        CancellationToken ct);
}

public record WorkerOptions(
    TimeSpan Timeout,
    IReadOnlyList<string>? AllowedTools,
    IReadOnlyDictionary<string, string>? EnvironmentVariables);
```

Acceptance:
- [ ] Interface compiles
- [ ] WorkerOptions has optional collections (nullable for "use defaults")
- [ ] Name property identifies the worker (e.g., "claude-code", "codex", "gemini")

Notes: Different vendor CLIs accept different flags. WorkerOptions stays generic; vendor-specific concerns live in the implementations.

OOS:
- Do not implement any worker
- Do not add vendor-specific options
- Do not add streaming worker output

#### Brief 05: verifier-contract

Goal: The IVerifier interface for independent verification on a worker's diff. No shared context with the implementer.

Inputs:
- Brief, Verdict, WorkerResult from B01

Outputs:
- `src/ThroughlineBuild.Contracts/IVerifier.cs` containing:

```csharp
public interface IVerifier
{
    Task<Verdict> VerifyAsync(
        Brief brief,
        GitDiff diff,
        WorkerResult workerResult,
        CancellationToken ct);
}

public record GitDiff(
    string FromRef,
    string ToRef,
    IReadOnlyList<DiffEntry> Entries);

public record DiffEntry(
    string Path,
    DiffKind Kind,
    string? OldPath,
    int LinesAdded,
    int LinesRemoved,
    string? PatchContent);

public enum DiffKind { Added, Modified, Deleted, Renamed }
```

Acceptance:
- [ ] Interface compiles
- [ ] GitDiff and DiffEntry are records
- [ ] DiffKind enum exhaustive
- [ ] PatchContent nullable (for large diffs that omit content)

Notes: GitDiff is the typed boundary between git operations and the verifier. The verifier receives only what's in this record.

OOS:
- Do not implement a verifier
- Do not embed git logic in this interface

#### Brief 06: event-sink-contract

Goal: The IEventSink interface for appending WorkflowEvent entries.

Inputs:
- WorkflowEvent from B01

Outputs:
- `src/ThroughlineBuild.Contracts/IEventSink.cs` containing:

```csharp
public interface IEventSink
{
    Task EmitAsync(WorkflowEvent ev, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}
```

Acceptance:
- [ ] Interface compiles
- [ ] FlushAsync provided for graceful shutdown

Notes: Implementations may buffer; FlushAsync ensures durable write before exit.

OOS:
- Do not implement
- Do not add query/replay methods

## Plan B: Pure helpers

### Goal

A `ThroughlineBuild.Helpers` class library containing pure-function helpers for the deterministic phases. No I/O, no external services. Each helper fully unit-tested with fixtures.

Brief sequence: All four briefs are independent and can be implemented in any order.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | slug-builder | Build a branch slug from ticket ID + title | - | src/ThroughlineBuild.Helpers/ThroughlineBuild.Helpers.csproj, src/ThroughlineBuild.Helpers/SlugBuilder.cs |
| 02 | marker-parser | Parse `[name: value]` markers from comment text | - | src/ThroughlineBuild.Helpers/MarkerParser.cs |
| 03 | doc-only-detector | Detect whether a list of file paths is all documentation | - | src/ThroughlineBuild.Helpers/DocOnlyDetector.cs |
| 04 | drift-comparator | Compare planned-at SHA to current; report overlap with relevant files | - | src/ThroughlineBuild.Helpers/DriftComparator.cs |

### Briefs - detail

#### Brief 01: slug-builder

Goal: Pure function that takes a ticket ID and title and returns a sanitized branch slug.

Inputs:
- Two strings: ticket ID (e.g., "TT2-113"), title (e.g., "Register canonical-symbol normalizers in DI")
- Slugification rules: lowercase, ASCII only, hyphens for word breaks, truncate to ~80 chars total, strip leading/trailing hyphens, collapse consecutive hyphens

Outputs:
- `src/ThroughlineBuild.Helpers/ThroughlineBuild.Helpers.csproj` (classlib)
- `src/ThroughlineBuild.Helpers/SlugBuilder.cs` with `public static string BuildBranchSlug(string ticketId, string title)`
- xUnit tests covering: normal case, very long title, special characters, non-ASCII characters, empty title

Acceptance:
- [ ] `BuildBranchSlug("TT2-113", "Register canonical-symbol normalizers in DI")` returns `tt2-113-register-canonical-symbol-normalizers-in-di`
- [ ] Result is always ASCII-only
- [ ] Result is at most 80 characters
- [ ] No leading or trailing hyphens
- [ ] Consecutive hyphens collapsed to one
- [ ] xUnit tests pass

Notes: Lowercase via `string.ToLowerInvariant()`. For non-ASCII characters, strip or transliterate (transliteration optional, stripping is fine for v1).

OOS:
- Do not add a "validate existing slug" method
- Do not consult any external service

#### Brief 02: marker-parser

Goal: Parse marker comments of the form `[name: value]` or `[name]` from a block of HTML or plain text.

Inputs:
- A string of comment text (may contain HTML or plain text)
- Marker format examples: `[planned_at: abc123]`, `[implemented]`, `[reviewed: pass]`

Outputs:
- `src/ThroughlineBuild.Helpers/MarkerParser.cs` with:

```csharp
public static class MarkerParser
{
    public static IReadOnlyList<Marker> Parse(string commentText);
}

public record Marker(string Name, string? Value);
```

- xUnit tests covering: single marker, multiple markers, marker with no value, markers in HTML, malformed markers (skipped, not thrown)

Acceptance:
- [ ] `Parse("[planned_at: abc123]")` returns one Marker(Name="planned_at", Value="abc123")
- [ ] `Parse("[implemented]")` returns one Marker(Name="implemented", Value=null)
- [ ] `Parse("<p>text [planned_at: def456] more</p>")` extracts the marker
- [ ] Malformed markers don't throw
- [ ] xUnit tests pass

Notes: Simple regex or character scan. Do not pull in an HTML parser.

OOS:
- Do not implement marker writing (separate)
- Do not validate marker names against a schema

#### Brief 03: doc-only-detector

Goal: Given a list of file paths, return true if every path is a documentation file or in a documentation directory.

Inputs:
- A list of relative file paths

Outputs:
- `src/ThroughlineBuild.Helpers/DocOnlyDetector.cs` with:

```csharp
public static class DocOnlyDetector
{
    public static bool IsDocOnly(IEnumerable<string> changedFiles);
}
```

- xUnit tests covering: all-doc list, mixed list, all-code list, empty list, nested docs/ paths, README files

Acceptance:
- [ ] All-`.md` list returns true
- [ ] Mixed list returns false
- [ ] All-`.cs` list returns false
- [ ] Empty list returns false
- [ ] Paths under any `docs/` or `documentation/` directory count as docs
- [ ] README files at any level count as docs
- [ ] xUnit tests pass

Notes: Doc extensions: `.md`, `.markdown`, `.txt`, `.rst`, `.adoc`. Directory match case-insensitive on the component name.

OOS:
- Do not read file contents (path-based only)
- Do not consult git
- Do not classify by language

#### Brief 04: drift-comparator

Goal: Given a marker SHA and current SHA plus a list of relevant files and a list of files changed between, report whether any relevant file overlaps with changes.

Inputs:
- Marker SHA (string), recorded when the plan phase ran
- Current SHA (string)
- Relevant files (list of paths)
- Files changed between marker and current (list of paths; git operation is injected, not performed here)

Outputs:
- `src/ThroughlineBuild.Helpers/DriftComparator.cs` with:

```csharp
public static class DriftComparator
{
    public static DriftResult Compare(
        string markerSha,
        string currentSha,
        IReadOnlyList<string> relevantFiles,
        IReadOnlyList<string> filesChangedBetween);
}

public record DriftResult(bool HasDrift, IReadOnlyList<string> OverlappingFiles);
```

- xUnit tests covering: same SHA (no drift), different SHAs no overlap, different SHAs with overlap, empty relevant files

Acceptance:
- [ ] Same SHA returns `DriftResult(false, [])`
- [ ] Different SHAs no overlap returns `DriftResult(false, [])`
- [ ] Different SHAs with overlap returns `DriftResult(true, [...overlapping paths])`
- [ ] xUnit tests pass
- [ ] No git I/O in this helper

Notes: The git operation is left to a separate component. This helper stays pure.

OOS:
- Do not call git
- Do not parse refs or SHAs (treat as opaque strings)
- Do not validate SHA format

## What done looks like

Two class libraries (`ThroughlineBuild.Contracts` and `ThroughlineBuild.Helpers`) compile clean as part of the solution, with full xUnit test coverage on the helpers and equality tests on the records. Both libraries have zero external dependencies beyond .NET 8 stdlib. The CLI project from op-doc 1 can reference both libraries successfully. Op-doc 3 can begin implementing concrete clients (PlaneTicketingClient, AnthropicClient, ClaudeCodeAgent, JsonlEventSink) and the plan phase against these typed contracts.
