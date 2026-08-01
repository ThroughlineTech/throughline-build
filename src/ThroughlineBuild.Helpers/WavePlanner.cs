namespace ThroughlineBuild.Helpers;

public enum WaveSerializeKind
{
    Global,
    CohesiveModule,
    Pairwise,
}

public sealed record WaveSerializeRule(
    WaveSerializeKind Kind,
    IReadOnlyList<string> Paths);

public sealed record WaveTicket(
    string Id,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Dependencies,
    bool Uncertain = false);

public sealed record WaveConflictReason(
    string Rule,
    string Path,
    string Pattern,
    string Detail);

public sealed record WaveConflict(
    int Level,
    string A,
    string B,
    IReadOnlyList<WaveConflictReason> Reasons);

public sealed record WaveScheduleEntry(
    int Wave,
    int Level,
    IReadOnlyList<string> Tickets);

public sealed record WaveSpeedup(
    int SerialTicketSlots,
    int ScheduledWaves,
    double EstimatedSpeedup,
    int ParallelTickets,
    int SerialBoundTickets,
    string Verdict);

public sealed record WavePlan(
    int TicketCount,
    int Cap,
    IReadOnlyList<string> VerifiedExternalDeps,
    IReadOnlyList<WaveConflict> Conflicts,
    IReadOnlyList<WaveScheduleEntry> Waves,
    WaveSpeedup Speedup);

public sealed class WaveDependencyCycleException : Exception
{
    public WaveDependencyCycleException(IReadOnlyList<string> ticketIds)
        : base($"dependency cycle involving: {string.Join(", ", ticketIds)}")
    {
        TicketIds = ticketIds;
    }

    public IReadOnlyList<string> TicketIds { get; }
}

public static class WavePlanner
{
    public const int DefaultCap = 2;
    public const int MaximumCap = 16;

    public static WavePlan Plan(
        IReadOnlyList<WaveTicket> tickets,
        int cap,
        IReadOnlyCollection<string> verifiedExternalDeps,
        IReadOnlyList<WaveSerializeRule> rules)
    {
        if (cap is < 1 or > MaximumCap)
            throw new ArgumentException($"cap must be an integer from 1 to {MaximumCap}");

        var normalizedRules = NormalizeRules(rules);
        var normalizedTickets = NormalizeTickets(tickets);
        var verified = NormalizeIds(verifiedExternalDeps, "verifiedExternalDeps");
        var selectedIds = normalizedTickets.Select(ticket => ticket.Id).ToHashSet(StringComparer.Ordinal);
        var external = normalizedTickets
            .SelectMany(ticket => ticket.Dependencies)
            .Where(dependency => !selectedIds.Contains(dependency))
            .Distinct(StringComparer.Ordinal)
            .Order(WaveTicketIdComparer.Instance)
            .ToList();
        var verifiedSet = verified.ToHashSet(StringComparer.Ordinal);
        var unverified = external.Where(dependency => !verifiedSet.Contains(dependency)).ToList();
        if (unverified.Count > 0)
            throw new ArgumentException(
                $"unverified dependencies outside selected scope: {string.Join(", ", unverified)}");

        var levels = DependencyLevels(normalizedTickets);
        var conflicts = new List<WaveConflict>();
        var schedule = new List<WaveScheduleEntry>();
        var waveNumber = 0;

        for (var levelNumber = 0; levelNumber < levels.Count; levelNumber++)
        {
            var level = levels[levelNumber]
                .OrderBy(ticket => ticket.Id, WaveTicketIdComparer.Instance)
                .ToList();
            var localWaves = new List<List<WaveTicket>>();

            foreach (var ticket in level)
            {
                var target = localWaves.FirstOrDefault(wave =>
                    wave.Count < cap
                    && wave.All(member => ConflictReasons(ticket, member, normalizedRules).Count == 0));
                if (target is null)
                {
                    target = new List<WaveTicket>();
                    localWaves.Add(target);
                }
                target.Add(ticket);
            }

            for (var i = 0; i < level.Count; i++)
            {
                for (var j = i + 1; j < level.Count; j++)
                {
                    var reasons = ConflictReasons(level[i], level[j], normalizedRules);
                    if (reasons.Count > 0)
                        conflicts.Add(new WaveConflict(
                            levelNumber, level[i].Id, level[j].Id, reasons));
                }
            }

            foreach (var wave in localWaves)
            {
                waveNumber++;
                schedule.Add(new WaveScheduleEntry(
                    waveNumber,
                    levelNumber,
                    wave.Select(ticket => ticket.Id).ToList()));
            }
        }

        var parallelTickets = schedule.Where(wave => wave.Tickets.Count > 1)
            .Sum(wave => wave.Tickets.Count);
        var estimatedSpeedup = schedule.Count == 0
            ? 1.0
            : Math.Round((double)normalizedTickets.Count / schedule.Count, 2);
        var verdict = normalizedTickets.Count == 0
            ? "no tickets selected"
            : estimatedSpeedup > 1
                ? "parallelism available"
                : "serial schedule";

        return new WavePlan(
            normalizedTickets.Count,
            cap,
            external,
            conflicts,
            schedule,
            new WaveSpeedup(
                normalizedTickets.Count,
                schedule.Count,
                estimatedSpeedup,
                parallelTickets,
                normalizedTickets.Count - parallelTickets,
                verdict));
    }

