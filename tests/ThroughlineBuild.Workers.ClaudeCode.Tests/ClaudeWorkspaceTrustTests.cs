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
        var other = projects.GetProperty(NormalizeKey(otherProject));
        Assert.True(other.GetProperty("hasTrustDialogAccepted").GetBoolean());
        Assert.Equal(42, other.GetProperty("exampleSetting").GetInt32());

        // The target worktree is now trusted.
        var entry = projects.GetProperty(NormalizeKey(worktree));
        Assert.True(entry.GetProperty("hasTrustDialogAccepted").GetBoolean());
        Assert.True(entry.GetProperty("hasCompletedProjectOnboarding").GetBoolean());
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

    private static string NormalizeKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Json(string value) => JsonSerializer.Serialize(value);

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }
}
