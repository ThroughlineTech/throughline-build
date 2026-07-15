using ThroughlineBuild.Plane;

namespace ThroughlineBuild.Cli;

/// <summary>
/// Builds the <see cref="PlaneClientOptions"/> every config-driven verb hands to
/// <see cref="PlaneTicketingClient"/>. The options literal used to be duplicated at ten
/// call sites in Program.cs, which is how <c>RequestsPerMinute</c> stayed unwired long
/// after it was configurable in principle: the field existed on the options object, but
/// no site ever assigned it, so every deployment - self-hosted included - was pinned to
/// the Plane-Cloud-sized default (TLB-565).
///
/// This is the test-coverable seam for that mapping: config in, options out, no I/O.
/// Keep it the ONLY place the mapping is written, so a new option is either wired
/// everywhere or nowhere - never at nine sites out of ten.
///
/// Note <c>build init</c> does not route through here: it runs before a config file
/// exists, so it has no budget to read and keeps the default.
/// </summary>
public static class PlaneOptionsFactory
{
    public static PlaneClientOptions From(BuildConfig config, BuildSecrets secrets) => new()
    {
        BaseUrl = config.Ticketing.PlaneBaseUrl,
        ApiToken = secrets.PlaneApiToken,
        WorkspaceSlug = config.Ticketing.PlaneWorkspaceSlug,
        ProjectId = config.Ticketing.PlaneProjectId,
        ProjectIdentifier = config.Ticketing.PlaneProjectIdentifier,
        RequestsPerMinute = config.Ticketing.PlaneRequestsPerMinute
    };
}
