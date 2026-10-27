using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Native;

namespace BrimDeck;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _settingsEvent;
    private RegisteredWaitHandle? _listener;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public DeckSettings Settings { get; private set; } = new();
    public SettingsStore Store { get; private set; } = null!;
    public UsageService Usage { get; private set; } = null!;
    public ProviderSecrets Secrets { get; private set; } = null!;
    public Pricing Prices { get; private set; } = null!;
    public AppUpdates Updates { get; internal set; } = null!;
    // The main panel. It owns the tray icon, reads the usage data and takes the settings preview; every other chosen
    // monitor gets a panel that shows the same data.
    public MainWindow Deck { get; private set; } = null!;
    private readonly Dictionary<string, MainWindow> _otherDecks = [];
    public IEnumerable<MainWindow> Decks => _otherDecks.Values.Prepend(Deck);
    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = [];
    private bool _arrangePending;
    // Every panel shows the one set of players, and their lyrics are looked up once.
    internal MediaSessions? Media { get; private set; }
    internal event Action? MediaChanged;
    internal LyricsService Lyrics { get; } = new();
    private readonly HashSet<MainWindow> _presentingMedia = [];
    // The system monitor page's readings, shared by every panel; created when a panel first shows the page.
    internal SystemSampler? Monitor { get; private set; }
    // Which panels are showing each extension's page; an extension works while any of them is.
    private readonly HashSet<MainWindow>[] _presentingExtension = [ [], [], [], [] ];
    internal event Action<MonitorSnapshot>? MonitorUpdated;
    private readonly HashSet<MainWindow> _presentingMonitor = [], _stripMonitor = [];
    public CancellationTokenSource Lifetime { get; } = new();
    public bool Exiting { get; private set; }

    // Tooltip delays are not inherited, so the defaults are replaced for every element before any window exists.
    // The system hover time on some machines is a full second; every tooltip here opens after the same short pause.
    static App()
    {
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(120));
        System.Windows.Controls.ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(400));
        System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources[SystemParameters.VerticalScrollBarWidthKey] = 7.0;
        // A profile moved with --data-dir is an instance of its own: two profiles (the installed copy and a test run)
        // can be open together, and the second instance of the same profile still leaves at once.
        var suffix = Environment.UserName + (AppPaths.IsOverridden ? "." + AppPaths.ProfileTag : "");
        var eventName = "Local\\BrimDeck.Settings." + suffix;
        _mutex = new Mutex(true, "Local\\BrimDeck.Instance." + suffix, out var first);
        if (!first)
        {
            try { using var existing = EventWaitHandle.OpenExisting(eventName); existing.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        SetCurrentProcessExplicitAppUserModelID("BrimDeck.Desktop");
        RecordCrashes();
        var folder = AppPaths.Data;
        if (AppPaths.LastError is { } dataError)
            try { File.AppendAllText(Path.Combine(folder, "crash.log"), $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {dataError}\n\n"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Store = new SettingsStore(folder); Settings = Store.Load();
        UseLanguage();
        PanelTheme.Use(Settings);
        IslandExtensions.SetPalette(Settings);
        // A plugin page that wrote one of its own declared controls reaches the file through the app, not through itself:
        // the same queued save every other change uses, and the settings window redone so its row shows the new value.
        // The pages of that plugin are told by the host, which does that whether the change came from a page or a click.
        IslandExtensions.WriteValue = (id, key, value) =>
        {
            Settings.SetExtensionValue(id, key, value);
            QueueSave();
            _settingsWindow?.PluginValuesChanged();
        };
        // The host is started once the single-instance check has passed and the language and theme are current: a second
        // instance leaves before running any plugin code, and the first views get the saved palette.
        IslandExtensions.Load();
        IslandExtensions.ExtensionsChanged += slots =>
        {
            // A replaced instance starts knowing nothing; the panels showing its page tell it again.
            foreach (var slot in slots)
                if (_presentingExtension[slot].Count > 0) IslandExtensions.Notify(slot, true);
            foreach (var deck in Decks.ToList()) deck.OnExtensionsChanged(slots);
        };
        // "跟随系统" follows the Windows app theme while BrimDeck runs.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        // The language a first start takes from Windows is kept, even if the display language changes later.
        if (Store.IsFirstStart) FlushSettings();
        try
        {
            Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
            // When moving from the old EXE to an installed copy, keep an enabled startup entry at the new stable path.
            if (Settings.LaunchAtStartup) StartupRegistration.SetEnabled(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Prices = new Pricing(folder);
        Updates = new AppUpdates(BrimDeck.Updates.GitHubUpdates.Create(folder), folder);
        Secrets = new ProviderSecrets(folder);
        Usage = new UsageService(new DesktopSources(), secrets: Secrets, quotaCache: Path.Combine(folder, ClaudeQuotaCache.FileName),
            kimiQuotaCache: Path.Combine(folder, KimiQuotaCache.FileName));
        Deck = new MainWindow(this); MainWindow = Deck;
        Displays = Native.Displays.Current(); AssignDisplays();
        Deck.Show();
        InitializeHotkeys();
        StartBackgroundUpdates();
        _save.Tick += (_, _) => FlushSettings();
        _tray = new TrayIcon(Deck, OpenSettings, ExitApplication, ToggleHidden, () => UserHidden);
        _settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _listener = ThreadPool.RegisterWaitForSingleObject(_settingsEvent, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);
        if (e.Args.Contains("--settings")) OpenSettings();
    }
    // A resident app must not vanish without a trace. Unhandled errors are appended to crash.log in the data folder;
    // errors on the UI thread are logged and the app keeps running, and unobserved task errors are only logged.
    private void RecordCrashes()
    {
        var log = Path.Combine(AppPaths.Data, "crash.log");
        void Write(object error)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                // The log keeps only recent entries.
                if (File.Exists(log) && new FileInfo(log).Length > 1_048_576) File.Delete(log);
                File.AppendAllText(log, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {ApplicationVersion.Current}\n{error}\n\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DispatcherUnhandledException += (_, args) => { Write(args.Exception); args.Handled = !Exiting; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Write(args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Write(args.Exception); args.SetObserved(); };
    }
    // Names used by XAML templates follow the language through these resources.
    private void UseLanguage()
    {
        Loc.Use(Settings.Language);
        // The extension pages hear the language as a push, the same way they hear the palette.
        WebRuntime.SetLanguage(Loc.IsEnglish);
        Resources["PinSourceName"] = Loc.T("固定显示", "Pin");
        Resources["UnpinSourceName"] = Loc.T("取消固定", "Unpin");
    }
    public void OpenSettings() => ShowSettings(false);
    // The panel ends a collapsed preview when the pointer reaches it. The settings window clears its own preview state; the
    // panel is resumed either way, in case the two disagree (ResumeAfterPreview does nothing when no preview is on).
    internal void EndPreviewFromDeck()
    {
        _settingsWindow?.EndPreview();
        Deck.ResumeAfterPreview();
    }
    // The panel's update dot opens the About page with the update dialog.
    internal void OpenUpdateSettings() => ShowSettings(true);
    private void ShowSettings(bool update)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            if (Store.LoadWarning is { } warning) _settingsWindow.SaveStatus(warning, true);
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        _ = update ? _settingsWindow.ShowUpdateNoticeAsync() : _settingsWindow.CheckUpdatesOnOpenAsync();
    }
    public void UpdateSettings(DeckSettings settings)
    {
        foreach (var previous in Settings.Apps)
        {
            var next = settings.Entry(previous.InstanceId);
            if (previous.SecretRevision != Guid.Empty && (next is null || next.QuotaSource != previous.QuotaSource))
            {
                try { Secrets.Delete(previous.InstanceId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { _settingsWindow?.SaveStatus(Loc.T("密钥文件无法更新，请检查本机设置目录后重试。", "The key file could not be updated. Check the local settings folder and try again."), true); return; }
            }
        }
        bool dataChanged = settings.IncludeWsl != Settings.IncludeWsl || !settings.RequiredProviders.SetEquals(Settings.RequiredProviders) ||
            !settings.EnabledApps.Select(e => e.ConfigurationKey).ToHashSet().SetEquals(Settings.EnabledApps.Select(e => e.ConfigurationKey));
        bool syncEnabled = settings.UsageAutoSync && !Settings.UsageAutoSync;
        // A new collapsed style morphs every panel's surface into its form (MainWindow.MorphShape).
        bool styleChanged = settings.Style != Settings.Style;
        settings.Normalize(); Settings = settings;
        ApplyHotkeys();
        // Statuses and notes are worded when the data is read, so a new language reads the data again.
        bool languageChanged = Loc.Normalize(settings.Language) != Loc.Language;
        if (languageChanged)
        {
            UseLanguage();
            // The extension tabs carry the language too: their tooltip and accessible name.
            foreach (var deck in Decks.ToList()) deck.RefreshExtensionButtons();
        }
        PanelTheme.Use(settings);
        IslandExtensions.SetPalette(settings);
        AssignDisplays();
        foreach (var deck in Decks.ToList()) { if (styleChanged) deck.MorphShape(); else deck.ApplySettings(); }
        // A page's own switch takes effect at once, closing or reopening what its extension reads.
        foreach (var deck in Decks.ToList()) deck.UpdateMonitorDemand();
        QueueSave();
        CollapsedChanged();
        if (dataChanged || syncEnabled || languageChanged) _ = Deck.RefreshAutomaticallyAsync(queueIfBusy: true);
    }
    // The settings window's size group follows changes made on the panel (a drag, or content that changed the auto width).
    internal void CollapsedChanged() => _settingsWindow?.CollapsedChanged();
    private void SystemThemeChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Exiting) return;
            if (PanelThemes.NormalizeId(Settings.PanelTheme) == PanelThemes.SystemId) ApplyPanelTheme(systemChanged: true);
            // The theme page's "System" card shows the Windows mode whichever theme is chosen.
            else _settingsWindow?.PanelThemeChanged(onlyThemePage: true);
        });
    }
    // Makes the chosen panel theme current (reading the theme folder again when asked) and repaints every panel if it changed.
    // When Windows switches "system" between dark and light, a preset clock gradient carries over as it does when a theme is chosen.
    internal void ApplyPanelTheme(bool reload = false, bool systemChanged = false)
    {
        var previous = PanelTheme.Current;
        if (!PanelTheme.Use(Settings, reload, systemChanged)) return;
        // The extension pages are drawn in the theme's text and surface colors, so a new theme gives them new ones.
        IslandExtensions.SetPalette(Settings);
        if (systemChanged && PanelThemes.CarryGradient(previous, PanelTheme.Current, Settings)) QueueSave();
        foreach (var deck in Decks.ToList()) deck.ApplySettings();
        if (!reload) _settingsWindow?.PanelThemeChanged();
    }
    // A panel reports display changes (monitors added or removed, the primary changed, a work area or scale changed).
    // Each panel receives the same messages, so they are handled once, after Windows has moved the windows to the new layout.
    internal void DisplaysChanged()
    {
        if (_arrangePending || Exiting) return;
        _arrangePending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _arrangePending = false;
            if (Exiting) return;
            Displays = Native.Displays.Current(); AssignDisplays();
            foreach (var deck in Decks.ToList()) deck.ApplySettings();
            _settingsWindow?.DisplaysChanged();
        });
    }
    // Gives the main panel its monitor and opens or closes the panels of the other monitors. A panel that keeps its
    // monitor is kept, with its state.
    internal void AssignDisplays()
    {
        var plan = DisplayLayout.Arrange(Settings.Display, Settings.DisplayMonitors.Select(d => d.Id).ToList(), Displays.Select(d => (d.Id, d.Primary)).ToList());
        Deck.Display = Displays.FirstOrDefault(d => d.Id == plan.Main);
        foreach (var id in _otherDecks.Keys.Except(plan.Others).ToList())
        { var deck = _otherDecks[id]; _otherDecks.Remove(id); deck.Retire(); }
        // Panels are opened only once the main panel runs, so that they can take its data.
        if (!Deck.IsLoaded) return;
        foreach (var id in plan.Others)
        {
            var display = Displays.First(d => d.Id == id);
            if (_otherDecks.TryGetValue(id, out var deck)) { deck.Display = display; continue; }
            deck = new MainWindow(this, Deck) { Display = display };
            _otherDecks[id] = deck; deck.Show();
        }
    }
    internal void StartMedia()
    {
        bool wanted = Settings.MediaWanted;
        if (wanted && Media is null) { Media = new MediaSessions(Dispatcher); Media.Changed += RaiseMediaChanged; _ = Media.StartAsync(); }
        else if (!wanted && Media is not null) { Media.Changed -= RaiseMediaChanged; Media.Dispose(); Media = null; RaiseMediaChanged(); }
    }
    private void RaiseMediaChanged() => MediaChanged?.Invoke();
    // Audio levels are read while any panel shows music.
    internal void PresentMedia(MainWindow deck, bool active)
    {
        if (active) _presentingMedia.Add(deck); else _presentingMedia.Remove(deck);
        Media?.SetPresentationActive(_presentingMedia.Count > 0);
    }
    // The system is read while any panel shows the monitor page open (and briefly after; see SystemSampler) or shows readings in
    // its collapsed row. Processes are listed only for an open page.
    internal void PresentMonitor(MainWindow deck, bool page, bool strip)
    {
        if (page) _presentingMonitor.Add(deck); else _presentingMonitor.Remove(deck);
        if (strip) _stripMonitor.Add(deck); else _stripMonitor.Remove(deck);
        bool wanted = _presentingMonitor.Count + _stripMonitor.Count > 0;
        if (Exiting || Monitor is null && !wanted) return;
        if (Monitor is null) { Monitor = new SystemSampler(Dispatcher); Monitor.Updated += snapshot => MonitorUpdated?.Invoke(snapshot); }
        Monitor.SetDemand(wanted, Settings.MonitorInterval, _presentingMonitor.Count > 0);
    }
    // An extension's page being shown or stopped being shown by one panel; the extension learns whether any panel
    // shows it, and nothing else — the page switch and the loading decide the rest.
    internal void PresentExtension(MainWindow deck, int slot, bool shown)
    {
        if (slot < 0 || slot >= _presentingExtension.Length) return;
        bool before = _presentingExtension[slot].Count > 0;
        if (shown) _presentingExtension[slot].Add(deck); else _presentingExtension[slot].Remove(deck);
        bool after = _presentingExtension[slot].Count > 0;
        // Only the change of the answer is news: a panel re-rendering while another already shows the page is not.
        if (before != after) IslandExtensions.Notify(slot, after && !Exiting);
    }
    // Settings changed outside UpdateSettings are written after the same short pause instead of immediately.
    public void QueueSave() { _save.Stop(); _save.Start(); }

    // Told by a panel whenever it opens or closes (MainWindow.SetExpanded, window close): while any panel is open the
    // pre-built plugin pages stay ready to switch to; once the last one closes, the pages nobody watches are
    // reclaimed after the usual grace.
    internal void PanelUseChanged() => IslandExtensions.PanelInUse(Decks.Any(deck => deck.IsExpanded));
    // The settings are turned into text here, where they live, and written on the thread pool: writing the file (and the virus
    // scan that follows a write) took 60 ms and more, which showed as a stall after every page switch. Writes run one after
    // another in the order they were asked for, so the newest settings always land last.
    private Task _writing = Task.CompletedTask;
    public void FlushSettings()
    {
        _save.Stop();
        string text = Store.Serialize(Settings);
        _writing = _writing.ContinueWith(_ =>
        {
            string? error = null;
            try { Store.Write(text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = Loc.T("保存失败，请检查设置目录权限。", "Saving failed. Check the permissions of the settings folder."); }
            Dispatcher.BeginInvoke(() => { if (error is null) _settingsWindow?.SaveStatus(""); else _settingsWindow?.SaveStatus(error, true); });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
    // Before the process ends, the last write must have reached the disk.
    private void FinishWriting() { try { _writing.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { } }
    // The settings window records its size before the final save; closing it during shutdown would be too late.
    // Every panel powers off first (MainWindow.Lifecycle.cs): the islands draw back into dots that sink down through the floor, then the process ends. The
    // settings window goes at once, so the exit answers immediately. A panel that never reports back cannot hold the exit up:
    // the process ends anyway once the longest power-off would have finished.
    public void ExitApplication()
    {
        if (Exiting) return;
        Exiting = true; Lifetime.Cancel(); _settingsWindow?.RememberSize(); FlushSettings();
        _settingsWindow?.Hide();
        var decks = Decks.ToList();
        int waiting = decks.Count;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BrimDeck.MainWindow.PowerOffLength(Settings.AnimationDuration) + 500) };
        bool done = false;
        void End() { if (done) return; done = true; fallback.Stop(); FinishWriting(); Shutdown(); }
        fallback.Tick += (_, _) => End();
        fallback.Start();
        foreach (var deck in decks) deck.PowerOff(() => { if (--waiting <= 0) End(); });
        if (decks.Count == 0) End();
    }
    internal void InstallUpdate()
    {
        if (Updates.PrepareRestart(() => { FinishWriting(); Store.Save(Settings); })) ExitApplication();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true; Lifetime.Cancel(); _save.Stop(); FinishWriting(); StopBackgroundUpdates(); Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged; _tray?.Dispose(); DisposeHotkeys(); _listener?.Unregister(null); _settingsEvent?.Dispose();
        Media?.Dispose(); Monitor?.Dispose(); Lyrics.Dispose(); Prices?.Dispose(); _mutex?.Dispose();
        for (int slot = 0; slot < IslandExtensions.Slots.Length; slot++) IslandExtensions.Slots[slot]?.SetDemand(false);
        base.OnExit(e);
    }
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
