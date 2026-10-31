using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using BrimDeck.Core;
using BrimDeck.Island;

namespace BrimDeck;

// The plugins' host: every DLL in the plugins folder is loaded into its own collectible assembly context, the one class
// implementing IIslandExtension is constructed, and Initialize hands it a host it registers with — panel pages and
// compact widgets for the capsule. A DLL that fails anywhere is skipped, noted by name for the plugins page, and never
// takes the app with it: every step that runs third-party code (the assembly's types, the constructor, Initialize, the
// id and title getters) is guarded on its own.
//
// A scan is a full hot reload: a DLL that changed on disk is loaded fresh and replaces its slot, a DLL taken away has
// its slot retired, and what did not change is kept as it is. Each DLL is loaded from a copy in a cache folder, so the
// plugins folder itself stays writable while the app runs.
//
// One slot is one plugin's page (the first page it registers); compact widgets are kept per plugin whether or not a
// page slot was free, so a widget-only plugin works too. The contract always comes from the host, so host and plugins
// share one IIslandExtension type; the assembly version is the compatibility gate.
internal static class IslandExtensions
{
    public const int HostVersion = IslandHost.Version;

    public sealed class Plugin
    {
        public required IIslandExtension Extension;
        public required string Id;
        public required AssemblyLoadContext Context;
        public required string File;
        public (DateTime Written, long Size) Stamp;
        public required IslandHostImpl Host;
        public int Slot = -1; // -1: no page slot (widget-only, or the slots were full)
    }

    // One host implementation per plugin: it collects what Initialize registers and answers the settings store.
    public sealed class IslandHostImpl : IIslandHost
    {
        public required Plugin Owner;
        public readonly List<IIslandPage> Pages = [];
        public readonly List<IIslandCompact> Compacts = [];
        public Dictionary<string, string> Stored = [];
        public bool StoreDirty;

        public IslandColors Colors => IslandExtensions.Colors;
        public event Action? ColorsChanged;
        internal void RaiseColors() => ColorsChanged?.Invoke();

        public void RegisterPage(IIslandPage page)
        {
            if (Pages.Any(registered => string.Equals(registered.Id, page.Id, StringComparison.Ordinal)))
                ExtensionLog.Write($"plugin {Owner.Id}: page '{page.Id}' registered twice; the first one is kept");
            else Pages.Add(page);
        }

        public void RegisterCompact(IIslandCompact compact)
        {
            if (Compacts.Any(registered => string.Equals(registered.Id, compact.Id, StringComparison.Ordinal)))
                ExtensionLog.Write($"plugin {Owner.Id}: compact '{compact.Id}' registered twice; the first one is kept");
            else Compacts.Add(compact);
        }

        public string? GetSetting(string key) => Stored.TryGetValue(key, out var value) ? value : null;

