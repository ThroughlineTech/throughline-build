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
// The pricing table is authoritative for recognized models; event-supplied
// cost_usd is the fallback for unrecognized models only. Worker CLIs can carry
// stale per-model pricing (a new model billed at an old model's rates), so when
// both are available and disagree by more than 10% a warning shows both sums.

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
    // First prefix match wins: specific rows must precede the generic "claude-opus-4" row,
    // which catches only the older dated IDs (claude-opus-4-20250514, claude-opus-4-1-...)
    // still priced at $15/$75. Opus was repriced to $5/$25 from 4.5 onward.
    new("claude-fable-5",   10.00m, 50.00m, 1.00m, 12.50m, CachedTokensIncludedInInput: false),
    new("claude-opus-4-5",   5.00m, 25.00m, 0.50m,  6.25m, CachedTokensIncludedInInput: false),
    new("claude-opus-4-6",   5.00m, 25.00m, 0.50m,  6.25m, CachedTokensIncludedInInput: false),
    new("claude-opus-4-7",   5.00m, 25.00m, 0.50m,  6.25m, CachedTokensIncludedInInput: false),
    new("claude-opus-4-8",   5.00m, 25.00m, 0.50m,  6.25m, CachedTokensIncludedInInput: false),
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
    [6] = "Command",
    [7] = "Draft",
    [8] = "Scaffold",
    [9] = "Decompose",
    [10] = "Gate",
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
    if (grand.SkippedLines > 0)
        Console.WriteLine($"\n!! WARNING: skipped {grand.SkippedLines} malformed/truncated line(s) across all files; some totals are incomplete.");
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
    string? projectId = null, workspaceSlug = null, buildVersion = null;
    string? firstTs = null, lastTs = null;
    var tickets = new List<string>();
    var chainEnds = new List<(string TicketId, string Outcome, long PhasesRun, long TotalMs)>();
    var subsumedEvents = new List<(string ticketId, string commit)>();
    var reworkByTicket = new SortedDictionary<string, long>(StringComparer.Ordinal);

    int lineNo = 0;
    int skipped = 0;
    var skippedLines = new List<int>();

    foreach (var raw in File.ReadLines(path))
    {
        lineNo++;
        var line = raw.Trim();
        if (line.Length == 0) continue;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Truncated or invalid JSON -- common when a run dies mid-write.
            skipped++;
            skippedLines.Add(lineNo);
            continue;
        }

        try
        {
        var root = doc.RootElement;

        int kind = root.GetProperty("Kind").GetInt32();
        int phase = root.GetProperty("Phase").GetInt32();
        var ticketId = root.GetProperty("TicketId").GetString();
        if (ticketId is not null && !tickets.Contains(ticketId))
            tickets.Add(ticketId);
        var ts = root.GetProperty("Timestamp").GetString();
        firstTs ??= ts;
        lastTs = ts;

        if (TryGetString(root, "project_id",     out var pid)) projectId     ??= pid;
        if (TryGetString(root, "workspace_slug", out var ws))  workspaceSlug ??= ws;
        if (TryGetString(root, "build_version",  out var bv))  buildVersion  ??= bv;

        if (kind == 7) // ChainEnd
        {
            // A log file can hold many sequential chains (one per ticket in an
            // op run) -- collect every ChainEnd, not just the last one.
            var data = root.GetProperty("Data");
            var outcome = TryGetString(data, "outcome", out var o) ? o! : "n/a";
            TryGetLong(data, "phases_run", out var phasesRun);
            TryGetLong(data, "total_duration_ms", out var totalMs);
            chainEnds.Add((ticketId ?? "n/a", outcome, phasesRun, totalMs));
        }
        else if (kind == 9) // TicketSubsumed
        {
            var data = root.GetProperty("Data");
            TryGetString(data, "ticket_id", out var subTicketId);
            TryGetString(data, "subsumed_by_commit", out var subCommit);
            subsumedEvents.Add((subTicketId ?? ticketId ?? "n/a", subCommit ?? ""));
        }
        else if (kind == 8) // ReworkRound
        {
            // Emitted once per rework round that actually runs, whether triggered
            // by a verifier Rework verdict or a gate failure. Counting verifier
            // verdicts instead would miss gate-triggered rounds entirely and count
            // a Rework verdict at the round cap that never executes.
            var reworkTicketId = ticketId ?? "n/a";
            reworkByTicket[reworkTicketId] = reworkByTicket.TryGetValue(reworkTicketId, out var count)
                ? count + 1
                : 1;
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
            // Pricing table first: worker CLIs have shipped stale per-model
            // pricing (a new model billed at an old model's rates), so the
            // event-supplied cost_usd is only trusted for models the table
            // does not know. When both exist, track the event sum so the
            // report can flag a divergence.
            var tableCost = ComputeCost(data, model, pricing);
            bool hasEventCost = TryGetDecimal(data, "cost_usd", out var eventCost);
            if (tableCost is decimal computed)
            {
                bucket.CostUsd += computed;
                if (hasEventCost)
                {
                    bucket.EventCostUsd += eventCost;
                    bucket.EventCostCalls++;
                }
            }
            else if (hasEventCost)
            {
                bucket.CostUsd += eventCost;
            }
            else
            {
                bucket.UnknownModelCost = true;
            }
        }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            // Structurally valid JSON but missing/ill-typed required fields.
            skipped++;
            skippedLines.Add(lineNo);
        }
        finally
        {
            doc.Dispose();
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== {path} ===");
    Console.WriteLine($"Project:        {projectId ?? "n/a"}");
    Console.WriteLine($"Workspace:      {workspaceSlug ?? "n/a"}");
    Console.WriteLine($"Build version:  {buildVersion ?? "n/a"}");
    var ticketLabel = tickets.Count > 1 ? "Tickets:" : "Ticket:";
    var ticketValue = tickets.Count > 0 ? string.Join(", ", tickets) : "n/a";
    Console.WriteLine($"{ticketLabel,-15} {ticketValue}");
    Console.WriteLine($"First event:    {firstTs ?? "n/a"}");
    Console.WriteLine($"Last event:     {lastTs ?? "n/a"}");

    if (chainEnds.Count == 1)
    {
        var (_, outcome, phasesRun, totalMs) = chainEnds[0];
        var rework = reworkByTicket.Values.Sum();
        Console.WriteLine($"Chain outcome:  {outcome}");
        Console.WriteLine($"Phases run:     {phasesRun}");
        Console.WriteLine($"Rework rounds:  {rework}");
        foreach (var (tid, count) in reworkByTicket)
            Console.WriteLine($"  {tid}: {count}");
        Console.WriteLine($"Chain duration: {totalMs / 1000.0:F1}s ({totalMs / 60000.0:F2}m)");
    }
    else if (chainEnds.Count > 1)
    {
        var rework = reworkByTicket.Values.Sum();
        var sumMs = chainEnds.Sum(c => c.TotalMs);
        var sumPhases = chainEnds.Sum(c => c.PhasesRun);
        Console.WriteLine($"Chains:         {chainEnds.Count}");
        foreach (var (tid, outcome, phasesRun, totalMs) in chainEnds)
            Console.WriteLine($"  {tid}: {outcome}, {phasesRun} phases, {totalMs / 1000.0:F1}s");
        Console.WriteLine($"Phases run:     {sumPhases} (all chains)");
        Console.WriteLine($"Rework rounds:  {rework}");
        foreach (var (tid, count) in reworkByTicket)
            Console.WriteLine($"  {tid}: {count}");
        Console.WriteLine($"Chain duration: {sumMs / 1000.0:F1}s ({sumMs / 60000.0:F2}m) across {chainEnds.Count} chains");
    }

    if (chainEnds.Count > 0 && subsumedEvents.Count > 0)
    {
        Console.WriteLine($"Subsumed:       {subsumedEvents.Count} ticket(s) auto-resolved");
        foreach (var (tid, commit) in subsumedEvents)
            Console.WriteLine($"  {tid}  subsumed_by {commit}");
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

    if (totals.EventCostCalls > 0)
    {
        var diff = Math.Abs(totals.CostUsd - totals.EventCostUsd);
        var baseline = Math.Max(totals.CostUsd, totals.EventCostUsd);
        if (baseline > 0 && diff / baseline > 0.10m)
            Console.WriteLine($"\n!! WARNING: worker-reported cost_usd sums to ${totals.EventCostUsd:F4} but the "
                + $"pricing table computes ${totals.CostUsd:F4} for the same {totals.EventCostCalls} call(s). "
                + "Reporting the table value; the worker CLI likely has stale pricing for this model.");
    }

    totals.SkippedLines = skipped;
    if (skipped > 0)
    {
        var which = string.Join(", ", skippedLines);
        var noun = skipped == 1 ? "line" : "lines";
        Console.WriteLine($"\n!! WARNING: skipped {skipped} malformed/truncated {noun} ({which}); "
            + "the run likely ended abnormally and the totals above are incomplete.");
    }

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

// Claude Code tier aliases can land in the event log verbatim (a run configured with an
// alias echoes it in the init system event; an invalid-model failure records the raw
// configured value). Map them to the slug of the model the alias currently resolves to
// so those runs do not silently drop out of cost reporting.
static string NormalizeModelAlias(string model) => model.Trim().ToLowerInvariant() switch
{
    "fable" => "claude-fable-5",
    "opus" => "claude-opus-4-8",
    "sonnet" => "claude-sonnet-4-6",
    "haiku" => "claude-haiku-4-5",
    _ => model,
};

static decimal? ComputeCost(
    JsonElement data, string model,
    Pricing[] pricing)
{
    model = NormalizeModelAlias(model);
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
    public decimal EventCostUsd;   // worker-reported cost for calls where the table was used
    public int EventCostCalls;
    public bool UnknownModelCost;
    public int SkippedLines;
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
        EventCostUsd      += other.EventCostUsd;
        EventCostCalls    += other.EventCostCalls;
        SkippedLines      += other.SkippedLines;
        if (other.UnknownModelCost) UnknownModelCost = true;
        foreach (var m in other.Models) Models.Add(m);
    }
}
