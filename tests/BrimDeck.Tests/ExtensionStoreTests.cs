using BrimDeck.Core;

// What a reader chose, read back against what the author declared. The declaration wins every disagreement — a stored
// number off the step grid is snapped, a stored kind the control does not have falls back to its default — because a
// page must never be handed a value its own manifest would refuse to draw.
internal static class ExtensionStoreTests
{
    internal static void Run(Action<string, bool> check)
    {
        var fan = new ExtensionSetting("showFan", ExtensionSettingKind.Toggle, new ExtensionText("风扇", "Fan"), true, 0, "", 0, 0, 1, []);
        var warn = new ExtensionSetting("warn", ExtensionSettingKind.Number, new ExtensionText("告警", "Warn"), false, 65, "", 30, 110, 5, []);
        var mode = new ExtensionSetting("mode", ExtensionSettingKind.Choice, new ExtensionText("配色", "Palette"), false, 0, "cool", 0, 0, 1,
            [new("auto", new("自动", "Auto")), new("cool", new("冷静", "Cool"))]);
        ExtensionSetting[] declared = [fan, warn, mode];

        check("Store: an untouched control reads the default its kind carries",
            ExtensionSettingStore.Initial(fan) is { Kind: "toggle", On: true }
            && ExtensionSettingStore.Initial(warn) is { Kind: "number", Number: 65 }
            && ExtensionSettingStore.Initial(mode) is { Kind: "choice", Choice: "cool" });
        check("Store: nothing stored is the same as the default, not a missing row",
            ExtensionSettingStore.Honor(warn, null) is { Number: 65 });
        check("Store: a value stored under another kind falls back instead of being guessed at",
            ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("toggle", true, 88, "")) is { Number: 65 }
            && ExtensionSettingStore.Honor(fan, new ExtensionSettingValue("number", false, 1, "")) is { On: true });
        check("Store: a stored toggle keeps its own answer, not the declared default",
            ExtensionSettingStore.Honor(fan, new ExtensionSettingValue("toggle", false)) is { On: false });
        check("Store: a number off the ends is pulled onto the range",
            ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("number", false, 900)) is { Number: 110 }
            && ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("number", false, -40)) is { Number: 30 });
        check("Store: a number between the steps lands on the nearest one, counted from min",
            ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("number", false, 63)) is { Number: 65 }
            && ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("number", false, 61)) is { Number: 60 });
        var fine = new ExtensionSetting("f", ExtensionSettingKind.Number, ExtensionText.Empty, false, 0, "", 0, 1, 0.1, []);
        check("Store: float dust is swept off the value a page has to compare against",
            ExtensionSettingStore.Honor(fine, new ExtensionSettingValue("number", false, 0.30000000000000004)) is { Number: 0.3 });
        check("Store: a number the file cannot hold falls back to the default",
            ExtensionSettingStore.Honor(warn, new ExtensionSettingValue("number", false, double.NaN)) is { Number: 65 });
        check("Store: a choice the author does not offer is not honored",
            ExtensionSettingStore.Honor(mode, new ExtensionSettingValue("choice", false, 0, "vivid")) is { Choice: "cool" }
            && ExtensionSettingStore.Honor(mode, new ExtensionSettingValue("choice", false, 0, "auto")) is { Choice: "auto" });

        var honored = ExtensionSettingStore.Normalize(declared, new Dictionary<string, ExtensionSettingValue>(StringComparer.Ordinal)
        {
            ["warn"] = new("number", false, 63), ["gone"] = new("toggle", true), ["mode"] = new("choice", false, 0, "auto")
        });
        check("Store: every declared control is answered, and a value the manifest dropped is kept alongside",
            honored.Count == 4 && new HashSet<string>(honored.Keys, StringComparer.Ordinal).SetEquals(new[] { "showFan", "warn", "mode", "gone" })
            && honored["showFan"] is { On: true } && honored["gone"] is { On: true }
            && honored["warn"].Number == 65 && honored["mode"].Choice == "auto");
        // The row order a reader sees comes from the declaration list, never from this map: a dictionary is not
        // promised to enumerate in the order it was filled.
        check("Store: the declaration keeps its own order for whoever renders it",
            string.Join(",", declared.Select(setting => setting.Key)) == "showFan,warn,mode");
        var roundAgain = ExtensionSettingStore.Normalize(declared, honored);
        check("Store: normalizing twice changes nothing, so a reload cannot drift",
            roundAgain.Count == honored.Count && roundAgain.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(honored.Keys.OrderBy(key => key, StringComparer.Ordinal))
            && roundAgain["warn"].Number == honored["warn"].Number && roundAgain["gone"].On == honored["gone"].On);

        var values = ExtensionSettingStore.Normalize(declared, null);
        check("Store: what a page reads is one scalar per declared control",
            ExtensionSettingStore.ValuesJson(declared, values) == "{\"showFan\":true,\"warn\":65,\"mode\":\"cool\"}");
        var half = new ExtensionSetting("h", ExtensionSettingKind.Number, ExtensionText.Empty, false, 0, "", 0, 10, 0.5, []);
        check("Store: the page's numbers are written with a dot, on the grid the author set",
            ExtensionSettingStore.ValuesJson([half], ExtensionSettingStore.Normalize([half],
                new Dictionary<string, ExtensionSettingValue> { ["h"] = new("number", false, 7.5) })) == "{\"h\":7.5}");
        // The host's JSON escaper is its own business (it writes \u0022 for a quote today); what a page is promised is
        // that reading the message back gives the words and value it declared.
        var awkward = """a"b\c""";
        var odd = new ExtensionSetting("o", ExtensionSettingKind.Choice, ExtensionText.Empty, false, 0, awkward, 0, 0, 1,
            [new(awkward, new(awkward, awkward))]);
        var oddStored = ExtensionSettingStore.Normalize([odd], new Dictionary<string, ExtensionSettingValue> { ["o"] = new("choice", false, 0, awkward) });
        var oddJson = ExtensionSettingStore.ValuesJson([odd], oddStored);
        check("Store: a value that needs escaping is escaped, and the page reads back exactly what was declared",
            System.Text.Json.JsonDocument.Parse(oddJson).RootElement.GetProperty("o").GetString() == awkward);
        var quotedPhrases = new Dictionary<string, ExtensionText>(StringComparer.Ordinal) { ["say"] = new("说\"引号\"", "Say \"quotes\"") };
        check("Store: a phrase with quotes in it survives the trip into the message",
            System.Text.Json.JsonDocument.Parse(ExtensionSettingStore.PhrasesJson(quotedPhrases, english: false))
                .RootElement.GetProperty("say").GetString() == "说\"引号\"");

        var phrases = new Dictionary<string, ExtensionText>(StringComparer.Ordinal)
        {
            ["hot"] = new("过热", "Overheating"), ["one"] = new("一个", "1"), ["blank"] = ExtensionText.Empty
        };
        var zh = System.Text.Json.JsonDocument.Parse(ExtensionSettingStore.PhrasesJson(phrases, english: false)).RootElement;
        var en = System.Text.Json.JsonDocument.Parse(ExtensionSettingStore.PhrasesJson(phrases, english: true)).RootElement;
        check("Store: the words arrive in the language the interface speaks, and a phrase with none is left out",
            zh.GetProperty("hot").GetString() == "过热" && zh.GetProperty("one").GetString() == "一个"
            && en.GetProperty("hot").GetString() == "Overheating" && en.GetProperty("one").GetString() == "1"
            && !zh.TryGetProperty("blank", out _) && !en.TryGetProperty("blank", out _));

        // The settings file's own half: a plugin's values ride on its row, created on the first change and copied apart.
        var settings = new DeckSettings();
        check("Store: a plugin with no row yet reads as no values, and nothing is written for it",
            settings.ExtensionValues("demo").Count == 0 && settings.Extensions.Count == 0);
        settings.SetExtensionValue("demo", "warn", new ExtensionSettingValue("number", false, 70));
        settings.SetExtensionValue("demo", "mode", new ExtensionSettingValue("choice", false, 0, "auto"));
        settings.SetExtensionValue("other", "warn", new ExtensionSettingValue("number", false, 42));
        check("Store: the first choice creates the row, the next ones join it",
            settings.Extensions.Count == 2 && settings.ExtensionValues("demo") is { Count: 2 }
            && settings.ExtensionValues("demo")["warn"].Number == 70 && settings.ExtensionValues("other")["warn"].Number == 42);
        settings.SetExtensionValue("demo", "warn", new ExtensionSettingValue("number", false, 99));
        check("Store: a second write of the same key replaces it",
            settings.ExtensionValues("demo")["warn"].Number == 99 && settings.ExtensionValues("demo").Count == 2);
        var copy = settings.Copy();
        copy.SetExtensionValue("demo", "warn", new ExtensionSettingValue("number", false, 10));
        check("Store: a copied settings object does not share a plugin's values",
            settings.ExtensionValues("demo")["warn"].Number == 99 && copy.ExtensionValues("demo")["warn"].Number == 10);

        // A settings file is text somebody can edit, and "Values": null is the shape an editor writes when it knows the
        // key but has nothing to put in it. Read as a null dictionary it turns every later use — the copy the settings
        // window makes, the first value written — into an exception, so Normalize is where it stops being one.
        var handEdited = new DeckSettings();
        handEdited.Extensions.Add(new ExtensionPageSettings("demo") { Values = null! });
        handEdited.Normalize();
        check("Store: a settings file that says \"Values\": null is repaired on load, not an exception later",
            Survives(() => handEdited.Copy()) && handEdited.Extensions[0].Values.Count == 0
            && handEdited.ExtensionValues("demo").Count == 0
            && Survives(() => handEdited.SetExtensionValue("demo", "warn", new ExtensionSettingValue("number", false, 40)))
            && handEdited.ExtensionValues("demo")["warn"].Number == 40);
    }

    // A crash is a finding, not a test result: run it and say whether it survived.
    private static bool Survives(Action act)
    {
        try { act(); return true; } catch (Exception) { return false; }
    }
}