        public void SetSetting(string key, string? value)
        {
            if (value is null) Stored.Remove(key); else Stored[key] = value;
            StoreDirty = true;
            // Written at once: a plugin may set before the app ever closes, and the store is small.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath(Owner.Id))!);
                File.WriteAllText(StorePath(Owner.Id), JsonSerializer.Serialize(Stored));
                StoreDirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ExtensionLog.Write($"plugin {Owner.Id}: settings store write failed: {ex.Message.Split('\n')[0]}"); }
        }
    }

    private static string StorePath(string id) => Path.Combine(PluginDataFolder, id + ".json");

    public static readonly List<Plugin> Plugins = [];
    // The page slots, in placement order; Slots[i] is the plugin whose page sits in slot i (null when empty).
    public static readonly Plugin?[] Slots = new Plugin?[4];

    private static readonly string LocalData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Folder { get; } = Path.Combine(LocalData, "BrimDeck", "plugins");
    // The copies the contexts load from: a loaded assembly locks its file on Windows, and the plugins folder must stay
    // writable so a new DLL can be dropped over the old one.
    private static string CacheFolder => Path.Combine(LocalData, "BrimDeck", "plugin-cache");
    private static string PluginDataFolder => Path.Combine(LocalData, "BrimDeck", "plugin-data");

    // The DLLs that could not be loaded, by file name with the error's first line. Cleared on each scan.
    public static readonly List<(string File, string Error)> Failures = new();
    // Files that loaded but offered nothing usable, or offered more than this host takes — noted so nothing is silently
    // invisible. Cleared on each scan.
    public static readonly List<(string File, string Note)> Skipped = new();
    // Files read without finding a plugin, by stamp, so a dependency is not loaded into a context on every scan.
    private static readonly Dictionary<string, (DateTime Written, long Size)> _withoutPlugin = new(StringComparer.OrdinalIgnoreCase);

    // Raised after a scan changed what is loaded: the app rebuilds the tabs, the capsule row and the plugins page.
    public static event Action? ExtensionsChanged;

    private static DeckSettings Settings = new();

    public static void Load(DeckSettings settings)
    {
        Settings = settings;
        // The palette has been set by the app before this; loading does not change it.
        Scan();
    }

    // The master switch turned the plugin system off mid-run: every plugin unloads now, the folders and their data are
    // untouched, and the page layer retires its tabs and views through ExtensionsChanged.
    public static void UnloadAll()
    {
        bool hadAny = Plugins.Count > 0 || Failures.Count > 0 || Skipped.Count > 0;
        foreach (var plugin in Plugins)
            try { plugin.Context.Unload(); } catch (Exception) { }
        Plugins.Clear(); Failures.Clear(); Skipped.Clear(); _withoutPlugin.Clear();
        for (int slot = 0; slot < 4; slot++) Slots[slot] = null;
        _previousSlots = new Plugin?[4];
        if (hadAny || ExtensionLog.Enabled) ExtensionLog.Write(hadAny ? "master switch: all plugins unloaded" : "master switch: nothing was loaded");
        ExtensionsChanged?.Invoke();
    }

    // The settings instance the loader reads its gates from, kept current as the app applies new ones.
    internal static void SetSettings(DeckSettings settings) => Settings = settings;

    private static bool Disabled(string file) =>
        Settings.PluginsDisabledFiles.Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase));

    private static void Scan()
    {
        Failures.Clear();
        Skipped.Clear();
        var disk = new List<string>();
        try
        {
            if (Directory.Exists(Folder))
                disk.AddRange(Directory.EnumerateFiles(Folder, "*.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Failures.Add((Path.GetFileName(Folder), ex.Message.Split('\n')[0])); }
        // The reader's order first (the plugins page's drag handle), then the rest alphabetically.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in Settings.PluginsOrder.Select((name, index) => (name, index)))
            if (!rank.TryAdd(name, index)) rank[name] = index;
        disk.Sort((a, b) =>
        {
            int byRank = rank.TryGetValue(Path.GetFileName(a), out var ra) ? ra : int.MaxValue;
            int byRankB = rank.TryGetValue(Path.GetFileName(b), out var rb) ? rb : int.MaxValue;
            return byRank != byRankB ? byRank.CompareTo(byRankB) : string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);
        });

        var stamps = new Dictionary<string, (DateTime Written, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in disk)
        {
            try { var info = new FileInfo(file); stamps[Path.GetFileName(file)] = (info.LastWriteTimeUtc, info.Length); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Failures.Add((Path.GetFileName(file), ex.Message.Split('\n')[0])); }
        }

        // A file whose stamp still matches the plugin it produced is kept whole: pages, compacts and their views stay.
        // A file the user disabled is not kept even when its stamp matches: disabling a running plugin ends it at once,
        // which is what the plugins page's toggle promises.
        bool Kept(Plugin plugin) =>
            !string.Equals(plugin.File, typeof(IIslandExtension).Assembly.GetName().Name + ".dll", StringComparison.OrdinalIgnoreCase)
            && !Disabled(Path.GetFileName(plugin.File))
            && stamps.TryGetValue(Path.GetFileName(plugin.File), out var stamp) && stamp == plugin.Stamp;

        var kept = Plugins.Where(Kept).ToList();
        var keptFiles = kept.Select(plugin => Path.GetFileName(plugin.File)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Unchanged(string name) => keptFiles.Contains(name);

        var fresh = new List<Plugin>();
        var contexts = new List<(AssemblyLoadContext Context, int Owner)>();
        try { Directory.CreateDirectory(CacheFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Failures.Add(("plugin-cache", ex.Message.Split('\n')[0])); }

        foreach (var file in disk)
        {
            string name = Path.GetFileName(file);
            if (!stamps.TryGetValue(name, out var stamp) || Unchanged(name)) continue;
            // The contract belongs to the host; a copy in the plugins folder is ignored rather than loaded again.
            if (string.Equals(name, Path.GetFileName(typeof(IIslandExtension).Assembly.Location), StringComparison.OrdinalIgnoreCase)) continue;
            if (Disabled(name)) continue; // The plugins page lists it with its toggle; loading nothing is the disable.
            if (_withoutPlugin.TryGetValue(name, out var empty) && empty == stamp)
            {
                Skipped.Add((name, Loc.T("不含插件实现", "no plugin in it")));
                continue;
            }
            var watch = Stopwatch.StartNew();
            LoadOne(file, name, stamp, fresh, contexts);
            if (ExtensionLog.Enabled) ExtensionLog.Write($"scan: {name} loaded in {watch.ElapsedMilliseconds}ms");
        }

        // Place: the kept plugins stay; the fresh ones fill the free slots in file order. A repeated id is skipped, and
        // a page that does not fit a slot is noted — its compacts still work.
        var slots = new Plugin?[4];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in kept)
        {
            if (plugin.Slot is >= 0 and < 4 && slots[plugin.Slot] is null) { slots[plugin.Slot] = plugin; seen.Add(plugin.Id); }
        }
        int at = 0;
        foreach (var plugin in fresh)
        {
            if (!seen.Add(plugin.Id))
            {
                Failures.Add((Path.GetFileName(plugin.File), Loc.T("插件 ID 重复：", "Duplicate plugin id: ") + plugin.Id));
                continue;
            }
            while (at < 4 && slots[at] is not null) at++;
            if (at < 4) { slots[at] = plugin; plugin.Slot = at; }
            else
            {
                plugin.Slot = -1;
                if (plugin.Host.Pages.Count > 0)
                    Skipped.Add((Path.GetFileName(plugin.File), Loc.T("槽位已满，页面未显示", "All four slots are filled; the page is not shown")));
            }
        }

        Plugins.Clear();
        Plugins.AddRange(kept.Concat(fresh).OrderBy(plugin => plugin.Slot is >= 0 and < 4 ? plugin.Slot : 99).ThenBy(plugin => Path.GetFileName(plugin.File), StringComparer.OrdinalIgnoreCase));
        for (int slot = 0; slot < 4; slot++) Slots[slot] = slots[slot];

        // Every context that did not end up behind a live plugin is let go: one whose DLL failed mid-load, one whose
        // plugin registered nothing, one whose id duplicated another's.
        var live = Plugins.Select(plugin => plugin.Context).ToHashSet();
        var unloadWatch = Stopwatch.StartNew();
        foreach (var (context, _) in contexts)
            if (!live.Contains(context))
                try { context.Unload(); } catch (Exception) { }
        if (ExtensionLog.Enabled && unloadWatch.ElapsedMilliseconds > 50)
            ExtensionLog.Write($"scan: retiring {contexts.Count - live.Count} context(s) took {unloadWatch.ElapsedMilliseconds}ms");

        var changed = false;
        for (int slot = 0; slot < 4; slot++)
        {
            var next = Slots[slot];
            var before = _previousSlots.Length > slot ? _previousSlots[slot] : null;
            if (!ReferenceEquals(before, next)) changed = true;
        }
        _previousSlots = (Plugin?[])Slots.Clone();
        // The page layer asks these per slot: whether a plugin page sits there, and the plugin id its settings are keyed by.
        DeckPages.SlotOccupied = slot => slot >= 0 && slot < 4 && Slots[slot] is { Host.Pages.Count: > 0 };
        DeckPages.SlotId = slot => slot >= 0 && slot < 4 ? Slots[slot]?.Id : null;
        if (ExtensionLog.Enabled)
        {
            foreach (var plugin in Plugins)
                ExtensionLog.Write($"loaded {Path.GetFileName(plugin.File)}: id={plugin.Id} pages={plugin.Host.Pages.Count} compacts={plugin.Host.Compacts.Count} slot={plugin.Slot}");
            foreach (var failure in Failures)
                ExtensionLog.Write($"failed {failure.File}: {failure.Error}");
            foreach (var skipped in Skipped)
                ExtensionLog.Write($"skipped {skipped.File}: {skipped.Note}");
            if (Plugins.Count == 0 && Failures.Count == 0 && Skipped.Count == 0)
                ExtensionLog.Write("scan found no plugins");
        }
        if (changed) ExtensionsChanged?.Invoke();
    }

    // The slots as the last scan left them, so "did anything change" compares placements rather than references.
    private static Plugin?[] _previousSlots = new Plugin?[4];

    private static void LoadOne(string file, string name, (DateTime Written, long Size) stamp, List<Plugin> fresh,
        List<(AssemblyLoadContext Context, int Owner)> contexts)
    {
        AssemblyLoadContext? context = null;
        int owner = fresh.Count;
        try
        {
            context = new AssemblyLoadContext("BrimDeck.Plugins:" + name, isCollectible: true);
            context.Resolving += Resolve;
            var assembly = context.LoadFromAssemblyPath(Shadow(file, name, stamp));
            contexts.Add((context, owner));
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IIslandExtension).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                try
                {
                    var extension = (IIslandExtension)Activator.CreateInstance(type)!;
                    var id = extension.Id?.Trim() ?? "";
                    if (id.Length == 0) { Failures.Add((name, Loc.T("插件 Id 为空", "The plugin id is empty"))); continue; }
                    if (extension.MinHostVersion > HostVersion)
                    {
                        Failures.Add((name, Loc.T($"需要 BrimDeck {extension.MinHostVersion}，当前 {HostVersion}",
                            $"needs BrimDeck {extension.MinHostVersion}, this host is {HostVersion}")));
                        continue;
                    }
                    var impl = new IslandHostImpl { Owner = new Plugin { Extension = extension, Id = id, Context = context, File = file, Stamp = stamp, Host = null! } };
                    var plugin = new Plugin { Extension = extension, Id = id, Context = context, File = file, Stamp = stamp, Host = impl };
                    impl.Owner = plugin;
                    try
                    {
                        var store = Path.Combine(PluginDataFolder, id + ".json");
                        if (File.Exists(store))
                            impl.Stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(store)) ?? [];
                        extension.Initialize(impl);
                    }
                    catch (Exception ex)
                    { Failures.Add((name, Loc.T("Initialize 失败：", "Initialize failed: ") + ex.Message.Split('\n')[0])); continue; }
                    if (impl.Pages.Count == 0 && impl.Compacts.Count == 0)
                    { Skipped.Add((name, Loc.T("未注册任何页面或胶囊内容", "registered no page and no compact widget"))); continue; }
                    fresh.Add(plugin);
                }
                catch (Exception ex)
                { Failures.Add((name, ex.Message.Split('\n')[0])); }
            }
        }
        catch (Exception ex)
        {
            if (context is not null && contexts.All(entry => entry.Context != context)) context.Unload();
            Failures.Add((name, ex.Message.Split('\n')[0]));
        }
        if (fresh.Count == owner && !Failures.Any(f => string.Equals(f.File, name, StringComparison.OrdinalIgnoreCase)) && !Skipped.Any(s => string.Equals(s.File, name, StringComparison.OrdinalIgnoreCase)))
        {
            _withoutPlugin[name] = stamp;
            Skipped.Add((name, Loc.T("不含插件实现", "no plugin in it")));
        }
        else _withoutPlugin.Remove(name);
    }

    // A dependency next to the plugin is loaded from the plugins folder; the contract always comes from the host, so
    // both sides share one IIslandExtension type.
    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.Equals(name.Name, typeof(IIslandExtension).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Name is not { Length: > 0 } simple) return null;
        var candidate = Path.Combine(Folder, simple + ".dll");
        return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
    }

    // The cached copy a context loads from, made once per file version.
    private static string Shadow(string file, string name, (DateTime Written, long Size) stamp)
    {
        var shadow = Path.Combine(CacheFolder, $"{name}.{stamp.Written.Ticks}.{stamp.Size}.dll");
        if (!File.Exists(shadow))
        {
            File.Copy(file, shadow, true);
            foreach (var old in Directory.EnumerateFiles(CacheFolder, name + ".*.dll"))
                if (!string.Equals(old, shadow, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return shadow;
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

    public static bool Occupied(int slot) => slot >= 0 && slot < 4 && Slots[slot] is { Host.Pages.Count: > 0 };
    public static int OccupiedCount { get { int n = 0; for (int slot = 0; slot < 4; slot++) if (Occupied(slot)) n++; return n; } }

    // The page a slot shows (the first one the plugin registered), or null.
    public static IIslandPage? PageOf(int slot) => Occupied(slot) ? Slots[slot]!.Host.Pages[0] : null;

    // The page's preferred box, as its contract asks; a plugin that throws or asks for nothing takes the host default.
    public static (double Width, double Height)? PreferredSize(int slot)
    {
        if (slot < 0 || slot >= 4 || Slots[slot] is not { } plugin) return null;
        try { return plugin.Host.Pages.FirstOrDefault()?.PreferredSize; }
        catch (Exception) { return null; }
    }
    // The tab glyph a slot's plugin declared, for the tab button and the plugins page.
    public static string Glyph(int slot) => slot >= 0 && slot < 4 && Slots[slot] is { } plugin ? plugin.Extension.Glyph : "";

    // A plugin's page view is a plain WPF element that stays alive as long as the panel does, so there is no "is this
    // view still usable" question like the web host had; the hook stays so the panel's code reads the same.
    public static bool Alive(int slot, System.Windows.FrameworkElement view) => Occupied(slot);

    // The demand a page hands to its plugin, never letting a plugin's exception escape into the host.
    public static void Notify(int slot, bool shown)
    {
        if (slot < 0 || slot >= 4 || Slots[slot] is not { } plugin) return;
        try { plugin.Host.Pages.FirstOrDefault()?.SetDemand(shown); }
        catch (Exception) { }
    }

    public static string Title(int slot)
    {
        if (slot < 0 || slot >= 4 || Slots[slot] is not { } plugin) return "";
        try { return Loc.IsEnglish ? plugin.Host.Pages.FirstOrDefault()?.TitleEn ?? plugin.Extension.TitleEn : plugin.Host.Pages.FirstOrDefault()?.TitleZh ?? plugin.Extension.TitleZh; }
        catch (Exception) { return plugin.Id; }
    }

    // The compacts the collapsed row draws, in plugin order: page slots first, then widget-only plugins. Empty views
    // (a plugin registered nothing) are skipped by the caller.
    public static IReadOnlyList<(Plugin Plugin, IIslandCompact Compact)> Compacts()
    {
        var list = new List<(Plugin, IIslandCompact)>();
        foreach (var plugin in Plugins)
            foreach (var compact in plugin.Host.Compacts)
                list.Add((plugin, compact));
        return list;
    }

    // The colors a plugin draws with: its monitor palette's scheme on the theme's text and surface colors. The app sets
    // the palette whenever the theme or the choice changes, before any view is created, and again on every change after.
    public static IslandColors Colors { get; private set; } = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");
    public static void SetPalette(DeckSettings settings)
    {
        Colors = With(MonitorThemeColors.For(settings));
        foreach (var plugin in Plugins)
        {
            var colors = Colors;
            foreach (var page in plugin.Host.Pages)
                try { page.OnColorsChanged(colors); } catch (Exception) { }
            foreach (var compact in plugin.Host.Compacts)
                try { compact.OnColorsChanged(colors); } catch (Exception) { }
            try { plugin.Host.RaiseColors(); } catch (Exception) { }
        }
    }
    private static IslandColors With(MonitorColors colors) => new(
        PanelTheme.Current.IsNight ? "#4FD17F" : "#1C9A50", colors.Warning, colors.Critical,
        UI.Primary, UI.Secondary, UI.Tertiary, UI.Track, UI.Fill);
}
