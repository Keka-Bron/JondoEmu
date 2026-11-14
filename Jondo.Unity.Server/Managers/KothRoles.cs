using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The conquests' roles: which one a character holds, changing it with Hal Ylanza, and how many
    /// of each an alliance may bring to one conquest.
    /// </summary>
    /// <remarks>
    /// A role is held by carrying its certificate, «Certificado de explorador» and the rest (item
    /// type 24, datos/alliances_*.json's kothRoles): the role potions' criteria ask for it
    /// (PO=27659 for the scout's), and Hal Ylanza hands it over with «Ya puedes irte, ve a
    /// divertirte con tu nuevo juguete». Carrying none, a character is cannon fodder, the default
    /// role (Translations 1119557). One at a time: taking a role takes the other certificates away.
    /// </remarks>
    public static class KothRoles
    {
        /// <summary>The roles' ids, the client's kothRoles.</summary>
        public const int Seer = 2;
        public const int Gravedigger = 3;
        public const int Resurrector = 4;
        public const int Sentinel = 5;
        public const int Scout = 6;
        public const int Champion = 7;

        /// <summary>
        /// How many of a role one alliance may bring into one conquest's zone, the numbers of the
        /// client's help (Translations 1119557): «Campeón (1)», «Vidente (10)», «Explorador (50)»,
        /// «Resucitador (100)», «Centinela (150)», «Enterrador (150)». Cannon fodder has none.
        /// </summary>
        public static readonly IReadOnlyDictionary<int, int> Limits = new Dictionary<int, int>
        {
            [Champion] = 1,
            [Seer] = 10,
            [Scout] = 50,
            [Resurrector] = 100,
            [Sentinel] = 150,
            [Gravedigger] = 150,
        };

        /// <summary>
        /// INFERRED: how long before a character may change role again. Hal Ylanza only says «Vuelve
        /// a verme dentro de unos minutos».
        /// </summary>
        public const long ChangeCooldownMs = 5 * 60_000;

        /// <summary>When each character last changed role, this run.</summary>
        private static readonly ConcurrentDictionary<long, long> _changedAt = new();

        /// <summary>The role a role id's certificate stands for, or 0 for an item that is none.</summary>
        public static int RoleOfCertificate(int template)
            => AllianceData.KothRoles.FirstOrDefault(r => r.Certificate == template && template != 0)?.Id ?? 0;

        /// <summary>A role's certificate, or 0 without the data.</summary>
        public static int CertificateOf(int role) => AllianceData.KothRoles.FirstOrDefault(r => r.Id == role)?.Certificate ?? 0;

        /// <summary>The role of the session being served: the certificate it carries, or the default.</summary>
        public static int RoleOfCurrent()
        {
            foreach (var role in AllianceData.KothRoles)
                if (!role.Default && role.Certificate != 0 && Equipment.HowMany(role.Certificate) > 0) return role.Id;
            return AllianceData.DefaultKothRole;
        }

        /// <summary>The role of a connected character.</summary>
        public static int RoleOf(GameSession session)
        {
            if (session == null) return AllianceData.DefaultKothRole;
            using (SessionContext.Push(session)) return RoleOfCurrent();
        }

        /// <summary>No role, as the alliance's members tab shows it: «---».</summary>
        public const int None = -1;

        /// <summary>
        /// The role whose certificate a character carries, online or not -- the default role's too,
        /// which Hal Ylanza hands over like the others --, or <see cref="None"/>: the alliance's
        /// members tab shows it in «Rôle AvA».
        /// </summary>
        public static int HeldRoleOf(long characterId)
        {
            var certificates = AllianceData.KothRoles.Where(r => r.Certificate != 0).ToList();
            if (characterId == 0 || certificates.Count == 0) return None;
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Gid FROM CharacterItems WHERE CharacterId = $id AND Quantity > 0 AND Gid IN (" +
                                      string.Join(",", certificates.Select(r => r.Certificate)) + ");";
                command.Parameters.AddWithValue("$id", characterId);
                using var reader = command.ExecuteReader();
                var held = new HashSet<int>();
                while (reader.Read()) held.Add(reader.GetInt32(0));
                // A role's certificate before the default's, as RoleOfCurrent reads them.
                return certificates.OrderBy(r => r.Default).FirstOrDefault(r => held.Contains(r.Certificate))?.Id ?? None;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Conquest] The role of {characterId} could not be read: {ex.Message}");
                return None;
            }
        }

        /// <summary>Whether the character may change role now.</summary>
        public static bool MayChange(long characterId, long nowMs)
            => !_changedAt.TryGetValue(characterId, out long at) || nowMs - at >= ChangeCooldownMs;

        /// <summary>
        /// The session being served takes a role: the other certificates go, the role's comes. False,
        /// and nothing changed, when it changed too recently or the role has no certificate.
        /// </summary>
        public static async Task<bool> BecomeAsync(NetworkStream stream, int role, long nowMs)
        {
            long characterId = SessionContext.State.CharacterId;
            int certificate = CertificateOf(role);
            if (characterId == 0 || certificate == 0 || !MayChange(characterId, nowMs)) return false;

            foreach (var other in AllianceData.KothRoles)
            {
                if (other.Certificate == 0 || other.Certificate == certificate) continue;
                int held = Equipment.HowMany(other.Certificate);
                if (held > 0) await Equipment.TakeAsync(stream, other.Certificate, held);
            }
            if (Equipment.HowMany(certificate) == 0 && !await Equipment.GiveAsync(stream, certificate, 1)) return false;

            _changedAt[characterId] = nowMs;
            Console.WriteLine($"[Conquest] {SessionContext.State.CharacterName} takes the role {role}.");
            return true;
        }

        /// <summary>For the tests: everybody may change at once.</summary>
        internal static void Forget() => _changedAt.Clear();
    }
}
