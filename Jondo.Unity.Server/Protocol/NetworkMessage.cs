using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Jondo.Unity.Protocol;

namespace Jondo.Protocol
{
    public static class NetworkMessage
    {
        private static readonly ConditionalWeakTable<Stream, SemaphoreSlim> WriteLocks
            = new ConditionalWeakTable<Stream, SemaphoreSlim>();

        public static async Task<byte[]> ReadFrameAsync(Stream stream)
        {
            // Read VarInt length
            int length = 0;
            int shift = 0;
            while (true)
            {
                byte[] buf = new byte[1];
                int read = await stream.ReadAsync(buf, 0, 1);
                if (read == 0) return null; // End of stream
                
                byte b = buf[0];
                length |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            
            // Read payload
            byte[] payload = new byte[length];
            int totalRead = 0;
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(payload, totalRead, length - totalRead);
                if (read == 0) return null; // End of stream prematurely
                totalRead += read;
            }

            ApuntarEntrada(totalRead);

            // Log packet
            try
            {
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Client -> Server", typeUrl, payload);
                }
            }
            catch { }
            
            return payload;
        }

        public static async Task WriteFrameAsync(Stream stream, IMessage message)
        {
            int size = message.CalculateSize();
            
            using var ms = new MemoryStream();
            var codedStream = new CodedOutputStream(ms);
            
            // Write length as VarInt
            codedStream.WriteUInt32((uint)size);
            
            // Write message payload
            message.WriteTo(codedStream);
            codedStream.Flush();
            
            byte[] buf = ms.ToArray();

            // Log packet
            try
            {
                int pos = 0;
                uint len = Jondo.Unity.Launcher.Network.NetworkEnvelope.ReadVarInt(buf, ref pos);
                byte[] payload = new byte[len];
                Array.Copy(buf, pos, payload, 0, len);
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Server -> Client", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            await WriteSerializedAsync(stream, buf);
        }

        public static async Task WriteFrameAsync(Stream stream, byte[] payload)
        {
            using var ms = new MemoryStream();
            var codedStream = new CodedOutputStream(ms);
            
            // Write length as VarInt
            codedStream.WriteUInt32((uint)payload.Length);
            codedStream.Flush();
            byte[] lenBytes = ms.ToArray();

            // Log packet
            try
            {
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Server -> Client", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            byte[] frame = new byte[lenBytes.Length + payload.Length];
            Buffer.BlockCopy(lenBytes, 0, frame, 0, lenBytes.Length);
            Buffer.BlockCopy(payload, 0, frame, lenBytes.Length, payload.Length);
            await WriteSerializedAsync(stream, frame);
        }

        // Use this for packets that already include their VarInt length prefix.
        // Keeping these writes behind the same per-stream gate prevents two
        // concurrent producers from interleaving bytes on a client socket.
        public static Task WriteRawFrameAsync(Stream stream, byte[] frame)
        {
            return WriteSerializedAsync(stream, frame);
        }

        // ─── El tráfico que pasa por aquí ────────────────────────────────────────────────────
        //
        // Dos pares de contadores: paquetes y bytes, de salida y de entrada. Los pinta la ventana
        // del servidor, y son la forma más directa de ver de un vistazo si está pasando algo o si
        // el servidor está mudo. Van con Interlocked porque los tocan todos los sockets a la vez.

        private static long _paquetesFuera, _bytesFuera, _paquetesDentro, _bytesDentro;

        public static long PaquetesFuera => Interlocked.Read(ref _paquetesFuera);
        public static long BytesFuera => Interlocked.Read(ref _bytesFuera);
        public static long PaquetesDentro => Interlocked.Read(ref _paquetesDentro);
        public static long BytesDentro => Interlocked.Read(ref _bytesDentro);

        /// <summary>Lo que acaba de llegar por un socket. Lo llama quien lee tramas.</summary>
        public static void ApuntarEntrada(int bytes)
        {
            Interlocked.Increment(ref _paquetesDentro);
            Interlocked.Add(ref _bytesDentro, bytes);
        }

        private static async Task WriteSerializedAsync(Stream stream, byte[] frame)
        {
            var gate = WriteLocks.GetValue(stream, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length);
                Interlocked.Increment(ref _paquetesFuera);
                Interlocked.Add(ref _bytesFuera, frame.Length);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>Cuántos paquetes llevamos, que es lo que numera cada renglón.</summary>
        private static int _packetCount;

        /// <summary>
        /// Un paquete, un renglón.
        ///
        /// Antes cada paquete ocupaba veinte líneas: la cabecera de colores, el volcado en
        /// hexadecimal y el árbol de campos con sus emojis. Con el juego andando eso son cientos de
        /// líneas por segundo y no se lee nada; el registro pasaba de largo antes de que te diera
        /// tiempo a mirarlo.
        ///
        /// Ahora sale así, que es como lo enseña un sniffer:
        ///
        ///   1579 [server&gt;client] kuf (CharacterExperienceGainEvent) { 1: 453 }        3 B
        ///
        /// No se pierde nada: el hexadecimal completo lo sigue escribiendo
        /// <see cref="GameServerProxy.LogTraffic"/> en gameserver_traffic.log, y el árbol de campos
        /// sigue estando en <c>ProtoMessage.DumpFieldsToString</c> para mirar un paquete concreto.
        /// Lo que cambia es qué se enseña EN VIVO, que es otra cosa.
        ///
        /// El nombre sale primero de la capa Op, que se genera de las anclas medidas, y sólo si ahí
        /// no está se cae a la tabla escrita a mano. Ese orden importa: una tabla a mano se queda
        /// vieja en cuanto Ankama rota los nombres y la generada no.
        /// </summary>
        private static void LogTrafficEnriched(string direction, string typeUrl, byte[] payload)
        {
            string opcode = typeUrl.Replace("type.ankama.com/", "").Trim();
            int number = System.Threading.Interlocked.Increment(ref _packetCount);

            // Primero lo que alguien haya ligado a mano, que es lo unico verificado. Op.Label queda
            // como respaldo y hoy esta vacio a proposito: los nombres inventados se quitaron.
            string name = Jondo.Unity.Launcher.Managers.NameBinding.Of(opcode);
            if (name.Length == 0) name = Op.Label(opcode);
            if (name.Length == 0)
            {
                string description = GetPacketMetadata(typeUrl).Description;
                int paren = description.IndexOf(" (", StringComparison.Ordinal);
                string candidate = paren > 0 ? description[..paren] : description;

                // El caso por defecto de la tabla devuelve «Utility message (xxx)», que no es un
                // nombre: es la manera de decir que no se sabe. Enseñarlo llenaría el registro de
                // una etiqueta que no distingue un paquete de otro.
                name = candidate.StartsWith("Utility message", StringComparison.Ordinal) ? "" : candidate;
            }

            // Lo que se enseña es el MENSAJE, no el sobre.
            //
            // Lo que llega aquí es la trama entera, y volcarla tal cual da
            // «{ 3: { 1: { 1: "type.ankama.com/jsq" } 2: -1 } }», que es fontanería: el campo raíz
            // que dice si va o viene, el Any con su url repetida y el id de petición. De los campos
            // del mensaje, que es lo único que se quiere leer, no se ve ni uno.
            //
            // ReadPayload busca la url dentro de la trama y devuelve lo que hay detrás, que es el
            // mensaje de verdad. El tamaño que se enseña también es el suyo y no el de la trama:
            // con el del sobre, un mensaje vacío parecía pesar treinta y seis bytes.
            byte[] dentro = Jondo.Unity.Launcher.Network.ConnectionProtocol.ReadPayload(payload, opcode)
                            ?? payload;

            string fields = "";
            try { fields = Jondo.Unity.Launcher.Network.ProtoMessage.Parse(dentro).Compact(); }
            catch { }

            string where = direction.StartsWith("Client", StringComparison.Ordinal)
                ? "client>server" : "server>client";

            // El cuerpo se rellena a un ancho fijo para que el tamaño caiga siempre en la misma
            // columna: leyendo hacia abajo se ve de un vistazo qué paquete abulta.
            string body = $"{number,5} [{where}] {opcode}" +
                          (name.Length > 0 ? $" ({name})" : "") +
                          (fields.Length > 0 ? $" {fields}" : "");

            lock (Console.Out)
            {
                Console.WriteLine(body.PadRight(112) + $"{payload.Length,6} B");
            }
        }

        private static (string Context, string Task, string Description) GetPacketMetadata(string typeUrl)
        {
            string uri = typeUrl.Replace("type.ankama.com/", "").Trim();

            switch (uri)
            {
                // === Context: Server List ===
                case "knx": return ("Server List", "Connection", "AuthTokenRequest (Token authentication request)");
                case Op.Kof: return ("Server List", "Connection", "ProtocolAccepted (Network protocol accepted)");
                case "lor": return ("Server List", "Sync", "TimeMessage (Server clock synchronization)");
                case Op.Hnp: return ("Server List", "Sync", "SystemConfiguration (Game variable configuration)");
                case "knr": return ("Server List", "Sync", "BreedListMessage (List of enabled character breeds)");
                case Op.Mfa: return ("Server List", "Connection", "FeatureStatus (Account feature status)");
                case Op.Mez: return ("Server List", "Connection", "ServerSeasonInfo (Season of the active server)");
                case Op.Hnv: return ("Server List", "Connection", "ServerOptionalFeatures (Optional features)");
                case "kpc": return ("Server List", "Connection", "PingRequest (Latency and session check)");
                case "kos": return ("Server List", "Connection", "ServerSelectionStatus (Server connection status)");

                // === Context: Character Selection ===
                case Op.Klp: return ("Character Selection", "Interfaces", "CharacterListEmpty (Initial empty character list)");
                case Op.Ksx: return ("Character Selection", "Interfaces", "CharacterListRequest (Character request - phase 1)");
                case Op.Kpa: return ("Character Selection", "Interfaces", "CharacterListRequest (Character request - phase 2)");
                case Op.Mes: return ("Character Selection", "Interfaces", "MessageWrapper (UI container wrapper)");
                case Op.Knv: return ("Character Selection", "Interfaces", "UiLayoutMessage (Metadata of the selection UI)");
                case "ksq": return ("Character Selection", "Character", "CharacterListMessage (Detailed character list)");
                case "jrf": return ("Character Selection", "Sync", "WorldReady (Signal that the world is ready to simulate)");
                case Op.Ksl: return ("Character Selection", "Character", "CharacterSelectionRequest (Request to enter the world)");

                // === Context: World Loading ===
                case Op.Kri: return ("World Loading", "Character", "CharacterStatsListMessage (Full stats and characteristics)");
                case Op.Itp: return ("World Loading", "Interfaces", "ShortcutBarContentMessage (Keyboard shortcuts and UI bars)");
                case "izn": return ("World Loading", "Interfaces", "ChatChannelsListMessage (Initialization of the local chat channels)");
                case Op.Krh: return ("World Loading", "Sync", "ClientReadyForPackets (Ack that the client is ready)");
                case Op.Imd: return ("World Loading", "Inventory", "InventoryWeightMessage (Basic initialization of the inventory weight)");
                case Op.Ktw: return ("World Loading", "Character", "CharacterSelectedSuccessMessage (Character spawn on the pedestal)");
                case "mek": return ("World Loading", "Interfaces", "SpellListMessage (The character's spell book)");
                case Op.Lry: return ("World Loading", "Interfaces", "QuestListMessage (Active quest list and journal)");
                case "icb": return ("World Loading", "Character", "CharacterStatsListMessage (Combat state and base stats)");
                case Op.Irm: return ("World Loading", "Character", "MapActorsListMessage (Initial NPC and mob spawns of the map)");
                case Op.Hke: return ("World Loading", "Interfaces", "ServerWelcomeMessage (Welcome message and news of the day)");
                case "kfr": return ("World Loading", "Character", "EmoteListMessage (Unlocked emotes and animations)");
                case Op.Ipv: return ("World Loading", "Map", "MapComplementaryInformationsData (Cell interactives)");
                case "ipu": return ("World Loading", "Map", "MapInteractiveElements (Active doors and triggers)");
                case Op.Ipw: return ("World Loading", "Map", "MapStatedElements (Visual state of the interactive elements)");
                case "icw": return ("World Loading", "Inventory", "InventoryContentMessage (Full inventory - 180 items)");
                case Op.Loy: return ("World Loading", "Sync", "WorldLoadAck (Client ack of a successful map load)");
                case "lok": return ("World Loading", "Sync", "SelectedServerData (Server session metadata)");
                case "jdj": return ("World Loading", "Sync", "ServerDateMessage (Server date synchronization)");

                // === Context: World Loading - 33 Packets Transition Burst ===
                case Op.Kqo: return ("World Loading", "Interfaces", "ChatChannelsReadMessage (Chat channels open for reading)");
                case Op.Hhq: return ("World Loading", "Interfaces", "SocialGroupPackets (Guild and alliance information)");
                case Op.Hml: return ("World Loading", "Interfaces", "SocialPreferences (The player's social settings)");
                case Op.Isf: return ("World Loading", "Sync", "QuestListMessage (Notification of the active quests)");
                case Op.Lol: return ("World Loading", "Interfaces", "NotificationListMessage (Quest notifications)");
                case "icg": return ("World Loading", "Inventory", "InventoryWeightMessage (Inventory carry pods)");
                case Op.Ibo: return ("World Loading", "Interfaces", "ShortcutBarContentMessage (Quick spell bar)");
                case Op.Hmj: return ("World Loading", "Interfaces", "SocialGroupStatus (The player's guild status)");
                case Op.Lxs: return ("World Loading", "Sync", "AlignmentSubAreaUpdate (PvP and sub-area alignment)");
                case "hnq": return ("World Loading", "Sync", "SpouseStatusMessage (Marital status / marriage)");
                case Op.Ksv: return ("World Loading", "Character", "CharacterCapabilitiesMessage (Stat caps and capabilities)");
                case Op.Lou: return ("World Loading", "Connection", "ServerAccessStatus (Server accessibility status)");
                case Op.Iya: return ("World Loading", "Sync", "FeatureStatusMessage (Active experimental features)");
                case Op.Kdx: return ("World Loading", "Connection", "AccountCapabilitiesMessage (Global account rights)");
                case Op.Izh: return ("World Loading", "Sync", "AlmanaxDateMessage (Almanax day and active bonus)");
                case "ity": return ("World Loading", "Sync", "ExpMultiplierMessage (Global experience multipliers)");
                case Op.Koj: return ("World Loading", "Map", "HavenBagStatusMessage (Haven bag or player house data)");
                case "kyj": return ("World Loading", "Sync", "ArenaRankInfosMessage (Kolossium league information)");
                case "ktj": return ("World Loading", "Character", "ExpGainDetails (Details of the accumulated experience pool)");
                case Op.Ltk: return ("World Loading", "Interfaces", "TitleListMessage (Available honorific titles)");
                case "lvk": return ("World Loading", "Interfaces", "OrnamentListMessage (Available graphical ornaments)");
                case Op.Lwb: return ("World Loading", "Character", "EmoteListMessage (Unlocked emoticons)");
                case Op.Luy: return ("World Loading", "Inventory", "JobDescriptionMessage (List of learned jobs)");
                case Op.Hhf: return ("World Loading", "Interfaces", "SocialGroupRights (Rights granted within the guild)");
                case Op.Hhh: return ("World Loading", "Interfaces", "SocialGroupDetails (Descriptive sheet of the guild)");
                case Op.Luq: return ("World Loading", "Interfaces", "JobCrafterDirectorySettings (Job directory visibility settings)");
                case "hhi": return ("World Loading", "Interfaces", "SocialGroupAlliance (Sheet of the alliance)");
                case Op.Idf: return ("World Loading", "Inventory", "InventoryPreview (Quick preview of the items)");
                case Op.Izu: return ("World Loading", "Sync", "QuestStepProgress (Current quest progress)");

                // === Context: World Loading - kkn Burst ===
                case "kkn": return ("World Loading", "Sync", "MapLoadCompleted (Notification that the graphics load finished)");
                case "kkp": return ("World Loading", "Interfaces", "SocialStatusMessage (Configuration of the online social status)");
                case Op.Kkm: return ("World Loading", "Interfaces", "SocialOptionsMessage (Friend notification settings)");
                case "krb": return ("World Loading", "Character", "CharacterRemainingPoints (Confirmed remaining stat points)");
                case Op.Ilc: return ("World Loading", "Sync", "ServerSettingsMessage (Regional settings of the server)");
                case Op.Joh: return ("World Loading", "Map", "CurrentMapMessage (Confirms the Map ID to load into the scene)");
                case Op.Hmd: return ("World Loading", "Inventory", "InventoryWeightMessage (Inventory carry pods)");
                case Op.Lpj: return ("World Loading", "Sync", "SecondaryReadySignal (Signal that the secondary threads are ready)");
                case Op.Lpe: return ("World Loading", "Sync", "SecondaryReadyConfirm (Ack that the secondary threads are ready)");
                case "hmv": return ("World Loading", "Chat", "ChatChannelsListRequest (Request for the chat channels)");
                case Op.Hnk: return ("World Loading", "Chat", "ChatChannelsListMessage (Available chat channels)");
                case Op.Kqm: return ("World Loading", "Chat", "ChatChannelConfigMessage (Configuration and colour of the chat channel)");
                case "ibt": return ("World Loading", "Sync", "GameReadyTrigger (Request to hand over control of the game)");
                case Op.Ith: return ("World Loading", "Character", "FullCharacterStatsMessage (Bulk stats sheet)");

                // === Context: In Game ===
                case Op.Kkr: return ("In Game", "Map", "MapComplementaryInfoRequest (Request for actors and interactives)");
                case Op.Lxd: return ("In Game", "Map", "MapComplementaryInfo (Wrapper for interactives, cells and weather)");
                case Op.Jpv: return ("In Game", "Character", "MapActorsShowMessage (Character spawns on the current map)");
                case Op.Kns: return ("In Game", "Sync", "KnockAck / Heartbeat (Sync heartbeat / Pong)");
                case Op.Kod: return ("In Game", "Sync", "HeartbeatRequest (Sync heartbeat / Ping)");
                case "joi": return ("In Game", "Character", "PlayerMovementRequest (Cell-by-cell movement request)");
                case "jpp": return ("In Game", "Character", "PlayerMovementConfirm (Confirms the destination cell was reached)");
                case Op.Jos: return ("In Game", "Map", "MapChangeRequest (Request to cross the map boundary)");
                case Op.Isi: return ("In Game", "Inventory", "ItemMovementRequest (Request to equip/move an item)");
                case Op.Iry: return ("In Game", "Inventory", "ItemMovementConfirm (Confirmation that the item was equipped)");
                case Op.Krc: return ("In Game", "Character", "StatsUpgradeRequest (Request to assign points to stats)");
                case "kqn": return ("In Game", "Chat", "ChatSendRequest (Sending a chat message typed by the player)");
                case Op.Kqp: return ("In Game", "Chat", "ChatBroadcastMessage (Broadcast of a chat message on the channel)");

                default: return ("In Game", "Unknown", $"Utility message ({uri})");
            }
        }
    }
}
