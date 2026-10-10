using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Gigalodón's "Idées noires": the darker a floor, the stronger its monsters. The fewer
    /// lights the raid keeps burning, the more vitality, power and movement its fights face.
    /// </summary>
    /// <remarks>
    /// All of it is the client's data. The spell "Pensamientos Oscuros" (32741) casts four others,
    /// one per tier, whose admin names say which: "Palier 3" (32742), "Palier 2" (32744),
    /// "Palier 1" (32745) and "Palier 0" (32746). Each is a permanent buff on its caster:
    ///
    ///   light 3   +20 % vitality, +100 power
    ///   light 2   +50 % vitality, +250 power
    ///   light 1   +100 % vitality, +500 power, +1 MP
    ///   light 0   +200 % vitality, +1000 power, +2 MP
    ///
    /// which is the guides' table to the point. The dispatcher casts all four unconditionally, so
    /// choosing the tier is the server's: the one of the fight floor's light, and at full light
    /// (4) none.
    ///
    /// Which monsters: the ones whose aggression immunity criterion names that floor's light
    /// variable (<c>…|(PB=1132&amp;RV!7,n2_worldlight,0)|…</c>): they are the ones bound to the
    /// light. Willorque's criterion is empty and the Gigalodón's too, and the guides say exactly
    /// that neither gets the boost.
    ///
    /// The tier goes on as an attitude, so it is cast in the open before the first turn, with the
    /// rest of the fight's attitudes, and the client draws the buff on each monster.
    /// </remarks>
    public static class GuildRaidDarkness
    {
        /// <summary>The client's "Pensamientos Oscuros", the spell that casts the tiers.</summary>
        public const int DarkThoughts = 32741;

        private static IReadOnlyDictionary<int, int> _tiers;

        /// <summary>For the tests: the tier spells by light, instead of the database's.</summary>
        internal static void UseTiers(IReadOnlyDictionary<int, int> tiers) => _tiers = tiers;

        /// <summary>The tier spells by the light they answer to, read once from the dispatcher.</summary>
        public static IReadOnlyDictionary<int, int> Tiers => _tiers ??= ReadTiers();

        private static IReadOnlyDictionary<int, int> ReadTiers()
        {
            var tiers = new Dictionary<int, int>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                foreach (var cast in SpellEffects.De(DarkThoughts, 1))
                {
                    if (cast.EffectId != EffectEngine.EfectoQueLanzaHechizo || cast.DiceNum <= 0) continue;
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Data FROM SpellTemplates WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", cast.DiceNum);
                    if (command.ExecuteScalar() is not string data) continue;
                    using var doc = JsonDocument.Parse(data);
                    string admin = doc.RootElement.TryGetProperty("adminName", out var a) ? a.GetString() ?? "" : "";
                    var tier = Regex.Match(admin, @"Palier\s+(\d+)");
                    if (tier.Success) tiers[int.Parse(tier.Groups[1].Value)] = cast.DiceNum;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raids] Could not read the tiers of Pensamientos Oscuros: {ex.Message}");
            }
            return tiers;
        }

        /// <summary>
        /// Whether a monster's starting spell gives it Pensamientos Oscuros already: the abyss's
        /// monsters start with the dispatcher itself (their startingSpellId, 85924, is its level),
        /// the Cangrancio with a spell that casts it.
        /// </summary>
        public static bool CastsDarkThoughts(int spell, int grade)
            => spell == DarkThoughts
               || spell > 0 && SpellEffects.De(spell, Math.Max(1, grade))
                                           .Any(e => e.EffectId == EffectEngine.EfectoQueLanzaHechizo && e.DiceNum == DarkThoughts);

        /// <summary>The tier spell for a floor's light, or 0 when that light gives none.</summary>
        public static int TierFor(int light) => Tiers.TryGetValue(light, out int spell) ? spell : 0;

        /// <summary>Whether a monster is bound to a floor's light: its criterion names the floor's variable.</summary>
        public static bool BoundToLight(string immunityCriterion, int floor)
            => !string.IsNullOrEmpty(immunityCriterion)
               && Regex.IsMatch(immunityCriterion, @"RV[!=<>]+\d+," + Regex.Escape(RaidInstance.LightVariable(floor)) + @"\b");

        /// <summary>
        /// Gives a raid fight's monsters the tier of their floor's light, as an attitude cast before
        /// the first turn. Nothing outside a raid with light, or at full light.
        /// </summary>
        public static int Boost(FightInstance fight, long characterId)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            var kind = raid == null ? null : Raids.Of(raid.RaidId);
            if (kind == null || !kind.HasLight) return 0;
            int floor = kind.FloorOf(GuildRaidManager.SubAreaOf(fight.RoleplayMapId));
            if (floor <= 0) return 0;
            int light = raid.LightAt(floor, DateTimeOffset.UtcNow);
            int spell = TierFor(light);
            fight.DarknessTier = spell;
            if (spell == 0) return 0;

            int boosted = 0;
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado))
            {
                if (!BoundToLight(DatabaseManager.MonsterAggressiveImmunity(monster.MonsterId), floor)) continue;
                // A monster whose starting spell is (or casts) Pensamientos Oscuros takes its tier there,
                // through the fight's DarknessTier; the attitude is for those whose start does not.
                if (CastsDarkThoughts(monster.Conducta.Spell, monster.Conducta.Grade)) continue;
                monster.Buffs.PonerActitud(spell);
                boosted++;
            }
            if (boosted > 0)
                Program.LogDebug($"[Raids] Light {light} on floor {floor}: {boosted} monster(s) take Pensamientos Oscuros ({spell}).");
            return boosted;
        }
    }
}
