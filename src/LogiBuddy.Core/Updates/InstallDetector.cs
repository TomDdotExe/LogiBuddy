using System.IO;

namespace LogiBuddy.Core.Updates;

/// Distinguishes an Inno-installed copy of LogiBuddy from a portable zip
/// extraction, so in-app auto-update (which needs an installer to run) only
/// offers itself where there's actually an installer backing this copy.
public static class InstallDetector
{
    /// True if appDirectory contains the uninstaller Inno Setup writes next
    /// to the app — present only for an installed copy, never a portable
    /// zip extraction.
    public static bool IsInstalled(string appDirectory) =>
        File.Exists(Path.Combine(appDirectory, "unins000.exe"));
}
