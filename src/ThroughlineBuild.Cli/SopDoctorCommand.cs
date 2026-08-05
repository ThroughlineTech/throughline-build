using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;
using Tomlyn;
using Tomlyn.Model;

namespace ThroughlineBuild.Cli;

public sealed record ConductorConfig(
    string MinBuildVersion,
    string BranchPrefix,
    string TicketPrefix,
    IReadOnlyList<string> SourceRoots,
    string ArchitectureMap,
    int ReworkCap,
    ConductorReviewConfig Review,
    ConstellationConfig Constellation);

public sealed record ConductorReviewConfig(
    IReadOnlyList<ConductorReviewInvariant> Invariants,
    ConductorReviewEscalation Escalation);

public sealed record ConductorReviewInvariant(
    string Id,
    string Statement,
    IReadOnlyList<string>? Paths = null,
    bool? BlocksDone = null);

public sealed record ConductorReviewEscalation(
    string ModelSize,
    IReadOnlyList<string> Paths);

public sealed record ConstellationConfig(
    string Platform,
    string ContractAuthority,
    IReadOnlyList<ConstellationSibling> Siblings);

public sealed record ConstellationSibling(
    string Platform,
    string Path,
    string TicketPrefix);

public sealed record SopDoctorFinding(
    string Code,
    string Path,
    string Message);

public sealed record SopDoctorView(
    string RepositoryRoot,
    string? ConductorPath,
    string? ConfigPath,
    string BuildVersion,
    bool Passed,
    string ReviewInvariantMode,
    ConductorConfig? Conductor,
    IReadOnlyList<SopDoctorFinding> Findings);

