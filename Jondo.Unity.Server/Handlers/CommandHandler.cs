using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Los comandos de administración que el jugador escribe por el chat.
    ///
    /// Entran por donde entra cualquier línea de chat —el ktm, con su canal dentro— así que valen
    /// en general, en gremio, en comercio o donde sea: el canal solo decide en qué pestaña sale la
    /// respuesta, no si el comando se atiende. Y no se publican: quien los reconoce
    /// (<see cref="TryHandleAsync"/>) devuelve cierto y el eco no llega a salir, que es lo que
    /// impide que un ".kamas 10000" acabe escrito en el chat del gremio.
    ///
    /// La respuesta va en un kti, que es la línea de chat de la captura —la misma con la que el
    /// servidor devuelve lo que uno dice— y no en el csm que se usaba antes: csm no aparece en
    /// ninguna de las capturas ni en la tabla de mensajes del cliente, así que no hay forma de
    /// saber que el jugador lo esté viendo. Con kti se ve seguro.
    ///
    /// Lo que cada comando manda después de tocar el personaje sale de mensajes que ya existen en
    /// el emulador y están medidos contra capturas:
    ///
    ///   kub, iun   la hoja y los pods, igual que al repartir características (CharacteristicsHandler)
    ///   ivf        las kamas, igual que al pagar un viaje en zaap (ZaapTravelHandler)
    ///   jsd/jru/lqu/hjk   el cambio de mapa, igual que el zaap y el borde (TeleportHandler)
    ///   hms, itg   los hechizos y su barra, igual que al entrar al mundo (WorldEntry)
    ///   jsn, lxc   el aspecto, igual que al equiparse algo (EquipmentHandler)
    ///
    /// Los bvr, bcy y krd/kri/krb que ya había se dejan como estaban por no romper lo que el
    /// jugador ya tiene funcionando, pero no vienen de ninguna captura: van en el sobre de
    /// RESPUESTA (campo 3 de la raíz) y sin id de petición, que no es como el servidor real empuja
    /// nada. Lo nuevo va todo por Push, que es el campo 1, el de los mensajes que el servidor manda
    /// por su cuenta.
    /// </summary>
    public static class CommandHandler
    {
        /// <summary>
        /// Los comandos que existen y la clave de su texto de uso. Manda en dos sitios: decide qué
        /// líneas se traga el servidor en vez de publicarlas, y lleva al catálogo que contesta al
        /// jugador en el idioma de su sesión cuando se equivoca escribiéndolas.
        /// </summary>
        private static readonly Dictionary<string, string> Uso =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".kamas"] = "usage.kamas",
                [".level"] = "usage.level",
                [".teleport"] = "usage.teleport",
                [".relative"] = "usage.relative",
                [".shop"] = "usage.shop",
                [".size"] = "usage.size",
                [".item"] = "usage.item",
                [".itemset"] = "usage.itemset",
                [".packets"] = "usage.packets",
            };

        /// <summary>
        /// Qué rol hace falta para cada comando.
        ///
        /// El reparto: moverse por el mundo es de moderador, porque es lo que hace falta para ir a
        /// atender a alguien; tocar el personaje —kamas, nivel, tamaño— o abrirse una tienda es de
        /// game master. Un comando que no esté en esta tabla se trata como de administrador, que es
        /// el lado seguro por el que equivocarse: añadir uno nuevo y olvidarse de ponerle permiso
        /// lo deja cerrado, no abierto.
        /// </summary>
        private static readonly Dictionary<string, int> HaceFalta =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [".teleport"] = Roles.Moderador,
                [".relative"] = Roles.Administrador,
                [".kamas"] = Roles.GameMaster,
                [".level"] = Roles.GameMaster,
                [".size"] = Roles.GameMaster,
                [".shop"] = Roles.GameMaster,
            };

        /// <summary>El nivel al que se acaba el juego normal; de ahí para arriba es Omega.</summary>
        private const int MaxNormalLevel = 200;

        /// <summary>
        /// Atiende la línea. Devuelve cierto cuando era un comando y por tanto NO hay que
        /// publicarla por el chat.
        ///
        /// Se traga solo los comandos que EXISTEN: un mensaje que empiece por punto y no sea
        /// ninguno de ellos es una línea de chat como otra cualquiera y sigue su camino.
        ///
        /// Cierto también cuando el comando existe pero viene mal escrito: en ese caso lo que se
        /// hace es contestar cómo se escribe. Publicar un comando a medio escribir sería enseñarle
        /// a todo el mundo lo que el jugador quería hacer, que es justo lo que no puede pasar.
        /// </summary>
        public static async Task<bool> TryHandleAsync(NetworkStream stream, string text,
                                                      int channel = 0, long accountId = 0)
        {
            string? command = CommandOf(text);
            if (command == null) return false;

            if (!Uso.ContainsKey(command))
            {
                // No es nuestro. Se avisa —solo si tiene pinta de comando, para no contestar a
                // quien escribe "...bueno"— pero la línea sigue su camino normal.
                if (LooksLikeCommand(command))
                {
                    await NotifyAsync(stream, T("command.unknown", command,
                                              string.Join(", ", Uso.Keys)), channel, accountId);
                }
                return false;
            }

            // ¿Puede esta persona escribir este comando?
            //
            // Hasta ahora no lo comprobaba NADIE: cualquier jugador podía escribir ".kamas 10000"
            // o ".level 200" y el servidor se lo daba. Se mira aquí, en el servidor, y contra la
            // base, cada vez que se escribe el comando; no se guarda en la sesión, así que quitarle
            // el rol a alguien tiene efecto en el acto.
            //
            // La cuenta sale de la sesión de este socket, no de nada que mande el cliente.
            long quien = accountId > 0 ? accountId : Network.SessionContext.Current.AccountId;
            int rol = DatabaseManager.GetAccountRole(quien);
            int haceFalta = HaceFalta.TryGetValue(command, out int pide) ? pide : Roles.Administrador;

            if (!Roles.AlMenos(rol, haceFalta))
            {
                Console.WriteLine($"[Comandos] La cuenta {quien} ({Roles.Nombre(rol)}) ha intentado " +
                                  $"{command}, que es de {Roles.Nombre(haceFalta)}. Rechazado.");
                await NotifyAsync(stream, T("command.denied", command), channel, accountId);
                return true;   // se lo traga: ni se ejecuta ni se publica en el chat
            }

            string rest = RestOf(text);
            Console.WriteLine($"[Comandos] {command} {rest}".TrimEnd() +
                              $"  (cuenta {quien}, {Roles.Nombre(rol)})");

            try
            {
                switch (command)
                {
                    case ".kamas": await KamasAsync(stream, rest, channel, accountId); break;
                    case ".level": await LevelAsync(stream, rest, channel, accountId); break;
                    case ".teleport": await TeleportAsync(stream, rest, channel, accountId); break;
                    case ".relative": await RelativeAsync(stream, rest, channel, accountId); break;
                    case ".shop": await ShopAsync(stream, channel, accountId); break;
                    case ".size": await SizeAsync(stream, rest, channel, accountId); break;
                    case ".item": await ItemAsync(stream, rest, channel, accountId); break;
                    case ".itemset": await ItemSetAsync(stream, rest, channel, accountId); break;
                    case ".packets": await PacketsAsync(stream, rest, channel, accountId); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Comandos] {command} ha fallado: {ex}");
                await NotifyAsync(stream, T("command.failed", command, ex.Message),
                                  channel, accountId);
            }

            return true;
        }

        // ─── .kamas ─────────────────────────────────────────────────────────────

        private static async Task KamasAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!long.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                               out long amount))
            {
                await NotifyAsync(stream, Usage(".kamas"), channel, accountId);
                return;
            }

            long before = GameState.Kamas;
            GameState.Kamas = Math.Max(0, before + amount);
            DatabaseManager.SaveCurrentCharacter();

            // El de siempre, que no viene de ninguna captura pero lleva aquí desde el principio.
            await NetworkMessage.WriteFrameAsync(stream, NetworkEnvelope.BuildGameNodePacket(
                "type.ankama.com/bvr", Pb.New().Var(1, GameState.Kamas).Build()));

            // Y el que sí está medido: es el que manda el servidor real al cobrar un viaje en
            // zaap. Lleva la cifra ENTERA, no la diferencia, así que mandar los dos no descuadra
            // nada — el segundo dice lo mismo que el primero.
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(GameState.Kamas)));

            long difference = GameState.Kamas - before;
            await NotifyAsync(stream, T("kamas.result", GameState.Kamas,
                                         difference >= 0 ? "+" : "", difference),
                              channel, accountId);

            Console.WriteLine($"[Comandos] Kamas {before} -> {GameState.Kamas}.");
        }

        // ─── .level ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Poner el nivel, y con él todo lo que cuelga del nivel: la experiencia, los puntos de
        /// característica y los hechizos.
        ///
        /// La experiencia se pone en el SUELO del nivel (ExperienceTable.LevelFloor). Sin eso el
        /// personaje quedaba a nivel 150 con la experiencia de un nivel 40, y el cliente pinta la
        /// barra con lo que le manda el kub: barra desbordada o vacía según se subiera o se bajara.
        ///
        /// Los hechizos se recalculan enteros y se mandan otra vez: SpellTable ya sabe qué pareja
        /// abre cada nivel y a qué grado, leyendo MinPlayerLevel de SpellLevels, que es lo mismo
        /// que se le manda al entrar al mundo. Se mandan la lista (hms) y la barra (itg) porque la
        /// lista sola deja huecos apuntando a hechizos que ya no se tienen al BAJAR de nivel.
        ///
        /// Por encima de 200 —los niveles Omega— no se tocan los puntos de característica: el
        /// capital se queda en el del 200, que es lo que da el juego.
        /// </summary>
        private static async Task LevelAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int wanted))
            {
                await NotifyAsync(stream, Usage(".level"), channel, accountId);
                return;
            }

            // El techo lo pone la tabla de experiencia del cliente, que llega al 1889. Sin ella
            // cargada no hay suelo de experiencia que poner y no se pasa del 200.
            int ceiling = ExperienceTable.IsLoaded ? ExperienceTable.MaxLevel : MaxNormalLevel;
            int newLevel = Math.Clamp(wanted, 1, ceiling);

            int oldLevel = GameState.CharacterLevel;
            var before = Spells(oldLevel);

            GameState.CharacterLevel = newLevel;

            // La ventana de subida, con el nivel de destino. Va ANTES de las características
            // nuevas porque es de ahí de donde el cliente saca lo que enseña dentro, y ése es el
            // orden de la captura. Se manda también al BAJAR de nivel: el mensaje sólo lleva el
            // nivel al que se va, así que sirve igual, y ver la ventana es la forma de saber que
            // el comando ha hecho algo.
            if (newLevel != oldLevel)
            {
                await NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));
            }

            // La experiencia solo se toca si hay tabla con la que ponerla donde toca: sin ella
            // LevelFloor devuelve cero para todo, y eso no es "el suelo del nivel", es borrarle al
            // personaje la experiencia que tenía.
            if (ExperienceTable.IsLoaded)
            {
                GameState.Experience = ExperienceTable.LevelFloor(newLevel);
            }

            // El capital es de cinco por nivel desde el segundo, que es como lo cuentan
            // StatsHandler y CharacteristicsHandler. Por encima de 200 se congela: los niveles
            // Omega no reparten puntos.
            int capital = StatsHandler.TotalCapitalForLevel(Math.Min(newLevel, MaxNormalLevel));
            GameState.CharacterRemainingPoints = Math.Max(0, capital - SpentCapital());

            DatabaseManager.SaveCurrentCharacter();

            // Lo que ya mandaba este comando, tal cual estaba. No se quita —lleva aquí desde el
            // principio y no hay forma de comprobar desde fuera si el cliente lo mira— pero tampoco
            // se cuenta con ello: kri, krb y krd salen por el campo 3 de la raíz, que es el sobre
            // de las RESPUESTAS, y sin id de petición dentro. Lo que de verdad refresca la ficha es
            // el kub de más abajo.
            byte[]? kri = StatsHandler.BuildUpdatedKriPacket();
            if (kri != null) await NetworkMessage.WriteFrameAsync(stream, kri);

            await NetworkMessage.WriteFrameAsync(stream,
                StatsHandler.BuildKrbPacket(GameState.CharacterRemainingPoints));
            await NetworkMessage.WriteFrameAsync(stream,
                NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krd", Array.Empty<byte>()));

            // El bcy solo tiene sentido subiendo: es el mensaje de "has subido de nivel". Bajando
            // no se manda, que sus dos campos de puntos irían en negativo.
            if (newLevel > oldLevel)
            {
                await NetworkMessage.WriteFrameAsync(stream, NetworkEnvelope.BuildGameNodePacket(
                    "type.ankama.com/bcy", Pb.New()
                        .Var(1, newLevel)
                        .Var(2, oldLevel)
                        .Var(3, 5L * (newLevel - oldLevel))
                        .Var(4, 5L * (newLevel - oldLevel))
                        .Build()));
            }

            // Y la hoja de verdad: el kub lleva el nivel, la vida que da el nivel, la experiencia
            // con su suelo y su techo, y los puntos que quedan. Es el mismo par de mensajes que
            // manda CharacteristicsHandler al repartir puntos.
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun,
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * GameState.StatStrength)));
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            string spellNote = await RefreshSpellsAsync(stream, before);

            string capped = newLevel != wanted ? T("level.requested", wanted) : "";
            string omega = newLevel > MaxNormalLevel
                ? T("level.omega")
                : "";

            await NotifyAsync(stream, T("level.result", newLevel, capped, oldLevel,
                                         GameState.Experience, GameState.CharacterRemainingPoints,
                                         capital, spellNote, omega), channel, accountId);

            Console.WriteLine($"[Comandos] Nivel {oldLevel} -> {newLevel}, experiencia " +
                              $"{GameState.Experience}, puntos {GameState.CharacterRemainingPoints}.");
        }

        /// <summary>
        /// Lo que le ha costado al personaje la ficha que tiene puesta.
        ///
        /// Con los precios del cliente (<see cref="BreedStatCost"/>), que es lo que usa el reparto
        /// de puntos de verdad: el panel calcula el coste por su cuenta antes de mandar el kum, y
        /// un servidor que cuente distinto le devuelve al jugador un número de puntos que la
        /// ventana acaba de prometerle que no era. Sumar los puntos a pelo —que es lo que hacía
        /// este comando— dejaba libres de más a cualquiera que hubiera pasado de cien en algo,
        /// porque a partir de ahí cada punto cuesta dos.
        ///
        /// Sin esa tabla cargada se cae al modelo por tramos de StatsHandler, que da lo mismo para
        /// las razas cuyo precio conocemos.
        /// </summary>
        private static int SpentCapital()
        {
            if (!BreedStatCost.IsLoaded)
            {
                return StatsHandler.ComputeDistributionCost(
                    GameState.StatStrength, GameState.StatIntelligence, GameState.StatChance,
                    GameState.StatAgility, GameState.StatVitality, GameState.StatWisdom);
            }

            var sheet = new (string Name, int Points)[]
            {
                ("strength", GameState.StatStrength),
                ("intelligence", GameState.StatIntelligence),
                ("chance", GameState.StatChance),
                ("agility", GameState.StatAgility),
                ("vitality", GameState.StatVitality),
                ("wisdom", GameState.StatWisdom),
            };

            int spent = 0;
            foreach (var (name, points) in sheet)
            {
                for (int i = 0; i < points; i++)
                {
                    spent += Math.Max(1, BreedStatCost.PriceOf(GameState.Breed, name, i));
                }
            }
            return spent;
        }

        /// <summary>Los hechizos que tiene el personaje a un nivel dado, por id y grado.</summary>
        private static Dictionary<int, int> Spells(int level)
        {
            var spells = new Dictionary<int, int>();
            if (!SpellTable.IsLoaded) return spells;

            foreach (var spell in SpellTable.KnownFor(GameState.Breed, level, SpellChoices.Chosen))
            {
                spells[spell.SpellId] = spell.Grade;
            }
            return spells;
        }

        /// <summary>
        /// Le manda al cliente los hechizos del nivel nuevo y devuelve qué ha cambiado, para
        /// contárselo por el chat.
        ///
        /// Si la tabla de hechizos no está cargada no se manda nada: un hms vacío no dice "no ha
        /// cambiado nada", dice "no tienes hechizos", y dejaría el panel en blanco por un problema
        /// de datos que no tiene nada que ver con el comando.
        /// </summary>
        private static async Task<string> RefreshSpellsAsync(NetworkStream stream,
                                                             Dictionary<int, int> before)
        {
            if (!SpellTable.IsLoaded)
            {
                return T("spells.table_missing");
            }

            var after = Spells(GameState.CharacterLevel);
            if (after.Count == 0)
            {
                return T("spells.breed_missing", GameState.Breed);
            }

            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Hms,
                    ConnectionProtocol.BuildSpellList(GameState.Breed, GameState.CharacterLevel)));
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Itg,
                    ConnectionProtocol.BuildSpellBar(GameState.Breed, GameState.CharacterLevel)));

            int opened = 0, closed = 0, moved = 0;
            foreach (var spell in after)
            {
                if (!before.TryGetValue(spell.Key, out int grade)) opened++;
                else if (grade != spell.Value) moved++;
            }
            foreach (var spell in before)
            {
                if (!after.ContainsKey(spell.Key)) closed++;
            }

            return T("spells.result", after.Count, opened, closed, moved);
        }

        // ─── .teleport ──────────────────────────────────────────────────────────

        private static async Task TeleportAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!ParseCoordinates(rest, out int x, out int y))
            {
                await NotifyAsync(stream, Usage(".teleport"), channel, accountId);
                return;
            }

            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("teleport.in_fight"), channel, accountId);
                return;
            }

            var match = MapLookup.AtCoordinates(x, y);
            if (match == null)
            {
                await NotifyAsync(stream, T("teleport.no_map", x, y), channel, accountId);
                return;
            }

            int cell = await TeleportHandler.ToMapAsync(stream, match.Map.MapId);
            if (cell < 0)
            {
                await NotifyAsync(stream, T("teleport.load_failed", match.Map.MapId, x, y),
                                  channel, accountId);
                return;
            }

            // Cuando había varios se dice por qué se ha elegido ese: son coordenadas compartidas
            // por casas, interiores y mundos aparte, y el jugador tiene que poder saber a cuál de
            // todos ha ido a parar.
            string chosen = match.Candidates > 1
                ? T("teleport.multiple", match.Candidates, match.SubAreaCells)
                : "";

            await NotifyAsync(stream, T("teleport.result", x, y, match.Map.MapId,
                                         SubAreaName(match.Map.SubAreaId), cell, chosen),
                              channel, accountId);
        }

        // ─── .relative ──────────────────────────────────────────────────────────

        /// <summary>
        /// Cycles through the MapIds which share the current coordinates. This is useful for
        /// entering houses, workshops and other layers whose world point is the same as outdoors.
        /// </summary>
        private static async Task RelativeAsync(NetworkStream stream, string rest,
                                                int channel, long accountId)
        {
            if (!string.IsNullOrWhiteSpace(rest))
            {
                await NotifyAsync(stream, Usage(".relative"), channel, accountId);
                return;
            }
            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("teleport.in_fight"), channel, accountId);
                return;
            }

            long previousMapId = GameState.MapId;
            var current = MapManager.GetMapInfo(previousMapId);
            if (current == null)
            {
                await NotifyAsync(stream, T("relative.current_missing", previousMapId),
                                  channel, accountId);
                return;
            }

            var relative = MapLookup.NextRelative(previousMapId);
            if (relative == null)
            {
                await NotifyAsync(stream, T("relative.none", current.PosX, current.PosY),
                                  channel, accountId);
                return;
            }

            int cell = await TeleportHandler.ToMapAsync(stream, relative.Map.MapId);
            if (cell < 0)
            {
                await NotifyAsync(stream, T("relative.load_failed", relative.Map.MapId),
                                  channel, accountId);
                return;
            }

            string loop = relative.Wrapped ? T("relative.wrapped") : "";
            await NotifyAsync(stream, T("relative.result", current.PosX, current.PosY,
                                         previousMapId, relative.Map.MapId, relative.Position,
                                         relative.Candidates, SubAreaName(relative.Map.SubAreaId),
                                         cell, loop), channel, accountId);
        }

        // ─── .shop ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Al mapa de los vendedores, que se busca en vez de escribirse: el que más filas tiene en
        /// NpcSpawns. Hoy son las 52 del Pueblo de Amakna (88212759, [-1,0]) contra una sola del
        /// segundo, así que no hay empate posible; y si un día se puebla otro mapa, el comando
        /// sigue llevando donde están los vendedores sin que haya que tocarlo.
        /// </summary>
        private static async Task ShopAsync(NetworkStream stream, int channel, long accountId)
        {
            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("teleport.in_fight"), channel, accountId);
                return;
            }

            var (mapId, npcs) = DatabaseManager.GetMapWithMostNpcSpawns();
            if (mapId <= 0)
            {
                await NotifyAsync(stream, T("shop.no_npcs"), channel, accountId);
                return;
            }

            int cell = await TeleportHandler.ToMapAsync(stream, mapId);
            if (cell < 0)
            {
                await NotifyAsync(stream, T("shop.map_missing", mapId), channel, accountId);
                return;
            }

            var info = MapManager.GetMapInfo(mapId);
            string where = info != null
                ? $"[{info.PosX},{info.PosY}], {SubAreaName(info.SubAreaId)}"
                : T("shop.unknown_place");

            await NotifyAsync(stream, T("shop.result", mapId, where, npcs, cell),
                              channel, accountId);
        }

        // ─── .size ──────────────────────────────────────────────────────────────

        /// <summary>
        /// El tamaño del muñeco. Se guarda en el personaje y lo aplica BreedLookTable al construir
        /// el aspecto; aquí solo hay que hacer que se vuelva a construir, que son los dos mensajes
        /// que ya manda EquipmentHandler al cambiarse de ropa: el jsn redibuja al del mapa y el lxc
        /// al de la ficha.
        /// </summary>
        private static async Task SizeAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int wanted))
            {
                await NotifyAsync(stream, Usage(".size"), channel, accountId);
                return;
            }

            int size = CharacterSize.Set(GameState.CharacterId, wanted);

            var character = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (character == null)
            {
                await NotifyAsync(stream, T("size.character_missing"),
                                  channel, accountId);
                return;
            }

            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, GameState.CellId, GameState.Orientation, accountId)));
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));

            string capped = size != wanted
                ? T("size.clamped", wanted, CharacterSize.Minimum, CharacterSize.Maximum)
                : "";
            await NotifyAsync(stream, T("size.result", size, capped, CharacterSize.Normal),
                              channel, accountId);

            Console.WriteLine($"[Comandos] Tamaño del personaje {GameState.CharacterId}: {size} %.");
        }

        // ─── .item / .itemset ──────────────────────────────────────────────────

        /// <summary>
        /// Creates an item from the client template, with its real factory effects, persists it,
        /// updates both in-memory inventory views and immediately pushes it to the client.
        /// </summary>
        private static async Task<bool> GiveItemAsync(NetworkStream stream, int gid, int quantity)
        {
            if (!DatabaseManager.TryGetItemTemplateEffects(gid, out string effects)) return false;

            long uid = DatabaseManager.NextItemUid();
            if (!DatabaseManager.InsertCharacterItem(uid, GameState.CharacterId, gid, quantity,
                                                     Equipment.Bag, effects))
                return false;

            Equipment.Add(uid, gid, quantity, Equipment.Bag, effects);

            var legacy = new PlayerItem
            {
                Uid = uid,
                ItemId = gid,
                Quantity = quantity,
                Position = Equipment.Bag,
                RawEffects = effects,
            };
            foreach (var effect in Equipment.ParseEffects(effects))
            {
                legacy.Effects.TryGetValue(effect.Effect, out int had);
                legacy.Effects[effect.Effect] = had + (int)effect.Value;
            }
            GameState.AddInventoryItem(legacy);

            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3,
                    new HavenBagStore.StoredItem
                    {
                        Uid = uid,
                        Gid = gid,
                        Quantity = quantity,
                        Effects = effects,
                    })));
            return true;
        }

        private static async Task ItemAsync(NetworkStream stream, string rest,
                                            int channel, long accountId)
        {
            string[] parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gid) ||
                (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.Integer,
                                                   CultureInfo.InvariantCulture, out _)))
            {
                await NotifyAsync(stream, Usage(".item"), channel, accountId);
                return;
            }

            int quantity = 1;
            if (parts.Length == 2)
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity);

            if (quantity <= 0)
            {
                await NotifyAsync(stream, T("item.quantity"), channel, accountId);
                return;
            }

            if (!await GiveItemAsync(stream, gid, quantity))
            {
                await NotifyAsync(stream, T("item.template_missing", gid), channel, accountId);
                return;
            }

            await RefreshPodsAsync(stream);
            await NotifyAsync(stream, T("item.added", gid, quantity),
                              channel, accountId);
        }

        private static async Task ItemSetAsync(NetworkStream stream, string rest,
                                               int channel, long accountId)
        {
            if (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                              out int setId))
            {
                await NotifyAsync(stream, Usage(".itemset"), channel, accountId);
                return;
            }

            if (!ItemSets.TryGetItems(setId, out var templates))
            {
                await NotifyAsync(stream, T("itemset.missing", setId), channel, accountId);
                return;
            }

            int added = 0;
            var missing = new List<int>();
            foreach (int gid in templates)
            {
                if (await GiveItemAsync(stream, gid, 1)) added++;
                else missing.Add(gid);
            }

            await RefreshPodsAsync(stream);
            string warning = missing.Count == 0
                ? ""
                : T("itemset.templates_missing", string.Join(", ", missing));
            await NotifyAsync(stream, T("itemset.added", setId, added, templates.Count, warning),
                              channel, accountId);
        }

        // ─── .packets ──────────────────────────────────────────────────────────

        /// <summary>
        /// Lo que el cliente nos manda y no sabemos atender, de lo que más pasa a lo que menos.
        ///
        /// Va agrupado por FORMA y no por opcode, que es lo que hace que la lista sirva: un mismo
        /// opcode puede llevar cargas distintas según lo que el jugador esté haciendo, y contarlas
        /// juntas esconde justo lo que hay que ver.
        ///
        /// Esto no descifra nada. Dice dónde mirar; lo que se mire se mide contra una captura como
        /// todo lo demás, y hasta entonces no se contesta nada, porque una respuesta inventada deja
        /// al cliente con un estado que el servidor no tiene.
        /// </summary>
        private static async Task PacketsAsync(NetworkStream stream, string rest,
                                               int channel, long accountId)
        {
            int cuantas = 10;
            if (rest.Trim().Length > 0 &&
                (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                               out cuantas) || cuantas <= 0 || cuantas > 40))
            {
                await NotifyAsync(stream, Usage(".packets"), channel, accountId);
                return;
            }

            var lista = Network.UnknownPackets.Top(cuantas);
            if (lista.Count == 0)
            {
                await NotifyAsync(stream, T("packets.none"),
                                  channel, accountId);
                return;
            }

            var counts = Network.UnknownPackets.Counts();
            await NotifyAsync(stream, T("packets.summary", Network.UnknownPackets.ShapeCount,
                                         Network.UnknownPackets.OpcodeCount, counts.Unhandled,
                                         counts.Silenced, counts.Undecodable), channel, accountId);
            foreach (var fila in lista)
            {
                string marca = fila.Kind switch
                {
                    Network.UnknownPackets.Kind.Silenced => T("packets.silenced"),
                    Network.UnknownPackets.Kind.Undecodable => T("packets.undecodable"),
                    _ => T("packets.unhandled"),
                };
                await NotifyAsync(stream,
                    $"{fila.Opcode} x{fila.Occurrences} ({marca}, f{fila.RootField}, " +
                    $"{fila.PayloadBytes} B) {fila.Signature}",
                    channel, accountId);
            }
        }

        private static async Task RefreshPodsAsync(NetworkStream stream)
        {
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(
                    0, 1000 + 5L * GameState.StatStrength)));
        }

        // ─── Piezas sueltas ─────────────────────────────────────────────────────

        private static string T(string key, params object[] values)
            => CommandTexts.Get(key, values);

        private static string Usage(string command) => T(Uso[command]);

        /// <summary>
        /// El aviso al jugador, por el canal donde escribió para que le salga en la pestaña que
        /// está mirando. Es un kti, la línea de chat de la captura.
        ///
        /// OJO: esto es la EXCEPCIÓN, no la norma. Un kti sale por el canal general y lo lee todo
        /// el mundo. Para decirle algo al jugador —«no tienes nivel», «has ganado kamas»— va un
        /// lqn con su número de mensaje; ver <see cref="Managers.InfoMessages"/>. Aquí se usa el
        /// chat porque la respuesta de un comando es texto libre que no está en la tabla del
        /// cliente, y porque el jugador acaba de escribir en esa misma pestaña y espera la
        /// respuesta ahí.
        /// </summary>
        private static async Task NotifyAsync(NetworkStream stream, string text, int channel, long accountId)
        {
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kti, ConnectionProtocol.BuildChatLine(
                    GameState.CharacterName, GameState.CharacterId, accountId,
                    "[INFO] " + text, channel)));
        }

        /// <summary>La primera palabra en minúsculas, o null si la línea no empieza por punto.</summary>
        private static string? CommandOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(".", StringComparison.Ordinal)) return null;

            int space = trimmed.IndexOf(' ');
            string word = space < 0 ? trimmed : trimmed.Substring(0, space);
            return word.ToLowerInvariant();
        }

        /// <summary>Lo que va detrás del comando, sin tocar.</summary>
        private static string RestOf(string text)
        {
            string trimmed = text.TrimStart();
            int space = trimmed.IndexOf(' ');
            return space < 0 ? "" : trimmed.Substring(space + 1).Trim();
        }

        /// <summary>
        /// Si una palabra que empieza por punto TIENE PINTA de comando: punto y letras, nada más.
        /// Sirve para no contestar "ese comando no existe" a quien escribe "...bueno" o ".", que
        /// son líneas de chat normales y corrientes.
        /// </summary>
        private static bool LooksLikeCommand(string word)
        {
            if (word.Length < 2) return false;
            for (int i = 1; i < word.Length; i++)
            {
                if (!char.IsLetter(word[i])) return false;
            }
            return true;
        }

        /// <summary>
        /// Las coordenadas, escritas como sea: [-1,0], -1 0, -1,0 o (-1;0). Los corchetes y los
        /// separadores se cambian por espacios y lo que queda tienen que ser dos números.
        /// </summary>
        private static bool ParseCoordinates(string rest, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrWhiteSpace(rest)) return false;

            var cleaned = new System.Text.StringBuilder(rest.Length);
            foreach (char c in rest)
            {
                cleaned.Append(c == '[' || c == ']' || c == '(' || c == ')' || c == ',' || c == ';'
                    ? ' ' : c);
            }

            var parts = cleaned.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;

            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>El nombre de la subzona, y si no se sabe, su número.</summary>
        private static string SubAreaName(int subAreaId)
        {
            string name = DatabaseManager.GetSubAreaName(subAreaId);
            return string.IsNullOrEmpty(name) ? T("map.subarea", subAreaId) : name;
        }
    }
}
