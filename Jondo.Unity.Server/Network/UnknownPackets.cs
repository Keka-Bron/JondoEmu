using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Network
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
                if (cuantas == 1 || cuantas % 100 == 0) Guardar(fila);
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
        /// </summary>
        public static void RecordFrame(byte[] frame, Kind kind)
        {
            try
            {
                if (frame == null || frame.Length == 0) return;

                string tipo = NetworkEnvelope.GetMessageTypeUrl(frame) ?? "";
                int barra = tipo.LastIndexOf('/');
                string opcode = barra >= 0 && barra + 1 < tipo.Length ? tipo[(barra + 1)..] : tipo;

                // El campo de la raíz dice la dirección: 1 empuje, 2 petición del cliente, 3
                // respuesta. Aquí siempre debería ser el 2, pero se mira en vez de suponerlo.
                int raiz = 0;
                try
                {
                    foreach (var campo in ProtoMessage.Parse(frame).Fields)
                    {
                        if (campo.WireType != 2) continue;
                        if (campo.FieldNumber is 1 or 2 or 3) { raiz = campo.FieldNumber; break; }
                    }
                }
                catch { }

                byte[] dentro = NetworkEnvelope.ExtractGameNodePayload(frame) ?? Array.Empty<byte>();
                Record(opcode, raiz, dentro, kind);
            }
            catch
            {
                // Igual que Record: esto no puede tumbar a nadie.
            }
        }

        /// <summary>
        /// La FORMA de un mensaje: número de campo y tipo de dato, metiéndose en los submensajes.
        ///
        ///   v   un número (varint)
        ///   f   un número de tamaño fijo
        ///   s   una cadena o unos bytes que no son un submensaje
        ///   {…} un submensaje, con su forma dentro
        ///
        /// Un campo de longitud variable se prueba a leer como submensaje, y sólo cuenta como tal
        /// si se lee ENTERO sin sobras: así una cadena de texto que por casualidad empiece por un
        /// byte que parece una etiqueta no se cuela como estructura. Se para a la sexta capa,
        /// porque más abajo ya no distingue nada y un mensaje mal formado podría no tener fondo.
        /// </summary>
        public static string Signature(byte[] payload, int profundidad = 0)
        {
            if (payload == null || payload.Length == 0) return "(vacío)";
            if (profundidad >= ProfundidadMaxima) return "…";

            ProtoMessage leido;
            try { leido = ProtoMessage.Parse(payload); }
            catch { return "(ilegible)"; }
            if (leido == null || leido.Fields.Count == 0) return "(vacío)";

            var partes = new List<string>();
            foreach (var campo in leido.Fields)
            {
                switch (campo.WireType)
                {
                    case 0:
                        partes.Add($"{campo.FieldNumber}:v");
                        break;
                    case 1:
                    case 5:
                        partes.Add($"{campo.FieldNumber}:f");
                        break;
                    case 2:
                        var dentro = campo.BytesValue;
                        if (dentro != null && dentro.Length > 0 && EsSubmensaje(dentro))
                            partes.Add($"{campo.FieldNumber}:{{{Signature(dentro, profundidad + 1)}}}");
                        else
                            partes.Add($"{campo.FieldNumber}:s");
                        break;
                    default:
                        partes.Add($"{campo.FieldNumber}:?");
                        break;
                }
            }

            return string.Join(",", partes);
        }

        private const int ProfundidadMaxima = 6;

        /// <summary>
        /// Si esos bytes son un protobuf de verdad, o unos datos que sólo lo parecen.
        ///
        /// Hacen falta las DOS comprobaciones, y la segunda se descubrió midiendo.
        ///
        /// La primera es que se lea entero: ProtoMessage.Parse se para en el primer campo que no
        /// cuadra y devuelve lo que llevara, así que hay que comprobar que lo leído vuelve a dar
        /// los mismos bytes.
        ///
        /// La segunda es el tope del número de campo, y sin ella esto no valía para nada. El jrw
        /// —el paquete de andar— lleva el camino como un bloque de bytes, y ese bloque se leía
        /// como si fuera una estructura: salían campos 1024, 1025, 1566, 1600, distintos en cada
        /// paso que daba el jugador. Resultado: 307 «formas» distintas de un mismo mensaje en
        /// 1.798 capturados, que es exactamente el registro inservible que esto quería evitar.
        ///
        /// El tope no es a ojo. En el protocolo entero de 3.6.10.10, extraído del cliente, hay
        /// 8.972 campos declarados y EL MÁS ALTO ES EL 40; la mediana es 2 y el percentil 99 es
        /// 19. Con 64 caben todos y sobra sitio para lo que Ankama añada.
        /// </summary>
        private static bool EsSubmensaje(byte[] bytes)
        {
            try
            {
                var leido = ProtoMessage.Parse(bytes);
                if (leido == null || leido.Fields.Count == 0) return false;

                foreach (var campo in leido.Fields)
                    if (campo.FieldNumber > CampoMasAlto || campo.FieldNumber <= 0) return false;

                byte[] devuelta = leido.ToByteArray();
                return devuelta.Length == bytes.Length;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// El número de campo más alto que se acepta como parte de una estructura de verdad.
        /// Medido: el más alto del protocolo 3.6.10.10 es el 40, sobre 8.972 campos.
        /// </summary>
        private const int CampoMasAlto = 64;

        /// <summary>El mapa donde está el que lo mandó, si es que hay sesión. Nunca lanza.</summary>
        private static long SeguroElMapa()
        {
            try { return SessionContext.State.MapId; }
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
