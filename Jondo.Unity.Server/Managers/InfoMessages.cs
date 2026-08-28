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
    /// La excepción es el texto libre que no esté en la tabla —la respuesta de un comando como
    /// <c>.teleport</c>—: eso no se puede mandar por aquí y no queda más remedio que el chat.
    /// </summary>
    public static class InfoMessages
    {
        /// <summary>Información normal. Proto3 no manda el campo.</summary>
        public const int Info = 0;

        /// <summary>Aviso o error: el cliente lo pinta distinto.</summary>
        public const int Warning = 1;

        // ─── Los que usa el emulador, con su texto al lado ──────────────────────

        /// <summary>«Has ganado $quantity{0} kamas.»</summary>
        public const int KamasGained = 45;

        /// <summary>«Has perdido $quantity{0} kamas.»</summary>
        public const int KamasLost = 46;

        /// <summary>«Has conseguido {0} '$item{1}'.»</summary>
        public const int ItemGained = 21;

        /// <summary>«$quantity{2} x {{item,{0},{1}}} ($quantity{3} kamas)», la compra.</summary>
        public const int Purchase = 252;

        /// <summary>«No tienes el nivel de oficio necesario.» Va con <see cref="Warning"/>.</summary>
        public const int JobLevelTooLow = 284;

        /// <summary>«No cumples los requisitos.» Va con <see cref="Warning"/>.</summary>
        public const int RequirementsNotMet = 1;

        /// <summary>«No tienes el nivel requerido.» Va con <see cref="Warning"/>.</summary>
        public const int CharacterLevelTooLow = 3;

        private static readonly Dictionary<(int Type, int Id), string> _texts = new();

        public static int Count => _texts.Count;

        public static void Initialize()
        {
            _texts.Clear();

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
            => _texts.TryGetValue((type, id), out string? text) ? text : $"({type}, {id})";

        /// <summary>¿Existe ese mensaje en el cliente? Mandar uno que no existe no enseña nada.</summary>
        public static bool Exists(int type, int id) => _texts.ContainsKey((type, id));
    }
}
