using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Amurka.Shared.Platform;

public static partial class MemoryOptimizer
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// Полный сброс неиспользуемого рабочего набора (Working Set) процесса в Windows.
    /// Выполняет уплотнение LOH (кучи больших объектов) и возвращает физические страницы операционной системе.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (OperatingSystem.IsWindows())
            {
                using var current = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(current.Handle, -1, -1);
                EmptyWorkingSet(current.Handle);
            }
        }
        catch
        {
            // Игнорируем системные ограничения прав вызова EmptyWorkingSet / SetProcessWorkingSetSize
        }
    }
}
