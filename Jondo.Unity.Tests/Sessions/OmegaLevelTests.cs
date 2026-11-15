using Jondo.Unity.Server;
using Jondo.Unity.Server.Network;
using System.Linq;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    /// <summary>
    /// Levels above 200 are Omega: the level and the experience go on, the sheet does not.
    /// </summary>
    /// <remarks>
    /// Measured on the characteristics captures: a character with the experience of level 354
    /// (200 with Omega 154) gets 1,050 base life and 995 points to spend from the real server.
    /// The .level command already said so in chat, while the sheet of a level 1000 showed 5,050
    /// base life and 4,995 points: life and the capital were worked out from the level without
    /// the cap.
    /// </remarks>
    public sealed class OmegaLevelTests
    {
        [Theory]
        [InlineData(1, 55, 0)]
        [InlineData(200, 1050, 995)]
        [InlineData(201, 1050, 995)]
        [InlineData(354, 1050, 995)]
        [InlineData(1000, 1050, 995)]
        public void Life_and_points_stop_at_level_200(int level, int life, int capital)
        {
            Assert.Equal(life, CharacteristicRules.BaseLifeForLevel(level));
            Assert.Equal(capital, CharacteristicRules.TotalCapitalForLevel(level));
            Assert.Equal(life, CharacteristicsMessages.Live.ValueOf(Stat.LifePoints, level, SessionContext.Current));
        }

        /// <summary>
        /// In a fight the formulas read the capped level: a shield worth "100 % of the level" was
        /// 1,000 points on a level 1000, and pushback damage five times what the client previewed.
        /// The real level stays for what the client draws, and a monster keeps its own.
        /// </summary>
        [Theory]
        [InlineData(false, 1000, 200)]
        [InlineData(false, 150, 150)]
        [InlineData(true, 1000, 1000)]
        public void Fight_formulas_read_the_capped_level_of_a_player(bool monster, int level, int formulas)
        {
            var fighter = new Jondo.Unity.World.Fights.Fighter { IsMonster = monster, Level = level };

            Assert.Equal(formulas, fighter.StatLevel);
            Assert.Equal(level, fighter.Level);
        }

        /// <summary>
        /// On the map the actor's own level stops at 200; the whole one travels in the ornament.
        /// Measured over the 319 captured players wearing one: below 200 both are equal, above it
        /// every one shows 200 and carries its full level in the ornament.
        /// </summary>
        [Theory]
        [InlineData(150, 150)]
        [InlineData(200, 200)]
        [InlineData(1000, 200)]
        public void The_map_actor_shows_the_level_up_to_200(int level, int shown)
        {
            var character = new Jondo.Unity.Server.DbCharacter
            {
                Id = 7101, Name = "Omega", Level = level, Breed = 7,
            };

            var root = ProtoMessage.Parse(ConnectionProtocol.BuildPlayerActorBlock(character, 341, 5, 60000001));
            var details = ProtoMessage.Parse(root.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            var kind = ProtoMessage.Parse(details.Fields.Single(f => f.FieldNumber == 1).BytesValue);
            var humanoid = ProtoMessage.Parse(kind.Fields.Single(f => f.FieldNumber == 5).BytesValue);
            var body = ProtoMessage.Parse(humanoid.Fields.Single(f => f.FieldNumber == 3).BytesValue);
            var identity = ProtoMessage.Parse(body.Fields.First(f => f.FieldNumber == 1).BytesValue);

            Assert.Equal(shown, identity.Fields.Single(f => f.FieldNumber == 5).VarIntValue);
        }
    }
}
