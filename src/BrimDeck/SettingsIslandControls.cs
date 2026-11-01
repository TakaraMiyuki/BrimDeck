using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Island;

namespace BrimDeck;

// The settings window IS the plugins' control factory: the widgets a plugin's settings page builds through
// IIslandHost.Controls come from here, so they carry the same look as every row the window draws itself. Everything
// returns a fresh element for the plugin to stack top to bottom; nothing here touches the page's own content.
public sealed partial class SettingsWindow : IIslandControls
{
    private FrameworkElement IslandControlsGroup(string title, FrameworkElement[] children)
    {
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var label = TextLine(title, 11, TextSecondary); label.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(label);
        var panel = new StackPanel();
        foreach (var child in children)
        {
            if (panel.Children.Count > 0) panel.Children.Add(new Border { Height = 1, Background = UI.Brush(_palette.RowDivider) });
            panel.Children.Add(child);
        }
        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.Add(new Border { Child = panel, BorderBrush = UI.Brush(Divider), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 0, 0, 28) });
        return stack;
    }

    FrameworkElement IIslandControls.Group(string title, params FrameworkElement[] children)
        => IslandControlsGroup(title, children);

    FrameworkElement IIslandControls.Row(string title, FrameworkElement content) => Row(title, content);

    FrameworkElement IIslandControls.Toggle(string title, bool isOn, Action<bool> changed)
        => Toggle(title, isOn, changed);

    // The settings window's own numeric control: the slider, the box to type into and the step buttons (Dimension).
    // One line, as the settings window's own slider rows stand: the title at the left, the slider across the row and the
    // number box with its step buttons at the right (SliderRow).
    FrameworkElement IIslandControls.Number(string title, double value, double min, double max, Action<double> changed, double step, string unit)
        => SliderRow(title, value, min, max, step, unit, changed);

    FrameworkElement IIslandControls.Slider(string title, double value, double min, double max, Action<double> changed)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("SettingsSlider"), Minimum = min, Maximum = max,
            Value = Math.Clamp(value, min, max), SmallChange = 1, LargeChange = 2,
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Width = 180
        };
        slider.ValueChanged += (_, _) => changed(Math.Round(slider.Value, 2));
        return Row(title, slider);
    }

    FrameworkElement IIslandControls.Combo(string title, IReadOnlyList<string> options, int index, Action<int> changed)
    {
        var combo = new ComboBox { Style = (Style)FindResource("SettingsCombo"), Width = 150 };
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Clamp(index, 0, Math.Max(0, options.Count - 1));
        combo.SelectionChanged += (_, _) => changed(Math.Max(0, combo.SelectedIndex));
        return Row(title, combo);
    }

    FrameworkElement IIslandControls.Text(string text, bool secondary)
        => TextLine(text, 12, secondary ? TextSecondary : TextPrimary);

    FrameworkElement IIslandControls.ActionRow(string title, string buttonText, Action clicked)
        => Row(title, ActionButton(buttonText, clicked));
}
