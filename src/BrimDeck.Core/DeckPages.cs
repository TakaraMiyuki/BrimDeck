namespace BrimDeck.Core;

// The pages of the open panel, in one place: their order in the page switch, whether each is shown, the size each asks for,
// and which page the panel falls back to. The panel and the settings window ask here instead of comparing pages themselves.
public static class DeckPages
{
    // The page switch, left to right, until the order is changed in the settings (DeckSettings.PageOrder).
    public static readonly IReadOnlyList<DeckPage> Order = [DeckPage.Usage, DeckPage.Music, DeckPage.Monitor];
    // Each known page once, in the saved order; a page missing from the file takes its default place after the others.
    public static List<DeckPage> Normalize(IEnumerable<DeckPage>? order) => [.. (order ?? []).Where(page => Enum.IsDefined(page)).Concat(Order).Distinct()];

    // The plugin page slots: the host (MainWindow) answers which slots are occupied and by which plugin id, and what
    // size a slot's page carries. Core stays free of the plugin loader; the delegates are set once, at startup.
    public static Func<int, bool>? SlotOccupied;
    public static Func<int, string?>? SlotId;
    public static Func<DeckSettings, int, (double Width, double Height)>? ExtensionSize;

    public static bool IsExtensionPage(DeckPage page) => page is >= DeckPage.Extension0 and <= DeckPage.Extension3;
    public static int SlotOf(DeckPage page) => IsExtensionPage(page) ? page - DeckPage.Extension0 : -1;

    public static bool Shown(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Usage => settings.UsagePage, DeckPage.Music => settings.MusicPage, DeckPage.Monitor => settings.MonitorPage,
        >= DeckPage.Extension0 => SlotOccupied?.Invoke(SlotOf(page)) == true, _ => false
    };

    public static IReadOnlyList<DeckPage> Visible(DeckSettings settings) => settings.PageOrder.Where(page => Shown(settings, page)).ToList();

    // The page shown when wanted is asked for: wanted while it is shown, otherwise the first shown page in switch order.
    // With every page switched off it is the usage page, which then says that it is off.
    public static DeckPage Resolve(DeckSettings settings, DeckPage wanted)
        => Shown(settings, wanted) ? wanted : Visible(settings) is [var first, ..] ? first : DeckPage.Usage;

    // The page the panel opens on; null keeps the last viewed page.
    public static DeckPage? Opening(DefaultDeckPage choice) => choice switch
    {
        DefaultDeckPage.Usage => DeckPage.Usage, DefaultDeckPage.Music => DeckPage.Music, DefaultDeckPage.Monitor => DeckPage.Monitor, _ => null
    };

    // Every page's size lies within these; the usage page is also at least as wide as its columns need (MinimumWidth).
    // Sizes are before the scale (PanelScales): drawn at 150 %, a page of the largest size is 1800 x 600 DIP.
    public const double MinWidth = 440, MaxWidth = 1200, MinHeight = 140, MaxHeight = 400;
    // The widest a page can be on a monitor with this work area: drawn at the scale, the work area's width less 12 DIP each side.
    public static double MaxWidthOn(double workWidth, double scale = 1) => Math.Max(MinWidth, Math.Min(MaxWidth, Math.Floor((workWidth - 24) / scale)));

    public static (double MinWidth, double MaxWidth, double MinHeight, double MaxHeight) Limits(DeckSettings settings, DeckPage page)
        => (page == DeckPage.Usage ? Math.Max(MinWidth, settings.MinimumWidth) : MinWidth, MaxWidth, MinHeight, MaxHeight);

    // Sets a page's size, as dragging an edge of the open panel does; the values are kept within the page's limits.
    public static void SetSize(DeckSettings settings, DeckPage page, double width, double height)
    {
        var (minWidth, maxWidth, minHeight, maxHeight) = Limits(settings, page);
        width = Math.Clamp(width, minWidth, Math.Max(minWidth, maxWidth)); height = Math.Clamp(height, minHeight, maxHeight);
        switch (page)
        {
            case DeckPage.Music: settings.MusicWidth = width; settings.MusicHeight = height; break;
            case DeckPage.Monitor: settings.MonitorWidth = width; settings.MonitorHeight = height; break;
            case >= DeckPage.Extension0:
                // A plugin page's size is kept on its plugin's own settings entry, keyed by the plugin's id; a slot whose
                // plugin is unknown carries nothing to key on, so the drag's size is not remembered until it loads.
                if (SlotId?.Invoke(SlotOf(page)) is not { Length: > 0 } id) break;
                var at = settings.Extensions.FindIndex(extension => extension.Id == id);
                if (at < 0) settings.Extensions.Add(new ExtensionPageSettings(id, Width: width, Height: height));
                else settings.Extensions[at] = settings.Extensions[at] with { Width = width, Height = height };
                break;
            default: settings.Width = width; settings.Height = height; break;
        }
    }

    public static (double Width, double Height) Size(DeckSettings settings, DeckPage page) => page switch
    {
        DeckPage.Music => (settings.MusicWidth, settings.MusicHeight),
        DeckPage.Monitor => (settings.MonitorWidth, settings.MonitorHeight),
        >= DeckPage.Extension0 => ExtensionSize?.Invoke(settings, SlotOf(page)) ?? ExtensionSizes.Default,
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
