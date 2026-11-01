using System;
using System.Collections.Generic;
using System.Diagnostics;
using RlValheimLink.Core;

namespace RlValheimLink.Server
{
    /// <summary>
    /// The portal scan (CONTRACT.md §5c, ROK-1945), game glue only: find every portal ZDO in the world and
    /// read its position and pairing tag. Portal prefabs are every network prefab with a
    /// <see cref="TeleportWorld"/> (on the root or a child: vanilla <c>portal_wood</c> / <c>portal_stone</c>
    /// and modded portals cloned from them, e.g. AdvancedPortals), plus <c>Portals.ExtraPrefabs</c>. The tag
    /// is the vanilla <c>ZDOVars.s_tag</c> string. XPortal appears to write its name there too (observed in a
    /// server log; UNVERIFIED, operator checklist §J). Prefab selection and the 0-prefab rule are
    /// <see cref="PortalDiscovery"/>'s; rounding, dedupe, tag clipping and the cap are <see cref="PortalsBuilder"/>'s. Time-sliced like the chest scan's discovery: one
    /// GetAllZDOsWithPrefabIterative call per tick. Tags and positions are never logged.
    /// </summary>
    internal sealed class PortalScanner
    {
        private const int MaxCallsPerPrefab = 100_000;

        public sealed class Result
        {
            public DateTime ScheduledAt;
            public List<ScannedPortal> Portals = new List<ScannedPortal>();
            public int Duplicates, Ticks;
            public double Ms, BusiestTickMs;
        }

        private readonly List<string> _prefabs;
        private IEnumerator<object> _run;
        private Result _result;
        private bool _finished;

        private PortalScanner(List<string> prefabs, List<string> unknownExtras) { _prefabs = prefabs; UnknownExtras = unknownExtras; }

        public IReadOnlyList<string> Prefabs => _prefabs;
        /// <summary><c>Portals.ExtraPrefabs</c> entries that are not network prefabs (typo, mod not installed).</summary>
        public IReadOnlyList<string> UnknownExtras { get; }
        public bool Running => _run != null;
        /// <summary>True after the tick on which a scan ended without finishing (the world unloaded mid-scan).</summary>
        public bool Stopped { get; private set; }

        /// <summary>Portal prefabs to scan; null when the game is not ready yet.</summary>
        public static PortalScanner Create(ICollection<string> extra)
        {
            if (ZNetScene.instance == null || ZDOMan.instance == null) return null;
            var net = new List<KeyValuePair<string, bool>>();
            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;
                net.Add(new KeyValuePair<string, bool>(go.name, extra.Contains(go.name) || go.GetComponentInChildren<TeleportWorld>(true) != null));
            }
            var names = PortalDiscovery.Select(net, extra, out var unknownExtras);
            return new PortalScanner(names, unknownExtras);
        }

        public void Begin(DateTime scheduledAt)
        {
            _result = new Result { ScheduledAt = scheduledAt };
            _finished = false; Stopped = false;
            _run = Run(_result).GetEnumerator();
        }

        public void Cancel() { _run = null; _result = null; }

        /// <summary>One tick of the running scan; returns the result once, when the scan has finished.</summary>
        public Result Tick()
        {
            if (_run == null) return null;
            bool more;
            try { more = _run.MoveNext(); }
            catch { _run = null; _result = null; throw; }
            if (more) return null;
            _run = null;
            var r = _result; _result = null;
            Stopped = !_finished;
            return _finished ? r : null;
        }

        private IEnumerable<object> Run(Result r)
        {
            var total = Stopwatch.StartNew();
            var seen = new HashSet<ZDOID>();
            var found = new List<ZDO>();
            foreach (var name in _prefabs)
            {
                int index = 0, calls = 0;
                bool done = false;
                while (!done)
                {
                    if (ZDOMan.instance == null) yield break;
                    var tick = Stopwatch.StartNew();
                    done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, found, ref index);
                    // ZDOs are pooled and the iterator can return one twice: read each at once, dedupe by id.
                    foreach (var z in found)
                    {
                        if (z == null) continue;
                        if (!seen.Add(z.m_uid)) { r.Duplicates++; continue; }
                        var pos = z.GetPosition();
                        r.Portals.Add(new ScannedPortal { Prefab = name, X = pos.x, Y = pos.y, Z = pos.z, Tag = z.GetString(ZDOVars.s_tag, "") });
                    }
                    found.Clear();
                    if (++calls >= MaxCallsPerPrefab && !done) { Log.Warn($"portal scan: discovery of {name} gave up after {calls} calls"); done = true; }
                    r.BusiestTickMs = Math.Max(r.BusiestTickMs, tick.Elapsed.TotalMilliseconds);
                    r.Ticks++;
                    yield return null;
                }
            }
            r.Ms = total.Elapsed.TotalMilliseconds;
            _finished = true;
        }
    }
}
