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
    // The rows as built, so a drag's start can collapse the open ones in place instead of rebuilding the page.
    private readonly List<(Border Row, StackPanel Details, Button Arrow)> _pluginRowRefs = [];

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

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 18) };
        actions.Children.Add(OutlineAction(Loc.T("打开插件文件夹", "Open plugins folder"),
            "M 1.5,4.4 H 4.4 L 5.6,3 H 10.5 V 9.5 H 1.5 Z", OpenPluginsFolder,
            Loc.T("打开存放插件的目录。", "Opens the folder the plugins live in.")));
        var reload = OutlineAction(Loc.T("重新加载", "Reload"),
            "M 9.1,4.2 A 3.7,3.7 0 1 0 9.9,7.4 M 9.1,2.2 V 4.2 H 7.1", ReloadPlugins,
            Loc.T("重新扫描插件文件夹：新放入的插件被装入，被修改的插件被替换，被禁用的插件被卸下；无需重启本软件。",
                "Scans the plugins folder again: plugins added are loaded, plugins changed are replaced, plugins disabled are retired; no restart needed."));
        reload.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(reload);
        _content.Children.Add(actions);

        var files = OrderedFiles();
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
        _pluginRowRefs.Clear();
        foreach (var file in files) list.Children.Add(PluginRow(file, list));
        Group(list);
        UpdateDividers(list);
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
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        header.Children.Add(glyph);
        var name = TextLine(title, 12.5, disabled ? TextSecondary : TextPrimary);
        name.VerticalAlignment = VerticalAlignment.Center; name.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = !disabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(toggle, Loc.T("启用 ", "Enable ") + title);
        toggle.Checked += (_, _) => EnablePlugin(file);
        toggle.Unchecked += (_, _) => DisablePlugin(file);
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        arrow = ChevronButton(Loc.T("展开插件信息", "Show plugin details"), Flip);
        arrow.Margin = new Thickness(4, 0, 0, 0);
        PointArrow(arrow, open);
        Grid.SetColumn(arrow, 3);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(arrow);
        header.MouseLeftButtonDown += (_, e) => { Flip(); e.Handled = true; };

        var detailsBody = new StackPanel { Margin = new Thickness(26, 0, 0, 10), Visibility = open ? Visibility.Visible : Visibility.Collapsed };
        foreach (var line in DetailLines(file, plugin, known, failure, skipped)) detailsBody.Children.Add(line);
        details = detailsBody;

        var body = new StackPanel(); body.Children.Add(header); body.Children.Add(details);
        body.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(body, 1);
        var handle = new Border { Width = 22, Height = PluginRowHeight, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Left, Child = Grip(TextTertiary) };
        var row = new Grid { Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PartHandleWidth) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(handle); row.Children.Add(body);
        item = new Border { Child = row, Height = open ? double.NaN : PluginRowHeight, BorderBrush = UI.Brush(_palette.RowDivider), BorderThickness = new Thickness(0), Tag = file, RenderTransform = new TranslateTransform() };
        _pluginRowRefs.Add((item, details, arrow));
        // The details close when a drag begins, so every row is one line high while they move.
        AttachDrag(handle, () => rows, PluginRowHeight, arranged =>
        {
            var order = arranged.Children.OfType<Border>().Where(b => b.Tag is string).Select(b => (string)b.Tag).ToList();
            Change(s =>
            {
                s.PluginsOrder = order;
                // The page switch follows at once: the plugin pages are re-laid in the plugins' order, keeping the
                // block position they hold among the built-in pages.
                var pageOf = IslandExtensions.Plugins.Where(plugin => plugin.Slot is >= 0 and < 4)
                    .GroupBy(plugin => Path.GetFileName(plugin.File), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => DeckPage.Extension0 + group.First().Slot);
                var sequence = order.Where(pageOf.ContainsKey).Select(file => pageOf[file]).ToList();
                int at = 0;
                s.PageOrder = s.PageOrder.Select(page => DeckPages.IsExtensionPage(page) && at < sequence.Count ? sequence[at++] : page).ToList();
            });
        }, ClosePluginRows);
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

    private void ClosePluginRows()
    {
        _pluginOpenRows.Clear();
        foreach (var (row, details, arrow) in _pluginRowRefs)
        {
            details.Visibility = Visibility.Collapsed;
            PointArrow(arrow, false);
            row.Height = PluginRowHeight;
        }
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

    private List<string> SafeDiskFiles()
    {
        try { return Directory.Exists(IslandExtensions.Folder) ? Directory.EnumerateFiles(IslandExtensions.Folder, "*.dll").Select(Path.GetFileName).Where(name => name is not null).Select(name => name!).ToList() : []; }
        catch (Exception) { return []; }
    }
}
