using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The companion worn in the «Compagnon» slot (Managers.Companions) into its master's fights
    /// against monsters: beside him on his side, at his level, played by his client in its turns
    /// as Barnabé is -- a played ally, its bar a summon's and its turn his.
    /// </summary>
    /// <remarks>
    /// Not into a duel, a Koliseo or any fight of people: only where monsters are fought, as the
    /// game keeps companions out of PvP. It is no person of the fight -- it does not count for
    /// the group, earns nothing and drops nothing -- and goes with the fight.
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>
        /// The companion the character in session wears into the fight he is opening or joining,
        /// on a free cell of his side, with the id given. Null when he wears none, or it has no data.
        /// </summary>
        internal static Fighter? AddCompanion(FightInstance fight, Fighter master, long id)
        {
            var companion = Companions.Worn();
            if (companion == null) return null;

            var fighter = BuildCompanionFighter(companion, master, id);
            var taken = fight.Todos.Select(f => f.CellId).ToHashSet();
            var side = master.TeamId == FightInstance.Azules ? fight.BluePlacementCells : fight.RedPlacementCells;
            int cell = side.FirstOrDefault(c => !taken.Contains(c), -1);
            if (cell < 0)
            {
                Program.LogDebug($"[Companions] No free cell for {master.Name}'s companion {companion.Id} in fight #{fight.FightId}.");
                return null;
            }
            fighter.CellId = cell;
            fight.AddPlayer(fighter);
            Program.LogDebug($"[Companions] Companion {companion.Id} ({fighter.Id}) comes into fight #{fight.FightId} beside {master.Name}, " +
                             $"level {fighter.Level}, {fighter.MaxHP} life, {fighter.MaxAP} AP, {fighter.MaxMP} MP.");
            return fighter;
        }

        /// <summary>
        /// A companion's fighter at its master's level: its characteristics from their ranges, its
        /// spells at the grade that level reaches, its behaviour as a monster's, played by him.
        /// </summary>
        internal static Fighter BuildCompanionFighter(Companions.Companion companion, Fighter master, long id)
        {
            int level = Math.Max(1, master.Level);
            var stats = companion.Characteristics.ToDictionary(c => c.Characteristic,
                                                                c => Companions.ValueAt(c.Ranges, level));
            int Stat(int characteristic) => stats.TryGetValue(characteristic, out int v) ? v : 0;

            string look = companion.Look;
            int bones = 1;
            var first = look.Trim('{', '}').Split('|')[0];
            if (int.TryParse(first, out int parsed)) bones = parsed;

            var fighter = new Fighter
            {
                Id = id,
                Name = $"Companion_{companion.Id}",
                Look = look,
                LookBoneId = bones,
                TeamId = master.TeamId,
                IsMonster = true,
                CompanionId = companion.Id,
                Level = level,
                // Its life is its own and what its vitality adds, as a character's.
                MaxHP = Math.Max(1, Stat(0) + Stat(11)),
                MaxAP = Stat(1),
                MaxMP = Stat(23),
                Vitality = Stat(11),
                Strength = Stat(10),
                Intelligence = Stat(15),
                Chance = Stat(13),
                Agility = Stat(14),
                Power = Stat(25),
                FlatDamage = Stat(16),
                PushDamage = Stat(84),
                NeutralResPct = Stat(37),
                EarthResPct = Stat(33),
                FireResPct = Stat(34),
                WaterResPct = Stat(35),
                AirResPct = Stat(36),
                IsReady = true,
            };
            fighter.Initiative = (fighter.Strength + fighter.Intelligence + fighter.Chance + fighter.Agility)
                                 + fighter.MaxHP / 10;
            fighter.CurrentHP = fighter.MaxHP;
            fighter.CurrentAP = fighter.MaxAP;
            fighter.CurrentMP = fighter.MaxMP;
            fighter.Otras[Fighter.CaracteristicaDeErosion] = Fighter.ErosionBase;
            foreach (int other in new[] { EffectEngine.EsquivaPA, EffectEngine.EsquivaPM, EffectEngine.RetiraPA,
                                          EffectEngine.RetiraPM, Tackle.EscapeCharacteristic, Tackle.TackleCharacteristic })
                fighter.Otras[other] = Stat(other);

            foreach (var (spell, grades) in companion.Spells)
            {
                int grade = Companions.GradeAt(grades, level);
                fighter.SpellIds.Add(spell);
                fighter.SpellGrades[spell] = grade;
                int wait = LimitesDeGrado(spell, grade).EsperaInicial;
                if (wait > 0) fighter.Recarga[spell] = wait;
            }
            fighter.Conducta = SpellCriteria.SpellOfLevel(companion.StartingSpellLevelId);

            // Its master plays it, from his client: its bar and its turns go to him.
            fighter.DrivenBy = master.Id;
            fighter.IsPlayedAlly = true;
            fighter.HechizosDeInvocado = fighter.SpellIds
                .Select(spell => (spell, Math.Max(1, fighter.SpellGrades.TryGetValue(spell, out int g) ? g : 1)))
                .ToList();
            return fighter;
        }
    }
}