internal static class SopDoctorCommand
{
    private const int BinaryDefaultReworkCap = 3;
    private const string ScaffoldTicketPrefix = "TICKET";
    private const string ScaffoldInvariantStatement =
        "Replace this sentence with a true review invariant for this repository.";
    private const string ScaffoldPlatform = "unknown";
    private static readonly HashSet<string> ValidEscalationModelSizes = new(StringComparer.Ordinal)
    {
        "small",
        "medium",
        "large",
    };
    private static readonly HashSet<string> KnownRootKeys = new(StringComparer.Ordinal)
    {
        "conductor",
        "constellation",
    };
    private static readonly HashSet<string> KnownConductorKeys = new(StringComparer.Ordinal)
    {
        "min_build_version",
        "branch_prefix",
        "ticket_prefix",
        "source_roots",
        "architecture_map",
        "rework_cap",
        "review",
    };
    private static readonly HashSet<string> KnownConductorReviewKeys = new(StringComparer.Ordinal)
    {
        "invariants",
        "escalation",
    };
    private static readonly HashSet<string> KnownInvariantKeys = new(StringComparer.Ordinal)
    {
        "id",
        "statement",
        "paths",
        "blocks_done",
    };
    private static readonly HashSet<string> KnownEscalationKeys = new(StringComparer.Ordinal)
    {
        "model_size",
        "paths",
    };
    private static readonly HashSet<string> KnownConstellationKeys = new(StringComparer.Ordinal)
    {
        "platform",
        "contract_authority",
        "siblings",
    };
    private static readonly HashSet<string> KnownSiblingKeys = new(StringComparer.Ordinal)
    {
        "platform",
        "path",
        "ticket_prefix",
    };

    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count < 2)
            return Usage(json, output, error, "sop subcommand is required");
        if (!string.Equals(args[1], "doctor", StringComparison.Ordinal))
            return Usage(json, output, error, $"unknown sop subcommand: {args[1]}");
        if (args.Count > 2)
            return Usage(json, output, error, $"unknown argument for sop doctor: {args[2]}");

        var report = RunDoctor(startDirectory, BuildVersion.Current);
        if (json)
            CliEnvelopeWriter.WriteSopDoctor(output, report);
        else
            WriteHuman(output, report);

        return report.Passed ? 0 : 1;
    }

    internal static SopDoctorView RunDoctor(string startDirectory, string runningBuildVersion) =>
        RunDoctor(startDirectory, runningBuildVersion, validateReviewChecks: true, validateStubs: true);

    internal static SopDoctorView RunConductorOnlyDoctor(string startDirectory, string runningBuildVersion) =>
        RunDoctor(startDirectory, runningBuildVersion, validateReviewChecks: false, validateStubs: false);

    private static SopDoctorView RunDoctor(
        string startDirectory,
        string runningBuildVersion,
        bool validateReviewChecks,
        bool validateStubs)
    {
        var findings = new List<SopDoctorFinding>();
        var conductorPath = FindConductorFile(startDirectory);
        var repositoryRoot = conductorPath is null
            ? ResolveRepositoryRootFallback(startDirectory)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(conductorPath)!, ".."));

        ConductorConfig? conductor = null;
        if (conductorPath is null)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.file.missing",
                "conductor",
                "missing .build/conductor.toml"));
        }
        else
        {
            conductor = ReadConductor(conductorPath, runningBuildVersion, findings);
        }

        var configPath = Path.Combine(repositoryRoot, ".build", "config.toml");
        if (validateReviewChecks)
            ValidateReviewChecks(configPath, findings);
        if (validateStubs)
            ValidateEmittedStubs(repositoryRoot, findings);

        return new SopDoctorView(
            RepositoryRoot: repositoryRoot,
            ConductorPath: conductorPath,
            ConfigPath: File.Exists(configPath) ? configPath : null,
            BuildVersion: runningBuildVersion,
            Passed: findings.Count == 0,
            ReviewInvariantMode: "shape_only",
            Conductor: conductor,
            Findings: findings.AsReadOnly());
    }

    private static void ValidateEmittedStubs(string repositoryRoot, List<SopDoctorFinding> findings)
    {
        foreach (var result in SopInstaller.InspectInstalledOrPresentEmittedStubs(repositoryRoot, SopBundleCatalog.All))
        {
            if (string.Equals(result.Status, "clean", StringComparison.Ordinal))
                continue;

            // Drift is a claim about content; an unanswerable scope question is not, so it is
            // reported in its own words rather than as drift.
            var message = string.Equals(result.Status, "scope_unavailable", StringComparison.Ordinal)
                ? $"emitted stub scope unavailable: {result.Message}"
                : $"emitted stub drift: {result.Message}";
            findings.Add(new SopDoctorFinding(
                StubFindingCode(result.Status),
                result.Path,
                message));
        }
    }

    private static string StubFindingCode(string status) => status switch
    {
        "missing" => "sop.stub.missing",
        "modified" => "sop.stub.modified",
        "not_regular" => "sop.stub.not_regular",
        "unsafe_path" => "sop.stub.unsafe_path",
        "invalid_catalog" => "sop.stub.invalid_catalog",
        "scope_unavailable" => "sop.stub.scope_unavailable",
        _ => "sop.stub.drift",
    };

    internal static string? FindConductorFile(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var buildDirectory = Path.Combine(current.FullName, ".build");
            var candidate = Path.Combine(buildDirectory, "conductor.toml");
            if (File.Exists(candidate))
                return candidate;
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(buildDirectory) || Directory.Exists(gitPath) || File.Exists(gitPath))
                return null;
            current = current.Parent;
        }

        return null;
    }

    private static string ResolveRepositoryRootFallback(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.Exists(Path.Combine(current.FullName, ".build")))
                return current.FullName;
            current = current.Parent;
        }

        return Path.GetFullPath(startDirectory);
    }

    private static ConductorConfig? ReadConductor(
        string conductorPath,
        string runningBuildVersion,
        List<SopDoctorFinding> findings)
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize(
                File.ReadAllText(conductorPath),
                BuildTomlSerializerContext.Default.TomlTable)
                ?? throw new ConfigException("TOML document produced no root table");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.unreadable",
                "conductor",
                $"failed to read .build/conductor.toml: {ex.Message}"));
            return null;
        }
        catch (Exception ex)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.malformed",
                "conductor",
                $"failed to parse .build/conductor.toml: {ex.Message}"));
            return null;
        }

        AddUnknownKeyFindings(root, string.Empty, KnownRootKeys, findings);

        if (!TryGetTable(root, "conductor", "conductor", findings, out var conductor))
            return null;

        AddUnknownKeyFindings(conductor, "conductor", KnownConductorKeys, findings);

        var minBuildVersion = RequiredString(conductor, "min_build_version", "conductor.min_build_version", findings);
        if (minBuildVersion is not null)
            ValidateMinBuildVersion(minBuildVersion, runningBuildVersion, findings);

        var branchPrefix = RequiredString(conductor, "branch_prefix", "conductor.branch_prefix", findings);
        var ticketPrefix = RequiredString(conductor, "ticket_prefix", "conductor.ticket_prefix", findings);
        if (string.Equals(ticketPrefix, ScaffoldTicketPrefix, StringComparison.Ordinal))
        {
            findings.Add(new SopDoctorFinding(
                "conductor.ticket_prefix.placeholder",
                "conductor.ticket_prefix",
                ".build/conductor.toml key 'conductor.ticket_prefix' still holds the scaffold " +
                "placeholder 'TICKET'; replace it with the prefix used by this repository's ticket IDs"));
        }
        var sourceRoots = RequiredStringList(conductor, "source_roots", "conductor.source_roots", findings);
        var architectureMap = RequiredString(conductor, "architecture_map", "conductor.architecture_map", findings);
        var reworkCap = RequiredInt(conductor, "rework_cap", "conductor.rework_cap", findings);
        ValidateReworkCap(reworkCap, findings);

        var review = ReadReview(conductor, findings);
        var constellation = ReadConstellation(root, findings);

        return new ConductorConfig(
            MinBuildVersion: minBuildVersion ?? string.Empty,
            BranchPrefix: branchPrefix ?? string.Empty,
            TicketPrefix: ticketPrefix ?? string.Empty,
            SourceRoots: sourceRoots,
            ArchitectureMap: architectureMap ?? string.Empty,
            ReworkCap: reworkCap ?? BinaryDefaultReworkCap,
            Review: review,
            Constellation: constellation);
    }

    private static ConductorReviewConfig ReadReview(
        TomlTable conductor,
        List<SopDoctorFinding> findings)
    {
        if (!TryGetTable(conductor, "review", "conductor.review", findings, out var review))
        {
            return new ConductorReviewConfig(
                Array.Empty<ConductorReviewInvariant>(),
                new ConductorReviewEscalation(string.Empty, Array.Empty<string>()));
        }

        AddUnknownKeyFindings(review, "conductor.review", KnownConductorReviewKeys, findings);

        var invariants = ReadInvariants(review, findings);
        var escalation = ReadEscalation(review, findings);
        return new ConductorReviewConfig(invariants, escalation);
    }

    private static IReadOnlyList<ConductorReviewInvariant> ReadInvariants(
        TomlTable review,
        List<SopDoctorFinding> findings)
    {
        if (!review.TryGetValue("invariants", out var raw) || raw is not TomlTableArray array)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.review.invariants.missing",
                "conductor.review.invariants",
                "missing [[conductor.review.invariants]] entries"));
            return Array.Empty<ConductorReviewInvariant>();
        }

        if (array.Count == 0)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.review.invariants.empty",
                "conductor.review.invariants",
                "[[conductor.review.invariants]] must contain at least one entry"));
            return Array.Empty<ConductorReviewInvariant>();
        }

        var invariants = new List<ConductorReviewInvariant>(array.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < array.Count; i++)
        {
            var entry = array[i];
            var path = $"conductor.review.invariants[{i}]";
            AddUnknownKeyFindings(entry, path, KnownInvariantKeys, findings);
            var id = RequiredString(entry, "id", $"{path}.id", findings);
            var statement = RequiredString(entry, "statement", $"{path}.statement", findings);
            if (string.Equals(statement, ScaffoldInvariantStatement, StringComparison.Ordinal))
            {
                findings.Add(new SopDoctorFinding(
                    "conductor.review.invariants.statement.placeholder",
                    $"{path}.statement",
                    $".build/conductor.toml key '{path}.statement' still holds the scaffold placeholder; " +
                    "replace it with a true review invariant for this repository"));
            }
            if (id is not null && !seen.Add(id))
            {
                findings.Add(new SopDoctorFinding(
                    "conductor.review.invariants.duplicate_id",
                    $"{path}.id",
                    $"duplicate review invariant id '{id}'"));
            }

            var paths = OptionalStringList(entry, "paths", $"{path}.paths", findings);
            var blocksDone = OptionalBool(entry, "blocks_done", $"{path}.blocks_done", findings);
            invariants.Add(new ConductorReviewInvariant(
                id ?? string.Empty,
                statement ?? string.Empty,
                paths,
                blocksDone));
        }

        return invariants.AsReadOnly();
    }

    private static ConductorReviewEscalation ReadEscalation(
        TomlTable review,
        List<SopDoctorFinding> findings)
    {
        if (!TryGetTable(review, "escalation", "conductor.review.escalation", findings, out var escalation))
            return new ConductorReviewEscalation(string.Empty, Array.Empty<string>());

        AddUnknownKeyFindings(escalation, "conductor.review.escalation", KnownEscalationKeys, findings);

        var modelSize = RequiredString(
            escalation,
            "model_size",
            "conductor.review.escalation.model_size",
            findings);
        if (modelSize is not null)
        {
            modelSize = modelSize.ToLowerInvariant();
            if (!ValidEscalationModelSizes.Contains(modelSize))
            {
                findings.Add(new SopDoctorFinding(
                    "conductor.review.escalation.model_size.invalid",
                    "conductor.review.escalation.model_size",
                    "model_size must be small, medium, or large"));
            }
        }

        var paths = RequiredStringList(
            escalation,
            "paths",
            "conductor.review.escalation.paths",
            findings);
        return new ConductorReviewEscalation(modelSize ?? string.Empty, paths);
    }

    private static ConstellationConfig ReadConstellation(
        TomlTable root,
        List<SopDoctorFinding> findings)
    {
        if (!TryGetTable(root, "constellation", "constellation", findings, out var constellation))
            return new ConstellationConfig(string.Empty, string.Empty, Array.Empty<ConstellationSibling>());

        AddUnknownKeyFindings(constellation, "constellation", KnownConstellationKeys, findings);

        var platform = RequiredString(constellation, "platform", "constellation.platform", findings);
        if (string.Equals(platform, ScaffoldPlatform, StringComparison.Ordinal))
        {
            findings.Add(new SopDoctorFinding(
                "constellation.platform.placeholder",
                "constellation.platform",
                ".build/conductor.toml key 'constellation.platform' still holds the scaffold " +
                "placeholder 'unknown'; replace it with the repository's actual platform identifier"));
        }
        var contractAuthority = RequiredString(
            constellation,
            "contract_authority",
            "constellation.contract_authority",
            findings);
        var siblings = ReadSiblings(constellation, findings);

        return new ConstellationConfig(
            platform ?? string.Empty,
            contractAuthority ?? string.Empty,
            siblings);
    }

    private static IReadOnlyList<ConstellationSibling> ReadSiblings(
        TomlTable constellation,
        List<SopDoctorFinding> findings)
    {
        if (!constellation.TryGetValue("siblings", out var raw))
            return Array.Empty<ConstellationSibling>();
        if (raw is not TomlTableArray array)
        {
            findings.Add(new SopDoctorFinding(
                "constellation.siblings.invalid",
                "constellation.siblings",
                "constellation siblings must be declared as [[constellation.siblings]] entries"));
            return Array.Empty<ConstellationSibling>();
        }

        var siblings = new List<ConstellationSibling>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var entry = array[i];
            var path = $"constellation.siblings[{i}]";
            AddUnknownKeyFindings(entry, path, KnownSiblingKeys, findings);
            var platform = RequiredString(entry, "platform", $"{path}.platform", findings);
            var siblingPath = RequiredString(entry, "path", $"{path}.path", findings);
            var ticketPrefix = RequiredString(entry, "ticket_prefix", $"{path}.ticket_prefix", findings);
            siblings.Add(new ConstellationSibling(
                platform ?? string.Empty,
                siblingPath ?? string.Empty,
                ticketPrefix ?? string.Empty));
        }

        return siblings.AsReadOnly();
    }

    private static void ValidateMinBuildVersion(
        string minBuildVersion,
        string runningBuildVersion,
        List<SopDoctorFinding> findings)
    {
        if (!VersionCore.TryParse(minBuildVersion, out var required))
        {
            findings.Add(new SopDoctorFinding(
                "conductor.min_build_version.malformed",
                "conductor.min_build_version",
                "min_build_version must be a semantic version like 0.1.0"));
            return;
        }

        if (!VersionCore.TryParse(runningBuildVersion, out var running))
        {
            findings.Add(new SopDoctorFinding(
                "conductor.min_build_version.running_malformed",
                "conductor.min_build_version",
                $"running build version '{runningBuildVersion}' is not comparable"));
            return;
        }

        if (required.CompareTo(running) > 0)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.min_build_version.newer_than_binary",
                "conductor.min_build_version",
                $"min_build_version {minBuildVersion} is newer than running build {runningBuildVersion}"));
        }
    }

    private static void ValidateReworkCap(int? reworkCap, List<SopDoctorFinding> findings)
    {
        if (reworkCap is null)
            return;
        if (reworkCap <= 0)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.rework_cap.invalid",
                "conductor.rework_cap",
                "rework_cap must be a positive integer"));
            return;
        }

        if (reworkCap > BinaryDefaultReworkCap)
        {
            findings.Add(new SopDoctorFinding(
                "conductor.rework_cap.loosened",
                "conductor.rework_cap",
                $"rework_cap may tighten the binary default of {BinaryDefaultReworkCap}, not loosen it"));
        }
    }

    private static void ValidateReviewChecks(string configPath, List<SopDoctorFinding> findings)
    {
        if (!File.Exists(configPath))
        {
            findings.Add(new SopDoctorFinding(
                "review.checks.empty",
                "review.checks",
                ".build/config.toml was not found, so [[review.checks]] is empty"));
            return;
        }

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize(
                File.ReadAllText(configPath),
                BuildTomlSerializerContext.Default.TomlTable)
                ?? throw new ConfigException("TOML document produced no root table");
        }
        catch (Exception ex)
        {
            findings.Add(new SopDoctorFinding(
                "review.config.malformed",
                "review",
                $"failed to parse .build/config.toml for [[review.checks]]: {ex.Message}"));
            return;
        }

        if (!root.TryGetValue("review", out var reviewRaw) || reviewRaw is not TomlTable review ||
            !review.TryGetValue("checks", out var checksRaw) || checksRaw is not TomlTableArray checks ||
            checks.Count == 0)
        {
            findings.Add(new SopDoctorFinding(
                "review.checks.empty",
                "review.checks",
                "[[review.checks]] must contain at least one runnable check"));
            return;
        }

        var hasBlockingCheck = false;
        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            if (!check.TryGetValue("name", out var nameRaw) ||
                nameRaw is not string name ||
                string.IsNullOrEmpty(name))
            {
                findings.Add(new SopDoctorFinding(
                    "review.checks.name.invalid",
                    $"review.checks[{i}].name",
                    "review check must have a non-empty name"));
            }

            if (!check.TryGetValue("executable", out var executableRaw) ||
                executableRaw is not string executable ||
                string.IsNullOrWhiteSpace(executable))
            {
                findings.Add(new SopDoctorFinding(
                    "review.checks.command.missing",
                    $"review.checks[{i}].executable",
                    "review check has no runnable command; set executable to a non-empty command name"));
            }

            var rolePath = $"review.checks[{i}].role";
            if (!check.TryGetValue("role", out var roleRaw))
            {
                hasBlockingCheck = true;
            }
            else if (roleRaw is not string role || string.IsNullOrWhiteSpace(role))
            {
                findings.Add(new SopDoctorFinding(
                    "review.checks.role.invalid",
                    rolePath,
                    "review check role must be gating, advisory, or setup"));
            }
            else
            {
                switch (role.ToLowerInvariant())
                {
                    case "gating":
                    case "setup":
                        hasBlockingCheck = true;
                        break;
                    case "advisory":
                        break;
                    default:
                        findings.Add(new SopDoctorFinding(
                            "review.checks.role.invalid",
                            rolePath,
                            "review check role must be gating, advisory, or setup"));
                        break;
                }
            }
        }

        if (!hasBlockingCheck)
        {
            findings.Add(new SopDoctorFinding(
                "review.checks.no_gating",
                "review.checks",
                "[[review.checks]] must include at least one setup or gating check"));
        }
    }

    private static void AddUnknownKeyFindings(
        TomlTable table,
        string path,
        HashSet<string> knownKeys,
        List<SopDoctorFinding> findings)
    {
        foreach (var key in table.Keys)
        {
            if (knownKeys.Contains(key))
                continue;

            var fullPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
            findings.Add(new SopDoctorFinding(
                "conductor.unknown_key",
                fullPath,
                $"unknown key '{fullPath}' in .build/conductor.toml"));
        }
    }

    private static bool TryGetTable(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings,
        out TomlTable result)
    {
        if (!table.TryGetValue(key, out var raw) || raw is not TomlTable nested)
        {
            findings.Add(new SopDoctorFinding(
                $"{path}.missing",
                path,
                $"missing required TOML table [{path}]"));
            result = new TomlTable();
            return false;
        }

        result = nested;
        return true;
    }

    private static string? RequiredString(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings)
    {
        if (!table.TryGetValue(key, out var raw))
        {
            findings.Add(new SopDoctorFinding($"{path}.missing", path, $"missing required key '{key}'"));
            return null;
        }

        if (raw is not string value || string.IsNullOrWhiteSpace(value))
        {
            findings.Add(new SopDoctorFinding($"{path}.invalid", path, $"key '{key}' must be a non-empty string"));
            return null;
        }

        return value.Trim();
    }

    private static int? RequiredInt(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings)
    {
        if (!table.TryGetValue(key, out var raw))
        {
            findings.Add(new SopDoctorFinding($"{path}.missing", path, $"missing required key '{key}'"));
            return null;
        }

        if (raw is long value && value >= int.MinValue && value <= int.MaxValue)
            return (int)value;
        if (raw is int intValue)
            return intValue;

        findings.Add(new SopDoctorFinding($"{path}.invalid", path, $"key '{key}' must be an integer"));
        return null;
    }

    private static IReadOnlyList<string> RequiredStringList(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings)
    {
        var values = OptionalStringList(table, key, path, findings);
        if (values is null || values.Count == 0)
        {
            findings.Add(new SopDoctorFinding(
                $"{path}.empty",
                path,
                $"key '{key}' must contain at least one non-empty string"));
            return Array.Empty<string>();
        }

        return values;
    }

    private static IReadOnlyList<string>? OptionalStringList(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings)
    {
        if (!table.TryGetValue(key, out var raw))
            return null;
        if (raw is not TomlArray array)
        {
            findings.Add(new SopDoctorFinding($"{path}.invalid", path, $"key '{key}' must be an array of strings"));
            return null;
        }

        var values = new List<string>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not string value || string.IsNullOrWhiteSpace(value))
            {
                findings.Add(new SopDoctorFinding(
                    $"{path}.invalid_entry",
                    $"{path}[{i}]",
                    $"key '{key}' must contain only non-empty strings"));
                continue;
            }

            values.Add(value.Trim());
        }

        return values.Count == 0 ? null : values.AsReadOnly();
    }

    private static bool? OptionalBool(
        TomlTable table,
        string key,
        string path,
        List<SopDoctorFinding> findings)
    {
        if (!table.TryGetValue(key, out var raw))
            return null;
        if (raw is bool value)
            return value;

        findings.Add(new SopDoctorFinding($"{path}.invalid", path, $"key '{key}' must be a boolean"));
        return null;
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
            error.WriteLine("Usage: build sop doctor [--json]");
        }

        return 2;
    }

    private static void WriteHuman(TextWriter output, SopDoctorView report)
    {
        output.WriteLine(report.Passed ? "sop doctor passed" : "sop doctor failed");
        output.WriteLine($"conductor: {report.ConductorPath ?? "(missing)"}");
        output.WriteLine($"config: {report.ConfigPath ?? "(missing)"}");
        output.WriteLine("review invariants: shape only; truth not evaluated");
        foreach (var finding in report.Findings)
            output.WriteLine($"[{finding.Code}] {finding.Path}: {finding.Message}");
    }

    private readonly record struct VersionCore(int Major, int Minor, int Patch) : IComparable<VersionCore>
    {
        public int CompareTo(VersionCore other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0)
                return major;
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0)
                return minor;
            return Patch.CompareTo(other.Patch);
        }

        public static bool TryParse(string raw, out VersionCore version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var trimmed = raw.Trim();
            var suffixStart = trimmed.IndexOfAny(['-', '+']);
            var core = suffixStart >= 0 ? trimmed[..suffixStart] : trimmed;
            var parts = core.Split('.');
            if (parts.Length != 3)
                return false;

            if (!TryParsePart(parts[0], out var major) ||
                !TryParsePart(parts[1], out var minor) ||
                !TryParsePart(parts[2], out var patch))
                return false;

            version = new VersionCore(major, minor, patch);
            return true;
        }

        private static bool TryParsePart(string raw, out int value)
        {
            if (!int.TryParse(raw, out value))
                return false;
            return value >= 0;
        }
    }
}
