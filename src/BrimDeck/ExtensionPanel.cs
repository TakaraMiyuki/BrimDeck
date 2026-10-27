using System.Windows;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

// The extension pages' rendering: each panel keeps one view per occupied slot and stretches it with the page. The
// extension owns its view's content entirely; the panel only owns the chrome every page shares — the header, the
// margins, and which slot is showing.
public partial class MainWindow
{
    private readonly FrameworkElement?[] _extensionViews = new FrameworkElement?[4];
    private readonly string?[] _extensionKeys = new string?[4];

    // A web page restyles itself from the pushes the host sends on every theme, palette or language change, so a view
    // is built once per panel and kept: recreating it would throw away the page for no gain. The key stays a constant.
    private string ExtensionKey(int slot) => "web";

    // What a plugin's page never sees: the header row above it, the margin below, and the two side insets — the panel's
    // own spending, in the same steps RenderExtension applies them. A manifest's minimum is written in the page's own
    // pixels, so it becomes a panel size only once these are added back; asking the author to know them would make every
    // plugin re-derive the panel's layout rules.
    public static (double Width, double Height) PanelFloorFor(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 && viewportHeight <= 0) return (0, 0);
        double height = viewportHeight + 46; // a 36 row over a 10 margin is the common case; the loop settles the tiers
        for (int at = 0; at < 3; at++) height = viewportHeight + ToolbarFor(height) + (height < 170 ? 8 : 10);
        double side = Math.Max(height < 170 ? 14 : 20, PanelHeader.CornerClear);
        return (viewportWidth <= 0 ? 0 : viewportWidth + 2 * side, height);
    }

    // A scan replaced or retired some slots: their views and tabs are dropped, the tabs are rebuilt, and the panel
    // re-renders — the page it stood on resolves to another when its slot is gone, and the fresh extension learns the
    // demand of the page that survived.
    internal void OnExtensionsChanged(IReadOnlyList<int> slots)
    {
        foreach (var slot in slots)
        {
            if (_extensionViews[slot] is { } view) ExtensionContent.Children.Remove(view);
            _extensionViews[slot] = null;
            _extensionKeys[slot] = null;
            if (_extensionButtons[slot] is { } button) PageButtons.Children.Remove(button);
            _extensionButtons[slot] = null;
        }
        BuildExtensionButtons();
        RenderPageSwitch();
        UpdateMonitorDemand();
        if (_expanded)
        {
            ApplySettings();
            // A plugin that just appeared gets its page built now, as an opening panel does: the first switch to it is
            // warm instead of paying the page's whole load in front of the user.
            EnsureExtensionViews();
        }
    }

    internal void RenderExtension()
    {
        int slot = ExtensionSlot;
        if (slot < 0 || !IslandExtensions.Occupied(slot)) return;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MusicContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ExtensionContent.Visibility = Visibility.Visible;
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double width = ContentCanvas.Width, height = ContentCanvas.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;
        // The header keeps the usage page's controls, as the other pages do, so the switch does not move between pages.
        var header = PanelHeader.For(ToolbarFor(height), height < 170 ? 14 : 20);
        ApplyHeader(Settings.UsagePage ? header.WithControlsOf(CachedUsageHeader()) : header);
        ExtensionContent.Margin = new Thickness(_header.Side, 0, _header.Side, height < 170 ? 8 : 10);
        EnsureExtensionViews();
        ShowExtensionView(slot);
    }

    // A view is built once for the panel's life and never taken off the window: a page switch hides it (Visibility),
    // which keeps the page alive, so coming back is at once. What the page costs while nobody watches it is the host's
    // to reclaim — after the usual grace it navigates the page to blank, and the next visit simply loads it again.
    // Called when the panel expands, so every plugin page is ready before the first switch to it.
    internal void EnsureExtensionViews()
    {
        if (IslandExtensions.OccupiedCount == 0) return;
        for (int slot = 0; slot < IslandExtensions.Slots.Length; slot++)
        {
            if (!IslandExtensions.Occupied(slot)) continue;
            var key = ExtensionKey(slot);
            if (_extensionViews[slot] is { } kept && _extensionKeys[slot] == key && IslandExtensions.Alive(slot, kept)) continue;
            if (_extensionViews[slot] is { } old) ExtensionContent.Children.Remove(old);
            FrameworkElement view;
            try { view = IslandExtensions.Slots[slot]!.CreateView(IslandExtensions.Colors); }
            catch (Exception ex)
            {
                view = new System.Windows.Controls.TextBlock
                {
                    Text = Loc.T("插件页面无法显示：", "The plugin page could not be shown: ") + ex.Message.Split('\n')[0],
                    Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
                };
            }
            view.Visibility = Visibility.Hidden;
            _extensionViews[slot] = view;
            _extensionKeys[slot] = key;
            ExtensionContent.Children.Add(view);
            ExtensionLog.Write("panel: view built for slot " + slot);
        }
    }

    // The chosen slot's view is the one that shows; the others stay in the tree, hidden and costless to switch back to.
    private void ShowExtensionView(int slot)
    {
        for (int at = 0; at < _extensionViews.Length; at++)
            if (_extensionViews[at] is { } view)
                view.Visibility = at == slot ? Visibility.Visible : Visibility.Hidden;
        if (slot < 0) return;
        if (EnteringExtension) VeilArrival(); else LiftVeil();
    }

    // Set while a page switch is arriving at a plugin page: the arriving page is covered for the beat it cannot be
    // trusted to show, and its entrance plays inside the browser.
    internal bool EnteringExtension { get; set; }

    // A WebView that was off screen keeps handing out its last composited frame for a few frames after it comes back,
    // so the arriving page is briefly seen at full brightness over the page it replaces — the overlap that reads as a
    // stutter. The panel's own surface covers that beat, then lifts while the page fades in behind it.
    private void VeilArrival()
    {
        ArrivalVeil.Background = _surface;
        ArrivalVeil.Opacity = 1;
        ArrivalVeil.Visibility = Visibility.Visible;
        // Last child, so it lies over every page view: a Grid draws its children in document order.
        ExtensionContent.Children.Remove(ArrivalVeil);
        ExtensionContent.Children.Add(ArrivalVeil);
        if (!Soft.Enabled) return;
        // Held for the beat the stale frame lasts, then lifted quickly: the page's own entrance carries the rest of the
        // way in, and a cover still half-opaque that late would only make the arrival drag.
        var lift = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            BeginTime = TimeSpan.FromMilliseconds(90),
            FillBehavior = FillBehavior.Stop
        };
        lift.Completed += (_, _) =>
        {
            ArrivalVeil.BeginAnimation(OpacityProperty, null);
            ArrivalVeil.Opacity = 1;
            ArrivalVeil.Visibility = Visibility.Collapsed;
        };
        ArrivalVeil.BeginAnimation(UIElement.OpacityProperty, lift);
    }

    // A page shown without a switch's entrance (the panel reopened on it, a theme change) needs no cover at all.
    private void LiftVeil()
    {
        ArrivalVeil.BeginAnimation(OpacityProperty, null);
        ArrivalVeil.Opacity = 1;
        ArrivalVeil.Visibility = Visibility.Collapsed;
    }
}
