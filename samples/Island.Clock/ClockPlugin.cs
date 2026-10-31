using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Clock;

// The minimal plugin, meant as the starting point for a new one: a clock page for the open panel and a small clock
// widget for the collapsed capsule. It reads nothing outside itself and uses only the contract assembly and WPF.
// Build it, put Island.Clock.dll into %LOCALAPPDATA%\BrimDeck\plugins, and press Reload in Settings > Plugins.
public sealed class ClockPlugin : IIslandExtension
{
    private ClockPage _page = null!;
    private ClockCompact _compact = null!;

    public string Id => "sample-clock";
    public string TitleZh => "时钟";
    public string TitleEn => "Clock";
    public string Glyph => "M 12,3 A 9,9 0 1 1 11.99,3 M 12,7 V 12.5 L 15.5,14.5";

    public void Initialize(IIslandHost host)
    {
        _page = new ClockPage();
        _compact = new ClockCompact();
        host.RegisterPage(_page);
        host.RegisterCompact(_compact);
        // A theme switch recolors in place; the page keeps its state because nothing is rebuilt.
        host.ColorsChanged += () =>
        {
            _page.Recolor(host.Colors);
            _compact.Recolor(host.Colors);
        };
    }
}

// The page: the time large in the middle, the date under it, ticking once a second while some panel shows the page.
internal sealed class ClockPage : IIslandPage
{
    private readonly TextBlock _time = new() { FontSize = 44, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _date = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

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
        _time.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        _date.Text = now.ToString("yyyy-MM-dd dddd", CultureInfo.CurrentCulture);
    }
}

// The capsule's widget: the time small in the collapsed row, ticking once a second while the island sits there.
internal sealed class ClockCompact : IIslandCompact
{
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

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
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(_text.DesiredSize.Width);
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Recolor(IslandColors colors)
        => _text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Secondary));

    private void Paint(object? sender = null, EventArgs? args = null)
        => _text.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
}
