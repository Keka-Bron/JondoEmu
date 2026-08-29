using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Finding a guardian's "use the keyring" and "hand over the key" replies among everything else.
    /// </summary>
    /// <remarks>
    /// Driven by Mawy Ingals' real declared replies, copied out of NpcTemplates 780 with their
    /// translation keys. She guards the Granero del Girasol Hambriento, and she is the case that
    /// exposed this: the player saw one option, "No.", out of the nineteen she declares.
    ///
    /// She is also the awkward case rather than the easy one, which is why she is the fixture. She
    /// declares each of the two sentences TWICE -- once for her dungeon and once for its Expedición
    /// version -- so a naive "find the wording" returns two answers and has to choose.
    /// </remarks>
    public class DungeonDoorRepliesTests
    {
        // (reply id, translation key), verbatim from NpcTemplates 780.
        private static readonly (long Reply, long Text)[] Mawy =
        {
            (6615, 29432),      // Utilizar el manojo de llaves.
            (10474, 21218),     // Cuéntame más cosas sobre los alrededores, por favor.
            (8870, 24268),      // Retomar la Mazmorra de los Campos donde la dejaste.
            (11162, 27151),     // Teletransportar a los miembros del grupo.
            (2802, 386714),     // Darle la llave y entrar.
            (36911, 749485),    // Hablemos del recrudecimiento de parásitos.
            (74101, 1070585),   // Me gustaría salir de expedición.
            (20906, 425815),    // Sí.
            (20907, 425806),    // No.
            (8869, 24319),      // Sí, quiero volver a esta sala.
            (20904, 425811),    // Sí.
            (20905, 425817),    // No.
            (36910, 749484),    // Ir a visitar a Megustam Laspelas.
            (74097, 1070580),   // Retomar la Mazmorra de los Campos donde la dejaste. (expedición)
            (74100, 1070584),   // Darle la llave y entrar.                            (expedición)
            (15920, 366337),    // Salir.
            (74096, 1070579),   // Sí, quiero volver a esta sala.                      (expedición)
            (74098, 1070582),   // Sí.                                                 (expedición)
            (74099, 1070583),   // No.                                                 (expedición)
        };

        // The translation keys those two sentences have, for Mawy. The real table has 126 keys
        // carrying the keyring sentence across the game; only hers are needed here.
        private static readonly HashSet<long> KeyringTexts = new HashSet<long> { 29432, 1070586 };
        private static readonly HashSet<long> KeyTexts = new HashSet<long> { 386714, 1070584 };

        private static DungeonDoor.Options? PickFrom((long Reply, long Text)[] rows)
            => DungeonDoor.Pick(rows.Select(r => r.Reply).ToArray(),
                                rows.Select(r => r.Text).ToArray(),
                                KeyringTexts, KeyTexts);

        [Fact]
        public void Mawy_offers_the_keyring_and_the_key_of_her_own_dungeon()
        {
            var options = PickFrom(Mawy);

            Assert.NotNull(options);
            Assert.Equal(6615, options!.Keyring);
            Assert.Equal(2802, options.Key);
        }

        [Fact]
        public void And_not_the_expedition_ones()
        {
            // 74100 says the same sentence and opens a different dungeon: the Expedición version,
            // which the data marks as not taking the keyring at all. Handing it to the client here
            // would send the player into the wrong run.
            var options = PickFrom(Mawy);

            Assert.NotEqual(74100, options!.Key);
            Assert.True(options.Key < DungeonDoor.Expedition);
        }

        [Fact]
        public void Two_of_the_same_wording_is_not_reported_as_a_clean_pick()
        {
            // Mawy has one of each once the Expedición block is dropped, so this is honest work
            // rather than a guess. The 15 guardians that keep more than one after that guard
            // several base dungeons, and this flag is what tells them apart in the log.
            Assert.False(PickFrom(Mawy)!.Ambiguous);

            var twice = new[] { (100L, 29432L), (200L, 29432L), (300L, 386714L) };
            var options = DungeonDoor.Pick(twice.Select(r => r.Item1).ToArray(),
                                           twice.Select(r => r.Item2).ToArray(),
                                           KeyringTexts, KeyTexts);

            Assert.True(options!.Ambiguous);
            Assert.Equal(100, options.Keyring);   // the lowest, which is the older content
        }

        [Fact]
        public void An_npc_that_says_neither_is_not_a_guardian()
        {
            var ordinary = new[] { (10474L, 21218L), (20907L, 425806L) };

            Assert.Null(DungeonDoor.Pick(ordinary.Select(r => r.Item1).ToArray(),
                                         ordinary.Select(r => r.Item2).ToArray(),
                                         KeyringTexts, KeyTexts));
        }

        [Fact]
        public void A_guardian_with_only_a_keyring_reply_still_counts()
        {
            // 50 of the 119 are like this: they take the keyring and never declare a "hand over the
            // key" line. Returning null for them would take the keyring option away from most of
            // the dungeons that accept it.
            var onlyKeyring = new[] { (6615L, 29432L), (20907L, 425806L) };

            var options = DungeonDoor.Pick(onlyKeyring.Select(r => r.Item1).ToArray(),
                                           onlyKeyring.Select(r => r.Item2).ToArray(),
                                           KeyringTexts, KeyTexts);

            Assert.NotNull(options);
            Assert.Equal(6615, options!.Keyring);
            Assert.Equal(0, options.Key);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Mismatched_or_empty_lists_do_not_throw(int howMany)
        {
            // The two arrays are filled in the same loop, so they should always match; "should"
            // is not a reason to index past the end of one of them.
            Assert.Null(DungeonDoor.Pick(new long[howMany], System.Array.Empty<long>(),
                                         KeyringTexts, KeyTexts));
            Assert.Null(DungeonDoor.Pick(null!, null!, KeyringTexts, KeyTexts));
        }
    }
}
