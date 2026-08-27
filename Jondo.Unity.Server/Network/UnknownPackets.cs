using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol.Wire;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Lo que el cliente nos manda y no sabemos atender, apuntado en vez de tirado.
    ///
    /// Hasta ahora un paquete sin manejador hacía dos cosas, las dos malas: o se imprimía en la
    /// consola con un marco de asteriscos —y se iba scroll arriba a los treinta segundos— o caía
    /// en la lista de silencio de GameNodeProxy, que son diecisiete opcodes escritos a mano para
    /// que no inunden el registro. Lo silenciado es peor que lo ruidoso: deja de existir.
    ///
    /// Aquí se guardan los dos, con la diferencia apuntada, y así «hay algo que no sé» se
    /// convierte en una lista con la que se puede trabajar: qué falta, cuántas veces pasa y desde
    /// dónde.
    ///
    /// LO QUE HACE QUE ESTO SIRVA es que NO se agrupa por opcode, sino por la FORMA del mensaje.
    /// Un mismo opcode puede llevar cargas distintas según lo que el jugador esté haciendo, y
    /// contarlas juntas esconde justo lo que hace falta ver. La forma se saca recorriendo el
    /// protobuf y anotando número de campo y tipo de dato, metiéndose dentro de los submensajes:
    ///
    ///     jjm  1:v,2:{1:v,3:s}     el jugador manda un número y una cadena
    ///     jjm  1:v,4:{2:v}         el mismo opcode, otra cosa distinta
    ///
    /// Esto NO descifra nada y no debe hacerlo. Un paquete apuntado aquí no autoriza a inventarse
    /// una respuesta: sin una captura del servidor real que diga qué contesta, contestar cualquier
    /// cosa es peor que no contestar, porque el cliente se queda con un estado que nadie tiene.
    /// La lista dice DÓNDE MIRAR, y lo que se mire se mide como todo lo demás.
    /// </summary>
    public static class UnknownPackets
    {
        /// <summary>Por qué está aquí este paquete.</summary>
        public enum Kind
        {
            /// <summary>Ningún manejador lo reclamó: cayó al final de la cadena.</summary>
            Unhandled = 0,

            /// <summary>Lo tapa la lista de silencio, que es una decisión vieja y sin medir.</summary>
            Silenced = 1,

            /// <summary>Llegó, pero no se pudo leer como protobuf.</summary>
            Undecodable = 2,
        }

        /// <summary>Una forma de mensaje, con lo que se sabe de ella.</summary>
        public sealed class Row
        {
            public string Opcode = "";
            public int RootField;
            public Kind Kind;
            public string Signature = "";
            public long Occurrences;
            public DateTimeOffset FirstSeen;
            public DateTimeOffset LastSeen;
            public long MapId;
            public int PayloadBytes;

            /// <summary>Una muestra para poder volver a mirarla. Se guarda la primera.</summary>
            public string SampleHex = "";
        }

        private static readonly ConcurrentDictionary<string, Row> _rows =
            new ConcurrentDictionary<string, Row>();

        private static readonly object _candadoDeLaBase = new object();
        private static bool _basePreparada;

        /// <summary>Cuántas formas distintas hay apuntadas.</summary>
        public static int ShapeCount => _rows.Count;

        /// <summary>Y cuántos opcodes distintos, que siempre son menos.</summary>
        public static int OpcodeCount
        {
            get
            {
                var vistos = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in _rows.Values) vistos.Add(r.Opcode);
                return vistos.Count;
            }
        }

        /// <summary>
        /// Apunta un paquete. No lanza nunca: esto va colgado del despachador y un fallo aquí no
        /// puede tumbar la conexión de nadie.
        /// </summary>
        public static void Record(string opcode, int rootField, byte[] payload, Kind kind)
        {
            try
            {
                if (string.IsNullOrEmpty(opcode)) opcode = "(sin opcode)";
                payload ??= Array.Empty<byte>();

                string firma = Signature(payload);
                string clave = $"{opcode}|{rootField}|{(int)kind}|{firma}";

                // Un techo, por si algún día una forma se desboca. Medido sobre las 305 capturas:
                // 243 opcodes distintos del cliente dan 317 formas en 29.991 mensajes, así que mil
                // es diez veces lo que hace falta. Si se llega ahí es que algo está generando
                // firmas basura, y entonces lo que hay que hacer es arreglarlo, no comerse la
                // memoria del servidor mientras tanto.
                if (_rows.Count >= TechoDeFormas && !_rows.ContainsKey(clave)) return;

                var fila = _rows.GetOrAdd(clave, _ =>
                {
                    var nueva = new Row
                    {
                        Opcode = opcode,
                        RootField = rootField,
                        Kind = kind,
                        Signature = firma,
                        FirstSeen = DateTimeOffset.UtcNow,
                        PayloadBytes = payload.Length,
                        MapId = SeguroElMapa(),
                        SampleHex = Convert.ToHexString(
                            payload.Length <= MuestraMaxima
                                ? payload
                                : payload[..MuestraMaxima]),
                    };
                    return nueva;
                });

                long cuantas = System.Threading.Interlocked.Increment(ref fila.Occurrences);
                fila.LastSeen = DateTimeOffset.UtcNow;

                // Se escribe a la base la primera vez y luego de tanto en tanto. Un paquete de
                // estos puede llegar cien veces por minuto —el ping del cliente sin ir más lejos—
                // y abrir SQLite en cada uno pondría el disco a trabajar para no aprender nada
                // nuevo. Lo que interesa es que la forma EXISTA en la lista, no el número exacto.
                if (cuantas == 1 || cuantas % 100 == 0)
                {
                    Guardar(fila);
                    ActivityJournal.Current.Write("packet.unknown", SeguroLaCuenta(),
                        SeguroElPersonaje(),
                        new
                        {
                            opcode = fila.Opcode,
                            rootField = fila.RootField,
                            kind = fila.Kind.ToString(),
                            signature = fila.Signature,
                            occurrences = cuantas,
                            mapId = fila.MapId,
                            payloadBytes = fila.PayloadBytes,
                        });
                }
            }
            catch
            {
                // A propósito. Esto es diagnóstico: si falla, se pierde una anotación, no una
                // partida.
            }
        }

        /// <summary>Lo más que se guarda de una muestra, en bytes.</summary>
        private const int MuestraMaxima = 512;

        /// <summary>Cuántas formas distintas se aguantan en memoria antes de dejar de apuntar.</summary>
        private const int TechoDeFormas = 1000;

        /// <summary>
        /// Apunta una trama entera: le saca el sobre, el opcode y la carga, y llama al de arriba.
        ///
        /// Es lo que llama el despachador, que a esas alturas sólo tiene los bytes crudos. Sacar
        /// el opcode aquí y no allí evita repetir el destripe en los dos sitios desde los que se
        /// llama, y sobre todo evita que el despachador tenga que saber cómo es un sobre.
        ///
        /// AQUÍ ESTABA EL FALLO que dejaba todo esto sin servir. Se abría el sobre con
        /// <c>ExtractGameNodePayload</c>, que sólo mira el campo 3 de la raíz, y con
        /// <c>GetMessageTypeUrl</c>, que mira el 1 y el 3. Las tramas del cliente van en el campo
        /// <b>2</b>: medido sobre las 72.879 del registro de tráfico, 8.974 de cliente y todas en
        /// el 2. Así que cada paquete que pasaba por aquí entraba sin opcode y con la carga vacía,
        /// y después de semanas de juego la tabla tenía dos filas, las dos «(sin opcode)» sobre un
        /// cuerpo vacío. El despachador no se enteró nunca porque él busca los opcodes como texto
        /// dentro de la trama, y eso funciona sea cual sea el sobre.
        ///
        /// Ahora lo abre <see cref="Envelope"/>, que vive en el proyecto de protocolo justamente
        /// para que el editor calcule lo mismo que el servidor escribe.
        /// </summary>
        public static void RecordFrame(byte[] frame, Kind kind)
        {
            try
            {
                if (frame == null || frame.Length == 0) return;

                var sobre = Envelope.Read(frame);
                Record(sobre.Found ? sobre.Opcode : "", sobre.RootField, sobre.Payload, kind);
            }
            catch
            {
                // Igual que Record: esto no puede tumbar a nadie.
            }
        }

        /// <summary>
        /// La FORMA de un mensaje: número de campo y tipo de dato, metiéndose en los submensajes.
        ///
        /// El algoritmo se mudó a <see cref="ProtoShape"/>, en el proyecto de protocolo, y aquí
        /// queda la puerta de siempre. La razón de la mudanza es que el editor tiene que calcular
        /// EXACTAMENTE la misma cadena que el servidor escribe en paquetes.db: con una copia a cada
        /// lado, los dos coinciden hasta el día en que alguien mejora uno, y a partir de ahí el
        /// editor deja de encontrar las filas que el servidor apuntó, sin decir nada.
        /// </summary>
        public static string Signature(byte[] payload) => ProtoShape.Of(payload);

        /// <summary>El mapa donde está el que lo mandó, si es que hay sesión. Nunca lanza.</summary>
        private static long SeguroElMapa()
        {
            try { return SessionContext.State.MapId; }
            catch { return 0; }
        }

        private static long SeguroLaCuenta()
        {
            try { return SessionContext.Current.AccountId; }
            catch { return 0; }
        }

        private static long SeguroElPersonaje()
        {
            try { return SessionContext.State.CharacterId; }
            catch { return 0; }
        }

        // ─── La base ────────────────────────────────────────────────────────────

        private static void Guardar(Row fila)
        {
            lock (_candadoDeLaBase)
            {
                try
                {
                    using var connection = new SqliteConnection(Paths.PacketTelemetryConnectionString);
                    connection.Open();
                    PrepararBase(connection);

                    var command = connection.CreateCommand();
                    command.CommandText = @"
                        INSERT INTO PaquetesSinAtender
                            (Opcode, RootField, Kind, Signature, Occurrences,
                             FirstSeen, LastSeen, MapId, PayloadBytes, SampleHex)
                        VALUES ($op, $root, $kind, $firma, $veces, $primera, $ultima, $mapa, $bytes, $muestra)
                        ON CONFLICT(Opcode, RootField, Kind, Signature) DO UPDATE SET
                            Occurrences = excluded.Occurrences,
                            LastSeen    = excluded.LastSeen;
                    ";
                    command.Parameters.AddWithValue("$op", fila.Opcode);
                    command.Parameters.AddWithValue("$root", fila.RootField);
                    command.Parameters.AddWithValue("$kind", (int)fila.Kind);
                    command.Parameters.AddWithValue("$firma", fila.Signature);
                    command.Parameters.AddWithValue("$veces", fila.Occurrences);
                    command.Parameters.AddWithValue("$primera", fila.FirstSeen.ToString("O"));
                    command.Parameters.AddWithValue("$ultima", fila.LastSeen.ToString("O"));
                    command.Parameters.AddWithValue("$mapa", fila.MapId);
                    command.Parameters.AddWithValue("$bytes", fila.PayloadBytes);
                    command.Parameters.AddWithValue("$muestra", fila.SampleHex);
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Paquetes] No se pudo apuntar {fila.Opcode}: {ex.Message}");
                }
            }
        }

        private static void PrepararBase(SqliteConnection connection)
        {
            if (_basePreparada) return;

            var crear = connection.CreateCommand();
            crear.CommandText = @"
                CREATE TABLE IF NOT EXISTS PaquetesSinAtender (
                    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    Opcode        TEXT    NOT NULL,
                    RootField     INTEGER NOT NULL,
                    Kind          INTEGER NOT NULL,
                    Signature     TEXT    NOT NULL,
                    Occurrences   INTEGER NOT NULL,
                    FirstSeen     TEXT    NOT NULL,
                    LastSeen      TEXT    NOT NULL,
                    MapId         INTEGER NOT NULL,
                    PayloadBytes  INTEGER NOT NULL,
                    SampleHex     TEXT    NOT NULL,
                    Status        TEXT    NOT NULL DEFAULT 'nuevo',
                    Notes         TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_paquetes_forma
                    ON PaquetesSinAtender (Opcode, RootField, Kind, Signature);
            ";
            crear.ExecuteNonQuery();
            _basePreparada = true;
        }

        /// <summary>
        /// Lo apuntado hasta ahora, de lo que más pasa a lo que menos. Lo usa el comando .packets.
        /// </summary>
        public static List<Row> Top(int cuantas)
        {
            var todas = new List<Row>(_rows.Values);
            todas.Sort((a, b) => b.Occurrences.CompareTo(a.Occurrences));
            if (todas.Count > cuantas) todas.RemoveRange(cuantas, todas.Count - cuantas);
            return todas;
        }

        /// <summary>Un resumen de una línea, para el arranque y para el registro.</summary>
        public static string Resumen()
        {
            var counts = Counts();
            return $"{ShapeCount} forma(s) de {OpcodeCount} opcode(s): " +
                   $"{counts.Unhandled} sin atender, {counts.Silenced} silenciada(s), " +
                   $"{counts.Undecodable} ilegible(s)";
        }

        /// <summary>Counts by reason, kept separate from the Spanish diagnostic summary.</summary>
        public static (int Unhandled, int Silenced, int Undecodable) Counts()
        {
            int sinAtender = 0, silenciados = 0, ilegibles = 0;
            foreach (var r in _rows.Values)
            {
                if (r.Kind == Kind.Unhandled) sinAtender++;
                else if (r.Kind == Kind.Silenced) silenciados++;
                else ilegibles++;
            }
            return (sinAtender, silenciados, ilegibles);
        }

        /// <summary>
        /// Vuelve a leer de la base lo que se apuntó en arranques anteriores.
        ///
        /// Sin esto, cada vez que se reinicia el servidor la lista empieza vacía y lo que costó una
        /// tarde de juego se pierde. Las cuentas se suman a lo que venga de esta sesión.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(Paths.PacketTelemetryConnectionString);
                connection.Open();
                PrepararBase(connection);

                // Las filas que dejó el fallo del sobre: sin opcode y con el cuerpo vacío. No se
                // puede hacer nada con ellas —no dicen ni qué mensaje era ni qué llevaba— y en la
                // lista del editor sólo ocupan sitio pareciendo trabajo pendiente. Se van una vez
                // y no vuelven, porque ahora RecordFrame abre bien el sobre.
                var limpiar = connection.CreateCommand();
                limpiar.CommandText =
                    "DELETE FROM PaquetesSinAtender WHERE Opcode = '' OR Opcode = '(sin opcode)';";
                int viejas = limpiar.ExecuteNonQuery();
                if (viejas > 0)
                    Console.WriteLine($"[Paquetes] {viejas} fila(s) sin opcode del sobre mal abierto, borradas.");

                var leer = connection.CreateCommand();
                leer.CommandText = @"
                    SELECT Opcode, RootField, Kind, Signature, Occurrences,
                           FirstSeen, LastSeen, MapId, PayloadBytes, SampleHex
                    FROM PaquetesSinAtender;
                ";
                using var reader = leer.ExecuteReader();
                while (reader.Read())
                {
                    var fila = new Row
                    {
                        Opcode = reader.GetString(0),
                        RootField = reader.GetInt32(1),
                        Kind = (Kind)reader.GetInt32(2),
                        Signature = reader.GetString(3),
                        Occurrences = reader.GetInt64(4),
                        FirstSeen = DateTimeOffset.Parse(reader.GetString(5)),
                        LastSeen = DateTimeOffset.Parse(reader.GetString(6)),
                        MapId = reader.GetInt64(7),
                        PayloadBytes = reader.GetInt32(8),
                        SampleHex = reader.GetString(9),
                    };
                    _rows[$"{fila.Opcode}|{fila.RootField}|{(int)fila.Kind}|{fila.Signature}"] = fila;
                }

                Console.WriteLine(_rows.Count == 0
                    ? "[Paquetes] Ninguno sin atender apuntado todavía."
                    : $"[Paquetes] {Resumen()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Paquetes] No se pudo leer lo apuntado: {ex.Message}");
            }
        }
    }
}
