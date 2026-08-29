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
    /// Entrar al merkasako, cambiarse de decorado y colocar los muebles.
    ///
    /// De las capturas:
    ///
    ///   cliente  jbn { f2: de quién }     el botón y la tecla H
    ///   cliente  jbl { f1: tema }         cambiarse de decorado
    ///   servidor jru { f2: el mapa } + el mapa entero + jbu (muebles) + jaz (permisos)
    ///
    ///   cliente  jbv          servidor jbm        abrir el modo de colocar
    ///   cliente  jbg { f2 (rep): {f1: casilla, f2: mueble, f3: giro} }   aceptar
    ///   cliente  jbk / jav / jaw   servidor jba                          cerrar el modo
    ///
    /// El jbg llega troceado —en la captura son tres seguidos— y trae la habitación ENTERA, no las
    /// diferencias. Por eso se juntan los trozos y se escriben de una vez al cerrar: guardando cada
    /// trozo por separado, el primero borraría lo que traen los otros dos.
    /// </summary>
    public static class MerkasakoHandler
    {
        /// <summary>Los muebles que van llegando mientras el modo de colocar está abierto.</summary>
        /// <summary>
        /// El botón y la tecla H, que es un mensaje distinto del de cambiar de decorado:
        ///
        ///   cliente  jbn { f2: de quién es el merkasako }
        ///
        /// Lleva un personaje porque se puede visitar el de otro. Aquí solo hay uno, así que se
        /// entra al propio, al decorado que se dejó puesto la última vez.
        ///
        /// Y ES UN INTERRUPTOR: el mismo mensaje entra y sale, y decide el servidor por dónde
        /// está el jugador. Medido en «Movimiento/ir al merkasako y volver.pcapng»: las
        /// peticiones #1 y #8 del jugador son el mismo jbn con el cuerpo 10a28280c8e708 byte a
        /// byte; a la primera contesta con el mapa 162795538 —subzona 851, la de las bolsas— y a
        /// la segunda con el 217056262, un mapa normal del mundo. No hay un tercer opcode: un
        /// barrido de las capturas encuentra jbn en cinco ficheros y jbl en uno, y el jbl vacío es
        /// el decorado 0, no la salida.
        ///
        /// Antes esto llamaba a GoToThemeAsync sin mirar nada, así que la segunda H volvía a
        /// meter al jugador en la misma habitación de la que quería salir.
        /// </summary>
        public static async Task EnterFromOutsideAsync(NetworkStream stream, byte[] payload)
        {
            if (ConnectionProtocol.ReadPayload(payload, Op.Jbn) == null) return;

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
        /// De vuelta al mundo, por donde se entró.
        /// </summary>
        /// <remarks>
        /// Las mismas tramas que la salida de una casa y en el mismo orden, que es lo que hay en
        /// la captura: tras el jru de salida no aparece ni jbf, ni jbu, ni jaz —los tres que sí
        /// van al ENTRAR—, sólo lqu, lva, lva, iom y el jss.
        ///
        /// Si no se sabe de dónde se vino —se desconectó dentro, o se entró antes de que esto
        /// existiera— se le devuelve al punto de partida en vez de dejarlo encerrado.
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

        /// <summary>Cambiarse de decorado desde dentro.</summary>
        public static async Task ChangeThemeAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbl = ConnectionProtocol.ReadPayload(payload, Op.Jbl);
            if (jbl == null) return;

            // Sin f1 —proto3 se come el cero— se entiende el de siempre.
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

            Jondo.Unity.Server.Network.SessionContext.State.MapId = target;

            // Al lado del zaap, que es donde deja a uno el juego al entrar.
            var zaap = Merkasako.ZaapOf(target);
            Jondo.Unity.Server.Network.SessionContext.State.CellId = MapManager.GetNearestWalkableCell(target, zaap.Cell);
            DatabaseManager.SaveCurrentCharacter();

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

        // ─── El modo de colocar muebles ─────────────────────────────────────────

        /// <summary>El cliente abre el menú de gestión. Contesta un jbm vacío y ya le deja colocar.</summary>
        public static async Task OpenEditorAsync(NetworkStream stream)
        {
            SessionContext.State.IsHavenBagEditing = true;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jbm));

            Console.WriteLine("[Merkasako] Modo de colocar muebles abierto.");
        }

        /// <summary>Un trozo de la habitación. Se apunta y se espera al cierre para escribirla.</summary>
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

                // Un mueble que no está en el catálogo del cliente no se guarda: el cliente no
                // sabría dibujarlo y la habitación quedaría con un hueco invisible que bloquea.
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
        /// El cliente cierra el menú. Aquí es donde la habitación se escribe en la base de datos y
        /// se le devuelve tal como ha quedado.
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
        /// Los muebles y los permisos, que el cliente espera detrás del mapa. Los permisos van
        /// vacíos: aquí no hay nadie a quien invitar.
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
