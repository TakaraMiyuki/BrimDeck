using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Core;
using BrimDeck.Island;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrimDeck;

// The palette fragment shared by a page's init message and the push a palette change sends.
internal static class WebColors
{
    public static string Json { get; private set; } = "{}";
    public static void Set(IslandColors colors) => Json = JsonSerializer.Serialize(colors);
}

// The pieces every web extension shares: one WebView2 environment and browser process for all four slots, the palette
// and language pushed to live pages, and the single tick that fires registered alarms. A shared environment is what
// keeps four plugin pages at "a few extra tabs" rather than four browser runtimes.
internal static class WebRuntime
{
    private static Task<CoreWebView2Environment?>? _environment;
    private static readonly List<WebExtension> _all = new();
    private static readonly DispatcherTimer _alarmTick = new() { Interval = TimeSpan.FromSeconds(1) };
    // Kept for the extensions registered later: a plugin discovered while a panel is open must not start reclaiming its
    // pages before that panel closes.
    private static bool _panelInUse;

    // Set when the WebView2 runtime itself is missing: every plugin page becomes a note about it instead of a blank
    // rectangle. Cleared never — without the runtime there is nothing to retry into until the app restarts.
    public static string? EnvironmentError { get; private set; }

    // First asked at startup (IslandExtensions.Load), so by the time a page is created the answer is ready. Called on
    // the UI thread only; the one environment is made once and shared by every control.
    public static async Task<CoreWebView2Environment?> Environment()
    {
        _environment ??= MakeEnvironment();
        try { return await _environment.ConfigureAwait(true); }
        catch (Exception ex)
        {
            EnvironmentError = ex.Message.Split('\n')[0];
            ExtensionLog.Write("runtime: environment failed: " + EnvironmentError);
            return null;
        }
    }

    private static async Task<CoreWebView2Environment?> MakeEnvironment()
    {
        try
        {
            var folder = Path.Combine(AppPaths.Data, "extension-web");
            ExtensionLog.Write("runtime: creating environment at " + folder);
            var environment = await CoreWebView2Environment.CreateAsync(null, folder, null).ConfigureAwait(true);
            ExtensionLog.Write("runtime: environment ready");
            return environment;
        }
        catch (Exception ex)
        {
            EnvironmentError = ex.Message.Split('\n')[0];
            ExtensionLog.Write("runtime: environment failed: " + EnvironmentError);
            return null;
        }
    }

    public static void Register(WebExtension extension)
    {
        if (_all.Contains(extension)) return;
        _all.Add(extension);
        // A plugin found while a panel stands open learns that at once: its pages wait for the panel to close before
        // they are reclaimed, like every page that was already here.
        if (_panelInUse) extension.PanelInUse(true);
    }
    public static void Unregister(WebExtension extension)
    {
        _all.Remove(extension);
        AlarmsChanged();
    }

    // Whether any panel is open, told by the app as that changes: pages are reclaimed only while the panel is closed.
    public static void PanelInUse(bool inUse)
    {
        _panelInUse = inUse;
        foreach (var extension in _all.ToList()) extension.PanelInUse(inUse);
    }

    public static void SetColors(IslandColors colors)
    {
        WebColors.Set(colors);
        foreach (var extension in _all.ToList()) extension.Push("{\"channel\":\"brimdeck\",\"kind\":\"colors\",\"data\":" + WebColors.Json + "}");
    }

    public static void SetLanguage(bool english)
    {
        var language = english ? "en" : "zh";
        // A plugin's own words are read in the interface's language, so they follow the language wherever it changes.
        foreach (var extension in _all.ToList())
        {
            extension.Push("{\"channel\":\"brimdeck\",\"kind\":\"language\",\"data\":\"" + language + "\"}");
            extension.PushPhrases();
        }
    }

    // A reader changed one plugin's control in the settings window, or a page wrote one: every live page of that plugin
    // gets the whole set again, in the shape its own manifest declares.
    public static void ValuesChanged(string id)
    {
        foreach (var extension in _all.ToList()) if (extension.Id == id) extension.PushSettings();
    }

    // Every plugin's values, pushed: the settings window applied a change, and each page should read what the file says
    // now rather than what it read when it loaded.
    public static void PushValues()
    {
        foreach (var extension in _all.ToList()) extension.PushSettings();
    }

    // The tick runs only while some extension has an alarm waiting; a sleeping app costs nothing here.
    public static void AlarmsChanged()
    {
        if (_all.Any(extension => extension.HasAlarms)) _alarmTick.Start();
        else _alarmTick.Stop();
    }
    static WebRuntime() => _alarmTick.Tick += (_, _) => { foreach (var extension in _all.ToList()) extension.CheckAlarms(); };
}

