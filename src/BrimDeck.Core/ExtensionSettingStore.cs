namespace BrimDeck.Core;

// One value the user chose for a control a plugin declared. The kind travels with it: a settings file edited by hand,
// or written by a host that knew a different declaration, then reads as its default rather than as a type mismatch the
// page would have to guess about.
public sealed record ExtensionSettingValue(string Kind = "toggle", bool On = false, double Number = 0, string Choice = "")
{
    // The scalar a page reads for this value, as JSON: true, 65, "cool". The page never has to know which field of the
    // record carried it, so a control can change its mind about storage without telling a single plugin.
    public string Json(ExtensionSettingKind kind) => kind switch
    {
        ExtensionSettingKind.Toggle => On ? "true" : "false",
        ExtensionSettingKind.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => System.Text.Json.JsonSerializer.Serialize(Choice)
    };

    public static ExtensionSettingValue Of(ExtensionSettingKind kind, bool on, double number, string choice) => kind switch
    {
        ExtensionSettingKind.Toggle => new ExtensionSettingValue("toggle", on, 0, ""),
        ExtensionSettingKind.Number => new ExtensionSettingValue("number", false, number, ""),
        _ => new ExtensionSettingValue("choice", false, 0, choice)
    };
}

// The values kept for a plugin's declared controls: what they start at, what the user's choice means against the
// declaration, and how they are handed to a page. The declaration is the contract on both sides of that — the host
// renders from it, the page reads from it, and nothing a reader stored is allowed to contradict it.
public static class ExtensionSettingStore
{
    // The value before anybody touched the control: the default the author wrote, in the shape the control has.
    public static ExtensionSettingValue Initial(ExtensionSetting setting) => setting.Kind switch
    {
        ExtensionSettingKind.Toggle => new ExtensionSettingValue("toggle", setting.DefaultToggle),
        ExtensionSettingKind.Number => new ExtensionSettingValue("number", false, setting.DefaultNumber),
        _ => new ExtensionSettingValue("choice", false, 0, setting.DefaultChoice)
    };

    // A stored value read against the control it belongs to. A wrong kind or an impossible number is a hint, not an
    // error: it is pulled onto the declaration's own range and step, so the page receives a value the author accepts
    // and the reader's intent survives a hand-edited settings file.
    public static ExtensionSettingValue Honor(ExtensionSetting setting, ExtensionSettingValue? stored)
    {
        if (stored is null || stored.Kind != setting.Type) return Initial(setting);
        return setting.Kind switch
        {
            ExtensionSettingKind.Toggle => new ExtensionSettingValue("toggle", stored.On),
            ExtensionSettingKind.Number => new ExtensionSettingValue("number", false, setting.Snap(stored.Number)),
            _ => new ExtensionSettingValue("choice", false, 0,
                setting.Options.Any(option => option.Value == stored.Choice) ? stored.Choice : setting.DefaultChoice)
        };
    }

    // Every declared control's value, plus anything stored that the plugin no longer declares — kept, because a plugin
    // taken away and put back should come back as it was left, and hidden, because the page only ever reads what its own
    // manifest declares. This is a lookup, not a list: whoever shows the controls in order walks the declaration
    // (Settings), which is where the author's order actually lives.
    public static Dictionary<string, ExtensionSettingValue> Normalize(IReadOnlyList<ExtensionSetting> declared,
        IReadOnlyDictionary<string, ExtensionSettingValue>? stored)
    {
        var values = new Dictionary<string, ExtensionSettingValue>(StringComparer.Ordinal);
        foreach (var setting in declared)
            values[setting.Key] = Honor(setting, stored is not null && stored.TryGetValue(setting.Key, out var kept) ? kept : null);
        if (stored is not null)
            foreach (var (key, value) in stored)
                if (!values.ContainsKey(key) && !string.IsNullOrEmpty(key)) values[key] = value;
        return values;
    }

    // The values a page reads: only the declared controls, each as its own scalar.
    public static string ValuesJson(IReadOnlyList<ExtensionSetting> declared, IReadOnlyDictionary<string, ExtensionSettingValue> stored)
        => "{" + string.Join(",", declared.Select(setting =>
            System.Text.Json.JsonSerializer.Serialize(setting.Key) + ":" +
            (stored.TryGetValue(setting.Key, out var value) ? value : Initial(setting)).Json(setting.Kind))) + "}";

    // The plugin's own words, in the language the interface is in. A phrase the host has none of is left out rather than
    // answered with another language: the page's fallback is the author's own text, which is at least theirs.
    public static string PhrasesJson(IReadOnlyDictionary<string, ExtensionText> phrases, bool english)
        => "{" + string.Join(",", phrases.Where(pair => !pair.Value.IsEmpty).Select(pair =>
            System.Text.Json.JsonSerializer.Serialize(pair.Key) + ":" +
            System.Text.Json.JsonSerializer.Serialize(pair.Value.For(english)))) + "}";
}
