using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClaudeInteractiveProbe;

internal static class Program
{
    private const string OptInVariable = "LATTICEFLOW_RUN_CLAUDE_INTERACTIVE_PROBE";
    private static readonly TimeSpan HookTimeout = TimeSpan.FromMinutes(1);

    public static async Task<int> Main(string[] args)
    {
        if (args is ["capture-hook", var outputPath])
            return await CaptureHookAsync(outputPath);

        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Refusing to consume Claude usage. Set {OptInVariable}=1 to run the probe.");
            return 2;
        }

        var repositoryRoot = FindRepositoryRoot();
        var runRoot = Path.Combine(repositoryRoot, ".tmp", "claude-interactive-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);

        try
        {
            var version = (await RunAndCaptureAsync("claude", ["--version"], repositoryRoot)).Trim();
            var hookCommandPrefix = BuildHookCommandPrefix();
            var results = new List<object>();

            foreach (var mode in new[] { "inherited", "redirected", "winpty", "windows-terminal" })
                results.Add(await RunModeAsync(mode, runRoot, hookCommandPrefix));

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                claude_version = version,
                host = Environment.OSVersion.VersionString,
                probe_runtime = Environment.Version.ToString(),
                raw_evidence_policy = "captured under gitignored .tmp and deleted on exit",
                modes = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            TryDeleteDirectory(runRoot);
            TryDeleteDirectory(Path.GetDirectoryName(runRoot)!);
        }
    }

    private static async Task<object> RunModeAsync(string mode, string runRoot, string hookCommandPrefix)
    {
        var modeRoot = Path.Combine(runRoot, mode);
        var repository = Path.Combine(modeRoot, "repo");
        var payloadPath = Path.Combine(modeRoot, "stop-payload.json");
        var settingsPath = Path.Combine(modeRoot, "settings.json");
        var debugPath = Path.Combine(modeRoot, "claude-debug.log");
        Directory.CreateDirectory(repository);
        await RunAndCaptureAsync("git", ["init", "--quiet"], repository);

        var sentinel = $"LATTICEFLOW_INTERACTIVE_SENTINEL_{Guid.NewGuid():N}";
        var hookCommand = $"{hookCommandPrefix} capture-hook {ProbeContract.QuoteCommandArgument(ProbeContract.NormalizeHookPath(payloadPath))}";
        await File.WriteAllTextAsync(settingsPath, ProbeContract.BuildSettingsJson(hookCommand));

        var claudeArgs = new List<string>
        {
            "--model", "sonnet",
            "--dangerously-skip-permissions",
            "--permission-mode", "bypassPermissions",
            "--settings", settingsPath,
            "--debug-file", debugPath,
            $"Reply with exactly {sentinel} and nothing else. Do not use tools."
        };

        var psi = BuildStartInfo(mode, repository, claudeArgs);
        var commandShape = RenderCommand(psi.FileName, psi.ArgumentList);
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        if (psi.RedirectStandardOutput)
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        if (psi.RedirectStandardError)
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        var pid = process.Id;
        if (psi.RedirectStandardOutput) process.BeginOutputReadLine();
        if (psi.RedirectStandardError) process.BeginErrorReadLine();

        var detachedTerminal = mode is "winpty" or "windows-terminal";
        var hookObserved = await WaitForFileAsync(payloadPath, HookTimeout, process, detachedTerminal);
        await Task.Delay(750);
        var correlatedPids = await FindCorrelatedProcessIdsAsync(sentinel);
        var aliveAfterStop = correlatedPids.Count > 0 || !process.HasExited;
        int? exitCodeBeforeCleanup = !detachedTerminal && process.HasExited ? process.ExitCode : null;
        var payload = hookObserved ? await File.ReadAllTextAsync(payloadPath) : "{}";
        var shape = ProbeContract.InspectStopPayload(payload);

        string? lastMessage = null;
        string? cwd = null;
        string? transcriptPath = null;
        string? sessionId = null;
        bool? stopHookActive = null;
        using (var document = JsonDocument.Parse(payload))
        {
            var root = document.RootElement;
            lastMessage = GetString(root, "last_assistant_message");
            cwd = GetString(root, "cwd");
            transcriptPath = GetString(root, "transcript_path");
            sessionId = GetString(root, "session_id");
            stopHookActive = GetBoolean(root, "stop_hook_active");
        }

        var transcriptModel = TryFindModel(transcriptPath);
        foreach (var correlatedPid in correlatedPids)
            await KillProcessTreeAsync(correlatedPid);
        if (!detachedTerminal && !process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        if (!detachedTerminal)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
        }
        var remainingCorrelatedPids = await FindCorrelatedProcessIdsAsync(sentinel);
        var cleanedUp = remainingCorrelatedPids.Count == 0 && (detachedTerminal || process.HasExited);

        return new
        {
            mode,
            command = commandShape.Replace(settingsPath, "<ephemeral-settings>").Replace(debugPath, "<ephemeral-debug>"),
            started_pid = pid,
            correlated_process_pids = correlatedPids,
            hook_observed = hookObserved,
            alive_after_stop = aliveAfterStop,
            exit_code_before_cleanup = exitCodeBeforeCleanup,
            process_exited_after_cleanup = cleanedUp,
            sentinel_observed = lastMessage?.Contains(sentinel, StringComparison.Ordinal) == true,
            cwd_matches_disposable_repo = PathsEqual(cwd, repository),
            transcript_exists_during_probe = transcriptPath is not null && File.Exists(transcriptPath),
            transcript_model = transcriptModel,
            permission_mode = "bypassPermissions",
            settings_hook_marker = hookObserved,
            stop_hook_active = stopHookActive,
            payload_fields = shape,
            redacted_payload = new
            {
                session_id = sessionId is null ? null : "<uuid>",
                cwd = cwd is null ? null : "<disposable-repo>",
                transcript_path = transcriptPath is null ? null : "<user-claude-data>/projects/.../session.jsonl",
                last_assistant_message = lastMessage is null ? null : "<unique-sentinel-response>",
                stop_hook_active = stopHookActive
            },
            redirected_stdout_chars = stdout.Length,
            redirected_stderr = Redact(stderr.ToString(), runRoot)
        };
    }

