using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>
    /// The death history the client keeps in the character's custom data (ROK-1931, CONTRACT.md §3 `deaths[]`):
    /// newest first, at most <see cref="Caps.DeathsMax"/>, `at` unique (second precision; a same-second death moves
    /// one second on). Pure logic, no Unity: the plugin only reads/writes the string under <see cref="CustomDataKey"/>.
    /// </summary>
    public static class DeathHistory
    {
        /// <summary>Key in <c>Player.m_customData</c>; saved with the player data in the profile, so it survives restarts.</summary>
        public const string CustomDataKey = "RlValheimLink.deaths";

        private static readonly Regex Int64 = new Regex(@"^(0|-?[1-9]\d{0,18})\z", RegexOptions.CultureInvariant);

        /// <summary>Adds a death at <paramref name="nowUtc"/>; returns it, or null when the world uid is unusable.</summary>
        public static DeathRecord Record(List<DeathRecord> list, string worldUid, double x, double z, DateTime nowUtc)
        {
            if (worldUid == null || !Int64.IsMatch(worldUid) || !long.TryParse(worldUid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return null;
            var at = Truncate(nowUtc.ToUniversalTime());
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in list) taken.Add(d.At);
            while (taken.Contains(MessageJson.Timestamp(at))) at = at.AddSeconds(1);
            var rec = new DeathRecord { WorldUid = worldUid, X = Coord(x), Z = Coord(z), At = MessageJson.Timestamp(at) };
            list.Add(rec);
            SortNewestFirst(list);
            if (list.Count > Caps.DeathsMax) list.RemoveRange(Caps.DeathsMax, list.Count - Caps.DeathsMax);
            return rec;
        }

        /// <summary>
        /// What a character message carries: entries with a valid shape, `at` ≤ capturedAt (a later one waits for the
        /// next capture, CONTRACT §6), unique, newest first, capped. Null when nothing remains (the field is omitted).
        /// </summary>
        public static List<DeathRecord> ForMessage(List<DeathRecord> stored, string capturedAt)
        {
            if (stored == null || !MessageJson.TryParseTimestamp(capturedAt ?? "", out DateTime captured)) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var r = new List<DeathRecord>();
            foreach (var d in stored)
            {
                if (!Usable(d) || !MessageJson.TryParseTimestamp(d.At, out DateTime at) || at > captured || !seen.Add(d.At)) continue;
                r.Add(new DeathRecord { WorldUid = d.WorldUid, X = d.X, Z = d.Z, At = d.At });
            }
            SortNewestFirst(r);
            if (r.Count > Caps.DeathsMax) r.RemoveRange(Caps.DeathsMax, r.Count - Caps.DeathsMax);
            return r.Count == 0 ? null : r;
        }

        /// <summary>Server half: drops `deaths[]` entries later than the (possibly server-clamped) capturedAt. Returns how many.</summary>
        public static int DropAfter(JArray deaths, string capturedAt)
        {
            if (deaths == null || !MessageJson.TryParseTimestamp(capturedAt ?? "", out DateTime captured)) return 0;
            int dropped = 0;
            for (int i = deaths.Count - 1; i >= 0; i--)
            {
                var at = (deaths[i] as JObject)?["at"] as JValue;
                if (at != null && at.Type == JTokenType.String && MessageJson.TryParseTimestamp((string)at, out DateTime t) && t > captured)
                { deaths.RemoveAt(i); dropped++; }
            }
            return dropped;
        }

        public static string Serialize(List<DeathRecord> list) => JsonConvert.SerializeObject(list ?? new List<DeathRecord>(), Formatting.None);

        /// <summary>Tolerant read of the stored string: anything unreadable is skipped, a corrupt store reads as empty.</summary>
        public static List<DeathRecord> Parse(string stored)
        {
            var r = new List<DeathRecord>();
            if (string.IsNullOrEmpty(stored)) return r;
            JArray a;
            try { a = MessageJson.Parse(stored) as JArray; } catch (Exception) { return r; }
            if (a == null) return r;
            foreach (var t in a)
            {
                try
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    var d = new DeathRecord { WorldUid = (string)(o["worldUid"] as JValue), X = (double)o["x"], Z = (double)o["z"], At = (string)(o["at"] as JValue) };
                    if (Usable(d) && MessageJson.TryParseTimestamp(d.At, out _)) r.Add(d);
                }
                catch (Exception) { }
            }
            SortNewestFirst(r);
            return r;
        }

        private static bool Usable(DeathRecord d) =>
            d != null && d.WorldUid != null && Int64.IsMatch(d.WorldUid) && long.TryParse(d.WorldUid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) &&
            d.At != null && !double.IsNaN(d.X) && !double.IsNaN(d.Z) && Math.Abs(d.X) <= Caps.WorldCoordMax && Math.Abs(d.Z) <= Caps.WorldCoordMax;

        private static double Coord(double v)
        {
            if (double.IsNaN(v)) v = 0;
            v = Math.Max(-Caps.WorldCoordMax, Math.Min(Caps.WorldCoordMax, v));
            return Math.Round(v, 2, MidpointRounding.AwayFromZero);
        }

        private static DateTime Truncate(DateTime t) => new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        private static void SortNewestFirst(List<DeathRecord> list) => list.Sort((a, b) => string.CompareOrdinal(b.At, a.At));
    }
}
