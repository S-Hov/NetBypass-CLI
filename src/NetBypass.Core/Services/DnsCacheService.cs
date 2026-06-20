using System.Diagnostics;

namespace NetBypass.Core.Services;

public static class DnsCacheService
{
    public static void Flush()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "ipconfig.exe",
            Arguments = "/flushdns",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new InvalidOperationException("Не удалось очистить DNS-кеш Windows.");
    }
}
