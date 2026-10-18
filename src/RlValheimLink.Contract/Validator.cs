using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RlValheimLink.Contract
{
    public sealed class ValidationIssue
    {
        public string Path { get; }
        public string Code { get; }
        public string Message { get; }
        public ValidationIssue(string path, string code, string message) { Path = path; Code = code; Message = message; }
        public override string ToString() => $"{(Path.Length == 0 ? "<root>" : Path)}: {Code} ({Message})";
    }

    /// <summary>
    /// Pre-send validator: everything contract/v1/schema.json says PLUS the refinements of
    /// CONTRACT.md §6 that JSON Schema cannot express. Issue codes and paths follow Zod's
    /// (unrecognized_keys, invalid_type, invalid_value, invalid_union, invalid_format,
    /// too_small, too_big, custom; path joined with ".", "" = root) so
    /// contract/v1/fixtures/invalid/EXPECTED.json can be checked against it.
    /// Pure structural validation: it never inflates a bitmap (see <see cref="Bitset.Decode"/>).
    /// </summary>
    public static class Validator
    {
        private static readonly Regex ServerId = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex GameVersion = new Regex(@"^\d{1,4}(\.\d{1,5}){1,3}$", RegexOptions.CultureInvariant);
        private static readonly Regex SemVer = new Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex IsoDateTime = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?Z$", RegexOptions.CultureInvariant);
        private static readonly Regex SteamId = new Regex(@"^[1-9]\d{16}$", RegexOptions.CultureInvariant);
        private static readonly Regex Int64 = new Regex(@"^(0|-?[1-9]\d{0,18})$", RegexOptions.CultureInvariant);
        private static readonly Regex Prefab = new Regex("^[A-Za-z0-9_.-]{1,96}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256 = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SkillId = new Regex("^-?[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly Regex PinType = new Regex("^[A-Za-z0-9_]{1,32}$", RegexOptions.CultureInvariant);
        private static readonly Regex Category = new Regex("^[A-Za-z0-9_]{1,48}$", RegexOptions.CultureInvariant);
        private static readonly Regex Locale = new Regex("^[A-Za-z_-]{2,32}$", RegexOptions.CultureInvariant);
        private static readonly Regex IconFile = new Regex(@"^[A-Za-z0-9_.-]{1,100}\.png$", RegexOptions.CultureInvariant);
        private static readonly Regex BitmapData = new Regex("^H4sI[A-Za-z0-9+/]*={0,2}$", RegexOptions.CultureInvariant);

        private static readonly BigInteger SteamMin = BigInteger.Parse(Caps.SteamId64Min, CultureInfo.InvariantCulture);
        private static readonly BigInteger I64Min = BigInteger.Parse(Caps.Int64Min, CultureInfo.InvariantCulture);
        private static readonly BigInteger I64Max = BigInteger.Parse(Caps.Int64Max, CultureInfo.InvariantCulture);

        public static List<ValidationIssue> Validate(LinkMessage message) => Validate(MessageJson.ToToken(message));

        public static List<ValidationIssue> Validate(string json)
        {
            try { return Validate(MessageJson.Parse(json)); }
            catch (Exception e) { return new List<ValidationIssue> { new ValidationIssue("", "invalid_type", "not JSON: " + e.Message) }; }
        }

        public static List<ValidationIssue> Validate(JToken root)
        {
            var issues = new List<ValidationIssue>();
            var obj = root as JObject;
            if (obj == null) { issues.Add(new ValidationIssue("", "invalid_type", "message must be an object")); return issues; }

            string kind = (obj["kind"] as JValue)?.Type == JTokenType.String ? (string)obj["kind"] : null;
            if (kind != "character" && kind != "map" && kind != "items")
            {
                issues.Add(new ValidationIssue("kind", "invalid_union", "kind must be character, map or items"));
                return issues;
            }

            switch (kind)
            {
                case "character": Character(obj, issues); break;
                case "map": Map(obj, issues); break;
                case "items": Items(obj, issues); break;
            }
            return issues;
        }

        // ---------- kinds ----------

        private static readonly string[] HeaderKeys = { "schemaVersion", "serverId", "capturedAt", "gameVersion", "modVersion", "kind" };

        private static void Header(JObject o, List<ValidationIssue> issues)
        {
            var sv = o["schemaVersion"];
            if (sv == null || sv.Type != JTokenType.Integer && sv.Type != JTokenType.Float)
                issues.Add(new ValidationIssue("schemaVersion", "invalid_type", "expected number"));
            else if ((double)sv != Caps.SchemaVersion)
                issues.Add(new ValidationIssue("schemaVersion", "invalid_value", "schemaVersion must be 1"));

            Str(o, "serverId", "", issues, 1, 64, ServerId);
            if (Str(o, "capturedAt", "", issues, 1, null, null, out string ts))
            {
                if (!IsoDateTime.IsMatch(ts)) issues.Add(new ValidationIssue("capturedAt", "invalid_format", "ISO-8601 UTC with Z"));
                if (ts.Length > Caps.TimestampMaxChars) issues.Add(new ValidationIssue("capturedAt", "too_big", "at most 3 fractional digits"));
            }
            Str(o, "gameVersion", "", issues, 1, null, GameVersion);
            if (Str(o, "modVersion", "", issues, 1, null, null, out string mv))
            {
                if (mv.Length > 64) issues.Add(new ValidationIssue("modVersion", "too_big", "at most 64 chars"));
                else if (!SemVer.IsMatch(mv)) issues.Add(new ValidationIssue("modVersion", "invalid_format", "SemVer 2.0.0"));
            }
        }

        private static void Character(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "character", "equipment", "inventory", "skills", "trophies", "knownItems", "knownRecipes");
            Header(o, issues);

            if (Obj(o, "character", "", issues, out JObject ch))
            {
                Keys(ch, "character", issues, "steamId", "playerId", "name", "worldUid", "worldName");
                SteamId64(ch, "steamId", "character", issues);
                Int64Str(ch, "playerId", "character", issues);
                Str(ch, "name", "character", issues, 1, 32, null);
                Int64Str(ch, "worldUid", "character", issues);
                Str(ch, "worldName", "character", issues, 1, 64, null);
            }

            if (Arr(o, "equipment", "", issues, 0, Caps.EquipmentMax, out JArray eq))
            {
                var seen = new HashSet<string>();
                for (int i = 0; i < eq.Count; i++)
                {
                    string p = "equipment." + i;
                    if (!ObjAt(eq[i], p, issues, out JObject e)) continue;
                    Keys(e, p, issues, "slot", "item");
                    if (Enum(e, "slot", p, issues, EquipSlot.All, out string slot))
                        if (!seen.Add(slot)) issues.Add(new ValidationIssue(p, "custom", "duplicate equipment entry"));
                    if (Obj(e, "item", p, issues, out JObject item)) ItemStack(item, p + ".item", issues);
                }
            }

            if (Obj(o, "inventory", "", issues, out JObject inv))
            {
                Keys(inv, "inventory", issues, "grid", "items");
                int width = 0, height = 0;
                bool gridOk = false;
                JArray extraRows = null;
                if (Obj(inv, "grid", "inventory", issues, out JObject grid))
                {
                    Keys(grid, "inventory.grid", issues, "width", "height", "extraRows");
                    bool w = Int(grid, "width", "inventory.grid", issues, 1, Caps.GridEdgeMax, out width);
                    bool h = Int(grid, "height", "inventory.grid", issues, 1, Caps.GridEdgeMax, out height);
                    gridOk = w && h;
                    if (Arr(grid, "extraRows", "inventory.grid", issues, 0, Caps.GridEdgeMax, out extraRows))
                    {
                        for (int i = 0; i < extraRows.Count; i++)
                        {
                            string p = "inventory.grid.extraRows." + i;
                            if (!ObjAt(extraRows[i], p, issues, out JObject row)) { extraRows = null; continue; }
                            Keys(row, p, issues, "y", "purpose");
                            Int(row, "y", p, issues, 0, Caps.GridEdgeMax - 1, out _);
                            Enum(row, "purpose", p, issues, RowPurpose.All, out _);
                        }
                    }
                }
                if (Arr(inv, "items", "inventory", issues, 0, Caps.InventoryItemsMax, out JArray items))
                {
                    var seen = new HashSet<string>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        string p = "inventory.items." + i;
                        if (!ObjAt(items[i], p, issues, out JObject slot)) continue;
                        Keys(slot, p, issues, "x", "y", "equipped", "item");
                        bool xok = Int(slot, "x", p, issues, 0, Caps.GridEdgeMax - 1, out int x);
                        bool yok = Int(slot, "y", p, issues, 0, Caps.GridEdgeMax - 1, out int y);
                        Bool(slot, "equipped", p, issues);
                        if (Obj(slot, "item", p, issues, out JObject item)) ItemStack(item, p + ".item", issues);
                        if (gridOk && xok && yok)
                        {
                            if (x >= width || y >= height) issues.Add(new ValidationIssue(p, "custom", "slot outside grid"));
                            else if (!seen.Add(x + "," + y)) issues.Add(new ValidationIssue(p, "custom", "duplicate slot"));
                        }
                    }
                }
                if (gridOk && extraRows != null)
                {
                    var seenRows = new HashSet<int>();
                    bool bad = false;
                    for (int i = 0; i < extraRows.Count; i++)
                    {
                        var row = (JObject)extraRows[i];
                        if (!(row["y"] is JValue yv) || yv.Type != JTokenType.Integer) { bad = true; continue; }
                        int y = (int)yv;
                        string problem = y < Caps.VanillaRows ? "row is a vanilla row" : y >= height ? "row outside grid" : !seenRows.Add(y) ? "duplicate row" : null;
                        if (problem != null) { bad = true; issues.Add(new ValidationIssue("inventory.grid.extraRows." + i, "custom", problem)); }
                    }
                    int want = Math.Max(0, height - Caps.VanillaRows);
                    if (!bad && extraRows.Count != want)
                        issues.Add(new ValidationIssue("inventory.grid.extraRows", "custom", $"every row past the vanilla four must be listed ({want})"));
                }
            }

            if (Arr(o, "skills", "", issues, 0, Caps.SkillsMax, out JArray skills))
            {
                var seen = new HashSet<string>();
                for (int i = 0; i < skills.Count; i++)
                {
                    string p = "skills." + i;
                    if (!ObjAt(skills[i], p, issues, out JObject s)) continue;
                    Keys(s, p, issues, "id", "level", "accumulator");
                    if (Str(s, "id", p, issues, 1, null, SkillId, out string id))
                        if (!seen.Add(id)) issues.Add(new ValidationIssue(p, "custom", "duplicate skills entry"));
                    Num(s, "level", p, issues, 0, 100);
                    Num(s, "accumulator", p, issues, 0, 1_000_000);
                }
            }

            PrefabList(o, "trophies", "", issues, Caps.TrophiesMax);
            PrefabList(o, "knownItems", "", issues, Caps.KnownMax);
            PrefabList(o, "knownRecipes", "", issues, Caps.KnownMax);
        }

        private static void ItemStack(JObject item, string path, List<ValidationIssue> issues)
        {
            Keys(item, path, issues, "prefab", "stack", "quality", "variant", "durability", "maxDurability");
            Str(item, "prefab", path, issues, 1, 96, Prefab);
            Int(item, "stack", path, issues, 1, 100_000, out _);
            Int(item, "quality", path, issues, 1, 10, out _);
            Int(item, "variant", path, issues, 0, 63, out _);
            bool dNull = NullableNum(item, "durability", path, issues, 0, 1_000_000);
            bool mNull = NullableNum(item, "maxDurability", path, issues, 0, 1_000_000);
            if (item["durability"] != null && item["maxDurability"] != null && dNull != mNull)
                issues.Add(new ValidationIssue(path + ".maxDurability", "custom", "durability and maxDurability are null together"));
        }

        private static void Map(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "character", "grid", "explored", "sharedExplored", "pins", "baseMap");
            Header(o, issues);

            if (Obj(o, "character", "", issues, out JObject ch))
            {
                Keys(ch, "character", issues, "steamId", "playerId", "worldUid");
                SteamId64(ch, "steamId", "character", issues);
                Int64Str(ch, "playerId", "character", issues);
                Int64Str(ch, "worldUid", "character", issues);
            }

            int size = 0; bool sizeOk = false;
            if (Obj(o, "grid", "", issues, out JObject grid))
            {
                Keys(grid, "grid", issues, "textureSize", "pixelSizeMetres");
                sizeOk = Int(grid, "textureSize", "grid", issues, Caps.BitmapEdgeMin, Caps.BitmapEdgeMax, out size);
                Num(grid, "pixelSizeMetres", "grid", issues, double.Epsilon, 256);
            }

            if (Obj(o, "explored", "", issues, out JObject explored))
                Bitmap(explored, "explored", issues, sizeOk ? size : -1);

            var shared = o["sharedExplored"];
            if (shared == null) issues.Add(new ValidationIssue("sharedExplored", "invalid_type", "required (nullable)"));
            else if (shared.Type != JTokenType.Null)
            {
                if (ObjAt(shared, "sharedExplored", issues, out JObject sh)) Bitmap(sh, "sharedExplored", issues, sizeOk ? size : -1);
            }

            if (Arr(o, "pins", "", issues, 0, Caps.PinsMax, out JArray pins))
            {
                for (int i = 0; i < pins.Count; i++)
                {
                    string p = "pins." + i;
                    if (!ObjAt(pins[i], p, issues, out JObject pin)) continue;
                    Keys(pin, p, issues, "type", "label", "x", "z", "checked", "shared");
                    Str(pin, "type", p, issues, 1, 32, PinType);
                    Str(pin, "label", p, issues, 0, 64, null);
                    Num(pin, "x", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax);
                    Num(pin, "z", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax);
                    Bool(pin, "checked", p, issues);
                    Bool(pin, "shared", p, issues);
                }
            }

            var bm = o["baseMap"];
            if (bm == null) issues.Add(new ValidationIssue("baseMap", "invalid_type", "required (nullable)"));
            else if (bm.Type != JTokenType.Null)
            {
                if (ObjAt(bm, "baseMap", issues, out JObject b) && BinaryRef(b, "baseMap", issues, out int w, out int h) && sizeOk && (w != size || h != size))
                    issues.Add(new ValidationIssue("baseMap", "custom", "base map size must equal grid.textureSize"));
            }
        }

        private static void Bitmap(JObject b, string path, List<ValidationIssue> issues, int textureSize)
        {
            Keys(b, path, issues, "encoding", "width", "height", "data");
            if (Str(b, "encoding", path, issues, 1, null, null, out string enc) && enc != ExploredBitmap.EncodingName)
                issues.Add(new ValidationIssue(path + ".encoding", "invalid_value", "encoding must be " + ExploredBitmap.EncodingName));
            bool wOk = Int(b, "width", path, issues, Caps.BitmapEdgeMin, Caps.BitmapEdgeMax, out int w);
            bool hOk = Int(b, "height", path, issues, Caps.BitmapEdgeMin, Caps.BitmapEdgeMax, out int h);
            if (Str(b, "data", path, issues, 0, null, null, out string data))
            {
                string dp = path + ".data";
                if (data.Length < Caps.BitmapDataMinChars) issues.Add(new ValidationIssue(dp, "too_small", "at least 28 chars"));
                if (data.Length > Caps.BitmapDataMaxChars) issues.Add(new ValidationIssue(dp, "too_big", "at most 3000000 chars"));
                if (!BitmapData.IsMatch(data)) issues.Add(new ValidationIssue(dp, "invalid_format", "one gzip member (H4sI…), standard base64"));
                if (data.Length % 4 != 0) issues.Add(new ValidationIssue(dp, "custom", "base64 length must be a multiple of 4"));
                if (wOk && hOk && data.Length > Caps.BitmapMaxDataChars(w, h))
                    issues.Add(new ValidationIssue(dp, "custom", $"compressed bitmap exceeds {Caps.BitmapMaxDataChars(w, h)} chars for {w}×{h}"));
            }
            if (wOk && hOk && textureSize > 0 && (w != textureSize || h != textureSize))
                issues.Add(new ValidationIssue(path, "custom", "bitmap size must equal grid.textureSize"));
        }

        private static bool BinaryRef(JObject b, string path, List<ValidationIssue> issues, out int w, out int h)
        {
            Keys(b, path, issues, "sha256", "byteLength", "contentType", "width", "height");
            Str(b, "sha256", path, issues, 64, 64, Sha256);
            Int(b, "byteLength", path, issues, 1, Caps.BinaryMaxBytes, out _);
            if (Str(b, "contentType", path, issues, 1, null, null, out string ct) && ct != "image/png")
                issues.Add(new ValidationIssue(path + ".contentType", "invalid_value", "contentType must be image/png"));
            bool wOk = Int(b, "width", path, issues, 1, Caps.BinaryEdgeMax, out w);
            bool hOk = Int(b, "height", path, issues, 1, Caps.BinaryEdgeMax, out h);
            return wOk && hOk;
        }

        private static void Items(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "locale", "atlas", "items");
            Header(o, issues);
            Str(o, "locale", "", issues, 2, 32, Locale);

            bool hasAtlas = false; int aw = 0, ah = 0;
            var atlas = o["atlas"];
            if (atlas == null) issues.Add(new ValidationIssue("atlas", "invalid_type", "required (nullable)"));
            else if (atlas.Type != JTokenType.Null)
            {
                if (ObjAt(atlas, "atlas", issues, out JObject a)) hasAtlas = BinaryRef(a, "atlas", issues, out aw, out ah);
            }

            bool anyAtlasIcon = false;
            if (Arr(o, "items", "", issues, Caps.ItemExportMin, Caps.ItemExportMax, out JArray items))
            {
                var seen = new HashSet<string>();
                for (int i = 0; i < items.Count; i++)
                {
                    string p = "items." + i;
                    if (!ObjAt(items[i], p, issues, out JObject it)) continue;
                    Keys(it, p, issues, "prefab", "name", "category", "maxStack", "maxQuality", "variants", "icon");
                    if (Str(it, "prefab", p, issues, 1, 96, Prefab, out string prefab))
                        if (!seen.Add(prefab)) issues.Add(new ValidationIssue(p + ".prefab", "custom", "duplicate prefab"));
                    Str(it, "name", p, issues, 1, 96, null);
                    Str(it, "category", p, issues, 1, 48, Category);
                    Int(it, "maxStack", p, issues, 1, 100_000, out _);
                    Int(it, "maxQuality", p, issues, 1, 10, out _);
                    Int(it, "variants", p, issues, 0, 64, out _);

                    var icon = it["icon"];
                    if (icon == null) { issues.Add(new ValidationIssue(p + ".icon", "invalid_type", "required (nullable)")); continue; }
                    if (icon.Type == JTokenType.Null) continue;
                    string ip = p + ".icon";
                    if (!ObjAt(icon, ip, issues, out JObject ic)) continue;
                    string ik = (ic["kind"] as JValue)?.Type == JTokenType.String ? (string)ic["kind"] : null;
                    if (ik == "atlas")
                    {
                        anyAtlasIcon = true;
                        Keys(ic, ip, issues, "kind", "x", "y", "w", "h");
                        bool xo = Int(ic, "x", ip, issues, 0, 16_383, out int x);
                        bool yo = Int(ic, "y", ip, issues, 0, 16_383, out int y);
                        bool wo = Int(ic, "w", ip, issues, 1, 512, out int w);
                        bool ho = Int(ic, "h", ip, issues, 1, 512, out int h);
                        if (hasAtlas && xo && yo && wo && ho && (x + w > aw || y + h > ah))
                            issues.Add(new ValidationIssue(ip, "custom", "icon outside atlas"));
                    }
                    else if (ik == "file")
                    {
                        Keys(ic, ip, issues, "kind", "file");
                        Str(ic, "file", ip, issues, 5, 104, IconFile);
                    }
                    else issues.Add(new ValidationIssue(ip + ".kind", "invalid_union", "icon kind must be atlas or file"));
                }
            }
            if (atlas != null && atlas.Type == JTokenType.Null && anyAtlasIcon)
                issues.Add(new ValidationIssue("atlas", "custom", "atlas icon without an atlas"));
        }

        // ---------- primitives ----------

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private static void Keys(JObject o, string path, List<ValidationIssue> issues, string[] first, params string[] rest)
        {
            var all = new string[first.Length + rest.Length];
            first.CopyTo(all, 0); rest.CopyTo(all, first.Length);
            Keys(o, path, issues, all);
        }

        private static void Keys(JObject o, string path, List<ValidationIssue> issues, params string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            List<string> unknown = null;
            foreach (var prop in o.Properties())
                if (!set.Contains(prop.Name)) (unknown ?? (unknown = new List<string>())).Add(prop.Name);
            if (unknown != null) issues.Add(new ValidationIssue(path, "unrecognized_keys", "unknown key(s): " + string.Join(", ", unknown)));
        }

        private static bool ObjAt(JToken t, string path, List<ValidationIssue> issues, out JObject o)
        {
            o = t as JObject;
            if (o != null) return true;
            issues.Add(new ValidationIssue(path, "invalid_type", "expected object"));
            return false;
        }

        private static bool Obj(JObject parent, string key, string path, List<ValidationIssue> issues, out JObject o)
        {
            o = null;
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required")); return false; }
            return ObjAt(t, Join(path, key), issues, out o);
        }

        private static bool Arr(JObject parent, string key, string path, List<ValidationIssue> issues, int min, int max, out JArray a)
        {
            a = null;
            string p = Join(path, key);
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(p, "invalid_type", "required")); return false; }
            a = t as JArray;
            if (a == null) { issues.Add(new ValidationIssue(p, "invalid_type", "expected array")); return false; }
            if (a.Count < min) issues.Add(new ValidationIssue(p, "too_small", $"at least {min} entries"));
            if (a.Count > max) issues.Add(new ValidationIssue(p, "too_big", $"at most {max} entries"));
            return true;
        }

        private static bool Str(JObject parent, string key, string path, List<ValidationIssue> issues, int min, int? max, Regex pattern) =>
            Str(parent, key, path, issues, min, max, pattern, out _);

        private static bool Str(JObject parent, string key, string path, List<ValidationIssue> issues, int min, int? max, Regex pattern, out string value)
        {
            value = null;
            string p = Join(path, key);
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(p, "invalid_type", "required")); return false; }
            if (t.Type != JTokenType.String) { issues.Add(new ValidationIssue(p, "invalid_type", "expected string")); return false; }
            value = (string)t;
            bool ok = true;
            if (value.Length < min) { issues.Add(new ValidationIssue(p, "too_small", $"at least {min} chars")); ok = false; }
            if (max.HasValue && value.Length > max.Value) { issues.Add(new ValidationIssue(p, "too_big", $"at most {max} chars")); ok = false; }
            if (ok && pattern != null && !pattern.IsMatch(value)) { issues.Add(new ValidationIssue(p, "invalid_format", "pattern " + pattern)); ok = false; }
            return ok;
        }

        private static bool Enum(JObject parent, string key, string path, List<ValidationIssue> issues, string[] values, out string value)
        {
            if (!Str(parent, key, path, issues, 0, null, null, out value)) return false;
            if (Array.IndexOf(values, value) >= 0) return true;
            issues.Add(new ValidationIssue(Join(path, key), "invalid_value", "one of " + string.Join("|", values)));
            return false;
        }

        private static bool Bool(JObject parent, string key, string path, List<ValidationIssue> issues)
        {
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required")); return false; }
            if (t.Type != JTokenType.Boolean) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "expected boolean")); return false; }
            return true;
        }

        private static bool NumberToken(JToken t, out double v)
        {
            v = 0;
            if (t == null) return false;
            if (t.Type == JTokenType.Integer) { v = (double)(long)t; return true; }
            if (t.Type == JTokenType.Float) { v = (double)t; return !double.IsNaN(v) && !double.IsInfinity(v); }
            return false;
        }

        private static bool Int(JObject parent, string key, string path, List<ValidationIssue> issues, long min, long max, out int value)
        {
            value = 0;
            string p = Join(path, key);
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(p, "invalid_type", "required")); return false; }
            if (!NumberToken(t, out double d)) { issues.Add(new ValidationIssue(p, "invalid_type", "expected number")); return false; }
            if (Math.Floor(d) != d) { issues.Add(new ValidationIssue(p, "invalid_type", "expected integer")); return false; }
            if (d < min) { issues.Add(new ValidationIssue(p, "too_small", $">= {min}")); return false; }
            if (d > max) { issues.Add(new ValidationIssue(p, "too_big", $"<= {max}")); return false; }
            value = (int)d;
            return true;
        }

        private static bool Num(JObject parent, string key, string path, List<ValidationIssue> issues, double min, double max)
        {
            string p = Join(path, key);
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(p, "invalid_type", "required")); return false; }
            if (!NumberToken(t, out double d)) { issues.Add(new ValidationIssue(p, "invalid_type", "expected number")); return false; }
            if (d < min) { issues.Add(new ValidationIssue(p, "too_small", $">= {min}")); return false; }
            if (d > max) { issues.Add(new ValidationIssue(p, "too_big", $"<= {max}")); return false; }
            return true;
        }

        /// <summary>Returns true when the value is JSON null (and valid otherwise).</summary>
        private static bool NullableNum(JObject parent, string key, string path, List<ValidationIssue> issues, double min, double max)
        {
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required (nullable)")); return false; }
            if (t.Type == JTokenType.Null) return true;
            Num(parent, key, path, issues, min, max);
            return false;
        }

        private static void SteamId64(JObject parent, string key, string path, List<ValidationIssue> issues)
        {
            if (!Str(parent, key, path, issues, 1, null, SteamId, out string v)) return;
            if (BigInteger.Parse(v, CultureInfo.InvariantCulture) < SteamMin)
                issues.Add(new ValidationIssue(Join(path, key), "custom", "SteamID64 must be >= " + Caps.SteamId64Min));
        }

        private static void Int64Str(JObject parent, string key, string path, List<ValidationIssue> issues)
        {
            if (!Str(parent, key, path, issues, 1, null, Int64, out string v)) return;
            var n = BigInteger.Parse(v, CultureInfo.InvariantCulture);
            if (n < I64Min || n > I64Max)
                issues.Add(new ValidationIssue(Join(path, key), "custom", "outside the signed 64-bit range"));
        }

        private static void PrefabList(JObject parent, string key, string path, List<ValidationIssue> issues, int max)
        {
            if (!Arr(parent, key, path, issues, 0, max, out JArray a)) return;
            for (int i = 0; i < a.Count; i++)
            {
                string p = Join(path, key) + "." + i;
                if (a[i].Type != JTokenType.String) { issues.Add(new ValidationIssue(p, "invalid_type", "expected string")); continue; }
                if (!Prefab.IsMatch((string)a[i])) issues.Add(new ValidationIssue(p, "invalid_format", "prefab id"));
            }
        }
    }
}
