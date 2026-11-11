using System.Collections.Generic;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;
using Kind = Jondo.Unity.Server.Managers.LinePlaceholders.Kind;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// An NPC line's «#1», read off its text: the character's name, breed or level, or nothing
    /// this server can tell.
    /// </summary>
    public class LinePlaceholdersTests
    {
        [Theory]
        [InlineData("¡Te doy la bienvenida, #1! A este lugar sagrado...", Kind.Name)]
        [InlineData("Tú eres #1, ¿verdad? Me han hablado", Kind.Name)]
        [InlineData("#1... #1. ¿Por qué me desafías?", Kind.Name)]
        [InlineData("Con la excusa de ser un $breed#1, crees que todo te está permitido", Kind.Breed)]
        [InlineData("Soy de nivel #1.", Kind.Level)]
        [InlineData("no creas que tu nivel #1 me impresiona", Kind.Level)]
        public void What_the_line_says_is_filled(string text, Kind kind)
            => Assert.Equal(kind, LinePlaceholders.KindOf(text));

        [Theory]
        [InlineData("¿Quieres volver a $map#1?")]
        [InlineData("vuelve dentro de #1 hora/s si quieres seguir")]
        [InlineData("Vuelve dentro de #1 h, ya veremos")]
        [InlineData("te costará #1 kamas")]
        [InlineData("Actualmente, el resultado es #1 puntos.")]
        [InlineData("Soy uno de los #5 recaudadores del gremio #1.")]
        [InlineData("Una frase sin nada que rellenar.")]
        [InlineData("")]
        public void What_it_cannot_tell_is_left_alone(string text)
            => Assert.Equal(Kind.None, LinePlaceholders.KindOf(text));

        [Fact]
        public void Each_kind_takes_the_characters_value()
        {
            Assert.Equal("Someone", LinePlaceholders.ValueOf(Kind.Name, "Someone", 9, 135));
            Assert.Equal("9", LinePlaceholders.ValueOf(Kind.Breed, "Someone", 9, 135));
            Assert.Equal("135", LinePlaceholders.ValueOf(Kind.Level, "Someone", 9, 135));
            Assert.Null(LinePlaceholders.ValueOf(Kind.None, "Someone", 9, 135));
        }

        /// <summary>
        /// A line with a name and a reply that starts a quest: the reply keeps its effect's
        /// argument (f2 of its f3, the quest) beside the line's f3.
        /// </summary>
        [Fact]
        public void A_named_line_keeps_its_replies_arguments()
        {
            var parameters = new Dictionary<long, IReadOnlyList<long>> { [19328] = new long[] { 34 } };
            var arguments = new Dictionary<long, IReadOnlyList<IReadOnlyList<long>>>
            {
                [19328] = new IReadOnlyList<long>[] { new long[] { 1425 } },
            };
            byte[] with = ConnectionProtocol.BuildNpcQuestion(16285, new long[] { 19328 }, parameters,
                                                              new[] { "Someone" }, arguments);
            byte[] without = ConnectionProtocol.BuildNpcQuestion(16285, new long[] { 19328 }, parameters, arguments);
            Assert.Equal(without, with[..without.Length]);
            Assert.True(with.Length > without.Length);
        }
    }
}
