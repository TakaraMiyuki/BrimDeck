using System.IO;
using BrimDeck.Core;

namespace BrimDeck;

// The extension host's own record: which page was created, navigated, recycled or disposed, and why a WebView2 process
// went away. It is off unless BRIMDECK_EXTENSION_LOG is set — to 1 for <data>\extensions.log, or to a file path — so a
// normal install writes nothing. The UI test turns it on, and it is the first thing to read when a plugin page is blank.
internal static class ExtensionLog
{
    private const long Limit = 512 * 1024;
    private static readonly object Gate = new();
    private static readonly string? FilePath = Resolve();

    public static bool Enabled => FilePath is not null;

    public static void Write(string message)
    {
        if (FilePath is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > Limit) File.Delete(FilePath);
                File.AppendAllText(FilePath, DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + message + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string? Resolve()
    {
        var value = Environment.GetEnvironmentVariable("BRIMDECK_EXTENSION_LOG");
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim().Trim('"');
        if (value is "1" or "true" or "on") return Path.Combine(AppPaths.Data, "extensions.log");
        try { return Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }
}
