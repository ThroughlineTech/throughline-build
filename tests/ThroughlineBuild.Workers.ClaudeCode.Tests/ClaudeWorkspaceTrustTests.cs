using System.Text.Json;
using ThroughlineBuild.Workers.ClaudeCode;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public sealed class ClaudeWorkspaceTrustTests : IDisposable
{
    // Hermetic config path so the test never touches the real ~/.claude.json.
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"lattice-trust-{Guid.NewGuid():N}");

    [Fact]
    public void EnsureTrusted_CreatesTrustFlagsAndPreservesUnrelatedContent()
    {
        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "fresh worktree");
        Directory.CreateDirectory(worktree);

        // Pre-existing file with an unrelated top-level key and an unrelated project.
        var otherProject = Path.Combine(_home, "some other project");
        File.WriteAllText(configPath, $$"""
            {
              "numStartups": 7,
              "oauthAccount": { "emailAddress": "dan@example.com" },
              "projects": {
                {{Json(otherProject)}}: { "hasTrustDialogAccepted": true, "exampleSetting": 42 }
              }
            }
            """);

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;

        // Unrelated top-level content preserved.
        Assert.Equal(7, root.GetProperty("numStartups").GetInt32());
        Assert.Equal("dan@example.com", root.GetProperty("oauthAccount").GetProperty("emailAddress").GetString());

        var projects = root.GetProperty("projects");
        // Unrelated project entry preserved untouched.
        var other = projects.GetProperty(otherProject);
        Assert.True(other.GetProperty("hasTrustDialogAccepted").GetBoolean());
        Assert.Equal(42, other.GetProperty("exampleSetting").GetInt32());

        // The target worktree is now trusted.
        var entry = projects.GetProperty(NormalizeKey(worktree));
        Assert.True(entry.GetProperty("hasTrustDialogAccepted").GetBoolean());
        // The onboarding field is the integer projectOnboardingSeenCount, seeded to 1.
        var onboarding = entry.GetProperty("projectOnboardingSeenCount");
        Assert.Equal(JsonValueKind.Number, onboarding.ValueKind);
        Assert.Equal(1, onboarding.GetInt32());
        // hasCompletedProjectOnboarding is not a real key and must not be written.
        Assert.False(entry.TryGetProperty("hasCompletedProjectOnboarding", out _));
    }

    [Fact]
    public void EnsureTrusted_PreservesExistingOnboardingCount()
    {
        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);

        // The project already carries a real onboarding count; seeding must not clobber it.
        File.WriteAllText(configPath, $$"""
            {
              "projects": {
                {{Json(NormalizeKey(worktree))}}: { "projectOnboardingSeenCount": 5 }
              }
            }
            """);

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var entry = document.RootElement.GetProperty("projects").GetProperty(NormalizeKey(worktree));
        Assert.True(entry.GetProperty("hasTrustDialogAccepted").GetBoolean());
        Assert.Equal(5, entry.GetProperty("projectOnboardingSeenCount").GetInt32());
        Assert.False(entry.TryGetProperty("hasCompletedProjectOnboarding", out _));
    }

    [Fact]
    public void EnsureTrusted_MissingFile_CreatesItWithTrustedProject()
    {
        var configPath = Path.Combine(_home, "nested", ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);

        Assert.True(File.Exists(configPath));
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var entry = document.RootElement.GetProperty("projects").GetProperty(NormalizeKey(worktree));
        Assert.True(entry.GetProperty("hasTrustDialogAccepted").GetBoolean());
    }

    [Fact]
    public void EnsureTrusted_SecondCall_IsNoOpSafeAndIdempotent()
    {
        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);
        var first = File.ReadAllText(configPath);
        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);
        var second = File.ReadAllText(configPath);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(second);
        var entry = document.RootElement.GetProperty("projects").GetProperty(NormalizeKey(worktree));
        Assert.True(entry.GetProperty("hasTrustDialogAccepted").GetBoolean());
    }

    [Fact]
    public void TryEnsureTrusted_NeverThrows_OnUnwritableConfigDir()
    {
        // CLAUDE_CONFIG_DIR points at a path that cannot be a directory (a regular
        // file sits where the directory would be), so the write fails - and is swallowed.
        Directory.CreateDirectory(_home);
        var blocker = Path.Combine(_home, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(blocker, "child"));
            ClaudeWorkspaceTrust.TryEnsureTrusted(Path.Combine(_home, "worktree"));
            // No exception is the assertion.
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        }
    }

    [Fact]
    public async Task EnsureTrusted_ConcurrentWritersForDifferentWorktrees_AllEntriesSurvive()
    {
        // Finding 3: concurrent Latticeflow runs in different worktrees both
        // read-modify-write the SAME global ~/.claude.json. Without serialization their
        // writes clobber each other and entries are lost. The cross-process writer lock
        // must serialize them so EVERY worktree ends up trusted.
        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        File.WriteAllText(configPath, """{ "numStartups": 1 }""");

        const int writers = 16;
        var worktrees = Enumerable.Range(0, writers)
            .Select(i => Path.Combine(_home, $"worktree-{i}"))
            .ToArray();
        foreach (var worktree in worktrees) Directory.CreateDirectory(worktree);

        await Task.WhenAll(worktrees.Select(worktree =>
            Task.Run(() => ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree))));

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var projects = document.RootElement.GetProperty("projects");
        // Unrelated content survived...
        Assert.Equal(1, document.RootElement.GetProperty("numStartups").GetInt32());
        // ...and every concurrent worktree is present and trusted (none lost to a race).
        foreach (var worktree in worktrees)
            Assert.True(projects.GetProperty(NormalizeKey(worktree)).GetProperty("hasTrustDialogAccepted").GetBoolean());
    }

    [Fact]
    public void EnsureTrusted_ExternalChangeBetweenReadAndWrite_IsDetectedAndPreserved()
    {
        // Finding 3: claude is an UNCOORDINATED external writer. If it rewrites the config
        // between our read and our write, the optimistic version check must detect the
        // change and re-read/re-apply on top of it - preserving claude's update rather
        // than clobbering it, while still landing our trust entry.
        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(configPath, """{ "numStartups": 1 }""");

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree, configFile =>
        {
            // Simulate claude writing the file AFTER we read but BEFORE we write.
            File.WriteAllText(configFile, """{ "numStartups": 2, "externalKey": "claude wrote this" }""");
        });

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        // The external writer's change survived (we re-read and re-applied on top of it).
        Assert.Equal(2, root.GetProperty("numStartups").GetInt32());
        Assert.Equal("claude wrote this", root.GetProperty("externalKey").GetString());
        // And our trust entry is present.
        Assert.True(root.GetProperty("projects").GetProperty(NormalizeKey(worktree))
            .GetProperty("hasTrustDialogAccepted").GetBoolean());
    }

    [Fact]
    public void EnsureTrusted_OnUnix_WritesOwnerOnlyConfig()
    {
        // Finding 4: the config can hold the operator's account/credential state, so a
        // newly created file must be owner-only (no group/other access). Unix-only:
        // Windows protects the file through the profile-directory ACL instead.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);

        var mode = File.GetUnixFileMode(configPath);
        var groupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal(UnixFileMode.None, mode & groupOther);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead) && mode.HasFlag(UnixFileMode.UserWrite));
    }

    [Fact]
    public void EnsureTrusted_OnUnix_TightensGroupOtherReadableConfig()
    {
        // Finding 4: an existing config that is group/other-readable must be tightened to
        // owner-only across the atomic replace, never left widened.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_home);
        var configPath = Path.Combine(_home, ".claude.json");
        var worktree = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(configPath, """{ "numStartups": 1 }""");
        File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0644

        ClaudeWorkspaceTrust.EnsureTrusted(configPath, worktree);

        var mode = File.GetUnixFileMode(configPath);
        var groupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal(UnixFileMode.None, mode & groupOther);
    }

    [Fact]
    public void ProjectKeyFor_UsesClaudeProjectPathForm()
    {
        var path = Path.Combine(_home, "worktree");
        var key = ClaudeWorkspaceTrust.ProjectKeyFor(path);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/'), key);
    }

    private static string NormalizeKey(string path) => ClaudeWorkspaceTrust.ProjectKeyFor(path);

    private static string Json(string value) => JsonSerializer.Serialize(value);

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }
}
