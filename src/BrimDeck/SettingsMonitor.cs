using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrimDeck.Core;

namespace BrimDeck;

// The system monitor's own settings page ("系统监控 › 显示与读数"), as AI usage and music have theirs: how the expanded page looks
// and what the collapsed notch or capsule shows. The page's size stays with the other pages' sizes under "展开外观", and the
// switches that show the readings in the notch or the capsule stay under "收起外观 › 显示内容", next to the quota rings.
public sealed partial class SettingsWindow
{
    // The colors come first on their own, as they color both the collapsed readings and the expanded page: a color per reading,
    // with ftop's schemes and the panel theme's colors under them as quick presets. Then what the collapsed row shows and how the
    // expanded page looks, under the music page's "收起时" and "展开时".
    private void MonitorSettings()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        _refreshReadings = null;
        // Shows what the settings hold in place: the colors, the preset they match, and the readings' colors in the table.
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } _refreshReadings?.Invoke(); }
        // A color per reading, three to a row.
        var roles = KeepPreview(new UniformGrid { Columns = 3, Width = Loc.IsEnglish ? 450 : 330, Margin = new Thickness(0, 6, 0, 6) });
        foreach (var role in Enum.GetValues<MonitorRole>()) roles.Children.Add(SwatchCell(RoleName(role), RoleHelp(role), () => RoleSwatch(role, Refresh), refresh));
        // Quick presets under the colors, as under the collapsed readings' table: one sets every color, and the one the colors
        // match is lit, else none (the colors are then the user's own). Each is shown by its CPU, memory and GPU colors as the
        // panel draws them with the panel theme in use. Each segment fits its three dots and "跟随主题" inside its padding; at 97
        // DIPs the centred content overflowed and lost the first dot's left edge.
        var presets = KeepPreview(new UniformGrid { Columns = 4, Width = 420 });
        foreach (var palette in Enum.GetValues<MonitorPalette>().Where(palette => palette != MonitorPalette.Custom))
        {
            var colors = MonitorThemeColors.For(palette, S.MonitorCustomColors);
            var content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var color in new[] { colors.Performance, colors.Memory, colors.Gpu })
                content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = UI.Brush(color), Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = MonitorColors.Name(palette), Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            string help = palette == MonitorPalette.Theme
                ? Loc.T("使用当前面板主题的颜色，切换主题时随之改变。", "Takes the colors of the panel theme in use, and changes with it.")
                : Loc.T($"将各项颜色设为“{MonitorColors.Name(palette)}”配色。", $"Sets every color to the {MonitorColors.Name(palette)} scheme.");
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = content, GroupName = "MonitorPalette" };
            AutomationProperties.SetName(choice, Loc.T("颜色预设 ", "Color preset ") + MonitorColors.Name(palette)); AutomationProperties.SetHelpText(choice, help);
            // Choosing a preset shows it on the open monitor page, as a size change does.
            choice.Checked += (_, _) =>
            {
                if (syncing) return;
                Resize(s => { s.MonitorPalette = palette; s.MonitorCustomColors = new() { Base = palette }; }, DeckPage.Monitor);
                Refresh();
            };
            refresh.Add(() => choice.IsChecked = S.MonitorPalette == palette);
            presets.Children.Add(choice);
        }
        Refresh();
        // The options stay available while the page is off, so they can be set before it is shown.
        SectionTitle(Loc.T("通用", "General"));
        Group(Row(Loc.T("各项颜色", "Reading colors"), roles),
            QuickRow(Loc.T("快捷预设", "Presets"), presets));
        CompactMonitorOptions();
        SectionTitle(Loc.T("展开时", "When expanded"));
        // The choices that show themselves on the monitor page keep its preview open, so switching between them does not collapse it first.
        Group(MusicChoice(Loc.T("主视图", "Large gauge"), S.MonitorMain, [(MonitorMain.Cpu, "CPU"), (MonitorMain.Gpu, "GPU")], v => Resize(s => s.MonitorMain = v, DeckPage.Monitor), keepPreview: true),
            MusicChoice(Loc.T("刷新间隔", "Update interval"), S.MonitorInterval, [(1, Loc.T("1 秒", "1 s")), (2, Loc.T("2 秒", "2 s"))], v => Change(s => s.MonitorInterval = v)),
            MusicChoice(Loc.T("进程行", "Process rows"), S.MonitorProcessView,
                [(MonitorProcessView.Auto, Loc.T("自动", "Auto")), (MonitorProcessView.Compact, Loc.T("简略", "Compact")), (MonitorProcessView.Detailed, Loc.T("详细", "Detailed"))],
                v => Resize(s => s.MonitorProcessView = v, DeckPage.Monitor), keepPreview: true),
            Toggle(Loc.T("显示进程列表", "Show the process list"), S.MonitorProcesses, v => Resize(s => s.MonitorProcesses = v, DeckPage.Monitor),
                help:
                Loc.T("按 CPU 或内存列出占用最高的应用，同一程序的多个进程合并为一行。面板足够大时显示。",
                    "Lists the apps using the most CPU or memory, with the processes of one program folded into one row. Shown when the panel is large enough."),
                keepPreview: true));
    }

    // A color circle with its name beside it. refresh builds the circle again, so it shows the color the settings hold. The note
    // shows on hover over the cell, or, with info, by an info icon after the name, for a note the user needs to find.
    private StackPanel SwatchCell(string name, string? help, Func<Button> swatch, List<Action> refresh, bool info = false)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Margin = new Thickness(0, 3, 0, 3), ToolTip = info ? null : help };
        Button? shown = null;
        refresh.Add(() =>
        {
            var next = swatch();
            // A panel will not take a new child at an index still holding one, so the old circle leaves first.
            if (shown is not null) cell.Children.Remove(shown);
            cell.Children.Insert(0, next);
            shown = next;
        });
        var label = TextLine(name, 12.5, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(8, 0, 0, 0);
        cell.Children.Add(label);
        if (info && help is not null) cell.Children.Add(HelpIcon(name, () => help, 12, nudge: 0));
        return cell;
    }

    // Changing a color of a preset makes the colors the user's own, starting from that preset; null returns a color to the
    // preset's, and with none changed the preset is lit again.
    private static void SetSchemeColor(DeckSettings settings, MonitorRole role, string? color)
    {
        if (settings.MonitorPalette != MonitorPalette.Custom) settings.MonitorCustomColors = new() { Base = settings.MonitorPalette };
        settings.MonitorCustomColors.Set(role, color);
        settings.MonitorPalette = settings.MonitorCustomColors.Changed ? MonitorPalette.Custom : settings.MonitorCustomColors.Base;
    }

    // A reading's color as the panel draws it. The picker's "默认" returns it to the preset's.
    private Button RoleSwatch(MonitorRole role, Action refreshed)
    {
        string? own = S.MonitorPalette == MonitorPalette.Custom ? S.MonitorCustomColors.Get(role) : null;
        string color = MonitorThemeColors.For(S)[role], name = RoleName(role);
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(color) };
        AutomationProperties.SetName(button, Loc.T($"{name}颜色", $"{name} color"));
        AutomationProperties.SetHelpText(button, color + (own is null ? Loc.T("（默认）", " (default)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(own ?? color, own is null, value =>
        {
            popup.IsOpen = false;
            Resize(s => SetSchemeColor(s, role, value.Length > 0 ? value : null), DeckPage.Monitor);
            refreshed();
        });
        TogglePopup(button, popup);
        return button;
    }

    // The collapsed readings' color at the warning or critical level, as the panel draws it; "默认" returns it to the panel theme's.
    private Button LevelSwatch(MemoryLevel level, Action refreshed)
    {
        bool critical = level == MemoryLevel.Critical;
        string? own = critical ? S.CompactCriticalColor : S.CompactWarningColor;
        string color = critical ? MonitorThemeColors.Critical(S) : MonitorThemeColors.Warning(S), name = LevelName(level);
        var button = new Button { Style = (Style)FindResource("SettingsColorButton"), Foreground = UI.Brush(color) };
        AutomationProperties.SetName(button, Loc.T($"收起时{name}颜色", $"Collapsed {name} color"));
        AutomationProperties.SetHelpText(button, color + (own is null ? Loc.T("（默认）", " (default)") : ""));
        var popup = new Popup { PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, VerticalOffset = 4 };
        popup.Child = ColorPicker(own ?? (critical ? PanelTheme.CriticalDefault : PanelTheme.WarningDefault), own is null, value =>
        {
            popup.IsOpen = false;
            string? chosen = value.Length > 0 ? value : null;
            Change(s => { if (critical) s.CompactCriticalColor = chosen; else s.CompactWarningColor = chosen; });
            refreshed();
        });
        TogglePopup(button, popup);
        return button;
    }

    private static string RoleName(MonitorRole role) => role switch
    {
        MonitorRole.Efficiency => Loc.T("能效核", "Efficiency cores"), MonitorRole.Memory => Loc.T("内存", "Memory"), MonitorRole.Gpu => "GPU",
        MonitorRole.Download => Loc.T("下载", "Download"), MonitorRole.Upload => Loc.T("上传", "Upload"), _ => "CPU"
    };
    private static string LevelName(MemoryLevel level) => level == MemoryLevel.Critical ? Loc.T("告警", "Critical") : Loc.T("预警", "Warning");

    // Only CPU and the efficiency cores say where they show, as both color the core columns; the others' names say it.
    private static string? RoleHelp(MonitorRole role) => role switch
    {
        MonitorRole.Cpu => Loc.T("CPU 读数与核心柱中性能核的颜色。", "The color of the CPU readings and of the performance cores in the core columns."),
        MonitorRole.Efficiency => Loc.T("核心柱中能效核的颜色。", "The color of the efficiency cores in the core columns."),
        _ => null
    };

    // What the collapsed notch or capsule shows when its "系统监控" switch is on: a table of the readings, laid out as the app list
    // is (drag handle, the reading's colors from the scheme, switches), with Full and Brief under it as quick presets, then the
    // readings' warning and critical colors; and whether each surface widens to its content.
    private Action? _refreshReadings;
    private const double ReadingRowHeight = 53;
    private void CompactMonitorOptions()
    {
        bool syncing = false;
        var refresh = new List<Action>();
        // Shows what the settings hold in place, without saving anything: the preset the table matches, the switches, the colors.
        void Refresh() { syncing = true; try { foreach (var update in refresh) update(); } finally { syncing = false; } }
        _refreshReadings = Refresh;
        void Edit(Action<DeckSettings> edit) { if (syncing) return; Change(edit); Refresh(); }
        SectionTitle(Loc.T("收起时", "When collapsed"));

        var rows = new StackPanel();
        foreach (var reading in S.CompactReadings) rows.Children.Add(ReadingRow(reading.Item, rows, refresh, Edit));
        UpdateDividers(rows);
        var headings = ReadingColumns(); headings.Height = 34;
        AddHeadings(headings, new[]
        {
            (Loc.T("监控项", "Item"), 2, ""),
            (Loc.T("显示数值", "Value"), 3, ""),
            (Loc.T("显示", "Show"), 4, "")
        });
        // The names in the rows start at the left of their column, so their heading does too rather than floating in its middle.
        var nameHeading = headings.Children.OfType<FrameworkElement>().First(heading => Grid.GetColumn(heading) == 2);
        nameHeading.HorizontalAlignment = HorizontalAlignment.Left; nameHeading.Margin = new Thickness(6, 0, 6, 0);
        var table = new StackPanel();
        table.Children.Add(headings);
        table.Children.Add(new Border { Child = rows, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0, 1, 0, 0) });
        // Quick presets under the table, as the quick sizes: one sets which readings show and which rings carry their figure, never
        // the order or the colors. The one the table matches exactly is lit, else none; the table itself is the custom choice.
        // Each segment keeps the width of a quick size's.
        var presets = new UniformGrid { Columns = 2, Width = 196 };
        foreach (var (mode, label, help) in new[]
        {
            (CompactMonitorMode.Full, Loc.T("完整", "Full"), Loc.T("显示全部读数，CPU、内存和 GPU 带百分比。不改变顺序和颜色。", "Shows every reading, with the percentages of CPU, memory and GPU. The order and the colors stay.")),
            (CompactMonitorMode.Brief, Loc.T("简洁", "Brief"), Loc.T("只显示 CPU、内存和 GPU 的圆环，不带数值。不改变顺序和颜色。", "Shows only the CPU, memory and GPU rings, without figures. The order and the colors stay."))
        })
        {
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = label, GroupName = "CompactMonitorPreset" };
            AutomationProperties.SetName(choice, Loc.T("快捷预设 ", "Preset ") + label); AutomationProperties.SetHelpText(choice, help);
            choice.Checked += (_, _) => { if (!syncing) Edit(s => CompactMonitor.Apply(s.CompactReadings, mode)); };
            refresh.Add(() => choice.IsChecked = CompactMonitor.Preset(S.CompactReadings) == mode);
            presets.Children.Add(choice);
        }
        var quick = QuickRow(Loc.T("快捷预设", "Presets"), presets);
        // The readings' colors when usage is high; the expanded page does not change color by level.
        var levels = KeepPreview(new UniformGrid { Columns = 2, Width = Loc.IsEnglish ? 300 : 220, Margin = new Thickness(0, 6, 0, 6) });
        foreach (var level in new[] { MemoryLevel.Elevated, MemoryLevel.Critical })
            levels.Children.Add(SwatchCell(LevelName(level), LevelHelp(level), () => LevelSwatch(level, Refresh), refresh, info: true));
        var status = Row(Loc.T("状态颜色", "Level colors"), levels, help: () => Loc.T("仅在收起时生效，展开时不随占用变色。",
            "Used only while collapsed; the expanded page does not change color with usage."));
        Refresh();
        Group(table, quick, status);
    }

    private static Grid ReadingColumns()
    {
        var grid = new Grid { Background = Brushes.Transparent };
        // The handle, the reading's color dots (one per channel, CompactMonitor.Channels), the name, the two switches.
        foreach (var width in new[] { new GridLength(40), new GridLength(ReadingDot * CompactMonitor.Order.Max(CompactMonitor.Channels) + 3 + 6),
            new GridLength(1, GridUnitType.Star), new GridLength(Loc.IsEnglish ? 76 : 84), new GridLength(64) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        return grid;
    }

    // The note of a level's color column, from the thresholds the levels are read with (ReadingLevels, MemorySample.Level).
    private static string LevelHelp(MemoryLevel level)
    {
        bool critical = level == MemoryLevel.Critical;
        double share = critical ? ReadingLevels.CriticalShare : ReadingLevels.ElevatedShare,
            commit = critical ? MemorySample.CriticalCommit : MemorySample.ElevatedCommit,
            rate = (critical ? ReadingLevels.CriticalRate : ReadingLevels.ElevatedRate) / 1_000_000;
        // The memory commit charge is always named: by its own threshold where it differs from the shares', else as part of memory.
        bool apart = commit != share;
        return Loc.T($"占用达到 {share * 100:0}%{(apart ? $"（内存提交量为 {commit * 100:0}%）" : "（内存包括内存提交量）")}或网速达到 {rate:0} MB/s 时的颜色。",
            $"The color when usage reaches {share * 100:0}%{(apart ? $" ({commit * 100:0}% for the memory commit charge)" : " (for memory, including the commit charge)")} or the network rate reaches {rate:0} MB/s.");
    }

    private const double ReadingDot = 10;

    private static string ReadingName(CompactMonitorItems item) => item switch
    {
        CompactMonitorItems.Columns => Loc.T("核心柱", "Core columns"), CompactMonitorItems.Memory => Loc.T("内存", "Memory"), CompactMonitorItems.Gpu => "GPU",
        CompactMonitorItems.Download => Loc.T("下载", "Download"), CompactMonitorItems.Upload => Loc.T("上传", "Upload"), _ => "CPU"
    };
    private static CompactReading Reading(DeckSettings settings, CompactMonitorItems item) => settings.CompactReadings.First(reading => reading.Item == item);

    // One row of the readings' table; its switches follow the settings when a preset changes them (refresh).
    private Border ReadingRow(CompactMonitorItems item, StackPanel rows, List<Action> refresh, Action<Action<DeckSettings>> edit)
    {
        string name = ReadingName(item);
        var row = ReadingColumns();
        var handle = new Border { Width = 22, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle);
        var label = TextLine(name, 13, TextPrimary); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(label, 2); row.Children.Add(label);
        // The reading's colors in the normal state as the scheme gives them, one per channel (the core columns: one per kind of
        // core). They are set under "配色", so they only show here.
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            ToolTip = Loc.T("颜色在上方“各项颜色”中设置。", "Set under Reading colors above.") };
        var fills = Enumerable.Range(0, CompactMonitor.Channels(item)).Select(channel =>
            new Ellipse { Width = ReadingDot, Height = ReadingDot, Margin = new Thickness(channel == 0 ? 0 : 3, 0, 0, 0) }).ToArray();
        foreach (var fill in fills) dots.Children.Add(fill);
        refresh.Add(() =>
        {
            var colors = MonitorThemeColors.For(S);
            for (int channel = 0; channel < fills.Length; channel++) fills[channel].Fill = UI.Brush(colors[CompactMonitor.Role(item, channel)]);
        });
        Grid.SetColumn(dots, 1); row.Children.Add(dots);

        // The figure switch is for the rings only; the core columns (no figure) and the rates (always text) show it off and dimmed.
        bool choice = CompactMonitor.HasValueChoice(item);
        var value = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = choice, IsChecked = item is CompactMonitorItems.Download or CompactMonitorItems.Upload };
        AutomationProperties.SetName(value, Loc.T("显示数值 ", "Show figure ") + name);
        if (!choice)
        {
            value.ToolTip = item == CompactMonitorItems.Columns ? Loc.T("核心柱没有数值。", "The core columns have no figure.") : Loc.T("下载和上传始终显示速率。", "Download and upload always show their rate.");
            ToolTipService.SetShowOnDisabled(value, true);
        }
        else
        {
            value.Checked += (_, _) => edit(s => Reading(s, item).ShowValue = true); value.Unchecked += (_, _) => edit(s => Reading(s, item).ShowValue = false);
            refresh.Add(() => value.IsChecked = Reading(S, item).ShowValue);
        }
        Grid.SetColumn(value, 3); row.Children.Add(value);

        var shown = new CheckBox { Style = (Style)FindResource("SettingsToggle"), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(shown, Loc.T("显示 ", "Show ") + name);
        shown.Checked += (_, _) => edit(s => Reading(s, item).Visible = true); shown.Unchecked += (_, _) => edit(s => Reading(s, item).Visible = false);
        refresh.Add(() => shown.IsChecked = Reading(S, item).Visible);
        Grid.SetColumn(shown, 4); row.Children.Add(shown);

        // Dropped in a new place, the strip takes the new order at once.
        AttachDrag(handle, () => rows, list =>
        {
            var order = list.Children.OfType<Border>().Where(b => b.Tag is CompactMonitorItems).Select(b => (CompactMonitorItems)b.Tag).ToArray();
            Change(s => s.CompactReadings = order.Select(id => Reading(s, id)).ToList());
        });
        return new Border { Child = row, Height = ReadingRowHeight, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0), Tag = item, RenderTransform = new TranslateTransform() };
    }

    private void MonitorSizeControls()
    {
        var dimensions = new Grid { Margin = new Thickness(0, 8, 0, 0) }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        var width = Dimension(Loc.T("宽度", "Width"), S.MonitorWidth, 440, 1200, value => Resize(s => s.MonitorWidth = value, DeckPage.Monitor)); width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), S.MonitorHeight, 140, 400, value => Resize(s => s.MonitorHeight = value, DeckPage.Monitor)); height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        Target.Children.Add(dimensions);
        var sizes = KeepPreview(new UniformGrid { Columns = 3, Width = 292 });
        foreach (var size in Enum.GetValues<PanelSize>())
        {
            var (w, h) = MonitorSizes.For(size);
            var choice = new RadioButton { Style = (Style)FindResource("SettingsSegment"), Content = PanelSizes.Name(size), GroupName = "MonitorSize", IsChecked = (S.MonitorWidth, S.MonitorHeight) == (w, h) };
            AutomationProperties.SetName(choice, Loc.T("系统监控快捷尺寸 ", "System monitor quick size ") + PanelSizes.Name(size));
            choice.Checked += (_, _) => { Resize(s => { s.MonitorWidth = w; s.MonitorHeight = h; }, DeckPage.Monitor); ShowPage(2); };
            sizes.Children.Add(choice);
        }
        QuickSizeRow(sizes);
    }
}
