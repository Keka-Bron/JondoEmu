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
    /// The administration commands the player types in the chat.
    ///
    /// They come in where any chat line comes in -- the ktm, with its channel inside -- so they work
    /// in general, guild, trade or wherever: the channel only decides which tab the answer appears
    /// in, not whether the command is served. And they are not published: whoever recognises them
    /// (<see cref="TryHandleAsync"/>) returns true and the echo never goes out, which is what keeps a
    /// ".kamas 10000" from ending up written in the guild chat.
    ///
    /// The answer goes in a kti, which is the capture's chat line -- the same one the server uses to
    /// send back what somebody says -- and not in the csm that was used before: csm does not appear
    /// in any of the captures nor in the client's message table, so there is no way of knowing the
    /// player is seeing it. With kti it is seen for sure.
    ///
    /// What each command sends after touching the character comes from messages that already exist
    /// in the emulator and are measured against captures:
    ///
    ///   kub, iun   the sheet and the pods, as when spending characteristics (CharacteristicsHandler)
    ///   ivf        the kamas, as when paying for a zaap trip (ZaapTravelHandler)
    ///   jsd/jru/lqu/hjk   the map change, as the zaap and the edge do (TeleportHandler)
    ///   hms, itg   the spells and their bar, as on entering the world (WorldEntry)
    ///   jsn, lxc   the look, as when equipping something (EquipmentHandler)
    ///
    /// The bvr, bcy and krd/kri/krb that were already there are left as they were so as not to break
    /// what the player already has working, but they do not come from any capture: they go in the
    /// ANSWER envelope (root field 3) and without a request id, which is not how the real server
    /// pushes anything. Everything new goes through Push, which is field 1, the one for the messages
    /// the server sends on its own.
    /// </summary>
    public static class CommandHandler
    {
        /// <summary>
        /// The commands that exist and the key of their usage text. It rules in two places: it decides
        /// which lines the server swallows instead of publishing them, and it leads to the catalogue
        /// that answers the player in his session's language when he gets one wrong.
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
        /// Which role each command requires.
        ///
        /// The split: moving around the world is a moderator's, because it is what is needed to go and
        /// help somebody; touching the character -- kamas, level, size -- or opening a shop is a game
        /// master's. A command missing from this table is treated as an administrator's, which is the
        /// safe side to be wrong on: adding a new one and forgetting to give it a permission leaves it
        /// closed, not open.
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

        /// <summary>The level at which the normal game ends; from there up it is Omega.</summary>
        private const int MaxNormalLevel = 200;

        /// <summary>
        /// Serves the line. Returns true when it was a command and so it must NOT be published in the
        /// chat.
        ///
        /// It swallows only the commands that EXIST: a message that starts with a dot and is none of
        /// them is a chat line like any other and goes on its way.
        ///
        /// True as well when the command exists but is written wrong: in that case what is done is
        /// answering how it is written. Publishing a half-written command would be showing everybody
        /// what the player wanted to do, which is exactly what cannot happen.
        /// </summary>
        public static async Task<bool> TryHandleAsync(NetworkStream stream, string text,
                                                      int channel = 0, long accountId = 0)
        {
            string? command = CommandOf(text);
            if (command == null) return false;

            // In jail, no command of any kind -- an administrator's neither: see Managers.Jail.
            // Swallowed, not echoed, so it does not go out on the general channel either.
            if (Managers.Jail.IsJailed(Network.SessionContext.State.CharacterId)
                && (Uso.ContainsKey(command) || LooksLikeCommand(command)))
            {
                await NotifyAsync(stream, T("jail.no_commands"), channel, accountId);
                return true;
            }

            if (!Uso.ContainsKey(command))
            {
                // Not ours. A notice is given -- only if it looks like a command, so as not to answer
                // whoever writes "...well" -- but the line goes on its normal way.
                if (LooksLikeCommand(command))
                {
                    await NotifyAsync(stream, T("command.unknown", command,
                                              string.Join(", ", Uso.Keys)), channel, accountId);
                }
                return false;
            }

            // May this person type this command?
            //
            // Until now NOBODY checked it: any player could type ".kamas 10000" or ".level 200" and
            // the server gave it to him. It is looked up here, on the server, and against the
            // database, every time the command is typed; it is not kept in the session, so taking a
            // role away from somebody takes effect at once.
            //
            // The account comes from this socket's session, not from anything the client sends.
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
                return true;   // swallowed: neither run nor published in the chat
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

            // The usual one, which comes from no capture but has been here since the beginning.
            await NetworkMessage.WriteFrameAsync(stream, NetworkEnvelope.BuildGameNodePacket(
                "type.ankama.com/bvr", Pb.New().Var(1, GameState.Kamas).Build()));

            // And the one that is measured: it is what the real server sends when charging for a zaap
            // trip. It carries the WHOLE figure, not the difference, so sending both does not throw
            // anything off -- the second says the same as the first.
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
        /// Setting the level, and with it everything that hangs off the level: experience,
        /// characteristic points and spells.
        ///
        /// Experience is set at the level's FLOOR (ExperienceTable.LevelFloor). Without that the
        /// character stayed at level 150 with a level 40's experience, and the client draws the bar
        /// with what the kub sends it: an overflowing or empty bar depending on going up or down.
        ///
        /// The spells are recalculated whole and sent again: SpellTable already knows which pair each
        /// level unlocks and at which grade, reading MinPlayerLevel from SpellLevels, which is the same
        /// as what is sent on entering the world. The list (hms) and the bar (itg) are sent because the
        /// list alone leaves slots pointing at spells no longer owned when going DOWN a level.
        ///
        /// Above 200 -- the Omega levels -- characteristic points are not touched: the capital stays at
        /// level 200's, which is what the game gives.
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

            // The ceiling is set by the client's experience table, which goes up to 1889. Without it
            // loaded there is no experience floor to set and it does not go past 200.
            int ceiling = ExperienceTable.IsLoaded ? ExperienceTable.MaxLevel : MaxNormalLevel;
            int newLevel = Math.Clamp(wanted, 1, ceiling);

            int oldLevel = GameState.CharacterLevel;
            var before = Spells(oldLevel);

            GameState.CharacterLevel = newLevel;

            // The level-up window, with the target level. It goes BEFORE the new characteristics
            // because that is where the client takes what it shows inside from, and that is the
            // capture's order. It is also sent when going DOWN a level: the message only carries the
            // level being reached, so it works the same, and seeing the window is the way to know the
            // command did something.
            if (newLevel != oldLevel)
            {
                await NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Kua, ConnectionProtocol.BuildLevelUp(newLevel)));
            }

            // Experience is only touched if there is a table to put it where it belongs: without it
            // LevelFloor returns zero for everything, and that is not "the level's floor", it is
            // wiping the character's experience.
            if (ExperienceTable.IsLoaded)
            {
                GameState.Experience = ExperienceTable.LevelFloor(newLevel);
            }

            // The capital is five per level from the second, which is how StatsHandler and
            // CharacteristicsHandler count it. Above 200 it freezes: Omega levels give no
            // points.
            int capital = StatsHandler.TotalCapitalForLevel(Math.Min(newLevel, MaxNormalLevel));
            GameState.CharacterRemainingPoints = Math.Max(0, capital - SpentCapital(capital));

            DatabaseManager.SaveCurrentCharacter();

            // What this command already sent, as it was. It is not removed -- it has been here since the
            // beginning and there is no way to check from outside whether the client looks at it -- but it
            // is not relied on either: kri, krb and krd go out through root field 3, which is the
            // ANSWERS envelope, and without a request id inside. What really refreshes the sheet is
            // the kub further down.
            byte[]? kri = StatsHandler.BuildUpdatedKriPacket();
            if (kri != null) await NetworkMessage.WriteFrameAsync(stream, kri);

            await NetworkMessage.WriteFrameAsync(stream,
                StatsHandler.BuildKrbPacket(GameState.CharacterRemainingPoints));
            await NetworkMessage.WriteFrameAsync(stream,
                NetworkEnvelope.BuildGameNodePacket("type.ankama.com/krd", Array.Empty<byte>()));

            // The bcy only makes sense going up: it is the "you went up a level" message. Going down
            // it is not sent, since its two point fields would go negative.
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

            // And the real sheet: the kub carries the level, the life the level gives, the experience
            // with its floor and ceiling, and the points left. It is the same pair of messages
            // CharacteristicsHandler sends when spending points.
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
        /// What the sheet the character is wearing has cost him.
        ///
        /// With the client's prices (<see cref="BreedStatCost"/>), which is what real point spending
        /// uses: the panel works out the cost by itself before sending the kum, and a server that counts
        /// differently gives the player back a number of points the window has just promised him it was
        /// not. Adding the points up plainly -- which is what this command did -- left too many free for
        /// anybody who had gone over a hundred in something, because from there on each point costs
        /// two.
        ///
        /// Without that table loaded it falls back to StatsHandler's banded model, which gives the same
        /// for the breeds whose price we know.
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

        /// <summary>The spells the character has at a given level, by id and grade.</summary>
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
        /// Sends the client the spells of the new level and returns what changed, to tell him in the
        /// chat.
        ///
        /// If the spell table is not loaded nothing is sent: an empty hms does not say "nothing has
        /// changed", it says "you have no spells", and it would leave the panel blank because of a data
        /// problem that has nothing to do with the command.
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
            // A single number is a map id, and goes straight there: it is the way to reach a specific
            // interior when there are four maps at the same coordinate, as in the Guild Temple.
            // Only if the map exists; a number that is not a map is a usage error.
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

            // When there were several, it is said why that one was chosen: they are coordinates shared
            // by houses, interiors and separate worlds, and the player has to be able to know which of
            // them he ended up in.
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
        /// To the vendors' map, which is looked up instead of written: the one with the most rows in
        /// NpcSpawns. Today that is the 52 of the Amakna Village (88212759, [-1,0]) against a single one
        /// on the second, so a tie is not possible; and if another map is populated one day, the command
        /// still takes you where the vendors are without having to be touched.
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
        /// The size of the figure. It is stored on the character and BreedLookTable applies it when
        /// building the look; here it only has to be rebuilt, which is the two messages
        /// EquipmentHandler already sends when changing clothes: the jsn redraws the one on the map
        /// and the lxc the one on the sheet.
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
        /// PR #22 brought here a whole copy of GiveAsync that only differed in returning the item. Two
        /// copies of «give something to somebody» is how they end up disagreeing about whether the
        /// client was told, so what was done was giving that ability to the one that already
        /// existed.
        /// </remarks>
        public static Task<HavenBagStore.StoredItem?> GrantItemAsync(NetworkStream stream,
                                                                     int gid, int quantity)
            => Equipment.GrantAsync(stream, gid, quantity);

        private static async Task ItemAsync(NetworkStream stream, string rest,
                                            int channel, long accountId)
        {
            string[] parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // A last word of "random" rolls each characteristic in its range, the way a craft
            // does; "max", or nothing, is the top of every one, as it has always been.
            bool random = false;
            if (parts.Length > 1 && TryParseStatMode(parts[^1], out bool asked))
            {
                random = asked;
                parts = parts[..^1];
            }

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

            if (random && TooManyRolled(gid, quantity))
            {
                await NotifyAsync(stream, T("item.too_many_rolled", MaxRolledItems), channel, accountId);
                return;
            }

            bool given = random
                ? await WorkshopHandler.GiveAsync(stream, gid, quantity)
                : await GrantItemAsync(stream, gid, quantity) != null;
            if (!given)
            {
                await NotifyAsync(stream, T("item.template_missing", gid), channel, accountId);
                return;
            }

            await RefreshPodsAsync(stream);
            ActivityJournal.Current.Write("item.granted",
                accountId > 0 ? accountId : SessionContext.Current.AccountId,
                GameState.CharacterId,
                new { source = "command", gid, quantity, random });
            await NotifyAsync(stream, T("item.added", gid, quantity),
                              channel, accountId);
        }

        /// <summary>
        /// The most items that roll one gift hands over, by command or by the control API: each is
        /// a row of its own and a message to the client, and ".item 2469 1000000 random" was a
        /// million of both. What rolls nothing joins one stack and has no such cost.
        /// </summary>
        internal const int MaxRolledItems = 100;

        /// <summary>Whether rolling that many of this item is over <see cref="MaxRolledItems"/>.</summary>
        internal static bool TooManyRolled(int gid, long quantity)
            => quantity > MaxRolledItems
               && Managers.Forgemagic.TemplateOf(gid) is { } template
               && !Managers.Forgemagic.Stacks(template);

        /// <summary>"max" or "random" (and "aleatorio", "aléatoire"): how an item given comes out.</summary>
        internal static bool TryParseStatMode(string word, out bool random)
        {
            random = false;
            switch ((word ?? "").Trim().ToLowerInvariant())
            {
                case "max": return true;
                case "random":
                case "aleatorio":
                case "aléatoire":
                case "aleatoire": random = true; return true;
                default: return false;
            }
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
        /// What the client sends us and we do not know how to handle, from the most frequent to the
        /// least.
        ///
        /// It is grouped by SHAPE and not by opcode, which is what makes the list useful: the same
        /// opcode can carry different payloads depending on what the player is doing, and counting them
        /// together hides exactly what has to be seen.
        ///
        /// This deciphers nothing. It says where to look; whatever is looked at is measured against a
        /// capture like everything else, and until then nothing is answered, because a made-up answer
        /// leaves the client with a state the server does not have.
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
        /// Guild raids: buying one, launching it, going in, leaving, closing it, and seeing how it goes.
        ///
        /// A command and not buttons for the same reason as the invitation: the raids tab of the guild
        /// shop comes out EMPTY in the captures -- the recorded guild had none --, so it is not known
        /// which message buys one or which launches it. What lies underneath is real: the instance, the
        /// clock and the variables the client's content reads.
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
        /// A raid's weekly ranking.
        /// </summary>
        /// <remarks>
        /// Through the chat, like everything about raids, and for the same reason: the rankings window
        /// exists in the client -- «Acceder a las clasificaciones», «Ver la clasificación» -- but no
        /// capture opens it, so it is not known which message fills it.
        ///
        /// The podium ornament is NAMED and not handed out. Today the wardrobe offers all 167 to
        /// everybody, so «giving it» would give nothing; the day there are ornaments to win, here is
        /// who they go to.
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

        /// <summary>How the guild's raid is going, which is what the client's panel would show.</summary>
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

            // And the light, which has nowhere else to show. The raid panel would draw it, but that
            // panel needs messages no capture carries; until then, here.
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
        /// Inviting somebody to the guild, or sending an application to one.
        ///
        /// This is a command and not a button because the button IS NOT MEASURED: the guild captures
        /// are from the side of whoever receives the invitation and of the leader who reads the
        /// application, so it is known what the server sends -- the jiq and the jma -- and what the
        /// client answers -- the jiz and the jjn --, but not which message asks for them. The day it
        /// shows up in a capture, the button calls the same two methods and the command is no longer
        /// needed.
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

            // Leaving through the chat is the same as leaving through the window: the jho, without the jho.
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
        /// The notice to the player, on the channel he wrote in so that it shows in the tab he is
        /// looking at. It is a kti, the capture's chat line.
        ///
        /// CAREFUL: this is the EXCEPTION, not the rule. A kti goes out on the general channel and
        /// everybody reads it. To tell the player something -- «you do not have the level», «you won
        /// kamas» -- an lqn with its message number goes; see <see cref="Managers.InfoMessages"/>. The
        /// chat is used here because a command's answer is free text that is not in the client's table,
        /// and because the player has just written in that same tab and expects the answer there.
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

        /// <summary>The first word in lower case, or null if the line does not start with a dot.</summary>
        private static string? CommandOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(".", StringComparison.Ordinal)) return null;

            int space = trimmed.IndexOf(' ');
            string word = space < 0 ? trimmed : trimmed.Substring(0, space);
            return word.ToLowerInvariant();
        }

        /// <summary>What comes after the command, untouched.</summary>
        private static string RestOf(string text)
        {
            string trimmed = text.TrimStart();
            int space = trimmed.IndexOf(' ');
            return space < 0 ? "" : trimmed.Substring(space + 1).Trim();
        }

        /// <summary>
        /// Whether a word starting with a dot LOOKS LIKE a command: a dot and letters, nothing else.
        /// It is there so as not to answer "that command does not exist" to whoever writes "...well"
        /// or ".", which are perfectly ordinary chat lines.
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
        /// Coordinates, written however: [-1,0], -1 0, -1,0 or (-1;0). Brackets and separators are
        /// turned into spaces and what is left has to be two numbers.
        /// </summary>
        /// <summary>
        /// A command's coordinates, as the client sends them.
        /// </summary>
        /// <remarks>
        /// And that is not how the player types them. On typing <c>[0,-8]</c> in the chat, the client
        /// turns it into a map link before sending it, and what reaches the server is
        /// <c>.teleport {{map,0,-8,1}}</c> -- measured in the log, three times in a row --. With the old
        /// parser that was four pieces and not two, so the command answered with its usage to whoever
        /// had written it right. Now the link is read: the word «map» and the world after it are
        /// dropped and the two figures are left.
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
                // {{map,x,y,world}}: the word goes, and the world at the end is not needed.
                parts.RemoveAt(0);
                if (parts.Count == 3) parts.RemoveAt(2);
            }

            if (parts.Count != 2) return false;

            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>The subarea's name, and if it is not known, its number.</summary>
        private static string SubAreaName(int subAreaId)
        {
            string name = DatabaseManager.GetSubAreaName(subAreaId);
            return string.IsNullOrEmpty(name) ? T("map.subarea", subAreaId) : name;
        }
    }
}
