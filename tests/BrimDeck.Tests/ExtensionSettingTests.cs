using BrimDeck.Core;

// A plugin's declared controls and words: the host draws them and speaks them, so what a manifest says is the interface
// a reader sees. Each check here is one way an author can write it — the notes are the plugin page's explanations, and
// the rule under all of them is that one unusable entry costs that entry, never the plugin.
internal static class ExtensionSettingTests
{
    internal static void Run(string root, Action<string, bool> check)
    {
        int at = 0;
        ExtensionManifest? Loaded(string json)
        {
            var folder = Path.Combine(root, "settings-" + (++at));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), json, new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html><title>p</title>");
            return ExtensionManifests.Read(folder).Manifest;
        }
        // The controls a manifest declared, or none when it could not be read at all.
        IReadOnlyList<ExtensionSetting> Declared(string json) => Loaded(json)?.Settings ?? [];
        IReadOnlyList<string> Noted(string json) => Loaded(json)?.Notes ?? [];
        ExtensionSetting One(string json) => Declared(json).SingleOrDefault()!;
        string FirstNote(string json) => Noted(json).FirstOrDefault() ?? "";

        check("Settings: a manifest without settings declares none, and says nothing about it",
            Loaded("""{"id":"demo","titleZh":"演示"}""") is { Settings.Count: 0, Notes.Count: 0 });
        check("Settings: a toggle keeps the default the author wrote",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"showFan","type":"toggle","label":"显示风扇","default":true}]}""")
            is { Kind: ExtensionSettingKind.Toggle, DefaultToggle: true, Label.Zh: "显示风扇", Label.En: "显示风扇" });
        check("Settings: a toggle without a default starts off, and the type is not case-sensitive",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"TOGGLE","label":"A"}]}""") is { DefaultToggle: false, Type: "toggle" });

        var gauge = One("""{"id":"demo","titleZh":"演示","settings":[{"key":"warn","type":"number","label":{"zh":"告警温度","en":"Warn at"},"min":30,"max":110,"step":2,"default":64}]}""");
        check("Settings: a number carries its range, step and default in the language pair the author wrote",
            gauge is { Kind: ExtensionSettingKind.Number, Minimum: 30, Maximum: 110, Step: 2, DefaultNumber: 64, Label.Zh: "告警温度", Label.En: "Warn at" });
        check("Settings: a number default outside the range is pulled into it, not thrown away",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":30,"max":40,"default":900}]}""") is { DefaultNumber: 40 });
        // The page reads its first value from the declaration and writes it back through the gate the host honours, so a
        // default between two steps is a number that moves when nobody touches it: the first setSetting lands it on the
        // grid, saves it and pushes the row again. The declaration is made self-consistent here, once, for every reader.
        var offGrid = One("""{"id":"demo","titleZh":"演示","settings":[{"key":"warn","type":"number","min":30,"max":110,"step":5,"default":66}]}""");
        check("Settings: a number default between the steps lands on one, so a read value writes back unchanged",
            offGrid is { DefaultNumber: 65 }
            && ExtensionSettingStore.Initial(offGrid) is { Number: 65 }
            && ExtensionSettingStore.Honor(offGrid, new ExtensionSettingValue("number", false, 66)) is { Number: 65 }
            && ExtensionSettingStore.ValuesJson([offGrid], new Dictionary<string, ExtensionSettingValue>(StringComparer.Ordinal)) == "{\"warn\":65}");
        check("Settings: a number without a default gets the closest the author allowed to zero",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":30,"max":40}]}""") is { DefaultNumber: 30 }
            && One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":-5,"max":-1}]}""") is { DefaultNumber: -1 });
        check("Settings: a number with no range written is 0 to 100 by ones",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","label":"A"}]}""") is { Minimum: 0, Maximum: 100, Step: 1 });
        check("Settings: a step that is zero, negative or not a number reads as one",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":0,"max":10,"step":0}]}""") is { Step: 1 }
            && One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":0,"max":10,"step":"fast"}]}""") is { Step: 1 });
        check("Settings: min not below max is a dropped row with a note, and the plugin still loads",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":80,"max":80}]}""") is null
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":80,"max":80}]}""").StartsWith("settings[0]")
            && Loaded("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"number","min":80,"max":80}]}""") is { Id: "demo" });

        var palette = One("""{"id":"demo","titleZh":"演示","settings":[{"key":"mode","type":"choice","label":"配色","default":"cool","options":[{"value":"auto","label":{"zh":"自动","en":"Auto"}},{"value":"cool","label":"冷静"}]}]}""");
        check("Settings: a choice keeps its options, their labels and the default that names one of them",
            palette is { Kind: ExtensionSettingKind.Choice, DefaultChoice: "cool", Options.Count: 2 }
            && palette.Options[0].Label.En == "Auto" && palette.Options[1].Label.Zh == "冷静");
        check("Settings: a default naming no option falls back to the first, which is what a reader sees anyway",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice","default":"nope","options":[{"value":"a"},{"value":"b"}]}]}""") is { DefaultChoice: "a" });
        check("Settings: an option with no label is shown by its value",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice","options":[{"value":"a"}]}]}""")
            is { Options: [{ Value: "a", Label.Zh: "a", Label.En: "a" }] });
        check("Settings: an option missing its value is skipped; none left is a dropped row with a note",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice","options":[{"label":"只写了名字"},{"value":"a"}]}]}""") is { Options.Count: 1 }
            && One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice","options":[{"label":"只有名字"}]}]}""") is null);
        check("Settings: a choice without an options array is refused that row, with the reason naming options",
            FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice"}]}""").Contains("options")
            && One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice"}]}""") is null);
        check("Settings: a repeated option value is the second one dropped, not two rows alike",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"m","type":"choice","options":[{"value":"a"},{"value":"a"},{"value":"b"}]}]}""")
            is { Options.Count: 2 });

        check("Settings: a key outside letters, digits, - . _ is dropped and the note says which one",
            FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"temp warn","type":"toggle"}]}""").StartsWith("settings[0].key")
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"a/b","type":"toggle"}]}""").StartsWith("settings[0].key")
            && Declared("""{"id":"demo","titleZh":"演示","settings":[{"key":"temp_warn.v2","type":"toggle"}]}""").Count == 1);
        check("Settings: two controls cannot share a key; the second is dropped with a note",
            Declared("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"toggle"},{"key":"a","type":"number"}]}""").Count == 1
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"toggle"},{"key":"a","type":"number"}]}""").StartsWith("settings[1].key"));
        check("Settings: a type the host does not know is dropped and the note quotes what was written",
            Declared("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"slider"}]}""").Count == 0
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"slider"}]}""").Contains("slider"));
        check("Settings: an entry that is not an object is dropped, and its neighbours survive",
            Declared("""{"id":"demo","titleZh":"演示","settings":["我不是对象",{"key":"a","type":"toggle"}]}""").Count == 1
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":["我不是对象",{"key":"a","type":"toggle"}]}""").StartsWith("settings[0]"));
        check("Settings: settings that is not an array costs the array, not the plugin",
            Loaded("""{"id":"demo","titleZh":"演示","settings":{"a":1}}""") is { Id: "demo", Settings.Count: 0 }
            && FirstNote("""{"id":"demo","titleZh":"演示","settings":{"a":1}}""").StartsWith("settings:"));
        var crowded = string.Join(",", Enumerable.Range(0, 20).Select(i => $$"""{"key":"k{{i}}","type":"toggle"}"""));
        check("Settings: more than the host will show is cut at the ceiling with a note",
            Declared($$"""{"id":"demo","titleZh":"演示","settings":[{{crowded}}]}""").Count == ExtensionDeclarations.MaximumSettings
            && Noted($$"""{"id":"demo","titleZh":"演示","settings":[{{crowded}}]}""").Any(note => note.StartsWith("settings:")));
        check("Settings: a control with no label is titled by its key, so the row is never blank",
            One("""{"id":"demo","titleZh":"演示","settings":[{"key":"showFan","type":"toggle"}]}""") is { Label.Zh: "showFan" });
        check("Settings: a label past the cap is cut rather than rejected",
            One($$"""{"id":"demo","titleZh":"演示","settings":[{"key":"a","type":"toggle","label":"{{new string('长', 100)}}"}]}""")
            is { Label.Zh.Length: 64 });

        var pairPhrases = Loaded("""{"id":"demo","titleZh":"演示","phrases":{"hot":"过热"}}""");
        check("Phrases: a bare string is the same words in both languages",
            pairPhrases is { Phrases.Count: 1 } && pairPhrases!.Phrases["hot"].Zh == "过热" && pairPhrases.Phrases["hot"].En == "过热");
        var twoLanguages = Loaded("""{"id":"demo","titleZh":"演示","phrases":{"hot":{"zh":"过热","en":"Hot","jz":"多余"},"cold":{"en":"Cold"}}}""");
        check("Phrases: an object names either language, and the one written stands in for the one missing",
            twoLanguages is { Phrases.Count: 2 } && twoLanguages!.Phrases["hot"].Zh == "过热" && twoLanguages.Phrases["hot"].En == "Hot"
            && twoLanguages.Phrases["cold"].Zh == "Cold" && twoLanguages.Phrases["cold"].En == "Cold");
        var partial = Loaded("""{"id":"demo","titleZh":"演示","phrases":{"not a key":"x","gone":"","ok":{"zh":"好"}}}""");
        check("Phrases: a key the host cannot use is skipped with a note, and a value with no words is skipped without one",
            partial is { Phrases.Count: 1, Notes.Count: 1 } && partial!.Notes[0].StartsWith("phrases.not a key"));
        var many = "{" + string.Join(",", Enumerable.Range(0, 70).Select(i => $$""" "k{{i}}": "v" """)) + "}";
        var longPhrase = Loaded($$$"""{"id":"demo","titleZh":"演示","phrases":{"a":"{{{new string('字', 300)}}}"}}""");
        var crowdedPhrases = Loaded($$"""{"id":"demo","titleZh":"演示","phrases":{{many}}}""");
        check("Phrases: a phrase past the cap is cut, and the count past the ceiling is cut with a note",
            longPhrase is { Phrases.Count: 1 } && longPhrase!.Phrases["a"].Zh.Length == ExtensionDeclarations.PhraseChars
            && crowdedPhrases is { Phrases.Count: 64, Notes.Count: 1 });
        check("Phrases: phrases that is not an object costs the object, not the plugin",
            Loaded("""{"id":"demo","titleZh":"演示","phrases":["hot"]}""") is { Id: "demo", Phrases.Count: 0 }
            && FirstNote("""{"id":"demo","titleZh":"演示","phrases":["hot"]}""").StartsWith("phrases:"));
        check("Phrases: the page asks for whichever language the host is speaking",
            twoLanguages is { Phrases: { } pair } && pair["hot"].For(english: false) == "过热" && pair["hot"].For(english: true) == "Hot");
    }
}
