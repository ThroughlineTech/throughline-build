using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Verification;

// Collects non-gating smoke signals over a GitDiff or a worktree directory tree.
// None of the signals produced here can fail a gate - they inform; they do not block.
public class SmokeCollector
{
    private const string DefaultTestPattern = @"(?i)(test|spec)";

    // Collect diff-fact signal: files touched, add/delete line counts, test-file presence.
    // Always returns Matched=true (facts are always reported, even for an empty diff).
    public SmokeSignal CollectDiffFacts(GitDiff diff, string? testFilePattern = null)
    {
        var testRe = new Regex(
            testFilePattern ?? DefaultTestPattern,
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        int total = diff.Entries.Count;
        int added = diff.Entries.Count(e => e.Kind == DiffKind.Added);
        int modified = diff.Entries.Count(e => e.Kind == DiffKind.Modified);
        int deleted = diff.Entries.Count(e => e.Kind == DiffKind.Deleted);
        int linesAdded = diff.Entries.Sum(e => e.LinesAdded);
        int linesRemoved = diff.Entries.Sum(e => e.LinesRemoved);

        var testFiles = diff.Entries.Where(e => testRe.IsMatch(e.Path)).Select(e => e.Path).ToList();

        var sb = new StringBuilder();
        sb.Append(total.ToString(CultureInfo.InvariantCulture));
        sb.Append(" file(s)");
        if (total > 0)
        {
            sb.Append(": +");
            sb.Append(added.ToString(CultureInfo.InvariantCulture));
            sb.Append(" added, ");
            sb.Append(modified.ToString(CultureInfo.InvariantCulture));
            sb.Append(" modified, ");
            sb.Append(deleted.ToString(CultureInfo.InvariantCulture));
            sb.Append(" deleted");
        }
        sb.Append(". Lines +");
        sb.Append(linesAdded.ToString(CultureInfo.InvariantCulture));
        sb.Append("/-");
        sb.Append(linesRemoved.ToString(CultureInfo.InvariantCulture));
        sb.Append('.');

        if (testFiles.Count > 0)
        {
            sb.Append(" Test files present: ");
            sb.Append(string.Join(", ", testFiles));
            sb.Append('.');
        }
        else
        {
            sb.Append(" No test files in diff.");
        }

        return new SmokeSignal("diff-facts", SmokeSignalKind.DiffFacts, true, sb.ToString());
    }

    // Search each diff entry's patch content for pattern.
    // Matched=true: pattern found (expected wiring or marker is present).
    // Matched=false: pattern not found (suspicious - expected thing may be absent).
    public SmokeSignal GrepPresentInDiff(string label, string pattern, GitDiff diff)
    {
        var (found, details) = SearchDiff(pattern, diff);
        return new SmokeSignal(label, SmokeSignalKind.GrepPresent, found, details);
    }

    // Search each diff entry's patch content for pattern and expect it to be absent.
    // Matched=true: pattern absent from diff (clean - forbidden marker not present).
    // Matched=false: pattern found (suspicious - something that should be absent remains).
    public SmokeSignal GrepAbsentInDiff(string label, string pattern, GitDiff diff)
    {
        var (found, foundDetails) = SearchDiff(pattern, diff);
        if (found)
            return new SmokeSignal(label, SmokeSignalKind.GrepAbsent, false, foundDetails);

        return new SmokeSignal(
            label,
            SmokeSignalKind.GrepAbsent,
            true,
            $"Pattern \"{pattern}\" absent from diff (clean).");
    }

    // Search all matching files in the worktree directory tree for pattern.
    // fileGlob follows the same syntax as Directory.EnumerateFiles (e.g. "*.cs", "*.ts").
    // Hidden directories (names starting with '.') are skipped.
    // Matched=true: pattern found.
    // Matched=false: pattern not found.
    public SmokeSignal GrepPresentInTree(
        string label,
        string pattern,
        string worktreePath,
        string fileGlob = "*.cs")
    {
        var (found, details) = SearchTree(pattern, worktreePath, fileGlob);
        return new SmokeSignal(label, SmokeSignalKind.GrepPresent, found, details);
    }

    // Search all matching files in the worktree directory tree for pattern and expect it absent.
    // Matched=true: pattern absent (clean).
    // Matched=false: pattern found (suspicious).
    public SmokeSignal GrepAbsentInTree(
        string label,
        string pattern,
        string worktreePath,
        string fileGlob = "*.cs")
    {
        var (found, foundDetails) = SearchTree(pattern, worktreePath, fileGlob);
        if (found)
            return new SmokeSignal(label, SmokeSignalKind.GrepAbsent, false, foundDetails);

        return new SmokeSignal(
            label,
            SmokeSignalKind.GrepAbsent,
            true,
            $"Pattern \"{pattern}\" absent from tree (clean).");
    }

    private static (bool Found, string Details) SearchDiff(string pattern, GitDiff diff)
    {
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return (false, $"Pattern \"{pattern}\" is invalid: {ex.Message}");
        }

        int matchCount = 0;
        int filesWithMatch = 0;
        int entriesWithoutPatch = 0;
        var samples = new List<string>();

        foreach (var entry in diff.Entries)
        {
            if (entry.PatchContent is null)
            {
                entriesWithoutPatch++;
                continue;
            }

            bool fileMatched = false;
            foreach (var line in entry.PatchContent.Split('\n'))
            {
                if (!re.IsMatch(line)) continue;
                matchCount++;
                if (samples.Count < 5)
                    samples.Add($"{entry.Path}: {line.TrimStart('+', '-', ' ').Trim()}");
                fileMatched = true;
            }
            if (fileMatched) filesWithMatch++;
        }

        var sb = new StringBuilder();
        if (matchCount > 0)
        {
            sb.Append("Pattern \"");
            sb.Append(pattern);
            sb.Append("\": ");
            sb.Append(matchCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(" match(es) in ");
            sb.Append(filesWithMatch.ToString(CultureInfo.InvariantCulture));
            sb.Append(" file(s).");
            if (samples.Count > 0)
            {
                sb.Append(" Samples: ");
                sb.Append(string.Join("; ", samples.Take(3)));
                sb.Append('.');
            }
        }
        else
        {
            sb.Append("Pattern \"");
            sb.Append(pattern);
            sb.Append("\": not found in diff.");
        }

        if (entriesWithoutPatch > 0)
        {
            sb.Append(' ');
            sb.Append('(');
            sb.Append(entriesWithoutPatch.ToString(CultureInfo.InvariantCulture));
            sb.Append(" entr");
            sb.Append(entriesWithoutPatch == 1 ? "y" : "ies");
            sb.Append(" had no patch content.)");
        }

        return (matchCount > 0, sb.ToString());
    }

    private static (bool Found, string Details) SearchTree(string pattern, string worktreePath, string fileGlob)
    {
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return (false, $"Pattern \"{pattern}\" is invalid: {ex.Message}");
        }

        if (!Directory.Exists(worktreePath))
            return (false, $"Worktree path not found: {worktreePath}");

        int matchCount = 0;
        int filesWithMatch = 0;
        var samples = new List<string>();

        try
        {
            var sep = Path.DirectorySeparatorChar;
            foreach (var filePath in Directory.EnumerateFiles(worktreePath, fileGlob, SearchOption.AllDirectories))
            {
                // Skip hidden directories (e.g. .git)
                var rel = Path.GetRelativePath(worktreePath, filePath);
                var segments = rel.Split(new char[] { sep, '/' }, StringSplitOptions.RemoveEmptyEntries);
                bool inHidden = false;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    if (segments[i].Length > 0 && segments[i][0] == '.')
                    {
                        inHidden = true;
                        break;
                    }
                }
                if (inHidden) continue;

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(filePath);
                }
                catch
                {
                    continue;
                }

                bool fileMatched = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!re.IsMatch(lines[i])) continue;
                    matchCount++;
                    if (samples.Count < 5)
                        samples.Add($"{rel}:{(i + 1).ToString(CultureInfo.InvariantCulture)}: {lines[i].Trim()}");
                    fileMatched = true;
                }
                if (fileMatched) filesWithMatch++;
            }
        }
        catch (Exception ex)
        {
            return (false, $"Tree search error: {ex.Message}");
        }

        var sb = new StringBuilder();
        if (matchCount > 0)
        {
            sb.Append("Pattern \"");
            sb.Append(pattern);
            sb.Append("\": ");
            sb.Append(matchCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(" match(es) in ");
            sb.Append(filesWithMatch.ToString(CultureInfo.InvariantCulture));
            sb.Append(" file(s).");
            if (samples.Count > 0)
            {
                sb.Append(" Samples: ");
                sb.Append(string.Join("; ", samples.Take(3)));
                sb.Append('.');
            }
        }
        else
        {
            sb.Append("Pattern \"");
            sb.Append(pattern);
            sb.Append("\": not found in tree.");
        }

        return (matchCount > 0, sb.ToString());
    }
}
