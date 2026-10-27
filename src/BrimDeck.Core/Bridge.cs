namespace BrimDeck.Core;

// The bridge is the only contract between a plugin page and the host. Its version and its ceilings are stated once
// here, where both sides of the seam read the same numbers and a test can hold them to it.
public static class Bridge
{
    // Raised when an event or message a page can see changes shape in a way an older page would misread. A plugin that
    // needs a newer bridge says so with "minApiVersion" and is refused at the folder, not left showing a blank page.
    public const int Version = 1;
    // What a page may hand the host to keep on its behalf. Measured in UTF-8 bytes, the unit the ceiling is named in:
    // a Chinese-heavy state costs three of those per character the page wrote.
    public const int MaximumStateBytes = 64 * 1024;
    public static int StateBytes(string json) => System.Text.Encoding.UTF8.GetByteCount(json);
    public static bool StateFits(string json) => StateBytes(json) <= MaximumStateBytes;
    public const int MaximumAlarms = 32;
    // How far ahead an alarm can be asked for: the ends DateTimeOffset can hold, in Unix milliseconds. Past either end
    // the conversion throws, and a throw inside the bridge leaves the page with no answer at all — which the bridge has
    // promised it never does.
    public const long MinimumAlarmMillis = -62135596800000L;
    public const long MaximumAlarmMillis = 253402300799999L;
    public static bool AlarmTimeReachable(long milliseconds) => milliseconds is >= MinimumAlarmMillis and <= MaximumAlarmMillis;
    // Events the host buffers for a page that has not subscribed yet — enough for init and a couple of changes, not a tape.
    public const int BufferedEventsPerKind = 8;
    // A link a page hands out: a web address, not a payload. Longer than this it is something else.
    public const int MaximumUrlChars = 2048;
    // How often one plugin may open the desktop browser, and over how long a stretch. Every ask is counted: what reaches
    // the host is a message, and a message cannot show a reader's fingerprint.
    public const int MaximumLinksPerWindow = 6;
    public static readonly TimeSpan LinkWindow = TimeSpan.FromSeconds(20);

    // The ceilings exactly as a page receives them in init.limits, so the words the docs use, the numbers the host keeps
    // and the JSON a page reads are one definition rather than three that can drift apart. Rate is sent as the window it
    // is enforced over — folded into "per minute" it would promise a burst the window does not allow.
    public static string LimitsJson { get; } = "{\"stateBytes\":" + MaximumStateBytes
        + ",\"alarms\":" + MaximumAlarms
        + ",\"urlChars\":" + MaximumUrlChars
        + ",\"linksPerWindow\":" + MaximumLinksPerWindow
        + ",\"linkWindowSeconds\":" + (int)LinkWindow.TotalSeconds + "}";
}
