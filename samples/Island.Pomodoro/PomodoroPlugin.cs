using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BrimDeck.Island;

namespace BrimDeck.Island.Pomodoro;

// The Pomodoro plugin: a focus timer on its own page, with start, pause and reset, and a small read-out in the
// collapsed capsule that shows the time alone. Work length, break length and the number of rounds are the reader's
// own settings, built through the host's control factory and kept in the host's settings store.
//
// The timer runs while some panel shows the page (SetDemand); the capsule read-out keeps ticking on its own, so the
// collapsed island still tells the time left without the page being open.
public sealed class PomodoroPlugin : IIslandExtension
{
    private PomodoroTimer _timer = null!;
    private PomodoroPage _page = null!;
    private PomodoroCompact _compact = null!;
    private IIslandHost _host = null!;

    public string Id => "sample-pomodoro";
    public string TitleZh => "番茄钟";
    public string TitleEn => "Pomodoro";
    public string Author => "TakaraMiyuki";
    public string Description => "番茄工作法计时器：专注与休息交替，可自定义时长与轮数。";
    // A tomato, on the 24 grid the contract draws glyphs on.
    public string Glyph => "M 12,7 C 7.6,7 4,10.4 4,14.6 C 4,18.8 7.6,22 12,22 C 16.4,22 20,18.8 20,14.6 C 20,10.4 16.4,7 12,7 Z M 12,7 C 12,4.8 13.6,3 15.6,2.4 M 12,7 C 11,5.2 9.2,4 7,4";

    public void Initialize(IIslandHost host)
    {
        _host = host;
        _timer = new PomodoroTimer(PomodoroSettings.Load(host));
        _page = new PomodoroPage(_timer);
        _compact = new PomodoroCompact(_timer);
        host.RegisterPage(_page);
        host.RegisterCompact(_compact);
        host.ColorsChanged += () =>
        {
            _page.Recolor(host.Colors);
            _compact.Recolor(host.Colors);
        };
    }

    // The plugin's own settings: the work and break lengths and how many rounds a run holds. Changing one stops the
    // current run and puts the timer back at the start of a work period, so the new lengths apply from the top.
    public FrameworkElement? CreateSettingsView(IslandColors colors)
    {
        var controls = _host.Controls;
        var settings = _timer.Settings;
        void Apply(double work, double rest, double rounds)
        {
            settings.WorkMinutes = work;
            settings.BreakMinutes = rest;
            settings.Rounds = rounds;
            settings.Save(_host);
            _timer.ApplySettings();
        }
        // The host's number control: a slider for sweeping, a box to type an exact value into, step buttons for one
        // minute at a time.
        var work = controls.Number("专注时长", settings.WorkMinutes, 5, 90, value => Apply(value, settings.BreakMinutes, settings.Rounds), 1, "分钟");
        var rest = controls.Number("休息时长", settings.BreakMinutes, 1, 30, value => Apply(settings.WorkMinutes, value, settings.Rounds), 1, "分钟");
        var rounds = controls.Number("轮数", settings.Rounds, 1, 8, value => Apply(settings.WorkMinutes, settings.BreakMinutes, value), 1, "轮");
        return controls.Group("番茄钟", work, rest, rounds);
    }
}

// The timer's settings, read and written through the host's store.
internal sealed class PomodoroSettings
{
    public double WorkMinutes = 25;
    public double BreakMinutes = 5;
    public double Rounds = 4;

