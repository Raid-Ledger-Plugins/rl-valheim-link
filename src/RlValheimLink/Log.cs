using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace RlValheimLink
{
    /// <summary>
    /// All log lines carry the RLVL: prefix the operator checklist greps for. Any registered secret
    /// (the per-server token) is replaced by *** in every line, whatever code path produced it.
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _log;
        private static readonly List<string> Secrets = new List<string>();

        public static void Init(ManualLogSource log) => _log = log;

        public static void RedactSecret(string secret)
        {
            if (!string.IsNullOrEmpty(secret) && secret.Length >= 8 && !Secrets.Contains(secret)) Secrets.Add(secret);
        }

        public static string Redact(string s)
        {
            if (s == null) return null;
            foreach (var secret in Secrets) if (s.Contains(secret)) s = s.Replace(secret, "***");
            return s;
        }

        public static void Debug(string msg) => _log?.LogDebug("RLVL: " + Redact(msg));
        public static void Info(string msg) => _log?.LogInfo("RLVL: " + Redact(msg));
        public static void Warn(string msg) => _log?.LogWarning("RLVL: " + Redact(msg));
        public static void Error(string msg) => _log?.LogError("RLVL: " + Redact(msg));
        public static void Error(string msg, Exception e) => _log?.LogError("RLVL: " + Redact(msg + ": " + e));
    }
}
