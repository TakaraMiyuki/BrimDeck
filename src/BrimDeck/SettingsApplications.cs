using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BrimDeck.Core;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private Border AppNameField(AppEntry? entry)
    {
        var input = new TextBox { Style = (Style)FindResource("SettingsNumber"), TextAlignment = TextAlignment.Left,
            FontSize = 12.5, MaxLength = 64, VerticalAlignment = VerticalAlignment.Center, IsEnabled = entry is not null };
        AutomationProperties.SetAutomationId(input, "display-name-" + entry?.InstanceId.ToString("N"));
        string help = Loc.T("显示在看板上的名称；浅色文字为默认名称，点击后可直接输入，留空恢复配额来源名称",
            "The name shown on the panel. Light text is the default name; click to type a new one, or leave it empty to use the quota source's name");
        AutomationProperties.SetHelpText(input, help);
        var field = new Border { Child = input, Height = 34, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = UI.Brush(_palette.Input),
            BorderBrush = UI.Brush(_palette.Border), BorderThickness = new Thickness(1) };
        bool showingDefault = false;
        void Refresh()
        {
            var current = entry is null ? null : S.Entry(entry.InstanceId);
            showingDefault = !input.IsKeyboardFocused && string.IsNullOrWhiteSpace(current?.DisplayName);
            input.Text = input.IsKeyboardFocused ? current?.DisplayName ?? "" : current?.Name ?? "";
            input.Foreground = UI.Brush(showingDefault ? TextTertiary : TextPrimary);
            field.BorderBrush = UI.Brush(input.IsKeyboardFocused ? _palette.Accent : _palette.Border);
            AutomationProperties.SetName(input, Loc.T("显示名称 ", "Display name ") + (current?.Name ?? Loc.T("未配置应用", "unconfigured app")));
        }
        void Commit()
        {
            if (entry is not null && S.Entry(entry.InstanceId) is { } current)
            {
                var value = showingDefault ? "" : input.Text.Trim();
                if (value != current.DisplayName) Change(s => s.Entry(entry.InstanceId)!.DisplayName = value);
            }
            Refresh();
        }
        input.GotKeyboardFocus += (_, _) => Refresh();
        input.LostKeyboardFocus += (_, _) => Commit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Refresh(); e.Handled = true; }
        };
        // Padding belongs to the input's click target as well.
        field.MouseLeftButtonDown += (_, e) => { input.Focus(); e.Handled = true; };
        Refresh();
        return field;
    }

    private const double AppRowHeight = 69;
    private AppDrag? _appDrag;
    private readonly Dictionary<Border, long> _appRowMotionVersions = [];
    private long _appRowMotionVersion;
    // A drag in a list of rows — the app list, the collapsed content, the readings, the plugins. The rows are measured
    // where layout put them, so a list whose rows differ in height (a plugin open to its settings) moves by the same
    // rules as one of rows of a single height. Reordered saves the rows' new order.
    private sealed class AppDrag(Panel list, Action<Panel> reordered, Border[] rows, double[] tops, double[] heights, Border row, FrameworkElement handle, double grabOffset)
    {
        public Panel List { get; } = list;
        public Action<Panel> Reordered { get; } = reordered;
        public Border[] Rows { get; } = rows;
        public double[] Tops { get; } = tops;
        public double[] Heights { get; } = heights;
        public Border Row { get; } = row;
        public FrameworkElement Handle { get; } = handle;
        public double GrabOffset { get; } = grabOffset;
        public int From { get; } = Array.IndexOf(rows, row);
        public int Target { get; set; } = Array.IndexOf(rows, row);
        // Where each row comes to stand once the dragged one is set down at Target.
        public double[] LayoutTops()
        {
            var order = Enumerable.Range(0, Rows.Length).Where(i => i != From).ToList();
            order.Insert(Target, From);
            var landing = new double[Rows.Length];
            double top = Tops[0];
            foreach (int i in order) { landing[i] = top; top += Heights[i]; }
            return landing;
        }
    }

    private static TranslateTransform RowTransform(Border row) => (TranslateTransform)row.RenderTransform;
    private void SetRowOffset(Border row, double offset)
    {
        _appRowMotionVersions[row] = ++_appRowMotionVersion;
        var transform = RowTransform(row);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = offset;
    }

    private void AnimateRow(Border row, double target, Action? completed = null, bool animate = true)
    {
        var transform = RowTransform(row);
        double start = transform.Y;
        if (!animate || !S.Animations || !SystemParameters.ClientAreaAnimation || Math.Abs(start - target) < .1)
        {
            SetRowOffset(row, target);
            completed?.Invoke();
            return;
        }
        // A new WPF animation clock starts on the next render tick. Keep the
        // current position as the base value so neither that tick nor a rapid
        // direction change can briefly expose the destination position.
        SetRowOffset(row, start);
        long version = _appRowMotionVersions[row];
        var motion = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(S.AnimationDuration))
        {
            EasingFunction = Soft.Out
        };
        motion.Completed += (_, _) =>
        {
            // Replaced clocks can still complete; they must not move a row
            // that has since been dragged, reversed or returned on cancellation.
            if (_appRowMotionVersions.GetValueOrDefault(row) != version) return;
            SetRowOffset(row, target); completed?.Invoke();
        };
        transform.BeginAnimation(TranslateTransform.YProperty, motion);
    }

    private static void ResetDragAppearance(Border row)
    {
        row.Background = Brushes.Transparent;
        Panel.SetZIndex(row, 0);
    }

    private void AttachDrag(Border handle) => AttachDrag(handle, () => _appRows, rows =>
    {
        var order = rows.Children.OfType<Border>().Where(b => b.Tag is AppEntry).Select(b => ((AppEntry)b.Tag).InstanceId).ToArray();
        Change(s => s.Apps = order.Select(id => s.Entry(id)!).ToList());
    }, CollapseSourceRows);

    // The rows are the list's children marked with a Tag, whatever the row holds; the handle finds its own by walking up.
    // preview takes the mouse events before the handle's own class handlers: a Button presses itself and lets go of the
    // capture first otherwise, and that release would read as the capture being taken away.
    private void AttachDrag(FrameworkElement handle, Func<Panel?> list, Action<Panel> reordered, Action? starting = null, bool preview = false)
    {
        AutomationProperties.SetName(handle, Loc.T("拖动调整顺序", "Drag to reorder"));
        // Focusable so that pressing it commits a name being edited; reordering is by mouse only, so Tab skips it.
        handle.Focusable = true; KeyboardNavigation.SetIsTabStop(handle, false); handle.FocusVisualStyle = null;
        void Down(object _, MouseButtonEventArgs e)
        {
            if (list() is not { } rows || RowFor(handle) is not { } row) return;
            handle.Focus(); // Commits any name being edited before beginning the drag.
            StartAppDrag(rows, reordered, starting, row, handle, e.GetPosition(rows).Y);
            e.Handled = true;
        }
        void Move(object _, MouseEventArgs e)
        {
            if (_appDrag?.Handle != handle || !handle.IsMouseCaptured) return;
            UpdateAppDrag(e.GetPosition(_appDrag.List).Y);
        }
        void Up(object _, MouseButtonEventArgs e)
        {
            if (_appDrag?.Handle != handle) return;
            EndAppDrag(true); e.Handled = true;
        }
        void Captured(object _, MouseEventArgs e) { if (_appDrag?.Handle == handle) EndAppDrag(false); }
        if (preview)
        {
            handle.PreviewMouseLeftButtonDown += Down;
            handle.PreviewMouseMove += Move;
            handle.PreviewMouseLeftButtonUp += Up;
        }
        else
        {
            handle.MouseLeftButtonDown += Down;
            handle.MouseMove += Move;
            handle.MouseLeftButtonUp += Up;
        }
        handle.LostMouseCapture += Captured;
    }

    // The row a handle belongs to: the first border above it that the list marks with a Tag.
    private static Border? RowFor(DependencyObject handle)
    {
        for (var at = VisualTreeHelper.GetParent(handle); at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is Border { Tag: not null } row) return row;
        return null;
    }

    private void StartAppDrag(Panel list, Action<Panel> reordered, Action? starting, Border row, FrameworkElement handle, double pointerY)
    {
        EndAppDrag(false, false);
        starting?.Invoke();
        var rows = list.Children.OfType<Border>().Where(b => b.Tag is not null).ToArray();
        foreach (var item in rows) ResetDragAppearance(item);
        int from = Array.IndexOf(rows, row);
        if (from < 0) return;
        // Rows are measured where layout put them: the dragged row is moved by a transform, so where layout put it stays
        // the same for as long as the drag lasts, and the rows it passes keep their measured places too.
        var tops = rows.Select(item => VisualTreeHelper.GetOffset(item).Y).ToArray();
        var heights = rows.Select(item => item.ActualHeight).ToArray();
        _appDrag = new AppDrag(list, reordered, rows, tops, heights, row, handle, pointerY - tops[from] - RowTransform(row).Y);
        // Keep the visual tree and mouse capture intact until release. Only transforms move during a drag.
        SetRowOffset(row, RowTransform(row).Y);
        row.Background = UI.Brush(Inset); row.CornerRadius = new CornerRadius(6); Panel.SetZIndex(row, 2);
        if (!handle.CaptureMouse()) EndAppDrag(false, false);
    }

    internal void UpdateAppDrag(double pointerY)
    {
        if (_appDrag is not { } drag) return;
        double lowest = drag.Tops[^1] + drag.Heights[^1] - drag.Heights[drag.From];
        double top = Math.Clamp(pointerY - drag.GrabOffset, drag.Tops[0], Math.Max(drag.Tops[0], lowest));
        SetRowOffset(drag.Row, top - drag.Tops[drag.From]);
        // The place the row would take is the row it stands nearest: a row of its own height passes the middle of its
        // neighbours' places, so rows of different heights are still crossed where they look crossed.
        double center = top + drag.Heights[drag.From] / 2;
        int target = 0;
        double nearest = double.MaxValue;
        for (int i = 0; i < drag.Rows.Length; i++)
        {
            double distance = Math.Abs(center - (drag.Tops[i] + drag.Heights[i] / 2));
            if (distance >= nearest) continue;
            nearest = distance; target = i;
        }
        if (target == drag.Target) return;
        drag.Target = target;
        var landing = drag.LayoutTops();
        for (int i = 0; i < drag.Rows.Length; i++)
        {
            if (i == drag.From) continue;
            AnimateRow(drag.Rows[i], landing[i] - drag.Tops[i]);
        }
    }

    internal void EndAppDrag(bool commit, bool animate = true)
    {
        if (_appDrag is not { } drag) return;
        _appDrag = null;
        if (drag.Handle.IsMouseCaptured) drag.Handle.ReleaseMouseCapture();
        var list = drag.List;
        // Where each row is drawn now, before the list takes its new places.
        var drawn = drag.Rows.Select((row, i) => drag.Tops[i] + RowTransform(row).Y).ToArray();
        bool changed = commit && drag.From != drag.Target;
        if (changed)
        {
            list.Children.Remove(drag.Row);
            list.Children.Insert(drag.Target, drag.Row);
            UpdateDividers(list);
        }
        // The rows that are in the list now, in the order it has them: the dropped row's place is the one it was moved to.
        double top = drag.Tops[0];
        foreach (var row in list.Children.OfType<Border>().Where(b => b.Tag is not null))
        {
            int i = Array.IndexOf(drag.Rows, row);
            SetRowOffset(row, drawn[i] - top);
            AnimateRow(row, 0, () => { if (_appDrag?.Row != row) ResetDragAppearance(row); }, animate);
            top += drag.Heights[i];
        }
        if (changed) drag.Reordered(list);
    }
}
