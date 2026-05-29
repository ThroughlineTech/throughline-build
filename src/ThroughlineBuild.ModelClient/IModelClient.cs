using System.Collections.Generic;

namespace ThroughlineBuild.ModelClient;

public interface IModelClient
{
    Task<ModelResponse> SendAsync(ModelRequest request, CancellationToken ct = default);
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default);
}

/// <summary>Configuration for an IModelClient implementation.</summary>
/// <remarks>
/// Anthropic shape: AuthScheme="x-api-key", ExtraHeaders={"x-api-key":"{key}","anthropic-version":"2023-06-01"}, Vendor="anthropic"
/// OpenAI-compatible shape: AuthScheme="Bearer", ExtraHeaders={"Authorization":"Bearer {key}"}, Vendor="openai"
/// No-auth local shape (Ollama): AuthScheme="none", ExtraHeaders={}, Vendor="ollama"
/// </remarks>
public record ProviderConfig(
    string BaseUrl,
    string AuthScheme,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string Vendor,
    TimeSpan DefaultTimeout
);
