using System;

namespace RlValheimLink.Core
{
    /// <summary>
    /// One-time rewrites of an existing <c>RaidLedger.ValheimLink.cfg</c> when a default changes.
    /// BepInEx keeps a value already written to the file, so a new default reaches only fresh installs;
    /// <c>[Server] ConfigRevision</c> records which rewrites have run so each runs once and an operator's
    /// later choice is never overwritten.
    /// </summary>
    public static class ConfigMigration
    {
        /// <summary>The revision a config file is at once every migration below has run.</summary>
        public const int CurrentRevision = 1;

        /// <summary>Outcome of <see cref="Migrate"/>: the values to keep, and whether ItemsExportBy was rewritten.</summary>
        public readonly struct Result
        {
            public Result(string itemsExportBy, int revision, bool itemsExportByChanged)
            {
                ItemsExportBy = itemsExportBy;
                Revision = revision;
                ItemsExportByChanged = itemsExportByChanged;
            }

            public string ItemsExportBy { get; }
            public int Revision { get; }
            public bool ItemsExportByChanged { get; }
            /// <summary>True when the caller must write the file (revision moved or a value changed).</summary>
            public bool NeedsSave(int previousRevision) => Revision != previousRevision || ItemsExportByChanged;
        }

        /// <summary>
        /// Revision 1 (0.4.6): <c>ItemsExportBy</c>'s default moved from <c>admins</c> to <c>anyone</c>.
        /// A file below revision 1 that still says <c>admins</c> (the old default) becomes <c>anyone</c>;
        /// <c>anyone</c> and <c>nobody</c> are kept. At revision 1 or later nothing is touched.
        /// </summary>
        public static Result Migrate(string itemsExportBy, int revision)
        {
            if (revision >= CurrentRevision) return new Result(itemsExportBy, revision, false);
            bool oldDefault = string.Equals((itemsExportBy ?? "").Trim(), "admins", StringComparison.OrdinalIgnoreCase);
            return oldDefault
                ? new Result("anyone", CurrentRevision, true)
                : new Result(itemsExportBy, CurrentRevision, false);
        }
    }
}