// One web plugin: its manifest, its live pages, and everything its pages hand the host — saved state, registered
// alarms, the keep-alive wish. The pages are WebView2 controls pointed at the plugin's folder through a virtual host,
// so a plugin is files the user dropped in a directory, never code the host runs outside the browser.
//
// Every message from a page is untrusted text: parsed defensively, answered best-effort, never allowed to throw into
// the host. A page that misbehaves loses its bridge, nothing more.
internal sealed class WebExtension : IIslandExtension
{
    private readonly ExtensionManifest _manifest;
    private readonly string _folder;
    private readonly string _host;
    private readonly List<(WebView2CompositionControl Control, CoreWebView2 Core)> _pages = new();
    // Views whose control has been disposed. A panel may still hold one in its view cache for a moment; asking the
    // extension first keeps a dead page from being put back on screen with nothing in it.
    private readonly HashSet<WebView2CompositionControl> _retired = new();
    // Controls this extension created and has not disposed. A control that is still initializing belongs here too:
    // its CoreWebView2 being null is "not ready yet", not "dead".
    private readonly HashSet<WebView2CompositionControl> _created = new();
    // The plugin's state and alarms live in the host, not in the page, so they survive the page being reclaimed while
    // nobody watches it: what the page hands over it gets back the next time it loads.
    private string? _stateJson;
    private (double Width, double Height)? _fitLogged;
    private readonly Dictionary<string, (DateTime Due, bool Fired)> _alarms = new(StringComparer.Ordinal);
    private bool _shown, _keepAlive, _reclaimed;
    // How often this plugin has asked for the desktop browser lately; every ask is counted, because none of them can
    // prove a reader was the one who asked.
    private readonly LinkBudget _links = new(Bridge.MaximumLinksPerWindow, Bridge.LinkWindow);
    // Whether any panel is open: pages are only reclaimed while the panel itself is closed (see SetDemand).
    private bool _inUse;
    // Whether some page has finished a navigation and not been reclaimed since: the switch entrance needs a loaded
    // page; a cold page arrives by the fade-in of its first navigation instead.
    private bool _loaded;
    // The side a switch came in from while the page was still loading: the first navigation arrives from that side
    // instead of fading in place. Null when the load was not caused by a switch (the panel opening on the page).
    private bool? _enterForward;
    private DispatcherTimer? _reclaim;

