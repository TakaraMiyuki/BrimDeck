using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The plugins page: the folder button and the scan button on the left, the loaded extensions under a divider, each row
// opening like the app rules row to its page's size and origin, with an enable switch of its own in the header — off
// takes the tab off the island and stops what the extension reads. The scan loads plugin folders added or changed while the app
// runs and retires the ones taken away; the plugins that could not be read are listed by name and reason.
public sealed partial class SettingsWindow
{
    // Holds the list, so a flip, a switch or a scan rebuilds the list alone.
    private Border? _pluginsHost;
    // Which plugin rows stand open, kept while the page is rebuilt.
    private readonly bool[] _pluginOpen = new bool[4];

    private void PluginsPage()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 18) };
        actions.Children.Add(OutlineAction(Loc.T("打开插件文件夹", "Open plugins folder"),
            "M 1.5,4.4 H 4.4 L 5.6,3 H 10.5 V 9.5 H 1.5 Z", OpenPluginsFolder,
            Loc.T("打开存放插件文件夹的目录。", "Opens the folder the plugins live in.")));
        var reload = OutlineAction(Loc.T("重新加载", "Reload"),
            "M 9.1,4.2 A 3.7,3.7 0 1 0 9.9,7.4 M 9.1,2.2 V 4.2 H 7.1",
            () => { IslandExtensions.Scan(); ShowPage(PluginsPageNumber); },
            Loc.T("重新扫描插件文件夹：新放入的插件被装入，被修改的插件被替换，被删除的插件被停用；无需重启本软件。",
                "Scans the plugins folder again: plugins added are loaded, plugins changed are replaced, plugins taken away are retired; no restart needed."));
        reload.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reload);
        _content.Children.Add(actions);

        SectionTitle(Loc.T("已加载", "Loaded"));
        _pluginsHost = new Border { Child = PluginsList() };
        Group(_pluginsHost);

        if (IslandExtensions.Failures.Count > 0)
        {
            SectionTitle(Loc.T("未能加载", "Could not load"));
            Group(IslandExtensions.Failures.Select(failure => Row(System.IO.Path.GetFileName(failure.File), Wrapped(failure.Error, true))).ToArray());
        }
        if (IslandExtensions.Skipped.Count > 0)
        {
            SectionTitle(Loc.T("已跳过", "Skipped"));
            Group(IslandExtensions.Skipped.Select(skipped => Row(System.IO.Path.GetFileName(skipped.File), Wrapped(skipped.Note, false))).ToArray());
        }
    }

    // An outline button with a small stroke glyph before its text, as the app rules' Add app button is.
    private Button OutlineAction(string text, string glyph, Action action, string tip)
    {
        var button = new Button { Style = (Style)FindResource("SettingsOutlineButton"), ToolTip = tip };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Stroke(glyph, 12, 12, TextSecondary));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        button.Content = content;
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(IslandExtensions.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + IslandExtensions.Folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SaveStatus(ex.Message, true);
        }
    }

    private void RefreshPlugins()
    {
        if (_pluginsHost is not null) _pluginsHost.Child = PluginsList();
    }

    // The rows stand in the order the reader arranged them — read from DeckPages.Order, the same list the island's tabs
    // and the wheel walk — so this page cannot say one order and the panel another.
    private FrameworkElement PluginsList()
    {
        if (IslandExtensions.OccupiedCount == 0)
        {
            var empty = Wrapped(Loc.T("未装入插件。", "No plugins loaded."), false);
            empty.Margin = new Thickness(0, 6, 0, 8);
            return empty;
        }
        _pluginRows = DeckPages.Order(S).Select(DeckPages.SlotOf).Where(slot => slot >= 0 && IslandExtensions.Occupied(slot)).ToList();
        var list = new StackPanel();
        Array.Clear(_pluginHandles, 0, _pluginHandles.Length);
        for (int at = 0; at < _pluginRows.Count; at++) list.Children.Add(PluginRow(_pluginRows[at], at, _pluginRows.Count));
        // The rows carry their own divider, as the app list's do, so moving one moves its line with it.
        UpdateDividers(list);
        _pluginList = list;
        return list;
    }

    private List<int> _pluginRows = [];
    private StackPanel? _pluginList;

    // One loaded plugin: a header row — the handle it is dragged by, its glyph and title, its enable switch, and the arrow
    // that opens the settings under it; open, the page's size and the id and folder it came from. The switch is the plugin's
    // own: off takes its tab off the island and stops what it reads, in every state of the panel.
    private FrameworkElement PluginRow(int slot, int position, int total)
    {
        var extension = IslandExtensions.Slots[slot]!;
        string id = extension.Id, title = IslandExtensions.Title(slot);
        var entry = S.Extensions.FirstOrDefault(e => e.Id == id);
        bool enabled = entry?.Shown ?? true;
        string file = IslandExtensions.Source(slot);

        var column = new StackPanel();
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(DragHandle(slot, position, total));
        var glyph = new System.Windows.Shapes.Path
        {
            Data = IslandExtensions.SafeGlyph(extension.Glyph), Stroke = UI.Brush(TextSecondary), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(glyph, 1);
        header.Children.Add(glyph);
        var name = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 2);
        header.Children.Add(name);
        var enable = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(enable, Loc.T("启用 ", "Enable ") + title);
        enable.Checked += (_, _) => ChangePlugin(id, current => current with { Shown = true });
        enable.Unchecked += (_, _) => ChangePlugin(id, current => current with { Shown = false });
        Grid.SetColumn(enable, 3);
        header.Children.Add(enable);
        void Flip() { _pluginOpen[slot] = !_pluginOpen[slot]; RefreshPlugins(); }
        var arrow = ChevronButton(Loc.T("展开 ", "Expand ") + title, Flip);
        arrow.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(arrow, 4);
        header.Children.Add(arrow);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        column.Children.Add(header);

        var details = new StackPanel { Margin = new Thickness(42, 0, 0, 10), Visibility = _pluginOpen[slot] ? Visibility.Visible : Visibility.Collapsed };
        var dimensions = new Grid { MinHeight = 44 }; dimensions.ColumnDefinitions.Add(new()); dimensions.ColumnDefinitions.Add(new());
        // The page's own limits, which a plugin's manifest can raise: the fields offer exactly the sizes the page can take,
        // and show the size it is actually using even when an older file saved less.
        var page = DeckPages.ExtensionPage(slot);
        var (floorWidth, ceilingWidth, floorHeight, ceilingHeight) = DeckPages.Limits(S, page);
        var (usedWidth, usedHeight) = DeckPages.Size(S, page);
        var width = Dimension(Loc.T("宽度", "Width"), usedWidth, floorWidth, ceilingWidth, value => Resize(s => SetSlotSize(s, slot, value, null), page));
        width.Margin = new Thickness(0, 0, 20, 0); dimensions.Children.Add(width);
        var height = Dimension(Loc.T("高度", "Height"), usedHeight, floorHeight, ceilingHeight, value => Resize(s => SetSlotSize(s, slot, null, value), page));
        height.Margin = new Thickness(20, 0, 0, 0); Grid.SetColumn(height, 1); dimensions.Children.Add(height);
        details.Children.Add(dimensions);
        // The controls the plugin's own manifest declared. The host draws them — a reader sets a plugin up here instead
        // of opening its folder, and an author writes no settings interface at all.
        foreach (var row in DeclaredRows(id, IslandExtensions.Declared(slot))) details.Children.Add(row);
        // What the author declared that the host could not use. Without this a control that vanished reads as the host
        // ignoring it on purpose — the same reason a refused bridge message is answered.
        var notes = IslandExtensions.Notes(slot);
        if (notes.Count > 0)
        {
            var heading = TextLine(Loc.T("已忽略的声明项", "Declared entries the host skipped"), 11.5, TextTertiary);
            heading.Margin = new Thickness(0, 8, 0, 0);
            details.Children.Add(heading);
            foreach (var note in notes.Take(6))
            {
                var line = TextLine(note, 11.5, TextTertiary);
                line.TextWrapping = TextWrapping.Wrap;
                line.Margin = new Thickness(0, 2, 0, 0);
                details.Children.Add(line);
            }
            if (notes.Count > 6)
            {
                var more = TextLine(Loc.T($"另有 {notes.Count - 6} 条", $"{notes.Count - 6} more"), 11.5, TextTertiary);
                more.Margin = new Thickness(0, 2, 0, 0);
                details.Children.Add(more);
            }
        }
        var source = TextLine(id + (file.Length > 0 ? " · " + file : ""), 11.5, TextTertiary);
        source.Margin = new Thickness(0, 2, 0, 0);
        if (file.Length > 0) source.ToolTip = IslandExtensions.Folder + "\\" + file;
        details.Children.Add(source);
        column.Children.Add(details);
        PointArrow(arrow, _pluginOpen[slot]);
        // The row is what the list measures and moves: its own divider, its place marked by the slot it shows, and the
        // transform a drag lives in.
        return new Border
        {
            Child = column, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0),
            Tag = slot, RenderTransform = new TranslateTransform()
        };
    }

    // Writes one dimension of an extension page's size into its own settings entry. The other dimension is read back
    // first, and the host clamps both: a page never ends up smaller than its plugin asked for.
    private static void SetSlotSize(DeckSettings settings, int slot, double? width, double? height)
    {
        var page = DeckPages.ExtensionPage(slot);
        var (nowWidth, nowHeight) = DeckPages.Size(settings, page);
        DeckPages.SetSize(settings, page, width ?? nowWidth, height ?? nowHeight);
    }

    // The six dots a reader drags the row by, as the app list and the collapsed content are dragged: press and move, the
    // row lifts onto the page's own surface, and the rows it passes make room for it. Only this handle drags, so a click
    // anywhere else keeps opening and closing the row the way it always did. Reordering is by mouse only, like every
    // other arrangement in the product — so this handle is not a tab stop and offers no keys. With a single plugin there
    // is no place to move it to, so the space is kept — the rows line up either way — and the handle is not offered.
    private FrameworkElement DragHandle(int slot, int position, int total)
    {
        if (total < 2) return new Border { Width = 22, Margin = new Thickness(0, 0, 4, 0) };
        // The same six dots as the app list and the collapsed content: two columns of three, in the same 22-wide strip.
        var handle = new Button
        {
            Content = Grip(TextTertiary), Style = (Style)FindResource("SettingsGlyphButton"), Cursor = Cursors.SizeAll,
            Width = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        };
        _pluginHandles[slot] = handle;
        // The page's own drag, the one the app list and the collapsed content move by. The button would take the press
        // and let go of it itself first, so this handle is wired to what happens before that.
        AttachDrag(handle, () => _pluginList, PluginsReordered, preview: true);
        // Where the row stands is the point of the control, so its name says it: a screen reader hears which place this
        // row holds, and a reader who moves rows by mouse hears the row arrive at its new place.
        AutomationProperties.SetName(handle, PluginHandleName(slot, position, total));
        return handle;
    }

    private string PluginHandleName(int slot, int position, int total)
    {
        var title = IslandExtensions.Title(slot);
        return Loc.T($"{title} 的位置，第 {position + 1} 个，共 {total} 个", $"{title} position, {position + 1} of {total}");
    }

    // After a drop the rows already stand in their new order: nothing is rebuilt, or the rows would jump from where the
    // drag set them down. What the page says is what the settings are about to say — the same places the tabs will take.
    private void PluginsReordered(Panel list)
    {
        _pluginRows = list.Children.OfType<Border>().Select(row => (int)row.Tag!).ToList();
        for (int at = 0; at < _pluginRows.Count; at++)
            if (_pluginHandles[_pluginRows[at]] is { } handle)
                AutomationProperties.SetName(handle, PluginHandleName(_pluginRows[at], at, _pluginRows.Count));
        var ids = _pluginRows.Select(slot => IslandExtensions.Slots[slot]?.Id).ToList();
        if (ids.Any(id => string.IsNullOrEmpty(id))) return;
        Change(settings => settings.ReorderExtensions(ids.Select(id => id!).ToList()));
    }

    // The handle of each loaded slot, so a drop can rename the rows it moved: the list is not built again after a drag,
    // and each handle's name is the only place its row says where it stands.
    private readonly Button?[] _pluginHandles = new Button?[4];

    // Writes the plugin's own entry, creating it on the first change so an untouched plugin keeps its defaults.
    private void ChangePlugin(string id, Func<ExtensionPageSettings, ExtensionPageSettings> edit)
    {
        Change(s =>
        {
            var at = s.Extensions.FindIndex(e => e.Id == id);
            if (at < 0) { s.Extensions.Add(edit(new ExtensionPageSettings(id))); return; }
            s.Extensions[at] = edit(s.Extensions[at]);
        });
        RefreshPlugins();
    }

    // One row per declared control, in the order the manifest wrote them. The declaration is the whole design: the
    // labels are the host's own words in the interface's language, the number's range and step come from the author,
    // and a plugin with nothing declared gets the row exactly as it was.
    private IEnumerable<FrameworkElement> DeclaredRows(string id, IReadOnlyList<ExtensionSetting> declared)
    {
        if (declared.Count == 0) yield break;
        var values = IslandExtensions.ValuesFor(id);
        foreach (var setting in declared)
        {
            var current = values.TryGetValue(setting.Key, out var kept) ? kept : ExtensionSettingStore.Initial(setting);
            var label = setting.Label.For(Loc.IsEnglish);
            switch (setting.Kind)
            {
                case ExtensionSettingKind.Toggle:
                    var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = current.On, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(toggle, label);
                    toggle.Checked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("toggle", true));
                    toggle.Unchecked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("toggle", false));
                    yield return Row(label, toggle);
                    break;
                case ExtensionSettingKind.Number:
                    yield return SliderRow(label, current.Number, setting.Minimum, setting.Maximum, setting.Step, "",
                        value => WriteDeclared(id, setting, new ExtensionSettingValue("number", false, value)));
                    break;
                default:
                    var options = new UniformGrid { Columns = Math.Min(setting.Options.Count, 3) };
                    foreach (var option in setting.Options)
                    {
                        var words = option.Label.For(Loc.IsEnglish);
                        var choice = new RadioButton
                        {
                            Content = words, Style = (Style)FindResource("SettingsSegment"),
                            GroupName = id + "." + setting.Key, IsChecked = current.Choice == option.Value
                        };
                        AutomationProperties.SetName(choice, label + " · " + words);
                        choice.Checked += (_, _) => WriteDeclared(id, setting, new ExtensionSettingValue("choice", false, 0, option.Value));
                        options.Children.Add(choice);
                    }
                    yield return Row(label, SegmentBorder(options));
                    break;
            }
        }
    }

    // Written the way the settings window writes everything: a copy, the edit, the app's apply — and that apply is what
    // tells the plugin's own pages, so a click here and the page's setSetting end in the same place. The value is
    // honoured against the declaration on the way in, so the file can only hold what the author's own range allows.
    private void WriteDeclared(string id, ExtensionSetting setting, ExtensionSettingValue value)
        => Change(s => s.SetExtensionValue(id, setting.Key, ExtensionSettingStore.Honor(setting, value)));

    // A page wrote one of its own controls: the row this window shows has to say the same thing now.
    internal void PluginValuesChanged() { if (_pluginsHost is not null) RefreshPlugins(); }
}
