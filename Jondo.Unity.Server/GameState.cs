using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// El estado del personaje, para el código que todavía no se ha migrado a las sesiones.
    ///
    /// Ya NO guarda nada: cada propiedad reenvía a la sesión de la conexión actual. Es la fachada
    /// que pedía docs/multijugador.md para poder migrar por ficheros en vez de cambiar 263 sitios
    /// de golpe.
    ///
    /// Hasta ahora esta clase seguía teniendo campos propios CON VALORES POR DEFECTO. El refactor
    /// de sesiones convirtió a todos los que escriben, pero no a todos los que leen, así que los
    /// 263 accesos que quedaban recibían aquellos valores de hace meses: nivel 40, y el mapa
    /// 154010883. De ahí que un personaje de nivel 200 guardado en Amakna apareciera en Incarnam
    /// —el mapa por defecto de la línea 18— sin que nada avisara. Ninguno de los dos estados
    /// estaba mal: es que había dos, y el código leía el que ya nadie mantenía.
    ///
    /// Lo que queda por hacer es sustituir los usos por SessionContext.State fichero a fichero;
    /// mientras tanto, todo apunta al mismo sitio y no puede volver a descuadrarse.
    /// </summary>
    public static class GameState
    {
        private static SessionState S => Network.SessionContext.State;

        // Quién es
        public static long CharacterId { get => S.CharacterId; set => S.CharacterId = value; }
        public static string CharacterName { get => S.CharacterName; set => S.CharacterName = value; }
        public static int CharacterLevel { get => S.CharacterLevel; set => S.CharacterLevel = value; }
        public static int Breed { get => S.Breed; set => S.Breed = value; }
        public static int Sex { get => S.Sex; set => S.Sex = value; }
        public static byte[]? PlayerActorDetails { get => S.PlayerActorDetails; set => S.PlayerActorDetails = value; }
        public static byte[]? LookBytes { get => S.LookBytes; set => S.LookBytes = value; }

        // Dónde está
        public static long MapId { get => S.MapId; set => S.MapId = value; }
        public static int CellId { get => S.CellId; set => S.CellId = value; }
        public static int Orientation { get => S.Orientation; set => S.Orientation = value; }
        public static long Kamas { get => S.Kamas; set => S.Kamas = value; }

        /// <summary>La experiencia ACUMULADA, no la del nivel actual.</summary>
        public static long Experience { get => S.Experience; set => S.Experience = value; }

        // El combate
        public static bool IsInFight { get => S.IsInFight; set => S.IsInFight = value; }
        public static long CurrentFightMobId { get => S.CurrentFightMobId; set => S.CurrentFightMobId = value; }

        // Las características y el capital
        public static int CharacterRemainingPoints { get => S.CharacterRemainingPoints; set => S.CharacterRemainingPoints = value; }
        public static int StatVitality { get => S.StatVitality; set => S.StatVitality = value; }
        public static int StatWisdom { get => S.StatWisdom; set => S.StatWisdom = value; }
        public static int StatStrength { get => S.StatStrength; set => S.StatStrength = value; }
        public static int StatIntelligence { get => S.StatIntelligence; set => S.StatIntelligence = value; }
        public static int StatChance { get => S.StatChance; set => S.StatChance = value; }
        public static int StatAgility { get => S.StatAgility; set => S.StatAgility = value; }
        public static int BaseActionPoints { get => S.BaseActionPoints; set => S.BaseActionPoints = value; }
        public static int BaseMovementPoints { get => S.BaseMovementPoints; set => S.BaseMovementPoints = value; }

        // El inventario y el equipo
        public static List<PlayerItem> GetInventoryCopy() => S.GetInventoryCopy();
        public static void SetInventory(List<PlayerItem> items) => S.SetInventory(items);
        public static void AddInventoryItem(PlayerItem item) => S.AddInventoryItem(item);
        public static void ClearInventory() => S.ClearInventory();
        public static PlayerItem? GetInventoryItem(long uid) => S.GetInventoryItem(uid);
        public static Dictionary<long, EquippedItemInfo> GetEquippedItemsCopy() => S.GetEquippedItemsCopy();
        public static void SetEquippedItem(long uid, EquippedItemInfo info) => S.SetEquippedItem(uid, info);
        public static void RemoveEquippedItem(long uid) => S.RemoveEquippedItem(uid);
        public static void ClearEquippedItems() => S.ClearEquippedItems();
    }

    public class PlayerItem
    {
        public long Uid { get; set; }
        public int ItemId { get; set; }
        public int Quantity { get; set; }
        public int Position { get; set; }
        public Dictionary<int, int> Effects { get; set; } = new Dictionary<int, int>();

        /// <summary>
        /// Los efectos tal cual estaban guardados, [[efecto, valor, dado, cara], ...].
        ///
        /// Se conserva para devolverlos a la base sin tocar. El diccionario de arriba sólo se
        /// queda con el efecto y su valor, y volver a escribir eso perdería los dados —el daño de
        /// las armas, sin ir más lejos— en cuanto el objeto se guardase por cualquier motivo.
        /// </summary>
        public string RawEffects { get; set; } = "";
    }

    public class EquippedItemInfo
    {
        public int Slot { get; set; }
        public Dictionary<int, int> Stats { get; } = new Dictionary<int, int>();
    }
}
