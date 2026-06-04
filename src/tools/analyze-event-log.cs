// Analyze throughline-build event log JSONL files.
//
// Run on .NET 10+ (file-based programs):
//     dotnet run analyze_event_log.cs <event-log.jsonl> [more files...]
//     dotnet run analyze_event_log.cs <directory>
//     dotnet run analyze_event_log.cs <glob> [more globs/files/dirs...]
//
// Or compile a standalone exe:
//     dotnet publish analyze_event_log.cs -c Release
//
// Reports per-phase LLM token usage, estimated cost, and timing.
// Event-supplied cost_usd is authoritative; the pricing table is a fallback.

using System.Text.Json;

if (args.Length == 0 || (args.Length > 0 && (args[0] == "--help" || args[0] == "-h")))
{
    Console.WriteLine("Usage: analyze_event_log <event-log.jsonl | glob | dir> [more...]");
    Console.WriteLine();
    Console.WriteLine("  <file>        analyze a single event log JSONL file");
    Console.WriteLine("  <dir>         analyze all *.jsonl files in a directory");
    Console.WriteLine("  <glob>        e.g. 'TLB-3*' or '*' -- works even when the shell");
    Console.WriteLine("                does not expand globs (cmd / PowerShell)");
    Console.WriteLine("  [more...]     any combination of the above; output sorted oldest-first");
    Console.WriteLine();
    Console.WriteLine("  --help, -h    show this message");
    return args.Length == 0 ? 1 : 0;
}

// USD per million tokens. Anthropic cache_read/cache_create are separate from
// input_tokens in the Claude stream. OpenAI cached_input_tokens are a subset of
// input_tokens, so the estimator subtracts them before applying the full input rate.
var pricing = new Pricing[]
{
    new("claude-opus-4",    15.00m, 75.00m, 1.50m, 18.75m, CachedTokensIncludedInInput: false),
    new("claude-sonnet-4",   3.00m, 15.00m, 0.30m,  3.75m, CachedTokensIncludedInInput: false),
    new("claude-haiku-4",    1.00m,  5.00m, 0.10m,  1.25m, CachedTokensIncludedInInput: false),
    new("gpt-5.4-mini",      0.75m,  4.50m, 0.075m, 0.00m, CachedTokensIncludedInInput: true),
    new("gpt-5.4",           2.50m, 15.00m, 0.25m,  0.00m, CachedTokensIncludedInInput: true),
    new("gpt-5.5",           5.00m, 30.00m, 0.50m,  0.00m, CachedTokensIncludedInInput: true),
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

var files = ResolveFiles(args);
if (files.Count == 0)
{
    Console.Error.WriteLine("error: no matching .jsonl files");
    return 1;
}

int fileCount = 0;
var grand = new Bucket();

foreach (var path in files)
{
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
    Console.WriteLine($"  reasoning_tokens:    {grand.ReasoningOutputTokens,14:N0}");
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
    Pricing[] pricing,
    Dictionary<int, string> phaseNames)
{
    var byPhase = new SortedDictionary<int, Bucket>();
    string? projectId = null, workspaceSlug = null, buildVersion = null, ticketId = null;
    string? firstTs = null, lastTs = null;
    JsonElement chainEndData = default;
    bool sawChainEnd = false;
    var subsumedEvents = new List<(string ticketId, string commit)>();
    var reworkByTicket = new SortedDictionary<string, long>(StringComparer.Ordinal);

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
        else if (kind == 3) // VerifierVerdict
        {
            var data = root.GetProperty("Data");
            if (TryGetString(data, "kind", out var verdictKind)
                && string.Equals(verdictKind, "Rework", StringComparison.Ordinal))
            {
                var verdictTicketId = root.GetProperty("TicketId").GetString() ?? "n/a";
                reworkByTicket[verdictTicketId] = reworkByTicket.TryGetValue(verdictTicketId, out var count)
                    ? count + 1
                    : 1;
            }
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
            bucket.ReasoningOutputTokens += GetLong(data, "reasoning_output_tokens");
            bucket.WallClockMs       += GetLong(data, "wall_clock_ms");
            var model = TryGetString(data, "model", out var m) ? m! : "unknown";
            bucket.Models.Add(model);
            if (TryGetDecimal(data, "cost_usd", out var eventCost))
            {
                bucket.CostUsd += eventCost;
            }
            else
            {
                var cost = ComputeCost(data, model, pricing);
                if (cost is null) bucket.UnknownModelCost = true;
                else bucket.CostUsd += cost.Value;
            }
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
        var rework = reworkByTicket.Values.Sum();
        var totalMs = TryGetLong(chainEndData, "total_duration_ms", out var td) ? td : 0;
        Console.WriteLine($"Chain outcome:  {outcome}");
        Console.WriteLine($"Phases run:     {phasesRun}");
        Console.WriteLine($"Rework rounds:  {rework}");
        foreach (var (tid, count) in reworkByTicket)
            Console.WriteLine($"  {tid}: {count}");
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
        "{0,-10} {1,5} {2,4} {3,8} {4,12} {5,12} {6,12} {7,12} {8,12} {9,11}  {10}",
        "Phase", "evts", "LLM", "wall(s)", "input", "output", "cache_r", "cache_c", "reasoning", "cost", "model");
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
            "{0,-10} {1,5} {2,4} {3,8:F1} {4,12:N0} {5,12:N0} {6,12:N0} {7,12:N0} {8,12:N0} {9,11}  {10}",
            name, b.Events, b.LlmCalls, b.WallClockMs / 1000.0,
            b.InputTokens, b.OutputTokens, b.CacheReadTokens, b.CacheCreateTokens,
            b.ReasoningOutputTokens, costStr, models));

        totals.Add(b);
    }

    Console.WriteLine(new string('-', header.Length));
    var totalCostStr = $"${totals.CostUsd:F4}";
    if (totals.UnknownModelCost) totalCostStr += "*";
    Console.WriteLine(string.Format(
        "{0,-10} {1,5} {2,4} {3,8:F1} {4,12:N0} {5,12:N0} {6,12:N0} {7,12:N0} {8,12:N0} {9,11}",
        "TOTAL", totals.Events, totals.LlmCalls, totals.WallClockMs / 1000.0,
        totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheCreateTokens,
        totals.ReasoningOutputTokens, totalCostStr));

    if (totals.UnknownModelCost)
        Console.WriteLine("\n* cost is partial; one or more LlmCalls used a model not in the pricing table");

    return totals;
}

