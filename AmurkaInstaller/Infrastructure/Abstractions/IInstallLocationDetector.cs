namespace AmurkaInstaller.Infrastructure.Abstractions;

public interface IInstallLocationDetector
{
    string DetermineDefaultInstallPath(bool isUpdate);
    string GetDiskSpaceInfo(string path);
}
