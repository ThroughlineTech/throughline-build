namespace ThroughlineBuild.Cli;

/// <summary>
/// AOT-safe topic lookup for long-form help reference pages.
/// </summary>
public sealed class HelpTopicRegistry
{
    private readonly Dictionary<string, HelpTopic> _byName;

    private HelpTopicRegistry(IEnumerable<HelpTopic> topics)
    {
        _byName = new Dictionary<string, HelpTopic>(StringComparer.Ordinal);
        foreach (var topic in topics)
            _byName[topic.Name] = topic;
    }

    public HelpTopic? TryGet(string name) =>
        _byName.TryGetValue(name, out var topic) ? topic : null;

    public IReadOnlyList<string> TopicNames
    {
        get
        {
            var names = _byName.Keys.ToList();
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    public static HelpTopicRegistry Build() => new HelpTopicRegistry(
    [
        new HelpTopic("config", HelpTopicContent.Config),
        new HelpTopic("digest", HelpTopicContent.Digest),
        new HelpTopic("exit-codes", HelpTopicContent.ExitCodes),
        new HelpTopic("summary", HelpTopicContent.Summary),
    ]);
}
