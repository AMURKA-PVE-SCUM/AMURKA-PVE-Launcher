using System.IO;
using Amurka.Shared.Core.Domain.Results;

namespace Amurka.Shared.IO;

public static class PathSecurityGuard
{
    public static bool IsPathInsideRoot(string rootDirectory, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            string fullRoot = Path.GetFullPath(rootDirectory);
            string rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            string fullCandidate = Path.GetFullPath(candidatePath);
            return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolveSafeChildPath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        string sanitizedRel = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(sanitizedRel))
            return null;

        try
        {
            string combined = Path.GetFullPath(Path.Combine(rootDirectory, sanitizedRel));
            return IsPathInsideRoot(rootDirectory, combined) ? combined : null;
        }
        catch
        {
            return null;
        }
    }

    public static Result<string> ValidateInstallDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result<string>.Failure("Укажите корректную папку установки.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            return Result<string>.Failure($"Некорректный путь установки: {ex.Message}");
        }

        string? root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath,
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Failure("Нельзя устанавливать программу в корень системного или локального диска. Выберите отдельную папку (например, D:\\Games\\AMURKA PVE Launcher).");
        }

        string[] forbiddenRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
        ];

        foreach (string forbidden in forbiddenRoots)
        {
            if (string.IsNullOrWhiteSpace(forbidden)) continue;
            string normForbidden = Path.GetFullPath(forbidden).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullPath, normForbidden, StringComparison.OrdinalIgnoreCase) ||
                IsPathInsideRoot(normForbidden, fullPath))
            {
                return Result<string>.Failure("Установка в системные каталоги Windows запрещена.");
            }
        }

        return Result<string>.Success(fullPath);
    }

    /// <summary>
    /// Проверяет, является ли файл разрешенным модификатором SCUM (.pak или .sig).
    /// </summary>
    public static bool HasModExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        return fileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".sig", StringComparison.OrdinalIgnoreCase);
    }
}
