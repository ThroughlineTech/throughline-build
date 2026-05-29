// Analyze throughline-build event log JSONL files.
//
// Run on .NET 10+ (file-based programs):
//     dotnet run analyze_event_log.cs <event-log.jsonl> [more files...]
//
// Or compile a standalone exe:
//     dotnet publish analyze_event_log.cs -c Release
//
// Reports per-phase LLM token usage, estimated cost, and timing.
// Update the pricing table below when Anthropic prices change.

using System.Text.Json;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: dotnet run analyze_event_log.cs <event-log.jsonl> [more files...]");
    return 1;
}

// USD per million tokens: (prefix, input, output, cache_read, cache_create).
// cache_create assumes 5-minute cache (1.25x input). 1-hour cache is 2x.
var pricing = new (string Prefix, decimal In, decimal Out, decimal CR, decimal CC)[]
{
    ("claude-opus-4",   15.00m, 75.00m, 1.50m, 18.75m),
    ("claude-sonnet-4",  3.00m, 15.00m, 0.30m,  3.75m),
    ("claude-haiku-4",   1.00m,  5.00m, 0.10m,  1.25m),
};

var phaseNames = new Dictionary<int, string>
{
    [0] = "Plan",
    [1] = "Implement",
    [2] = "Review",
    [3] = "Ship",
    [4] = "Chain",
    [5] = "New",
};

int fileCount = 0;
var grand = new Bucket();

foreach (var path in args)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"error: file not found: {path}");
        continue;
    }
    fileCount++;
    var totals = AnalyzeAndReport(path, pricing, phaseNames);
    grand.Add(totals);
}

if (fileCount > 1)
{
    Console.WriteLine($"\n=== GRAND TOTAL across {fileCount} files ===");
    Console.WriteLine($"  events:              {grand.Events,14:N0}");
    Console.WriteLine($"  LLM calls:           {grand.LlmCalls,14:N0}");
    Console.WriteLine($"  input_tokens:        {grand.InputTokens,14:N0}");
    Console.WriteLine($"  output_tokens:       {grand.OutputTokens,14:N0}");
    Console.WriteLine($"  cache_read_tokens:   {grand.CacheReadTokens,14:N0}");
    Console.WriteLine($"  cache_create_tokens: {grand.CacheCreateTokens,14:N0}");
    Console.WriteLine($"  wall_clock (s):      {grand.WallClockMs / 1000.0,14:F1}");
    var costStr = $"${grand.CostUsd:F4}";
    if (grand.UnknownModelCost) costStr += "*";
    Console.WriteLine($"  estimated cost:      {costStr,14}");
    if (grand.UnknownModelCost)
        Console.WriteLine("\n* cost is partial; update pricing for unrecognized models");
}

return 0;


// ---------------------------------------------------------------------------
// Local functions and types follow.
// ---------------------------------------------------------------------------

