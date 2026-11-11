using System.Collections.Concurrent;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The characters who walk as a soul until a phoenix brings them back: Warko Linch's brew in «El
    /// cementerio primitivo» (485), which kills whoever drinks it and leaves him beside the primitive
    /// cemetery, «Renace junto al fénix del cementerio primitivo».
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 3.6 does not kill anybody on its own any more -- a lost fight only costs energy (Translations
    /// 1156704) --, but the client still knows the phantom: the life state 2 (kup), the popup
    /// «Ahora formas parte del mundo de los espíritus» (1094418), and sixteen phoenix statues, graphic
    /// 7521, with their «Resucitar» (skill 211), mostly in the cemeteries.
    /// </para>
    /// <para>
    /// The look is the souls' own: bones 3, the body 322 or 323 and a face, as the client's NPCs and
    /// monsters that are a soul wear it -- Guzmán Onelsako {3|322,341||47}, a female one
    /// {3|323,333||48}.
    /// </para>
    /// <para>
    /// Kept in memory: a server started again finds him alive, and Warko's brew can be drunk again
    /// for as long as the phoenix has not been reached.
    /// </para>
    /// </remarks>
    public static class Phantoms
    {
        /// <summary>The phoenix statues' graphic.</summary>
        public const int PhoenixGfx = 7521;

        /// <summary>«Resucitar», the phoenix's skill.</summary>
        public const int ResurrectSkill = 211;

        private static readonly ConcurrentDictionary<long, long> _phoenixOf = new();

        /// <summary>Whether a character walks as a soul.</summary>
        public static bool Is(long characterId) => _phoenixOf.ContainsKey(characterId);

        /// <summary>The map of the phoenix a soul is sent to, or zero.</summary>
        public static long PhoenixOf(long characterId)
            => _phoenixOf.TryGetValue(characterId, out long map) ? map : 0;

        /// <summary>A character turned into a soul, the phoenix that brings him back on <paramref name="phoenixMap"/>.</summary>
        public static void Become(long characterId, long phoenixMap) => _phoenixOf[characterId] = phoenixMap;

        /// <summary>A soul alive again: false when he was none.</summary>
        public static bool Rise(long characterId) => _phoenixOf.TryRemove(characterId, out _);

        /// <summary>A soul's look, the souls' body by sex (1 female).</summary>
        public static string LookOf(int sex)
            => sex == 1 ? "{3|323,333||48}" : "{3|322,341||47}";
    }
}
