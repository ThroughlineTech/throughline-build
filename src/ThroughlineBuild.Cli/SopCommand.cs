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

    public static async Task<int> ExecuteInstallAsync(
        IReadOnlyList<string> args,
        bool json,
        string startDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!SopInstallCommand.ValidateInvocation(args, json, output, error, out var usageExit))
            return usageExit;

        var configBootstrap = await CliBootstrap.CreateAsync(
            startDirectory,
            cancellationToken,
            requireTicketing: false).ConfigureAwait(false);
        if (configBootstrap.Failure is { } configFailure)
            return WriteBootstrapFailure(configFailure, json, output, error);

        using var configContext = configBootstrap.Context!;
        var ticketing = configContext.Config.Ticketing;
        if (!string.IsNullOrWhiteSpace(ticketing.PlaneProjectIdentifier))
        {
            return await SopInstallCommand.ExecuteInstallAsync(
                args,
                json,
                startDirectory,
                output,
                error,
                ticketing.PlaneProjectIdentifier,
                ticketing.PlaneProjectId,
                projectDiscovery: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var ticketingBootstrap = await CliBootstrap.CreateAsync(
            startDirectory,
            cancellationToken,
            requireTicketing: true).ConfigureAwait(false);
        if (ticketingBootstrap.Failure is { } ticketingFailure)
            return WriteBootstrapFailure(ticketingFailure, json, output, error);

        using var ticketingContext = ticketingBootstrap.Context!;
        return await SopInstallCommand.ExecuteInstallAsync(
            args,
            json,
            startDirectory,
            output,
            error,
            configuredProjectIdentifier: string.Empty,
            ticketingContext.Config.Ticketing.PlaneProjectId,
            ticketingContext.Ticketing,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static int WriteBootstrapFailure(
        CliBootstrapFailure failure,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (json)
            CliEnvelopeWriter.WriteError(output, failure.JsonErrorCode, failure.Message);
        else
            error.WriteLine($"{failure.HumanPrefix}: {failure.Message}");
        return failure.ExitCode;
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
        if (!TryParseRunMode(args, startDirectory, output, error, out var runMode, out var usageExit))
            return usageExit;

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
            var failed = BuildView(catalogEntry, doctor, sopText: null, runMode);
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

        CliEnvelopeWriter.WriteSopBrief(output, BuildView(catalogEntry, doctor, text, runMode));
        return 0;
    }

    private static bool TryParseRunMode(
        IReadOnlyList<string> args,
        string startDirectory,
        TextWriter output,
        TextWriter error,
        out SopRunModeView runMode,
        out int usageExit)
    {
        runMode = SopAdmission.StandardRunMode();
        usageExit = 0;
        if (args.Count == 3)
        {
            if (!SopAdmission.IsActiveFromEnvironment())
                return true;

            if (SopAdmission.TryCreateAdmissionRunModeFromEnvironment(
                startDirectory,
                out runMode,
                out var environmentError))
                return true;

            usageExit = Usage(json: true, output, error, environmentError);
            return false;
        }

        if (args.Count == 6 && string.Equals(args[3], SopAdmission.ModeName, StringComparison.Ordinal))
        {
            if (SopAdmission.TryCreateAdmissionRunMode(
                args[4],
                args[5],
                startDirectory,
                out runMode,
                out var admissionError))
                return true;

            usageExit = Usage(json: true, output, error, admissionError);
            return false;
        }

        usageExit = Usage(
            json: true,
            output,
            error,
            $"unknown argument for sop brief: {args[3]}");
        return false;
    }

    private static SopBriefView BuildView(
        SopCatalogEntry entry,
        SopDoctorView doctor,
        string? sopText,
        SopRunModeView runMode) => new(
        Name: entry.Name,
        SopSchemaVersion: SopBundleCatalog.SchemaVersion,
        SopVersion: BuildVersion.Current,
        BinaryVersion: BuildVersion.Current,
        Ready: doctor.Passed && !string.IsNullOrEmpty(sopText),
        SopText: sopText,
        Conductor: doctor.Conductor,
        Doctor: doctor,
        OwnedPaths: entry.OwnedPaths,
        RunMode: runMode);

    private static int Usage(bool json, TextWriter output, TextWriter error, string message)
    {
        CliEnvelopeWriter.WriteError(output, CliErrorCodes.Usage, message);
        return 2;
    }
}
