using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The dream spells cast from the dream's side bar, through the same path a client's cast
    /// takes (jwh): at the grade their reward taught, by the dream's people, with their global
    /// cooldown and their charges.
    /// </summary>
    [Collection("MapManager")]
    public class DreamSpellTests
    {
        private const long RoomMap = 237777922;
        private const int PsstPsst = 32424, PalmaAncestral = 32517, Absolucion = 32008, Oblivion = 31745, DomiReversi = 31791;

        private static readonly ConcurrentDictionary<long, FightInstance> Active =
            (ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private sealed class Scene : IAsyncDisposable
        {
            public ClientPipe Pipe = null!;
            public Dreams.Sueno Dream = null!;
            public FightInstance Fight = null!;
            public Fighter Me = null!, Ally = null!, Mob = null!;

            public async Task CastAsync(int spell, int cell)
            {
                using (SessionContext.Push(Pipe.Session))
                    await FightHandler.CastAsync(Pipe.ToClient, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build()));
            }

            public async ValueTask DisposeAsync()
            {
                Active.TryRemove(Fight.FightId, out _);
                Dreams.Olvidar(Me.Id);
                await Pipe.DisposeAsync();
            }
        }

        /// <summary>A dream on a room map, its owner and an ally in a fight there against one monster; the dream holds <paramref name="held"/>.</summary>
        private static async Task<Scene?> SetUpAsync(long who, int held = 0, int charges = 1)
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Interactives.Initialize();
            Dreams.Initialize();
            if (!MapManager.WalkableCells.TryGetValue(RoomMap, out var walk) || !Dreams.IsDreamMap(RoomMap)) return null;

            var dream = Dreams.Crear(who, "Soñador", 200, 5, 0, 0, 1);
            dream.SalaActual!.MapaDeLaSala = RoomMap;
            if (held != 0) dream.Ganados.Add(new Dreams.Bono(DreamSpells.LearnEffect, GradeOneOf(held), false, charges));

            var cells = walk.OrderBy(c => c).ToList();
            var scene = new Scene { Dream = dream };
            scene.Pipe = await ClientPipe.OpenAsync(who, who, RoomMap, "Soñador");
            scene.Pipe.Session.State.CharacterLevel = 200;
            scene.Fight = new FightInstance(who, RoomMap);
            scene.Me = new Fighter { Id = who, TeamId = 0, CellId = cells[cells.Count / 2], MaxHP = 5000, CurrentHP = 5000, MaxAP = 6, CurrentAP = 6, Level = 200, Initiative = 9999 };
            scene.Ally = new Fighter { Id = who + 1, TeamId = 0, CellId = cells[cells.Count / 2 + 1], MaxHP = 3000, CurrentHP = 3000, MaxAP = 6, CurrentAP = 6, Level = 200, Initiative = 10 };
            scene.Mob = new Fighter { Id = -1, TeamId = 1, CellId = cells[cells.Count / 2 + 30], MaxHP = 4000, CurrentHP = 4000, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 31, Level = 100, Initiative = 1 };
            scene.Fight.AddPlayer(scene.Me);
            scene.Fight.AddPlayer(scene.Ally);
            scene.Fight.AddMonster(scene.Mob);
            Active[scene.Fight.FightId] = scene.Fight;
            scene.Fight.StartFight();
            return scene;
        }

        private static int GradeOneOf(int spell)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM SpellLevels WHERE SpellId = $s AND Grade = 1;";
            command.Parameters.AddWithValue("$s", spell);
            return (int)(long)command.ExecuteScalar()!;
        }

        /// <summary>
        /// Psst Psst at its grade 1: the target walks up to the caster, the AP comes back, and the
        /// ally waits the same cooldown. At its grade 3 -- what the level lookup took -- it only
        /// removed its own effects, and did nothing.
        /// </summary>
        [Fact]
        public async Task A_dream_spell_is_cast_at_the_grade_its_reward_taught()
        {
            await using var scene = await SetUpAsync(8_996_001, PsstPsst);
            if (scene == null) return;
            int from = scene.Mob.CellId;

            await scene.CastAsync(PsstPsst, scene.Mob.CellId);

            Assert.NotEqual(from, scene.Mob.CellId);
            Assert.Equal(6, scene.Me.CurrentAP);
            Assert.True(scene.Me.Recarga.TryGetValue(PsstPsst, out int mine) && mine > 0);
            Assert.Equal(mine, scene.Ally.Recarga.GetValueOrDefault(PsstPsst));
        }

        /// <summary>Palma Ancestral sets its threshold at 30 % of the life; it killed outright at its grade 2.</summary>
        [Fact]
        public async Task Palma_Ancestral_lays_a_threshold_and_does_not_kill()
        {
            await using var scene = await SetUpAsync(8_996_011, PalmaAncestral);
            if (scene == null) return;

            await scene.CastAsync(PalmaAncestral, scene.Mob.CellId);

            Assert.True(scene.Mob.IsAlive);
            Assert.Equal(4000, scene.Mob.CurrentHP);
            Assert.Contains(scene.Mob.Buffs.Puestos, b => b.EffectId == 2872);
        }

        /// <summary>A dream spell the fight's dream does not hold is not cast: nothing spent, nothing done.</summary>
        [Fact]
        public async Task A_dream_spell_not_held_is_refused()
        {
            await using var scene = await SetUpAsync(8_996_021);
            if (scene == null) return;

            await scene.CastAsync(Oblivion, scene.Mob.CellId);

            Assert.True(scene.Mob.IsAlive);
            Assert.Equal(6, scene.Me.CurrentAP);
            Assert.False(scene.Me.Recarga.ContainsKey(Oblivion));
        }

        /// <summary>Oblivion spends a charge per cast: two become one, and the last takes it off the bar.</summary>
        [Fact]
        public async Task Oblivion_spends_its_charges()
        {
            await using var scene = await SetUpAsync(8_996_031, Oblivion, charges: 2);
            if (scene == null) return;

            await scene.CastAsync(Oblivion, scene.Mob.CellId);
            Assert.False(scene.Mob.IsAlive);
            Assert.Equal(1, DreamSpells.Held(scene.Dream, Oblivion)?.Count);

            Assert.True(DreamSpells.Consume(scene.Dream, Oblivion));
            Assert.Null(DreamSpells.Held(scene.Dream, Oblivion));
            Assert.DoesNotContain(scene.Dream.Ganados, b => b.Efecto == DreamSpells.LearnEffect);
        }

        /// <summary>
        /// Domi Reversi turns the last monster standing to the players' side: the fight is won --
        /// "si l'ennemi ciblé est le seul monstre en vie, vous gagnez automatiquement le combat".
        /// </summary>
        [Fact]
        public async Task Domi_Reversi_on_the_last_monster_wins()
        {
            await using var scene = await SetUpAsync(8_996_041, DomiReversi);
            if (scene == null) return;

            await scene.CastAsync(DomiReversi, scene.Mob.CellId);

            Assert.True(scene.Mob.IsAlive);
            Assert.True(scene.Mob.TurnsOnTheOtherSide > 0);
            Assert.False(scene.Fight.SigueVivo(FightInstance.Rojos));
            Assert.Null(DreamSpells.Held(scene.Dream, DomiReversi));
        }

        /// <summary>Absolución cuts every spell's cooldown of the allies by 12, and waits 12 itself.</summary>
        [Fact]
        public async Task Absolucion_cuts_every_cooldown_of_the_allies()
        {
            await using var scene = await SetUpAsync(8_996_051, Absolucion);
            if (scene == null) return;
            scene.Ally.Recarga[12345] = 5;

            await scene.CastAsync(Absolucion, scene.Me.CellId);

            Assert.Equal(0, scene.Ally.Recarga.GetValueOrDefault(12345));
            Assert.Equal(12, scene.Me.Recarga.GetValueOrDefault(Absolucion));
        }

        /// <summary>
        /// A dream passive travels as the invitation capture's do -- f6 { f2: the spell, f3: 1 } --
        /// and a dream spell with its charges in the f2 the bar writes "x2" from.
        /// </summary>
        [Fact]
        public void Bonuses_have_the_capture_s_shape()
        {
            var dream = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 1 };
            dream.Ganados.Add(new Dreams.Bono(Dreams.PassiveEffect, 31735, true));
            dream.Ganados.Add(new Dreams.Bono(DreamSpells.LearnEffect, 83551, false, 2));
            var bonuses = ProtoMessage.Parse(DreamProtocol.BuildDreamState(dream).Payload()).Fields.Where(f => f.FieldNumber == 15).ToList();

            var passive = ProtoMessage.Parse(bonuses[0].BytesValue);
            var inner = ProtoMessage.Parse(passive.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            var nested = ProtoMessage.Parse(inner.Fields.Single(f => f.FieldNumber == 6).BytesValue);
            Assert.Equal(31735, nested.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(1, nested.Fields.Single(f => f.FieldNumber == 3).VarIntValue);

            var spell = ProtoMessage.Parse(bonuses[1].BytesValue);
            Assert.Equal(2, spell.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
        }

        /// <summary>
        /// Agrupación pulls every allied character up to the caster: its "h" is the caster's
        /// side's characters, and it reached only a summoner.
        /// </summary>
        [Fact]
        public async Task Agrupacion_pulls_the_allies_in()
        {
            await using var scene = await SetUpAsync(8_996_061, 32743);
            if (scene == null) return;
            var walk = MapManager.WalkableCells[RoomMap].OrderBy(c => c).ToList();
            scene.Ally.CellId = walk.First(c => c > scene.Me.CellId + 80);
            int far = Jondo.Unity.World.Maps.MapGeometry.Distance(scene.Me.CellId, scene.Ally.CellId);

            await scene.CastAsync(32743, scene.Me.CellId);

            Assert.True(Jondo.Unity.World.Maps.MapGeometry.Distance(scene.Me.CellId, scene.Ally.CellId) < far);
        }

        /// <summary>The same dream spell taught twice is one entry with two charges.</summary>
        [Fact]
        public void A_dream_spell_taught_twice_stacks()
        {
            var dream = new Dreams.Sueno { CharacterId = 1, Nombre = "a", Dificultad = 1 };
            Dreams.Gain(dream, new Dreams.Bono(DreamSpells.LearnEffect, 83551));
            Dreams.Gain(dream, new Dreams.Bono(DreamSpells.LearnEffect, 83551));
            Assert.Single(dream.Ganados);
            Assert.Equal(2, dream.Ganados[0].Count);
        }
    }
}
