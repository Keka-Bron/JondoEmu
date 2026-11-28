using Jondo.Unity.Server.Handlers;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The quest souls of a capture (SoulCaptureHandler): a monster's «souls» row in the client's
    /// data names the item its captured soul turns into, under a criterion.
    /// </summary>
    public sealed class QuestSoulsTests
    {
        /// <summary>
        /// «Plantas contra mediomuertos» (1951): the girasol salvaje (48) yields «Alma capturada»
        /// 34209 while Jauel asks for it; «Qo!13680», on «bring 34209 to Jauel», reads as not
        /// carried yet.
        /// </summary>
        [Fact]
        public void A_plant_soul_is_asked_for_while_its_item_is_not_carried()
        {
            QuestEngine.Load();
            // The girasol salvaje's row, as MonsterTemplates.Data carries it.
            const string criterion = "Qa=1951&Qo>13679&Qo!13680&Qo<13680";
            Assert.Equal("Qa=1951&Qo>13679&PO!34209&Qo<13680", SoulCaptureHandler.AsCarried(criterion));
            Assert.Equal("Pm!98567687", SoulCaptureHandler.AsCarried("Pm!98567687"));
        }
    }
}
