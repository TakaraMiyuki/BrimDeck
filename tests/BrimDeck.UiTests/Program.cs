using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace BrimDeck.UiTests;

// A black-box smoke test for the extension host. It runs the real app against a temporary profile with two tiny web
// plugins and walks the path that matters most: first visit, switch away, switch back. A page that is put back after
// its WebView was disposed shows nothing, so the mark text disappearing is the failure this test exists for.
//
// It needs a desktop session and no other BrimDeck panel on screen (two panels would both open at the top centre).
// The app is started with --data-dir, so the user's settings, plugins and window layout are never touched.
internal static class Program
{
    private const string MarkA = "fixture-a";
    private const string MarkB = "fixture-b";
    private const string MarkC = "fixture-c";
    private const string MarkD = "fixture-d";
    private static readonly Color ColorA = Color.FromArgb(192, 0, 0);
    private static readonly Color ColorB = Color.FromArgb(0, 192, 0);
    // C starts dark and turns blue only when the bridge has said everything the page has to hear.
    private static readonly Color ColorC = Color.FromArgb(0, 0, 192);
    // D turns purple only when its declared controls arrived, and came back the way the host honours them.
    private static readonly Color ColorD = Color.FromArgb(156, 39, 176);
    private static AutomationElement? _window;

    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null) return 2;
        if (!options.AllowParallel && Process.GetProcessesByName("BrimDeck").Length > 0)
        {
            Console.Error.WriteLine("A BrimDeck instance is already running; close it first (or pass --allow-parallel).");
            return 2;
        }
        if (!File.Exists(options.App))
        {
            Console.Error.WriteLine("BrimDeck.exe not found: " + options.App + " (pass --app <path>)");
            return 2;
        }

        SetProcessDpiAwarenessContext(-4);
        var data = options.Data ?? Path.Combine(Path.GetTempPath(), "BrimDeck-UiTests-" + Guid.NewGuid().ToString("N"));
        // This is a test profile: the suite writes fixture plugins into it, lets their pages save into it, and
        // rearranges the plugin order it holds. A --data naming the folder BrimDeck really uses is refused at the door,
        // where one guard covers every step, instead of being noticed in the settings file the run rewrote.
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrimDeck");
        if (string.Equals(data.TrimEnd('\\', '/'), real, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--data is the profile BrimDeck uses for real settings (" + real
                + "). Leave --data out to run on a throwaway folder.");
            return 2;
        }
        Directory.CreateDirectory(data);
        var log = Path.Combine(data, "extensions.log");
        WriteFixture(data, "fixture-a", "FixtureA", MarkA, "#C00000", minWidth: 440, minHeight: 220);
        WriteFixture(data, "fixture-b", "FixtureB", MarkB, "#00C000");
        WriteFixture(data, "fixture-c", "FixtureC", MarkC, "#202020", bridgeProbe: true);
        WriteDeclaredFixture(data);
        Console.WriteLine("profile: " + data);
        Console.WriteLine("app: " + options.App);

        using var app = new BrimDeckProcess(options.App, data, log);
        try
        {
            var window = WaitFor(app.MainWindow, TimeSpan.FromSeconds(20), "the panel window")
                ?? throw new TestFailure("the app did not show its window; see " + Path.Combine(data, "crash.log"));
            _window = window;

            var tabA = ExpandAndFind(window, "FixtureA");
            Press(tabA);
            Reopen();
            Require(WaitForColorWithReopen(ColorA, TimeSpan.FromSeconds(8)), "first visit shows A (red page)");
            Console.WriteLine("PASS first visit shows A");

            Press(Find(window, "FixtureB"));
            Reopen();
            Require(WaitForColorWithReopen(ColorB, TimeSpan.FromSeconds(8)), "switching A -> B shows B (green page)");
            Console.WriteLine("PASS switching A -> B shows B");

            Press(Find(window, "FixtureA"));
            Reopen();
            Require(WaitForColorWithReopen(ColorA, TimeSpan.FromSeconds(8)), "switching back B -> A shows A (red page)");
            Console.WriteLine("PASS switching back B -> A shows A");

            Hide();
            Thread.Sleep(1200);
            Expand();
            Thread.Sleep(1500);
            KeepOpen();
            Require(WaitForColorWithReopen(ColorA, TimeSpan.FromSeconds(8)), "hiding and showing again keeps A (red page)");
            Console.WriteLine("PASS hiding and showing again keeps A");

            // Two units the plugin author writes in, and the panel has to honour both. Its manifest minimum is the page's
            // own viewport, so the panel's header and margins go on top — a 440×220 ask means a 480×266 panel. And one CSS
            // pixel has to be one panel DIP: a browser works at the display's scale, which need not be the scale this
            // window is drawn at (measured 1.14 against 1.0, where a 400×174 box handed the page 352×153 CSS pixels).
            var fit = WaitForFit(log, "fixture-a", TimeSpan.FromSeconds(4));
            Require(fit is { } seen && seen.Box.W is >= 438 and <= 442 && seen.Box.H is >= 218 and <= 222
                    && Math.Abs(seen.Box.W - seen.Page.W) <= 2 && Math.Abs(seen.Box.H - seen.Page.H) <= 2,
                fit is { } reported
                    ? $"the page gets the viewport its manifest asked for, in the panel's own pixels (box {reported.Box.W}x{reported.Box.H}, page {reported.Page.W}x{reported.Page.H})"
                    : "the page reports the box it was laid out in");
            Console.WriteLine("PASS the page gets the viewport its manifest asked for");

            // What the page hears back. A host that drops a message silently is indistinguishable, from inside a web
            // page, from one that is busy: the author's only tool is the reason the host sends, so a refusal has to
            // arrive as an event the page can act on — and C paints itself blue only once both refusals and the
            // bridge's own version have come through.
            Press(Find(window, "FixtureC"));
            Reopen();
            Require(WaitForColorWithReopen(ColorC, TimeSpan.FromSeconds(8)),
                "C turns blue only after the host answers with its version and the two refusals");
            Console.WriteLine("PASS the host's refusals reach the page as error events");
            Require(CountLog(log, "view: refused fixture-c state-too-large") > 0, "the host says why it dropped an oversized state");
            Require(CountLog(log, "view: refused fixture-c link-refused") > 0, "a link that is not a web address is refused, not opened");
            // The page's own jump to a real web address must stay a refusal. It is the case that reads the browser's
            // "was this clicked?" answer as the guardrail — and that answer says yes to a scripted location change,
            // which is how a plugin would otherwise open a desktop tab nobody asked for.
            Require(CountLog(log, "view: refused navigation fixture-c") > 0, "a page that navigates itself out to the web stays inside the plugin");
            Require(CountLog(log, "view: link fixture-c") == 0, "no desktop browser was opened for a link nobody clicked");
            Console.WriteLine("PASS the refusals the page got are the ones the host logged");

            // The declared controls, both ways round. The page cannot paint itself purple until init has handed it the
            // three values and its own words; and it cannot know the round trip works until the number it wrote (63, on
            // a control whose step is five) has come back as the 65 its declaration allows.
            Press(Find(window, "FixtureD"));
            Reopen();
            Require(WaitForColorWithReopen(ColorD, TimeSpan.FromSeconds(8)),
                "D turns purple once its declared values arrive and a real change comes back honoured");
            Console.WriteLine("PASS a plugin's declared settings round-trip through the host");
            Require(CountLog(log, "view: setting fixture-d warn=65") == 1, "the host kept the honoured number, not the one it was sent");
            // Two writes, one answer: the number that honours to what the page already holds is nothing to store, and a
            // page that got no second push is the point — pushing it would invite the page to write again, and every
            // round would rebuild the settings row and queue a save.
            Require(CountLog(log, "view: settings fixture-d") == 1, "one change was pushed once, and the unchanged write was pushed not at all");
            // The fourth entry declares a key with a space in it: the host must draw the three it can and say why the
            // other was skipped, in the log the author can read and on the plugins page the reader can see.
            Require(CountLog(log, "extension: note fixture-d settings[3].key") > 0, "a declared entry the host skipped is said out loud, not just dropped");
            Require(CountLog(log, "view: refused fixture-d unknown-setting") == 0, "a skipped declaration is not also a message the page is punished for");
            // Once each: the page writes the value back unchanged and the host applies settings several times over the
            // run (a theme, a size). Either would answer the other forever if a push or a write were not compared first.
            Require(CountLog(log, "view: setting fixture-d warn=65") == 1 && CountLog(log, "view: settings fixture-d") == 1,
                "a write that changes nothing is not written, and one change is not pushed twice");
            // Back to A: everything below watches A, and the panel's size follows the page it is on.
            Press(Find(window, "FixtureA"));
            Reopen();
            Require(WaitForColorWithReopen(ColorA, TimeSpan.FromSeconds(8)), "switching back from D shows A (red page)");

            if (options.Full)
            {
                int reclaims = CountLog(log, "view: reclaim fixture-a");
                int arrivals = CountLog(log, "view: arrive fixture-a");
                Hide();
                Thread.Sleep(17_000);
                // The colour alone cannot tell whether the page was reclaimed: a page that was never reclaimed is still
                // there and looks identical. The host's own log is the evidence the 15s grace actually ran.
                Require(CountLog(log, "view: reclaim fixture-a") > reclaims, "the closed panel reclaims A after the 15s grace");
                Console.WriteLine("PASS the closed panel reclaims A after the 15s grace");

                Expand();
                var watch = Stopwatch.StartNew();
                bool back = WaitForColor(ColorA, TimeSpan.FromSeconds(5));
                double elapsed = watch.Elapsed.TotalMilliseconds;
                KeepOpen();
                Require(back, "after the 15s reclaim A comes back (red page)");
                // The reloaded document is shown by its own arrival, not by the shim's 1.5s fallback: a page that only
                // appears after that fallback is exactly the blank page this test exists for.
                Require(elapsed < 1400, "after the reclaim A is drawn without the 1.5s fallback (" + (int)elapsed + " ms)");
                Require(CountLog(log, "view: arrive fixture-a") > arrivals, "the reclaimed page's new document arrived");
                Console.WriteLine("PASS after the 15s reclaim A comes back in " + (int)elapsed + " ms");
            }

            // One order, not two. The strip is laid out by slot and the wheel walks those same slots, so a reload that
            // replaces one plugin rebuilds exactly one tab — and a rebuilt tab that lands at the end of the line leaves
            // the reader's wheel arriving at pages in a sequence their eyes never saw.
            Reopen();
            Require(Rising(TabLefts(window)), "the four plugin tabs lie in switch order");
            Console.WriteLine("PASS the four plugin tabs lie in switch order");
            File.AppendAllText(Path.Combine(data, "extensions", "fixture-b", "index.html"), "\n<!-- touched -->");
            Press(FindAny(window, "设置", "Settings"));
            var settings = WaitFor(app.SettingsWindow, TimeSpan.FromSeconds(8), "the settings window")
                ?? throw new TestFailure("the settings window never opened");
            Press(FindAny(settings, "插件", "Plugins"));
            Press(FindAny(settings, "重新加载", "Reload"));
            Thread.Sleep(1500);
            Reopen();
            var after = TabLefts(window);
            Require(Rising(after), "a reload leaves the tabs in the order the page switch walks them (" + string.Join(", ", after) + ")");
            Console.WriteLine("PASS a reload keeps the plugin tabs in switch order");

            // The order is the reader's to arrange, and the panel has to follow it. The arrangement is made the only way
            // a reader makes it — press the six dots, walk the pointer down past two rows, let go — and it counts as
            // tested only once the strip itself moves, since the tabs are what the reader actually sees.
            var handles = OrderHandles(settings);
            Require(handles.Length == 4, "every loaded plugin offers a handle to move it by (" + handles.Length + ")");
            BringForward(settings);
            var pointer = DragRow(handles, 0, 2);
            Require(WaitTitles(settings, ["FixtureB", "FixtureC", "FixtureA", "FixtureD"]),
                "the pointer drag lands where it was taken — " + pointer + ", rows: " + string.Join(", ", RowTitles(settings)));
            // The rows say their own place in the name they carry, so a drop that reached the app is visible here even
            // before the panel is read: rows unmoved means the pointer never moved the row, rows moved and tabs still in
            // the old order means the panel did not follow.
            var rows = string.Join(" | ", OrderHandles(settings).Select(Place));
            Reopen();
            var arranged = TabsByLeft(window);
            Require(arranged.SequenceEqual(new[] { "FixtureB", "FixtureC", "FixtureA", "FixtureD" }),
                "a drag past two rows moves the first plugin, and the tabs move with it — " + pointer
                + ", rows: " + rows + ", tabs: " + string.Join(", ", arranged));
            Console.WriteLine("PASS the page switch follows the order the reader arranged: " + string.Join(", ", arranged));

            Console.WriteLine("UI SMOKE PASSED");
            return 0;
        }
        catch (TestFailure failure)
        {
            Console.WriteLine("FAIL " + failure.Message);
            Dump(app, data, log);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message);
            Dump(app, data, log);
            return 1;
        }
        finally
        {
            app.Stop();
            if (!options.Keep) DeleteWhenFree(data);
        }
    }

    private static AutomationElement ExpandAndFind(AutomationElement window, string name)
    {
        var (width, height) = Screen();
        var positions = new (int X, int Y)[] { (width / 2, 6), (width / 2, 14), (width / 2, 30), (width / 2 - 60, 10), (width / 2 + 60, 10) };
        foreach (var (x, y) in positions)
        {
            SetCursorPos(x, y);
            var button = WaitFor(() => FindOrNull(window, name), TimeSpan.FromSeconds(3), name);
            if (button is not null) return button;
        }
        throw new TestFailure("the panel never expanded and the tab \"" + name + "\" never appeared");
    }

    private static void Hide()
    {
        var (width, height) = Screen();
        SetCursorPos(width / 2, height - 8);
    }

    private static void Expand()
    {
        var (width, _) = Screen();
        SetCursorPos(width / 2, 8);
    }

    // Once open, the pointer is moved onto the page: the header row alone is over the collapsed surface, and a page
    // switch that moves the panel can otherwise be read as the pointer leaving.
    private static void KeepOpen()
    {
        var (width, _) = Screen();
        SetCursorPos(width / 2, 92);
    }

    private static AutomationElement Find(AutomationElement window, string name)
        => FindOrNull(window, name) ?? throw new TestFailure("the tab \"" + name + "\" is not on the panel");

    private static AutomationElement? FindOrNull(AutomationElement window, string name)
    {
        try
        {
            return window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
        }
        catch (ElementNotAvailableException) { return null; }
    }

    // Either language: the throwaway profile's is not the harness's to know.
    private static AutomationElement FindAny(AutomationElement window, params string[] names)
    {
        foreach (var name in names)
            if (WaitFor(() => FindOrNull(window, name), TimeSpan.FromSeconds(3), name) is { } found) return found;
        throw new TestFailure("none of \"" + string.Join("\" / \"", names) + "\" is in that window");
    }

    // The plugin tabs' left edges, listed in the order the page switch walks them. The strip tells the truth only when
    // these rise: a tab rebuilt by a reload has to go back to its slot's place, not to the end of the line.
    private static int[] TabLefts(AutomationElement window)
    {
        string[] names = ["FixtureA", "FixtureB", "FixtureC", "FixtureD"];
        var lefts = new int[names.Length];
        for (int i = 0; i < names.Length; i++) lefts[i] = (int)Find(window, names[i]).Current.BoundingRectangle.Left;
        return lefts;
    }

    private static bool Rising(int[] lefts)
    {
        for (int i = 1; i < lefts.Length; i++) if (lefts[i] <= lefts[i - 1]) return false;
        return true;
    }

    // The plugin tabs read left to right, which is the only order the reader sees. Listed by name so a change of the
    // arranged order shows up as a different sequence instead of merely a set of rising edges.
    private static string[] TabsByLeft(AutomationElement window)
    {
        string[] names = ["FixtureA", "FixtureB", "FixtureC", "FixtureD"];
        return names.OrderBy(name => Find(window, name).Current.BoundingRectangle.Left).ToArray();
    }

    // The six-dot handles on the plugins page, top row first. Their names carry the position they stand at, which is
    // how a screen reader is told where the row is — and how this test finds them in either interface language.
    private static AutomationElement[] OrderHandles(AutomationElement settings)
    {
        var found = settings.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.IsContentElementProperty, true)));
        var handles = new List<AutomationElement>();
        foreach (AutomationElement element in found)
        {
            try
            {
                string name = element.Current.Name;
                if (name.Contains("位置") || name.Contains("position")) handles.Add(element);
            }
            catch (ElementNotAvailableException) { }
        }
        return handles.OrderBy(element => element.Current.BoundingRectangle.Top).ToArray();
    }

    // What the handle says its row is: "FixtureA position, 1 of 4" in English, "A 的位置，第 1 个，共 4 个" in Chinese.
    // The name is the row's own report of its place, so it answers "did the drop reach the app?" without reading a file.
    private static string Place(AutomationElement handle)
    {
        try { return handle.Current.Name; }
        catch (ElementNotAvailableException) { return "(gone)"; }
    }

    // The row titles read top to bottom, taken from what each handle says about itself — the app's own report of the
    // arrangement, which is the thing worth waiting on before the panel is read.
    private static string[] RowTitles(AutomationElement settings)
    {
        var titles = new List<string>();
        foreach (var handle in OrderHandles(settings))
        {
            var name = Place(handle);
            int at = name.IndexOf(" 的位置，", StringComparison.Ordinal);
            if (at < 0) at = name.IndexOf(" position, ", StringComparison.Ordinal);
            titles.Add(at >= 0 ? name[..at] : name);
        }
        return titles.ToArray();
    }

    private static bool WaitTitles(AutomationElement settings, string[] expected)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (RowTitles(settings).SequenceEqual(expected)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    // A row moved the only way a reader moves it: press the six dots, walk the pointer down to the row it should pass,
    // let go. No UIA pattern can do this — the drag lives on the mouse moves that follow a captured press — so the
    // button events come from user32 and land on whatever sits under the cursor, which is why the window is brought
    // forward first. The row centres are read before the press, because the rows shift under the pointer while it walks.
    private static string DragRow(AutomationElement[] handles, int from, int to)
    {
        int X(AutomationElement element) => (int)((element.Current.BoundingRectangle.Left + element.Current.BoundingRectangle.Right) / 2);
        int Y(AutomationElement element) => (int)((element.Current.BoundingRectangle.Top + element.Current.BoundingRectangle.Bottom) / 2);
        int x = X(handles[from]), start = Y(handles[from]), end = Y(handles[to]);
        SetCursorPos(x, start);
        Thread.Sleep(150);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        for (int step = 1; step <= 8; step++)
        {
            SetCursorPos(x, start + (end - start) * step / 8);
            Thread.Sleep(50);
        }
        Thread.Sleep(150);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(200);
        return "pointer " + x + "," + start + " -> " + x + "," + end;
    }

    // The window whose rows are dragged has to be the one under those coordinates. The panel's own window is opened and
    // closed by hover all through a run and may well be the one holding the foreground.
    private static void BringForward(AutomationElement window)
    {
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        if (handle == IntPtr.Zero || !SetForegroundWindow(handle)) throw new TestFailure("that window cannot be brought forward");
        Thread.Sleep(300);
    }

    // UIA Invoke is used instead of a pointer click: the panel's buttons are exposed as invoke patterns, and clicking
    // by coordinates depends on the window's DPI mapping. Invoking also changes the foreground app in some WebView2
    // hosts, so the panel is reopened after each switch; the page itself stays in its slot.
    private static void Press(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) { ((InvokePattern)invoke).Invoke(); return; }
        throw new TestFailure("the tab \"" + element.Current.Name + "\" cannot be pressed");
    }

    // The switch may have been made while the panel lost the foreground; move the pointer out and back so the hover
    // state starts over, then wait for the panel to open.
    private static void Reopen()
    {
        var (width, height) = Screen();
        SetCursorPos(width / 2, height - 8);
        Thread.Sleep(500);
        Expand();
        Thread.Sleep(1500);
        KeepOpen();
    }

    // The pages are solid colours, so the assertion is what a user sees: the centre of the panel. UIA's WebView2
    // subtree is not published in every automation session, while the pixels are always there.
    private static bool WaitForColor(Color expected, TimeSpan timeout)
    {
        var (width, _) = Screen();
        int x = width / 2, y = 100;
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (Near(x, y, expected)) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    // The panel can lose the foreground on a UIA Invoke and not reopen from the first hover; a blank page (the bug this
    // test guards) stays blank however many times it is reopened, so retrying only removes the harness flakiness.
    private static bool WaitForColorWithReopen(Color expected, TimeSpan timeout)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (WaitForColor(expected, timeout)) return true;
            if (attempt < 2) Reopen();
        }
        return false;
    }

    // The host writes its log while the test reads it, and a missing or half-written line is not a test failure: a count
    // only ever has to grow.
    private static int CountLog(string log, string text)
    {
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            int count = 0;
            while (reader.ReadLine() is { } line)
                if (line.Contains(text, StringComparison.Ordinal)) count++;
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // The last "view: fit <id> box=WxH page=WxH" the host wrote: what box the panel made, and how many CSS pixels the
    // page was actually laid out in. Null while the page has not reported its settled size yet.
    private static ((int W, int H) Box, (int W, int H) Page)? LastFit(string log, string id)
    {
        ((int W, int H) Box, (int W, int H) Page)? last = null;
        string mark = "view: fit " + id + " ";
        try
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                int at = line.IndexOf(mark, StringComparison.Ordinal);
                if (at < 0) continue;
                var parts = line[(at + mark.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !TrySize(parts[0], "box=", out var box) || !TrySize(parts[1], "page=", out var page)) continue;
                last = (box, page);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return last; }
        return last;
    }

    private static bool TrySize(string text, string prefix, out (int W, int H) size)
    {
        size = default;
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var axes = text[prefix.Length..].Split('x');
        if (axes.Length != 2 || !int.TryParse(axes[0], out int width) || !int.TryParse(axes[1], out int height)) return false;
        size = (width, height);
        return true;
    }

    private static ((int W, int H) Box, (int W, int H) Page)? WaitForFit(string log, string id, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        ((int W, int H) Box, (int W, int H) Page)? fit;
        while (DateTime.UtcNow < end)
        {
            fit = LastFit(log, id);
            if (fit is not null) return fit;
            Thread.Sleep(200);
        }
        return LastFit(log, id);
    }

    private static bool Near(int x, int y, Color expected)
    {
        try
        {
            using var bitmap = new Bitmap(5, 5);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(x - 2, y - 2, 0, 0, new System.Drawing.Size(5, 5));
            int red = 0, green = 0, blue = 0;
            for (int py = 0; py < 5; py++)
                for (int px = 0; px < 5; px++)
                {
                    var pixel = bitmap.GetPixel(px, py);
                    red += pixel.R; green += pixel.G; blue += pixel.B;
                }
            return Math.Abs(red / 25 - expected.R) < 45 && Math.Abs(green / 25 - expected.G) < 45 && Math.Abs(blue / 25 - expected.B) < 45;
        }
        catch (Exception) { return false; }
    }

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if (probe() is { } value) return value; }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            Thread.Sleep(150);
        }
        return null;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new TestFailure(what + " — " + Describe());
    }

    private static string Describe()
    {
        var names = new List<string>();
        try
        {
            foreach (AutomationElement element in (_window ?? AutomationElement.RootElement).FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                try { if (!string.IsNullOrWhiteSpace(element.Current.Name)) names.Add(element.Current.Name); }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (Exception) { }
        return "visible names: " + string.Join(" | ", names.Take(40));
    }

    private static void Dump(BrimDeckProcess app, string data, string log)
    {
        Console.WriteLine("app exited: " + app.HasExited + (app.HasExited ? " code=" + app.ExitCode : ""));
        try { Console.WriteLine("uia window: " + (_window?.Current.Name ?? "(none)")); } catch (Exception) { }
        var shot = Path.Combine(Path.GetTempPath(), "BrimDeck-UiTests-failure.png");
        try
        {
            var (width, _) = Screen();
            // The panel is at the top centre in a fresh profile; crop to it so a failure screenshot does not carry the
            // rest of the desktop.
            const int panelWidth = 560, panelHeight = 300;
            int left = Math.Max(0, width / 2 - panelWidth / 2);
            using var bitmap = new Bitmap(panelWidth, panelHeight);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(left, 0, 0, 0, new System.Drawing.Size(panelWidth, panelHeight));
            bitmap.Save(shot);
            Console.WriteLine("screenshot: " + shot);
        }
        catch (Exception ex) { Console.WriteLine("screenshot failed: " + ex.Message); }
        if (File.Exists(log))
        {
            Console.WriteLine("--- extensions.log (tail) ---");
            foreach (var line in File.ReadLines(log).TakeLast(30)) Console.WriteLine(line);
        }
        var crash = Path.Combine(data, "crash.log");
        if (File.Exists(crash))
        {
            Console.WriteLine("--- crash.log (tail) ---");
            foreach (var line in File.ReadLines(crash).TakeLast(15)) Console.WriteLine(line);
        }
        Console.WriteLine("profile: " + data);
    }

    private static void WriteFixture(string root, string id, string title, string mark, string color, int minWidth = 0, int minHeight = 0, bool bridgeProbe = false)
    {
        var folder = Path.Combine(root, "extensions", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), $$"""
            {
              "id": "{{id}}",
              "titleZh": "{{title}}",
              "titleEn": "{{title}}",
              "glyph": "M 4,4 H 20 V 20 H 4 Z",
              "entry": "index.html"{{(minWidth > 0 ? ",\n  \"minWidth\": " + minWidth + ",\n  \"minHeight\": " + minHeight : "")}}
            }
            """);
        // A solid page: the test reads the centre pixel, which is the paint a user sees even when WebView2's
        // accessibility tree is unavailable to the automation session.
        var script = bridgeProbe ? BridgeProbe : """
              if (window.brimdeck) brimdeck.on('init', function (init) {
                if (init.shown) document.body.dataset.ready = '1';
              });
            """;
        File.WriteAllText(Path.Combine(folder, "index.html"), $$"""
            <!doctype html>
            <meta charset="utf-8">
            <style>
              html, body { margin: 0; height: 100%; background: {{color}}; }
              body { box-sizing: border-box; padding: 8px; font: 600 20px "Segoe UI", sans-serif; color: #fff; }
            </style>
            <div>{{mark}}</div>
            <script>
            {{script}}
            </script>
            """);
    }

    // A page that only paints once the host has told it three things: which bridge it is speaking, how much room the
    // host will keep, and — after two asks the host must refuse — the reason each one was refused. A silent drop reads
    // as a host that ignored the page on purpose, so the refusal arriving is what the colour proves. The page also
    // tries to navigate itself out to a real web address, which is the one thing the link rules must never allow: a
    // page that leaves the plugin's own origin was never clicked by anyone.
    private const string BridgeProbe = """
              var seen = {}, told = false;
              function paint() {
                if (!(told && seen['state-too-large'] && seen['link-refused'])) return;
                var c = '#0000C0';
                document.documentElement.style.background = c;
                document.body.style.background = c;
              }
              if (window.brimdeck) {
                brimdeck.on('error', function (reply) { seen[reply.code] = 1; paint(); });
                brimdeck.on('init', function (init) {
                  told = init.apiVersion >= 1 && init.limits && init.limits.stateBytes > 0 && init.limits.alarms > 0;
                  brimdeck.saveState(new Array(70002).join('x'));
                  brimdeck.openUrl('javascript:alert(1)');
                  setTimeout(function () { location.href = 'https://example.com/'; }, 300);
                  paint();
                });
              }
            """;

    // A plugin that declares its controls in the manifest instead of drawing them: the host has to read them back to the
    // page, honour what the page writes against its own range and step, and speak the plugin's own words in whichever
    // language the interface is in. Purple is only painted once a real change comes back — and only one change ever,
    // because the page also writes a number that honours to what it was already given (63 on a step of fives is the 65
    // it holds), which is nothing to store, push or rebuild.
    private static void WriteDeclaredFixture(string root)
    {
        var folder = Path.Combine(root, "extensions", "fixture-d");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """
            {
              "id": "fixture-d",
              "titleZh": "FixtureD",
              "titleEn": "FixtureD",
              "entry": "index.html",
              "settings": [
                { "key": "showFan", "type": "toggle", "label": "风扇", "default": true },
                { "key": "warn", "type": "number", "label": { "zh": "告警温度", "en": "Warn at" }, "min": 30, "max": 110, "step": 5, "default": 65 },
                { "key": "mode", "type": "choice", "label": "配色", "default": "cool",
                  "options": [ { "value": "auto", "label": "自动" }, { "value": "cool", "label": "冷静" } ] },
                { "key": "bad key", "type": "toggle", "label": "这一项该被跳过" }
              ],
              "phrases": { "hot": { "zh": "过热", "en": "Overheating" } }
            }
            """);
        File.WriteAllText(Path.Combine(folder, "index.html"), """
            <!doctype html>
            <meta charset="utf-8">
            <style>
              html, body { margin: 0; height: 100%; background: #202020; }
              body { box-sizing: border-box; padding: 8px; font: 600 20px "Segoe UI", sans-serif; color: #fff; }
            </style>
            <div>fixture-d</div>
            <script>
              var told = false, honored = false;
              function paint() {
                if (!(told && honored)) return;
                var c = '#9C27B0';
                document.documentElement.style.background = c;
                document.body.style.background = c;
              }
              if (window.brimdeck) {
                brimdeck.on('init', function (init) {
                  var s = init.settings || {};
                  told = s.showFan === true && s.warn === 65 && s.mode === 'cool'
                    && typeof init.phrases.hot === 'string' && init.phrases.hot.length > 0
                    && brimdeck.phrase('absent') === 'absent'
                    && brimdeck.version >= 2;
                  if (told) {
                    brimdeck.setSetting('warn', 63);   // honoured to 65: the value the page already holds
                    brimdeck.setSetting('mode', 'auto'); // a real change, and the only one that comes back
                  }
                  paint();
                });
                brimdeck.on('settings', function (values) {
                  honored = values.warn === 65 && values.mode === 'auto' && values.showFan === true;
                  paint();
                });
              }
            </script>
            """);
    }

    private static void DeleteWhenFree(string folder)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try { Directory.Delete(folder, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(400); }
        }
    }

    private static (int Width, int Height) Screen() => (GetSystemMetrics(0), GetSystemMetrics(1));

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private const uint LeftDown = 0x0002, LeftUp = 0x0004;
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(int value);

    private sealed class TestFailure(string message) : Exception(message);

    private sealed record Options(string App, string? Data, bool Full, bool Keep, bool AllowParallel)
    {
        public static Options? Parse(string[] args)
        {
            var app = Environment.GetEnvironmentVariable("BRIMDECK_UI_APP")
                ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BrimDeck", "bin", "Release", "net10.0-windows10.0.19041.0", "BrimDeck.exe"));
            string? data = null;
            bool full = false, keep = false, allowParallel = false;
            for (int at = 0; at < args.Length; at++)
            {
                switch (args[at])
                {
                    case "--app" when at + 1 < args.Length: app = Path.GetFullPath(args[++at]); break;
                    case "--data" when at + 1 < args.Length: data = Path.GetFullPath(args[++at]); break;
                    case "--full": full = true; break;
                    case "--keep": keep = true; break;
                    case "--allow-parallel": allowParallel = true; break;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + args[at]);
                        Console.Error.WriteLine("Usage: BrimDeck.UiTests [--app <BrimDeck.exe>] [--data <folder>] [--full] [--keep] [--allow-parallel]");
                        return null;
                }
            }
            return new Options(app, data, full, keep, allowParallel);
        }
    }

    // One app process on its own profile. The data folder is passed both as an argument and as an environment variable:
    // the argument is what a developer sees in a shortcut, the variable covers tools that start the app without args.
    private sealed class BrimDeckProcess : IDisposable
    {
        private readonly Process _process;
        public BrimDeckProcess(string app, string data, string log)
        {
            var start = new ProcessStartInfo(app)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(app)!,
                RedirectStandardError = false,
                RedirectStandardOutput = false
            };
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(data);
            start.Environment["BRIMDECK_DATA_DIR"] = data;
            start.Environment["BRIMDECK_EXTENSION_LOG"] = log;
            _process = Process.Start(start) ?? throw new TestFailure("could not start " + app);
        }
        public int Id => _process.Id;
        public bool HasExited { get { try { return _process.HasExited; } catch (Exception) { return true; } } }
        public int? ExitCode { get { try { return _process.ExitCode; } catch (Exception) { return null; } } }
        public AutomationElement? MainWindow() => WaitWindow();
        // The settings window is a second top-level window of the same process, told apart by its title.
        public AutomationElement? SettingsWindow()
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            try
            {
                foreach (AutomationElement element in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
                    if (element.Current.Name is { } name && (name.Contains("设置") || name.Contains("Settings"))) return element;
            }
            catch (ElementNotAvailableException) { }
            return null;
        }
        private AutomationElement? WaitWindow()
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
            try { return AutomationElement.RootElement.FindFirst(TreeScope.Children, condition); }
            catch (ElementNotAvailableException) { return null; }
        }
        public void Stop()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception) { }
            try { _process.WaitForExit(5000); } catch (Exception) { }
        }
        public void Dispose() => Stop();
    }
}
