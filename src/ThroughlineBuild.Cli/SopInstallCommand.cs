using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

internal static class SopInstallCommand
{
    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextWriter output,
        TextWriter error)
    {
        var operation = args[1];
        if (!TryParseScope(args, json, output, error, out var entries, out var exitCode))
            return exitCode;

        var result = SopInstaller.Run(
            operation,
            startDirectory,
            entries,
            BuildVersion.Current,
            DateTimeOffset.UtcNow);

        if (json)
            CliEnvelopeWriter.WriteSopOperation(output, result);
        else
            WriteHuman(output, result);

        return result.Passed ? 0 : 1;
    }

    private static bool TryParseScope(
        IReadOnlyList<string> args,
        bool json,
        TextWriter output,
        TextWriter error,
        out IReadOnlyList<SopCatalogEntry> entries,
        out int exitCode)
    {
        entries = SopBundleCatalog.All;
        exitCode = 0;
        string? sopName = null;

        for (var i = 2; i < args.Count; i++)
        {
            if (!string.Equals(args[i], "--sop", StringComparison.Ordinal))
            {
                exitCode = Usage(json, output, error, $"unknown argument for sop {args[1]}: {args[i]}");
                return false;
            }

            if (sopName is not null)
            {
                exitCode = Usage(json, output, error, "--sop may be supplied only once");
                return false;
            }

            if (i + 1 >= args.Count)
            {
                exitCode = Usage(json, output, error, "--sop requires a SOP name");
                return false;
            }

            sopName = args[++i];
        }

        if (sopName is null)
            return true;

        var entry = SopBundleCatalog.Find(sopName);
        if (entry is null)
        {
            if (json)
                CliEnvelopeWriter.WriteError(output, CliErrorCodes.UnknownSop, $"unknown SOP: {sopName}");
            else
                error.WriteLine($"Error: unknown SOP: {sopName}");
            exitCode = SopCommand.UnknownSopExitCode;
            return false;
        }

        entries = [entry];
        return true;
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
        {
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        }
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build sop install|upgrade|uninstall|status [--sop <name>] [--json]");
        }

        return 2;
    }

    private static void WriteHuman(TextWriter output, SopOperationView result)
    {
        output.WriteLine(result.Passed
            ? $"sop {result.Operation} passed"
            : $"sop {result.Operation} reported drift");
        output.WriteLine($"changed: {result.Changed.ToString().ToLowerInvariant()}");
        output.WriteLine($"manifest: {result.ManifestPath}");
        foreach (var item in result.Results)
            output.WriteLine($"{item.Status}\t{item.Path}\t{item.Message}");
    }
}

