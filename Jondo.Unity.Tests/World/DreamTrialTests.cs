using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Infinite Dreams' trials (content/dreams/trials.json): each at its own difficulty, and
    /// what each does to the dream's fights -- REVERSED's adventurers' scraps in the rooms and a
    /// guardian chosen to fight as, FONSOCAC's doubled monsters, NILEZAFF's Cohobación on everybody,
    /// SINJSONJ's Simueño.
    /// </summary>
    [Collection("MapManager")]
    public class DreamTrialTests
    {
        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private const int Simueno = 8264, GuardianScraps = 8211, Cohobacion = 31891, GuardianChoice = 33;

        [Fact]
        public void The_five_trials_are_read_and_each_is_played_at_its_difficulty()
        {
            Assert.Equal(new[] { "FONSOCAC", "REVERSED", "NILEZAFF", "SINJSONJ", "DOFOUSSE" },
                         DreamTrials.All.Select(t => t.Seed));
            Assert.Equal(8, DreamTrials.BySeed("reversed")!.Difficulty);
            Assert.Null(DreamTrials.BySeed("AAAAAAAA"));

            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var trial = Dreams.Crear(1, "Prueba", 200, 2, 100, 200, seed: "NILEZAFF");
            Assert.Equal(5, trial.Dificultad);
            Assert.Equal(17, DreamTrials.Of(trial)!.Id);
            Assert.Null(DreamTrials.Of(Dreams.Crear(2, "Otro", 200, 2, 100, 200)));
            Dreams.OlvidarTodo();
        }

        [Fact]
        public void Reversed_rooms_hold_the_adventurers_scraps()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(3, "Prueba", 200, 8, 100, 200, seed: "REVERSED");
            var trial = DreamTrials.Of(dream)!;
            var allowed = trial.Enemies.Concat(trial.RareEnemies).Concat(trial.LateEnemies).ToHashSet();

            var members = dream.Salas.SelectMany(s => s.Miembros).ToList();
            if (members.Count == 0) return;   // no world groups in this base
            Assert.All(members, m => Assert.Contains(m.Monstruo, allowed));
            Assert.All(Dreams.FinalWave(1, new System.Random(1), trial), m => Assert.Contains(m.Monstruo, allowed));
            Dreams.OlvidarTodo();
        }

        [Fact]
        public void Fonsocac_doubles_the_monsters_and_nilezaff_gives_everybody_its_cohobacion()
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var fonsocac = Dreams.Crear(4, "Prueba", 200, 2, 100, 200, seed: Dreams.FonsocacSeed);
            var monster = new Fighter { IsMonster = true, MaxHP = 200, CurrentHP = 200 };
            Dreams.AsTrialWants(monster, fonsocac);
            Assert.Equal((400, 400), (monster.MaxHP, monster.CurrentHP));
            var summon = new Fighter { IsMonster = true, MaxHP = 100, CurrentHP = 100 };
            Dreams.AsTrialWants(summon, fonsocac, monstersSide: false);
            Assert.Equal(100, summon.MaxHP);   // a player's summon keeps its life

            var nilezaff = Dreams.Crear(5, "Prueba", 200, 2, 100, 200, seed: "NILEZAFF");
            var player = new Fighter { MaxHP = 300, CurrentHP = 300 };
            var enemy = new Fighter { IsMonster = true, MaxHP = 300, CurrentHP = 300 };
            Dreams.AsTrialWants(player, nilezaff, monstersSide: false);
            Dreams.AsTrialWants(enemy, nilezaff);
            Assert.Contains(Cohobacion, player.Buffs.Actitudes);
            Assert.Contains(Cohobacion, enemy.Buffs.Actitudes);
            Assert.Equal(300, enemy.MaxHP);
            Dreams.OlvidarTodo();
        }

        [Fact]
        public void A_player_fights_as_simueno_with_its_sheet_spells_look_and_start()
        {
            var player = new Fighter { Id = 9_100_000_001, Name = "Sinj", MaxHP = 3000, CurrentHP = 3000, MaxAP = 11, MaxMP = 6, Power = 120, Range = 2 };
            if (!FightHandler.Incarnate(player, Simueno, 200)) return;   // no monster data in this base

            Assert.Equal(Simueno, player.IncarnatedMonster);
            Assert.Equal((8200, 8200, 12, 5), (player.MaxHP, player.CurrentHP, player.MaxAP, player.MaxMP));
            Assert.Equal((1500, 1500, 0, 0), (player.Strength, player.Agility, player.Power, player.Range));
            Assert.Equal("{3170|||110}", player.IncarnationLook);
            Assert.Contains(player.IncarnationSpells, s => s.Spell == 31901);   // Simueñería
            Assert.Equal(31912, player.Conducta.Spell);                          // summons its Monórico

            // Brought to a dream's level as its monsters are: the characteristics with the level.
            var higher = new Fighter { Id = 9_100_000_002, Name = "Sinj" };
            Assert.True(FightHandler.Incarnate(higher, Simueno, 300));
            Assert.Equal(2250, higher.Strength);
            Assert.True(higher.MaxHP > 8200);
        }

        [Fact]
        public void A_guardian_s_look_is_the_monster_whose_spells_its_choice_gives()
        {
            // «Retazos de Guardián» grade 1, the Jalató Real: Cura Jalató, Mordedura de Jalató Real
            // and Coraza Lanuda, the Jalató Real's (147) three.
            string? look = FightHandler.LookOfTheSpells(31795, 1);
            if (look == null) return;   // no spell data in this base
            Assert.Equal("{571|||100}", look);
        }

        [Fact]
        public void Effect_3008_proposes_its_choice_to_a_character_only()
        {
            var fight = new FightInstance(9_100_100, 191105026);
            var player = new Fighter { Id = 9_100_000_011, CellId = 200, MaxHP = 100, CurrentHP = 100 };
            var monster = new Fighter { Id = -1, TeamId = 1, CellId = 228, MaxHP = 100, CurrentHP = 100, IsMonster = true };
            fight.AddPlayer(player);
            fight.AddMonster(monster);
            var choose = new SpellEffect { EffectId = EffectSupport.ProposesChoice, Value = GuardianChoice, TargetMask = "C", Triggers = "I" };

            var outcomes = EffectEngine.ResolveEffects(fight, player, 31796, 1, player, EffectEngine.AlLanzar, 1, new[] { choose });
            Assert.Equal(GuardianChoice, Assert.Single(outcomes).Choice);
            Assert.Empty(EffectEngine.ResolveEffects(fight, monster, 31796, 1, monster, EffectEngine.AlLanzar, 1, new[] { choose }));
        }

        /// <summary>
        /// REVERSED end to end: fighting as the «Retazos de guardián», its start proposes choice 33
        /// (kss, three of its ten guardians); the kso's option is cast on him -- the guardian's spells
        /// for the fight, its look, and no weapon on his bar.
        /// </summary>
        [Fact]
        public async Task The_guardian_chosen_gives_its_spells_and_its_look()
        {
            const long who = 9_100_000_021;
            const long map = 191105026;
            Interactives.Initialize();
            var player = new Fighter { Id = who, Name = "Guardian", TeamId = 0, CellId = 200, Level = 200, MaxHP = 1000, CurrentHP = 1000, MaxAP = 6, CurrentAP = 6 };
            if (!FightHandler.Incarnate(player, GuardianScraps, 200) || player.Conducta.Spell == 0) return;
            if (TravelData.ChoiceOf(GuardianChoice) == null) return;

            await using var pipe = await ClientPipe.OpenAsync(who, who, map, "Guardian");
            var fight = new FightInstance(who, map);
            fight.AddPlayer(player);
            fight.AddMonster(new Fighter { Id = -1, TeamId = 1, CellId = 400, MaxHP = 4000, CurrentHP = 4000, IsMonster = true, MonsterId = 31, Level = 100 });
            Active[fight.FightId] = fight;
            try
            {
                fight.StartFight();
                using (SessionContext.Push(pipe.Session)) await FightHandler.LanzarLaConductaAsync(pipe.ToClient, fight, player);

                var kss = ProtoMessage.Parse(await UntilAsync(pipe, Op.Kss));
                Assert.Equal(GuardianChoice, (int)kss.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
                Assert.Equal(FightHandler.ChoiceOptionsShown, kss.Fields.Count(f => f.FieldNumber == 1));
                Assert.True(FightHandler.WaitsForChoice(who));

                using (SessionContext.Push(pipe.Session))
                    Assert.True(await FightHandler.AnswerChoiceAsync(pipe.Session, 1));
                Assert.False(FightHandler.WaitsForChoice(who));
                Assert.NotEmpty(player.GrantedSpells);
                Assert.Contains(31795, player.Buffs.Actitudes);
                Assert.NotEqual("{4933}", player.IncarnationLook);

                using (SessionContext.Push(pipe.Session))
                {
                    var layout = FightSpellLayout.ForFight(1, 200, who);
                    Assert.False(layout.WithWeapon);
                    Assert.Equal(player.GrantedSpells.Select(g => g.Spell).OrderBy(s => s), layout.Spells.Select(s => s.Spell).OrderBy(s => s));
                }
            }
            finally
            {
                Active.TryRemove(fight.FightId, out _);
            }
        }

        /// <summary>The next frame of that opcode, the others let by.</summary>
        private static async Task<byte[]> UntilAsync(ClientPipe pipe, string opcode)
        {
            for (int i = 0; i < 200; i++)
            {
                var (got, payload) = await pipe.NextAny();
                if (got == opcode) return payload;
            }
            Assert.Fail(opcode + " never came");
            return null!;
        }
    }
}
