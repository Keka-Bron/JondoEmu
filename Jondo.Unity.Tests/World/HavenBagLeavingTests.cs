using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Going into the haven bag takes the character off the street for whoever stays on it. It did
    /// not: he went on being drawn there, a ghost, for everybody on the map he had left.
    /// </summary>
    [Collection("MapManager")]
    public class HavenBagLeavingTests
    {
        [Fact]
        public async Task Whoever_stays_on_the_map_stops_seeing_him()
        {
            if (!MapManager.WalkableCells.ContainsKey(Jail.MapId)) MapManager.Initialize();
            Merkasako.Initialize();
            long bag = Merkasako.MapOfTheme(Merkasako.DefaultTheme);
            if (bag == 0 || MapManager.GetMapInfo(bag) == null) return;   // no client data

            const long street = 191105026;
            await using var leaving = await ClientPipe.OpenAsync(990_000_701, 990_000_701, street, "SeVa");
            await using var staying = await ClientPipe.OpenAsync(990_000_702, 990_000_702, street, "SeQueda");

            using (SessionContext.Push(leaving.Session))
                await MerkasakoHandler.EnterFromOutsideAsync(leaving.ToClient, ConnectionProtocol.Push(Op.Jbn));

            Assert.True(Merkasako.IsHavenBag(leaving.Session.State.MapId));
            var removed = await staying.Next(Op.Kmu);
            Assert.Equal(990_000_701, ProtoMessage.Parse(removed).Fields.Find(f => f.FieldNumber == 2)!.VarIntValue);
        }
    }
}
