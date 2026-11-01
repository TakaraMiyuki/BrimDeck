using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;

namespace BrimDeck;

// The plugins page: the master switch, the folder and reload actions, then one row per DLL in the plugins folder —
// loaded or not, each file keeps its row in the order the reader arranges with its drag handle, and turning a plugin
// off greys its row where it stands instead of moving it. Expanding a row shows what the plugin registered, where it
// came from and, when it could not load, why. Enabling a plugin for the first time says what an in-process plugin can
// do before it is allowed to: this is the trust model, said out loud at the only moment the reader can still choose.
public sealed partial class SettingsWindow
{
    private const double PluginRowHeight = 44;
    // Which plugin rows stand open, kept while the page is rebuilt; keyed by the DLL file name.
    private readonly HashSet<string> _pluginOpenRows = [];
    // The search text the plugins list filters by; empty shows everything.
    private string _pluginSearch = "";

    private void PluginsPage()
    {
        // The master switch: off, the page is the switch alone and nothing loads.
        var master = new Grid { MinHeight = 40 };
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var masterText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        masterText.Children.Add(TextLine(Loc.T("启用插件", "Enable plugins"), 13, TextPrimary, FontWeights.SemiBold));
        Grid.SetColumn(masterText, 0);
        master.Children.Add(masterText);
        var masterToggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = S.EnablePlugins, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(masterToggle, Loc.T("启用插件", "Enable plugins"));
        // Rebuild the page at once, so the safe-mode note replaces the list the moment the switch moves.
        masterToggle.Checked += (_, _) => { Change(s => s.EnablePlugins = true); ShowPage(PluginsPageNumber); };
        masterToggle.Unchecked += (_, _) => { Change(s => s.EnablePlugins = false); ShowPage(PluginsPageNumber); };
        Grid.SetColumn(masterToggle, 1);
        master.Children.Add(masterToggle);
        Group(master);

        if (!S.EnablePlugins) return; // Off: the page is the switch alone.

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        actions.Children.Add(OutlineAction(Loc.T("安装插件", "Install plugin"),
            "M 8,1 V 9 M 8,9 L 4.5,5.5 M 8,9 L 11.5,5.5 M 2,11.5 H 14", InstallPlugin,
            Loc.T("选择插件的 DLL 文件，拷入插件文件夹并装入。", "Pick the plugin's DLL file; it is copied into the plugins folder and loaded.")));
        actions.Children.Add(OutlineAction(Loc.T("打开插件文件夹", "Open plugins folder"),
            "M 1.5,4.4 H 4.4 L 5.6,3 H 10.5 V 9.5 H 1.5 Z", OpenPluginsFolder,
            Loc.T("打开存放插件的目录。", "Opens the folder the plugins live in.")));
        var reload = OutlineAction(Loc.T("重新加载", "Reload"),
            "M 9.1,4.2 A 3.7,3.7 0 1 0 9.9,7.4 M 9.1,2.2 V 4.2 H 7.1", ReloadPlugins,
            Loc.T("重新扫描插件文件夹：新放入的插件被装入，被修改的插件被替换，被禁用的插件被卸下。重载后若表现异常，重启 BrimDeck。",
                "Scans the plugins folder again: plugins added are loaded, plugins changed are replaced, plugins disabled are retired. If anything misbehaves after a reload, restart BrimDeck."));
        reload.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reload);
        var reset = OutlineAction(Loc.T("恢复默认", "Restore defaults"),
            "M 8,2 A 6,6 0 1 0 13,5 M 13,1 V 5 H 9", ResetAllPlugins,
            Loc.T("把所有插件的开关、尺寸和设置还原为默认值。", "Resets every plugin's switches, sizes and settings to their defaults."));
        reset.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reset);
        var search = new TextBox { Style = (Style)FindResource("SettingsNumber"), Width = 180, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(search, Loc.T("搜索插件", "Search plugins"));
        search.TextChanged += (_, _) => { _pluginSearch = search.Text; ShowPage(PluginsPageNumber); search.Focus(); search.CaretIndex = search.Text.Length; };
        actions.Children.Add(search);
        _content.Children.Add(actions);

        var files = OrderedFiles();
        if (_pluginSearch.Length > 0)
            files = files.Where(file =>
            {
                var plugin = IslandExtensions.Plugins.FirstOrDefault(p => string.Equals(Path.GetFileName(p.File), file, StringComparison.OrdinalIgnoreCase));
                string hay = file;
                if (plugin is not null) try { hay = string.Join(" ", file, plugin.Id, plugin.Extension.TitleZh, plugin.Extension.TitleEn); } catch (Exception) { }
                return hay.Contains(_pluginSearch, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        SectionTitle(files.Count > 0
            ? Loc.T($"已安装（{files.Count}）", $"Installed ({files.Count})")
            : Loc.T("已安装", "Installed"));
        if (files.Count == 0)
        {
            var empty = Wrapped(Loc.T("未安装插件。把插件的 DLL 放进插件文件夹后点「重新加载」。", "No plugins installed. Drop a plugin's DLL into the plugins folder and press Reload."), false);
            empty.Margin = new Thickness(0, 6, 0, 8);
            _content.Children.Add(empty);
            return;
        }
        var list = new StackPanel();
        foreach (var file in files) list.Children.Add(PluginRow(file, list));
        Group(list);
        UpdateDividers(list);
        // The collapsed row had less room than the widgets asked: the reader can widen it or turn auto-width back on.
        if (IslandExtensions.CompactTruncated)
        {
            var crowded = Wrapped(Loc.T("收起行宽度不足，部分部件未完整显示；可加宽收起行或开启自动宽度。", "The collapsed row is too narrow for every widget; widen it or turn auto-width back on."), false);
            crowded.Margin = new Thickness(0, 6, 0, 8);
            _content.Children.Add(crowded);
        }
    }

    // Every DLL in the plugins folder, the reader's order first and the rest alphabetically — the same order the scan
    // loads in, so this page and the island cannot tell two different stories.
    private List<string> OrderedFiles()
    {
        var files = SafeDiskFiles();
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, index) in S.PluginsOrder.Select((name, index) => (name, index)))
            if (!rank.TryAdd(name, index)) rank[name] = index;
        return files.OrderBy(file => rank.TryGetValue(file, out var at) ? at : int.MaxValue)
            .ThenBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // One row per file: the drag handle, the glyph and title (greyed while disabled), the enable toggle and the arrow
    // that opens what the plugin registered, where it came from and why it may not have loaded.
    private FrameworkElement PluginRow(string file, StackPanel rows)
    {
        bool disabled = S.PluginsDisabledFiles.Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase));
        var plugin = IslandExtensions.Plugins.FirstOrDefault(p => string.Equals(Path.GetFileName(p.File), file, StringComparison.OrdinalIgnoreCase));
        var failure = IslandExtensions.Failures.FirstOrDefault(f => string.Equals(Path.GetFileName(f.File), file, StringComparison.OrdinalIgnoreCase));
        var skipped = failure.File is null ? IslandExtensions.Skipped.FirstOrDefault(s => string.Equals(Path.GetFileName(s.File), file, StringComparison.OrdinalIgnoreCase)) : default;
        // A file that is not loaded right now keeps the name and glyph of its last successful load, so the reader still
        // knows which plugin the row is.
        var known = S.PluginsKnown.FirstOrDefault(info => string.Equals(info?.File, file, StringComparison.OrdinalIgnoreCase));
        string title = file;
        if (plugin is not null)
            try { title = Loc.IsEnglish ? plugin.Extension.TitleEn : plugin.Extension.TitleZh; }
            catch (Exception) { title = plugin.Id; }
        else if (known is { } info)
        {
            string zh = info.TitleZh.Length > 0 ? info.TitleZh : info.Id, en = info.TitleEn.Length > 0 ? info.TitleEn : info.Id;
            title = Loc.IsEnglish ? en : zh;
            if (title.Length == 0) title = file;
        }
        bool open = _pluginOpenRows.Contains(file);

        Button arrow = null!;
        Border item = null!;
        StackPanel details = null!;
        void Flip()
        {
            bool now = !_pluginOpenRows.Remove(file);
            if (now) _pluginOpenRows.Add(file);
            details.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            PointArrow(arrow, now);
            item.Height = now ? double.NaN : PluginRowHeight;
        }

        var header = new Grid { MinHeight = PluginRowHeight, Background = Brushes.Transparent };
        // The drag handle: the plugins' order here is the load order and the sidebar's order (the display order lives
        // in the collapsed and expanded views' own editors).
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string? glyphData = null;
        if (plugin is not null) try { glyphData = plugin.Extension.Glyph; } catch (Exception) { }
        else if (known is { } info && info.Glyph.Length > 0) glyphData = info.Glyph;
        var glyph = new System.Windows.Shapes.Path
        {
            Data = IslandExtensions.SafeGlyph(glyphData),
            Stroke = UI.Brush(TextSecondary), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 16, Height = 16, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Opacity = disabled ? 0.4 : 1
        };
        Grid.SetColumn(glyph, 0);
        header.Children.Add(glyph);
        var name = TextLine(title, 12.5, disabled ? TextSecondary : TextPrimary);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        // The state the row is in: what the reader needs to know at a glance.
        string state = disabled ? Loc.T("已停用", "Disabled")
            : failure.File is not null ? Loc.T("加载失败", "Failed to load")
            : plugin is not null && plugin.Extension.MinHostVersion > BrimDeck.Island.IslandHost.Version ? Loc.T($"需要更新 BrimDeck（此插件需要 v{plugin.Extension.MinHostVersion}）", "Needs a newer BrimDeck (this plugin asks for v" + plugin.Extension.MinHostVersion + ")")
            : Loc.T("正常", "Normal");
        var stateLabel = TextLine(state, 11.5, disabled || failure.File is not null ? TextTertiary : UI.Secondary);
        stateLabel.VerticalAlignment = VerticalAlignment.Center; stateLabel.Margin = new Thickness(12, 0, 12, 0);
        stateLabel.MinWidth = 90; stateLabel.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(stateLabel, 2);
        header.Children.Add(stateLabel);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = !disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(toggle, Loc.T("启用 ", "Enable ") + title);
        toggle.Checked += (_, _) => EnablePlugin(file);
        toggle.Unchecked += (_, _) => DisablePlugin(file);
        Grid.SetColumn(toggle, 3);
        header.Children.Add(toggle);
        arrow = ChevronButton(Loc.T("展开插件信息", "Show plugin details"), Flip);
        arrow.Margin = new Thickness(4, 0, 0, 0);
        PointArrow(arrow, open);
        Grid.SetColumn(arrow, 4);
        header.Children.Add(arrow);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };
        var detailsBody = new StackPanel { Margin = new Thickness(26, 0, 0, 10), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        foreach (var line in DetailLines(file, plugin, known, failure, skipped)) detailsBody.Children.Add(line);
        details = detailsBody;

        // The drag handle needs the row shape AttachDrag expects: the handle in a Grid whose parent is the row Border.
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var body = new StackPanel(); body.Children.Add(header); body.Children.Add(details);
        body.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(body, 1);
        var handle = new Border { Width = 22, Height = PluginRowHeight, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        row.Children.Add(handle); row.Children.Add(body);
        Grid.SetColumn(body, 1);
        item = new Border { Child = row, Height = open ? double.NaN : PluginRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = file, RenderTransform = new TranslateTransform() };
        // The plugins' order here is the load order and the sidebar's order (the display order lives in the collapsed
        // and expanded views' own editors).
        AttachDrag(handle, () => rows, PluginRowHeight, arranged =>
        {
            var order = arranged.Children.OfType<Border>().Where(b => b.Tag is string).Select(b => (string)b.Tag).ToList();
            Change(s => s.PluginsOrder = order);
            ReloadPlugins();
        }, null);
        return item;
    }

    // The facts a reader needs, by state: what a loaded plugin registered and where it came from; why a file failed or
    // was skipped. A disabled row adds nothing — the greyed name and its switch already tell that story — and the file
    // name always comes last, as the toggle is written against it.
    private IEnumerable<FrameworkElement> DetailLines(string file, IslandExtensions.Plugin? plugin, PluginInfo? known,
        (string File, string Error) failure, (string File, string Note) skipped)
    {
        bool disabled = S.PluginsDisabledFiles.Any(name => string.Equals(name, file, StringComparison.OrdinalIgnoreCase));
        if (failure.File is not null)
            yield return TextLine(Loc.T("未能加载：", "Could not load: ") + failure.Error, 11.5, TextSecondary);
        else if (skipped.File is not null)
            yield return TextLine(skipped.Note, 11.5, TextSecondary);
        else if (plugin is not null)
        {
            string version = "", repository = "";
            try { version = plugin.Extension.Version; repository = plugin.Extension.Repository; } catch (Exception) { }
            yield return TextLine(
                plugin.Id + (version.Length > 0 ? " · v" + version : "") +
                Loc.T($" · 注册了 {plugin.Host.Pages.Count} 个页面 · {plugin.Host.Compacts.Count} 个胶囊部件",
                    $" · registered {plugin.Host.Pages.Count} page(s) · {plugin.Host.Compacts.Count} compact widget(s)"),
                11.5, TextSecondary);
            if (repository.Length > 0) yield return TextLine(repository, 11.5, TextTertiary);
        }
        else if (!disabled && known is { } info && info.Id.Length > 0)
            yield return TextLine(info.Id, 11.5, TextSecondary);
        yield return TextLine(file, 11, TextTertiary);
    }

    // An outline button with a small stroke glyph before its text, as the app rules' add button is.
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

    // Enabling a plugin the first time says what the trust model is, plainly. Refusing leaves the file disabled.
    private void EnablePlugin(string file)
    {
        var watch = Stopwatch.StartNew();
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
        ExtensionLog.Write($"ui: enable {file} prepared at {watch.ElapsedMilliseconds}ms");
        ReloadPlugins();
    }

    private void DisablePlugin(string file)
    {
        var watch = Stopwatch.StartNew();
        ExtensionLog.Write("ui: disable " + file);
        Change(s => { if (!s.PluginsDisabledFiles.Contains(file)) s.PluginsDisabledFiles.Add(file); });
        ExtensionLog.Write($"ui: disable {file} settings applied at {watch.ElapsedMilliseconds}ms");
        ReloadPlugins();
    }

    private void ReloadPlugins()
    {
        var watch = Stopwatch.StartNew();
        IslandExtensions.SetSettings(S);
        IslandExtensions.Load(S);
        _app.RememberKnownPlugins();
        ExtensionLog.Write($"ui: reload scan took {watch.ElapsedMilliseconds}ms");
        watch.Restart();
        ShowPage(PluginsPageNumber);
        ExtensionLog.Write($"ui: reload page rebuild took {watch.ElapsedMilliseconds}ms");
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

    // 安装插件: pick a DLL, copy it into the plugins folder, and reload so it loads at once.
    private void InstallPlugin()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择插件", "Choose a plugin"),
            Filter = Loc.T("插件 (*.dll)|*.dll", "Plugins (*.dll)|*.dll"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(IslandExtensions.Folder);
            var target = Path.Combine(IslandExtensions.Folder, Path.GetFileName(dialog.FileName));
            File.Copy(dialog.FileName, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { SaveStatus(ex.Message, true); return; }
        ReloadPlugins();
    }

    // 恢复默认: every plugin's switches, sizes and settings entries go back to their defaults; the plugins reload.
    private void ResetAllPlugins()
    {
        var choice = MessageBox.Show(this,
            Loc.T("要把所有插件的开关、尺寸和设置还原为默认值吗？", "Reset every plugin's switches, sizes and settings to their defaults?"),
            "BrimDeck", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return;
        Change(s => { s.Extensions = []; s.PluginsOrder = []; });
        ReloadPlugins();
    }

    private List<string> SafeDiskFiles()
    {
        try { return Directory.Exists(IslandExtensions.Folder) ? Directory.EnumerateFiles(IslandExtensions.Folder, "*.dll").Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToList() : []; }
        catch (Exception) { return []; }
    }
}
