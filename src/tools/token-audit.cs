// .NET version of claude-config's token-audit-v2.
//
// Run on .NET 10+ (file-based programs):
//
//   dotnet run token_audit.cs latest [--path <repo-root>]
//       prints the path to the most recent Claude Code session JSONL for the
//       given repo (or current working directory if --path omitted)
//
//   dotnet run token_audit.cs extract <session.jsonl>
//       parses one session file and emits one JSON line summarizing the run
//       (command, session_id, timestamps, model, token usage)
//
//   dotnet run token_audit.cs extract <glob|dir> [more...]
//       extract also accepts multiple files, a directory (all *.jsonl in it),
//       or a glob like '*' or 'rgv*'. Emits one JSON line per file, sorted
//       oldest-first by modified time. Works whether the shell expands the
//       glob (bash) or passes it literally (cmd / PowerShell).
//
//   dotnet run token_audit.cs
//       runs `latest` then `extract` on the result (the common case)
//
// Compile to a standalone exe:
//   dotnet publish token_audit.cs -c Release
//
// ASSUMPTIONS that may need tuning after first run against a known-good file:
//   - Claude Code project dir encoding: each non-alphanumeric char in the
//     absolute repo path is replaced with '-'. Case preserved. Directory
//     name lives under ~/.claude/projects/<encoded>/
//   - Session JSONL event shape:
//       { "type": "user"|"assistant"|..., "timestamp": "...",
//         "message": { "model": "...", "usage": { "input_tokens": ..., ... },
//                      "content": "/tch ..." or [{"text": "..."}, ...] },
//         "isSidechain": true (when this event belongs to a subagent),
//         ... }
//   - Slash command sits in the FIRST user event's message.content as a
//     leading "/tch ..." string (or in the first text block of an array).
//   - wall_clock_ms in token-audit-v2's output is the elapsed between the
//     last user-typed event's timestamp (start_ts) and the last
//     assistant-typed event's timestamp (ts) - i.e. final-turn duration,
//     NOT total session duration. This matches the small 2-3s values in
//     observed output.
//
// If any of these are wrong against your real files, edit the FIELD_* /
// TYPE_* constants below and the ExtractCommand / wall-clock logic in
// Extract(). The output JSON shape is locked to match token-audit-v2.

using System.Globalization;
using System.Text;
using System.Text.Json;

const string TYPE_USER = "user";
const string TYPE_ASSISTANT = "assistant";

const string FIELD_TYPE = "type";
const string FIELD_TIMESTAMP = "timestamp";
const string FIELD_MESSAGE = "message";
const string FIELD_MODEL = "model";
const string FIELD_USAGE = "usage";
const string FIELD_CONTENT = "content";
const string FIELD_IS_SIDECHAIN = "isSidechain";

const string USAGE_INPUT = "input_tokens";
const string USAGE_OUTPUT = "output_tokens";
const string USAGE_CACHE_READ = "cache_read_input_tokens";
const string USAGE_CACHE_CREATE = "cache_creation_input_tokens";

var subcommand = args.Length > 0 ? args[0].ToLowerInvariant() : "";

switch (subcommand)
{
    case "latest":
    {
        var path = ParseFlag(args, "--path") ?? Environment.CurrentDirectory;
        var latest = FindLatestJsonl(path);
        if (latest is null)
        {
            Console.Error.WriteLine($"error: no Claude Code session files found for {path}");
            Console.Error.WriteLine($"  (looked in ~/.claude/projects/{EncodeProjectPath(path)}/)");
            return 1;
        }
        Console.WriteLine(latest);
        return 0;
    }

    case "extract":
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: token_audit.cs extract <session.jsonl | glob | dir> [more...]");
            return 1;
        }
        var files = ResolveFiles(args[1..]);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no matching .jsonl files");
            return 1;
        }
        foreach (var f in files)
            Console.WriteLine(ExtractToJson(f));
        return 0;
    }

    case "":
    {
        // Combined: latest + extract in one shot.
        var path = ParseFlag(args, "--path") ?? Environment.CurrentDirectory;
        var latest = FindLatestJsonl(path);
        if (latest is null)
        {
            Console.Error.WriteLine($"error: no Claude Code session files found for {path}");
            return 1;
        }
        Console.WriteLine(ExtractToJson(latest));
        return 0;
    }

    default:
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  token_audit.cs latest [--path <repo-root>]");
        Console.Error.WriteLine("  token_audit.cs extract <session.jsonl | glob | dir> [more...]");
        Console.Error.WriteLine("  token_audit.cs                            (latest + extract)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("extract accepts multiple files, a directory (all *.jsonl within),");
        Console.Error.WriteLine("or a glob like '*' or 'rgv*'. Output is one JSON line per file,");
        Console.Error.WriteLine("sorted oldest-first by modified time.");
        return 1;
}


