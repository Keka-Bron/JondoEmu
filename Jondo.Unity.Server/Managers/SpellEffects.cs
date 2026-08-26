using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Una entrada del EffectsJson de un hechizo, tal cual viene, sin interpretar.
    ///
    /// El emulador ya leía este JSON, pero se quedaba sólo con tres cosas —el daño, el empuje y las
    /// características— y tiraba el resto: el disparador, la máscara del objetivo y el
    /// identificador del efecto. Sin esas tres no se puede hacer nada de lo que hacen los dofus ni
    /// los boosts, así que aquí se conserva la entrada entera.
    /// </summary>
    public sealed class SpellEffect
    {
        public int EffectId { get; init; }
        public int EffectUid { get; init; }
        public int Value { get; init; }
        public int DiceNum { get; init; }
        public int DiceSide { get; init; }
        public int Duration { get; init; }

        /// <summary>
        /// El RETARDO en turnos: el efecto no arranca al lanzarlo, sino tantas rondas después.
        ///
        /// Esta clave del catálogo no se leía en ninguna parte del emulador, y por eso la Flecha
        /// Castigadora estaba rota: sus efectos retardados se aplicaban en el acto y se caían a la
        /// ronda siguiente, así que como el hechizo sólo se puede lanzar una vez por turno, el
        /// bono nacía y moría dentro del mismo turno y no servía absolutamente de nada.
        ///
        /// Medido contra hechizos cuyo texto lo dice: Precipitación lleva delay 1 —«en el turno
        /// siguiente»— y Palabra Secreta delay 2 —«dentro de 2 turnos»—.
        /// </summary>
        public int Delay { get; init; }

        public int Element { get; init; }

        /// <summary>Si se puede disipar. Va al cliente restándole uno, que es como se midió.</summary>
        public int Dispellable { get; init; }

        /// <summary>Cuándo salta: "I" en el acto, "TB" al empezar el turno, "TE" al acabarlo,
        /// "DBE" cuando le pegan... Un efecto puede traer varios separados por barras.</summary>
        public string Triggers { get; init; } = "I";

        /// <summary>A quién va: "C" a quien lo lanza, "a"/"A" a los de enfrente, y con "e519" o
        /// "E519" pegado, sólo si NO tiene o SÍ tiene ese estado.</summary>
        public string TargetMask { get; init; } = "";

        /// <summary>
        /// La FORMA de la zona, que es una letra guardada como su código: 'P' un punto, 'C' un
        /// círculo, 'X' una cruz, 'L' una línea... y <see cref="Tamano"/> es su radio o su largo.
        ///
        /// Sin esto, un hechizo de zona sólo tocaba a quien estuviera justo en la casilla apuntada:
        /// el Ojo de Topo enseñaba la previsualización sobre los dos pious y luego no le hacía
        /// nada al segundo.
        /// </summary>
        public int Forma { get; init; } = 'P';
        public int Tamano { get; init; } = 1;

        /// <summary>Si la zona se corta al llegar al objetivo, para las líneas.</summary>
        public bool ParaEnElObjetivo { get; init; }

        /// <summary>
        /// Cuánto daño pierde por cada casilla que uno esté alejado del centro de la zona, en
        /// tanto por ciento, y cuántas casillas como mucho se cuentan.
        ///
        /// Sale del <c>zoneDescr</c> y va POR HECHIZO: dieciséis de los efectos de zona del Ocra
        /// llevan un diez por ciento con tope de cuatro pasos, y otros siete lo llevan a cero, o
        /// sea que pegan lo mismo en todo su alcance —Diamantes Destructores es de ésos—.
        /// </summary>
        public int PasoDeCaida { get; init; }
        public int TopeDeCaida { get; init; }

        /// <summary>
        /// La probabilidad de que a este efecto le toque, en tanto por ciento, y el sorteo al que
        /// pertenece.
        ///
        /// Es lo que hace que Invocación de Arakna saque una Arakna corriente el ochenta por
        /// ciento de las veces y una Arakna mayor el veinte: son DOS efectos 181, uno con la
        /// plantilla 246 y un random de 80, y otro con la 2630 y un random de 20. Sin mirar esto
        /// salían las dos a la vez.
        /// </summary>
        public double Probabilidad { get; init; }
        public int Sorteo { get; init; }

        public IEnumerable<string> Disparadores()
        {
            if (string.IsNullOrEmpty(Triggers)) { yield return "I"; yield break; }
            foreach (var t in Triggers.Split('|'))
            {
                string limpio = t.Trim();
                if (limpio.Length > 0) yield return limpio;
            }
        }
    }

    /// <summary>
    /// Los efectos de un hechizo, en un grado, leídos de SpellLevels.
    ///
    /// Se cachea por (hechizo, grado) porque durante un combate se piden muchas veces y la tabla no
    /// cambia mientras el servidor está levantado.
    /// </summary>
    public static class SpellEffects
    {
        private static readonly Dictionary<(int, int), List<SpellEffect>> _cache
            = new Dictionary<(int, int), List<SpellEffect>>();

        private static readonly Dictionary<(int, int), List<SpellEffect>> _criticos
            = new Dictionary<(int, int), List<SpellEffect>>();

        private static readonly object _candado = new object();

        public static IReadOnlyList<SpellEffect> De(int hechizo, int grado)
            => Leer(hechizo, grado).Normales;

        public static IReadOnlyList<SpellEffect> Criticos(int hechizo, int grado)
            => Leer(hechizo, grado).Criticos;

        private static (List<SpellEffect> Normales, List<SpellEffect> Criticos)
            Leer(int hechizo, int grado)
        {
            var clave = (hechizo, Math.Max(1, grado));
            lock (_candado)
            {
                if (_cache.TryGetValue(clave, out var ya)) return (ya, _criticos[clave]);

                var normales = new List<SpellEffect>();
                var criticos = new List<SpellEffect>();
                try
                {
                    using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    conexion.Open();

                    var orden = conexion.CreateCommand();
                    orden.CommandText =
                        "SELECT EffectsJson, CriticalEffectsJson FROM SpellLevels " +
                        "WHERE SpellId = $id AND Grade = $g LIMIT 1;";
                    orden.Parameters.AddWithValue("$id", hechizo);
                    orden.Parameters.AddWithValue("$g", clave.Item2);

                    using var lector = orden.ExecuteReader();
                    if (lector.Read())
                    {
                        Parsear(lector.IsDBNull(0) ? "" : lector.GetString(0), normales);
                        Parsear(lector.IsDBNull(1) ? "" : lector.GetString(1), criticos);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Efectos] No se pudieron leer los del hechizo {hechizo} " +
                                     $"grado {grado}: {ex.Message}");
                }

                _cache[clave] = normales;
                _criticos[clave] = criticos;
                return (normales, criticos);
            }
        }

        private static void Parsear(string json, List<SpellEffect> donde)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    int forma = 'P', tamano = 1, paso = 0, tope = 0;
                    bool para = false;
                    if (e.TryGetProperty("zoneDescr", out var z) && z.ValueKind == JsonValueKind.Object)
                    {
                        int f = Entero(z, "shape");
                        if (f > 0) forma = f;
                        tamano = Entero(z, "param1");
                        para = Entero(z, "isStopAtTarget") != 0;
                        paso = Entero(z, "damageDecreaseStepPercent");
                        tope = Entero(z, "maxDamageDecreaseApplyCount");
                    }

                    donde.Add(new SpellEffect
                    {
                        EffectId = Entero(e, "effectId"),
                        EffectUid = Entero(e, "effectUid"),
                        Value = Entero(e, "value"),
                        DiceNum = Entero(e, "diceNum"),
                        DiceSide = Entero(e, "diceSide"),
                        Duration = Entero(e, "duration"),
                        Delay = Entero(e, "delay"),
                        Dispellable = Entero(e, "dispellable"),
                        Element = e.TryGetProperty("effectElement", out var el) && el.TryGetInt32(out int v) ? v : -1,
                        Triggers = Texto(e, "triggers", "I"),
                        TargetMask = Texto(e, "targetMask", ""),
                        Forma = forma,
                        Tamano = tamano,
                        ParaEnElObjetivo = para,
                        PasoDeCaida = paso,
                        TopeDeCaida = tope,
                        Probabilidad = e.TryGetProperty("random", out var rnd) &&
                                       rnd.TryGetDouble(out double p) ? p : 0,
                        Sorteo = Entero(e, "group"),
                    });
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] JSON de efectos ilegible: {ex.Message}");
            }
        }

        private static int Entero(JsonElement e, string nombre)
            => e.TryGetProperty(nombre, out var v) && v.TryGetInt32(out int n) ? n : 0;

        private static string Texto(JsonElement e, string nombre, string porDefecto)
            => e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? porDefecto)
                : porDefecto;

        /// <summary>
        /// El grado que un personaje tiene abierto de un hechizo, y el identificador de esa fila.
        /// Sale de SpellLevels por MinPlayerLevel, que es de donde lo saca el propio cliente.
        /// </summary>
        public static (int Grado, int NivelId, int Coste) GradoDe(int hechizo, int nivelDelPersonaje)
        {
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();
                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT Grade, Id, APCost FROM SpellLevels WHERE SpellId = $id " +
                                    "AND MinPlayerLevel <= $lvl ORDER BY Grade DESC LIMIT 1;";
                orden.Parameters.AddWithValue("$id", hechizo);
                orden.Parameters.AddWithValue("$lvl", Math.Max(1, nivelDelPersonaje));
                using var lector = orden.ExecuteReader();
                if (lector.Read())
                {
                    return ((int)lector.GetInt64(0), (int)lector.GetInt64(1), (int)lector.GetInt64(2));
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] No se pudo mirar el grado del hechizo {hechizo}: {ex.Message}");
            }
            return (1, 0, 0);
        }

        /// <summary>
        /// Las actitudes que regalan los objetos equipados: el efecto 1175 de cada uno lleva en su
        /// <c>diceNum</c> el hechizo que da. De ahí salen las de los seis dofus y las de los
        /// trofeos, y con ellas la regla del Ocre —"al principio de cada turno, un PA si no te han
        /// pegado"— sin escribir ni una línea sobre el Ocre.
        /// </summary>
        public const int EfectoQueRegalaHechizo = 1175;

        /// <summary>Las casillas que son equipo de verdad; de la 63 en adelante es la bolsa.</summary>
        private const int UltimaCasillaDeEquipo = 62;

        public static List<int> ActitudesDelEquipo(long personaje)
        {
            var fuera = new List<int>();
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();
                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT Effects FROM CharacterItems WHERE CharacterId = $id " +
                                    "AND Position >= 0 AND Position <= $ultima;";
                orden.Parameters.AddWithValue("$id", personaje);
                orden.Parameters.AddWithValue("$ultima", UltimaCasillaDeEquipo);

                using var lector = orden.ExecuteReader();
                while (lector.Read())
                {
                    // Hace falta el efecto crudo y no el resumen del inventario: el hechizo que
                    // regala el objeto viaja en el DADO, no en el valor.
                    foreach (var efecto in Equipment.ParseEffects(lector.IsDBNull(0) ? "" : lector.GetString(0)))
                    {
                        if (efecto.Effect != EfectoQueRegalaHechizo) continue;
                        int hechizo = (int)efecto.DiceNum;
                        if (hechizo > 0 && !fuera.Contains(hechizo)) fuera.Add(hechizo);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] No se pudieron mirar las actitudes del equipo: {ex.Message}");
            }
            return fuera;
        }
    }
}
