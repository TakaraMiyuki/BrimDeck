using System.Globalization;
using System.Windows;
using BrimDeck.Core;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private FrameworkElement SettingLabel(string title, Func<string>? help = null)
    {
        var label = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        if (help is null) return label;
        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        heading.Children.Add(label);
        heading.Children.Add(HelpIcon(title, help, 14));
        return heading;
    }

    // The info icon that shows a setting's or a section's description on hover or focus.
    private FrameworkElement HelpIcon(string title, Func<string> help, double size, double nudge = 1)
    {
        // The geometry is drawn for a 14 DIP box; smaller icons scale it so the circle stays centered and unclipped.
        var icon = Stroke("M 7,1 A 6,6 0 1 1 6.99,1 M 7,6.5 V 10 M 7,4 V 4.1", 14, 14, TextTertiary);
        if (size != 14) icon.LayoutTransform = new ScaleTransform(size / 14, size / 14);
        // Measured ink: beside a 13pt label the circle's center sat 1 DIP above the center of the label's glyphs.
        if (nudge != 0) icon.RenderTransform = new TranslateTransform(0, nudge);
        var info = new Border
        {
            Width = size + 8, Height = 24, Margin = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Focusable = true, Cursor = Cursors.Help,
            Child = icon, FocusVisualStyle = (Style)FindResource("SettingsHelpFocus")
        };
        AttachSettingHelp(info, title, help);
        return info;
    }

    private static void AttachSettingHelp(FrameworkElement info, string title, Func<string> help)
    {
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        var tooltip = new ToolTip { Content = description, PlacementTarget = info, Placement = PlacementMode.Bottom, HorizontalOffset = -4, VerticalOffset = 4 };
        info.ToolTip = tooltip;
        // An explicit help target should respond immediately, including when switching between nearby icons.
        ToolTipService.SetInitialShowDelay(info, 0);
        ToolTipService.SetBetweenShowDelay(info, 0);
        void UpdateHelp()
        {
            description.Text = help();
            AutomationProperties.SetHelpText(info, description.Text);
        }
        UpdateHelp();
        AutomationProperties.SetName(info, Loc.T(title + "说明", "About " + title));
        info.ToolTipOpening += (_, _) => UpdateHelp();
        // WPF normally keeps a tooltip open over the popup and the gap below its owner.
        // Help icons own the hover boundary; reopening here also handles immediate re-entry.
        info.MouseEnter += (_, _) => { UpdateHelp(); tooltip.IsOpen = true; };
        info.MouseLeave += (_, _) => tooltip.IsOpen = false;
        info.GotKeyboardFocus += (_, _) => { UpdateHelp(); tooltip.IsOpen = true; };
        info.LostKeyboardFocus += (_, _) => tooltip.IsOpen = false;
        info.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { tooltip.IsOpen = false; e.Handled = true; } };
        info.Unloaded += (_, _) => tooltip.IsOpen = false;
    }

    // Where each segmented control's highlight stood when the user last pressed it, so a page rebuilt by that
    // choice (theme, quick size) can still slide the highlight from the old option instead of jumping.
    private readonly Dictionary<string, (double X, DateTime Pressed)> _segmentPresses = [];

    // The selected option's fill is one shared highlight that slides between options.
    private Border SegmentBorder(FrameworkElement content)
    {
        var options = ((Panel)content).Children.OfType<RadioButton>().ToList();
        string group = options.FirstOrDefault()?.GroupName ?? "";
        var move = new TranslateTransform();
        var highlight = new Border { CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, RenderTransform = move, Visibility = Visibility.Hidden };
        highlight.SetResourceReference(Border.BackgroundProperty, "SettingsFillSelected");
        var host = new Grid(); host.Children.Add(highlight); host.Children.Add(content);
        void Slide(double from, double to)
        {
            if (!SystemParameters.ClientAreaAnimation || Math.Abs(from - to) < 0.5) { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = to; return; }
            var slide = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
            { EasingFunction = Soft.Out };
            // Hand the final position back to the property so later layout passes can move the highlight again.
            slide.Completed += (_, _) => { if (move.X == to) { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = to; } };
            move.BeginAnimation(TranslateTransform.XProperty, slide);
        }
        void Place(bool changed)
        {
            var chosen = options.FirstOrDefault(o => o.IsChecked == true);
            if (chosen is null || chosen.ActualWidth <= 0 || !host.IsAncestorOf(chosen)) { highlight.Visibility = Visibility.Hidden; return; }
            var at = chosen.TranslatePoint(new Point(), host);
            highlight.Width = chosen.ActualWidth; highlight.Height = chosen.ActualHeight; highlight.Margin = new Thickness(0, at.Y, 0, 0);
            double from = move.X;
            bool animate = changed && highlight.Visibility == Visibility.Visible;
            // A freshly built control continues the slide the user started on the control it replaced.
            if (!changed && _segmentPresses.TryGetValue(group, out var press) && DateTime.Now - press.Pressed < TimeSpan.FromMilliseconds(800))
            { from = press.X; animate = true; _segmentPresses.Remove(group); }
            if (animate) Slide(from, at.X); else { move.BeginAnimation(TranslateTransform.XProperty, null); move.X = at.X; }
            highlight.Visibility = Visibility.Visible;
        }
        foreach (var option in options)
        {
            option.PreviewMouseLeftButtonDown += (_, _) => { if (highlight.Visibility == Visibility.Visible) _segmentPresses[group] = (move.X, DateTime.Now); };
            option.Checked += (_, _) => Place(true);
        }
        // The first size pass places the highlight (and may start a continued slide); later passes only follow resizes.
        host.Loaded += (_, _) => { if (highlight.Visibility != Visibility.Visible) Place(false); };
        host.SizeChanged += (_, _) => { if (move.HasAnimatedProperties) return; Place(false); };
        return new Border { Child = host, Background = UI.Brush(Inset), CornerRadius = new CornerRadius(7), Padding = new Thickness(3), Margin = new Thickness(0, 4, 0, 4) };
    }

    // The number editor every size and every declared setting uses. Its step decides how the value is written: a whole
    // step shows whole numbers, a fraction shows as many places as the step itself has. snap is for controls whose
    // author declared a grid — a plugin's number range with a step of five answers 65 to a reader who typed 63, the
    // same way the host stores it, so the box never shows a value the plugin will not receive.
    private (Slider Slider, Border Number) NumericControls(string title, double value, double min, double max, double tick, string unit, Action<double> changed, bool snap = false)
    {
        var grid = double.IsFinite(tick) && tick > 0 ? tick : 1;
        var places = Places(grid);
        var format = places == 0 ? "0" : "0." + new string('#', places);
        static int Places(double step)
        {
            for (int at = 0; at < 6; at++)
            {
                var scaled = step * Math.Pow(10, at);
                if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9) return at;
            }
            return 6;
        }
        var slider = new Slider { Style=(Style)FindResource("SettingsSlider"),Minimum=min,Maximum=max,Value=Math.Clamp(value,min,max),
            SmallChange=grid,LargeChange=grid*2,TickFrequency=grid,IsSnapToTickEnabled=true,IsMoveToPointEnabled=true,VerticalAlignment=VerticalAlignment.Center };
        var input = new TextBox { Style=(Style)FindResource("SettingsNumber"),Width=40,Text=slider.Value.ToString(format,CultureInfo.InvariantCulture),VerticalAlignment=VerticalAlignment.Center };
        AutomationProperties.SetName(slider,title); AutomationProperties.SetName(input,Loc.T(title+"数值", title+" value"));
        var line=new StackPanel {Orientation=Orientation.Horizontal};line.Children.Add(input);
        var units=TextLine(unit,10,TextTertiary);units.Margin=new Thickness(4,0,6,0);units.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(units);
        var minus=new RepeatButton { Style=(Style)FindResource("SettingsStepper"),Content="−" };
        var plus=new RepeatButton { Style=(Style)FindResource("SettingsStepper"),Content="+" };
        AutomationProperties.SetName(minus,Loc.T("减少"+title, "Decrease "+title));AutomationProperties.SetName(plus,Loc.T("增加"+title, "Increase "+title));
        line.Children.Add(new Border {Width=1,Background=UI.Brush(Divider),Margin=new Thickness(2,5,3,5)});line.Children.Add(minus);line.Children.Add(plus);
        var chip=new Border {Child=line,Height=30,CornerRadius=new CornerRadius(6),Background=UI.Brush(_palette.Input),BorderBrush=UI.Brush(_palette.Border),BorderThickness=new Thickness(1),Padding=new Thickness(7,0,2,0)};
        input.GotKeyboardFocus+=(_,_)=>chip.BorderBrush=UI.Brush(_palette.Accent);
        input.LostKeyboardFocus+=(_,_)=>chip.BorderBrush=UI.Brush(_palette.Border);
        void Set(double number)
        {
            var bounded = Math.Clamp(number, min, max);
            var next = Math.Clamp(snap
                ? min + Math.Round((bounded - min) / grid, MidpointRounding.AwayFromZero) * grid
                : Math.Round(bounded, places, MidpointRounding.AwayFromZero), min, max);
            // The tolerance is a rounding guard, not a floor of its own: a declared step of 0.001 must still register as
            // a change when the reader presses + or −, so it shrinks with the step rather than swallowing it.
            if(Math.Abs(slider.Value-next)<Math.Min(.01,grid/100)) { input.Text=next.ToString(format,CultureInfo.InvariantCulture);return; }
            slider.Value=next;
        }
        slider.ValueChanged+=(_,_)=>{input.Text=slider.Value.ToString(format,CultureInfo.InvariantCulture);changed(slider.Value);};
        void Commit()
        {
            if(double.TryParse(input.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var number)&&double.IsFinite(number))Set(number);
            input.Text=slider.Value.ToString(format,CultureInfo.InvariantCulture);
        }
        minus.Click+=(_,_)=>Set(slider.Value-grid);plus.Click+=(_,_)=>Set(slider.Value+grid);
        input.LostKeyboardFocus+=(_,_)=>Commit();
        input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit();e.Handled=true;}if(e.Key==Key.Escape){input.Text=slider.Value.ToString(format,CultureInfo.InvariantCulture);e.Handled=true;}};
        return(slider,chip);
    }
    private FrameworkElement Dimension(string title, double value, double min, double max, Action<double> changed)
    {
        var panel=new StackPanel();
        var controls=NumericControls(title,value,min,max,10,"DIP",changed);
        KeepPreview(controls.Slider); KeepPreview(controls.Number);
        var head=new DockPanel();DockPanel.SetDock(controls.Number,Dock.Right);head.Children.Add(controls.Number);
        var label=TextLine(title,13,TextPrimary);label.VerticalAlignment=VerticalAlignment.Center;head.Children.Add(label);panel.Children.Add(head);
        controls.Slider.Margin=new Thickness(0,24,0,6);panel.Children.Add(controls.Slider);
        var scale=new DockPanel();var last=TextLine(max.ToString("0"),10,TextTertiary);DockPanel.SetDock(last,Dock.Right);scale.Children.Add(last);scale.Children.Add(TextLine(min.ToString("0"),10,TextTertiary));panel.Children.Add(scale);
        return panel;
    }

    // A question before an action that cannot be undone, in a small dialog over the settings: Cancel (Esc) or the action, in red.
    private bool Confirm(string title, string message, string action)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = UI.PanelFont,
            Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary), ResizeMode = ResizeMode.NoResize };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog);
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        var heading = TextLine(title, 16, TextPrimary, FontWeights.SemiBold); heading.Margin = new Thickness(0, 0, 0, 10); panel.Children.Add(heading);
        var text = TextLine(message, 12.5, TextSecondary); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
        bool confirmed = false;
        var cancel = ActionButton(Loc.T("取消", "Cancel"), dialog.Close); cancel.Style = (Style)dialog.FindResource("SettingsOutlineButton"); cancel.IsCancel = true;
        var confirm = ActionButton(action, () => { confirmed = true; dialog.Close(); }); confirm.Style = (Style)dialog.FindResource("SettingsDangerButton");
        foreach (var button in new[] { cancel, confirm }) { button.MinWidth = 84; button.Padding = new Thickness(14, 6, 14, 6); }
        confirm.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        var frame = new Border { BorderThickness = new Thickness(1), Child = panel };
        frame.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, "SettingsSurface"); dialog.Content = frame;
        // Cancel has the focus, so Enter alone does not delete.
        dialog.Loaded += (_, _) => cancel.Focus();
        dialog.ShowDialog();
        return confirmed;
    }
}
