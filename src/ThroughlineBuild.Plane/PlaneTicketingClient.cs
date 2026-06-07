using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Polly;
using Polly.Retry;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Plane;

/// <summary>
/// ITicketing implementation backed by the Plane REST API.
/// </summary>
public sealed class PlaneTicketingClient : ITicketing, ITicketingProvisioner, ITicketingConnectivity, IProjectDiscovery
{
    private readonly HttpClient _http;
    private readonly PlaneClientOptions _options;
    private readonly ResiliencePipeline _pipeline;

    // Hard rate gate: every HTTP send waits here so we never exceed Plane's 60/min limit.
    private readonly RequestThrottle _throttle;

    // State cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _statesByName;
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    // Label cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _labelsByName;
    private readonly SemaphoreSlim _labelLock = new(1, 1);

    // Issue-type cache: name -> uuid, lazy-loaded on first use
    private Dictionary<string, string>? _issueTypesByName;
    private readonly SemaphoreSlim _issueTypeLock = new(1, 1);

    // Operation-wide issue snapshot. Within a single run the project's issue set, the
    // seq<->uuid identity map, and the parent/child hierarchy do not change, so we paginate
    // the whole project exactly once and answer every FindIssueAsync lookup and QueryAsync
    // parent-probe from memory. Ticket STATE does change across phases, so each mutating call
    // updates the cached issue in place (write-through) - we own every write, so a read through
    // the snapshot is never stale. This collapses what used to be O(tickets x project_pages)
    // HTTP calls (a full pagination per phase, per ticket) down to one pagination per run, which
    // is what kept `build chain` hammering Plane's rate limiter as the project grew. (TLB-366)
    // seq -> uuid is immutable identity: written once at load, never mutated.
    private readonly ConcurrentDictionary<int, string> _seqToUuid = new();
    // uuid -> issue is the single mutable source of truth. Write-through updates it via an
    // atomic AddOrUpdate so concurrent mutations (parallel sibling rollups on a shared parent,
    // or a Transition racing an ApplyLabels) compose against the live value instead of
    // clobbering each other. Keeping the seq index as seq->uuid (not seq->issue) means there
    // is only one mutable copy of an issue, so the two indexes can never drift.
    //
    // Freshness scope: the snapshot reflects every write THIS client makes (state/labels/parent
    // are stored verbatim by Plane; descriptions are re-read from the PATCH response). It does
    // not poll for changes made by other processes mid-run - the project is treated as stable
    // for the run's duration, which is the operating assumption that makes the one-shot load safe.
    private readonly ConcurrentDictionary<string, PlaneIssue> _issueByUuid = new();
    // Per-run relations cache: sibling dependency analysis may ask for the same ticket's relations
    // multiple times within one chain run. Relations are effectively stable for the run, so cache
    // them by Plane issue UUID after the first GET and reuse them thereafter.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Relation>> _relationsByIssueUuid = new();
    private volatile bool _snapshotLoaded;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

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

        _throttle = new RequestThrottle(options.RequestsPerMinute, TimeSpan.FromMinutes(1));

