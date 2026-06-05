using System.Text;
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

    private static string Render(OpDoc opDoc)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Operation: {opDoc.OperationSlug}");
        sb.AppendLine();
        sb.AppendLine(opDoc.Title);
        sb.AppendLine();
        sb.AppendLine("## Why this exists");
        sb.AppendLine();
        sb.AppendLine(opDoc.Why);
        sb.AppendLine();
        sb.AppendLine("## Dispatch order");
        sb.AppendLine();
        sb.AppendLine("| Plan | Name | Depends on | Effort |");
        sb.AppendLine("| ---- | ---- | ---------- | ------ |");
        foreach (var entry in opDoc.DispatchOrder)
        {
            sb.AppendLine($"| {entry.PlanId} | {entry.Name} | {FormatOptional(entry.DependsOn)} | {entry.Effort} |");
        }
        sb.AppendLine();

        foreach (var plan in opDoc.Plans)
        {
            sb.AppendLine($"## Plan {plan.Id}: {plan.Name}");
            sb.AppendLine();
            sb.AppendLine("### Goal");
            sb.AppendLine();
            sb.AppendLine(plan.Goal);
            sb.AppendLine();
            sb.AppendLine("### Briefs");
            sb.AppendLine();
            sb.AppendLine("| # | Slug | Intent | Deps | Files |");
            sb.AppendLine("|---|------|--------|------|-------|");
            foreach (var brief in plan.Briefs)
            {
                sb.AppendLine($"| {brief.Number:D2} | {brief.Slug} | {brief.Title} | {FormatOptional(brief.DependsOn)} | - |");
            }
            sb.AppendLine();
            sb.AppendLine("### Briefs - detail");
            sb.AppendLine();

            foreach (var brief in plan.Briefs)
            {
                sb.AppendLine($"#### Brief {brief.Number:D2}: {brief.Slug}");
                sb.AppendLine();
                sb.AppendLine($"Goal: {brief.Goal}");
                sb.AppendLine();
                AppendBullets(sb, "Inputs:", brief.Inputs, checkbox: false);
                AppendBullets(sb, "Outputs:", brief.Outputs, checkbox: false);
                AppendBullets(sb, "Acceptance:", brief.AcceptanceCriteria, checkbox: true);
                if (!string.IsNullOrWhiteSpace(brief.Notes))
                {
                    sb.AppendLine("Notes:");
                    sb.AppendLine(brief.Notes);
                    sb.AppendLine();
                }
                AppendBullets(sb, "OOS:", brief.OutOfScope, checkbox: false);
            }
        }

        sb.AppendLine("## What done looks like");
        sb.AppendLine();
        sb.AppendLine(opDoc.WhatDoneLooksLike);

        return sb.ToString();
    }

    private static void AppendBullets(StringBuilder sb, string heading, IReadOnlyList<string> items, bool checkbox)
    {
        sb.AppendLine(heading);
        foreach (var item in items)
        {
            sb.AppendLine(checkbox ? $"- [ ] {item}" : $"- {item}");
        }
        sb.AppendLine();
    }

    private static string FormatOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value;

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KebabSlugPattern();
}
