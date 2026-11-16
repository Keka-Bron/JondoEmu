using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Protocol;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The staff's commands: the ones the admin menu of the client (menuadmin.xml) and the staff
    /// console (F11) run, and that work in the chat with a dot all the same.
    ///
    /// Two rules shape them. A command about a PLAYER takes the name first (".jail Name", ".mute Name
    /// 30"), which is what the menu's %p fills in. A command that changes a CHARACTER -- the ones
    /// that existed already (.kamas, .level, ...) and the ones here (.energy, .quest, ...) -- acts
    /// on the staff's own character unless the name comes LAST (".kamas 1000 Name"): see
    /// <see cref="SplitTarget"/>. Both reuse what the control API's routes and the other handlers
    /// already do -- the jail, the map's spawns, the gifts, the quest and content calls -- instead of
    /// doing it again.
    ///
    /// The role of each is in CommandHandler's table: a moderator moves and sanctions, a game master
    /// touches characters and the map, an administrator rules the server.
    /// </summary>
    public static partial class CommandHandler
    {
        /// <summary>How long to wait for another session's turn before telling the staff it is busy.</summary>
        private static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The commands that may name another character as their LAST word: the ones that change the
        /// character they run for. The rest take the player first, or none.
        /// </summary>
        private static readonly HashSet<string> Targetable = new(StringComparer.OrdinalIgnoreCase)
        {
            ".kamas", ".level", ".oficio", ".oficios", ".size", ".item", ".itemset", ".receta",
            ".energy", ".heal", ".stats", ".quest", ".gift",
        };

        /// <summary>
        /// Last words that are options and never a player: with these, ".item 39 5 max" is not an item
        /// for a player called Max.
        /// </summary>
        private static readonly HashSet<string> OptionWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "max", "random", "aleatorio", "all", "todos", "tous", "on", "off", "start", "finish", "forget",
        };

        // ─── Who the command is for ─────────────────────────────────────────────

        /// <summary>
        /// Takes the target off the end of a command's arguments. The last word is the target when it
        /// is a connected player (<paramref name="isPlayer"/>), is not an option word and is not a
        /// number; a leading '@' ("@Name") forces it to be read as a name. Returns the arguments
        /// without it and the name, or the arguments untouched and null.
        /// </summary>
        internal static (string Arguments, string? Target) SplitTarget(string rest, Func<string, bool> isPlayer)
        {
            string trimmed = (rest ?? "").Trim();
            if (trimmed.Length == 0) return ("", null);

            int cut = trimmed.LastIndexOfAny(new[] { ' ', '\t' });
            string last = cut < 0 ? trimmed : trimmed[(cut + 1)..];
            string before = cut < 0 ? "" : trimmed[..cut].TrimEnd();

            bool forced = last.StartsWith("@", StringComparison.Ordinal);
            string name = forced ? last[1..] : last;
            if (name.Length == 0) return (trimmed, null);
            if (!forced && (OptionWords.Contains(name) || long.TryParse(name, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out _) || name.StartsWith("[", StringComparison.Ordinal)))
                return (trimmed, null);
            return isPlayer(name) ? (before, name) : (trimmed, null);
        }

        private static GameSession? FindOnline(string name)
        {
            var found = SessionRegistry.FindByName(name);
            return found != null && found.IsInWorld && found.HasCharacter && found.Stream != null ? found : null;
        }

        /// <summary>
        /// Runs work as another connected session: on ITS turn, so it does not cross a packet the
        /// client is being answered, and in its context, so that the handlers that read the ambient
        /// state act on it. The staff's own session runs the work directly: its turn is the one the
        /// command is being served on, and waiting for it would wait for itself.
        /// </summary>
        private static async Task<bool> RunAsAsync(GameSession target, Func<GameSession, Task> work)
        {
            if (ReferenceEquals(target, SessionContext.Current))
            {
                await work(target);
                return true;
            }
            if (!await target.UnoCadaVez.WaitAsync(TurnWait)) return false;
            try
            {
                using (SessionContext.Push(target)) await work(target);
            }
            finally
            {
                target.UnoCadaVez.Release();
            }
            return true;
        }

        /// <summary>
        /// Whether the staff may act on a character: its own, or one whose account has a lower role.
        /// A moderator does not jail, kick, mute or summon an administrator, nor does a game master
        /// change his kamas: whoever is sanctioned could not lift it himself (a prisoner runs no
        /// command at all).
        /// </summary>
        internal static bool MayActOn(int staffRole, long staffAccount, int targetRole, long targetAccount)
            => staffAccount == targetAccount || staffRole > targetRole;

        /// <summary><see cref="MayActOn"/> for the session serving the command, told to the staff when refused.</summary>
        private static async Task<bool> OutranksAsync(NetworkStream stream, GameSession target, int channel)
        {
            long mine = SessionContext.Current.AccountId;
            if (MayActOn(DatabaseManager.GetAccountRole(mine), mine,
                         DatabaseManager.GetAccountRole(target.AccountId), target.AccountId))
                return true;
            await NotifyAsync(stream, T("admin.outranked", target.State.CharacterName ?? ""), channel, 0);
            return false;
        }

        /// <summary>
        /// A command of the existing set run for another character: refused if he is fighting or
        /// does not rank below the staff, and answered to the staff, since what the command tells
        /// goes to whoever it ran for.
        /// </summary>
        private static async Task RunOnOtherAsync(NetworkStream staffStream, GameSession target, string command,
                                                  string rest, int channel)
        {
            string name = target.State.CharacterName ?? "";
            if (!await OutranksAsync(staffStream, target, channel)) return;
            if (target.State.IsInFight)
            {
                await NotifyAsync(staffStream, T("admin.in_fight", name), channel, 0);
                return;
            }
            bool ran = await RunAsAsync(target, s => DispatchAsync(command, s.Stream!, rest, channel, s.AccountId));
            await NotifyAsync(staffStream, ran ? T("admin.done_on", name, command.TrimStart('.'), rest) : T("admin.busy", name),
                              channel, 0);
            if (ran)
                ActivityJournal.Current.Write("command.on_target", SessionContext.Current.AccountId, GameState.CharacterId,
                    new { command, target = target.CharacterId, args = rest });
        }

        // ─── The commands ───────────────────────────────────────────────────────

        private static async Task AdminAsync(string command, NetworkStream stream, string rest, int channel, long accountId)
        {
            switch (command)
            {
                case ".who": await WhoAsync(stream, channel); break;
                case ".whoami": await WhoisAsync(stream, SessionContext.Current, channel); break;
                case ".whois": await WithPlayerAsync(stream, command, rest, channel, (s, name) => WhoisAsync(stream, s, channel)); break;
                case ".where": await WhereAsync(stream, rest, channel); break;
                case ".mapinfo": await MapInfoAsync(stream, channel); break;
                case ".version": await NotifyAsync(stream, T("version.result", Contract.Version, SessionRegistry.InWorld().Count), channel, accountId); break;
                case ".goto": await WithPlayerAsync(stream, command, rest, channel, (s, name) => GotoAsync(stream, s, channel)); break;
                case ".summon": await WithPlayerAsync(stream, command, rest, channel, (s, name) => SummonAsync(stream, s, channel)); break;
                case ".jail": await WithPlayerAsync(stream, command, rest, channel, (s, name) => JailAsync(stream, s, channel)); break;
                case ".unjail": await UnjailAsync(stream, rest, channel); break;
                case ".jailed": await JailedAsync(stream, channel); break;
                case ".kick": await WithPlayerAsync(stream, command, rest, channel, (s, name) => KickAsync(stream, s, channel)); break;
                case ".mute": await MuteAsync(stream, rest, channel); break;
                case ".unmute": await WithPlayerAsync(stream, command, rest, channel, (s, name) => UnmuteAsync(stream, s, channel)); break;
                case ".warn": await WarnAsync(stream, rest, channel); break;
                case ".ban": await BanAsync(stream, rest, channel); break;
                case ".unban": await UnbanAsync(stream, rest, channel); break;
                case ".bans": await BansAsync(stream, channel); break;
                case ".announce": await AnnounceAsync(stream, rest, channel); break;
                case ".save": await SaveAsync(stream, channel); break;
                case ".reload": await ReloadAsync(stream, channel); break;
                case ".shutdown": await ShutdownAsync(stream, rest, channel); break;
                case ".quest": await QuestAsync(stream, rest, channel); break;
                case ".quests": await QuestsAsync(stream, channel); break;
                case ".energy": await EnergyAsync(stream, rest, channel); break;
                case ".heal": await HealAsync(stream, channel); break;
                case ".stats": await StatsAsync(stream, rest, channel); break;
                case ".spawn": await SpawnAsync(stream, rest, channel); break;
                case ".remove": await RemoveAsync(stream, rest, channel); break;
                case ".gift": await GiftAsync(stream, rest, channel); break;
            }
        }

        /// <summary>
        /// The commands about a player: the name is the first word. Usage when it is missing, and
        /// "not connected" when nobody has it; the rest of the arguments are the command's own.
        /// </summary>
        private static async Task WithPlayerAsync(NetworkStream stream, string command, string rest, int channel,
                                                  Func<GameSession, string, Task> act)
        {
            string name = FirstWord(rest, out _);
            if (name.Length == 0)
            {
                await NotifyAsync(stream, Usage(command), channel, 0);
                return;
            }
            var player = FindOnline(name.TrimStart('@'));
            if (player == null)
            {
                await NotifyAsync(stream, T("admin.no_player", name), channel, 0);
                return;
            }
            await act(player, name);
        }

        /// <summary>The last word of the arguments, or "" when there are none.</summary>
        internal static string LastWordOf(string rest)
        {
            string trimmed = (rest ?? "").Trim();
            int cut = trimmed.LastIndexOfAny(new[] { ' ', '\t' });
            return cut < 0 ? trimmed : trimmed[(cut + 1)..];
        }

        /// <summary>The first word of the arguments, and what comes after it.</summary>
        internal static string FirstWord(string rest, out string after)
        {
            string trimmed = (rest ?? "").Trim();
            int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
            after = space < 0 ? "" : trimmed[(space + 1)..].Trim();
            return space < 0 ? trimmed : trimmed[..space];
        }

        // ─── Information ────────────────────────────────────────────────────────

        private static string PlaceOf(long mapId, int cell, out int x, out int y)
        {
            var info = MapManager.GetMapInfo(mapId);
            x = info?.PosX ?? 0;
            y = info?.PosY ?? 0;
            return info == null ? T("map.subarea", 0) : SubAreaName(info.SubAreaId);
        }

        private static async Task WhoAsync(NetworkStream stream, int channel)
        {
            var online = SessionRegistry.InWorld().Where(s => s.HasCharacter).OrderBy(s => s.State.CharacterName).ToList();
            await NotifyAsync(stream, T("who.header", online.Count), channel, 0);
            foreach (var s in online.Take(40))
            {
                PlaceOf(s.State.MapId, s.State.CellId, out int x, out int y);
                await NotifyAsync(stream, T("who.line", s.State.CharacterName, s.State.CharacterLevel, x, y, s.State.MapId),
                                  channel, 0);
            }
        }

        private static async Task WhoisAsync(NetworkStream stream, GameSession who, int channel)
        {
            var s = who.State;
            string where = PlaceOf(s.MapId, s.CellId, out int x, out int y);
            string flags = (Jail.IsJailed(s.CharacterId) ? T("whois.jailed") : "")
                         + (Sanctions.IsMuted(s.CharacterId) ? T("whois.muted") : "")
                         + (s.IsInFight ? T("whois.fighting") : "");
            await NotifyAsync(stream, T("whois.result", s.CharacterName, s.CharacterId, s.CharacterLevel, who.AccountId,
                                        Roles.Nombre(DatabaseManager.GetAccountRole(who.AccountId)), s.MapId, x, y,
                                        s.CellId, s.Kamas, s.Language, flags, where), channel, 0);
        }

        private static async Task WhereAsync(NetworkStream stream, string rest, int channel)
        {
            var who = SessionContext.Current;
            if (rest.Trim().Length > 0)
            {
                who = FindOnline(rest.Trim().TrimStart('@'))!;
                if (who == null)
                {
                    await NotifyAsync(stream, T("admin.no_player", rest.Trim()), channel, 0);
                    return;
                }
            }
            var s = who.State;
            string where = PlaceOf(s.MapId, s.CellId, out int x, out int y);
            await NotifyAsync(stream, T("where.result", s.CharacterName, s.MapId, x, y, where, s.CellId), channel, 0);
        }

        private static async Task MapInfoAsync(NetworkStream stream, int channel)
        {
            long mapId = GameState.MapId;
            string where = PlaceOf(mapId, GameState.CellId, out int x, out int y);
            var (npcs, groups) = MapAdmin.On(mapId);
            await NotifyAsync(stream, T("mapinfo.result", mapId, x, y, where, GameState.CellId, npcs.Count, groups.Count,
                                        SessionRegistry.OnMap(mapId).Count), channel, 0);
            // What ".remove" takes: the ids of what stands here.
            foreach (var npc in npcs.Take(15))
                await NotifyAsync(stream, T("mapinfo.npc", npc.ContextualId, npc.NpcId, npc.Cell), channel, 0);
            foreach (var group in groups.Take(15))
                await NotifyAsync(stream, T("mapinfo.group", group.MobId, group.Cell,
                    string.Join(", ", group.Members.Select(m => $"{m.MonsterId}:{m.Grade}"))), channel, 0);
        }

        // ─── Moving ─────────────────────────────────────────────────────────────

        private static async Task GotoAsync(NetworkStream stream, GameSession player, int channel)
        {
            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("teleport.in_fight"), channel, 0);
                return;
            }
            var s = player.State;
            int landed = await TeleportHandler.ToMapAsync(stream, s.MapId, s.CellId);
            if (landed < 0)
            {
                await NotifyAsync(stream, T("teleport.load_failed", s.MapId, 0, 0), channel, 0);
                return;
            }
            PlaceOf(s.MapId, landed, out int x, out int y);
            await NotifyAsync(stream, T("goto.result", s.CharacterName, s.MapId, x, y, landed), channel, 0);
        }

        private static async Task SummonAsync(NetworkStream stream, GameSession player, int channel)
        {
            string name = player.State.CharacterName ?? "";
            long map = GameState.MapId;
            int cell = GameState.CellId;
            string staff = GameState.CharacterName ?? "";
            if (ReferenceEquals(player, SessionContext.Current))
            {
                await NotifyAsync(stream, T("admin.self"), channel, 0);
                return;
            }
            if (!await OutranksAsync(stream, player, channel)) return;
            if (player.State.IsInFight || GameState.IsInFight)
            {
                await NotifyAsync(stream, T("admin.in_fight", player.State.IsInFight ? name : staff), channel, 0);
                return;
            }

            int landed = -1;
            bool ran = await RunAsAsync(player, async s =>
            {
                landed = await TeleportHandler.ToMapAsync(s.Stream!, map, cell);
                if (landed >= 0)
                    await NotifyAsync(s.Stream!, T("summon.notice", staff), channel, 0);
            });
            await NotifyAsync(stream, !ran ? T("admin.busy", name)
                                   : landed < 0 ? T("admin.refused", name)
                                   : T("summon.result", name, map, landed), channel, 0);
        }

        // ─── Sanctions ──────────────────────────────────────────────────────────

        private static string JailReason(Jail.Refusal refusal) => refusal switch
        {
            Jail.Refusal.NotConnected => "admin.no_player",
            Jail.Refusal.InFight => "admin.in_fight",
            Jail.Refusal.Busy => "admin.busy",
            Jail.Refusal.AlreadyIn => "jail.already",
            Jail.Refusal.NotIn => "jail.not_in",
            Jail.Refusal.Self => "admin.self",
            _ => "jail.no_prison",
        };

        private static async Task JailAsync(NetworkStream stream, GameSession player, int channel)
        {
            string name = player.State.CharacterName ?? "";
            if (ReferenceEquals(player, SessionContext.Current))
            {
                await NotifyAsync(stream, T("admin.self"), channel, 0);
                return;
            }
            if (!await OutranksAsync(stream, player, channel)) return;
            // No warden: the call would take the staff session's turn to walk him to the corridor,
            // and that turn is the one this command is running on.
            var (refusal, sentence) = await Jail.LockUpAsync(player, null);
            if (refusal != Jail.Refusal.None)
            {
                await NotifyAsync(stream, T(JailReason(refusal), name), channel, 0);
                return;
            }
            await NotifyAsync(stream, T("jail.done", name, (int)Jail.Sentence.TotalMinutes), channel, 0);
            ActivityJournal.Current.Write("admin.jailed", SessionContext.Current.AccountId, GameState.CharacterId,
                new { prisoner = sentence!.CharacterId, name = sentence.Name, until = sentence.UntilUtc, via = "command" });
        }

        private static async Task UnjailAsync(NetworkStream stream, string rest, int channel)
        {
            string name = FirstWord(rest, out _).TrimStart('@');
            if (name.Length == 0)
            {
                await NotifyAsync(stream, Usage(".unjail"), channel, 0);
                return;
            }
            // A prisoner may be offline: he is found among the sentences, not among the sessions.
            var prisoner = Jail.Prisoners.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prisoner == null)
            {
                await NotifyAsync(stream, T("jail.not_in", name), channel, 0);
                return;
            }
            var refusal = await Jail.ReleaseAsync(prisoner.CharacterId, "released by a command");
            await NotifyAsync(stream, refusal == Jail.Refusal.None ? T("unjail.done", prisoner.Name)
                                                                  : T(JailReason(refusal), prisoner.Name), channel, 0);
            if (refusal == Jail.Refusal.None)
                ActivityJournal.Current.Write("admin.released", SessionContext.Current.AccountId, GameState.CharacterId,
                    new { prisoner = prisoner.CharacterId, name = prisoner.Name, via = "command" });
        }

        private static async Task JailedAsync(NetworkStream stream, int channel)
        {
            var prisoners = Jail.Prisoners;
            if (prisoners.Count == 0)
            {
                await NotifyAsync(stream, T("jailed.none"), channel, 0);
                return;
            }
            foreach (var p in prisoners)
                await NotifyAsync(stream, T("jailed.line", p.Name,
                    Math.Max(1, (int)Math.Ceiling((p.UntilUtc - DateTime.UtcNow).TotalMinutes))), channel, 0);
        }

        private static async Task KickAsync(NetworkStream stream, GameSession player, int channel)
        {
            string name = player.State.CharacterName ?? "";
            if (ReferenceEquals(player, SessionContext.Current))
            {
                await NotifyAsync(stream, T("admin.self"), channel, 0);
                return;
            }
            if (!await OutranksAsync(stream, player, channel)) return;
            // The socket is closed as the second connection of an account closes the first one: the
            // session's own way out saves the character.
            try { player.Stream?.Dispose(); } catch { }
            await NotifyAsync(stream, T("kick.done", name), channel, 0);
            ActivityJournal.Current.Write("admin.kicked", SessionContext.Current.AccountId, GameState.CharacterId,
                new { target = player.CharacterId, name });
        }

        private static async Task MuteAsync(NetworkStream stream, string rest, int channel)
        {
            string name = FirstWord(rest, out string after);
            if (name.Length == 0)
            {
                await NotifyAsync(stream, Usage(".mute"), channel, 0);
                return;
            }
            var player = FindOnline(name.TrimStart('@'));
            if (player == null)
            {
                await NotifyAsync(stream, T("admin.no_player", name), channel, 0);
                return;
            }
            // Ten minutes when no time is given, and never more than three weeks.
            var time = TimeSpan.FromMinutes(10);
            string timeWord = FirstWord(after, out _);
            if (timeWord.Length > 0 && (!Sanctions.TryParseDuration(timeWord, out time) || time <= TimeSpan.Zero))
            {
                await NotifyAsync(stream, Usage(".mute"), channel, 0);
                return;
            }
            if (time.TotalMinutes > Sanctions.MaxMuteMinutes) time = TimeSpan.FromMinutes(Sanctions.MaxMuteMinutes);
            if (ReferenceEquals(player, SessionContext.Current))
            {
                await NotifyAsync(stream, T("admin.self"), channel, 0);
                return;
            }
            if (!await OutranksAsync(stream, player, channel)) return;

            Sanctions.Mute(player.CharacterId, time);
            int minutes = (int)Math.Ceiling(time.TotalMinutes);
            await player.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildNotice(CommandTexts.Get(player.State.Language, "mute.notice", minutes))));
            await NotifyAsync(stream, T("mute.done", player.State.CharacterName, minutes), channel, 0);
            ActivityJournal.Current.Write("admin.muted", SessionContext.Current.AccountId, GameState.CharacterId,
                new { target = player.CharacterId, minutes });
        }

        private static async Task UnmuteAsync(NetworkStream stream, GameSession player, int channel)
        {
            string name = player.State.CharacterName ?? "";
            if (!Sanctions.Unmute(player.CharacterId))
            {
                await NotifyAsync(stream, T("unmute.none", name), channel, 0);
                return;
            }
            await player.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildNotice(CommandTexts.Get(player.State.Language, "unmute.notice"))));
            await NotifyAsync(stream, T("unmute.done", name), channel, 0);
        }

        private static async Task WarnAsync(NetworkStream stream, string rest, int channel)
        {
            string name = FirstWord(rest, out string text);
            if (name.Length == 0 || text.Length == 0)
            {
                await NotifyAsync(stream, Usage(".warn"), channel, 0);
                return;
            }
            var player = FindOnline(name.TrimStart('@'));
            if (player == null)
            {
                await NotifyAsync(stream, T("admin.no_player", name), channel, 0);
                return;
            }
            await player.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildNotice(CommandTexts.Get(player.State.Language, "warn.notice", text))));
            await NotifyAsync(stream, T("warn.sent", player.State.CharacterName), channel, 0);
            ActivityJournal.Current.Write("admin.warned", SessionContext.Current.AccountId, GameState.CharacterId,
                new { target = player.CharacterId, text });
        }

        /// <summary>
        /// ".ban Name [duration]": the ACCOUNT of a connected character is refused at the game's door
        /// and thrown out now. No duration is for good; "30m", "12h", "7d" or a number of minutes
        /// make it a ban that ends. An administrator's.
        /// </summary>
        private static async Task BanAsync(NetworkStream stream, string rest, int channel)
        {
            string name = FirstWord(rest, out string after);
            if (name.Length == 0)
            {
                await NotifyAsync(stream, Usage(".ban"), channel, 0);
                return;
            }
            var player = FindOnline(name.TrimStart('@'));
            if (player == null)
            {
                await NotifyAsync(stream, T("admin.no_player", name), channel, 0);
                return;
            }
            TimeSpan? time = null;
            string timeWord = FirstWord(after, out _);
            if (timeWord.Length > 0)
            {
                if (!Sanctions.TryParseDuration(timeWord, out var parsed))
                {
                    await NotifyAsync(stream, Usage(".ban"), channel, 0);
                    return;
                }
                if (parsed > TimeSpan.Zero) time = parsed;
            }
            // Neither oneself nor another administrator: the ban is an administrator's, but nobody
            // is locked out of the server by a slip of the menu's click.
            if (player.AccountId == SessionContext.Current.AccountId
                || Roles.AlMenos(DatabaseManager.GetAccountRole(player.AccountId), Roles.Administrador))
            {
                await NotifyAsync(stream, T("ban.protected", player.State.CharacterName), channel, 0);
                return;
            }

            string who = player.State.CharacterName ?? "";
            var ban = Sanctions.Add(player.AccountId, who, time, SessionContext.Current.AccountId);
            try { player.Stream?.Dispose(); } catch { }
            string until = ban.UntilUtc == null ? T("ban.forever")
                : T("ban.minutes", (int)Math.Ceiling((ban.UntilUtc.Value - DateTime.UtcNow).TotalMinutes));
            await NotifyAsync(stream, T("ban.done", who, player.AccountId, until), channel, 0);
            ActivityJournal.Current.Write("admin.banned", SessionContext.Current.AccountId, GameState.CharacterId,
                new { account = player.AccountId, name = who, until = ban.UntilUtc });
        }

        private static async Task UnbanAsync(NetworkStream stream, string rest, int channel)
        {
            string name = FirstWord(rest, out _).TrimStart('@');
            if (name.Length == 0)
            {
                await NotifyAsync(stream, Usage(".unban"), channel, 0);
                return;
            }
            bool lifted = Sanctions.Lift(name);
            await NotifyAsync(stream, lifted ? T("unban.done", name) : T("unban.none", name), channel, 0);
            if (lifted)
                ActivityJournal.Current.Write("admin.unbanned", SessionContext.Current.AccountId, GameState.CharacterId,
                    new { name });
        }

        private static async Task BansAsync(NetworkStream stream, int channel)
        {
            var bans = Sanctions.Bans();
            if (bans.Count == 0)
            {
                await NotifyAsync(stream, T("bans.none"), channel, 0);
                return;
            }
            foreach (var ban in bans)
                await NotifyAsync(stream, T("bans.line", ban.Name, ban.AccountId, ban.UntilUtc == null ? T("ban.forever")
                    : T("ban.minutes", (int)Math.Ceiling((ban.UntilUtc.Value - DateTime.UtcNow).TotalMinutes))), channel, 0);
        }

        // ─── The server ─────────────────────────────────────────────────────────

        private static async Task AnnounceAsync(NetworkStream stream, string rest, int channel)
        {
            string text = (rest ?? "").Trim();
            if (text.Length == 0)
            {
                await NotifyAsync(stream, Usage(".announce"), channel, 0);
                return;
            }
            int told = await AnnounceToAllAsync("announce.line", text);
            await NotifyAsync(stream, T("announce.sent", told), channel, 0);
            ActivityJournal.Current.Write("admin.announced", SessionContext.Current.AccountId, GameState.CharacterId,
                new { text, told });
        }

        /// <summary>A line to everybody in the world, in each one's own language.</summary>
        private static async Task<int> AnnounceToAllAsync(string key, params object[] values)
        {
            int told = 0;
            foreach (var s in SessionRegistry.InWorld())
            {
                try
                {
                    await s.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildNotice(CommandTexts.Get(s.State.Language, key, values))));
                    told++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Commands] An announcement did not reach {s.State.CharacterName}: {ex.Message}");
                }
            }
            return told;
        }

        /// <summary>Every connected character written to the database now, each on its own turn.</summary>
        private static async Task SaveAsync(NetworkStream stream, int channel)
        {
            int saved = 0;
            foreach (var s in SessionRegistry.InWorld().Where(s => s.HasCharacter).ToList())
            {
                if (await RunAsAsync(s, _ => { DatabaseManager.SaveCurrentCharacter(); return Task.CompletedTask; })) saved++;
            }
            await NotifyAsync(stream, T("save.done", saved), channel, 0);
            Console.WriteLine($"[Commands] Saved {saved} character(s).");
        }

        private static async Task ReloadAsync(NetworkStream stream, int channel)
        {
            long ms = ContentReload.Run();
            await NotifyAsync(stream, T("reload.done", ms), channel, 0);
        }

        private static CancellationTokenSource? _shutdownCountdown;

        /// <summary>
        /// ".shutdown [minutes]": the server stops now, or after a countdown the players are warned of
        /// at the start and at the last minute; ".shutdown cancel" takes it back. An administrator's.
        /// </summary>
        private static async Task ShutdownAsync(NetworkStream stream, string rest, int channel)
        {
            string word = rest.Trim();
            if (word.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            {
                var pending = Interlocked.Exchange(ref _shutdownCountdown, null);
                pending?.Cancel();
                await NotifyAsync(stream, pending != null ? T("shutdown.cancelled") : T("shutdown.none"), channel, 0);
                return;
            }
            int minutes = 0;
            if (word.Length > 0 && (!int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
                                    || minutes > 24 * 60))
            {
                await NotifyAsync(stream, Usage(".shutdown"), channel, 0);
                return;
            }

            string who = GameState.CharacterName ?? "";
            ActivityJournal.Current.Write("admin.server.shutdown", SessionContext.Current.AccountId, GameState.CharacterId,
                new { minutes, via = "command" });
            Console.WriteLine($"[Commands] {who} asks for the server to stop in {minutes} minute(s).");
            if (minutes == 0)
            {
                await NotifyAsync(stream, T("shutdown.now"), channel, 0);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    Program.RequestShutdown($"the staff's command ({who})");
                });
                return;
            }

            var countdown = new CancellationTokenSource();
            Interlocked.Exchange(ref _shutdownCountdown, countdown)?.Cancel();
            await AnnounceToAllAsync("shutdown.announce", minutes);
            await NotifyAsync(stream, T("shutdown.scheduled", minutes), channel, 0);
            _ = Task.Run(async () =>
            {
                try
                {
                    if (minutes > 1)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(minutes - 1), countdown.Token);
                        await AnnounceToAllAsync("shutdown.announce", 1);
                    }
                    await Task.Delay(TimeSpan.FromMinutes(1), countdown.Token);
                    if (!ReferenceEquals(Interlocked.CompareExchange(ref _shutdownCountdown, null, countdown), countdown)) return;
                    Program.RequestShutdown($"the staff's countdown ({who})");
                }
                catch (OperationCanceledException) { }
            });
        }

        // ─── The character ──────────────────────────────────────────────────────

        /// <summary>What every command that changes the sheet sends after: the stored character, the sheet, the kamas, the pods.</summary>
        public static async Task RefreshSheetAsync(GameSession session)
        {
            DatabaseManager.SaveCurrentCharacter();
            await CharacteristicsMessages.Live.SendAsync(session);
            await session.SendAsync(ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(session.State.Kamas)));
            await session.SendAsync(ConnectionProtocol.Push(Op.Iun,
                ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(session.State), ConnectionProtocol.Capacity(session.State))));
        }

        /// <summary>The quest ops: "start 619", "finish 619", "forget 619".</summary>
        internal static bool TryParseQuest(string rest, out string op, out int quest)
        {
            op = "";
            quest = 0;
            var parts = (rest ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            op = parts[0].ToLowerInvariant();
            return (op is "start" or "finish" or "forget")
                   && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out quest) && quest > 0;
        }

        private static async Task QuestAsync(NetworkStream stream, string rest, int channel)
        {
            if (!TryParseQuest(rest, out string op, out int quest))
            {
                await NotifyAsync(stream, Usage(".quest"), channel, 0);
                return;
            }
            if (Quests.Book?.Of(quest) == null || SessionContext.State.Quests == null)
            {
                await NotifyAsync(stream, T("quest.unknown", quest), channel, 0);
                return;
            }
            switch (op)
            {
                case "start": await Quests.StartAsync(SessionContext.Current, quest); break;
                case "finish": await QuestAdmin.FinishAsync(stream, quest); break;
                case "forget": await QuestAdmin.ForgetAsync(stream, quest); break;
            }
            await NotifyAsync(stream, T("quest.result", quest, op), channel, 0);
            ActivityJournal.Current.Write("admin.quest", SessionContext.Current.AccountId, GameState.CharacterId,
                new { quest, op });
        }

        private static async Task QuestsAsync(NetworkStream stream, int channel)
        {
            var log = SessionContext.State.Quests;
            if (log == null)
            {
                await NotifyAsync(stream, T("quest.unknown", 0), channel, 0);
                return;
            }
            var doing = log.Runs.Values.Where(r => !r.Finished).Select(r => r.QuestId).OrderBy(q => q).ToList();
            int finished = log.Runs.Values.Count(r => r.Finished);
            await NotifyAsync(stream, T("quests.result", doing.Count, string.Join(", ", doing), finished), channel, 0);
        }

        /// <summary>
        /// The character's sheet is not touched in a fight, the staff's own neither: the fight keeps
        /// its own life and characteristics, as <see cref="RunOnOtherAsync"/> already says for others.
        /// </summary>
        private static async Task<bool> NotInAFightAsync(NetworkStream stream, int channel)
        {
            if (!GameState.IsInFight) return true;
            await NotifyAsync(stream, T("admin.in_fight", GameState.CharacterName ?? ""), channel, 0);
            return false;
        }

        private static async Task EnergyAsync(NetworkStream stream, string rest, int channel)
        {
            if (!await NotInAFightAsync(stream, channel)) return;
            int wanted = DefeatPenalty.MaxEnergy;
            if (rest.Trim().Length > 0 && !int.TryParse(rest.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out wanted))
            {
                await NotifyAsync(stream, Usage(".energy"), channel, 0);
                return;
            }
            Energy.Set(GameState.CharacterId, wanted);
            await CharacteristicsMessages.Live.SendAsync(SessionContext.Current);
            await NotifyAsync(stream, T("energy.result", GameState.CharacterName, Energy.Of(GameState.CharacterId),
                                        DefeatPenalty.MaxEnergy), channel, 0);
        }

        private static async Task HealAsync(NetworkStream stream, int channel)
        {
            if (!await NotInAFightAsync(stream, channel)) return;
            long id = GameState.CharacterId;
            RestingLife.Set(id, 0, DateTime.UtcNow);
            await CharacteristicsMessages.Live.SendAsync(SessionContext.Current);
            await NotifyAsync(stream, T("heal.result", GameState.CharacterName), channel, 0);
        }

        /// <summary>The base characteristics ".stats" sets, by the names the staff types.</summary>
        internal static bool TryParseStat(string word, out string stat)
        {
            stat = (word ?? "").Trim().ToLowerInvariant() switch
            {
                "vit" or "vita" or "vitalidad" or "vitality" or "vitalite" => "vitality",
                "sag" or "sab" or "sabiduria" or "wisdom" or "sagesse" => "wisdom",
                "for" or "fuerza" or "strength" or "force" => "strength",
                "int" or "inteligencia" or "intelligence" => "intelligence",
                "cha" or "suerte" or "chance" => "chance",
                "agi" or "agilidad" or "agility" or "agilite" => "agility",
                _ => "",
            };
            return stat.Length > 0;
        }

        private static async Task StatsAsync(NetworkStream stream, string rest, int channel)
        {
            if (!await NotInAFightAsync(stream, channel)) return;
            var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !TryParseStat(parts[0], out string stat)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
            {
                await NotifyAsync(stream, Usage(".stats"), channel, 0);
                return;
            }
            // The control API's own ceiling: a vitality at int.MaxValue overflows the maximum life.
            value = Math.Clamp(value, 0, 10_000_000);
            var s = SessionContext.State;
            switch (stat)
            {
                case "vitality": s.StatVitality = value; break;
                case "wisdom": s.StatWisdom = value; break;
                case "strength": s.StatStrength = value; break;
                case "intelligence": s.StatIntelligence = value; break;
                case "chance": s.StatChance = value; break;
                case "agility": s.StatAgility = value; break;
            }
            await RefreshSheetAsync(SessionContext.Current);
            await NotifyAsync(stream, T("stats.result", s.CharacterName, stat, value), channel, 0);
        }

        /// <summary>
        /// A group of monsters as typed: "31", "31:2" (monster:grade), up to eight, the grade from 1 to
        /// 6 and one when it is not given.
        /// </summary>
        internal static bool TryParseMonsters(string rest, out List<(int Monster, int Grade)> members)
        {
            members = new List<(int, int)>();
            foreach (string word in (rest ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = word.Split(':');
                if (pair.Length > 2 || !int.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out int monster)
                    || monster <= 0) return false;
                int grade = 1;
                if (pair.Length == 2 && (!int.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out grade)
                                         || grade < 1 || grade > 6)) return false;
                members.Add((monster, grade));
            }
            return members.Count is >= 1 and <= MapAdmin.MaxMembers;
        }

        private static async Task SpawnAsync(NetworkStream stream, string rest, int channel)
        {
            string kind = FirstWord(rest, out string after).ToLowerInvariant();
            long mapId = GameState.MapId;
            int cell = GameState.CellId;
            if (GameState.IsInFight)
            {
                await NotifyAsync(stream, T("admin.in_fight", GameState.CharacterName), channel, 0);
                return;
            }

            if (kind == "npc" && int.TryParse(after.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int npcId) && npcId > 0)
            {
                var spawn = MapAdmin.SpawnNpc(mapId, npcId, cell, GameState.Orientation);
                if (spawn == null)
                {
                    await NotifyAsync(stream, T("spawn.refused", npcId), channel, 0);
                    return;
                }
                ActivityJournal.Current.Write("admin.npc.spawned", SessionContext.Current.AccountId, GameState.CharacterId,
                    new { map = mapId, cell, npc = npcId });
                await MapAdmin.RefreshAsync(mapId);
                await NotifyAsync(stream, T("spawn.npc", npcId, cell), channel, 0);
                return;
            }
            if ((kind == "monster" || kind == "monsters") && TryParseMonsters(after, out var members))
            {
                var group = MapAdmin.SpawnMonsters(mapId, cell, members);
                if (group == null)
                {
                    await NotifyAsync(stream, T("spawn.refused", after), channel, 0);
                    return;
                }
                ActivityJournal.Current.Write("admin.monsters.spawned", SessionContext.Current.AccountId, GameState.CharacterId,
                    new { map = mapId, cell, group = group.MobId, members = members.Select(m => new { m.Monster, m.Grade }) });
                await MapAdmin.RefreshAsync(mapId);
                await NotifyAsync(stream, T("spawn.group", group.MobId, members.Count, cell), channel, 0);
                return;
            }
            await NotifyAsync(stream, Usage(".spawn"), channel, 0);
        }

        private static async Task RemoveAsync(NetworkStream stream, string rest, int channel)
        {
            string kind = FirstWord(rest, out string after).ToLowerInvariant();
            // The ids of what stands on a map are negative (".mapinfo" lists them), hence Integer.
            if (!(kind is "npc" or "group") || !long.TryParse(after.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                await NotifyAsync(stream, Usage(".remove"), channel, 0);
                return;
            }
            long mapId = GameState.MapId;
            bool gone = kind == "npc" ? MapAdmin.RemoveNpc(mapId, id) : MapAdmin.RemoveGroup(mapId, id);
            if (!gone)
            {
                await NotifyAsync(stream, T("remove.none", kind, id), channel, 0);
                return;
            }
            ActivityJournal.Current.Write("admin.removed", SessionContext.Current.AccountId, GameState.CharacterId,
                new { map = mapId, kind, id });
            await MapAdmin.RefreshAsync(mapId);
            await NotifyAsync(stream, T("remove.done", kind, id), channel, 0);
        }

        /// <summary>
        /// ".gift item [quantity] [Name]": a gift on the account, waiting in the shop's gifts window
        /// (the control API's /api/regalo does the same), so it goes to whoever the command ran for.
        /// </summary>
        private static async Task GiftAsync(NetworkStream stream, string rest, int channel)
        {
            var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int quantity = 1;
            if (parts.Length < 1 || parts.Length > 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int gid) || gid <= 0
                || (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity)))
            {
                await NotifyAsync(stream, Usage(".gift"), channel, 0);
                return;
            }
            if (!DatabaseManager.TryGetItemTemplateEffects(gid, out _))
            {
                await NotifyAsync(stream, T("gift.unknown", gid), channel, 0);
                return;
            }
            quantity = Math.Clamp(quantity, 1, 1000);
            var target = SessionContext.Current;
            string language = target.State.Language;
            int id = Gifts.Grant(target.AccountId, gid, quantity, CommandTexts.Get(language, "gift.title"),
                                 CommandTexts.Get(language, "gift.description"));
            if (id == 0)
            {
                await NotifyAsync(stream, T("admin.refused", target.State.CharacterName ?? ""), channel, 0);
                return;
            }
            await GiftHandler.TellAsync(target.AccountId);
            ActivityJournal.Current.Write("admin.gift", SessionContext.Current.AccountId, target.CharacterId,
                new { gift = id, account = target.AccountId, gid, quantity });
            await NotifyAsync(stream, T("gift.sent", gid, quantity, target.State.CharacterName), channel, 0);
        }
    }
}