    private static ProcessStartInfo BuildStartInfo(string mode, string workingDirectory, List<string> claudeArgs)
    {
        var isRedirected = mode == "redirected";
        var psi = new ProcessStartInfo
        {
            FileName = mode switch
            {
                "winpty" => @"C:\Program Files\Git\usr\bin\winpty.exe",
                "windows-terminal" => "wt.exe",
                _ => "claude"
            },
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = isRedirected,
            RedirectStandardOutput = isRedirected,
            RedirectStandardError = isRedirected,
            CreateNoWindow = isRedirected
        };
        if (mode == "winpty")
        {
            psi.ArgumentList.Add("-Xallow-non-tty");
            psi.ArgumentList.Add("claude.exe");
        }
        else if (mode == "windows-terminal")
        {
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add($"latticeflow-probe-{Guid.NewGuid():N}");
            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add("Latticeflow interactive contract probe");
            psi.ArgumentList.Add("claude.exe");
        }
        foreach (var argument in claudeArgs) psi.ArgumentList.Add(argument);
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        return psi;
    }

    private static async Task<int> CaptureHookAsync(string outputPath)
    {
        var payload = await Console.In.ReadToEndAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, payload);
        Console.Write("{}");
        return 0;
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout, Process process, bool ignoreLauncherExit)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return true;
            if (!ignoreLauncherExit && process.HasExited) return false;
            await Task.Delay(200);
        }
        return false;
    }

    private static async Task<string> RunAndCaptureAsync(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
        return await stdout;
    }

    private static string? TryFindModel(string? transcriptPath)
    {
        if (transcriptPath is null || !File.Exists(transcriptPath)) return null;
        foreach (var line in File.ReadLines(transcriptPath).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("model", out var model))
                    return model.GetString();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static async Task<List<int>> FindCorrelatedProcessIdsAsync(string sentinel)
    {
        var escaped = sentinel.Replace("'", "''");
        var script = $"Get-CimInstance Win32_Process | Where-Object {{ $_.Name -in @('claude.exe', 'WindowsTerminal.exe') -and $_.CommandLine -like '*{escaped}*' }} | Select-Object -ExpandProperty ProcessId";
        var output = await RunAndCaptureAsync("powershell.exe", ["-NoProfile", "-Command", script], Environment.CurrentDirectory);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.TryParse(value.Trim(), out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .ToList();
    }

    private static async Task KillProcessTreeAsync(int pid)
    {
        try { await RunAndCaptureAsync("taskkill.exe", ["/PID", pid.ToString(), "/T", "/F"], Environment.CurrentDirectory); }
        catch { }
    }

    private static string RenderCommand(string fileName, System.Collections.ObjectModel.Collection<string> arguments) =>
        string.Join(" ", new[] { fileName }.Concat(arguments).Select(ProbeContract.QuoteCommandArgument));

    private static string BuildHookCommandPrefix()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Probe process path is unavailable.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return ProbeContract.QuoteCommandArgument(ProbeContract.NormalizeHookPath(processPath));

        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        return $"{ProbeContract.QuoteCommandArgument(ProbeContract.NormalizeHookPath(processPath))} {ProbeContract.QuoteCommandArgument(ProbeContract.NormalizeHookPath(assemblyPath))}";
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, ".git"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Run the probe from the Latticeflow repository.");
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static bool PathsEqual(string? left, string right) => left is not null &&
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string Redact(string value, string runRoot) => value.Replace(runRoot, "<probe-run>").Trim();

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                Thread.Sleep(250);
            }
        }
    }

}
