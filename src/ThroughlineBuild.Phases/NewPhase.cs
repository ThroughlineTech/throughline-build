using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Workers.Common;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Options passed to NewPhase.RunAsync to configure ticket creation.
/// </summary>
public record NewPhaseOptions(
    string BodyPath,
    IReadOnlyList<string>? InitialLabels = null,
    string? OverrideTitle = null,
    string? OverrideType = null);

/// <summary>
/// Exception thrown when NewPhase encounters validation errors that block ticket creation.
/// Validation errors are fatal (e.g., missing title, empty body);
/// warnings are non-fatal and proceed to ticket creation.
/// </summary>
public class NewPhaseValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public NewPhaseValidationException(IReadOnlyList<string> errors)
        : base($"NewPhase validation failed: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }
}

/// <summary>
/// NewPhase orchestrates the deterministic create flow.
/// Reads a body file from disk, validates basic structure (title present, body non-empty,
/// acceptance criteria section present), calls CreateTicketAsync, emits events, returns
/// the new ticket ID and any validation warnings.
///
/// No LlmCall event is emitted for this deterministic phase; only WorkerSpawn (deterministic
/// creator dispatch) and TicketWrite (create_ticket action) events are recorded.
/// </summary>
public class NewPhase : IWorkflowPhase
{
    private readonly ITicketing _ticketing;
    private readonly IEventSink _events;
    private readonly BuildOptions _options;

    public NewPhase(ITicketing ticketing, IEventSink events, BuildOptions options)
    {
        _ticketing = ticketing;
        _events = events;
        _options = options;
    }

    public Phase Phase => Phase.New;

    /// <summary>
    /// Reads a body file, parses title, validates structure, creates a ticket,
    /// emits events, and returns a NewResult with the ticket ID, UUID, and warnings.
    /// </summary>
    public async Task<NewResult> RunAsync(NewPhaseOptions options, CancellationToken ct)
    {
        // Read and validate the body file.
        string bodyText;
        try
        {
            bodyText = await File.ReadAllTextAsync(options.BodyPath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new NewPhaseValidationException(new[] { $"Body file not found: {options.BodyPath}", ex.Message });
        }
        catch (Exception ex)
        {
            throw new NewPhaseValidationException(new[] { $"Failed to read body file: {ex.Message}" });
        }

        var errors = new List<string>();
        var warnings = new List<string>();

        // Parse title from body: first line with "# " prefix or "**Title:**" marker.
        string? title = ParseTitle(bodyText);
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add("No title found: body must start with '# ' (ATX heading) or contain '**Title:**' line");
        }

        // Check for empty body.
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            errors.Add("Body is empty");
        }

        // Block creation if there are errors.
        if (errors.Count > 0)
        {
            throw new NewPhaseValidationException(errors);
        }

        // Collect non-fatal warnings.
        if (!ContainsAcceptanceCriteria(bodyText))
        {
            warnings.Add("Body does not contain an 'Acceptance' or 'acceptance criteria' header");
        }

        if (bodyText.Length < 200)
        {
            warnings.Add("Body is shorter than 200 characters (recommend more detail)");
        }

        if (!ContainsOosSection(bodyText))
        {
            warnings.Add("Body does not contain an 'Out of scope' section");
        }

        if (!ContainsTypeField(bodyText))
        {
            warnings.Add("Body does not contain a Type field");
        }

        // Compose create call parameters.
        var createTitle = options.OverrideTitle ?? title!;
        var createType = options.OverrideType;
        var createBody = bodyText; // Pass through as-is; Plane handles markdown rendering.
        var createLabels = options.InitialLabels;

        // Emit WorkerSpawn event for deterministic creator dispatch.
        await EmitAsync(EventKind.WorkerSpawn, new Dictionary<string, object>
        {
            ["worker"] = "deterministic",
            ["role"] = "creator"
        }, ct).ConfigureAwait(false);

        // Call CreateTicketAsync.
        var ticketResult = await _ticketing.CreateTicketAsync(
            createTitle,
            createType,
            createBody,
            createLabels,
            ct).ConfigureAwait(false);

