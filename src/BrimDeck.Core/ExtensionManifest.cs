using System.IO;
using System.Text.Json;

namespace BrimDeck.Core;

// A plugin folder's manifest: the manifest.json a plugin author writes, read once per scan. Everything on it is plain
// data from a local file — no code of any kind runs while loading a plugin, which is the whole point of the web model.
// MinWidth/MinHeight are the smallest page the plugin can show without losing content; zero means it asks for none.
// It lives beside the page limits that cap it, so the one input the host acts on before a browser ever sees a plugin is
// readable, validatable and testable without a window.
public sealed record ExtensionManifest(string Id, string TitleZh, string TitleEn, string Glyph, string Entry, double MinWidth, double MinHeight, int MinApiVersion);

public static class ExtensionManifests
{
    // A manifest is small or it is not a manifest: read whole, and past this it is refused before it is parsed.
    public const int MaximumBytes = 64 * 1024;
    // The shortest page floor worth honoring; anything under it cannot be a layout's real minimum.
    public const int MinimumFloor = 40;

    // Reads one plugin folder's manifest, or the reason it cannot be one. A folder without a manifest is not a plugin
    // and not an error either: the caller notes it as skipped so a stray folder is not silently invisible.
    public static (ExtensionManifest? Manifest, string? Error) Read(string folder)
    {
        var path = Path.Combine(folder, "manifest.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaximumBytes) return (null, "manifest.json: " + Loc.T("文件过大（上限 64 KB）", "is too large (64 KB max)"));
            // An editor that writes a byte-order mark — Windows PowerShell's Set-Content -Encoding UTF8, Notepad — is not
            // a broken plugin: the mark is stepped over rather than the author being told their file is invalid.
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            string Id(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
            // A page's floor is the author's claim about their own layout: a number of at least MinimumFloor pixels,
            // rounded up — a floor rounded down would let the page go under it — and larger than the panel's own limits
            // is honored as far as a page can go (DeckPages.Limits caps it).
            double Min(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var number) && number >= MinimumFloor ? Math.Ceiling(number) : 0;
            // The bridge the page needs: a whole number of at least 1, and anything else reads as "this one".
            int Api() => root.TryGetProperty("minApiVersion", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var asked) && asked >= 1 && asked == Math.Floor(asked) ? (int)Math.Min(asked, int.MaxValue) : 1;
            var id = Id("id");
            var titleZh = Id("titleZh");
            var titleEn = Id("titleEn");
            var glyph = Id("glyph");
            if (glyph.Length > 4096) glyph = "";
            var entry = Id("entry");
            if (id.Length == 0) return (null, "manifest.json: id " + Loc.T("缺失或为空", "is missing or empty"));
            if (titleZh.Length > 64) titleZh = titleZh[..64];
            if (titleEn.Length > 64) titleEn = titleEn[..64];
            if (id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.'))
                return (null, "manifest.json: id " + Loc.T("只允许字母、数字、- 和 .（64 字符内）", "allows letters, digits, - and . only (64 chars max)"));
            if (titleZh.Length == 0 && titleEn.Length == 0) return (null, "manifest.json: titleZh/titleEn " + Loc.T("至少要有一个", "need at least one of the two"));
            // A plugin written against a newer bridge is refused here, where the reason can be shown, rather than loading
            // to discover that the messages it waits for never come.
            if (Api() > Bridge.Version)
                return (null, "manifest.json: minApiVersion " + Loc.T($"需要 {Api()}，本宿主是 {Bridge.Version}", $"needs {Api()}, this host speaks {Bridge.Version}"));
            if (entry.Length == 0) entry = "index.html";
            // The entry stays inside the plugin folder: a relative path with no parent walk and no scheme of its own.
            if (entry.Contains("..") || entry.Contains('\\') || Path.IsPathRooted(entry) || Uri.TryCreate(entry, UriKind.Absolute, out _))
                return (null, "manifest.json: entry " + Loc.T("必须是插件文件夹内的相对路径", "must be a relative path inside the plugin folder"));
            if (!File.Exists(Path.Combine(folder, entry))) return (null, Loc.T("入口文件不存在：", "The entry file is missing: ") + entry);
            return (new ExtensionManifest(id, titleZh.Length > 0 ? titleZh : titleEn, titleEn.Length > 0 ? titleEn : titleZh, glyph, entry,
                Min("minWidth"), Min("minHeight"), Api()), null);
        }
        catch (Exception ex)
        {
            return (null, "manifest.json: " + ex.Message.Split('\n')[0]);
        }
    }
}
