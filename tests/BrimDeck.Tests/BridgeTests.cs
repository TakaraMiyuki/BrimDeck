using BrimDeck.Core;

// The ceilings a page is answered by. Each of these numbers is quoted to the page in init.limits, so a check here is a
// check that the page can compute the same answer the host will give it.
internal static class BridgeTests
{
    internal static void Run(Action<string, bool> check)
    {
        // The ceiling says bytes; a character-counting comparison would let a Chinese-heavy state be three times the
        // size the page was told it may use.
        check("Bridge: the state ceiling is measured in UTF-8 bytes, not in characters",
            Bridge.StateBytes("aaaa") == 4 && Bridge.StateBytes("啊啊") == 6 && Bridge.StateBytes("😀") == 4);
        var justFits = new string('x', Bridge.MaximumStateBytes - 2); // wrapped in quotes by the page's JSON
        var oneOver = new string('啊', Bridge.MaximumStateBytes / 3 + 1); // three bytes each, so it is over on its own
        check("Bridge: a state fits to the byte and is refused one byte past it",
            Bridge.StateFits("\"" + justFits + "\"") && !Bridge.StateFits("\"" + justFits + "x\""));
        check("Bridge: a state of non-ASCII text is judged by what it weighs, not by what the page counted",
            oneOver.Length < Bridge.MaximumStateBytes && !Bridge.StateFits(oneOver));

        // An alarm time outside DateTimeOffset's range throws where it is read; the bridge owes the page an answer, so
        // the range is refused before the conversion is ever tried.
        check("Bridge: an alarm inside the reachable range converts",
            Bridge.AlarmTimeReachable(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        bool extremes = true;
        foreach (var bad in new long[] { long.MinValue, long.MinValue / 2, -62135596800001L, 253402300800000L, long.MaxValue / 2, long.MaxValue })
            if (Bridge.AlarmTimeReachable(bad)) extremes = false;
        check("Bridge: an alarm past either end of the calendar is refused instead of throwing", extremes
            && Bridge.AlarmTimeReachable(Bridge.MinimumAlarmMillis) && Bridge.AlarmTimeReachable(Bridge.MaximumAlarmMillis));
        check("Bridge: the range is the calendar DateTimeOffset itself holds",
            DateTimeOffset.FromUnixTimeMilliseconds(Bridge.MinimumAlarmMillis).UtcDateTime.Year == 1
            && DateTimeOffset.FromUnixTimeMilliseconds(Bridge.MaximumAlarmMillis).UtcDateTime.Year == 9999);

        // The page is told the ceilings as JSON; the words in it are the host's own, in the units they are enforced in,
        // so a page that plans around them cannot be refused for something it computed correctly.
        check("Bridge: what a page is told is what the host keeps, spelled as it is kept",
            Bridge.LimitsJson.Contains("\"stateBytes\":" + Bridge.MaximumStateBytes)
            && Bridge.LimitsJson.Contains("\"alarms\":" + Bridge.MaximumAlarms)
            && Bridge.LimitsJson.Contains("\"urlChars\":" + Bridge.MaximumUrlChars)
            && Bridge.LimitsJson.Contains("\"linksPerWindow\":" + Bridge.MaximumLinksPerWindow)
            && Bridge.LimitsJson.Contains("\"linkWindowSeconds\":" + (int)Bridge.LinkWindow.TotalSeconds)
            && !Bridge.LimitsJson.Contains("PerMinute"));
        check("Bridge: the ceilings arrive as one object a page can read",
            System.Text.Json.JsonDocument.Parse(Bridge.LimitsJson).RootElement.ValueKind is System.Text.Json.JsonValueKind.Object);
    }
}
