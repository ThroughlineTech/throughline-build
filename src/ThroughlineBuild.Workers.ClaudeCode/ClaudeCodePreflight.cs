using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ThroughlineBuild.Workers.ClaudeCode;

/// <summary>
/// Why a preflight decided the configured Claude transport cannot run on this host. Kept
/// distinct from provider-quota, model, permission, and result-protocol failures so the
/// operator-facing message never conflates a capability gap with a usage/billing problem.
/// </summary>
public enum ClaudePreflightFailureKind
{
    None,
    ExecutableNotFound,
    VersionUndetectable,
    VersionTooOld,
    PlatformUnsupported,
}

/// <summary>
/// The outcome of a <see cref="ClaudeCodePreflight"/> capability check. <see cref="Supported"/>
/// is the gate; <see cref="Message"/> is the operator-facing explanation (already labelled as a
/// transport-capability check, not a quota/model/permission error).
/// </summary>
public sealed record ClaudePreflightResult(
    bool Supported,
    ClaudePreflightFailureKind Kind,
    string Message,
    Version? DetectedVersion,
    string? RawVersionOutput);

/// <summary>
/// Verifies that the configured <c>claude</c> executable, its version, and the host platform can
/// support a selected <see cref="ClaudeCodeTransport"/> BEFORE a phase runs. The interactive-hook
/// transport depends on undocumented <c>claude</c> behaviors (see <see cref="MinimumInteractiveVersion"/>),
/// so selecting it on an unsupported host fails clearly here rather than hanging a launch or
/// returning a wrong result. There is no silent fallback to <c>print</c>: a failed check is reported,
/// not worked around.
/// </summary>
public static class ClaudeCodePreflight
{
    // The interactive-hook transport depends on three undocumented claude behaviors that are not
    // part of any published API:
    //   1. an interactive session persists a transcript JSONL that records the initial user prompt
    //      verbatim (the per-run nonce the locator correlates on), with cwd, session id, usage, and
    //      assistant messages tagged stop_reason == end_turn;
    //   2. ~/.claude.json projects[cwd].hasTrustDialogAccepted is honored to skip the workspace-trust
    //      dialog on a fresh worktree;
    //   3. settings.json skipDangerousModePermissionPrompt is honored (with --dangerously-skip-permissions)
    //      to skip the one-time Bypass-Permissions acceptance dialog.
    // All three were confirmed by live probe against claude 2.1.177 across Windows, macOS, and Linux
    // (the cross-platform live gate; docs/heartbeat/HANDOFF.md). They are NOT an Anthropic-published
    // contract, and at least one (skipDangerousModePermissionPrompt, TLB-550) is recent - so 2.1.177
    // is the lowest version with positive evidence that all three hold. A lower floor may work but is
    // unvalidated and therefore unsupported: the preflight fails clearly below it rather than risk a
    // wrong result or a hung launch. 2.1.177 is recorded as the supported minimum on that capability
    // basis, not merely because it is the version that was tested; revise it downward only with
    // cross-platform validation on the older build.
    public static readonly Version MinimumInteractiveVersion = new(2, 1, 177);

