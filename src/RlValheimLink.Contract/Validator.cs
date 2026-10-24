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
        private static readonly Regex ServerId = new Regex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex GameVersion = new Regex(@"^\d{1,4}(\.\d{1,5}){1,3}\z", RegexOptions.CultureInvariant);
        private static readonly Regex SemVer = new Regex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant);
        private static readonly Regex IsoDateTime = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?Z\z", RegexOptions.CultureInvariant);
        private static readonly Regex SteamId = new Regex(@"^[1-9]\d{16}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Int64 = new Regex(@"^(0|-?[1-9]\d{0,18})\z", RegexOptions.CultureInvariant);
        private static readonly Regex Prefab = new Regex(@"^[A-Za-z0-9_.-]{1,96}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256 = new Regex(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex SkillId = new Regex(@"^-?[A-Za-z0-9_]{1,64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex PinType = new Regex(@"^[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Category = new Regex(@"^[A-Za-z0-9_]{1,48}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Locale = new Regex(@"^[A-Za-z_-]{2,32}\z", RegexOptions.CultureInvariant);
        private static readonly Regex IconFile = new Regex(@"^[A-Za-z0-9_.-]{1,100}\.png\z", RegexOptions.CultureInvariant);
        private static readonly Regex BitmapData = new Regex(@"^H4sI[A-Za-z0-9+/]*={0,2}\z", RegexOptions.CultureInvariant);

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
            if (kind != "character" && kind != "map" && kind != "items" && kind != "containers" && kind != "recipes" && kind != "portals")
            {
                issues.Add(new ValidationIssue("kind", "invalid_union", "kind must be character, map, items, containers, recipes or portals"));
                return issues;
            }

            switch (kind)
            {
                case "character": Character(obj, issues); break;
                case "map": Map(obj, issues); break;
                case "items": Items(obj, issues); break;
                case "containers": Containers(obj, issues); break;
                case "recipes": Recipes(obj, issues); break;
                case "portals": Portals(obj, issues); break;
            }
            return issues;
        }

        // ---------- kinds ----------

        private static readonly string[] HeaderKeys = { "schemaVersion", "serverId", "capturedAt", "gameVersion", "modVersion", "kind" };

        private static void Header(JObject o, List<ValidationIssue> issues)
        {
            var sv = o["schemaVersion"];
            if (sv == null || sv.Type != JTokenType.Integer && sv.Type != JTokenType.Float || (double)sv != Caps.SchemaVersion)
                issues.Add(new ValidationIssue("schemaVersion", "invalid_value", "schemaVersion must be 1"));

            Str(o, "serverId", "", issues, 0, null, ServerId);
            if (Str(o, "capturedAt", "", issues, 1, null, null, out string ts))
            {
                if (!IsoDateTime.IsMatch(ts)) issues.Add(new ValidationIssue("capturedAt", "invalid_format", "ISO-8601 UTC with Z"));
                if (ts.Length > Caps.TimestampMaxChars) issues.Add(new ValidationIssue("capturedAt", "too_big", "at most 3 fractional digits"));
            }
            Str(o, "gameVersion", "", issues, 0, null, GameVersion);
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
                    if (Str(s, "id", p, issues, 0, null, SkillId, out string id))
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
            Str(item, "prefab", path, issues, 0, null, Prefab);
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
                    Str(pin, "type", p, issues, 0, null, PinType);
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
            Literal(b, "encoding", path, issues, ExploredBitmap.EncodingName);
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
            Str(b, "sha256", path, issues, 0, null, Sha256);
            Int(b, "byteLength", path, issues, 1, Caps.BinaryMaxBytes, out _);
            Literal(b, "contentType", path, issues, "image/png");
            bool wOk = Int(b, "width", path, issues, 1, Caps.BinaryEdgeMax, out w);
            bool hOk = Int(b, "height", path, issues, 1, Caps.BinaryEdgeMax, out h);
            return wOk && hOk;
        }

        private static void Items(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "locale", "atlas", "items");
            Header(o, issues);
            Str(o, "locale", "", issues, 0, null, Locale);

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
                    if (Str(it, "prefab", p, issues, 0, null, Prefab, out string prefab))
                        if (!seen.Add(prefab)) issues.Add(new ValidationIssue(p + ".prefab", "custom", "duplicate prefab"));
                    Str(it, "name", p, issues, 1, 96, null);
                    Str(it, "category", p, issues, 0, null, Category);
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
                        Keys(ic, ip, issues, "kind", "file", "blob");
                        Str(ic, "file", ip, issues, 0, null, IconFile);
                        var blob = ic["blob"];
                        if (blob != null && ObjAt(blob, ip + ".blob", issues, out JObject bl)) BinaryRef(bl, ip + ".blob", issues, out _, out _);
                    }
                    else issues.Add(new ValidationIssue(ip + ".kind", "invalid_union", "icon kind must be atlas or file"));
                }
            }
            if (atlas != null && atlas.Type == JTokenType.Null && anyAtlasIcon)
                issues.Add(new ValidationIssue("atlas", "custom", "atlas icon without an atlas"));
        }

        private static readonly string[] ContainerPrivacy = { "public", "private", "group" };

        private static void Containers(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "worldUid", "containers", "omittedShared", "omittedPrivate");
            Header(o, issues);
            Int64Str(o, "worldUid", "", issues);

            if (Arr(o, "containers", "", issues, 0, Caps.ContainersMax, out JArray containers))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int entries = 0;
                for (int i = 0; i < containers.Count; i++)
                {
                    string p = "containers." + i;
                    if (!ObjAt(containers[i], p, issues, out JObject c)) continue;
                    Keys(c, p, issues, "prefab", "x", "y", "z", "creatorPlayerId", "privacy", "vehicle", "slots", "usedSlots", "unreadable", "omittedEntries", "items");
                    bool prefabOk = Str(c, "prefab", p, issues, 0, null, Prefab, out string prefab);
                    bool xOk = Num(c, "x", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double x);
                    bool yOk = Num(c, "y", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double y);
                    bool zOk = Num(c, "z", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double z);
                    bool creatorOk = Int64Str(c, "creatorPlayerId", p, issues, out string creator);
                    if (creatorOk && creator == "0") { issues.Add(new ValidationIssue(p + ".creatorPlayerId", "custom", "creatorPlayerId must not be 0")); creatorOk = false; }
                    Enum(c, "privacy", p, issues, ContainerPrivacy, out _);
                    Bool(c, "vehicle", p, issues);
                    bool slotsOk = Int(c, "slots", p, issues, 1, Caps.ContainerSlotsMax, out int slots);
                    bool usedOk = Int(c, "usedSlots", p, issues, 0, Caps.ContainerSlotsMax, out int used);
                    bool unreadableOk = Bool(c, "unreadable", p, issues, out bool unreadable);
                    Int(c, "omittedEntries", p, issues, 0, 100_000_000, out _);

                    if (Arr(c, "items", p, issues, 0, Caps.ContainerItemsMax, out JArray stacks))
                    {
                        entries += stacks.Count;
                        var seenStacks = new HashSet<string>(StringComparer.Ordinal);
                        for (int j = 0; j < stacks.Count; j++)
                        {
                            string sp = p + ".items." + j;
                            if (!ObjAt(stacks[j], sp, issues, out JObject s)) continue;
                            Keys(s, sp, issues, "prefab", "quality", "stack");
                            bool sPrefabOk = Str(s, "prefab", sp, issues, 0, null, Prefab, out string stackPrefab);
                            bool qualityOk = Int(s, "quality", sp, issues, 1, 10, out int quality);
                            Int(s, "stack", sp, issues, 1, 1_000_000_000, out _);
                            if (sPrefabOk && qualityOk && !seenStacks.Add(stackPrefab + "@" + quality.ToString(CultureInfo.InvariantCulture)))
                                issues.Add(new ValidationIssue(sp, "custom", "duplicate (prefab, quality) stack"));
                        }
                        if (unreadableOk && unreadable && (stacks.Count > 0 || usedOk && used != 0))
                            issues.Add(new ValidationIssue(p + ".items", "custom", "an unreadable container carries no items"));
                    }
                    if (slotsOk && usedOk && used > slots)
                        issues.Add(new ValidationIssue(p + ".usedSlots", "custom", "usedSlots exceeds slots"));
                    if (prefabOk && xOk && yOk && zOk && creatorOk && !seen.Add(prefab + "|" + Coord(x) + "|" + Coord(y) + "|" + Coord(z) + "|" + creator))
                        issues.Add(new ValidationIssue(p, "custom", "duplicate container (prefab, position, creator)"));
                }
                if (entries > Caps.ContainerEntriesMax)
                    issues.Add(new ValidationIssue("containers", "too_big", $"items summed over all containers exceed {Caps.ContainerEntriesMax}"));
            }

            Int(o, "omittedShared", "", issues, 0, 1_000_000, out _);
            Int(o, "omittedPrivate", "", issues, 0, 1_000_000, out _);
        }

        /// <summary>CONTRACT.md §5c (Zod: valheim-link-portals.schema.ts): a full snapshot of the world's portals.</summary>
        private static void Portals(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "worldUid", "portals", "omitted");
            Header(o, issues);
            Int64Str(o, "worldUid", "", issues);
            if (Arr(o, "portals", "", issues, 0, Caps.PortalsMax, out JArray portals))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < portals.Count; i++)
                {
                    string p = "portals." + i;
                    if (!ObjAt(portals[i], p, issues, out JObject pt)) continue;
                    Keys(pt, p, issues, "prefab", "x", "y", "z", "tag");
                    bool prefabOk = Str(pt, "prefab", p, issues, 0, null, Prefab, out string prefab);
                    bool xOk = Num(pt, "x", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double x);
                    bool zOk = Num(pt, "z", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double z);
                    string yKey = null;
                    var yt = pt["y"];
                    if (yt == null) issues.Add(new ValidationIssue(p + ".y", "invalid_type", "required (nullable)"));
                    else if (yt.Type == JTokenType.Null) yKey = "null";
                    else if (Num(pt, "y", p, issues, -Caps.WorldCoordMax, Caps.WorldCoordMax, out double y)) yKey = Coord(y);
                    Str(pt, "tag", p, issues, 0, Caps.PortalTagMaxChars, null);
                    if (prefabOk && xOk && zOk && yKey != null && !seen.Add(prefab + "|" + Coord(x) + "|" + yKey + "|" + Coord(z)))
                        issues.Add(new ValidationIssue(p, "custom", "duplicate portal (prefab, position)"));
                }
            }
            Int(o, "omitted", "", issues, 0, 1_000_000, out _);
        }

        private static readonly string[] ProcessorTypes = { "smelter", "fermenter", "cooking", "incinerator" };
        private static readonly string[] Formulas = { "vanilla", "custom" };

        /// <summary>CONTRACT.md §5b (Zod: valheim-link-recipes.schema.ts, valheim-link-recipes-processing.schema.ts).</summary>
        private static void Recipes(JObject o, List<ValidationIssue> issues)
        {
            Keys(o, "", issues, HeaderKeys, "locale", "atlas", "stations", "recipes", "pieces", "processors", "processes");
            Header(o, issues);
            Str(o, "locale", "", issues, 0, null, Locale);

            bool hasAtlas = false; int aw = 0, ah = 0;
            var atlas = o["atlas"];
            if (atlas == null) issues.Add(new ValidationIssue("atlas", "invalid_type", "required (nullable)"));
            else if (atlas.Type != JTokenType.Null)
            {
                if (ObjAt(atlas, "atlas", issues, out JObject a)) hasAtlas = BinaryRef(a, "atlas", issues, out aw, out ah);
            }
            var icons = new AtlasCheck { HasAtlas = hasAtlas, Width = aw, Height = ah };
            int entries = 0;

            var stations = new HashSet<string>(StringComparer.Ordinal);
            if (Arr(o, "stations", "", issues, 0, Caps.StationsMax, out JArray stationList))
            {
                for (int i = 0; i < stationList.Count; i++)
                {
                    string p = "stations." + i;
                    if (!ObjAt(stationList[i], p, issues, out JObject s)) continue;
                    Keys(s, p, issues, "prefab", "name", "maxLevel", "stackableExtension", "upgrader", "icon");
                    if (Str(s, "prefab", p, issues, 0, null, Prefab, out string prefab) && !stations.Add(prefab))
                        issues.Add(new ValidationIssue(p + ".prefab", "custom", "duplicate stations prefab"));
                    Str(s, "name", p, issues, 1, 96, null);
                    Int(s, "maxLevel", p, issues, 1, Caps.StationLevelMax, out _);
                    Bool(s, "stackableExtension", p, issues);
                    OptionalBool(s, "upgrader", p, issues);
                    AtlasIcon(s, p, issues, icons);
                }
            }

            if (Arr(o, "recipes", "", issues, 0, Caps.RecipesMax, out JArray recipes))
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < recipes.Count; i++)
                {
                    string p = "recipes." + i;
                    if (!ObjAt(recipes[i], p, issues, out JObject r)) continue;
                    Keys(r, p, issues, "id", "result", "amount", "enabled", "station", "repairStation", "minStationLevel", "upgradeOnly", "anyOneOf", "qualityResultAmountMultiplier", "formula", "resources");
                    if (Str(r, "id", p, issues, 0, null, Prefab, out string id) && !ids.Add(id))
                        issues.Add(new ValidationIssue(p + ".id", "custom", "duplicate recipe id"));
                    Str(r, "result", p, issues, 0, null, Prefab);
                    Int(r, "amount", p, issues, 1, 10_000, out _);
                    Bool(r, "enabled", p, issues);
                    StationRef(r, "station", p, issues, stations);
                    StationRef(r, "repairStation", p, issues, stations);
                    Int(r, "minStationLevel", p, issues, 0, Caps.StationLevelMax, out _);
                    Bool(r, "upgradeOnly", p, issues);
                    Bool(r, "anyOneOf", p, issues);
                    Num(r, "qualityResultAmountMultiplier", p, issues, 0, 100);
                    Enum(r, "formula", p, issues, Formulas, out _);
                    entries += Resources(r, p, issues, true);
                }
            }

            if (Arr(o, "pieces", "", issues, 0, Caps.PiecesMax, out JArray pieces))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < pieces.Count; i++)
                {
                    string p = "pieces." + i;
                    if (!ObjAt(pieces[i], p, issues, out JObject pc)) continue;
                    Keys(pc, p, issues, "prefab", "name", "category", "tool", "station", "icon", "resources");
                    if (Str(pc, "prefab", p, issues, 0, null, Prefab, out string prefab) && !seen.Add(prefab))
                        issues.Add(new ValidationIssue(p + ".prefab", "custom", "duplicate pieces prefab"));
                    Str(pc, "name", p, issues, 1, 96, null);
                    Str(pc, "category", p, issues, 0, null, Category);
                    Str(pc, "tool", p, issues, 0, null, Prefab);
                    StationRef(pc, "station", p, issues, stations);
                    AtlasIcon(pc, p, issues, icons);
                    entries += Resources(pc, p, issues, false);
                }
            }

            var processors = new HashSet<string>(StringComparer.Ordinal);
            if (Arr(o, "processors", "", issues, 0, Caps.ProcessorsMax, out JArray processorList))
            {
                for (int i = 0; i < processorList.Count; i++)
                {
                    string p = "processors." + i;
                    if (!ObjAt(processorList[i], p, issues, out JObject pr)) continue;
                    Keys(pr, p, issues, "prefab", "type", "fuel", "fuelPerProduct", "fuelSecondsPerUnit", "windPowered", "requiresRoof", "maxInput", "burntResult", "defaultResult");
                    if (Str(pr, "prefab", p, issues, 0, null, Prefab, out string prefab) && !processors.Add(prefab))
                        issues.Add(new ValidationIssue(p + ".prefab", "custom", "duplicate processors prefab"));
                    Enum(pr, "type", p, issues, ProcessorTypes, out _);
                    NullablePrefab(pr, "fuel", p, issues, out _);
                    NullableInt(pr, "fuelPerProduct", p, issues, 0, 1_000);
                    NullableNum(pr, "fuelSecondsPerUnit", p, issues, 0.001, 86_400);
                    Bool(pr, "windPowered", p, issues);
                    Bool(pr, "requiresRoof", p, issues);
                    NullableInt(pr, "maxInput", p, issues, 1, 10_000);
                    NullablePrefab(pr, "burntResult", p, issues, out _);
                    var dr = pr["defaultResult"];
                    if (dr == null) issues.Add(new ValidationIssue(p + ".defaultResult", "invalid_type", "required (nullable)"));
                    else if (dr.Type != JTokenType.Null && ObjAt(dr, p + ".defaultResult", issues, out JObject d))
                    {
                        Keys(d, p + ".defaultResult", issues, "prefab", "perInputs");
                        Str(d, "prefab", p + ".defaultResult", issues, 0, null, Prefab);
                        Int(d, "perInputs", p + ".defaultResult", issues, 1, 10_000, out _);
                    }
                }
            }

            if (Arr(o, "processes", "", issues, 0, Caps.ProcessesMax, out JArray processes))
            {
                for (int i = 0; i < processes.Count; i++)
                {
                    string p = "processes." + i;
                    if (!ObjAt(processes[i], p, issues, out JObject pr)) continue;
                    Keys(pr, p, issues, "processor", "inputs", "anyOneOf", "output", "seconds", "priority");
                    if (Str(pr, "processor", p, issues, 0, null, Prefab, out string processor) && !processors.Contains(processor))
                        issues.Add(new ValidationIssue(p + ".processor", "custom", "unknown processor"));
                    if (Arr(pr, "inputs", p, issues, 1, Caps.ProcessInputsMax, out JArray inputs))
                    {
                        entries += inputs.Count;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        for (int j = 0; j < inputs.Count; j++)
                        {
                            string ip = p + ".inputs." + j;
                            if (!ObjAt(inputs[j], ip, issues, out JObject inp)) continue;
                            Keys(inp, ip, issues, "prefab", "amount");
                            if (Str(inp, "prefab", ip, issues, 0, null, Prefab, out string prefab) && !seen.Add(prefab))
                                issues.Add(new ValidationIssue(ip + ".prefab", "custom", "duplicate input prefab"));
                            Int(inp, "amount", ip, issues, 1, 10_000, out _);
                        }
                    }
                    Bool(pr, "anyOneOf", p, issues);
                    if (Obj(pr, "output", p, issues, out JObject output))
                    {
                        Keys(output, p + ".output", issues, "prefab", "amount");
                        Str(output, "prefab", p + ".output", issues, 0, null, Prefab);
                        Int(output, "amount", p + ".output", issues, 1, 10_000, out _);
                    }
                    NullableNum(pr, "seconds", p, issues, 0, 604_800);
                    Int(pr, "priority", p, issues, -10_000, 10_000, out _);
                }
            }

            if (icons.Any && atlas != null && atlas.Type == JTokenType.Null)
                issues.Add(new ValidationIssue("atlas", "custom", "atlas icon without an atlas"));
            if (entries > Caps.RecipeEntriesMax)
                issues.Add(new ValidationIssue("", "too_big", $"recipe resources, piece resources and process inputs exceed {Caps.RecipeEntriesMax} in total"));
        }

        private sealed class AtlasCheck { public bool HasAtlas, Any; public int Width, Height; }

        /// <summary>A recipes-kind icon: atlas-only (no file icons), nullable, inside the atlas when one is sent.</summary>
        private static void AtlasIcon(JObject parent, string path, List<ValidationIssue> issues, AtlasCheck atlas)
        {
            string ip = Join(path, "icon");
            var icon = parent["icon"];
            if (icon == null) { issues.Add(new ValidationIssue(ip, "invalid_type", "required (nullable)")); return; }
            if (icon.Type == JTokenType.Null) return;
            if (!ObjAt(icon, ip, issues, out JObject ic)) return;
            atlas.Any = true;
            Keys(ic, ip, issues, "kind", "x", "y", "w", "h");
            Literal(ic, "kind", ip, issues, "atlas");
            bool xo = Int(ic, "x", ip, issues, 0, 16_383, out int x);
            bool yo = Int(ic, "y", ip, issues, 0, 16_383, out int y);
            bool wo = Int(ic, "w", ip, issues, 1, 512, out int w);
            bool ho = Int(ic, "h", ip, issues, 1, 512, out int h);
            if (atlas.HasAtlas && xo && yo && wo && ho && (x + w > atlas.Width || y + h > atlas.Height))
                issues.Add(new ValidationIssue(ip, "custom", "icon outside atlas"));
        }

        /// <summary>A recipe's or piece's resources[]: prefab unique; returns the entry count for the message total.</summary>
        private static int Resources(JObject parent, string path, List<ValidationIssue> issues, bool recipe)
        {
            if (!Arr(parent, "resources", path, issues, 0, Caps.RequirementsMax, out JArray list)) return 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0; j < list.Count; j++)
            {
                string rp = path + ".resources." + j;
                if (!ObjAt(list[j], rp, issues, out JObject res)) continue;
                if (recipe) Keys(res, rp, issues, "prefab", "amount", "amountPerLevel", "extraAmountOnlyOneIngredient", "recover", "upgraderResource");
                else Keys(res, rp, issues, "prefab", "amount", "recover");
                if (Str(res, "prefab", rp, issues, 0, null, Prefab, out string prefab) && !seen.Add(prefab))
                    issues.Add(new ValidationIssue(rp + ".prefab", "custom", "duplicate resources prefab"));
                Int(res, "amount", rp, issues, 0, 10_000, out _);
                if (recipe)
                {
                    Int(res, "amountPerLevel", rp, issues, 0, 10_000, out _);
                    Int(res, "extraAmountOnlyOneIngredient", rp, issues, 0, 10_000, out _);
                }
                Bool(res, "recover", rp, issues);
                if (recipe) OptionalBool(res, "upgraderResource", rp, issues);
            }
            return list.Count;
        }

        private static void StationRef(JObject parent, string key, string path, List<ValidationIssue> issues, HashSet<string> stations)
        {
            if (NullablePrefab(parent, key, path, issues, out string station) && station != null && !stations.Contains(station))
                issues.Add(new ValidationIssue(Join(path, key), "custom", "unknown station"));
        }

        /// <summary>A coordinate as part of the container identity; -0 and 0 are one position, as in Zod's template-string key.</summary>
        private static string Coord(double v) => (v == 0 ? 0d : v).ToString("R", CultureInfo.InvariantCulture);

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

        /// <summary>Zod 4 enum / literal: anything that is not one of the values (missing, another type, another string) is invalid_value.</summary>
        private static bool Enum(JObject parent, string key, string path, List<ValidationIssue> issues, string[] values, out string value)
        {
            var t = parent[key];
            value = t != null && t.Type == JTokenType.String ? (string)t : null;
            if (value != null && Array.IndexOf(values, value) >= 0) return true;
            issues.Add(new ValidationIssue(Join(path, key), "invalid_value", "one of " + string.Join("|", values)));
            return false;
        }

        private static bool Literal(JObject parent, string key, string path, List<ValidationIssue> issues, string expected) =>
            Enum(parent, key, path, issues, new[] { expected }, out _);

        private static bool Bool(JObject parent, string key, string path, List<ValidationIssue> issues) =>
            Bool(parent, key, path, issues, out _);

        private static bool Bool(JObject parent, string key, string path, List<ValidationIssue> issues, out bool value)
        {
            value = false;
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required")); return false; }
            if (t.Type != JTokenType.Boolean) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "expected boolean")); return false; }
            value = (bool)t;
            return true;
        }

        /// <summary>Absent is fine; present must be a boolean (Zod `.optional()`, not nullable).</summary>
        private static void OptionalBool(JObject parent, string key, string path, List<ValidationIssue> issues)
        {
            if (parent[key] != null) Bool(parent, key, path, issues);
        }

        /// <summary>A prefab id or JSON null; true when valid (value = null for JSON null).</summary>
        private static bool NullablePrefab(JObject parent, string key, string path, List<ValidationIssue> issues, out string value)
        {
            value = null;
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required (nullable)")); return false; }
            if (t.Type == JTokenType.Null) return true;
            return Str(parent, key, path, issues, 0, null, Prefab, out value);
        }

        private static void NullableInt(JObject parent, string key, string path, List<ValidationIssue> issues, long min, long max)
        {
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(Join(path, key), "invalid_type", "required (nullable)")); return; }
            if (t.Type != JTokenType.Null) Int(parent, key, path, issues, min, max, out _);
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

        private static bool Num(JObject parent, string key, string path, List<ValidationIssue> issues, double min, double max) =>
            Num(parent, key, path, issues, min, max, out _);

        private static bool Num(JObject parent, string key, string path, List<ValidationIssue> issues, double min, double max, out double value)
        {
            value = 0;
            string p = Join(path, key);
            var t = parent[key];
            if (t == null) { issues.Add(new ValidationIssue(p, "invalid_type", "required")); return false; }
            if (!NumberToken(t, out double d)) { issues.Add(new ValidationIssue(p, "invalid_type", "expected number")); return false; }
            if (d < min) { issues.Add(new ValidationIssue(p, "too_small", $">= {min}")); return false; }
            if (d > max) { issues.Add(new ValidationIssue(p, "too_big", $"<= {max}")); return false; }
            value = d;
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

        private static void Int64Str(JObject parent, string key, string path, List<ValidationIssue> issues) =>
            Int64Str(parent, key, path, issues, out _);

        private static bool Int64Str(JObject parent, string key, string path, List<ValidationIssue> issues, out string value)
        {
            if (!Str(parent, key, path, issues, 1, null, Int64, out value)) return false;
            var n = BigInteger.Parse(value, CultureInfo.InvariantCulture);
            if (n >= I64Min && n <= I64Max) return true;
            issues.Add(new ValidationIssue(Join(path, key), "custom", "outside the signed 64-bit range"));
            return false;
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
