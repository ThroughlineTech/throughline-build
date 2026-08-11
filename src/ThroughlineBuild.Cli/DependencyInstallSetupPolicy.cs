using ThroughlineBuild.Contracts;

namespace ThroughlineBuild.Cli;

internal static class DependencyInstallSetupPolicy
{
    public const string FindingCode = "review.checks.setup.installs_dependencies";

    public static CheckSpec? Find(
        IEnumerable<CheckSpec> checks,
        string projectInstallCommand) =>
        checks.FirstOrDefault(check =>
            check.Role == CheckRole.Setup &&
            Matches(projectInstallCommand, check.Executable, check.Arguments));

    public static bool Matches(
        string projectInstallCommand,
        string executable,
        IEnumerable<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(projectInstallCommand) ||
            string.IsNullOrWhiteSpace(executable))
            return false;

        var checkCommand = string.Join(
            " ",
            new[] { executable }.Concat(arguments).Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.Equals(
            Normalize(projectInstallCommand),
            Normalize(checkCommand),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string command) =>
        string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
