using System;
using System.IO;
using System.Threading;
using RlValheimLink.Contract;
using UnityEngine;

namespace RlValheimLink.Server
{
    /// <summary>
    /// Renders the world's base map once per (world uid, game version) from the game's own
    /// WorldGenerator (design §2): biome fill + height shading + water, time-sliced on the main
    /// thread, PNG-encoded on a worker thread, cached under the spool as <worldUid>-<gameVersion>.png.
    /// The seed never leaves the server; only the PNG does. Colours approximate the in-game minimap (Q7).
    /// </summary>
    internal sealed class BaseMapRenderer
    {
        private readonly string _cacheDir;
        private readonly int _size;
        private readonly float _pixel;
        private readonly int _rowsPerTick;
        private byte[] _rgba;
        private int _nextRow;
        private Thread _encoder;
        private byte[] _png;
        private Exception _encodeError;
        private string _cacheKey;
        private System.Diagnostics.Stopwatch _sw;

        public BinaryRef Ref { get; private set; }
        public byte[] Png { get; private set; }
        public bool Done => Ref != null;
        public bool Busy => _rgba != null || _encoder != null;

        public BaseMapRenderer(string cacheDir, int size, float pixelSizeMetres, int rowsPerTick)
        {
            _cacheDir = cacheDir; _size = size; _pixel = pixelSizeMetres; _rowsPerTick = Math.Max(1, rowsPerTick);
            Directory.CreateDirectory(cacheDir);
        }

        /// <summary>Load the cached PNG for this world/version or start rendering. Returns true when a cached map was found.</summary>
        public bool Begin(string worldUid, string gameVersion)
        {
            _cacheKey = $"{worldUid}-{gameVersion}-{_size}";
            string file = Path.Combine(_cacheDir, _cacheKey + ".png");
            if (File.Exists(file))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    if (Contract.Png.TryReadSize(bytes, out int w, out int h) && w == _size && h == _size)
                    {
                        Png = bytes; Ref = Contract.Png.MakeRef(bytes);
                        Log.Info($"base map: cached {_size}² PNG ({bytes.Length} bytes) for world {worldUid} @ {gameVersion}");
                        return true;
                    }
                }
                catch (Exception e) { Log.Warn("base map cache unreadable: " + e.Message); }
            }
            if (WorldGenerator.instance == null) { Log.Warn("base map: WorldGenerator not ready; will retry"); _cacheKey = null; return false; }
            _rgba = new byte[_size * _size * 4];
            _nextRow = 0;
            _sw = System.Diagnostics.Stopwatch.StartNew();
            Log.Info($"base map: rendering {_size}² at {_pixel} m/px, {_rowsPerTick} rows per tick");
            return false;
        }

        /// <summary>Advance the render; call every Update. Returns true the tick the PNG becomes available.</summary>
        public bool Tick()
        {
            if (Ref != null) return false;
            if (_cacheKey == null) return false;
            if (_rgba != null)
            {
                var gen = WorldGenerator.instance;
                if (gen == null) return false;
                int end = Math.Min(_size, _nextRow + _rowsPerTick);
                for (int py = _nextRow; py < end; py++) RenderRow(gen, py);
                _nextRow = end;
                if (_nextRow < _size) return false;
                var rgba = _rgba; _rgba = null;
                Log.Info($"base map: sampled in {_sw.ElapsedMilliseconds} ms; encoding PNG");
                _encoder = new Thread(() => { try { _png = Contract.Png.EncodeRgba(rgba, _size, _size); } catch (Exception e) { _encodeError = e; } }) { IsBackground = true, Name = "RLVL-basemap-png" };
                _encoder.Start();
                return false;
            }
            if (_encoder != null)
            {
                if (_encoder.IsAlive) return false;
                _encoder = null;
                if (_encodeError != null) { Log.Error("base map: PNG encode failed", _encodeError); _cacheKey = null; return false; }
                Png = _png; _png = null;
                Ref = Contract.Png.MakeRef(Png);
                try { File.WriteAllBytes(Path.Combine(_cacheDir, _cacheKey + ".png"), Png); } catch (Exception e) { Log.Warn("base map: cache write failed: " + e.Message); }
                Log.Info($"base map: ready, {Png.Length} bytes, sha256={Ref.Sha256.Substring(0, 12)}… in {_sw.ElapsedMilliseconds} ms");
                return true;
            }
            return false;
        }

        // Contract §4: px = x / pixel + size/2, py = z / pixel + size/2, image row = size - 1 - py.
        private void RenderRow(WorldGenerator gen, int py)
        {
            float wz = (py - _size / 2) * _pixel;
            int row = _size - 1 - py;
            for (int px = 0; px < _size; px++)
            {
                float wx = (px - _size / 2) * _pixel;
                Heightmap.Biome biome = gen.GetBiome(wx, wz, 0.02f, false);
                float height = gen.GetHeight(wx, wz);
                Color32 c = Shade(biome, height);
                int o = (row * _size + px) * 4;
                _rgba[o] = c.r; _rgba[o + 1] = c.g; _rgba[o + 2] = c.b; _rgba[o + 3] = 255;
            }
        }

        private const float WaterLevel = 30f;

        private static Color32 Shade(Heightmap.Biome biome, float height)
        {
            Color c;
            switch (biome)
            {
                case Heightmap.Biome.Meadows: c = new Color(0.575f, 0.75f, 0.35f); break;
                case Heightmap.Biome.BlackForest: c = new Color(0.4f, 0.55f, 0.25f); break;
                case Heightmap.Biome.Swamp: c = new Color(0.6f, 0.5f, 0.4f); break;
                case Heightmap.Biome.Mountain: c = new Color(1f, 1f, 1f); break;
                case Heightmap.Biome.Plains: c = new Color(0.9f, 0.8f, 0.5f); break;
                case Heightmap.Biome.Mistlands: c = new Color(0.5f, 0.5f, 0.55f); break;
                case Heightmap.Biome.AshLands: c = new Color(0.8f, 0.3f, 0.25f); break;
                case Heightmap.Biome.DeepNorth: c = new Color(0.85f, 0.9f, 1f); break;
                case Heightmap.Biome.Ocean: c = new Color(0.1f, 0.3f, 0.55f); break;
                default: c = new Color(0.35f, 0.35f, 0.35f); break;
            }
            if (height < WaterLevel)
            {
                float depth = Mathf.Clamp01((WaterLevel - height) / 60f);
                var water = new Color(0.12f, 0.32f, 0.6f);
                var shallow = new Color(0.25f, 0.5f, 0.7f);
                c = Color.Lerp(shallow, water, depth);
            }
            else
            {
                float t = Mathf.Clamp01((height - WaterLevel) / 160f);
                c = Color.Lerp(c * 0.85f, c * 1.15f, t);
            }
            return new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255), (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255), 255);
        }
    }
}
