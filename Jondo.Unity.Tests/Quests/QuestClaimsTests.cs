using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Quests
{
    /// <summary>
    /// The client's word (idw) finishes a free-text objective nothing on the server watches, and
    /// nothing else: not a fight, not items handed over, not a bound free-text one.
    /// </summary>
    public class QuestClaimsTests
    {
        private static QuestObjective Objective(int type) => new() { Id = 1, StepId = 1, TypeId = type };

        [Fact]
        public void A_free_text_objective_with_no_binding_is_the_clients_to_finish()
        {
            Assert.True(QuestClaims.ClientMayClaim(Objective(QuestCatalogue.FreeText), null));
        }

        [Theory]
        [InlineData(QuestCatalogue.BeatInOneFight)]
        [InlineData(QuestCatalogue.BeatAnywhere)]
        [InlineData(QuestCatalogue.BeatOnMap)]
        [InlineData(QuestCatalogue.BringItems)]
        [InlineData(QuestCatalogue.ShowItems)]
        [InlineData(QuestCatalogue.BringSouls)]
        [InlineData(QuestCatalogue.GoToSee)]
        [InlineData(QuestCatalogue.GoBackToSee)]
        [InlineData(QuestCatalogue.DiscoverMap)]
        [InlineData(QuestCatalogue.CraftItem)]
        public void An_objective_the_server_works_out_is_not_taken_on_the_clients_word(int type)
        {
            // «Vence a los Malters» and «Entrega 20 bayas maléficas» were ticked off by one idw each.
            Assert.False(QuestClaims.ClientMayClaim(Objective(type), null));
        }

        [Theory]
        [InlineData(QuestBindingKind.Beat)]
        [InlineData(QuestBindingKind.Click)]
        [InlineData(QuestBindingKind.Talk)]
        [InlineData(QuestBindingKind.Gather)]
        [InlineData(QuestBindingKind.Use)]
        public void A_free_text_objective_the_server_is_bound_to_is_the_servers(QuestBindingKind kind)
        {
            var binding = new QuestBinding { ObjectiveId = 1, QuestId = 1, Kind = kind };
            Assert.False(QuestClaims.ClientMayClaim(Objective(QuestCatalogue.FreeText), binding));
        }

        [Fact]
        public void An_order_row_says_nothing_about_who_finishes_it()
        {
            var binding = new QuestBinding { ObjectiveId = 1, QuestId = 1, Kind = QuestBindingKind.Order };
            Assert.True(QuestClaims.ClientMayClaim(Objective(QuestCatalogue.FreeText), binding));
        }

        [Fact]
        public void An_objective_the_catalogue_does_not_know_is_not_claimed()
        {
            Assert.False(QuestClaims.ClientMayClaim(null, null));
        }

        [Fact]
        public void The_tutorials_claims_are_still_taken()
        {
            // The guided tutorial's own idw frames: «Aprende a luchar» (19163) and «Equipa a tu
            // personaje con los objetos que has ganado» (19170), both in the captures.
            string path = Paths.ContentFile(QuestBindingContent.AuthoredFile);
            if (!File.Exists(path) || !File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (!book.Ready) return;
            var bindings = QuestBindingContent.Load(path);

            foreach (int objective in new[] { 19163, 19170 })
                Assert.True(QuestClaims.ClientMayClaim(book.Objective(objective), bindings.Of(objective)), $"objective {objective}");
            // And «Gana el combate contra los dos monstruos celestiales» is the fight's to finish.
            Assert.False(QuestClaims.ClientMayClaim(book.Objective(19172), bindings.Of(19172)));
        }
    }
}