    public WebExtension(ExtensionManifest manifest, string folder)
    {
        _manifest = manifest;
        _folder = folder;
        // The readable part plus a short digest, so ids that differ only in '.' versus '-' do not share one virtual host.
        var digest = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(manifest.Id.ToLowerInvariant())))[..8].ToLowerInvariant();
        _host = manifest.Id.ToLowerInvariant().Replace('.', '-') + "-" + digest + ".plugin.brimdeck";
        WebRuntime.Register(this);
    }

    public string Id => _manifest.Id;
    public string TitleZh => _manifest.TitleZh;
    public string TitleEn => _manifest.TitleEn;
    public string Glyph => _manifest.Glyph;
    // The smallest page this plugin can show without losing content, as its manifest asks; zero on either axis means it
    // asks for nothing beyond the panel's own floor.
    internal (double Width, double Height) Minimum => (_manifest.MinWidth, _manifest.MinHeight);
    // What the plugin declared for the host to draw and speak: the controls it offers and the words it needs.
    internal IReadOnlyList<ExtensionSetting> Declared => _manifest.Settings;
    // The declared entries the host could not use, in the author's own words: a control that vanished because its label
    // was written as a number is otherwise invisible to the person who wrote it.
    internal IReadOnlyList<string> Notes => _manifest.Notes;
    // The values the pages of this plugin were last given. A settings event is a reason to re-render, so it is sent when
    // there is something to re-render for — not on every theme change, panel resize or language switch, which is what
    // pushing blindly from the palette made it.
    private string? _pushedSettings;
    private string SettingsJson => ExtensionSettingStore.ValuesJson(_manifest.Settings, IslandExtensions.ValuesFor(Id));
    private string PhrasesJson => ExtensionSettingStore.PhrasesJson(_manifest.Phrases, Loc.IsEnglish);
    internal void PushSettings()
    {
        if (_manifest.Settings.Count == 0) return; // a plugin that declared nothing has nothing to hear about
        var json = SettingsJson;
        if (json == _pushedSettings) return;
        _pushedSettings = json;
        ExtensionLog.Write("view: settings " + Id); // one line per settings event that actually went out
        Push("{\"channel\":\"brimdeck\",\"kind\":\"settings\",\"data\":" + json + "}");
    }
    internal void PushPhrases() => Push("{\"channel\":\"brimdeck\",\"kind\":\"phrases\",\"data\":" + PhrasesJson + "}");

    // A page asked to change one of its own controls. Only a declared key is answerable, and the value is read as that
    // control reads it: a page that writes 63 to a step of fives gets 65 back, because the declaration — not the page —
    // decides what a value means. The write goes to the app, which owns the file and the other views of it.
    private void WriteSetting(JsonElement root, CoreWebView2? from)
    {
        if (!root.TryGetProperty("key", out var keyNode) || keyNode.ValueKind != JsonValueKind.String)
        {
            Refused(from, "bad-setting", "setSetting wants a key and a value");
            return;
        }
        var key = keyNode.GetString()!;
        var setting = _manifest.Settings.FirstOrDefault(item => item.Key == key);
        if (setting is null)
        {
            Refused(from, "unknown-setting", key);
            return;
        }
        if (!root.TryGetProperty("value", out var value))
        {
            Refused(from, "bad-setting", setting.Key + " wants a value");
            return;
        }
        ExtensionSettingValue? parsed = setting.Kind switch
        {
            ExtensionSettingKind.Toggle when value.ValueKind is JsonValueKind.True or JsonValueKind.False => new ExtensionSettingValue("toggle", value.GetBoolean()),
            ExtensionSettingKind.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) => new ExtensionSettingValue("number", false, number),
            ExtensionSettingKind.Choice when value.ValueKind == JsonValueKind.String => new ExtensionSettingValue("choice", false, 0, value.GetString() ?? ""),
            _ => null
        };
        if (parsed is null)
        {
            Refused(from, "bad-setting", setting.Key + " takes a " + setting.Type);
            return;
        }
        var honored = ExtensionSettingStore.Honor(setting, parsed);
        // What the host kept, not what was asked for: a page that wrote 63 to a step of fives reads this line and knows
        // the number it will be given back is 65.
        ExtensionLog.Write($"view: setting {Id} {key}={honorValue(honored, setting)}");
        // The declaration decides the value, and the value already in force is what the pages were last told: writing
        // it again would queue a save, rebuild the settings row and push a settings event — which a page that normalizes
        // inside its own settings handler would answer by writing again. Nothing changed is nothing to do, and the page
        // needs no echo: what it already holds is what the host holds.
        if (IslandExtensions.ValuesFor(Id).TryGetValue(key, out var kept) && kept == honored) return;
        IslandExtensions.Write(Id, key, honored);

        static string honorValue(ExtensionSettingValue value, ExtensionSetting setting) => setting.Kind switch
        {
            ExtensionSettingKind.Toggle => value.On ? "true" : "false",
            ExtensionSettingKind.Number => value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.Choice
        };
    }

    public FrameworkElement CreateView(IslandColors colors)
    {
        ExtensionLog.Write("view: create " + Id);
        var page = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // A machine without the WebView2 runtime gets the reason in place of the page, not a blank rectangle.
        var note = new TextBlock
        {
            Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
        };
        var host = new Grid();
        host.Children.Add(page);
        host.Children.Add(note);
        _created.Add(page);
        page.Unloaded += (_, _) => RemovePage(page);
        _ = InitializeAsync(page, note);
        return host;
    }

    // A freshly built page loads its document for a moment — longer than a page switch's own arrival — and then
    // appeared in one step. The shim holds the document unseen from its very first paint; every finished navigation
    // fades it in inside the browser, where WPF's own opacity does not reliably reach the composition content. The
    // shim also clears the hold by itself after a moment, so a host that stops mid-way cannot leave a page invisible.
    // A page reloaded after a reclaim arrives the same way: its new document is held by the shim again.
    private void ArriveWhenLoaded(CoreWebView2 core)
    {
        // The blank a reclaim navigates to is not content: its completion must neither mark the page loaded nor reveal
        // an empty document. Every other document, reloads included, is revealed.
        var blanks = new HashSet<ulong>();
        core.NavigationStarting += (_, e) => { if (e.Uri == "about:blank") blanks.Add(e.NavigationId); };
        core.NavigationCompleted += async (_, e) =>
        {
            if (blanks.Remove(e.NavigationId) || !e.IsSuccess) return;
            _loaded = true;
            ExtensionLog.Write("view: arrive " + Id);
            // A load a switch is waiting for comes in from that side; a load the panel opened on fades in place.
            int from = _enterForward is { } forward ? (forward ? 16 : -16) : 0;
            _enterForward = null;
            var script = Soft.Enabled
                ? $$"""
                  (function(){var d=document.documentElement||document.body;if(!d)return;var s=d.style;
                  s.transition='none';s.transform='translateX({{from}}px)';
                  void d.offsetHeight;
                  s.transition='opacity 350ms ease-out, transform 350ms ease-out';
                  s.opacity='1';s.transform='none';})()
                  """
                : "document.documentElement.style.opacity='1';";
            try { await core.ExecuteScriptAsync(script).ConfigureAwait(true); } catch (Exception) { }
        };
    }

    // The switch's own arrival cannot animate a WebView through WPF opacity. A page that is already loaded gets its
    // entrance inside the browser instead: it comes in from the side it is switched in from while the picture of the
    // page left behind fades off it. The page carries the switch's own arrival too — faint to full over the same move —
    // so the motion stays visible after the picture has gone rather than finishing unseen beneath it.
    internal void Enter(bool forward)
    {
        if (!Soft.Enabled) return;
        ExtensionLog.Write("view: enter " + Id + " forward=" + forward + " loaded=" + _loaded);
        // A loaded page takes its direction now; holding it for a later load would make the next arrival come in from
        // a switch that already happened. Only a page still loading keeps the direction for its first document.
        if (!_loaded) { _enterForward = forward; return; }
        _enterForward = null;
        // Reading a layout property between the two writes is what makes this animate: without the flush the browser
        // never commits the held start, sees no change from full to full, and the page simply appears at full
        // brightness. A frame callback is not enough — a view that has just been shown can run both in one batch.
        var script = $$"""
            (function(){var d=document.documentElement||document.body;if(!d)return;var s=d.style;
            s.transition='none';s.transform='translateX({{(forward ? 16 : -16)}}px)';s.opacity='0';
            void d.offsetHeight;
            s.transition='transform {{Soft.Move}}ms ease-out, opacity {{Soft.Move}}ms ease-out';
            s.transform='none';s.opacity='1';})()
            """;
        foreach (var (_, core) in Pages())
            _ = Run(core, script);
    }

    private static async Task Run(CoreWebView2 core, string script)
    {
        // A page mid-teardown or already disposed loses its entrance, nothing more; the page itself stays as it was.
        try { await core.ExecuteScriptAsync(script).ConfigureAwait(true); } catch (Exception) { }
    }

    // One CSS pixel per panel DIP. The page reports its own scale, which is the only number that says how many CSS
    // pixels it will lay out in, and the browser's scale for this window need not be the scale the panel is drawn at
    // (measured: a 400×174 box came out 352×153 CSS pixels where the browser works at 1.14 and the window at 1.0). The
    // control's zoom is what makes the two agree — the unit a plugin's own layout, and its manifest minimum, are
    // written in. Dividing the reported scale by the zoom already applied keeps this stable: once corrected the page
    // reports a scale of 1, the same base comes back out, and the value settles instead of oscillating.
    private void Fit(CoreWebView2 core, double reportedDpr, double reportedWidth, double reportedHeight)
    {
        int at = _pages.FindIndex(pair => ReferenceEquals(pair.Core, core));
        if (at < 0) return;
        var control = _pages[at].Control;
        double baseScale = reportedDpr / control.ZoomFactor;
        if (!double.IsFinite(baseScale) || baseScale <= 0) return;
        double want = VisualTreeHelper.GetDpi(control).DpiScaleX / baseScale;
        if (Math.Abs(want - control.ZoomFactor) <= 0.01)
        {
            // A report that needs no change is the page settled at the box it was given — the pair the UI test reads
            // back. It is worth a line each time the box moves by more than a step of rounding, so the last line in the
            // log is always about the page as it now stands, and one long drag still cannot fill the log.
            if (_fitLogged is { } logged && Math.Abs(logged.Width - control.ActualWidth) < 8 && Math.Abs(logged.Height - control.ActualHeight) < 8) return;
            _fitLogged = (control.ActualWidth, control.ActualHeight);
            ExtensionLog.Write($"view: fit {Id} box={control.ActualWidth:0}x{control.ActualHeight:0} page={reportedWidth:0}x{reportedHeight:0}");
            return;
        }
        control.ZoomFactor = (float)Math.Clamp(want, 0.2, 5);
        ExtensionLog.Write($"view: scale {Id} zoom={control.ZoomFactor:0.###} browser={baseScale:0.###}");
    }

    // A number a page chose to send; absent or non-numeric reads as zero, which every caller rejects.
    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;

    // The panel asks before putting a cached view back: a control that left the tree was disposed with the page, and
    // re-adding it would show an empty rectangle with no way to tell why.
    internal bool Alive(FrameworkElement view) => view switch
    {
        WebView2CompositionControl page => _created.Contains(page) && !_retired.Contains(page),
        Grid host => host.Children.OfType<WebView2CompositionControl>().All(page => _created.Contains(page) && !_retired.Contains(page)),
        _ => true
    };

    // The order matters: the shim must be in place before the first navigation, or the page's first scripts run with
    // no bridge to call and its hello is never sent. Everything here runs on the UI thread.
    private async Task InitializeAsync(WebView2CompositionControl page, TextBlock note)
    {
        try
        {
            var environment = await WebRuntime.Environment().ConfigureAwait(true);
            if (environment is null) { Fail(note, WebRuntime.EnvironmentError); return; }
            await page.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
            if (page.CoreWebView2 is not { } core) { Fail(note, ""); return; }
            ArriveWhenLoaded(core);
            core.SetVirtualHostNameToFolderMapping(_host, _folder, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            // A plugin page stays on its own virtual host: navigation elsewhere and new windows are refused, so the page
            // cannot become a browser tab pointed at the network.
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            _pages.Add((page, core));
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim).ConfigureAwait(true);
            ExtensionLog.Write("view: ready " + Id);
            NavigateIn(core);
        }
        catch (Exception ex) { Fail(note, ex.Message.Split('\n')[0]); }
    }

    private void Fail(TextBlock note, string? reason)
    {
        ExtensionLog.Write("view: failed " + Id + ": " + (reason ?? "no core"));
        note.Text = Loc.T("需要 WebView2 运行时才能显示插件页面：", "The WebView2 runtime is needed to show plugin pages: ") + (reason ?? "");
        note.Visibility = Visibility.Visible;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri == "about:blank") return;
        if (e.Uri.StartsWith("https://" + _host + "/", StringComparison.OrdinalIgnoreCase)) return;
        // A page that takes its own frame somewhere else is not answered at all, whatever it claims: a plugin that
        // wanted a reader out to a web address has a call for it (and a clicked anchor reaches that call through the
        // shim). Following a self-navigation would turn the plugin page into a browser tab — and WebView2 reports a
        // scripted location change as user-initiated, so the browser's own answer to "did someone click?" is not a
        // guardrail here. Measured, in the UI test: location.href set 300ms after load arrived as IsUserInitiated.
        ExtensionLog.Write("view: refused navigation " + Id + " -> " + Short(e.Uri));
        e.Cancel = true;
    }

    // window.open is a page asking for the outside, and the island never grows a browser window of its own: the request
    // goes to the desktop browser under the same rules and the same rate as the bridge's own call, and is always
    // answered here so no window can be left half-open.
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        Open(sender as CoreWebView2, e.Uri);
        e.Handled = true;
    }

    // One link out. What the page wrote is checked, not trusted (ExternalLinks holds the rule and its ceilings), and
    // every ask is rationed: the host cannot see a reader's click, only a message, and a page can send one in a loop.
    private bool Open(CoreWebView2? core, string? text)
    {
        var (url, reason) = ExternalLinks.Accept(text, _host);
        if (url is null) { Refused(core, "link-refused", reason ?? ""); return false; }
        if (!_links.Allow(DateTime.UtcNow))
        {
            Refused(core, "links-too-many", Bridge.MaximumLinksPerWindow + " per " + (int)Bridge.LinkWindow.TotalSeconds + "s");
            return false;
        }
        ExtensionLog.Write("view: link " + Id + " -> " + Short(url));
        try { UI.OpenUrl(url); return true; }
        catch (Exception ex) { Refused(core, "link-failed", ex.Message.Split('\n')[0]); return false; }
    }

    // A browser or renderer that dies leaves the control blank. It is logged (the log is on in development and in the
    // UI test), and a renderer that exited once is reloaded; a second failure is left to the next visit instead of
    // reloading in a loop.
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        ExtensionLog.Write("view: process failed " + Id + " kind=" + e.ProcessFailedKind);
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            && sender is CoreWebView2 core)
        {
            try { core.Reload(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    private void NavigateIn(CoreWebView2 core)
    {
        try
        {
            ExtensionLog.Write("view: navigate " + Id + " -> " + _manifest.Entry);
            core.Navigate("https://" + _host + "/" + _manifest.Entry.Replace('\\', '/'));
            _reclaimed = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            ExtensionLog.Write("view: navigate failed " + Id + ": " + ex.Message);
        }
    }

    // The demand a page hears as its visibility: true while any panel shows it, false once none does. A page nobody
    // watches is navigated to blank after a grace period — its state and alarms stay with the host, and the next true
    // hands them back — so a page nobody watches costs its renderer memory, not just its timers. The grace only runs
    // while the panel itself is closed: an open panel keeps its pre-built pages ready, because a switch between them
    // is meant to be at once and animated, and the whole panel closing is the real "nobody is watching".
    public void SetDemand(bool shown)
    {
        ExtensionLog.Write("view: demand " + Id + " shown=" + shown);
        _shown = shown;
        Push("{\"channel\":\"brimdeck\",\"kind\":\"visibility\",\"data\":" + (shown ? "true" : "false") + "}");
        if (shown)
        {
            _reclaim?.Stop();
            if (_reclaimed) foreach (var (_, core) in Pages()) NavigateIn(core);
        }
        else ArmReclaim();
    }

    // Whether any panel is open. Opening stops every grace period; closing starts them for the pages not shown.
    internal void PanelInUse(bool inUse)
    {
        ExtensionLog.Write("view: panel use " + Id + " inUse=" + inUse);
        _inUse = inUse;
        if (inUse) _reclaim?.Stop();
        else ArmReclaim();
    }

    private void ArmReclaim()
    {
        if (_keepAlive || _shown || _inUse) return;
        if (_reclaim is null)
        {
            _reclaim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _reclaim.Tick += Reclaim;
        }
        _reclaim.Stop();
        _reclaim.Start();
    }

    private void Reclaim(object? sender, EventArgs e)
    {
        _reclaim?.Stop();
        _reclaimed = true;
        // The blank page is no content: the next load arrives by its own fade-in, not by a switch entrance.
        _loaded = false;
        _enterForward = null;
        ExtensionLog.Write("view: reclaim " + Id);
        foreach (var (_, core) in Pages())
            try { core.Navigate("about:blank"); } catch (Exception) { }
    }

    internal bool HasAlarms => _alarms.Count > 0;

    internal void CheckAlarms()
    {
        var due = new List<string>();
        foreach (var (id, alarm) in _alarms)
            if (DateTime.UtcNow >= alarm.Due)
            {
                _alarms[id] = (alarm.Due, true);
                due.Add(id);
            }
        // An alarm owed is delivered at once to pages that exist and held for the next load of any that do not.
        if (due.Count > 0 && Pages().Count > 0)
            foreach (var id in due) { _alarms.Remove(id); Push("{\"channel\":\"brimdeck\",\"kind\":\"alarm\",\"data\":" + JsonSerializer.Serialize(id) + "}"); }
        WebRuntime.AlarmsChanged();
    }

    // One message to every live page of this plugin. A page mid-teardown or already disposed is dropped from the list,
    // not fought: the next call simply has fewer pages to reach. A control disposed by the window closing reports
    // itself as "wrong state" (InvalidOperationException over a COM error), not as ObjectDisposedException, and the
    // exit path pushes to every page after the panels are gone.
    internal void Push(string json)
    {
        foreach (var (_, core) in Pages())
            try { core.PostWebMessageAsJson(json); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    // A panel's view was taken off the window: its page goes with it, so a closed panel leaves no WebView behind. The
    // control is also marked retired: a panel's view cache may still hold it, and Alive() must say no before it is
    // put back on screen.
    private void RemovePage(WebView2CompositionControl control)
    {
        ExtensionLog.Write("view: dispose " + Id);
        _retired.Add(control);
        for (int at = _pages.Count - 1; at >= 0; at--)
            if (ReferenceEquals(_pages[at].Control, control))
            {
                try { _pages[at].Core.WebMessageReceived -= OnWebMessage; } catch (Exception) { }
                _pages.RemoveAt(at);
            }
        try { control.Dispose(); } catch (Exception) { }
    }

    private List<(WebView2CompositionControl, CoreWebView2)> Pages()
    {
        for (int at = _pages.Count - 1; at >= 0; at--)
            try { var _ = _pages[at].Core.Source; }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // A core disposed outside RemovePage must retire its view too, or a panel could put it back on screen.
                _retired.Add(_pages[at].Control);
                _pages.RemoveAt(at);
            }
        return _pages;
    }

    // The bridge's receive side. Every message is text a page chose to send: nothing here is trusted, nothing here
    // throws into the host.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The page that sent this is the page that has to hear back about it, so it is read once, here.
        var from = sender as CoreWebView2;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object) return;
            if (root.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.String && channel.GetString() == "brimdeck"
                && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                switch (kind.GetString())
                {
                    case "hello": ExtensionLog.Write("view: hello " + Id); ReplyInit(); break;
                    case "scale":
                        if (Number(root, "dpr") is var ratio && ratio is > 0.2 and < 5
                            && Number(root, "width") is var width && width > 0 && Number(root, "height") is var height && height > 0
                            && from is not null) Fit(from, ratio, width, height);
                        break;
                    case "saveState":
                        if (root.TryGetProperty("state", out var state) && state.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                        {
                            // State is untrusted text kept for a later load; anything past the ceiling is dropped rather
                            // than held in memory for good — and the page is told, since nothing else says it failed.
                            // The ceiling is in UTF-8 bytes, the unit its name and the docs state: a state written in
                            // Chinese costs about three of them per character the page counted.
                            var json = state.GetRawText();
                            if (Bridge.StateFits(json)) _stateJson = json;
                            else Refused(from, "state-too-large", Bridge.StateBytes(json) + " / " + Bridge.MaximumStateBytes + " bytes");
                        }
                        else _stateJson = null;
                        break;
                    case "alarmSet":
                        // The reachable range is checked before the conversion: past either end DateTimeOffset throws,
                        // and an exception here would leave the page with no answer at all — the one thing the bridge
                        // promises it never does.
                        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                            && id.GetString() is { Length: > 0 and <= 64 } alarmId
                            && root.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var milliseconds)
                            && Bridge.AlarmTimeReachable(milliseconds))
                        {
                            var due = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
                            if (due <= DateTime.UtcNow) Refused(from, "alarm-in-the-past", alarmId);
                            else if (!_alarms.ContainsKey(alarmId) && _alarms.Count >= Bridge.MaximumAlarms)
                                Refused(from, "alarms-full", Bridge.MaximumAlarms + " per plugin");
                            else _alarms[alarmId] = (due, false);
                        }
                        else Refused(from, "bad-alarm", "alarm.set wants a name and a time the host can reach");
                        WebRuntime.AlarmsChanged();
                        break;
                    case "alarmClear":
                        if (root.TryGetProperty("id", out var clear) && clear.ValueKind == JsonValueKind.String) _alarms.Remove(clear.GetString()!);
                        WebRuntime.AlarmsChanged();
                        break;
                    case "keepAlive":
                        if (root.TryGetProperty("on", out var keep) && keep.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            _keepAlive = keep.GetBoolean();
                            if (!_shown && _reclaim is not null)
                            {
                                // Turning it on saves a page already counting down; turning it off starts the count again.
                                _reclaim.Stop();
                                if (!_keepAlive) _reclaim.Start();
                            }
                        }
                        else Refused(from, "bad-keepAlive", "setKeepAlive wants true or false");
                        break;
                    case "openUrl":
                        if (root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String) Open(from, url.GetString());
                        else Refused(from, "bad-link", "openUrl wants a url string");
                        break;
                    case "setSetting":
                        WriteSetting(root, from);
                        break;
                    default:
                        // A page that sends something the host does not know is a page written against another bridge.
                        Refused(from, "unknown-kind", Short(kind.GetString() ?? ""));
                        break;
                }
            }
        }
        catch (Exception) { }
    }

    // A refusal the page can see. The host never fails a message quietly: a plugin author debugging a web page has
    // nothing else to look at, and a silent drop reads as a host that ignored them on purpose.
    private void Refused(CoreWebView2? core, string code, string detail)
    {
        if (core is null) return;
        ExtensionLog.Write($"view: refused {Id} {code} {Short(detail)}");
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(new { channel = "brimdeck", kind = "error", code, detail = Short(detail) })); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }

    // Text a page wrote, shortened for a log line and for the reply: a refusal quotes back what was refused, and a
    // 2000-character URL has no business in either.
    private static string Short(string text) => text.Length <= 120 ? text : text[..120] + "…";

    // The hello a freshly loaded page sends is answered with everything it cannot know yet: the bridge the host speaks
    // and the ceilings it holds, the palette and language, whether the page is being shown, the state an earlier load
    // handed over, and any alarm that came due while no page was there to hear it.
    private void ReplyInit()
    {
        var missed = _alarms.Where(alarm => alarm.Value.Fired).Select(alarm => alarm.Key).ToList();
        foreach (var id in missed) _alarms.Remove(id);
        // The version is the host's, never a number copied into two places: a page written against a newer bridge reads
        // this and degrades instead of waiting for messages that will never come. The ceilings come from Bridge whole, so
        // the page is told the rate as it is kept.
        _pushedSettings = SettingsJson; // the init below carries it: a settings event with the same numbers is not a change
        Push("{\"channel\":\"brimdeck\",\"kind\":\"init\",\"apiVersion\":" + Bridge.Version
            + ",\"appVersion\":" + JsonSerializer.Serialize(ApplicationVersion.Current)
            + ",\"limits\":" + Bridge.LimitsJson
            + ",\"settings\":" + SettingsJson
            + ",\"phrases\":" + PhrasesJson
            + ",\"language\":\"" + (Loc.IsEnglish ? "en" : "zh")
            + "\",\"shown\":" + (_shown ? "true" : "false") + ",\"colors\":" + WebColors.Json + ",\"state\":" + (_stateJson ?? "null") + "}");
        foreach (var id in missed) Push("{\"channel\":\"brimdeck\",\"kind\":\"alarm\",\"data\":" + JsonSerializer.Serialize(id) + "}");
    }

    public void Dispose()
    {
        ExtensionLog.Write("view: extension disposed " + Id);
        _reclaim?.Stop();
        foreach (var (control, core) in Pages())
        {
            _retired.Add(control);
            try { core.WebMessageReceived -= OnWebMessage; } catch (Exception) { }
            try { control.Dispose(); } catch (Exception) { }
        }
        _pages.Clear();
        WebRuntime.Unregister(this);
    }

    // The bridge the host injects before any page script runs. It is deliberately small: a version, events for what
    // the host pushes, and four calls — state, alarms, keep-alive, links. Pages run in a plain browser too (the bridge
    // is simply absent there), which is what makes a plugin debuggable outside the app. The version and the event queue
    // come from Bridge, so the page sees the numbers the host actually enforces.
    private static readonly string Shim = $$"""
        (function () {
            if (window.brimdeck) return;
            // Held unseen from the first paint; the host fades the document in when its first navigation completes.
            // If the host never comes back to do that, the document appears by itself rather than staying invisible.
            var held = document.documentElement;
            if (held) {
                held.style.opacity = '0';
                setTimeout(function () {
                    var root = document.documentElement;
                    if (root && root.style.opacity === '0') root.style.opacity = '1';
                }, 1500);
            }
            var listeners = {}, pending = {};
            function post(message) {
                message.channel = 'brimdeck';
                try { window.chrome && window.chrome.webview && window.chrome.webview.postMessage(message); } catch (e) { }
            }
            // How many CSS pixels the page actually gets is the browser's business, not the panel's: it reports the
            // number, and the resize that follows any change of it, so the host can keep one CSS pixel one panel DIP.
            function reportScale() { post({ kind: 'scale', dpr: window.devicePixelRatio || 1, width: window.innerWidth, height: window.innerHeight }); }
            reportScale();
            window.addEventListener('resize', reportScale);
            // A reader's click is the one thing the host can be told about for real — a page that moves its own frame
            // looks, to the browser, exactly like one a person moved. So the click is read here, inside the page: an
            // anchor that does not point at the plugin's own folder leaves for the desktop browser instead of loading
            // in the panel. A link to the page's own files is the browser's business, as it always was.
            //
            // Last, not first: listening in the bubble phase is what lets the page say "I took that one" — its own
            // handler runs before this one, and its preventDefault is then visible here. A page that also stops the
            // click from reaching the document keeps the browser's own answer instead, which for a link out of the
            // plugin's folder is a cancelled navigation: nothing opens.
            document.addEventListener('click', function (event) {
                if (event.defaultPrevented || event.button !== 0) return;
                var node = event.target;
                while (node && (node.tagName || '').toUpperCase() !== 'A') node = node.parentElement;
                if (!node || typeof node.href !== 'string' || !node.href) return;
                // A javascript: href is a page's own action wearing a link's clothes, not an address anybody means to
                // visit — the host would answer every click of it with a refusal.
                if (/^javascript:/i.test(node.href)) return;
                if (node.href.indexOf(location.origin + '/') === 0) return;
                event.preventDefault();
                post({ kind: 'openUrl', url: node.href });
            });
            // The host answers hello at once, possibly before the page's own script has run. Events a kind has no
            // listener for yet are held (at most the last few) and handed over when the page subscribes.
            function emit(kind, data) {
                var list = listeners[kind];
                if (!list || !list.length) {
                    var queued = pending[kind] || (pending[kind] = []);
                    queued.push(data);
                    if (queued.length > {{Bridge.BufferedEventsPerKind}}) queued.shift();
                    return;
                }
                list.slice().forEach(function (fn) { try { fn(data); } catch (e) { } });
            }
            var api = {
                version: {{Bridge.Version}},
                limits: null,
                appVersion: '',
                settings: {},
                phrases: {},
                on: function (kind, fn) {
                    if (typeof kind !== 'string' || typeof fn !== 'function') return;
                    (listeners[kind] = listeners[kind] || []).push(fn);
                    var queued = pending[kind];
                    if (queued) { delete pending[kind]; queued.forEach(function (data) { try { fn(data); } catch (e) { } }); }
                },
                // The plugin's own words, in the language the interface is spoken in. A key the author left out reads
                // back as itself, so a missing phrase shows the author's name for it instead of a hole.
                phrase: function (key) { return api.phrases[key] !== undefined ? api.phrases[key] : key; },
                saveState: function (state) { post({ kind: 'saveState', state: state === undefined ? null : state }); },
                setKeepAlive: function (on) { post({ kind: 'keepAlive', on: !!on }); },
                // A link leaves the island: the host opens it in the desktop browser, but only an http/https address,
                // and at a rate every plugin is held to — the host cannot tell this call from a loop.
                openUrl: function (url) { if (typeof url === 'string') post({ kind: 'openUrl', url: url }); },
                // One of the controls the manifest declared. The host answers with the value it kept, which is that
                // control's own range and step applied to what was written here.
                setSetting: function (key, value) { if (typeof key === 'string') post({ kind: 'setSetting', key: key, value: value }); },
                alarm: {
                    set: function (id, at) {
                        var ms = at instanceof Date ? at.getTime() : Number(at);
                        if (typeof id !== 'string' || !id || !isFinite(ms)) return;
                        post({ kind: 'alarmSet', id: id, at: ms });
                    },
                    clear: function (id) { if (typeof id === 'string' && id) post({ kind: 'alarmClear', id: id }); }
                }
            };
            window.brimdeck = api;
            var inbox = (window.chrome && window.chrome.webview) || window;
            inbox.addEventListener('message', function (event) {
                var message = event.data;
                if (!message || message.channel !== 'brimdeck') return;
                // init carries the ceilings too, so a page can ask how much room it has rather than guessing.
                if (message.kind === 'init') {
                    api.appVersion = message.appVersion;
                    api.limits = message.limits;
                    api.colors = message.colors;
                    api.language = message.language;
                    api.settings = message.settings || {};
                    api.phrases = message.phrases || {};
                }
                // The live copies are replaced before the page hears about them: brimdeck.settings and brimdeck.phrase
                // should never answer with a value the event has already superseded.
                if (message.kind === 'settings') api.settings = message.data || {};
                if (message.kind === 'phrases') api.phrases = message.data || {};
                emit(message.kind, message.data !== undefined ? message.data : message);
            });
            post({ kind: 'hello' });
        })();
        """;
}
