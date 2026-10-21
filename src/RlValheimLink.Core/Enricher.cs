using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RlValheimLink.Contract;

namespace RlValheimLink.Core
{
    /// <summary>What the server knows about the sender and itself when a message arrives (docs/design.md §4).</summary>
    public sealed class EnrichContext
    {
        public string ServerId;
        /// <summary>SteamID64 digits proven by the peer's socket, or null for a non-Steam peer.</summary>
        public string SocketSteamId;
        /// <summary>Accept the client's own steamId claim when the socket cannot vouch for one.</summary>
        public bool TrustClientSteamId;
        /// <summary>The sender's player id as the server's ZDO reports it; null when unknown (peer already gone).</summary>
        public long? ZdoPlayerId;
        public string ServerModVersion;
        public DateTime NowUtc = DateTime.UtcNow;
        public int ClockToleranceSeconds = 60;
        /// <summary>The rendered base map for this world + game version, attached to map messages whose grid matches.</summary>
        public BinaryRef BaseMap;
    }

    public sealed class EnrichResult
    {
        public bool Ok;
        public string Reason;
        public string Kind;
        /// <summary>Snapshot key (design §5): always starts with the kind.</summary>
        public string Key;
        public string CapturedAt;
        public string SteamId;
        public string PlayerId;
        public List<string> BlobRefs = new List<string>();
        public List<string> Notes = new List<string>();
        /// <summary>The exact bytes to POST.</summary>
        public byte[] Bytes;
    }

    /// <summary>
    /// Server-side enrichment of a client message (design §4): the server sets serverId, replaces the
    /// identity with what it can prove, cross-checks playerId, clamps a skewed capturedAt, attaches
    /// its base-map ref, and only then validates and serialises.
    /// </summary>
    public static class Enricher
    {
        /// <summary>A syntactically valid SteamID64 that is nobody's: the client's claim when Steam was unavailable.</summary>
        public const string PlaceholderSteamId = "76561197960265728";

