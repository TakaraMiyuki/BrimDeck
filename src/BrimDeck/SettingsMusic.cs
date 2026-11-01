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
    private FrameworkElement MusicChoice<T>(string title, T selected, (T Value, string Label)[] options, Action<T> change, bool enabled = true, bool keepPreview = false, string? help = null) where T : notnull
    {
        var strip = new UniformGrid { Columns = options.Length, Width = 292, IsEnabled = enabled };
        if (keepPreview) KeepPreview(strip);
        foreach (var option in options)
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = option.Label, GroupName = title, IsChecked = EqualityComparer<T>.Default.Equals(option.Value, selected) };
            AutomationProperties.SetName(choice, title + " " + option.Label); choice.Checked += (_, _) => change(option.Value); strip.Children.Add(choice);
        }
        var row = Row(title, SegmentBorder(strip), help: help is null ? null : () => help); row.IsEnabled = enabled; return row;
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
        // The rows are in the order the collapsed row shows the parts, left to right; only the cover keeps its place at the left.
        var rows = new StackPanel();
        _partOptions.Clear();
        foreach (var part in S.CompactOrder(style))
            rows.Children.Add(part switch
            {
                CompactPart.Equalizer => EqualizerRow(style, rows),
                CompactPart.Clock => ClockRow(style, rows),
                var pluginKey when pluginKey.StartsWith("plugin:", StringComparison.Ordinal) => PluginPartRow(style, pluginKey, rows),
                _ => PartRow(style, part, rows)
            });
        UpdateDividers(rows);
        Group(rows);
    }

    // One plugin's widget as its own part: the handle, the plugin's name and the switch, which keeps the widget out of
    // the collapsed row without losing its place in the order.
    private Border PluginPartRow(CompactStyle style, string key, StackPanel rows)
    {
        var id = key["plugin:".Length..];
        string title = id;
        try
        {
            var plugin = IslandExtensions.Plugins.First(p => p.Id == id);
            title = Loc.IsEnglish ? plugin.Extension.TitleEn : plugin.Extension.TitleZh;
        }
        catch (Exception) { }
        bool shown = S.Extensions.FirstOrDefault(extension => extension.Id == id) is not { CompactShown: false };
        void Show(bool on) => Change(s =>
        {
            var at = s.Extensions.FindIndex(extension => extension.Id == id);
            if (at < 0) s.Extensions.Add(new ExtensionPageSettings(id) { CompactShown = on });
            else s.Extensions[at] = s.Extensions[at] with { CompactShown = on };
        });
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = SettingLabel(title); Grid.SetColumn(label, 1); row.Children.Add(label);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title); toggle.Checked += (_, _) => Show(true); toggle.Unchecked += (_, _) => Show(false);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        AttachPartDrag(handle, style, rows);
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = key, RenderTransform = new TranslateTransform() };
    }

    private const double PartRowHeight = 44, PartHandleWidth = 40;
    // The parts whose options are open (the equalizer, the clock), kept while the page is rebuilt or the collapsed style changes.
    private readonly HashSet<string> _openParts = [];
    private readonly List<(Border Row, Border Options, Button Arrow)> _partOptions = [];
    // One draggable row of the collapsed content: the handle, the name and the switch.
    private Border PartRow(CompactStyle style, string part, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        var (title, shown, help) = part switch
        {
            CompactPart.Rings => (Loc.T("配额环", "Quota rings"), S.ShowsSummary(style), null),
            CompactPart.Music => (Loc.T("正在播放的音乐", "Music now playing"), S.ShowsMusic(style),
                Loc.T("拖动只改变歌名或歌词的位置。封面始终在最左侧。", "Dragging moves only the title or lyric. The cover stays at the left.")),
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
        AttachPartDrag(handle, style, rows);
        return new Border { Child = row, Height = PartRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
    }
    // Dropped in a new place, the collapsed row takes the new order at once. The open options close when a drag begins, so every
    // row is one line high while they move.
    private void AttachPartDrag(Border handle, CompactStyle style, StackPanel rows) => AttachDrag(handle, () => rows, PartRowHeight, list =>
    {
        var order = list.Children.OfType<Border>().Where(b => b.Tag is string).Select(b => (string)b.Tag).ToArray();
        Change(s => s.SetCompactOrder(style, order));
    }, ClosePartOptions);
    private void ClosePartOptions()
    {
        _openParts.Clear();
        foreach (var (row, options, arrow) in _partOptions) { options.Visibility = Visibility.Collapsed; PointArrow(arrow, false); row.Height = PartRowHeight; }
    }
    // A part with options under it: the handle, the title with its arrow, and the switch, which only turns the part on or off.
    // The title and its arrow open and close the options; the options are dimmed while the part is off. Without padding they lie
    // under the title, a little indented, as the page rows' sizes do; with it, on a card across the row.
    private Border OptionsPartRow(CompactStyle style, string part, StackPanel rows, string title, string arrowName, bool shown, Action<bool> turn, FrameworkElement content, Thickness? padding = null)
    {
        var options = padding is { } card ? new Border { Background = UI.Brush(Inset), CornerRadius = new CornerRadius(10), Padding = card, Margin = new Thickness(0, 0, 0, 12), Child = content }
            : new Border { Margin = new Thickness(20, 0, 0, 8), Child = content };
        void Enable(bool on) { options.IsEnabled = on; options.Opacity = on ? 1 : .45; }
        Enable(shown);
        Button arrow = null!;
        Border item = null!;
        void Show(bool open)
        {
            if (open) _openParts.Add(part); else _openParts.Remove(part);
            options.Visibility = open ? Visibility.Visible : Visibility.Collapsed; PointArrow(arrow, open);
            item.Height = open ? double.NaN : PartRowHeight;
        }
        arrow = ChevronButton(arrowName, () => Show(!_openParts.Contains(part)));
        arrow.Margin = new Thickness(2, 0, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(SettingLabel(title)); heading.Children.Add(arrow);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = shown, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title);
        toggle.Checked += (_, _) => { turn(true); Enable(true); }; toggle.Unchecked += (_, _) => { turn(false); Enable(false); };
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PartRowHeight) }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        Grid.SetColumn(heading, 1); row.Children.Add(heading);
        Grid.SetColumn(toggle, 2); row.Children.Add(toggle);
        Grid.SetRow(options, 1); if (padding is null) Grid.SetColumn(options, 1); Grid.SetColumnSpan(options, padding is null ? 2 : 3); row.Children.Add(options);
        // The handle, the switch and the arrow take their own clicks; a click anywhere else on the title line opens or closes the
        // options, as the size rows do.
        row.MouseLeftButtonDown += (_, e) => { if (e.GetPosition(row).Y >= PartRowHeight) return; Show(!_openParts.Contains(part)); e.Handled = true; };
        AttachPartDrag(handle, style, rows);
        item = new Border { Child = row, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = part, RenderTransform = new TranslateTransform() };
        _partOptions.Add((item, options, arrow));
        Show(_openParts.Contains(part));
        return item;
    }
    private Border EqualizerRow(CompactStyle style, StackPanel rows)
    {
        bool notch = style == CompactStyle.Notch;
        string when = Loc.T("显示时机", "Shown"), length = Loc.T("长度", "Length"), color = Loc.T("颜色", "Color");
        var options = new StackPanel();
        options.Children.Add(Lighter(MusicChoice(when, S.EqualizerAlways, [(false, Loc.T("播放音乐时", "While music plays")), (true, Loc.T("始终显示", "Always"))],
            v => Change(s => s.EqualizerAlways = v), help: Loc.T("始终显示时，没有音乐播放也会显示，读取系统的全部声音。", "When always shown, it also shows while no music plays, reading all system sound.")), when));
        options.Children.Add(Lighter(SliderRow(length, S.EqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, Loc.T("根", "bars"),
            v => Change(s => s.EqualizerBars = (int)v)), length));
        options.Children.Add(Lighter(Row(color, EqualizerSwatches(style)), color));
        return OptionsPartRow(style, CompactPart.Equalizer, rows, Loc.T("律动条", "Equalizer"), Loc.T("律动条设置", "Equalizer settings"), S.ShowsEqualizer(style),
            on => Change(s => { if (notch) s.NotchEqualizer = on; else s.CapsuleEqualizer = on; }), options);
    }
    // The clock's swatches but the gradient: the theme's equalizer color, the presets, a custom color and the cover's.
    private StackPanel EqualizerSwatches(CompactStyle compact)
    {
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        ClockColor[] order = [ClockColor.White, ClockColor.Amber, ClockColor.Cyan, ClockColor.Pink, ClockColor.Lime, ClockColor.Custom, ClockColor.Cover];
        foreach (var color in order)
        {
            string name = ColorName(color);
            var swatch = new RadioButton { Style = (Style)FindResource("SettingsClockSwatch"), GroupName = "EqualizerColor" + compact, IsChecked = S.EqualizerColor == color,
                Background = EqualizerSwatchFill(color), ToolTip = name, Margin = new Thickness(swatches.Children.Count == 0 ? 0 : 6, 0, 0, 0) };
            AutomationProperties.SetName(swatch, Loc.T("律动条颜色 ", "Equalizer color ") + name);
            swatch.Checked += (_, _) => Change(s => s.EqualizerColor = color);
            if (color == ClockColor.Cover) swatch.Content = MusicVisuals.Icon("music", 13, UI.Brush(SwatchInk(SampleCover)));
            if (color == ClockColor.Custom)
            {
                // Choosing this swatch also opens the picker; each later click opens it again.
                swatch.Content = Pencil(ClockFace.Parse(S.EqualizerCustomColor));
                AttachClockPicker(swatch, () => S.EqualizerCustomColor, ClockColors.DefaultCustom, value =>
                {
                    Change(s => { s.EqualizerCustomColor = value; s.EqualizerColor = ClockColor.Custom; });
                    swatch.Background = EqualizerSwatchFill(ClockColor.Custom); swatch.Content = Pencil(ClockFace.Parse(value));
                });
            }
            swatches.Children.Add(swatch);
        }
        return swatches;
    }
    private Brush EqualizerSwatchFill(ClockColor color) => color switch
    {
        ClockColor.Custom => ClockFace.Fill([ClockFace.Parse(S.EqualizerCustomColor)]),
        ClockColor.Cover => ClockFace.Fill([SampleCover]),
        ClockColor.White => UI.Brush(PanelTheme.Hex("equalizer")),
        _ => ClockFace.Fill([PanelTheme.Readable(ClockFace.Preset(color))])
    };
    // Labels inside an open row are set lighter than the row's own title, as "快捷尺寸" is (QuickRow).
    private static FrameworkElement Lighter(FrameworkElement row, string title)
    {
        static IEnumerable<TextBlock> Texts(DependencyObject parent) => LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()
            .SelectMany(child => child is TextBlock text ? new[] { text } : Texts(child));
        if (Texts(row).FirstOrDefault(text => text.Text == title) is { } label) label.FontWeight = FontWeights.Normal;
        return row;
    }
    // Settings every player shares: colors, cover and text.
    private void MusicStyle()
    {
        SectionTitle(Loc.T("收起时", "When collapsed"));
        Group(MusicChoice(Loc.T("中间文字", "Middle text"), S.MusicText, new[] { (CompactMusicText.None, Loc.T("无", "None")), (CompactMusicText.Title, Loc.T("歌名", "Title")), (CompactMusicText.Lyrics, Loc.T("歌词", "Lyrics")) }, v => Change(s => s.MusicText = v)),
            Toggle(Loc.T("切歌时短暂显示歌名", "Briefly show the title on a track change"), S.MusicTrackNotice, v => Change(s => s.MusicTrackNotice = v)));
        SectionTitle(Loc.T("展开时", "When expanded"));
        var bars = SliderRow(Loc.T("律动条长度", "Equalizer length"), S.MusicEqualizerBars, CompactMusicLayout.MinimumBars, CompactMusicLayout.MaximumBars, 1, Loc.T("根", "bars"),
            v => Change(s => s.MusicEqualizerBars = (int)v));
        bars.IsEnabled = S.MusicPage;
        Group(Toggle(Loc.T("显示歌词", "Show lyrics"), S.LyricsEnabled, v => Change(s => s.LyricsEnabled = v), S.MusicPage), bars);
    }
    // Operations only one player supports, grouped under that player's name.
    private void Players()
    {
        SectionTitle(Loc.T("网易云音乐", "NetEase Cloud Music"));
        Group(Toggle(Loc.T("始终显示“启用完整控制”按钮", "Always show \"Enable full control\""), S.NeteaseFullControlEntry, v => Change(s => s.NeteaseFullControlEntry = v), S.MusicPage,
            Loc.T("在音乐页面右上角的来源菜单末尾固定显示“网易云音乐 · 完整控制”，以便随时启用完整控制。启用完整控制后，可以在面板中拖动网易云音乐的播放进度，并切换播放模式。",
                "Keeps \"NetEase Cloud Music · Full control\" at the end of the source menu at the top right of the music page, so full control can be enabled at any time. With full control, NetEase Cloud Music's playback position can be dragged in the panel and its playback mode switched.")),
            Toggle(Loc.T("启动后自动启用完整控制", "Enable full control when it starts"), S.NeteaseAutoFullControl, NeteaseAutoFullControl, S.MusicPage,
                Loc.T("网易云音乐以普通方式启动后，BrimDeck 会自动将它重启一次，改为完整控制，通常需要几秒钟。",
                    "When NetEase Cloud Music is started the normal way, BrimDeck restarts it once with full control, which usually takes a few seconds.")));
        SectionTitle("Apple Music");
        Group(Toggle(Loc.T("始终启用完整控制", "Always enable full control"), S.AppleMusicFullControl, v => Change(s => s.AppleMusicFullControl = v), S.MusicPage,
            Loc.T("BrimDeck 启动后自动启用 Apple Music 的完整控制。", "Enables full control of Apple Music when BrimDeck starts.")));
    }
    // Switching it on is confirmed with the explanation of the debugging port; declining rebuilds the page with the switch off.
    private void NeteaseAutoFullControl(bool on)
    {
        if (on && new NeteaseControlWindow(S.Theme, NeteaseControlAction.Auto) { Owner = this }.ShowDialog() != true)
        {
            Dispatcher.BeginInvoke(() => ShowPage(_page)); return;
        }
        Change(s => s.NeteaseAutoFullControl = on);
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