    // First "N.N" or "N.N.N" token in claude --version output (e.g. "2.1.177 (Claude Code)").
    private static readonly Regex VersionToken =
        new(@"(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant);

    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>True on the three platforms the interactive hosts implement (Windows ConPTY, Unix PTY).</summary>
    public static bool IsInteractivePlatformSupported() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Parses the leading semantic version from <c>claude --version</c> output. Tolerant of suffixes
    /// ("2.1.177 (Claude Code)") and a missing patch ("2.1" -> 2.1.0). Returns null when no version
    /// token is present.
    /// </summary>
    public static Version? ParseVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput))
            return null;
        var match = VersionToken.Match(versionOutput);
        if (!match.Success)
            return null;
        int major = int.Parse(match.Groups[1].Value);
        int minor = int.Parse(match.Groups[2].Value);
        int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }

    /// <summary>
    /// Pure capability decision. <paramref name="executableResolved"/> is whether <c>claude</c> could
    /// be launched at all; <paramref name="rawVersionOutput"/> is its <c>--version</c> text (or null).
    /// The <c>print</c> transport is always supported (no version gate); only interactive-hook gates.
    /// </summary>
    public static ClaudePreflightResult Evaluate(
        ClaudeCodeTransport transport,
        bool executableResolved,
        string? rawVersionOutput)
    {
        if (transport == ClaudeCodeTransport.Print)
        {
            // The legacy print transport carries no interactive-capability requirement; report it as
            // supported. A missing executable still surfaces clearly at launch on the print path.
            var detected = ParseVersion(rawVersionOutput);
            return new ClaudePreflightResult(
                Supported: true,
                Kind: ClaudePreflightFailureKind.None,
                Message: "transport = \"print\": legacy headless path; no interactive-hook capability required.",
                DetectedVersion: detected,
                RawVersionOutput: rawVersionOutput);
        }

        const string distinguisher =
            " (This is an interactive-transport capability check - not a provider quota, model, permission, or WORKER_RESULT protocol error.)";
        const string rollback =
            " To roll back to the legacy path, set transport = \"print\" under [workers.claude-code].";

        if (!IsInteractivePlatformSupported())
            return new ClaudePreflightResult(false, ClaudePreflightFailureKind.PlatformUnsupported,
                "transport = \"interactive-hook\" is only supported on Windows, Linux, and macOS." + rollback + distinguisher,
                null, rawVersionOutput);

        if (!executableResolved)
            return new ClaudePreflightResult(false, ClaudePreflightFailureKind.ExecutableNotFound,
                "transport = \"interactive-hook\" requires a runnable claude executable, but it could not be launched. " +
                "Confirm 'claude --version' works and the [workers.claude-code] executable path is correct." + rollback + distinguisher,
                null, rawVersionOutput);

        var version = ParseVersion(rawVersionOutput);
        if (version is null)
            return new ClaudePreflightResult(false, ClaudePreflightFailureKind.VersionUndetectable,
                "transport = \"interactive-hook\" requires Claude Code >= " + MinimumInteractiveVersion +
                ", but the installed claude version could not be determined from 'claude --version'" +
                (string.IsNullOrWhiteSpace(rawVersionOutput) ? "" : $" (got: {rawVersionOutput!.Trim()})") + "." +
                rollback + distinguisher,
                null, rawVersionOutput);

        if (version < MinimumInteractiveVersion)
            return new ClaudePreflightResult(false, ClaudePreflightFailureKind.VersionTooOld,
                $"transport = \"interactive-hook\" requires Claude Code >= {MinimumInteractiveVersion}, but the installed claude is {version}. " +
                "Upgrade Claude Code." + rollback + distinguisher,
                version, rawVersionOutput);

        return new ClaudePreflightResult(true, ClaudePreflightFailureKind.None,
            $"transport = \"interactive-hook\": supported (claude {version} >= {MinimumInteractiveVersion}).",
            version, rawVersionOutput);
    }

    /// <summary>
    /// Resolves the executable, probes <c>claude --version</c> for interactive-hook, and evaluates. The
    /// version probe is skipped for <c>print</c> (no gate). Never throws for a missing executable or a
    /// probe failure - those become a non-supported result.
    /// </summary>
    public static async Task<ClaudePreflightResult> CheckAsync(
        string executable, ClaudeCodeTransport transport, CancellationToken ct)
    {
        if (transport == ClaudeCodeTransport.Print)
            return Evaluate(ClaudeCodeTransport.Print, executableResolved: true, rawVersionOutput: null);

        bool executableResolved = true;
        string? raw = null;
        try
        {
            raw = await ProbeVersionAsync(executable, ct).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            // claude is not on PATH / not at the configured path.
            executableResolved = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Any other probe failure leaves the version undetectable; Evaluate reports it.
            raw = null;
        }

        return Evaluate(transport, executableResolved, raw);
    }

    private static async Task<string?> ProbeVersionAsync(string executable, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = psi };
        process.Start(); // throws Win32Exception when the executable cannot be found.

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(VersionProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Hung --version: kill and report as undetectable rather than blocking the phase.
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }
}
