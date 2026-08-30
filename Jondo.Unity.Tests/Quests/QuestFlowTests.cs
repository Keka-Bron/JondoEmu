using System.IO;
using System.Linq;
using System.Text;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// What a quest element can now demand, start and let loose.
    /// </summary>
    /// <remarks>
    /// Three things were missing, and "Mort au rat" needs all three at once, which is why it is the
    /// case this was built against. Its walkthrough is: click the job posting on the tavern front
    /// -- that is what STARTS the quest, no NPC involved -- buy a lemonade from Grobid, go down to
    /// the cellar, and click the beer tap WHILE CARRYING the lemonade, at which point the rat comes
    /// out of hiding and can be fought.
    ///
    /// Against Ankama's own catalogue, quest 1633 reads:
    ///
    /// <code>
    ///   9895  texto libre   "Inspecciona el sótano"
    ///   9769  texto libre   "Haz salir a la rata de su escondite"
    ///   9770  combate       vence al 4113, la Rata Nsiosa
    ///   9771  hablar        ve a ver al NPC 2885
    /// </code>
    ///
    /// And the rat is in ZERO groups of the world, which is not an oversight: its place is behind
    /// the tap, not on a map. Before this, objective 9770 could not be finished by anybody.
    ///
    /// These tests are on the schema and the reading of it. Whether the rat actually appears is a
    /// question for the server and for a person with a client, and it is not claimed here.
    /// </remarks>
    public class QuestFlowTests
    {
        private static QuestBindingBook Read(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), "jondo-bindings-" + Path.GetRandomFileName());
            File.WriteAllText(path, json, Encoding.UTF8);

            try { return QuestBindingContent.Load(path, _ => { }); }
            finally { File.Delete(path); }
        }

        private const string MortAuRat = @"{
          ""bindings"": [
            { ""objective"": 9769, ""quest"": 1633, ""kind"": ""click"",
              ""elements"": [ { ""map"": 153358340, ""element"": 500001 } ],
              ""requires"": [ { ""item"": 8259, ""count"": 1 } ],
              ""spends"": true,
              ""spawns"": { ""monster"": 4113, ""count"": 1 },
              ""why"": ""El tirador del sotano, con la limonada encima."" },
            { ""objective"": 9895, ""quest"": 1633, ""kind"": ""enter"", ""map"": 153358340,
              ""why"": ""Inspecciona el sotano: basta con bajar."" }
          ]
        }";

        [Fact]
        public void An_element_can_demand_what_you_carry()
        {
            var binding = Read(MortAuRat).Of(9769);

            Assert.NotNull(binding);
            Assert.Single(binding!.Requires);
            Assert.Equal((8259, 1), binding.Requires[0]);
        }

        [Fact]
        public void And_say_whether_it_is_spent()
        {
            // Two different things, and the default is the careful one: showing something is not
            // handing it over. Only a row that says so takes it.
            Assert.True(Read(MortAuRat).Of(9769)!.SpendsRequired);
            Assert.False(Read(MortAuRat).Of(9895)!.SpendsRequired);
        }

        [Fact]
        public void An_element_can_let_a_monster_out()
        {
            var binding = Read(MortAuRat).Of(9769);

            Assert.Equal(4113, binding!.SpawnsMonster);
            Assert.Equal(1, binding.SpawnsCount);
        }

        [Fact]
        public void A_row_that_says_nothing_demands_and_spawns_nothing()
        {
            // The 20 rows already written say none of this, and they have to keep behaving exactly
            // as they did: no requirement, nothing spent, nothing let out, no quest handed over.
            var binding = Read(MortAuRat).Of(9895);

            Assert.Empty(binding!.Requires);
            Assert.False(binding.SpendsRequired);
            Assert.Equal(0, binding.SpawnsMonster);
            Assert.Equal(0, binding.Starts);
        }

        [Fact]
        public void An_element_can_hand_the_quest_over_itself()
        {
            // The job posting. No NPC says a word in this one.
            var binding = Read(@"{ ""bindings"": [
                { ""objective"": 9895, ""quest"": 1633, ""starts"": 1633,
                  ""elements"": [ { ""map"": 153357316, ""element"": 500002 } ] } ] }").Of(9895);

            Assert.Equal(1633, binding!.Starts);
        }

        [Fact]
        public void The_count_of_a_requirement_is_never_zero()
        {
            // "requires": [{"item": 8259}] means one, not none. A zero would make the check pass
            // for a player carrying nothing, which is the one outcome nobody wants from a row that
            // exists to demand something.
            var binding = Read(@"{ ""bindings"": [
                { ""objective"": 9769, ""quest"": 1633,
                  ""requires"": [ { ""item"": 8259 } ],
                  ""elements"": [ { ""map"": 1, ""element"": 2 } ] } ] }").Of(9769);

            Assert.Equal((8259, 1), binding!.Requires[0]);
        }
    }
}
