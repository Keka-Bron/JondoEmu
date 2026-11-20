using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Jondo.Unity.World.Achievements;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// A hero's achievements (Achievements.ForHeroAsync): his own, earned in his session, told on
    /// his master's client and shown in the account's list from then on -- each his own to be paid for.
    /// </summary>
    public class HeroAchievementsTests
    {
        private const long Master = 990000301, Hero = 990000302, Account = 990000303;

        private static void Forget()
        {
            DatabaseManager.EnsureProgressionTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM CharacterAchievements WHERE CharacterId IN ($m, $h);
                DELETE FROM CharacterAchievementCounters WHERE CharacterId IN ($m, $h);";
            command.Parameters.AddWithValue("$m", Master);
            command.Parameters.AddWithValue("$h", Hero);
            command.ExecuteNonQuery();
        }

        [Fact]
        public async Task A_heros_level_earns_his_own_achievements_on_his_masters_client()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();

            try
            {
                var codec = new RecordingCodec();
                await using var pipe = await ClientPipe.OpenAsync(Account, Master, 1, codec: codec);
                var master = pipe.Session;
                master.State.CharacterLevel = 1;
                using (SessionContext.Push(master)) Achievements.LoadFrom(Master);

                var hero = GameSession.SinSocket();
                hero.State.CharacterId = Hero;
                hero.State.CharacterName = "Mule";
                hero.State.CharacterLevel = 200;
                using (SessionContext.Push(hero)) Achievements.LoadFrom(Hero);

                // Account-wide credit is shared, but each-character achievements stay each his own.
                var book = hero.State.Achievements!.Book;
                int each = book.All().First(achievement => achievement.EachCharacter).Id;
                master.State.Achievements!.Restore(each, true, Master);
                hero.State.Achievements.Restore(each, false, Hero);
                master.State.Achievements.Restore(3, true, Master);

                var wrong = GameSession.SinSocket();
                using var context = SessionContext.Push(wrong);
                using var turn = await master.EnterTurnAsync();
                // His level, never looked at since he was connected: the level achievements.
                await Achievements.ForHeroAsync(master, hero);
                // The master's 3 too: what another character of the account did is still the
                // hero's to earn and be paid for (#196).
                var earned = hero.State.Achievements!.Earned.Except(new[] { each }).ToList();
                Assert.Contains(3, earned);
                // Told through his master's codec, with the hero's credit and capped level.
                Assert.Equal(earned.Count, codec.Frames.Count);
                foreach (var frame in codec.Frames)
                {
                    var achievement = Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!;
                    Assert.Contains(achievement.AchievementId, earned);
                    Assert.Equal(Hero, achievement.AchievedBy);
                    Assert.Equal(200, achievement.FinishedLevel);
                    Assert.Equal(0, achievement.PioneerRank);
                    Assert.Null(frame.Uid);
                    Assert.Equal(RecordingCodec.Token, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)));
                }

                // Shown in the master's list now, credited to him -- but the master's own 3, which
                // stays the master's and paid.
                var account = master.State.Achievements!;
                var heroOnly = earned.Where(id => id != 3).ToList();
                Assert.All(heroOnly, id => Assert.Equal(Hero, account.AchieverOf(id)));
                Assert.All(heroOnly, id => Assert.Equal(200, account.LevelOf(id)));
                Assert.True(account.WasClaimed(each));
                Assert.False(hero.State.Achievements.WasClaimed(each));
                Assert.Equal(Hero, hero.State.Achievements.AchieverOf(each));
                Assert.Equal(Master, account.AchieverOf(3));
                Assert.True(account.WasClaimed(3));
                // His own 3, his reward still to take.
                Assert.Equal(Hero, hero.State.Achievements.AchieverOf(3));
                Assert.False(hero.State.Achievements.WasClaimed(3));
                Assert.Equal(0, master.UnoCadaVez.CurrentCount);
                Assert.Same(wrong, SessionContext.Actual);

                // Looked at again, nothing new: no achievement is earned twice.
                hero.State.AchievementLevelChecked = 0;
                await Achievements.ForHeroAsync(master, hero);
                Assert.Equal(earned.Count, codec.Frames.Count);
                Assert.Equal(0, codec.Decodes);
                await pipe.Quiet();
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public async Task A_hero_following_his_master_into_a_zone_explores_it_too()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();
            const int zone = 518, explored = 307;
            try
            {
                var codec = new RecordingCodec();
                await using var pipe = await ClientPipe.OpenAsync(Account, Master, 1, codec: codec);
                var master = pipe.Session;
                using (SessionContext.Push(master)) Achievements.LoadFrom(Master);
                var hero = GameSession.SinSocket();
                hero.State.CharacterId = Hero;
                hero.State.CharacterName = "Lilia";
                hero.State.CharacterLevel = 50;
                // His level looked at already: only what arriving earns.
                hero.State.AchievementLevelChecked = 50;
                using (SessionContext.Push(hero)) Achievements.LoadFrom(Hero);
                Assert.False(hero.State.Achievements!.Has(explored));
                using var turn = await master.EnterTurnAsync();

                // Beside his master on a map of the zone (HeroPresence.MasterMovedAsync): Koalitions'
                // Lilia was left without it when only the one played got the map (#213).
                await Achievements.ForHeroAsync(master, hero, 990000310, zone);

                Assert.True(hero.State.Achievements.Has(explored));
                Assert.Equal(Hero, hero.State.Achievements.AchieverOf(explored));
                Assert.Contains(codec.Frames, frame => frame.Content is AchievementFinishedEvent done
                                                       && done.Achievement!.AchievementId == explored
                                                       && done.Achievement.AchievedBy == Hero);
                // His exploration, written down as his own: not his master's.
                using (SessionContext.Push(hero)) Assert.Equal(1, Achievements.Tally(AchievementCatalogue.ExploreKey, zone));
                using (SessionContext.Push(master)) Assert.Equal(0, Achievements.Tally(AchievementCatalogue.ExploreKey, zone));

                // Back on the zone: nothing earned twice.
                int told = codec.Frames.Count;
                await Achievements.ForHeroAsync(master, hero, 990000310, zone);
                Assert.Equal(told, codec.Frames.Count);
                for (int i = 0; i < told; i++)
                    Assert.Equal(RecordingCodec.Token, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)));
                await pipe.Quiet();
            }
            finally
            {
                Forget();
            }
        }

        [Fact]
        public async Task A_failed_hero_notification_keeps_hero_persistence_and_pending_timing_before_master_synchronization()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();
            try
            {
                var codec = new RecordingCodec { Fail = true };
                await using var pipe = await ClientPipe.OpenAsync(Account, Master, 1, codec: codec);
                var master = pipe.Session;
                master.State.CharacterLevel = 1;
                using (SessionContext.Push(master)) Achievements.LoadFrom(Master);
                var hero = GameSession.SinSocket();
                hero.State.CharacterId = Hero;
                hero.State.CharacterLevel = 247;
                using (SessionContext.Push(hero)) Achievements.LoadFrom(Hero);
                hero.State.AchievementsPending.Add(3);
                using var turn = await master.EnterTurnAsync();

                Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => Achievements.ForHeroAsync(master, hero)));
                var first = Assert.IsType<AchievementFinishedEvent>(Assert.Single(codec.Frames).Content).Achievement!;
                Assert.Equal(Hero, first.AchievedBy);
                Assert.Equal(200, first.FinishedLevel);
                Assert.Equal(Hero, hero.State.Achievements!.AchieverOf(first.AchievementId));
                Assert.Equal(200, hero.State.Achievements.LevelOf(first.AchievementId));
                Assert.Equal((first.AchievementId, false), Assert.Single(DatabaseManager.LoadAchievements(Hero)));
                Assert.Empty(DatabaseManager.LoadAchievements(Master));
                Assert.Empty(master.State.Achievements!.Earned);
                Assert.Empty(hero.State.AchievementsPending);
                Assert.Equal(247, hero.State.AchievementLevelChecked);
                Assert.Equal(0, master.UnoCadaVez.CurrentCount);
                Assert.False(pipe.FromServer.DataAvailable);
            }
            finally { Forget(); }
        }

        private sealed class RecordingCodec : IGameCodec
        {
            internal static readonly byte[] Token = [0xA8, 0x71];
            internal readonly System.Collections.Generic.List<GameFrame> Frames = [];
            internal readonly IOException Failure = new("hero achievement encoding failed");
            internal bool Fail;
            internal int Decodes;
            public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Fail) throw Failure; return Token; }
            public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Hero achievements must use the recipient codec directly."); }
        }
    }
}
