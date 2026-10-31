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
    // The plugin's own view per slot, created once and kept alive as long as the panel is: a page switch only toggles
    // the host's visibility, so the page keeps its state, and the plugin learns about being shown through SetDemand.
    // The owner tracks WHICH plugin instance the view was built from: a plugin whose file did not change is kept whole
    // across scans (its view must never be built twice — the pages hold their elements as fields), and only a slot that
    // changed hands has its view retired.
    private readonly Dictionary<int, FrameworkElement> _extensionViews = [];
    private readonly Dictionary<int, IslandExtensions.Plugin> _extensionViewOwners = [];
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
            // The reader's own size first, then the plugin's preferred box, then the host default.
            var id = DeckPages.SlotId?.Invoke(slot);
            if (id is { Length: > 0 } key)
            {
                var entry = settings.Extensions.FirstOrDefault(extension => extension.Id == key);
                if (entry is { Width: > 0 }) return (entry.Width, entry.Height > 0 ? entry.Height : ExtensionSizes.Default.Height);
            }
            if (IslandExtensions.PreferredSize(slot) is { } preferred) return preferred;
            return ExtensionSizes.Default;
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

    // A scan rearranged the slots: rebuild the tabs, retire ONLY the views whose slot changed hands (a plugin whose
    // file did not change keeps its view alive — its page holds its elements as fields, and building them twice fails),
    // enter newly occupied pages into the saved order, and fall back to a shown page when the open page went away.
    private void ExtensionSlotsChanged()
    {
        _extensionButtons.Clear();
        foreach (var slot in _extensionViewOwners.Keys.ToList())
        {
            if (slot < 4 && IslandExtensions.Slots[slot] == _extensionViewOwners[slot]) continue;
            IslandExtensions.Notify(slot, shown: false);
            if (_extensionHosts.TryGetValue(slot, out var host))
            {
                host.Children.Clear();
                if (host.Parent is Panel parent) parent.Children.Remove(host);
            }
            _extensionHosts.Remove(slot);
            _extensionViews.Remove(slot);
            _extensionViewOwners.Remove(slot);
        }
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

    // The content host of one plugin slot, a sibling of the built-in page contents.
    private Grid ExtensionHost(int slot)
    {
        if (_extensionHosts.TryGetValue(slot, out var host)) return host;
        host = new Grid { Visibility = Visibility.Collapsed };
        if (UsageContent.Parent is Panel parent)
        {
            Grid.SetRow(host, Grid.GetRow(UsageContent));
            parent.Children.Add(host);
        }
        _extensionHosts[slot] = host;
        return host;
    }

    // Creates the plugin's view for a slot, once: CreateView on the UI thread with the current palette, an honest
    // placeholder when the plugin fails, and a demand hook so the plugin runs only while its page is on screen.
    private void EnsureExtensionView(int slot)
    {
        if (slot < 0 || slot >= 4 || _extensionViews.ContainsKey(slot)) return;
        var host = ExtensionHost(slot);
        var plugin = IslandExtensions.Slots[slot];
        if (plugin is null) return;
        FrameworkElement view;
        try
        {
            view = plugin.Host.Pages.FirstOrDefault()?.CreateView(IslandExtensions.Colors)
                ?? new TextBlock { Text = Loc.T("插件未提供页面内容", "The plugin offered no page content") };
            ExtensionLog.Write($"plugin {plugin.Id}: page view created");
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"plugin {plugin.Id}: CreateView failed: {ex.Message.Split('\n')[0]}");
            view = new TextBlock
            {
                Text = Loc.T("插件页面加载失败", "The plugin page failed to load"),
                FontSize = 13, Foreground = UI.Brush(UI.Tertiary),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
        }
        view.IsHitTestVisible = true;
        host.Children.Add(view);
        _extensionViews[slot] = view;
        _extensionViewOwners[slot] = plugin;
        // The demand follows the host's visibility: a page that is on screen (panel open, page chosen) asks the plugin
        // to run; one that is switched away, or whose panel collapsed, lets it stop.
        host.IsVisibleChanged += (_, _) => IslandExtensions.Notify(slot, host.IsVisible);
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
        EnsureExtensionView(slot);
    }
}
