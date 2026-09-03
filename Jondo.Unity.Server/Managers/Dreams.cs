using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los Sueños Infinitos: el mapa de un sueño y por dónde va cada jugador.
    /// </summary>
    /// <remarks>
    /// Es la versión del POZO, la refundición que convirtió los Sueños en un roguelite: eliges
    /// dificultad, te dan un mapa de salas con bifurcaciones, y en cada sala hay un grupo y una
    /// modificación. Las anteriores funcionaban de otra manera y no valen de referencia.
    ///
    /// Todo lo de aquí sale de las trece capturas de <c>Sueños Infinitos/</c>. El mensaje que abre
    /// la ventana, el iyj, trae DOS listas y son la clave del asunto:
    ///
    /// <code>
    ///   las salas    f1 = "0".."10"
    ///                  f6   la fila del grafo
    ///                  f9   MapMobs.Id, un grupo de monstruos REAL del mundo
    ///                  f10  el efecto que modifica la sala      f11  su valor
    ///   el grafo     0 -> 1,2   1 -> 3,4   2 -> 4,5   3 -> 6,7
    ///                4 -> 7,8   5 -> 8,9   6..9 -> 10
    /// </code>
    ///
    /// Que dibuja un rombo de once salas en cinco filas —1, 2, 3, 4, 1— y no un árbol: a la sala 4
    /// se llega desde la 1 y desde la 2.
    ///
    ///   MEDIDO en la captura de Paradoja I, sala por sala: la fila que dice el f6 de cada una
    ///   coincide exactamente con la que le toca en el grafo. Los cinco f9 de esa partida
    ///   —14931, 14812, 15026, 14798, 14797— son filas de MapMobs con su mapa, su casilla y sus
    ///   miembros; y los f10 —118, 119, 125, 126— son efectos de nuestro propio catálogo: fuerza,
    ///   agilidad, vitalidad e inteligencia.
    ///
    /// La dificultad va de 1 a 10 y la numeración también está medida, comparando el ixf de nueve
    /// capturas contra el nombre que el jugador eligió en cada una:
    ///
    /// <code>
    ///   1..3   Sueño I, II, III            8..10  Pesadilla I, II, III
    ///   4..7   Paradoja I, II, III, IV
    /// </code>
    /// </remarks>
    public static class Dreams
    {
        /// <summary>Cuántas salas hay en cada fila del rombo. Medido sobre el grafo del iyj.</summary>
        private static readonly int[] Filas = { 1, 3, 3, 3, 1 };

        /// <summary>Lo que puede medir una fila de en medio. Medido de 2 a 4 en nueve capturas.</summary>
        private const int MinimoPorFila = 2;
        private const int MaximoPorFila = 4;

        /// <summary>Y lo que suman las tres juntas: de 7 a 9, o sea sueños de 9, 10 u 11 salas.</summary>
        private const int MinimoDeEnMedio = 7;
        private const int MaximoDeEnMedio = 9;

        /// <summary>
        /// Con cuántos puntos de sueño se empieza, por dificultad.
        /// </summary>
        /// <remarks>
        /// Medido en el f22 de los 39 izg de las capturas, y sale limpio: una dificultad, un
        /// valor, sin una sola discrepancia.
        ///
        ///   1: 50   2: 75   3: 100   4: 120   5: 140
        ///   6: 160  7: 190  8: 220   9: 250  10: 300
        ///
        /// El f22 es la dotación de salida y no se mueve; el f8 es el total de ahora y SÍ sube.
        /// Se ve en Pesadilla III: los dos valen 300 en la sala 0 y el f8 pasa a 315 en la 2,
        /// justo los quince de una sala de clase 15. Y en la captura de la invitación, donde el
        /// jugador lleva 58 salas hechas, el f8 va por 168 con el f22 todavía en 120.
        /// </remarks>
        private static readonly int[] PuntosPorDificultad =
        {
            0, 50, 75, 100, 120, 140, 160, 190, 220, 250, 300,
        };

        /// <summary>La dotación de salida de una dificultad. Es el f22 del izg.</summary>
        public static int PuntosDeSalida(int dificultad)
            => dificultad >= 1 && dificultad < PuntosPorDificultad.Length
                ? PuntosPorDificultad[dificultad]
                : PuntosPorDificultad[1];

        /// <summary>La dificultad más alta, Pesadilla III.</summary>
        public const int MaximaDificultad = 10;

        /// <summary>El mapa del Plano Astral, que es donde está el pozo.</summary>
        /// <remarks>
        /// Medido: es a donde lleva el jru que sigue al iyc del botón del menú, y en nuestra propia
        /// base es la subárea 938, «Dominios de Draconiros».
        /// </remarks>
        public const long MapaDelPozo = 238551040;

        /// <summary>El pozo, que en los datos del cliente es un elemento más de ese mapa.</summary>
        /// <remarks>
        /// El 539616, gráfico 90166, casilla 370. Está en el mapa desde siempre; lo que faltaba era
        /// declararle una acción, porque sin ella el cliente no lo deja pulsar y queda de adorno.
        /// </remarks>
        public const int ElementoDelPozo = 539616;

        /// <summary>La habilidad con la que se usa el pozo.</summary>
        /// <remarks>
        /// El 20743 del iwo «0887a20110e0f720» NO es esto. Es el uid de instancia, y confundir uno
        /// con otro es lo que dejó el pozo sin pulsar: anunciábamos la habilidad 20743, que el
        /// cliente no conoce, y un elemento cuya habilidad no existe no se puede clicar y no da un
        /// solo error. El f11 del jss real del mapa lo dice campo a campo:
        ///
        ///   f11 { f1: 1, f4 { f1: 20744, f2: 360 }, f4 { f1: 20743, f2: 184 }, f5: 539616, f6: -1 }
        ///
        /// El f4.f1 es el uid —lo que el cliente devuelve en el iwo— y el f4.f2 la habilidad. La
        /// 184 es la misma con la que ya se entra en una casa y se usa la lotería, así que el
        /// cliente la conoce de sobra. El uid nuestro lo pone Interactives.SkillInstanceOf y el
        /// cliente lo devuelve tal cual, así que no hace falta copiar el suyo.
        /// </remarks>
        public const int HabilidadDelPozo = 184;

        /// <summary>El tipo de interactivo del pozo y de las arcadas: el f6 del f11, medido en -1.</summary>
        public const int TipoDelPozo = -1;

        /// <summary>La segunda acción del pozo, la del f4 { 20744, 360 }.</summary>
        /// <remarks>
        /// El pozo ofrece DOS cosas, no una: en las 22 tramas jss del mapa 238551040 que hay en las
        /// trece capturas —las 22 idénticas— van dos f4, el de la habilidad 184 y éste. Declarar
        /// sólo uno deja al jugador con media carta.
        ///
        /// Qué contesta el servidor real a ésta no se ha medido: en las capturas nadie la pulsa,
        /// las once veces que se usa el pozo van por la 184. Aquí abre la misma ventana, que es lo
        /// único que sabemos hacer con el pozo, y queda dicho que es una suposición.
        /// </remarks>
        public const int SegundaHabilidadDelPozo = 360;

        /// <summary>El mapa de la sala de entrada de todo sueño.</summary>
        /// <remarks>
        /// Medido en las diez capturas que empiezan un sueño: el jru que sigue al primer izg lleva
        /// siempre aquí, sin excepción. Las salas de pelea vienen después y ésas sí cambian.
        /// </remarks>
        public const long MapaDeEntrada = 237897728;

        /// <summary>La subárea donde viven las salas: 484 mapas hechos para esto.</summary>
        /// <remarks>
        /// Los nueve mapas de sala que aparecen en las capturas —237764608, 237765632, 237766656,
        /// 237767680, 237768704, 237765684, 237765686, 237777980 y 237774854— están todos aquí, y
        /// también la entrada. Cada uno lleva EXACTAMENTE tres elementos interactivos con el
        /// gráfico 90166, que son las tres puertas a la fila de abajo.
        ///
        /// Esto es lo que faltaba para que entrar en un sueño no fuese un viaje a Frigost: se
        /// estaba mandando al jugador al mapa del grupo de monstruos, que es un mapa del mundo.
        /// </remarks>
        public const int SubareaDeLasSalas = 904;

        /// <summary>Cuántas puertas tiene una sala. Tres en los 100 mapas que las traen.</summary>
        public const int PuertasPorSala = 3;

        /// <summary>La sala de Draconiros, al otro lado de cualquiera de las cuatro arcadas.</summary>
        /// <remarks>
        /// No es vecina de la del pozo en la rejilla —una está en (0,0) y la otra en (1,-1)— así que
        /// no se llega andando: se llega pulsando una arcada. Sin declararlas, Draconiros está bien
        /// colocado y es inalcanzable, que para el jugador es lo mismo que no estar.
        /// </remarks>
        public const long MapaDeDraconiros = 238553348;

        /// <summary>Las dos clases de sala del f3: 5 en 63 salas medidas, 15 en 8.</summary>
        private const int ClaseNormal = 5;
        private const int ClaseSenalada = 15;

        /// <summary>Cuántos sueños se le han ofrecido a cada personaje, para el f13.</summary>
        private static readonly Dictionary<long, int> _cuenta = new Dictionary<long, int>();

        public sealed class Sala
        {
            /// <summary>Su número, que en el cable viaja como CADENA: «0», «1»…</summary>
            public int Id { get; init; }

            /// <summary>La fila del rombo, de 0 a 4. Es el f6 del iyj.</summary>
            public int Fila { get; init; }

            /// <summary>A qué salas se puede ir desde aquí.</summary>
            public List<int> Salidas { get; } = new List<int>();

            /// <summary>La fila de MapMobs que se pelea aquí. Cero en la entrada.</summary>
            public int Grupo { get; set; }

            /// <summary>Los monstruos de ese grupo, con su grado, para plantarlos en la sala.</summary>
            public List<(int Monstruo, int Grado)> Miembros { get; } = new List<(int, int)>();

            /// <summary>El grupo ya plantado en el mapa de la sala, para poder quitarlo.</summary>
            public long Plantado { get; set; }

            /// <summary>El mapa del mundo donde vive ese grupo. NO es a donde se va el jugador.</summary>
            /// <remarks>
            /// Se guarda para poder plantar la pelea con los monstruos que le tocan; mandarle a él
            /// allí es lo que le dejaba en mitad de Frigost con el minimapa apagado.
            /// </remarks>
            public long MapaId { get; set; }

            /// <summary>El mapa de la subárea 904 en el que ocurre esta sala.</summary>
            public long MapaDeLaSala { get; set; }

            /// <summary>La casilla donde está plantado el grupo.</summary>
            public int Casilla { get; set; }

            /// <summary>El efecto que modifica la sala, y cuánto. Cero: sin modificación.</summary>
            public int Efecto { get; set; }
            public int Valor { get; set; }

            /// <summary>Si ya se ha peleado aquí.</summary>
            public bool Hecha { get; set; }

            /// <summary>Los puntos de sueño que da limpiarla: el f1 de la sala en el iyj.</summary>
            /// <remarks>
            /// Medido entre 4 y 40 sobre las 89 salas de las nueve capturas, sin una regla clara
            /// que lo ate al nivel ni a la fila. Aquí se reparte por fila, que es lo único que se
            /// ve subir con ella.
            /// </remarks>
            public int Puntos { get; set; }

            /// <summary>La clase de recompensa: el f3. Medido 5 en 63 salas y 15 en 8.</summary>
            public int Clase { get; set; }

            /// <summary>Sala señalada. El f7, que vale 1 en 8 de las 89 y siempre con Clase 15.</summary>
            public bool Senalada { get; set; }

            /// <summary>Si en esta sala está el Rey Gob en vez de un grupo de monstruos.</summary>
            /// <remarks>
            /// La guía la llama Favor Onírico. En la captura larga es el npc 7850 en la casilla
            /// 232 del mapa 237783053, y no hay protocolo nuevo: se habla con él como con
            /// cualquier NPC y lo que da va escrito en su respuesta.
            /// </remarks>
            public bool EsFavor { get; set; }
        }

        public sealed class Sueno
        {
            public long CharacterId { get; init; }
            public string Nombre { get; init; } = "";
            public int Nivel { get; init; }
            public int Dificultad { get; init; }

            public List<Sala> Salas { get; } = new List<Sala>();

            /// <summary>En qué sala está. Empieza en la cero, que es la entrada.</summary>
            public int Actual { get; set; }

            /// <summary>Los puntos de AHORA: la dotación de salida más lo ganado. Es el f8.</summary>
            public int Puntos { get; set; }

            /// <summary>La dotación de salida, que no se mueve en todo el sueño. Es el f22.</summary>
            public int PuntosDeSalida { get; init; }

            /// <summary>Tormentas astrales que quedan. Es el f7, y el número del botón.</summary>
            public int Tormentas { get; set; } = 1;

            /// <summary>Arenas de Draconiros: los reintentos. Es el f19.</summary>
            /// <remarks>
            /// La guía dice que en dificultad Sueño se empieza con una, y la ventana de la captura
            /// de pantalla lo confirma —«Arena de Draconiros: 1» en un Sueño I nuevo y 0 en el que
            /// estaba en curso—. Que se gaste al morir no está implementado.
            /// </remarks>
            public int Arena { get; set; } = 1;

            /// <summary>Cuántos sueños se le han ofrecido ya. Es el f13 del iyj.</summary>
            /// <remarks>
            /// Las nueve capturas son del mismo personaje y el f13 vale 1, 2, 3, 4, 5, 6, 8, 9 y
            /// 10, en el orden en que se grabaron. O sea: una cuenta, no un identificador.
            /// </remarks>
            public int Cuenta { get; init; }

            /// <summary>Dónde estaba en el mundo antes de entrar, para devolverlo al salir.</summary>
            public long MapaDeVuelta { get; init; }
            public int CasillaDeVuelta { get; init; }

            public Sala? SalaActual => Buscar(Actual);

            public Sala? Buscar(int id)
            {
                foreach (var s in Salas) if (s.Id == id) return s;
                return null;
            }
        }

        private static readonly ConcurrentDictionary<long, Sueno> _enCurso = new();
        private static readonly Random _azar = new Random();

        /// <summary>Los grupos que se pueden plantar en una sala, por nivel.</summary>
        /// <remarks>
        /// Se leen una vez y se quedan: son 38.744 filas y consultarlas por sala sería una lectura
        /// completa por bifurcación. Sólo interesan el mapa, la casilla y el nivel del grupo.
        /// </remarks>
        private static List<(int Id, long MapaId, int Casilla, int Nivel, string Miembros)>? _grupos;
        private static readonly object _candado = new object();

        public static int Activos => _enCurso.Count;

        public static Sueno? De(long characterId)
            => _enCurso.TryGetValue(characterId, out var s) ? s : null;

        /// <summary>Se acabó el sueño: se olvida, y con él los grupos que dejó plantados.</summary>
        /// <remarks>
        /// Lo segundo importa tanto como lo primero. Los mapas de sala son cien y se reparten
        /// entre todos los sueños; un grupo que no se quita se queda ahí para el siguiente que
        /// caiga en ese mapa, y se va acumulando sala tras sala hasta que la sala tiene monstruos
        /// de tres sueños ajenos.
        /// </remarks>
        public static void Olvidar(long characterId)
        {
            if (!_enCurso.TryRemove(characterId, out var sueno)) return;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado == 0) continue;
                MobSpawnManager.RemoveMobGroup(sala.MapaDeLaSala, sala.Plantado);
                sala.Plantado = 0;
            }
        }

        /// <summary>De donde salio cada uno hacia el Plano Astral.</summary>
        /// <remarks>
        /// Se apunta al pulsar el boton del menu, que es el ultimo momento en que se sabe: dentro
        /// del plano y de las salas el mapa de la sesion ya es otro. Sin esto, salir del sueno
        /// dejaria al jugador en el plano en vez de donde estaba.
        /// </remarks>
        private static readonly ConcurrentDictionary<long, (long Mapa, int Casilla)> _deDonde = new();

        public static void RecordarDeDondeViene(long characterId, long mapa, int casilla)
            => _deDonde[characterId] = (mapa, casilla);

        public static (long Mapa, int Casilla) DeDondeViene(long characterId)
            => _deDonde.TryGetValue(characterId, out var d) ? d : (0, 0);

        /// <summary>Cuántos grupos hay disponibles para plantar en las salas.</summary>
        public static int GruposDisponibles { get { Cargar(); return _grupos?.Count ?? 0; } }

        private static void Cargar()
        {
            if (_grupos != null) return;
            lock (_candado)
            {
                if (_grupos != null) return;
                var grupos = new List<(int, long, int, int, string)>();

                try
                {
                    using var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                        DatabaseManager.WorldConnectionString);
                    conexion.Open();

                    var orden = conexion.CreateCommand();
                    orden.CommandText = "SELECT Id, MapId, CellId, MembersJson FROM MapMobs;";

                    using var lector = orden.ExecuteReader();
                    while (lector.Read())
                    {
                        if (lector.IsDBNull(3)) continue;

                        int nivel = NivelDe(lector.GetString(3));
                        if (nivel <= 0) continue;

                        grupos.Add((lector.GetInt32(0), lector.GetInt64(1),
                                    lector.IsDBNull(2) ? 0 : lector.GetInt32(2), nivel,
                                    lector.GetString(3)));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Sueños] No se pudieron leer los grupos: {ex.Message}");
                }

                _grupos = grupos;
            }
        }

        /// <summary>El nivel de un grupo: el del miembro más alto, que es lo que lo hace difícil.</summary>
        /// <summary>Los monstruos de un grupo, con el grado con el que salen en el mundo.</summary>
        private static List<(int Monstruo, int Grado)> MiembrosDe(string miembros)
        {
            var salen = new List<(int, int)>();
            try
            {
                using var doc = JsonDocument.Parse(miembros);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (!m.TryGetProperty("id", out var id) || !id.TryGetInt32(out int monstruo)) continue;
                    int grado = m.TryGetProperty("grade", out var g) && g.TryGetInt32(out int n) ? n : 0;
                    salen.Add((monstruo, grado));
                }
            }
            catch (Exception) { salen.Clear(); }
            return salen;
        }

        private static int NivelDe(string miembros)
        {
            int mayor = 0;
            try
            {
                using var doc = JsonDocument.Parse(miembros);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (m.TryGetProperty("level", out var n) && n.TryGetInt32(out int nivel))
                    {
                        if (nivel > mayor) mayor = nivel;
                    }
                }
            }
            catch (Exception) { return 0; }
            return mayor;
        }

        public static void Initialize()
        {
            Cargar();
            Console.WriteLine($"[Sueños] {GruposDisponibles} grupos para plantar en las salas.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Montar un sueño
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Genera un sueño nuevo: el rombo de once salas, con su grupo y su modificación.
        /// </summary>
        /// <remarks>
        /// La entrada y la última no llevan grupo — en la captura la sala «0» viaja con un solo
        /// campo y la «10» sin f9 —, así que sólo se puebla lo de en medio.
        /// </remarks>
        public static Sueno Crear(long characterId, string nombre, int nivel, int dificultad,
                                  long mapaDeVuelta, int casillaDeVuelta)
        {
            Cargar();

            // Empezar uno nuevo tira el anterior, que es lo que hace el cliente al confirmar. Va
            // por Olvidar para que se lleve por delante los grupos que dejó plantados.
            Olvidar(characterId);

            _cuenta.TryGetValue(characterId, out int cuenta);
            _cuenta[characterId] = ++cuenta;

            var sueno = new Sueno
            {
                CharacterId = characterId,
                Nombre = nombre,
                Nivel = nivel,
                Dificultad = Math.Clamp(dificultad, 1, MaximaDificultad),
                Cuenta = cuenta,
                PuntosDeSalida = PuntosDeSalida(Math.Clamp(dificultad, 1, MaximaDificultad)),
                Puntos = PuntosDeSalida(Math.Clamp(dificultad, 1, MaximaDificultad)),
                MapaDeVuelta = mapaDeVuelta,
                CasillaDeVuelta = casillaDeVuelta,
            };

            // Cinco filas: la entrada, tres de entre dos y cuatro salas, y la última. El ancho de
            // las de en medio cambia de un sueño a otro —nueve capturas y siete repartos
            // distintos— así que se sortea, con una semilla que hace el sueño reproducible.
            var dado = new Random(HashCode.Combine(characterId, cuenta));

            // El total de las tres filas de en medio va de siete a nueve —los sueños medidos
            // tienen nueve, diez u once salas—, así que no vale sortear cada fila por su cuenta:
            // tres tiradas libres de 2 a 4 dan de seis a doce. Se reparte un total.
            var anchos = new int[] { MinimoPorFila, MinimoPorFila, MinimoPorFila };
            int sobran = dado.Next(MinimoDeEnMedio, MaximoDeEnMedio + 1) - MinimoPorFila * 3;
            while (sobran > 0)
            {
                int donde = dado.Next(anchos.Length);

                // La primera fila nunca pasa de tres, y no por gusto: la entrada abre a TODAS sus
                // salas y un mapa de sala sólo trae tres puertas. Con cuatro, una quedaría sin
                // puerta que la abriese. Medido además: en las nueve capturas la primera fila
                // tiene dos o tres, nunca cuatro.
                int tope = donde == 0 ? PuertasPorSala : MaximoPorFila;
                if (anchos[donde] >= tope) continue;

                anchos[donde]++;
                sobran--;
            }

            int siguiente = 0;
            var porFila = new List<List<Sala>>();
            for (int fila = 0; fila < Filas.Length; fila++)
            {
                int cuantas = Filas[fila] == 1 ? 1 : anchos[fila - 1];

                var deLaFila = new List<Sala>();
                for (int i = 0; i < cuantas; i++)
                {
                    var sala = new Sala { Id = siguiente++, Fila = fila };
                    deLaFila.Add(sala);
                    sueno.Salas.Add(sala);
                }
                porFila.Add(deLaFila);
            }

            // Y las salidas. Cada sala se abre a la de su misma posición en la fila siguiente y a
            // la de al lado, que es lo que hace que la de en medio se alcance por dos caminos: en
            // la captura a la 4 se llega desde la 1 y desde la 2.
            for (int fila = 0; fila + 1 < porFila.Count; fila++)
            {
                var esta = porFila[fila];
                var abajo = porFila[fila + 1];

                // La entrada abre a TODA la fila siguiente —«0 -> 1,2,3» en la captura— y la fila
                // de encima de la última lleva entera a la última —«7,8,9 -> 10»—. Las dos cosas
                // están en las nueve.
                if (esta.Count == 1 || abajo.Count == 1)
                {
                    foreach (var origen in esta)
                    {
                        foreach (var destino in abajo) origen.Salidas.Add(destino.Id);
                    }
                    continue;
                }

                for (int i = 0; i < esta.Count; i++)
                {
                    int primero = i * abajo.Count / esta.Count;
                    esta[i].Salidas.Add(abajo[primero].Id);
                    if (primero + 1 < abajo.Count) esta[i].Salidas.Add(abajo[primero + 1].Id);
                }

                // Y que no quede ninguna sin padre. Una sala a la que no se puede llegar se dibuja
                // igual en la ventana, y el jugador la ve y no entiende por qué no la alcanza.
                for (int j = 0; j < abajo.Count; j++)
                {
                    if (esta.Exists(x => x.Salidas.Contains(abajo[j].Id))) continue;

                    // Al que tenga sitio: ninguna sala puede ofrecer más salidas que puertas hay
                    // en su mapa, o la de más no se podría pulsar.
                    var padre = esta.Find(x => x.Salidas.Count < PuertasPorSala)
                                ?? esta[Math.Min(j, esta.Count - 1)];
                    padre.Salidas.Add(abajo[j].Id);
                }
            }

            RepartirMapas(sueno, dado);

            foreach (var sala in sueno.Salas)
            {
                if (sala.Fila == 0 || sala.Fila == Filas.Length - 1) continue;
                Poblar(sala, nivel, sueno.Dificultad);

                // Y lo que la ventana enseña de ella. La clase 15 y la marca del f7 van juntas en
                // las ocho salas de las nueve capturas que las llevan, así que aquí también.
                // Una sala de Favor por fila de en medio, la última de la fila. La guía dice que
                // la Fuente sale en las filas 2, 3 y 4; aquí se reparte una por fila, que es lo
                // que reproduce el ritmo sin inventar una regla que no se ha medido.
                sala.EsFavor = sala.Fila >= 2 && EsLaUltimaDeSuFila(sueno, sala);
                if (sala.EsFavor) sala.Miembros.Clear();

                sala.Senalada = sala.Fila == Filas.Length - 2 && sala.Id % 3 == 0;
                sala.Clase = sala.Senalada ? ClaseSenalada : ClaseNormal;
                sala.Puntos = sala.Fila * 5 + (sala.Senalada ? 15 : 5);
            }

            _enCurso[characterId] = sueno;
            return sueno;
        }

        /// <summary>
        /// A cada sala, un mapa de los suyos.
        /// </summary>
        /// <remarks>
        /// La entrada es siempre el 237897728 —diez de diez capturas— y las demás salen del
        /// catálogo de la subárea 904, cogiendo sólo los que traen sus tres puertas. Sin repetir
        /// dentro de un mismo sueño: dos salas en el mismo mapa harían que sus puertas fueran las
        /// mismas y el camino dejaría de significar nada.
        /// </remarks>
        private static void RepartirMapas(Sueno sueno, Random dado)
        {
            var libres = new List<long>(MapasDeSala());
            if (libres.Count == 0) return;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Fila == 0)
                {
                    sala.MapaDeLaSala = MapaDeEntrada;
                    continue;
                }

                int i = dado.Next(libres.Count);
                sala.MapaDeLaSala = libres[i];
                libres.RemoveAt(i);

                if (libres.Count == 0) libres.AddRange(MapasDeSala());
            }
        }

        private static List<long>? _mapasDeSala;

        /// <summary>Los mapas de sala: subárea 904 y con sus tres puertas.</summary>
        /// <remarks>
        /// La subárea trae 484 mapas y sólo 100 llevan elementos interactivos. Los que los llevan
        /// llevan exactamente tres, con el gráfico 90166 —el mismo del pozo—, que son las puertas.
        /// Un mapa de sala sin puertas sería un callejón del que no se puede salir.
        /// </remarks>
        /// <summary>Todos los mapas del sueño, la entrada incluida, para declararles las puertas.</summary>
        public static IEnumerable<long> TodosLosMapasDeSala()
        {
            yield return MapaDeEntrada;
            foreach (long mapa in MapasDeSala()) yield return mapa;
        }

        private static List<long> MapasDeSala()
        {
            if (_mapasDeSala != null) return _mapasDeSala;

            var deLaSubarea = new HashSet<long>();
            try
            {
                using var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                conexion.Open();

                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT MapId FROM MapSubareas WHERE SubAreaId = $sub;";
                orden.Parameters.AddWithValue("$sub", SubareaDeLasSalas);

                using var lector = orden.ExecuteReader();
                while (lector.Read()) deLaSubarea.Add(lector.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sueños] No se han podido leer los mapas de sala: {ex.Message}");
            }

            var salen = new List<long>();
            foreach (long mapId in deLaSubarea)
            {
                if (mapId == MapaDeEntrada) continue;
                if (Interactives.ElementsOf(mapId).Count < PuertasPorSala) continue;
                salen.Add(mapId);
            }

            salen.Sort();

            // Vacío NO se guarda. Si esto se pide antes de que Interactives esté cargado la lista
            // sale vacía, y cachearla dejaría todos los sueños de la sesión sin mapas de sala.
            if (salen.Count == 0) return salen;

            _mapasDeSala = salen;
            Console.WriteLine($"[Sueños] {salen.Count} mapas de sala en la subárea {SubareaDeLasSalas}.");
            return _mapasDeSala;
        }

        /// <summary>La puerta número <paramref name="cual"/> de una sala, o cero si no la tiene.</summary>
        public static int PuertaDe(Sala sala, int cual)
        {
            if (sala.MapaDeLaSala == 0) return 0;

            var elementos = Interactives.ElementsOf(sala.MapaDeLaSala);
            if (cual < 0 || cual >= elementos.Count) return 0;
            return elementos[cual].Id;
        }

        private static bool EsLaUltimaDeSuFila(Sueno sueno, Sala sala)
        {
            int mayor = -1;
            foreach (var otra in sueno.Salas)
            {
                if (otra.Fila == sala.Fila && otra.Id > mayor) mayor = otra.Id;
            }
            return sala.Id == mayor;
        }

        /// <summary>El Rey Gob del Favor Onírico, y dónde se pone.</summary>
        /// <remarks>
        /// Medido en «sueño infinito largo»: npc 7850, casilla 232, orientación 3, con el id
        /// contextual negativo de siempre. Su diálogo está en content/npcs/dialogues.json.
        /// </remarks>
        public const int ReyGob = 7850;
        public const int CasillaDelReyGob = 232;
        public const int OrientacionDelReyGob = 3;

        /// <summary>Le pone a una sala su grupo y su modificación.</summary>
        /// <remarks>
        /// El grupo se elige entre los que andan por el nivel del personaje, con una banda que se
        /// abre si no hay bastantes: los Sueños se juegan a partir del 50 y hay tramos del mundo
        /// donde no hay grupos de ese nivel exacto.
        /// </remarks>
        private static void Poblar(Sala sala, int nivel, int dificultad)
        {
            var candidatos = new List<(int Id, long MapaId, int Casilla, int Nivel, string Miembros)>();

            for (int banda = 20; banda <= 200 && candidatos.Count == 0; banda += 40)
            {
                foreach (var g in _grupos!)
                {
                    if (Math.Abs(g.Nivel - nivel) <= banda) candidatos.Add(g);
                }
            }
            if (candidatos.Count == 0) return;

            (int Id, long MapaId, int Casilla, int Nivel, string Miembros) elegido;
            lock (_azar) elegido = candidatos[_azar.Next(candidatos.Count)];

            sala.Grupo = elegido.Id;
            sala.MapaId = elegido.MapaId;
            sala.Casilla = elegido.Casilla;

            sala.Miembros.Clear();
            sala.Miembros.AddRange(MiembrosDe(elegido.Miembros));

            // La modificación. En la captura son efectos de bonificación de característica —fuerza,
            // agilidad, vitalidad, inteligencia— con su valor. Se saca del mismo catálogo que usa
            // el motor de hechizos, así que no hay nada inventado.
            var efectos = EfectosDeSala();
            if (efectos.Count == 0) return;

            lock (_azar)
            {
                sala.Efecto = efectos[_azar.Next(efectos.Count)];
                // Más dificultad, más regalo: es lo que hace que valga la pena subir.
                sala.Valor = _azar.Next(1, 20 + dificultad * 10);
            }
        }

        private static List<int>? _efectosDeSala;

        /// <summary>Los efectos que pueden modificar una sala.</summary>
        /// <remarks>
        /// Los cuatro medidos en la captura son de característica —118 fuerza, 119 agilidad,
        /// 125 vitalidad, 126 inteligencia— así que se usan esos cuatro y no los 114 de
        /// bonificación que trae el catálogo: de los demás no se ha visto ni uno.
        /// </remarks>
        private static List<int> EfectosDeSala()
        {
            if (_efectosDeSala != null) return _efectosDeSala;
            _efectosDeSala = new List<int> { 118, 119, 125, 126 };
            return _efectosDeSala;
        }

        /// <summary>Sólo para las pruebas.</summary>
        internal static void OlvidarTodo()
        {
            _enCurso.Clear();
            _cuenta.Clear();
        }
    }
}
