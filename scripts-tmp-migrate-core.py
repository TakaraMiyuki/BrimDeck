import io

p = r'src\BrimDeck.Core\Settings.cs'
s = io.open(p, encoding='utf-8').read()

# 1. CompactPart enum -> string constants + tolerant order converter
old = '''// The parts of the collapsed notch and capsule that are put in order, left to right. Plugins is the group of the plugins' own widgets, each drawn at the width its plugin asked.
// Saved as numbers, so new members go at the end.
public enum CompactPart { Rings, Music, Monitor, Equalizer, Clock, Plugins }'''
new = '''// The parts of the collapsed notch and capsule that are put in order, left to right. The cover stays at the left; Music places
// only the title or lyric. Each enabled plugin's widget is its own part, keyed "plugin:<id>", so the parts live in one string
// list the reader arranges; the names are stable where an enum's numbers cannot grow per plugin.
public static class CompactPart
{
    public const string Rings = "rings";
    public const string Music = "music";
    public const string Monitor = "monitor";
    public const string Equalizer = "equalizer";
    public const string Clock = "clock";

    // The built-in parts, in their default order; the plugins' own parts are appended by the app as they load.
    public static readonly IReadOnlyList<string> Default = [Rings, Music, Monitor, Equalizer, Clock];

    // A part name the settings recognize: a built-in or one plugin widget.
    public static bool IsKnown(string name) =>
        name is Rings or Music or Monitor or Equalizer or Clock
        || (name.StartsWith("plugin:", StringComparison.Ordinal) && name["plugin:".Length..].Trim().Length > 0);
}

// Reads an order saved as numbers (the enum era) or strings, so old files migrate without a version step: the two branches
// saved their orders as 1.2.3 alike, and a version step cannot tell them apart.
public sealed class CompactPartsOrderConverter : System.Text.Json.Serialization.JsonConverter<List<string>>
{
    private static readonly string[] Numeric = [CompactPart.Rings, CompactPart.Music, CompactPart.Monitor, CompactPart.Equalizer, CompactPart.Clock];
    public override List<string>? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null) return null;
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) throw new System.Text.Json.JsonException("Compact order must be an array.");
        var order = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == System.Text.Json.JsonTokenType.EndArray) return order;
            if (reader.TokenType == System.Text.Json.JsonTokenType.Number)
            {
                var number = reader.GetInt32();
                if (number >= 0 && number < Numeric.Length) order.Add(Numeric[number]);
                continue;                                   // 5 was the short-lived bundled plugins part; per-plugin keys replace it
            }
            if (reader.TokenType == System.Text.Json.JsonTokenType.String && reader.GetString() is { Length: > 0 } name)
                order.Add(name);
        }
        throw new System.Text.Json.JsonException("Compact order array was not closed.");
    }
    public override void Write(System.Text.Json.Utf8JsonWriter writer, List<string> value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var name in value) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}'''
assert old in s, 'enum block not found'
s = s.replace(old, new)

# 2. CompactParts: names + normalize over strings
old = '''public static class CompactParts
{
    // A file saved before the equalizer and the clock were put in order adds them after its parts, the clock last, where they stood.
    public static IReadOnlyList<CompactPart> Default { get; } = [CompactPart.Rings, CompactPart.Music, CompactPart.Monitor, CompactPart.Equalizer, CompactPart.Clock, CompactPart.Plugins];
    // Each known part once, in the saved order; a part missing from the file takes its default place after the others.
    public static List<CompactPart> Normalize(IEnumerable<CompactPart>? order) => [.. (order ?? []).Where(part => Enum.IsDefined(part)).Concat(Default).Distinct()];
}'''
new = '''public static class CompactParts
{
    // The built-in parts in their default order; each enabled plugin's widget is appended after them by the app as it
    // loads (the plugins' keys stay where the reader arranged them).
    public static IReadOnlyList<string> Default { get; } = CompactPart.Default;
    // Each known part once, in the saved order; a part missing from the file takes its default place after the others.
    public static List<string> Normalize(IEnumerable<string>? order) =>
    [
        .. (order ?? []).Where(CompactPart.IsKnown).Distinct(StringComparer.Ordinal),
        .. Default.Where(name => !(order ?? []).Contains(name)),
    ];
}'''
assert old in s, 'CompactParts not found'
s = s.replace(old, new)

# 3. DeckSettings fields/properties
old = 'private List<CompactPart> _notchOrder = [.. CompactParts.Default], _capsuleOrder = [.. CompactParts.Default];'
new = 'private List<string> _notchOrder = [.. CompactParts.Default], _capsuleOrder = [.. CompactParts.Default];'
assert old in s; s = s.replace(old, new)

old = '''    // The order of the quota rings, the title or lyric, the system readings, the equalizer and the clock, for the notch and the capsule each.
    public List<CompactPart> NotchOrder { get => _notchOrder; set => _notchOrder = CompactParts.Normalize(value); }
    public List<CompactPart> CapsuleOrder { get => _capsuleOrder; set => _capsuleOrder = CompactParts.Normalize(value); }'''
new = '''    // The order of the quota rings, the title or lyric, the system readings, the equalizer, the clock and the plugins'
    // own widgets, for the notch and the capsule each. Saved as names; a plugin's key rides with its id.
    [System.Text.Json.Serialization.JsonConverter(typeof(CompactPartsOrderConverter))]
    public List<string> NotchOrder { get => _notchOrder; set => _notchOrder = CompactParts.Normalize(value); }
    [System.Text.Json.Serialization.JsonConverter(typeof(CompactPartsOrderConverter))]
    public List<string> CapsuleOrder { get => _capsuleOrder; set => _capsuleOrder = CompactParts.Normalize(value); }'''
assert old in s; s = s.replace(old, new)

old = '''    public List<CompactPart> CompactOrder(CompactStyle style) => style == CompactStyle.Capsule ? CapsuleOrder : NotchOrder;
    public void SetCompactOrder(CompactStyle style, IEnumerable<CompactPart> order) { if (style == CompactStyle.Notch) NotchOrder = [.. order]; else if (style == CompactStyle.Capsule) CapsuleOrder = [.. order]; }'''
new = '''    public List<string> CompactOrder(CompactStyle style) => style == CompactStyle.Capsule ? CapsuleOrder : NotchOrder;
    public void SetCompactOrder(CompactStyle style, IEnumerable<string> order) { if (style == CompactStyle.Notch) NotchOrder = [.. order]; else if (style == CompactStyle.Capsule) CapsuleOrder = [.. order]; }'''
assert old in s; s = s.replace(old, new)

# 4. ShowsPlugins helper next to the other Shows helpers
old = '''    // The indicator is too thin for readings, as for the clock.
    public bool ShowsMonitor(CompactStyle style) => style switch { CompactStyle.Notch => NotchMonitor, CompactStyle.Capsule => CapsuleMonitor, _ => false };'''
new = '''    // The indicator never shows the plugins' widgets either; the notch and the capsule each switch them on.
    public bool ShowsPlugins(CompactStyle style) => style switch { CompactStyle.Notch => NotchPlugins, CompactStyle.Capsule => CapsulePlugins, _ => false };'''
assert old in s; s = s.replace(old, new)

io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('Core order migration done')
