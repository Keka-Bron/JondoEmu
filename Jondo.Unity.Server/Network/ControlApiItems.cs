using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The harness's item routes: the legendary items (ItemFlags.IsLegendary, 2048), the Dofus
    /// (type 23) and the prismaradites (217) put on the slot's character the way equipment is
    /// kept -- a row of CharacterItems in an equipment position -- so that a fight gives him their
    /// spell (1175) as it gives anybody's, and the triggers of that spell provoked one by one.
    /// </summary>
    public static partial class ControlApi
    {
        private const int LegendaryFlag = 2048, DofusType = 23, PrismaraditeType = 217, TrophyType = 151;

        /// <summary>The equipment positions the harness uses: the weapon, the first Dofus slot, the shield.</summary>
        private static readonly int[] HarnessPositions = { 1, 9, 15 };

        /// <summary>The item types worn in the weapon's hand.</summary>
        private static readonly HashSet<int> WeaponTypes = new() { 2, 3, 4, 5, 6, 7, 8, 19, 20, 21, 22, 114, 271 };

        private static (int Type, int Flags, string Name, List<(int Spell, int Grade)> Spells)? ItemOf(int gid)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Type, NameId, Data FROM ItemTemplates WHERE Id = $gid;";
            command.Parameters.AddWithValue("$gid", gid);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            int type = reader.GetInt32(0);
            long nameId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            using var data = JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
            int flags = data.RootElement.TryGetProperty("m_flags", out var f) ? f.GetInt32() : 0;
            reader.Close();
            var translation = connection.CreateCommand();
            translation.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
            translation.Parameters.AddWithValue("$key", nameId.ToString());
            string name = translation.ExecuteScalar() as string ?? "";
            var spells = new List<(int, int)>();
            if (DatabaseManager.TryGetItemTemplateEffects(gid, out string effects))
                foreach (var e in Equipment.ParseEffects(effects))
                    if (e.Effect == SpellEffects.EfectoQueRegalaHechizo && e.DiceNum > 0)
                        spells.Add(((int)e.DiceNum, (int)Math.Max(1, e.DiceSide)));
            return (type, flags, name, spells);
        }

        /// <summary>{ group: legendary | dofus | prismaradite | trophy }: the items of the group that give a fight spell.</summary>
        private static Respuesta TestItemList(string cuerpo)
        {
            string group = Texto(cuerpo, "group");
            var found = new List<object>();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Type, Data FROM ItemTemplates;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                int gid = reader.GetInt32(0), type = reader.GetInt32(1);
                bool wanted;
                if (group == "legendary")
                {
                    using var data = JsonDocument.Parse(reader.IsDBNull(2) ? "{}" : reader.GetString(2));
                    wanted = data.RootElement.TryGetProperty("m_flags", out var f) && (f.GetInt32() & LegendaryFlag) != 0;
                }
                else wanted = group switch { "dofus" => type == DofusType, "prismaradite" => type == PrismaraditeType, "trophy" => type == TrophyType, _ => false };
                if (wanted) found.Add(gid);
            }
            var items = found.Cast<int>().Select(gid => (gid, ItemOf(gid))).Where(x => x.Item2 is { Spells.Count: > 0 })
                             .Select(x => new { gid = x.gid, name = x.Item2!.Value.Name, type = x.Item2.Value.Type, spells = x.Item2.Value.Spells.Select(s => new { spell = s.Spell, grade = s.Grade }) })
                             .ToList();
            return Bien(items);
        }

        /// <summary>
        /// { gid }: what the item's fight spells do and on which trigger -- each effect of each
        /// spell at the grade the item gives, and what the spells they cast do, a level down.
        /// </summary>
        private static Respuesta TestItem(string cuerpo)
        {
            int gid = (int)Numero(cuerpo, "gid");
            var item = ItemOf(gid);
            if (item == null) return Mal(404, "item");
            object Effects(int spell, int grade, int depth) => SpellEffects.De(spell, grade).Select(e => new
            {
                effect = e.EffectId, e.DiceNum, e.DiceSide, e.Value, e.Duration, e.Delay, mask = e.TargetMask, triggers = e.Triggers,
                casts = depth < 1 && SubCasts.Contains(e.EffectId) && e.DiceNum > 0 ? Effects(e.DiceNum, Math.Max(1, e.DiceSide), depth + 1) : null,
            }).ToList();
            return Bien(new
            {
                gid, name = item.Value.Name, type = item.Value.Type, legendary = (item.Value.Flags & LegendaryFlag) != 0,
                spells = item.Value.Spells.Select(s => new
                {
                    s.Spell, s.Grade,
                    // Every trigger the spell answers, from its own rows (the attitude's hooks).
                    triggers = SpellEffects.De(s.Spell, s.Grade).SelectMany(e => e.Triggers.Split('|')).Where(t => t.Length > 0).Distinct().ToList(),
                    effects = Effects(s.Spell, s.Grade, 0),
                }).ToList(),
            });
        }

        /// <summary>
        /// { gid }: the item worn by the slot's character, out of a fight -- in the weapon's hand,
        /// the shield's or the first Dofus slot -- and whatever the harness had put on before back
        /// in the bag. The next fight reads it as it reads anybody's equipment.
        /// </summary>
        private static Respuesta TestEquip(string cuerpo)
        {
            var session = TestSessionOnline();
            if (session == null) return Mal(404, "offline");
            long character = session.State.CharacterId;
            if (FightHandler.FightOf(character) != null) return Mal(409, "in-fight");
            int gid = (int)Numero(cuerpo, "gid");
            bool none = Numero(cuerpo, "none") != 0;
            var item = none ? null : ItemOf(gid);
            string effects = "[]";
            if (!none && (item == null || !DatabaseManager.TryGetItemTemplateEffects(gid, out effects))) return Mal(404, "item");
            int position = item == null ? 0 : WeaponTypes.Contains(item.Value.Type) ? 1 : item.Value.Type == 82 ? 15 : 9;
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                var off = connection.CreateCommand();
                off.CommandText = $"UPDATE CharacterItems SET Position = 63 WHERE CharacterId = $id AND Position IN ({string.Join(",", HarnessPositions)});";
                off.Parameters.AddWithValue("$id", character);
                off.ExecuteNonQuery();
            }
            if (none || item == null) return Bien(new { character, worn = 0 });
            long uid = DatabaseManager.NextItemUid();
            if (!DatabaseManager.InsertCharacterItem(uid, character, gid, 1, position, effects)) return Mal(500, "insert");
            return Bien(new { character, worn = gid, uid, position, name = item.Value.Name, spells = SpellEffects.ActitudesDelEquipo(character) });
        }

        /// <summary>{ team? }: the fight ended as a win of the slot's side (or of the team given).</summary>
        private static Respuesta TestFinish(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            var me = fight.Buscar(session.State.CharacterId);
            int winners = cuerpo.Contains("\"team\"") ? (int)Numero(cuerpo, "team") : me?.TeamId ?? 0;
            var lines = RunInSession(session, stream => FightHandler.TestFinishAsync(stream, fight, winners));
            return Bien(new { over = FightHandler.FightOf(session.State.CharacterId) == null, lines });
        }

        /// <summary>{ as?, on }: every cast of the fighter critical, or back to the dice.</summary>
        private static Respuesta TestCrit(string cuerpo)
        {
            var session = TestSessionOnline();
            var fight = session == null ? null : FightHandler.FightOf(session.State.CharacterId);
            if (session == null || fight == null) return Mal(409, "no-fight");
            var (who, _, error) = ActingAs(session, fight, cuerpo, toItsTurn: false);
            if (who == null || error.Length > 0 && error != "not-played-here") return Mal(404, error);
            bool on = cuerpo.Contains("\"on\":true");
            if (on) FightHandler.AlwaysCritical[who.Id] = true;
            else FightHandler.AlwaysCritical.TryRemove(who.Id, out _);
            return Bien(new { who = who.Id, always = on });
        }
    }
}
