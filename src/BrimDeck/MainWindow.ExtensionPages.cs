using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    // The plugin pages of the open panel: one tab button and one content host per slot, built on demand and kept, so a
    // page switch never rebuilds them. A slot whose plugin is gone drops its tab (the order rebuild keeps only the shown
    // pages) and its host hides; the caches survive to serve the plugin when it comes back.
    private readonly Dictionary<int, Button> _extensionButtons = [];
    private readonly Dictionary<int, Grid> _extensionHosts = [];
    private bool _extensionPagesWired;

    private static DeckPage ExtensionPageOf(int slot) => DeckPage.Extension0 + slot;
    private bool IsExtensionPage => DeckPages.IsExtensionPage(CurrentPage);

    // Wires the plugin pages into the panel: the size and occupancy answers DeckPages asks for, and the slot changes
    // that rebuild the tabs. Called once, where the page machinery is initialized. The startup scan ran before this
    // window existed, so its event was unheard: the occupied pages are entered into the saved order here as well.
    private void InitializeExtensionPages()
    {
        if (_extensionPagesWired) return;
        _extensionPagesWired = true;
        DeckPages.ExtensionSize = (settings, slot) =>
        {
            var id = DeckPages.SlotId?.Invoke(slot);
            if (id is not { Length: > 0 } key) return ExtensionSizes.Default;
            var entry = settings.Extensions.FirstOrDefault(extension => extension.Id == key);
            if (entry is not { Width: > 0 }) return ExtensionSizes.Default;
            return (entry.Width, entry.Height > 0 ? entry.Height : ExtensionSizes.Default.Height);
        };
        IslandExtensions.ExtensionsChanged += ExtensionSlotsChanged;
        EnsureExtensionPagesOrdered();
    }

    // Enters every occupied slot's page into the saved order once, so the tab stands and the order persists.
    private void EnsureExtensionPagesOrdered()
    {
        bool changed = false;
        for (int slot = 0; slot < 4; slot++)
        {
            if (!IslandExtensions.Occupied(slot)) continue;
            var page = ExtensionPageOf(slot);
            if (!Settings.PageOrder.Contains(page)) { Settings.PageOrder = [.. Settings.PageOrder, page]; changed = true; }
        }
        if (changed) _app.QueueSave();
    }

    // A scan rearranged the slots: rebuild the tabs (glyphs and titles may have changed), enter newly occupied pages
    // into the saved order once, and fall back to a shown page when the open page's plugin went away.
    private void ExtensionSlotsChanged()
    {
        _extensionButtons.Clear();
        EnsureExtensionPagesOrdered();
        if (DeckPages.IsExtensionPage(CurrentPage) && !DeckPages.Shown(Settings, CurrentPage))
        {
            var fallback = DeckPages.Visible(Settings) is [var first, ..] ? first : DeckPage.Usage;
            SelectPage(fallback, remember: false);
        }
        else Dispatcher.BeginInvoke(RenderPageSwitch);
    }

    // The tab of one plugin slot: the plugin's glyph in the page switch's own button style, clicking selects the page.
    private Button ExtensionPageButton(int slot)
    {
        if (_extensionButtons.TryGetValue(slot, out var cached)) return cached;
        string title = IslandExtensions.Title(slot);
        var button = new Button { Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = 24, Margin = new Thickness(1, 0, 0, 0) };
        button.Content = new Viewbox
        {
            Width = 16, Height = 16,
            Child = new System.Windows.Shapes.Path
            {
                Data = IslandExtensions.SafeGlyph(slot >= 0 && slot < 4 ? IslandExtensions.Glyph(slot) : null),
                Stretch = Stretch.Uniform, StrokeThickness = 1.65, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = UI.Brush(UI.Secondary)
            }
        };
        button.Click += (_, _) => SelectPage(ExtensionPageOf(slot));
        System.Windows.Automation.AutomationProperties.SetName(button, title);
        button.ToolTip = title;
        _extensionButtons[slot] = button;
        return button;
    }

    // The content host of one plugin slot, a sibling of the built-in page contents. Stage 2a fills it with a title
    // placeholder; the plugin's own view (IIslandPage.CreateView) arrives with the next step.
    private Grid ExtensionHost(int slot)
    {
        if (_extensionHosts.TryGetValue(slot, out var host)) return host;
        host = new Grid { Visibility = Visibility.Collapsed };
        host.Children.Add(new TextBlock
        {
            Text = IslandExtensions.Title(slot),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontSize = 14, Foreground = UI.Brush(UI.Tertiary),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });
        if (UsageContent.Parent is Panel parent)
        {
            Grid.SetRow(host, Grid.GetRow(UsageContent));
            parent.Children.Add(host);
        }
        _extensionHosts[slot] = host;
        return host;
    }

    // Every render path states every page container's visibility, the built-ins included; the plugin hosts are hidden
    // here so a built-in page never draws over a stale plugin host (the hosts are later in the panel's child order).
    internal void HideExtensionHosts()
    {
        foreach (var pair in _extensionHosts) pair.Value.Visibility = Visibility.Collapsed;
    }

    // Shows the open plugin page alone: the built-in contents, the model toolbar and the application tabs hide, the
    // header keeps the usage page's controls so the switch does not move between pages, as the monitor page does.
    private void RenderExtension()
    {
        int slot = DeckPages.SlotOf(CurrentPage);
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        foreach (var pair in _extensionHosts) pair.Value.Visibility = pair.Key == slot ? Visibility.Visible : Visibility.Collapsed;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        if (_extensionHosts.TryGetValue(slot, out var host)) host.Margin = new Thickness(_header.Side, 0, _header.Side, 10);
    }
}
