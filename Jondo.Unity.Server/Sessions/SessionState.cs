using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server
{
    /// <summary>Mutable game data owned by exactly one network session.</summary>
    public sealed class SessionState
    {
        // Player Identity
        public long CharacterId { get; set; }
        public string CharacterName { get; set; } = "";
        public int CharacterLevel { get; set; } = 1;
        public int Breed { get; set; }
        public int Sex { get; set; }
        public string Language { get; set; } = "es";
        public byte[]? PlayerActorDetails { get; set; }
        public byte[]? LookBytes { get; set; }

        // Positioning
        public long MapId { get; set; }
        public int CellId { get; set; }
        public int Orientation { get; set; } = 1;
        public long Kamas { get; set; }

        /// <summary>The character's ACCUMULATED experience, not the current level's.</summary>
        public long Experience { get; set; }

        // Combat State
        public bool IsInFight { get; set; }

        /// <summary>
        /// De dónde salió este jugador al entrar en combate, para devolverlo ahí al acabar.
        ///
        /// Eran dos estáticos del manejador de combate, uno para todo el servidor: el segundo que
        /// entrara a pelear pisaba el sitio del primero, y al terminar los dos aparecían donde
        /// estaba el último.
        /// </summary>
        public long RoleplayMapId { get; set; }
        public int RoleplayCellId { get; set; }

        /// <summary>Which fight this character is in. Zero when they are not in one.</summary>
        /// <remarks>
        /// Needed because <see cref="MapId"/> stops telling fights apart the moment a fight starts.
        /// Two fights on the same roleplay map resolve to the SAME arena through
        /// <c>MapManager.ResolveArenaMapId</c> -- one arena per roleplay map, by design, because the
        /// arena is a real map from the game files and its cell layout is what the fight is drawn
        /// on. So both groups sit on one map id, and anything that reaches "everybody on this map"
        /// reaches the other fight as well. Map chat did exactly that: you could read what the
        /// strangers fighting beside you were saying, and they could read you.
        ///
        /// The fight packets themselves were never affected -- those go to the fight's own list of
        /// participants and never through the map. It is only what broadcasts by map that has to
        /// ask this question.
        /// </remarks>
        public long FightId { get; set; }
        public long CurrentFightMobId { get; set; }

        // Characteristics / Capital
        public int CharacterRemainingPoints { get; set; }
        public int StatVitality { get; set; }
        public int StatWisdom { get; set; }
        public int StatStrength { get; set; }

        /// <summary>Desde donde se ha conectado esta sesion. Se guarda para la proxima vez.</summary>
        public string ClientIp { get; set; } = "";

        /// <summary>Cuando y desde donde se conecto la vez anterior, leido antes de pisarlo.</summary>
        public DatabaseManager.LastVisit? PreviousVisit { get; set; }

        /// <summary>
        /// La experiencia de cada oficio de este personaje, cargada al entrar y guardada en la
        /// base cada vez que sube. Vive aquí y no en un estático porque dos jugadores a la vez
        /// tienen oficios distintos.
        /// </summary>
        public Dictionary<int, Managers.JobExperience.Progress> Jobs { get; } = new();

        /// <summary>
        /// Las misiones de este personaje: cuáles lleva, por qué paso va y qué ha cumplido.
        /// </summary>
        /// <remarks>
        /// Aquí y no en un estático, por lo mismo que los oficios y con más motivo: una misión se
        /// consulta en cada frase de cada diálogo, así que un diccionario compartido haría que
        /// hablar con un NPC le moviese la misión al de al lado.
        ///
        /// Es <c>null</c> hasta que se entra al mundo. Lo pone <c>Managers.Quests.LoadFrom</c>,
        /// porque necesita el catálogo, que es de otro proyecto y pesa 3 MB: construirlo aquí
        /// obligaría a cargarlo también en las sesiones que nunca llegan a jugar.
        /// </remarks>
        public World.Quests.QuestLog? Quests { get; set; }

        /// <summary>Los logros de este personaje. Null hasta entrar al mundo, como las misiones.</summary>
        public World.Achievements.AchievementLog? Achievements { get; set; }

        /// <summary>En qué nivel va un oficio. Cero experiencia es nivel 1, no nivel cero.</summary>
        public int JobLevel(int jobId)
            => Jobs.TryGetValue(jobId, out var progress) ? progress.Level : 1;

        /// <summary>Suma experiencia a un oficio y dice si ha subido.</summary>
        public bool AddJobExperience(int jobId, long amount, out long total, out int level)
        {
            bool sube = Managers.JobExperience.Add(Jobs, jobId, amount, out var progress);
            total = progress.Experience;
            level = progress.Level;
            return sube;
        }
        public int StatIntelligence { get; set; }
        public int StatChance { get; set; }
        public int StatAgility { get; set; }

        // Session-local UI/dialog state. These used to be static fields in handlers.
        public long OpenZaapMapId { get; set; }

        /// <summary>
        /// Por dónde se entró en la casa en la que se está, para salir por ahí mismo.
        ///
        /// Varias puertas del mundo pueden llevar al mismo interior, así que sin esto se sale por
        /// la primera que lleve allí y el jugador aparece en otro barrio. Si no hay nada —porque
        /// se desconectó dentro— se tira de la puerta que dicen los datos, que al menos existe.
        /// </summary>
        public long HouseEntryMapId { get; set; }

        /// <summary>La casilla de la calle desde la que se entró.</summary>
        public int HouseEntryCell { get; set; }

        /// <summary>
        /// Desde qué mapa se entró al merkasako, para volver ahí con la misma tecla.
        /// </summary>
        /// <remarks>
        /// Hace falta porque el cliente manda EL MISMO mensaje para entrar y para salir: en
        /// «Movimiento/ir al merkasako y volver.pcapng» las dos peticiones del jugador, la #1 y la
        /// #8, son un jbn con el cuerpo 10a28280c8e708 byte a byte, y el servidor contesta a la
        /// primera con el mapa de la bolsa y a la segunda con un mapa del mundo. O sea que quién
        /// decide la dirección es el servidor, mirando dónde está el jugador, y para eso hay que
        /// saber de dónde vino.
        ///
        /// Aparte de RoleplayMapId, que es del combate: se puede entrar al merkasako y pelear
        /// dentro, y compartir el campo dejaría al que sale de la pelea en la calle.
        /// </remarks>
        public long HavenBagEntryMapId { get; set; }

        /// <summary>La casilla del mundo desde la que se entró al merkasako.</summary>
        public int HavenBagEntryCell { get; set; }
        public bool IsChestOpen { get; set; }
        public bool IsHavenBagEditing { get; set; }
        public List<Managers.HavenBagStore.Furniture> PendingHavenBagFurniture { get; }
            = new List<Managers.HavenBagStore.Furniture>();
        public int WardrobeDraftTitle { get; set; }
        public int WardrobeDraftOrnament { get; set; }
        public bool IsWardrobeDraftLoaded { get; set; }
        public long OpenNpcShopId { get; set; }
        public int OpenNpcShopNpcId { get; set; }

        /// <summary>
        /// Qué conversación hay abierta y por dónde va.
        ///
        /// Hace falta guardarlo porque el cliente, al elegir una respuesta, manda el ioy con el id
        /// de la respuesta Y NADA MÁS: ni de qué NPC ni de qué frase venía. Sin esto no hay manera
        /// de saber a qué línea lleva, y por eso el diálogo sólo podía tener una frase.
        ///
        /// Va en el estado de sesión y no en un estático como todo lo demás: con ocho clientes a la
        /// vez, uno estático haría que la respuesta de un jugador avanzara la conversación de otro.
        /// </summary>
        public int OpenDialogueNpcId { get; set; }

        /// <summary>El mapa donde se abrió, que es parte de qué conversación es.</summary>
        public long OpenDialogueMapId { get; set; }

        /// <summary>En qué frase está ahora mismo.</summary>
        public long OpenDialogueMessage { get; set; }

        // Per-character manager caches. These must never be static: loading the second account
        // would otherwise replace the first account's equipment, appearance and spell bar.
        internal Dictionary<long, Managers.Equipment.Item> EquipmentItems { get; }
            = new Dictionary<long, Managers.Equipment.Item>();
        internal Dictionary<int, int> ChosenSpells { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> SpellBar { get; } = new Dictionary<int, int>();
        internal long SpellChoicesCharacterId { get; set; }

        // Thread-Safety Synchronization Lock
        private readonly object _lock = new object();

        // Inventory / Items (Private Backing Fields)
        private readonly List<PlayerItem> _inventory = new List<PlayerItem>();

        // Equipped Items Cache (Private Backing Fields)
        private readonly Dictionary<long, EquippedItemInfo> _equippedItems = new Dictionary<long, EquippedItemInfo>();

        public List<PlayerItem> GetInventoryCopy()
        {
            lock (_lock)
            {
                return new List<PlayerItem>(_inventory);
            }
        }

        public void SetInventory(List<PlayerItem> items)
        {
            lock (_lock)
            {
                _inventory.Clear();
                _inventory.AddRange(items);
            }
        }

        public void AddInventoryItem(PlayerItem item)
        {
            lock (_lock)
            {
                _inventory.Add(item);
            }
        }

        public void ClearInventory()
        {
            lock (_lock)
            {
                _inventory.Clear();
            }
        }

        public PlayerItem? GetInventoryItem(long uid)
        {
            lock (_lock)
            {
                return _inventory.Find(i => i.Uid == uid);
            }
        }

        public Dictionary<long, EquippedItemInfo> GetEquippedItemsCopy()
        {
            lock (_lock)
            {
                var dict = new Dictionary<long, EquippedItemInfo>();
                foreach (var kvp in _equippedItems)
                {
                    var info = new EquippedItemInfo { Slot = kvp.Value.Slot };
                    foreach (var stat in kvp.Value.Stats)
                    {
                        info.Stats[stat.Key] = stat.Value;
                    }
                    dict[kvp.Key] = info;
                }
                return dict;
            }
        }

        public void SetEquippedItem(long uid, EquippedItemInfo info)
        {
            lock (_lock)
            {
                _equippedItems[uid] = info;
            }
        }

        public void RemoveEquippedItem(long uid)
        {
            lock (_lock)
            {
                _equippedItems.Remove(uid);
            }
        }

        public void ClearEquippedItems()
        {
            lock (_lock)
            {
                _equippedItems.Clear();
            }
        }
    }

    // PlayerItem y EquippedItemInfo viven en GameState.cs: la copia de aqui perdia RawEffects.
}
