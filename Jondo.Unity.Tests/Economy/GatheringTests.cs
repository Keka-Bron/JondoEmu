using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    [Collection("forgemagic")]
    public class GatheringTests
    {
        private const long Character = 8_810_100_090, Map = 8_810_000_090;
        private const int ResourceItem = 303;

        [Theory]
        [InlineData(0, -1)]
        [InlineData(7, 7)]
        [InlineData(7, 2)]
        [InlineData(7, -1)]
        public async Task Harvested_resources_survive_a_workshop_inventory_refresh(int stored, int cached)
        {
            Forget();
            await using var pipe = await ClientPipe.OpenAsync(8_810_000_091, Character, Map, "Gatherer");
            using var context = SessionContext.Push(pipe.Session);
            try
            {
                // An unrelated equipped item must survive the refresh, including its effects.
                var worn = DatabaseManager.AddItemToInventory(Character, 2478, 1);
                worn.Position = 0;
                worn.RawEffects = "[[118,5,0,0]]";
                DatabaseManager.SaveInventoryItem(Character, worn);
                Equipment.Add(worn.Uid, worn.ItemId, 1, 0, worn.RawEffects);

                long resourceUid = 0;
                if (stored > 0)
                {
                    var existing = DatabaseManager.AddItemToInventory(Character, ResourceItem, stored);
                    resourceUid = existing.Uid;
                    if (cached >= 0)
                        Equipment.Add(existing.Uid, ResourceItem, cached, Equipment.Bag, null);
                }
                pipe.Session.State.SetInventory(DatabaseManager.LoadInventory(Character));

                var resource = new Resources.Resource
                {
                    MapId = Map, ElementId = 901090, Cell = 100,
                    Type = 1, SkillId = 6, JobId = 2, ItemId = ResourceItem, LevelMin = 1,
                };

                // Exercise the actual delayed completion, without depending on world resources
                // or exposing a second production path just for tests.
                var finish = typeof(GatheringHandler).GetMethod("FinishAsync",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                await (Task)finish.Invoke(null, new object[]
                    { pipe.Session, pipe.ToClient, resource, resource.SkillId, 911090, 1 })!;

                var saved = Assert.Single(DatabaseManager.LoadInventory(Character), i => i.ItemId == ResourceItem);
                Assert.True(saved.Quantity > stored);
                if (stored > 0) Assert.Equal(resourceUid, saved.Uid);
                Assert.Equal(saved.Quantity, Equipment.ByUid(saved.Uid)!.Quantity);
                Assert.Equal(saved.Quantity, pipe.Session.State.GetInventoryItem(saved.Uid)!.Quantity);

                await pipe.Next(Op.Iwi);
                if (stored == 0)
                    await pipe.Next(Op.Iua);
                else
                    Assert.Equal(ConnectionProtocol.BuildItemQuantity(saved.Uid, saved.Quantity), await pipe.Next(Op.Ivj));
                await pipe.Next(Op.Iun);
                await pipe.Next(Op.Itn);
                // A bonus may also produce a job level-up before the final resource state.
                while ((await pipe.NextAny()).Opcode != Op.Iwm) { }

                await WorkshopHandler.InventoryAsync(pipe.ToClient);
                var inventory = ProtoMessage.Parse(await pipe.Next(Op.Ivx)).Fields
                    .Where(f => f.FieldNumber == 3)
                    .Select(f => ProtoMessage.Parse(f.BytesValue!)).ToList();
                var body = Assert.Single(inventory.Select(entry => ProtoMessage.Parse(
                    entry.Fields.Single(f => f.FieldNumber == 5).BytesValue!)),
                    item => item.Fields.Single(f => f.FieldNumber == 1).VarIntValue == ResourceItem);
                Assert.Equal(saved.Quantity, body.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
                Assert.Equal(saved.Uid, body.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
                Assert.Equal(2, inventory.Count);
                Assert.Equal(0, Equipment.ByUid(worn.Uid)!.Position);
                Assert.Equal(5, Assert.Single(Equipment.ByUid(worn.Uid)!.Effects).Value);
                await pipe.Next(Op.Hlm);

                // Updating this character's views must not populate another session's bag.
                using (SessionContext.Push(GameSession.SinSocket())) Assert.Empty(Equipment.All);
            }
            finally
            {
                Forget();
            }
        }

        private static void Forget()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $id; " +
                                  "DELETE FROM CharacterJobs WHERE CharacterId = $id;";
            command.Parameters.AddWithValue("$id", Character);
            command.ExecuteNonQuery();
        }
    }
}
