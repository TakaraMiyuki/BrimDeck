using BrimDeck.Core;

// Which page comes after which. The panel's tab strip, its wheel and its fallback page all read this one rule, so the
// tests here are about the rule itself: the reader's own order decides the plugin pages, the built-in three stay first,
// and a reorder writes that order down without losing anything the reader never touched.
internal static class PageOrderTests
{
    // Slots stand up as if the scanner had loaded them; the hooks are restored afterwards because they are static.
    private static IDisposable Load(params string?[] slots)
    {
        var (wasOccupied, wasId, wasMinimum) = (DeckPages.SlotOccupied, DeckPages.SlotId, DeckPages.SlotMinimum);
        DeckPages.SlotOccupied = slot => slots[slot] is { Length: > 0 };
        DeckPages.SlotId = slot => slots[slot];
        DeckPages.SlotMinimum = _ => (0, 0);
        return new Restore(() => { (DeckPages.SlotOccupied, DeckPages.SlotId, DeckPages.SlotMinimum) = (wasOccupied, wasId, wasMinimum); });
    }

    private sealed class Restore(Action act) : IDisposable
    {
        public void Dispose() => act();
    }

    private static ExtensionPageSettings Entry(string id, bool shown = true) => new(id, shown);
    private static ExtensionPageSettings Placed(string id, int order) => new(id) { Order = order };

    internal static void Run(Action<string, bool> check)
    {
        // ── the order the panel switches in ──────────────────────────────────────────────────────────────
        using (Load("alpha", "beta", "gamma", null))
        {
            var listed = new DeckSettings();
            listed.Extensions.Add(Placed("gamma", 0));
            listed.Extensions.Add(Placed("alpha", 1));
            check("Order: the places the reader wrote decide the plugin pages, and an unplaced plugin goes last",
                DeckPages.Order(listed).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension3]));
            check("Order: the built-in three stay first no matter what the places say",
                DeckPages.Order(listed)[0] == DeckPage.Usage && DeckPages.Order(listed)[1] == DeckPage.Music && DeckPages.Order(listed)[2] == DeckPage.Monitor);

