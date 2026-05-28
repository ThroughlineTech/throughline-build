using System.Text.RegularExpressions;

namespace ThroughlineBuild.Scaffold;

public static class OpDocValidator
{
    public static ValidationResult Validate(OpDoc opDoc)
    {
        var errors = new List<ValidationError>();
        var warnings = new List<ValidationWarning>();

        ValidateSlug(opDoc, errors);
        ValidateDispatchOrder(opDoc, errors, warnings);
        ValidatePlans(opDoc, errors, warnings);

        return new ValidationResult(
            IsValid: errors.Count == 0,
            Errors: errors.AsReadOnly(),
            Warnings: warnings.AsReadOnly());
    }

    private static void ValidateSlug(OpDoc opDoc, List<ValidationError> errors)
    {
        if (!Regex.IsMatch(opDoc.OperationSlug, "^[a-z][a-z0-9-]*$"))
        {
            errors.Add(new ValidationError(
                Code: "SLUG_INVALID",
                Path: "OpDoc.OperationSlug",
                Message: "Operation slug must start with lowercase letter and contain only lowercase letters, digits, and hyphens."));
        }
    }

    private static void ValidateDispatchOrder(OpDoc opDoc, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        var planIds = new HashSet<string>(opDoc.Plans.Select(p => p.Id));

        foreach (var entry in opDoc.DispatchOrder)
        {
            // Validate PlanId references an existing Plan
            if (!planIds.Contains(entry.PlanId))
            {
                errors.Add(new ValidationError(
                    Code: "DISPATCH_PLAN_MISSING",
                    Path: $"DispatchOrder[{entry.PlanId}]",
                    Message: $"DispatchOrder references Plan '{entry.PlanId}' which does not exist."));
            }

            // Validate DependsOn references an existing Plan (or is null/empty/"-")
            if (!string.IsNullOrEmpty(entry.DependsOn) && entry.DependsOn != "-")
            {
                if (!planIds.Contains(entry.DependsOn))
                {
                    errors.Add(new ValidationError(
                        Code: "DISPATCH_DEP_INVALID",
                        Path: $"DispatchOrder[{entry.PlanId}].DependsOn",
                        Message: $"DispatchEntry.DependsOn references Plan '{entry.DependsOn}' which does not exist."));
                }
            }

            // Warn if Effort is not standard (S, M, L)
            if (entry.Effort != "S" && entry.Effort != "M" && entry.Effort != "L")
            {
                warnings.Add(new ValidationWarning(
                    Code: "EFFORT_NONSTANDARD",
                    Path: $"DispatchOrder[{entry.PlanId}].Effort",
                    Message: $"Effort value '{entry.Effort}' is non-standard. Standard values are S, M, L."));
            }
        }
    }

    private static void ValidatePlans(OpDoc opDoc, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        int? prevPlanLastBriefNumber = null;
        string? prevPlanId = null;

        foreach (var plan in opDoc.Plans)
        {
            ValidatePlanGoal(plan, errors);
            ValidateForbiddenSectionsInPlan(plan, errors);
            ValidateBriefNumbering(plan, errors);
            ValidateBriefs(plan, errors, warnings);
            ValidateCrossPlanContinuity(plan, prevPlanLastBriefNumber, prevPlanId, warnings);

            if (plan.Briefs.Count > 0)
            {
                prevPlanLastBriefNumber = plan.Briefs.Max(b => b.Number);
                prevPlanId = plan.Id;
            }
        }
    }

