using System.Runtime.InteropServices;

namespace TbotUltra.Worker.Infrastructure;

/// <summary>
/// Locates the Node runtime inside a Playwright driver folder (<c>.playwright/node/&lt;platform&gt;/node</c>).
/// The platform folder and executable name differ per OS, so every caller resolves them here instead
/// of pinning the Windows layout.
/// </summary>
public static class PlaywrightDriverLayout
{
    public static string NodePath(string driverRoot)
        => Path.Combine(driverRoot, "node", PlatformFolder(), OperatingSystem.IsWindows() ? "node.exe" : "node");

    internal static string PlatformFolder()
    {
        var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsMacOS())
        {
            return arm64 ? "darwin-arm64" : "darwin-x64";
        }

        if (OperatingSystem.IsLinux())
        {
            return arm64 ? "linux-arm64" : "linux-x64";
        }

        return "win32_x64";
    }
}
