using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Thrm;

// The THRM cooler's read-out, in the shape of the cooler software's own monitor panel: a row of gauge cards (CPU
// temperature, GPU temperature, fan speed) with an arc, the figure large in the middle and the second reading under
// it. The collapsed capsule carries the temperatures and the fan speed small, beside the quota rings the island
// already shows. The numbers come from the THRM bridge (thrm-bridge.py, the local HTTP service on 127.0.0.1:27391):
// the plugin polls it every two seconds and writes into the views, so nothing is recreated while the user reads it.
public sealed class ThrmPlugin : IIslandExtension
{
    private readonly ThrmPage _page = new();
    private readonly ThrmCompact _compact = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public string Id => "thrm-cooler";
    public string TitleZh => "散热器";
    public string TitleEn => "Cooler";
    public string Author => "TakaraMiyuki";
    public string Description => "散热器状态：CPU/GPU 温度、功耗与风扇转速，经本地桥轮询。";
    // A three-blade fan in the round, soft outline the cooler software draws: blades as flat lenses about a hub.
    public string Glyph =>
        "M 12,10.3 A 3.9,1.95 -90 0 1 12,2.5 A 3.9,1.95 -90 0 1 12,10.3 Z " +
        "M 13.47,12.85 A 3.9,1.95 30 0 1 20.23,16.75 A 3.9,1.95 30 0 1 13.47,12.85 Z " +
        "M 10.53,12.85 A 3.9,1.95 150 0 1 3.77,16.75 A 3.9,1.95 150 0 1 10.53,12.85 Z " +
        "M 13.3,12 A 1.3,1.3 0 1 1 13.29,12";

    public void Initialize(IIslandHost host)
    {
        host.RegisterPage(_page);
        host.RegisterCompact(_compact);
        // One poll feeds both surfaces, on the UI thread.
        _timer.Tick += (_, _) =>
        {
            var sample = ThrmBridge.Read();
            _page.Update(sample);
            _compact.Update(sample);
        };
        host.ColorsChanged += () =>
        {
            _page.Recolor(host.Colors);
            _compact.OnColorsChanged(host.Colors);
        };
        // The first read right away, so the page is not blank for two seconds.
        var first = ThrmBridge.Read();
        _page.Update(first);
        _compact.Update(first);
        _timer.Start();
    }
}

// One bridge sample, with what the page and the widget read out of it.
internal sealed record ThrmSample(double CpuTemp, double GpuTemp, double CpuPower, double GpuPower, double CpuFan, double GpuFan, string Mode);

