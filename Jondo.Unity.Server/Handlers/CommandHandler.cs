using Jondo.Unity.Launcher;
using System;
using System.Linq;
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
                [".receta"] = "usage.recipe",
                [".packets"] = "usage.packets",
                [".gremio"] = "usage.guild",
                [".raid"] = "usage.raid",
                [".oficio"] = "usage.job",
                [".oficios"] = "usage.jobs",
                [".forjadios"] = "usage.forgegod",
                [".forgegod"] = "usage.forgegod",
                [".forgedieu"] = "usage.forgegod",
                [".sueno"] = "usage.dream",
                [".sueño"] = "usage.dream",
                [".dream"] = "usage.dream",
                [".reve"] = "usage.dream",
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
                [".oficio"] = Roles.GameMaster,
                [".oficios"] = Roles.GameMaster,
                // Written out rather than left to the default, which is the same: forgegod is for
                // the highest role there is and nobody else, whatever the default becomes.
                [".forjadios"] = Roles.Administrador,
                [".forgegod"] = Roles.Administrador,
                [".forgedieu"] = Roles.Administrador,
                // The same for skipping a dream forward: it skips the game.
                [".sueno"] = Roles.Administrador,
                [".sueño"] = Roles.Administrador,
                [".dream"] = Roles.Administrador,
                [".reve"] = Roles.Administrador,
                [".size"] = Roles.GameMaster,
                [".shop"] = Roles.GameMaster,
                [".gremio"] = Roles.Jugador,
            };

        /// <summary>
        /// The role a command asks for: its row in the table, or Administrator for one without --
        /// the safe side to be wrong on.
        /// </summary>
        internal static int RequiredRole(string command)
            => HaceFalta.TryGetValue(command, out int role) ? role : Roles.Administrador;

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
            int haceFalta = RequiredRole(command);

            if (!Roles.AlMenos(rol, haceFalta))
            {
                Console.WriteLine($"[Comandos] La cuenta {quien} ({Roles.Nombre(rol)}) ha intentado " +
                                  $"{command}, que es de {Roles.Nombre(haceFalta)}. Rechazado.");
                ActivityJournal.Current.Write("command.denied", quien, GameState.CharacterId,
                    new { command, role = rol, requiredRole = haceFalta });
                await NotifyAsync(stream, T("command.denied", command), channel, accountId);
                return true;   // se lo traga: ni se ejecuta ni se publica en el chat
            }

            string rest = RestOf(text);
            Console.WriteLine($"[Comandos] {command} {rest}".TrimEnd() +
                              $"  (cuenta {quien}, {Roles.Nombre(rol)})");
            ActivityJournal.Current.Write("command.requested", quien, GameState.CharacterId,
                new { command, role = rol });

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
                    case ".receta": await RecipeAsync(stream, rest, channel, accountId); break;
                    case ".packets": await PacketsAsync(stream, rest, channel, accountId); break;
                    case ".gremio": await GremioAsync(stream, rest, channel, accountId); break;
                    case ".raid": await RaidAsync(stream, rest, channel, accountId); break;
                    case ".oficio": await JobAsync(stream, rest, channel, accountId); break;
                    case ".oficios": await AllJobsAsync(stream, rest, channel, accountId); break;
                    case ".forjadios":
                    case ".forgegod":
                    case ".forgedieu": await ForgeGodAsync(stream, rest, channel, accountId); break;
                    case ".sueno":
                    case ".sueño":
                    case ".dream":
                    case ".reve": await DreamAsync(stream, rest, channel, accountId); break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Comandos] {command} ha fallado: {ex}");
                ActivityJournal.Current.Write("command.failed", quien, GameState.CharacterId,
                    new { command, error = ex.GetType().Name, message = ex.Message });
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

            LevelChange result = await SetLevelAsync(stream, wanted);
            string capped = result.Level != wanted ? T("level.requested", wanted) : "";
            string omega = result.Level > MaxNormalLevel ? T("level.omega") : "";

            await NotifyAsync(stream, T("level.result", result.Level, capped, result.PreviousLevel,
                                         result.Experience, result.RemainingPoints,
                                         result.Capital, result.SpellNote, omega), channel, accountId);
        }

        // ─── .oficio ────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts a job at a level, to try the recipes of that level without gathering for hours.
        /// The experience goes to the floor of the level, and the client hears it the way a real
        /// level-up says it: isz, then irq.
        /// </summary>
        private static async Task JobAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && AllWords.Contains(parts[0].ToLowerInvariant()))
            {
                await AllJobsAsync(stream, parts[1], channel, accountId);
                return;
            }
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int job)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)
                || !JobManager.TryGet(job, out _))
            {
                await NotifyAsync(stream, Usage(".oficio"), channel, accountId);
                return;
            }

            int level = Math.Clamp(wanted, 1, JobExperience.MaxLevel);
            var state = Network.SessionContext.State;
            int before = state.JobLevel(job);
            long experience = JobExperience.Floor(level);
            state.Jobs[job] = new JobExperience.Progress { JobId = job, Experience = experience };
            DatabaseManager.SaveJobExperience(state.CharacterId, job, experience);

            if (level != before) await WorkshopHandler.SendLevelUpAsync(stream, job, level);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Irq, ConnectionProtocol.BuildJobExperience(
                    job, JobExperience.Next(level), level, JobExperience.Floor(level), experience)));

            await NotifyAsync(stream, T("job.result", job, level, before), channel, accountId);
        }

        /// <summary>"Every job", in the three languages the replies speak.</summary>
        private static readonly HashSet<string> AllWords = new HashSet<string> { "todos", "all", "tous" };

        /// <summary>The level .oficios puts every job at when it is given none.</summary>
        private const int AllJobsDefault = 200;

        /// <summary>
        /// Every job at one level: ".oficios" for 200, ".oficios 150", or ".oficio todos 150". The
        /// jobs are the directory's -- the twenty with a recipe, a resource or a magus table, the base one aside,
        /// which has no level. The client is told with one irq carrying all of them, the way the
        /// entry into the world does it, and no level-up window: twenty of them in a row would be
        /// twenty windows to close.
        /// </summary>
        private static async Task AllJobsAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            string word = rest.Trim();
            int wanted = AllJobsDefault;
            if (word.Length > 0 && !int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out wanted))
            {
                await NotifyAsync(stream, Usage(".oficios"), channel, accountId);
                return;
            }

            int level = Math.Clamp(wanted, 1, JobExperience.MaxLevel);
            long experience = JobExperience.Floor(level);
            var state = Network.SessionContext.State;
            var jobs = ArtisanHandler.Jobs().Where(j => j != WorkshopHandler.BaseJob).ToList();
            foreach (int job in jobs)
            {
                state.Jobs[job] = new JobExperience.Progress { JobId = job, Experience = experience };
                DatabaseManager.SaveJobExperience(state.CharacterId, job, experience);
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Irq,
                ConnectionProtocol.BuildJobsExperience(jobs.Select(job =>
                    (job, JobExperience.Next(level), level, JobExperience.Floor(level), experience)))));

            await NotifyAsync(stream, T("jobs.result", jobs.Count, level), channel, accountId);
            Console.WriteLine($"[Comandos] {jobs.Count} jobs of {state.CharacterName} at level {level}.");
        }

        // ─── .forjadios / .forgegod / .forgedieu ───────────────────────────────

        /// <summary>
        /// Forgegod mode, on or off: at the forge no rune fails, no weight cap holds on an over or
        /// an exo (two AP of exo, a thousand vitality), a transcendence goes on anything, an item
        /// "sin forjamagia futura" takes runes again, a magus table takes any item and a recipe
        /// asks no job level. For this session only.
        /// </summary>
        private static async Task ForgeGodAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            string word = rest.Trim().ToLowerInvariant();
            bool? on = word switch
            {
                "on" or "1" or "si" or "sí" or "yes" or "oui" => true,
                "off" or "0" or "no" or "non" => false,
                _ => null,
            };
            var state = Network.SessionContext.State;
            if (on == null)
            {
                await NotifyAsync(stream, Usage(".forjadios") + " " + T(state.ForgeGod ? "forgegod.on" : "forgegod.off"),
                                  channel, accountId);
                return;
            }

            state.ForgeGod = on.Value;
            await NotifyAsync(stream, T(on.Value ? "forgegod.on" : "forgegod.off"), channel, accountId);
            Console.WriteLine($"[Comandos] Forgegod {(on.Value ? "on" : "off")} for {state.CharacterName}.");
        }

        public sealed class LevelChange
        {
            public int PreviousLevel { get; init; }
            public int Level { get; init; }
            public long Experience { get; init; }
            public int RemainingPoints { get; init; }
            public int Capital { get; init; }
            public string SpellNote { get; init; } = "";
        }

        /// <summary>
        /// Applies the complete level transition and refreshes the client, without writing a chat
        /// response. The chat command and the live administration endpoint share this path.
        /// </summary>
        public static async Task<LevelChange> SetLevelAsync(NetworkStream stream, int wanted)
        {

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
            GameState.CharacterRemainingPoints = Math.Max(0, capital - SpentCapital(capital));

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
                    ConnectionProtocol.BuildPods(0, 1000 + 5L * GameState.TotalStrength)));
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            string spellNote = await RefreshSpellsAsync(stream, before);
            await FightHandler.RefreshPlayerSpellBarAsync(stream);

            Console.WriteLine($"[Comandos] Nivel {oldLevel} -> {newLevel}, experiencia " +
                              $"{GameState.Experience}, puntos {GameState.CharacterRemainingPoints}.");
            return new LevelChange
            {
                PreviousLevel = oldLevel,
                Level = newLevel,
                Experience = GameState.Experience,
                RemainingPoints = GameState.CharacterRemainingPoints,
                Capital = capital,
                SpellNote = spellNote,
            };
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
        private static int SpentCapital(int stopAfter = int.MaxValue)
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
                    // The caller only needs to know that no capital remains. This also prevents a
                    // live-admin character with millions of points from turning a level change
                    // into millions of table lookups.
                    if (spent > stopAfter) return spent;
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
                    ConnectionProtocol.BuildSpellList(GameState.Breed, GameState.CharacterLevel,
                        Network.SessionContext.Current.AccountId)));
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
            // Un solo número es un id de mapa, y va directo: es la forma de llegar a un interior
            // concreto cuando hay cuatro mapas en la misma coordenada, como en el Templo de los
            // Gremios. Sólo si el mapa existe; un número que no es un mapa es un error de uso.
            bool byId = long.TryParse((rest ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mapId)
                        && mapId > 0;
            var info = byId ? MapManager.GetMapInfo(mapId) : null;

            int x = 0, y = 0;
            if (!byId && !ParseCoordinates(rest, out x, out y))
            {
                await NotifyAsync(stream, Usage(".teleport"), channel, accountId);
                return;
            }

            if (byId && info == null)
            {
                await NotifyAsync(stream, T("teleport.no_such_map", mapId), channel, accountId);
                return;
            }

            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("teleport.in_fight"), channel, accountId);
                return;
            }

            if (byId)
            {
                int landed = await TeleportHandler.ToMapAsync(stream, mapId);
                if (landed < 0)
                {
                    await NotifyAsync(stream, T("teleport.load_failed", mapId, info!.PosX, info.PosY), channel, accountId);
                    return;
                }

                await NotifyAsync(stream, T("teleport.result", info!.PosX, info.PosY, mapId,
                                             SubAreaName(info.SubAreaId), landed, ""), channel, accountId);
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
        /// <summary>
        /// Puts an item in the bag and tells the client. Now lives in <see cref="Equipment"/>.
        /// </summary>
        /// <remarks>
        /// Kept as a one-liner rather than replaced everywhere because the quest engine needs the
        /// same thing and two copies of "give somebody an item" is how the two of them end up
        /// disagreeing about whether the client gets told.
        /// </remarks>
        private static Task<bool> GiveItemAsync(NetworkStream stream, int gid, int quantity)
            => Equipment.GiveAsync(stream, gid, quantity);

        /// <summary>The same, saying what it handed over. Lives in <see cref="Equipment"/> too.</summary>
        /// <remarks>
        /// La PR #22 traia aqui una copia entera de GiveAsync que solo se diferenciaba en devolver
        /// el objeto. Dos copias de «dar algo a alguien» es como acaban discrepando sobre si al
        /// cliente se le ha avisado, asi que lo que se hizo fue darle esa capacidad a la que ya
        /// existia.
        /// </remarks>
        public static Task<HavenBagStore.StoredItem?> GrantItemAsync(NetworkStream stream,
                                                                     int gid, int quantity)
            => Equipment.GrantAsync(stream, gid, quantity);

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

            if (await GrantItemAsync(stream, gid, quantity) == null)
            {
                await NotifyAsync(stream, T("item.template_missing", gid), channel, accountId);
                return;
            }

            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("item.granted",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                GameState.CharacterId,
                new { source = "command", gid, quantity });
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
                if (await GrantItemAsync(stream, gid, 1) != null) added++;
                else missing.Add(gid);
            }

            await RefreshPodsAsync(stream);
            string warning = missing.Count == 0
                ? ""
                : T("itemset.templates_missing", string.Join(", ", missing));
            ActivityJournal.Current.Write("itemset.granted",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                GameState.CharacterId,
                new { source = "command", setId, added, requested = templates.Count, missing });
            await NotifyAsync(stream, T("itemset.added", setId, added, templates.Count, warning),
                              channel, accountId);
        }

        // ─── .receta ───────────────────────────────────────────────────────────

        /// <summary>The most times .receta multiplies a recipe by: a guard on the command, not a rule of the game.</summary>
        internal const int MaxRecipeTimes = 100;

        /// <summary>
        /// ".receta &lt;item&gt; [times]": every ingredient of the item's recipe into the bag, as many
        /// as the recipe asks for, times as many times as given. Each joins the stack of the same
        /// thing the character already has, so the workshop finds it in one piece. Administrators
        /// only, like .item: it makes items out of nothing, and it is left out of the table on
        /// purpose, for the default to close it.
        /// </summary>
        private static async Task RecipeAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!TryParseRecipe(rest, out int gid, out int times))
            {
                await NotifyAsync(stream, Usage(".receta"), channel, accountId);
                return;
            }
            if (!RecipeManager.TryGetByResult(gid, out var recipe))
            {
                await NotifyAsync(stream, T("recipe.missing", gid), channel, accountId);
                return;
            }

            var ingredients = IngredientsOf(recipe, times);
            var given = new List<(int Item, int Quantity)>();
            var missing = new List<int>();
            foreach (var (item, quantity) in ingredients)
            {
                if (await WorkshopHandler.GiveAsync(stream, item, quantity)) given.Add((item, quantity));
                else missing.Add(item);
            }

            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("recipe.granted",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                GameState.CharacterId,
                new
                {
                    source = "command", result = gid, times,
                    ingredients = given.Select(g => new { gid = g.Item, quantity = g.Quantity }).ToList(),
                    missing,
                });

            string list = given.Count == 0 ? "-" : string.Join(", ", given.Select(g => $"{g.Quantity} x {g.Item}"));
            string warning = missing.Count == 0 ? "" : T("itemset.templates_missing", string.Join(", ", missing));
            await NotifyAsync(stream, T("recipe.added", gid, times, recipe.JobId, recipe.ResultLevel, list, warning),
                              channel, accountId);
            Console.WriteLine($"[Comandos] Recipe {gid} x{times} for {GameState.CharacterName}: {list}" +
                              (missing.Count == 0 ? "." : $", missing {string.Join(", ", missing)}."));
        }

        /// <summary>".receta 44" or ".receta 44 5": the item, and how many times its recipe, 1 to <see cref="MaxRecipeTimes"/>.</summary>
        internal static bool TryParseRecipe(string rest, out int gid, out int times)
        {
            gid = 0;
            times = 1;
            var parts = (rest ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1 || parts.Length > 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out gid) || gid <= 0) return false;
            if (parts.Length == 2 &&
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out times)) return false;
            return times >= 1 && times <= MaxRecipeTimes;
        }

        /// <summary>
        /// What a recipe asks for, times over: one line per item, in the recipe's order, an item
        /// that appeared twice added up into its first line.
        /// </summary>
        internal static IReadOnlyList<(int Item, int Quantity)> IngredientsOf(RecipeDefinition recipe, int times)
            => recipe.Ingredients
                .GroupBy(i => i.ItemId)
                .Select(g => (g.Key, g.Sum(i => i.Quantity) * times))
                .ToList();

        // ─── .sueno ────────────────────────────────────────────────────────────

        /// <summary>
        /// ".sueno [row]" (".dream", ".reve"): the dream one has going carried forward to a room of
        /// the row given, or to the Fin du rêve with none, the rooms on the way won as if fought.
        /// For testing what lies deep in a dream -- band V's fountain, the end and its waves --
        /// without twenty-five fights first. Administrators only: it skips the game.
        /// </summary>
        private static async Task DreamAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            if (!TryParseDreamRow(rest, out int? row))
            {
                await NotifyAsync(stream, Usage(".sueno"), channel, accountId);
                return;
            }
            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("dream.in_fight"), channel, accountId);
                return;
            }
            var dream = Dreams.De(GameState.CharacterId);
            if (dream == null)
            {
                await NotifyAsync(stream, T("dream.none"), channel, accountId);
                return;
            }

            var (outcome, room, skipped) = await DreamHandler.SkipToAsync(stream, dream, row);
            if (outcome == Dreams.SkipOutcome.Done && room != null)
            {
                ActivityJournal.Current.Write("dream.skipped",
                    accountId > 0 ? accountId : SessionContext.Current.AccountId,
                    GameState.CharacterId,
                    new { source = "command", room = room.Id, row = room.Fila, skipped, dreamPoints = dream.DreamPoints });
            }

            string reply = outcome switch
            {
                Dreams.SkipOutcome.Done when room != null => T("dream.skipped", room.Id, room.Fila, skipped, dream.DreamPoints),
                Dreams.SkipOutcome.Behind => T("dream.behind", dream.SalaActual?.Fila ?? 0),
                Dreams.SkipOutcome.Past => T("dream.past", dream.Salas.Count == 0 ? 0 : dream.Salas.Max(r => r.Fila)),
                _ => T("dream.none"),
            };
            await NotifyAsync(stream, reply, channel, accountId);
        }

        /// <summary>".sueno" alone for the end, or ".sueno 25": a row of rooms, 1 or deeper.</summary>
        internal static bool TryParseDreamRow(string rest, out int? row)
        {
            row = null;
            string text = (rest ?? "").Trim();
            if (text.Length == 0) return true;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 1) return false;
            row = value;
            return true;
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
                    0, 1000 + 5L * GameState.TotalStrength)));
        }

        // ─── Piezas sueltas ─────────────────────────────────────────────────────

        private static string T(string key, params object[] values)
            => CommandTexts.Get(key, values);

        private static string Usage(string command) => T(Uso[command]);

        /// <summary>
        /// Las raids de gremio: comprarla, lanzarla, entrar, salir, cerrarla, y ver cómo va.
        ///
        /// Comando y no botones por lo mismo que la invitación: la pestaña de raids de la tienda
        /// del gremio sale en las capturas VACÍA -el gremio grabado no tenía ninguna-, así que no
        /// se sabe con qué mensaje se compra ni con cuál se lanza. Lo que hay debajo sí es de
        /// verdad: la instancia, el reloj y las variables que el contenido del cliente lee.
        /// </summary>
        private static async Task RaidAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
            string[] partes = (rest ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string que = partes.Length > 0 ? partes[0].ToLowerInvariant() : "";

            if (que.Length == 0)
            {
                await NotifyAsync(stream, RaidStatus(who), channel, accountId);
                return;
            }

            if (que == "entrar" || que == "salir" || que == "fin")
            {
                string fallo = que == "entrar" ? await Managers.GuildRaidManager.EnterAsync(who)
                             : que == "salir" ? await Managers.GuildRaidManager.LeaveAsync(who)
                             : await Managers.GuildRaidManager.CloseAsync(who);
                await NotifyAsync(stream, fallo == null ? RaidStatus(who) : T(fallo), channel, accountId);
                return;
            }

            if ((que == "comprar" || que == "lanzar") && partes.Length > 1
                && int.TryParse(partes[1].Trim(), out int cual))
            {
                string fallo = que == "comprar"
                    ? Managers.GuildRaidManager.Buy(who, cual)
                    : await Managers.GuildRaidManager.LaunchAsync(who, cual);
                await NotifyAsync(stream, fallo == null ? RaidStatus(who) : T(fallo), channel, accountId);
                return;
            }

            if (que == "clasificacion" || que == "clasificación")
            {
                await LadderAsync(stream, partes, channel, accountId);
                return;
            }

            await NotifyAsync(stream, Usage(".raid"), channel, accountId);
        }

        /// <summary>
        /// La clasificación semanal de una raid.
        /// </summary>
        /// <remarks>
        /// Por el chat, como todo lo de las raids, y por lo mismo: la ventana de clasificaciones
        /// existe en el cliente -«Acceder a las clasificaciones», «Ver la clasificación»- pero
        /// ninguna captura la abre, así que no se sabe con qué mensaje se llena.
        ///
        /// El ornamento del podio se NOMBRA y no se entrega. Hoy el guardarropa ofrece los 167 a
        /// todo el mundo, así que «darlo» no sería dar nada; el día que haya ornamentos por ganar,
        /// aquí está a quién le tocan.
        /// </remarks>
        private static async Task LadderAsync(NetworkStream stream, string[] partes, int channel, long accountId)
        {
            int cual = Jondo.Unity.World.Content.Raids.Gigalodon;
            if (partes.Length > 1 && int.TryParse(partes[1].Trim(), out int pedida)) cual = pedida;

            var kind = Jondo.Unity.World.Content.Raids.Of(cual);
            if (kind == null)
            {
                await NotifyAsync(stream, T("raid.unknown"), channel, accountId);
                return;
            }

            var ahora = DateTimeOffset.UtcNow;
            var tabla = Managers.GuildStore.Ladder(cual, ahora);
            if (tabla.Count == 0)
            {
                await NotifyAsync(stream, T("raid.ladder.empty", kind.Name), channel, accountId);
                return;
            }

            await NotifyAsync(stream, T("raid.ladder.head", kind.Name,
                                        Managers.GuildStore.WeekOf(ahora)), channel, accountId);

            foreach (var fila in tabla)
            {
                string premio = fila.Place <= kind.Podium.Count
                    ? T("raid.ladder.podium", kind.Podium[fila.Place - 1].ToString())
                    : "";
                await NotifyAsync(stream, T("raid.ladder.row", fila.Place.ToString(), fila.Name,
                                            fila.Score.ToString(), fila.Runs.ToString(), premio),
                                  channel, accountId);
            }
        }

        /// <summary>Cómo va la raid del gremio, que es lo que el panel del cliente enseñaría.</summary>
        private static string RaidStatus(long characterId)
        {
            var guild = Managers.GuildStore.GuildOf(characterId);
            if (guild == null) return T("raid.noguild");

            var running = Managers.GuildRaidManager.RunningOf(characterId);
            if (running == null)
            {
                var compradas = Managers.GuildStore.OwnedRaids(guild.Id);
                string tiene = compradas.Count == 0
                    ? T("raid.status.none")
                    : string.Join(", ", compradas.Select(r =>
                        Jondo.Unity.World.Content.Raids.Of(r)?.Name + " (" + r + ")"));
                return T("raid.status.idle", tiene, guild.GuildKamas.ToString());
            }

            var kind = Jondo.Unity.World.Content.Raids.Of(running.RaidId);
            var queda = running.Left(DateTimeOffset.UtcNow);
            int planta = kind.FloorOf(Managers.GuildRaidManager.SubAreaOf(
                Jondo.Unity.Server.Network.SessionContext.State.MapId));
            string estado = T("raid.status.running", kind.Name, ((int)queda.TotalMinutes).ToString(),
                              running.Score.ToString(), running.Members.Count.ToString(),
                              planta > 0 ? planta.ToString() : "-");

            // Y la luz, que no tiene otro sitio donde salir. El panel de la raid la pintaría, pero
            // ese panel necesita mensajes que ninguna captura trae; hasta entonces, aquí.
            if (!kind.HasLight) return estado;

            var luces = new List<string>();
            for (int planta2 = 1; planta2 <= Jondo.Unity.World.Content.Luminomachine.Machines; planta2++)
            {
                luces.Add($"{planta2}:{running.Get(Jondo.Unity.World.Content.RaidInstance.LightVariable(planta2))}" +
                          $"/{Jondo.Unity.World.Content.Luminomachine.MostLight}");
            }

            return estado + T("raid.status.light", string.Join(" ", luces),
                              Managers.Equipment.HowMany(
                                  Jondo.Unity.World.Content.Luminomachine.SaltItem).ToString());
        }

        /// <summary>
        /// Invitar a alguien al gremio, o echar una candidatura a uno.
        ///
        /// Esto es un comando y no un botón porque el botón NO ESTÁ MEDIDO: las capturas de
        /// gremio son del lado de quien recibe la invitación y del líder que lee la candidatura,
        /// así que se sabe lo que el servidor manda -el jiq y el jma- y lo que el cliente
        /// contesta -el jiz y el jjn-, pero no con qué mensaje se piden. El día que aparezca en
        /// una captura, el botón llama a los mismos dos métodos y el comando sobra.
        /// </summary>
        private static async Task GremioAsync(NetworkStream stream, string rest, int channel, long accountId)
        {
            string entrada = (rest ?? "").Trim();
            string[] crear = entrada.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (crear.Length > 0 && (crear[0].Equals("crear", StringComparison.OrdinalIgnoreCase) ||
                                     crear[0].Equals("creer", StringComparison.OrdinalIgnoreCase)))
            {
                if (crear.Length < 2)
                {
                    await NotifyAsync(stream, Usage(".gremio"), channel, accountId);
                    return;
                }

                long founder = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
                string name = crear[1].Trim();
                string? fallo = await Handlers.GuildHandler.CreateFromCommandAsync(stream, founder, name);
                await NotifyAsync(stream,
                    fallo == null ? T("guild.create.done", name) : T(fallo, name),
                    channel, accountId);
                return;
            }

            string[] partes = entrada.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            long who = Jondo.Unity.Server.Network.SessionContext.State.CharacterId;

            // Salir por el chat es lo mismo que salir por la ventana: el jho, sin el jho.
            if (partes.Length == 1 && partes[0].Equals("salir", StringComparison.OrdinalIgnoreCase))
            {
                var dejado = Managers.GuildStore.GuildOf(who);
                if (dejado == null)
                {
                    await NotifyAsync(stream, T("guild.invite.noguild"), channel, accountId);
                    return;
                }

                await Handlers.GuildHandler.LeaveAsync(stream, who);
                await NotifyAsync(stream, T("guild.left", dejado.Name), channel, accountId);
                return;
            }

            if (partes.Length < 2)
            {
                await NotifyAsync(stream, Usage(".gremio"), channel, accountId);
                return;
            }

            string que = partes[0].ToLowerInvariant();
            string quien = partes[1];

            if (que == "rango" && partes.Length > 2 && int.TryParse(partes[2].Trim(), out int rango))
            {
                string fallo = await Handlers.GuildHandler.SetMemberRankAsync(who, quien, rango);
                await NotifyAsync(stream, fallo == null ? T("guild.rank.done", quien, rango.ToString()) : T(fallo, quien),
                                  channel, accountId);
                return;
            }

            if (que == "expulsar")
            {
                string fallo = await Handlers.GuildHandler.KickAsync(who, quien);
                await NotifyAsync(stream, fallo == null ? T("guild.kick.done", quien) : T(fallo, quien),
                                  channel, accountId);
                return;
            }

            if (que == "invitar")
            {
                string fallo = await Handlers.GuildHandler.InviteAsync(who, quien);
                await NotifyAsync(stream, fallo == null ? T("guild.invite.sent", quien) : T(fallo, quien),
                                  channel, accountId);
                return;
            }

            if (que == "solicitar")
            {
                string mensaje = partes.Length > 2 ? partes[2] : "";
                string fallo = await Handlers.GuildHandler.ApplyAsync(who, quien, mensaje);
                await NotifyAsync(stream, fallo == null ? T("guild.apply.sent", quien) : T(fallo, quien),
                                  channel, accountId);
                return;
            }

            await NotifyAsync(stream, Usage(".gremio"), channel, accountId);
        }

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
        /// <summary>
        /// A command's answer: an information line only its author sees. The channel and the
        /// account are what the chat line it used to be needed; the information message needs
        /// neither, and they stay so the sixty-odd callers do not all change.
        /// </summary>
        private static async Task NotifyAsync(NetworkStream stream, string text, int channel, long accountId)
        {
            await NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));
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
        /// <summary>
        /// Las coordenadas de un comando, tal como el cliente las manda.
        /// </summary>
        /// <remarks>
        /// Y no es como el jugador las escribe. Al teclear <c>[0,-8]</c> en el chat, el cliente lo
        /// convierte en un enlace de mapa antes de enviarlo, y lo que llega al servidor es
        /// <c>.teleport {{map,0,-8,1}}</c> -medido en el registro, tres veces seguidas-. Con el
        /// parser de antes eso eran cuatro trozos y no dos, así que el comando contestaba con su
        /// uso a quien lo había escrito bien. Ahora se lee el enlace: la palabra «map» y el mundo
        /// de detrás se descartan y quedan las dos cifras.
        /// </remarks>
        internal static bool ParseCoordinates(string rest, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrWhiteSpace(rest)) return false;

            var cleaned = new System.Text.StringBuilder(rest.Length);
            foreach (char c in rest)
            {
                cleaned.Append(c == '[' || c == ']' || c == '(' || c == ')' || c == '{' || c == '}'
                               || c == ',' || c == ';'
                    ? ' ' : c);
            }

            var parts = new List<string>(cleaned.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (parts.Count > 0 && parts[0].Equals("map", StringComparison.OrdinalIgnoreCase))
            {
                // {{map,x,y,mundo}}: fuera la palabra, y el mundo del final sobra.
                parts.RemoveAt(0);
                if (parts.Count == 3) parts.RemoveAt(2);
            }

            if (parts.Count != 2) return false;

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
