using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Signs, books and posters, each opening the document the official server opened when it was
    /// clicked (packet captures, 2026-11-20 to 11-24).
    /// </summary>
    public sealed class MeasuredReadableBatch2Tests
    {
        [Theory]
        [InlineData(54171944, 458034, 147)]     // Frigost's Bourgade: «Aviso Oficial»
        [InlineData(54535197, 457739, 148)]     // the farming contest's register
        [InlineData(54536209, 457732, 171)]     // the Bourgade's board: YeCh'Ti
        [InlineData(54536209, 457979, 360)]     // and Conde Traspafrent
        [InlineData(101450251, 477792, 341)]    // the Porkassos plain: «Guia do Efeito Merida»
        [InlineData(207623172, 519677, 628)]    // Pandala: «O conto do guia errante»
        [InlineData(213909518, 541967, 242)]    // Bonta: «A Ordem do Olho Aguçado»
        [InlineData(215876874, 541966, 255)]    // Brâkmar: «A Ordem do Espírito Doentio»
        [InlineData(183766016, 511681, 231)]    // the Ecaflip temple's other book
        public void The_element_opens_the_measured_document(long map, int element, int document)
        {
            Readables.Load();
            Assert.Equal(document, Readables.Of(map, element)?.Document);
        }
    }
}
