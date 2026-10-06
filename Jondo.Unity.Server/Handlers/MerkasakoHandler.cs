using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Entering the haven bag, changing the decor and placing the furniture.
    ///
    /// From the captures:
    ///
    ///   client   jbn { f2: whose }        the button and the H key
    ///   client   jbl { f1: theme }        changing the decor
    ///   server   jru { f2: the map } + the whole map + jbu (furniture) + jaz (permissions)
    ///
    ///   client   jbv          server jbm        opening placement mode
    ///   client   jbg { f2 (rep): {f1: cell, f2: piece, f3: rotation} }   accept
    ///   client   jbk / jav / jaw   server jba                            closing the mode
    ///
    /// The jbg arrives in pieces -- in the capture there are three in a row -- and carries the WHOLE
    /// room, not the differences. That is why the pieces are put together and written at once on
    /// closing: saving each piece separately, the first would delete what the other two carry.
    /// </summary>
    public static class MerkasakoHandler
    {
        /// <summary>The furniture arriving while placement mode is open.</summary>
        /// <summary>
        /// The button and the H key, which is a different message from changing the decor:
        ///
        ///   client   jbn { f2: whose haven bag it is }
        ///
        /// It carries a character because somebody else's can be visited. Here there is only one, so one
        /// goes into one's own, with the decor left on the last time.
        ///
        /// And IT IS A TOGGLE: the same message goes in and out, and the server decides by where the
        /// player is. Measured in «Movimiento/ir al merkasako y volver.pcapng»: the player's requests #1
        /// and #8 are the same jbn with the body 10a28280c8e708 byte for byte; to the first it answers
        /// with map 162795538 -- subarea 851, the bags' one -- and to the second with 217056262, a normal
        /// world map. There is no third opcode: a sweep of the captures finds jbn in five files and jbl in
        /// one, and the empty jbl is decor 0, not the exit.
        ///
        /// Before, this called GoToThemeAsync without looking at anything, so the second H put the player
        /// back in the same room he wanted to leave.
        /// </summary>
        public static async Task EnterFromOutsideAsync(NetworkStream stream, byte[] payload)
        {
            if (ConnectionProtocol.ReadPayload(payload, Op.Jbn) == null) return;
            if (await Managers.Jail.KeepsInAsync(stream)) return;

            var state = Jondo.Unity.Server.Network.SessionContext.State;

            if (Merkasako.IsHavenBag(state.MapId))
            {
                await LeaveAsync(stream);
                return;
            }

            state.HavenBagEntryMapId = state.MapId;
            state.HavenBagEntryCell = state.CellId;

            await GoToThemeAsync(stream, HavenBagStore.ThemeOf(state.CharacterId));
        }

        /// <summary>
        /// Back to the world, the way he came in.
        /// </summary>
        /// <remarks>
        /// The same frames as leaving a house and in the same order, which is what the capture has: after
        /// the exit jru neither jbf, nor jbu, nor jaz appear -- the three that do go on ENTERING --, only
        /// lqu, lva, lva, iom and the jss.
        ///
        /// If it is not known where he came from -- he disconnected inside, or went in before this
        /// existed -- he is sent back to the starting point instead of being left locked in.
        /// </remarks>
        private static async Task LeaveAsync(NetworkStream stream)
        {
            var state = Jondo.Unity.Server.Network.SessionContext.State;
            long dentro = state.MapId;

            long salidaMapa = state.HavenBagEntryMapId;
            int salidaCasilla = state.HavenBagEntryCell;

            if (salidaMapa == 0 || Merkasako.IsHavenBag(salidaMapa))
            {
                salidaMapa = DatabaseManager.StartingMap;
                salidaCasilla = DatabaseManager.StartingCell;
                Console.WriteLine("[Merkasako] No se sabe de dónde entró: se le saca al punto de partida.");
            }

            state.MapId = salidaMapa;
            state.CellId = MapManager.GetNearestWalkableCell(salidaMapa, salidaCasilla);
            state.HavenBagEntryMapId = 0;
            state.HavenBagEntryCell = 0;
            DatabaseManager.SaveCurrentCharacter();

            await SessionRegistry.AnunciarMudanzaAsync(SessionContext.Current, dentro);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(state.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(salidaMapa));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(salidaMapa));

            Console.WriteLine($"[Merkasako] Fuera del {dentro} al mapa {salidaMapa}, " +
                              $"casilla {state.CellId}.");
        }

        /// <summary>Changing the decor from inside.</summary>
        public static async Task ChangeThemeAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbl = ConnectionProtocol.ReadPayload(payload, Op.Jbl);
            if (jbl == null) return;

            // Without f1 -- proto3 swallows the zero -- the usual one is meant.
            int theme = Merkasako.DefaultTheme;
            foreach (var field in ProtoMessage.Parse(jbl).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) theme = (int)field.VarIntValue;
            }

            await GoToThemeAsync(stream, theme);
        }

        private static async Task GoToThemeAsync(NetworkStream stream, int theme)
        {
            long target = Merkasako.MapOfTheme(theme);
            if (target == 0)
            {
                Console.WriteLine("[Merkasako] No hay ningún decorado en los datos del cliente.");
                return;
            }

            if (MapManager.GetMapInfo(target) == null)
            {
                Console.WriteLine($"[Merkasako] El mapa {target} no está en los datos del mundo.");
                return;
            }

            HavenBagStore.SaveTheme(Jondo.Unity.Server.Network.SessionContext.State.CharacterId, Merkasako.ThemeOfMap(target));

            long left = Jondo.Unity.Server.Network.SessionContext.State.MapId;
            Jondo.Unity.Server.Network.SessionContext.State.MapId = target;

            // Next to the zaap, which is where the game leaves one on entering.
            var zaap = Merkasako.ZaapOf(target);
            Jondo.Unity.Server.Network.SessionContext.State.CellId = MapManager.GetNearestWalkableCell(target, zaap.Cell);
            DatabaseManager.SaveCurrentCharacter();

            // Whoever stays on the map he left stops seeing him there. This was missing: going
            // into the haven bag left him standing on the street, drawn, for everybody on it.
            // Only the leaving is told -- a haven bag is nobody else's to be told about.
            if (left != target)
                await SessionRegistry.RemoveFromMapAsync(left, SessionContext.Current.CharacterId, SessionContext.Current.Id);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(Jondo.Unity.Server.Network.SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(target));

            Console.WriteLine($"[Merkasako] Decorado {Merkasako.ThemeOfMap(target)} -> mapa {target}, " +
                              $"casilla {Jondo.Unity.Server.Network.SessionContext.State.CellId} (zaap en la {zaap.Cell}).");
        }

        // ─── Furniture placement mode ───────────────────────────────────────────

        /// <summary>The client opens the management menu. An empty jbm is answered and it lets him place.</summary>
        public static async Task OpenEditorAsync(NetworkStream stream)
        {
            SessionContext.State.IsHavenBagEditing = true;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbm));

            Console.WriteLine("[Merkasako] Modo de colocar muebles abierto.");
        }

        /// <summary>A piece of the room. It is noted and the closing is waited for to write it.</summary>
        public static void CollectFurniture(byte[] payload)
        {
            byte[]? jbg = ConnectionProtocol.ReadPayload(payload, Op.Jbg);
            if (jbg == null) return;

            foreach (var field in ProtoMessage.Parse(jbg).Fields)
            {
                if (field.FieldNumber != 2 || field.WireType != 2) continue;

                int cell = 0, orientation = 0;
                long typeId = 0;
                foreach (var inner in ProtoMessage.Parse(field.BytesValue).Fields)
                {
                    if (inner.WireType != 0) continue;
                    if (inner.FieldNumber == 1) cell = (int)inner.VarIntValue;
                    else if (inner.FieldNumber == 2) typeId = (long)inner.VarIntValue;
                    else if (inner.FieldNumber == 3) orientation = (int)inner.VarIntValue;
                }

                // A piece of furniture that is not in the client's catalogue is not stored: the client
                // would not know how to draw it and the room would be left with an invisible blocking gap.
                if (typeId == 0 || !Merkasako.IsFurniture(typeId)) continue;

                SessionContext.State.PendingHavenBagFurniture.Add(new HavenBagStore.Furniture
                {
                    Cell = cell,
                    TypeId = typeId,
                    Orientation = orientation,
                });
            }
        }

        /// <summary>
        /// The client closes the menu. This is where the room is written to the database and sent back to
        /// him as it ended up.
        /// </summary>
        public static async Task CloseEditorAsync(NetworkStream stream)
        {
            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
            int theme = Merkasako.ThemeOfMap(Jondo.Unity.Server.Network.SessionContext.State.MapId);

            if (SessionContext.State.IsHavenBagEditing)
            {
                HavenBagStore.SaveFurniture(who, theme, SessionContext.State.PendingHavenBagFurniture);
                Console.WriteLine($"[Merkasako] Decorado {theme}: {SessionContext.State.PendingHavenBagFurniture.Count} mueble(s) guardados.");
            }

            SessionContext.State.IsHavenBagEditing = false;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await SendFurnitureAsync(stream);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jba));
        }

        /// <summary>
        /// The furniture and the permissions, which the client expects after the map. The permissions go
        /// empty: there is nobody here to invite.
        /// </summary>
        public static async Task SendFurnitureAsync(NetworkStream stream)
        {
            var pieces = HavenBagStore.FurnitureOf(Jondo.Unity.Server.Network.SessionContext.State.CharacterId,
                                                   Merkasako.ThemeOfMap(Jondo.Unity.Server.Network.SessionContext.State.MapId));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbu, ConnectionProtocol.BuildHavenBagFurniture(pieces)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jaz));
        }
    }
}
