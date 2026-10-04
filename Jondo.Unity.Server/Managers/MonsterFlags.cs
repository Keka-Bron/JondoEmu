using System;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The <c>m_flags</c> of a monster template, and what its bits mean.
    /// </summary>
    /// <remarks>
    /// The bits are the client's own enum, <c>Core.DataCenter.Metadata.Monster.MonsterFlags</c>,
    /// read off its constants in <c>Il2CppAnkama.Dofus.Core.DataCenter.dll</c>: 1 UseSummonSlot,
    /// 2 UseBombSlot, 4 IsBoss, 8 IsMiniBoss, 16 IsQuestMonster, 32 FastAnimsFun, 64 CanPlay,
    /// 128 CanTackle, 256 CanBePushed, 512 CanSwitchPos, 1024 CanSwitchPosOnTarget, 2048
    /// CanBeCarried, 4096 CanUsePortal... Two of them were already read here from the captures
    /// alone -- CanPlay is <see cref="Summons.CanPlayFlag"/> and IsBoss the bit Dreams reads --
    /// and both land on the enum's numbers.
    /// </remarks>
    public static class MonsterFlags
    {
        /// <summary>MonsterFlags.CanTackle: the creature holds whoever tries to walk away from it.</summary>
        public const long CanTackle = 128;

        private static readonly ConcurrentDictionary<int, long> _flags = new();

        /// <summary>The template's m_flags, or -1 when the template is not in world.db.</summary>
        public static long Of(int templateId)
            => _flags.GetOrAdd(templateId, id =>
            {
                if (id <= 0) return -1;
                try
                {
                    using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", id);
                    if (command.ExecuteScalar() is not string data) return -1;
                    using var doc = JsonDocument.Parse(data);
                    return doc.RootElement.TryGetProperty("m_flags", out var flags) && flags.TryGetInt64(out long bits)
                        ? bits
                        : 0;
                }
                catch (Exception)
                {
                    return -1;
                }
            });

        /// <summary>
        /// Whether the template lets its creature tackle. A template world.db does not have is
        /// taken as an ordinary monster, which is what 4,776 of the 5,134 are.
        /// </summary>
        public static bool AllowsTackle(int templateId)
        {
            long flags = Of(templateId);
            return flags < 0 || (flags & CanTackle) != 0;
        }
    }
}