        var maxRetryDelay = options.MaxRetryDelay;
        _pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<PlaneApiException>(ex => ex.Status == 429 || ex.Status >= 500),
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                // Jitter desynchronizes two concurrent build instances that would
                // otherwise retry in lockstep and keep colliding on the same window.
                UseJitter = true,
                Delay = options.RetryBaseDelay,
                // When Plane sends Retry-After (429s from its limiter do), wait exactly
                // that long instead of our exponential guess - the window is shared with
                // other processes, so our blind backoff is usually too short. Returning
                // null falls back to the exponential-with-jitter delay above.
                DelayGenerator = args =>
                {
                    if (args.Outcome.Exception is PlaneApiException { RetryAfter: { } retryAfter })
                    {
                        var capped = retryAfter > maxRetryDelay ? maxRetryDelay : retryAfter;
                        if (capped < TimeSpan.Zero) capped = TimeSpan.Zero;
                        return new ValueTask<TimeSpan?>(capped);
                    }
                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                },
                OnRetry = args =>
                {
                    Console.Error.WriteLine($"[plane] retry {args.AttemptNumber + 1} after {args.RetryDelay.TotalSeconds:F1}s");
                    return default;
                }
            })
            .Build();
    }

    public BackendCapabilities Capabilities => new(
        TypedRelations: true,
        TypedLabels: true,
        RichHtmlComments: true,
        Attachments: true);

    public async Task<TicketingConnectivityResult> TestConnectivityAsync(CancellationToken ct)
    {
        try
        {
            await GetJsonAsync<PlaneLabelList>(LabelsBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            await GetJsonAsync<PlaneStateList>(StatesBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            await ProbeIssueCreatePermissionAsync(ct).ConfigureAwait(false);
            return new TicketingConnectivityResult(true, "Plane project connectivity and issue create permission OK.");
        }
        catch (PlaneApiException ex) when (ex.Status is 401 or 403)
        {
            return new TicketingConnectivityResult(
                false,
                $"Plane project connectivity failed: API token is not authorized to create issues in workspace '{_options.WorkspaceSlug}' project '{_options.ProjectId}' ({ex.Message}).");
        }
        catch (PlaneApiException ex) when (ex.Status == 404)
        {
            return new TicketingConnectivityResult(false, ProjectNotFoundMessage(ex));
        }
        catch (PlaneApiException ex)
        {
            return new TicketingConnectivityResult(
                false,
                $"Plane project connectivity failed for workspace '{_options.WorkspaceSlug}' project '{_options.ProjectId}' ({ex.Message}).");
        }
        catch (Exception ex)
        {
            return new TicketingConnectivityResult(
                false,
                $"Plane project connectivity failed for workspace '{_options.WorkspaceSlug}' project '{_options.ProjectId}': {ex.Message}");
        }
    }

    private string ProjectNotFoundMessage(PlaneApiException ex) =>
        BuildProjectNotFoundMessage(_options.WorkspaceSlug, _options.ProjectId, ex);

    /// <summary>
    /// Actionable message for a 404 on a project-scoped Plane route: the configured
    /// project id does not resolve to a project in this workspace, so it is either
    /// wrong or the project was never created. Shared by connectivity probing and by
    /// 'build setup' so both report the same remedy instead of a raw "Page not found.".
    /// </summary>
    public static string BuildProjectNotFoundMessage(string workspaceSlug, string projectId, PlaneApiException ex) =>
        $"Plane project not found: plane_project_id '{projectId}' does not resolve to a project in workspace "
        + $"'{workspaceSlug}'. The id is wrong or the project was never created. Re-run 'build init' connected mode "
        + $"(or fix plane_project_id in .build/config.toml) and retry. ({ex.Message})";

    private async Task ProbeIssueCreatePermissionAsync(CancellationToken ct)
    {
        // This deliberately invalid POST reaches the same create route scaffold uses,
        // but cannot create an issue because the required name field has the wrong type.
        // Plane should run authorization before request validation: 400/422 means the
        // token got as far as create validation, while 401/403 means it cannot create.
        const string probeJson = """
        {"name":{"throughline_build_probe":true},"description_html":"<p>Throughline Build create-permission probe.</p>","label_ids":[]}
        """;

        using var content = new StringContent(probeJson, System.Text.Encoding.UTF8, "application/json");
        await _throttle.AcquireAsync(ct).ConfigureAwait(false);
        var response = await _http.PostAsync(IssuesBase, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if ((int)response.StatusCode is 400 or 422)
            return;

        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, responseBody, ParseRetryAfter(response));

        throw new PlaneApiException(
            (int)response.StatusCode,
            "Plane create-permission probe unexpectedly succeeded; refusing to treat readiness as verified because the probe may have created an issue.",
            ParseRetryAfter(response));
    }

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

    /// <summary>
    /// Extracts the <c>Retry-After</c> back-off hint from a response, supporting both
    /// the seconds-delta and HTTP-date forms. Returns null when the header is absent.
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;
        if (retryAfter.Delta is { } delta)
            return delta;
        if (retryAfter.Date is { } date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private async Task<T> GetJsonAsync<T>(string url, JsonSerializerContext ctx, CancellationToken ct)
    {
        await _throttle.AcquireAsync(ct).ConfigureAwait(false);
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, body, ParseRetryAfter(response));

        var result = (T?)JsonSerializer.Deserialize(body, typeof(T), ctx);
        return result ?? throw new InvalidOperationException($"Deserialized null for {typeof(T).Name}");
    }

    private async Task<string> PatchJsonAsync<TBody>(string url, TBody body, JsonSerializerContext ctx, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, WriteTypeInfo<TBody>(ctx));
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await _throttle.AcquireAsync(ct).ConfigureAwait(false);
        var response = await _http.PatchAsync(url, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, responseBody, ParseRetryAfter(response));
        return responseBody;
    }

    private async Task<string> PostJsonAsync<TBody>(string url, TBody body, JsonSerializerContext ctx, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, WriteTypeInfo<TBody>(ctx));
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await _throttle.AcquireAsync(ct).ConfigureAwait(false);
        var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new PlaneApiException((int)response.StatusCode, responseBody, ParseRetryAfter(response));
        return responseBody;
    }

    // Resolves the source-generated JsonTypeInfo for a write body, with the relaxed encoder
    // applied. Plane stores HTML in description_html / comments; the default encoder would
    // \uXXXX-escape <, >, & on the wire. Using the resolved JsonTypeInfo lets the write path
    // call the AOT-safe Serialize(value, JsonTypeInfo) overload instead of the reflection-
    // capable Serialize(value, Type, options) overload that trips IL2026/IL3050 under AOT.
    private static JsonTypeInfo WriteTypeInfo<TBody>(JsonSerializerContext ctx)
    {
        var options = new JsonSerializerOptions(ctx.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return options.GetTypeInfo(typeof(TBody));
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
            _statesByName = (list.Results ?? []).ToDictionary(s => s.Name, s => s.Id, StringComparer.OrdinalIgnoreCase);
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
            _labelsByName = (list.Results ?? []).ToDictionary(l => l.Name, l => l.Id, StringComparer.OrdinalIgnoreCase);
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
            _issueTypesByName = (list.Results ?? []).ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
            return _issueTypesByName;
        }
        finally
        {
            _issueTypeLock.Release();
        }
    }

    // State-name -> TicketState, derived from the canonical WorkspaceSchema so the runtime
    // translation and the `build setup` provisioner can never disagree about the state set.
    private static readonly Dictionary<string, TicketState> _stateNameMap =
        WorkspaceSchema.States.ToDictionary(s => s.Name, s => s.State, StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ provisioning (build setup)

    // Default Plane colors for created entries; cosmetic only - `build` matches by name.
    private const string DefaultStateColor = "#60646C";
    private const string DefaultLabelColor = "#9CA3AF";

    public async Task<IReadOnlyList<ExistingState>> ListStatesAsync(CancellationToken ct)
    {
        var list = await GetJsonAsync<PlaneStateList>(StatesBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        return (list.Results ?? [])
            .Select(s => new ExistingState(s.Name, s.Group, s.Sequence ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListLabelNamesAsync(CancellationToken ct)
    {
        var list = await GetJsonAsync<PlaneLabelList>(LabelsBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        return (list.Results ?? []).Select(l => l.Name).ToList();
    }

    public async Task CreateStateAsync(string name, string group, double sequence, CancellationToken ct)
    {
        var body = new CreateStateRequest(name, group, DefaultStateColor, sequence);
        await PostJsonAsync(StatesBase, body, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        _statesByName = null; // force a re-read so a later lookup sees the new state
    }

    public async Task CreateLabelAsync(string name, CancellationToken ct)
    {
        var body = new CreateLabelRequest(name, DefaultLabelColor);
        await PostJsonAsync(LabelsBase, body, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        _labelsByName = null; // force a re-read so a later lookup sees the new label
    }

    // ------------------------------------------------------------------ project discovery (IProjectDiscovery)

    // Workspace-level projects URL: routes on slug only, works with an empty ProjectId.
    private string ProjectsBase =>
        $"api/v1/workspaces/{_options.WorkspaceSlug}/projects/";

    public async Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct)
    {
        var list = await GetJsonAsync<PlaneProjectList>(ProjectsBase, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        return (list.Results ?? [])
            .Select(p => new ProjectInfo(p.Id, p.Name, p.Identifier))
            .ToList();
    }

    public async Task<string?> FindProjectByNameAsync(string name, CancellationToken ct)
    {
        var projects = await ListProjectsAsync(ct).ConfigureAwait(false);
        return projects
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .FirstOrDefault();
    }

    public async Task<string> CreateProjectAsync(string name, string identifier, CancellationToken ct)
    {
        var body = new CreateProjectRequest(name, identifier, Network: 0);
        string responseBody;
        try
        {
            responseBody = await PostJsonAsync(ProjectsBase, body, PlaneJsonContext.Default, ct).ConfigureAwait(false);
        }
        catch (PlaneApiException ex) when (ex.Status is 401 or 403)
        {
            throw new PlaneApiException(
                ex.Status,
                $"workspace-admin scope required to create a project in '{_options.WorkspaceSlug}': {ex.Body}",
                ex.RetryAfter);
        }

        var response = (PlaneCreateProjectResponse?)JsonSerializer.Deserialize(
            responseBody, typeof(PlaneCreateProjectResponse), PlaneJsonContext.Default)
            ?? throw new InvalidOperationException("Plane returned null after project create");

        return response.Id;
    }

    // ------------------------------------------------------------------ translation

    private async Task<PlaneIssue> FindIssueAsync(int seq, CancellationToken ct)
    {
        await EnsureSnapshotAsync(ct).ConfigureAwait(false);
        if (_seqToUuid.TryGetValue(seq, out var uuid) && _issueByUuid.TryGetValue(uuid, out var cached))
            return cached;
        throw new KeyNotFoundException($"Issue with sequence_id {seq} not found in Plane");
    }

    /// <summary>
    /// Paginates the entire project exactly once per client and indexes every issue by both
    /// sequence id and uuid. Single-flight: concurrent callers (parallel dispatch shares one
    /// client) trigger one load, not N. The previous design fetched only page one and cached a
    /// single matched issue per call, so each distinct seq re-fetched the list and any issue
    /// past the first 100 was unreachable; loading the full project once fixes both.
    /// </summary>
    private async Task EnsureSnapshotAsync(CancellationToken ct)
    {
        if (_snapshotLoaded) return;
        await _snapshotLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshotLoaded) return;
            var all = await FetchAllIssuesAsync($"{IssuesBase}?per_page=100", ct).ConfigureAwait(false);
            foreach (var issue in all)
                IndexIssue(issue);
            _snapshotLoaded = true;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Write-through cache update applied after a successful mutation so a later read sees the
    /// new state/labels/description/parent without another GET. No-op if the issue isn't in the
    /// snapshot (we never fabricate cache entries here). The update runs inside an atomic
    /// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey,Func{TKey,TValue},Func{TKey,TValue,TValue})"/>
    /// so it composes against the live value - concurrent write-throughs to the same issue (or to
    /// different fields of it) cannot lose each other's change. The closure is pure and may run
    /// more than once under contention, which is fine. The seq index is identity-only and never
    /// touched here, so the two indexes cannot drift.
    /// </summary>
    /// <summary>
    /// Inserts or replaces an issue in both indexes. Used by the snapshot load and by the
    /// creators so a ticket made on this client is visible to a later lookup or parent-probe in
    /// the same process (otherwise the snapshot, captured before the create, would never see it).
    /// </summary>
    private void IndexIssue(PlaneIssue issue)
    {
        _seqToUuid[issue.SequenceId] = issue.Id;
        _issueByUuid[issue.Id] = issue;
    }

    /// <summary>
    /// Reads the <c>description_html</c> back from a PATCH response so the cache holds the value
    /// Plane actually stored (it may normalize HTML) rather than the optimistic value we sent -
    /// important because descriptions are appended to, so divergence would compound. Returns null
    /// if the body isn't a parseable issue; the caller falls back to the sent value.
    /// </summary>
    private static string? TryReadDescription(string patchResponseBody)
    {
        try
        {
            var issue = (PlaneIssue?)JsonSerializer.Deserialize(
                patchResponseBody, typeof(PlaneIssue), PlaneJsonContext.Default);
            return issue?.DescriptionHtml;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateCachedIssue(string uuid, Func<PlaneIssue, PlaneIssue> update)
    {
        // Cache keys are never removed, so a present key stays present: the add factory is
        // unreachable and the update factory is what actually runs (atomically).
        if (!_issueByUuid.ContainsKey(uuid))
            return;
        _issueByUuid.AddOrUpdate(
            uuid,
            _ => throw new InvalidOperationException("unreachable: snapshot key presence checked"),
            (_, existing) => update(existing));
    }

    private async Task<Ticket> ToTicketAsync(PlaneIssue issue, CancellationToken ct)
    {
        var statesTask = GetStatesByNameAsync(ct);
        var labelsTask = GetLabelsByNameAsync(ct);

        var states = await statesTask.ConfigureAwait(false);
        var statesById = states.ToDictionary(kvp => kvp.Value, kvp => new PlaneState(kvp.Value, kvp.Key, string.Empty));
        var stateName = statesById.TryGetValue(issue.StateId, out var st) ? st.Name : string.Empty;
        var ticketState = _stateNameMap.TryGetValue(stateName, out var ts) ? ts : TicketState.Backlog;

        var labelsByName = await labelsTask.ConfigureAwait(false);
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
            Id: $"{_options.ProjectIdentifier}-{issue.SequenceId}",
            Uuid: issue.Id,
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
        var transitioned = false;
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

            // Write-through: keep the snapshot's state fresh so the next phase's state guard
            // reads the state this transition just wrote, not the value cached at first load.
            UpdateCachedIssue(issue.Id, i => i with { StateId = stateId });
            transitioned = true;
        }, ct).ConfigureAwait(false);

        if (transitioned && newState == TicketState.Done)
            await RollupParentAsync(id, ct).ConfigureAwait(false);
    }

    public async Task AppendDescriptionAsync(string id, string html, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            var existing = issue.DescriptionHtml ?? string.Empty;
            var combined = existing + html;

            var responseBody = await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new AppendDescriptionRequest(combined),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            // Cache what Plane stored (it may normalize the HTML) so a later append builds on the
            // canonical value, not our optimistic concat. Composed onto the live issue so a
            // concurrent state/label write is not lost; falls back to the value we sent.
            var stored = TryReadDescription(responseBody) ?? combined;
            UpdateCachedIssue(issue.Id, i => i with { DescriptionHtml = stored });
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

            UpdateCachedIssue(issue.Id, i => i with { LabelIds = labelIds });
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Relation>> GetRelationsAsync(string id, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            if (_relationsByIssueUuid.TryGetValue(issue.Id, out var cached))
                return cached;

            var relationList = await GetJsonAsync<PlaneRelationList>(
                $"{IssuesBase}{issue.Id}/relations/", PlaneJsonContext.Default, token).ConfigureAwait(false);

            var relations = (relationList.Results ?? [])
                .Select(r => new Relation(r.RelationType, r.RelatedIssue))
                .ToList()
                .AsReadOnly();
            return _relationsByIssueUuid.GetOrAdd(issue.Id, relations);
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

            // GET siblings (expand=state), filtering by parent ourselves: Plane ignores
            // the server-side `parent=` param, so the unfiltered list would let unrelated
            // tickets drive the parent's rollup state. See FetchAllIssuesAsync.
            var allExpanded = await FetchAllExpandedAsync(
                $"{IssuesBase}?per_page=100&parent={parentId}&expand=state", ct).ConfigureAwait(false);
            var siblings = allExpanded
                .Where(s => string.Equals(s.ParentId, parentId, StringComparison.Ordinal))
                .ToList();

            // Apply ranked rules to determine desired state
            var desired = ApplyRollupRules(siblings);
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

            UpdateCachedIssue(parentId, i => i with { StateId = desiredStateId });

            // POST comment: [rollup] marker is load-bearing
            var commentHtml = $"<p>[rollup] {_options.ProjectIdentifier}-{childIssue.SequenceId} -> {childStateName}; parent -> {desired}</p>";
            await PostJsonAsync(
                $"{IssuesBase}{parentId}/comments/",
                new CreateCommentRequest(commentHtml),
                PlaneJsonContext.Default,
                ct).ConfigureAwait(false);

            if (desired == "Done")
                await RollupParentAsync($"{_options.ProjectIdentifier}-{parentExpanded.SequenceId}", ct).ConfigureAwait(false);

            return new RollupResult(true, desired, null);
        }
        catch (Exception ex)
        {
            return new RollupResult(false, null, ex.Message);
        }
    }

    public async Task<NewTicketResult> CreateTicketAsync(
        string title,
        string? type,
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

            // Seed the snapshot so a later lookup/parent-probe on this client sees the new ticket.
            // The create response carries no state; a new issue lands in the default (Backlog), and
            // an empty StateId resolves to Backlog in ToTicketAsync, so leaving it empty is correct.
            IndexIssue(new PlaneIssue(
                Id: response.Id,
                SequenceId: response.SequenceId,
                Name: title,
                DescriptionHtml: descriptionHtml,
                StateId: string.Empty,
                LabelIds: labelIds,
                ParentId: null,
                Type: typeId));

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

            UpdateCachedIssue(childUuid, i => i with { ParentId = parentUuid });
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Ticket>> QueryAsync(TicketQuery query, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            // Plane's list endpoint silently ignores its query filters (notably `parent=`) and
            // returns the whole project, so historically every QueryAsync re-paginated the entire
            // project and filtered client-side - once per phase, per ticket. That re-fetch is what
            // made `build chain` saturate the rate limiter as the project grew. The project's issue
            // set is stable within a run, so we load it once (EnsureSnapshotAsync) and filter the
            // cached snapshot in memory; write-through keeps state/parent fields current. The
            // client-side parent filter is still load-bearing: without it a leaf looks like a parent.
            await EnsureSnapshotAsync(token).ConfigureAwait(false);

            IEnumerable<PlaneIssue> issues = _issueByUuid.Values;

            if (query.State.HasValue)
            {
                var stateName = query.State.Value switch
                {
                    TicketState.Backlog     => "Backlog",
                    TicketState.Planning    => "Planning",
                    TicketState.Ready       => "Ready",
                    TicketState.InProgress  => "In Progress",
                    TicketState.InReview    => "In Review",
                    TicketState.Done        => "Done",
                    TicketState.Cancelled   => "Cancelled",
                    _ => throw new ArgumentOutOfRangeException(nameof(query))
                };
                var statesByName = await GetStatesByNameAsync(token).ConfigureAwait(false);
                issues = statesByName.TryGetValue(stateName, out var stateUuid)
                    ? issues.Where(i => string.Equals(i.StateId, stateUuid, StringComparison.Ordinal))
                    : Enumerable.Empty<PlaneIssue>();
            }

            if (!string.IsNullOrEmpty(query.ParentId))
                issues = issues.Where(i => string.Equals(i.ParentId, query.ParentId, StringComparison.Ordinal));

            if (!string.IsNullOrEmpty(query.Type))
            {
                // query.Type is a human issue-type name (e.g. "Bug"); the cached issue carries the
                // issue-type UUID. Resolve name -> UUID the same way CreateTicketAsync does so the
                // comparison is UUID-vs-UUID. (Requires the issue-types feature; without it this
                // throws, matching create-with-type. An unknown type name matches nothing.)
                var typesByName = await GetIssueTypesByNameAsync(token).ConfigureAwait(false);
                issues = typesByName.TryGetValue(query.Type, out var typeUuid)
                    ? issues.Where(i => string.Equals(i.Type, typeUuid, StringComparison.Ordinal))
                    : Enumerable.Empty<PlaneIssue>();
            }

            var tickets = new List<Ticket>();
            foreach (var issue in issues)
                tickets.Add(await ToTicketAsync(issue, token).ConfigureAwait(false));

            return (IReadOnlyList<Ticket>)tickets;
        }, ct).ConfigureAwait(false);
    }

    // Maximum issue-list pages to walk before giving up (guards against a server that
    // keeps handing back a non-empty cursor). 50 pages * 100 per page = 5000 issues.
    private const int MaxListPages = 50;

    /// <summary>
    /// Walks every cursor page for an issues-list URL and returns the flattened results.
    /// <paramref name="baseUrl"/> must already carry its leading query string (it is
    /// extended with <c>&amp;cursor=</c>). Stops when Plane reports no further page
    /// (<c>next_page_results=false</c>), on an empty/repeated cursor or empty page, or at the cap.
    /// </summary>
    private async Task<List<PlaneIssue>> FetchAllIssuesAsync(string baseUrl, CancellationToken ct)
    {
        var all = new List<PlaneIssue>();
        string? cursor = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var url = string.IsNullOrEmpty(cursor)
                ? baseUrl
                : $"{baseUrl}&cursor={Uri.EscapeDataString(cursor)}";
            var list = await GetJsonAsync<PlaneIssueList>(url, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            var batch = list.Results ?? [];
            all.AddRange(batch);

            // Plane echoes a non-empty, advancing next_cursor even past the last page, so the
            // cursor alone never ends the walk - next_page_results is the authoritative signal.
            // Also stop on an empty page or a missing/repeated cursor as defensive fallbacks.
            if (list.NextPageResults == false
                || batch.Count == 0
                || string.IsNullOrEmpty(list.NextCursor)
                || string.Equals(list.NextCursor, cursor, StringComparison.Ordinal))
                return all;
            cursor = list.NextCursor;
        }

        // Fell out via the page cap while the server still had more pages. Since the snapshot
        // is now the single source of truth for every lookup, a truncated load silently makes
        // issues beyond the cap unresolvable for the whole run - warn loudly rather than fail quietly.
        Console.Error.WriteLine(
            $"[plane] WARNING: issue-list pagination hit the {MaxListPages}-page cap ({MaxListPages * 100} issues) " +
            "with more pages available - the project snapshot is TRUNCATED for this run and lookups for issues " +
            "beyond the cap will throw 'not found'. Raise MaxListPages or narrow the project.");
        return all;
    }

    /// <summary>Cursor-paginated variant of <see cref="FetchAllIssuesAsync"/> for the expand=state shape.</summary>
    private async Task<List<PlaneIssueExpanded>> FetchAllExpandedAsync(string baseUrl, CancellationToken ct)
    {
        var all = new List<PlaneIssueExpanded>();
        string? cursor = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var url = string.IsNullOrEmpty(cursor)
                ? baseUrl
                : $"{baseUrl}&cursor={Uri.EscapeDataString(cursor)}";
            var list = await GetJsonAsync<PlaneIssueExpandedList>(url, PlaneJsonContext.Default, ct).ConfigureAwait(false);
            var batch = list.Results ?? [];
            all.AddRange(batch);

            // Same as FetchAllIssuesAsync: next_page_results is the authoritative end signal.
            if (list.NextPageResults == false
                || batch.Count == 0
                || string.IsNullOrEmpty(list.NextCursor)
                || string.Equals(list.NextCursor, cursor, StringComparison.Ordinal))
                break;
            cursor = list.NextCursor;
        }
        return all;
    }

    public async Task TransitionLifecycleAsync(string id, LifecycleTransition transition, string? reason, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var (targetState, commentMarker) = transition switch
            {
                LifecycleTransition.Close  => (TicketState.Cancelled, "<strong>wontfix:</strong>"),
                LifecycleTransition.Defer  => (TicketState.Cancelled, "<strong>deferred:</strong>"),
                LifecycleTransition.Reopen => (TicketState.Backlog,   "<strong>reopened:</strong>"),
                _ => throw new ArgumentOutOfRangeException(nameof(transition))
            };

            var commentHtml = string.IsNullOrEmpty(reason)
                ? $"<p>{commentMarker}</p>"
                : $"<p>{commentMarker} {reason}</p>";

            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            // Post comment
            await PostJsonAsync(
                $"{IssuesBase}{issue.Id}/comments/",
                new CreateCommentRequest(commentHtml),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            // Resolve state UUID and patch
            var stateName = targetState switch
            {
                TicketState.Backlog     => "Backlog",
                TicketState.Planning    => "Planning",
                TicketState.Ready       => "Ready",
                TicketState.InProgress  => "In Progress",
                TicketState.InReview    => "In Review",
                TicketState.Done        => "Done",
                TicketState.Cancelled   => "Cancelled",
                _ => throw new ArgumentOutOfRangeException(nameof(targetState))
            };
            var statesByName = await GetStatesByNameAsync(token).ConfigureAwait(false);
            if (!statesByName.TryGetValue(stateName, out var stateId))
            {
                Console.Error.WriteLine(
                    $"Warning: Plane project has no '{stateName}' state; leaving {id} in its current state.");
                return;
            }
            await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new TransitionRequest(stateId),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            UpdateCachedIssue(issue.Id, i => i with { StateId = stateId });
        }, ct).ConfigureAwait(false);
    }

    public async Task UpdateDescriptionAsync(string id, string html, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var seq = ParseSequenceId(id);
            var issue = await FindIssueAsync(seq, token).ConfigureAwait(false);

            var responseBody = await PatchJsonAsync(
                $"{IssuesBase}{issue.Id}/",
                new UpdateDescriptionRequest(html),
                PlaneJsonContext.Default,
                token).ConfigureAwait(false);

            // Cache the server-stored description (Plane may normalize HTML); fall back to ours.
            var stored = TryReadDescription(responseBody) ?? html;
            UpdateCachedIssue(issue.Id, i => i with { DescriptionHtml = stored });
        }, ct).ConfigureAwait(false);
    }

    public async Task<CreateChildTicketsResult> CreateChildTicketsAsync(
        string parentUuid,
        IReadOnlyList<ChildTicketSpec> children,
        CancellationToken ct)
    {
        var created = new List<CreatedChild>();
        var failures = new List<string>();

        // Resolve label cache once up front; catch failures per-child below
        Dictionary<string, string>? labelsByName = null;
        try
        {
            labelsByName = await GetLabelsByNameAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // If we cannot load labels at all, record a failure for every child and return
            foreach (var child in children)
                failures.Add($"{child.Title}: failed to load label cache: {ex.Message}");
            return new CreateChildTicketsResult(created.AsReadOnly(), failures.AsReadOnly());
        }

        foreach (var child in children)
        {
            try
            {
                var labelIds = new List<string>();
                foreach (var labelName in child.LabelNames)
                {
                    if (labelsByName.TryGetValue(labelName, out var labelId))
                        labelIds.Add(labelId);
                    // Unknown label names are silently skipped for child creation
                }

                var request = new CreateIssueRequest(
                    Name: child.Title,
                    DescriptionHtml: child.DescriptionHtml,
                    Type: null,
                    LabelIds: labelIds)
                {
                    ParentId = parentUuid
                };

                var responseBody = await PostJsonAsync(
                    IssuesBase,
                    request,
                    PlaneJsonContext.Default,
                    ct).ConfigureAwait(false);

                var response = (PlaneCreateIssueResponse?)JsonSerializer.Deserialize(
                    responseBody, typeof(PlaneCreateIssueResponse), PlaneJsonContext.Default);

                if (response is null)
                {
                    failures.Add($"{child.Title}: deserialized null response");
                    continue;
                }

                // Seed the snapshot with the new child (parented to parentUuid) so a subsequent
                // parent-probe on this client sees it. Empty StateId -> Backlog in ToTicketAsync.
                IndexIssue(new PlaneIssue(
                    Id: response.Id,
                    SequenceId: response.SequenceId,
                    Name: child.Title,
                    DescriptionHtml: child.DescriptionHtml,
                    StateId: string.Empty,
                    LabelIds: labelIds,
                    ParentId: parentUuid,
                    Type: null));

                created.Add(new CreatedChild(
                    Id: $"{_options.ProjectIdentifier}-{response.SequenceId}",
                    Uuid: response.Id));
            }
            catch (Exception ex)
            {
                failures.Add($"{child.Title}: {ex.Message}");
            }
        }

        return new CreateChildTicketsResult(created.AsReadOnly(), failures.AsReadOnly());
    }

    public async Task AddRelationAsync(string blockedId, string blockerId, CancellationToken ct)
    {
        await _pipeline.ExecuteAsync(async token =>
        {
            var blockedSeq = ParseSequenceId(blockedId);
            var blockedIssue = await FindIssueAsync(blockedSeq, token).ConfigureAwait(false);

            var blockerSeq = ParseSequenceId(blockerId);
            var blockerIssue = await FindIssueAsync(blockerSeq, token).ConfigureAwait(false);

            await PostJsonAsync(
                $"{IssuesBase}{blockedIssue.Id}/relations/",
                new CreateRelationRequest("blocked_by", new List<string> { blockerIssue.Id }),
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
        // Rule 1: every direct child is Done -> "Done". Cancelled children still block
        // completion because they are not Done.
        if (stateNames.All(n => n == "Done"))
            return "Done";

        // Rule 2: all non-Cancelled are in (In Review, Done) -> "In Review"
        var nonCancelled = stateNames.Where(n => n != "Cancelled").ToList();
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
