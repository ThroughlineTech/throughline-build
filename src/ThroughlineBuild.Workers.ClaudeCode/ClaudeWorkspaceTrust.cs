using System.Security.Cryptography;
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
/// launching. This behavior is covered by the cross-platform interactive transport tests.
///
/// The read-modify-write is non-destructive AND concurrency-safe:
/// - It parses the operator-owned file with JsonNode (AOT-safe and reflection-free
///   for arbitrary JSON) and preserves every other key.
/// - Throughline Build writers are SERIALIZED by a cross-process lock keyed on the config
///   path, so two concurrent runs in different worktrees never interleave their
///   read-modify-write and lose each other's trust entry. If the lock cannot be
///   acquired within the bound, seeding is SKIPPED (never performed unserialized).
/// - An UNCOORDINATED external writer (claude itself rewrites ~/.claude.json) is
///   handled with optimistic concurrency: the version (mtime + length) is captured at
///   read and re-checked immediately before the atomic replace; a detected change
///   triggers a bounded re-read/re-apply so claude's update is preserved rather than
///   clobbered. The replacement is NEVER forced - if contention persists past the
///   bound, or an existing config cannot be read, seeding is ABANDONED rather than
///   risk overwriting claude's concurrent update (the launch's trust-dialog timeout
///   then surfaces the missed seed as an actionable, non-destructive failure).
/// - The replacement is durable (flushed to disk before the rename) and never widens
///   permissions: the temp is made owner-only on Unix (the file can hold the
///   operator's account/credential state) and the destination's ACL is preserved on
///   Windows via File.Replace.
/// Everything is wrapped in try/catch and swallowed - if trust seeding fails the
/// launch may hang and the existing timeout surfaces an actionable failure; this never
/// throws out of the transport.
/// </summary>
internal static class ClaudeWorkspaceTrust
{
    // claude 2.1.177 trust keys, confirmed by live runs: seeding hasTrustDialogAccepted
    // alone suppresses the trust dialog. The onboarding field is the integer
    // projectOnboardingSeenCount (there is no hasCompletedProjectOnboarding key). If a
    // future claude release renames them the launch falls back to the timeout (no
    // behavior regression).
    private const string TrustAcceptedKey = "hasTrustDialogAccepted";
    private const string OnboardingSeenCountKey = "projectOnboardingSeenCount";

    // Bounded optimistic-concurrency retries against an external (claude) writer. Each
    // attempt re-reads the latest file; the final attempt writes unconditionally on top
    // of the freshest read. Real contention clears in one retry.
    private const int MaxWriteAttempts = 5;

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
    internal static void EnsureTrusted(string configFilePath, string workingDirectory) =>
        EnsureTrusted(configFilePath, workingDirectory, externalMutationForTest: null);

    // Test seam overload: externalMutationForTest fires once, after the first read and
    // before the first write, to simulate claude racing the read-modify-write so the
    // detect/retry path is exercised deterministically. Null in production.
    internal static void EnsureTrusted(string configFilePath, string workingDirectory, Action<int, string>? externalMutationForTest)
    {
        var worktreePath = ProjectKeyFor(workingDirectory);

        // Serialize Throughline Build writers: hold a cross-process lock for the whole
        // read-modify-write so two of our runs cannot interleave and drop one another's
        // trust entry. If the lock cannot be acquired within the bound, SKIP trust seeding
        // entirely rather than perform the unserialized RMW the lock exists to prevent.
        using var guard = TrustWriterLock.TryAcquire(configFilePath);
        if (guard is null)
            return;

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            var snapshot = ReadSnapshot(configFilePath);
            // A null snapshot means an existing config could not be read (claude mid-write,
            // permissions). That is an ABORT, not "absent": never create/overwrite over a
            // file we could not read. Abandon seeding; the trust-dialog timeout surfaces it.
            if (snapshot is not ConfigSnapshot current)
                return;

            // Simulate an uncoordinated external writer racing us (tests only); the test
            // decides per-attempt whether to mutate.
            externalMutationForTest?.Invoke(attempt, configFilePath);

            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(current.Text) ? "{}" : current.Text!) as JsonObject
                ?? new JsonObject();

