using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Thrm;

// The THRM cooler's read-out: temperatures, power and fan speed for the CPU and the GPU, on a page of its own and as a
// small pair of readings on the capsule. The numbers come from the THRM bridge (thrm-bridge.py, the local HTTP service
// on 127.0.0.1:27391 that the web plugin used before this): the plugin polls it every two seconds and writes into
// TextBlocks, so nothing is recreated while the user reads it. Nothing here reads anything else.
//
// When the bridge is not running the page says so instead of showing stale numbers, and the widget shows nothing.
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
    public string Glyph => "M 12,8.8 C 10.9,8.6 10.3,7.4 10.8,6 C 11.2,4.9 12,4.1 12,2.8 C 12,4.1 12.8,4.9 13.2,6 C 13.7,7.4 13.1,8.6 12,8.8 M 15.2,12 C 15.4,10.9 16.6,10.3 18,10.8 C 19.1,11.2 19.9,12 21.2,12 C 19.9,12 19,12.8 18,13.2 C 16.6,13.7 15.4,13.1 15.2,12 M 12,15.2 C 13.1,15.4 13.7,16.6 13.2,18 C 12.8,19.1 12,19.9 12,21.2 C 12,19.9 11.2,19.1 10.8,18 C 10.3,16.6 10.9,15.4 12,15.2 M 8.8,12 C 8.6,13.1 7.4,13.7 6,13.2 C 4.9,12.8 4.1,12 2.8,12 C 4.1,12 5,11.2 6,10.8 C 7.4,10.3 8.6,10.9 8.8,12 M 13.4,12 A 1.4,1.4 0 1 1 13.39,12";

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
        _page.Update(ThrmBridge.Read());
        _compact.Update(ThrmBridge.Read());
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

// The page: two chip cards (CPU and GPU), each showing temperature large with power and fan under it, and the bridge's
// state line under them. Colored by the palette: temperature by level (normal/warning/critical), power and fan in the
// secondary shade.
internal sealed class ThrmPage : IIslandPage
{
    private readonly TextBlock _cpuTemp = new() { FontSize = 34, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _cpuSub = new() { FontSize = 11.5 };
    private readonly TextBlock _gpuTemp = new() { FontSize = 34, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _gpuSub = new() { FontSize = 11.5 };
    private readonly TextBlock _state = new() { FontSize = 11 };
    private readonly TextBlock _title = new() { FontSize = 11 };
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public string Id => "cooler";
    public string TitleZh => "散热器";
    public string TitleEn => "Cooler";
    public string Glyph => "M 12,8.8 C 10.9,8.6 10.3,7.4 10.8,6 C 11.2,4.9 12,4.1 12,2.8 C 12,4.1 12.8,4.9 13.2,6 C 13.7,7.4 13.1,8.6 12,8.8 M 15.2,12 C 15.4,10.9 16.6,10.3 18,10.8 C 19.1,11.2 19.9,12 21.2,12 C 19.9,12 19,12.8 18,13.2 C 16.6,13.7 15.4,13.1 15.2,12 M 12,15.2 C 13.1,15.4 13.7,16.6 13.2,18 C 12.8,19.1 12,19.9 12,21.2 C 12,19.9 11.2,19.1 10.8,18 C 10.3,16.6 10.9,15.4 12,15.2 M 8.8,12 C 8.6,13.1 7.4,13.7 6,13.2 C 4.9,12.8 4.1,12 2.8,12 C 4.1,12 5,11.2 6,10.8 C 7.4,10.3 8.6,10.9 8.8,12 M 13.4,12 A 1.4,1.4 0 1 1 13.39,12";
    public (double Width, double Height)? PreferredSize => (520, 240);

    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 8, 20, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cpuCard = Card("CPU", _cpuTemp, _cpuSub);
        Grid.SetColumn(cpuCard, 0);
        grid.Children.Add(cpuCard);
        var gpuCard = Card("GPU", _gpuTemp, _gpuSub);
        Grid.SetColumn(gpuCard, 1);
        grid.Children.Add(gpuCard);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(grid);
        _state.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_state);
        return stack;
    }

    // One chip card: the chip name small above, the temperature large, power and fan speed under it.
    private Border Card(string chip, TextBlock temp, TextBlock sub)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = chip, FontSize = 11, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_colors.Secondary)), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(temp);
        stack.Children.Add(sub);
        return new Border
        {
            Child = stack, Margin = new Thickness(0, 0, 16, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12)
        };
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Update(ThrmSample? sample)
    {
        if (sample is null)
        {
            _cpuTemp.Foreground = Brush(_colors.Warning);
            _cpuTemp.Text = "--°C";
            _cpuSub.Text = "散热器桥未运行";
            _gpuTemp.Text = "";
            _gpuSub.Text = "";
            _state.Text = "";
            return;
        }
        _cpuTemp.Text = $"{sample.CpuTemp:0}°C";
        _cpuSub.Text = $"{sample.CpuPower:0.#} W · {sample.CpuFan:0} rpm";
        _gpuTemp.Text = $"{sample.GpuTemp:0}°C";
        _gpuSub.Text = $"{sample.GpuPower:0.#} W · {sample.GpuFan:0} rpm";
        _state.Text = "挡位 / gear: " + sample.Mode;
        // Temperature by level: the palette's warning and critical shades, normal in the primary.
        _cpuTemp.Foreground = Brush(TempColor(sample.CpuTemp));
        _gpuTemp.Foreground = Brush(TempColor(sample.GpuTemp));
    }

    private string TempColor(double celsius) => celsius >= 85 ? _colors.Critical : celsius >= 70 ? _colors.Warning : _colors.Primary;

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        _title.Foreground = Brush(colors.Secondary);
        _state.Foreground = Brush(colors.Tertiary);
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

// The capsule's widget: the two temperatures, tight. The plugin's poll writes into its text, so there is exactly one
// timer for the whole plugin.
internal sealed class ThrmCompact : IIslandCompact
{
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public string Id => "temps";
    public FrameworkElement CreateView(IslandColors colors)
    {
        OnColorsChanged(colors);
        return _text;
    }

    // "88° 88°" measured once: the width is steady, so the row does not breathe with the numbers.
    public double DesiredWidth(double availableHeight)
    {
        var keep = _text.Text;
        _text.Text = "88° 88°";
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(_text.DesiredSize.Width);
        _text.Text = keep;
        return width + 8;
    }

    public void OnColorsChanged(IslandColors colors)
        => _colors = colors;

    public void Update(ThrmSample? sample)
    {
        _text.Text = sample is null ? "" : $"{sample.CpuTemp:0}° {sample.GpuTemp:0}°";
        _text.Foreground = Brush(_colors.Secondary);
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}