    public static PomodoroSettings Load(IIslandHost host)
    {
        var settings = new PomodoroSettings();
        if (double.TryParse(host.GetSetting("work"), NumberStyles.Float, CultureInfo.InvariantCulture, out var work)) settings.WorkMinutes = Math.Clamp(work, 5, 90);
        if (double.TryParse(host.GetSetting("break"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rest)) settings.BreakMinutes = Math.Clamp(rest, 1, 30);
        if (double.TryParse(host.GetSetting("rounds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rounds)) settings.Rounds = Math.Clamp(rounds, 1, 8);
        return settings;
    }

    public void Save(IIslandHost host)
    {
        host.SetSetting("work", WorkMinutes.ToString("0", CultureInfo.InvariantCulture));
        host.SetSetting("break", BreakMinutes.ToString("0", CultureInfo.InvariantCulture));
        host.SetSetting("rounds", Rounds.ToString("0", CultureInfo.InvariantCulture));
    }
}

// The Pomodoro state machine, shared by the page and the capsule widget: a run walks work -> break -> work ... for
// the chosen number of rounds and then stops at a fresh work period. Ticks once a second while anything listens.
internal sealed class PomodoroTimer
{
    public enum Phase { Work, Break, Done }
    public PomodoroSettings Settings { get; private set; }
    public Phase Current { get; private set; } = Phase.Work;
    public bool Running { get; private set; }
    public int Round { get; private set; } = 1;
    // Seconds left in the current period.
    public int Remaining { get; private set; }
    public bool IsBreak => Current == Phase.Break;
    public bool IsDone => Current == Phase.Done;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    public event Action? Changed;

    public PomodoroTimer(PomodoroSettings settings)
    {
        Settings = settings;
        Remaining = WorkSeconds;
        _tick.Tick += (_, _) =>
        {
            if (!Running) return;
            if (Remaining > 0) Remaining--;
            if (Remaining <= 0) Advance();
            Changed?.Invoke();
        };
        _tick.Start();
    }

    private int WorkSeconds => (int)Math.Round(Settings.WorkMinutes * 60);
    private int BreakSeconds => (int)Math.Round(Settings.BreakMinutes * 60);

    public void Start()
    {
        if (Current == Phase.Done) Reset();
        Running = true;
        Changed?.Invoke();
    }
    public void Pause() { Running = false; Changed?.Invoke(); }
    public void Reset()
    {
        Running = false; Current = Phase.Work; Round = 1; Remaining = WorkSeconds;
        Changed?.Invoke();
    }
    // Toggle for the capsule's click: start while resting or paused, pause while running.
    public void Toggle() { if (Running) Pause(); else Start(); }

    // The settings changed: the run stops and the timer stands at the start of a work period.
    public void ApplySettings()
    {
        Running = false; Current = Phase.Work; Round = 1; Remaining = WorkSeconds;
        Changed?.Invoke();
    }

    // The period ran out: a work period opens its break, a break closes the round and opens the next work period, and
    // the last break ends the run.
    private void Advance()
    {
        if (Current == Phase.Work) { Current = Phase.Break; Remaining = BreakSeconds; return; }
        if (Current == Phase.Break)
        {
            if (Round >= (int)Math.Round(Settings.Rounds)) { Current = Phase.Done; Running = false; Remaining = 0; return; }
            Round++; Current = Phase.Work; Remaining = WorkSeconds;
        }
    }

    public string Time => Current == Phase.Done ? "完成"
        : $"{Remaining / 60:00}:{Remaining % 60:00}";
}

// The page: the time large, the phase and round under it, and start / pause / reset under those. The time turns the
// palette's normal color while working and its warning-free green while resting (the contract's Normal shade).
internal sealed class PomodoroPage : IIslandPage
{
    private readonly PomodoroTimer _timer;
    private readonly TextBlock _time = new() { FontSize = 46, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _phase = new() { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _start = Button("开始");
    private readonly Button _pause = Button("暂停");
    private readonly Button _reset = Button("重置");
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public PomodoroPage(PomodoroTimer timer)
    {
        _timer = timer;
        _timer.Changed += Paint;
        _start.Click += (_, _) => _timer.Start();
        _pause.Click += (_, _) => _timer.Pause();
        _reset.Click += (_, _) => _timer.Reset();
    }

    public string Id => "pomodoro";
    public string TitleZh => "番茄钟";
    public string TitleEn => "Pomodoro";
    public string Glyph => "M 12,7 C 7.6,7 4,10.4 4,14.6 C 4,18.8 7.6,22 12,22 C 16.4,22 20,18.8 20,14.6 C 20,10.4 16.4,7 12,7 Z M 12,7 C 12,4.8 13.6,3 15.6,2.4 M 12,7 C 11,5.2 9.2,4 7,4";
    public (double Width, double Height)? PreferredSize => (440, 230);

    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(_start); buttons.Children.Add(_pause); buttons.Children.Add(_reset);
        // The panel's box is the page area: the stack must not collapse to its text and sit at the top, so it is laid
        // out across the box and centred by the outer grid.
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(_time);
        _phase.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(_phase);
        stack.Children.Add(buttons);
        Paint();
        return stack;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);
    public void SetDemand(bool shown) => Paint();

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        Paint();
    }

    private void Paint()
    {
        _time.Text = _timer.Time;
        // Working is the ordinary primary; resting is the palette's normal (green) shade; a finished run is tertiary.
        _time.Foreground = Brush(_timer.IsDone ? _colors.Tertiary : _timer.IsBreak ? _colors.Normal : _colors.Primary);
        _phase.Text = _timer.IsDone
            ? "全部完成"
            : $"{(_timer.IsBreak ? "休息" : "专注")} · 第 {_timer.Round}/{(int)Math.Round(_timer.Settings.Rounds)} 轮{(_timer.Running ? "" : " · 已暂停")}";
        _phase.Foreground = Brush(_timer.IsBreak ? _colors.Normal : _colors.Secondary);
        _start.IsEnabled = !_timer.Running && !_timer.IsDone;
        _pause.IsEnabled = _timer.Running;
    }

    private SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    // A button in the panel's own quiet style: a rounded fill, a hairline border and the secondary text shade.
    private static Button Button(string text) => new()
    {
        Content = text, FontSize = 12, Height = 26, MinWidth = 58, Margin = new Thickness(4, 0, 4, 0),
        Padding = new Thickness(12, 0, 12, 0), Cursor = Cursors.Hand,
        Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray
    };
}

// The capsule's widget: the time alone, small, clicking it starts and pauses the run.
internal sealed class PomodoroCompact : IIslandCompact
{
    private readonly PomodoroTimer _timer;
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, Cursor = Cursors.Hand };
    private IslandColors _colors = new("#4FD17F", "#F0B54A", "#FF6B5E", "#F1F1F4", "#C5C5CB", "#8E8E93", "#2A2A30", "#1D1D21");

    public PomodoroCompact(PomodoroTimer timer)
    {
        _timer = timer;
        _timer.Changed += Paint;
        _text.MouseLeftButtonUp += (_, _) => _timer.Toggle();
    }

    public string Id => "pomodoro";
    public FrameworkElement CreateView(IslandColors colors)
    {
        Recolor(colors);
        Paint();
        return _text;
    }

    // "44:44" measured once, so the row does not breathe with the seconds.
    public double DesiredWidth(double availableHeight)
    {
        var keep = _text.Text;
        _text.Text = "44:44";
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(_text.DesiredSize.Width);
        _text.Text = keep;
        return width + 8;
    }

    public void OnColorsChanged(IslandColors colors) => Recolor(colors);

    public void Recolor(IslandColors colors)
    {
        _colors = colors;
        Paint();
    }

    private void Paint()
    {
        _text.Text = _timer.Time;
        _text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_timer.IsBreak ? _colors.Normal : _colors.Secondary));
    }
}
