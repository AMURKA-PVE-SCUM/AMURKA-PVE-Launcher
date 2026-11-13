using System.Text.Json.Serialization;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Core.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(LauncherConfig))]
[JsonSerializable(typeof(UpdateInfo))]
[JsonSerializable(typeof(ModItem))]
[JsonSerializable(typeof(List<ModItem>))]
[JsonSerializable(typeof(ManagedMod))]
[JsonSerializable(typeof(List<ManagedMod>))]
[JsonSerializable(typeof(Dictionary<string, ManagedMod>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
public partial class LauncherJsonContext : JsonSerializerContext
{
}
