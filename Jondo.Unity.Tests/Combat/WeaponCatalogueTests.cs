using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Every weapon of the catalogue, not one at a time: born as the game makes it -- from its
    /// template, and rolled at a workshop --, read through the same code the fight reads the hand
    /// with, and swung at a monster. A weapon that loses a line on the way, or deals nothing,
    /// is named.
    /// </summary>
    [Collection("MapManager")]
    public class WeaponCatalogueTests
    {
        private const int WeaponCategory = 2;
        private const long Wielder = 9_970_401;

        private sealed record Weapon(int Gid, int Type, IReadOnlyList<int> HitEffects);

        /// <summary>The weapons of the base: their type and the effects of their template a hit does (category 2).</summary>
        private static List<Weapon> Catalogue()
        {
            var weapons = new List<Weapon>();
            if (!File.Exists(Jondo.Unity.Launcher.Paths.WorldDb)) return weapons;
            using var db = new SqliteConnection(Jondo.Unity.Launcher.Paths.WorldConnectionString);
            db.Open();
            var category = new Dictionary<int, int>();
            using (var effects = db.CreateCommand())
            {
                effects.CommandText = "SELECT Id, COALESCE(Category, 0) FROM Effects;";
                using var r = effects.ExecuteReader();
                while (r.Read()) category[r.GetInt32(0)] = r.GetInt32(1);
            }
            var lines = new Dictionary<long, int>();
            using (var rows = db.CreateCommand())
            {
                rows.CommandText = "SELECT Rid, EffectId FROM ItemEffects;";
                using var r = rows.ExecuteReader();
                while (r.Read()) lines[r.GetInt64(0)] = r.GetInt32(1);
            }
            using var templates = db.CreateCommand();
            templates.CommandText = "SELECT Id, Type, Data FROM ItemTemplates;";
            using var t = templates.ExecuteReader();
            while (t.Read())
            {
                using var data = JsonDocument.Parse(t.IsDBNull(2) ? "{}" : t.GetString(2));
                if (!data.RootElement.TryGetProperty("apCost", out var ap) || ap.GetInt32() <= 0) continue;
                var hit = new List<int>();
                if (data.RootElement.TryGetProperty("possibleEffects", out var possible)
                    && possible.TryGetProperty("Array", out var array))
                {
                    foreach (var reference in array.EnumerateArray())
                    {
                        if (!reference.TryGetProperty("rid", out var rid)) continue;
                        if (lines.TryGetValue(rid.GetInt64(), out int effect)
                            && category.TryGetValue(effect, out int c) && c == WeaponCategory)
                            hit.Add(effect);
                    }
                }
                weapons.Add(new Weapon(t.GetInt32(0), t.GetInt32(1), hit));
            }
            return weapons;
        }

        private static IEnumerable<(string Road, string Effects)> BornAs(int gid)
        {
            yield return ("template", DatabaseManager.FreshEffectsOf(gid));
            var template = Forgemagic.TemplateOf(gid);
            if (template != null) yield return ("workshop", Forgemagic.Serialize(Forgemagic.Roll(template, new Random(gid))));
        }

        /// <summary>
        /// Every line a weapon's hit does -- damage, steal, points taken or stolen, push, pull,
        /// heal, a step -- is read, whichever road made the weapon; its cost is its own and its
        /// type has a zone. The hand used to keep the damage and drop the rest: 116 weapons.
        /// </summary>
        [Fact]
        public void Every_weapon_reads_every_line_its_hit_does()
        {
            var weapons = Catalogue();
            if (weapons.Count == 0) return;
            var zones = JsonDocument.Parse(File.ReadAllText(Jondo.Unity.Launcher.Paths.WeaponZonesJson)).RootElement;
            var failures = new List<string>();
            foreach (var weapon in weapons)
            {
                if (!zones.TryGetProperty(weapon.Type.ToString(), out _))
                    failures.Add($"{weapon.Gid}: type {weapon.Type} has no zone");
                if (weapon.HitEffects.Count == 0) continue;
                foreach (var (road, effects) in BornAs(weapon.Gid))
                {
                    var arma = DatabaseManager.WeaponFromTemplate(weapon.Gid, effects);
                    if (arma == null) { failures.Add($"{weapon.Gid} ({road}): read as no weapon from {effects}"); continue; }
                    var read = arma.WeaponLines.Select(l => l.Effect).Concat(arma.WeaponSideLines.Select(l => l.Effect)).ToHashSet();
                    foreach (int effect in weapon.HitEffects.Distinct().Where(e => !read.Contains(e)))
                        failures.Add($"{weapon.Gid} ({road}): effect {effect} is not read, from {effects}");
                    if (arma.APCost <= 0) failures.Add($"{weapon.Gid} ({road}): costs {arma.APCost} AP");
                    if (arma.MaxRange < arma.MinRange) failures.Add($"{weapon.Gid} ({road}): range {arma.MinRange}-{arma.MaxRange}");
                }
            }
            Assert.True(failures.Count == 0, $"{failures.Count} problem(s):\n" + string.Join("\n", failures.Take(30)));
        }

        /// <summary>
        /// Every weapon with damage hurts the monster it is swung at, through the fight's own
        /// road (HurtAsync with no spell), and the rest of its hit goes through without failing.
        /// </summary>
        [Fact]
        public async Task Every_weapon_hurts_what_it_hits()
        {
            var weapons = Catalogue();
            if (weapons.Count == 0) return;
            var failures = new List<string>();
            var hand = FightHandler.ArmaEnLaMano;
            var session = GameSession.SinSocket();
            session.State.CharacterId = Wielder;
            try
            {
                using (SessionContext.Push(session))
                {
                    foreach (var weapon in weapons.Where(w => w.HitEffects.Count > 0))
                    {
                        string effects = DatabaseManager.FreshEffectsOf(weapon.Gid);
                        var arma = DatabaseManager.WeaponFromTemplate(weapon.Gid, effects);
                        if (arma == null) continue;
                        FightHandler.ArmaEnLaMano = _ => arma;

                        var wielder = new Fighter
                        {
                            Id = Wielder, Name = "W", CellId = 300, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Strength = 200, Intelligence = 200,
                            Chance = 200, Agility = 200,
                        };
                        var monster = new Fighter
                        {
                            Id = -1, Name = "M", IsMonster = true, CellId = 301, Level = 50, MaxHP = 50000, CurrentHP = 40000,
                            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
                        };
                        var fight = new FightInstance(4107, 990_004_107);
                        fight.AddPlayer(wielder); wielder.CellId = 300;
                        fight.AddMonster(monster); monster.CellId = 301;

                        try
                        {
                            // The damage first, as the fight deals it, and then the rest: a healing
                            // wand's heal lands on whoever it hit, and would hide its blow.
                            int before = monster.CurrentHP;
                            await FightHandler.HurtAsync(null, fight, wielder, 0, 0, monster, 301);
                            if (arma.WeaponLines.Count > 0 && monster.CurrentHP >= before)
                                failures.Add($"{weapon.Gid}: {arma.WeaponLines.Count} damage line(s) and no damage, from {effects}");
                            await FightHandler.WeaponSideLinesAsync(null, fight, wielder, monster, 301, false);
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{weapon.Gid}: {ex.GetType().Name} {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                FightHandler.ArmaEnLaMano = hand;
            }
            Assert.True(failures.Count == 0, $"{failures.Count} problem(s):\n" + string.Join("\n", failures.Take(30)));
        }
    }
}