        // Emit TicketWrite event for the create action.
        await EmitAsync(EventKind.TicketWrite, new Dictionary<string, object>
        {
            ["action"] = "create_ticket",
            ["id"] = ticketResult.Id,
            ["uuid"] = ticketResult.Uuid
        }, ct).ConfigureAwait(false);

        return new NewResult(ticketResult.Id, ticketResult.Uuid, warnings.AsReadOnly());
    }

    /// <summary>
    /// Deterministic create from an already-structured draft (build new - --json): no worker,
    /// no markdown title-parsing. Renders description + acceptance criteria markdown to HTML,
    /// resolves parent and relation targets before creation, creates the ticket, then applies
    /// parent and relation writes.
    /// Throws NewPhaseValidationException on a missing title and KeyNotFoundException when the
    /// parent id does not resolve.
    /// </summary>
    public Task<NewResult> RunFromStructuredAsync(
        string? title,
        string? type,
        string? descriptionMarkdown,
        string? acceptanceCriteriaMarkdown,
        IReadOnlyList<string>? labels,
        string? parentId,
        CancellationToken ct) =>
        RunFromStructuredAsync(title, type, descriptionMarkdown, acceptanceCriteriaMarkdown,
            labels, parentId, null, ct);

    public async Task<NewResult> RunFromStructuredAsync(
        string? title,
        string? type,
        string? descriptionMarkdown,
        string? acceptanceCriteriaMarkdown,
        IReadOnlyList<string>? labels,
        string? parentId,
        IReadOnlyList<Relation>? relations,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new NewPhaseValidationException(new[] { "title is required" });

        // Compose one markdown body (description, then an Acceptance criteria section) and render
        // to HTML so Plane stores rich content rather than literal markdown.
        var bodyMarkdown = descriptionMarkdown ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(acceptanceCriteriaMarkdown))
        {
            if (bodyMarkdown.Length > 0)
                bodyMarkdown += "\n\n";
            bodyMarkdown += "## Acceptance criteria\n\n" + acceptanceCriteriaMarkdown.Trim();
        }
        var descriptionHtml = MarkdownRenderer.Render(bodyMarkdown);

        // Resolve the parent BEFORE creating, so a bad parent id fails fast (KeyNotFoundException)
        // without leaving an orphaned ticket behind.
        string? parentUuid = null;
        if (!string.IsNullOrWhiteSpace(parentId))
        {
            var parent = await _ticketing.GetAsync(parentId!, ct).ConfigureAwait(false);
            parentUuid = parent.Uuid;
        }

        // Resolve and validate every relation target before creating the ticket. An unknown target
        // therefore cannot leave a newly-created orphan. The subsequent POSTs cannot be atomic with
        // ticket creation; a later backend failure is reported with the created ticket id below.
        var resolvedRelations = new List<Relation>();
        foreach (var relation in relations ?? Array.Empty<Relation>())
        {
            if (!RelationKinds.TryNormalize(relation.Kind, out var normalizedKind))
                throw new NewPhaseValidationException(new[]
                {
                    $"invalid relation type '{relation.Kind}'; valid types: {string.Join(", ", RelationKinds.Allowed)}"
                });
            _ = await _ticketing.GetRelationTicketAsync(relation.TargetId, ct).ConfigureAwait(false);
            resolvedRelations.Add(new Relation(normalizedKind, relation.TargetId));
        }

        await EmitAsync(EventKind.WorkerSpawn, new Dictionary<string, object>
        {
            ["worker"] = "deterministic",
            ["role"] = "creator"
        }, ct).ConfigureAwait(false);

        var ticketResult = await _ticketing.CreateTicketAsync(
            title!, type, descriptionHtml, labels, ct).ConfigureAwait(false);

        await EmitAsync(EventKind.TicketWrite, new Dictionary<string, object>
        {
            ["action"] = "create_ticket",
            ["id"] = ticketResult.Id,
            ["uuid"] = ticketResult.Uuid
        }, ct).ConfigureAwait(false);

        if (parentUuid is not null)
        {
            await _ticketing.SetParentAsync(ticketResult.Uuid, parentUuid, ct).ConfigureAwait(false);
            await EmitAsync(EventKind.TicketWrite, new Dictionary<string, object>
            {
                ["action"] = "set_parent",
                ["id"] = ticketResult.Id,
                ["parent"] = parentId!
            }, ct).ConfigureAwait(false);
        }


        foreach (var relation in resolvedRelations)
        {
            try
            {
                await _ticketing.CreateRelationAsync(
                    ticketResult.Id, relation.Kind, relation.TargetId, ct).ConfigureAwait(false);
                await EmitAsync(EventKind.TicketWrite, new Dictionary<string, object>
                {
                    ["action"] = "create_relation",
                    ["id"] = ticketResult.Id,
                    ["kind"] = relation.Kind,
                    ["target"] = relation.TargetId
                }, ct).ConfigureAwait(false);
            }
            catch (RelationEndpointUnavailableException ex)
            {
                throw new RelationEndpointUnavailableException(
                    $"Ticket {ticketResult.Id} was created, but relation '{relation.Kind}' to {relation.TargetId} failed. " +
                    $"Earlier relation edges may already exist; inspect 'build relate {ticketResult.Id} --list'. {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Ticket {ticketResult.Id} was created, but relation '{relation.Kind}' to {relation.TargetId} failed. " +
                    $"Earlier relation edges may already exist; inspect 'build relate {ticketResult.Id} --list'. {ex.Message}", ex);
            }
        }

        return new NewResult(ticketResult.Id, ticketResult.Uuid, Array.Empty<string>());
    }

    /// <summary>
    /// Explicit IWorkflowPhase.RunAsync implementation.
    /// NewPhase creates tickets rather than operating on existing ones, so it does not
    /// support the ticketId-based dispatch pattern used by other phases.
    /// Callers (CLI) invoke the typed RunAsync(NewPhaseOptions) directly.
    /// </summary>
    async Task<PhaseResult> IWorkflowPhase.RunAsync(string ticketId, string workingDirectory, CancellationToken ct)
    {
        // NewPhase does not operate on existing tickets; this path should not be called.
        throw new NotSupportedException(
            "NewPhase creates new tickets and does not support the ticketId-based dispatch pattern. " +
            "Call RunAsync(NewPhaseOptions, CancellationToken) directly.");
    }

    private string? ParseTitle(string bodyText)
    {
        // Rule 1: First line starting with "# " (ATX heading).
        var lines = bodyText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var candidate = line.Substring(2).Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }
        }

        // Rule 2: First line matching "**Title:**" pattern.
        var titleMatch = Regex.Match(bodyText, @"\*\*Title:\*\*\s*(.+)", RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            var candidate = titleMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private bool ContainsAcceptanceCriteria(string bodyText)
    {
        // Case-insensitive substring search for "acceptance" in any heading-like line.
        return Regex.IsMatch(bodyText, @"(^|\n)\s*[#*]+\s*.*Acceptance.*", RegexOptions.IgnoreCase)
            || Regex.IsMatch(bodyText, @"(^|\n)\s*\*\*.*Acceptance.*\*\*", RegexOptions.IgnoreCase);
    }

    private bool ContainsOosSection(string bodyText)
    {
        // Look for "Out of scope" (case-insensitive) as a heading or marker.
        return Regex.IsMatch(bodyText, @"(^|\n)\s*[#*]+\s*.*Out\s+of\s+scope.*", RegexOptions.IgnoreCase)
            || Regex.IsMatch(bodyText, @"(^|\n)\s*\*\*.*Out\s+of\s+scope.*\*\*", RegexOptions.IgnoreCase);
    }

    private bool ContainsTypeField(string bodyText)
    {
        // Look for a "Type:" field (case-insensitive).
        return Regex.IsMatch(bodyText, @"\*\*Type:\*\*", RegexOptions.IgnoreCase)
            || Regex.IsMatch(bodyText, @"^Type:", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private async Task EmitAsync(EventKind kind, IReadOnlyDictionary<string, object> data, CancellationToken ct)
    {
        await _events.EmitAsync(new WorkflowEvent(
            _options.SessionId,
            DateTimeOffset.UtcNow,
            kind,
            "", // NewPhase does not operate on an existing ticket ID.
            Phase.New,
            data), ct).ConfigureAwait(false);
    }
}
