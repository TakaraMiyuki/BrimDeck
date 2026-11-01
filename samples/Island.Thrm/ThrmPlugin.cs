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
// temperature, GPU temperature, fan speed) with the card's mark and title, the figure large with its unit small and
// an arc filled to the figure's share. The collapsed capsule carries three readings — CPU temperature, GPU temperature
// and fan speed — as the system monitor's own readings do: a small ring with its initial inside, the figure beside it.
// The numbers come from the THRM bridge (thrm-bridge.py, the local HTTP service on 127.0.0.1:27391): the plugin polls
// it every two seconds and writes into the views, so nothing is recreated while the user reads it.
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

// The levels the cooler software colors its readings by: temperature healthy at 70 and below, warm above 70 up to 90,
// hot above 90; fan speed in four bands at 1900, 2700 and 3500. The plugin owns these colors — they mean the same at
// any panel theme — and the figures, arcs and rings all take them.
internal static class ThrmLevels
{
    public const string Green = "#34C759", Blue = "#0A84FF", Purple = "#AF52DE", Orange = "#FF9F0A", Red = "#FF3B30";

    public static string Temperature(double celsius) => celsius > 90 ? Red : celsius > 70 ? Orange : Green;

    public static string Fan(double rpm) => rpm > 3500 ? Orange : rpm > 2700 ? Purple : rpm > 1900 ? Blue : Green;
}

// The page, shaped as the cooler software's monitor panel: a row of cards, each with its own mark and title, an arc
// filled to the figure's share, the figure large with its unit small beside it, and a second line under it.
internal sealed class ThrmPage : IIslandPage
{
    private readonly GaugeCard _cpu;
    private readonly GaugeCard _gpu;
    private readonly GaugeCard _fan;
    private readonly TextBlock _state = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
    private IslandColors _colors = Initial();

    public ThrmPage()
    {
        _cpu = new GaugeCard("CPU 温度", "°C", 0, 100, Icons.Cpu);
        _gpu = new GaugeCard("GPU 温度", "°C", 0, 100, Icons.Gpu);
        _fan = new GaugeCard("风扇转速", "RPM", 0, 5000, Icons.Fan);
    }

    private static IslandColors Initial() => new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

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
        var cards = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(14, 8, 14, 0) };
        cards.Children.Add(_cpu.Root);
        cards.Children.Add(_gpu.Root);
        cards.Children.Add(_fan.Root);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(cards);
        _state.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_state);
        return stack;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Update(ThrmSample? sample)
    {
        if (sample is null)
        {
            _cpu.Show(0, "--", "--", ThrmLevels.Green, _colors.Tertiary);
            _gpu.Show(0, "--", "--", ThrmLevels.Green, _colors.Tertiary);
            _fan.Show(0, "--", "桥未运行", ThrmLevels.Green, _colors.Tertiary);
            _state.Text = "";
            return;
        }
        // The arc carries the level's color; the figures stay in the palette's primary, as the panel's own numbers do.
        _cpu.Show(sample.CpuTemp, $"{sample.CpuTemp:0}", $"{sample.CpuFan:0} RPM", ThrmLevels.Temperature(sample.CpuTemp), _colors.Primary);
        _gpu.Show(sample.GpuTemp, $"{sample.GpuTemp:0}", $"{sample.GpuFan:0} RPM", ThrmLevels.Temperature(sample.GpuTemp), _colors.Primary);
        _fan.Show(sample.CpuFan, $"{sample.CpuFan:0}", $"目标 {sample.CpuFan:0} · {sample.Mode}", ThrmLevels.Fan(sample.CpuFan), _colors.Primary);
        _state.Text = "挡位 / gear: " + sample.Mode;
    }

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        foreach (var card in new[] { _cpu, _gpu, _fan }) card.ApplyTheme(colors);
        _state.Foreground = Brush(colors.Tertiary);
    }

    internal static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

