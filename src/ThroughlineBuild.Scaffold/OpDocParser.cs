using System.Text.RegularExpressions;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// Hand-rolled, line-oriented parser for op-doc markdown files.
/// Single-pass over lines; tracks heading depth and current section state.
/// Errors are gathered and returned in ParseResult rather than thrown.
/// </summary>
public static class OpDocParser
{
    // Heading patterns
    private static readonly Regex H1Pattern = new(@"^# Operation:\s*(?<slug>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex H2Pattern = new(@"^## (?<title>.+)$", RegexOptions.Compiled);
    private static readonly Regex H3Pattern = new(@"^### (?<title>.+)$", RegexOptions.Compiled);
    private static readonly Regex H4Pattern = new(@"^#### Brief (?<num>\d{1,2}):\s*(?<slug>.+)$", RegexOptions.Compiled);
    private static readonly Regex PlanH2Pattern = new(@"^## Plan (?<id>[A-Z]):\s*(?<name>.+)$", RegexOptions.Compiled);

    // Table patterns
    private static readonly Regex TableRowPattern = new(@"^\|.+\|$", RegexOptions.Compiled);
    private static readonly Regex TableSepPattern = new(@"^\|[\s\-\|:]+\|$", RegexOptions.Compiled);

    // Bullet patterns
    private static readonly Regex BulletPattern = new(@"^[\-\*]\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex CheckboxPattern = new(@"^-\s+\[[ xX]\]\s*(?<text>.+)$", RegexOptions.Compiled);

    // Brief subsection label, tolerant of markdown emphasis around the label token.
    // "Goal:", "**Goal:**", "**Goal**:", "*Goal:*", and "__OOS__:" all resolve to the same label.
    // The closing emphasis is only consumed when there was a matching opening emphasis (backreference \k<e>),
    // so a plain "Goal: **bold** content" keeps the leading "**" as content rather than treating it as a marker.
    private static readonly Regex BriefLabelPattern = new(
        @"^\s*(?:(?<e>\*{1,2}|_{1,2})\s*(?<label>Goal|Inputs|Outputs|Acceptance|Notes|OOS|Preload)\s*(?::\s*\k<e>|\k<e>\s*:)|(?<label2>Goal|Inputs|Outputs|Acceptance|Notes|OOS|Preload)\s*:)\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Required H2 section names (case-sensitive match per spec)
    private const string WhySection = "Why this exists";
    private const string DispatchSection = "Dispatch order";
    private const string WhatDoneSection = "What done looks like";

    // Required dispatch table columns (lowercase for matching)
    private static readonly string[] RequiredDispatchColumns = { "plan", "name", "depends on", "effort" };

    // Required brief subsection labels
    private static readonly string[] RequiredBriefLabels = { "Goal:", "OOS:", "Acceptance:" };

    public static ParseResult Parse(string filePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (Exception ex)
        {
            var fileErr = new OpDocParseError(0, "file", $"Cannot read file: {ex.Message}");
            return new ParseResult(null, new[] { fileErr });
        }

        return ParseLines(lines);
    }

    // Public entry for testing with inline content (avoids file I/O in tests)
    public static ParseResult ParseLines(string[] lines)
    {
        var errors = new List<OpDocParseError>();

        // ---- Phase 1: single-pass line scan to extract sections ----

        string? operationSlug = null;
        string? operationTitle = null;
        int h1Line = -1;

        string? whyContent = null;
        int whyLine = -1;

        List<string> dispatchLines = new();
        int dispatchLine = -1;

        List<(string PlanId, string PlanName, int LineNum, List<string> H3Lines)> planSections = new();

        string? whatDoneContent = null;
        int whatDoneLine = -1;

        // We walk line by line tracking current context
        // State machine: what H2 context are we inside?
        string? currentH2 = null;
        int currentH2Line = -1;
        string? currentPlanId = null;
        string? currentPlanName = null;
        int currentPlanLine = -1;
        List<string> currentSectionLines = new();
        List<string> currentPlanH3Lines = new();

        void FlushCurrentH2()
        {
            if (currentH2 == null) return;

            string body = string.Join("\n", currentSectionLines);

            if (currentH2 == WhySection)
            {
                whyContent = body.Trim();
                whyLine = currentH2Line;
            }
            else if (currentH2 == DispatchSection)
            {
                dispatchLines.AddRange(currentSectionLines);
                dispatchLine = currentH2Line;
            }
            else if (currentH2 == WhatDoneSection)
            {
                whatDoneContent = body.Trim();
                whatDoneLine = currentH2Line;
            }
            else if (currentPlanId != null)
            {
                planSections.Add((currentPlanId, currentPlanName!, currentPlanLine, new List<string>(currentPlanH3Lines)));
                currentPlanId = null;
                currentPlanName = null;
            }
        }

        bool titleNextParagraph = false;
        List<string> titleLines = new();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];

            // Check H1
            var h1Match = H1Pattern.Match(line);
            if (h1Match.Success)
            {
                FlushCurrentH2();
                currentH2 = null;
                currentSectionLines = new();
                currentPlanH3Lines = new();

                operationSlug = h1Match.Groups["slug"].Value;
                h1Line = lineNum;
                titleNextParagraph = true;
                titleLines = new();
                continue;
            }

            // Collect title paragraph (lines right after H1 until blank)
            if (titleNextParagraph)
            {
                // Skip blank lines before title paragraph
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (titleLines.Count > 0)
                    {
                        // We have content, blank ends the paragraph
                        operationTitle = string.Join(" ", titleLines).Trim();
                        titleNextParagraph = false;
                    }
                    continue;
                }
                // Stop title collection at any heading
                if (line.StartsWith("#"))
                {
                    operationTitle = string.Join(" ", titleLines).Trim();
                    titleNextParagraph = false;
                    // Fall through to heading handling
                }
                else
                {
                    titleLines.Add(line.Trim());
                    continue;
                }
            }

            // Check H2
            var h2Match = H2Pattern.Match(line);
            if (h2Match.Success)
            {
                FlushCurrentH2();
                currentSectionLines = new();
                currentPlanH3Lines = new();

                string h2Title = h2Match.Groups["title"].Value.Trim();

                // Check if this is a Plan H2
                var planMatch = PlanH2Pattern.Match(line);
                if (planMatch.Success)
                {
                    currentH2 = line; // use full line as key to avoid collision
                    currentH2Line = lineNum;
                    currentPlanId = planMatch.Groups["id"].Value;
                    currentPlanName = planMatch.Groups["name"].Value.Trim();
                    currentPlanLine = lineNum;
                }
                else
                {
                    currentH2 = h2Title;
                    currentH2Line = lineNum;
                    currentPlanId = null;
                    currentPlanName = null;
                }
                continue;
            }

            // Accumulate lines for current H2 section
            if (currentH2 != null)
            {
                if (currentPlanId != null)
                {
                    currentPlanH3Lines.Add(line);
                }
                else
                {
                    currentSectionLines.Add(line);
                }
            }
        }

        // Flush last section
        FlushCurrentH2();

        // If title wasn't terminated by blank/heading
        if (titleNextParagraph && titleLines.Count > 0)
            operationTitle = string.Join(" ", titleLines).Trim();

        // ---- Phase 2: validate required top-level sections ----

        if (operationSlug == null)
        {
            errors.Add(OpDocParseError.Create(1, "document", "missing_h1: No '# Operation: <slug>' header found"));
            return new ParseResult(null, errors);
        }

        if (string.IsNullOrWhiteSpace(operationTitle))
            operationTitle = operationSlug;

        if (whyContent == null)
            errors.Add(OpDocParseError.Create(h1Line, "document", "missing_section: '## Why this exists' section not found"));

        if (dispatchLine < 0)
            errors.Add(OpDocParseError.Create(h1Line, "document", "missing_section: '## Dispatch order' section not found"));

        if (whatDoneContent == null)
            errors.Add(OpDocParseError.Create(h1Line, "document", "missing_section: '## What done looks like' section not found"));

        // ---- Phase 3: parse dispatch table ----

        var dispatchEntries = new List<DispatchEntry>();
        if (dispatchLine >= 0 && dispatchLines.Count > 0)
        {
            var (entries, dispatchErrors) = ParseDispatchTable(dispatchLines, dispatchLine);
            dispatchEntries.AddRange(entries);
            errors.AddRange(dispatchErrors);
        }

        // Build set of declared plan IDs from dispatch table
        var declaredPlanIds = new HashSet<string>(dispatchEntries.Select(e => e.PlanId), StringComparer.OrdinalIgnoreCase);

        // ---- Phase 4: parse plan sections ----

        // Check plans in dispatch order that have no matching section
        var parsedPlanIds = new HashSet<string>(planSections.Select(p => p.PlanId), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in dispatchEntries)
        {
            if (!parsedPlanIds.Contains(entry.PlanId))
            {
                errors.Add(OpDocParseError.Create(dispatchLine, $"Plans[{entry.PlanId}]",
                    $"plan_section_missing: Dispatch order references Plan {entry.PlanId} but no '## Plan {entry.PlanId}: ...' section found"));
            }
        }

        // Warn about plans not in dispatch order (forward-compat warning, surfaced with "warning:" prefix)
        foreach (var planSec in planSections)
        {
            if (!declaredPlanIds.Contains(planSec.PlanId))
            {
                errors.Add(OpDocParseError.Create(planSec.LineNum, $"Plans[{planSec.PlanId}]",
                    $"warning:plan_not_in_dispatch: Plan {planSec.PlanId} exists but is not referenced in dispatch order"));
            }
        }

        var plans = new List<Plan>();
        foreach (var planSec in planSections)
        {
            var (plan, planErrors) = ParsePlanSection(planSec.PlanId, planSec.PlanName, planSec.LineNum, planSec.H3Lines);
            plans.Add(plan);
            errors.AddRange(planErrors);
        }

        // If critical errors make OpDoc unbuildable, still return best-effort
        var opDoc = new OpDoc(
            OperationSlug: operationSlug,
            Title: operationTitle,
            Why: whyContent ?? string.Empty,
            DispatchOrder: dispatchEntries,
            Plans: plans,
            WhatDoneLooksLike: whatDoneContent ?? string.Empty);

        return new ParseResult(opDoc, errors);
    }

