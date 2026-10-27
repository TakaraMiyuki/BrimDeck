using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;
using MediaState = BrimDeck.Core.MediaState;

namespace BrimDeck;

public partial class MainWindow
{
    private static readonly DependencyProperty SuppressMusicToolTipsProperty = DependencyProperty.RegisterAttached(
        "SuppressMusicToolTips", typeof(bool), typeof(MainWindow), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    static MainWindow()
    {
        // ToolTipOpening is a direct event. A class handler covers nested template elements as well as their controls.
        static void Suppress(object sender, ToolTipEventArgs e)
        {
            if (sender is DependencyObject element && (bool)element.GetValue(SuppressMusicToolTipsProperty)) e.Handled = true;
        }
        EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement), ToolTipService.ToolTipOpeningEvent, new ToolTipEventHandler(Suppress), true);
    }
    private DeckPage _page;
    private double _pageWidth = 760, _usageWidth = 760;
    private MediaSessions? _media => _app.Media;
    private MediaTrack? _mediaTrack;
    private IReadOnlyList<MediaTrack> _mediaTracks = [];
    private string? _pinnedMedia;
    private BitmapSource? _mediaCover;
    private Brush _musicAccent = MusicVisuals.Accent(null);
    // The color the cover gave, if it gave one; _musicAccent is the theme's equalizer color otherwise.
    private Color? _coverAccent;
    private Lyrics _lyrics = Lyrics.Empty;
    private CancellationTokenSource? _lyricsRequest;
    private string _lyricsKey = "", _musicRenderKey = "", _compactLyric = "";
    // Drives the equalizer, which must keep moving while the cursor moves; see PromptTimer.
    private PromptTimer _musicTick = null!;
    private DateTimeOffset _lastMusicTick = DateTimeOffset.UtcNow, _positionTick, _trackNoticeUntil;
    private readonly TrackNoticeGate _trackNotice = new();
    private bool _musicStarted, _seeking, _updatingSeek, _commandBusy;
    private MediaTrack? _seekTrack;
    private readonly MediaSeekPreview _seekPreview = new();
    // Keeps the progress bar's room while the player moves to its next song; the timer lays the page out again when it ends.
    private readonly MusicTimelineHold _timelineHold = new();
    private DispatcherTimer? _timelineHoldEnd;
    private Slider? _musicSeek;
    private readonly ScaleTransform _musicCoverScale = new(1, 1);
    internal MusicLayout? CurrentMusicLayout { get; private set; }
    private TextBlock? _musicElapsed, _musicDuration, _lyricPrevious, _lyricNext, _musicMessage;
    // The current line scrolls when it is wider than the column; the lines around it are cut off with an ellipsis.
    private MusicMarquee? _lyricCurrent;
    private Grid? _musicInfo;
    private Canvas? _musicControls;
    private Button? _musicFullControl;
    private TextBlock? _musicSmtcHint;
    private readonly Dictionary<MediaCommand, (MusicControlButton Button, string Icon)> _musicControlButtons = new();
    private MusicEqualizer? _musicEqualizer, _compactEqualizer;
    private readonly double[] _flowLevels = new double[5];
    private MusicMarquee? _compactMarquee;
    private ClockFace? _compactClock;
    private QuotaCarousel? _quotaCarousel;
    private TimeSpan _quotaElapsed;
    // The readings' carousel while they are wider than their region, and its place in the cycle across rebuilds.
    private ReadingsCarousel? _stripCarousel;
    private TimeSpan _stripElapsed;
    // The page on screen: the chosen one while it is shown, otherwise the one DeckPages falls back to.
    internal DeckPage CurrentPage => DeckPages.Resolve(Settings, _page);
    internal bool IsMusicPage => CurrentPage == DeckPage.Music;
    internal bool IsMonitorPage => CurrentPage == DeckPage.Monitor;
    internal bool IsExtensionPage => DeckPages.SlotOf(CurrentPage) >= 0;
    internal int ExtensionSlot => DeckPages.SlotOf(CurrentPage);
    internal bool IsUsagePage => CurrentPage == DeckPage.Usage;
    internal CompactMusicLayout? CurrentCompactLayout { get; private set; }

    private void InitializeMusic()
    {
        _page = Settings.LastPage;
        _sourceMenuWatch = new(Dispatcher, TickSourceMenu) { Interval = TimeSpan.FromMilliseconds(40) };
        _musicTick = new(Dispatcher, TickMusic) { Interval = TimeSpan.FromMilliseconds(33) };
        IsVisibleChanged += (_, _) => UpdateMusicTimer();
        _app.MediaChanged += MediaChanged;
    }
    private void StartMusic()
    {
        _musicStarted = true;
        ApplyMusicSettings();
        // A panel opened while music plays takes the current song without announcing it as a new one.
        if (_media is { } media) { _pinnedMedia = media.Pinned; ApplyMediaUpdate(media.Current, media.Tracks, media.Cover, notice: false); }
    }
    private void ApplyMusicSettings()
    {
        if (!Settings.MusicPage) CloseSourceMenu();
        _page = CurrentPage;
        if (!Settings.UsagePage || !IsUsagePage) _details = false;
        if (_musicStarted) _app.StartMedia();
        if (!Settings.MediaWanted) { _mediaTrack = null; _mediaCover = null; _mediaTracks = []; _seekPreview.Observe(null, DateTimeOffset.UtcNow); }
        UpdateLyrics();
        _musicRenderKey = "";
    }
    private void ApplyDefaultMusicPage()
    {
        var wanted = DeckPages.Opening(Settings.DefaultPage) ?? _page;
        if (wanted != _page && DeckPages.Shown(Settings, wanted)) SelectPage(wanted, remember: false);
    }
    // The page switch moves as one: the page left behind leaves as a picture toward the side it is left for, while the next
    // page is built at once and comes in from the other side and the panel takes its size (PageHandover).
    internal void SelectPage(DeckPage page, bool remember = true)
    {
        if (!DeckPages.Shown(Settings, page)) return;
        bool changed = CurrentPage != page;
        if (changed) CloseSourceMenu();
        if (changed && _expanded && !_preview)
        {
            // Start before resizing: the shrinking surface can immediately fire MouseLeave.
            _hover.Stop(); _leave.Stop(); _pageSwitchHold.Stop(); _pageSwitchHold.Start();
        }
        if (remember) { Settings.LastPage = page; _app.QueueSave(); }
        bool animate = changed && _expanded && !_preview && Soft.Enabled;
        var before = CurrentPage;
        var from = PageContent(before);
        // A plugin page draws on transparency over the panel's surface; its leaving picture carries the surface as
        // its own fill, so what slides away reads as a page, not a wisp of strokes.
        var picture = animate ? PageHandover.Capture(from, DeckPages.SlotOf(before) >= 0 ? _surface : null) : null;
        var place = PageHandover.Place(from);
        int arriving = DeckPages.SlotOf(page);
        // The arriving plugin page is covered for its first frames (see VeilArrival): the show has to say so.
        EnteringExtension = arriving >= 0 && animate;
        try { ShowPage(page, animatePage: changed && _expanded && !_preview); }
        finally { EnteringExtension = false; }
        bool back = DeckPages.Order.ToList().IndexOf(page) < DeckPages.Order.ToList().IndexOf(before);
        if (arriving is var extension and >= 0)
        {
            // The page switch's own arrival cannot animate a WebView through WPF opacity: the plugin page arrives by
            // an entrance the host plays inside the browser, while the page left behind leaves as a picture above it.
            if (animate) IslandExtensions.Enter(extension, forward: !back);
            _handover.Send(picture, place, PageContent(page), back ? Handover.Back : Handover.Forward, animateArrival: false);
        }
        else
            _handover.Send(picture, place, PageContent(page), back ? Handover.Back : Handover.Forward);
    }
    // The page switch's hand-over animates the content of the page it leaves and the page it enters, so every page —
    // the extension slots included — must resolve to the grid that shows it.
    private FrameworkElement PageContent(DeckPage page) => page switch
    {
        DeckPage.Music => MusicContent,
        DeckPage.Monitor => MonitorContent,
        DeckPage.Extension0 or DeckPage.Extension1 or DeckPage.Extension2 or DeckPage.Extension3 => ExtensionContent,
        _ => UsageContent
    };
    private void ShowPage(DeckPage page, bool animatePage)
    {
        _detailPopup?.Close(); _details = false; _page = page; _musicRenderKey = ""; _handover.Drop();
        ApplySettings(animatePage);
    }
    // Places the chosen page's fill under its button. While the open panel shows it, it glides there as the monitor page's sort pill
    // does; otherwise it is set in place. A switch whose buttons are not laid out yet places it once they are.
    private void PlacePageCapsule()
    {
        var button = CurrentPage switch
        {
            DeckPage.Music => MusicPageButton, DeckPage.Monitor => MonitorPageButton,
            DeckPage.Extension0 or DeckPage.Extension1 or DeckPage.Extension2 or DeckPage.Extension3
                => _extensionButtons[DeckPages.SlotOf(CurrentPage)] ?? AIUsageButton,
            _ => AIUsageButton
        };
        if (button.Visibility != Visibility.Visible || !button.IsMeasureValid || button.ActualWidth <= 0 || PresentationSource.FromVisual(button) is null)
        {
            if (!_capsulePending) { _capsulePending = true; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { _capsulePending = false; if (button.ActualWidth > 0) PlacePageCapsule(); }); }
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
    // The wheel walks the shown pages (down moves along the switch, up moves back) or steps the system volume. A wheel that
    // a scrolling part of a page uses (the lyrics, the model details) never arrives, as those mark it handled on the way up;
    // an open menu or the detail popup keeps the wheel to itself as well.
    private void PanelWheel(object sender, MouseWheelEventArgs e)
    {
        if (Settings.MouseWheel == MouseWheelAction.None || !_expanded || _preview || _detailPopup is not null || MediaSourceButton.ContextMenu?.IsOpen == true) return;
        if (Settings.MouseWheel == MouseWheelAction.Volume) { WheelVolume(e.Delta); e.Handled = true; return; }
        var shown = DeckPages.Visible(Settings);
        if (shown.Count < 2) return;
        int at = shown.ToList().IndexOf(CurrentPage);
        if (at < 0) return;
        int next = e.Delta > 0 ? at - 1 : at + 1;
        if (next < 0 || next >= shown.Count) return;
        SelectPage(shown[next]);
        e.Handled = true;
    }
    // A precision touchpad sends parts of a notch; they add up until a whole notch (120) presses the volume key once.
    private int _wheelVolumeRest;
    private void WheelVolume(int delta)
    {
        if (Math.Sign(delta) != Math.Sign(_wheelVolumeRest)) _wheelVolumeRest = 0;
        _wheelVolumeRest += delta;
        int notches = _wheelVolumeRest / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) return;
        _wheelVolumeRest -= notches * Mouse.MouseWheelDeltaForOneLine;
        Native.VolumeKeys.Press(notches > 0, Math.Abs(notches));
    }

    private void MusicPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Music);
    private void MonitorPage_Click(object sender, RoutedEventArgs e) => SelectPage(DeckPage.Monitor);
    // The extension pages' tabs are made in code, one per occupied slot, after the fixed ones in switch order. Called
    // again after a reload scan: a slot that already has its button keeps it, so only the new tabs are added.
    internal void BuildExtensionButtons()
    {
        for (int slot = 0; slot < IslandExtensions.Slots.Length; slot++)
        {
            if (!IslandExtensions.Occupied(slot) || _extensionButtons[slot] is not null) continue;
            var extension = IslandExtensions.Slots[slot]!;
            var page = DeckPages.ExtensionPage(slot);
            var path = new System.Windows.Shapes.Path
            {
                Data = IslandExtensions.SafeGlyph(extension.Glyph),
                StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            };
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            var button = new Button
            {
                Style = (Style)FindResource("PanelIconButton"), Width = 28, Height = 24, Margin = new Thickness(1, 0, 0, 0),
                Content = new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 24, Height = 24, Children = { path } } },
                ToolTip = IslandExtensions.Title(slot)
            };
            AutomationProperties.SetName(button, IslandExtensions.Title(slot));
            button.Click += (_, _) => SelectPage(page);
            _extensionButtons[slot] = button;
            PageButtons.Children.Add(button);
        }
    }
    private readonly Button?[] _extensionButtons = new Button?[4];
    // Called when the interface language changes: a tab's tooltip and accessible name are text, so the tabs are made again.
    internal void RefreshExtensionButtons()
    {
        foreach (var button in _extensionButtons.OfType<Button>()) PageButtons.Children.Remove(button);
        Array.Clear(_extensionButtons);
        BuildExtensionButtons();
        RenderPageSwitch();
    }
    // Where the usage page's application tabs start: after the page switch, which holds one 28 DIP button per shown page.
    private double ApplicationPagesLeft(double side) { int pages = DeckPages.Visible(Settings).Count; return side + (pages <= 1 ? 36 : 72 + 29 * (pages - 2)); }
    // The header shared by both pages (see PanelHeader). Its row holds the page's top margin, so the clock, the switch and the
    // right group are centred on one line, and the switch and the gear line up with the page's content edges. Toolbar's own
    // height replaces its binding to the row: the music page takes the usage page's controls (PanelHeader.WithControlsOf), so
    // its band can be shorter than its row, with the rest below the controls; the row may also be one DIP taller, and the
    // controls are centred in the even part. Toolbar lies outside ContentCanvas's rows, so a band taller than the row is not
    // clipped. Only layout values change, and setting an unchanged value does nothing.
    private PanelHeader _header = PanelHeader.For(36, 20);
    private void ApplyHeader(PanelHeader header)
    {
        _header = header;
        ToolbarRow.Height = new GridLength(header.Row);
        Toolbar.Height = header.Height;
        PageSwitch.Margin = new Thickness(header.Side, 0, 0, 0);
        foreach (var button in new[] { AIUsageButton, MonitorPageButton, MusicPageButton }.Concat(_extensionButtons.OfType<Button>()))
        {
            button.Height = header.ButtonHeight;
            // A local corner for these two buttons only; PanelIconButton keeps its 5 DIP corner elsewhere.
            button.ApplyTemplate();
            if (button.Template?.FindName("Chrome", button) is Border chrome) chrome.CornerRadius = new CornerRadius(PanelHeader.ButtonRadius);
        }
        PageCapsule.CornerRadius = new CornerRadius(PanelHeader.ButtonRadius);
        SettingsButton.Height = header.SettingsHeight;
        if (SettingsButton.Parent is FrameworkElement right) right.Margin = new Thickness(0, 0, header.RightMargin, 0);
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(header.Side), 0, 0, 0);
    }
    private void RenderPageSwitch()
    {
        SetValue(SuppressMusicToolTipsProperty, IsMusicPage);
        foreach (var control in new FrameworkElement[] { AIUsageButton, MonitorPageButton, MusicPageButton, MediaSourceButton, ClockLabel }.Concat(_extensionButtons.OfType<Button>()))
            ToolTipService.SetIsEnabled(control, !IsMusicPage);
        PageSwitch.Visibility = _details && IsUsagePage ? Visibility.Collapsed : Visibility.Visible;
        var tabs = new List<(Button Button, DeckPage Page)> { (AIUsageButton, DeckPage.Usage), (MusicPageButton, DeckPage.Music), (MonitorPageButton, DeckPage.Monitor) };
        for (int slot = 0; slot < _extensionButtons.Length; slot++)
            if (_extensionButtons[slot] is { } extensionButton) tabs.Add((extensionButton, DeckPages.ExtensionPage(slot)));
        foreach (var (button, page) in tabs)
        {
            bool current = CurrentPage == page;
            button.Visibility = DeckPages.Shown(Settings, page) ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = UI.Brush(current ? UI.Primary : UI.Secondary);
        }
        PlacePageCapsule();
        ApplicationPages.Margin = new Thickness(ApplicationPagesLeft(_header.Side), 0, 0, 0);
        // With the entry switched on the source menu has one more row. Without a source that row stands alone in the button.
        bool entry = FullControlEntryShown, alone = entry && _mediaTrack is null;
        MediaSourceButton.Visibility = IsMusicPage && (_mediaTrack is not null || entry) ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceButton.IsEnabled = !alone || _media?.RestartingNetease != true;
        // An open menu keeps its rows; only the entry's availability follows the restart.
        if (MediaSourceButton.ContextMenu is { IsOpen: true } open)
            foreach (var item in open.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "action"))) item.IsEnabled = _media?.RestartingNetease != true;
        AutomationProperties.SetName(MediaSourceButton, alone ? FullControlButtonLabel : Loc.T("切换播放来源", "Switch playback source"));
        MediaSourceLabel.Text = _mediaTrack is { } playing ? Loc.Label(playing.Source) : alone ? FullControlButtonLabel : "";
        MediaSourcePin.Visibility = _pinnedMedia is not null && _pinnedMedia == _mediaTrack?.Id ? Visibility.Visible : Visibility.Collapsed;
        MediaSourceChevron.Visibility = _mediaTrack is not null && (_mediaTracks.Count > 1 || entry) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MediaChanged()
    {
        if (_media is null) return;
        _pinnedMedia = _media.Pinned;
        ApplyMediaUpdate(_media.Current, _media.Tracks, _media.Cover);
    }
    private void ApplyMediaUpdate(MediaTrack? track, IReadOnlyList<MediaTrack> tracks, BitmapSource? cover, bool notice = true)
    {
        bool changedSong = _mediaTrack?.Id != track?.Id || _mediaTrack is not null && track is not null && !SameMediaSong(_mediaTrack, track);
        bool lyricsChanged = changedSong || _mediaTrack?.SongKey != track?.SongKey || _mediaTrack?.SongId != track?.SongId || _mediaTrack?.EmbeddedLyrics != track?.EmbeddedLyrics;
        var old = _mediaTrack;
        _mediaTrack = track; _mediaTracks = tracks;
        var now = DateTimeOffset.UtcNow;
        _seekPreview.Observe(track, now);
        _timelineHold.Observe(track, now); ScheduleTimelineHoldEnd(now);
        bool coverChanged = _mediaCover != cover;
        if (coverChanged) { _mediaCover = cover; _musicAccent = MusicVisuals.Accent(_mediaCover, out _coverAccent); }
        if (changedSong)
        {
            _seeking = false; _seekTrack = null;
            // A notice still on screen switches to the new title instead of giving way to the summary until playback starts.
            if (_mediaTrack is not null && Settings.MusicTrackNotice && notice) _trackNotice.Arm(now); else _trackNotice.Disarm();
        }
        if (_trackNotice.Observe(_mediaTrack, now)) _trackNoticeUntil = now.AddSeconds(4);
        if (lyricsChanged) UpdateLyrics();
        if (changedSong || lyricsChanged || coverChanged || old?.State != _mediaTrack?.State || old?.HasTimeline != _mediaTrack?.HasTimeline) RenderCompact();
        if (IsMusicPage) RenderMusic();
        // Native lyric events must update compact text even while playback is paused.
        UpdateMusicPosition();
        UpdateMusicTimer();
    }
    private void ScheduleTimelineHoldEnd(DateTimeOffset now)
    {
        _timelineHoldEnd?.Stop();
        if (_timelineHold.Until is not { } until || until <= now) return;
        if (_timelineHoldEnd is null)
        {
            var timer = _timelineHoldEnd = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            timer.Tick += (_, _) => { timer.Stop(); if (IsMusicPage) RenderMusic(); };
        }
        // A little after the end, so the page laid out then no longer holds.
        _timelineHoldEnd.Interval = until - now + TimeSpan.FromMilliseconds(50); _timelineHoldEnd.Start();
    }
    // The expanded page and the compact text choose lyrics independently; either one is enough to look them up.
    private bool CompactLyrics => Settings.ShowsMusic(Settings.Style) && Settings.MusicText == CompactMusicText.Lyrics;
    private bool NeedsLyrics => Settings.MusicPage && Settings.LyricsEnabled || CompactLyrics;
    private string LyricsKey(MediaTrack? track) => NeedsLyrics && track is not null
        ? string.Join("\n", track.Id, track.SongKey, track.SongId, track.EmbeddedLyrics) : "";
    private async void UpdateLyrics()
    {
        string key = LyricsKey(_mediaTrack);
        if (_lyricsKey == key) return;
        _lyricsKey = key; _lyricsRequest?.Cancel(); _lyricsRequest?.Dispose(); _lyricsRequest = null;
        _lyrics = Lyrics.Empty; _compactLyric = ""; _musicRenderKey = "";
        if (key.Length == 0) return;
        var requested = _mediaTrack!;
        if (!requested.IsQqMusic && !string.IsNullOrWhiteSpace(requested.EmbeddedLyrics))
        {
            _lyrics = Lyrics.Parse(requested.EmbeddedLyrics, null, requested.Duration.TotalSeconds, requested.Source);
            if (IsMusicPage) RenderMusic(); RenderCompact(); return;
        }
        var request = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token); _lyricsRequest = request;
        try
        {
            var lyrics = await _app.Lyrics.FindAsync(requested, true, request.Token);
            if (request.IsCancellationRequested || _lyricsKey != key) return;
            _lyrics = lyrics; _musicRenderKey = "";
            if (IsMusicPage) RenderMusic(); RenderCompact();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Lyrics are optional; provider failures keep the no-lyrics layout. */ }
    }
    private void ToggleSourcePin(ContextMenu menu, MenuItem item, string id)
    {
        bool pin = !Equals(item.Tag, "pinned");
        _pinnedMedia = pin ? id : null;
        foreach (var other in menu.Items.OfType<MenuItem>().Where(other => !Equals(other.Tag, "action")))
        {
            other.Tag = pin && other == item ? "pinned" : null;
            if (pin) other.IsChecked = other == item;
        }
        _media?.Pin(_pinnedMedia);
        RenderPageSwitch();
    }
    // The heart of a liked song, in the theme's color for it (NetEase Cloud Music's red in the dark theme).
    private static string LikedColor => PanelTheme.Hex("liked");
    internal static string FullControlEntryLabel => Loc.T("网易云音乐 · 完整控制", "NetEase Cloud Music · Full control");
    // Alone in the toolbar the entry has at most 150 DIP; the full English name would lose "Full control" to the ellipsis.
    internal static string FullControlButtonLabel => Loc.T("网易云音乐 · 完整控制", "NetEase full control");
    private bool FullControlEntryShown => Settings.NeteaseFullControlEntry && _media?.NeteaseInstalled == true;
    // Started from the source menu or the lone button. Only the disabled entry shows that it is under way;
    // neither progress nor a failure is reported, and a later click is ignored until it ends.
    private async Task StartNeteaseFullControl()
    {
        if (_media is not { RestartingNetease: false } media) return;
        var action = !media.NeteaseRunning ? NeteaseControlAction.Start : media.NeteaseFullControl ? NeteaseControlAction.Reconnect : NeteaseControlAction.Restart;
        var dialog = new NeteaseControlWindow(Settings.Theme, action) { Owner = this };
        if (dialog.ShowDialog() != true || _media != media || media.RestartingNetease) return;
        var pending = media.EnableFullControlAsync(quiet: true);
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
        await pending;
        if (_media != media) return;
        _musicRenderKey = ""; RenderPageSwitch(); if (IsMusicPage) RenderMusic();
    }
    private void MediaSource_Click(object sender, RoutedEventArgs e)
    {
        if (MediaSourceButton.ContextMenu?.IsOpen == true) { CloseSourceMenu(); return; }
        if (_mediaTrack is null) { if (FullControlEntryShown) _ = StartNeteaseFullControl(); return; }
        var menu = new ContextMenu { Style = (Style)FindResource("MediaSourceMenu") };
        menu.SetValue(SuppressMusicToolTipsProperty, true);
        foreach (var track in _mediaTracks)
        {
            var state = track.State switch { MediaState.Playing => Loc.T("播放中", "Playing"), MediaState.Paused => Loc.T("已暂停", "Paused"),
                MediaState.Stopped => Loc.T("已停止", "Stopped"), _ => Loc.T("状态未知", "Unknown state") };
            var label = $"{Loc.Label(track.Source)} · {track.Title} · {state}";
            // The menu's viewport clip would turn the row grayscale; the menu's background beneath it is opaque.
            var row = new Grid(); RenderOptions.SetClearTypeHint(row, ClearTypeHint.Enabled);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var source = new TextBlock { Text = Loc.Label(track.Source), FontSize = 12.5, Foreground = UI.Brush(UI.Primary), MaxWidth = 100,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
            var title = new MusicMarquee { Text = track.Title, FontSize = 12, Foreground = UI.Brush(PanelTheme.Hex("menuDetail")),
                CenterWhenFits = false, IsHitTestVisible = false, Height = 28, Margin = new Thickness(0, 0, 12, 0) };
            var status = new TextBlock { Text = state, FontSize = 11, Foreground = UI.Brush(UI.Tertiary), TextWrapping = TextWrapping.NoWrap };
            foreach (var text in new FrameworkElement[] { source, title, status }) text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1); Grid.SetColumn(status, 2); row.Children.Add(source); row.Children.Add(title); row.Children.Add(status);
            var item = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = row, IsCheckable = true, IsChecked = track.Id == _mediaTrack?.Id };
            AutomationProperties.SetName(item, label);
            item.Tag = track.Id == _pinnedMedia ? "pinned" : _mediaTracks.Count < 2 ? "single" : null;
            // The pin button marks its click handled, so MenuItem neither selects the row nor closes the menu.
            item.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) => { e.Handled = true; ToggleSourcePin(menu, item, track.Id); }));
            item.Click += (_, _) => _media?.Select(track.Id); menu.Items.Add(item);
        }
        if (FullControlEntryShown)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MediaSourceMenuSeparator") });
            var label = new TextBlock { Text = FullControlEntryLabel, FontSize = 12.5, Foreground = UI.Brush(UI.Primary), TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var entry = new MenuItem { Style = (Style)FindResource("MediaSourceMenuItem"), Header = label, Tag = "action", IsEnabled = _media?.RestartingNetease != true };
            AutomationProperties.SetName(entry, FullControlEntryLabel);
            entry.Click += (_, _) => { CloseSourceMenu(); _ = StartNeteaseFullControl(); }; menu.Items.Add(entry);
        }
        WatchSourceMenu(menu);
        MediaSourceButton.ContextMenu = menu; menu.PlacementTarget = MediaSourceButton; menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 6; ResizeSourceMenu(menu); menu.IsOpen = true;
    }
    // Guidance shown in place of the transport when Netease runs without SMTC. Both sentences stay at every size:
    // what is required and where to switch it on. Only the type size and the entry's label adapt to the row.
    internal static string SmtcHintWhy => Loc.T("开启网易云的 SMTC 后，才能在这里控制播放", "Turn on SMTC in NetEase to control playback here");
    // The English hint names the menu items as NetEase Cloud Music's Chinese interface shows them.
    internal static string SmtcHintHow => Loc.T("位置：网易云音乐 › 设置 › 系统 › 开启 SMTC", "Where: NetEase Cloud Music › 设置 › 系统 › 开启 SMTC");
    // The song and the form (PageHandover) the media page was last built with: a new song swaps in, a new form (the lyric lines
    // the room holds, the title merged with the artist, the message or the entry shown) morphs in, as when a drag crosses a size step.
    private string _musicSong = "", _musicForm = "";
    private bool _musicHadLyrics;
    private long _songArrived;
    private BitmapSource? _musicCoverShown;
    // The titles of a new song rise into place. A rebuild during the change continues it from where it is.
    private void RiseIn(FrameworkElement titles)
    {
        if (!Soft.Enabled) return;
        double done = Math.Clamp((Environment.TickCount64 - _songArrived) / (double)Soft.Move, 0, 1), start = Curves.Follow(done);
        int left = (int)Math.Max(1, Soft.Move * (1 - done));
        var shift = new TranslateTransform();
        titles.RenderTransform = shift;
        var rise = Soft.Fade(8 * (1 - start), 0, left, Soft.Follow); rise.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        var fade = Soft.Fade(start, 1, left, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
        titles.BeginAnimation(OpacityProperty, fade);
    }
    private void RenderMusic()
    {
        // Only a page that was on screen hands over; one that arrives by the page switch or with the panel opening comes in whole.
        bool shown = MusicContent.IsVisible && _expanded && !_preview;
        RefreshStatus();
        UsageContent.Visibility = Visibility.Collapsed; MonitorContent.Visibility = Visibility.Collapsed; ExtensionContent.Visibility = Visibility.Visible; MusicContent.Visibility = Visibility.Visible;
        ShowExtensionView(-1);
        ModelToolbar.Visibility = Visibility.Collapsed; ApplicationPages.Visibility = Visibility.Collapsed; ClockLabel.Visibility = Visibility.Visible;
        double h = ContentCanvas.Height, w = ContentCanvas.Width;
        var track = _mediaTrack;
        if (_seeking && _seekTrack is { } seeking && track is not null && SameMediaSong(seeking, track))
        { UpdateMusicPosition(); return; }
        bool playerLyrics = track?.IsQqMusic == true && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental;
        bool hasPlayerLyric = playerLyrics && Settings.LyricsEnabled && !string.IsNullOrWhiteSpace(track?.CurrentLyric) && track?.State != MediaState.Stopped;
        bool hasLyrics = playerLyrics ? hasPlayerLyric : (_lyrics.Lines.Count > 0 || _lyrics.Instrumental) && Settings.LyricsEnabled;
        bool logMode = track?.IsNeteaseLog == true;
        bool showSmtcHint = logMode && track is { CanToggle: false, CanPrevious: false, CanNext: false }
            && _media?.RestartingNetease != true && _media?.Error is null;
        string message = _media?.RestartingNetease == true ? Loc.T("正在重启网易云…", "Restarting NetEase Cloud Music…") : _media?.Error ?? "";
        var modes = track is null ? [] : MusicPlaybackModes.Supported(track);
        // While the timeline is held (MusicTimelineHold) the bar stays in its place, empty and without times.
        bool timeline = track?.HasTimeline == true || track is not null && _timelineHold.Holds(DateTimeOffset.UtcNow);
        var layout = MusicLayout.Calculate(w, h, hasLyrics, timeline, message.Length > 0, logMode, transport: !showSmtcHint,
            lyricsExpected: Settings.LyricsEnabled);
        CurrentMusicLayout = layout;
        var c = layout.Metrics;
        // With the usage page on, the controls are the usage page's, as it would lay them out now; see PanelHeader.WithControlsOf.
        ApplyHeader(Settings.UsagePage ? PanelHeader.Music(c).WithControlsOf(UsageHeader(_usageWidth, UsagePageHeight)) : PanelHeader.Music(c));
        bool animate = Settings.Animations && SystemParameters.ClientAreaAnimation;
        string key = $"{track?.Id}|{track?.SongKey}|{logMode}|{_media?.RestartingNetease}|{track?.State}|{track?.CanToggle}|{track?.CanPrevious}|{track?.CanNext}|{track?.CanSeek}|{track?.CanRepeat}|{track?.CanShuffle}|{track?.Shuffle}|{track?.Repeat}|{track?.CanLike}|{track?.Liked}|{track?.HasTimeline}|{timeline}|{playerLyrics}|{hasPlayerLyric}|{w}|{h}|{_lyrics.GetHashCode()}|{_mediaCover?.GetHashCode()}|{_media?.Error}|{Settings.LyricsEnabled}|{animate}";
        if (_musicRenderKey == key) { UpdateMusicPosition(); return; }
        _musicRenderKey = key;
        string song = track is null ? "" : $"{track.Id}|{track.SongKey}|{track.Title}|{track.Artist}";
        // Lyrics that arrive after the song, or come and go with the player's line, only fade in their own lines (lyricsArrive):
        // the form counts the lyric lines the room holds, not whether lyrics are there.
        string form = $"{track is null}|{layout.LyricRoom}|{layout.MergeTitle}|{layout.ShowFullControl}|{layout.ShowMessage}|{Settings.LyricsEnabled}|{showSmtcHint}|{timeline}";
        bool lyricsArrive = shown && hasLyrics && !_musicHadLyrics && Soft.Enabled;
        _musicHadLyrics = hasLyrics;
        // A new song changes the titles and perhaps the cover; the rest of the page is the same and stays still. The titles
        // rise into place (RiseIn) and a new cover fades in. A player announces a song in a few updates (title, artist, and a
        // timeline that may go and come back); the first starts the change and the rest carry it on instead of starting it
        // again, and a form that changes during it is taken at once rather than morphed twice.
        bool newCover = shown && !ReferenceEquals(_mediaCover, _musicCoverShown);
        _musicCoverShown = _mediaCover;
        if (shown && song != _musicSong && Environment.TickCount64 - _songArrived >= Soft.Move) _songArrived = Environment.TickCount64;
        long sinceSong = Environment.TickCount64 - _songArrived;
        bool songChanging = sinceSong < Soft.Move;
        // A slow player may bring the timeline back a little after the move: the form is not morphed for twice its length.
        var motion = shown && form != _musicForm && sinceSong >= 2 * Soft.Move ? Handover.Morph : Handover.None;
        _musicSong = song; _musicForm = form;
        // Transport buttons that stay glide to their new places. While the panel is dragged they follow it frame by frame like
        // the rest of the page, and glide only when its form changes.
        bool glide = shown && animate && (!Resizing || motion != Handover.None);
        // Keep the transport layer attached: replacing a hovered button restarts
        // hit testing and its fade-in on every metadata, artwork or lyric update.
        _handover.Hold(MusicContent, keep: _musicControls);
        _musicSeek = null; _musicElapsed = null; _musicDuration = null; _musicEqualizer = null; _lyricCurrent = null; _lyricPrevious = null; _lyricNext = null; _musicMessage = null; _musicInfo = null;
        MusicContent.Margin = new Thickness(c.Side, 0, c.Side, c.Bottom);
        if (track is null)
        {
            MusicContent.Children.Clear(); _musicControls = null; _musicFullControl = null; _musicSmtcHint = null; _musicControlButtons.Clear();
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = Math.Max(200, w - 60) };
            var icon = MusicVisuals.Icon("music", 26); icon.Margin = new Thickness(0, 0, 0, 8); empty.Children.Add(icon);
            empty.Children.Add(UI.Fixed(Loc.T("没有正在播放的媒体", "Nothing is playing"), 14, 22, UI.Secondary));
            if (_media?.Error is { } error)
            {
                var hint = UI.Line(error, 11.5, UI.Tertiary); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
            }
            MusicContent.Children.Add(empty); _handover.Release(empty, motion); return;
        }
        var body = new Grid();
        var upper = new Grid { Height = layout.Cover, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.CoverY, 0, 0) };
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.Cover) });
        upper.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c.Gap) });
        upper.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(upper);
        var art = MusicVisuals.Cover(_mediaCover, layout.Cover, layout.CoverRadius, outlined: true);
        art.RenderTransformOrigin = new Point(.5, .5); art.RenderTransform = _musicCoverScale;
        MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleXProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
        MusicControlButton.Animate(_musicCoverScale, ScaleTransform.ScaleYProperty, track.State == MediaState.Paused ? .94 : 1, animate, Soft.Move);
        AutomationProperties.SetName(art, Loc.T("专辑封面", "Album cover")); upper.Children.Add(art);
        AttachCoverFocus(art);
        if (newCover && animate) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; art.BeginAnimation(OpacityProperty, fade); }
        var right = new Grid(); Grid.SetColumn(right, 2); upper.Children.Add(right);
        bool synchronized = playerLyrics || _lyrics.CanSynchronize(track);
        int lyricLines = playerLyrics ? Math.Min(1, layout.LyricLines) : layout.LyricLines;
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(layout.HeaderHeight) });
        right.RowDefinitions.Add(new RowDefinition());
        double equalizerWidth = Math.Max(27, Math.Round(c.Equalizer * 1.7));
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(equalizerWidth + 16) });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var title = UI.Fixed(track.Title, c.Title, c.TitleLine, UI.Primary, FontWeights.SemiBold);
        AutomationProperties.SetName(title, Loc.T("歌名 ", "Title ") + track.Title); titles.Children.Add(title);
        if (layout.MergeTitle)
        {
            if (track.Artist.Length > 0) title.Inlines.Add(new Run(" · " + track.Artist) { Foreground = UI.Brush(UI.Secondary), FontSize = c.Artist, FontWeight = FontWeights.Normal });
            ShowCutText(title, track.Artist.Length > 0 ? track.Title + " · " + track.Artist : track.Title);
        }
        else
        {
            string artist = string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => s.Length > 0));
            var line = UI.Fixed(artist, c.Artist, c.ArtistLine, UI.Secondary); titles.Children.Add(line);
            ShowCutText(title, track.Title); ShowCutText(line, artist);
        }
        header.Children.Add(titles);
        if (shown && songChanging) RiseIn(titles);
        _musicEqualizer = new MusicEqualizer { Width = equalizerWidth, Height = c.Equalizer, Color = _musicAccent, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, (c.TitleLine - c.Equalizer) / 2, 0, 0) };
        Grid.SetColumn(_musicEqualizer, 1); header.Children.Add(_musicEqualizer); right.Children.Add(header);

        var middle = new Grid(); Grid.SetRow(middle, 1); right.Children.Add(middle); _musicInfo = middle;
        if (hasLyrics && lyricLines > 0 && synchronized)
        {
            var lyrics = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // All lines share one size; only the brightness marks the current one.
            if (lyricLines == 3) { _lyricPrevious = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricPrevious); }
            _lyricCurrent = new MusicMarquee { FontSize = c.Lyric, Height = c.LyricLine, CenterWhenFits = false, Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay };
            lyrics.Children.Add(_lyricCurrent); UpdateLyricScrolling();
            if (lyricLines >= 2) { _lyricNext = UI.Fixed("", c.Lyric, c.LyricLine, UI.Tertiary); lyrics.Children.Add(_lyricNext); }
            lyrics.Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible; middle.Children.Add(lyrics);
        }
        else if (hasLyrics && lyricLines > 0)
        {
            var text = UI.Line(_lyrics.Instrumental && _lyrics.Lines.Count == 0 ? "♪" : string.Join("\n", _lyrics.Lines.Select(line => line.Text)), c.Lyric, UI.Secondary);
            text.TextWrapping = TextWrapping.Wrap; text.LineHeight = c.LyricLine; text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var lyrics = new ScrollViewer { Content = text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Center, Height = lyricLines * c.LyricLine, CanContentScroll = false, Visibility = layout.ShowMessage ? Visibility.Collapsed : Visibility.Visible };
            // The viewport clip would turn the lyrics grayscale; they lie over the island's opaque surface.
            RenderOptions.SetClearTypeHint(text, ClearTypeHint.Enabled);
            AutomationProperties.SetName(lyrics, Loc.T("歌词", "Lyrics")); middle.Children.Add(lyrics);
        }
        if (lyricsArrive) { var fade = Soft.Fade(0, 1, Soft.Move, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop; middle.BeginAnimation(OpacityProperty, fade); }
        if (layout.MiddleHeight >= c.MiddleGap + c.LyricLine)
        {
            // One line; a longer message (a failed full control connection) is read whole in its tooltip, like a cut title.
            _musicMessage = UI.Fixed(message, c.Lyric, c.LyricLine, UI.Tertiary); ShowCutText(_musicMessage, message);
            _musicMessage.Visibility = layout.ShowMessage ? Visibility.Visible : Visibility.Collapsed; middle.Children.Add(_musicMessage);
        }
        if (timeline)
        {
            var times = new Grid { Height = c.TimeLine, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.TimesY, 0, 0), IsHitTestVisible = false };
            _musicElapsed = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); times.Children.Add(_musicElapsed);
            _musicDuration = UI.Fixed("", c.Time, c.TimeLine, UI.Secondary); _musicDuration.HorizontalAlignment = HorizontalAlignment.Right; times.Children.Add(_musicDuration);
            foreach (var time in new[] { _musicElapsed, _musicDuration }) Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            body.Children.Add(times);
            _musicSeek = new MusicSeekSlider { Style = (Style)FindResource("MusicSeek"), RestHeight = c.Bar, HoverHeight = c.BarHover, BarHeight = c.Bar,
                Accent = ((SolidColorBrush)_musicAccent).Color, DurationSeconds = track.Duration.TotalSeconds, Height = c.SeekRow, AnimationsEnabled = animate, IsEnabled = track.CanSeek && track.HasTimeline,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, layout.ProgressY, 0, 0) };
            AutomationProperties.SetName(_musicSeek, Loc.T("播放进度", "Playback position"));
            // Slider handles move-to-point before instance handlers; observe that handled event too.
            _musicSeek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((sender, _) =>
            {
                var seek = (Slider)sender; BeginSeek(seek);
                var sliderTrack = seek.Template.FindName("PART_Track", seek) as System.Windows.Controls.Primitives.Track;
                // Track clicks do not capture in WPF. Keep the release even if it is outside the bar.
                if (_seeking && Mouse.LeftButton == MouseButtonState.Pressed && sliderTrack?.Thumb.IsMouseOver != true) seek.CaptureMouse();
            }), true);
            _musicSeek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((sender, _) => BeginSeek((Slider)sender)), true);
            _musicSeek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(async (_, e) =>
            {
                if (e.Canceled) { _seeking = false; _seekTrack = null; if (_musicSeek is MusicSeekSlider slider) slider.Seeking = false; UpdateMusicPosition(); }
                else await CommitSeek();
            }), true);
            _musicSeek.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => await CommitSeek()), true);
            _musicSeek.LostMouseCapture += async (_, _) => { if (_seeking && _musicSeek?.IsMouseCaptureWithin == false) await CommitSeek(); };
            _musicSeek.ValueChanged += (_, _) => { if (!_updatingSeek && _seeking) UpdateSeekLabels(_musicSeek.Value); };
            _musicSeek.PreviewKeyUp += async (_, e) => { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp) { _seekTrack = _mediaTrack; _seeking = true; await CommitSeek(); } };
            body.Children.Add(_musicSeek);
        }
        if (_musicControls is null)
        {
            _musicControls = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            MusicContent.Children.Add(_musicControls);
        }
        var controls = _musicControls;
        controls.Width = layout.BodyWidth; controls.Height = c.Play;
        controls.Margin = new Thickness(0, layout.ControlsY, 0, 0);
        var visibleControls = new HashSet<MediaCommand>();
        void Add(MediaCommand id, string name, string glyph, double size, double x, bool enabled, Func<Task> command, bool mode = false, bool single = false, string? color = null)
        {
            visibleControls.Add(id);
            if (!_musicControlButtons.TryGetValue(id, out var slot))
            {
                var created = new MusicControlButton { Style = (Style)FindResource("MusicControl") };
                created.Click += async (_, _) => { if (created.Tag is Func<Task> execute) await execute(); };
                slot = (created, ""); controls.Children.Add(created);
            }
            var button = slot.Button;
            string iconKey = $"{glyph}|{size}|{mode}|{single}|{color}|{PanelTheme.Generation}";
            if (slot.Icon != iconKey)
            {
                UIElement icon = MusicVisuals.Icon(glyph, size, UI.Brush(color ?? (mode ? UI.Secondary : UI.Primary)));
                if (single)
                {
                    var container = new Grid { Width = size, Height = size }; container.Children.Add(icon);
                    var singleLabel = UI.CenteredSymbol("1", c.Mode <= 14 ? 7 : c.Mode >= 22 ? 10 : 9, UI.Secondary);
                    singleLabel.HorizontalAlignment = HorizontalAlignment.Center;
                    container.Children.Add(singleLabel); icon = container;
                }
                // A changed glyph (play and pause, the playback mode, the heart) grows into place instead of switching.
                if (glide && button.IsVisible && slot.Icon.Length > 0 && icon is FrameworkElement grown)
                {
                    var scale = new ScaleTransform(.6, .6);
                    grown.RenderTransformOrigin = new Point(.5, .5); grown.RenderTransform = scale;
                    var grow = Soft.Fade(.6, 1, Soft.Choose, Soft.Follow); grow.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    var fade = Soft.Fade(0, 1, Soft.Choose, Soft.Follow); fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
                    scale.ScaleX = scale.ScaleY = 1;
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow); scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    grown.BeginAnimation(OpacityProperty, fade);
                }
                button.Content = icon;
            }
            _musicControlButtons[id] = (button, iconKey);
            button.Width = size + 16; button.Height = size + 16;
            button.IsEnabled = enabled; button.AnimationsEnabled = animate; button.Tag = command;
            bool moves = glide && button.IsVisible;
            PageHandover.Glide(button, Canvas.LeftProperty, x - 8, moves);
            PageHandover.Glide(button, Canvas.TopProperty, (c.Play - size) / 2 - 8, moves);
            AutomationProperties.SetName(button, name);
        }
        if (modes.Count > 0)
        {
            var mode = MusicPlaybackModes.Current(track);
            Add(MediaCommand.Repeat, Loc.T("播放模式 · ", "Playback mode · ") + MusicPlaybackModes.Label(mode), mode == MusicPlaybackMode.Shuffle ? "shuffle" : mode == MusicPlaybackMode.Order ? "sequential" : "repeat",
                c.Mode, layout.ModeX, true, () => CyclePlaybackMode(track), true, mode == MusicPlaybackMode.Track);
        }
        // The full-control entry never moves the transport. It shortens its label step by step to the room it is given.
        double EntryWidth(string label)
        {
            var probe = new TextBlock { Text = label, FontFamily = UI.TextFont, FontSize = c.Time };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(probe.DesiredSize.Width) + MusicLayout.FullControlChrome;
        }
        string? entryLabel = null; double entryWidth = 0;
        if (showSmtcHint)
        {
            // Without SMTC the transport cannot do anything, so its row carries the explanation instead of three dead glyphs.
            // The wording is fixed; the row is fitted by trying the full entry label first, then smaller type, then the short label.
            if (_musicSmtcHint is null)
            {
                _musicSmtcHint = new TextBlock { FontFamily = UI.TextFont, TextWrapping = TextWrapping.Wrap, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
                AutomationProperties.SetName(_musicSmtcHint, Loc.T("SMTC 提示", "SMTC hint")); controls.Children.Add(_musicSmtcHint);
            }
            var hint = _musicSmtcHint; hint.Visibility = Visibility.Visible; hint.Inlines.Clear();
            hint.Inlines.Add(new Run(SmtcHintWhy) { Foreground = UI.Brush(UI.Tertiary) });
            hint.Inlines.Add(new LineBreak());
            hint.Inlines.Add(new Run(SmtcHintHow) { Foreground = UI.Brush(UI.Secondary) });
            var sizes = Enumerable.Range(0, 4).Select(step => c.Time - step * .5).Where(size => size >= 9.5);
            bool fitted = false;
            foreach (var label in layout.ShowFullControl ? MusicLayout.FullControlLabels : [""])
            {
                double reserve = label.Length > 0 ? EntryWidth(label) : 0;
                foreach (double size in sizes)
                {
                    double width = layout.BodyWidth - (reserve > 0 ? reserve + MusicLayout.FullControlGap : 0);
                    hint.FontSize = size; hint.LineHeight = Math.Max(12, Math.Round(size * 1.25)); hint.Width = width;
                    hint.Measure(new Size(width, double.PositiveInfinity));
                    if (hint.DesiredSize.Height <= c.Play + 2) { fitted = true; break; }
                }
                if (label.Length > 0) { entryLabel = label; entryWidth = reserve; }
                if (fitted) break;
            }
            Canvas.SetLeft(hint, 0); Canvas.SetTop(hint, Math.Floor((c.Play - hint.DesiredSize.Height) / 2));
        }
        else
        {
            if (_musicSmtcHint is not null) _musicSmtcHint.Visibility = Visibility.Collapsed;
            double gx = layout.TripletX;
            Add(MediaCommand.Previous, Loc.T("上一首", "Previous"), "previous", c.Skip, gx, track.CanPrevious, () => RunMediaCommand(track, MediaCommand.Previous)); gx += c.Skip + layout.ControlGap;
            Add(MediaCommand.PlayPause, track.State == MediaState.Playing ? Loc.T("暂停", "Pause") : Loc.T("播放", "Play"), track.State == MediaState.Playing ? "pause" : "play", c.Play, gx, track.CanToggle, () => RunMediaCommand(track, MediaCommand.PlayPause)); gx += c.Play + layout.ControlGap;
            Add(MediaCommand.Next, Loc.T("下一首", "Next"), "next", c.Skip, gx, track.CanNext, () => RunMediaCommand(track, MediaCommand.Next));
            // The heart mirrors the mode button: right of next, as far from it as the mode button is from previous. Only
            // full control offers it, and the full-control entry, which shares this side, appears only without full control.
            if (track.CanLike)
                Add(MediaCommand.Like, track.Liked ? Loc.T("取消喜欢", "Unlike") : Loc.T("喜欢", "Like"), track.Liked ? "liked" : "like", c.Mode,
                    layout.TripletX + layout.TripletWidth + (layout.TripletX - layout.ModeX - c.Mode), true, () => RunMediaCommand(track, MediaCommand.Like), mode: true, color: track.Liked ? LikedColor : null);
            if (layout.ShowFullControl)
                foreach (var label in MusicLayout.FullControlLabels)
                    if ((entryWidth = EntryWidth(label)) <= layout.FullControlRoom) { entryLabel = label; break; }
        }
        foreach (var (id, slot) in _musicControlButtons)
            slot.Button.Visibility = visibleControls.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        if (entryLabel is not null)
        {
            if (_musicFullControl is null)
            {
                _musicFullControl = new Button { Style = (Style)FindResource("PanelTextButton"), FontFamily = UI.TextFont, Padding = new Thickness(6, 2, 6, 2),
                    Height = 26, HorizontalContentAlignment = HorizontalAlignment.Right };
                AutomationProperties.SetName(_musicFullControl, MusicLayout.FullControlLabels[0]);
                _musicFullControl.Click += async (_, _) => await EnableNeteaseControl(); controls.Children.Add(_musicFullControl);
            }
            var enable = _musicFullControl; enable.IsEnabled = _media?.RestartingNetease != true;
            // The app-wide TextBlock style wraps; pixel snapping must never push the last character to a second line.
            enable.Width = entryWidth;
            enable.Content = new TextBlock { Text = entryLabel, FontFamily = UI.TextFont, FontSize = c.Time, TextWrapping = TextWrapping.NoWrap };
            Canvas.SetLeft(enable, layout.BodyWidth - entryWidth); Canvas.SetTop(enable, (c.Play - 26) / 2);
        }
        if (_musicFullControl is not null) _musicFullControl.Visibility = entryLabel is not null ? Visibility.Visible : Visibility.Collapsed;
        MusicContent.Children.Insert(0, body); _handover.Release(body, motion); UpdateMusicPosition();
    }
    // The page suppresses tooltips. A line cut off by its ellipsis (a title, an artist, a long message) still shows its whole
    // text on hover. A later text goes in ToolTip alone: whether it is cut is checked when the tooltip would open.
    private static void ShowCutText(TextBlock block, string text)
    {
        block.SetValue(SuppressMusicToolTipsProperty, false);
        block.ToolTip = text.Length > 0 ? text : null;
        block.ToolTipOpening += (_, e) => { if (!UI.IsCut(block)) e.Handled = true; };
    }
    private async Task CyclePlaybackMode(MediaTrack track)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id || _media is null) return;
        _commandBusy = true;
        try
        {
            var next = MusicPlaybackModes.Next(_mediaTrack);
            if (!await _media.SetPlaybackModeAsync(track.Id, next)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player."));
            await _media.RefreshAsync();
        }
        finally { _commandBusy = false; }
    }
    private async Task EnableNeteaseControl()
    {
        if (_media?.CanEnableFullControl != true || _media.RestartingNetease) return;
        var dialog = new NeteaseControlWindow(Settings.Theme) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var media = _media;
        var pending = media.EnableFullControlAsync();
        _musicRenderKey = ""; RenderMusic();
        bool succeeded = await pending;
        if (_media != media) return;
        await media.RefreshAsync(); _musicRenderKey = ""; RenderMusic();
        if (!succeeded) ShowMusicError(media.Error ?? Loc.T("未能启用完整控制，请重试。", "Full control could not be enabled. Try again."));
    }

    private async Task RunMediaCommand(MediaTrack track, MediaCommand command)
    {
        if (_commandBusy || _mediaTrack?.Id != track.Id) return;
        _commandBusy = true;
        try { if (_media is not null && !await _media.CommandAsync(track.Id, command)) ShowMusicError(Loc.T("播放器未接受此操作，请在原应用中重试。", "The player did not accept this action. Try again in the player.")); }
        finally { _commandBusy = false; }
    }
    private void BeginSeek(Slider seek)
    {
        if (seek != _musicSeek || _mediaTrack is not { CanSeek: true, HasTimeline: true }) return;
        if (!_seeking) { _seeking = true; _seekTrack = _mediaTrack; }
        if (seek is MusicSeekSlider musicSeek) musicSeek.Seeking = true;
        UpdateSeekLabels(seek.Value);
    }
    private async Task CommitSeek()
    {
        if (!_seeking || _musicSeek is null) return;
        var track = _seekTrack; double value = _musicSeek.Value; _seeking = false; _seekTrack = null;
        if (_musicSeek is MusicSeekSlider musicSeek) musicSeek.Seeking = false;
        if (_musicSeek.IsMouseCaptured) _musicSeek.ReleaseMouseCapture();
        if (track is null || track.Id != _mediaTrack?.Id || !SameMediaSong(track, _mediaTrack)) return;
        if (_media is not null)
        {
            var request = _seekPreview.Begin(track, value, DateTimeOffset.UtcNow);
            UpdateMusicPosition();
            if (!await _media.SeekAsync(track, value) && _seekPreview.Cancel(request))
            {
                UpdateMusicPosition();
                ShowMusicError(Loc.T("播放器未接受进度调整。", "The player did not accept the new position."));
            }
        }
        RenderMusic();
    }
    private static bool SameMediaSong(MediaTrack first, MediaTrack second) => MediaRouting.SameSong(first, second) &&
        (string.IsNullOrEmpty(first.SongId) || string.IsNullOrEmpty(second.SongId) || first.SongId == second.SongId);
    private void ShowMusicError(string error)
    {
        if (_musicMessage is null) return;
        _musicMessage.Text = error; _musicMessage.ToolTip = error; _musicMessage.Visibility = Visibility.Visible;
        if (_musicInfo is not null) foreach (UIElement child in _musicInfo.Children) child.Visibility = ReferenceEquals(child, _musicMessage) ? Visibility.Visible : Visibility.Collapsed;
    }
    private static string MusicTime(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    private void UpdateSeekLabels(double fraction)
    {
        if (_mediaTrack is not { HasTimeline: true } track || _musicElapsed is null || _musicDuration is null) return;
        var position = TimeSpan.FromSeconds(track.Duration.TotalSeconds * fraction);
        _musicElapsed.Text = MusicTime(position);
        _musicDuration.Text = MusicTime(track.Duration);
        AutomationProperties.SetName(_musicElapsed, Loc.T("已播放 ", "Elapsed ") + _musicElapsed.Text);
        AutomationProperties.SetName(_musicDuration, Loc.T("总时长 ", "Duration ") + _musicDuration.Text);
    }
    private void UpdateMusicPosition()
    {
        if (_mediaTrack is not { } track || track.State == MediaState.Stopped) { MusicIndicator.Visibility = Visibility.Collapsed; return; }
        var position = _seekPreview.Position(track, DateTimeOffset.UtcNow);
        // The expanded page's controls stay in the tree while collapsed; they are brought up to date when the panel opens.
        bool page = _expanded && IsMusicPage;
        if (page && !_seeking && _musicSeek is not null) { _updatingSeek = true; _musicSeek.Value = track.Duration > TimeSpan.Zero ? position.TotalSeconds / track.Duration.TotalSeconds : 0; _updatingSeek = false; UpdateSeekLabels(_musicSeek.Value); }
        var line = CurrentLyrics(track);
        if (page && _lyricCurrent is not null) _lyricCurrent.Text = line.Current;
        if (page && _lyricPrevious is not null) _lyricPrevious.Text = line.Previous;
        if (page && _lyricNext is not null) _lyricNext.Text = line.Next;
        if (CompactLyrics && _compactLyric != line.Current)
        {
            _compactLyric = line.Current;
            if (_trackNoticeUntil <= DateTimeOffset.UtcNow)
            {
                if (_compactMarquee is not null) _compactMarquee.Text = line.Current.Length > 0 ? line.Current : track.Caption;
                else RenderCompact();
            }
        }
        MusicIndicator.Visibility = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && track.State != MediaState.Stopped && track.HasTimeline ? Visibility.Visible : Visibility.Collapsed;
        // The progress runs along the indicator's own length, whatever length is set.
        double length = double.IsFinite(Collapsed.Surface.Width) ? Math.Max(0, Collapsed.Surface.Width) : 0;
        double progress = track.HasTimeline && track.Duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / track.Duration.TotalSeconds, 0, 1) : 0;
        MusicIndicator.Width = length * progress;
    }
    private (string Previous, string Current, string Next) CurrentLyrics(MediaTrack track)
    {
        if (!NeedsLyrics || track.State == MediaState.Stopped) return ("", "", "");
        if (track.IsQqMusic && _lyrics.Lines.Count == 0 && !_lyrics.Instrumental) return ("", track.CurrentLyric ?? "", "");
        return _lyrics.At(track, DateTimeOffset.UtcNow);
    }
    // The color taken from the playing song's cover; null without one, or when the cover gave no color.
    internal Color? CoverColor => _mediaCover is not null && _mediaTrack is { State: not MediaState.Stopped } ? _coverAccent : null;
    private void RenderCompactMusic()
    {
        // The collapsed shape gives the row's width in DIPs and the height scale; every size below follows from them.
        var shape = Collapsed; _compactRendered = shape;
        double scale = CompactMusicLayout.SafeScale(shape.Scale);
        double contentWidth = CompactContentWidth(shape);
        bool media = Settings.ShowsMusic(EffectiveStyle) && _mediaTrack is { State: not MediaState.Stopped };
        bool notice = media && _trackNoticeUntil > DateTimeOffset.UtcNow;
        string text = "";
        if (media)
        {
            if (notice || Settings.MusicText == CompactMusicText.Title) text = _mediaTrack!.Caption;
            else if (Settings.MusicText == CompactMusicText.Lyrics)
            {
                text = CurrentLyrics(_mediaTrack!).Current;
                _compactLyric = text;
                if (text.Length == 0) text = _mediaTrack!.Caption;
            }
        }
        var apps = Settings.ShowsSummary(EffectiveStyle) && !notice ? Settings.EnabledApps : [];
        // The clock keeps its own width at the right; shown alone, it is centred.
        var clock = Settings.ShowsClock(EffectiveStyle) ? new ClockFace(Settings.ClockStyle, Settings.Clock24Hour, ClockFace.Palette(Settings, CoverColor), DateTime.Now, scale) : null;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // The system readings ask for the width of everything chosen, on whole device pixels, and show what fits in the region they get.
        var strip = Settings.ShowsMonitor(EffectiveStyle) ? new MonitorStrip(Settings.CompactReadings, MonitorThemeColors.Strip(Settings), scale,
            !media && apps.Count == 0 && text.Length == 0 && clock is null, _stripPresence, dpi) : null;
        // Readings that do not fit scroll in their region, as the rings and the lyric do, so the region keeps its width.
        var layout = CompactMusicLayout.Calculate(contentWidth, scale, media, apps.Count, text.Length > 0, clock?.ReservedWidth ?? 0, strip?.PreferredWidth ?? 0); CurrentCompactLayout = layout;
        // An auto-width surface takes the width of what the row shows; the new shape builds the row again. The track notice
        // and the lyric's changing length are left out, so the width changes only when the content itself does.
        int steadyRings = Settings.ShowsSummary(EffectiveStyle) ? Settings.EnabledApps.Count : 0;
        bool steadyText = media && Settings.MusicText != CompactMusicText.None;
        double wanted = CompactMusicLayout.NaturalWidth(scale, media, steadyRings, steadyText, clock?.ReservedWidth ?? 0, strip?.PreferredWidth ?? 0);
        if (double.IsNaN(_compactWanted) || Math.Abs(wanted - _compactWanted) > .5) { _compactWanted = wanted; Dispatcher.BeginInvoke(DispatcherPriority.Background, FitCompactToContent); }
        double row = layout.RowHeight;
        var grid = new Grid { Width = SnapDown(contentWidth, dpi), Height = row };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SnapDown(layout.CoverWidth, dpi)) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SnapDown(layout.EqualizerWidth, dpi)) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ClockAlone ? 0 : SnapDown(layout.ClockWidth, dpi)) });
        // The readings in a region of the given width; wider than it, they scroll in a carousel whose left fade lies over the gap before it.
        FrameworkElement Readings(MonitorStrip readings, double span)
        {
            // A region squeezed to nothing (the readings' gap took all of it) shows none of them, which then need no sampling.
            _compactStrip = CompactMonitor.HasRoom(span) ? readings : null;
            if (readings.PreferredWidth > span + .001)
                return _stripCarousel = new ReadingsCarousel(readings, span, Snap(layout.TextEdge, dpi), _stripElapsed, Settings.Animations && SystemParameters.ClientAreaAnimation);
            readings.Width = span; readings.Margin = new Thickness(0); return readings;
        }
        // Alone, the readings span the row, centred.
        if (strip is not null && layout.MonitorWidth > 0 && layout.MonitorAlone)
        {
            var region = Readings(strip, SnapDown(layout.MonitorWidth, dpi));
            region.HorizontalAlignment = HorizontalAlignment.Center; region.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumnSpan(region, 4); grid.Children.Add(region);
        }
        if (clock is not null && layout.ClockWidth > 0)
        {
            var region = CompactRegion(clock, layout.ClockWidth, clock.ReservedWidth, clock.Height, row, layout.ClockAlone ? RegionAnchor.Center : RegionAnchor.Right, dpi);
            region.HorizontalAlignment = layout.ClockAlone ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            if (layout.ClockAlone) Grid.SetColumnSpan(region, 4); else Grid.SetColumn(region, 3);
            grid.Children.Add(region); _compactClock = clock;
        }
        if (media && layout.CoverWidth > 0)
        {
            var cover = MusicVisuals.Cover(_mediaCover, layout.CoverSize, layout.CoverRadius); AutomationProperties.SetName(cover, _mediaTrack!.Caption);
            grid.Children.Add(CompactRegion(cover, layout.CoverWidth, layout.CoverSize, layout.CoverSize, row, RegionAnchor.Left, dpi));
        }
        if (media && layout.EqualizerWidth > 0)
        {
            _compactEqualizer = new MusicEqualizer { Bars = 4, Width = layout.EqualizerSize, Height = layout.EqualizerHeight, BarWidth = layout.EqualizerBar, Color = Settings.MusicCoverColor ? _musicAccent : UI.Brush(PanelTheme.Hex("equalizer")) };
            var region = CompactRegion(_compactEqualizer, layout.EqualizerWidth, layout.EqualizerSize, layout.EqualizerHeight, row, RegionAnchor.Right, dpi);
            Grid.SetColumn(region, 2); grid.Children.Add(region);
        }
        var middle = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(middle, 1); grid.Children.Add(middle);
        double middleColumn = Math.Max(0, grid.Width - grid.ColumnDefinitions[0].Width.Value - grid.ColumnDefinitions[2].Width.Value - grid.ColumnDefinitions[3].Width.Value);
        // The rings, the lyric and the readings in the order chosen for this style. The readings' gap lies on the side of their
        // neighbour (after them only when they come first and something follows); the space between the rings and the lyric lies
        // before whichever of the two comes second. Positions are snapped cumulatively from the middle's left edge and kept inside
        // its column, so rounding never pushes one region into the next.
        bool hasRings = layout.RingWidth > 0 && apps.Count > 0, hasText = layout.TextWidth > 0, hasReadings = strip is not null && layout.MonitorWidth > 0 && !layout.MonitorAlone;
        var parts = Settings.CompactOrder(EffectiveStyle).Where(part => part switch { CompactPart.Rings => hasRings, CompactPart.Music => hasText, _ => hasReadings }).ToList();
        double readingsGap = CompactMusicLayout.Whole(8 * scale), cursor = 0, placed = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            double gap = (part == CompactPart.Monitor && (i > 0 || parts.Count == 1)) || (i == 1 && parts[0] == CompactPart.Monitor) ? readingsGap : 0;
            if (part != CompactPart.Monitor && parts.Take(i).Any(p => p != CompactPart.Monitor)) gap += layout.Between;
            double width = part switch { CompactPart.Rings => layout.RingWidth, CompactPart.Music => layout.TextWidth, _ => Math.Max(0, layout.MonitorWidth - readingsGap) };
            double start = Math.Min(middleColumn, SnapDown(cursor + gap, dpi)), end = Math.Min(middleColumn, SnapDown(cursor + gap + width, dpi));
            cursor += gap + width;
            FrameworkElement element = part switch
            {
                CompactPart.Rings => _quotaCarousel = new QuotaCarousel(QuotaRings(apps), layout, _quotaElapsed, Settings.Animations && SystemParameters.ClientAreaAnimation),
                // Too narrow for a meaningful stretch of text, the lyric rests at its start, cut at the edge.
                CompactPart.Music => _compactMarquee = new MusicMarquee { Text = text, Width = end - start, Height = layout.TextHeight, FontSize = layout.TextFont, EdgeWidth = layout.TextEdge,
                    ScrollAllowed = layout.TextScrolls, Backdrop = PanelTheme.MarqueeBackdrop, InkFade = PanelTheme.Current.HasOverlay },
                _ => Readings(strip!, end - start)
            };
            // The carousels keep their own width and left fade; the margins put each element's slot at [start, end).
            double left = element.Margin.Left;
            element.Margin = new Thickness(left + start - placed, 0, end - start - (element.Width + left), 0);
            element.VerticalAlignment = VerticalAlignment.Center; element.HorizontalAlignment = HorizontalAlignment.Left;
            middle.Children.Add(element); placed = end;
        }
        CompactRings.Children.Add(grid); UpdateMusicTimer();
        // The readings were built with the row; they start or stop the sampler and take the newest reading.
        _stripShowing = false; UpdateMonitorDemand();
    }
    // One ring per application: the highest used share among its quotas, with every quota in the tooltip.
    private (double Used, string Color, string Tip)[] QuotaRings(IReadOnlyList<AppEntry> apps) => apps.Select(entry =>
    {
        var snapshot = DashboardUsage.Quota(entry, Snapshots);
        var percentages = snapshot.Metrics.Where(m => m.UsedPercent is not null).Select(m => Math.Clamp(m.UsedPercent!.Value, 0, 100)).ToArray();
        double used = percentages.Length > 0 ? percentages.Max() : snapshot.Metrics.Count > 0 ? 100 : 0;
        string tip = entry.Name + " · " + (percentages.Length > 0 ? Loc.T($"已用 {used:0.#}%", $"{used:0.#}% used")
            : snapshot.Metrics.Count > 0 ? string.Join(Loc.T("；", "; "), snapshot.Metrics.Select(m => m.ValueText.Length > 0 ? m.ValueText + " " + m.ValueSuffix : Loc.Label(m.Label))) : snapshot.StatusLabel);
        return (used, PanelTheme.AppTheme(entry), tip);
    }).ToArray();
    private void UpdateMusicTimer()
    {
        bool musicVisible = MusicPresentationVisible;
        _app.PresentMedia(this, musicVisible);
        bool progressVisible = !_expanded && EffectiveStyle == CompactStyle.Line && Settings.MusicIndicatorProgress && _mediaTrack is { HasTimeline: true, State: not MediaState.Stopped };
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        // A paused song rests at the start of its line; an endless scroll would keep the window composing for hours.
        _compactMarquee?.SetScrolling(animations && MusicAudioActive && IsVisible && !IsSuppressed && !_expanded && _alert is null && EffectiveStyle != CompactStyle.Line);
        UpdateLyricScrolling();
        UpdateQuotaCarousel();
        bool needed = IsVisible && !IsSuppressed && (MusicAudioActive && (musicVisible || progressVisible) || _trackNoticeUntil > DateTimeOffset.UtcNow);
        // Frame-rate ticks only drive visible equalizer bars; the line indicator's progress moves a pixel every few seconds.
        _musicTick.Interval = TimeSpan.FromMilliseconds(animations && MusicAudioActive && musicVisible ? 33 : 250);
        if (needed) { if (!_musicTick.IsEnabled) { _lastMusicTick = DateTimeOffset.UtcNow; _musicTick.Start(); } }
        else _musicTick.Stop();
        if (_expanded || EffectiveStyle != CompactStyle.Line || !Settings.MusicIndicatorProgress || _mediaTrack is not { HasTimeline: true, State: not MediaState.Stopped }) MusicIndicator.Visibility = Visibility.Collapsed;
    }
    private void UpdateLyricScrolling() => _lyricCurrent?.SetScrolling(Settings.Animations && SystemParameters.ClientAreaAnimation
        && MusicAudioActive && IsVisible && !IsSuppressed && _expanded && IsMusicPage);
    // The carousel runs its own timer and WPF animation; it only needs to know when it may move.
    private void UpdateQuotaCarousel()
    {
        bool running = IsVisible && !IsSuppressed && !_expanded && _alert is null;
        _quotaCarousel?.SetRunning(running, _pointerInside);
        _stripCarousel?.SetRunning(running, _pointerInside);
    }
    private bool MusicPresentationVisible => IsVisible && !IsSuppressed && _mediaTrack is { State: not MediaState.Stopped }
        && (_expanded ? IsMusicPage : Settings.ShowsMusic(EffectiveStyle) && _alert is null);
    private bool MusicAudioActive => _mediaTrack is { State: MediaState.Playing };
    private void TickMusic()
    {
        var now = DateTimeOffset.UtcNow; double seconds = Math.Min(.5, (now - _lastMusicTick).TotalSeconds); _lastMusicTick = now;
        bool animations = Settings.Animations && SystemParameters.ClientAreaAnimation;
        bool playing = animations && MusicAudioActive;
        bool musicVisible = MusicPresentationVisible;
        _app.PresentMedia(this, musicVisible);
        float peak = musicVisible ? _media?.Peak() ?? 0 : 0;
        // Captured audio drives the flowing bars; the session peak is the fallback.
        bool flowing = musicVisible && _media?.FlowLevels(_flowLevels) == true;
        if (_expanded && IsMusicPage) { if (flowing) _musicEqualizer?.Show(_flowLevels, playing); else _musicEqualizer?.Tick(peak, playing, seconds); }
        if (!_expanded && _alert is null) { if (flowing) _compactEqualizer?.Show(_flowLevels, playing); else _compactEqualizer?.Tick(peak, playing, seconds); }
        if (_trackNoticeUntil != default && now >= _trackNoticeUntil) { _trackNoticeUntil = default; RenderCompact(); }
        if ((now - _positionTick).TotalMilliseconds >= 250) { _positionTick = now; UpdateMusicPosition(); }
    }
    private void DisposeMusic() { CloseSourceMenu(); _musicTick.Stop(); _lyricsRequest?.Cancel(); _app.MediaChanged -= MediaChanged; _app.PresentMedia(this, false); }
}