// The cards' marks, on the 24 grid the contract's glyphs use: a chip for the CPU, a display for the GPU, a fan for the
// speed.
internal static class Icons
{
    public const string Cpu =
        "M 7,7 H 17 V 17 H 7 Z M 10,10 H 14 V 14 H 10 Z " +
        "M 9,7 V 4 M 12,7 V 4 M 15,7 V 4 M 9,17 V 20 M 12,17 V 20 M 15,17 V 20 " +
        "M 7,9 H 4 M 7,12 H 4 M 7,15 H 4 M 17,9 H 20 M 17,12 H 20 M 17,15 H 20";
    public const string Gpu =
        "M 3,6 H 21 V 15 H 3 Z M 6,9 H 18 M 6,12 H 12 M 7,15 V 18 M 17,15 V 18";
    // Four blades about the hub, as the cooler software draws its fan: each a rounded leaf pointing out.
    public const string Fan =
        "M 12,9.5 C 10.6,8.2 10.6,5.6 12,3.5 C 13.4,5.6 13.4,8.2 12,9.5 Z " +
        "M 14.5,12 C 15.8,10.6 18.4,10.6 20.5,12 C 18.4,13.4 15.8,13.4 14.5,12 Z " +
        "M 12,14.5 C 13.4,15.8 13.4,18.4 12,20.5 C 10.6,18.4 10.6,15.8 12,14.5 Z " +
        "M 9.5,12 C 8.2,13.4 5.6,13.4 3.5,12 C 5.6,10.6 8.2,10.6 9.5,12 Z " +
        "M 13.2,12 A 1.2,1.2 0 1 1 13.19,12";
}

