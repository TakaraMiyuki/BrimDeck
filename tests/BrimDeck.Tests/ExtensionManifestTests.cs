using BrimDeck.Core;

// The manifest is the only piece of a plugin the host acts on before a browser ever sees it: it names the plugin, its
// tab, the file to open, and the box the page cannot be shown in. Every rejection here is a folder that must not become
// a page, so each branch is worth a line of test.
internal static class ExtensionManifestTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        int at = 0;
        string Folder(string json, string entryFile = "index.html", bool bom = false)
        {
            var folder = Path.Combine(root, "manifest-" + (++at));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), json, bom ? System.Text.Encoding.UTF8 : new System.Text.UTF8Encoding(false));
            if (entryFile.Length > 0)
            {
                var entry = Path.Combine(folder, entryFile);
                Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                File.WriteAllText(entry, "<!doctype html><title>p</title>");
            }
            return folder;
        }
        // The manifest a folder was allowed to have, or null: a rejection is never also a load.
        ExtensionManifest? Loaded(string json, string entry = "index.html", bool bom = false)
            => ExtensionManifests.Read(Folder(json, entry, bom)).Manifest;
        // The reason a folder was refused, or null when it loaded (or was only skipped).
        string? Refused(string json, string entry = "index.html")
        {
            var (manifest, error) = ExtensionManifests.Read(Folder(json, entry));
            return manifest is null ? error : null;
        }

        var (manifest, error) = ExtensionManifests.Read(Folder("""{"id":"demo","titleZh":"演示"}"""));
        check("Manifest: the smallest plugin loads, with the other title and the default entry filled in",
            manifest is not null && error is null && manifest.Id == "demo" && manifest.TitleZh == "演示" && manifest.TitleEn == "演示"
            && manifest.Entry == "index.html" && manifest.MinWidth == 0 && manifest.MinHeight == 0);
        check("Manifest: a folder without manifest.json is skipped, not an error",
            ExtensionManifests.Read(Path.Combine(root, "not-a-plugin-" + ++at)) is (null, null));

        check("Manifest: an id that is missing or empty is refused, with the reason naming it",
            Refused("""{"titleZh":"演示"}""") is { Length: > 0 } absent && absent.Contains("id") && Refused("""{"id":"","titleZh":"演示"}""") is { Length: > 0 });
        check("Manifest: an id outside letters, digits, - and . is refused",
            Refused("""{"id":"a/b","titleZh":"演示"}""") is { Length: > 0 } slash && slash.Contains("id"));
        check("Manifest: an id over 64 characters is refused, and 64 is kept",
            Refused($$"""{"id":"{{new string('k', 65)}}","titleZh":"演示"}""") is { Length: > 0 }
            && Loaded($$"""{"id":"{{new string('k', 64)}}","titleZh":"演示"}""") is { Id.Length: 64 });
        check("Manifest: dots and dashes in an id are the plugin's own business",
            Loaded("""{"id":"thrm-cooler.v2","titleZh":"散热器"}""") is { Id: "thrm-cooler.v2" });
        check("Manifest: no title at all is refused, and a long one is cut rather than rejected",
            Refused("""{"id":"demo"}""") is { Length: > 0 } title && title.Contains("title")
            && Loaded($$"""{"id":"demo","titleZh":"{{new string('标', 70)}}"}""") is { TitleZh.Length: 64 });
        check("Manifest: the missing title language stands in with the one that is there",
            Loaded("""{"id":"demo","titleEn":"Cooler"}""") is { TitleZh: "Cooler", TitleEn: "Cooler" });
        check("Manifest: a glyph past the cap is dropped, and the plugin still loads",
            Loaded($$"""{"id":"demo","titleZh":"演示","glyph":"{{new string('M', 5000)}}"}""") is { Glyph: "" });

        check("Manifest: an entry in a subfolder of the plugin is kept as written",
            Loaded("""{"id":"demo","titleZh":"演示","entry":"pages/page.html"}""", "pages/page.html") is { Entry: "pages/page.html" });
        string[] escapes = ["../evil.html", "a/../../evil.html", @"C:\evil.html", @"\\server\share\evil.html", "https://example.com/evil.html", "/etc/passwd"];
        // Serialized, so a backslash reaches the validator as a path instead of failing the JSON parse first.
        string WithEntry(string entry) => System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["id"] = "demo", ["titleZh"] = "演示", ["entry"] = entry });
        bool allEscapesRefused = true, reasonsNamed = true;
        foreach (var bad in escapes)
        {
            var reason = Refused(WithEntry(bad), "evil.html");
            if (reason is null) allEscapesRefused = false;
            else if (!reason.Contains("entry")) reasonsNamed = false;
        }
        check("Manifest: parent walks, rooted paths, UNC shares and any scheme all leave the folder", allEscapesRefused && reasonsNamed);
        check("Manifest: an entry that is not on disk is refused, naming the file",
            Refused("""{"id":"demo","titleZh":"演示","entry":"gone.html"}""") is { Length: > 0 } missing && missing.Contains("gone.html"));

        check("Manifest: a manifest past 64 KB is refused before it is parsed",
            Refused($$"""{"id":"demo","titleZh":"演示","glyph":"{{new string('M', 70 * 1024)}}"}""") is { Length: > 0 });
        check("Manifest: text that is not JSON is refused with a reason", Refused("this is not json") is { Length: > 0 });
        check("Manifest: JSON that is not an object is refused, not crashed on", Refused("[1,2,3]") is { Length: > 0 });
        var (bomManifest, bomError) = ExtensionManifests.Read(Folder("""{"id":"demo","titleZh":"演示"}""", bom: true));
        check("Manifest: a byte-order mark does not hide the plugin", bomManifest is { Id: "demo" } && bomError is null);
        check("Manifest: fields the host does not know are ignored",
            Loaded("""{"id":"demo","titleZh":"演示","author":"someone","permissions":["everything"]}""") is { Id: "demo" });

        check("Manifest: the page's floor is read as written, fractions rounded up",
            Loaded("""{"id":"demo","titleZh":"演示","minWidth":440,"minHeight":220.4}""") is { MinWidth: 440, MinHeight: 221 });
        check("Manifest: a floor under 40 pixels, or not a number, asks for nothing",
            Loaded("""{"id":"demo","titleZh":"演示","minWidth":39,"minHeight":-500}""") is { MinWidth: 0, MinHeight: 0 }
            && Loaded("""{"id":"demo","titleZh":"演示","minWidth":"440","minHeight":null}""") is { MinWidth: 0, MinHeight: 0 });
        check("Manifest: a floor beyond a page's ceiling is kept and left for the page limits to cap",
            Loaded("""{"id":"demo","titleZh":"演示","minWidth":9000,"minHeight":9000}""") is { MinWidth: 9000, MinHeight: 9000 });

        // The bridge's version is the one thing an author can write a plugin against, so a plugin that needs a newer
        // one is refused at the folder — before a page loads, finds nothing it asked for, and shows a blank.
        check("Manifest: no stated API version means the bridge as it is now",
            Loaded("""{"id":"demo","titleZh":"演示"}""") is { MinApiVersion: 1 });
        check("Manifest: a plugin asking for the bridge the host has loads",
            Loaded($$"""{"id":"demo","titleZh":"演示","minApiVersion":{{Bridge.Version}}}""") is { MinApiVersion: 1 });
        check("Manifest: a plugin needing a newer bridge is refused, naming both versions",
            Refused($$"""{"id":"demo","titleZh":"演示","minApiVersion":{{Bridge.Version + 1}}}""") is { Length: > 0 } older
            && older.Contains("minApiVersion") && older.Contains(Bridge.Version.ToString()));
        check("Manifest: a version that is not a whole number above zero asks for nothing",
            Loaded("""{"id":"demo","titleZh":"演示","minApiVersion":0}""") is { MinApiVersion: 1 }
            && Loaded("""{"id":"demo","titleZh":"演示","minApiVersion":-3}""") is { MinApiVersion: 1 }
            && Loaded("""{"id":"demo","titleZh":"演示","minApiVersion":"one"}""") is { MinApiVersion: 1 }
            && Loaded("""{"id":"demo","titleZh":"演示","minApiVersion":1.5}""") is { MinApiVersion: 1 });
    }
}
