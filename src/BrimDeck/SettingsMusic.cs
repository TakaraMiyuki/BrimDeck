using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private DeckPage _previewPage;
    // keepPreview marks the segments alone as preview controls, so a press on the rest of the row still ends the preview.
    private FrameworkElement MusicChoice<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true, bool keepPreview = false) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = 292, IsEnabled = enabled };
        if (keepPreview) KeepPreview(strip);
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip)); row.IsEnabled = enabled; return row;
    }
    // The options under the style cards belong to the chosen style. Choosing another card slides the current options
    // out and the chosen style's options in, moving the way the chosen card lies from the previous one.
    private Grid? _styleOptions;
    private void ShowStyleOptions(CompactStyle style, CompactStyle? previous)
    {
        if (_styleOptions is not { } host) return;
        var incoming = Collect(() => { StyleOptions(style); CollapsedSizeOptions(style); });
        var outgoing = host.Children.OfType<FrameworkElement>().LastOrDefault();
        double width = host.ActualWidth;
        if (outgoing is null || previous is not { } from || from == style || width <= 0 || !SystemParameters.ClientAreaAnimation)
        { host.Children.Clear(); host.Children.Add(incoming); return; }
        // A panel still leaving from an earlier choice is dropped; the one arriving becomes the one leaving.
        foreach (var leaving in host.Children.OfType<FrameworkElement>().Where(child => child != outgoing).ToList()) host.Children.Remove(leaving);
        double direction = style > from ? 1 : -1;
        host.ClipToBounds = true; outgoing.IsHitTestVisible = false; host.Children.Add(incoming);
        var leave = outgoing.RenderTransform as TranslateTransform ?? new TranslateTransform(); outgoing.RenderTransform = leave;
        var enter = new TranslateTransform(direction * width, 0); incoming.RenderTransform = enter;
        DoubleAnimation Slide(double to) => new(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = Soft.Out };
        var leaveMotion = Slide(-direction * width); leaveMotion.Completed += (_, _) => host.Children.Remove(outgoing);
        var enterMotion = Slide(0); enterMotion.Completed += (_, _) => { if (host.Children.Count == 1) host.ClipToBounds = false; };
        leave.BeginAnimation(TranslateTransform.XProperty, leaveMotion); enter.BeginAnimation(TranslateTransform.XProperty, enterMotion);
    }
    private void StyleOptions(CompactStyle style)
    {
        SectionTitle(Loc.T("显示内容 · ", "Content · ") + StyleName(style));
        if (style == CompactStyle.Line)
        {
            Group(Toggle(Loc.T("显示播放进度", "Show playback progress"), S.MusicIndicatorProgress, v => Change(s => s.MusicIndicatorProgress = v)));
            return;
        }
        // The rows that can be dragged are in the order the row shows them; the clock always stays at the right, so it comes last.
        var rows = new StackPanel();
        foreach (var part in S.CompactOrder(style)) rows.Children.Add(PartRow(style, part, rows));
        UpdateDividers(rows);
        Group(rows, ClockRow(style, PartHandleWidth));
    }

    private const double PartRowHeight = 44, PartHandleWidth = 40;
    // One draggable row of the collapsed content: the handle, the name and the switch.
    private Border PartRow(CompactStyle style, CompactPart part, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        var (title, shown, help) = part switch
        {
            CompactPart.Rings => (Loc.T("配额环", "Quota rings"), S.ShowsSummary(style), null),
            CompactPart.Music => (Loc.T("正在播放的音乐", "Music now playing"), S.ShowsMusic(style),
                Loc.T("拖动只改变歌名或歌词的位置。封面始终在最左侧，律动条在右侧。", "Dragging moves only the title or lyric. The cover stays at the left and the equalizer at the right.")),
            _ => (Loc.T("系统监控", "System monitor"), S.ShowsMonitor(style), null)
        };
        void Show(bool on) => Change(s =>
        {
            switch (part)
            {
                case CompactPart.Rings: if (notch) s.NotchSummary = on; else s.CapsuleSummary = on; break;
                case CompactPart.Music: if (notch) s.NotchMusic = on; else s.CapsuleMusic = on; break;
                default: if (notch) s.NotchMonitor = on; else s.CapsuleMonitor = on; break;
            }
        });
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = SettingLabel(title, help is null ? null : () => help); Grid.SetColumn(label, 1); row.Children.Add(label);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => Show(true); toggle.Unchecked += (_, _) => Show(false);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        // Dropped in a new place, the collapsed row takes the new order at once.
        AttachDrag(handle, () => rows, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactPart).Select(b => (CompactPart)b.Tag).ToArray();
            Change(s => s.SetCompactOrder(style, order));
        });
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
    }
    // Settings every player shares: colors, cover and text.
    private void MusicStyle()
    {
        SectionTitle(Loc.T("收起时", "When collapsed"));
        Group(MusicChoice(Loc.T("中间文字", "Middle text"), S.MusicText, new[] { (CompactMusicText.None, Loc.T("无", "None")), (CompactMusicText.Title, Loc.T("歌名", "Title")), (CompactMusicText.Lyrics, Loc.T("歌词", "Lyrics")) }, v => Change(s => s.MusicText = v)),
            MusicChoice(Loc.T("律动条颜色", "Equalizer color"), S.MusicCoverColor, new[] { (true, Loc.T("封面主色", "Cover color")), (false, Loc.T("主题默认色", "Theme default")) }, v => Change(s => s.MusicCoverColor = v)),
            Toggle(Loc.T("切歌时短暂显示歌名", "Briefly show the title on a track change"), S.MusicTrackNotice, v => Change(s => s.MusicTrackNotice = v)));
        SectionTitle(Loc.T("展开时", "When expanded"));
        Group(Toggle(Loc.T("显示歌词", "Show lyrics"), S.LyricsEnabled, v => Change(s => s.LyricsEnabled = v), S.MusicPage));
    }
    // Operations only one player supports, grouped under that player's name.
    private void Players()
    {
        SectionTitle(Loc.T("网易云音乐", "NetEase Cloud Music"));
        Group(Toggle(Loc.T("始终显示“启用完整控制”按钮", "Always show \"Enable full control\""), S.NeteaseFullControlEntry, v => Change(s => s.NeteaseFullControlEntry = v), S.MusicPage,
            Loc.T("在音乐页面右上角的来源菜单末尾固定显示“网易云音乐 · 完整控制”，以便随时启用完整控制。启用完整控制后，可以在面板中拖动网易云音乐的播放进度，并切换播放模式。",
                "Keeps \"NetEase Cloud Music · Full control\" at the end of the source menu at the top right of the music page, so full control can be enabled at any time. With full control, NetEase Cloud Music's playback position can be dragged in the panel and its playback mode switched.")));
    }
    private void MusicSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MusicWidth, 440, 1200, value => Resize(s => s.MusicWidth = value, DeckPage.Music)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MusicHeight, 140, 400, value => Resize(s => s.MusicHeight = value, DeckPage.Music)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MusicSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MusicSize", IsChecked = (S.MusicWidth, S.MusicHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("音乐快捷尺寸 ", "Music quick size ") + PanelSizes.Name(size)); choice.Checked += (_, _) => { Resize(s => { s.MusicWidth = w; s.MusicHeight = h; }, DeckPage.Music); ShowPage(2); }; sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
