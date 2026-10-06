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
    /// What the client sends us and we do not know how to handle, recorded instead of thrown away.
    ///
    /// Until now a packet with no handler did two things, both bad: either it was printed on the
    /// console with a frame of asterisks —and scrolled off in thirty seconds— or it fell
    /// into GameNodeProxy's silence list, which is seventeen opcodes written by hand so
    /// that they do not flood the log. The silenced is worse than the noisy: it stops existing.
    ///
    /// Here both are kept, with the difference noted, and so «there is something I do not know»
    /// becomes a list that can be worked with: what is missing, how often it happens and from
    /// where.
    ///
    /// WHAT MAKES THIS USEFUL is that it does NOT group by opcode, but by the message's SHAPE.
    /// One same opcode can carry different payloads depending on what the player is doing, and
    /// counting them together hides exactly what needs to be seen. The shape is taken by walking the
    /// protobuf and noting field number and data type, going inside the submessages:
    ///
    ///     jjm  1:v,2:{1:v,3:s}     the player sends a number and a string
    ///     jjm  1:v,4:{2:v}         the same opcode, something entirely different
    ///
    /// This does NOT decipher anything and must not. A packet recorded here does not authorise inventing
    /// an answer: without a capture of the real server saying what it answers, answering anything
    /// is worse than not answering, because the client is left with a state nobody has.
    /// The list says WHERE TO LOOK, and what is looked at is measured like everything else.
    /// </summary>
    public static class UnknownPackets
    {
        /// <summary>Why this packet is here.</summary>
        public enum Kind
        {
            /// <summary>No handler claimed it: it fell to the end of the chain.</summary>
            Unhandled = 0,

            /// <summary>The silence list covers it, which is an old and unmeasured decision.</summary>
            Silenced = 1,

            /// <summary>It arrived, but could not be read as protobuf.</summary>
            Undecodable = 2,
        }

        /// <summary>A message shape, with what is known about it.</summary>
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

            /// <summary>A sample to be able to look at it again. The first one is kept.</summary>
            public string SampleHex = "";
        }

        private static readonly ConcurrentDictionary<string, Row> _rows =
            new ConcurrentDictionary<string, Row>();

        private static readonly object _candadoDeLaBase = new object();
        private static bool _basePreparada;

        /// <summary>Cuántas formas distintas hay apuntadas.</summary>
        public static int ShapeCount => _rows.Count;

        /// <summary>And how many distinct opcodes, which are always fewer.</summary>
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
        /// Records a packet. It never throws: this hangs off the dispatcher and a failure here
        /// cannot bring down anyone's connection.
        /// </summary>
        public static void Record(string opcode, int rootField, byte[] payload, Kind kind)
        {
            try
            {
                if (string.IsNullOrEmpty(opcode)) opcode = "(sin opcode)";
                payload ??= Array.Empty<byte>();

                string firma = Signature(payload);
                string clave = $"{opcode}|{rootField}|{(int)kind}|{firma}";

                // A ceiling, in case some day a shape runs wild. Measured over the 305 captures:
                // 243 distinct client opcodes give 317 shapes in 29,991 messages, so a thousand
                // is ten times what is needed. If it gets there, something is generating
                // garbage signatures, and then what has to be done is fix it, not eat up the
                // server's memory meanwhile.
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

                // It is written to the base the first time and then every so often. A packet of
                // these can arrive a hundred times a minute —the client's ping, for one—
                // and opening SQLite for each one would put the disk to work to learn nothing
                // new. What matters is that the shape EXISTS in the list, not the exact number.
                if (cuantas == 1 || cuantas % 100 == 0)
                {
                    Guardar(fila);
                    // Direct, without wrapping in try/catch: SessionContext.Current returns the
                    // Suelta session when the AsyncLocal is empty and State is Current.State, so
                    // neither of the two can throw. The two helpers that wrapped this
                    // were dead catches.
                    ActivityJournal.Current.Write("packet.unknown",
                        SessionContext.Current.AccountId, SessionContext.State.CharacterId,
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
                // On purpose. This is diagnostics: if it fails, a note is lost, not a
                // game.
            }
        }

        /// <summary>The most that is kept of a sample, in bytes.</summary>
        private const int MuestraMaxima = 512;

        /// <summary>How many distinct shapes are held in memory before stopping recording.</summary>
        private const int TechoDeFormas = 1000;

        /// <summary>
        /// Records a whole frame: takes off its envelope, the opcode and the payload, and calls the one above.
        ///
        /// It is what the dispatcher calls, which at that point only has the raw bytes. Extracting
        /// the opcode here and not there avoids repeating the gutting in the two places it is
        /// called from, and above all keeps the dispatcher from having to know what an envelope looks like.
        ///
        /// HERE WAS THE BUG that left all this useless. The envelope was opened with
        /// <c>ExtractGameNodePayload</c>, which only looks at the root's field 3, and with
        /// <c>GetMessageTypeUrl</c>, which looks at 1 and 3. The client's frames go in field
        /// <b>2</b>: measured over the 72,879 of the traffic log, 8,974 from the client and all in
        /// 2. So every packet going through here came in with no opcode and an empty payload,
        /// and after weeks of play the table had two rows, both «(sin opcode)» over an
        /// empty body. The dispatcher never noticed because it looks for the opcodes as text
        /// inside the frame, and that works whatever the envelope.
        ///
        /// Now <see cref="Envelope"/> opens it, which lives in the protocol project precisely
        /// so that the editor computes the same thing the server writes.
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
                // Same as Record: this cannot bring anyone down.
            }
        }

        /// <summary>
        /// A message's SHAPE: field number and data type, going into the submessages.
        ///
        /// The algorithm moved to <see cref="ProtoShape"/>, in the protocol project, and here
        /// the usual door remains. The reason for the move is that the editor has to compute
        /// EXACTLY the same string the server writes in paquetes.db: with a copy on each
        /// side, both agree until the day someone improves one, and from then on the
        /// editor stops finding the rows the server recorded, without a word.
        /// </summary>
        public static string Signature(byte[] payload) => ProtoShape.Of(payload);

        /// <summary>The map whoever sent it is on, if there is a session. It never throws.</summary>
        private static long SeguroElMapa()
        {
            try { return SessionContext.State.MapId; }
            catch { return 0; }
        }

        // ─── The base ───────────────────────────────────────────────────────────

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
        /// What has been recorded so far, from the most frequent to the least. The .packets command uses it.
        /// </summary>
        public static List<Row> Top(int cuantas)
        {
            var todas = new List<Row>(_rows.Values);
            todas.Sort((a, b) => b.Occurrences.CompareTo(a.Occurrences));
            if (todas.Count > cuantas) todas.RemoveRange(cuantas, todas.Count - cuantas);
            return todas;
        }

        /// <summary>A one-line summary, for startup and for the log.</summary>
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
        /// Reads back from the base what was recorded in previous starts.
        ///
        /// Without this, every time the server restarts the list starts empty and what cost an
        /// afternoon of play is lost. The counts are added to whatever comes from this session.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(Paths.PacketTelemetryConnectionString);
                connection.Open();
                PrepararBase(connection);

                // The rows the envelope bug left: no opcode and an empty body. Nothing
                // can be done with them —they say neither which message it was nor what it carried— and in the
                // editor's list they only take up space looking like pending work. They go once
                // and do not come back, because RecordFrame now opens the envelope correctly.
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
