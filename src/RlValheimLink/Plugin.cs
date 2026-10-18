using BepInEx;
using HarmonyLib;
using RlValheimLink.Client;
using RlValheimLink.Net;
using RlValheimLink.Server;
using UnityEngine;

namespace RlValheimLink
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "RaidLedger.ValheimLink";
        public const string Name = "RL Valheim Link";
        /// <summary>SemVer; must equal ModVersion in Directory.Build.props (CI checks). Goes into every message as modVersion.</summary>
        public const string Version = "0.1.0";

        public static Plugin Instance { get; private set; }
        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log.Init(Logger);
            PluginConfig.Bind(Config);
            Log.RedactSecret(PluginConfig.ServerToken.Value);
            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Patches));
            Log.Info($"loaded v{Version} (contract v{Contract.Caps.SchemaVersion}); waiting for ZNet to decide client/server role");
        }

        private void Update()
        {
            LinkChannel.Tick();
            ClientHalf.Tick();
            ServerHalf.Tick();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>Harmony hooks. Everything they do is delegated so the hooks stay one line each.</summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake)), HarmonyPostfix]
        private static void ZNet_Awake_Postfix(ZNet __instance) => Roles.OnZNetAwake(__instance);

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnDestroy)), HarmonyPostfix]
        private static void ZNet_OnDestroy_Postfix() => Roles.OnZNetDestroyed();

        // Periodic save broadcast, logout and quit all end up here on the client.
        [HarmonyPatch(typeof(Game), nameof(Game.SavePlayerProfile)), HarmonyPostfix]
        private static void Game_SavePlayerProfile_Postfix(bool setLogoutPoint) => ClientHalf.OnProfileSaved(setLogoutPoint);

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal)), HarmonyPostfix]
        private static void Terminal_InitTerminal_Postfix() => ConsoleCommands.Register();
    }

    /// <summary>Decides which half runs once ZNet exists; a non-dedicated host runs both.</summary>
    internal static class Roles
    {
        public static bool IsServer { get; private set; }
        public static bool IsClient { get; private set; }

        public static void OnZNetAwake(ZNet znet)
        {
            try
            {
                IsServer = znet.IsServer();
                IsClient = !znet.IsDedicated();
                LinkChannel.Register(IsServer);
                Log.Info($"ZNet up: server={IsServer} client={IsClient} dedicated={znet.IsDedicated()}");
                if (IsServer) ServerHalf.Start();
                if (IsClient) ClientHalf.Start();
            }
            catch (System.Exception e) { Log.Error("role start failed: " + e); }
        }

        public static void OnZNetDestroyed()
        {
            try
            {
                if (IsClient) ClientHalf.Stop();
                if (IsServer) ServerHalf.Stop();
                LinkChannel.Reset();
            }
            catch (System.Exception e) { Log.Error("role stop failed: " + e); }
            IsServer = IsClient = false;
        }
    }
}
