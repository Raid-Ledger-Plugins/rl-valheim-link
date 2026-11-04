using System.Text.RegularExpressions;

namespace RlValheimLink.Core
{
    /// <summary>
    /// Pure rules for `map` pins (CONTRACT.md §4, ROK-1881). The client localises the raw pin name with the
    /// player's loaded language first; everything after that lives here so it is unit-tested.
    /// </summary>
    public static class MapPins
    {
        public const int LabelMax = 64;

        // TextMeshPro rich-text tags, matched by an allow-list of TMP tag names (case-insensitive), opening or
        // closing: <b>, </color>, <color=#f00>, <size=120%>, <sprite=3>, <sprite name="x">. The name must be
        // followed by '=', whitespace or '>', so player text such as "<Home>", "<b-side>", "<3" or "a < b"
        // survives (TMP renders unknown tags literally, so that is what the player sees in game).
        private const string TagNames =
            "b|i|u|s|color|size|sprite|mark|sub|sup|font|alpha|material|link|voffset|cspace|mspace|pos|indent|" +
            "line-height|line-indent|margin|align|rotate|noparse|nobr|lowercase|uppercase|smallcaps|style|gradient|width";

        // The <#rgb>, <#rgba>, <#rrggbb>, <#rrggbbaa> colour shorthand is a real TMP colour tag, so it goes too;
        // any other length ("<#12345>", "<#123x>") is plain text and stays.
        private static readonly Regex Tag = new Regex(
            @"</?(?:" + TagNames + @")(?=[\s=>])[^<>]{0,127}>|<#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})>",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // C0 + DEL + C1 controls, the line/paragraph separators and the bidi embedding/override/isolate controls
        // (which can visually reorder a label) all become spaces.
        private static readonly Regex Control = new Regex(
            @"[\p{Cc}\u2028\u2029\u202A-\u202E\u2066-\u2069]+", RegexOptions.CultureInvariant);
        private static readonly Regex Integer = new Regex(@"^-?[0-9]+\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// The wire label from an already-localised pin name: rich-text tags removed, control characters
        /// (newlines, tabs) turned into spaces, trimmed, clipped to 64 UTF-16 units without splitting a surrogate
        /// pair. Null, whitespace or tags-only become "" (the contract allows an empty label).
        /// </summary>
        public static string Label(string localized)
        {
            if (string.IsNullOrEmpty(localized)) return "";
            string s = Tag.Replace(localized, "");
            s = Control.Replace(s, " ").Trim();
            return Names.Clip(s, LabelMax).TrimEnd();
        }

        /// <summary>
        /// A pin came from a cartography table when its owner id is non-zero. Vanilla (0.221.4 IL):
        /// AddSharedMapData skips pins whose owner is the local player and adds the rest with the writer's
        /// owner id; the player's own pins carry 0; clicking a shared pin adopts it by setting the id to 0.
        /// </summary>
        public static bool IsShared(long ownerId) => ownerId != 0L;

        /// <summary>
        /// True when a pin type's enum name is a bare integer: a value outside the vanilla
        /// <c>Minimap.PinType</c> enum, added by a mod. It is still sent as-is (the contract's type set is open).
        /// </summary>
        public static bool IsUnnamedType(string enumName) => enumName != null && Integer.IsMatch(enumName);
    }
}