        public static EnrichResult Enrich(JObject msg, EnrichContext ctx)
        {
            var r = new EnrichResult();
            if (msg == null) { r.Reason = "not a JSON object"; return r; }
            string kind = (msg["kind"] as JValue)?.Type == JTokenType.String ? (string)msg["kind"] : null;
            r.Kind = kind;
            if (kind != "character" && kind != "map" && kind != "items") { r.Reason = "unknown kind"; return r; }

            msg["serverId"] = ctx.ServerId ?? "";

            string modVersion = (string)(msg["modVersion"] as JValue);
            if (!string.IsNullOrEmpty(ctx.ServerModVersion) && modVersion != ctx.ServerModVersion)
            {
                r.Reason = $"modVersion {modVersion} != server {ctx.ServerModVersion}";
                return r;
            }

            // capturedAt: the client's clock, replaced by ours when it is clearly off (design §4).
            string capturedAt = (string)(msg["capturedAt"] as JValue);
            if (!MessageJson.TryParseTimestamp(capturedAt ?? "", out DateTime at) ||
                Math.Abs((at - ctx.NowUtc).TotalSeconds) > ctx.ClockToleranceSeconds)
            {
                capturedAt = MessageJson.Timestamp(ctx.NowUtc);
                msg["capturedAt"] = capturedAt;
                r.Notes.Add("capturedAt replaced by the server clock (client skew)");
            }
            r.CapturedAt = capturedAt;

            if (kind == "character" || kind == "map")
            {
                var ch = msg["character"] as JObject;
                if (ch == null) { r.Reason = "character missing"; return r; }
                string claim = (string)(ch["steamId"] as JValue);
                string steamId;
                if (ctx.SocketSteamId != null) steamId = ctx.SocketSteamId;
                else if (ctx.TrustClientSteamId && !string.IsNullOrEmpty(claim) && claim != PlaceholderSteamId) { steamId = claim; r.Notes.Add("steamId taken from the client's claim (non-Steam peer, TrustClientSteamId)"); }
                else { r.Reason = "peer identity is not a SteamID64 and TrustClientSteamId is off"; return r; }
                if (claim != null && claim != steamId && claim != PlaceholderSteamId) r.Notes.Add("client's steamId claim differed from the socket identity and was replaced");
                ch["steamId"] = steamId;
                r.SteamId = steamId;

                string playerId = (string)(ch["playerId"] as JValue);
                if (ctx.ZdoPlayerId.HasValue && playerId != ctx.ZdoPlayerId.Value.ToString(CultureInfo.InvariantCulture))
                {
                    r.Reason = $"playerId claim {playerId} != the peer's character {ctx.ZdoPlayerId.Value}";
                    return r;
                }
                r.PlayerId = playerId;

                if (kind == "character") r.Key = $"character|{steamId}|{playerId}";
                else
                {
                    string worldUid = (string)(ch["worldUid"] as JValue);
                    r.Key = $"map|{steamId}|{playerId}|{worldUid}";
                    var grid = msg["grid"] as JObject;
                    int size = grid != null && grid["textureSize"] is JValue tv && tv.Type == JTokenType.Integer ? (int)tv : -1;
                    if (ctx.BaseMap != null && ctx.BaseMap.Width == size && ctx.BaseMap.Height == size)
                    {
                        msg["baseMap"] = JObject.FromObject(ctx.BaseMap);
                        r.Notes.Add("baseMap attached");
                    }
                    else msg["baseMap"] = JValue.CreateNull();
                }
            }
            else
            {
                r.Key = $"items|{(string)(msg["gameVersion"] as JValue)}";
            }

            var issues = Validator.Validate(msg);
            if (issues.Count > 0)
            {
                r.Reason = "contract: " + string.Join("; ", issues.ConvertAll(i => i.ToString()).ToArray());
                return r;
            }
            CollectRefs(msg, r.BlobRefs);
            r.Bytes = MessageJson.ToBytes(msg);
            r.Ok = true;
            return r;
        }

        /// <summary>
        /// A message the server built itself (`containers`, `recipes`): there is no sending peer to check, so
        /// the server sets serverId, validates and serialises; the snapshot key is (kind, world) for
        /// containers and (kind, game version) for recipes (CONTRACT.md §5a, §5b).
        /// </summary>
        public static EnrichResult Seal(LinkMessage message, string serverId)
        {
            var r = new EnrichResult { Kind = message?.Kind };
            if (message is ContainersMessage c) r.Key = ContainersBuilder.SpoolKey(c.WorldUid);
            else if (message is RecipesMessage) r.Key = RecipesBuilder.SpoolKey(message.GameVersion);
            else { r.Reason = "not a server-built kind"; return r; }
            message.ServerId = serverId ?? "";
            r.CapturedAt = message.CapturedAt;
            var msg = MessageJson.ToToken(message);
            var issues = Validator.Validate(msg);
            if (issues.Count > 0)
            {
                r.Reason = "contract: " + string.Join("; ", issues.ConvertAll(i => i.ToString()).ToArray());
                return r;
            }
            CollectRefs(msg, r.BlobRefs);
            r.Bytes = MessageJson.ToBytes(msg);
            r.Ok = true;
            return r;
        }

        /// <summary>Every sha256 a message references (base map, atlas, file-icon blobs).</summary>
        public static void CollectRefs(JObject msg, List<string> into)
        {
            foreach (var key in new[] { "baseMap", "atlas" })
                if (msg[key] is JObject b && b["sha256"] is JValue s && s.Type == JTokenType.String) into.Add((string)s);
            if (msg["items"] is JArray items)
                foreach (var it in items)
                    if (it is JObject io && io["icon"] is JObject icon && (string)(icon["kind"] as JValue) == "file" && icon["blob"] is JObject blob && blob["sha256"] is JValue bs && bs.Type == JTokenType.String)
                        into.Add((string)bs);
        }
    }
}
