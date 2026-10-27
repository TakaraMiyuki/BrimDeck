using System.Text.Json;

namespace BrimDeck.Core;

// Both languages of a piece of text a plugin author writes in the manifest. One side missing stands in with the other,
// the way titleZh and titleEn already do: an author who writes only Chinese is never forced to invent English, and the
// host still has one text to show whichever language the interface is in.
public sealed record ExtensionText(string Zh, string En)
{
    public static readonly ExtensionText Empty = new("", "");
    public bool IsEmpty => Zh.Length == 0 && En.Length == 0;
    public string For(bool english) => english ? En : Zh;

    // A bare string is the same text in both languages; an object may name either or both. Anything past the cap is cut,
    // not refused — a long label is a typographic problem, not a broken plugin.
    public static ExtensionText Read(JsonElement? value, int cap)
    {
        if (value is not { } node) return Empty;
        string Cut(string text) => text.Length > cap ? text[..cap] : text;
        if (node.ValueKind == JsonValueKind.String)
        {
            var one = Cut((node.GetString() ?? "").Trim());
            return one.Length == 0 ? Empty : new ExtensionText(one, one);
        }
        if (node.ValueKind != JsonValueKind.Object) return Empty;
        string Part(string name) => node.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.String ? Cut(part.GetString()!.Trim()) : "";
        var zh = Part("zh");
        var en = Part("en");
        if (zh.Length == 0 && en.Length == 0) return Empty;
        return new ExtensionText(zh.Length > 0 ? zh : en, en.Length > 0 ? en : zh);
    }
}

public enum ExtensionSettingKind { Toggle, Number, Choice }

// One choice an author's page offers, and the words the host shows beside it.
public sealed record ExtensionSettingOption(string Value, ExtensionText Label);

