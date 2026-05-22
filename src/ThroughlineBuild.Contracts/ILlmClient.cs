namespace ThroughlineBuild.Contracts;

/// <summary>
/// Interface for invoking language models with both request-response and streaming patterns.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Invokes an LLM with a single response.
    /// </summary>
    /// <param name="modelId">The identifier of the model to invoke.</param>
    /// <param name="messages">The conversation messages to send to the model.</param>
    /// <param name="options">Configuration options for the invocation.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The LLM response containing content and token usage.</returns>
    Task<LlmResponse> InvokeAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);

    /// <summary>
    /// Invokes an LLM with streaming response.
    /// </summary>
    /// <param name="modelId">The identifier of the model to invoke.</param>
    /// <param name="messages">The conversation messages to send to the model.</param>
    /// <param name="options">Configuration options for the invocation.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>An async enumerable of stream events containing deltas and finality markers.</returns>
    IAsyncEnumerable<LlmStreamEvent> InvokeStreamAsync(
        string modelId,
        IReadOnlyList<LlmMessage> messages,
        InvocationOptions options,
        CancellationToken ct);
}

/// <summary>
/// Represents a message in an LLM conversation.
/// </summary>
/// <param name="Role">The role of the message sender (e.g., "user", "assistant", "system").</param>
/// <param name="Content">The textual content of the message.</param>
public record LlmMessage(string Role, string Content);

/// <summary>
/// Configuration options for an LLM invocation.
/// </summary>
/// <param name="MaxTokens">Maximum number of tokens in the response, if specified.</param>
/// <param name="Temperature">Sampling temperature (0.0 to 2.0), controlling response randomness, if specified.</param>
public record InvocationOptions(int? MaxTokens, double? Temperature);

/// <summary>
/// Response from an LLM invocation.
/// </summary>
/// <param name="Content">The generated content from the model.</param>
/// <param name="Usage">Token usage statistics for the invocation.</param>
public record LlmResponse(string Content, LlmUsage Usage);

/// <summary>
/// Token usage statistics for an LLM invocation.
/// </summary>
/// <param name="InputTokens">Number of tokens in the input prompt.</param>
/// <param name="OutputTokens">Number of tokens in the model output.</param>
public record LlmUsage(int InputTokens, int OutputTokens);

/// <summary>
/// Event in a streaming LLM response.
/// </summary>
/// <param name="Delta">The incremental text content from the stream.</param>
/// <param name="IsFinal">Indicates whether this is the final event in the stream.</param>
public record LlmStreamEvent(string Delta, bool IsFinal);
