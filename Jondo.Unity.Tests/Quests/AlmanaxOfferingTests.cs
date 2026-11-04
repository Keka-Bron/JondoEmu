using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;
using Offering = Jondo.Unity.Server.Handlers.AlmanaxOfferingHandler;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The day's offering through Ontoral's own replies, the day's altar and the saint, in their
    /// order: 4 November, Kacinka's day, a piece of fibra criminal (quest 1031). Ontoral used to take
    /// the offering on any reply, his farewell too, and to close the whole of it on a hello.
    /// </summary>
    [Collection(AlmanaxTests.AlmanaxClock)]
    public sealed class AlmanaxOfferingTests
    {
        private const long Character = 8_900_100_083;
        private const int Kacinka = 1674, Quest = 1031, Fibre = 424;
        private const int Bring = 6075, Prayer = 6076, Saint = 6077;
        private const long Room = 101450243, Altar = 475746;

        private static bool Available
            => File.Exists(Paths.AlmanaxJson) && File.Exists(Paths.QuestsJson) && File.Exists(Paths.WorldDb);

        private static void Clean()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterQuests WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterItems WHERE CharacterId = $c; " +
                                  "DELETE FROM CharacterAchievementCounters WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Character);
            try { command.ExecuteNonQuery(); } catch (SqliteException) { }
        }

        [Fact]
        public async Task The_offering_goes_by_Ontoral_s_replies_the_altar_and_the_saint()
        {
            if (!Available) return;
            QuestEngine.Load();
            Almanax.Load();
            if (Npcs.TemplateOf(Almanax.Giver) == null) Npcs.Initialize();
            DatabaseManager.EnsureProgressionTables();
            Offering.Reset();
            Clean();

            var clock = Almanax.Clock;
            Almanax.Clock = () => new DateTime(2026, 11, 4, 12, 0, 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(8_900_100_083, Character, AlmanaxTemple.Sanctuary, "Devoto");
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.CharacterLevel = 50;
                    QuestEngine.LoadFrom(Character);
                    Assert.Equal(Quest, Almanax.TodaysOffering());

                    // Not taken yet: the way to take it, and neither the offering nor the return.
                    var (welcome, offered, _) = Offering.Opening();
                    Assert.Equal(Offering.Welcome, welcome);
                    Assert.Contains(Offering.ReadyToCelebrate, offered);
                    Assert.DoesNotContain(Offering.HandTheOffering, offered);
                    Assert.DoesNotContain(Offering.SpokeWithTheSpirit, offered);

                    // His farewell takes nothing any more.
                    Assert.Equal(0, (await Offering.ReplyAsync(pipe.ToClient, Offering.NoThanks)).Question);
                    Assert.Null(QuestEngine.Log!.Run(Quest));

                    // «Imagino que sabes a qué santo le rezamos hoy» -- Kacinka, «¡Sí, es ella!»,
                    // and the offering taken: what she wants, in his words.
                    var which = await Offering.ReplyAsync(pipe.ToClient, Offering.ReadyToCelebrate);
                    Assert.Equal(Offering.WhichSaint, which.Question);
                    var lines = Offering.SaintLinesOf(Kacinka)!;
                    Assert.Equal(new Offering.SaintLines(13686, 14486, 12721, 12722), lines);
                    Assert.Contains(lines.Guess, which.Replies);
                    var yes = await Offering.ReplyAsync(pipe.ToClient, lines.Guess);
                    Assert.Equal(12721, yes.Question);
                    Assert.Contains(Offering.WillingToOffer, yes.Replies);
                    var taken = await Offering.ReplyAsync(pipe.ToClient, Offering.WillingToOffer);
                    Assert.Equal(12722, taken.Question);
                    Assert.True(QuestEngine.Log!.Active(Quest));

                    // A hello closes nothing: neither the offering, nor the return, nor the saint.
                    await QuestEngine.OnTalkingToAsync(pipe.ToClient, Almanax.Giver);
                    await QuestEngine.OnTalkingToAsync(pipe.ToClient, Kacinka);
                    Assert.Empty(QuestEngine.Log!.Run(Quest)!.Done);

                    // Two fibres: «ni más, ni menos». One: «Haré esta ofrenda en tu nombre, #1».
                    Assert.True(await Equipment.GiveAsync(pipe.ToClient, Fibre, 2));
                    Assert.Contains(Offering.HandTheOffering, Offering.Opening().Replies);
                    Assert.Equal(Offering.TooMany, (await Offering.ReplyAsync(pipe.ToClient, Offering.HandTheOffering)).Question);
                    Assert.True(await Equipment.TakeAsync(pipe.ToClient, Fibre, 1));
                    var made = await Offering.ReplyAsync(pipe.ToClient, Offering.HandTheOffering);
                    Assert.Equal(Offering.OfferingMade, made.Question);
                    Assert.Equal(new[] { "Devoto" }, made.Parameters);
                    Assert.Equal(0, Equipment.HowMany(Fibre));
                    Assert.Contains(Bring, QuestEngine.Log!.Run(Quest)!.Done);

                    // The saint before the prayer: nobody has shown up.
                    await QuestEngine.OnTalkingToAsync(pipe.ToClient, Kacinka);
                    Assert.DoesNotContain(Saint, QuestEngine.Log!.Run(Quest)!.Done);

                    // The day's room's altar: the prayer.
                    if (Offering.AltarOn(Room) != 0)
                    {
                        Assert.Equal(Room, AlmanaxTemple.RoomOf(Almanax.Clock()));
                        Assert.Equal(Altar, Offering.AltarOn(Room));
                        Assert.True(Offering.MayPray());
                        Assert.True(await Offering.OnAltarUsedAsync(pipe.ToClient, Room, (int)Altar));
                    }
                    else
                    {
                        await QuestEngine.TickAsync(pipe.ToClient, Quest, Prayer);   // no map data here
                    }
                    Assert.Contains(Prayer, QuestEngine.Log!.Run(Quest)!.Done);
                    Assert.False(Offering.MayPray());

                    // Now the saint is seen, and Ontoral hears about it.
                    await QuestEngine.OnTalkingToAsync(pipe.ToClient, Kacinka);
                    Assert.Contains(Saint, QuestEngine.Log!.Run(Quest)!.Done);
                    Assert.Contains(Offering.SpokeWithTheSpirit, Offering.Opening().Replies);
                    var rewarded = await Offering.ReplyAsync(pipe.ToClient, Offering.SpokeWithTheSpirit);
                    Assert.Equal(Offering.Rewarded, rewarded.Question);
                    Assert.False(QuestEngine.Log!.Active(Quest));

                    // Made today: not offered again.
                    Assert.DoesNotContain(Offering.ReadyToCelebrate, Offering.Opening().Replies);
                }
            }
            finally
            {
                Almanax.Clock = clock;
                Almanax.Reset();
                Offering.Reset();
                Clean();
            }
        }
    }
}
