using Amurka.Shared.Core.Domain.Results;

namespace AmurkaInstaller.Infrastructure.Abstractions;

public interface IPayloadExtractor
{
    Result RunPreFlightChecks(string targetDir, long requiredBytes = 0);
    Task<Result> ExtractPayloadAsync(string targetDir, IProgress<(int percent, string status)> progress, CancellationToken ct = default);
}
