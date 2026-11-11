using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Quests;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The alignment's level and rank, from the quests: each alignment quest raises the level by
    /// one, each order's apprenticeship gives its rank, and leaving the city forgets them.
    /// </summary>
    public sealed class AlignmentProgressionTests
    {
        private static bool Available => File.Exists(Paths.QuestsJson);

        private static Alignments.Progression Index()
        {
            QuestEngine.Load();
            return Alignments.Index();
        }

        [Fact]
        public void Each_alignment_quest_raises_the_level_by_one()
        {
            if (!Available) return;
            var index = Index();
            Assert.Equal(0, index.Level(1, Array.Empty<int>()));
            Assert.Equal(3, index.Level(1, new[] { 55, 56, 57 }));

            // Bonta's quests raise no Brakmarian.
            Assert.Equal(0, index.Level(2, new[] { 55, 56, 57 }));

            // A hundred on each side: the f1 = 100 of the level 200 Bontarian's sheet.
            Assert.Equal(100, index.Level(1, index.Levels.Keys));
            Assert.Equal(100, index.Level(2, index.Levels.Keys));
            Assert.Equal((true, false), Alignments.Moves(55));
            Assert.Equal((false, true), Alignments.Moves(433));
            Assert.Equal((false, false), Alignments.Moves(1680));
        }

        [Theory]
        [InlineData(0, new int[0], 0)]                              // neutral
        [InlineData(1, new int[0], 1)]                              // Néophyte, Bonta's
        [InlineData(2, new int[0], 18)]                             // Néophyte, Brâkmar's
        [InlineData(1, new[] { 433 }, 2)]                           // Disciple de Menalt
        [InlineData(1, new[] { 433, 109 }, 3)]                      // Écuyer
        [InlineData(1, new[] { 433, 109, 120, 421, 1916 }, 6)]      // Héros Légendaire
        [InlineData(1, new[] { 434, 111, 121, 423, 1917 }, 11)]     // Gardien du Savoir
        [InlineData(1, new[] { 435, 110, 122, 425, 1918 }, 17)]     // Maître des Illusions, the capture's hod
        [InlineData(2, new[] { 436, 112, 123, 422, 1919 }, 23)]     // Héros de l'Apocalypse
        [InlineData(2, new[] { 437, 114, 124, 424, 1920 }, 28)]     // Gardien des Tortures
        [InlineData(2, new[] { 438, 113, 125, 426, 1921 }, 33)]     // Maître des Ombres
        [InlineData(1, new[] { 436 }, 1)]                           // the other side's order counts for nothing
        public void Each_apprenticeship_gives_its_rank(int side, int[] finished, int rank)
        {
            if (!Available) return;
            Assert.Equal(rank, Index().Rank(side, finished));
        }

        [Fact]
        public async Task The_level_opens_the_next_quest_and_shows_on_the_sheet()
        {
            if (!Available) return;
            Index();
            const long me = 9_400_300_010;
            Alignments.Set(me, Alignments.Bonta, 0);
            await using var pipe = await ClientPipe.OpenAsync(me, me, 990_100_120, "Milicien");
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.CharacterLevel = 50;
                var log = Log(pipe, 55, 56, 57);
                Assert.Equal(3, Alignments.Level());
                Assert.True(log.CanStart(58, out _));       // Ps=1&Pa=3&PL>29&Qf=57
                Assert.False(log.CanStart(59, out _));      // Pa=4
                Assert.False(log.CanStart(433, out _));     // the first order asks Pa>19
                Assert.False(log.CanStart(375, out _));     // Brâkmar's first

                // The sheet's alignment block: f1 the level, f2 the side.
                var kub = ProtoMessage.Parse(ConnectionProtocol.BuildCharacteristics());
                var sheet = Sub(Sub(kub, 2), 9);
                Assert.Equal(3, sheet.Fields.First(f => f.FieldNumber == 1).VarIntValue);
                Assert.Equal(Alignments.Bonta, sheet.Fields.First(f => f.FieldNumber == 2).VarIntValue);

                // A Néophyte, and the rank comes after an order's apprenticeship.
                Assert.Equal(1, Alignments.Rank());
                log.Restore(433, QuestEngine.Book!.Of(433)!.Steps[0].Id, null, finished: true);
                await AlignmentHandler.AfterQuestAsync(pipe.ToClient, 433);
                Assert.Equal(Convert.FromHexString("0802"), await pipe.Next(Op.Hod));
            }
        }

        [Fact]
        public async Task World_entry_carries_the_character_s_rank_not_the_capturer_s()
        {
            if (!Available) return;
            Index();
            var codec = new GameCodec();
            var packets = new List<byte[]>();
            var session = new GameSession(codec, packet => { packets.Add(packet); return Task.CompletedTask; });
            using (SessionContext.Push(session))
            {
                const long me = 9_400_300_011;
                session.State.CharacterId = me;
                Alignments.Set(me, Alignments.Neutral, 0);
                // Neutral characters start at rank zero.
                Assert.Equal(new AlignmentRankUpdateEvent(0), await SendRankAsync());

                Alignments.Set(me, Alignments.Brakmar, 0);
                Assert.Equal(new AlignmentRankUpdateEvent(18), await SendRankAsync());
            }

            async Task<AlignmentRankUpdateEvent> SendRankAsync()
            {
                packets.Clear();
                var initial = new GameEntryInitialMessages([], [], session.State, new([], _ => []), Alignments.Rank(), 0,
                    () => new([]), () => new("1970-01-01T00:00:00.000Z", []), () => new([]),
                    () => CharacterSelectionMessages.Success(new() { Id = session.CharacterId, Name = "Selected", Level = 1 }, new([], 3, 1, [], [], [])),
                    () => InventoryMessages.FromCatalogue.Snapshot(session.State), () => new([]), () => new(81_001),
                    () => EntryInformationMessages.PreviousConnection(session.State.PreviousVisit),
                    new(_ => 0, _ => [], _ => 0, ItemEffectMessages.FromCatalogue),
                    new(() => new(0), () => new(null), () => new(null), () => new("", null)));
                await initial.SendAsync(session);
                return Assert.Single(packets.Select(packet => codec.Decode(packet).Content)
                    .OfType<AlignmentRankUpdateEvent>());
            }
        }

        [Fact]
        public async Task Leaving_the_city_forgets_what_was_done_for_its_militia()
        {
            if (!Available) return;
            Index();
            const long me = 9_400_300_012;
            Alignments.Set(me, Alignments.Brakmar, DateTimeOffset.UtcNow.AddDays(-15).ToUnixTimeMilliseconds());
            await using var pipe = await ClientPipe.OpenAsync(me, me, 990_100_121, "Desertor");
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.CharacterLevel = 60;
                pipe.Session.State.Kamas = 1000;
                var log = Log(pipe, 375, 376, 18);
                Assert.Equal(2, Alignments.Level());
                Assert.Equal(0, Alignments.LeaveStep(Alignments.Level()));

                foreach (long reply in new long[] { 78119, 78117, 78114, 78144, 78171 })
                    await AlignmentHandler.ReplyAsync(pipe.ToClient, AlignmentHandler.Morry, reply);

                Assert.Equal(Alignments.Neutral, Alignments.Of(me).Side);
                Assert.False(log.Finished(375));
                Assert.False(log.Finished(376));
                Assert.True(log.Finished(18));              // what was not the militia's stays
                Assert.Equal(0, Alignments.Level());
                Assert.Equal(0, Alignments.Rank());
            }
        }

        /// <summary>A log of these quests finished, as the session's.</summary>
        private static QuestLog Log(ClientPipe pipe, params int[] finished)
        {
            var facts = new Achievements.CharacterFacts();
            var log = new QuestLog(QuestEngine.Book!, () => pipe.Session.State.CharacterLevel,
                                   () => pipe.Session.State.MapId, facts.Scalar);
            foreach (int quest in finished)
                log.Restore(quest, QuestEngine.Book!.Of(quest)?.Steps.FirstOrDefault()?.Id ?? 0, null, finished: true);
            pipe.Session.State.Quests = log;
            return log;
        }

        private static ProtoMessage Sub(ProtoMessage message, int field)
            => ProtoMessage.Parse(message.Fields.First(f => f.FieldNumber == field && f.WireType == 2).BytesValue);
    }
}
