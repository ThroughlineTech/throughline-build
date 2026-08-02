using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Cli;

internal static class SopCommand
{
    public const int UnknownSopExitCode = 9;

    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count < 2)
            return Usage(json, output, error, "sop subcommand is required");

        return args[1] switch
        {
            "doctor" => SopDoctorCommand.Execute(args, json, startDirectory, output, error),
            "list" => SopListCommand.Execute(args, json, output, error),
            "brief" => SopBriefCommand.Execute(args, json, startDirectory, output, error),
            "install" or "upgrade" or "uninstall" or "status" =>
                SopInstallCommand.Execute(args, json, startDirectory, output, error),
            _ => Usage(json, output, error, $"unknown sop subcommand: {args[1]}"),
        };
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build sop <doctor|list|brief|install|upgrade|uninstall|status> ...");
        }

        return 2;
    }
}

internal static class SopListCommand
{
    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count > 2)
            return Usage(json, output, error, $"unknown argument for sop list: {args[2]}");

        var rows = SopBundleCatalog.All
            .Select(entry => new SopListItemView(entry.Name, BuildVersion.Current, entry.OwnedPaths))
            .ToList();

        if (json)
            CliEnvelopeWriter.WriteSopList(output, rows);
        else
            WriteHuman(output, rows);

        return 0;
    }

    private static void WriteHuman(TextWriter output, IReadOnlyList<SopListItemView> rows)
    {
        foreach (var row in rows)
            output.WriteLine($"{row.Name}\t{row.Version}");
    }

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        else
        {
            error.WriteLine($"Error: {message}");
            error.WriteLine("Usage: build sop list [--json]");
        }

        return 2;
    }
}

internal static class SopBriefCommand
{
    public static int Execute(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextWriter output,
        TextWriter error)
    {
        if (args.Count < 3)
            return Usage(json, output, error, "sop name is required");
        if (args.Count > 3)
            return Usage(json, output, error, $"unknown argument for sop brief: {args[3]}");

        var sopName = args[2];
        var catalogEntry = SopBundleCatalog.Find(sopName);
        if (catalogEntry is null)
        {
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.UnknownSop, $"unknown SOP: {sopName}");
            return SopCommand.UnknownSopExitCode;
        }

        var doctor = SopDoctorCommand.RunDoctor(startDirectory, BuildVersion.Current);
        if (!doctor.Passed)
        {
            var failed = BuildView(catalogEntry, doctor, sopText: null);
            CliEnvelopeWriter.WriteSopBrief(output, failed);
            return 1;
        }

        string text;
        try
        {
            text = SopResourceLoader.LoadProcedure(catalogEntry);
        }
        catch (InvalidOperationException ex)
        {
            CliEnvelopeWriter.WriteError(output, CliErrorCodes.Failure, ex.Message);
            return 1;
        }

        CliEnvelopeWriter.WriteSopBrief(output, BuildView(catalogEntry, doctor, text));
        return 0;
    }

    private static SopBriefView BuildView(SopCatalogEntry entry, SopDoctorView doctor, string? sopText) => new(
        Name: entry.Name,
        SopSchemaVersion: SopBundleCatalog.SchemaVersion,
        SopVersion: BuildVersion.Current,
        BinaryVersion: BuildVersion.Current,
        Ready: doctor.Passed && !string.IsNullOrEmpty(sopText),
        SopText: sopText,
        Conductor: doctor.Conductor,
        Doctor: doctor,
        OwnedPaths: entry.OwnedPaths);

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        return 2;
    }
}
