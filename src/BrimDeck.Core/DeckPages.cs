namespace BrimDeck.Core;

// The pages of the open panel, in one place: their order in the page switch, whether each is shown, the size each asks for,
// and which page the panel falls back to. The panel and the settings window ask here instead of comparing pages themselves.
public static class DeckPages
{
    // The page switch, left to right, until the order is changed in the settings (DeckSettings.PageOrder). The built-in
    // three are the tail of that list, so a file written before plugin pages existed still reads exactly as it always did.
    public static readonly IReadOnlyList<DeckPage> Order = [DeckPage.Usage, DeckPage.Music, DeckPage.Monitor];
    // Each known page once, in the saved order; a page missing from the file takes its default place after the others.
    // Plugin pages are not the tail's business: their slots are only known once the app has loaded the extensions, and
    // the settings file is read before that, so pruning or appending them here would lose the reader's arrangement.
    public static List<DeckPage> Normalize(IEnumerable<DeckPage>? order) => [.. (order ?? []).Where(page => Enum.IsDefined(page)).Concat(Order).Distinct()];

    private static readonly DeckPage[] ExtensionPages = [DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension3];

    // The switch's order as the reader sees it: the saved list, then every loaded plugin page it has never heard of —
    // a plugin installed after the file was written, or one whose slot moved, gets a place at the end in slot order.
    // Kept whole rather than filtered: the arrangement is about places, and which of them are shown is Visible's business.
    public static List<DeckPage> Arrangement(DeckSettings settings)
    {
        var list = settings.PageOrder.ToList();
        list.AddRange(ExtensionPages.Where(page => !list.Contains(page) && SlotOccupied?.Invoke(SlotOf(page)) == true));
        return list;
    }

    // Put the plugin pages in exactly the order the reader dropped them into, leaving every built-in page where it
    // stands: the rows dragged are only the plugin ones, so only the places those rows already hold are rewritten.
    public static void ReorderExtensions(DeckSettings settings, IReadOnlyList<int> slotsInOrder)
    {
        var wanted = new List<DeckPage>();
        foreach (int slot in slotsInOrder)
            if (slot >= 0 && slot < ExtensionPages.Length && SlotOccupied?.Invoke(slot) == true && !wanted.Contains(ExtensionPages[slot])) wanted.Add(ExtensionPages[slot]);
        Reorder(settings, wanted, IsPluginPage);
    }

    // The same rule seen from the other settings section: the 显示的页面 rows arrange the built-in three and leave every
    // plugin page exactly where the plugin rows put it. One list, two sections, neither able to drop the other's places —
    // nor to touch a page left behind by a plugin that is not loaded, which belongs to neither row.
    public static void ReorderBuiltInPages(DeckSettings settings, IEnumerable<DeckPage> pagesInOrder)
        => Reorder(settings, pagesInOrder.Where(page => SlotOf(page) < 0).Distinct().ToList(), page => SlotOf(page) < 0);

    private static bool IsPluginPage(DeckPage page) => SlotOf(page) >= 0 && SlotOccupied?.Invoke(SlotOf(page)) == true;

    // The plugins page's "restore defaults": the plugin rows go back to their own defaults and their places come out of
    // the saved order, so the plugins stand in slot order again. The built-in pages keep the order their own page set —
    // their places were not written here.
    public static void ResetExtensionPlaces(DeckSettings settings) => settings.PageOrder = [.. settings.PageOrder.Where(page => SlotOf(page) < 0)];

    // Write `wanted` into the places of the saved list that belong to this section. A page the caller left out is not
    // dropped — it joins at the end, in the place it already holds — and a page nothing is loaded in is not a place.
    private static void Reorder(DeckSettings settings, List<DeckPage> wanted, Func<DeckPage, bool> isMine)
    {
        var list = Arrangement(settings);
        var places = Enumerable.Range(0, list.Count).Where(at => isMine(list[at])).ToList();
        foreach (var page in list) if (isMine(page) && !wanted.Contains(page)) wanted.Add(page);
        if (wanted.Count != places.Count) return;
        for (int at = 0; at < places.Count; at++) list[places[at]] = wanted[at];
        settings.PageOrder = list;
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

    public static List<DeckPage> Visible(DeckSettings settings) => Arrangement(settings).Where(page => Shown(settings, page)).ToList();

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

    // Every page's size lies within these; the usage page is also at least as wide as its columns need (MinimumWidth),
    // and a plugin can raise the floor for its own page, never lower it. Sizes are before the scale (PanelScales): drawn
    // at 150 %, a page of the largest size is 1800 x 600 DIP.
    public const double MinWidth = 440, MaxWidth = 1200, MinHeight = 140, MaxHeight = 400;
    // The widest a page can be on a monitor with this work area: drawn at the scale, the work area's width less 12 DIP each side.
    public static double MaxWidthOn(double workWidth, double scale = 1) => Math.Max(MinWidth, Math.Min(MaxWidth, Math.Floor((workWidth - 24) / scale)));

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

// The scale BrimDeck is drawn at (DeckSettings.PanelScale), in percent: one of Choices; any other value is the default.
public static class PanelScales
{
    public const int Default = 100;
    public static readonly IReadOnlyList<int> Choices = [100, 150];
    public static int Fit(int percent) => Choices.Contains(percent) ? percent : Default;
    public static double Factor(DeckSettings settings) => Fit(settings.PanelScale) / 100d;
}
