using System.Text.Json;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Scaffold;

/// <summary>
/// The toolchain profile an agent derives from an op-doc: the project's language/framework/manager
/// and, crucially, the automated checks that the review and ship phases must run. This is what fixes
/// the dotnet-template-on-a-Node-repo bug - the op-doc states the real toolchain in prose
/// (e.g. "npm run build" / "npm test") and we turn it into concrete <see cref="ProfileCheck"/>s.
///
/// Executables are kept portable (bare names like "npm", not "npm.cmd"); the runner's
/// ExecutableResolver handles the per-OS variant at launch time.
/// </summary>
public sealed record ProjectProfile(
    string Language,
    string Framework,
    string PackageManager,
    string InstallCommand,
    string BuildCommand,
    string TestCommand,
    string DevCommand,
    IReadOnlyList<ProfileCheck> ReviewChecks,
    IReadOnlyList<ProfileCheck> RegressionChecks)
{
    /// <summary>
    /// Stable, project-wide convention files (test harness/config + one canonical test example) to
    /// inline into EVERY implement brief so the worker does not re-discover them. Paths relative to the
    /// project root; empty when the deriver emitted none. Stack-agnostic: the deriver chose them per
    /// stack, the engine only carries the paths and reads whatever they are.
    /// </summary>
    public IReadOnlyList<string> ConventionFiles { get; init; } = Array.Empty<string>();
}

public sealed record ProfileCheck(
    string Name,
    string Executable,
    IReadOnlyList<string> Arguments,
    int TimeoutMinutes,
    IReadOnlyList<CanaryFile>? Canary = null);

// --- JSON DTOs (source-gen; AOT-safe) -------------------------------------------------

internal sealed class ProjectProfileDto
{
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("framework")] public string? Framework { get; set; }
    [JsonPropertyName("package_manager")] public string? PackageManager { get; set; }
    [JsonPropertyName("install_command")] public string? InstallCommand { get; set; }
    [JsonPropertyName("build_command")] public string? BuildCommand { get; set; }
    [JsonPropertyName("test_command")] public string? TestCommand { get; set; }
    [JsonPropertyName("dev_command")] public string? DevCommand { get; set; }
    [JsonPropertyName("review_checks")] public List<ProfileCheckDto>? ReviewChecks { get; set; }
    [JsonPropertyName("regression_checks")] public List<ProfileCheckDto>? RegressionChecks { get; set; }
    [JsonPropertyName("convention_files")] public List<string>? ConventionFiles { get; set; }
}

internal sealed class ProfileCheckDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("executable")] public string? Executable { get; set; }
    [JsonPropertyName("arguments")] public List<string>? Arguments { get; set; }
    [JsonPropertyName("timeout_minutes")] public int? TimeoutMinutes { get; set; }
    [JsonPropertyName("canary")] public List<CanaryFileDto>? Canary { get; set; }
}

internal sealed class CanaryFileDto
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

[JsonSerializable(typeof(ProjectProfileDto))]
[JsonSerializable(typeof(ProfileCheckDto))]
[JsonSerializable(typeof(CanaryFileDto))]
[JsonSerializable(typeof(List<CanaryFileDto>))]
internal partial class ProfileJsonContext : JsonSerializerContext { }

/// <summary>
/// Parses + validates the agent-emitted profile JSON into a <see cref="ProjectProfile"/>.
/// Uses the source-gen context so it works under PublishAot=true.
/// </summary>
public static class ProjectProfileParser
{
    private const int DefaultTimeoutMinutes = 5;

    public static bool TryParse(string json, out ProjectProfile? profile, out string? error)
    {
        profile = null;
        error = null;

        ProjectProfileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ProfileJsonContext.Default.ProjectProfileDto);
        }
        catch (JsonException ex)
        {
            error = $"profile JSON did not parse: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"profile JSON did not parse: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "profile JSON deserialized to null";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.BuildCommand))
        {
            error = "profile is missing build_command";
            return false;
        }
        if (string.IsNullOrWhiteSpace(dto.TestCommand))
        {
            error = "profile is missing test_command";
            return false;
        }

        if (!TryMapChecks(dto.ReviewChecks, "review_checks", out var reviewChecks, out error))
            return false;
        if (reviewChecks.Count == 0)
        {
            error = "profile review_checks is empty - at least one check is required";
            return false;
        }

        // Ship regression checks default to the review checks when the agent omits them.
        IReadOnlyList<ProfileCheck> regressionChecks;
        if (dto.RegressionChecks is { Count: > 0 })
        {
            if (!TryMapChecks(dto.RegressionChecks, "regression_checks", out var rc, out error))
                return false;
            regressionChecks = rc;
        }
        else
        {
            regressionChecks = reviewChecks;
        }

        // Convention bundle is optional/best-effort: trim, drop blank entries, never throw.
        var conventionFiles = (dto.ConventionFiles ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList()
            .AsReadOnly();

        profile = new ProjectProfile(
            Language: dto.Language?.Trim() ?? "",
            Framework: dto.Framework?.Trim() ?? "",
            PackageManager: dto.PackageManager?.Trim() ?? "",
            InstallCommand: dto.InstallCommand?.Trim() ?? "",
            BuildCommand: dto.BuildCommand!.Trim(),
            TestCommand: dto.TestCommand!.Trim(),
            DevCommand: dto.DevCommand?.Trim() ?? "",
            ReviewChecks: reviewChecks,
            RegressionChecks: regressionChecks)
        {
            ConventionFiles = conventionFiles
        };
        return true;
    }

    private static bool TryMapChecks(
        List<ProfileCheckDto>? dtos,
        string field,
        out IReadOnlyList<ProfileCheck> checks,
        out string? error)
    {
        error = null;
        var list = new List<ProfileCheck>();
        if (dtos is null)
        {
            checks = list;
            return true;
        }

        for (int i = 0; i < dtos.Count; i++)
        {
            var c = dtos[i];
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                error = $"{field}[{i}] is missing name";
                checks = Array.Empty<ProfileCheck>();
                return false;
            }
            if (string.IsNullOrWhiteSpace(c.Executable))
            {
                error = $"{field}[{i}] ({c.Name}) is missing executable";
                checks = Array.Empty<ProfileCheck>();
                return false;
            }
            var args = (c.Arguments ?? new List<string>())
                .Where(a => a is not null)
                .Select(a => a.Trim())
                .ToList();
            int timeout = c.TimeoutMinutes is > 0 ? c.TimeoutMinutes.Value : DefaultTimeoutMinutes;

            // Canary is optional/best-effort: map non-empty-Path entries, skip blanks, never throw.
            IReadOnlyList<CanaryFile>? canary = null;
            if (c.Canary is { Count: > 0 })
            {
                var canaryFiles = new List<CanaryFile>();
                foreach (var cf in c.Canary)
                {
                    if (cf is null || string.IsNullOrWhiteSpace(cf.Path))
                        continue;
                    canaryFiles.Add(new CanaryFile(cf.Path.Trim(), cf.Content ?? ""));
                }
                if (canaryFiles.Count > 0)
                    canary = canaryFiles;
            }

            list.Add(new ProfileCheck(c.Name!.Trim(), c.Executable!.Trim(), args, timeout, canary));
        }

        checks = list;
        return true;
    }
}
