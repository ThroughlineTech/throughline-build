using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Best-effort pre-seeding of Claude Code's workspace-trust state so an interactive
/// launch on a fresh (untrusted) worktree does not block at the trust dialog.
/// <c>--dangerously-skip-permissions</c> / <c>--permission-mode bypassPermissions</c>
/// do NOT bypass that dialog (trust is skipped only in <c>-p</c>/print mode), so the
/// interactive transport marks the worktree trusted in <c>~/.claude.json</c> before
/// launching. See docs/heartbeat/evidence/stage-06-process-hardening.md.
///
/// The read-modify-write is non-destructive: it parses the operator-owned file with
/// JsonNode (AOT-safe and reflection-free for arbitrary JSON), preserves every other
/// key, and writes back atomically (temp file + move) so a crash cannot corrupt the
/// real ~/.claude.json. Everything is wrapped in try/catch and swallowed - if trust
/// seeding fails the launch may hang and the existing timeout surfaces an actionable
/// failure; this never throws out of the transport.
/// </summary>
internal static class ClaudeWorkspaceTrust
{
    // Best-known claude 2.1.x trust keys. A live experiment (stage 06 evidence) pinned
    // that the trust dialog gate lives in projects[<path>] in ~/.claude.json; these are
    // the flags observed once a project has been accepted. If a future claude release
    // renames them the launch falls back to the timeout (no behavior regression).
    private const string TrustAcceptedKey = "hasTrustDialogAccepted";
    private const string OnboardingKey = "hasCompletedProjectOnboarding";

    public static void TryEnsureTrusted(string workingDirectory)
    {
        try
        {
            EnsureTrusted(ResolveConfigFilePath(), workingDirectory);
        }
        catch
        {
            // Trust seeding is best-effort; a failure must never break the launch.
        }
    }

    /// <summary>
    /// Resolves the path to <c>.claude.json</c>: <c>&lt;CLAUDE_CONFIG_DIR&gt;/.claude.json</c>
    /// when that env var is set, else <c>$HOME/.claude.json</c> (USERPROFILE on Windows).
    /// </summary>
    internal static string ResolveConfigFilePath()
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
            return Path.Combine(configDir, ".claude.json");
        var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrWhiteSpace(home))
            home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude.json");
    }

    // Test seam: drive the read-modify-write against an explicit config path so unit
    // tests never read or write the real user profile.
    internal static void EnsureTrusted(string configFilePath, string workingDirectory)
    {
        var worktreePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

        JsonObject root;
        if (File.Exists(configFilePath))
        {
            var existing = File.ReadAllText(configFilePath);
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(existing) ? "{}" : existing) as JsonObject
                ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
        }

        if (projects[worktreePath] is not JsonObject project)
        {
            project = new JsonObject();
            projects[worktreePath] = project;
        }

        project[TrustAcceptedKey] = true;
        project[OnboardingKey] = true;

        WriteAtomic(configFilePath, root);
    }

    private static void WriteAtomic(string configFilePath, JsonObject root)
    {
        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var tempPath = configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(tempPath, configFilePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
