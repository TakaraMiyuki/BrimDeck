using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Island;

namespace BrimDeck;

// The settings pages of the enabled plugins: the metadata the contract carries (icon, name, author, version,
// description), the plugin's own settings view built through the host's control factory, and the sections BrimDeck
// shows itself — the page's size and the uninstall and folder buttons. One page per enabled plugin, in the order the
// plugins page arranges; a page whose plugin is disabled or gone falls back to the plugin manager.
public sealed partial class SettingsWindow
{
        private void EnsureCompactKeys() => IslandExtensions.EnsureCompactKeys(S);

// One plugin's settings page: the contract's metadata at the top, the plugin's own settings view (built through
    // the host's control factory), and the sections BrimDeck shows itself — the page's size and the uninstall and
    // folder buttons. The permissions the plugin will ask for arrive with a later stage.
    private void PluginSettingsPage(int index)
    {
        if (index < 0 || index >= _pluginNavPages.Count) return;
        var plugin = _pluginNavPages[index];
        string author = "", description = "";
        try { author = plugin.Extension.Author; description = plugin.Extension.Description; } catch (Exception) { }

        // The metadata block: the glyph, the version and the author, then the description.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        meta.Children.Add(new System.Windows.Shapes.Path
        {
            Data = IslandExtensions.SafeGlyph(plugin.Extension.Glyph),
            Stroke = UI.Brush(TextPrimary), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 24, Height = 24, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
        });
        string version = "", pluginAuthor = "";
        try { version = plugin.Extension.Version; pluginAuthor = plugin.Extension.Author; } catch (Exception) { }
        var facts = string.Join(" · ", new[] { plugin.Id, version.Length > 0 ? "v" + version : "", pluginAuthor }.Where(part => part.Length > 0));
        var metaText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        metaText.Children.Add(TextLine(facts.Length > 0 ? facts : plugin.Id, 12, TextSecondary));
        meta.Children.Add(metaText);
        _content.Children.Add(meta);
        if (description.Length > 0)
        {
            var line = TextLine(description, 12, TextSecondary); line.TextWrapping = TextWrapping.Wrap; line.Margin = new Thickness(0, 0, 0, 16);
            _content.Children.Add(line);
        }
        else _content.Children.Add(new Border { Height = 10 });

        // The page's size, as the plugin asked or the reader dragged, with a reset back to the plugin's ask.
        int slot = IslandExtensions.Plugins.IndexOf(plugin);
        var (pageWidth, pageHeight) = IslandExtensions.PreferredSize(slot) ?? ExtensionSizes.Default;
        var entry = S.Extensions.FirstOrDefault(extension => extension.Id == plugin.Id);
        if (entry is { Width: > 0 }) (pageWidth, pageHeight) = (entry.Width, entry.Height > 0 ? entry.Height : pageHeight);
        SectionTitle(Loc.T("管理", "Management"));
        Group(
            Row(Loc.T("页面尺寸", "Page size"),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Children = { TextLine($"{pageWidth:0} × {pageHeight:0}", 12, TextSecondary),
                        new Button { Style = (Style)FindResource("SettingsLink"), Content = Loc.T("重置 ↗", "Reset ↗"), Margin = new Thickness(12, 0, 0, 0) } }
                }),
            Row(Loc.T("卸载插件", "Uninstall"), UninstallButton(plugin)),
            Row(Loc.T("插件文件夹", "Plugin folder"), OpenPluginFolderButton()));

        // The plugin's own settings, built now with the palette of the moment.
        SectionTitle(Loc.T("插件设置", "Plugin settings"));
        try
        {
            var view = plugin.Extension.CreateSettingsView(IslandExtensions.Colors);
            if (view is not null) { _content.Children.Add(view); return; }
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"plugin {plugin.Id}: CreateSettingsView failed: {ex.Message.Split('\n')[0]}");
            Group(TextLine(Loc.T("插件设置加载失败。", "The plugin's settings failed to load."), 12, TextSecondary));
            return;
        }
        Group(TextLine(Loc.T("此插件没有自己的设置项。", "This plugin has no settings of its own."), 12, TextSecondary));
    }

    // The uninstall button: one confirmation, then the DLL leaves the folder and the plugins reload.
    private Button UninstallButton(IslandExtensions.Plugin plugin)
    {
        string file = Path.GetFileName(plugin.File);
        var button = ActionButton(Loc.T("卸载 ↗", "Uninstall ↗"), () =>
        {
            var choice = MessageBox.Show(this,
                Loc.T($"要卸载 {file} 吗？插件的数据保留，重新安装后恢复。", $"Uninstall {file}? The plugin's data is kept and comes back when it is installed again."),
                "BrimDeck", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes) return;
            try { File.Delete(plugin.File); } catch (Exception ex) { SaveStatus(ex.Message, true); return; }
            IslandExtensions.SetSettings(S);
            IslandExtensions.Load(S);
            ShowPage(PluginsPageNumber);
        });
        return button;
    }

    private Button OpenPluginFolderButton()
    {
        var button = ActionButton(Loc.T("打开 ↗", "Open ↗"), () =>
        {
            try
            {
                Directory.CreateDirectory(IslandExtensions.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + IslandExtensions.Folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { SaveStatus(ex.Message, true); }
        });
        return button;
    }
}
