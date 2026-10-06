using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The game's information messages: the ones that come out in the chat without anyone writing them.
    ///
    /// ─── How they really work ───────────────────────────────────────────────────────────────
    ///
    /// «Última conexión a esta cuenta realizada el…», «Has ganado 320 kamas», «Misión actualizada»
    /// or «No tienes el nivel de oficio necesario» are NOT sent by the server as text. The server
    /// sends a NUMBER and the client supplies the text, already translated into the player's language:
    ///
    ///   lqn { f1: type, f2: message, f4 (repeated): the parameters, as strings }
    ///
    /// The (type, message) pair comes from InfoMessagesDataRoot —2,557 entries— and gives a textId that is
    /// resolved against Translations. The text carries holes the parameters fill:
    ///
    ///   type 0, id  45  →  «Has ganado $quantity{0} kamas.»
    ///   type 0, id  21  →  «Has conseguido {0} '$item{1}'.»
    ///   type 1, id 284  →  «No tienes el nivel de oficio necesario.»
    ///
    /// The TYPE is not decorative: it decides how the client draws it. It is 0 in 1,722 messages
    /// —normal information— and 1 in 691 —warnings and errors—, plus a few of types 2, 4, 6, 7 and 9.
    /// Proto3 swallows the zero, and that is why in the captures some lqn carry f1 and others do not.
    ///
    /// ─── The rule ───────────────────────────────────────────────────────────────────────────
    ///
    /// THIS is what must be used to tell the player something. A chat line instead goes out
    /// through the GENERAL CHANNEL and everybody reads it, which is a bug that already had to be removed
    /// from gathering.
    ///
    /// Free text that is in no row of the table -- what a command answers, like <c>.teleport</c>
    /// -- goes this way too, through <see cref="FreeText"/>. None of the 452 lqn of the captures
    /// uses that row: the real server has no free text to say.
    /// </summary>
    public static class InfoMessages
    {
        /// <summary>Normal information. Proto3 does not send the field.</summary>
        public const int Info = 0;

        /// <summary>Warning or error: the client draws it differently.</summary>
        public const int Warning = 1;

        /// <summary>«{0} acaba de volver a conectarse al combate.» Goes with <see cref="Warning"/>.</summary>
        public const int BackInTheFight = 184;

        // ─── The ones the emulator uses, with their text alongside ──────────────

        /// <summary>«Has ganado $quantity{0} kamas.»</summary>
        public const int KamasGained = 45;

        /// <summary>«Has perdido $quantity{0} kamas.»</summary>
        public const int KamasLost = 46;

        /// <summary>
        /// «Has perdido &lt;b&gt;{0}&lt;/b&gt; puntos de energía.» Info, the amount as its one parameter:
        /// "lqn { f2: 34, f4: "2000" }" right behind the kub of every lost fight against monsters.
        /// </summary>
        public const int EnergyLost = 34;

        /// <summary>«Has conseguido {0} '$item{1}'.»</summary>
        public const int ItemGained = 21;

        /// <summary>«$quantity{2} x {{item,{0},{1}}} ($quantity{3} kamas)», the purchase.</summary>
        public const int Purchase = 252;

        // ─── The marketplaces' ────────────────────────────────────────────────────
        //
        // No capture refuses anything or sells anything of the player's, so these are the rows of
        // the client's own table that say it, and which one goes where is INFERRED. The refusals
        // go with Warning: table 0 repeats the same sentences under other ids (57, 58, 59, 61,
        // 63, 64), and table 1 is the one of warnings and errors.

        /// <summary>«Este objeto no forma parte de las categorías aceptadas en este mercadillo.» Warning.</summary>
        public const int MarketplaceWrongCategory = 64;

        /// <summary>«No tienes suficientes kamas para poder pagar el impuesto de puesta en venta...» Warning.</summary>
        public const int MarketplaceCannotPayTax = 65;

        /// <summary>«No puedes poner más objetos en venta por el momento...» Warning.</summary>
        public const int MarketplaceTooManyListings = 66;

        /// <summary>«No tienes suficientes kamas para poder comprar este objeto.» Warning.</summary>
        public const int MarketplaceCannotAfford = 71;

        /// <summary>«Este objeto ya no está disponible por este precio. Alguien ha sido más rápido...» Warning.</summary>
        public const int MarketplaceSoldOut = 72;

        /// <summary>
        /// «Banco: + $quantity{0} kamas (venta: $quantity{3} $item{2}).» Info, to the seller of a
        /// lot. Its twin <see cref="MarketplaceSoldLinked"/> is the one sent now.
        /// </summary>
        public const int MarketplaceSold = 73;

        /// <summary>
        /// «Banco: + $quantity{0} kamas ({{salesHistory,0,false,true::venta}}: $quantity{3}
        /// $item{2}).» Info, to the seller of a lot: the same words as 73, with "venta" a link
        /// that opens the sales history on this session's sales. Which of the twins the real
        /// server sends is INFERRED: this is the one with a history behind it.
        /// </summary>
        public const int MarketplaceSoldLinked = 65;

        /// <summary>«No tienes el nivel de oficio necesario.» Goes with <see cref="Warning"/>.</summary>
        public const int JobLevelTooLow = 284;

        /// <summary>«No tienes el nivel requerido.» Goes with <see cref="Warning"/>.</summary>
        /// <remarks>
        /// Not the same sentence as <see cref="JobLevelTooLow"/>, and the difference is the whole
        /// point of having both: 284 says "de oficio". The dungeon door was sending 284 for a
        /// CHARACTER level check, so a level 8 player at a level 10 door was told their profession
        /// was not good enough -- which sends them off to level a trade that has nothing to do with
        /// it. Measured in datos/mensajes_3.6.10.10.json, table 1.
        /// </remarks>
        public const int LevelTooLow = 3;

        /// <summary>«No tienes el objeto necesario.» Goes with <see cref="Warning"/>.</summary>
        public const int MissingItem = 4;

        /// <summary>
        /// The empty template: its text is literally <c>{0}</c>, that is the parameter as is.
        /// </summary>
        /// <remarks>
        /// It is the hole through which the player can be told something the client does not bring written,
        /// and it exists in its own table — type 0, id 0 —, so it is not a trick: it is the channel there
        /// is for that. It serves for what has no template, like «la entrada gratis del manojo
        /// vuelve el martes 1/9», which none of the client's 339,175 sentences says.
        ///
        /// It goes with <see cref="Info"/> or with <see cref="Warning"/>, which is the only thing that changes how
        /// it is drawn. And it is not a chat line: the chat goes out through the map's channel and everybody
        /// reads it.
        ///
        /// What must NOT be done with this is replace a template that does exist. The client
        /// is translated into five languages and the template is translated with it; what is sent this way
        /// goes in the language whoever programmed this line wrote it in, and there it stays.
        /// </remarks>
        public const int FreeText = 0;

        /// <summary>
        /// «No puedes tener más de <b>{0}</b> invocación(es) al mismo tiempo.» Parameter 0
        /// is the cap. Goes with <see cref="Warning"/>.
        /// </summary>
        /// <remarks>
        /// The type matters more than usual in this one: in datos/mensajes_3.6.10.10.json id 203 of
        /// table 0 is «No tienes el nivel requerido», a completely different sentence. It is the
        /// same number saying two things depending on the type it is sent with, so sending it with
        /// <see cref="Info"/> by mistake would tell the player he lacks level.
        /// </remarks>
        public const int SummonLimitReached = 203;

        private static readonly Dictionary<(int Type, int Id), string> _texts = new();

        /// <summary>
        /// Whether the table is filled in and safe to read. Volatile because the fast path in
        /// <see cref="Ensure"/> reads it outside the lock, and raised LAST so a reader never
        /// lands on a half-filled dictionary.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        public static int Count { get { Ensure(); return _texts.Count; } }

        /// <summary>
        /// Reads the file, once per run. Kept as a separate call so the server pays for it at
        /// boot, with its log line, rather than on whoever first writes to the chat.
        /// </summary>
        /// <remarks>
        /// CALLING IT AGAIN DOES NOTHING, ON PURPOSE. It used to clear the table and re-read the
        /// file with no lock, and anyone reading during that window was told a message that
        /// exists does not -- measured, on every read that overlapped. Nothing here changes while
        /// the server is up: the json is extracted from the client once.
        /// </remarks>
        public static void Initialize() => Ensure();

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Load();
                }
                finally
                {
                    // In a finally so a missing file counts as tried, and we do not go back to
                    // the disk for every line the server writes to the chat.
                    _loaded = true;
                }
            }
        }

        private static void Load()
        {
            string path = Paths.Resolve("mensajes_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Mensajes] Falta {Path.GetFileName(path)}; los avisos seguirán " +
                                  "saliendo, pero el registro no dirá qué dicen. " +
                                  "Genéralo con tools/extraer_mensajes.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("mensajes", out var byType)) return;

                foreach (var type in byType.EnumerateObject())
                {
                    if (!int.TryParse(type.Name, out int typeId)) continue;
                    foreach (var message in type.Value.EnumerateObject())
                    {
                        if (!int.TryParse(message.Name, out int id)) continue;
                        _texts[(typeId, id)] = message.Value.GetString() ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mensajes] No se han podido leer: {ex.Message}");
                return;
            }

            Console.WriteLine($"[Mensajes] {_texts.Count} mensajes de información.");
        }

        /// <summary>
        /// What a message says. It is not sent to the client —it already has it— but it lets the
        /// server's log say what has just been sent instead of a couple of loose numbers.
        /// </summary>
        public static string Text(int type, int id)
        {
            Ensure();
            return _texts.TryGetValue((type, id), out string? text) ? text : $"({type}, {id})";
        }

        /// <summary>Does that message exist in the client? Sending one that does not exist shows nothing.</summary>
        public static bool Exists(int type, int id)
        {
            Ensure();
            return _texts.ContainsKey((type, id));
        }
    }
}
