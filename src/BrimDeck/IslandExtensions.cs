using System.IO;
using BrimDeck.Core;
using BrimDeck.Island;

namespace BrimDeck;

// The extension pages' host: every folder in the extensions folder that carries a manifest.json is one plugin, given a
// slot in name order, up to the four slots the page switch holds. A plugin is files — a manifest and the web page it
// points at — read by the browser, never run by the host, so a bad plugin is a row on the plugins page at worst.
//
// A scan is a full reload: a folder whose manifest or entry changed gets a fresh extension in its place, a folder
// taken away has its slot retired, and what did not change keeps its pages alive exactly as they are — their state and
// alarms live in the host, not in the pages. Nothing is locked: the folder stays editable while the app runs.
internal static class IslandExtensions
{
    public static readonly IIslandExtension?[] Slots = new IIslandExtension?[4];
    private static readonly string?[] _sources = new string?[4];
    // The id each plugin declared in its manifest; plain data the host already has, asked of nobody at runtime.
    private static readonly string?[] _ids = new string?[4];
    // A folder's manifest and entry stamps as one key, so an edit to either is a changed plugin.
    private static readonly string?[] _stamps = new string?[4];

    public static string Folder { get; } = Path.Combine(AppPaths.Data, "extensions");
    public static string Source(int slot) => _sources[slot] ?? "";

    // The plugins that could not be read, by folder name with the error's first line. Cleared on each scan.
    public static readonly List<(string File, string Error)> Failures = new();
    // Folders in the extensions folder that are not plugins — no manifest.json in them — noted so a stray folder is
    // not silently invisible. Cleared on each scan.
    public static readonly List<(string File, string Note)> Skipped = new();

    // Raised with the slots whose extension was replaced or retired; the app drops their tabs and views and re-renders.
    public static event Action<IReadOnlyList<int>>? ExtensionsChanged;

    public static void Load() => Scan();

