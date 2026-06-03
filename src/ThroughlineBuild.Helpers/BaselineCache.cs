namespace ThroughlineBuild.Helpers;

/// <summary>
/// Per-chain-invocation cache keyed by origin/target_branch SHA.
/// Holds at most one entry; a SHA change invalidates and replaces the cached entry.
/// Thread-safe: a simple lock is sufficient because ChainPhase serializes child ships
/// via SemaphoreSlim(1,1), and double-computation under rare concurrent conditions is safe
/// (both sides compute the same set).
/// </summary>
public sealed class BaselineCache
{
    private readonly object _lock = new();
    private string? _cachedSha;
    private IReadOnlySet<string>? _failingTests;

    public bool TryGet(string sha, out IReadOnlySet<string> failing)
    {
        lock (_lock)
        {
            if (_cachedSha == sha && _failingTests is not null)
            {
                failing = _failingTests;
                return true;
            }
            failing = default!;
            return false;
        }
    }

    public void Set(string sha, IReadOnlySet<string> failing)
    {
        lock (_lock)
        {
            _cachedSha = sha;
            _failingTests = failing;
        }
    }
}
