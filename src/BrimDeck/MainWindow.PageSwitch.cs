using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

public partial class MainWindow
{
    // Places the chosen page's fill under its button. While the open panel shows it, it glides there as the monitor page's sort pill
    // does; otherwise it is set in place. A switch whose buttons are not laid out yet places it once they are.
    private void PlacePageCapsule()
    {
        var button = PageButton(CurrentPage);
        if (!LayoutRetry.Laid(button))
        {
            // Retried after a layout pass, never by posting itself again: the switch is collapsed while the usage details show, its
            // buttons then keep an invalid measure and their old width, and a retry that posted itself spun the UI thread at 90%.
            if (!_capsulePending) { _capsulePending = true; LayoutRetry.WhenReady(this, () => LayoutRetry.Laid(PageButton(CurrentPage)), () => { _capsulePending = false; PlacePageCapsule(); }); }
            return;
        }
        double x = button.TranslatePoint(new Point(), (UIElement)PageCapsule.Parent).X, width = button.ActualWidth, height = button.ActualHeight;
        bool glide = _expanded && !_preview && PageSwitch.IsVisible && Soft.Enabled && PageCapsule.Width > 0 && Math.Abs(PageCapsuleShift.X - x) > .5;
        PageCapsule.Height = height;
        PageHandover.Glide(PageCapsule, WidthProperty, width, glide, Soft.Choose);
        double from = PageCapsuleShift.X;
        PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, null);
        PageCapsuleShift.X = x;
        if (glide) PageCapsuleShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(from, x, TimeSpan.FromMilliseconds(Soft.Choose))
        { EasingFunction = Soft.Follow, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
    }
    private bool _capsulePending;
    // The buttons stand in the order chosen in the settings; the first has no gap before it.
    private void OrderPageButtons()
    {
        if (AIUsageButton.Parent is not Panel buttons) return;
        // Plugin tabs and anything else that joined the strip are kept, appended after the built-in pages in the order
        // they were added — the reader's arrangement walks the built-ins; a plugin tab follows them by its slot.
        var extras = buttons.Children.Cast<UIElement>()
            .Where(child => child is not System.Windows.Controls.Button button
                || (button != AIUsageButton && button != MusicPageButton && button != MonitorPageButton)).ToList();
        var order = Settings.PageOrder.Select(PageButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order.Concat(extras)))
        {
            buttons.Children.Clear();
            foreach (var button in order) buttons.Children.Add(button);
            foreach (var extra in extras) buttons.Children.Add(extra);
        }
        for (int i = 0; i < order.Count; i++) order[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
    }
    private Button PageButton(DeckPage page) => page switch { DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton, _ => AIUsageButton };
}
