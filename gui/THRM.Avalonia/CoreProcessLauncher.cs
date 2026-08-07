using System.Diagnostics;

namespace THRM.Avalonia;

internal static class CoreProcessLauncher
{
    internal static IReadOnlyList<string> GetCandidates()
    {
        var directory = AppContext.BaseDirectory;
        return OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(directory, "THRM Core.exe"),
                Path.Combine(directory, "BS2PRO-Core.exe"),
            }
            : new[]
            {
                Path.Combine(directory, "thrm-core"),
                Path.Combine(directory, "bs2pro-core"),
                Path.GetFullPath(Path.Combine(directory, "..", "core", "thrm-core")),
                Path.GetFullPath(Path.Combine(directory, "..", "core", "bs2pro-core")),
            };
    }

    internal static bool TryStart(out string status)
    {
        var corePath = GetCandidates().FirstOrDefault(File.Exists);
        if (corePath is null)
        {
            status = "THRM Core is not running and no bundled executable was found.";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = corePath,
                WorkingDirectory = Path.GetDirectoryName(corePath)!,
            };
            if (OperatingSystem.IsWindows())
            {
                // The release Core has a requireAdministrator manifest. Let Windows
                // honor it so the bundled Core gets the same launch path as a double-click.
                startInfo.UseShellExecute = true;
            }
            else
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                status = "THRM Core could not be started.";
                return false;
            }

            status = "Starting bundled THRM Core.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Could not start THRM Core: {ex.Message}";
            return false;
        }
    }
}
