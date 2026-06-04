using System.Text;

namespace ThroughlineBuild.Cli;

public static class HelpTopicRenderer
{
    public static string Render(HelpTopic topic)
    {
        var body = topic.Body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return body.EndsWith('\n') ? body : body + "\n";
    }

    public static string RenderUnknownTopic(string topicName, IReadOnlyList<string> validTopicNames)
    {
        var sb = new StringBuilder();
        sb.Append("Unknown help topic: ");
        sb.Append(topicName);
        sb.Append('\n');
        sb.Append("Valid topics:\n");
        foreach (var name in validTopicNames)
        {
            sb.Append("  ");
            sb.Append(name);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
