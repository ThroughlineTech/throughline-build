using System.Text;
using ThroughlineBuild.Scaffold;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Glue between the scaffold verb and op-doc-driven config derivation: builds the default worker,
/// asks it to derive a <see cref="ProjectProfile"/> from the op-doc, and writes the resulting
/// review/ship checks into .build/config.toml via <see cref="ConfigProfileWriter"/>.
///
/// Best-effort by design: every failure is reported loudly with an actionable hint but is swallowed
/// so it cannot change the scaffold exit code. The created ticket tree is the scaffold's contract;
/// a missed config derivation is a re-runnable nuisance, not a scaffold failure.
/// </summary>
public static class ScaffoldProfileRunner
{
    public static async Task RunAsync(
        string opDocPath,
        string cwd,
        WorkersConfig workers,
        bool force,
        CancellationToken ct)
    {
        Console.WriteLine("[scaffold] deriving review/ship checks from op-doc ...");

        if (!workers.Agents.TryGetValue(workers.DefaultAgent, out var agentCfg))
        {
            Warn($"no [workers.{workers.DefaultAgent}] config; cannot derive checks. " +
                 "Fix config and re-run, or set checks in .build/config.toml manually.");
            return;
        }

        string opDocMarkdown;
        try
        {
            opDocMarkdown = await File.ReadAllTextAsync(opDocPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn($"could not read op-doc '{opDocPath}': {ex.Message}");
            return;
        }

        var worker = WorkerAgentBuilder.Create(workers.DefaultAgent, agentCfg);
        var deriver = new ScaffoldProfileDeriver(worker);
        var timeout = TimeSpan.FromMinutes(Math.Clamp(workers.TimeoutMinutes, 1, 30));

        ProfileDerivationResult derivation;
        try
        {
            derivation = await deriver.DeriveAsync(opDocMarkdown, cwd, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warn($"derivation failed: {ex.Message}");
            return;
        }

        if (!derivation.Success || derivation.Profile is null)
        {
            Warn($"derivation failed: {derivation.FailureReason}. " +
                 "Set [[review.checks]] in .build/config.toml manually, then run 'build chain'.");
            return;
        }

        var profile = derivation.Profile;

        var configPath = BuildConfigLoader.FindConfigFile(cwd);
        if (configPath is null)
        {
            Warn(".build/config.toml not found; run 'build init' first, then re-scaffold.");
            return;
        }

        string configText;
        try { configText = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Warn($"could not read {configPath}: {ex.Message}");
            return;
        }

        var outcome = ConfigProfileWriter.Apply(configText, profile, force);

        if (outcome.SkipReason is not null && !outcome.Changed)
        {
            Console.WriteLine($"[scaffold] checks unchanged: {outcome.SkipReason}");
            return;
        }

        if (!outcome.Changed || outcome.NewText is null)
        {
            Console.WriteLine($"[scaffold] checks unchanged: {outcome.SkipReason ?? "already matched the derived profile"}");
            return;
        }

        try
        {
            await File.WriteAllTextAsync(configPath, outcome.NewText, new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn($"derived a profile but could not write {configPath}: {ex.Message}");
            return;
        }

        Console.WriteLine($"[scaffold] wrote derived checks to {configPath}: {outcome.Summary}");
        foreach (var c in profile.ReviewChecks)
            Console.WriteLine($"  review/{c.Name}: {c.Executable} {string.Join(' ', c.Arguments)}");
    }

    private static void Warn(string message) =>
        Console.Error.WriteLine($"[scaffold] warning: profile derivation skipped - {message}");
}
