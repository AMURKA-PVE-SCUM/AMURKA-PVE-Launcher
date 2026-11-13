using System.Globalization;
using System.IO;
using System.Text;

namespace Amurka.Shared.Logging;

public static class CrashLogger
{
    private const long MaxCrashLogBytes = 2L * 1024 * 1024;
    private static readonly object CrashLock = new();

    public static void Log(string crashLogPath, string source, Exception? ex)
    {
        if (string.IsNullOrWhiteSpace(crashLogPath)) return;

        try
        {
            lock (CrashLock)
            {
                string? dir = Path.GetDirectoryName(crashLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(crashLogPath) && new FileInfo(crashLogPath).Length > MaxCrashLogBytes)
                {
                    string archived = crashLogPath + ".old";
                    File.Move(crashLogPath, archived, overwrite: true);
                }

                string entry = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}";
                byte[] bytes = Encoding.UTF8.GetBytes(entry);

                using var fs = new FileStream(crashLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes);
            }
        }
        catch
        {
            // Crash logging must not throw secondary exceptions
        }
    }
}
