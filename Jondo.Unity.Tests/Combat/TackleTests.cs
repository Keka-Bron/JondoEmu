using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Tackle and escape: the figure, the frames, who holds whom, the walk that pays it and the
    /// monsters that plan around it. Every number comes from a capture; see Tackle.cs.
    /// </summary>
    public class TackleTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        // ─── The figure ──────────────────────────────────────────────────────────────────────

        /// <summary>The seven tackles of the captures: escape, tackle, AP and MP held, AP and MP lost.</summary>
        [Theory]
        [InlineData(0, 0, 6, 3, 3, 2)]      // eleccion personaje-carga world-tutorial, frame 478
        [InlineData(0, 0, 2, 4, 1, 2)]      // the same tutorial, frame 2439
        [InlineData(75, 89, 2, 2, 1, 1)]    // aceptar desafio-combate completo, frame 1600
        [InlineData(10, 5, 7, 3, 1, 0)]     // entrar a combate-desconectarse-reconectar, frame 2007
        [InlineData(96, 60, 10, 2, 2, 0)]   // atacar a recaudador y perder, frame 367
        [InlineData(66, 98, 1, 6, 1, 4)]    // ruta muy larga zonas pandala-sidimote, frame 2355
        [InlineData(80, 60, 8, 5, 3, 2)]    // submarino steamer-...-dopeul-perder, frame 1241
        public void The_seven_tackles_of_the_captures(int escape, int tackle, int ap, int mp, int apLost, int mpLost)
        {
            var loss = Tackle.Resolve(escape, new[] { tackle }, ap, mp);

            Assert.Equal(apLost, loss.ActionPoints);
            Assert.Equal(mpLost, loss.MovementPoints);
        }

        /// <summary>
        /// The tutorial's three MP at a half: 1.5 is LOST as 2. Rounding what he keeps would have
        /// left him two, and he walked one.
        /// </summary>
        [Fact]
        public void A_half_is_lost_whole()
        {
            Assert.Equal(0.5, Tackle.KeptShare(0, new[] { 0 }));
            Assert.Equal(2, Tackle.PointsLost(3, 0, new[] { 0 }));
            Assert.Equal(3, Tackle.PointsLost(6, 0, new[] { 0 }));
        }

        /// <summary>
        /// Walks out of contact that cost nothing in the captures: an escape of 69 against a tackle
        /// of 0, and of 80 against 10. Keeping more than everything is keeping everything.
        /// </summary>
        [Theory]
        [InlineData(69, 0, 13, 5)]
        [InlineData(80, 10, 12, 5)]
        public void Enough_escape_keeps_everything(int escape, int tackle, int ap, int mp)
        {
            Assert.Equal(1.0, Tackle.KeptShare(escape, new[] { tackle }));
            Assert.False(Tackle.Resolve(escape, new[] { tackle }, ap, mp).Any);
        }

        /// <summary>
        /// Nobody around, nothing lost; and a negative escape counts as none -- inferred: the share
        /// would turn negative below -2, and no capture holds one.
        /// </summary>
        [Fact]
        public void No_tackler_and_negative_escape()
        {
            Assert.False(Tackle.Resolve(0, Array.Empty<int>(), 6, 3).Any);
            Assert.Equal(Tackle.Resolve(0, new[] { 0 }, 6, 3), Tackle.Resolve(-32, new[] { 0 }, 6, 3));
        }

        /// <summary>Two tacklers: each keeps his share of what is left -- inferred, no capture has two.</summary>
        [Fact]
        public void Two_tacklers_multiply()
        {
            Assert.Equal(0.25, Tackle.KeptShare(0, new[] { 0, 0 }));
            Assert.Equal(3, Tackle.PointsLost(4, 0, new[] { 0, 0 }));
        }

        // ─── The frames ──────────────────────────────────────────────────────────────────────

        private const long TutorialPlayer = 80259055967;

        [Fact]
        public void The_tackle_frame_is_the_capture_s()
        {
            // Tutorial 478: the player held by the monster -1.
            Assert.Equal(Hex("18df82c0feaa025a0c0a0affffffffffffffffff017068"),
                         TackleProtocol.BuildTackled(TutorialPlayer, new long[] { -1 }));
            // Collector 367: the collector -1 held by the summon -2.
            Assert.Equal(Hex("18ffffffffffffffffff015a0c0a0afeffffffffffffffff017068"),
                         TackleProtocol.BuildTackled(-1, new long[] { -2 }));
            // Challenge 1600: the summon -1 held by a player.
            Assert.Equal(Hex("18ffffffffffffffffff015a080a06a282f0a6c4087068"),
                         TackleProtocol.BuildTackled(-1, new long[] { 293213045026 }));
        }

        [Fact]
        public void The_losses_are_the_catalogue_s_rows_in_his_own_name()
        {
            // Tutorial 482 and 486: -3 AP and -2 MP, author and victim the player himself.
            Assert.Equal(Hex("18df82c0feaa027065a2011208fdffffffffffffffff0110df82c0feaa02"),
                         FightProtocol.BuildPointsLost(TutorialPlayer, FightProtocol.ActionPointsLost, TutorialPlayer, 3));
            Assert.Equal(Hex("18df82c0feaa02707fa2011208feffffffffffffffff0110df82c0feaa02"),
                         FightProtocol.BuildPointsLost(TutorialPlayer, FightProtocol.MovementPointsLost, TutorialPlayer, 2));
        }

        /// <summary>The jsj faces the way its last step goes, in the nine jsj around the tackles.</summary>
        [Theory]
        [InlineData(301, 315, 3)]
        [InlineData(343, 357, 1)]
        [InlineData(131, 145, 3)]
        [InlineData(145, 158, 3)]
        [InlineData(12, 26, 1)]
        [InlineData(26, 40, 3)]
        [InlineData(383, 369, 5)]
        [InlineData(354, 340, 5)]
        [InlineData(328, 343, 1)]
        [InlineData(273, 288, 1)]
        public void A_path_faces_its_last_step(int from, int to, int orientation)
        {
            Assert.Equal(orientation, FightHandler.StepOrientation(from, to));
        }

        // ─── Who holds whom ──────────────────────────────────────────────────────────────────

        private static Fighter Person(long id, int cell, int ap = 6, int mp = 3)
            => new Fighter { Id = id, Name = "P" + id, CellId = cell, Level = 200, MaxHP = 1000, CurrentHP = 1000,
                             MaxAP = ap, CurrentAP = ap, MaxMP = mp, CurrentMP = mp };

        private static Fighter Beast(long id, int cell, int tackle = 0)
        {
            var monster = new Fighter { Id = id, Name = "M" + id, IsMonster = true, CellId = cell, Level = 50,
                                        MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };
            monster.Otras[Tackle.TackleCharacteristic] = tackle;
            return monster;
        }

        /// <summary>A fight with the player on the blue side and the monsters on the red, where the test puts them.</summary>
        private static FightInstance Board(Fighter player, params Fighter[] monsters)
        {
            var fight = new FightInstance(4104, 0, 0);
            int cell = player.CellId;
            fight.AddPlayer(player);
            player.CellId = cell;
            foreach (var monster in monsters)
            {
                int where = monster.CellId;
                fight.AddMonster(monster);
                monster.CellId = where;
            }
            return fight;
        }

        // 301 is on an odd row: its four neighbours are 287, 288, 315 and 316.
        private const int Start = 301;
        private const int Beside = 287;

        [Fact]
        public void Only_the_four_cells_around_hold()
        {
            var player = Person(1, Start);
            var near = Beast(-1, Beside);
            var far = Beast(-2, 273);
            var fight = Board(player, near, far);

            var (tacklers, loss) = FightHandler.TackleAt(fight, player, Start, 6, 3);

            Assert.Equal(new[] { near }, tacklers);
            Assert.Equal(new Tackle.Loss(3, 2), loss);
        }

        [Fact]
        public void A_template_without_the_bit_holds_nobody()
        {
            // The training dummy 494 has no CanTackle bit; an ordinary monster, 492, has it.
            Assert.False(MonsterFlags.AllowsTackle(494));
            Assert.True(MonsterFlags.AllowsTackle(492));
            Assert.True(MonsterFlags.AllowsTackle(5192));

            var player = Person(1, Start);
            var dummy = Beast(-1, Beside);
            dummy.TemplateAllowsTackle = false;
            var fight = Board(player, dummy);

            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
        }

        [Fact]
        public void The_states_and_the_invisible_hold_nobody_and_are_not_held()
        {
            // No Placable (96) on the walker, Imbloqueante (95) on the tackler: the client's flags.
            var player = Person(1, Start);
            var monster = Beast(-1, Beside);
            var fight = Board(player, monster);

            player.Buffs.PonerEstado(96);
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
            player.Buffs.QuitarEstado(96);
            Assert.True(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);

            monster.Buffs.PonerEstado(95);
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
            monster.Buffs.QuitarEstado(95);

            // Hidden among his copies: neither holds nor is held.
            monster.HiddenAmongCopies = true;
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
            monster.HiddenAmongCopies = false;
            player.HiddenAmongCopies = true;
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
            player.HiddenAmongCopies = false;

            // The dead hold nobody, and neither does one being carried.
            monster.CurrentHP = 0;
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
            monster.CurrentHP = 500;
            monster.CarriedBy = 7;
            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
        }

        /// <summary>Allies hold nobody: the Ocra's own summon beside him costs him nothing.</summary>
        [Fact]
        public void Allies_do_not_hold()
        {
            var player = Person(1, Start);
            var monster = Beast(-1, 273);
            var fight = Board(player, monster);
            var summon = Beast(-2, Beside);
            fight.Invocar(summon, player);
            summon.CellId = Beside;

            Assert.False(FightHandler.TackleAt(fight, player, Start, 6, 3).Loss.Any);
        }

        /// <summary>The escape and the tackle a monster fights with: a tenth of its agility and its own bonus.</summary>
        [Fact]
        public void A_monster_s_sheet_is_a_tenth_of_its_agility()
        {
            var monster = new Fighter();
            FightHandler.SetTackleSheet(monster, 52, 0, 0);   // 492 grade 3: 5 and 5
            Assert.Equal(5, monster.Otra(Tackle.EscapeCharacteristic));
            Assert.Equal(5, monster.Otra(Tackle.TackleCharacteristic));

            FightHandler.SetTackleSheet(monster, 800, 3, 7);
            Assert.Equal(83, monster.Otra(Tackle.EscapeCharacteristic));
            Assert.Equal(87, monster.Otra(Tackle.TackleCharacteristic));
        }

        // ─── The walk ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The tutorial's tackled walk, frames 477-492, frame by frame: the 104, the AP and the MP
        /// lost -- each behind its sheet --, then the one step left, its sheet and its 129. The
        /// frames whose content this server writes its own way (the sheets and the sequence
        /// numbers) are checked for what they are; the rest byte for byte.
        /// </summary>
        [Fact]
        public async Task The_tutorial_s_tackled_walk_is_its_capture()
        {
            var player = Person(TutorialPlayer, Start, ap: 6, mp: 3);
            var monster = Beast(-1, Beside);
            var fight = Board(player, monster);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            GameSession? session = null;
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using var server = await listener.AcceptTcpClientAsync();
                var fromServer = client.GetStream();

                session = new GameSession(server.GetStream());
                session.BindAccount(8_900_000_001, 1);
                session.State.CharacterId = TutorialPlayer;
                session.EnterWorld();
                Assert.True(SessionRegistry.Register(session));

                var walked = await FightHandler.WalkPathAsync(fight, player, new[] { Start, 315 });

                Assert.Equal(new[] { Start, 315 }, walked);
                Assert.Equal(315, player.CellId);
                Assert.Equal(3, player.CurrentAP);
                Assert.Equal(0, player.CurrentMP);

                Assert.Equal(Hex("08df82c0feaa021004"), await Next(fromServer, Op.Jto));                        // 477
                Assert.Equal(Hex("18df82c0feaa025a0c0a0affffffffffffffffff017068"), await Next(fromServer, Op.Jwe)); // 478
                Assert.Equal(Hex("08df82c0feaa021003"), await Next(fromServer, Op.Jto));                        // 479
                await Next(fromServer, Op.Jxw);                                                                  // 480
                SheetClose(await Next(fromServer, Op.Jwi));                                                      // 481
                Assert.Equal(Hex("18df82c0feaa027065a2011208fdffffffffffffffff0110df82c0feaa02"),
                             await Next(fromServer, Op.Jwe));                                                    // 482
                Assert.Equal(Hex("08df82c0feaa021003"), await Next(fromServer, Op.Jto));                        // 483
                await Next(fromServer, Op.Jxw);                                                                  // 484
                SheetClose(await Next(fromServer, Op.Jwi));                                                      // 485
                Assert.Equal(Hex("18df82c0feaa02707fa2011208feffffffffffffffff0110df82c0feaa02"),
                             await Next(fromServer, Op.Jwe));                                                    // 486
                Assert.Equal(Hex("0a04ad02bb02100328df82c0feaa02"), await Next(fromServer, Op.Jsj));            // 487
                Assert.Equal(Hex("08df82c0feaa021003"), await Next(fromServer, Op.Jto));                        // 488
                await Next(fromServer, Op.Jxw);                                                                  // 489
                SheetClose(await Next(fromServer, Op.Jwi));                                                      // 490
                Assert.Equal(Hex("18df82c0feaa02708101a2011208ffffffffffffffffff0110df82c0feaa02"),
                             await Next(fromServer, Op.Jwe));                                                    // 491
                var close = ProtoMessage.Parse(await Next(fromServer, Op.Jwi)).Fields;                           // 492
                Assert.Equal(TutorialPlayer, (long)close.Single(f => f.FieldNumber == 2).VarIntValue);
                Assert.Equal(FightProtocol.WalkSequence, (int)close.Single(f => f.FieldNumber == 3).VarIntValue);
            }
            finally
            {
                if (session != null) SessionRegistry.Unregister(session);
                listener.Stop();
            }
        }

        private static void SheetClose(byte[] jwi)
        {
            var fields = ProtoMessage.Parse(jwi).Fields;
            Assert.Equal(TutorialPlayer, (long)fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(FightProtocol.SheetSequence, (int)fields.Single(f => f.FieldNumber == 3).VarIntValue);
        }

        private static async Task<byte[]> Next(System.IO.Stream stream, string opcode)
        {
            var read = Jondo.Protocol.NetworkMessage.ReadFrameAsync(stream);
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(first == read, $"nothing arrived where {opcode} was expected");
            byte[] frame = await read;
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, opcode);
            Assert.True(payload != null, $"{opcode} was expected and {NetworkEnvelope.GetMessageTypeUrl(frame)} came");
            return payload!;
        }

        /// <summary>
        /// The collector's walk, frames 361-377: five cells into the contact of a summon, the
        /// tackle paid THERE -- two AP of ten, no MP of the two left --, and the last cell after.
        /// </summary>
        [Fact]
        public async Task A_path_pays_where_it_meets_the_enemy()
        {
            var collector = Beast(-1, 74);
            collector.MaxAP = collector.CurrentAP = 10;
            collector.MaxMP = collector.CurrentMP = 7;
            collector.Otras[Tackle.EscapeCharacteristic] = 96;
            var player = Person(1, 400);
            var fight = Board(player, collector);
            // The summon beside 145 and not beside any earlier cell of the path.
            var summon = Beast(-2, 130, tackle: 60);
            fight.Invocar(summon, player);
            summon.CellId = 130;

            var path = new[] { 74, 89, 103, 118, 131, 145, 158 };
            Assert.Contains(130, MapGeometry.GetNeighbors(145));
            Assert.All(path.Take(5), c => Assert.DoesNotContain(130, MapGeometry.GetNeighbors(c)));

            var walked = await FightHandler.WalkPathAsync(fight, collector, path);

            Assert.Equal(path, walked);
            Assert.Equal(8, collector.CurrentAP);
            Assert.Equal(1, collector.CurrentMP);
        }

        /// <summary>
        /// A tackle that leaves fewer MP than the path asks for stops the walker where they run
        /// out -- inferred, no capture asks for more than it keeps.
        /// </summary>
        [Fact]
        public async Task A_tackle_cuts_the_path_short()
        {
            var player = Person(1, Start, ap: 6, mp: 3);
            var monster = Beast(-1, Beside, tackle: 200);
            var fight = Board(player, monster);

            // 2 / (2 × 202) kept: all three MP lost but none, so no step at all.
            var walked = await FightHandler.WalkPathAsync(fight, player, new[] { Start, 315, 329 });

            Assert.Equal(new[] { Start }, walked);
            Assert.Equal(Start, player.CellId);
            Assert.Equal(0, player.CurrentMP);
            Assert.Equal(0, player.CurrentAP);
        }

        [Fact]
        public async Task A_walk_with_nobody_around_costs_its_steps_only()
        {
            var player = Person(1, Start, ap: 6, mp: 3);
            var fight = Board(player, Beast(-1, 100));

            var walked = await FightHandler.WalkPathAsync(fight, player, new[] { Start, 315, 329 });

            Assert.Equal(new[] { Start, 315, 329 }, walked);
            Assert.Equal(1, player.CurrentMP);
            Assert.Equal(6, player.CurrentAP);
        }

        /// <summary>The MP a tackle takes are not MP the Zombi challenge counts as used.</summary>
        [Fact]
        public async Task The_zombi_does_not_count_a_tackle()
        {
            var player = Person(1, Start, ap: 6, mp: 3);
            var monster = Beast(-1, Beside);
            var fight = Board(player, monster);
            fight.StartFight();
            Assert.Same(player, fight.CurrentFighter);
            ChallengeWatcher.TurnStarted(fight, player);

            await FightHandler.WalkPathAsync(fight, player, new[] { Start, 315 });

            Assert.Equal(2, fight.TurnTackledMp);
            Assert.Equal(1, fight.TurnStartMp - player.CurrentMP - fight.TurnTackledMp);
        }

        // ─── The monsters plan around it ─────────────────────────────────────────────────────

        private static MonsterTactics.Board TacticsBoard(FightInstance fight)
            => new MonsterTactics.Board
            {
                Fighters = fight.Todos.ToList(),
                TackleAt = (mover, cell, ap, mp) => FightHandler.TackleAt(fight, mover, cell, ap, mp).Loss,
            };

        /// <summary>
        /// Held in melee, a monster with three MP and no escape reaches one cell away and no
        /// further: leaving the contact costs it two of them.
        /// </summary>
        [Fact]
        public void A_held_monster_plans_with_what_the_tackle_leaves()
        {
            var monster = Beast(-1, Start);
            var player = Person(1, Beside);
            var fight = Board(player, monster);
            var board = TacticsBoard(fight);

            var routes = MonsterTactics.Routes(board, monster);

            Assert.All(routes.Values, r => Assert.True(r.Path.Count <= 2));
            var away = routes[315];
            Assert.Equal(new List<int> { Start, 315 }, away.Path);
            Assert.Equal(3, away.ActionPoints);
            Assert.Equal(0, away.MovementPoints);

            // Without anybody holding it the same monster goes three cells out.
            var free = MonsterTactics.Routes(new MonsterTactics.Board { Fighters = fight.Todos.ToList() }, monster);
            Assert.Contains(free.Values, r => r.Path.Count == 4);
        }

        /// <summary>
        /// A spell it could reach by walking away is no plan when the tackle takes the AP to pay
        /// for it: four AP asked, three left after the tackle.
        /// </summary>
        [Fact]
        public void A_held_monster_does_not_plan_a_cast_the_tackle_leaves_it_unable_to_pay()
        {
            var monster = Beast(-1, Start);
            var player = Person(1, Beside);
            var fight = Board(player, monster);
            var ranged = new MonsterTactics.Spell { Id = 1, Grade = 1, Cost = 4, MinRange = 2, MaxRange = 2, Damage = 100 };

            Assert.Null(MonsterTactics.Next(TacticsBoard(fight), monster, new[] { ranged }));

            // A spell it can pay after the tackle is still planned from out of the contact.
            var cheap = new MonsterTactics.Spell { Id = 2, Grade = 1, Cost = 3, MinRange = 2, MaxRange = 2, Damage = 100 };
            var action = MonsterTactics.Next(TacticsBoard(fight), monster, new[] { cheap });
            Assert.NotNull(action);
            Assert.Equal(2, MapGeometry.Distance(action!.From, player.CellId));
        }
    }
}
