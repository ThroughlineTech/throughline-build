using System.Collections.Generic;

namespace ThroughlineBuild.ModelClient;

public interface IModelClient
{
    Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default);
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default);
}

public record ProviderConfig(
    string BaseUrl,
    string AuthScheme,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string Vendor,
    TimeSpan DefaultTimeout
);
