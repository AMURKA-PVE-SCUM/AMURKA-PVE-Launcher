using AmurkaLauncher.Core.Domain.ValueObjects;
using AmurkaLauncher.Models;

namespace AmurkaLauncher.Infrastructure.Abstractions;

public interface IInstalledStateRepository
{
    Dictionary<string, ManagedMod> ReadState(ModsPath modsPath);
    void WriteState(ModsPath modsPath, Dictionary<string, ManagedMod> state);
    void RecordVerifiedMods(IEnumerable<ModItem> mods, ModsPath modsPath);
    List<string> ScanInstalledModFiles(ModsPath modsPath);
}
