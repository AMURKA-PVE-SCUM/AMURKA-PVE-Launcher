namespace AmurkaInstaller.Infrastructure.Abstractions;

public interface IInstallerRegistryService
{
    string? GetExistingInstallLocation();
    void RegisterInstallation(string installDir, string version);
    void UnregisterInstallation();
}
