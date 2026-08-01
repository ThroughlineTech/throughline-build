using System.Diagnostics;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Helpers;

public static class WorktreeLeaseConstants
{
    public const int ManifestSchemaVersion = 1;
    public const string ManifestFileName = ".build-worktree-lease.json";
}

public sealed record WorktreeInstallRecord(
    string Status,
    long DurationMilliseconds,
    string? FailureReason = null);

public sealed record WorktreeLeaseManifest(
    int SchemaVersion,
    string Ticket,
    string Slug,
    string Branch,
    string BaseSha,
    string RepositoryPath,
    string MainWorktreePath,
    string WorktreeRoot,
    string WorktreePath,
    IReadOnlyList<string> SeededFiles,
    IReadOnlyList<string> LeasedResources,
    WorktreeInstallRecord Install);

public sealed record WorktreeLeaseOptions(
    string RepositoryPath,
    string MainWorktreePath,
    string WorktreeRoot,
    IReadOnlyList<string> SeedAllowlist,
    string InstallCommand);

public sealed record WorktreeLeaseRequest(
    string Ticket,
    string? Slug = null,
    string? BaseRef = null,
    string? RequiredSeed = null);

public sealed record WorktreeLeaseResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    WorktreeLeaseManifest? Manifest);

public sealed record WorktreeLeaseListResult(
    IReadOnlyList<WorktreeLeaseManifest> Leases,
    IReadOnlyList<string> UnmanifestedDirectories);

public interface IInstallCommandRunner
{
    Task<InstallCommandResult> RunAsync(string command, string workingDirectory, CancellationToken ct);
}

public sealed record InstallCommandResult(bool Success, string? FailureReason);

public sealed class ProcessInstallCommandRunner : IInstallCommandRunner
{
    public async Task<InstallCommandResult> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new InstallCommandResult(true, null);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var output = (await stderr.ConfigureAwait(false)).Trim();
                _ = await stdout.ConfigureAwait(false);
                return process.ExitCode == 0
                    ? new InstallCommandResult(true, null)
                    : new InstallCommandResult(
                        false,
                        output.Length == 0
                            ? $"install command exited with code {process.ExitCode}"
                            : $"install command exited with code {process.ExitCode}: {output}");
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Cancellation still propagates. Rollback remains the caller's job.
                }
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new InstallCommandResult(false, ex.Message);
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorktreeLeaseManifest))]
internal partial class WorktreeLeaseJsonContext : JsonSerializerContext { }