            if (!ApplyTrust(root, worktreePath))
                return; // already trusted; do not rewrite (idempotent, zero clobber risk)

            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (TryWriteIfUnchanged(configFilePath, json, current))
                return;

            // The file changed under us between read and write (claude wrote): re-read and
            // re-apply our key on top of the external change rather than overwriting it.
        }

        // Bounded contention exhausted with the file STILL changing under us. Abandon trust
        // seeding - we NEVER force the replacement, because overwriting claude's concurrent
        // update is worse than a missed seed (which the interactive launch's trust-dialog
        // timeout surfaces as an actionable, non-destructive failure).
    }

    // Claude 2.1.177 records Windows project keys with forward slashes even though
    // its transcript cwd uses the normal Windows form. JSON property lookup is exact,
    // so a backslash key looks trusted to us but is ignored by Claude, which then waits
    // at the interactive trust prompt. Unix keys already use forward slashes.
    internal static string ProjectKeyFor(string workingDirectory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        return OperatingSystem.IsWindows() ? fullPath.Replace('\\', '/') : fullPath;
    }

    // Adds the trust flags to the worktree's project entry. Returns true when the
    // document was actually changed; false when the worktree is already trusted and the
    // onboarding count is present, so the caller can skip the rewrite entirely.
    private static bool ApplyTrust(JsonObject root, string worktreePath)
    {
        var changed = false;

        if (root["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            root["projects"] = projects;
            changed = true;
        }

        if (projects[worktreePath] is not JsonObject project)
        {
            project = new JsonObject();
            projects[worktreePath] = project;
            changed = true;
        }

        if (project[TrustAcceptedKey] is not JsonValue trustValue
            || !trustValue.TryGetValue<bool>(out var trusted) || !trusted)
        {
            project[TrustAcceptedKey] = true;
            changed = true;
        }

        // Seed the onboarding counter only if absent so we never clobber a real count.
        if (project[OnboardingSeenCountKey] is null)
        {
            project[OnboardingSeenCountKey] = JsonValue.Create(1);
            changed = true;
        }

        return changed;
    }

    private readonly record struct ConfigSnapshot(string? Text, DateTime LastWriteUtc, long Length, bool Exists);

    // Returns the snapshot, or null to ABORT. A genuinely absent file (File.Exists false)
    // yields an Exists=false snapshot so we create it. But a read FAILURE on a file we
    // could not classify as absent (an exception from stat/read - e.g. claude mid-write or
    // a permissions hiccup) returns null: the caller must NOT treat that as "absent" and
    // create/overwrite over a file that exists but is momentarily unreadable.
    private static ConfigSnapshot? ReadSnapshot(string configFilePath)
    {
        try
        {
            if (!File.Exists(configFilePath))
                return new ConfigSnapshot(null, default, 0, false);
            // Stat BEFORE reading so an external write during our read is seen as a
            // version change at write time (a false positive just costs one extra retry).
            var lastWrite = File.GetLastWriteTimeUtc(configFilePath);
            var length = new FileInfo(configFilePath).Length;
            var text = File.ReadAllText(configFilePath);
            return new ConfigSnapshot(text, lastWrite, length, true);
        }
        catch
        {
            return null;
        }
    }

    // Writes only if the file's version still matches the snapshot taken at read time;
    // returns false when an external change is detected so the caller retries. There is NO
    // force path: we never overwrite a config that changed under us between read and write.
    private static bool TryWriteIfUnchanged(string configFilePath, string json, ConfigSnapshot snapshot)
    {
        var (exists, lastWrite, length) = CurrentVersion(configFilePath);
        if (exists != snapshot.Exists || lastWrite != snapshot.LastWriteUtc || length != snapshot.Length)
            return false;
        WriteAtomic(configFilePath, json);
        return true;
    }

    private static (bool Exists, DateTime LastWriteUtc, long Length) CurrentVersion(string configFilePath)
    {
        try
        {
            if (!File.Exists(configFilePath)) return (false, default, 0);
            return (true, File.GetLastWriteTimeUtc(configFilePath), new FileInfo(configFilePath).Length);
        }
        catch
        {
            return (false, default, 0);
        }
    }

    private static void WriteAtomic(string configFilePath, string json)
    {
        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        var tempPath = configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Durable write: flush the bytes to disk BEFORE the rename so a crash or power
            // loss cannot leave a half-written ~/.claude.json (claude's primary config).
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // The config can hold the operator's account/credential state, so the
            // replacement must never widen its permissions.
            ApplyRestrictivePermissions(tempPath, configFilePath);
            ReplacePreservingMetadata(tempPath, configFilePath);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    // Enforces owner-only access on the temp before it replaces the operator's config.
    // Unix: a temp file is born with the process umask (often group/other-readable);
    // strip group/other and keep the existing owner bits (preserve mode, enforce
    // owner-only). Windows: the destination's ACL is preserved by File.Replace and the
    // temp meanwhile inherits the (already owner-restricted) profile-directory ACL, so
    // no explicit work is needed.
    private static void ApplyRestrictivePermissions(string tempPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            if (File.Exists(existingPath))
            {
                var ownerBits = File.GetUnixFileMode(existingPath)
                    & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (ownerBits != UnixFileMode.None) mode = ownerBits;
            }
        }
        catch
        {
            // Fall back to 0600 if the existing mode cannot be read.
        }
        try { File.SetUnixFileMode(tempPath, mode); }
        catch { /* best-effort: never break the launch over a perms tweak */ }
    }

    private static void ReplacePreservingMetadata(string tempPath, string destinationPath)
    {
        // On Windows, File.Replace (ReplaceFile) swaps in the temp's CONTENTS while
        // preserving the destination's security descriptor (ACL) and attributes, so the
        // operator's config keeps its permissions across the update. It needs the
        // destination to already exist and to share a volume with the temp (it does - same
        // directory). For first-time creation, fall back to an atomic overwrite move.
        if (OperatingSystem.IsWindows() && File.Exists(destinationPath))
        {
            File.Replace(tempPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }
        File.Move(tempPath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Cross-process lock that serializes Throughline Build trust writers for one config file.
    /// Modeled on <see cref="InteractiveClaudeWorktreeLock"/>: a temp-keyed
    /// <see cref="FileShare.None"/> handle, so the operator's home directory is never
    /// polluted with a lock file and two spellings of the same config map to one lock.
    /// <see cref="TryAcquire"/> waits a bounded time and returns null if it cannot acquire
    /// - the caller then SKIPS trust seeding rather than run the read-modify-write
    /// unserialized, which would reintroduce the exact interleaving the lock prevents.
    /// </summary>
    private sealed class TrustWriterLock : IDisposable
    {
        private static readonly TimeSpan AcquireTimeout = TimeSpan.FromSeconds(15);

        private FileStream? _stream;

        private TrustWriterLock(FileStream stream) => _stream = stream;

        /// <returns>The held lock, or null when it could not be acquired within the bound
        /// (or the lock path could not be computed). Never returns an unlocked guard.</returns>
        public static TrustWriterLock? TryAcquire(string configFilePath)
        {
            string path;
            try { path = LockPathFor(configFilePath); }
            catch { return null; }

            var deadline = DateTime.UtcNow + AcquireTimeout;
            var delayMs = 10;
            while (true)
            {
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new TrustWriterLock(stream);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }

                if (DateTime.UtcNow >= deadline)
                    return null; // could not serialize within the bound -> caller skips seeding
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 200);
            }
        }

        private static string LockPathFor(string configFilePath)
        {
            var full = Path.GetFullPath(configFilePath);
            var key = OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var directory = Path.Combine(Path.GetTempPath(), "throughline-build-claude-locks");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "trust-" + hash + ".lock");
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
