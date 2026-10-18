using System;
using RlValheimLink.Client;
using RlValheimLink.Net;

namespace RlValheimLink
{
    /// <summary>`rlvl status|export items|export now` in the F5 console (client).</summary>
    internal static class ConsoleCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            try
            {
                new Terminal.ConsoleCommand("rlvl", "RL Valheim Link: rlvl status | rlvl export now | rlvl export items",
                    (Terminal.ConsoleEvent)Run, false, false, false, false, false, null, false, false, false);
            }
            catch (Exception e) { Log.Warn("console command not registered: " + e.Message); }
        }

        private static void Run(Terminal.ConsoleEventArgs args)
        {
            string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "status";
            string sub2 = args.Args.Length > 2 ? args.Args[2].ToLowerInvariant() : "";
            switch (sub)
            {
                case "status":
                    args.Context.AddString($"RLVL v{Plugin.Version}: server={Roles.IsServer} client={Roles.IsClient} optOut={PluginConfig.OptOut.Value} queued={LinkChannel.QueuedTransfers} itemsExport={(ItemsCapture.IsRunning ? "running" : "idle")}");
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
                default:
                    args.Context.AddString("usage: rlvl status | rlvl export now | rlvl export items");
                    break;
            }
        }
    }
}
