using System;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The green exclamation over an NPC, against the bytes Ankama's server actually sent.
    /// </summary>
    /// <remarks>
    /// This was chased for three sessions in the wrong place. The server was computing the right
    /// list, saying so in its own log — "Marcas del mapa 154010883: 1 de 1 NPCs con algo que
    /// ofrecer" — and sending an <c>iom</c> that was byte-identical in shape to a captured one, and
    /// the client drew nothing.
    ///
    /// <c>iom</c> is not the marker. It is an index of the whole SUB-AREA, which is why the one
    /// following "quest 2432 started" names 2427: two different maps of sub-area 980. A capture
    /// exists ("sin apariencias equipar un escudo") with no <c>iom</c> anywhere in the stream whose
    /// NPCs are marked all the same.
    ///
    /// The marker rides inside the NPC's own actor record in the <c>jss</c>. Diffing the same map
    /// and the same actor, ours against Ankama's, the whole difference was six bytes:
    ///
    /// <code>
    ///   Ankama  ...1217 0a0b3a09 12041a02e00c 28cc16 1a08...
    ///   Jondo   ...1211 0a053a03             28cc16 1a08...
    ///                            ^^^^^^^^^^^^
    ///                            f2 { f3: packed[1632] }
    /// </code>
    /// </remarks>
    public class NpcQuestMarkerTests
    {
        private static string Marker(int[] offered, int[] doing)
        {
            var identity = Pb.New();
            ConnectionProtocol.AddQuestMarker(identity, offered, doing);
            return Convert.ToHexString(identity.Build()).ToLowerInvariant();
        }

        [Fact]
        public void One_quest_on_offer_is_the_six_bytes_that_were_missing()
        {
            // Incarnam (-2,-3), map 154010883, NPC 2892 (Noken Okuto), quest 1632.
            // From `Movimiento\movimiento a mapa de arriba.pcapng`, jss frame 15.
            Assert.Equal("12041a02e00c", Marker(new[] { 1632 }, Array.Empty<int>()));
        }

        [Fact]
        public void And_the_same_shape_holds_for_the_next_map_along()
        {
            // The second, independent diff: map 154010371, NPC 2905 (Ternauta Unin), quest 1639,
            // out of `Movimiento\movimiento a mapa derecho.pcapng` jss frame 8. Two measurements
            // rather than one, because a single frame can be matched by a lucky coincidence.
            Assert.Equal("12041a02e70c", Marker(new[] { 1639 }, Array.Empty<int>()));
        }

        [Fact]
        public void Quests_in_hand_go_in_field_one_and_they_pack_together()
        {
            // From the tutorial capture, jss frame 1324, map 241440769, actor -20000: the block is
            // 12060a04c613dd0c, f1 packed with 2502 and 1629 — two quests under way that both want
            // something from that one NPC.
            Assert.Equal("12060a04c613dd0c", Marker(Array.Empty<int>(), new[] { 2502, 1629 }));
        }

        [Fact]
        public void In_progress_comes_before_on_offer()
        {
            // Field order is not free here even though protobuf allows any: these bytes are compared
            // against the capture, and the capture always writes f1 before f3.
            string both = Marker(new[] { 1632 }, new[] { 2502 });

            Assert.StartsWith("12", both);
            Assert.Contains("0a02c613", both);            // f1, packed, 2502
            Assert.Contains("1a02e00c", both);            // f3, packed, 1632
            Assert.True(both.IndexOf("0a02c613", StringComparison.Ordinal)
                        < both.IndexOf("1a02e00c", StringComparison.Ordinal),
                        "las que están en curso van delante de las que se ofrecen");
        }

        [Fact]
        public void An_npc_with_nothing_to_say_writes_no_block_at_all()
        {
            // Not an empty one. The byte pair 1200 — f2 with zero length — appears zero times in
            // the 145 real frames that carry markers: Ankama leaves the field out entirely, and a
            // present-but-empty block is a different message on the wire.
            Assert.Equal("", Marker(Array.Empty<int>(), Array.Empty<int>()));
        }

        [Fact]
        public void Several_quests_on_offer_share_one_packed_field()
        {
            // One length-delimited field with the varints end to end, not one field each. Written
            // the other way the client reads the first and drops the rest.
            string many = Marker(new[] { 1632, 1639 }, Array.Empty<int>());

            Assert.Equal("12061a04e00ce70c", many);
        }
    }
}
