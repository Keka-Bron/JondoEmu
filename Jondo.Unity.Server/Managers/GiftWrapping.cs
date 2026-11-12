namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The gift-wrapping workshop: «Envolver un regalo» (skill 209), the table of Rubén Paket Hado's
    /// tent at the Trool Fair. It has no recipe in the game's data, because what it makes depends on
    /// the paper and not on a list: a wrapping paper and any one thing, one of each, make the present
    /// the paper names, and the thing goes into it.
    /// </summary>
    /// <remarks>
    /// The paper says which present it makes in its own effect 1083, the present's template as its
    /// value: the birthday paper (12900) makes the birthday present (13142), the wedding paper
    /// (12899) the wedding one (13141), and so on for the fifteen papers. The present carries
    /// «Envuelto por: #4» (effect 1082) with the wrapper's name. Rubén's own words: «Primero, uno de
    /// los numerosos papeles de regalo que hay en mi tienda. Cuando lo tengas, usa uno de los talleres
    /// de empaquetado para envolver el objeto» (11401); Dofus pour les noobs, «La foire aux gobs»:
    /// the paper and «une ressource sans grand intérêt (vous la perdrez)».
    /// </remarks>
    public static class GiftWrapping
    {
        /// <summary>«Envolver un regalo».</summary>
        public const int Skill = 209;

        /// <summary>A paper's effect: the present it makes, as its value.</summary>
        public const int WrapsInto = 1083;

        /// <summary>«Envuelto por: #4», the wrapper's name on the present.</summary>
        public const int WrappedBy = 1082;

        /// <summary>The present a wrapping paper makes, or zero when the item is no paper.</summary>
        public static int PresentOf(int template)
        {
            var read = Forgemagic.TemplateOf(template);
            if (read == null) return 0;
            foreach (var line in read.Lines)
            {
                if (line.Effect == WrapsInto) return line.Value;
            }
            return 0;
        }
    }
}
