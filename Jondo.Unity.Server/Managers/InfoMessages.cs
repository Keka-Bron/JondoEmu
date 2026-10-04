using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los mensajes de información del juego: los que salen en el chat sin que nadie los escriba.
    ///
    /// ─── Cómo funcionan de verdad ───────────────────────────────────────────────────────────
    ///
    /// «Última conexión a esta cuenta realizada el…», «Has ganado 320 kamas», «Misión actualizada»
    /// o «No tienes el nivel de oficio necesario» NO los manda el servidor como texto. El servidor
    /// manda un NÚMERO y el texto lo pone el cliente, ya traducido al idioma del jugador:
    ///
    ///   lqn { f1: tipo, f2: mensaje, f4 (repetido): los parámetros, como cadenas }
    ///
    /// El par (tipo, mensaje) sale de InfoMessagesDataRoot —2.557 entradas— y da un textId que se
    /// resuelve contra Translations. El texto lleva huecos que rellenan los parámetros:
    ///
    ///   tipo 0, id  45  →  «Has ganado $quantity{0} kamas.»
    ///   tipo 0, id  21  →  «Has conseguido {0} '$item{1}'.»
    ///   tipo 1, id 284  →  «No tienes el nivel de oficio necesario.»
    ///
    /// El TIPO no es decorativo: decide cómo lo pinta el cliente. Sale 0 en 1.722 mensajes
    /// —información normal— y 1 en 691 —avisos y errores—, más unos pocos de tipos 2, 4, 6, 7 y 9.
    /// Proto3 se come el cero, y por eso en las capturas unos lqn llevan f1 y otros no.
    ///
    /// ─── La regla ───────────────────────────────────────────────────────────────────────────
    ///
    /// ESTO es lo que hay que usar para decirle algo al jugador. Una línea de chat en su lugar
    /// sale por el CANAL GENERAL y la lee todo el mundo, que es un fallo que ya hubo que quitar
    /// de la recolección.
    ///
    /// Free text that is in no row of the table -- what a command answers, like <c>.teleport</c>
    /// -- goes this way too, through <see cref="FreeText"/>. None of the 452 lqn of the captures
    /// uses that row: the real server has no free text to say.
    /// </summary>
    public static class InfoMessages
    {
        /// <summary>Información normal. Proto3 no manda el campo.</summary>
        public const int Info = 0;

        /// <summary>Aviso o error: el cliente lo pinta distinto.</summary>
        public const int Warning = 1;

        /// <summary>«{0} acaba de volver a conectarse al combate.» Va con <see cref="Warning"/>.</summary>
        public const int BackInTheFight = 184;

        // ─── Los que usa el emulador, con su texto al lado ──────────────────────

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

        /// <summary>«$quantity{2} x {{item,{0},{1}}} ($quantity{3} kamas)», la compra.</summary>
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

        /// <summary>«No tienes el nivel de oficio necesario.» Va con <see cref="Warning"/>.</summary>
        public const int JobLevelTooLow = 284;

        /// <summary>«No tienes el nivel requerido.» Va con <see cref="Warning"/>.</summary>
        /// <remarks>
        /// Not the same sentence as <see cref="JobLevelTooLow"/>, and the difference is the whole
        /// point of having both: 284 says "de oficio". The dungeon door was sending 284 for a
        /// CHARACTER level check, so a level 8 player at a level 10 door was told their profession
        /// was not good enough -- which sends them off to level a trade that has nothing to do with
        /// it. Measured in datos/mensajes_3.6.10.10.json, table 1.
        /// </remarks>
        public const int LevelTooLow = 3;

        /// <summary>«No tienes el objeto necesario.» Va con <see cref="Warning"/>.</summary>
        public const int MissingItem = 4;

        /// <summary>
        /// La plantilla vacía: su texto es literalmente <c>{0}</c>, o sea el parámetro tal cual.
        /// </summary>
        /// <remarks>
        /// Es el hueco por el que se le puede decir al jugador algo que el cliente no trae escrito,
        /// y existe en su propia tabla — tipo 0, id 0 —, así que no es un truco: es el canal que
        /// hay para eso. Sirve para lo que no tiene plantilla, como «la entrada gratis del manojo
        /// vuelve el martes 1/9», que ninguna de las 339.175 frases del cliente dice.
        ///
        /// Va con <see cref="Info"/> o con <see cref="Warning"/>, que es lo único que cambia cómo
        /// lo pinta. Y no es una línea de chat: el chat sale por el canal del mapa y lo lee todo
        /// el mundo.
        ///
        /// Lo que NO hay que hacer con esto es sustituir una plantilla que sí existe. El cliente
        /// está traducido a cinco idiomas y la plantilla lo está con él; lo que se mande por aquí
        /// va en el idioma en que lo escribió quien programó esta línea, y ahí se queda.
        /// </remarks>
        public const int FreeText = 0;

        /// <summary>
        /// «No puedes tener más de <b>{0}</b> invocación(es) al mismo tiempo.» El parámetro 0
        /// es el tope. Va con <see cref="Warning"/>.
        /// </summary>
        /// <remarks>
        /// El tipo importa más de lo normal en éste: en datos/mensajes_3.6.10.10.json el id 203 de
        /// la tabla 0 es «No tienes el nivel requerido», otra frase completamente distinta. Es el
        /// mismo número diciendo dos cosas según con qué tipo se mande, así que mandarlo con
        /// <see cref="Info"/> por descuido le diría al jugador que le falta nivel.
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
        /// Qué dice un mensaje. No se le manda al cliente —él ya lo tiene— pero sirve para que el
        /// registro del servidor diga qué se acaba de enviar en vez de un par de números sueltos.
        /// </summary>
        public static string Text(int type, int id)
        {
            Ensure();
            return _texts.TryGetValue((type, id), out string? text) ? text : $"({type}, {id})";
        }

        /// <summary>¿Existe ese mensaje en el cliente? Mandar uno que no existe no enseña nada.</summary>
        public static bool Exists(int type, int id)
        {
            Ensure();
            return _texts.ContainsKey((type, id));
        }
    }
}