internal static class SopInstaller
{
    private const int ManifestSchemaVersion = 1;
    private const string ManifestRelativePath = ".build/sop-manifest.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static SopOperationView Run(
        string operation,
        string startDirectory,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now)
    {
        var repositoryRoot = ResolveRepositoryRoot(startDirectory);
        var manifestPath = Path.Combine(repositoryRoot, ".build", "sop-manifest.json");
        var targets = BuildTargets(scopeEntries);
        var results = new List<SopPathResultView>();

        if (!TryResolvePath(repositoryRoot, ManifestRelativePath, out var manifestResolution, out var manifestError))
        {
            results.Add(Result(
                Array.Empty<string>(),
                ManifestRelativePath,
                "manifest",
                "unsafe_path",
                manifestError));
            return View(operation, repositoryRoot, manifestPath, buildVersion, scopeEntries, changed: false, results);
        }

        var resolutions = new Dictionary<string, ResolvedCatalogPath>(PathComparer);
        foreach (var target in targets)
        {
            if (TryResolvePath(repositoryRoot, target.Path, out var resolution, out var error))
            {
                resolutions[target.Path] = resolution;
                continue;
            }

            results.Add(Result(
                target.Sops,
                target.Path,
                target.Class,
                "unsafe_path",
                error));
        }

        if (results.Count > 0)
            return View(operation, repositoryRoot, manifestResolution.AbsolutePath, buildVersion, scopeEntries, changed: false, results);

        var manifest = ReadManifestOrNull(manifestResolution.AbsolutePath, results);
        var changed = operation switch
        {
            "install" => RunInstall(
                targets,
                resolutions,
                manifestResolution.AbsolutePath,
                manifest,
                scopeEntries,
                buildVersion,
                now,
                results),
            "upgrade" => RunUpgrade(
                targets,
                resolutions,
                manifestResolution.AbsolutePath,
                manifest,
                scopeEntries,
                buildVersion,
                now,
                results),
            "uninstall" => RunUninstall(
                targets,
                resolutions,
                manifestResolution.AbsolutePath,
                manifest,
                scopeEntries,
                buildVersion,
                now,
                results),
            "status" => RunStatus(targets, resolutions, buildVersion, results),
            _ => throw new InvalidOperationException($"unknown sop operation: {operation}"),
        };

        return View(operation, repositoryRoot, manifestResolution.AbsolutePath, buildVersion, scopeEntries, changed, results);
    }

    private static bool RunInstall(
        IReadOnlyList<SopInstallTarget> targets,
        IReadOnlyDictionary<string, ResolvedCatalogPath> resolutions,
        string manifestPath,
        SopManifest? manifest,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now,
        List<SopPathResultView> results)
    {
        var changed = false;
        foreach (var target in targets)
        {
            var resolved = resolutions[target.Path];
            if (target.IsEmitted)
            {
                changed |= InstallEmitted(target, resolved, results);
                continue;
            }

            if (target.IsScaffolded)
            {
                changed |= InstallScaffolded(target, resolved, buildVersion, results);
                continue;
            }

            results.Add(Result(target, "invalid_class", $"unknown catalog path class '{target.Class}'"));
        }

        if (HasBlockingFindings(results))
            return changed;

        if (changed || !ManifestCovers(manifest, scopeEntries))
        {
            WriteManifest(manifestPath, BuildUpdatedManifest(manifest, scopeEntries, buildVersion, now));
            changed = true;
        }

        return changed;
    }

    private static bool RunUpgrade(
        IReadOnlyList<SopInstallTarget> targets,
        IReadOnlyDictionary<string, ResolvedCatalogPath> resolutions,
        string manifestPath,
        SopManifest? manifest,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now,
        List<SopPathResultView> results)
    {
        var changed = false;
        foreach (var target in targets)
        {
            var resolved = resolutions[target.Path];
            if (target.IsEmitted)
            {
                changed |= UpgradeEmitted(target, resolved, results);
                continue;
            }

            if (target.IsScaffolded)
            {
                StatusScaffolded(target, resolved, buildVersion, results);
                continue;
            }

            results.Add(Result(target, "invalid_class", $"unknown catalog path class '{target.Class}'"));
        }

        if (HasBlockingFindings(results))
            return changed;

        if (changed)
            WriteManifest(manifestPath, BuildUpdatedManifest(manifest, scopeEntries, buildVersion, now));

        return changed;
    }

    private static bool RunUninstall(
        IReadOnlyList<SopInstallTarget> targets,
        IReadOnlyDictionary<string, ResolvedCatalogPath> resolutions,
        string manifestPath,
        SopManifest? manifest,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now,
        List<SopPathResultView> results)
    {
        var changed = false;
        foreach (var target in targets)
        {
            var resolved = resolutions[target.Path];
            if (target.IsEmitted)
            {
                changed |= UninstallEmitted(target, resolved, results);
                continue;
            }

            if (target.IsScaffolded)
            {
                results.Add(Result(
                    target,
                    File.Exists(resolved.AbsolutePath) ? "preserved_scaffolded" : "missing_scaffolded",
                    "scaffolded files are repository data and are not deleted by uninstall"));
                continue;
            }

            results.Add(Result(target, "invalid_class", $"unknown catalog path class '{target.Class}'"));
        }

        if (manifest is not null)
        {
            var (updated, manifestChanged) = BuildManifestAfterUninstall(manifest, scopeEntries, buildVersion, now, results);
            if (manifestChanged && updated is null)
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                    changed = true;
                }
            }
            else if (manifestChanged && updated is not null)
            {
                WriteManifest(manifestPath, updated);
                changed = true;
            }
        }

        return changed;
    }

    private static bool RunStatus(
        IReadOnlyList<SopInstallTarget> targets,
        IReadOnlyDictionary<string, ResolvedCatalogPath> resolutions,
        string buildVersion,
        List<SopPathResultView> results)
    {
        foreach (var target in targets)
        {
            var resolved = resolutions[target.Path];
            if (target.IsEmitted)
            {
                StatusEmitted(target, resolved, results);
                continue;
            }

            if (target.IsScaffolded)
            {
                StatusScaffolded(target, resolved, buildVersion, results);
                continue;
            }

            results.Add(Result(target, "invalid_class", $"unknown catalog path class '{target.Class}'"));
        }

        return false;
    }

    private static bool InstallEmitted(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        List<SopPathResultView> results)
    {
        if (!TryLoadEmittedContent(target, results, out var content, out _))
            return false;

        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.AbsolutePath)!);
            File.WriteAllText(resolved.AbsolutePath, content, Utf8NoBom);
            results.Add(Result(target, "created", "emitted catalog file was written"));
            return true;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "catalog path exists but is not a regular file"));
            return false;
        }

        var actualHash = ComputeFileSha256(resolved.AbsolutePath);
        if (string.Equals(actualHash, target.ExpectedContentHash, StringComparison.Ordinal))
        {
            results.Add(Result(target, "unchanged", "emitted file already matches the catalog"));
            return false;
        }

        results.Add(Result(target, "modified", "emitted file differs from the catalog; install will not overwrite it"));
        return false;
    }

    private static bool InstallScaffolded(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        string buildVersion,
        List<SopPathResultView> results)
    {
        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resolved.AbsolutePath)!);
            File.WriteAllText(resolved.AbsolutePath, BuildConductorScaffold(buildVersion), Utf8NoBom);
            results.Add(Result(target, "scaffolded", "scaffolded file was written"));
            return true;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "scaffolded path exists but is not a regular file"));
            return false;
        }

        results.Add(Result(target, "preserved_scaffolded", "existing scaffolded file was preserved"));
        return false;
    }

    private static bool UpgradeEmitted(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        List<SopPathResultView> results)
    {
        if (!TryLoadEmittedContent(target, results, out var content, out var newHash))
            return false;

        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "missing", "emitted catalog path is missing; run build sop install to restore it"));
            return false;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "catalog path exists but is not a regular file"));
            return false;
        }

        var actualHash = ComputeFileSha256(resolved.AbsolutePath);
        if (string.Equals(actualHash, newHash, StringComparison.Ordinal))
        {
            results.Add(Result(target, "unchanged", "emitted file already matches the current catalog"));
            return false;
        }

        if (target.TrustedPreviousContentHashes.Contains(actualHash, StringComparer.Ordinal))
        {
            File.WriteAllText(resolved.AbsolutePath, content, Utf8NoBom);
            results.Add(Result(target, "upgraded", "emitted file was rewritten from trusted previous catalog content"));
            return true;
        }

        var message = target.TrustedPreviousContentHashes.Count == 0
            ? "emitted file differs from the current catalog and no trusted previous catalog hash is embedded"
            : "emitted file matches neither the current catalog nor trusted previous catalog content";
        results.Add(Result(target, "modified", message));
        return false;
    }

    private static bool UninstallEmitted(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        List<SopPathResultView> results)
    {
        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "already_absent", "emitted catalog path is already absent"));
            return false;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "catalog path exists but is not a regular file"));
            return false;
        }

        var actualHash = ComputeFileSha256(resolved.AbsolutePath);
        if (!string.Equals(actualHash, target.ExpectedContentHash, StringComparison.Ordinal))
        {
            results.Add(Result(target, "modified", "emitted file differs from the catalog and was preserved"));
            return false;
        }

        File.Delete(resolved.AbsolutePath);
        results.Add(Result(target, "removed", "emitted catalog file was removed"));
        return true;
    }

    private static void StatusEmitted(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        List<SopPathResultView> results)
    {
        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "missing", "emitted catalog path is missing"));
            return;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "catalog path exists but is not a regular file"));
            return;
        }

        var actualHash = ComputeFileSha256(resolved.AbsolutePath);
        results.Add(string.Equals(actualHash, target.ExpectedContentHash, StringComparison.Ordinal)
            ? Result(target, "clean", "emitted file matches the catalog")
            : Result(target, "modified", "emitted file differs from the catalog"));
    }

    private static void StatusScaffolded(
        SopInstallTarget target,
        ResolvedCatalogPath resolved,
        string buildVersion,
        List<SopPathResultView> results)
    {
        if (!File.Exists(resolved.AbsolutePath) && !Directory.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "missing", "scaffolded catalog path is missing"));
            return;
        }

        if (!File.Exists(resolved.AbsolutePath))
        {
            results.Add(Result(target, "not_regular", "scaffolded path exists but is not a regular file"));
            return;
        }

        var doctor = SopDoctorCommand.RunConductorOnlyDoctor(Path.GetDirectoryName(resolved.AbsolutePath)!, buildVersion);
        if (doctor.Findings.Count == 0)
        {
            results.Add(Result(target, "clean_scaffolded", "scaffolded file passed schema validation"));
            return;
        }

        var message = string.Join("; ", doctor.Findings.Select(finding => $"{finding.Path}: {finding.Message}"));
        results.Add(Result(target, "invalid_scaffolded", message));
    }

    private static bool TryLoadEmittedContent(
        SopInstallTarget target,
        List<SopPathResultView> results,
        out string content,
        out string expectedHash)
    {
        content = string.Empty;
        expectedHash = target.ExpectedContentHash ?? string.Empty;
        if (!target.IsEmitted ||
            string.IsNullOrWhiteSpace(target.ResourceName) ||
            string.IsNullOrWhiteSpace(target.ExpectedContentHash))
        {
            results.Add(Result(target, "invalid_catalog", "emitted catalog path is missing resource or hash metadata"));
            return false;
        }

        try
        {
            content = SopResourceLoader.LoadResource(target.ResourceName);
            expectedHash = SopResourceLoader.ComputeSha256(content);
            if (!string.Equals(expectedHash, target.ExpectedContentHash, StringComparison.Ordinal))
            {
                results.Add(Result(target, "invalid_catalog", "embedded resource hash does not match the catalog"));
                return false;
            }

            return true;
        }
        catch (InvalidOperationException ex)
        {
            results.Add(Result(target, "invalid_catalog", ex.Message));
            return false;
        }
    }

    private static SopManifest? ReadManifestOrNull(string manifestPath, List<SopPathResultView> results)
    {
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                CliJsonContext.Default.SopManifest);
            if (manifest is null)
            {
                results.Add(Result(
                    Array.Empty<string>(),
                    ManifestRelativePath,
                    "manifest",
                    "manifest_ignored",
                    "manifest cache was empty and was ignored"));
                return null;
            }

            if (!IsManifestCacheUsable(manifest, out var reason))
            {
                results.Add(Result(
                    Array.Empty<string>(),
                    ManifestRelativePath,
                    "manifest",
                    "manifest_ignored",
                    reason));
                return null;
            }

            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            results.Add(Result(
                Array.Empty<string>(),
                ManifestRelativePath,
                "manifest",
                "manifest_ignored",
                $"manifest cache could not be read and was ignored: {ex.Message}"));
            return null;
        }
    }

    private static SopManifest BuildUpdatedManifest(
        SopManifest? existing,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now)
    {
        var selected = new HashSet<string>(scopeEntries.Select(entry => entry.Name), StringComparer.Ordinal);
        var rows = existing?.Sops
            .Where(row => row is not null && !selected.Contains(row.Name))
            .ToList() ?? [];
        rows.AddRange(scopeEntries.Select(entry => BuildManifestSop(entry, buildVersion, now)));
        return new SopManifest(ManifestSchemaVersion, buildVersion, now, rows);
    }

    private static (SopManifest? Manifest, bool Changed) BuildManifestAfterUninstall(
        SopManifest existing,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        string buildVersion,
        DateTimeOffset now,
        IReadOnlyList<SopPathResultView> results)
    {
        var selected = new HashSet<string>(scopeEntries.Select(entry => entry.Name), StringComparer.Ordinal);
        var changed = false;
        var rows = new List<SopManifestSop>();
        foreach (var row in existing.Sops)
        {
            if (row is null)
            {
                changed = true;
                continue;
            }

            if (!selected.Contains(row.Name))
            {
                rows.Add(row);
                continue;
            }

            var keptPaths = row.Paths
                .Where(path => path is not null && KeepManifestPathAfterUninstall(row.Name, path.Path, results))
                .ToList();
            var rowChanged = keptPaths.Count != row.Paths.Count;
            if (rowChanged || keptPaths.Count == 0)
                changed = true;
            if (keptPaths.Count > 0)
            {
                rows.Add(rowChanged
                    ? row with
                    {
                        InstalledByBuildVersion = buildVersion,
                        InstalledAtUtc = now,
                        Paths = keptPaths,
                    }
                    : row);
            }
        }

        if (!changed)
            return (existing, false);

        return (
            rows.Count == 0
                ? null
                : new SopManifest(ManifestSchemaVersion, buildVersion, now, rows),
            true);
    }

    private static bool KeepManifestPathAfterUninstall(
        string sopName,
        string path,
        IReadOnlyList<SopPathResultView> results)
    {
        var normalizedPath = path.Replace('\\', '/');
        var result = results.FirstOrDefault(item =>
            item.Sops.Contains(sopName, StringComparer.Ordinal) &&
            PathsEqual(item.Path, normalizedPath));
        return result is null ||
               result.Status is "modified" or "not_regular" or "preserved_scaffolded";
    }

    private static SopManifestSop BuildManifestSop(
        SopCatalogEntry entry,
        string buildVersion,
        DateTimeOffset now)
    {
        var paths = entry.OwnedPaths
            .Select(path => new SopManifestPath(
                path.Path,
                path.Class,
                path.Class == SopBundleCatalog.EmittedPathClass ? path.ExpectedContentHash : null,
                path.ResourceName))
            .ToList();
        return new SopManifestSop(entry.Name, buildVersion, now, paths);
    }

    private static bool ManifestCovers(SopManifest? manifest, IReadOnlyList<SopCatalogEntry> scopeEntries)
    {
        if (manifest is null || manifest.SchemaVersion != ManifestSchemaVersion)
            return false;

        foreach (var entry in scopeEntries)
        {
            var matchingSops = manifest.Sops
                .Where(sop => sop is not null && string.Equals(sop.Name, entry.Name, StringComparison.Ordinal))
                .ToList();
            if (matchingSops.Count != 1)
                return false;
            var row = matchingSops[0];

            foreach (var owned in entry.OwnedPaths)
            {
                var matchingPaths = row.Paths
                    .Where(item => item is not null && PathsEqual(item.Path, owned.Path))
                    .ToList();
                if (matchingPaths.Count != 1)
                    return false;
                var path = matchingPaths[0];
                if (path is null ||
                    !string.Equals(path.Class, owned.Class, StringComparison.Ordinal) ||
                    !string.Equals(path.ResourceName, owned.ResourceName, StringComparison.Ordinal) ||
                    !string.Equals(path.ContentHash, owned.ExpectedContentHash, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    private static bool IsManifestCacheUsable(SopManifest manifest, out string reason)
    {
        reason = string.Empty;
        if (manifest.SchemaVersion != ManifestSchemaVersion)
        {
            reason = "manifest cache has an unsupported schema and was ignored";
            return false;
        }

        if (manifest.Sops is null)
        {
            reason = "manifest cache is missing sops and was ignored";
            return false;
        }

        var sopNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sop in manifest.Sops)
        {
            if (sop is null || string.IsNullOrWhiteSpace(sop.Name) || sop.Paths is null)
            {
                reason = "manifest cache contains an invalid SOP row and was ignored";
                return false;
            }

            if (!sopNames.Add(sop.Name))
            {
                reason = "manifest cache contains duplicate SOP rows and was ignored";
                return false;
            }

            var paths = new HashSet<string>(PathComparer);
            foreach (var path in sop.Paths)
            {
                if (path is null ||
                    string.IsNullOrWhiteSpace(path.Path) ||
                    string.IsNullOrWhiteSpace(path.Class))
                {
                    reason = "manifest cache contains an invalid path row and was ignored";
                    return false;
                }

                if (!paths.Add(path.Path.Replace('\\', '/')))
                {
                    reason = "manifest cache contains duplicate path rows and was ignored";
                    return false;
                }
            }
        }

        return true;
    }

    private static void WriteManifest(string manifestPath, SopManifest manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var json = JsonSerializer.Serialize(manifest, CliJsonContext.Default.SopManifest);
        File.WriteAllText(manifestPath, json + Environment.NewLine, Utf8NoBom);
    }

    private static bool TryResolvePath(
        string repositoryRoot,
        string catalogPath,
        out ResolvedCatalogPath resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;

        if (!TryNormalizeCatalogPath(catalogPath, out var normalized, out error))
            return false;

        var absolutePath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictlyBelow(absolutePath, repositoryRoot))
        {
            error = "catalog path does not resolve strictly below the repository root";
            return false;
        }

        if (!ValidateNoLinkSegments(repositoryRoot, normalized, out error))
            return false;

        resolution = new ResolvedCatalogPath(normalized, absolutePath);
        return true;
    }

    private static bool TryNormalizeCatalogPath(string catalogPath, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(catalogPath))
        {
            error = "catalog path is blank";
            return false;
        }

        if (Path.IsPathRooted(catalogPath) || Path.IsPathFullyQualified(catalogPath))
        {
            error = "catalog path must be relative";
            return false;
        }

        var candidate = catalogPath.Replace('\\', '/');
        if (candidate.Contains(':', StringComparison.Ordinal))
        {
            error = "catalog path must not contain a drive or URI separator";
            return false;
        }

        var segments = candidate.Split('/');
        if (segments.Any(segment =>
            segment.Length == 0 ||
            segment == "." ||
            segment == ".."))
        {
            error = "catalog path must not contain empty, dot, or dot-dot segments";
            return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    private static bool ValidateNoLinkSegments(string repositoryRoot, string normalizedPath, out string error)
    {
        error = string.Empty;
        var current = repositoryRoot;
        foreach (var segment in normalizedPath.Split('/'))
        {
            current = Path.Combine(current, segment);
            try
            {
                var fileInfo = new FileInfo(current);
                var directoryInfo = new DirectoryInfo(current);
                if (fileInfo.LinkTarget is not null || directoryInfo.LinkTarget is not null)
                {
                    error = $"catalog path crosses a symlink or reparse point: {current}";
                    return false;
                }

                if (!fileInfo.Exists && !directoryInfo.Exists)
                    return true;

                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"catalog path crosses a symlink or reparse point: {current}";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"catalog path could not be inspected safely: {ex.Message}";
                return false;
            }
        }

        return true;
    }

    private static bool IsStrictlyBelow(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative.Length > 0 &&
               relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ResolveRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) ||
                File.Exists(gitPath) ||
                Directory.Exists(Path.Combine(current.FullName, ".build")))
                return current.FullName;
            current = current.Parent;
        }

        return Path.GetFullPath(startDirectory);
    }

    private static IReadOnlyList<SopInstallTarget> BuildTargets(IReadOnlyList<SopCatalogEntry> entries)
    {
        var rows = new List<SopInstallTarget>();
        foreach (var entry in entries)
        {
            foreach (var owned in entry.OwnedPaths)
            {
                var existing = rows.FirstOrDefault(row => PathsEqual(row.Path, owned.Path));
                if (existing is null)
                {
                    rows.Add(new SopInstallTarget(
                        [entry.Name],
                        owned.Path,
                        owned.Class,
                        owned.ExpectedContentHash,
                        owned.ResourceName,
                        owned.PreviousContentHashes ?? Array.Empty<string>()));
                    continue;
                }

                existing.Sops.Add(entry.Name);
                foreach (var hash in owned.PreviousContentHashes ?? Array.Empty<string>())
                {
                    if (!existing.TrustedPreviousContentHashes.Contains(hash, StringComparer.Ordinal))
                        existing.TrustedPreviousContentHashes.Add(hash);
                }
            }
        }

        return rows;
    }

    private static SopOperationView View(
        string operation,
        string repositoryRoot,
        string manifestPath,
        string buildVersion,
        IReadOnlyList<SopCatalogEntry> scopeEntries,
        bool changed,
        IReadOnlyList<SopPathResultView> results)
    {
        var passed = !HasBlockingFindings(results);
        return new SopOperationView(
            Operation: operation,
            RepositoryRoot: repositoryRoot,
            ManifestPath: manifestPath,
            SopSchemaVersion: SopBundleCatalog.SchemaVersion,
            BinaryVersion: buildVersion,
            ScopeSops: scopeEntries.Select(entry => entry.Name).ToList(),
            Changed: changed,
            Passed: passed,
            Results: results);
    }

    private static bool HasBlockingFindings(IReadOnlyList<SopPathResultView> results) =>
        results.Any(result => result.Status is
            "unsafe_path" or
            "invalid_class" or
            "invalid_catalog" or
            "missing" or
            "modified" or
            "not_regular" or
            "missing_manifest_history" or
            "invalid_scaffolded");

    private static SopPathResultView Result(SopInstallTarget target, string status, string message) =>
        Result(target.Sops, target.Path, target.Class, status, message);

    private static SopPathResultView Result(
        IReadOnlyList<string> sops,
        string path,
        string @class,
        string status,
        string message) => new(sops, path, @class, status, message);

    private static string BuildConductorScaffold(string buildVersion)
    {
        var minVersion = TrimVersionCore(buildVersion);
        return $$"""
            [conductor]
            min_build_version = "{{minVersion}}"
            branch_prefix = "ticket"
            ticket_prefix = "TICKET"
            source_roots = ["src"]
            architecture_map = "docs/throughline-build-architecture.md"
            rework_cap = 3

            [[conductor.review.invariants]]
            id = "repository-contract"
            statement = "Replace this sentence with a true review invariant for this repository."
            paths = ["src/**"]
            blocks_done = true

            [conductor.review.escalation]
            model_size = "large"
            paths = ["src/**"]

            [constellation]
            platform = "unknown"
            contract_authority = "src"
            """;
    }

    private static string TrimVersionCore(string buildVersion)
    {
        var suffix = buildVersion.IndexOfAny(['-', '+']);
        return suffix >= 0 ? buildVersion[..suffix] : buildVersion;
    }

    private static string ComputeFileSha256(string path)
    {
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/'),
            right.Replace('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class SopInstallTarget(
        IReadOnlyList<string> sops,
        string path,
        string @class,
        string? expectedContentHash,
        string? resourceName,
        IReadOnlyList<string> trustedPreviousContentHashes)
    {
        public List<string> Sops { get; } = sops.ToList();
        public string Path { get; } = path;
        public string Class { get; } = @class;
        public string? ExpectedContentHash { get; } = expectedContentHash;
        public string? ResourceName { get; } = resourceName;
        public List<string> TrustedPreviousContentHashes { get; } = trustedPreviousContentHashes.ToList();
        public bool IsEmitted => string.Equals(Class, SopBundleCatalog.EmittedPathClass, StringComparison.Ordinal);
        public bool IsScaffolded => string.Equals(Class, SopBundleCatalog.ScaffoldedPathClass, StringComparison.Ordinal);
    }

    private readonly record struct ResolvedCatalogPath(string NormalizedPath, string AbsolutePath);
}
