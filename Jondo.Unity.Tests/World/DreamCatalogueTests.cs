using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The fountain's and the favour's catalogue (content/dreams/fountain.json) against the
    /// client's reward rows and the guide's rules, and what its bonuses do in a fight.
    /// </summary>
    [Collection("MapManager")]
    public class DreamCatalogueTests
    {
        private static Dreams.Sueno New()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, 4, 100, 200);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        /// <summary>
        /// Every reward of the catalogue is a row of the client's InfiniteDreamRewardsDataRoot, and
        /// carries what its actions say: the same effects, as many dream points, sands and storms,
        /// and fifty dreamer levels per level action.
        /// </summary>
        [Fact]
        public void Every_reward_is_its_client_row()
        {
            var ids = DreamCatalogue.Fountain.Select(f => f.Reward).Concat(DreamCatalogue.Favour)
                                    .Append(DreamCatalogue.PurseId).Distinct().ToList();
            Assert.Equal(57, DreamCatalogue.Fountain.Count);
            Assert.Equal(14, DreamCatalogue.Favour.Count);

            foreach (int id in ids)
            {
                var reward = DreamCatalogue.RewardOf(id);
                var row = DreamData.RewardOf(id);
                Assert.NotNull(reward);
                Assert.NotNull(row);
                var effects = row!.Actions.Select(DreamData.EffectOfAction).Where(e => e != 0).OrderBy(e => e);
                Assert.Equal(effects, reward!.Bonuses.Select(b => b.Efecto).OrderBy(e => e));
                Assert.Equal(row.Actions.Count(a => a == DreamData.DreamPointAction), reward.Points);
                Assert.Equal(row.Actions.Count(a => a == DreamData.SandAction), reward.Sand);
                Assert.Equal(row.Actions.Count(a => a == DreamData.StormAction), reward.Storms);
                Assert.Equal(50 * row.Actions.Count(a => a == DreamData.DreamerLevelAction), reward.Levels);
                Assert.NotEqual(0, reward.Id);
            }
        }

        /// <summary>A spell's offer is drawn with the spell's icon and teaches its grade 1; a passive's names its spell.</summary>
        [Fact]
        public void Spells_and_passives_are_the_client_s()
        {
            var psst = DreamCatalogue.RewardOf(149)!;
            Assert.Equal(15389, psst.Id);
            Assert.Equal(new[] { (3405, 85231) }, psst.Bonuses.Select(b => (b.Efecto, b.Valor)));

            var sadida = DreamCatalogue.RewardOf(43)!;
            Assert.Equal(15347, sadida.Id);
            var passive = Assert.Single(sadida.Bonuses);
            Assert.Equal((Dreams.PassiveEffect, 31735, true), (passive.Efecto, passive.Valor, passive.Anidado));
        }

        /// <summary>
        /// The first fountain -- band I's -- offers nothing above fifteen points, and of the
        /// passives only Vent Arrière; a later one, anything.
        /// </summary>
        [Fact]
        public void The_first_fountain_sells_cheap()
        {
            var dream = New();
            var first = dream.Salas.First(r => r.EsFuente);
            Assert.Equal(1, first.Franja);

            for (int i = 0; i < 60; i++)
            {
                var offers = DreamCatalogue.DrawFountain(dream, first, new Random(i));
                Assert.Equal(5, offers.Count);
                Assert.Equal(5, offers.Select(o => o.Tag).Distinct().Count());
                Assert.All(offers, o => Assert.True(o.Price <= 15));
                Assert.All(offers.Where(o => DreamCatalogue.EntryOf(o.Tag)!.Kind == DreamCatalogue.Passive),
                           o => Assert.Equal(148, o.Tag));
            }

            var later = new Dreams.Sala { Id = 99, EsFuente = true, Franja = 2 };
            var seen = new HashSet<int>();
            for (int i = 0; i < 200; i++)
                foreach (var offer in DreamCatalogue.DrawFountain(dream, later, new Random(i))) seen.Add(offer.Price);
            Assert.Contains(25, seen);
        }

        /// <summary>
        /// What the dream holds is not offered again -- a unique bonus taken, a passive, a spell
        /// -- but a spell spent by charges is: one more Oblivion is one more charge.
        /// </summary>
        [Fact]
        public void What_is_held_is_not_offered_again()
        {
            var dream = New();
            var later = new Dreams.Sala { Id = 99, EsFuente = true, Franja = 2 };
            dream.RewardsTaken.Add(109);
            dream.Ganados.Add(new Dreams.Bono(Dreams.PassiveEffect, 31735, true));
            dream.Ganados.Add(new Dreams.Bono(DreamSpells.LearnEffect, DreamSpells.FirstLevelOf(32424)));
            dream.Ganados.Add(new Dreams.Bono(DreamSpells.LearnEffect, DreamSpells.FirstLevelOf(31745)));

            Assert.False(DreamCatalogue.MayBeOffered(dream, later, 109, 15));
            Assert.False(DreamCatalogue.MayBeOffered(dream, later, 43, 25));
            Assert.False(DreamCatalogue.MayBeOffered(dream, later, 149, 15));
            Assert.True(DreamCatalogue.MayBeOffered(dream, later, 98, 15));
            Assert.True(DreamCatalogue.MayBeOffered(dream, later, 110, 15));
        }

        /// <summary>
        /// A purchase is replaced on the spot, in its place -- "un nouveau choix prend la place de
        /// celui que vous venez d'acheter" -- and a storm draws all five again.
        /// </summary>
        [Fact]
        public void The_fountain_restocks_and_the_storm_renews_it()
        {
            var dream = New();
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            dream.DreamPoints = 100;
            var before = fountain.Offers!.Select(o => o.Tag).ToList();

            var bought = Dreams.Buy(dream, before[2], out _);
            Assert.NotNull(bought);
            Assert.Equal(5, fountain.Offers!.Count);
            Assert.DoesNotContain(fountain.Offers, o => o.Tag == before[2]);
            Assert.Equal(before.Take(2).Concat(before.Skip(3)), fountain.Offers.Where((_, i) => i != 2).Select(o => o.Tag));

            Assert.True(Dreams.RerollOffers(dream, fountain));
            Assert.Equal(5, fountain.Offers!.Count);
        }

        /// <summary>The fountain's bonuses on a fighter: each one the characteristic its effect names.</summary>
        [Fact]
        public void The_fountain_s_bonuses_are_the_fighter_s()
        {
            var dream = New();
            foreach (int reward in new[] { 103, 56, 102, 109, 110, 108, 162, 151, 135, 65, 142, 158 })
                foreach (var bonus in DreamCatalogue.RewardOf(reward)!.Bonuses) Dreams.Gain(dream, bonus);

            var fighter = new Fighter { MaxHP = 1000, CurrentHP = 1000, NeutralResPct = 10 };
            foreach (var (characteristic, value) in new[] { (78, 20), (79, 20), (82, 10), (83, 10), (27, 10), (28, 10), (75, 10), (26, 1) })
                fighter.Otras[characteristic] = value;
            Dreams.ApplyTo(fighter, dream);

            Assert.Equal(46, fighter.Otra(79));                      // tackle +100% (Acrobate) +30% (Belliciste)
            Assert.Equal(40, fighter.Otra(78));                      // escape +100%
            Assert.Equal(18, fighter.Otra(82));                      // AP removal +80% (Volonté)
            Assert.Equal(15, fighter.Otra(83));                      // MP removal +50%
            Assert.Equal(15, fighter.NeutralResPct);                 // +5% resistance (Armure)
            Assert.Equal(5, fighter.FireResPct);
            Assert.Equal(20, fighter.Otra(DamagePercentages.Ranged)); // Sentinelle
            Assert.Equal(25, fighter.Otra(DamagePercentages.Melee));  // Belliciste
            Assert.Equal(20, fighter.Otra(DamagePercentages.Weapons)); // Affûtage
            Assert.Equal(1, fighter.AllSpellsModifier(0, SpellAspect.AlcanceMaximo));
            Assert.Equal(1, fighter.AllSpellsModifier(0, SpellAspect.CastsPerTurnUp));
            Assert.Equal(1, fighter.AllSpellsModifier(161, SpellAspect.AlcanceMaximo));  // Souplesse
            Assert.Equal(1, fighter.AllSpellsModifier(161, SpellAspect.MinRangeDown));
            Assert.Equal(0, fighter.Otra(75));                       // -10% erosion (Longévité)
            Assert.Equal(100, fighter.Otra(158));                    // push damage (Carambolage)
            Assert.Equal(20, fighter.Otra(143));                     // final heals (Endurance)
            Assert.Equal(-100, fighter.CriticalBonus);               // Anti-Meta
            Assert.Equal(100 + 5 + 30, fighter.DamageDealtPercent);  // Volonté and Anti-Meta
            Assert.Equal(3, fighter.Otra(26));                       // two summons more (Narkooleuvre)
            Assert.Equal(1000 + 400 + 250, fighter.MaxHP);           // +40% (Endurance) +25% (Armure)
        }

        /// <summary>
        /// What a fighter carries for every spell reaches the cast: its range, its minimum, its
        /// casts per turn and per target, its interval -- and the weapon's its own.
        /// </summary>
        [Fact]
        public void Every_spell_modifiers_reach_the_cast()
        {
            var fighter = new Fighter();
            fighter.EverySpell[SpellAspect.AlcanceMaximo] = 1;
            fighter.EverySpell[SpellAspect.MinRangeDown] = 1;
            fighter.EverySpell[SpellAspect.CastsPerTurnUp] = 1;
            fighter.EverySpell[SpellAspect.CastsPerTargetUp] = 1;
            fighter.EverySpell[SpellAspect.CastIntervalDown] = 1;
            fighter.WeaponModifiers[SpellAspect.AlcanceMaximo] = 2;

            Assert.Equal((1, 7), SpellModifiers.Range(fighter, 161, 2, 6, 1));
            Assert.Equal((1, 3), SpellModifiers.Range(fighter, 0, 1, 1, 1));
            Assert.Equal(3, SpellModifiers.CastsPerTurn(fighter, 161, 2, 1));
            Assert.Equal(0, SpellModifiers.CastsPerTurn(fighter, 161, 0, 1));
            Assert.Equal(2, SpellModifiers.CastsPerTarget(fighter, 161, 1, 1));
            Assert.Equal(2, SpellModifiers.CastInterval(fighter, 161, 3, 1));
            Assert.Equal(1, SpellModifiers.Total(fighter, 161, SpellAspect.AlcanceMaximo, 1));
            Assert.True(SpellModifiers.Holds(fighter, 161, SpellAspect.AlcanceMaximo, 1));
        }

        /// <summary>
        /// The fight start tells the client of a dream's "+1 alcance máximo" the real server's way:
        /// one hnd per spell, the weapon left out -- the long capture's frame 4391, byte for byte.
        /// </summary>
        [Fact]
        public async Task The_every_spell_hnd_is_the_capture_s()
        {
            var fighter = new Fighter { Id = 870000000004 };
            fighter.EverySpell[SpellAspect.AlcanceMaximo] = 1;
            fighter.EverySpell[SpellAspect.CastIntervalDown] = 1;
            var sent = new List<Jondo.Unity.Protocol.Game.ApplySpellModifierEvent>();
            await FightHandler.SendEverySpellModifiersAsync(p => { sent.Add(p); return Task.CompletedTask; },
                                                            new FightInstance(1, 1), fighter, new[] { 0, 368, 24016 });

            Assert.Equal(2, sent.Count);
            Assert.Equal(new[] { 368, 24016 }, sent.Select(e => e.Modifier!.SpellId));
            Assert.All(sent, e =>
            {
                Assert.Equal(fighter.Id, e.ActorId);
                Assert.Equal(Jondo.Unity.Protocol.Game.SpellModifierType.RangeMax, e.Modifier!.ModifierType);
                Assert.Equal(1, e.Modifier.Context);
            });
        }

        /// <summary>Vent Arrière: the dream's side plays first, whatever the monsters' initiative.</summary>
        [Fact]
        public void Vent_Arriere_gives_the_initiative()
        {
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(new Fighter { Id = 1, MaxHP = 10, CurrentHP = 10, Initiative = 10 });
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MaxHP = 10, CurrentHP = 10, Initiative = 900 });
            Assert.Equal(-1, fight.BuildAlternatingTurnOrder()[0].Id);

            fight.BlueTakesInitiative = true;
            Assert.Equal(1, fight.BuildAlternatingTurnOrder()[0].Id);
        }

        /// <summary>The two passives whose spell does nothing are known by their special.</summary>
        [Fact]
        public void The_special_passives_are_known()
        {
            var dream = New();
            Assert.False(DreamCatalogue.Holds(dream, DreamCatalogue.AlliesPlayFirst));
            foreach (var bonus in DreamCatalogue.RewardOf(148)!.Bonuses.Concat(DreamCatalogue.RewardOf(160)!.Bonuses))
                Dreams.Gain(dream, bonus);
            Assert.True(DreamCatalogue.Holds(dream, DreamCatalogue.AlliesPlayFirst));
            Assert.True(DreamCatalogue.Holds(dream, DreamCatalogue.PlaceAnywhere));
        }
    }
}
