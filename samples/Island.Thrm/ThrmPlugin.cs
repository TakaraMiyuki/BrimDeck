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
    public string Glyph => "M 12,2 A 10,10 0 1 1 11.99,2 M 12,7 V 12 L 16,14";

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

// The page: one row per chip — temperature, power, fan speed — and the bridge's own state line under them.
internal sealed class ThrmPage : IIslandPage
{
    private readonly TextBlock _cpu = new() { FontSize = 14 };
    private readonly TextBlock _gpu = new() { FontSize = 14 };
    private readonly TextBlock _state = new() { FontSize = 11.5 };
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public string Id => "cooler";
    public string TitleZh => "散热器";
    public string TitleEn => "Cooler";
    public string Glyph => "M 12,2 A 10,10 0 1 1 11.99,2 M 12,7 V 12 L 16,14";
    public (double Width, double Height)? PreferredSize => (520, 240);

    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        stack.Children.Add(_cpu);
        stack.Children.Add(_gpu);
        _state.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_state);
        return stack;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Update(ThrmSample? sample)
    {
        if (sample is null)
        {
            _cpu.Foreground = Brush(_colors.Warning);
            _cpu.Text = "散热器桥未运行 / cooler bridge not running";
            _gpu.Text = "";
            _state.Text = "";
            return;
        }
        _cpu.Foreground = Brush(_colors.Primary);
        _cpu.Text = $"CPU  {sample.CpuTemp:0}°C   {sample.CpuPower:0.#} W   {sample.CpuFan:0} rpm";
        _gpu.Text = $"GPU  {sample.GpuTemp:0}°C   {sample.GpuPower:0.#} W   {sample.GpuFan:0} rpm";
        _state.Text = "挡位 / gear: " + sample.Mode;
    }

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        _cpu.Foreground = Brush(colors.Primary);
        _gpu.Foreground = Brush(colors.Primary);
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