    // ---- Dispatch table parser ----

    private static (List<DispatchEntry> Entries, List<OpDocParseError> Errors) ParseDispatchTable(
        List<string> bodyLines, int sectionLineOffset)
    {
        var entries = new List<DispatchEntry>();
        var errors = new List<OpDocParseError>();

        // Find the first table row (header)
        int headerIdx = -1;
        for (int i = 0; i < bodyLines.Count; i++)
        {
            if (TableRowPattern.IsMatch(bodyLines[i]))
            {
                headerIdx = i;
                break;
            }
        }

        if (headerIdx < 0)
        {
            errors.Add(OpDocParseError.Create(sectionLineOffset, "dispatch_order",
                "dispatch_columns_missing: No table found in Dispatch order section"));
            return (entries, errors);
        }

        // Parse header
        var headerCells = SplitTableRow(bodyLines[headerIdx]);
        var headerLower = headerCells.Select(c => c.Trim().ToLowerInvariant()).ToList();

        // Validate required columns
        foreach (var required in RequiredDispatchColumns)
        {
            if (!headerLower.Contains(required))
            {
                int lineNum = sectionLineOffset + headerIdx + 1;
                errors.Add(OpDocParseError.Create(lineNum, "dispatch_order",
                    $"dispatch_columns_missing: Required column '{required}' not found in dispatch table header"));
            }
        }

        // Find column indexes
        int planCol = headerLower.IndexOf("plan");
        int nameCol = headerLower.IndexOf("name");
        int depsCol = headerLower.IndexOf("depends on");
        int effortCol = headerLower.IndexOf("effort");

        if (planCol < 0 || nameCol < 0)
        {
            // Can't parse rows without at minimum Plan and Name
            return (entries, errors);
        }

        // Skip separator row
        int dataStart = headerIdx + 1;
        if (dataStart < bodyLines.Count && TableSepPattern.IsMatch(bodyLines[dataStart]))
            dataStart++;

        // Parse data rows
        for (int i = dataStart; i < bodyLines.Count; i++)
        {
            string line = bodyLines[i];
            if (!TableRowPattern.IsMatch(line))
                break; // end of table

            var cells = SplitTableRow(line);
            int lineNum = sectionLineOffset + i + 1;

            if (cells.Count != headerCells.Count)
            {
                errors.Add(OpDocParseError.Create(lineNum, "dispatch_order",
                    $"dispatch_row_malformed: Row has {cells.Count} cells but header has {headerCells.Count}"));
                continue;
            }

            string planId = planCol >= 0 && planCol < cells.Count ? cells[planCol].Trim() : string.Empty;
            string name = nameCol >= 0 && nameCol < cells.Count ? cells[nameCol].Trim() : string.Empty;
            string? dependsOn = null;
            if (depsCol >= 0 && depsCol < cells.Count)
            {
                string raw = cells[depsCol].Trim();
                dependsOn = raw == "-" || string.IsNullOrWhiteSpace(raw) ? null : raw;
            }
            string effort = effortCol >= 0 && effortCol < cells.Count ? cells[effortCol].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(planId))
                continue;

            entries.Add(new DispatchEntry(PlanId: planId, Name: name, DependsOn: dependsOn, Effort: effort));
        }

