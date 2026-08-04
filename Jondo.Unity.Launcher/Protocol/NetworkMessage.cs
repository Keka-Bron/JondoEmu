using System;
using System.IO;
using System.Threading.Tasks;
using Google.Protobuf;

namespace Jondo.Protocol
{
    public static class NetworkMessage
    {
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
            
            // Log packet
            try
            {
                string? typeUrl = Jondo.Unity.Launcher.Network.NetworkEnvelope.GetMessageTypeUrl(payload);
                if (typeUrl != null)
                {
                    LogTrafficEnriched("Cliente -> Servidor", typeUrl, payload);
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
                    LogTrafficEnriched("Servidor -> Cliente", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            await stream.WriteAsync(buf, 0, buf.Length);
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
                    LogTrafficEnriched("Servidor -> Cliente", typeUrl, payload);
                }
                Jondo.Unity.Launcher.Network.GameServerProxy.LogTraffic("S->C", payload, payload.Length);
            }
            catch { }
            
            await stream.WriteAsync(lenBytes, 0, lenBytes.Length);
            await stream.WriteAsync(payload, 0, payload.Length);
        }

        private static void LogTrafficEnriched(string direction, string typeUrl, byte[] payload)
        {
            int length = payload.Length;
            var meta = GetPacketMetadata(typeUrl);
            
            // Choose color for direction
            ConsoleColor dirColor = direction.Contains("Cliente") ? ConsoleColor.Cyan : ConsoleColor.Green;
            
            // Choose color for categories to make it visual
            ConsoleColor taskColor = ConsoleColor.Gray;
            if (meta.Task == "Personaje") taskColor = ConsoleColor.Yellow;
            else if (meta.Task == "Interfaces") taskColor = ConsoleColor.Magenta;
            else if (meta.Task == "Inventario") taskColor = ConsoleColor.DarkYellow;
            else if (meta.Task == "Mapa") taskColor = ConsoleColor.Blue;
            else if (meta.Task == "Chat") taskColor = ConsoleColor.Red;
            else if (meta.Task == "Conexión") taskColor = ConsoleColor.DarkCyan;
            else if (meta.Task == "Sincronización") taskColor = ConsoleColor.DarkGreen;

            lock (Console.Out)
            {
                Console.ForegroundColor = dirColor;
                Console.Write($"[{direction}] ");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"({length} B) ");
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"[{meta.Context}] ");
                
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"[{meta.Task}] ");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{typeUrl.Replace("type.ankama.com/", "")} -> ");
                
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(meta.Description);

                string shortType = typeUrl.Replace("type.ankama.com/", "").Trim();
                bool isPing = shortType == "kod" || shortType == "kns" || shortType == "kpc" || shortType == "jgv";

                if (!isPing)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    string hexStr = BitConverter.ToString(payload);
                    if (hexStr.Length > 160) hexStr = hexStr.Substring(0, 160) + "... [truncated]";
                    Console.WriteLine($"   📦 Hex Payload: {hexStr}");

