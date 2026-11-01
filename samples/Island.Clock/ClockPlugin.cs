using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Clock;

// The minimal plugin, meant as the starting point for a new one: a clock page for the open panel, a small clock
// widget for the collapsed capsule and a settings page of its own in the settings window, built through the host's
// control factory (IIslandHost.Controls) and persisted through the host's settings store. It reads nothing outside
// itself and uses only the contract assembly and WPF.
// Build it, put Island.Clock.dll into %LOCALAPPDATA%\BrimDeck\plugins, and press Reload in Settings > Plugins.
public sealed class ClockPlugin : IIslandExtension
{
    private ClockPage _page = null!;
    private ClockCompact _compact = null!;
    private IIslandHost _host = null!;

    public string Id => "sample-clock";
    public string TitleZh => "时钟";
    public string TitleEn => "Clock";
    public string Author => "TakaraMiyuki";
    public string Description => "样例插件：时钟页面 + 胶囊部件，演示设置页与控件工厂。";
    public string Glyph => "M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 V 12.5 L 15.5,14.5";

    // The settings the page and the widget read; the settings view writes them and the host persists them.
    private ClockSettings _settings = new();

    public void Initialize(IIslandHost host)
    {
        _host = host;
        _settings = ClockSettings.Load(host);
        _page = new ClockPage(_settings);
        _compact = new ClockCompact(_settings);
        host.RegisterPage(_page);
        host.RegisterCompact(_compact);
        // A theme switch recolors in place; the page keeps its state because nothing is rebuilt.
        host.ColorsChanged += () =>
        {
            _page.Recolor(host.Colors);
            _compact.Recolor(host.Colors);
        };
    }

    // The plugin's own settings page: three switches through the host's control factory, written through to the
    // page and the widget the moment they move, and persisted through the host's store.
    public FrameworkElement? CreateSettingsView(IslandColors colors)
    {
        var controls = _host.Controls;
        var showSeconds = controls.Toggle("显示秒", _settings.ShowSeconds, on => { _settings.ShowSeconds = on; _settings.Save(_host); _page.OnSettingsChanged(); });
        var showDate = controls.Toggle("显示日期", _settings.ShowDate, on => { _settings.ShowDate = on; _settings.Save(_host); _page.OnSettingsChanged(); });
        var hour24 = controls.Combo("小时制", ["24 小时制", "12 小时制"], _settings.Hour24 ? 0 : 1, index =>
        {
            _settings.Hour24 = index == 0; _settings.Save(_host); _page.OnSettingsChanged();
        });
        return controls.Group("显示", showSeconds, showDate, hour24);
    }
}

// The clock's own settings, read and written through the host's store; defaults match what the page showed before it
// had settings at all.
internal sealed class ClockSettings
{
    public bool ShowSeconds = true;
    public bool ShowDate = true;
    public bool Hour24 = true;

    public static ClockSettings Load(IIslandHost host)
    {
        var settings = new ClockSettings();
        if (host.GetSetting("seconds") is { Length: > 0 } seconds) settings.ShowSeconds = seconds == "1";
        if (host.GetSetting("date") is { Length: > 0 } date) settings.ShowDate = date == "1";
        if (host.GetSetting("hour24") is { Length: > 0 } hour) settings.Hour24 = hour == "1";
        return settings;
    }

    public void Save(IIslandHost host)
    {
        host.SetSetting("seconds", ShowSeconds ? "1" : "0");
        host.SetSetting("date", ShowDate ? "1" : "0");
        host.SetSetting("hour24", Hour24 ? "1" : "0");
    }
}

// The page: the time large in the middle, the date under it, ticking once a second while some panel shows the page.
// The settings (seconds, date, hour format) ride in and are read at every tick.
internal sealed class ClockPage : IIslandPage
{
    private readonly ClockSettings _settings;
    private readonly TextBlock _time = new() { FontSize = 44, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClockPage(ClockSettings settings) => _settings = settings;

    public string Id => "clock";
    public string TitleZh => "时钟";
    public string TitleEn => "Clock";
    public string Glyph => "M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 V 12.5 L 15.5,14.5";
    public (double Width, double Height)? PreferredSize => (440, 220);

    public FrameworkElement CreateView(IslandColors colors)
    {
        var grid = new Grid { RowDefinitions = { new RowDefinition(), new RowDefinition() }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(_time, 0); Grid.SetRow(_date, 1);
        grid.Children.Add(_time);
        grid.Children.Add(_date);
        Recolor(colors);
        Tick(true);
        return grid;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void SetDemand(bool shown) => Tick(shown);

    // The settings moved: repaint now (the format may have changed between ticks) and restack the date.
    public void OnSettingsChanged() => Paint();

    public void Recolor(IslandColors colors)
    {
        _time.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Primary));
        _date.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Tertiary));
    }

    private void Tick(bool on)
    {
        if (on == _timer.IsEnabled) return;
        if (on) { Paint(); _timer.Tick += Paint; _timer.Start(); }
        else { _timer.Tick -= Paint; _timer.Stop(); }
    }

    private void Paint(object? sender = null, EventArgs? args = null)
    {
        var now = DateTime.Now;
        var format = _settings.Hour24 ? "HH:mm:ss" : "hh:mm:ss tt";
        _time.Text = now.ToString(_settings.ShowSeconds ? format : format.Replace(":ss", ""), CultureInfo.InvariantCulture);
        _date.Text = _settings.ShowDate ? now.ToString("yyyy-MM-dd dddd", CultureInfo.CurrentCulture) : "";
        _date.Visibility = _settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
    }
}

// The capsule's widget: the time small in the collapsed row, ticking once a second while the island sits there.
internal sealed class ClockCompact : IIslandCompact
{
    private readonly ClockSettings _settings;
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ClockCompact(ClockSettings settings) => _settings = settings;

    public string Id => "clock";
    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        Paint();
        _timer.Tick += Paint;
        _timer.Start();
        return _text;
    }

    // "00:00" measured once: the width is stable, so the row does not breathe with the seconds.
    public double DesiredWidth(double availableHeight)
    {
        var keep = _text.Text;
        _text.Text = "00:00";
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(_text.DesiredSize.Width);
        _text.Text = keep;
        return width + 8;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Recolor(IslandColors colors)
        => _text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Secondary));

    private void Paint(object? sender = null, EventArgs? args = null)
        => _text.Text = DateTime.Now.ToString(_settings.Hour24 ? "HH:mm" : "hh:mm tt", CultureInfo.InvariantCulture);
}
