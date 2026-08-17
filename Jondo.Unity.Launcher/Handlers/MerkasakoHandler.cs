using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Handlers
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
        /// </summary>
        public static async Task EnterFromOutsideAsync(NetworkStream stream, byte[] payload)
        {
            if (ConnectionProtocol.ReadPayload(payload, "jbn") == null) return;
            await GoToThemeAsync(stream, HavenBagStore.ThemeOf(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId));
        }

        /// <summary>Cambiarse de decorado desde dentro.</summary>
        public static async Task ChangeThemeAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? jbl = ConnectionProtocol.ReadPayload(payload, "jbl");
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

            HavenBagStore.SaveTheme(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId, Merkasako.ThemeOfMap(target));

            Jondo.Unity.Launcher.Network.SessionContext.State.MapId = target;

            // Al lado del zaap, que es donde deja a uno el juego al entrar.
            var zaap = Merkasako.ZaapOf(target);
            Jondo.Unity.Launcher.Network.SessionContext.State.CellId = MapManager.GetNearestWalkableCell(target, zaap.Cell);
            DatabaseManager.SaveCurrentCharacter();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildActorLeft(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildLoadMap(target));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapClock());
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.BuildMapDiscovered(target));

            Console.WriteLine($"[Merkasako] Decorado {Merkasako.ThemeOfMap(target)} -> mapa {target}, " +
                              $"casilla {Jondo.Unity.Launcher.Network.SessionContext.State.CellId} (zaap en la {zaap.Cell}).");
        }

        // ─── El modo de colocar muebles ─────────────────────────────────────────

        /// <summary>El cliente abre el menú de gestión. Contesta un jbm vacío y ya le deja colocar.</summary>
        public static async Task OpenEditorAsync(NetworkStream stream)
        {
            SessionContext.State.IsHavenBagEditing = true;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("jbm"));

            Console.WriteLine("[Merkasako] Modo de colocar muebles abierto.");
        }

        /// <summary>Un trozo de la habitación. Se apunta y se espera al cierre para escribirla.</summary>
        public static void CollectFurniture(byte[] payload)
        {
            byte[]? jbg = ConnectionProtocol.ReadPayload(payload, "jbg");
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
            long who = Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId;
            int theme = Merkasako.ThemeOfMap(Jondo.Unity.Launcher.Network.SessionContext.State.MapId);

            if (SessionContext.State.IsHavenBagEditing)
            {
                HavenBagStore.SaveFurniture(who, theme, SessionContext.State.PendingHavenBagFurniture);
                Console.WriteLine($"[Merkasako] Decorado {theme}: {SessionContext.State.PendingHavenBagFurniture.Count} mueble(s) guardados.");
            }

            SessionContext.State.IsHavenBagEditing = false;
            SessionContext.State.PendingHavenBagFurniture.Clear();

            await SendFurnitureAsync(stream);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("jba"));
        }

        /// <summary>
        /// Los muebles y los permisos, que el cliente espera detrás del mapa. Los permisos van
        /// vacíos: aquí no hay nadie a quien invitar.
        /// </summary>
        public static async Task SendFurnitureAsync(NetworkStream stream)
        {
            var pieces = HavenBagStore.FurnitureOf(Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId,
                                                   Merkasako.ThemeOfMap(Jondo.Unity.Launcher.Network.SessionContext.State.MapId));

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("jbu", ConnectionProtocol.BuildHavenBagFurniture(pieces)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push("jaz"));
        }
    }
}
