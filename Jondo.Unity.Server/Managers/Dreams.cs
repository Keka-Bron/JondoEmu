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
        private static readonly int[] Filas = { 1, 2, 3, 4, 1 };

        /// <summary>La dificultad más alta, Pesadilla III.</summary>
        public const int MaximaDificultad = 10;

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

            /// <summary>El mapa de ese grupo, que es a donde se teletransporta al jugador.</summary>
            public long MapaId { get; set; }

            /// <summary>La casilla donde está plantado el grupo.</summary>
            public int Casilla { get; set; }

            /// <summary>El efecto que modifica la sala, y cuánto. Cero: sin modificación.</summary>
            public int Efecto { get; set; }
            public int Valor { get; set; }

            /// <summary>Si ya se ha peleado aquí.</summary>
            public bool Hecha { get; set; }
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

            /// <summary>Los puntos acumulados. Suben al limpiar una sala.</summary>
            public int Puntos { get; set; }

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
        private static List<(int Id, long MapaId, int Casilla, int Nivel)>? _grupos;
        private static readonly object _candado = new object();

        public static int Activos => _enCurso.Count;

        public static Sueno? De(long characterId)
            => _enCurso.TryGetValue(characterId, out var s) ? s : null;

        public static void Olvidar(long characterId) => _enCurso.TryRemove(characterId, out _);

        /// <summary>Cuántos grupos hay disponibles para plantar en las salas.</summary>
        public static int GruposDisponibles { get { Cargar(); return _grupos?.Count ?? 0; } }

        private static void Cargar()
        {
            if (_grupos != null) return;
            lock (_candado)
            {
                if (_grupos != null) return;
                var grupos = new List<(int, long, int, int)>();

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
                                    lector.IsDBNull(2) ? 0 : lector.GetInt32(2), nivel));
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

            var sueno = new Sueno
            {
                CharacterId = characterId,
                Nombre = nombre,
                Nivel = nivel,
                Dificultad = Math.Clamp(dificultad, 1, MaximaDificultad),
                MapaDeVuelta = mapaDeVuelta,
                CasillaDeVuelta = casillaDeVuelta,
            };

            // El rombo: una sala en la primera fila, dos en la siguiente, y así hasta cerrar.
            int siguiente = 0;
            var porFila = new List<List<Sala>>();
            for (int fila = 0; fila < Filas.Length; fila++)
            {
                var deLaFila = new List<Sala>();
                for (int i = 0; i < Filas[fila]; i++)
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

                for (int i = 0; i < esta.Count; i++)
                {
                    esta[i].Salidas.Add(abajo[Math.Min(i, abajo.Count - 1)].Id);
                    if (abajo.Count > 1 && i + 1 < abajo.Count && !esta[i].Salidas.Contains(abajo[i + 1].Id))
                    {
                        esta[i].Salidas.Add(abajo[i + 1].Id);
                    }
                }
            }

            foreach (var sala in sueno.Salas)
            {
                if (sala.Id == 0 || sala.Fila == Filas.Length - 1) continue;
                Poblar(sala, nivel, sueno.Dificultad);
            }

            _enCurso[characterId] = sueno;
            return sueno;
        }

        /// <summary>Le pone a una sala su grupo y su modificación.</summary>
        /// <remarks>
        /// El grupo se elige entre los que andan por el nivel del personaje, con una banda que se
        /// abre si no hay bastantes: los Sueños se juegan a partir del 50 y hay tramos del mundo
        /// donde no hay grupos de ese nivel exacto.
        /// </remarks>
        private static void Poblar(Sala sala, int nivel, int dificultad)
        {
            var candidatos = new List<(int Id, long MapaId, int Casilla, int Nivel)>();

            for (int banda = 20; banda <= 200 && candidatos.Count == 0; banda += 40)
            {
                foreach (var g in _grupos!)
                {
                    if (Math.Abs(g.Nivel - nivel) <= banda) candidatos.Add(g);
                }
            }
            if (candidatos.Count == 0) return;

            (int Id, long MapaId, int Casilla, int Nivel) elegido;
            lock (_azar) elegido = candidatos[_azar.Next(candidatos.Count)];

            sala.Grupo = elegido.Id;
            sala.MapaId = elegido.MapaId;
            sala.Casilla = elegido.Casilla;

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
        internal static void OlvidarTodo() => _enCurso.Clear();
    }
}