// ---------------------------------------------------------------------------
// Local functions
// ---------------------------------------------------------------------------

static string? ParseFlag(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

static List<string> ResolveFiles(string[] patterns)
{
    // Drop a trailing --path <x> if it slipped into the file args.
    var cleaned = new List<string>();
    for (int i = 0; i < patterns.Length; i++)
    {
        if (patterns[i] == "--path") { i++; continue; }
        cleaned.Add(patterns[i]);
    }

    bool singleExplicitFile = cleaned.Count == 1 && File.Exists(cleaned[0]);
    var found = new List<string>();

    foreach (var pat in cleaned)
    {
        if (File.Exists(pat))
        {
            // Shell already expanded a glob, or user named a file directly.
            found.Add(pat);
        }
        else if (Directory.Exists(pat))
        {
            // A directory: take every session file in it.
            found.AddRange(Directory.GetFiles(pat, "*.jsonl"));
        }
        else if (pat.Contains('*') || pat.Contains('?'))
        {
            // A literal glob the shell did NOT expand (cmd / PowerShell).
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

    // When the set came from globs, directories, or a multi-file expansion,
    // keep only .jsonl files (filters out junk a bare '*' might have caught).
    // A single explicitly-named file is trusted as-is regardless of extension.
    IEnumerable<string> filtered = singleExplicitFile
        ? found
        : found.Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));

    // De-duplicate, then sort oldest-first so multi-file output reads as a
    // chronological audit timeline.
    return filtered
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(File.GetLastWriteTimeUtc)
        .ToList();
}

static string EncodeProjectPath(string path)
{
    // Replace each non-alphanumeric char with '-'. Case preserved, no
    // collapsing of consecutive dashes (so C:\Users -> C--Users).
    var sb = new StringBuilder(path.Length);
    foreach (var c in path)
        sb.Append(char.IsLetterOrDigit(c) ? c : '-');
    return sb.ToString();
}

static string? FindLatestJsonl(string repoPath)
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var projectsRoot = Path.Combine(home, ".claude", "projects");
    if (!Directory.Exists(projectsRoot))
        return null;

    // Try the directly-encoded path first.
    var encoded = EncodeProjectPath(Path.GetFullPath(repoPath));
    var candidate = Path.Combine(projectsRoot, encoded);

    if (!Directory.Exists(candidate))
    {
        // Fall back to fuzzy matching: find the subdirectory whose name is a
        // prefix or substring match. Handles case where the user passed a
        // worktree subdirectory or a path with case differences.
        var leaf = encoded.TrimStart('-');
        var matches = Directory.GetDirectories(projectsRoot)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name.Equals(encoded, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
        if (matches.Count == 0)
            return null;
        candidate = matches.OrderByDescending(d => Directory.GetLastWriteTimeUtc(d)).First();
    }

    var jsonls = Directory.GetFiles(candidate, "*.jsonl");
    if (jsonls.Length == 0)
        return null;

    return jsonls.OrderByDescending(File.GetLastWriteTimeUtc).First();
}

static string ExtractToJson(string filePath)
{
    var sessionId = Path.GetFileNameWithoutExtension(filePath);

    string? command = null;
    string? model = null;

    long mainInput = 0, mainOutput = 0, mainCR = 0, mainCC = 0;
    long subInput = 0, subOutput = 0, subCR = 0, subCC = 0;

    string? lastUserTs = null;
    string? lastAssistantTs = null;
    bool seenFirstUser = false;

    foreach (var raw in File.ReadLines(filePath))
    {
        var line = raw.Trim();
        if (line.Length == 0) continue;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { continue; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;

            var type = TryGetString(root, FIELD_TYPE);
            var ts = TryGetString(root, FIELD_TIMESTAMP);
            bool isSidechain = root.TryGetProperty(FIELD_IS_SIDECHAIN, out var sc)
                && sc.ValueKind == JsonValueKind.True;

            if (type == TYPE_USER)
            {
                if (ts is not null) lastUserTs = ts;
                if (!seenFirstUser)
                {
                    command = ExtractSlashCommand(root) ?? command;
                    seenFirstUser = true;
                }
            }
            else if (type == TYPE_ASSISTANT)
            {
                if (ts is not null) lastAssistantTs = ts;

                if (root.TryGetProperty(FIELD_MESSAGE, out var msg)
                    && msg.ValueKind == JsonValueKind.Object)
                {
                    if (model is null && msg.TryGetProperty(FIELD_MODEL, out var m)
                        && m.ValueKind == JsonValueKind.String)
                    {
                        model = m.GetString();
                    }
                    if (msg.TryGetProperty(FIELD_USAGE, out var usage)
                        && usage.ValueKind == JsonValueKind.Object)
                    {
                        var i  = GetLong(usage, USAGE_INPUT);
                        var o  = GetLong(usage, USAGE_OUTPUT);
                        var cr = GetLong(usage, USAGE_CACHE_READ);
                        var cc = GetLong(usage, USAGE_CACHE_CREATE);
                        if (isSidechain) { subInput += i; subOutput += o; subCR += cr; subCC += cc; }
                        else             { mainInput += i; mainOutput += o; mainCR += cr; mainCC += cc; }
                    }
                }
            }
        }
    }

    long wallClockMs = 0;
    if (lastUserTs is not null && lastAssistantTs is not null
        && DateTimeOffset.TryParse(lastUserTs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var s)
        && DateTimeOffset.TryParse(lastAssistantTs, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e))
    {
        var delta = (long)(e - s).TotalMilliseconds;
        wallClockMs = delta < 0 ? 0 : delta;
    }

    // Build the output JSON manually to lock the field order (matches token-audit-v2).
    var sb = new StringBuilder(512);
    sb.Append('{');
    AppendStr(sb, "label", "");                          sb.Append(',');
    AppendStr(sb, "command", command ?? "");             sb.Append(',');
    AppendStr(sb, "session_id", sessionId);              sb.Append(',');
    AppendStr(sb, "ts", lastAssistantTs ?? "");          sb.Append(',');
    AppendStr(sb, "start_ts", lastUserTs ?? "");         sb.Append(',');
    AppendNum(sb, "wall_clock_ms", wallClockMs);         sb.Append(',');
    AppendStr(sb, "model", model ?? "");                 sb.Append(',');
    AppendNum(sb, "input", mainInput);                   sb.Append(',');
    AppendNum(sb, "output", mainOutput);                 sb.Append(',');
    AppendNum(sb, "cache_read", mainCR);                 sb.Append(',');
    AppendNum(sb, "cache_create", mainCC);               sb.Append(',');
    AppendNum(sb, "subagent_input", subInput);           sb.Append(',');
    AppendNum(sb, "subagent_output", subOutput);         sb.Append(',');
    AppendNum(sb, "subagent_cache_read", subCR);         sb.Append(',');
    AppendNum(sb, "subagent_cache_create", subCC);
    sb.Append('}');
    return sb.ToString();
}

static string? ExtractSlashCommand(JsonElement root)
{
    if (!root.TryGetProperty(FIELD_MESSAGE, out var msg)
        || msg.ValueKind != JsonValueKind.Object
        || !msg.TryGetProperty(FIELD_CONTENT, out var content))
    {
        return null;
    }

    string? text = null;
    if (content.ValueKind == JsonValueKind.String)
    {
        text = content.GetString();
    }
    else if (content.ValueKind == JsonValueKind.Array)
    {
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("text", out var t)
                && t.ValueKind == JsonValueKind.String)
            {
                text = t.GetString();
                break;
            }
        }
    }

    if (string.IsNullOrEmpty(text)) return null;
    var trimmed = text.TrimStart();
    if (!trimmed.StartsWith('/')) return null;

    // Take the slash token up to the first whitespace or newline.
    int end = 0;
    while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
    return trimmed[..end];
}

static long GetLong(JsonElement el, string name)
    => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt64()
        : 0;

static string? TryGetString(JsonElement el, string name)
    => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString()
        : null;

static void AppendStr(StringBuilder sb, string name, string value)
{
    sb.Append('"').Append(name).Append("\":");
    AppendJsonString(sb, value);
}

static void AppendNum(StringBuilder sb, string name, long value)
{
    sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
}

static void AppendJsonString(StringBuilder sb, string s)
{
    sb.Append('"');
    foreach (var c in s)
    {
        switch (c)
        {
            case '"':  sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
                break;
        }
    }
    sb.Append('"');
}