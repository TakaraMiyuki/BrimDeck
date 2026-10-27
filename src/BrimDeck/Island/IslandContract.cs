using System.Windows;

namespace BrimDeck.Island;

// The colors a page draws with, handed to the extension when its view is created and whenever the theme or the monitor
// palette changes. Plain "#RRGGBB" strings; the view owns every brush it makes from them.
public sealed record IslandColors(string Normal, string Warning, string Critical, string Primary, string Secondary, string Tertiary, string Track, string Fill);

// One extension page on the island. This is the host's own seam now, not a third-party contract: the only
// implementation is the built-in web adapter (WebExtension), which loads a plugin's folder — its manifest and its
// HTML — into a WebView2 and bridges the page's messages. Keeping the interface lets the slot machinery, the demand
// wiring and the panels stay written against "an extension" rather than against web specifics.
//
// The view is the extension's alone: it is placed as the page's content and stretches with the page, and nothing else
// touches it. SetDemand tells it whether any panel is showing its page, so work nobody watches can stop.
public interface IIslandExtension
{
    // The stable id the page's settings are kept under: letters, digits, dash and dot.
    string Id { get; }
    // The tab's title in the two interface languages.
    string TitleZh { get; }
    string TitleEn { get; }
    // The tab's glyph: stroke path data on a 24 by 24 grid, round caps, drawn about 1.65 thick.
    string Glyph { get; }
    // The page's content. Called once per panel that shows the page; the extension decides what to share between them.
    FrameworkElement CreateView(IslandColors colors);
    // Whether any panel is showing the page. The first call with true may come before any view exists.
    void SetDemand(bool shown);
}
