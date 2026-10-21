using System;
using RlValheimLink.Client;
using RlValheimLink.Net;

namespace RlValheimLink
{
    /// <summary>`rlvl status|export items|export icons|export now` in the F5 console (client).</summary>
    internal static class ConsoleCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                // The ConsoleCommand constructor grows a parameter every few game versions (1.0.20 added
                // hideBehindDevCommands), so pick the ConsoleEvent overload by reflection and fill the rest
                // with defaults (false / null) instead of binding to one signature at compile time.
                Terminal.ConsoleEvent handler = Run;
                var ctor = FindCtor(out var parameters);
                if (ctor == null) { Log.Warn("console command not registered: no ConsoleCommand(string, string, ConsoleEvent, …) constructor found"); return; }
                var args = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var pt = parameters[i].ParameterType;
                    if (i == 0) args[i] = "rlvl";
                    else if (i == 1) args[i] = "RL Valheim Link: rlvl status | rlvl export now | rlvl export items | rlvl export icons";
                    else if (pt == typeof(Terminal.ConsoleEvent)) args[i] = handler;
                    else if (pt == typeof(bool)) args[i] = false;
                    else args[i] = null;
                }
                ctor.Invoke(args);
            }
            catch (Exception e) { Log.Warn("console command not registered: " + e.Message); }
        }

        private static System.Reflection.ConstructorInfo FindCtor(out System.Reflection.ParameterInfo[] parameters)
        {
            foreach (var c in typeof(Terminal.ConsoleCommand).GetConstructors())
            {
                var ps = c.GetParameters();
                if (ps.Length >= 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string) && ps[2].ParameterType == typeof(Terminal.ConsoleEvent))
                { parameters = ps; return c; }
            }
            parameters = null; return null;
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "status";
            string sub2 = args.Args.Length > 2 ? args.Args[2].ToLowerInvariant() : "";
            switch (sub)
            {
                case "status":
                    args.Context.AddString($"RLVL v{Plugin.Version}: server={Roles.IsServer} client={Roles.IsClient} optOut={PluginConfig.OptOut.Value} queued={LinkChannel.QueuedTransfers} itemsExport={(ItemsCapture.IsRunning ? "running" : "idle")} pieceIcons={(PieceIconsCapture.IsRunning ? "running" : "idle")}");
                    if (Roles.IsServer) args.Context.AddString("RLVL server: " + Server.ServerHalf.Status());
                    break;
                case "export" when sub2 == "now":
                    if (!Roles.IsClient) { args.Context.AddString("RLVL: not a client"); break; }
                    args.Context.AddString("RLVL: capturing character + map now");
                    ClientHalf.OnProfileSaved(false);
                    break;
                case "export" when sub2 == "items":
                    if (!Roles.IsClient) { args.Context.AddString("RLVL: not a client"); break; }
                    args.Context.AddString("RLVL: items export started (see the log)");
                    ItemsCapture.Start("console");
                    break;
                case "export" when sub2 == "icons":
                    if (!Roles.IsClient) { args.Context.AddString("RLVL: not a client"); break; }
                    args.Context.AddString("RLVL: piece icon export started (see the log); the server uses it only from a client Server.ItemsExportBy allows");
                    PieceIconsCapture.Start("console");
                    break;
                default:
                    args.Context.AddString("usage: rlvl status | rlvl export now | rlvl export items | rlvl export icons");
                    break;
            }
        }
    }
}
