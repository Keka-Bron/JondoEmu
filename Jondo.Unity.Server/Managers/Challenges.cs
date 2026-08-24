using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Los retos del combate: lo que se elige en la preparación y da un extra al ganar.
    ///
    /// ─── De dónde salen ─────────────────────────────────────────────────────────────────────
    ///
    /// De la tabla del cliente, 842 entradas, con sus nombres y descripciones ya traducidos
    /// —lo hace tools/extraer_retos.py—. Cada reto trae dos criterios en un idioma propio muy
    /// corto: <c>activacion</c> dice cuándo se puede ofrecer y <c>cumplimiento</c> qué hay que
    /// hacer para lograrlo.
    ///
    /// ─── El porcentaje NO está en el cliente ────────────────────────────────────────────────
    ///
    /// La tabla no lleva ninguna bonificación: el porcentaje lo pone el servidor y viaja por el
    /// cable dentro del ldd. Así que aquí sólo hay el de los QUINCE retos que se han visto pasar,
    /// y es su valor base: el mismo reto sale a veces con sesenta puntos más —en la anomalía los
    /// llevan todos—, y ese modificador no se ha podido reconstruir.
    ///
    /// Por eso el emulador ofrece SÓLO esos quince. Ofrecer los ochocientos con un número
    /// inventado sería peor: el jugador vería un reto prometiendo un extra que nadie ha medido.
    ///
    /// ─── Cuándo se puede ofrecer un reto ────────────────────────────────────────────────────
    ///
    /// El criterio de activación explica por qué los poutchs no dan retos: casi todos exigen
    /// <c>GL&gt;4,0</c>, o sea nivel de grupo por encima de cuatro, y contra un poutch de nivel 1
    /// no se llega. Los hay que exigen un monstruo concreto (<c>GM&gt;0,1185,1</c>); ésos no se
    /// ofrecen aquí, porque el servidor real los IMPONE, no los propone, y eso es otra historia.
    /// </summary>
    public static class Challenges
    {
        /// <summary>Un reto de la tabla del cliente.</summary>
        public sealed class Challenge
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public int Category { get; init; }

            /// <summary>Con cuáles no puede convivir. Manda entre los ya fijados, no entre los ofrecidos.</summary>
            public IReadOnlyList<int> Incompatible { get; init; } = Array.Empty<int>();

            /// <summary>Cuándo se puede ofrecer, en el idioma corto de la tabla.</summary>
            public string Activation { get; init; } = "";

            /// <summary>Qué hay que hacer para cumplirlo. Todavía nadie lo comprueba.</summary>
            public string Completion { get; init; } = "";

            /// <summary>El extra que promete, en tanto por ciento. Cero si no se ha medido.</summary>
            public int Percent { get; set; }

            /// <summary>¿El porcentaje sale del cable, o se lo hemos puesto nosotros?</summary>
            public bool PercentMeasured { get; set; }

            /// <summary>Nivel de grupo por encima del cual se puede ofrecer. Cero si no hace falta.</summary>
            public int MinGroupLevel { get; init; }

            /// <summary>Sólo vale dentro de una mazmorra.</summary>
            public bool DungeonOnly { get; init; }

            /// <summary>Exige que en el grupo haya un monstruo concreto.</summary>
            public bool NeedsMonster => Monsters.Count > 0;

            /// <summary>Qué monstruos tienen que estar delante para que este reto exista.</summary>
            public IReadOnlyList<int> Monsters { get; init; } = Array.Empty<int>();

            /// <summary>
            /// ¿Se puede proponer? Hace falta que se haya visto su porcentaje, que no dependa de
            /// una mazmorra ni de un monstruo —esos los IMPONE el contenido, no se proponen— y
            /// que traiga umbral de nivel, que es lo único que aquí se sabe leer del criterio.
            /// </summary>
            public bool Offerable => Percent > 0 && MinGroupLevel > 0 && !DungeonOnly && !NeedsMonster;
        }

        private static readonly Dictionary<int, Challenge> _byId = new();
        private static readonly List<Challenge> _offerable = new();

        /// <summary>Los retos que trae cada monstruo puestos: monstruo → los suyos.</summary>
        private static readonly Dictionary<int, List<Challenge>> _byMonster = new();

        public static int Count => _byId.Count;
        public static int OfferableCount => _offerable.Count;
        public static int WithMonsterCount => _byMonster.Count;

        /// <summary>
        /// Deja en la oferta SÓLO lo que se sabe vigilar, y le pone porcentaje al que no lo tenga.
        ///
        /// Lo que manda es la vigilancia, no el porcentaje. Un reto que nadie comprueba no se
        /// rompe nunca, así que al ganar saldría cumplido y pagaría el extra: ofrecerlo sería
        /// regalar experiencia y botín en cada combate.
        ///
        /// Al revés sí se puede tirar: de un reto que sí se vigila, si su porcentaje no ha pasado
        /// nunca por el cable, se le pone uno. No es medida y queda marcado como tal.
        /// </summary>
        public static void OnlyOffer(IReadOnlyDictionary<int, int> vigilados)
        {
            _offerable.Clear();

            foreach (var reto in _byId.Values)
            {
                if (!vigilados.TryGetValue(reto.Id, out int puesto)) continue;
                if (reto.DungeonOnly || reto.NeedsMonster || reto.MinGroupLevel <= 0) continue;

                reto.PercentMeasured = reto.Percent > 0;
                if (reto.Percent <= 0) reto.Percent = puesto;
                if (reto.Percent > 0) _offerable.Add(reto);
            }

            int medidos = _offerable.FindAll(r => r.PercentMeasured).Count;
            Console.WriteLine($"[Retos] Se ofrecerán {_offerable.Count}: {medidos} con el porcentaje " +
                              $"medido y {_offerable.Count - medidos} con uno puesto por nosotros.");
        }

        public static Challenge? Get(int id) => _byId.TryGetValue(id, out var reto) ? reto : null;

        public static void Initialize()
        {
            _byId.Clear();
            _offerable.Clear();

            string path = Paths.Resolve("retos_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Retos] Falta {Path.GetFileName(path)}; no se ofrecerá ninguno. " +
                                  "Genéralo con tools/extraer_retos.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("retos", out var retos)) return;

                foreach (var entry in retos.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var d = entry.Value;

                    var incompatible = new List<int>();
                    if (d.TryGetProperty("incompatibles", out var lista))
                    {
                        foreach (var uno in lista.EnumerateArray()) incompatible.Add(uno.GetInt32());
                    }

                    var reto = new Challenge
                    {
                        Id = id,
                        Name = Text(d, "nombre"),
                        Description = Text(d, "descripcion"),
                        Category = d.TryGetProperty("categoria", out var c) ? c.GetInt32() : 0,
                        Incompatible = incompatible,
                        Activation = Text(d, "activacion"),
                        Completion = Text(d, "cumplimiento"),
                        Percent = LowestSeen(d),
                        MinGroupLevel = d.TryGetProperty("nivel_umbral", out var u)
                                        && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
                        DungeonOnly = d.TryGetProperty("solo_mazmorra", out var m)
                                      && m.ValueKind == JsonValueKind.True,
                        Monsters = Monsters(d),
                    };

                    _byId[id] = reto;
                    if (reto.Offerable) _offerable.Add(reto);
                    if (reto.NeedsMonster)
                    {
                        foreach (int bicho in reto.Monsters)
                        {
                            if (!_byMonster.TryGetValue(bicho, out var suyos))
                            {
                                suyos = new List<Challenge>();
                                _byMonster[bicho] = suyos;
                            }
                            suyos.Add(reto);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Retos] No se han podido leer: {ex.Message}");
                return;
            }

            Console.WriteLine($"[Retos] {_byId.Count} retos, {_offerable.Count} ofrecibles " +
                              "(los que tienen porcentaje medido).");
        }

        /// <summary>
        /// Los retos que IMPONE el contenido: los que exigen un monstruo que está delante.
        ///
        /// Éstos no se proponen, se ponen, y llegan con el extra a cero. Está medido en la
        /// anomalía: el jugador eligió uno de los dos normales, el servidor rellenó el que
        /// faltaba, y detrás mandó tres kww más —772 Duelo, 773 Prudente y 774 Superviviente—
        /// que no se habían ofrecido nunca y que van sin porcentaje. Los tres exigen el monstruo
        /// 5781, que era justo el de esa anomalía.
        ///
        /// Son los que llevan logro detrás, así que se le quitan al personaje que ya los tenga
        /// hecho: un logro se hace una vez.
        /// </summary>
        public static IReadOnlyList<Challenge> Imposed(IEnumerable<int> monsters,
                                                       IReadOnlyCollection<int> alreadyDone)
        {
            var salida = new List<Challenge>();
            var puestos = new HashSet<int>();

            foreach (int bicho in monsters)
            {
                if (!_byMonster.TryGetValue(bicho, out var suyos)) continue;
                foreach (var reto in suyos)
                {
                    if (alreadyDone.Contains(reto.Id)) continue;
                    if (!puestos.Add(reto.Id)) continue;
                    salida.Add(reto);
                }
            }
            return salida;
        }

        private static List<int> Monsters(JsonElement d)
        {
            var salida = new List<int>();
            if (d.TryGetProperty("monstruos_requeridos", out var lista)
                && lista.ValueKind == JsonValueKind.Array)
            {
                foreach (var uno in lista.EnumerateArray())
                {
                    if (uno.ValueKind == JsonValueKind.Number) salida.Add(uno.GetInt32());
                }
            }
            return salida;
        }

        private static string Text(JsonElement d, string campo)
            => d.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
               ? (v.GetString() ?? "") : "";

        /// <summary>
        /// El porcentaje base de un reto: el MÁS BAJO de los que se le han visto.
        ///
        /// Se coge el más bajo porque el mismo reto sale a veces con sesenta puntos de más —el 6
        /// a 90 y a 150, el 40 a 65 y a 125, el 971 a 80 y a 140—, así que el alto lleva dentro
        /// un modificador del combate que no se ha sabido reconstruir.
        ///
        /// Dos de los dieciséis se quedan altos por fuerza: del 9 y del 969 sólo hay una lectura,
        /// y las dos son de peleas donde los demás retos también iban subidos. Lo más probable es
        /// que su base sea sesenta menos, pero eso ya sería deducir, así que va lo medido.
        /// </summary>
        private static int LowestSeen(JsonElement d)
        {
            if (!d.TryGetProperty("porcentajes_vistos", out var vistos)) return 0;

            int menor = 0;
            foreach (var uno in vistos.EnumerateArray())
            {
                if (!uno.TryGetProperty("exp", out var e)) continue;
                int valor = e.GetInt32();
                if (valor > 0 && (menor == 0 || valor < menor)) menor = valor;
            }
            return menor;
        }

        /// <summary>
        /// Dos candidatos para proponer, o ninguno si no hay de dónde sacarlos.
        ///
        /// Son ALTERNATIVAS entre sí, así que no hace falta que sean compatibles el uno con el
        /// otro —en las capturas se ofrecieron juntos dos que la tabla marca como incompatibles—.
        /// Lo que sí se respeta es lo que ya está FIJADO: contra eso sí manda la lista.
        /// </summary>
        public static IReadOnlyList<Challenge> Pair(int groupLevel, IReadOnlyCollection<int> alreadyFixed,
                                                    Random dado)
        {
            var pool = new List<Challenge>();
            foreach (var reto in _offerable)
            {
                if (groupLevel <= reto.MinGroupLevel) continue;
                if (alreadyFixed.Contains(reto.Id)) continue;
                if (ClashesWithFixed(reto, alreadyFixed)) continue;
                pool.Add(reto);
            }

            if (pool.Count == 0) return Array.Empty<Challenge>();
            if (pool.Count == 1) return new[] { pool[0] };

            int uno = dado.Next(pool.Count);
            int otro = dado.Next(pool.Count - 1);
            if (otro >= uno) otro++;
            return new[] { pool[uno], pool[otro] };
        }

        /// <summary>¿Choca con alguno de los ya fijados? La incompatibilidad va en los dos sentidos.</summary>
        private static bool ClashesWithFixed(Challenge reto, IReadOnlyCollection<int> alreadyFixed)
        {
            foreach (int fijado in alreadyFixed)
            {
                if (reto.Incompatible.Contains(fijado)) return true;
                var otro = Get(fijado);
                if (otro != null && otro.Incompatible.Contains(reto.Id)) return true;
            }
            return false;
        }
    }
}
