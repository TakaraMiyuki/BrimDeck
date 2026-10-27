namespace BrimDeck.Core;

// The pages of the open panel, in one place: their order in the page switch, whether each is shown, the size each asks for,
// and which page the panel falls back to. The panel and the settings window ask here instead of comparing pages themselves.
public static class DeckPages
{
    // The page switch, left to right: the built-in three, then the plugin pages in the order the reader arranged them.
    // A plugin the reader never moved carries no place and goes last; places that tie — two untouched plugins, or a file
    // written by hand — fall back to slot order, so a settings file nobody rearranged reads exactly as it always did.
    // A list rather than a read-only view: asking where a page sits is the whole point of asking for the order.
    public static List<DeckPage> Order(DeckSettings settings) =>
        [DeckPage.Usage, DeckPage.Music, DeckPage.Monitor,
        .. ExtensionPages.Select(page => (page, slot: SlotOf(page), rank: Rank(settings, page)))
            .OrderBy(item => item.rank).ThenBy(item => item.slot).Select(item => item.page)];

    private static readonly DeckPage[] ExtensionPages = [DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension3];

    // The place written on the plugin's own row, which only an arrangement writes. Everything else that creates a row —
    // the switch, a resize, a value the plugin's page saved — leaves it at -1, and an unplaced plugin follows the others.
    private static int Rank(DeckSettings settings, DeckPage page)
    {
        if (SlotId?.Invoke(SlotOf(page)) is not { Length: > 0 } id) return int.MaxValue;
        var placed = settings.Extensions.Find(entry => entry.Id == id)?.Order ?? -1;
        return placed >= 0 ? placed : int.MaxValue;
    }

    // Set by the app at startup: whether each extension slot has an extension behind it, that extension's id, and how
    // small its page may go. Without them the slots read as empty, which is what the plain tests see.
    public static Func<int, bool>? SlotOccupied;
    public static Func<int, string?>? SlotId;
    // The floor a plugin asks for its own page: (0, 0) when it declares none. A page that loses content below a certain
    // box cannot be dragged, or left saved, under that box.
    public static Func<int, (double Width, double Height)>? SlotMinimum;
    public static DeckPage ExtensionPage(int slot) => slot switch
    {
        <= 0 => DeckPage.Extension0, 1 => DeckPage.Extension1, 2 => DeckPage.Extension2, _ => DeckPage.Extension3
    };
    public static int SlotOf(DeckPage page) => page switch
    {
        DeckPage.Extension0 => 0, DeckPage.Extension1 => 1, DeckPage.Extension2 => 2, DeckPage.Extension3 => 3, _ => -1
    };

    public static bool Shown(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Usage => settings.UsagePage, DeckPage.Music => settings.MusicPage, DeckPage.Monitor => settings.MonitorPage,
            DeckPage.Extension0 or DeckPage.Extension1 or DeckPage.Extension2 or DeckPage.Extension3 => SlotShown(settings, SlotOf(page)), _ => false
    };

    public static List<DeckPage> Visible(DeckSettings settings) => Order(settings).Where(page => Shown(settings, page)).ToList();

    // The page shown when wanted is asked for: wanted while it is shown, otherwise the first shown page in switch order.
    // With every page switched off it is the usage page, which then says that it is off.
    public static DeckPage Resolve(DeckSettings settings, DeckPage wanted)
        => Shown(settings, wanted) ? wanted : Visible(settings) is [var first, ..] ? first : DeckPage.Usage;

    // A slot is shown while its extension is loaded and its own switch is on; an entry that has not been saved yet
    // counts as on, so a freshly loaded extension appears without a visit to the settings.
    internal static bool SlotShown(DeckSettings settings, int slot)
        => SlotOccupied?.Invoke(slot) == true && SlotId?.Invoke(slot) is { } id
            && settings.Extensions.FirstOrDefault(e => e.Id == id) is not { Shown: false };