                    try
                    {
                        var protoTree = Jondo.Unity.Launcher.Network.ProtoMessage.Parse(payload);
                        string treeDump = protoTree.DumpFieldsToString("      ", maxLines: 12);
                        if (!string.IsNullOrWhiteSpace(treeDump))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine("   🌳 Protobuf Payload Tree:");
                            Console.WriteLine(treeDump);
                        }
                    }
                    catch { }
                }
                
                Console.ResetColor();
            }
        }

        private static (string Context, string Task, string Description) GetPacketMetadata(string typeUrl)
        {
            string uri = typeUrl.Replace("type.ankama.com/", "").Trim();

            switch (uri)
            {
                // === Context: Lista de Servidores ===
                case "knx": return ("Lista de Servidores", "Conexión", "AuthTokenRequest (Solicitud de autenticación de token)");
                case "kof": return ("Lista de Servidores", "Conexión", "ProtocolAccepted (Protocolo de red aceptado)");
                case "lor": return ("Lista de Servidores", "Sincronización", "TimeMessage (Sincronización horaria del servidor)");
                case "hnp": return ("Lista de Servidores", "Sincronización", "SystemConfiguration (Configuración de variables del juego)");
                case "knr": return ("Lista de Servidores", "Sincronización", "BreedListMessage (Lista de razas de personajes activas)");
                case "mfa": return ("Lista de Servidores", "Conexión", "FeatureStatus (Estado de características de la cuenta)");
                case "mez": return ("Lista de Servidores", "Conexión", "ServerSeasonInfo (Temporada del servidor activo)");
                case "hnv": return ("Lista de Servidores", "Conexión", "ServerOptionalFeatures (Funcionalidades opcionales)");
                case "kpc": return ("Lista de Servidores", "Conexión", "PingRequest (Validación de latencia y sesión)");
                case "kos": return ("Lista de Servidores", "Conexión", "ServerSelectionStatus (Estado de conexión del servidor)");

                // === Context: Elegir Personaje ===
                case "klp": return ("Elegir Personaje", "Interfaces", "CharacterListEmpty (Lista de personajes vacía inicial)");
                case "ksx": return ("Elegir Personaje", "Interfaces", "CharacterListRequest (Petición de personajes - fase 1)");
                case "kpa": return ("Elegir Personaje", "Interfaces", "CharacterListRequest (Petición de personajes - fase 2)");
                case "mes": return ("Elegir Personaje", "Interfaces", "MessageWrapper (Wrapper contenedor de interfaz)");
                case "knv": return ("Elegir Personaje", "Interfaces", "UiLayoutMessage (Metadatos de la interfaz de selección)");
                case "ksq": return ("Elegir Personaje", "Personaje", "CharacterListMessage (Lista detallada de personajes con Bruxa)");
                case "jrf": return ("Elegir Personaje", "Sincronización", "WorldReady (Señal de mundo listo para simulación)");
                case "ksl": return ("Elegir Personaje", "Personaje", "CharacterSelectionRequest (Petición para entrar al mundo)");

                // === Context: Carga del Mundo ===
                case "kri": return ("Carga del Mundo", "Personaje", "CharacterStatsListMessage (Estadísticas y características completas)");
                case "itp": return ("Carga del Mundo", "Interfaces", "ShortcutBarContentMessage (Atajos de teclado y barras de interfaz)");
                case "izn": return ("Carga del Mundo", "Interfaces", "ChatChannelsListMessage (Inicialización de canales de chat locales)");
                case "krh": return ("Carga del Mundo", "Sincronización", "ClientReadyForPackets (Ack de preparación del cliente)");
                case "imd": return ("Carga del Mundo", "Inventario", "InventoryWeightMessage (Inicialización básica del peso de inventario)");
                case "ktw": return ("Carga del Mundo", "Personaje", "CharacterSelectedSuccessMessage (Spawn del personaje en el pedestal)");
                case "mek": return ("Carga del Mundo", "Interfaces", "SpellListMessage (Libro de hechizos del personaje)");
                case "lry": return ("Carga del Mundo", "Interfaces", "QuestListMessage (Lista de misiones activas y diario)");
                case "icb": return ("Carga del Mundo", "Personaje", "CharacterStatsListMessage (Estado de combate y stats base)");
                case "irm": return ("Carga del Mundo", "Personaje", "MapActorsListMessage (Spawns iniciales de NPCs y mobs del mapa)");
                case "hke": return ("Carga del Mundo", "Interfaces", "ServerWelcomeMessage (Mensaje de bienvenida y noticias del día)");
                case "kfr": return ("Carga del Mundo", "Personaje", "EmoteListMessage (Emotes y animaciones desbloqueadas)");
                case "ipv": return ("Carga del Mundo", "Mapa", "MapComplementaryInformationsData (Interactivos de celdas)");
                case "ipu": return ("Carga del Mundo", "Mapa", "MapInteractiveElements (Puertas y triggers activos)");
                case "ipw": return ("Carga del Mundo", "Mapa", "MapStatedElements (Estado visual de elementos interactivos)");
                case "icw": return ("Carga del Mundo", "Inventario", "InventoryContentMessage (Inventario completo - 180 objetos)");
                case "loy": return ("Carga del Mundo", "Sincronización", "WorldLoadAck (Ack de carga de mapa exitosa por el cliente)");
                case "lok": return ("Carga del Mundo", "Sincronización", "SelectedServerData (Metadatos de sesión del servidor)");
                case "jdj": return ("Carga del Mundo", "Sincronización", "ServerDateMessage (Sincronización de fecha del servidor)");

                // === Context: Carga del Mundo - 33 Packets Transition Burst ===
                case "kqo": return ("Carga del Mundo", "Interfaces", "ChatChannelsReadMessage (Canales de chat para lectura)");
                case "hhq": return ("Carga del Mundo", "Interfaces", "SocialGroupPackets (Información de gremio y alianzas)");
                case "hml": return ("Carga del Mundo", "Interfaces", "SocialPreferences (Ajustes sociales del jugador)");
                case "isf": return ("Carga del Mundo", "Sincronización", "QuestListMessage (Notificación de misiones activas)");
                case "lol": return ("Carga del Mundo", "Interfaces", "NotificationListMessage (Notificaciones de misiones)");
                case "icg": return ("Carga del Mundo", "Inventario", "InventoryWeightMessage (Pods de carga del inventario)");
                case "ibo": return ("Carga del Mundo", "Interfaces", "ShortcutBarContentMessage (Barra de hechizos rápidos)");
                case "hmj": return ("Carga del Mundo", "Interfaces", "SocialGroupStatus (Estado de gremio del jugador)");
                case "lxs": return ("Carga del Mundo", "Sincronización", "AlignmentSubAreaUpdate (PvP y alineamiento de zona)");
                case "hnq": return ("Carga del Mundo", "Sincronización", "SpouseStatusMessage (Estado civil / matrimonio)");
                case "ksv": return ("Carga del Mundo", "Personaje", "CharacterCapabilitiesMessage (Límites de stats y capacidad)");
                case "lou": return ("Carga del Mundo", "Conexión", "ServerAccessStatus (Estado de accesibilidad del servidor)");
                case "iya": return ("Carga del Mundo", "Sincronización", "FeatureStatusMessage (Características experimentales activas)");
                case "kdx": return ("Carga del Mundo", "Conexión", "AccountCapabilitiesMessage (Derechos globales de la cuenta)");
                case "izh": return ("Carga del Mundo", "Sincronización", "AlmanaxDateMessage (Día y bonus activo del almanax)");
                case "ity": return ("Carga del Mundo", "Sincronización", "ExpMultiplierMessage (Multiplicadores de experiencia globales)");
                case "koj": return ("Carga del Mundo", "Mapa", "HavenBagStatusMessage (Datos de merkasako o casa propia)");
                case "kyj": return ("Carga del Mundo", "Sincronización", "ArenaRankInfosMessage (Información de liga de Koliseo)");
                case "ktj": return ("Carga del Mundo", "Personaje", "ExpGainDetails (Detalles del capital de experiencia acumulado)");
                case "ltk": return ("Carga del Mundo", "Interfaces", "TitleListMessage (Títulos honoríficos disponibles)");
                case "lvk": return ("Carga del Mundo", "Interfaces", "OrnamentListMessage (Ornamentos gráficos disponibles)");
                case "lwb": return ("Carga del Mundo", "Personaje", "EmoteListMessage (Emoticonos desbloqueados)");
                case "luy": return ("Carga del Mundo", "Inventario", "JobDescriptionMessage (Lista de oficios aprendidos)");
                case "hhf": return ("Carga del Mundo", "Interfaces", "SocialGroupRights (Derechos asignados en gremio)");
                case "hhh": return ("Carga del Mundo", "Interfaces", "SocialGroupDetails (Ficha descriptiva del gremio)");
                case "luq": return ("Carga del Mundo", "Interfaces", "JobCrafterDirectorySettings (Ajustes de visibilidad de oficios)");
                case "hhi": return ("Carga del Mundo", "Interfaces", "SocialGroupAlliance (Ficha de la alianza)");
                case "idf": return ("Carga del Mundo", "Inventario", "InventoryPreview (Previsualización rápida de objetos)");
                case "izu": return ("Carga del Mundo", "Sincronización", "QuestStepProgress (Avance actual de misiones)");

                // === Context: Carga del Mundo - kkn Burst ===
                case "kkn": return ("Carga del Mundo", "Sincronización", "MapLoadCompleted (Notificación de carga gráfica concluida)");
                case "kkp": return ("Carga del Mundo", "Interfaces", "SocialStatusMessage (Configuración de estado social en línea)");
                case "kkm": return ("Carga del Mundo", "Interfaces", "SocialOptionsMessage (Ajustes de notificaciones de amigos)");
                case "krb": return ("Carga del Mundo", "Personaje", "CharacterRemainingPoints (Puntos de stats restantes confirmados)");
                case "ilc": return ("Carga del Mundo", "Sincronización", "ServerSettingsMessage (Ajustes regional del servidor)");
                case "joh": return ("Carga del Mundo", "Mapa", "CurrentMapMessage (Confirmación del Map ID a cargar en escena)");
                case "hmd": return ("Carga del Mundo", "Inventario", "InventoryWeightMessage (Pods de carga del inventario)");
                case "lpj": return ("Carga del Mundo", "Sincronización", "SecondaryReadySignal (Señal de hilos secundarios listos)");
                case "lpe": return ("Carga del Mundo", "Sincronización", "SecondaryReadyConfirm (Ack de hilos secundarios listos)");
                case "hmv": return ("Carga del Mundo", "Chat", "ChatChannelsListRequest (Petición de canales de chat)");
                case "hnk": return ("Carga del Mundo", "Chat", "ChatChannelsListMessage (Canales de chat disponibles)");
                case "kqm": return ("Carga del Mundo", "Chat", "ChatChannelConfigMessage (Configuración y color del canal de chat)");
                case "ibt": return ("Carga del Mundo", "Sincronización", "GameReadyTrigger (Petición para dar el control de juego)");
                case "ith": return ("Carga del Mundo", "Personaje", "FullCharacterStatsMessage (Ficha masiva de estadísticas)");

                // === Context: En el Juego ===
                case "kkr": return ("En el Juego", "Mapa", "MapComplementaryInfoRequest (Petición de actores e interactivos)");
                case "lxd": return ("En el Juego", "Mapa", "MapComplementaryInfo (Wrapper de interactivos, celdas y clima)");
                case "jpv": return ("En el Juego", "Personaje", "MapActorsShowMessage (Spawns de personajes en el mapa actual)");
                case "kns": return ("En el Juego", "Sincronización", "KnockAck / Heartbeat (Latido de sincronización / Pong)");
                case "kod": return ("En el Juego", "Sincronización", "HeartbeatRequest (Latido de sincronización / Ping)");
                case "joi": return ("En el Juego", "Personaje", "PlayerMovementRequest (Petición de movimiento por celdas)");
                case "jpp": return ("En el Juego", "Personaje", "PlayerMovementConfirm (Confirmación de celda de destino alcanzada)");
                case "jos": return ("En el Juego", "Mapa", "MapChangeRequest (Petición para cruzar la frontera del mapa)");
                case "isi": return ("En el Juego", "Inventario", "ItemMovementRequest (Petición para equipar/mover un objeto)");
                case "iry": return ("En el Juego", "Inventario", "ItemMovementConfirm (Confirmación de equipación de objeto)");
                case "krc": return ("En el Juego", "Personaje", "StatsUpgradeRequest (Petición para asignar puntos a estadísticas)");
                case "kqn": return ("En el Juego", "Chat", "ChatSendRequest (Envío de mensaje de chat escrito por el jugador)");
                case "kqp": return ("En el Juego", "Chat", "ChatBroadcastMessage (Difusión de mensaje de chat en el canal)");

                default: return ("En el Juego", "Desconocido", $"Mensaje de utilidad ({uri})");
            }
        }
    }
}
