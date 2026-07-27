namespace ThroughlineBuild.Commands;

public sealed class CliVerbRegistry
{
    private readonly Dictionary<string, ICliVerb> _verbs = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ICliVerb> Verbs => _verbs.Values;

    public void Register(ICliVerb verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        if (string.IsNullOrWhiteSpace(verb.Name))
            throw new ArgumentException("verb name must be non-empty", nameof(verb));
        if (!_verbs.TryAdd(verb.Name, verb))
            throw new InvalidOperationException($"verb '{verb.Name}' is already registered");
    }

    public bool TryGet(string name, out ICliVerb? verb)
    {
        if (string.IsNullOrEmpty(name))
        {
            verb = null;
            return false;
        }

        return _verbs.TryGetValue(name, out verb);
    }
}
