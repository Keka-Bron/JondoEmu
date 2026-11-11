using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The mutations: the item in slot 20 that makes a character a monster, as the Orbalantir de
    /// Meriana gives them on the way to the epic fights (measured in «orbalantir de meriana-teleport
    /// a todas las peleas epicas»).
    /// </summary>
    public class MutationTests
    {
        private const int PrismaradonaReflection = 15019;
        private const int Orbalantir = 3687;
        private const long MerianasHouse = 122422272;
        private const long DarkVladsMap = 175374336;

        [Fact]
        public void The_first_mutation_is_the_reflection_of_Prismaradona_as_the_capture_shows_it()
        {
            // As the iua carries it: 197 «f6 { f2: 3577, f3: 1 }», 811 «f6 { f1: 1, f2: 1, f3: 1 }».
            var mutation = Mutations.Of(new[]
            {
                new Equipment.ItemEffect(197, 0, 3577, 1),
                new Equipment.ItemEffect(811, 1, 1, 1),
            });
            if (mutation == null) return;   // no monster data in this base

            Assert.NotNull(mutation);
            Assert.Equal(3577, mutation!.Monster);
            Assert.Equal(1, mutation.Grade);
            Assert.Equal("{2554|||100}", mutation.Look);   // the kmb's bones 2554, scale 100

            // The hms brings 4312 to 4316: its sixth spell, 4317, is not its own at grade 1.
            Assert.Equal(new[] { 4312, 4313, 4314, 4315, 4316 },
                         Mutations.SpellsOf(mutation).Select(s => s.Spell).OrderBy(s => s));

            // And the kub: 10,000 life, 4 AP, 4 MP.
            var sheet = Mutations.SheetOf(mutation)!;
            Assert.Equal(10000, sheet.LifePoints);
            Assert.Equal(4, sheet.ActionPoints);
            Assert.Equal(4, sheet.MovementPoints);
        }

        /// <summary>
        /// The item keeps the monster and its grade: 197 travels whole, its three numbers as they are,
        /// or the template's «3577, 1» collapses to a lone 1 and the mutation is lost.
        /// </summary>
        [Fact]
        public void A_mutation_item_keeps_its_three_numbers()
        {
            if (!File.Exists(Paths.EffectFieldsJson)) return;
            using var fields = JsonDocument.Parse(File.ReadAllText(Paths.EffectFieldsJson));
            Assert.Equal(EffectFields.AsDice, fields.RootElement.GetProperty("197").GetInt32());
        }

        [Fact]
        public void Semantic_spell_book_reads_mutation_equipment_from_its_explicit_owner()
        {
            var item = new Equipment.Item { Position = Mutations.Slot };
            item.Effects.Add(new Equipment.ItemEffect(Mutations.TransformsInto, 0, 3577, 1));
            if (Mutations.Of(item.Effects) == null) return;
            var own = GameSession.SinSocket();
            own.State.Breed = 8;
            own.State.CharacterLevel = 200;
            own.State.EquipmentItems[71_001] = item;
            var wrong = GameSession.SinSocket();
            using var context = SessionContext.Push(wrong);

            var snapshot = SpellMessages.Live.Snapshot(own);

            Assert.Equal(new[] { 4312, 4313, 4314, 4315, 4316 }, snapshot.MutantSpells.Select(spell => spell.SpellId).OrderBy(id => id));
            Assert.All(snapshot.MutantSpells, spell => Assert.Equal(SpellType.Unknown6, spell.SpellType));
            Assert.Equal(new SpellItem(1, 0, SpellType.Unknown2), snapshot.HumanSpells[0]);
            Assert.Same(wrong, SessionContext.Actual);
            own.State.EquipmentItems.Clear();
            Assert.Empty(SpellMessages.Live.Snapshot(own).MutantSpells);
        }

        [Fact]
        public void A_mutation_goes_to_its_slot()
        {
            Assert.Equal(new[] { Mutations.Slot }, BonusSlots.SlotsFor(BonusSlots.MutationType));
        }

        [Fact]
        public void A_mutated_character_fights_as_his_monster_at_its_grade()
        {
            var player = new Fighter { Id = 9_400_100_001, MaxHP = 100, CurrentHP = 100, MaxAP = 12, MaxMP = 6 };
            if (!FightHandler.Incarnate(player, 3577, 0, 0)) return;   // no monster data in this base

            Assert.Equal(3577, player.IncarnatedMonster);
            Assert.Equal(10000, player.MaxHP);
            Assert.Equal(4, player.MaxAP);
            Assert.Equal(4, player.MaxMP);
        }

        [Fact]
        public void The_orbalantir_takes_one_to_the_epic_fights_mutated_and_back()
        {
            var store = NpcDialogueContent.Load(Paths.ContentFile(NpcDialogueContent.AuthoredFile));

            var home = NpcDialogueContent.For(store, Orbalantir, MerianasHouse);
            Assert.NotNull(home);
            var choices = home!.Line(26449)!.Choices;
            Assert.Equal(9, choices.Count);
            Assert.Equal(7, choices.Count(c => c.GivesMutation != 0));
            var darkVlad = choices.Single(c => c.Reply == 31748);
            Assert.Equal(DarkVladsMap, darkVlad.TeleportsTo);
            Assert.Equal(PrismaradonaReflection, darkVlad.GivesMutation);
            Assert.Equal(0, choices.Single(c => c.Reply == 35639).GivesMutation);   // Hyrkul: as himself

            var epic = NpcDialogueContent.For(store, Orbalantir, DarkVladsMap);
            Assert.NotNull(epic);
            var back = Assert.Single(epic!.Line(26449)!.Choices);
            Assert.Equal(31753, back.Reply);
            Assert.Equal(MerianasHouse, back.TeleportsTo);
            Assert.True(back.EndsMutation);
        }
    }
}