static Bucket AnalyzeAndReport(
    string path,
    (string Prefix, decimal In, decimal Out, decimal CR, decimal CC)[] pricing,
    Dictionary<int, string> phaseNames)
{
    var byPhase = new SortedDictionary<int, Bucket>();
    string? projectId = null, workspaceSlug = null, buildVersion = null, ticketId = null;
    string? firstTs = null, lastTs = null;
    JsonElement chainEndData = default;
    bool sawChainEnd = false;
    var subsumedEvents = new List<(string ticketId, string commit)>();

    foreach (var raw in File.ReadLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0) continue;

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        int kind = root.GetProperty("Kind").GetInt32();
        int phase = root.GetProperty("Phase").GetInt32();
        ticketId ??= root.GetProperty("TicketId").GetString();
        var ts = root.GetProperty("Timestamp").GetString();
        firstTs ??= ts;
        lastTs = ts;

        if (TryGetString(root, "project_id",     out var pid)) projectId     ??= pid;
        if (TryGetString(root, "workspace_slug", out var ws))  workspaceSlug ??= ws;
        if (TryGetString(root, "build_version",  out var bv))  buildVersion  ??= bv;

        if (kind == 7) // ChainEnd
        {
            // Clone because the JsonDocument is disposed at end of scope.
            chainEndData = root.GetProperty("Data").Clone();
            sawChainEnd = true;
        }
        else if (kind == 9) // TicketSubsumed
        {
            var data = root.GetProperty("Data");
            TryGetString(data, "ticket_id", out var subTicketId);
            TryGetString(data, "subsumed_by_commit", out var subCommit);
            subsumedEvents.Add((subTicketId ?? ticketId ?? "n/a", subCommit ?? ""));
        }

        if (!byPhase.TryGetValue(phase, out var bucket))
        {
            bucket = new Bucket();
            byPhase[phase] = bucket;
        }
        bucket.Events++;

        if (kind == 1) // LlmCall
        {
            var data = root.GetProperty("Data");
            bucket.LlmCalls++;
            bucket.InputTokens       += GetLong(data, "input_tokens");
            bucket.OutputTokens      += GetLong(data, "output_tokens");
            bucket.CacheReadTokens   += GetLong(data, "cache_read_tokens");
            bucket.CacheCreateTokens += GetLong(data, "cache_create_tokens");
            bucket.WallClockMs       += GetLong(data, "wall_clock_ms");
            var model = TryGetString(data, "model", out var m) ? m! : "unknown";
            bucket.Models.Add(model);
            var cost = ComputeCost(data, model, pricing);
            if (cost is null) bucket.UnknownModelCost = true;
            else bucket.CostUsd += cost.Value;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== {path} ===");
    Console.WriteLine($"Project:        {projectId ?? "n/a"}");
    Console.WriteLine($"Workspace:      {workspaceSlug ?? "n/a"}");
    Console.WriteLine($"Build version:  {buildVersion ?? "n/a"}");
    Console.WriteLine($"Ticket:         {ticketId ?? "n/a"}");
    Console.WriteLine($"First event:    {firstTs ?? "n/a"}");
    Console.WriteLine($"Last event:     {lastTs ?? "n/a"}");

    if (sawChainEnd)
    {
        var outcome = TryGetString(chainEndData, "outcome", out var o) ? o : "n/a";
        var phasesRun = TryGetLong(chainEndData, "phases_run", out var pr) ? pr.ToString() : "n/a";
        var rework = TryGetLong(chainEndData, "rework_rounds", out var rr) ? rr.ToString() : "n/a";
        var totalMs = TryGetLong(chainEndData, "total_duration_ms", out var td) ? td : 0;
        Console.WriteLine($"Chain outcome:  {outcome}");
        Console.WriteLine($"Phases run:     {phasesRun}");
        Console.WriteLine($"Rework rounds:  {rework}");
        Console.WriteLine($"Chain duration: {totalMs / 1000.0:F1}s ({totalMs / 60000.0:F2}m)");
        if (subsumedEvents.Count > 0)
        {
            Console.WriteLine($"Subsumed:       {subsumedEvents.Count} ticket(s) auto-resolved");
            foreach (var (tid, commit) in subsumedEvents)
                Console.WriteLine($"  {tid}  subsumed_by {commit}");
        }
    }

    Console.WriteLine();
    var header = string.Format(
        "{0,-10} {1,5} {2,4} {3,8} {4,12} {5,12} {6,12} {7,12} {8,11}  {9}",
        "Phase", "evts", "LLM", "wall(s)", "input", "output", "cache_r", "cache_c", "cost", "model");
    Console.WriteLine(header);
    Console.WriteLine(new string('-', header.Length));

    var totals = new Bucket();
    foreach (var (phase, b) in byPhase)
    {
        var name = phaseNames.TryGetValue(phase, out var n) ? n : $"Phase{phase}";
        var models = b.Models.Count > 0
            ? string.Join(", ", b.Models.OrderBy(s => s, StringComparer.Ordinal))
            : "-";
        var costStr = $"${b.CostUsd:F4}";
        if (b.UnknownModelCost) costStr += "*";

        Console.WriteLine(string.Format(
            "{0,-10} {1,5} {2,4} {3,8:F1} {4,12:N0} {5,12:N0} {6,12:N0} {7,12:N0} {8,11}  {9}",
            name, b.Events, b.LlmCalls, b.WallClockMs / 1000.0,
            b.InputTokens, b.OutputTokens, b.CacheReadTokens, b.CacheCreateTokens, costStr, models));

        totals.Add(b);
    }

    Console.WriteLine(new string('-', header.Length));
    var totalCostStr = $"${totals.CostUsd:F4}";
    if (totals.UnknownModelCost) totalCostStr += "*";
    Console.WriteLine(string.Format(
        "{0,-10} {1,5} {2,4} {3,8:F1} {4,12:N0} {5,12:N0} {6,12:N0} {7,12:N0} {8,11}",
        "TOTAL", totals.Events, totals.LlmCalls, totals.WallClockMs / 1000.0,
        totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheCreateTokens, totalCostStr));

    if (totals.UnknownModelCost)
        Console.WriteLine("\n* cost is partial; one or more LlmCalls used a model not in the pricing table");

    return totals;
}

static decimal? ComputeCost(
    JsonElement data, string model,
    (string Prefix, decimal In, decimal Out, decimal CR, decimal CC)[] pricing)
{
    foreach (var p in pricing)
    {
        if (model.StartsWith(p.Prefix, StringComparison.Ordinal))
        {
            return GetLong(data, "input_tokens")        * p.In / 1_000_000m
                 + GetLong(data, "output_tokens")       * p.Out / 1_000_000m
                 + GetLong(data, "cache_read_tokens")   * p.CR / 1_000_000m
                 + GetLong(data, "cache_create_tokens") * p.CC / 1_000_000m;
        }
    }
    return null;
}

static long GetLong(JsonElement el, string name)
    => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt64()
        : 0;

static bool TryGetLong(JsonElement el, string name, out long val)
{
    if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
    {
        val = v.GetInt64();
        return true;
    }
    val = 0;
    return false;
}

static bool TryGetString(JsonElement el, string name, out string? val)
{
    if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
    {
        val = v.GetString();
        return true;
    }
    val = null;
    return false;
}

sealed class Bucket
{
    public int Events;
    public int LlmCalls;
    public long InputTokens;
    public long OutputTokens;
    public long CacheReadTokens;
    public long CacheCreateTokens;
    public long WallClockMs;
    public decimal CostUsd;
    public bool UnknownModelCost;
    public HashSet<string> Models = new();

    public void Add(Bucket other)
    {
        Events            += other.Events;
        LlmCalls          += other.LlmCalls;
        InputTokens       += other.InputTokens;
        OutputTokens      += other.OutputTokens;
        CacheReadTokens   += other.CacheReadTokens;
        CacheCreateTokens += other.CacheCreateTokens;
        WallClockMs       += other.WallClockMs;
        CostUsd           += other.CostUsd;
        if (other.UnknownModelCost) UnknownModelCost = true;
        foreach (var m in other.Models) Models.Add(m);
    }
}