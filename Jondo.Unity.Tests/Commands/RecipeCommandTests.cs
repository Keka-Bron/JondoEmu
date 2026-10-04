using System;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>".receta": the ingredients of a recipe into the bag, and how commands answer.</summary>
    public class RecipeCommandTests
    {
        /// <summary>It makes items out of nothing, like .item: administrators only.</summary>
        [Fact]
        public void A_recipe_is_for_administrators()
            => Assert.Equal(Roles.Administrador, CommandHandler.RequiredRole(".receta"));

        [Theory]
        [InlineData("44", 44, 1)]
        [InlineData(" 44 ", 44, 1)]
        [InlineData("44 5", 44, 5)]
        [InlineData("8231 100", 8231, 100)]
        public void The_item_and_the_times(string rest, int gid, int times)
        {
            Assert.True(CommandHandler.TryParseRecipe(rest, out int g, out int t));
            Assert.Equal(gid, g);
            Assert.Equal(times, t);
        }

        [Theory]
        [InlineData("")]
        [InlineData("espada")]
        [InlineData("-44")]
        [InlineData("0")]
        [InlineData("44 0")]
        [InlineData("44 -2")]
        [InlineData("44 101")]
        [InlineData("44 dos")]
        [InlineData("44 5 6")]
        public void Anything_else_is_the_usage(string rest)
            => Assert.False(CommandHandler.TryParseRecipe(rest, out _, out _));

        /// <summary>The recipe of 44 as the catalogue has it: 3 x 16512 and 3 x 303, twice over.</summary>
        [Fact]
        public void The_ingredients_times_over()
        {
            var recipe = new RecipeDefinition { ResultId = 44, JobId = 11, ResultLevel = 7 };
            recipe.MutableIngredients.Add(new RecipeIngredient(16512, 3));
            recipe.MutableIngredients.Add(new RecipeIngredient(303, 3));

            Assert.Equal(new[] { (16512, 6), (303, 6) }, CommandHandler.IngredientsOf(recipe, 2));
        }

        /// <summary>None of the catalogue's recipes names an item twice, and one that did would get one line for it.</summary>
        [Fact]
        public void An_item_named_twice_is_one_line()
        {
            var recipe = new RecipeDefinition { ResultId = 1 };
            recipe.MutableIngredients.Add(new RecipeIngredient(303, 2));
            recipe.MutableIngredients.Add(new RecipeIngredient(16512, 1));
            recipe.MutableIngredients.Add(new RecipeIngredient(303, 1));

            Assert.Equal(new[] { (303, 3), (16512, 1) }, CommandHandler.IngredientsOf(recipe, 1));
        }

        /// <summary>
        /// A command answers with an information line, lqn type 0 id 0 -- the client's «{0}» --
        /// both zeros left out: only f4, the text. Not a chat line in the player's own name.
        /// </summary>
        [Fact]
        public void An_answer_is_an_information_line()
            => Assert.Equal(Convert.FromHexString("2204686f6c61"), ConnectionProtocol.BuildNotice("hola"));
    }
}
