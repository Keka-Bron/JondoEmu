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
    /// Companion achievements keep each character's credit and are announced on the controller's client.
    /// </summary>
    public class CompanionAchievementsTests
    {
        private const long Controller = 990000301, Companion = 990000302, Account = 990000303;

        private static void Forget()
        {
            DatabaseManager.EnsureProgressionTables();
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM CharacterAchievements WHERE CharacterId IN ($m, $h);
                DELETE FROM CharacterAchievementCounters WHERE CharacterId IN ($m, $h);";
            command.Parameters.AddWithValue("$m", Controller);
            command.Parameters.AddWithValue("$h", Companion);
            command.ExecuteNonQuery();
        }

        [Fact]
        public async Task A_companions_level_earns_achievements_on_the_controllers_client()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();

            try
            {
                var codec = new RecordingCodec();
                await using var pipe = await ClientPipe.OpenAsync(Account, Controller, 1, codec: codec);
                var controller = pipe.Session;
                controller.State.CharacterLevel = 1;
                using (SessionContext.Push(controller)) Achievements.LoadFrom(Controller);

                var companion = GameSession.SinSocket();
                companion.State.CharacterId = Companion;
                companion.State.CharacterName = "Mule";
                companion.State.CharacterLevel = 200;
                using (SessionContext.Push(companion)) Achievements.LoadFrom(Companion);

                // Account-wide credit is shared, but each-character achievements stay each his own.
                var book = companion.State.Achievements!.Book;
                int each = book.All().First(achievement => achievement.EachCharacter).Id;
                controller.State.Achievements!.Restore(each, true, Controller);
                companion.State.Achievements.Restore(each, false, Companion);
                controller.State.Achievements.Restore(3, true, Controller);

                var wrong = GameSession.SinSocket();
                using var context = SessionContext.Push(wrong);
                using var turn = await controller.EnterTurnAsync();
                // His level, never looked at since he was connected: the level achievements.
                await Achievements.ForCompanionAsync(controller, companion);
                // The controller's 3 too: what another character of the account did is still the
                // companion's to earn and be paid for (#196).
                var earned = companion.State.Achievements!.Earned.Except(new[] { each }).ToList();
                Assert.Contains(3, earned);
                // Told through his controller's codec, with the companion's credit and capped level.
                Assert.Equal(earned.Count, codec.Frames.Count);
                foreach (var frame in codec.Frames)
                {
                    var achievement = Assert.IsType<AchievementFinishedEvent>(frame.Content).Achievement!;
                    Assert.Contains(achievement.AchievementId, earned);
                    Assert.Equal(Companion, achievement.AchievedBy);
                    Assert.Equal(200, achievement.FinishedLevel);
                    Assert.Equal(0, achievement.PioneerRank);
                    Assert.Null(frame.Uid);
                    Assert.Equal(RecordingCodec.Token, await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5)));
                }

                // Shown in the controller's list now, credited to him -- but the controller's own 3, which
                // stays the controller's and paid.
                var account = controller.State.Achievements!;
                var companionOnly = earned.Where(id => id != 3).ToList();
                Assert.All(companionOnly, id => Assert.Equal(Companion, account.AchieverOf(id)));
                Assert.All(companionOnly, id => Assert.Equal(200, account.LevelOf(id)));
                Assert.True(account.WasClaimed(each));
                Assert.False(companion.State.Achievements.WasClaimed(each));
                Assert.Equal(Companion, companion.State.Achievements.AchieverOf(each));
                Assert.Equal(Controller, account.AchieverOf(3));
                Assert.True(account.WasClaimed(3));
                // His own 3, his reward still to take.
                Assert.Equal(Companion, companion.State.Achievements.AchieverOf(3));
                Assert.False(companion.State.Achievements.WasClaimed(3));
                Assert.Equal(0, controller.UnoCadaVez.CurrentCount);
                Assert.Same(wrong, SessionContext.Actual);

                // Looked at again, nothing new: no achievement is earned twice.
                companion.State.AchievementLevelChecked = 0;
                await Achievements.ForCompanionAsync(controller, companion);
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
        public async Task A_companion_following_its_controller_into_a_zone_explores_it()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();
            const int zone = 518, explored = 307;
            try
            {
                var codec = new RecordingCodec();
                await using var pipe = await ClientPipe.OpenAsync(Account, Controller, 1, codec: codec);
                var controller = pipe.Session;
                using (SessionContext.Push(controller)) Achievements.LoadFrom(Controller);
                var companion = GameSession.SinSocket();
                companion.State.CharacterId = Companion;
                companion.State.CharacterName = "Lilia";
                companion.State.CharacterLevel = 50;
                // His level looked at already: only what arriving earns.
                companion.State.AchievementLevelChecked = 50;
                using (SessionContext.Push(companion)) Achievements.LoadFrom(Companion);
                Assert.False(companion.State.Achievements!.Has(explored));
                using var turn = await controller.EnterTurnAsync();

                // Arriving alongside the controller counts toward the companion's exploration (#213).
                await Achievements.ForCompanionAsync(controller, companion, 990000310, zone);

                Assert.True(companion.State.Achievements.Has(explored));
                Assert.Equal(Companion, companion.State.Achievements.AchieverOf(explored));
                Assert.Contains(codec.Frames, frame => frame.Content is AchievementFinishedEvent done
                                                       && done.Achievement!.AchievementId == explored
                                                       && done.Achievement.AchievedBy == Companion);
                // His exploration, written down as his own: not his controller's.
                using (SessionContext.Push(companion)) Assert.Equal(1, Achievements.Tally(AchievementCatalogue.ExploreKey, zone));
                using (SessionContext.Push(controller)) Assert.Equal(0, Achievements.Tally(AchievementCatalogue.ExploreKey, zone));

                // Back on the zone: nothing earned twice.
                int told = codec.Frames.Count;
                await Achievements.ForCompanionAsync(controller, companion, 990000310, zone);
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
        public async Task A_failed_companion_notification_preserves_achievements_before_controller_synchronization()
        {
            if (!File.Exists(Paths.WorldDb) || !File.Exists(Paths.AchievementsJson)) return;
            Achievements.Load();
            Forget();
            try
            {
                var codec = new RecordingCodec { Fail = true };
                await using var pipe = await ClientPipe.OpenAsync(Account, Controller, 1, codec: codec);
                var controller = pipe.Session;
                controller.State.CharacterLevel = 1;
                using (SessionContext.Push(controller)) Achievements.LoadFrom(Controller);
                var companion = GameSession.SinSocket();
                companion.State.CharacterId = Companion;
                companion.State.CharacterLevel = 247;
                using (SessionContext.Push(companion)) Achievements.LoadFrom(Companion);
                companion.State.AchievementsPending.Add(3);
                using var turn = await controller.EnterTurnAsync();

                Assert.Same(codec.Failure, await Assert.ThrowsAsync<IOException>(() => Achievements.ForCompanionAsync(controller, companion)));
                var first = Assert.IsType<AchievementFinishedEvent>(Assert.Single(codec.Frames).Content).Achievement!;
                Assert.Equal(Companion, first.AchievedBy);
                Assert.Equal(200, first.FinishedLevel);
                Assert.Equal(Companion, companion.State.Achievements!.AchieverOf(first.AchievementId));
                Assert.Equal(200, companion.State.Achievements.LevelOf(first.AchievementId));
                Assert.Equal((first.AchievementId, false), Assert.Single(DatabaseManager.LoadAchievements(Companion)));
                Assert.Empty(DatabaseManager.LoadAchievements(Controller));
                Assert.Empty(controller.State.Achievements!.Earned);
                Assert.Empty(companion.State.AchievementsPending);
                Assert.Equal(247, companion.State.AchievementLevelChecked);
                Assert.Equal(0, controller.UnoCadaVez.CurrentCount);
                Assert.False(pipe.FromServer.DataAvailable);
            }
            finally { Forget(); }
        }

        private sealed class RecordingCodec : IGameCodec
        {
            internal static readonly byte[] Token = [0xA8, 0x71];
            internal readonly System.Collections.Generic.List<GameFrame> Frames = [];
            internal readonly IOException Failure = new("companion achievement encoding failed");
            internal bool Fail;
            internal int Decodes;
            public byte[] Encode(GameFrame frame) { Frames.Add(frame); if (Fail) throw Failure; return Token; }
            public GameFrame Decode(ReadOnlySpan<byte> packet) { Decodes++; throw new InvalidOperationException("Companion achievements must use the recipient codec directly."); }
        }
    }
}