    private static IReadOnlyList<WaveTicket> NormalizeTickets(IReadOnlyList<WaveTicket> tickets)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<WaveTicket>(tickets.Count);
        foreach (var ticket in tickets)
        {
            if (string.IsNullOrWhiteSpace(ticket.Id))
                throw new ArgumentException("each ticket must contain a non-empty string id");
            var id = ticket.Id.Trim();
            if (!ids.Add(id))
                throw new ArgumentException($"duplicate ticket id: {id}");

            var dependencies = NormalizeIds(ticket.Dependencies, $"{id}.deps");
            if (dependencies.Contains(id, StringComparer.Ordinal))
                throw new WaveDependencyCycleException([id]);
            var files = ticket.Files.Select(NormalizeRepositoryPath).ToList();
            if (files.Count != files.Distinct(StringComparer.Ordinal).Count())
                throw new ArgumentException($"{id}.files contains duplicates");

            output.Add(new WaveTicket(
                id,
                files,
                dependencies,
                ticket.Uncertain || files.Count == 0));
        }
        return output;
    }

    private static IReadOnlyList<string> NormalizeIds(
        IEnumerable<string> values,
        string field)
    {
        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{field} must contain only non-empty strings");
            var normalized = value.Trim();
            if (!seen.Add(normalized))
                throw new ArgumentException($"{field} contains duplicates");
            output.Add(normalized);
        }
        return output;
    }

    private static IReadOnlyList<WaveSerializeRule> NormalizeRules(
        IReadOnlyList<WaveSerializeRule> rules)
    {
        var output = new List<WaveSerializeRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (rule.Paths.Count == 0)
                throw new ArgumentException($"waves serialize rule '{KindName(rule.Kind)}' requires at least one path");
            var paths = rule.Paths.Select(NormalizePathPattern).ToList();
            output.Add(new WaveSerializeRule(rule.Kind, paths));
        }
        return output;
    }

    private static IReadOnlyList<IReadOnlyList<WaveTicket>> DependencyLevels(
        IReadOnlyList<WaveTicket> tickets)
    {
        var byId = tickets.ToDictionary(ticket => ticket.Id, StringComparer.Ordinal);
        var indegree = tickets.ToDictionary(ticket => ticket.Id, _ => 0, StringComparer.Ordinal);
        var children = tickets.ToDictionary(
            ticket => ticket.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var ticket in tickets)
        {
            foreach (var dependency in ticket.Dependencies)
            {
                if (!byId.ContainsKey(dependency))
                    continue;
                indegree[ticket.Id]++;
                children[dependency].Add(ticket.Id);
            }
        }

        var ready = indegree.Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .Order(WaveTicketIdComparer.Instance)
            .ToList();
        var levels = new List<IReadOnlyList<WaveTicket>>();
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready;
            levels.Add(current.Select(id => byId[id]).ToList());
            visited += current.Count;
            var next = new List<string>();
            foreach (var ticketId in current)
            {
                foreach (var child in children[ticketId].Order(WaveTicketIdComparer.Instance))
                {
                    indegree[child]--;
                    if (indegree[child] == 0)
                        next.Add(child);
                }
            }
            ready = next.Order(WaveTicketIdComparer.Instance).ToList();
        }

        if (visited != tickets.Count)
        {
            var cycle = indegree.Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .Order(WaveTicketIdComparer.Instance)
                .ToList();
            throw new WaveDependencyCycleException(cycle);
        }
        return levels;
    }

    private static IReadOnlyList<WaveConflictReason> ConflictReasons(
        WaveTicket left,
        WaveTicket right,
        IReadOnlyList<WaveSerializeRule> rules)
    {
        var reasons = new List<WaveConflictReason>();
        foreach (var shared in left.Files.Intersect(right.Files, StringComparer.Ordinal).Order())
        {
            reasons.Add(new WaveConflictReason(
                "exact-file",
                shared,
                shared,
                $"both tickets predict the exact file '{shared}'"));
        }

        if (left.Uncertain || right.Uncertain)
        {
            var uncertainIds = new[] { left, right }
                .Where(ticket => ticket.Uncertain)
                .Select(ticket => ticket.Id);
            reasons.Add(new WaveConflictReason(
                "uncertain",
                string.Join(", ", uncertainIds),
                string.Empty,
                "uncertain or empty file prediction serializes globally"));
        }

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case WaveSerializeKind.Global:
                    foreach (var match in Matches(left.Files.Concat(right.Files), rule.Paths))
                    {
                        reasons.Add(new WaveConflictReason(
                            "global",
                            match.Path,
                            match.Pattern,
                            $"global rule matched '{match.Path}'"));
                    }
                    break;
                case WaveSerializeKind.CohesiveModule:
                    foreach (var pattern in rule.Paths)
                    {
                        var leftPath = left.Files.FirstOrDefault(path => PatternMatches(pattern, path));
                        var rightPath = right.Files.FirstOrDefault(path => PatternMatches(pattern, path));
                        if (leftPath is not null && rightPath is not null)
                        {
                            reasons.Add(new WaveConflictReason(
                                "cohesive-module",
                                $"{leftPath} | {rightPath}",
                                pattern,
                                $"both tickets touch cohesive module '{pattern}'"));
                        }
                    }
                    break;
                case WaveSerializeKind.Pairwise:
                    var leftMatch = Matches(left.Files, rule.Paths).FirstOrDefault();
                    var rightMatch = Matches(right.Files, rule.Paths).FirstOrDefault();
                    if (leftMatch is not null && rightMatch is not null)
                    {
                        reasons.Add(new WaveConflictReason(
                            "pairwise",
                            $"{leftMatch.Path} | {rightMatch.Path}",
                            $"{leftMatch.Pattern} | {rightMatch.Pattern}",
                            "both tickets touch the configured pairwise surface"));
                    }
                    break;
            }
        }
        return reasons;
    }

    private static IEnumerable<PathMatch> Matches(
        IEnumerable<string> paths,
        IReadOnlyList<string> patterns)
    {
        foreach (var path in paths)
        {
            foreach (var pattern in patterns)
            {
                if (PatternMatches(pattern, path))
                    yield return new PathMatch(path, pattern);
            }
        }
    }

    public static bool PatternMatches(string pattern, string path)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return path.Equals(pattern, StringComparison.Ordinal)
                || path.StartsWith(pattern + "/", StringComparison.Ordinal);

        var patternSegments = pattern.Split('/');
        var pathSegments = path.Split('/');
        return MatchSegments(patternSegments, 0, pathSegments, 0);
    }

    private static bool MatchSegments(
        IReadOnlyList<string> pattern,
        int patternIndex,
        IReadOnlyList<string> path,
        int pathIndex)
    {
        if (patternIndex == pattern.Count)
            return pathIndex == path.Count;
        if (pattern[patternIndex] == "**")
        {
            if (patternIndex + 1 == pattern.Count)
                return true;
            for (var next = pathIndex; next <= path.Count; next++)
            {
                if (MatchSegments(pattern, patternIndex + 1, path, next))
                    return true;
            }
            return false;
        }
        return pathIndex < path.Count
            && MatchSegment(pattern[patternIndex], path[pathIndex])
            && MatchSegments(pattern, patternIndex + 1, path, pathIndex + 1);
    }

    private static bool MatchSegment(string pattern, string value)
    {
        var rows = pattern.Length + 1;
        var columns = value.Length + 1;
        var matches = new bool[rows, columns];
        matches[0, 0] = true;
        for (var i = 1; i < rows; i++)
        {
            if (pattern[i - 1] == '*')
                matches[i, 0] = matches[i - 1, 0];
            for (var j = 1; j < columns; j++)
            {
                matches[i, j] = pattern[i - 1] switch
                {
                    '*' => matches[i - 1, j] || matches[i, j - 1],
                    '?' => matches[i - 1, j - 1],
                    _ => matches[i - 1, j - 1] && pattern[i - 1] == value[j - 1],
                };
            }
        }
        return matches[pattern.Length, value.Length];
    }

    public static string NormalizeRepositoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("paths must be non-empty strings");
        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.StartsWith('/') || HasDrivePrefix(normalized))
            throw new ArgumentException($"paths must be repository-relative: {value}");
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part != ".")
            .ToList();
        if (parts.Count == 0 || parts.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException($"invalid repository-relative path: {value}");
        return string.Join('/', parts);
    }

    public static string NormalizePathPattern(string value)
    {
        var normalized = NormalizeRepositoryPath(value);
        if (normalized.Contains("***", StringComparison.Ordinal))
            throw new ArgumentException($"invalid waves path pattern: {value}");
        return normalized.TrimEnd('/');
    }

    private static bool HasDrivePrefix(string value) =>
        value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';

    private static string KindName(WaveSerializeKind kind) => kind switch
    {
        WaveSerializeKind.Global => "global",
        WaveSerializeKind.CohesiveModule => "cohesive-module",
        WaveSerializeKind.Pairwise => "pairwise",
        _ => kind.ToString(),
    };

    private sealed record PathMatch(string Path, string Pattern);

    private sealed class WaveTicketIdComparer : IComparer<string>
    {
        public static WaveTicketIdComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            var leftNumber = TrailingNumber(left);
            var rightNumber = TrailingNumber(right);
            if (leftNumber.HasValue && rightNumber.HasValue)
            {
                var numeric = leftNumber.Value.CompareTo(rightNumber.Value);
                if (numeric != 0)
                    return numeric;
            }
            else if (leftNumber.HasValue != rightNumber.HasValue)
            {
                return leftNumber.HasValue ? -1 : 1;
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left, right) is var folded && folded != 0
                ? folded
                : StringComparer.Ordinal.Compare(left, right);
        }

        private static long? TrailingNumber(string value)
        {
            var start = value.Length;
            while (start > 0 && char.IsDigit(value[start - 1]))
                start--;
            return start == value.Length || !long.TryParse(value.AsSpan(start), out var number)
                ? null
                : number;
        }
    }
}
