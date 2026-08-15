using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Fights
{
    public class Fighter
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public int TeamId { get; set; } // 0 = Team Blue (Players), 1 = Team Red (Monsters)
        public int CellId { get; set; }
        public bool IsMonster { get; set; }
        public int MonsterId { get; set; }
        public int GradeIndex { get; set; } = 0;
        public int Level { get; set; }
        public int LookBoneId { get; set; }
        public string Look { get; set; } = "";

        // Combat Stats
        public int MaxHP { get; set; }
        public int CurrentHP { get; set; }
        public int MaxAP { get; set; }
        public int CurrentAP { get; set; }
        public int MaxMP { get; set; }
        public int CurrentMP { get; set; }
        public int Initiative { get; set; }

        /// <summary>Experience this fighter awards on death (gradeXp from its record).</summary>
        public int XpReward { get; set; }

        // Elemental Stats
        public int Strength { get; set; }
        public int Intelligence { get; set; }
        public int Chance { get; set; }
        public int Agility { get; set; }
        public int Power { get; set; }

        /// <summary>Critical points granted by the equipment, added on top of the spell's own.</summary>
        public int CriticalBonus { get; set; }

        // Resistances (% and Flat)
        public int NeutralResPct { get; set; }
        public int EarthResPct { get; set; }
        public int FireResPct { get; set; }
        public int WaterResPct { get; set; }
        public int AirResPct { get; set; }

        public bool IsAlive => CurrentHP > 0;
        public bool IsReady { get; set; }

        // Spells available to this fighter
        public List<int> SpellIds { get; set; } = new List<int>();

        /// <summary>Grade of each spell (monsters do not always have it at level 1).</summary>
        public Dictionary<int, int> SpellGrades { get; set; } = new Dictionary<int, int>();

        public int AccumulatedMpLoss { get; set; } = 0;
        public int AccumulatedApLoss { get; set; } = 0;

        /// <summary>
        /// Temporary bonus to the base damage of a specific spell, together with the round it
        /// expires on. Frozen Arrow, for instance, leaves +4 base damage for 3 turns, and recasting
        /// it refreshes the deadline instead of stacking again (max stack 1).
        /// </summary>
        public Dictionary<int, (int Bonus, int ExpiresRound)> SpellDamageBuffs { get; }
            = new Dictionary<int, (int, int)>();

        public void ApplySpellDamageBuff(int spellId, int bonus, int duration, int currentRound)
        {
            SpellDamageBuffs[spellId] = (bonus, currentRound + duration);
        }

        public int GetSpellDamageBonus(int spellId, int currentRound)
        {
            if (!SpellDamageBuffs.TryGetValue(spellId, out var b)) return 0;
            if (currentRound >= b.ExpiresRound)
            {
                SpellDamageBuffs.Remove(spellId);
                return 0;
            }
            return b.Bonus;
        }

        public void StartTurn()
        {
            CurrentAP = MaxAP;
            CurrentMP = MaxMP;
            AccumulatedMpLoss = 0;
            AccumulatedApLoss = 0;
        }

        public void TakeDamage(int damage)
        {
            CurrentHP = Math.Max(0, CurrentHP - damage);
        }

        public int GetStatForElement(ElementType element)
        {
            return element switch
            {
                ElementType.Earth => Strength,
                ElementType.Fire => Intelligence,
                ElementType.Water => Chance,
                ElementType.Air => Agility,
                ElementType.Neutral => Math.Max(Strength, Math.Max(Intelligence, Math.Max(Chance, Agility))),
                _ => 0
            };
        }

        public int GetResPctForElement(ElementType element)
        {
            return element switch
            {
                ElementType.Neutral => NeutralResPct,
                ElementType.Earth => EarthResPct,
                ElementType.Fire => FireResPct,
                ElementType.Water => WaterResPct,
                ElementType.Air => AirResPct,
                _ => 0
            };
        }
    }
}
