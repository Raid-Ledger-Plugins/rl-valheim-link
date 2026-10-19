using System;
using System.Collections.Generic;
using System.Text;
using RlValheimLink.Contract;
using UnityEngine;

namespace RlValheimLink.Client
{
    /// <summary>Builds a `map` message from the local Minimap (docs/design.md §2). Main thread only.</summary>
    internal static class MapCapture
    {
        /// <summary>Pin types the game never saves; they are not part of a player's map.</summary>
        private static readonly HashSet<string> TransientPinTypes = new HashSet<string> { "None", "Player", "Ping", "Shout", "RandomEvent", "EventArea" };

        public sealed class Result
        {
            public MapMessage Message;
            /// <summary>SHA-256 over the packed bitsets and the pin list: unchanged hash = nothing to send.</summary>
            public string ContentHash;
        }

        public static Result Capture(Player p)
        {
            var mm = Minimap.instance;
            if (mm == null) return null;
            var explored = GameAccess.Explored(mm);
            if (explored == null) return null;
            int size = mm.m_textureSize;
            if (size < Caps.BitmapEdgeMin || size > Caps.BitmapEdgeMax) { Log.Warn($"minimap textureSize {size} outside the contract range; map not sent"); return null; }
            if (explored.Length != size * size) { Log.Warn($"minimap explored length {explored.Length} != {size}²; map not sent"); return null; }

            var m = new MapMessage
            {
                ServerId = ClientHalf.PendingServerId,
                CapturedAt = MessageJson.Timestamp(DateTime.UtcNow),
                GameVersion = GameInfo.GameVersion(),
                ModVersion = Plugin.Version,
                Character = new MapCharacterRef { SteamId = GameInfo.LocalSteamId(), PlayerId = p.GetPlayerID().ToString(), WorldUid = GameInfo.WorldUid() },
                Grid = new MapGrid { TextureSize = size, PixelSizeMetres = GameInfo.Clamp(mm.m_pixelSize, 0.001, 256) },
                BaseMap = null, // the server half fills this in from its own render
            };

            byte[] packedOwn = Bitset.Pack(explored, size, size);
            m.Explored = new ExploredBitmap { Width = size, Height = size, Data = Bitset.GzipBase64(packedOwn) };

            byte[] packedShared = new byte[0];
            var others = GameAccess.ExploredOthers(mm);
            if (others != null && others.Length == size * size && Bitset.Any(others))
            {
                packedShared = Bitset.Pack(others, size, size);
                m.SharedExplored = new ExploredBitmap { Width = size, Height = size, Data = Bitset.GzipBase64(packedShared) };
            }

            var pinText = new StringBuilder();
            Pins(mm, p, m.Pins, pinText);
            return new Result { Message = m, ContentHash = GameInfo.Sha256Hex(packedOwn, packedShared, Encoding.UTF8.GetBytes(pinText.ToString())) };
        }

        private static void Pins(Minimap mm, Player p, List<MapPin> list, StringBuilder hashText)
        {
            var pins = GameAccess.Pins(mm);
            if (pins == null) return;
            var all = new List<MapPin>();
            var dist = new List<float>();
            Vector3 here = p.transform.position;
            foreach (var pin in pins)
            {
                if (pin == null || !pin.m_save) continue;
                string type = GameInfo.Identifier(pin.m_type.ToString(), 32);
                if (type == null || TransientPinTypes.Contains(type)) continue;
                var mp = new MapPin
                {
                    Type = type,
                    Label = GameInfo.Clip(pin.m_name ?? "", 64),
                    X = GameInfo.Clamp(pin.m_pos.x, -Caps.WorldCoordMax, Caps.WorldCoordMax),
                    Z = GameInfo.Clamp(pin.m_pos.z, -Caps.WorldCoordMax, Caps.WorldCoordMax),
                    Checked = pin.m_checked,
                    Shared = pin.m_ownerID != 0L, // UNVERIFIED: vanilla stamps cartography-table pins with their author's id
                };
                all.Add(mp);
                dist.Add((pin.m_pos - here).sqrMagnitude);
            }
            if (all.Count > Caps.PinsMax)
            {
                // Keep the nearest; the contract's cap binds.
                var idx = new List<int>(); for (int i = 0; i < all.Count; i++) idx.Add(i);
                idx.Sort((a, b) => dist[a].CompareTo(dist[b]));
                var kept = new List<MapPin>(Caps.PinsMax);
                for (int i = 0; i < Caps.PinsMax; i++) kept.Add(all[idx[i]]);
                Log.Warn($"pins: {all.Count - Caps.PinsMax} farthest pin(s) dropped over the cap of {Caps.PinsMax}");
                all = kept;
            }
            all.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Type, b.Type); if (c != 0) return c;
                c = a.X.CompareTo(b.X); if (c != 0) return c;
                return a.Z.CompareTo(b.Z);
            });
            foreach (var mp in all)
            {
                list.Add(mp);
                // Labels are player free text: they feed the change hash but never a log line.
                hashText.Append(mp.Type).Append('|').Append(mp.X).Append('|').Append(mp.Z).Append('|').Append(mp.Checked).Append('|').Append(mp.Shared).Append('|').Append(mp.Label).Append('\n');
            }
        }
    }
}