static List<string> ResolveFiles(string[] patterns)
{
    bool singleExplicitFile = patterns.Length == 1 && File.Exists(patterns[0]);
    var found = new List<string>();

    foreach (var pat in patterns)
    {
        if (File.Exists(pat))
        {
            found.Add(pat);
        }
        else if (Directory.Exists(pat))
        {
            found.AddRange(Directory.GetFiles(pat, "*.jsonl"));
        }
        else if (pat.Contains('*') || pat.Contains('?'))
        {
            var dir = Path.GetDirectoryName(pat);
            var name = Path.GetFileName(pat);
            if (string.IsNullOrEmpty(dir)) dir = Environment.CurrentDirectory;
            if (Directory.Exists(dir))
                found.AddRange(Directory.GetFiles(dir, name));
        }
        else
        {
            Console.Error.WriteLine($"warning: no match for '{pat}'");
        }
    }

    IEnumerable<string> filtered = singleExplicitFile
        ? found
        : found.Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));

    return filtered
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(File.GetLastWriteTimeUtc)
        .ToList();
}

static decimal? ComputeCost(
    JsonElement data, string model,
    Pricing[] pricing)
{
    foreach (var p in pricing)
    {
        if (model.StartsWith(p.Prefix, StringComparison.Ordinal))
        {
            var input = GetLong(data, "input_tokens");
            var cacheRead = GetLong(data, "cache_read_tokens");
            var billableInput = p.CachedTokensIncludedInInput
                ? Math.Max(0, input - cacheRead)
                : input;
            return billableInput * p.In / 1_000_000m
                 + GetLong(data, "output_tokens") * p.Out / 1_000_000m
                 + cacheRead * p.CR / 1_000_000m
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

static bool TryGetDecimal(JsonElement el, string name, out decimal val)
{
    if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
    {
        val = v.GetDecimal();
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

sealed record Pricing(
    string Prefix,
    decimal In,
    decimal Out,
    decimal CR,
    decimal CC,
    bool CachedTokensIncludedInInput);

sealed class Bucket
{
    public int Events;
    public int LlmCalls;
    public long InputTokens;
    public long OutputTokens;
    public long CacheReadTokens;
    public long CacheCreateTokens;
    public long ReasoningOutputTokens;
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
        ReasoningOutputTokens += other.ReasoningOutputTokens;
        WallClockMs       += other.WallClockMs;
        CostUsd           += other.CostUsd;
        if (other.UnknownModelCost) UnknownModelCost = true;
        foreach (var m in other.Models) Models.Add(m);
    }
}
