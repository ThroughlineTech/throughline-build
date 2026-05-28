using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;
using Polly.Retry;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Plane;

/// <summary>
/// ITicketing implementation backed by the Plane REST API.
/// </summary>
public sealed class PlaneTicketingClient : ITicketing
{
    private readonly HttpClient _http;
    private readonly PlaneClientOptions _options;
    private readonly ResiliencePipeline _pipeline;

    // State cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _statesByName;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    // Label cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _labelsByName;
    private readonly SemaphoreSlim _labelLock = new(1, 1);

    // Issue-type cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _issueTypesByName;
    private readonly SemaphoreSlim _issueTypeLock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        TypeInfoResolver = PlaneJsonContext.Default,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PlaneTicketingClient(HttpClient httpClient, PlaneClientOptions options)
    {
        _http = httpClient;
        _options = options;

        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Add("X-API-Key", options.ApiToken);

        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<PlaneApiException>(ex => ex.Status == 429 || ex.Status >= 500),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1)
            })
            .Build();
    }

    public BackendCapabilities Capabilities => new(
        TypedRelations: true,
        TypedLabels: true,
        RichHtmlComments: true,
        Attachments: true);

    // ------------------------------------------------------------------ helpers

    private string IssuesBase =>
        $"api/v1/workspaces/{_options.WorkspaceSlug}/projects/{_options.ProjectId}/issues/";

    private string StatesBase =>
        $"api/v1/workspaces/{_options.WorkspaceSlug}/projects/{_options.ProjectId}/states/";

    private string LabelsBase =>
        $"api/v1/workspaces/{_options.WorkspaceSlug}/projects/{_options.ProjectId}/labels/";

    private string IssueTypesBase =>
        $"api/v1/workspaces/{_options.WorkspaceSlug}/projects/{_options.ProjectId}/issue-types/";

    private static int ParseSequenceId(string id)
    {
        // Accept "TLB-24" or "24"
        var dash = id.LastIndexOf('-');
        var raw = dash >= 0 ? id[(dash + 1)..] : id;
        if (!int.TryParse(raw, out var seq))
            throw new ArgumentException($"Cannot parse sequence id from '{id}'");
        return seq;
    }

    private async Task<T> GetJsonAsync<T>(string url, JsonSerializerContext ctx, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, body);

        var result = (T?)JsonSerializer.Deserialize(body, typeof(T), ctx);
        return result ?? throw new InvalidOperationException($"Deserialized null for {typeof(T).Name}");
    }

    private async Task<string> PatchJsonAsync<TBody>(string url, TBody body, JsonSerializerContext ctx, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, typeof(TBody), new JsonSerializerOptions
        {
            TypeInfoResolver = ctx,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PatchAsync(url, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, responseBody);
        return responseBody;
    }

    private async Task<string> PostJsonAsync<TBody>(string url, TBody body, JsonSerializerContext ctx, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, typeof(TBody), new JsonSerializerOptions
        {
            TypeInfoResolver = ctx,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, responseBody);
        return responseBody;
    }

    // ------------------------------------------------------------------ state/label caches

    private async Task<Dictionary<string, string>> GetStatesByNameAsync(CancellationToken ct)
    {
        if (_statesByName is not null) return _statesByName;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_statesByName is not null) return _statesByName;
            var list = await GetJsonAsync<PlaneStateList>(StatesBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            _statesByName = list.Results.ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);
            return _statesByName;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<Dictionary<string, string>> GetLabelsByNameAsync(CancellationToken ct)
    {
        if (_labelsByName is not null) return _labelsByName;

        await _labelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_labelsByName is not null) return _labelsByName;
            var list = await GetJsonAsync<PlaneLabelList>(LabelsBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            _labelsByName = list.Results.ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);
            return _labelsByName;
        }
        finally
        {
            _labelLock.Release();
        }
    }

    private async Task<Dictionary<string, string>> GetIssueTypesByNameAsync(CancellationToken ct)
    {
        if (_issueTypesByName is not null) return _issueTypesByName;

        await _issueTypeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_issueTypesByName is not null) return _issueTypesByName;
            var list = await GetJsonAsync<PlaneIssueTypeList>(IssueTypesBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            _issueTypesByName = list.Results.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
            return _issueTypesByName;
        }
        finally
        {
            _issueTypeLock.Release();
        }
    }

    // Reverse state lookup: uuid -> TicketState
    private static readonly Dictionary<string, TicketState> _stateNameMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Backlog"]     = TicketState.Backlog,
            ["Planning"]    = TicketState.Planning,
            ["Ready"]       = TicketState.Ready,
            ["In Progress"] = TicketState.InProgress,
            ["In Review"]   = TicketState.InReview,
            ["Done"]        = TicketState.Done,
            ["Cancelled"]   = TicketState.Cancelled,
        };

    // ------------------------------------------------------------------ translation

    private async Task<PlaneIssue> FindIssueAsync(int seq, CancellationToken ct)
    {
        var issueList = await GetJsonAsync<PlaneIssueList>(
            $"{IssuesBase}?per_page=100", PlaneJsonContext.Default, ct).ConfigureAwait(false);
        return issueList.Results.FirstOrDefault(i => i.SequenceId == seq)
            ?? throw new KeyNotFoundException($"Issue with sequence_id {seq} not found in Plane");
    }

    private async Task<Ticket> ToTicketAsync(PlaneIssue issue, CancellationToken ct)
    {
        var states = await GetStatesByNameAsync(ct).ConfigureAwait(false);
        var statesById = states.ToDictionary(kvp => kvp.Value, kvp => new PlaneState(kvp.Value, kvp.Key, string.Empty));
        var stateName = statesById.TryGetValue(issue.StateId, out var st) ? st.Name : string.Empty;
        var ticketState = _stateNameMap.TryGetValue(stateName, out var ts) ? ts : TicketState.Backlog;

        var labelsByName = await GetLabelsByNameAsync(ct).ConfigureAwait(false);
        var labelsById = labelsByName.ToDictionary(kvp => kvp.Value, kvp => kvp.Key, StringComparer.OrdinalIgnoreCase);
        var resolvedLabels = (issue.LabelIds ?? [])
            .Where(uid => labelsById.ContainsKey(uid))
            .Select(uid => labelsById[uid])
            .ToList();

        var sizeLabel = resolvedLabels.FirstOrDefault(
            l => l.StartsWith("size:", StringComparison.OrdinalIgnoreCase));
        var ticketSize = sizeLabel?.ToLowerInvariant() switch
        {
            "size:s" => Size.S,
            "size:l" => Size.L,
            _ => Size.M
        };

        return new Ticket(
            Id: issue.Id,
            Title: issue.Name,
            Type: issue.Type ?? string.Empty,
            State: ticketState,
            Size: ticketSize,
            Risk: Risk.Medium,
            DescriptionHtml: issue.DescriptionHtml ?? string.Empty,
            Relations: [],
            Labels: resolvedLabels.AsReadOnly(),
            ParentId: issue.ParentId);
    }

    // ------------------------------------------------------------------ ITicketing

    public async Task<Ticket> GetAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);
            return await ToTicketAsync(issue, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Ticket>> GetBatchAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var tasks = ids.Select(id => GetAsync(id, ct));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public async Task TransitionAsync(string id, TicketState newState, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            // Resolve state name -> uuid
            var stateName = newState switch
            {
                TicketState.Backlog     => "Backlog",
                TicketState.Planning    => "Planning",
                TicketState.Ready       => "Ready",
                TicketState.InProgress  => "In Progress",
                TicketState.InReview    => "In Review",
                TicketState.Done        => "Done",
                TicketState.Cancelled   => "Cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(newState))
            };

            var statesByName = await GetStatesByNameAsync(token).ConfigureAwait(false);
            if (!statesByName.TryGetValue(stateName, out var stateId))
            {
                // Plane project doesn't have this state installed. Warn and leave the
                // ticket where it is; a subsequent TransitionAsync to a state that
                // does exist will still proceed normally.
                Console.Error.WriteLine(
                    $"Warning: Plane project has no '{stateName}' state; leaving {id} in its current state.");
                return;
            }

            await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new TransitionRequest(stateId),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            var existing = issue.DescriptionHtml ?? string.Empty;
            var combined = existing + html;

            await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new AppendDescriptionRequest(combined),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> CreateCommentAsync(string id, string html, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            var responseBody = await PostJsonAsync(
                $"{IssuesBase}{issue.Id}/comments/",
                new CreateCommentRequest(html),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            var comment = (PlaneComment?)JsonSerializer.Deserialize(
                responseBody, typeof(PlaneComment), PlaneJsonContext.Default);
            return comment?.Id ?? string.Empty;
        }, ct).ConfigureAwait(false);
    }

    public async Task ApplyLabelsAsync(string id, IEnumerable<string> labels, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            // Resolve label names to UUIDs
            var labelsByName = await GetLabelsByNameAsync(token).ConfigureAwait(false);
            var labelIds = labels.Select(name =>
            {
                if (!labelsByName.TryGetValue(name, out var labelId))
                    throw new InvalidOperationException($"Label '{name}' not found in Plane project");
                return labelId;
            }).ToList();

            await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new ApplyLabelsRequest(labelIds),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            var relationList = await GetJsonAsync<PlaneRelationList>(
                $"{IssuesBase}{issue.Id}/relations/", PlaneJsonContext.Default, token).ConfigureAwait(false);

            return relationList.Results
                .Select(r => new Relation(r.RelationType, r.RelatedIssue))
                .ToList()
                .AsReadOnly();
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TicketComment>> GetCommentsAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            try
            {
                var seq = ParseSequenceId(id);
                var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

                var list = await GetJsonAsync<PlaneCommentList>(
                    $"{IssuesBase}{issue.Id}/comments/", PlaneJsonContext.Default, token).ConfigureAwait(false);

                if (list.Results is null || list.Results.Count == 0)
                    return (IReadOnlyList<TicketComment>)Array.Empty<TicketComment>();

                return (IReadOnlyList<TicketComment>)list.Results
                    .Select(c => new TicketComment(c.Id, c.CommentHtml ?? string.Empty, c.CreatedAt))
                    .ToList();
            }
            catch (PlaneApiException ex) when (ex.Status == 404)
            {
                return (IReadOnlyList<TicketComment>)Array.Empty<TicketComment>();
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<RollupResult> RollupParentAsync(string id, CancellationToken ct)
    {
        try
        {
            var seq = ParseSequenceId(id);
            var childIssue = await FindIssueAsync(seq, ct).ConfigureAwait(false);

            if (childIssue.ParentId is null)
                return new RollupResult(false, null, null);

            var parentId = childIssue.ParentId;

            // Load state cache now for both child-state-name lookup and desired-state-UUID resolution
            var statesByName = await GetStatesByNameAsync(ct).ConfigureAwait(false);
            var statesById = statesByName.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
            var childStateName = statesById.GetValueOrDefault(childIssue.StateId, string.Empty);

            // GET parent with expand=state
            var parentExpanded = await GetJsonAsync<PlaneIssueExpanded>(
                $"{IssuesBase}{parentId}/?expand=state", PlaneJsonContext.Default, ct).ConfigureAwait(false);
            var currentParentStateName = ExtractStateName(parentExpanded.State);

            // GET siblings with parent filter and expand=state
            var siblingsResult = await GetJsonAsync<PlaneIssueExpandedList>(
                $"{IssuesBase}?per_page=100&parent={parentId}&expand=state", PlaneJsonContext.Default, ct).ConfigureAwait(false);

            // Apply ranked rules to determine desired state
            var desired = ApplyRollupRules(siblingsResult.Results);
            if (desired is null)
                return new RollupResult(false, null, null);

            var currentRank = StateRank(currentParentStateName);
            var desiredRank = StateRank(desired);

            if (desiredRank <= currentRank)
                return new RollupResult(false, null, null);

            // Resolve desired state name -> UUID
            if (!statesByName.TryGetValue(desired, out var desiredStateId))
                return new RollupResult(false, null, $"State '{desired}' not found in Plane project");

            // PATCH parent state
            await PatchJsonAsync(
                $"{IssuesBase}{parentId}/",
                new TransitionRequest(desiredStateId),
                PlaneJsonContext.Default,
                ct).ConfigureAwait(false);

            // POST comment: [rollup] marker is load-bearing
            var commentHtml = $"<p>[rollup] {_options.ProjectIdentifier}-{childIssue.SequenceId} -> {childStateName}; parent -> {desired}</p>";
            await PostJsonAsync(
                $"{IssuesBase}{parentId}/comments/",
                new CreateCommentRequest(commentHtml),
                PlaneJsonContext.Default,
                ct).ConfigureAwait(false);

            return new RollupResult(true, desired, null);
        }
        catch (Exception ex)
        {
            return new RollupResult(false, null, ex.Message);
        }
    }

    public async Task<NewTicketResult> CreateTicketAsync(
        string title,
        string type,
        string descriptionHtml,
        IReadOnlyList<string>? initialLabelNames,
        CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            // Resolve label names to UUIDs if any are provided
            var labelIds = new List<string>();
            if (initialLabelNames is { Count: > 0 })
            {
                var labelsByName = await GetLabelsByNameAsync(token).ConfigureAwait(false);
                foreach (var name in initialLabelNames)
                {
                    if (!labelsByName.TryGetValue(name, out var labelId))
                        throw new InvalidOperationException($"Label '{name}' not found in Plane project");
                    labelIds.Add(labelId);
                }
            }

            string? typeId = null;
            if (!string.IsNullOrEmpty(type))
            {
                var issueTypesByName = await GetIssueTypesByNameAsync(token).ConfigureAwait(false);
                if (!issueTypesByName.TryGetValue(type, out typeId))
                    throw new InvalidOperationException($"Issue type '{type}' not found in Plane project");
            }

            var request = new CreateIssueRequest(
                Name: title,
                DescriptionHtml: descriptionHtml,
                Type: typeId,
                LabelIds: labelIds);

            var responseBody = await PostJsonAsync(
                IssuesBase,
                request,
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            var response = (PlaneCreateIssueResponse?)JsonSerializer.Deserialize(
                responseBody, typeof(PlaneCreateIssueResponse), PlaneJsonContext.Default)
                ?? throw new InvalidOperationException("Deserialized null for PlaneCreateIssueResponse");

            return new NewTicketResult(
                Id: $"{_options.ProjectIdentifier}-{response.SequenceId}",
                Uuid: response.Id,
                CreatedAt: response.CreatedAt);
        }, ct).ConfigureAwait(false);
    }

    public async Task SetParentAsync(string childUuid, string parentUuid, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            await PatchJsonAsync(
                $"{IssuesBase}{childUuid}/",
                new SetParentRequest(parentUuid),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ rollup helpers

    private static string ExtractStateName(JsonElement stateElement)
    {
        if (stateElement.ValueKind == JsonValueKind.Object)
        {
            if (stateElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                return nameEl.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static int StateRank(string stateName) =>
        stateName switch
        {
            "Backlog"     => 0,
            "Ready"       => 1,
            "In Progress" => 2,
            "In Review"   => 3,
            "Done"        => 4,
            "Cancelled"   => 5,
            _             => -1
        };

    private static string? ApplyRollupRules(List<PlaneIssueExpanded> siblings)
    {
        if (siblings.Count == 0)
            return null;

        var stateNames = siblings.Select(s => ExtractStateName(s.State)).ToList();
        var nonCancelled = stateNames.Where(n => n != "Cancelled").ToList();

        // Rule 1: all non-Cancelled are Done -> "Done"
        if (nonCancelled.Count > 0 && nonCancelled.All(n => n == "Done"))
            return "Done";

        // Rule 2: all non-Cancelled are in (In Review, Done) -> "In Review"
        if (nonCancelled.Count > 0 && nonCancelled.All(n => n == "In Review" || n == "Done"))
            return "In Review";

        // Rule 3: any child in (In Progress, In Review, Done) -> "In Progress"
        if (stateNames.Any(n => n == "In Progress" || n == "In Review" || n == "Done"))
            return "In Progress";

        // Rule 4: all children Cancelled, no Done -> "Cancelled"
        if (stateNames.All(n => n == "Cancelled") && !stateNames.Contains("Done"))
            return "Cancelled";

        return null;
    }
}