    public static void Scan()
    {
        Failures.Clear();
        Skipped.Clear();
        var disk = new List<string>();
        try
        {
            if (Directory.Exists(Folder))
                disk.AddRange(Directory.EnumerateDirectories(Folder).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures.Add((Path.GetFileName(Folder), ex.Message.Split('\n')[0]));
        }

        // One pass over the folders: a manifest and its entry, or the reason the folder is not a plugin. The stamp
        // covers the manifest and the entry file, so editing either is a change the next scan picks up. No extension
        // exists yet — a manifest is read, an extension is only made for a slot it actually takes.
        var read = new List<(string Name, string Stamp, ExtensionManifest? Manifest, string? Error)>();
        foreach (var folder in disk)
        {
            string name = Path.GetFileName(folder);
            try
            {
                var (manifest, error) = ExtensionManifests.Read(folder);
                if (manifest is null)
                {
                    if (error is not null) Failures.Add((name, error));
                    else Skipped.Add((name, Loc.T("缺少 manifest.json", "no manifest.json in it")));
                    continue;
                }
                var stamp = Stamp(Path.Combine(folder, "manifest.json")) + "|" + Stamp(Path.Combine(folder, manifest.Entry));
                read.Add((name, stamp, manifest, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failures.Add((name, ex.Message.Split('\n')[0]));
            }
        }

        // The kept slots keep their extension — a plugin whose folder did not change keeps its pages and their state;
        // everything else is placed fresh, in name order, into the slots that remain.
        var slots = new IIslandExtension?[4];
        var sources = new string?[4];
        var ids = new string?[4];
        var stamps = new string?[4];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int slot = 0; slot < 4; slot++)
        {
            var name = _sources[slot];
            if (Slots[slot] is null || name is not { Length: > 0 }) continue;
            var entry = read.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry.Manifest is not null && entry.Stamp == _stamps[slot])
            {
                slots[slot] = Slots[slot]; sources[slot] = name; ids[slot] = _ids[slot]; stamps[slot] = _stamps[slot];
                keptNames.Add(name);
                if (_ids[slot] is { } id) seen.Add(id);
            }
        }
        int at = 0;
        foreach (var item in read)
        {
            if (item.Manifest is null || keptNames.Contains(item.Name)) continue;
            if (!seen.Add(item.Manifest.Id))
            {
                Failures.Add((item.Name, Loc.T("插件 ID 重复：", "Duplicate plugin id: ") + item.Manifest.Id));
                continue;
            }
            while (at < 4 && slots[at] is not null) at++;
            if (at >= 4)
            {
                Failures.Add((item.Name, Loc.T("槽位已满", "All four slots are filled")));
                continue;
            }
            var extension = new WebExtension(item.Manifest, Path.Combine(Folder, item.Name));
            slots[at] = extension; sources[at] = item.Name; ids[at] = extension.Id; stamps[at] = item.Stamp;
        }

        var changed = new List<int>();
        var retired = new List<WebExtension>();
        for (int slot = 0; slot < 4; slot++)
        {
            if (ReferenceEquals(Slots[slot], slots[slot])) continue;
            Notify(slot, false);
            changed.Add(slot);
            if (Slots[slot] is WebExtension old) retired.Add(old);
        }
        for (int slot = 0; slot < 4; slot++) { Slots[slot] = slots[slot]; _sources[slot] = sources[slot]; _ids[slot] = ids[slot]; _stamps[slot] = stamps[slot]; }

        DeckPages.SlotOccupied = slot => Slots[slot] is not null;
        DeckPages.SlotId = slot => _ids[slot];
        // Each plugin's own floor for its page: the manifest asks in the page's pixels, the panel keeps the floor in its
        // own, so the host adds the chrome back — dragging can then never take a page under the box it was written for.
        DeckPages.SlotMinimum = slot => Slots[slot] is WebExtension web ? MainWindow.PanelFloorFor(web.Minimum.Width, web.Minimum.Height) : (0, 0);
        // The WebView2 runtime is probed once, but only when there is a plugin to show: a machine without plugins never
        // starts a browser process it has no use for, and a page created later still probes on its own.
        if (Slots.Any(slot => slot is not null)) _ = WebRuntime.Environment();
        // An author whose declared control was skipped is told in two places, because either is where they would look:
        // under that plugin's row on the plugins page, and in the extension log for anyone debugging with it switched on.
        for (int slot = 0; slot < Slots.Length; slot++)
            if (Slots[slot] is WebExtension placed)
                foreach (var note in placed.Notes) ExtensionLog.Write("extension: note " + placed.Id + " " + note);
        if (changed.Count > 0)
        {
            ExtensionsChanged?.Invoke(changed);
            // The panels have let go of the retired pages' views; only now may the extensions behind them go too.
            foreach (var extension in retired) extension.Dispose();
        }
    }

    private static string Stamp(string file)
    {
        try { var info = new FileInfo(file); return info.LastWriteTimeUtc.Ticks + ":" + info.Length; }
        catch (Exception) { return "?"; }
    }

    // A plugin's glyph as stroke path data on a 24 by 24 grid. A string that does not parse is a plugin's mistake, not
    // the panel's: a plain square stands in so the tab and its row keep their place.
    public static System.Windows.Media.Geometry SafeGlyph(string? data)
    {
        try
        {
            var geometry = System.Windows.Media.Geometry.Parse(data ?? "");
            geometry.Freeze();
            return geometry;
        }
        catch (Exception)
        {
            var fallback = System.Windows.Media.Geometry.Parse("M 4,4 H 20 V 20 H 4 Z");
            fallback.Freeze();
            return fallback;
        }
    }

    public static bool Occupied(int slot) => Slots[slot] is not null;

    // Whether a view a panel cached is still usable. A page leaves its WebView behind when the panel takes the view
    // off the window, so the extension — not the panel — is asked before a cached view is put back.
    public static bool Alive(int slot, System.Windows.FrameworkElement view)
        => slot >= 0 && slot < Slots.Length && Slots[slot] is WebExtension extension ? extension.Alive(view) : true;

    public static int OccupiedCount => Slots.Count(slot => slot is not null);

    // The demand a page hands to its extension; the web adapter is host code, but the guard stays so a bug in it is a
    // missed message, not a thrown startup.
    public static void Notify(int slot, bool shown)
    {
        if (slot < 0 || slot >= Slots.Length) return;
        try { Slots[slot]?.SetDemand(shown); }
        catch (Exception) { }
    }

    // A page switch is arriving at this slot's page from this direction; a web page carries its entrance inside the
    // browser, where the switch's own animation cannot reach it.
    public static void Enter(int slot, bool forward)
    {
        if (slot < 0 || slot >= Slots.Length) return;
        try { if (Slots[slot] is WebExtension web) web.Enter(forward); }
        catch (Exception) { }
    }

    // Whether any panel is open, told as it changes: an open panel keeps its pre-built pages ready to switch to, so
    // the reclaim of a page nobody watches waits for the panel itself to close.
    public static void PanelInUse(bool inUse)
    {
        try { WebRuntime.PanelInUse(inUse); }
        catch (Exception) { }
    }

    public static string Title(int slot) => Slots[slot] is { } extension ? (Loc.IsEnglish ? extension.TitleEn : extension.TitleZh) : "";

    // The colors an extension draws with: its monitor palette page's scheme on the theme's text and surface colors.
    // The app sets the palette whenever the theme or the choice changes, before any view is created, and the pages
    // that already exist hear about it as a push.
    public static IslandColors Colors { get; private set; } = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");
    // The settings file is adopted wherever the palette is, because both are read from the same object and every path
    // that applies one applies the other: the declared controls a plugin draws its settings row from, and the values a
    // reader chose, both live here.
    public static DeckSettings? Settings { get; private set; }
    // How a value a page wrote reaches the file, the settings page and every other panel showing that plugin. The app
    // installs it at startup; the host has no business deciding when settings are saved.
    public static Action<string, string, ExtensionSettingValue>? WriteValue;
    public static void SetPalette(DeckSettings settings)
    {
        Colors = With(MonitorThemeColors.For(settings));
        Settings = settings;
        WebRuntime.SetColors(Colors);
        // Whatever the settings window just changed — a declared control's value, or the plugin list itself — the pages
        // are told in the same breath, so a page and its row cannot be seen disagreeing.
        WebRuntime.PushValues();
    }
    // What a loaded plugin declared for the settings page to draw; nothing at all for a built-in page or a plugin with
    // no declared controls, which is most of them and leaves the row as it always was.
    public static IReadOnlyList<ExtensionSetting> Declared(int slot) => Slots[slot] is WebExtension web ? web.Declared : [];
    // The declared entries that plugin wrote which the host could not use — shown under its row on the plugins page.
    public static IReadOnlyList<string> Notes(int slot) => Slots[slot] is WebExtension web ? web.Notes : [];
    // The values chosen for one plugin, honoured against what its manifest declares — so a stale or hand-edited value
    // reads as the declaration would allow, and a page never sees a control its own author would not have drawn.
    public static IReadOnlyDictionary<string, ExtensionSettingValue> ValuesFor(string id)
        => Slots.FirstOrDefault(slot => slot is WebExtension web && web.Id == id) is WebExtension extension
            ? ExtensionSettingStore.Normalize(extension.Declared, Settings?.ExtensionValues(id))
            : EmptyValues;
    private static readonly IReadOnlyDictionary<string, ExtensionSettingValue> EmptyValues = new Dictionary<string, ExtensionSettingValue>();
    // One value written, then told to every page of that plugin: a reader may have three panels open, and a control on
    // one of them is the same control on all of them.
    public static void Write(string id, string key, ExtensionSettingValue value)
    {
        WriteValue?.Invoke(id, key, value);
        WebRuntime.ValuesChanged(id);
    }
    // The normal color is not part of MonitorColors any more; the green the readings have always used still fits a
    // plugin's temperature gauge, on the theme's dark or light surface.
    private static IslandColors With(MonitorColors colors)
        => new(PanelTheme.Current.IsDark ? "#4FD17F" : "#1C9A50", colors.Warning, colors.Critical, UI.Primary, UI.Secondary, UI.Tertiary, UI.Track, UI.Fill);
}
