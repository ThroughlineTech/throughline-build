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

    private static Ticket ToTicket(PlaneIssue issue, IReadOnlyDictionary<string, PlaneState> statesById)
    {
        var stateName = statesById.TryGetValue(issue.StateId, out var st) ? st.Name : string.Empty;
        var ticketState = _stateNameMap.TryGetValue(stateName, out var ts) ? ts : TicketState.Backlog;

        return new Ticket(
            Id: issue.Id,
            Title: issue.Name,
            Type: issue.Type ?? string.Empty,
            State: ticketState,
            Size: Size.M,
            Risk: Risk.Medium,
            DescriptionHtml: issue.DescriptionHtml ?? string.Empty,
            Relations: [],
            Labels: issue.LabelIds.AsReadOnly(),
            ParentId: issue.ParentId);
    }

    // ------------------------------------------------------------------ ITicketing

    public async Task<Ticket> GetAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);

            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

            // Fetch states for translation
            var states = await GetStatesByNameAsync(token).ConfigureAwait(false);
            var statesById = states.ToDictionary(kvp => kvp.Value, kvp => new PlaneState(kvp.Value, kvp.Key, string.Empty));

            return ToTicket(issue, statesById);
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

            // Fetch the issue UUID first
            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);
            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

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
                throw new InvalidOperationException($"State '{stateName}' not found in Plane project");

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

            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);
            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

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

            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);
            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

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

            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);
            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

            // Resolve label names to UUIDs
            var labelsByName = await GetLabelsByNameAsync(token).ConfigureAwait(false);
            var labelIds = labels.Select(name =>
            {
                if (!labelsByName.TryGetValue(name, out var labelId))
                    throw new InvalidOperationException($"Label '{name}' not found in Plane project");
                return labelId;
            }).ToList();

            // Merge with existing label_ids
            var merged = issue.LabelIds.Union(labelIds).ToList();

            await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new ApplyLabelsRequest(merged),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);

            var issueList = await GetJsonAsync<PlaneIssueList>(
                $"{IssuesBase}?sequence_id={seq}", PlaneJsonContext.Default, token).ConfigureAwait(false);
            var issue = issueList.Results.FirstOrDefault()
                ?? throw new KeyNotFoundException($"Issue '{id}' not found in Plane");

            var relationList = await GetJsonAsync<PlaneRelationList>(
                $"{IssuesBase}{issue.Id}/relations/", PlaneJsonContext.Default, token).ConfigureAwait(false);

            return relationList.Results
                .Select(r => new Relation(r.RelationType, r.RelatedIssue))
                .ToList()
                .AsReadOnly();
        }, ct).ConfigureAwait(false);
    }
}
