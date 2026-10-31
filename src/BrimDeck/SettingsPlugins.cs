using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The plugins page: the master switch, the folder and reload actions, one row per plugin with its enable toggle, and
// the honest lists underneath — what could not be loaded, and what was skipped with the reason. Enabling a plugin for
// the first time says what an in-process plugin can do before it is allowed to: this is the trust model, said out loud
// at the only moment the reader can still choose.
public sealed partial class SettingsWindow
{
    private StackPanel? _pluginsHost;

    private void PluginsPage()
    {
        // The master switch: off, the page is the switch alone and nothing loads.
        var master = new Grid { MinHeight = 40 };
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var masterText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        masterText.Children.Add(TextLine(Loc.T("启用插件", "Enable plugins"), 13, TextPrimary, FontWeights.SemiBold));
        masterText.Children.Add(TextLine(Loc.T("关闭后不加载任何插件；插件的数据保留，重新打开后恢复。", "Nothing loads while it is off; plugin data is kept and comes back with the switch."), 11, TextSecondary));
        Grid.SetColumn(masterText, 0);
        master.Children.Add(masterText);
        var masterToggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.EnablePlugins, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(masterToggle, Loc.T("启用插件", "Enable plugins"));
        masterToggle.Checked += (_, _) => Change(s => s.EnablePlugins = true);
        masterToggle.Unchecked += (_, _) => Change(s => s.EnablePlugins = false);
        Grid.SetColumn(masterToggle, 1);
        master.Children.Add(masterToggle);
        Group(master);

        if (!S.EnablePlugins)
        {
            var note = Wrapped(Loc.T("插件功能已关闭（安全模式）。删除或调整 %LOCALAPPDATA%\\BrimDeck\\plugins 里的文件不受影响；重新打开开关即恢复。", "The plugin system is off (safe mode). Files in %LOCALAPPDATA%\\BrimDeck\\plugins are untouched; turning the switch back on restores everything."), false);
            note.Margin = new Thickness(0, 6, 0, 8);
            _content.Children.Add(note);
            return;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 18) };
        actions.Children.Add(ActionButton(Loc.T("打开插件文件夹", "Open plugins folder"), OpenPluginsFolder));
        var reload = ActionButton(Loc.T("重新加载", "Reload"), ReloadPlugins);
        reload.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reload);
        _content.Children.Add(actions);

        var loaded = IslandExtensions.Plugins.Where(plugin => plugin.Slot is >= 0 and < 4).ToList();
        var widgetOnly = IslandExtensions.Plugins.Where(plugin => plugin.Slot < 0).ToList();
        SectionTitle(Loc.T("已加载", "Loaded"));
        if (loaded.Count == 0 && widgetOnly.Count == 0)
        {
            var empty = Wrapped(Loc.T("未安装插件。把插件的 DLL 放进插件文件夹后点「重新加载」。", "No plugins installed. Drop a plugin's DLL into the plugins folder and press Reload."), false);
            empty.Margin = new Thickness(0, 6, 0, 8);
            _content.Children.Add(empty);
        }
        else
        {
            var list = new StackPanel();
            bool first = true;
            foreach (var plugin in loaded.Concat(widgetOnly))
            {
                if (!first) { list.Children.Add(new Border { Height = 1, Background = UI.Brush("#2A2A30"), Opacity = 0.6 }); }
                first = false;
                list.Children.Add(PluginRow(plugin));
            }
            Group(list);
        }

        // The files on disk that are not loaded right now, by their toggle: disabled ones and never-enabled strays.
        var disk = SafeDiskFiles();
        var unknown = disk.Where(file => !IslandExtensions.Plugins.Any(plugin => string.Equals(plugin.File, file, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            SectionTitle(Loc.T("未加载的文件", "Not loaded"));
            var rows = new StackPanel();
            bool firstFile = true;
            foreach (var file in unknown)
            {
                if (!firstFile) rows.Children.Add(new Border { Height = 1, Background = UI.Brush("#2A2A30"), Opacity = 0.6 });
                firstFile = false;
                rows.Children.Add(UnknownRow(file));
            }
            Group(rows);
        }

        if (IslandExtensions.Failures.Count > 0)
        {
            SectionTitle(Loc.T("未能加载", "Could not load"));
            Group(IslandExtensions.Failures.Select(failure => NoteRow(System.IO.Path.GetFileName(failure.File), Wrapped(failure.Error, true))).ToArray());
        }
        if (IslandExtensions.Skipped.Count > 0)
        {
            SectionTitle(Loc.T("已跳过", "Skipped"));
            Group(IslandExtensions.Skipped.Select(skipped => NoteRow(System.IO.Path.GetFileName(skipped.File), Wrapped(skipped.Note, false))).ToArray());
        }
    }

    // One loaded plugin: glyph, name, its toggle, and underneath the facts a reader needs — id, version, repository,
    // where it came from, and what of the island it uses.
    private FrameworkElement PluginRow(IslandExtensions.Plugin plugin)
    {
        string file = System.IO.Path.GetFileName(plugin.File);
        bool page = plugin.Slot is >= 0 and < 4;
        string title;
        try { title = Loc.IsEnglish ? plugin.Extension.TitleEn : plugin.Extension.TitleZh; }
        catch (Exception) { title = plugin.Id; }

        var column = new StackPanel();
        var header = new Grid { MinHeight = 44, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new System.Windows.Shapes.Path
        {
            Data = IslandExtensions.SafeGlyph(plugin.Extension.Glyph),
            Stroke = UI.Brush(TextSecondary), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        };
        header.Children.Add(glyph);
        var name = TextLine(title, 13, TextPrimary, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(toggle, Loc.T("启用 ", "Enable ") + title);
        toggle.Unchecked += (_, _) => DisablePlugin(file);
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        column.Children.Add(header);

        var details = new StackPanel { Margin = new Thickness(26, 0, 0, 10) };
        string version = "";
        string repository = "";
        try { version = plugin.Extension.Version; repository = plugin.Extension.Repository; } catch (Exception) { }
        string uses = (page ? Loc.T("页面", "Page") : "") + (plugin.Host.Compacts.Count > 0 ? (page ? " · " : "") + Loc.T("胶囊", "Capsule") : "");
        details.Children.Add(TextLine(
            plugin.Id + (version.Length > 0 ? " · v" + version : "") + (uses.Length > 0 ? " · " + uses : ""),
            11.5, TextSecondary));
        if (repository.Length > 0)
            details.Children.Add(TextLine(repository, 11.5, TextTertiary));
        details.Children.Add(TextLine(file, 11, TextTertiary));
        column.Children.Add(details);
        return column;
    }

    // A file on disk that is not loaded: disabled by its toggle (shown off, ready to turn back on), or one this scan
    // could not place. The toggle is written against the file name, which is stable whether or not the DLL loads.
    private FrameworkElement UnknownRow(string file)
    {
        bool disabled = S.PluginsDisabledFiles.Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase));
        var header = new Grid { MinHeight = 40 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = TextLine(file, 12.5, disabled ? TextSecondary : TextPrimary);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 0);
        header.Children.Add(name);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = !disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(toggle, Loc.T("启用 ", "Enable ") + file);
        toggle.Checked += (_, _) => EnablePlugin(file);
        toggle.Unchecked += (_, _) => DisablePlugin(file);
        Grid.SetColumn(toggle, 1);
        header.Children.Add(toggle);
        return header;
    }

    // Enabling a plugin the first time says what the trust model is, plainly. Refusing leaves the file disabled.
    private void EnablePlugin(string file)
    {
        if (!S.PluginsWarnedFiles.Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase)))
        {
            var choice = MessageBox.Show(this,
                Loc.T("插件与 BrimDeck 运行在同一进程，可以读取本机文件和 BrimDeck 保存的数据（包括密钥）。\n\n请只安装来源可信的插件。要启用它吗？",
                      "A plugin runs in BrimDeck's own process and can read local files and everything BrimDeck has saved (including keys).\n\nOnly install plugins from sources you trust. Enable it?"),
                "BrimDeck", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) { ReloadPlugins(); return; }
            Change(s => { if (!s.PluginsWarnedFiles.Contains(file)) s.PluginsWarnedFiles.Add(file); });
        }
        Change(s => s.PluginsDisabledFiles.RemoveAll(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase)));
        ReloadPlugins();
    }

    private void DisablePlugin(string file)
    {
        Change(s => { if (!s.PluginsDisabledFiles.Contains(file)) s.PluginsDisabledFiles.Add(file); });
        ReloadPlugins();
    }

    private void ReloadPlugins()
    {
        IslandExtensions.SetSettings(S);
        IslandExtensions.Load(S);
        ShowPage(PluginsPageNumber);
    }

    private void OpenPluginsFolder()
    {
        try
        {
            Directory.CreateDirectory(IslandExtensions.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + IslandExtensions.Folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { SaveStatus(ex.Message, true); }
    }

    private List<string> SafeDiskFiles()
    {
        try { return Directory.Exists(IslandExtensions.Folder) ? Directory.EnumerateFiles(IslandExtensions.Folder, "*.dll").Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToList() : []; }
        catch (Exception) { return []; }
    }

    // One labelled value row, as the failure and skip lists use.
    private FrameworkElement NoteRow(string title, FrameworkElement content)
    {
        var column = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        column.Children.Add(TextLine(title, 12.5, TextPrimary, FontWeights.SemiBold));
        content.Margin = new Thickness(0, 3, 0, 0);
        column.Children.Add(content);
        return column;
    }
}