    private static void ValidatePlanGoal(Plan plan, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(plan.Goal))
        {
            errors.Add(new ValidationError(
                Code: "PLAN_GOAL_EMPTY",
                Path: $"Plans[{plan.Id}].Goal",
                Message: "Plan Goal must be non-empty."));
        }
        else
        {
            ValidateForbiddenSectionTokens(plan.Goal, $"Plans[{plan.Id}].Goal", errors);
        }
    }

    private static void ValidateForbiddenSectionsInPlan(Plan plan, List<ValidationError> errors)
    {
        // Already checked in ValidatePlanGoal, but could check other plan-level fields if needed
    }

    private static void ValidateBriefNumbering(Plan plan, List<ValidationError> errors)
    {
        var sortedBriefs = plan.Briefs.OrderBy(b => b.Number).ToList();

        // Briefs within a plan must be consecutive (no gaps), but may start at any number.
        // This allows globally-sequential numbering across plans (e.g. Plan A: 01-04, Plan B: 05-07).
        for (int i = 1; i < sortedBriefs.Count; i++)
        {
            int prev = sortedBriefs[i - 1].Number;
            int curr = sortedBriefs[i].Number;
            if (curr != prev + 1)
            {
                int expectedNumber = prev + 1;
                errors.Add(new ValidationError(
                    Code: "BRIEF_NUMBER_GAP",
                    Path: $"Plans[{plan.Id}].Briefs[{expectedNumber:D2}]",
                    Message: $"Brief numbering has a gap. Expected {expectedNumber:D2} but found {curr:D2}."));
            }
        }

        // Check for duplicates
        var numberCounts = new Dictionary<int, int>();
        foreach (var brief in plan.Briefs)
        {
            if (numberCounts.ContainsKey(brief.Number))
                numberCounts[brief.Number]++;
            else
                numberCounts[brief.Number] = 1;
        }

        foreach (var kvp in numberCounts)
        {
            if (kvp.Value > 1)
            {
                errors.Add(new ValidationError(
                    Code: "BRIEF_NUMBER_DUPLICATE",
                    Path: $"Plans[{plan.Id}].Briefs[{kvp.Key:D2}]",
                    Message: $"Brief number {kvp.Key:D2} appears {kvp.Value} times. Brief numbers must be unique within a Plan."));
            }
        }
    }

    private static void ValidateCrossPlanContinuity(Plan plan, int? prevPlanLastBriefNumber, string? prevPlanId, List<ValidationWarning> warnings)
    {
        if (prevPlanLastBriefNumber is null || plan.Briefs.Count == 0)
            return;

        int firstBriefNumber = plan.Briefs.Min(b => b.Number);
        int expectedFirst = prevPlanLastBriefNumber.Value + 1;

        if (firstBriefNumber != expectedFirst)
        {
            warnings.Add(new ValidationWarning(
                Code: "BRIEF_PLAN_GAP",
                Path: $"Plans[{plan.Id}].Briefs[{firstBriefNumber:D2}]",
                Message: $"Plan {plan.Id} starts at brief {firstBriefNumber:D2} but Plan {prevPlanId} ended at {prevPlanLastBriefNumber.Value:D2}. Expected {expectedFirst:D2}."));
        }
    }

    private static void ValidateBriefs(Plan plan, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        foreach (var brief in plan.Briefs)
        {
            ValidateBriefSlug(brief, plan, errors);
            ValidateBriefTitle(brief, plan, errors);
            ValidateBriefGoal(brief, plan, errors);
            ValidateBriefOutOfScope(brief, plan, errors, warnings);
            ValidateBriefAcceptanceCriteria(brief, plan, errors, warnings);
            ValidateBriefNotes(brief, plan, errors, warnings);
        }
    }

    private static void ValidateBriefSlug(Brief brief, Plan plan, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(brief.Slug))
        {
            errors.Add(new ValidationError(
                Code: "SLUG_EMPTY",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Slug",
                Message: "Brief Slug must be non-empty."));
        }
    }

    private static void ValidateBriefTitle(Brief brief, Plan plan, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(brief.Title))
        {
            errors.Add(new ValidationError(
                Code: "TITLE_EMPTY",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Title",
                Message: "Brief Title must be non-empty."));
        }
    }

    private static void ValidateBriefGoal(Brief brief, Plan plan, List<ValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(brief.Goal))
        {
            errors.Add(new ValidationError(
                Code: "GOAL_EMPTY",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Goal",
                Message: "Brief Goal must be non-empty."));
        }
        else
        {
            ValidateForbiddenSectionTokens(brief.Goal, $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Goal", errors);
        }
    }

    private static void ValidateBriefOutOfScope(Brief brief, Plan plan, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        if (brief.OutOfScope == null || brief.OutOfScope.Count == 0)
        {
            errors.Add(new ValidationError(
                Code: "OOS_MISSING",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].OutOfScope",
                Message: "Brief OutOfScope list must not be empty."));
        }
        else if (brief.OutOfScope.Count < 3)
        {
            warnings.Add(new ValidationWarning(
                Code: "OOS_LIGHT",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].OutOfScope",
                Message: $"Brief OutOfScope list has only {brief.OutOfScope.Count} entries. Consider adding at least 3 items for adequate coverage."));
        }
    }

    private static void ValidateBriefAcceptanceCriteria(Brief brief, Plan plan, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        if (brief.AcceptanceCriteria == null || brief.AcceptanceCriteria.Count == 0)
        {
            errors.Add(new ValidationError(
                Code: "ACCEPTANCE_MISSING",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].AcceptanceCriteria",
                Message: "Brief must have at least one acceptance criterion."));
        }
        else
        {
            // Check for HOW-language in acceptance criteria
            foreach (var criterion in brief.AcceptanceCriteria)
            {
                if (ContainsHowLanguage(criterion))
                {
                    warnings.Add(new ValidationWarning(
                        Code: "ACCEPTANCE_HOW",
                        Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].AcceptanceCriteria",
                        Message: $"Acceptance criterion may contain implementation details (HOW language): '{criterion}'. Consider rephrasing as WHAT instead of HOW."));
                    break; // Report once per brief
                }
            }
        }
    }

    private static void ValidateBriefNotes(Brief brief, Plan plan, List<ValidationError> errors, List<ValidationWarning> warnings)
    {
        if (string.IsNullOrWhiteSpace(brief.Notes))
        {
            warnings.Add(new ValidationWarning(
                Code: "NOTES_EMPTY",
                Path: $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Notes",
                Message: "Brief Notes section is empty. Consider adding context or caveats."));
        }
        else
        {
            ValidateForbiddenSectionTokens(brief.Notes, $"Plans[{plan.Id}].Briefs[{brief.Number:D2}].Notes", errors);
        }
    }

    private static void ValidateForbiddenSectionTokens(string content, string path, List<ValidationError> errors)
    {
        var forbiddenTokens = new[] { "Risks", "Future", "Verification" };

        foreach (var token in forbiddenTokens)
        {
            // Check for line-start patterns (after newline or at beginning)
            var patterns = new[]
            {
                $"^{token}:",
                $"\n{token}:",
                $"{token} -"
            };

            foreach (var pattern in patterns)
            {
                if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    errors.Add(new ValidationError(
                        Code: "FORBIDDEN_SECTION",
                        Path: path,
                        Message: $"Forbidden section '{token}' detected. The Op-Doc format does not allow 'Risks', 'Future', or 'Verification' sections."));
                    return;
                }
            }
        }
    }

    private static bool ContainsHowLanguage(string text)
    {
        var howPhrases = new[] { "implement", "create file", "write code" };
        var lowerText = text.ToLowerInvariant();

        foreach (var phrase in howPhrases)
        {
            if (lowerText.Contains(phrase))
            {
                return true;
            }
        }

        return false;
    }
}
