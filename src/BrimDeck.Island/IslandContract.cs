using System.Windows;

namespace BrimDeck.Island;

// The contract between BrimDeck and its DLL plugins, and the only assembly a plugin references. Everything a plugin
// declares or draws crosses this file and nothing else of the host, so the host's own internals can change freely.
//
// A plugin is a class library whose output DLL sits in the plugins folder. The host loads it into its own collectible
// assembly load context, finds the one class implementing IIslandExtension, constructs it, and hands it an IIslandHost
// through Initialize — the plugin registers what it offers (panel pages, a compact widget for the capsule) and keeps
// the references it cares about. Everything runs on the WPF UI thread unless the plugin moves it itself; a plugin that
// throws loses its own call, never the app.
//
// Everything here runs in-process, which is the trust model in one line: a plugin can do whatever any program on this
// machine can. BrimDeck shows that sentence at first enable, and it is why the surface is deliberately small.

// The colors a page or widget draws with, handed over at view creation and on every theme or monitor-palette change.
// Plain "#RRGGBB" strings; the plugin owns every brush it makes.
public sealed record IslandColors(string Normal, string Warning, string Critical, string Primary, string Secondary, string Tertiary, string Track, string Fill);

// The version of this contract the host implements. A plugin that needs a newer host says so with
// IIslandExtension.MinHostVersion and is refused at load with both numbers in the reason, rather than failing later
// on a member it cannot see.
public static class IslandHost
{
    public const int Version = 1;
}

// One plugin: a class library with a public, non-abstract class implementing this interface and a parameterless
// constructor. Initialize is called once, on the UI thread, before anything the plugin registered is shown.
public interface IIslandExtension
{
    // The stable id every setting is kept under: letters, digits, dash and dot, unique across plugins.
    string Id { get; }
    // The name shown in tabs, the plugins page and the tray, in the two interface languages.
    string TitleZh { get; }
    string TitleEn { get; }
    // The tab glyph: stroke path data on a 24 by 24 grid, round caps. Unparseable data draws a plain square.
    string Glyph { get; }
    // The plugin's own version, shown on its settings page and reserved for the plugin market.
    string Version => "";
    // The plugin's repository address, reserved for the plugin market.
    string Repository => "";
    // The lowest host contract version this plugin works with. A host older than this refuses the plugin at load.
    int MinHostVersion => 1;
    // Called once on the UI thread. Register pages and compact widgets here and keep the host reference for later.
    void Initialize(IIslandHost host);
}

// The host side of the contract, passed to Initialize. Registration is the whole surface for now: pages for the open
// panel, one compact widget for the collapsed capsule, a per-plugin settings store, and the palette with its change
// event. Later contract versions add notification, command and data-read entries; nothing here is removed.
public interface IIslandHost
{
    // Adds a page to the open panel. One page per plugin in this host version; further registrations are noted on the
    // plugins page and ignored.
    void RegisterPage(IIslandPage page);
    // Adds a widget to the collapsed capsule. Its width is asked per layout; the host clamps it to the room left.
    void RegisterCompact(IIslandCompact compact);
    // The palette as it stands. Read once per view creation; later changes arrive through ColorsChanged.
    IslandColors Colors { get; }
    // Raised on the UI thread whenever the theme or the monitor palette changed. Views are not rebuilt: update the
    // brushes in place, which is what keeps a theme switch from losing the page's state.
    event Action? ColorsChanged;
    // The plugin's own settings store, keyed per plugin and persisted as strings by the host. Values are opaque to the
    // host; serialize your own object into them.
    string? GetSetting(string key);
    void SetSetting(string key, string? value);
}

// One page of the open panel, drawn by the plugin as any WPF content. The view is created once per panel that shows
// the page and stretched to the page's box; SetDemand says whether any panel is showing it right now, so a plugin that
// polls something can stop while nobody looks. OnColorsChanged keeps a theme switch from costing the page its state.
public interface IIslandPage
{
    // Stable within the plugin; the settings key is this plugin's id plus this page's id.
    string Id { get; }
    string TitleZh { get; }
    string TitleEn { get; }
    string Glyph { get; }
    // The page's content. Called on the UI thread when a panel first needs it.
    FrameworkElement CreateView(IslandColors colors);
    // The palette changed: recolor in place. The default does nothing for pages that only used the host's colors.
    void OnColorsChanged(IslandColors colors) { }
    // Whether any panel is showing this page. The first call with true may come before CreateView.
    void SetDemand(bool shown) { }
    // The page's preferred size in DIPs, used as its initial box before the user drags it. Null takes the host default.
    (double Width, double Height)? PreferredSize => null;
}

// One widget of the collapsed capsule, drawn by the plugin as any WPF content. Its height is the capsule's row; its
// width is asked at every layout and clamped by the host to what the row has left, so a widget that wants too much is
// cut rather than pushing the row wide. Hover still expands the panel — the host handles that before the widget sees
// the mouse — while clicks reach the widget's own handlers first.
public interface IIslandCompact
{
    // Stable within the plugin.
    string Id { get; }
    // The widget's content. Created once per panel and kept for the capsule's whole life.
    FrameworkElement CreateView(IslandColors colors);
    // The width the widget needs at this row height, in DIPs. Called on every layout; return a stable number for an
    // unchanged content, or the widget flickers as the row breathes.
    double DesiredWidth(double availableHeight);
    // The palette changed: recolor in place.
    void OnColorsChanged(IslandColors colors) { }
}
