using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Crujidor de hielo (3370) wraps himself in Cura Glacial (3443), Invulnerable (state 56) for
    /// good. Grieta (3449), which deactivates it on him alone for a turn, is in nobody's list: the
    /// fight lends it to every character facing him (content/monsters/lent_spells.json).
    /// </summary>
    [Collection("MapManager")]
    public class CrujidorDeHieloTests
    {
        private const int Crujidor = 3370, CuraGlacial = 3443, Grieta = 3449;
        private const int Invulnerable = 56, DeactivateState = 952;

        private static bool HasData => SpellEffects.De(CuraGlacial, 1).Count > 0 && SpellEffects.De(Grieta, 1).Count > 0
                                       && MonsterLentSpells.For(Crujidor).Count > 0;

        private static Fighter Monster(long id, int template, int cell) => new()
        {
            Id = id, CellId = cell, MaxHP = 12000, CurrentHP = 12000, Level = 150, IsMonster = true,
            MonsterId = template, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, TeamId = 1,
        };

        private static (FightInstance Fight, Fighter Crujidor, Fighter Other, Fighter Character, Fighter Partner) Board()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200, Breed = 1,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var partner = new Fighter
            {
                Id = 11, CellId = 120, MaxHP = 5000, CurrentHP = 5000, Level = 200, Breed = 8,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(character);
            fight.AddPlayer(partner);
            var crujidor = Monster(-1, Crujidor, 330);
            var other = Monster(-2, 2857, 400);
            fight.AddMonster(crujidor);
            fight.AddMonster(other);
            fight.StartFight();
            return (fight, crujidor, other, character, partner);
        }

        [Fact]
        public void The_file_reads_monster_spell_and_grade()
        {
            var rows = MonsterLentSpells.Parse("{\"lent\":[{\"monster\":3370,\"spell\":3449,\"grade\":1,\"why\":\"x\"}," +
                                               "{\"monster\":5,\"spell\":6},{\"spell\":7},{\"monster\":8}]}");
            Assert.Equal(new[] { (3370, 3449, 1), (5, 6, 1) }, rows.ToArray());
        }

        [Fact]
        public void Every_character_facing_him_gets_grieta_and_no_monster_does()
        {
            if (!HasData) return;
            var (fight, crujidor, other, character, partner) = Board();

            MonsterLentSpells.OnFightStarts(fight);
            MonsterLentSpells.OnFightStarts(fight);

            Assert.Single(character.GrantedSpells, g => g.Spell == Grieta);
            Assert.Single(partner.GrantedSpells, g => g.Spell == Grieta);
            Assert.DoesNotContain(crujidor.GrantedSpells, g => g.Spell == Grieta);
            Assert.DoesNotContain(other.GrantedSpells, g => g.Spell == Grieta);
        }

        [Fact]
        public void A_fight_without_him_lends_nothing()
        {
            if (!HasData) return;
            var fight = new FightInstance(1, 1);
            var character = new Fighter { Id = 10, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 200, Breed = 1 };
            fight.AddPlayer(character);
            fight.AddMonster(Monster(-2, 2857, 400));
            fight.StartFight();

            MonsterLentSpells.OnFightStarts(fight);

            Assert.DoesNotContain(character.GrantedSpells, g => g.Spell == Grieta);
        }

        /// <summary>
        /// Shut in Cura Glacial he takes nothing; Grieta cast next to him deactivates state 56 on him
        /// alone, and the blow that follows lands. The other monster keeps whatever he has.
        /// </summary>
        [Fact]
        public async Task Grieta_next_to_him_opens_him_to_blows()
        {
            if (!HasData) return;
            var (fight, crujidor, other, character, _) = Board();
            MonsterLentSpells.OnFightStarts(fight);

            await FightHandler.AplicarEfectosAsync(null, fight, crujidor, CuraGlacial, 1, crujidor,
                                                   EffectEngine.AlLanzar, crujidor.CellId);
            Assert.Contains(Invulnerable, crujidor.Buffs.Estados);

            int life = crujidor.CurrentHP;
            await FightHandler.HurtAsync(null, fight, character, 1, 1, crujidor, crujidor.CellId, tirada: new[] { Blow(400) });
            Assert.Equal(life, crujidor.CurrentHP);

            character.CellId = MapGeometry.GetNeighbors(crujidor.CellId).First();
            await FightHandler.AplicarEfectosAsync(null, fight, character, Grieta, 1, crujidor,
                                                   EffectEngine.AlLanzar, crujidor.CellId);
            Assert.Contains(crujidor.Buffs.Puestos, row => row.HechizoOrigen == Grieta && row.EffectId == DeactivateState);
            Assert.DoesNotContain(other.Buffs.Puestos, row => row.HechizoOrigen == Grieta);

            await FightHandler.HurtAsync(null, fight, character, 1, 1, crujidor, crujidor.CellId, tirada: new[] { Blow(400) });
            Assert.True(crujidor.CurrentHP < life);
        }

        private static SpellEffect Blow(int amount) => new()
        {
            EffectId = 100, EffectUid = 900100, DiceNum = amount, DiceSide = amount, Element = 0, TargetMask = "a,A",
        };
    }
}