    internal static (double Width, double Height) SlotSize(DeckSettings settings, int slot)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Limits(settings, ExtensionPage(slot));
        // Read back at the floor: a size older than the plugin's ask, one a wider monitor's drag left behind — and the
        // size a plugin with no entry yet starts at, since its very first page must not be the clipped one.
        (double Width, double Height) saved = SlotId?.Invoke(slot) is { } id && settings.Extensions.FirstOrDefault(e => e.Id == id) is { } entry
            ? (entry.Width, entry.Height) : ExtensionSizes.Default;
        return (Math.Clamp(saved.Width, minWidth, Math.Max(minWidth, maxWidth)), Math.Clamp(saved.Height, minHeight, maxHeight));
    }

    // The page the panel opens on; null keeps the last viewed page.
    public static DeckPage? Opening(DefaultDeckPage choice) => choice switch
    {
        DefaultDeckPage.Usage => DeckPage.Usage, DefaultDeckPage.Music => DeckPage.Music, DefaultDeckPage.Monitor => DeckPage.Monitor,
            DefaultDeckPage.Extension0 => DeckPage.Extension0, DefaultDeckPage.Extension1 => DeckPage.Extension1,
            DefaultDeckPage.Extension2 => DeckPage.Extension2, DefaultDeckPage.Extension3 => DeckPage.Extension3, _ => null
    };

    // Every page's size lies within these; a plugin can raise the floor for its own page, never lower it.
    public const double MinWidth = 440, MaxWidth = 1200, MinHeight = 140, MaxHeight = 400;

    // Every page's size lies within these; the usage page is also at least as wide as its columns need (MinimumWidth),
    // and an extension page at least as large as its plugin asks for — but never larger than a page can be at all: a
    // manifest that asks beyond the ceiling is read as asking for the ceiling.
    public static (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) Limits(DeckSettings settings, DeckPage page)
    {
        double minWidth = MinWidth, minHeight = MinHeight;
        var asked = SlotOf(page) is var slot && slot >= 0 ? SlotMinimum?.Invoke(slot) : null;
        if (asked is not null)
        {
            minWidth = Math.Max(minWidth, Math.Min(MaxWidth, asked.Value.Width));
            minHeight = Math.Max(minHeight, Math.Min(MaxHeight, asked.Value.Height));
        }
        return (page == DeckPage.Usage ? Math.Max(minWidth, settings.MinimumWidth) : minWidth, MaxWidth, minHeight, MaxHeight);
    }

    // Sets a page's size, as dragging an edge of the open panel does; the values are kept within the page's limits.
    public static void SetSize(DeckSettings settings, DeckPage page, double width, double height)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Limits(settings, page);
        width = Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth)); height = Math.Clamp(height, minHeight, maxHeight);
        switch (page)
        {
            case DeckPage.Music: settings.MusicWidth = width; settings.MusicHeight = height; break;
            case DeckPage.Monitor: settings.MonitorWidth = width; settings.MonitorHeight = height; break;
            case DeckPage.Extension0 or DeckPage.Extension1 or DeckPage.Extension2 or DeckPage.Extension3:
            {
                if (SlotId?.Invoke(SlotOf(page)) is { } id)
                {
                    var entry = settings.Extensions.FirstOrDefault(e => e.Id == id);
                    if (entry is null) { entry = new ExtensionPageSettings(id); settings.Extensions.Add(entry); }
                    settings.Extensions[settings.Extensions.IndexOf(entry)] = entry with { Width = width, Height = height };
                }
                break;
            }
            default: settings.Width = width; settings.Height = height; break;
        }
    }

    public static (double Width, double Height) Size(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Music => (settings.MusicWidth, settings.MusicHeight),
        DeckPage.Monitor => (settings.MonitorWidth, settings.MonitorHeight),
        DeckPage.Extension0 or DeckPage.Extension1 or DeckPage.Extension2 or DeckPage.Extension3
            => SlotSize(settings, SlotOf(page)),
        _ => (Math.Max(settings.Width, settings.MinimumWidth), settings.Height)
    };

    // The largest width and height among the shown pages (the usage page when none is): the window holds any of them, so
    // switching pages never resizes the window itself.
    public static (double Width, double Height) Largest(DeckSettings settings)
    {
        var pages = Visible(settings) is { Count: > 0 } visible ? visible : [DeckPage.Usage];
        return (pages.Max(page => Size(settings, page).Width), pages.Max(page => Size(settings, page).Height));
    }
}
