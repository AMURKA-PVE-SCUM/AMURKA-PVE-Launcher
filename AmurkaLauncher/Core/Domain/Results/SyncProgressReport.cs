namespace AmurkaLauncher.Core.Domain.Results;

public readonly record struct SyncProgressReport(
    int Percent,
    string CurrentFile,
    long DownloadedBytes,
    long TotalBytes,
    string StatusText
);
