using System.Diagnostics;
using System.Text.Json;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Helpers;

public sealed class WorktreeLeaseManager
{
    public const string CollisionError = "lease_collision";
    public const string MissingSeedError = "missing_seed";
    public const string InvalidManifestError = "invalid_manifest";
    public const string ContainmentError = "containment_refused";
    public const string FailureError = "failure";

    private readonly IGitClient _git;
    private readonly IInstallCommandRunner _installer;
    private readonly WorktreeLeaseOptions _options;
    private readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public WorktreeLeaseManager(
        IGitClient git,
        IInstallCommandRunner installer,
        WorktreeLeaseOptions options)
    {
        _git = git;
        _installer = installer;
        _options = options;
    }

    public async Task<WorktreeLeaseResult> LeaseAsync(
        WorktreeLeaseRequest request,
        CancellationToken ct)
    {
        var ticket = NormalizeTicket(request.Ticket);
        if (ticket.Length == 0)
            return Failed(FailureError, "ticket must contain at least one letter or digit");

        var slug = NormalizeSlug(request.Slug);
        var suffix = SlugBuilder.BuildBranchSlug(ticket, slug);
        var branch = $"lease/{suffix}";
        var root = Path.GetFullPath(_options.WorktreeRoot);
        var target = Path.GetFullPath(Path.Combine(root, suffix));

        if (!IsStrictlyBelow(target, root))
            return Failed(ContainmentError, $"worktree path is not strictly below configured root: {target}");

        FileStream leaseLock;
        try
        {
            Directory.CreateDirectory(root);
            var lockPath = Path.Combine(root, $".ticket-{ticket.ToLowerInvariant()}.lock");
            leaseLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return Failed(CollisionError, $"another lease attempt is active for ticket: {ticket}");
        }
        catch (Exception ex)
        {
            return Failed(FailureError, $"could not acquire lease lock: {ex.Message}");
        }

        using (leaseLock)
        {
            return await LeaseUnderLockAsync(
                request,
                ticket,
                slug,
                branch,
                root,
                target,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<WorktreeLeaseResult> LeaseUnderLockAsync(
        WorktreeLeaseRequest request,
        string ticket,
        string slug,
        string branch,
        string root,
        string target,
        CancellationToken ct)
    {
        List<WorktreeLeaseManifest> existing;
        try
        {
            existing = ReadLeaseManifests(root, rejectInvalid: true);
        }
        catch (InvalidDataException ex)
        {
            return Failed(InvalidManifestError, ex.Message);
        }

        if (existing.Any(m => string.Equals(m.Ticket, ticket, StringComparison.OrdinalIgnoreCase)))
            return Failed(CollisionError, $"ticket already has a lease: {ticket}");
        if (existing.Any(m => string.Equals(m.Branch, branch, StringComparison.Ordinal)))
            return Failed(CollisionError, $"branch already has a lease: {branch}");
        if (existing.Any(m => PathsEqual(m.WorktreePath, target)))
            return Failed(CollisionError, $"target path already has a lease: {target}");

        IReadOnlyList<WorktreeInfo> worktrees;
        IReadOnlyList<string> branches;
        try
        {
            worktrees = await _git.ListWorktreesAsync(ct).ConfigureAwait(false);
            branches = await _git.ListLocalBranchesAsync(branch, _options.MainWorktreePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failed(FailureError, $"git collision preflight failed: {ex.Message}");
        }

        if (Directory.Exists(target) || File.Exists(target) ||
            worktrees.Any(w => PathsEqual(w.Path, target)) ||
            branches.Any(b => string.Equals(b, branch, StringComparison.Ordinal)))
            return Failed(CollisionError, $"branch or target path already exists for lease: {branch}");

        var seeds = ValidateSeeds(request.RequiredSeed);
        if (!seeds.Success)
            return Failed(seeds.ErrorCode!, seeds.Message!);
        var trackedSeeds = await _git.FilterTrackedPathsAsync(
            seeds.Paths!, _options.MainWorktreePath, ct).ConfigureAwait(false);
        if (trackedSeeds.Count > 0)
            return Failed(
                FailureError,
                $"worktree.seed_files may contain only untracked local files: {string.Join(", ", trackedSeeds)}");

        var baseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? "HEAD" : request.BaseRef.Trim();
        string baseSha;
        try
        {
            baseSha = await _git.RevParseAsync(baseRef, _options.MainWorktreePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Failed(FailureError, $"could not resolve base ref '{baseRef}': {ex.Message}");
        }
        if (baseSha.Length != 40 || !baseSha.All(Uri.IsHexDigit))
            return Failed(FailureError, $"base ref '{baseRef}' did not resolve to a full 40-character SHA");

        var branchCreated = false;
        var worktreeCreated = false;
        try
        {
            var createBranch = await _git.CreateBranchRefAsync(
                branch, baseSha, _options.MainWorktreePath, ct).ConfigureAwait(false);
            if (!createBranch.Success)
                return Failed(FailureError, $"branch creation failed: {createBranch.FailureReason}");
            branchCreated = true;

            var created = await _git.CheckoutWorktreeAsync(
                target, branch, _options.MainWorktreePath, ct).ConfigureAwait(false);
            if (!created.Success)
            {
                await RollBackCreatedLeaseAsync(
                    target, branch, worktreeCreated, branchCreated, CancellationToken.None)
                    .ConfigureAwait(false);
                return Failed(FailureError, $"worktree creation failed: {created.FailureReason}");
            }
            worktreeCreated = true;
        }
        catch (OperationCanceledException)
        {
            await RollBackCreatedLeaseAsync(
                target, branch, worktreeCreated, branchCreated, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await RollBackCreatedLeaseAsync(
                target, branch, worktreeCreated, branchCreated, CancellationToken.None)
                .ConfigureAwait(false);
            return Failed(FailureError, $"worktree creation failed: {ex.Message}");
        }

        var seeded = new List<string>();
        try
        {
            foreach (var seed in seeds.Paths!)
            {
                var source = Path.Combine(_options.MainWorktreePath, seed);
                if (!File.Exists(source))
                    continue;
                var destination = Path.Combine(target, seed);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
                seeded.Add(seed);
            }

            var stopwatch = Stopwatch.StartNew();
            var installResult = await _installer.RunAsync(
                _options.InstallCommand, target, ct).ConfigureAwait(false);
            stopwatch.Stop();
            var install = new WorktreeInstallRecord(
                string.IsNullOrWhiteSpace(_options.InstallCommand)
                    ? "skipped"
                    : installResult.Success ? "succeeded" : "failed",
                stopwatch.ElapsedMilliseconds,
                installResult.FailureReason);

            var manifest = new WorktreeLeaseManifest(
                WorktreeLeaseConstants.ManifestSchemaVersion,
                ticket,
                slug,
                branch,
                baseSha.ToLowerInvariant(),
                Path.GetFullPath(_options.RepositoryPath),
                Path.GetFullPath(_options.MainWorktreePath),
                root,
                target,
                seeded.AsReadOnly(),
                Array.Empty<string>(),
                install);
            WriteManifest(manifest);

            return installResult.Success
                ? new WorktreeLeaseResult(true, null, null, manifest)
                : new WorktreeLeaseResult(false, FailureError, installResult.FailureReason, manifest);
        }
        catch (OperationCanceledException)
        {
            await RollBackCreatedLeaseAsync(
                target, branch, worktreeCreated, branchCreated, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await RollBackCreatedLeaseAsync(
                target, branch, worktreeCreated, branchCreated, CancellationToken.None)
                .ConfigureAwait(false);
            return Failed(FailureError, $"lease setup failed and was rolled back: {ex.Message}");
        }
    }

    public Task<WorktreeLeaseListResult> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(_options.WorktreeRoot);
        if (!Directory.Exists(root))
            return Task.FromResult(new WorktreeLeaseListResult(
                Array.Empty<WorktreeLeaseManifest>(), Array.Empty<string>()));

        var leases = new List<WorktreeLeaseManifest>();
        var unmanifested = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(dir, WorktreeLeaseConstants.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                unmanifested.Add(Path.GetFullPath(dir));
                continue;
            }
            try
            {
                leases.Add(ReadAndValidateManifest(manifestPath, root));
            }
            catch (InvalidDataException)
            {
                unmanifested.Add(Path.GetFullPath(dir));
            }
        }
        return Task.FromResult(new WorktreeLeaseListResult(
            leases.AsReadOnly(), unmanifested.AsReadOnly()));
    }

    public async Task<WorktreeLeaseResult> TeardownAsync(
        string? ticket,
        string? directory,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(_options.WorktreeRoot);
        WorktreeLeaseManifest manifest;
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var target = Path.GetFullPath(directory);
                if (!IsStrictlyBelow(target, root))
                    return Failed(ContainmentError, $"target is not strictly below configured root: {target}");
                manifest = ReadAndValidateManifest(
                    Path.Combine(target, WorktreeLeaseConstants.ManifestFileName), root);
                if (!PathsEqual(target, manifest.WorktreePath))
                    return Failed(InvalidManifestError, "manifest worktreePath does not match requested directory");
            }
            else
            {
                var normalizedTicket = NormalizeTicket(ticket ?? string.Empty);
                var matches = ReadLeaseManifests(root, rejectInvalid: true)
                    .Where(m => string.Equals(m.Ticket, normalizedTicket, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count != 1)
                    return Failed(InvalidManifestError, $"expected exactly one manifest-backed lease for {normalizedTicket}");
                manifest = matches[0];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return Failed(InvalidManifestError, ex.Message);
        }

        var remove = await _git.RemoveWorktreeAsync(manifest.WorktreePath, force: true, ct)
            .ConfigureAwait(false);
        if (!remove.Success)
            return Failed(FailureError, $"worktree removal failed: {remove.FailureReason}");

        var delete = await _git.DeleteBranchAsync(
            manifest.Branch, force: true, manifest.MainWorktreePath, ct).ConfigureAwait(false);
        if (!delete.Success)
            return Failed(FailureError, $"worktree removed but branch deletion failed: {delete.FailureReason}");

        return new WorktreeLeaseResult(true, null, null, manifest);
    }

    private SeedValidation ValidateSeeds(string? requiredSeed)
    {
        var normalized = new List<string>();
        foreach (var configured in _options.SeedAllowlist)
        {
            if (!TryNormalizeRelativePath(configured, out var seed))
                return new SeedValidation(false, FailureError, $"invalid seed path in config: {configured}", null);
            if (!normalized.Contains(seed, StringComparer.Ordinal))
                normalized.Add(seed);
        }

        if (!string.IsNullOrWhiteSpace(requiredSeed))
        {
            if (!TryNormalizeRelativePath(requiredSeed, out var required))
                return new SeedValidation(false, MissingSeedError, $"invalid required seed path: {requiredSeed}", null);
            if (!normalized.Contains(required, StringComparer.Ordinal))
                return new SeedValidation(false, MissingSeedError, $"required seed is not in worktree.seed_files: {required}", null);
            if (!File.Exists(Path.Combine(_options.MainWorktreePath, required)))
                return new SeedValidation(false, MissingSeedError, $"required seed is absent from the primary worktree: {required}", null);
        }

        return new SeedValidation(true, null, null, normalized.AsReadOnly());
    }

    private List<WorktreeLeaseManifest> ReadLeaseManifests(string root, bool rejectInvalid)
    {
        var manifests = new List<WorktreeLeaseManifest>();
        if (!Directory.Exists(root))
            return manifests;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(dir, WorktreeLeaseConstants.ManifestFileName);
            if (!File.Exists(path))
                continue;
            try
            {
                manifests.Add(ReadAndValidateManifest(path, root));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                if (rejectInvalid)
                    throw new InvalidDataException($"invalid lease manifest '{path}': {ex.Message}", ex);
            }
        }
        return manifests;
    }

    private WorktreeLeaseManifest ReadAndValidateManifest(string manifestPath, string root)
    {
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"lease manifest not found: {manifestPath}");
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize(
            json, WorktreeLeaseJsonContext.Default.WorktreeLeaseManifest)
            ?? throw new InvalidDataException("lease manifest is empty");
        if (manifest.SchemaVersion != WorktreeLeaseConstants.ManifestSchemaVersion)
            throw new InvalidDataException($"unsupported manifest schema version: {manifest.SchemaVersion}");

        RequireText(manifest.Ticket, "ticket");
        if (manifest.Slug is null)
            throw new InvalidDataException("manifest slug is required");
        RequireText(manifest.Branch, "branch");
        RequireText(manifest.BaseSha, "baseSha");
        RequireText(manifest.RepositoryPath, "repositoryPath");
        RequireText(manifest.MainWorktreePath, "mainWorktreePath");
        RequireText(manifest.WorktreeRoot, "worktreeRoot");
        RequireText(manifest.WorktreePath, "worktreePath");
        if (manifest.SeededFiles is null)
            throw new InvalidDataException("manifest seededFiles is required");
        if (manifest.LeasedResources is null)
            throw new InvalidDataException("manifest leasedResources is required");
        if (manifest.Install is null)
            throw new InvalidDataException("manifest install is required");
        RequireText(manifest.Install.Status, "install.status");

        if (manifest.BaseSha.Length != 40 || !manifest.BaseSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("manifest baseSha is not a full 40-character SHA");
        if (!string.Equals(manifest.BaseSha, manifest.BaseSha.ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidDataException("manifest baseSha must be lowercase");
        ValidateAbsoluteNormalizedPath(manifest.RepositoryPath, "repositoryPath");
        ValidateAbsoluteNormalizedPath(manifest.MainWorktreePath, "mainWorktreePath");
        ValidateAbsoluteNormalizedPath(manifest.WorktreeRoot, "worktreeRoot");
        ValidateAbsoluteNormalizedPath(manifest.WorktreePath, "worktreePath");
        if (!PathsEqual(manifest.WorktreeRoot, root))
            throw new InvalidDataException("manifest worktreeRoot does not match configured root");
        if (!PathsEqual(manifest.RepositoryPath, _options.RepositoryPath) ||
            !PathsEqual(manifest.MainWorktreePath, _options.MainWorktreePath))
            throw new InvalidDataException("manifest repository paths do not match the current repository");
        if (!IsStrictlyBelow(manifest.WorktreePath, root))
            throw new InvalidDataException("manifest worktreePath is not strictly below worktreeRoot");
        if (!PathsEqual(Path.GetDirectoryName(manifestPath)!, manifest.WorktreePath))
            throw new InvalidDataException("manifest location does not match worktreePath");
        var normalizedTicket = NormalizeTicket(manifest.Ticket);
        var normalizedSlug = NormalizeSlug(manifest.Slug);
        if (!string.Equals(manifest.Ticket, normalizedTicket, StringComparison.Ordinal) ||
            !string.Equals(manifest.Slug, normalizedSlug, StringComparison.Ordinal))
            throw new InvalidDataException("manifest ticket or slug is not normalized");
        var suffix = SlugBuilder.BuildBranchSlug(normalizedTicket, normalizedSlug);
        var expectedBranch = $"lease/{suffix}";
        var expectedPath = Path.Combine(root, suffix);
        if (!string.Equals(manifest.Branch, expectedBranch, StringComparison.Ordinal))
            throw new InvalidDataException("manifest branch does not match its ticket and slug");
        if (!PathsEqual(manifest.WorktreePath, expectedPath))
            throw new InvalidDataException("manifest worktreePath does not match its ticket and slug");
        foreach (var seededFile in manifest.SeededFiles)
        {
            if (seededFile is null ||
                !TryNormalizeRelativePath(seededFile, out var normalizedSeed) ||
                !string.Equals(seededFile, normalizedSeed, StringComparison.Ordinal))
                throw new InvalidDataException("manifest seededFiles contains an invalid or non-normalized path");
        }
        if (manifest.LeasedResources.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("manifest leasedResources contains a null or blank value");
        if (manifest.Install.Status is not ("skipped" or "succeeded" or "failed"))
            throw new InvalidDataException("manifest install.status is invalid");
        if (manifest.Install.DurationMilliseconds < 0)
            throw new InvalidDataException("manifest install.durationMilliseconds must not be negative");
        return manifest;
    }

    private void ValidateAbsoluteNormalizedPath(string path, string field)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidDataException($"manifest {field} must be absolute");
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var supplied = Path.TrimEndingDirectorySeparator(path);
            if (!string.Equals(supplied, normalized, _pathComparison))
                throw new InvalidDataException($"manifest {field} must be normalized");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"manifest {field} is invalid: {ex.Message}", ex);
        }
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"manifest {field} is required");
    }

    private void WriteManifest(WorktreeLeaseManifest manifest)
    {
        var path = Path.Combine(manifest.WorktreePath, WorktreeLeaseConstants.ManifestFileName);
        var json = JsonSerializer.Serialize(
            manifest, WorktreeLeaseJsonContext.Default.WorktreeLeaseManifest);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private async Task RollBackCreatedLeaseAsync(
        string target,
        string branch,
        bool worktreeCreated,
        bool branchCreated,
        CancellationToken ct)
    {
        if (worktreeCreated)
        {
            try { await _git.RemoveWorktreeAsync(target, force: true, ct).ConfigureAwait(false); }
            catch { }
        }
        if (branchCreated)
        {
            try { await _git.DeleteBranchAsync(branch, force: true, _options.MainWorktreePath, ct).ConfigureAwait(false); }
            catch { }
        }
    }

    private bool IsStrictlyBelow(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative.Length > 0 &&
               relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _pathComparison);

    private static bool TryNormalizeRelativePath(string raw, out string normalized)
    {
        normalized = raw.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            return false;
        var parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
            return false;
        normalized = Path.Combine(parts);
        return true;
    }

    private static string NormalizeTicket(string raw)
    {
        var chars = raw.Trim().ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeSlug(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static WorktreeLeaseResult Failed(string code, string message) =>
        new(false, code, message, null);

    private sealed record SeedValidation(
        bool Success,
        string? ErrorCode,
        string? Message,
        IReadOnlyList<string>? Paths);
}