internal static class ThrmBridge
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    public const string Url = "http://127.0.0.1:27391/sample";

    public static ThrmSample? Read()
    {
        try
        {
            using var document = JsonDocument.Parse(Http.GetStringAsync(Url).GetAwaiter().GetResult());
            var root = document.RootElement;
            if (!root.TryGetProperty("bridgeOk", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            return new ThrmSample(
                Number(root, "cpuTemp"), Number(root, "gpuTemp"), Number(root, "cpuPower"), Number(root, "gpuPower"),
                Number(root, "cpuFanRpm"), Number(root, "gpuFanRpm"),
                root.TryGetProperty("workMode", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() ?? "" : "");
        }
        catch (Exception) { return null; }
    }

    private static double Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
}

// The page, shaped as the cooler software's monitor panel: a row of cards, each an arc gauge with the figure large and
// the second reading under it, and the bridge's state line below. The arcs fill with the reading and take the level's
// color; the figures stay in the palette's primary.
internal sealed class ThrmPage : IIslandPage
{
    private readonly GaugeCard _cpu;
    private readonly GaugeCard _gpu;
    private readonly GaugeCard _fan;
    private readonly TextBlock _state = new() { FontSize = 11 };
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public ThrmPage()
    {
        _cpu = new GaugeCard("CPU 温度", "°C", 0, 100);
        _gpu = new GaugeCard("GPU 温度", "°C", 0, 100);
        _fan = new GaugeCard("风扇转速", "RPM", 0, 4000);
    }

    public string Id => "cooler";
    public string TitleZh => "散热器";
    public string TitleEn => "Cooler";
    public string Glyph =>
        "M 12,10.3 A 3.9,1.95 -90 0 1 12,2.5 A 3.9,1.95 -90 0 1 12,10.3 Z " +
        "M 13.47,12.85 A 3.9,1.95 30 0 1 20.23,16.75 A 3.9,1.95 30 0 1 13.47,12.85 Z " +
        "M 10.53,12.85 A 3.9,1.95 150 0 1 3.77,16.75 A 3.9,1.95 150 0 1 10.53,12.85 Z " +
        "M 13.3,12 A 1.3,1.3 0 1 1 13.29,12";
    public (double Width, double Height)? PreferredSize => (620, 250);

    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        var cards = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(12, 6, 12, 0) };
        cards.Children.Add(_cpu.Root);
        cards.Children.Add(_gpu.Root);
        cards.Children.Add(_fan.Root);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(cards);
        _state.Margin = new Thickness(20, 8, 20, 0);
        stack.Children.Add(_state);
        return stack;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Update(ThrmSample? sample)
    {
        if (sample is null)
        {
            _cpu.Set(0, "--", "桥未运行");
            _gpu.Set(0, "--", "");
            _fan.Set(0, "--", "");
            _state.Text = "";
            return;
        }
        _cpu.Set(sample.CpuTemp, $"{sample.CpuTemp:0}", $"{sample.CpuFan:0} RPM");
        _gpu.Set(sample.GpuTemp, $"{sample.GpuTemp:0}", $"{sample.GpuFan:0} RPM");
        _fan.Set(sample.CpuFan, $"{sample.CpuFan:0}", $"目标 {sample.CpuFan:0} · {sample.Mode}");
        _state.Text = "挡位 / gear: " + sample.Mode;
    }

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        foreach (var card in new[] { _cpu, _gpu, _fan }) card.Recolor(colors);
        _state.Foreground = Brush(colors.Tertiary);
    }

    // A temperature's level color, as the panel's readings take them.
    internal static string LevelColor(IslandColors colors, double value, double warning, double critical)
        => value >= critical ? colors.Critical : value >= warning ? colors.Warning : colors.Primary;

    internal static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

// One gauge card: the label at the top with its small glyph, a 180-degree arc filled to the reading's share, the
// figure large in the middle of the arc and a second line under it.
internal sealed class GaugeCard
{
    private readonly string _label, _unit;
    private readonly double _min, _max;
    private readonly TextBlock _title = new() { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _value = new() { FontSize = 27, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _sub = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly System.Windows.Shapes.Path _track = new() { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly System.Windows.Shapes.Path _fill = new() { StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Border _root;

    private const double ArcWidth = 132, ArcHeight = 74;
    private static readonly Geometry ArcGeometry = Arc();

    public FrameworkElement Root => _root;

    public GaugeCard(string label, string unit, double min, double max)
    {
        _label = label; _unit = unit; _min = min; _max = max;
        _title.Text = label;
        _track.Data = ArcGeometry; _track.Stretch = Stretch.None;
        _fill.Data = ArcGeometry; _fill.Stretch = Stretch.None;
        var arc = new Canvas { Width = ArcWidth, Height = ArcHeight, HorizontalAlignment = HorizontalAlignment.Center };
        arc.Children.Add(_track); arc.Children.Add(_fill);
        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(arc);
        _value.Margin = new Thickness(0, -34, 0, 0);
        stack.Children.Add(_value);
        _sub.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_sub);
        _root = new Border { Child = stack, Padding = new Thickness(10, 12, 10, 12), Margin = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(12) };
    }

    // The arc: a half circle of the card's width, round caps at both ends, drawn from the left end.
    private static Geometry Arc()
    {
        double r = ArcWidth / 2 - 4, cx = ArcWidth / 2, cy = ArcHeight - 6;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx - r, cy), false, false);
            ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    public void Set(double value, string figure, string second)
    {
        _value.Text = figure + _unit;
        _sub.Text = second;
        // The fill is the arc up to the reading's share, as a stroke dash of that length.
        // StrokeDashArray counts multiples of the stroke width, not pixels.
        double span = Math.PI * (ArcWidth / 2 - 4) / 5;
        double share = _max > _min ? Math.Clamp((value - _min) / (_max - _min), 0, 1) : 0;
        _fill.StrokeDashArray = [span * share, span * 2];
        _fill.StrokeDashOffset = 0;
        _current = value;
        if (_colors is { } colors) Apply(colors);
    }

    private IslandColors? _colors;
    private double _current;

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        Apply(colors);
    }

    // The arc and the figure take the level's color: temperatures by the panel's warning and critical levels, the fan
    // speed in the palette's primary throughout (a speed has no warning level).
    private void Apply(IslandColors colors)
    {
        _title.Foreground = ThrmPage.Brush(colors.Secondary);
        _sub.Foreground = ThrmPage.Brush(colors.Tertiary);
        string level = _label.Contains("温度")
            ? ThrmPage.LevelColor(colors, _current, 75, 88)
            : colors.Primary;
        _value.Foreground = ThrmPage.Brush(level);
        _track.Stroke = ThrmPage.Brush(colors.Fill);
        _fill.Stroke = ThrmPage.Brush(level);
        _root.Background = ThrmPage.Brush(colors.Track);
    }
}

// The capsule's widget, shaped exactly as the system monitor's own readings: one small ring per figure, each filled to
// its share and level-colored, with the figure beside it. Three rings here — CPU temperature, GPU temperature and fan
// speed — so the cooler's read-out sits in the row the same way the built-in readings do.
internal sealed class ThrmCompact : IIslandCompact
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Reading _cpu = new(0, 100, "");
    private readonly Reading _gpu = new(0, 100, "");
    private readonly Reading _fan = new(0, 4000, "");
    private IslandColors _colors = InitialColors();

    private static IslandColors InitialColors()
        => new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public ThrmCompact()
    {
        _row.Children.Add(_cpu.Root);
        _row.Children.Add(_gpu.Root);
        _row.Children.Add(_fan.Root);
    }

    public string Id => "temps";
    public FrameworkElement CreateView(IslandColors colors)
    {
        OnColorsChanged(colors);
        return _row;
    }

    // The whole block measured once, so the row does not breathe with the numbers.
    public double DesiredWidth(double availableHeight)
    {
        _cpu.MeasurePreview("88°"); _gpu.MeasurePreview("88°"); _fan.MeasurePreview("8888");
        _row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(_row.DesiredSize.Width);
        return width + 6;
    }

    public void OnColorsChanged(IslandColors colors)
    {
        _colors = colors;
        _cpu.Apply(colors); _gpu.Apply(colors); _fan.Apply(colors);
        Paint();
    }

    public void Update(ThrmSample? sample)
    {
        _sample = sample;
        Paint();
    }

    private ThrmSample? _sample;

    private void Paint()
    {
        if (_sample is not { } sample)
        {
            _cpu.Show("--", 0, _colors.Tertiary);
            _gpu.Show("--", 0, _colors.Tertiary);
            _fan.Show("--", 0, _colors.Tertiary);
            return;
        }
        // Each ring fills to its own share of its range; temperatures take the panel's warning and critical levels.
        _cpu.Show($"{sample.CpuTemp:0}°", sample.CpuTemp, ThrmPage.LevelColor(_colors, sample.CpuTemp, 75, 88));
        _gpu.Show($"{sample.GpuTemp:0}°", sample.GpuTemp, ThrmPage.LevelColor(_colors, sample.GpuTemp, 75, 88));
        _fan.Show($"{sample.CpuFan:0}", sample.CpuFan, _colors.Primary);
    }

    // One reading: the built-ins' small ring (18 DIP, 2 DIP stroke) and its figure beside it.
    private sealed class Reading(double min, double max, string suffix)
    {
        private const double Size = 18, Thickness = 2;
        private static readonly Geometry RingGeometry = Ring();
        private readonly System.Windows.Shapes.Path _track = new() { Data = RingGeometry, StrokeThickness = Thickness, Stretch = Stretch.None };
        private readonly System.Windows.Shapes.Path _fill = new() { Data = RingGeometry, StrokeThickness = Thickness, Stretch = Stretch.None };
        private readonly TextBlock _text = new()
        {
            FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 10, 0), TextWrapping = TextWrapping.NoWrap
        };
        private FrameworkElement? _root;

        public FrameworkElement Root => _root ??= Build();

        private FrameworkElement Build()
        {
            var canvas = new Canvas { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            canvas.Children.Add(_track);
            canvas.Children.Add(_fill);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(canvas);
            row.Children.Add(_text);
            return row;
        }

        public void Apply(IslandColors colors) => _track.Stroke = ThrmPage.Brush(colors.Fill);

        public void Show(string figure, double value, string color)
        {
            _text.Text = figure + suffix;
            _text.Foreground = ThrmPage.Brush(color);
            double share = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
            // StrokeDashArray counts multiples of the stroke width, not pixels.
            double span = Math.PI * 2 * (Size / 2 - Thickness / 2) / Thickness;
            _fill.StrokeDashArray = [span * share, span * 2];
            _fill.Stroke = ThrmPage.Brush(color);
        }

        public void MeasurePreview(string preview)
        {
            var keep = _text.Text;
            _text.Text = preview + suffix;
            _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _text.Text = keep;
        }

        // A full circle from twelve o'clock, round caps.
        private static Geometry Ring()
        {
            double r = Size / 2 - Thickness / 2, c = Size / 2;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), false, false);
                ctx.ArcTo(new Point(c, c + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(new Point(c, c - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();
            return geometry;
        }
    }
}