// One gauge card: its mark and title across the top, a half-circle arc filled to the reading's share, the figure large
// with its unit small at its foot, and a second line under that.
internal sealed class GaugeCard
{
    private readonly string _unit;
    private readonly double _min, _max;
    private readonly TextBlock _title = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
    private readonly TextBlock _value = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unitText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 0, 6) };
    private readonly TextBlock _sub = new() { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly System.Windows.Shapes.Path _track = new() { StrokeThickness = 5.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly System.Windows.Shapes.Path _fill = new() { StrokeThickness = 5.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly System.Windows.Shapes.Path _icon;
    private readonly Border _root;

    private const double ArcWidth = 150, ArcHeight = 82, ArcStroke = 5.5;
    private static readonly Geometry ArcGeometry = Arc();

    public FrameworkElement Root => _root;

    public GaugeCard(string title, string unit, double min, double max, string icon)
    {
        _unit = unit; _min = min; _max = max;
        _title.Text = title;
        _icon = new System.Windows.Shapes.Path
        {
            Data = Parse(icon), Width = 13, Height = 13, Stretch = Stretch.Uniform,
            StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(_icon); head.Children.Add(_title);

        _track.Data = ArcGeometry;
        _fill.Data = ArcGeometry;
        var arc = new Canvas { Width = ArcWidth, Height = ArcHeight, HorizontalAlignment = HorizontalAlignment.Center };
        arc.Children.Add(_track); arc.Children.Add(_fill);

        var figure = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        figure.Children.Add(_value); figure.Children.Add(_unitText);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(arc);
        figure.Margin = new Thickness(0, -40, 0, 0);
        stack.Children.Add(figure);
        _sub.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_sub);
        _root = new Border { Child = stack, Padding = new Thickness(10, 12, 10, 14), Margin = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(12) };
    }

    private static Geometry Parse(string data)
    {
        try { return System.Windows.Media.Geometry.Parse(data); } catch (Exception) { return System.Windows.Media.Geometry.Empty; }
    }

    // The half circle, round caps, drawn from the left end clockwise over the top.
    private static Geometry Arc()
    {
        double r = ArcWidth / 2 - ArcStroke, cx = ArcWidth / 2, cy = ArcHeight - 8;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cx - r, cy), false, false);
            ctx.ArcTo(new Point(cx + r, cy), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    // The figures and the arc take the level's color; the card's chrome follows the panel theme.
    public void Show(double value, string figure, string second, string levelColor, string figureColor)
    {
        _value.Text = figure;
        _unitText.Text = _unit;
        _sub.Text = second;
        _value.Foreground = ThrmPage.Brush(figureColor);
        _unitText.Foreground = ThrmPage.Brush(figureColor);
        _fill.Stroke = ThrmPage.Brush(levelColor);
        double share = _max > _min ? Math.Clamp((value - _min) / (_max - _min), 0, 1) : 0;
        // StrokeDashArray counts multiples of the stroke width, not pixels.
        double span = Math.PI * (ArcWidth / 2 - ArcStroke) / ArcStroke;
        _fill.StrokeDashArray = [span * share, span * 2];
    }

    public void ApplyTheme(IslandColors colors)
    {
        _title.Foreground = ThrmPage.Brush(colors.Secondary);
        _icon.Stroke = ThrmPage.Brush(colors.Secondary);
        _sub.Foreground = ThrmPage.Brush(colors.Tertiary);
        _track.Stroke = ThrmPage.Brush(colors.Fill);
        _root.Background = ThrmPage.Brush(colors.Track);
    }
}

// The capsule's widget, shaped as the system monitor's own readings: a small ring with the reading's initial inside and
// the figure beside it. Three readings — CPU temperature (C), GPU temperature (G) and fan speed (R) — in the size and
// spacing of the built-in readings.
internal sealed class ThrmCompact : IIslandCompact
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Reading _cpu = new("C", 0, 100);
    private readonly Reading _gpu = new("G", 0, 100);
    private readonly Reading _fan = new("R", 0, 5000);
    private IslandColors _colors = Initial();

    private static IslandColors Initial() => new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

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
        return Math.Ceiling(_row.DesiredSize.Width) + 6;
    }

    public void OnColorsChanged(IslandColors colors)
    {
        _colors = colors;
        _cpu.ApplyTheme(colors); _gpu.ApplyTheme(colors); _fan.ApplyTheme(colors);
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
            _cpu.Show("--", 0, _colors.Tertiary, _colors.Fill);
            _gpu.Show("--", 0, _colors.Tertiary, _colors.Fill);
            _fan.Show("--", 0, _colors.Tertiary, _colors.Fill);
            return;
        }
        // The figures take the palette's shade (the row's numbers stay one color, as the built-in readings' do); the
        // levels show in the ring alone.
        _cpu.Show($"{sample.CpuTemp:0}°", sample.CpuTemp, _colors.Secondary, ThrmLevels.Temperature(sample.CpuTemp));
        _gpu.Show($"{sample.GpuTemp:0}°", sample.GpuTemp, _colors.Secondary, ThrmLevels.Temperature(sample.GpuTemp));
        _fan.Show($"{sample.CpuFan:0}", sample.CpuFan, _colors.Secondary, ThrmLevels.Fan(sample.CpuFan));
    }

    // One reading: the built-ins' ring (18 DIP, 2 DIP stroke) with its initial inside, and the figure beside it.
    private sealed class Reading(string initial, double min, double max)
    {
        // The built-in system readings' ring: 14 DIP across with a 2.2 DIP stroke.
        private const double Size = 14, Thickness = 2.2;
        private static readonly Geometry RingGeometry = Ring();
        private readonly System.Windows.Shapes.Path _track = new() { Data = RingGeometry, StrokeThickness = Thickness, Stretch = Stretch.None };
        private readonly System.Windows.Shapes.Path _fill = new() { Data = RingGeometry, StrokeThickness = Thickness, Stretch = Stretch.None };
        private readonly TextBlock _initial = new() { FontSize = 7, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _text = new()
        {
            FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 10, 0), TextWrapping = TextWrapping.NoWrap
        };
        private FrameworkElement? _root;

        public FrameworkElement Root => _root ??= Build();

        private FrameworkElement Build()
        {
            var ring = new Grid { Width = Size, Height = Size, VerticalAlignment = VerticalAlignment.Center };
            ring.Children.Add(_track); ring.Children.Add(_fill);
            _initial.Text = initial;
            ring.Children.Add(_initial);
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(ring); row.Children.Add(_text);
            return row;
        }

        public void ApplyTheme(IslandColors colors)
        {
            _track.Stroke = ThrmPage.Brush(colors.Fill);
            _initial.Foreground = ThrmPage.Brush(colors.Secondary);
        }

        public void Show(string figure, double value, string textColor, string ringColor)
        {
            _text.Text = figure;
            _text.Foreground = ThrmPage.Brush(textColor);
            double share = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
            // StrokeDashArray counts multiples of the stroke width, not pixels.
            double span = Math.PI * 2 * (Size / 2 - Thickness / 2) / Thickness;
            _fill.StrokeDashArray = [span * share, span * 2];
            _fill.Stroke = ThrmPage.Brush(ringColor);
        }

        public void MeasurePreview(string preview)
        {
            var keep = _text.Text;
            _text.Text = preview;
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
