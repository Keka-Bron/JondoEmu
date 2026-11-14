using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// Replies that count the character's marks in a range: the bounty hunter of Amakna's militia
    /// gives a primaton for each criminal brought to justice, nine at most, and past nine he is
    /// only impressed (Dofus pour les noobs, «Les bandits de Cania»: «Vous ne pouvez obtenir que 9
    /// primatons maximum auprès du Chasseur de primes»).
    /// </summary>
    public class DialogueMarkCountTests
    {
        private const string Tree = @"{ ""dialogues"": [ {
            ""npc"": 5963, ""map"": 0, ""opening"": 43400,
            ""lines"": [ { ""message"": 43400, ""choices"": [
                { ""reply"": 56786, ""next"": 43449, ""marks"": -1469002, ""marksBelow"": [-1469032, -1469001, 9], ""gives"": 23746 },
                { ""reply"": 56786, ""next"": 43451, ""marks"": -1469002, ""marksReached"": [-1469032, -1469001, 9] }
            ] } ] } ] }";

        private static NpcDialogue Read(string json)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, json);
                return NpcDialogueContent.Load(path).Rows.Single().Value.Value;
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Nine_criminals_and_no_more_are_paid()
        {
            var choices = Read(Tree).Lines.Single().Choices;
            var paid = choices[0];
            var impressed = choices[1];
            var marks = new HashSet<int> { -1469000, -5 };          // interested, and a mark of somebody else's

            for (int criminal = 1; criminal <= 9; criminal++)
            {
                Assert.True(paid.MarksAllow(marks));
                Assert.False(impressed.MarksAllow(marks));
                marks.Add(-1469000 - criminal);
            }
            Assert.False(paid.MarksAllow(marks));
            Assert.True(impressed.MarksAllow(marks));
            Assert.True(paid.Gated);
        }

        [Fact]
        public void The_count_is_written_back()
        {
            string path = Path.GetTempFileName();
            try
            {
                NpcDialogueContent.Save(path, new[] { Read(Tree) });
                var again = NpcDialogueContent.Load(path).Rows.Single().Value.Value.Lines.Single().Choices;
                Assert.Equal(new[] { -1469032, -1469001, 9 }, again[0].MarksBelow);
                Assert.Empty(again[0].MarksReached);
                Assert.Equal(new[] { -1469032, -1469001, 9 }, again[1].MarksReached);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