// One control the host draws in 「设置 → 插件」 and hands back to the page as a value. The author declares it; nobody's
// code runs to make it appear, which is the whole point of the web model — and it is why the label, the language and
// the persistence can all be done once, in the host, for every plugin at once.
// The three defaults are the three kinds' own; the one that does not apply keeps its zero value.
public sealed record ExtensionSetting(
    string Key, ExtensionSettingKind Kind, ExtensionText Label,
    bool DefaultToggle, double DefaultNumber, string DefaultChoice,
    double Minimum, double Maximum, double Step,
    IReadOnlyList<ExtensionSettingOption> Options)
{
    public string Type => Kind switch
    {
        ExtensionSettingKind.Toggle => "toggle",
        ExtensionSettingKind.Number => "number",
        _ => "choice"
    };

    // The grid this control's values live on: min, then whole steps, counted from min, with the float dust swept off —
    // step 0.1 three times from 0 is 0.30000000000000004, and a page should not be asked to compare against that.
    // Everything that can name a value goes through here, so the row in the settings page, the number a page reads at
    // init and the answer the host stores are one number rather than three that nearly agree.
    public double Snap(double value)
    {
        if (!double.IsFinite(value)) return DefaultNumber;
        var clamped = Math.Clamp(value, Minimum, Maximum);
        if (!(Step > 0) || !double.IsFinite(Step)) return Round(clamped);
        var steps = Math.Round((clamped - Minimum) / Step, MidpointRounding.AwayFromZero);
        return Round(Math.Clamp(Minimum + steps * Step, Minimum, Maximum));
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}

// Reading a plugin's declarations: the controls it wants the host to show, and the words it wants the host to speak in
// the interface's language. Both are plain data, and both are forgiving in the same way: an entry the host cannot use is
// dropped with a note, and the plugin still loads. A settings row is not load-bearing the way an id or an entry file is —
// refusing the whole folder because one label was a number would punish a reader for an author's typo.
public static class ExtensionDeclarations
{
    public const int MaximumSettings = 16;
    public const int MaximumOptions = 16;
    public const int MaximumPhrases = 64;
    public const int LabelChars = 64;
    public const int ValueChars = 64;
    public const int PhraseChars = 200;

    // A key is written into the page's code and into the settings file, so it stays in the same family the plugin id is,
    // with '_' allowed as well: temp_warn reads like the author's own variable.
    public static bool ValidKey(string key) => key.Length is > 0 and <= ValueChars
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_');

    public static (IReadOnlyList<ExtensionSetting> Settings, IReadOnlyDictionary<string, ExtensionText> Phrases)
        Read(JsonElement root, List<string> notes)
        => (ReadSettings(root, notes), ReadPhrases(root, notes));

    private static IReadOnlyList<ExtensionSetting> ReadSettings(JsonElement root, List<string> notes)
    {
        var settings = new List<ExtensionSetting>();
        if (!root.TryGetProperty("settings", out var node)) return settings;
        if (node.ValueKind != JsonValueKind.Array)
        {
            notes.Add("settings: " + Loc.T("不是数组，已忽略", "is not an array and was ignored"));
            return settings;
        }
        if (node.GetArrayLength() > MaximumSettings)
            notes.Add("settings: " + Loc.T($"最多 {MaximumSettings} 项，其余已忽略", $"holds more than {MaximumSettings} entries; the rest were ignored"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int at = 0;
        foreach (var item in node.EnumerateArray().Take(MaximumSettings))
        {
            at++;
            string where = "settings[" + (at - 1) + "]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                notes.Add(where + ": " + Loc.T("不是对象，已跳过", "is not an object and was skipped"));
                continue;
            }
            string key = item.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim() : "";
            if (!ValidKey(key))
            {
                notes.Add(where + ".key: " + Loc.T("缺失，或含字母、数字、-、.、_ 之外的字符（" + ValueChars + " 字符内）",
                    $"is missing or holds anything outside letters, digits, - . _ ({ValueChars} chars max)"));
                continue;
            }
            if (!seen.Add(key))
            {
                notes.Add(where + ".key: " + Loc.T("与前面一项重复：", "duplicates an earlier entry: ") + key);
                continue;
            }
            string type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim().ToLowerInvariant() : "";
            var label = ExtensionText.Read(item.TryGetProperty("label", out var l) ? l : null, LabelChars);
            if (label.IsEmpty) label = new ExtensionText(key, key); // the key is better than a blank row
            switch (type)
            {
                case "toggle":
                    settings.Add(Toggle(item, key, label));
                    break;
                case "number":
                    var number = Number(item, key, label, notes, where);
                    if (number is not null) settings.Add(number);
                    break;
                case "choice":
                    var choice = Choice(item, key, label, notes, where);
                    if (choice is not null) settings.Add(choice);
                    break;
                default:
                    notes.Add(where + ".type: " + Loc.T("只认识 toggle / number / choice，收到的是 ", "is not one of toggle / number / choice; it said ") + (type.Length > 0 ? type : "（空）"));
                    break;
            }
        }
        return settings;
    }

    // The three readers below each take one shape. They turn the parsed JsonElement into a record by hand rather than by
    // deserialization on purpose: the tolerances (a default outside the range is pulled in, a bad step becomes 1, a
    // missing default is the first option) are the contract, and a serializer would hide them.
    private static ExtensionSetting Toggle(JsonElement item, string key, ExtensionText label)
        => new(key, ExtensionSettingKind.Toggle, label, Flag(item, "default"), 0, "", 0, 0, 1, Array.Empty<ExtensionSettingOption>());

    private static ExtensionSetting? Number(JsonElement item, string key, ExtensionText label, List<string> notes, string where)
    {
        double minimum = Decimal(item, "min", 0), maximum = Decimal(item, "max", 100);
        if (!(double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum))
        {
            notes.Add(where + ": " + Loc.T("数字项的 min 必须小于 max，且两者都是有限数", "a number needs a finite min below a finite max"));
            return null;
        }
        double step = Decimal(item, "step", 1);
        if (!(double.IsFinite(step) && step > 0)) step = 1;
        // An author's default that misses the range is pulled into it rather than dropped: the row still says what they
        // meant, and the page reads a value the host can honour. Then onto the step as well — a default between two
        // steps is a value the page reads once and cannot write back unchanged.
        double fallback = item.TryGetProperty("default", out var asked) && asked.ValueKind == JsonValueKind.Number && asked.TryGetDouble(out var wanted)
            ? Math.Clamp(wanted, minimum, maximum) : Math.Clamp(0, minimum, maximum);
        var setting = new ExtensionSetting(key, ExtensionSettingKind.Number, label, false, fallback, "", minimum, maximum, step, Array.Empty<ExtensionSettingOption>());
        return setting with { DefaultNumber = setting.Snap(fallback) };
    }

    private static ExtensionSetting? Choice(JsonElement item, string key, ExtensionText label, List<string> notes, string where)
    {
        var options = new List<ExtensionSettingOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!item.TryGetProperty("options", out var node) || node.ValueKind != JsonValueKind.Array || node.GetArrayLength() == 0)
        {
            notes.Add(where + ".options: " + Loc.T("选项项需要一个非空的 options 数组", "a choice needs an options array with at least one entry"));
            return null;
        }
        if (node.GetArrayLength() > MaximumOptions)
            notes.Add(where + ".options: " + Loc.T($"最多 {MaximumOptions} 项，其余已忽略", $"holds more than {MaximumOptions} entries; the rest were ignored"));
        foreach (var option in node.EnumerateArray().Take(MaximumOptions))
        {
            if (option.ValueKind != JsonValueKind.Object) continue;
            string value = option.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            if (value.Length == 0 || value.Length > ValueChars || !seen.Add(value)) continue;
            var text = ExtensionText.Read(option.TryGetProperty("label", out var o) ? o : null, LabelChars);
            options.Add(new ExtensionSettingOption(value, text.IsEmpty ? new ExtensionText(value, value) : text));
        }
        if (options.Count == 0)
        {
            notes.Add(where + ".options: " + Loc.T("没有一个选项带可用的 value", "not one option had a usable value"));
            return null;
        }
        string fallback = item.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : "";
        if (!options.Any(option => option.Value == fallback)) fallback = options[0].Value;
        return new ExtensionSetting(key, ExtensionSettingKind.Choice, label, false, 0, fallback, 0, 0, 1, options);
    }

    private static IReadOnlyDictionary<string, ExtensionText> ReadPhrases(JsonElement root, List<string> notes)
    {
        var phrases = new Dictionary<string, ExtensionText>(StringComparer.Ordinal);
        if (!root.TryGetProperty("phrases", out var node)) return phrases;
        if (node.ValueKind != JsonValueKind.Object)
        {
            notes.Add("phrases: " + Loc.T("不是对象，已忽略", "is not an object and was ignored"));
            return phrases;
        }
        int count = 0;
        foreach (var entry in node.EnumerateObject())
        {
            if (!ValidKey(entry.Name))
            {
                notes.Add("phrases." + entry.Name + ": " + Loc.T("名字不合规则，已跳过", "is not a usable key and was skipped"));
                continue;
            }
            var text = ExtensionText.Read(entry.Value, PhraseChars);
            if (text.IsEmpty) continue;
            if (count >= MaximumPhrases)
            {
                notes.Add("phrases: " + Loc.T($"最多 {MaximumPhrases} 条，其余已忽略", $"holds more than {MaximumPhrases} entries; the rest were ignored"));
                break;
            }
            phrases[entry.Name] = text;
            count++;
        }
        return phrases;
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static double Decimal(JsonElement item, string name, double fallback) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
}
