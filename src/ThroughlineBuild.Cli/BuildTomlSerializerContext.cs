using Tomlyn.Model;
using Tomlyn.Serialization;

namespace ThroughlineBuild.Cli;

[TomlSerializable(typeof(TomlTable))]
internal partial class BuildTomlSerializerContext : TomlSerializerContext
{
}