        return (entries, errors);
    }

    // ---- Plan section parser ----

    private static (Plan Plan, List<OpDocParseError> Errors) ParsePlanSection(
        string planId, string planName, int planLineNum, List<string> h3Lines)
    {
        var errors = new List<OpDocParseError>();

        // Split h3Lines into H3 sub-sections
        string? goalContent = null;
        List<string> briefsTableLines = new();
        List<string> briefsDetailLines = new();
        int briefsTableLineOffset = planLineNum;
        int briefsDetailLineOffset = planLineNum;

        string? currentH3 = null;
        List<string> currentH3Body = new();
        int currentH3LineOffset = planLineNum;

        void FlushH3()
        {
            if (currentH3 == null) return;
            if (currentH3 == "Goal")
                goalContent = string.Join("\n", currentH3Body).Trim();
            else if (currentH3 == "Briefs")
            {
                briefsTableLines.AddRange(currentH3Body);
                briefsTableLineOffset = currentH3LineOffset;
            }
            else if (currentH3 == "Briefs - detail")
            {
                briefsDetailLines.AddRange(currentH3Body);
                briefsDetailLineOffset = currentH3LineOffset;
            }
        }

        for (int i = 0; i < h3Lines.Count; i++)
        {
            string line = h3Lines[i];
            var h3Match = H3Pattern.Match(line);
            if (h3Match.Success)
            {
                FlushH3();
                currentH3 = h3Match.Groups["title"].Value.Trim();
                currentH3Body = new();
                currentH3LineOffset = planLineNum + i + 1;
            }
            else
            {
                currentH3Body.Add(line);
            }
        }
        FlushH3();

        if (goalContent == null)
            goalContent = string.Empty;

        // Parse briefs table
        var (briefStubs, briefTableErrors) = ParseBriefsTable(briefsTableLines, planId, briefsTableLineOffset);
        errors.AddRange(briefTableErrors);

        // Parse briefs detail
        var (briefDetails, briefDetailErrors) = ParseBriefsDetail(briefsDetailLines, planId, briefsDetailLineOffset);
        errors.AddRange(briefDetailErrors);

        // Merge stubs with details
        var briefs = new List<Brief>();
        foreach (var stub in briefStubs)
        {
            if (briefDetails.TryGetValue(stub.Number, out var detail))
            {
                briefs.Add(stub with
                {
                    Title = detail.Title,
                    Goal = detail.Goal,
                    Inputs = detail.Inputs,
                    Outputs = detail.Outputs,
                    AcceptanceCriteria = detail.AcceptanceCriteria,
                    Notes = detail.Notes,
                    OutOfScope = detail.OutOfScope,
                    PreloadFiles = detail.PreloadFiles
                });
            }
            else
            {
                errors.Add(OpDocParseError.Create(briefsTableLineOffset, $"Plans[{planId}].Briefs[{stub.Number:D2}]",
                    $"brief_detail_missing: Brief {stub.Number:D2} ({stub.Slug}) appears in briefs table but has no '#### Brief {stub.Number:D2}:' detail section"));
                briefs.Add(stub);
            }
        }

        // Check for detail sections that weren't in the table
        foreach (var kvp in briefDetails)
        {
            if (!briefStubs.Any(s => s.Number == kvp.Key))
            {
                // Extra detail section not in table - not an error, just merge
                briefs.Add(kvp.Value);
            }
        }

        // Sort by brief number
        briefs.Sort((a, b) => a.Number.CompareTo(b.Number));

        var plan = new Plan(Id: planId, Name: planName, Goal: goalContent, Briefs: briefs);
        return (plan, errors);
    }

    // ---- Briefs table parser ----

    private static (List<Brief> Stubs, List<OpDocParseError> Errors) ParseBriefsTable(
        List<string> lines, string planId, int lineOffset)
    {
        var stubs = new List<Brief>();
        var errors = new List<OpDocParseError>();

        int headerIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (TableRowPattern.IsMatch(lines[i]))
            {
                headerIdx = i;
                break;
            }
        }

        if (headerIdx < 0)
            return (stubs, errors);

        var headerCells = SplitTableRow(lines[headerIdx]);
        var headerLower = headerCells.Select(c => c.Trim().ToLowerInvariant()).ToList();

        int numCol = headerLower.IndexOf("#");
        int slugCol = headerLower.IndexOf("slug");
        int intentCol = headerLower.IndexOf("intent");
        int depsCol = headerLower.IndexOf("deps");

        if (numCol < 0 || slugCol < 0)
            return (stubs, errors);

        int dataStart = headerIdx + 1;
        if (dataStart < lines.Count && TableSepPattern.IsMatch(lines[dataStart]))
            dataStart++;

        for (int i = dataStart; i < lines.Count; i++)
        {
            string line = lines[i];
            if (!TableRowPattern.IsMatch(line))
                break;

            var cells = SplitTableRow(line);
            if (cells.Count < 2)
                continue;

            string numStr = numCol < cells.Count ? cells[numCol].Trim() : string.Empty;
            string slug = slugCol < cells.Count ? cells[slugCol].Trim() : string.Empty;
            string intent = intentCol >= 0 && intentCol < cells.Count ? cells[intentCol].Trim() : string.Empty;
            string? dependsOn = null;
            if (depsCol >= 0 && depsCol < cells.Count)
            {
                string raw = cells[depsCol].Trim();
                dependsOn = raw == "-" || string.IsNullOrWhiteSpace(raw) ? null : raw;
            }

            if (!int.TryParse(numStr, out int briefNum))
                continue;

            stubs.Add(new Brief(
                Slug: slug,
                Number: briefNum,
                Title: intent, // default title from intent column; detail section overrides
                Goal: string.Empty,
                Inputs: Array.Empty<string>(),
                Outputs: Array.Empty<string>(),
                AcceptanceCriteria: Array.Empty<string>(),
                Notes: null,
                OutOfScope: Array.Empty<string>(),
                DependsOn: dependsOn));
        }

        return (stubs, errors);
    }

    // ---- Briefs detail parser ----

    private static (Dictionary<int, Brief> Details, List<OpDocParseError> Errors) ParseBriefsDetail(
        List<string> lines, string planId, int lineOffset)
    {
        var details = new Dictionary<int, Brief>();
        var errors = new List<OpDocParseError>();

        // Split into individual H4 brief blocks
        var briefBlocks = new List<(int Num, string Slug, int StartLine, List<string> Body)>();
        int? currentNum = null;
        string? currentSlug = null;
        int currentStart = lineOffset;
        List<string> currentBody = new();

        void FlushBrief()
        {
            if (currentNum == null) return;
            briefBlocks.Add((currentNum.Value, currentSlug!, currentStart, new List<string>(currentBody)));
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            var h4Match = H4Pattern.Match(line);
            if (h4Match.Success)
            {
                FlushBrief();
                int.TryParse(h4Match.Groups["num"].Value, out int num);
                currentNum = num;
                currentSlug = h4Match.Groups["slug"].Value.Trim();
                currentStart = lineOffset + i;
                currentBody = new();
            }
            else if (currentNum != null)
            {
                currentBody.Add(line);
            }
        }
        FlushBrief();

        // Parse each brief block
        foreach (var (num, slug, startLine, body) in briefBlocks)
        {
            var (brief, briefErrors) = ParseBriefDetail(num, slug, planId, startLine, body);
            details[num] = brief;
            errors.AddRange(briefErrors);
        }

        return (details, errors);
    }

    // ---- Single brief detail parser ----

    private static (Brief Brief, List<OpDocParseError> Errors) ParseBriefDetail(
        int num, string slug, string planId, int startLine, List<string> lines)
    {
        var errors = new List<OpDocParseError>();

        string goal = string.Empty;
        var inputs = new List<string>();
        var outputs = new List<string>();
        var acceptance = new List<string>();
        string? notes = null;
        var oos = new List<string>();
        var preload = new List<string>();

        // Simple label-scanning state machine
        string? currentLabel = null;
        var currentLines = new List<string>();
        int currentLabelLine = startLine;

        void FlushLabel()
        {
            if (currentLabel == null) return;
            switch (currentLabel)
            {
                case "goal":
                    goal = string.Join(" ", currentLines.Select(l => l.Trim())).Trim();
                    break;
                case "inputs":
                    inputs.AddRange(ExtractBullets(currentLines));
                    break;
                case "outputs":
                    outputs.AddRange(ExtractBullets(currentLines));
                    break;
                case "acceptance":
                    acceptance.AddRange(ExtractAcceptance(currentLines));
                    break;
                case "notes":
                    notes = string.Join("\n", currentLines.Select(l => l.Trim())).Trim();
                    if (string.IsNullOrWhiteSpace(notes)) notes = null;
                    break;
                case "oos":
                    var oosBullets = ExtractBullets(currentLines);
                    if (oosBullets.Count > 0)
                        oos.AddRange(oosBullets);
                    else
                    {
                        // Prose OOS (no bullet markers) - treat the whole text as one item
                        var prose = string.Join(" ", currentLines.Select(l => l.Trim())).Trim();
                        if (!string.IsNullOrEmpty(prose))
                            oos.Add(prose);
                    }
                    break;
                case "preload":
                    // Positive-only file paths (one bullet per path). Optional; greenfield briefs omit it.
                    preload.AddRange(ExtractBullets(currentLines));
                    break;
            }
        }

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];

            // Check for label line
            string? label = DetectLabel(line);
            if (label != null)
            {
                FlushLabel();
                currentLabel = label;
                currentLabelLine = startLine + i;
                // Capture any same-line content after the label
                string afterLabel = GetAfterLabel(line);
                currentLines = new();
                if (!string.IsNullOrWhiteSpace(afterLabel))
                    currentLines.Add(afterLabel);
            }
            else if (currentLabel != null)
            {
                // Continuation line
                currentLines.Add(line);
            }
        }
        FlushLabel();

        // Validate required subsections
        string sectionPath = $"Plans[{planId}].Briefs[{num:D2}]";
        if (string.IsNullOrWhiteSpace(goal))
            errors.Add(OpDocParseError.Create(startLine, sectionPath,
                "brief_subsection_missing: Brief missing required 'Goal:' subsection"));

        if (acceptance.Count == 0)
            errors.Add(OpDocParseError.Create(startLine, sectionPath,
                "brief_subsection_missing: Brief missing required 'Acceptance:' subsection"));

        if (oos.Count == 0)
            errors.Add(OpDocParseError.Create(startLine, sectionPath,
                "brief_subsection_missing: Brief missing required 'OOS:' subsection"));

        var brief = new Brief(
            Slug: slug,
            Number: num,
            Title: slug, // detail section title = slug; table's intent may have been set earlier
            Goal: goal,
            Inputs: inputs,
            Outputs: outputs,
            AcceptanceCriteria: acceptance,
            Notes: notes,
            OutOfScope: oos,
            DependsOn: null)
        {
            PreloadFiles = preload
        };

        return (brief, errors);
    }

    // ---- Helpers ----

    private static string? DetectLabel(string line)
    {
        var match = BriefLabelPattern.Match(line);
        if (!match.Success) return null;
        string label = match.Groups["label"].Success
            ? match.Groups["label"].Value
            : match.Groups["label2"].Value;
        return label.ToLowerInvariant();
    }

    private static string GetAfterLabel(string line)
    {
        var match = BriefLabelPattern.Match(line);
        return match.Success ? match.Groups["rest"].Value.Trim() : string.Empty;
    }

    private static List<string> ExtractBullets(List<string> lines)
    {
        var result = new List<string>();
        foreach (string line in lines)
        {
            var match = BulletPattern.Match(line.TrimStart());
            if (match.Success)
                result.Add(match.Groups["text"].Value.Trim());
        }
        return result;
    }

    private static List<string> ExtractAcceptance(List<string> lines)
    {
        var result = new List<string>();
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            // Match checkbox: - [ ] text or - [x] text
            var cbMatch = Regex.Match(trimmed, @"^-\s+\[[ xX]\]\s*(?<text>.+)$");
            if (cbMatch.Success)
            {
                result.Add(cbMatch.Groups["text"].Value.Trim());
                continue;
            }
            // Also accept plain bullets as acceptance items
            var bulletMatch = BulletPattern.Match(trimmed);
            if (bulletMatch.Success)
                result.Add(bulletMatch.Groups["text"].Value.Trim());
        }
        return result;
    }

    private static List<string> SplitTableRow(string line)
    {
        // Split on | but ignore leading/trailing empty segments
        var parts = line.Split('|');
        // First and last parts are empty (before first | and after last |)
        var cells = new List<string>();
        for (int i = 1; i < parts.Length - 1; i++)
            cells.Add(parts[i]);
        return cells;
    }
}