            // What the wheel and the tab strip actually walk: same order, minus the pages switched off.
            var hides = listed.Copy();
            hides.Extensions[1] = hides.Extensions[1] with { Shown = false };
            check("Order: a hidden plugin leaves the switch but does not move the others",
                DeckPages.Visible(hides).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension2, DeckPage.Extension1])
                && DeckPages.Order(hides).SequenceEqual(DeckPages.Order(listed)));
            // The fallback is the first shown page in that order, which is a plugin only once the built-in three are off.
            var onlyPlugins = hides.Copy();
            onlyPlugins.UsagePage = onlyPlugins.MusicPage = onlyPlugins.MonitorPage = false;
            check("Order: the page the panel falls back to is the first shown one in that order",
                DeckPages.Resolve(onlyPlugins, DeckPage.Extension0) == DeckPage.Extension2);

            // Nothing placed at all is the order the panel had before a reader ever rearranged it.
            check("Order: an untouched settings file keeps slot order",
                DeckPages.Order(new DeckSettings()).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension3]));
            // One place written alone is still an arrangement: the plugin named goes first, the rest keep slot order
            // behind it rather than being dragged along.
            var partly = new DeckSettings();
            partly.ReorderExtensions(["gamma"]);
            check("Order: arranging one plugin puts it in front and leaves the others in slot order",
                DeckPages.Order(partly).Skip(3).SequenceEqual([DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension3]));
        }

        // An empty slot has no plugin to rank, so it falls behind the loaded ones and the slots order what is left.
        using (Load(null, "beta", "alpha", null))
        {
            var listed = new DeckSettings();
            listed.Extensions.Add(Placed("alpha", 0));
            listed.Extensions.Add(Placed("beta", 1));
            check("Order: an empty slot has nothing to rank, so it falls back to slot order",
                DeckPages.Order(listed).Skip(3).SequenceEqual([DeckPage.Extension2, DeckPage.Extension1, DeckPage.Extension0, DeckPage.Extension3]));
        }

        // A row a plugin's own page wrote is not an arrangement. This is how a real run failed: the demo plugin saved one
        // declared value, which created its settings row, and its tab jumped in front of three plugins the reader had
        // never moved — because the list standing there was being read as the reader's order.
        using (Load("alpha", "beta", "gamma", "delta"))
        {
            var written = new DeckSettings();
            written.SetExtensionValue("delta", "warn", new ExtensionSettingValue("number", false, 65));
            check("Order: a row created by a value the plugin wrote arranges nothing",
                DeckPages.Order(written).SequenceEqual([DeckPage.Usage, DeckPage.Music, DeckPage.Monitor, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension3]));
            // The same for the other ways a row appears without the reader touching a position: its own switch, its page size.
            written.Extensions.Add(new ExtensionPageSettings("beta") { Shown = false });
            written.Extensions.Add(new ExtensionPageSettings("alpha") { Width = 640, Height = 220 });
            check("Order: rows made by a switch or a size keep the plugins in slot order too",
                DeckPages.Order(written).Skip(3).SequenceEqual([DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension2, DeckPage.Extension3]));
        }

        // ── writing an order down ────────────────────────────────────────────────────────────────────────
        using (Load("alpha", "beta", "gamma", null))
        {
            var settings = new DeckSettings();
            settings.Extensions.Add(Entry("alpha"));
            settings.Extensions.Add(Entry("gone"));   // a folder removed: its row stays so putting it back restores it
            settings.Extensions.Add(Entry("gamma"));
            settings.ReorderExtensions(["gamma", "alpha", "beta"]);
            check("Reorder: the places are written on the rows and the row kept for a missing folder is left alone",
                settings.Extensions.Select(e => e.Id).SequenceEqual(["alpha", "gone", "gamma", "beta"])
                && settings.Extensions.Select(e => e.Order).SequenceEqual([1, -1, 0, 2]));
            check("Reorder: the arrangement the panel reads comes from the places, not from the list",
                DeckPages.Order(settings).Skip(3).SequenceEqual([DeckPage.Extension2, DeckPage.Extension0, DeckPage.Extension1, DeckPage.Extension3]));
            check("Reorder: a row's own settings travel with it",
                settings.Extensions.Single(e => e.Id == "gamma").Shown && settings.Extensions.Single(e => e.Id == "alpha").Shown);

            // beta had no row at all: rearranging it into place creates one rather than dropping the move.
            var created = new DeckSettings();
            created.Extensions.Add(Entry("alpha"));
            created.ReorderExtensions(["beta", "alpha"]);
            check("Reorder: a plugin with no row yet gets a default one, pinned where it was named",
                created.Extensions.Select(e => e.Id).SequenceEqual(["alpha", "beta"])
                && created.Extensions.Single(e => e.Id == "beta").Order == 0);

            var untouched = new DeckSettings();
            untouched.Extensions.Add(Entry("alpha"));
            untouched.ReorderExtensions(["alpha"]);
            var pinned = untouched.Extensions[0];
            untouched.ReorderExtensions(["alpha"]);
            check("Reorder: asking for the order it already has changes nothing",
                untouched.Extensions.Count == 1 && ReferenceEquals(untouched.Extensions[0], pinned));
            untouched.ReorderExtensions(["alpha", "beta"]);
            check("Reorder: naming a plugin with no row pins it there, in the order it was named",
                untouched.Extensions.Select(e => e.Id).SequenceEqual(["alpha", "beta"])
                && untouched.Extensions[1].Order == 1);
            untouched.ReorderExtensions([]);
            check("Reorder: an empty order is not a request to erase the list",
                untouched.Extensions.Count == 2);

            // The sizes and values a reader set must survive the move, since they belong to the plugin and not to a slot.
            var kept = new DeckSettings();
            var alpha = new ExtensionPageSettings("alpha") { Width = 640, Height = 220 };
            alpha.Values["warn"] = new ExtensionSettingValue("number", false, 65);
            kept.Extensions.Add(alpha);
            kept.ReorderExtensions(["beta", "alpha"]);
            check("Reorder: size and declared values travel with the plugin",
                kept.Extensions.Single(e => e.Id == "alpha") is { Width: 640, Height: 220 } moved && moved.Values["warn"].Number == 65);
        }
    }
}
