using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The divine dimensions' old portals, kept in the Amakna village square, as the 3.6.10.11
    /// client reads them: map actor kind 3, the ioc that names the portal's type, its NPC's
    /// sentence, the hhu that crosses it.
    /// </summary>
    [Collection("MapManager")]
    public class DimensionPortalTests
    {
        [Fact]
        public void Four_portals_stand_in_the_village()
        {
            Assert.Equal(new[] { (1, 2374, 297), (2, 2594, 299), (3, 2667, 301), (4, 3136, 303) },
                DimensionPortals.All.Select(p => (p.Type, p.Dimension.PortalNpc, p.Cell)));
            Assert.All(DimensionPortals.All, p => Assert.Equal(DimensionPortals.VillageMap, p.MapId));
            Assert.DoesNotContain(DimensionPortals.All, p => p.Dimension.Name == "Osavora");
            Assert.Equal(4, DimensionPortals.OnMap(DimensionPortals.VillageMap).Count());
            Assert.Empty(DimensionPortals.OnMap(TravelersTower.PortalRoom));
            Assert.Equal("Xelorium", DimensionPortals.OfType(3)!.Dimension.Name);
            Assert.Equal(3, DimensionPortals.ByContext(DimensionPortals.ContextBase - 3)!.Type);
        }

        /// <summary>Their cells walkable, and none where a vendor or something clickable stands.</summary>
        [Fact]
        public void The_portals_stand_where_nothing_else_does()
        {
            if (!MapManager.WalkableCells.ContainsKey(DimensionPortals.VillageMap)) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(DimensionPortals.VillageMap)) return;   // no world data
            Interactives.Initialize();
            var clickable = Interactives.ElementsOf(DimensionPortals.VillageMap).Select(e => e.Cell).ToHashSet();
            foreach (var portal in DimensionPortals.All)
            {
                Assert.True(MapManager.IsCellWalkable(portal.MapId, portal.Cell));
                Assert.DoesNotContain(portal.Cell, clickable);
                Assert.DoesNotContain(Npcs.Of(portal.MapId), n => n.Cell == portal.Cell);
            }
        }

        /// <summary>
        /// The actor as gja reads it -- kind 3, f2 the portal and f3 the area --; the ioc with the
        /// portal's type in f2, as epd hands it to fsw; the hhu's portal in f3, as eoy writes it.
        /// </summary>
        [Fact]
        public void The_messages_go_as_the_client_reads_them()
        {
            var portal = DimensionPortals.OfType(1)!;
            var actor = MapActorMessages.Portal(portal);
            Assert.NotNull(actor);
            Assert.Equal(DimensionPortals.ContextBase - 1, actor.ActorId);
            Assert.Equal(297, actor.Disposition.CellId);
            Assert.Equal(MapActorMessages.Facing(DimensionPortals.Facing), actor.Disposition.Direction);
            Assert.Equal(2720, actor.ActorInformation!.Look.Bones);
            var kind = Assert.IsType<ActorInformationCaseRolePlayActor>(actor.ActorInformation.ActorCase).Value;
            Assert.Equal(1, Assert.IsType<RolePlayActorCasePortalActor>(kind.ActorCase).Value.PortalId);

            var ioc = ProtoMessage.Parse(DimensionPortalProtocol.BuildDialog(portal));
            Assert.Equal(new[] { (2, 1L), (4, DimensionPortals.VillageMap), (5, DimensionPortals.ContextBase - 1) },
                ioc.Fields.Select(f => (f.FieldNumber, f.VarIntValue)));

            Assert.Equal(3, DimensionPortalProtocol.ReadUse(Pb.New().Var(3, 3).Build()));
        }

        /// <summary>The sentence's #1 says the crossings never run out, its $time#2 a moment a century off, in unix seconds.</summary>
        [Fact]
        public void The_portal_says_it_never_closes()
        {
            var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
            var ios = ProtoMessage.Parse(DimensionPortalProtocol.BuildQuestion(17474, new[] { 21290L }, now));
            Assert.Equal(17474, ios.Fields.First(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(21290, ProtoMessage.Parse(ios.Fields.Single(f => f.FieldNumber == 2).BytesValue).Fields[0].VarIntValue);
            var parameters = ios.Fields.Where(f => f.FieldNumber == 3).Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue)).ToList();
            Assert.Equal(new[] { "∞", now.AddYears(100).ToUnixTimeSeconds().ToString() }, parameters);
        }

        /// <summary>The portals' NPCs as the client's data has them: their look, sentence and reply.</summary>
        [Fact]
        public void The_portals_are_their_npcs()
        {
            var expected = new Dictionary<int, (long Bones, long Message, long Reply)>
            {
                [2374] = (2720, 17474, 21290),
                [2594] = (2893, 18120, 21726),
                [2667] = (3031, 19086, 22756),
                [3136] = (3489, 22467, 27064),
            };
            foreach (var (npc, (bones, message, reply)) in expected)
            {
                var template = Npcs.EnsureTemplate(npc);
                if (template == null) return;   // no world data
                Assert.Equal(bones, Npcs.Variantes(template.Look)[0].Bones);
                Assert.Equal(message, template.DialogMessageId);
                Assert.Equal(new[] { reply }, template.Replies);
            }
        }
    }
}
