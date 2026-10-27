using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor page and the readings in the collapsed row. The sampler (App.Monitor) runs while any panel shows either;
// every panel draws the same readings. The page is built once per layout step (MonitorLayout), palette, sort, main gauge and
// theme; within a step it stretches, and each new reading only changes what its parts draw.
public partial class MainWindow
{
    private MonitorView? _monitorView;
    private string _monitorKey = "";
    private MonitorStrip? _compactStrip;
    // Which readings the collapsed row shows and how far each has come in or gone out, kept while the row is built again.
    private readonly StripPresence _stripPresence = new();
    private bool _monitorShowing, _stripShowing, _monitorFade;
    // The extension slot this panel last told the app it was showing, so leaving that page releases it.
    private int _extensionSlot = -1;

    private void InitializeMonitor()
    {
        IsVisibleChanged += (_, _) => UpdateMonitorDemand();
        _app.MonitorUpdated += MonitorUpdated;
        Closed += (_, _) =>
        {
            _app.MonitorUpdated -= MonitorUpdated; _app.PresentMonitor(this, false, false);
            if (_extensionSlot >= 0) _app.PresentExtension(this, _extensionSlot, false);
            _extensionSlot = -1;
            // A panel going away can be the last open one; the pages nobody watches then start their grace.
            _app.PanelUseChanged();
        };
    }

    // Asked whenever the panel opens, closes, changes page or is hidden, and whenever the collapsed row is built.
    internal void UpdateMonitorDemand()
    {
        bool page = _expanded && IsMonitorPage && IsVisible && !IsSuppressed;
        bool extension = _expanded && IsExtensionPage && IsVisible && !IsSuppressed;
        bool strip = !_expanded && _compactStrip is not null && IsVisible && !IsSuppressed && _alert is null && EffectiveStyle != CompactStyle.Line;
        if (!page)
        {
            // A list that is not on screen cannot be under the pointer, and a closed page keeps no card open.
            if (_monitorShowing && _app.Monitor is { } monitor) monitor.HoldProcesses = false;
            _monitorView?.CloseCard();
        }
        _app.PresentMonitor(this, page, strip);
        int slot = extension ? ExtensionSlot : -1;
        if (_extensionSlot != slot)
        {
            if (_extensionSlot >= 0) _app.PresentExtension(this, _extensionSlot, false);
            _extensionSlot = slot;
        }
        if (slot >= 0) _app.PresentExtension(this, slot, true);
        // Neither took readings while hidden: they show the current ones, or dashes once the sampler has paused, never the
        // figures they showed last time.
        if (page && !_monitorShowing) _monitorView?.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        if (strip && !_stripShowing) _compactStrip?.Update(_app.Monitor?.Latest);
        _monitorShowing = page; _stripShowing = strip;
    }
    private static MonitorSnapshot WaitingSnapshot => MonitorSnapshot.Empty(Environment.ProcessorCount, Loc.T("正在读取", "Reading"));

    private bool MonitorMotion => Settings.Animations && SystemParameters.ClientAreaAnimation;

    private void MonitorUpdated(MonitorSnapshot snapshot)
    {
        Motion.Enabled = MonitorMotion;
        if (!_expanded) { _compactStrip?.Update(snapshot); return; }
        if (!IsMonitorPage || _monitorView is null) return;
        if (MonitorKey() != _monitorKey) { RenderMonitor(); return; }
        _monitorView.Update(snapshot, MonitorMotion);
    }

    // The page's bottom margin for a page of this height; its sides are the header's inset.
    private static double MonitorBottom(double height) => height < 170 ? 10 : 16;
    private MonitorLayout CurrentMonitorLayout()
    {
        double height = ContentCanvas.Height;
        return MonitorLayout.For(ContentCanvas.Width - 2 * _header.Side, height - _header.Row - MonitorBottom(height), Settings.MonitorProcesses);
    }

    // What the view is built from: its layout step, not its exact size, so a page resized within a step is not built again.
    // The sort is not part of it: a new sort reorders the list in place.
    private string MonitorKey() => string.Join("|", CurrentMonitorLayout(), Settings.MonitorPalette, Settings.MonitorPalette == MonitorPalette.Custom ? Settings.MonitorCustomColors.Key : "", Settings.MonitorProcesses,
        Settings.MonitorMain, Settings.MonitorProcessView, Loc.Language, PanelTheme.Generation);

    private void RenderMonitor()
    {
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; ExtensionContent.Visibility = Visibility.Visible; MonitorContent.Visibility = Visibility.Visible;
        ShowExtensionView(-1);
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the music page does, so the switch does not move between pages. Fitting
        // the usage page for them is kept until its size, data or settings change: a drag resizes this page many times a second.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        MonitorContent.Margin = new Thickness(_header.Side, 0, _header.Side, MonitorBottom(height));
        Motion.Enabled = MonitorMotion;
        var key = MonitorKey();
        bool fade = _monitorFade; _monitorFade = false;
        if (_monitorView is not null && key == _monitorKey && MonitorContent.Children.Contains(_monitorView)) return;
        _monitorKey = key;
        // The list being replaced may have been under the pointer; its leave event never comes.
        if (_app.Monitor is { } monitor) monitor.HoldProcesses = false;
        _monitorView = new MonitorView(CurrentMonitorLayout(), MonitorThemeColors.For(Settings), Settings.MonitorMain, Settings.MonitorSort,
            Settings.MonitorProcessView, SwitchMonitorMain, SortProcesses, hold => { if (_app.Monitor is { } sampler) sampler.HoldProcesses = hold; });
        MonitorContent.Children.Clear(); MonitorContent.Children.Add(_monitorView);
        _monitorView.Update(_app.Monitor?.Latest ?? WaitingSnapshot, motion: false);
        // A new main gauge, asked for on the page, arrives with a short fade instead of a cut.
        if (fade && MonitorMotion)
            _monitorView.BeginAnimation(OpacityProperty, Soft.Fade(.15, 1, Soft.Short));
    }

    private (double Width, double Height, IReadOnlyList<ProviderSnapshot> Snapshots, DeckSettings Settings, PanelHeader Header)? _usageHeader;
    private PanelHeader CachedUsageHeader()
    {
        double width = _usageWidth, height = UsagePageHeight;
        if (_usageHeader is { } known && known.Width == width && known.Height == height && ReferenceEquals(known.Snapshots, Snapshots) && ReferenceEquals(known.Settings, Settings))
            return known.Header;
        var header = UsageHeader(width, height);
        _usageHeader = (width, height, Snapshots, Settings, header);
        return header;
    }

    // The sort and the main gauge are the same for every panel and are kept with the settings. A new main gauge rebuilds the page,
    // which fades in; a new sort only reorders the list.
    private void SortProcesses(ProcessSort sort)
    {
        if (Settings.MonitorSort == sort) return;
        Settings.MonitorSort = sort; _app.QueueSave();
        // Every panel's page, shown or not: a page kept for later is not built again, so it must not keep the old sort.
        foreach (var deck in _app.Decks) deck._monitorView?.SetSort(sort);
    }
    private void SwitchMonitorMain(MonitorMain main)
    {
        if (Settings.MonitorMain == main) return;
        Settings.MonitorMain = main; _app.QueueSave();
        RefreshMonitorPages();
    }
    private void RefreshMonitorPages()
    {
        foreach (var deck in _app.Decks) if (deck._expanded && deck.IsMonitorPage) { deck._monitorFade = true; deck.RenderMonitor(); }
    }
}
