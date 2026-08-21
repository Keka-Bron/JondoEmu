using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Durable, deduplicated evidence for packets which have not yet reached a real server
    /// handler.  This database is intentionally separate from auth.db and world.db: deleting or
    /// sharing diagnostics must never affect player state.
    ///
    /// A row is a protocol *shape*, not a packet occurrence.  A frequently repeated heartbeat
    /// therefore updates its occurrence count and last-seen context instead of creating thousands
    /// of identical rows.  The first full frame and its decoded body remain available for replay.
    /// </summary>
    public static class UnknownPacketStore
    {
        private const int MaxSampleBytes = 131_072;
        private static readonly object InitializationLock = new object();
        private static bool _initialized;

        public static void Initialize()
        {
            lock (InitializationLock)
            {
                if (_initialized) return;

                using var connection = Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
                    pragma.ExecuteNonQuery();
                }

                using (var table = connection.CreateCommand())
                {
                    table.CommandText = @"
                        CREATE TABLE IF NOT EXISTS UnknownPackets (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Fingerprint TEXT NOT NULL UNIQUE,
                            Status TEXT NOT NULL DEFAULT 'New',
                            Classification TEXT NOT NULL,
                            Protocol TEXT NOT NULL,
                            Direction TEXT NOT NULL,
                            Opcode TEXT,
                            TypeUrl TEXT,
                            EnvelopeRoot INTEGER,
                            RequestId INTEGER,
                            WireSignature TEXT NOT NULL,
                            DecodedSummary TEXT,
                            FirstSeenUtc TEXT NOT NULL,
                            LastSeenUtc TEXT NOT NULL,
                            Occurrences INTEGER NOT NULL DEFAULT 1,
                            AccountId INTEGER,
                            CharacterId INTEGER,
                            MapId INTEGER,
                            SessionCorrelation TEXT,
                            Phase TEXT,
                            ClientVersion TEXT,
                            FrameSha256 TEXT NOT NULL,
                            PayloadSha256 TEXT NOT NULL,
                            SampleFrame BLOB NOT NULL,
                            SamplePayload BLOB NOT NULL,
                            LastError TEXT,
                            Notes TEXT,
                            HandlerHint TEXT
                        );
                        CREATE INDEX IF NOT EXISTS IX_UnknownPackets_Status_LastSeen
                            ON UnknownPackets(Status, LastSeenUtc DESC);
                        CREATE INDEX IF NOT EXISTS IX_UnknownPackets_Opcode
                            ON UnknownPackets(Opcode, Classification);

                        CREATE TABLE IF NOT EXISTS PacketOccurrences (
                            Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                            PacketId INTEGER NOT NULL REFERENCES UnknownPackets(Id) ON DELETE CASCADE,
                            SeenUtc TEXT NOT NULL,
                            AccountId INTEGER,
                            CharacterId INTEGER,
                            MapId INTEGER,
                            SessionCorrelation TEXT,
                            Phase TEXT,
                            RequestId INTEGER,
                            PayloadSha256 TEXT NOT NULL,
                            DecodedSummary TEXT,
                            LastError TEXT
                        );
                        CREATE INDEX IF NOT EXISTS IX_PacketOccurrences_Packet_Sequence
                            ON PacketOccurrences(PacketId, Sequence);

                        CREATE TABLE IF NOT EXISTS ObservedPackets (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Fingerprint TEXT NOT NULL UNIQUE,
                            Protocol TEXT NOT NULL, Direction TEXT NOT NULL, Opcode TEXT, TypeUrl TEXT,
                            WireSignature TEXT NOT NULL, DecodedSummary TEXT, FirstSeenUtc TEXT NOT NULL,
                            LastSeenUtc TEXT NOT NULL, Occurrences INTEGER NOT NULL DEFAULT 1,
                            AccountId INTEGER, CharacterId INTEGER, MapId INTEGER, SessionCorrelation TEXT,
                            Phase TEXT, ClientVersion TEXT, FrameSha256 TEXT NOT NULL, PayloadSha256 TEXT NOT NULL,
                            SamplePayload BLOB NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS IX_ObservedPackets_Direction_Opcode
                            ON ObservedPackets(Direction, Opcode, LastSeenUtc DESC);
                    ";
                    table.ExecuteNonQuery();
                }

                // The first telemetry build used a different fingerprint layout.  Its rows are
                // valid evidence, but without this migration a packet seen after a server restart
                // gets a second "New" row instead of updating the reviewed one.  Only rows with
                // no context-specific handler hint are safe to normalise: interactive uses add
                // their map/element/skill identity to the fingerprint and that identity cannot be
                // reconstructed from older rows.
                MigrateLegacyFingerprints(connection);

                _initialized = true;
                Console.WriteLine($"[Packet Telemetry] Queue ready: {Paths.PacketTelemetryDb}");
            }
        }

        private sealed record LegacyFingerprintRow(
            long Id,
            string Fingerprint,
            string Status,
            string Classification,
            string Protocol,
            string Direction,
            string? Opcode,
            int EnvelopeRoot,
            string WireSignature,
            string FirstSeenUtc,
            string LastSeenUtc,
            long Occurrences,
            long AccountId,
            long CharacterId,
            long MapId,
            string? SessionCorrelation,
            string? Phase,
            string? ClientVersion,
            string? LastError,
            string? Notes);

        /// <summary>
        /// Merges rows created before the canonical fingerprint was introduced.  This is a
        /// diagnostics-only migration: it never touches auth or world state and preserves the
        /// strongest review state, earliest sample row and newest session correlation.
        /// </summary>
        private static void MigrateLegacyFingerprints(SqliteConnection connection)
        {
            var groups = new Dictionary<string, List<LegacyFingerprintRow>>(StringComparer.Ordinal);

            using (var select = connection.CreateCommand())
            {
                select.CommandText = @"
                    SELECT Id, Fingerprint, Status, Classification, Protocol, Direction, Opcode,
                           EnvelopeRoot, WireSignature, FirstSeenUtc, LastSeenUtc, Occurrences,
                           AccountId, CharacterId, MapId, SessionCorrelation, Phase, ClientVersion,
                           LastError, Notes
                    FROM UnknownPackets
                    WHERE HandlerHint IS NULL;";

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    var row = new LegacyFingerprintRow(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                        reader.GetString(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.GetInt64(11),
                        reader.IsDBNull(12) ? 0 : reader.GetInt64(12),
                        reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                        reader.IsDBNull(14) ? 0 : reader.GetInt64(14),
                        reader.IsDBNull(15) ? null : reader.GetString(15),
                        reader.IsDBNull(16) ? null : reader.GetString(16),
                        reader.IsDBNull(17) ? null : reader.GetString(17),
                        reader.IsDBNull(18) ? null : reader.GetString(18),
                        reader.IsDBNull(19) ? null : reader.GetString(19));

                    string canonical = CanonicalFingerprint(row.Protocol, row.Direction,
                        row.Classification, row.EnvelopeRoot, row.Opcode, row.WireSignature);
                    if (!groups.TryGetValue(canonical, out var group)) groups[canonical] = group = new();
                    group.Add(row);
                }
            }

            foreach (var pair in groups)
            {
                string canonical = pair.Key;
                List<LegacyFingerprintRow> rows = pair.Value;
                if (rows.Count == 1 && rows[0].Fingerprint == canonical) continue;

                // Prefer an already reviewed record.  For equal states, preserve the oldest row
                // so its original replay sample remains the canonical one.
                LegacyFingerprintRow survivor = rows
                    .OrderByDescending(row => ReviewRank(row.Status))
                    .ThenBy(row => row.FirstSeenUtc, StringComparer.Ordinal)
                    .First();
                LegacyFingerprintRow newest = rows
                    .OrderByDescending(row => row.LastSeenUtc, StringComparer.Ordinal)
                    .First();
                string status = rows.OrderByDescending(row => ReviewRank(row.Status))
                    .First().Status;
                long occurrences = rows.Sum(row => row.Occurrences);
                string firstSeen = rows.OrderBy(row => row.FirstSeenUtc, StringComparer.Ordinal)
                    .First().FirstSeenUtc;
                string? notes = rows.Select(row => row.Notes)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                string? lastError = newest.LastError ?? rows.Select(row => row.LastError)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                using var transaction = connection.BeginTransaction();
                foreach (var duplicate in rows.Where(row => row.Id != survivor.Id))
                {
                    using var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM UnknownPackets WHERE Id = $id;";
                    delete.Parameters.AddWithValue("$id", duplicate.Id);
                    delete.ExecuteNonQuery();
                }

                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = @"
                        UPDATE UnknownPackets
                        SET Fingerprint = $fingerprint,
                            Status = $status,
                            FirstSeenUtc = $firstSeen,
                            LastSeenUtc = $lastSeen,
                            Occurrences = $occurrences,
                            AccountId = $accountId,
                            CharacterId = $characterId,
                            MapId = $mapId,
                            SessionCorrelation = $session,
                            Phase = $phase,
                            ClientVersion = $version,
                            LastError = $lastError,
                            Notes = $notes
                        WHERE Id = $id;";
                    update.Parameters.AddWithValue("$fingerprint", canonical);
                    update.Parameters.AddWithValue("$status", status);
                    update.Parameters.AddWithValue("$firstSeen", firstSeen);
                    update.Parameters.AddWithValue("$lastSeen", newest.LastSeenUtc);
                    update.Parameters.AddWithValue("$occurrences", occurrences);
                    update.Parameters.AddWithValue("$accountId", newest.AccountId);
                    update.Parameters.AddWithValue("$characterId", newest.CharacterId);
                    update.Parameters.AddWithValue("$mapId", newest.MapId);
                    update.Parameters.AddWithValue("$session", (object?)newest.SessionCorrelation ?? DBNull.Value);
                    update.Parameters.AddWithValue("$phase", (object?)newest.Phase ?? DBNull.Value);
                    update.Parameters.AddWithValue("$version", (object?)newest.ClientVersion ?? DBNull.Value);
                    update.Parameters.AddWithValue("$lastError", (object?)lastError ?? DBNull.Value);
                    update.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
                    update.Parameters.AddWithValue("$id", survivor.Id);
                    update.ExecuteNonQuery();
                }

                transaction.Commit();
                if (rows.Count > 1)
                    Console.WriteLine($"[Packet Telemetry] Merged {rows.Count} legacy rows for " +
                                      $"{survivor.Opcode ?? "<malformed>"}.");
            }
        }

        private static int ReviewRank(string status) => status switch
        {
            "Implemented" => 5,
            "NoReplyObserved" => 4,
            "BlockedEvidence" => 3,
            "Investigating" => 2,
            "New" => 1,
            _ => 0
        };

        private static string CanonicalFingerprint(string protocol, string direction,
                                                   string classification, int root,
                                                   string? opcode, string signature)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
                protocol, direction, classification, root, opcode ?? "<malformed>", signature, ""))))
                .ToLowerInvariant();

        /// <summary>Records a packet for which no dispatch branch exists.</summary>
        public static long RecordUnhandledGamePacket(byte[] frame, GameSession session)
            => Record(frame, "Game", "C2S", "Unhandled", "New", session, null);

        /// <summary>
        /// Records a legacy branch that currently drops a packet.  It stays in the New queue so
        /// it is audited and either implemented or explicitly reclassified; a silent branch is
        /// not a compatibility decision.
        /// </summary>
        public static long RecordLegacyIgnoredGamePacket(byte[] frame, GameSession session)
            => Record(frame, "Game", "C2S", "LegacyIgnored", "New", session, null);

        /// <summary>
        /// Records a packet whose no-reply behaviour is evidenced.  It is kept in the map but is
        /// not actionable by the automatic review queue unless someone changes its Status to New.
        /// </summary>
        public static long RecordKnownNoReplyGamePacket(byte[] frame, GameSession session)
            => Record(frame, "Game", "C2S", "KnownNoReply", "NoReplyObserved", session, null);

        /// <summary>
        /// Records an <c>iwo</c> that reached the generic interactive dispatcher but did not
        /// resolve to an action registered for this exact map.  Doors, workshops, resource nodes
        /// and HDVs all use <c>iwo</c>, so its protobuf shape alone is not enough: the map,
        /// element and skill-instance are deliberately part of the fingerprint.
        /// </summary>
        public static long RecordUnhandledInteractiveUse(byte[] frame, GameSession? session,
                                                          long mapId, int elementId,
                                                          int skillInstanceId, int additionalParam,
                                                          int elementCell, int elementGfx)
        {
            string identity = $"map={mapId};element={elementId};skill={skillInstanceId};" +
                              $"parameter={additionalParam}";
            string hint = "Unregistered interactive use: " + identity +
                          $"; client map data cell={elementCell}, gfx={elementGfx}. " +
                          "Capture this click with the adjacent S2C frames to establish its " +
                          "interactive type, action and response order.";
            return Record(frame, "Game", "C2S", "UnhandledInteractiveUse", "New", session,
                          null, identity, hint);
        }

        /// <summary>Records a bare connection-server frame that could not be parsed.</summary>
        public static long RecordConnectionDecodeFailure(byte[] frame, Exception error)
            => Record(frame, "Connection", "C2S", "DecodeFailure", "New", null,
                error.GetType().Name + ": " + error.Message);

        /// <summary>Records a syntactically valid connection-server message with no implementation.</summary>
        public static long RecordUnhandledConnectionPacket(byte[] frame)
            => Record(frame, "Connection", "C2S", "Unhandled", "New", null, null);

        /// <summary>
        /// Captures every protocol frame in a separate, deduplicated traffic catalogue. Unlike
        /// <see cref="UnknownPackets"/>, this never changes the actionable unknown-packet queue.
        /// It is deliberately bounded to one replay sample per wire shape plus occurrence count.
        /// </summary>
        public static void RecordObservedPacket(byte[] payload, string protocol, string direction,
                                                GameSession? session = null)
        {
            if (payload == null || payload.Length == 0) return;
            try
            {
                Initialize();
                var snapshot = Snapshot.Create(payload, protocol, direction, "Observed", session, null, null, null);
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO ObservedPackets (
                        Fingerprint, Protocol, Direction, Opcode, TypeUrl, WireSignature, DecodedSummary,
                        FirstSeenUtc, LastSeenUtc, Occurrences, AccountId, CharacterId, MapId,
                        SessionCorrelation, Phase, ClientVersion, FrameSha256, PayloadSha256, SamplePayload)
                    VALUES ($fingerprint,$protocol,$direction,$opcode,$typeUrl,$signature,$summary,
                            $seen,$seen,1,$accountId,$characterId,$mapId,$session,$phase,$version,
                            $frameHash,$payloadHash,$payload)
                    ON CONFLICT(Fingerprint) DO UPDATE SET Occurrences = Occurrences + 1,
                        LastSeenUtc=excluded.LastSeenUtc, AccountId=excluded.AccountId,
                        CharacterId=excluded.CharacterId, MapId=excluded.MapId,
                        SessionCorrelation=excluded.SessionCorrelation, Phase=excluded.Phase;";
                command.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
                command.Parameters.AddWithValue("$protocol", snapshot.Protocol);
                command.Parameters.AddWithValue("$direction", snapshot.Direction);
                command.Parameters.AddWithValue("$opcode", (object?)snapshot.Opcode ?? DBNull.Value);
                command.Parameters.AddWithValue("$typeUrl", (object?)snapshot.TypeUrl ?? DBNull.Value);
                command.Parameters.AddWithValue("$signature", snapshot.WireSignature);
                command.Parameters.AddWithValue("$summary", snapshot.DecodedSummary);
                command.Parameters.AddWithValue("$seen", snapshot.SeenUtc);
                command.Parameters.AddWithValue("$accountId", snapshot.AccountId);
                command.Parameters.AddWithValue("$characterId", snapshot.CharacterId);
                command.Parameters.AddWithValue("$mapId", snapshot.MapId);
                command.Parameters.AddWithValue("$session", (object?)snapshot.SessionCorrelation ?? DBNull.Value);
                command.Parameters.AddWithValue("$phase", snapshot.Phase);
                command.Parameters.AddWithValue("$version", snapshot.ClientVersion);
                command.Parameters.AddWithValue("$frameHash", snapshot.FrameHash);
                command.Parameters.AddWithValue("$payloadHash", snapshot.PayloadHash);
                command.Parameters.Add("$payload", SqliteType.Blob).Value = snapshot.Payload;
                command.ExecuteNonQuery();
            }
            catch (Exception ex) { Console.WriteLine($"[Packet Capture] Could not record observed frame: {ex.Message}"); }
        }

        private static long Record(byte[] frame, string protocol, string direction, string classification,
                                   string initialStatus, GameSession? session, string? error,
                                   string? fingerprintContext = null, string? handlerHint = null)
        {
            if (frame == null || frame.Length == 0) return 0;

            try
            {
                Initialize();
                var snapshot = Snapshot.Create(frame, protocol, direction, classification, session, error,
                                               fingerprintContext, handlerHint);

                using var connection = Open();
                using var transaction = connection.BeginTransaction();

                long id;
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = @"
                        INSERT OR IGNORE INTO UnknownPackets (
                            Fingerprint, Status, Classification, Protocol, Direction, Opcode, TypeUrl,
                            EnvelopeRoot, RequestId, WireSignature, DecodedSummary, FirstSeenUtc, LastSeenUtc,
                            Occurrences, AccountId, CharacterId, MapId, SessionCorrelation, Phase, ClientVersion,
                            FrameSha256, PayloadSha256, SampleFrame, SamplePayload, LastError, HandlerHint)
                        VALUES (
                            $fingerprint, $status, $classification, $protocol, $direction, $opcode, $typeUrl,
                            $root, $requestId, $signature, $summary, $seen, $seen,
                            1, $accountId, $characterId, $mapId, $session, $phase, $version,
                            $frameHash, $payloadHash, $frame, $payload, $error, $hint);";
                    Bind(insert, snapshot, initialStatus);
                    int inserted = insert.ExecuteNonQuery();

                    if (inserted > 0)
                    {
                        using var identity = connection.CreateCommand();
                        identity.Transaction = transaction;
                        identity.CommandText = "SELECT last_insert_rowid();";
                        id = Convert.ToInt64(identity.ExecuteScalar() ?? 0L);
                    }
                    else
                    {
                        using var update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText = @"
                            UPDATE UnknownPackets
                            SET Occurrences = Occurrences + 1,
                                LastSeenUtc = $seen,
                                AccountId = $accountId,
                                CharacterId = $characterId,
                                MapId = $mapId,
                                SessionCorrelation = $session,
                                Phase = $phase,
                                RequestId = $requestId,
                                LastError = COALESCE($error, LastError),
                                HandlerHint = COALESCE($hint, HandlerHint)
                            WHERE Fingerprint = $fingerprint;";
                        update.Parameters.AddWithValue("$seen", snapshot.SeenUtc);
                        update.Parameters.AddWithValue("$accountId", snapshot.AccountId);
                        update.Parameters.AddWithValue("$characterId", snapshot.CharacterId);
                        update.Parameters.AddWithValue("$mapId", snapshot.MapId);
                        update.Parameters.AddWithValue("$session", snapshot.SessionCorrelation);
                        update.Parameters.AddWithValue("$phase", snapshot.Phase);
                        update.Parameters.AddWithValue("$requestId", snapshot.RequestId);
                        update.Parameters.AddWithValue("$error", (object?)snapshot.Error ?? DBNull.Value);
                        update.Parameters.AddWithValue("$hint", (object?)snapshot.HandlerHint ?? DBNull.Value);
                        update.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
                        update.ExecuteNonQuery();

                        using var getId = connection.CreateCommand();
                        getId.Transaction = transaction;
                        getId.CommandText = "SELECT Id FROM UnknownPackets WHERE Fingerprint = $fingerprint;";
                        getId.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
                        id = Convert.ToInt64(getId.ExecuteScalar() ?? 0L);
                    }
                }

                // Keep a compact, chronological occurrence trail. The canonical row remains
                // deduplicated and retains the replay sample; this table makes recurring C2S
                // sequences and changing map/session context visible without multiplying blobs.
                using (var occurrence = connection.CreateCommand())
                {
                    occurrence.Transaction = transaction;
                    occurrence.CommandText = @"
                        INSERT INTO PacketOccurrences (
                            PacketId, SeenUtc, AccountId, CharacterId, MapId, SessionCorrelation,
                            Phase, RequestId, PayloadSha256, DecodedSummary, LastError)
                        VALUES ($packetId, $seen, $accountId, $characterId, $mapId, $session,
                                $phase, $requestId, $payloadHash, $summary, $error);";
                    occurrence.Parameters.AddWithValue("$packetId", id);
                    occurrence.Parameters.AddWithValue("$seen", snapshot.SeenUtc);
                    occurrence.Parameters.AddWithValue("$accountId", snapshot.AccountId);
                    occurrence.Parameters.AddWithValue("$characterId", snapshot.CharacterId);
                    occurrence.Parameters.AddWithValue("$mapId", snapshot.MapId);
                    occurrence.Parameters.AddWithValue("$session", snapshot.SessionCorrelation);
                    occurrence.Parameters.AddWithValue("$phase", snapshot.Phase);
                    occurrence.Parameters.AddWithValue("$requestId", snapshot.RequestId);
                    occurrence.Parameters.AddWithValue("$payloadHash", snapshot.PayloadHash);
                    occurrence.Parameters.AddWithValue("$summary", snapshot.DecodedSummary);
                    occurrence.Parameters.AddWithValue("$error", (object?)snapshot.Error ?? DBNull.Value);
                    occurrence.ExecuteNonQuery();
                }

                transaction.Commit();
                return id;
            }
            catch (Exception ex)
            {
                // Observability cannot interfere with the client connection that generated it.
                Console.WriteLine($"[Packet Telemetry] Could not record packet: {ex.Message}");
                return 0;
            }
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(Paths.PacketTelemetryConnectionString + ";Default Timeout=3");
            connection.Open();
            return connection;
        }

        private static void Bind(SqliteCommand command, Snapshot item, string status)
        {
            command.Parameters.AddWithValue("$fingerprint", item.Fingerprint);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$classification", item.Classification);
            command.Parameters.AddWithValue("$protocol", item.Protocol);
            command.Parameters.AddWithValue("$direction", item.Direction);
            command.Parameters.AddWithValue("$opcode", (object?)item.Opcode ?? DBNull.Value);
            command.Parameters.AddWithValue("$typeUrl", (object?)item.TypeUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$root", item.EnvelopeRoot);
            command.Parameters.AddWithValue("$requestId", item.RequestId);
            command.Parameters.AddWithValue("$signature", item.WireSignature);
            command.Parameters.AddWithValue("$summary", item.DecodedSummary);
            command.Parameters.AddWithValue("$seen", item.SeenUtc);
            command.Parameters.AddWithValue("$accountId", item.AccountId);
            command.Parameters.AddWithValue("$characterId", item.CharacterId);
            command.Parameters.AddWithValue("$mapId", item.MapId);
            command.Parameters.AddWithValue("$session", item.SessionCorrelation);
            command.Parameters.AddWithValue("$phase", item.Phase);
            command.Parameters.AddWithValue("$version", item.ClientVersion);
            command.Parameters.AddWithValue("$frameHash", item.FrameHash);
            command.Parameters.AddWithValue("$payloadHash", item.PayloadHash);
            command.Parameters.Add("$frame", SqliteType.Blob).Value = item.Frame;
            command.Parameters.Add("$payload", SqliteType.Blob).Value = item.Payload;
            command.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
            command.Parameters.AddWithValue("$hint", (object?)item.HandlerHint ?? DBNull.Value);
        }

        private sealed class Snapshot
        {
            public string Fingerprint { get; private init; } = "";
            public string Classification { get; private init; } = "";
            public string Protocol { get; private init; } = "";
            public string Direction { get; private init; } = "";
            public string? Opcode { get; private init; }
            public string? TypeUrl { get; private init; }
            public int EnvelopeRoot { get; private init; }
            public long RequestId { get; private init; }
            public string WireSignature { get; private init; } = "";
            public string DecodedSummary { get; private init; } = "";
            public string SeenUtc { get; private init; } = "";
            public long AccountId { get; private init; }
            public long CharacterId { get; private init; }
            public long MapId { get; private init; }
            public string? SessionCorrelation { get; private init; }
            public string Phase { get; private init; } = "";
            public string ClientVersion { get; private init; } = "";
            public string FrameHash { get; private init; } = "";
            public string PayloadHash { get; private init; } = "";
            public byte[] Frame { get; private init; } = Array.Empty<byte>();
            public byte[] Payload { get; private init; } = Array.Empty<byte>();
            public string? Error { get; private init; }
            public string? HandlerHint { get; private init; }

            public static Snapshot Create(byte[] frame, string protocol, string direction,
                                          string classification, GameSession? session, string? error,
                                          string? fingerprintContext, string? handlerHint)
            {
                byte[] replayFrame = Limit(frame);
                string? opcode = ConnectionProtocol.ReadOpcode(frame);
                byte[] payload = opcode == null ? Array.Empty<byte>() :
                    (ConnectionProtocol.ReadPayload(frame, opcode) ?? Array.Empty<byte>());
                payload = Limit(payload);

                int root = 0;
                try
                {
                    var outer = ProtoMessage.Parse(frame);
                    if (outer.Fields.Count > 0) root = outer.Fields[0].FieldNumber;
                }
                catch { }

                string signature = BuildWireSignature(payload);
                string typeUrl = opcode == null ? "" : ConnectionProtocol.UriPrefix + opcode;
                string fingerprint = HashText(string.Join("|", protocol, direction, classification,
                    root, opcode ?? "<malformed>", signature, fingerprintContext ?? ""));

                string summary = DecodeSummary(payload);
                return new Snapshot
                {
                    Fingerprint = fingerprint,
                    Classification = classification,
                    Protocol = protocol,
                    Direction = direction,
                    Opcode = opcode,
                    TypeUrl = typeUrl.Length == 0 ? null : typeUrl,
                    EnvelopeRoot = root,
                    RequestId = root == 2 ? ConnectionProtocol.RequestId(frame) : -1,
                    WireSignature = signature,
                    DecodedSummary = summary,
                    SeenUtc = DateTime.UtcNow.ToString("O"),
                    AccountId = session?.AccountId ?? 0,
                    CharacterId = session?.CharacterId ?? 0,
                    MapId = session?.MapId ?? 0,
                    SessionCorrelation = session?.Id.ToString("N"),
                    Phase = SessionPhase(session),
                    ClientVersion = Contract.Version,
                    FrameHash = HashBytes(frame),
                    PayloadHash = HashBytes(payload),
                    Frame = replayFrame,
                    Payload = payload,
                    Error = error,
                    HandlerHint = handlerHint
                };
            }

            private static string SessionPhase(GameSession? session)
            {
                if (session == null) return "Connection";
                if (session.IsInWorld) return "World";
                if (session.HasCharacter) return "CharacterSelected";
                if (session.IsAuthenticated) return "GameAuthenticated";
                return "GameHandshake";
            }

            private static string DecodeSummary(byte[] payload)
            {
                if (payload.Length == 0) return "{}";
                try { return ProtoMessage.Parse(payload).Compact(1_024); }
                catch { return "raw:" + Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 96))).ToLowerInvariant(); }
            }

            private static string BuildWireSignature(byte[] payload)
            {
                if (payload.Length == 0) return "empty";
                try { return Signature(ProtoMessage.Parse(payload), 0); }
                catch { return "raw/" + payload.Length; }
            }

            private static string Signature(ProtoMessage message, int depth)
            {
                var parts = new List<string>();
                foreach (var field in message.Fields)
                {
                    string type = field.WireType switch
                    {
                        0 => "v",
                        1 => "f64",
                        5 => "f32",
                        2 => BytesSignature(field.BytesValue, depth),
                        _ => "wire" + field.WireType
                    };
                    parts.Add(field.FieldNumber + ":" + type);
                }
                return "{" + string.Join(",", parts) + "}";
            }

            private static string BytesSignature(byte[] bytes, int depth)
            {
                if (bytes.Length == 0) return "b0";
                if (depth >= 4 || bytes.Length > 8_192) return "b" + bytes.Length;
                try
                {
                    var nested = ProtoMessage.Parse(bytes);
                    return nested.Fields.Count == 0 ? "b" + bytes.Length : "m" + Signature(nested, depth + 1);
                }
                catch { return "b" + bytes.Length; }
            }

            private static byte[] Limit(byte[] bytes)
            {
                if (bytes.Length <= MaxSampleBytes) return bytes;
                var limited = new byte[MaxSampleBytes];
                Array.Copy(bytes, limited, limited.Length);
                return limited;
            }

            private static string HashBytes(byte[] bytes)
                => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            private static string HashText(string text)
                => HashBytes(Encoding.UTF8.GetBytes(text));
        }
    }
}
