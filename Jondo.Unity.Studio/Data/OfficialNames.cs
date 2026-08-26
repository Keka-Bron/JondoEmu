using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>One of the names the client still ships.</summary>
    public sealed class OfficialName
    {
        /// <summary>The short name: <c>MapMovementRequest</c>.</summary>
        public string Short { get; init; } = "";

        /// <summary>The whole namespace it came with, which says which part of the game it is.</summary>
        public string Full { get; init; } = "";

        /// <summary>The bit between the protocol root and the class: Account, Fight, Npc…</summary>
        public string Area { get; init; } = "";

        /// <summary>A sort key that keeps a name and its number apart. Not used as an id.</summary>
        public long Order { get; init; }

        public override string ToString() => Area.Length > 0 ? $"{Short}  ·  {Area}" : Short;
    }

    /// <summary>
    /// The closed list of names an opcode is allowed to be given.
    /// </summary>
    /// <remarks>
    /// Ankama's obfuscator renames every message class to three letters, but it leaves the original
    /// strings sitting in <c>global-metadata.dat</c>. They are orphaned — nothing references them,
    /// so the client cannot tell you which name goes with which opcode — and that is exactly why
    /// they are worth having: they are the <b>closed list of valid names for this version</b>.
    ///
    /// Which turns naming a packet from invention into a choice. Somebody walks through a door,
    /// sees <c>jjg</c> go past in the traffic view, and picks <c>MapMovementRequest</c> off a list
    /// of 513 rather than writing down a guess that nobody can check later.
    /// </remarks>
    public static class OfficialNames
    {
        private static List<OfficialName>? _all;

        /// <summary>All of them, loaded once. Empty when the file is not there.</summary>
        public static List<OfficialName> All(Action<string>? report = null)
        {
            if (_all != null) return _all;

            _all = new List<OfficialName>();
            string path = Paths.RealNamesTsv;

            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; packets can still be named, " +
                               "but from memory rather than from the client's own list.");
                return _all;
            }

            try
            {
                long order = 0;
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;

                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;

                    string shortName = line[..tab].Trim();
                    string full = line[(tab + 1)..].Trim();
                    if (shortName.Length == 0) continue;

                    _all.Add(new OfficialName
                    {
                        Short = shortName,
                        Full = full,
                        Area = AreaOf(full, shortName),
                        Order = order++,
                    });
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} could not be read: {ex.Message}");
            }

            _all.Sort((a, b) => string.Compare(a.Short, b.Short, StringComparison.OrdinalIgnoreCase));
            return _all;
        }

        /// <summary>
        /// The part of the game a name belongs to, out of its namespace.
        /// </summary>
        /// <remarks>
        /// <c>Com.Ankama.Dofus.Server.Game.Protocol.Npc.NpcDialogRequest</c> is an Npc message, and
        /// saying so next to the name is what makes a list of 513 searchable by what you were
        /// doing when the packet went past.
        /// </remarks>
        private static string AreaOf(string full, string shortName)
        {
            if (full.Length == 0) return "";

            int last = full.LastIndexOf('.');
            if (last <= 0) return "";

            string head = full[..last];
            int before = head.LastIndexOf('.');
            string area = before >= 0 ? head[(before + 1)..] : head;

            return string.Equals(area, shortName, StringComparison.Ordinal) ? "" : area;
        }
    }
}
