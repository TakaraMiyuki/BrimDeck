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
        var order = Settings.PageOrder.Select(PageButton).ToList();
        if (!buttons.Children.Cast<UIElement>().SequenceEqual(order))
        {
            buttons.Children.Clear();
            foreach (var button in order) buttons.Children.Add(button);
        }
        for (int i = 0; i < order.Count; i++) order[i].Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0);
    }
    private Button PageButton(DeckPage page) => page switch
    {
        DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton,
        >= DeckPage.Extension0 => ExtensionPageButton(DeckPages.SlotOf(page)),
        _ => AIUsageButton
    };
}
