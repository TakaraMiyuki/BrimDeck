using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The plugin pages' rendering: each panel keeps one view per occupied slot and stretches it with the page. The plugin
// owns its view's content entirely; the panel only owns the chrome every page shares — the header, the margins, and
// which slot is showing. A view is a plain WPF element the plugin built, so it is shown by visibility alone: there is
// no arrival veil here like the web host needed, because a hidden WPF view draws correctly the moment it is shown.
public partial class MainWindow
{
    private readonly FrameworkElement?[] _extensionViews = new FrameworkElement?[4];
    // The plugin page's tab, built when its slot filled; placed by PageSwitch's ordering with the built-in tabs.
    private readonly System.Windows.Controls.Button?[] _extensionButtons = new System.Windows.Controls.Button?[4];

    // What a plugin's page never sees: the header row above it, the margin below, and the two side insets — the panel's
    // own spending, in the same steps RenderExtension applies them. A PreferredSize is written in the page's own DIPs,
    // so it becomes a panel size only once these are added back; asking the author to know them would make every plugin
    // re-derive the panel's layout rules.
    public static (double Width, double Height) PanelFloorFor(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 && viewportHeight <= 0) return (0, 0);
        double height = viewportHeight + 46; // a 36 row over a 10 margin is the common case; the loop settles the tiers
        for (int at = 0; at < 3; at++) height = viewportHeight + ToolbarHeightFor(height) + (height < 170 ? 8 : 10);
        double side = Math.Max(height < 170 ? 14 : 20, PanelHeader.CornerClear);
        return (viewportWidth <= 0 ? 0 : viewportWidth + 2 * side, height);
    }

    // The header row's height at a page height: the tiers RenderExtension applies, stated once so the floor and the
    // render agree. (36 is the common row; shorter pages take the small tier.)
    private static double ToolbarHeightFor(double pageHeight) => 36;

    // A scan replaced or retired some slots: their views and tabs are dropped, the tabs are rebuilt, and the panel
    // re-renders — the page it stood on resolves to another when its slot is gone, and the fresh plugin learns the
    // demand of the page that survived.
    internal void OnExtensionsChanged()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            if (_extensionViews[slot] is { } view) ExtensionContent.Children.Remove(view);
            _extensionViews[slot] = null;
            if (_extensionButtons[slot] is { } button) PageButtons.Children.Remove(button);
            _extensionButtons[slot] = null;
        }
        BuildExtensionButtons();
        OrderPageButtons();
        UpdateMonitorDemand();
        if (_expanded)
        {
            ApplySettings(false);
            // A plugin that just appeared gets its page built now, as an opening panel does: the first switch to it is
            // warm instead of paying the page's whole build in front of the user.
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
        // The header keeps the standard row, as the other pages do, so the switch does not move between pages.
        ApplyHeader(PanelHeader.For(ToolbarHeightFor(height), height < 170 ? 14 : 20));
        ExtensionContent.Margin = new Thickness(_header.Side, 0, _header.Side, height < 170 ? 8 : 10);
        EnsureExtensionViews();
        ShowExtensionView(slot);
        try { IslandExtensions.Notify(slot, shown: _expanded && !_preview); } catch (Exception) { }
    }

    // A view is built once for the panel's life and never taken off the window: a page switch hides it (Visibility),
    // which keeps it alive, so coming back is at once and the plugin's state survives. Called when the panel expands,
    // so every plugin page is ready before the first switch to it.
    internal void EnsureExtensionViews()
    {
        if (IslandExtensions.OccupiedCount == 0) return;
        for (int slot = 0; slot < 4; slot++)
        {
            if (!IslandExtensions.Occupied(slot)) continue;
            if (_extensionViews[slot] is { } kept && IslandExtensions.Alive(slot, kept)) continue;
            if (_extensionViews[slot] is { } old) ExtensionContent.Children.Remove(old);
            FrameworkElement view;
            try { view = IslandExtensions.PageOf(slot)!.CreateView(IslandExtensions.Colors); }
            catch (Exception ex)
            {
                view = new System.Windows.Controls.TextBlock
                {
                    Text = Loc.T("插件页面无法显示：", "The plugin page could not be shown: ") + ex.Message.Split('\n')[0],
                    Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap
                };
            }
            view.Visibility = Visibility.Hidden;
            ApplyViewScale(view);
            _extensionViews[slot] = view;
            ExtensionContent.Children.Add(view);
            ExtensionLog.Write("panel: view built for slot " + slot);
        }
    }

    // A view is laid out at the panel's scale times the page and shrunk back into the page's own box, so the pixels the
    // reader is shown are the ones the page counted in (see MainWindow.ApplyScale). A view made while the panel was
    // already scaled needs it the moment it joins the tree, as does one after a scale change.
    private void ApplyViewScale(FrameworkElement view)
        => view.LayoutTransform = Scale == 1 ? Transform.Identity : new ScaleTransform(1 / Scale, 1 / Scale);

    // The chosen slot's view is the one that shows; the others stay in the tree, hidden and costless to switch back to.
    private void ShowExtensionView(int slot)
    {
        for (int at = 0; at < _extensionViews.Length; at++)
            if (_extensionViews[at] is { } view)
                view.Visibility = at == slot ? Visibility.Visible : Visibility.Hidden;
    }

    // One tab per occupied slot, appended after the built-in pages (the reader's arrangement walks them too). Rebuilt
    // whenever a scan changed the slots; the click selects the page like a built-in tab's does.
    internal void BuildExtensionButtons()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            if (!IslandExtensions.Occupied(slot)) { if (_extensionButtons[slot] is { } old) { PageButtons.Children.Remove(old); _extensionButtons[slot] = null; } continue; }
            if (_extensionButtons[slot] is not null) continue;
            string title = IslandExtensions.Title(slot);
            var button = new System.Windows.Controls.Button
            {
                Content = new Path
                {
                    Data = IslandExtensions.SafeGlyph(IslandExtensions.Glyph(slot)),
                    Stroke = UI.Brush(UI.Secondary), StrokeThickness = 1.4,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    Width = 16, Height = 16, Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                },
                Style = (Style)FindResource("PanelIconButton"),
                Margin = new Thickness(1, 0, 0, 0),
                Tag = slot,
            };
            button.Click += (_, _) => SelectPage(DeckPages.ExtensionPage(slot));
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            ToolTipService.SetToolTip(button, title);
            _extensionButtons[slot] = button;
            PageButtons.Children.Add(button);
        }
    }
}
