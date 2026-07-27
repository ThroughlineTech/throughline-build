using System.Text.RegularExpressions;

namespace ThroughlineBuild.Scaffold;

public static partial class OpDocSkeletonGenerator
{
    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && KebabSlugPattern().IsMatch(slug);

    public static string Render(string slug)
    {
        if (!IsValidSlug(slug))
            throw new ArgumentException("Operation slug must be kebab-case: lowercase letters and digits separated by single hyphens, starting with a letter.", nameof(slug));

        var opDoc = CreateSkeleton(slug);
        return Render(opDoc);
    }

    private static OpDoc CreateSkeleton(string slug)
    {
        var brief = new Brief(
            Slug: "first-brief",
            Number: 1,
            Title: "First brief",
            Goal: "Placeholder goal for the first independently shippable work item.",
            Inputs:
            [
                "Placeholder input or existing context.",
            ],
            Outputs:
            [
                "Placeholder output or changed artifact.",
            ],
            AcceptanceCriteria:
            [
                "The placeholder acceptance criterion is replaced with a concrete observable outcome.",
            ],
            Notes: "Placeholder notes for constraints, caveats, or useful context.",
            OutOfScope:
            [
                "Placeholder out-of-scope item one.",
                "Placeholder out-of-scope item two.",
                "Placeholder out-of-scope item three.",
            ],
            DependsOn: null);

        var plan = new Plan(
            Id: "A",
            Name: "First plan",
            Goal: "Placeholder goal for Plan A.",
            Briefs: [brief]);

        return new OpDoc(
            OperationSlug: slug,
            Title: "Placeholder lead paragraph for this operation. Replace this with one or two sentences describing what the operation accomplishes.",
            Why: "Placeholder motivation for why this operation exists. Replace this with concrete evidence, context, or failure mode.",
            DispatchOrder:
            [
                new DispatchEntry(
                    PlanId: "A",
                    Name: "First plan",
                    DependsOn: null,
                    Effort: "M"),
            ],
            Plans: [plan],
            WhatDoneLooksLike: "Placeholder done state. Replace this with the observable end state for the operation.");
    }

    // The document shape lives in embedded templates (Templates/op-doc-skeleton*.md). C# only
    // iterates the data and fills placeholders; no heading/label/table text is hardcoded here.
    // Templates are authored LF; the assembled document is converted to Environment.NewLine on
    // return so the on-disk bytes match the previous StringBuilder/AppendLine output verbatim.
    private static string Render(OpDoc opDoc)
    {
        var dispatchRows = string.Concat(opDoc.DispatchOrder.Select(RenderDispatchRow));
        var planBlocks = string.Concat(opDoc.Plans.Select(RenderPlan));

        var document = Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton.md"), new Dictionary<string, string>
        {
            ["operation_slug"] = opDoc.OperationSlug,
            ["title"] = opDoc.Title,
            ["why"] = opDoc.Why,
            ["dispatch_rows"] = dispatchRows,
            ["plan_blocks"] = planBlocks,
            ["what_done_looks_like"] = opDoc.WhatDoneLooksLike,
        });

        return document.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private static string RenderDispatchRow(DispatchEntry entry) =>
        Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton-dispatch-row.md"), new Dictionary<string, string>
        {
            ["plan_id"] = entry.PlanId,
            ["name"] = entry.Name,
            ["depends_on"] = FormatOptional(entry.DependsOn),
            ["effort"] = entry.Effort,
        });

    private static string RenderPlan(Plan plan)
    {
        var briefRows = string.Concat(plan.Briefs.Select(RenderBriefRow));
        var briefDetails = string.Concat(plan.Briefs.Select(RenderBriefDetail));

        return Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton-plan.md"), new Dictionary<string, string>
        {
            ["plan_id"] = plan.Id,
            ["plan_name"] = plan.Name,
            ["plan_goal"] = plan.Goal,
            ["brief_rows"] = briefRows,
            ["brief_details"] = briefDetails,
        });
    }

    private static string RenderBriefRow(Brief brief) =>
        Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton-brief-row.md"), new Dictionary<string, string>
        {
            ["number"] = brief.Number.ToString("D2"),
            ["slug"] = brief.Slug,
            ["intent"] = brief.Title,
            ["deps"] = FormatOptional(brief.DependsOn),
        });

    private static string RenderBriefDetail(Brief brief)
    {
        // The optional Notes block carries its own "Notes:" label in a template; the blank line
        // that follows it is the structural separator the section frame expects.
        var notesBlock = string.IsNullOrWhiteSpace(brief.Notes)
            ? string.Empty
            : Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton-notes.md"), new Dictionary<string, string>
            {
                ["notes"] = brief.Notes!,
            }) + "\n";

        return Fill(OpDocSkeletonTemplateLoader.Load("op-doc-skeleton-brief-detail.md"), new Dictionary<string, string>
        {
            ["number"] = brief.Number.ToString("D2"),
            ["slug"] = brief.Slug,
            ["goal"] = brief.Goal,
            ["inputs"] = RenderBullets(brief.Inputs, checkbox: false),
            ["outputs"] = RenderBullets(brief.Outputs, checkbox: false),
            ["acceptance"] = RenderBullets(brief.AcceptanceCriteria, checkbox: true),
            ["notes_block"] = notesBlock,
            ["oos"] = RenderBullets(brief.OutOfScope, checkbox: false),
        });
    }

    // Bullet-list rendering is data formatting: one line per item, terminated with a newline so
    // the section frame's blank line lands after the list. The marker (plain vs checkbox) stays
    // in code; the section labels live in the brief-detail template.
    private static string RenderBullets(IReadOnlyList<string> items, bool checkbox) =>
        string.Concat(items.Select(item => (checkbox ? "- [ ] " : "- ") + item + "\n"));

    // Single-pass {{token}} substitution. Throws on a token with no supplied value so template
    // and code cannot silently drift apart.
    private static string Fill(string template, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!values.TryGetValue(key, out var value))
                throw new InvalidOperationException(
                    $"op-doc skeleton template placeholder '{{{{{key}}}}}' has no supplied value.");
            return value;
        });

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    [GeneratedRegex(@"\{\{(\w+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabSlugPattern();
}
