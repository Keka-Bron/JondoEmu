using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The guild: creating it, showing it and leaving it. The base the shop, the chest and the raids
    /// will go on.
    ///
    /// Measured in the 12 captures in Gremio/. On creating (jjg) the real server answers with the
    /// guild you now belong to (jgw), its ranks (jco), its header (jhh) and your member sheet (jgu);
    /// on opening the window it repeats jco, jgu and jhh; on leaving (jho) it confirms with khj.
    /// What is not touched yet -- applications, permissions, contributions -- is said so.
    /// </summary>
    public static class GuildHandler
    {
        /// <summary>The guildalogem, the item spent on founding. «Gremialogema» in the client's catalogue.</summary>
        public const int GuildalogemTemplate = 1575;

        /// <summary>
        /// The Guild Temple and its altar, where founding starts.
        /// </summary>
        /// <remarks>
        /// Measured in the capture of founding «Jondo»: the player presses element 480310 of map
        /// 106169344 -- «The Guild Temple», north of the Amakna village, cell 326 in the map's data --,
        /// the server answers iwn with skill 184 and an empty jjc, and the client opens its name and
        /// emblem editor. Without that jjc the editor never opens, which is what led to writing the
        /// command.
        /// </remarks>
        public const long FoundingMap = 106169344;
        public const int FoundingAltar = 480310;
        public const int FoundingSkill = 184;
        public const int FoundingType = -1;

        // The emblem the COMMAND founds with, since it has no editor: the one of the «Jondo» capture.
        // Through the altar the player chooses the emblem and this is not used.
        private const int DefaultEmblemSymbol = 165;
        private const int DefaultEmblemSymbolColor = 8;
        private const int DefaultEmblemBackground = 16744448;
        private const int DefaultEmblemSymbolRgb = 9476018;

        private static async Task WriteAsync(NetworkStream stream, byte[] frame)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>
        /// The temple's altar: it has been pressed, and the founding editor is opened for the player.
        /// </summary>
        /// <remarks>
        /// It opens even if he carries no guildalogem or cannot found: the capture does not show what the
        /// real server does in that case, and what it does show is that the guildalogem is spent in the
        /// jjg, not here. Whoever does not have it will find out on signing, which is where it is checked.
        /// </remarks>
        public static async Task OpenFoundingAsync(NetworkStream stream, int elementId, int skillId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jjc, System.Array.Empty<byte>()));
            Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} opens the founding editor.");
        }

        /// <summary>
        /// Creating a guild (jjg): f1 the emblem {symbol, symbol colour, background, background colour},
        /// f2 the name, as the editor leaves them. It goes through <see cref="FoundAsync"/>.
        /// </summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjg = ConnectionProtocol.ReadPayload(frame, Op.Jjg);
            if (jjg == null) return;

            string name = "";
            int symbol = 0, symbolColor = 0, background = 0, symbolRgb = 0;
            foreach (var field in ProtoMessage.Parse(jjg).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 2)
                {
                    name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                }
                else if (field.FieldNumber == 1 && field.WireType == 2)
                {
                    foreach (var e in ProtoMessage.Parse(field.BytesValue).Fields)
                    {
                        if (e.WireType != 0) continue;
                        if (e.FieldNumber == 1) symbol = (int)e.VarIntValue;
                        else if (e.FieldNumber == 2) symbolColor = (int)e.VarIntValue;
                        else if (e.FieldNumber == 3) background = (int)e.VarIntValue;
                        else if (e.FieldNumber == 5) symbolRgb = (int)e.VarIntValue;
                    }
                }
            }

            string fallo = await FoundAsync(stream, SessionContext.State.CharacterId, name,
                                            symbol, symbolColor, background, symbolRgb);
            if (fallo != null)
            {
                // There is no measured frame to tell the editor no: the capture only has the good case.
                // It is told as an information line, which only they see.
                Console.WriteLine($"[Guild] Founding of «{name}» rejected: {fallo}.");
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(CommandTexts.Get(fallo, name))));
            }
        }

        /// <summary>
        /// Founds through the command, without an editor: the same road with the capture's emblem.
        /// Returns the error message's key, or null if it was created.
        /// </summary>
        public static Task<string> CreateFromCommandAsync(NetworkStream stream, long founderCharacterId, string name)
            => FoundAsync(stream, founderCharacterId, name, DefaultEmblemSymbol, DefaultEmblemSymbolColor,
                          DefaultEmblemBackground, DefaultEmblemSymbolRgb);

        /// <summary>
        /// The founding, a single one for the editor and for the command: everything is checked, the
        /// guildalogem is spent, the guild is stored and the founder is sent his part.
        /// </summary>
        /// <remarks>
        /// What goes out after the jjg, in the capture's order: ium (the guildalogem leaving), jjs, jhq,
        /// jco, jgw, khi, jgu, jhh and a jsn that redraws the founder with the guild's name under his.
        /// The ium is sent by Equipment.TakeAsync, which is what removes the item; the iun of the pods
        /// that follows in the capture is not sent, since the guildalogem weighs nothing here; and the
        /// khi, with its meaningless 97, neither.
        ///
        /// The guildalogem is spent LAST, once everything else has happened: a founding that fails over
        /// the name cannot leave the player without the stone.
        /// </remarks>
        private static async Task<string> FoundAsync(NetworkStream stream, long founderCharacterId, string name,
                                                     int symbol, int symbolColor, int background, int symbolRgb)
        {
            if (founderCharacterId == 0) return "guild.create.nocharacter";
            if (GuildStore.GuildOf(founderCharacterId) != null) return "guild.create.hasguild";

            name = (name ?? "").Trim();
            if (!IsValidGuildName(name)) return "guild.create.invalidname";
            if (GuildStore.ByName(name) != null) return "guild.create.exists";
            if (Equipment.HowMany(GuildalogemTemplate) < 1) return "guild.create.nogem";
            if (!await Equipment.TakeAsync(stream, GuildalogemTemplate, 1)) return "guild.create.nogem";

            var guild = GuildStore.Create(founderCharacterId, name, symbol, symbolColor, background, symbolRgb);

            // jjs, jhq, jco, jgw, khi, jgu, jhh: the capture's order.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jjs, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhq, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, GuildStore.RankOf(founderCharacterId))));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khi, GuildProtocol.BuildGoneNotice()));
            var members = GuildStore.Members(guild.Id);
            await WriteAsync(stream, MembersFrame(members));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count)));
            await RedrawAsync(stream, founderCharacterId);

            Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} founds «{name}» " +
                              "and spends their guildalogem.");
            return null;
        }

        /// <summary>
        /// What is accepted as a guild name.
        /// </summary>
        /// <remarks>
        /// It is NOT measured: the capture only founds «Jondo». Through the altar the name is filtered by
        /// the client's own editor before sending it; this is what is asked of a name typed by hand
        /// through the command, and it is the rule PR #43 brought.
        /// </remarks>
        internal static bool IsValidGuildName(string name)
        {
            if (name.Length < 3 || name.Length > 30) return false;
            foreach (char character in name)
            {
                if (!char.IsLetterOrDigit(character) && character != ' ' &&
                    character != '-' && character != '\'')
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Sends whoever has just joined a guild everything of his: ranks, membership, the member list
        /// and the header.
        /// </summary>
        /// <remarks>
        /// Only on joining: the jgw is "you have just joined", with its chat line and its popup.
        /// It went out too on opening the window and on every tab of it (jml, jii), and that was
        /// the "acabas de unirte al gremio" at every click. The ranks go first, as in the capture
        /// ("jco jgw"): the client's jgw handler looks the rank up among them and does not ask
        /// whether it is there.
        /// </remarks>
        public static async Task SendGuildToOwnerAsync(NetworkStream stream, GuildStore.Guild guild, long characterId)
        {
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, rank)));
            await WriteAsync(stream, MembersFrame(members));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
        }

        /// <summary>
        /// The member list, ONE jgu with every member's name, level and account: the client takes
        /// each jgu as the whole list (<see cref="GuildProtocol.BuildMembers"/>).
        /// </summary>
        public static byte[] MembersFrame(List<GuildStore.Member> members)
        {
            var rows = new List<GuildProtocol.MemberRow>();
            foreach (var member in members)
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character == null) continue;
                rows.Add(new GuildProtocol.MemberRow(member, character.Name, character.Level, character.AccountId,
                                                     character.Breed, Achievements.PointsOf(member.CharacterId),
                                                     GuildStore.ContributedBy(member.CharacterId),
                                                     SessionRegistry.FindByCharacter(member.CharacterId) != null));
            }
            return ConnectionProtocol.Push(Op.Jgu, GuildProtocol.BuildMembers(rows));
        }

        /// <summary>The guild window opening (jlk): the chest's tabs (ivl).</summary>
        /// <remarks>
        /// The window opens with a burst -- jlk, jiy{4}, jii{1}, jfp, jiy, then jiy{1}, jml{1},
        /// jlx{8} -- and the real server answers ivl, jci, jff, jhh, jla, jgu, jmf, in all six
        /// captures of it: ivl the jlk, jci the jiy{4}, jff the jfp, jhh and jla the bare jiy, jgu
        /// the jml and jmf the jlx; the jii is never answered anywhere (26 of 28). The header went
        /// out with the jlk, ahead of the window that shows it, and its experience bar read 0 / 0.
        /// </remarks>
        public static async Task OpenWindowAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivl,
                StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(guild.Id))));
        }

        /// <summary>The window's header (jhh), with the guild as it stands.</summary>
        public static byte[] HeaderFrame(GuildStore.Guild guild)
            => ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, GuildStore.Members(guild.Id).Count));

        /// <summary>
        /// The members (jml {f1: true}): one jgu per member. The jml of the perks tab and of
        /// closing the window come empty, and the capture answers them nothing.
        /// </summary>
        public static async Task MembersAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jml = ConnectionProtocol.ReadPayload(frame, Op.Jml);
            if (jml == null || FieldValue(jml, 1) == 0) return;

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, MembersFrame(GuildStore.Members(guild.Id)));
        }

        /// <summary>
        /// A tab whose contents this server does not keep -- the perks, the raids, the paged
        /// list, the collectors' -- answered as the captures answer it for a guild that has none:
        /// its empty message, on root 3 with the request's id.
        /// </summary>
        public static async Task EmptyTabAsync(NetworkStream stream, byte[] frame, string answer, int field)
            => await WriteAsync(stream, ConnectionProtocol.Answer(answer, GuildProtocol.BuildEmptyTab(field),
                                                                  ConnectionProtocol.RequestId(frame)));

        /// <summary>When the week starts again (jew → jez), on root 3 with the request's id.</summary>
        public static async Task WeeklyResetAsync(NetworkStream stream, byte[] frame)
            => await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jez, GuildProtocol.BuildWeeklyReset(DateTime.UtcNow),
                                                                  ConnectionProtocol.RequestId(frame)));

        /// <summary>The f2 of the jiy that asks for the directory sheet.</summary>
        public const int ProfileTab = 4;

        /// <summary>
        /// A tab of the window (jiy). With f2 = 4 the directory sheet (jci) is answered, which is the
        /// only pair of the opening measured on its own -- twice in the capture of founding «Jondo» --;
        /// without f2, the header (jhh) and the remaining contributions (jla), the two the opening
        /// burst's captures have between the jfp's jff and the jml's jgu.
        /// </summary>
        public static async Task TabAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jiy = ConnectionProtocol.ReadPayload(frame, Op.Jiy);
            if (jiy == null) return;

            long tab = 0;
            foreach (var field in ProtoMessage.Parse(jiy).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) tab = field.VarIntValue;
            }

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            if (tab == ProfileTab)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jci, ProfileOf(guild)));
            }
            else if (tab == 0)
            {
                await WriteAsync(stream, HeaderFrame(guild));
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jla,
                    GuildProtocol.BuildContributionsLeft(GuildStore.ContributionsLeft(who))));
            }
        }

        /// <summary>A guild's directory sheet, with its leader's name in it.</summary>
        private static byte[] ProfileOf(GuildStore.Guild guild)
        {
            var leader = GuildStore.LeaderOf(guild.Id);
            string leaderName = leader == null ? "" : DatabaseManager.GetCharacterById(leader.CharacterId)?.Name ?? "";
            return GuildProtocol.BuildProfile(guild, GuildStore.ProfileOf(guild.Id), leaderName);
        }

        /// <summary>The opening's jfp: the guild's week (jff), its tier or none.</summary>
        /// <remarks>
        /// As an answer, root 3 with the request's id, as in all eight captured: it went out as a
        /// push, and the window waits for its answer before asking for the members.
        /// </remarks>
        public static async Task WeekAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            var now = DateTimeOffset.UtcNow;
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jff,
                GuildProtocol.BuildWeek(GuildActivity.Of(guild.Id, now), GuildActivity.TokenCapOf(guild),
                                        GuildActivity.TokensOf(who, now)),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// Choosing the guild's tier (jet {f1 the tier, f4 the mission preferences}), answered with
        /// the week as it is left (jdb). Only the leader and the ranks with the right "Gestionar la
        /// franja de actividad"; to anybody else the week goes back unchanged.
        /// </summary>
        /// <remarks>
        /// The client builds the jet in one place (ffr, four identical copies): f1 the tier it was
        /// given, f4 the preferences' two lists. The capture of choosing tier 1: "jet {f1 1, f4 {}}"
        /// and back "jdb {f2 {f1 {}, f2 {f1 1, f3 250}, f3 {the missions}}}".
        /// </remarks>
        public static async Task ChooseTierAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jet = ConnectionProtocol.ReadPayload(frame, Op.Jet);
            long who = SessionContext.State.CharacterId;
            var guild = jet == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            int tier = 0;
            byte[] preferences = Array.Empty<byte>();
            foreach (var field in ProtoMessage.Parse(jet).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) tier = (int)field.VarIntValue;
                if (field.FieldNumber == 4 && field.WireType == 2) preferences = field.BytesValue ?? Array.Empty<byte>();
            }

            var now = DateTimeOffset.UtcNow;
            var week = GuildStore.HasRight(who, GuildActivity.ManageActivityRight)
                ? GuildActivity.Choose(guild.Id, tier, preferences, now)
                : null;
            if (week != null)
                Console.WriteLine($"[Guild] «{guild.Name}» chooses activity tier {tier}" +
                                  (week.PendingActivityId != 0 ? " for next week." : "."));
            week ??= GuildActivity.Of(guild.Id, now);
            if (!week.HasActivity) return;

            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jdb,
                GuildProtocol.BuildTierChosen(week, GuildActivity.TokenCapOf(guild), GuildActivity.TokensOf(who, now)),
                ConnectionProtocol.RequestId(frame)));
        }

        // ─── Ranks ─────────────────────────────────────────────────────────────

        /// <summary>Opening rank management (jcs): the jco with the guild's ranks.</summary>
        public static async Task RanksAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await SendRanksAsync(stream, guild);
        }

        private static async Task SendRanksAsync(NetworkStream stream, GuildStore.Guild guild)
            => await WriteAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildRanks(GuildStore.Ranks(guild.Id))));

        /// <summary>Only the leader touches the ranks. Returns his guild, or null if he may not.</summary>
        private static GuildStore.Guild GuildIfLeader(long who)
        {
            if (who == 0 || GuildStore.RankOf(who) != GuildStore.RankLeader) return null;
            return GuildStore.GuildOf(who);
        }

        /// <summary>
        /// Editing a rank (jct): the whole rank as the editor leaves it, and the jco back.
        /// </summary>
        /// <remarks>
        /// Measured twice in «muchas acciones»: renaming rank 1 to «Tesorero» with its f4 empty -- and the
        /// server keeps the icon 116 it had -- and renaming rank 2 to «Test rango».
        /// </remarks>
        public static async Task EditRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jct = ConnectionProtocol.ReadPayload(frame, Op.Jct);
            if (jct == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            var inner = ProtoMessage.Parse(jct).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            if (inner == null) return;

            string name = null;
            byte[] rights = null;
            bool? flag = null;
            int icon = 0, order = -1, id = 0;
            foreach (var field in ProtoMessage.Parse(inner.BytesValue).Fields)
            {
                switch (field.FieldNumber)
                {
                    case 2 when field.WireType == 2: name = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                    case 3 when field.WireType == 2:
                        flag = false;
                        rights = System.Array.Empty<byte>();
                        foreach (var right in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (right.FieldNumber == 1 && right.WireType == 0) flag = right.VarIntValue != 0;
                            else if (right.FieldNumber == 3 && right.WireType == 2) rights = right.BytesValue;
                        }
                        break;
                    case 4 when field.WireType == 2:
                        foreach (var look in ProtoMessage.Parse(field.BytesValue).Fields)
                        {
                            if (look.FieldNumber == 2 && look.WireType == 0) icon = (int)look.VarIntValue;
                            else if (look.FieldNumber == 3 && look.WireType == 0) order = (int)look.VarIntValue;
                        }
                        break;
                    case 5 when field.WireType == 0: id = (int)field.VarIntValue; break;
                }
            }

            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == id);
            if (rank == null) return;

            if (name != null) rank.Name = name;
            if (rights != null) rank.Rights = rights;
            if (flag.HasValue) rank.Flag = flag.Value;
            if (icon != 0) rank.Icon = icon;
            if (order >= 0) rank.Order = order;
            GuildStore.SaveRank(rank);

            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Guild] Rank {id} of «{guild.Name}» edited: «{rank.Name}».");
        }

        /// <summary>A rank's permissions (jck): f1 the list as it is, f2 the rank. The mark stays.</summary>
        public static async Task SetRightsAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jck = ConnectionProtocol.ReadPayload(frame, Op.Jck);
            if (jck == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            byte[] rights = System.Array.Empty<byte>();
            int id = 0;
            foreach (var field in ProtoMessage.Parse(jck).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) rights = field.BytesValue;
                else if (field.FieldNumber == 2 && field.WireType == 0) id = (int)field.VarIntValue;
            }

            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == id);
            if (rank == null) return;
            rank.Rights = rights;
            GuildStore.SaveRank(rank);

            await SendRanksAsync(stream, guild);
        }

        /// <summary>Creating a rank (jcv): f1 the order, f4 the name, f5 the icon.</summary>
        public static async Task CreateRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcv = ConnectionProtocol.ReadPayload(frame, Op.Jcv);
            if (jcv == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            int order = 0, icon = 0;
            string name = "";
            foreach (var field in ProtoMessage.Parse(jcv).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) order = (int)field.VarIntValue;
                else if (field.FieldNumber == 4 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 5 && field.WireType == 0) icon = (int)field.VarIntValue;
            }

            var created = GuildStore.CreateRank(guild.Id, name, icon, order);
            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Guild] Rank {created.Id} «{created.Name}» created in «{guild.Name}».");
        }

        /// <summary>
        /// A member's rank from the members window (jgv): f2 the member, f4 the rank. Read off the client
        /// (GuildMemberRightsUI.OnModifyRank, ffr.beet), which never fills the f3. Measured in
        /// «...-intentar asignar rango a otro»: "10a282acfea8052001", rank 1 to a member who had just
        /// joined, refused with the lqn 740 and its "24" -- a member has to have been in the guild a day.
        /// </summary>
        public static async Task AssignRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jgv = ConnectionProtocol.ReadPayload(frame, Op.Jgv);
            if (jgv == null) return;
            // The legacy contribution field is independent of rank. A contribution-only request
            // must never turn an absent rank into rank zero.
            var fields = ProtoMessage.Parse(jgv).Fields;
            bool hasRank = fields.Exists(f => f.FieldNumber == 4 && f.WireType == 0);
            if (!hasRank || fields.Exists(f => f.FieldNumber == 3 && f.WireType == 0))
                await SetExperienceShareAsync(SessionContext.State.CharacterId, FieldValue(jgv, 2), (int)FieldValue(jgv, 3));
            if (!hasRank) return;
            string error = await SetMemberRankAsync(SessionContext.State.CharacterId, FieldValue(jgv, 2), (int)FieldValue(jgv, 4));
            if (error == "guild.rank.toosoon")
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    Managers.InfoMessages.Warning, Managers.InfoMessages.RankChangeTooSoon,
                    GuildStore.RankChangeMinimumHours.ToString())));
            }
        }

        /// <summary>A member's rank by his name, from the command. Returns the error's key, or null.</summary>
        public static async Task<string> SetMemberRankAsync(long assignerCharacterId, string targetName, int rankId)
        {
            var guild = GuildStore.GuildOf(assignerCharacterId);
            if (guild == null) return "guild.noguild";
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null && string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase))
                    return await SetMemberRankAsync(assignerCharacterId, member.CharacterId, rankId);
            }
            return "guild.kick.notmember";
        }

        /// <summary>
        /// A member's rank, from the window or from the command. Returns the error's key, or null if it
        /// was changed and everybody has the entry brought up to date (jgz).
        /// </summary>
        /// <remarks>
        /// The leader gives any rank; anybody else needs right 26 of the client's data, "Atribuir
        /// rangos inferiores al mío", and only gives ranks below his own to members below him. And a
        /// member who joined less than a day ago keeps his rank, as the real server answered.
        ///
        /// Giving the leader's rank hands the guild over: the guild has one leader, so the one who
        /// was takes the rank the other one had. That swap is NOT measured -- the capture's attempt
        /// was refused before it -- and is the choice that loses nobody's rank.
        /// </remarks>
        public static async Task<string> SetMemberRankAsync(long assignerCharacterId, long targetCharacterId, int rankId)
        {
            var guild = GuildStore.GuildOf(assignerCharacterId);
            if (guild == null) return "guild.noguild";
            var ranks = GuildStore.Ranks(guild.Id);
            var rank = ranks.Find(r => r.Id == rankId);
            if (rank == null) return "guild.rank.norank";

            var members = GuildStore.Members(guild.Id);
            var assigner = members.Find(m => m.CharacterId == assignerCharacterId);
            var target = members.Find(m => m.CharacterId == targetCharacterId);
            if (assigner == null || target == null) return "guild.kick.notmember";
            if (target.CharacterId == assigner.CharacterId) return "guild.rank.self";

            if (assigner.Rank != GuildStore.RankLeader)
            {
                int own = ranks.Find(r => r.Id == assigner.Rank)?.Order ?? int.MaxValue;
                int theirs = ranks.Find(r => r.Id == target.Rank)?.Order ?? int.MaxValue;
                if (!GuildStore.HasRight(assignerCharacterId, GuildStore.AssignRanksRight)
                    || rank.Order <= own || theirs <= own)
                    return "guild.rank.noright";
            }

            long since = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - target.JoinedUtcMs;
            if (since < GuildStore.RankChangeMinimumHours * 3_600_000L) return "guild.rank.toosoon";

            var changed = new List<GuildStore.Member> { GuildStore.SetRank(target.CharacterId, rankId) };
            if (rankId == GuildStore.RankLeader && assigner.Rank == GuildStore.RankLeader)
                changed.Add(GuildStore.SetRank(assigner.CharacterId, target.Rank));

            foreach (var member in changed)
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null)
                    await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(member, character)));
            }
            return null;
        }

        // ─── The note, the log and the directory ────────────────────────────────

        /// <summary>A member's entry brought up to date (jgz), with everything known about him.</summary>
        private static byte[] MemberUpdated(GuildStore.Member member, DatabaseManager.DbCharacter character)
            => GuildProtocol.BuildMemberUpdated(member, character.Name, character.Level, character.AccountId,
                                                character.Breed, Achievements.PointsOf(member.CharacterId),
                                                GuildStore.ContributedBy(member.CharacterId),
                                                SessionRegistry.FindByCharacter(member.CharacterId) != null);

        /// <summary>A member sets his own contribution; the leader can set that of another member.</summary>
        public static async Task<bool> SetExperienceShareAsync(long actor, long target, int percent)
        {
            if (percent < 0 || percent > 90) return false;
            var own = GuildStore.MemberOf(actor);
            var member = GuildStore.MemberOf(target);
            if (own == null || member == null || own.GuildId != member.GuildId) return false;
            if (actor != target && own.Rank != GuildStore.RankLeader) return false;
            if (!GuildStore.SetExperiencePercent(target, percent)) return false;
            await ExperienceChangedAsync(own.GuildId, target);
            return true;
        }

        internal static async Task ExperienceChangedAsync(long guildId, long characterId)
        {
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null || guild.Id != guildId) return;
            await TellEveryoneAsync(guild, HeaderFrame(guild));
            var member = GuildStore.MemberOf(characterId);
            var character = DatabaseManager.GetCharacterById(characterId);
            if (member != null && character != null)
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(member, character)));
        }

        /// <summary>
        /// A line said on the guild channel (2) by a member: to the other members connected. It went
        /// back only to whoever said it. Returns how many heard it.
        /// </summary>
        public static async Task<int> SayAsync(long characterId, byte[] line)
        {
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return 0;
            int heard = 0;
            foreach (var member in GuildStore.Members(guild.Id))
            {
                if (member.CharacterId == characterId) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                await session.SendAsync(line);
                heard++;
            }
            return heard;
        }

        /// <summary>A frame for everybody in the guild who is connected.</summary>
        private static async Task TellEveryoneAsync(GuildStore.Guild guild, byte[] frame)
        {
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(frame);
            }
        }

        /// <summary>
        /// A member's note (jjj): f1 the text, f3 the character. Measured in «muchas acciones»: «hola»
        /// on the leader himself, and back his whole entry with the note and the time in f7.f8.
        /// </summary>
        public static async Task NoteAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjj = ConnectionProtocol.ReadPayload(frame, Op.Jjj);
            if (jjj == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            string note = "";
            long target = 0;
            foreach (var field in ProtoMessage.Parse(jjj).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) note = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 3 && field.WireType == 0) target = field.VarIntValue;
            }

            var member = GuildStore.MemberOf(target);
            if (member == null || member.GuildId != guild.Id) return;
            var character = DatabaseManager.GetCharacterById(target);
            if (character == null) return;

            var updated = GuildStore.SetNote(target, note, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(updated, character)));
        }

        /// <summary>The log (jim → jil).</summary>
        public static async Task LogAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jil, GuildProtocol.BuildLog(GuildStore.LogOf(guild.Id))));
        }

        /// <summary>
        /// Writing the directory sheet (jcc): it is stored as it arrives and the jci is returned with the
        /// time and the leader filled in. Measured in the capture of founding «Jondo» and again in the
        /// contributing one, with the same sheet.
        /// </summary>
        public static async Task SetProfileAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcc = ConnectionProtocol.ReadPayload(frame, Op.Jcc);
            if (jcc == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            var body = ProtoMessage.Parse(jcc).Fields.Find(f => f.FieldNumber == 2 && f.WireType == 2);
            if (body == null) return;

            var profile = new GuildStore.Profile { GuildId = guild.Id, WhenMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            foreach (var field in ProtoMessage.Parse(body.BytesValue).Fields)
            {
                switch (field.FieldNumber)
                {
                    case 2 when field.WireType == 2: profile.Description = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                    case 3 when field.WireType == 0: profile.MinLevel = (int)field.VarIntValue; break;
                    case 4 when field.WireType == 2: profile.Tags = field.BytesValue; break;
                    case 5 when field.WireType == 0: profile.F5 = (int)field.VarIntValue; break;
                    case 6 when field.WireType == 2: profile.F6 = field.BytesValue; break;
                    case 9 when field.WireType == 0: profile.MaxLevel = (int)field.VarIntValue; break;
                    case 13 when field.WireType == 2: profile.Title = System.Text.Encoding.UTF8.GetString(field.BytesValue); break;
                }
            }

            GuildStore.SaveProfile(profile);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jci, ProfileOf(guild)));
            Console.WriteLine($"[Guild] Profile of «{guild.Name}» written: «{profile.Title}».");
        }

        /// <summary>
        /// Searching the directory (jjm): the list of guilds (jiv). The jme that comes before it in the
        /// captures answers the jlu the client sends together with the jjm: see OwnApplicationAsync.
        /// </summary>
        /// <remarks>
        /// The jjm's filters -- levels, activities -- are not applied: with the guilds there are on a
        /// server of this size, the whole list is the useful answer. All those with a written sheet go,
        /// and also those without one, with the empty one.
        /// </remarks>
        public static async Task SearchAsync(NetworkStream stream, byte[] frame)
        {
            var entries = new List<GuildProtocol.DirectoryEntry>();
            foreach (var guild in GuildStore.AllGuilds())
            {
                var leader = GuildStore.LeaderOf(guild.Id);
                var leaderCharacter = leader == null ? null : DatabaseManager.GetCharacterById(leader.CharacterId);
                entries.Add(new GuildProtocol.DirectoryEntry
                {
                    Guild = guild,
                    Profile = GuildStore.ProfileOf(guild.Id),
                    LeaderId = leader?.CharacterId ?? 0,
                    LeaderName = leaderCharacter?.Name ?? "",
                    Members = GuildStore.Members(guild.Id).Count,
                });
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jiv, GuildProtocol.BuildDirectory(entries)));
        }

        /// <summary>
        /// Leaving the guild or expelling from it (jho): f1 the character. One's own id leaves: he is
        /// taken out and it is confirmed with khj, which is how the client empties the window,
        /// measured in «salir de mi gremio». Somebody else's expels him: the members window's kick
        /// popup (GuildMembersUI.OpenKickMemberPopup) sends this same message with the member's id,
        /// and taking every jho for leaving made whoever expelled leave the guild instead.
        /// </summary>
        public static async Task LeaveAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            byte[] jho = ConnectionProtocol.ReadPayload(frame, Op.Jho);
            long target = jho == null ? 0 : FieldValue(jho, 1);
            if (target != 0 && target != who)
            {
                await KickAsync(who, target);
                return;
            }
            await LeaveAsync(stream, who);
        }

        /// <summary>Leaving, whether from the jho or from the command.</summary>
        public static async Task LeaveAsync(NetworkStream stream, long who)
        {
            if (who == 0) return;
            var guild = GuildStore.GuildOf(who);
            if (GuildStore.Leave(who) == null) return;

            await GoneAsync(stream, who);
            if (guild != null) await RefreshEveryoneAsync(guild, who);
            Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} leaves «{guild?.Name}».");
        }

        /// <summary>
        /// What whoever is left without a guild receives, whether he leaves or is thrown out: the capture
        /// «salir de mi gremio» after the jho is khj {f1: 97}, an empty jhc and a jsn that redraws him
        /// without the guild's name under his. It is sent through the session passed, which may not be
        /// the one that spoke: the expelled player is told from the session of whoever expels him.
        /// </summary>
        private static async Task GoneAsync(NetworkStream stream, long characterId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khj, GuildProtocol.BuildGoneNotice()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhc, System.Array.Empty<byte>()));
            await RedrawAsync(stream, characterId);
        }

        /// <summary>
        /// The character drawn again (jsn), through his own stream and for whoever shares his map,
        /// so the guild's name and emblem appear or go under his name.
        /// </summary>
        /// <remarks>
        /// Founding, joining and leaving all end with it in the captures. Joining did not send it,
        /// so whoever accepted an invitation kept being drawn without the guild until the map was
        /// loaded again; and it went to him alone, so the rest of the map never saw the change.
        /// </remarks>
        private static async Task RedrawAsync(NetworkStream stream, long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            var character = DatabaseManager.GetCharacterById(characterId);
            if (session == null || character == null) return;

            byte[] jsn = ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                character, session.State.CellId, session.State.Orientation, session.AccountId));
            if (stream != null) await WriteAsync(stream, jsn);
            else await session.SendAsync(jsn);
            await SessionRegistry.BroadcastToMapAsync(session.MapId, jsn, session.Id);
        }

        /// <summary>
        /// Expelling a member. Returns the error message's key, or null if he is out.
        /// </summary>
        /// <remarks>
        /// What is measured is how each one ends up: the expelled player receives the same as
        /// whoever leaves of his own accord -- khj, jhc and his jsn without a guild --, and the others
        /// get the list and the header brought up to date.
        ///
        /// It takes right 13 of the client's data, "Excluir a un miembro" (the leader has them all),
        /// and nobody expels the leader nor himself: the jho with one's own id is for leaving.
        /// </remarks>
        public static async Task<string> KickAsync(long kickerCharacterId, string targetName)
        {
            var guild = GuildStore.GuildOf(kickerCharacterId);
            if (guild == null) return "guild.noguild";

            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null && string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase))
                    return await KickAsync(kickerCharacterId, member.CharacterId);
            }
            return "guild.kick.notmember";
        }

        /// <summary>Expelling a member by his character, from the jho or from the command.</summary>
        public static async Task<string> KickAsync(long kickerCharacterId, long targetCharacterId)
        {
            var guild = GuildStore.GuildOf(kickerCharacterId);
            if (guild == null) return "guild.noguild";
            if (!GuildStore.HasRight(kickerCharacterId, GuildStore.ExpelRight)) return "guild.kick.noright";

            var target = GuildStore.Members(guild.Id).Find(m => m.CharacterId == targetCharacterId);
            if (target == null) return "guild.kick.notmember";
            if (target.CharacterId == kickerCharacterId) return "guild.kick.self";
            if (target.Rank == GuildStore.RankLeader) return "guild.kick.noright";
            string targetName = DatabaseManager.GetCharacterById(target.CharacterId)?.Name ?? target.CharacterId.ToString();

            GuildStore.Leave(target.CharacterId);

            var kicked = SessionRegistry.FindByCharacter(target.CharacterId);
            if (kicked != null)
            {
                using (SessionContext.Push(kicked))
                {
                    await GoneAsync(kicked.Stream, target.CharacterId);
                }
            }

            await RefreshEveryoneAsync(guild);
            Console.WriteLine($"[Guild] {targetName} expelled from «{guild.Name}».");
            return null;
        }

        /// <summary>
        /// Brings the window up to date for everybody in the guild who is connected: the member list and
        /// the header with how many they are. It is what is needed when somebody joins or leaves.
        /// </summary>
        private static async Task RefreshEveryoneAsync(GuildStore.Guild guild, long exceptCharacter = 0)
        {
            var members = GuildStore.Members(guild.Id);
            byte[] list = MembersFrame(members);
            byte[] header = ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count));

            foreach (var member in members)
            {
                if (member.CharacterId == exceptCharacter) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                await session.SendAsync(list);
                await session.SendAsync(header);
            }
        }

        // ─── The guild shop ─────────────────────────────────────────────────────

        /// <summary>
        /// Opening the shop (jki) and answering with its five oracles (jkh), with the price already
        /// multiplied by the guild's accounts.
        /// </summary>
        public static async Task OpenShopAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkh, GuildProtocol.BuildShop(AccountsIn(guild))));
        }

        /// <summary>
        /// The guild's distinct accounts, which is what the price is multiplied by: four accounts,
        /// prices times four. Two characters of the same account count once.
        /// </summary>
        private static int AccountsIn(GuildStore.Guild guild)
        {
            var cuentas = new HashSet<long>();
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null) cuentas.Add(character.AccountId);
            }
            return cuentas.Count < 1 ? 1 : cuentas.Count;
        }

        /// <summary>
        /// Buying an oracle (jkw): if the guild kamas are enough it is noted and answered with the
        /// acknowledgement, the kamas left and what remains to be activated; if they are not, the EMPTY
        /// acknowledgement, which is what the real server sends when it refuses.
        /// </summary>
        public static async Task BuyOracleAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jkw = ConnectionProtocol.ReadPayload(frame, Op.Jkw);
            if (jkw == null) return;
            int oracle = (int)FieldValue(jkw, 1);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null || GuildOracles.Of(oracle) == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopRefused()));
                return;
            }

            int price = GuildOracles.PriceFor(oracle, AccountsIn(guild));
            if (!GuildStore.SpendGuildKamas(guild.Id, price))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopRefused()));
                return;
            }

            var deadline = DateTimeOffset.UtcNow.AddHours(GuildOracles.HoursToActivate);
            GuildStore.BuyOracle(guild.Id, oracle, deadline);
            long left = GuildStore.GuildOf(who).GuildKamas;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkj, GuildProtocol.BuildShopBought(oracle)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia, GuildProtocol.BuildGuildKamas(left)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkv,
                GuildProtocol.BuildPendingOracle(oracle, GuildStore.OracleDeadline(guild.Id, oracle))));
        }

        /// <summary>
        /// Activating a bought oracle (jky): the acknowledgement and the alteration, which lasts two
        /// hours. Only if the guild has it bought and the deadline has not passed.
        /// </summary>
        public static async Task ActivateOracleAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jky = ConnectionProtocol.ReadPayload(frame, Op.Jky);
            if (jky == null) return;
            int oracle = (int)FieldValue(jky, 1);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            var catalogue = GuildOracles.Of(oracle);
            if (guild == null || catalogue == null) return;

            string deadline = GuildStore.OracleDeadline(guild.Id, oracle);
            if (deadline == null) return;
            if (DateTimeOffset.TryParse(deadline, null, System.Globalization.DateTimeStyles.AdjustToUniversal,
                                        out var cuando) && cuando < DateTimeOffset.UtcNow)
            {
                return;   // the one-day deadline passed
            }

            // The alteration lasts what its own data says -- 2833, 120 minutes, ending on the minute --
            // and is kept with the character's, so it is still listed after a relog or a fight.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkx, GuildProtocol.BuildOracleActivated(oracle)));
            await AlterationHandler.GainAsync(stream, catalogue.Alteration);
        }

        // ─── Contributing ───────────────────────────────────────────────────────

        /// <summary>
        /// Contributing (jlb): ten thousand kamas of the character for ten of the guild, five times a
        /// week at most, and with them ten tokens for him and a hundred activity points for the guild
        /// (<see cref="GuildActivity"/>). It is answered with the contribution made, the guild's
        /// kamas and the character's.
        /// </summary>
        /// <remarks>
        /// Only with a tier chosen, as the client has it: without one its button says to choose a
        /// tier in the missions tab first.
        /// </remarks>
        public static async Task ContributeAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GameState.Kamas < GuildStore.ContributionKamas) return;
            var now = DateTimeOffset.UtcNow;
            if (!GuildActivity.Of(guild.Id, now).HasActivity) return;

            int left = GuildStore.Contribute(who, guild.Id);
            if (left < 0) return;   // he had none left this week

            GameState.Kamas -= GuildStore.ContributionKamas;
            DatabaseManager.SaveCurrentCharacter();
            var credited = GuildActivity.Credit(guild, who, GuildActivity.ContributionTokens,
                                                GuildActivity.ContributionActivityPoints, now);

            // The capture's order: ivf, jgz, (ivj), jia, (iun, khd), jle.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivf,
                ConnectionProtocol.BuildKamas(GameState.Kamas)));
            var me = GuildStore.MemberOf(who);
            var myself = DatabaseManager.GetCharacterById(who);
            if (me != null && myself != null)
            {
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(me, myself)));
            }
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia,
                GuildProtocol.BuildGuildKamas(GuildStore.GuildOf(who).GuildKamas)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jle,
                GuildProtocol.BuildContribution(GuildStore.ContributionKamas, left)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jla, GuildProtocol.BuildContributionsLeft(left)));

            // A milestone reached moves the guild's experience, and maybe its level: everybody's header.
            if (credited.Experience > 0) await TellEveryoneAsync(guild, HeaderFrame(GuildStore.GuildOf(who)));
        }

        // ─── Applications ───────────────────────────────────────────────────────

        /// <summary>What is needed of each applicant to build his block.</summary>
        private static List<(GuildStore.Application, string, int, long, string, string)> Detailed(
            IEnumerable<GuildStore.Application> applications)
        {
            var fuera = new List<(GuildStore.Application, string, int, long, string, string)>();
            foreach (var application in applications)
            {
                var character = DatabaseManager.GetCharacterById(application.CharacterId);
                if (character == null) continue;
                fuera.Add((application, character.Name, character.Level, character.AccountId, character.Name, ""));
            }
            return fuera;
        }

        /// <summary>The applications tab (jlx/jml → jmf).</summary>
        public static async Task ApplicationsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            byte[] jlx = ConnectionProtocol.ReadPayload(frame, Op.Jlx);
            int tab = jlx == null ? ApplicationsTab : (int)FieldValue(jlx, 1);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jmf,
                GuildProtocol.BuildApplications(tab, Detailed(GuildStore.Applications(guild.Id)))));
        }

        /// <summary>The tab the client asks for when opening the applications, measured in the capture.</summary>
        public const int ApplicationsTab = 8;

        /// <summary>
        /// Answering an application (jlt): f1 whether it is accepted -- absent when it is refused --,
        /// f2 the applicant. Read off the client: GuildApplicationsUI.ReplyToApplication(applicant,
        /// accept) sends it. Measured on refusing one, "10a282acfea805", the applicant alone,
        /// answered at once with the jly that takes it off the list. An accepted applicant joins at
        /// rank 4 and is sent his whole guild.
        /// </summary>
        public static async Task AnswerApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jlt = ConnectionProtocol.ReadPayload(frame, Op.Jlt);
            if (jlt == null) return;
            bool accepted = FieldValue(jlt, 1) != 0;
            long applicant = FieldValue(jlt, 2);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null || !GuildStore.HasRight(who, GuildStore.ManageApplicationsRight)) return;

            var application = GuildStore.ApplicationOf(guild.Id, applicant);
            if (application == null) return;
            GuildStore.DropApplication(guild.Id, applicant);
            await ApplicationMovedAsync(guild, application, GuildProtocol.ApplicationGone, who);

            bool joins = accepted && GuildStore.GuildOf(applicant) == null;   // not if he is already in another
            var applicantSession = SessionRegistry.FindByCharacter(applicant);
            if (applicantSession != null)
                await applicantSession.SendAsync(ConnectionProtocol.Push(Op.Jmp,
                    GuildProtocol.BuildApplicationAnswered(guild, joins)));

            if (!joins) return;
            GuildStore.Join(applicant, guild.Id);
            await SendGuildToNewMemberAsync(applicant, guild);
            await RefreshEveryoneAsync(guild, applicant);
        }

        /// <summary>
        /// Withdrawing one's own application (jlv, empty): GuildDirectory.DeleteApplication sends it,
        /// and the client waits for the jmi that says whether it went.
        /// </summary>
        public static async Task WithdrawApplicationAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var applications = who == 0 ? new List<GuildStore.Application>() : GuildStore.ApplicationsBy(who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jmi,
                GuildProtocol.BuildApplicationWithdrawn(applications.Count > 0)));

            foreach (var application in applications)
            {
                GuildStore.DropApplication(application.GuildId, who);
                var guild = GuildStore.ById(application.GuildId);
                if (guild != null) await ApplicationMovedAsync(guild, application, GuildProtocol.ApplicationGone, who);
            }
        }

        /// <summary>
        /// Applying from the guild directory (jlw): f11 the text, f13 the guild. Read off the client
        /// (GuildApplyUi.SendSubmitApplicationMessage, ffr.beej); no capture has one. The other text
        /// fields are the directory's filters written out, and are not needed. One application at a
        /// time: a pending one to another guild is taken back first.
        /// </summary>
        public static async Task ApplyFromClientAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jlw = ConnectionProtocol.ReadPayload(frame, Op.Jlw);
            if (jlw == null) return;
            var guild = GuildStore.ById(FieldValue(jlw, 13));
            long who = SessionContext.State.CharacterId;
            if (guild == null || who == 0) return;
            await ApplyAsync(who, guild, TextField(jlw, 11));
        }

        /// <summary>
        /// Rewriting one's own application (jmo): f2 the guild, f3 the text. The client asks for its
        /// application (jlu) right behind it, and that is what shows the new text.
        /// </summary>
        public static async Task RewriteApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jmo = ConnectionProtocol.ReadPayload(frame, Op.Jmo);
            if (jmo == null) return;
            long who = SessionContext.State.CharacterId;
            var guild = GuildStore.ById(FieldValue(jmo, 2));
            if (guild == null || who == 0 || GuildStore.ApplicationOf(guild.Id, who) == null) return;

            var application = GuildStore.Apply(who, guild.Id, Trimmed(TextField(jmo, 3)));
            await ApplicationMovedAsync(guild, application, GuildProtocol.ApplicationRewritten, who);
        }

        /// <summary>One's own pending application (jlu → jme), or an empty jme when there is none.</summary>
        public static async Task OwnApplicationAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jme, OwnApplicationOf(who)));
        }

        private static byte[] OwnApplicationOf(long characterId)
        {
            var application = characterId == 0 ? null : GuildStore.ApplicationsBy(characterId).LastOrDefault();
            var guild = application == null ? null : GuildStore.ById(application.GuildId);
            var detailed = guild == null ? null : Detailed(new[] { application });
            if (detailed == null || detailed.Count == 0) return System.Array.Empty<byte>();
            var (one, name, level, accountId, nick, tag) = detailed[0];
            return GuildProtocol.BuildOwnApplication(guild, one, name, level, accountId, nick, tag);
        }

        /// <summary>The longest text the client lets an application have.</summary>
        private const int ApplicationTextMax = 200;

        private static string Trimmed(string text)
            => (text ?? "").Length > ApplicationTextMax ? text.Substring(0, ApplicationTextMax) : text ?? "";

        /// <summary>The jly that moves an application on the list of whoever manages them.</summary>
        private static async Task ApplicationMovedAsync(GuildStore.Guild guild, GuildStore.Application application,
                                                        int move, long by)
        {
            var detailed = Detailed(new[] { application });
            if (detailed.Count == 0) return;
            var (one, name, level, accountId, nick, tag) = detailed[0];
            byte[] frame = ConnectionProtocol.Push(Op.Jly,
                GuildProtocol.BuildApplicationMoved(move, one, name, level, accountId, nick, tag, by));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                if (!GuildStore.HasRight(member.CharacterId, GuildStore.ManageApplicationsRight)) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(frame);
            }
        }

        // ─── Invitations ────────────────────────────────────────────────────────

        /// <summary>
        /// Inviting somebody from the client (jjn): f2 the character. A player's menu sends it
        /// (PlayerMenuMaker.OnGuildInvite), and it was taken for accepting an application, which is
        /// the jlt. Measured in «...-rechazarla-invitar a gremio»: the jjn "10a282acfea805" and a jin
        /// at 1 back at once.
        /// </summary>
        public static async Task InviteFromClientAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjn = ConnectionProtocol.ReadPayload(frame, Op.Jjn);
            if (jjn == null) return;
            long target = FieldValue(jjn, 2);
            long who = SessionContext.State.CharacterId;
            if (who == 0 || target == 0 || target == who) return;

            var session = SessionRegistry.FindByCharacter(target);
            if (session == null) return;
            if (await InviteAsync(who, session) != null) return;

            var invited = DatabaseManager.GetCharacterById(target);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jin,
                GuildProtocol.BuildApplicationStatus(invited?.Name ?? "", target, GuildProtocol.StatusInvited)));
        }

        /// <summary>Inviting somebody by name, from the chat command.</summary>
        public static async Task<string> InviteAsync(long inviterCharacterId, string targetName)
        {
            var session = SessionRegistry.FindByName(targetName);
            if (session == null) return "guild.invite.notfound";
            return await InviteAsync(inviterCharacterId, session);
        }

        /// <summary>
        /// The invitation itself: the invited player gets the jiq with the guild and the inviter's
        /// name, and he answers with the jiz.
        /// </summary>
        private static async Task<string> InviteAsync(long inviterCharacterId, GameSession session)
        {
            var guild = GuildStore.GuildOf(inviterCharacterId);
            if (guild == null) return "guild.invite.noguild";
            if (!GuildStore.HasRight(inviterCharacterId, GuildStore.ManageApplicationsRight)) return "guild.invite.noright";
            if (GuildStore.GuildOf(session.CharacterId) != null) return "guild.invite.hasguild";

            var inviter = DatabaseManager.GetCharacterById(inviterCharacterId);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jiq,
                GuildProtocol.BuildInvitation(guild, inviter?.Name ?? "")));
            lock (_invitations) _invitations[session.CharacterId] = (guild.Id, inviterCharacterId);
            return null;
        }

        /// <summary>Which guild each character has been invited to, and by whom, while he does not answer.</summary>
        private static readonly Dictionary<long, (long Guild, long Inviter)> _invitations = new();

        /// <summary>
        /// The answer to the invitation (jiz): empty refuses it, f1 = 1 accepts it. Measured in the
        /// capture of receiving it, where on accepting the whole guild and the jij arrive; and in the
        /// inviter's, the lqn 1423 and the jin at 3.
        /// </summary>
        public static async Task AnswerInvitationAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            (long Guild, long Inviter) invitation;
            lock (_invitations)
            {
                if (who == 0 || !_invitations.Remove(who, out invitation)) return;
            }

            byte[] jiz = ConnectionProtocol.ReadPayload(frame, Op.Jiz);
            bool accepts = jiz != null && FieldValue(jiz, 1) == 1;
            if (!accepts) return;
            if (GuildStore.GuildOf(who) != null) return;

            GuildStore.Join(who, invitation.Guild);
            var guild = GuildStore.GuildOf(who);
            if (guild == null) return;

            // The capture's order: the guild, the jsn that draws him with it, then the jij.
            await SendGuildToOwnerAsync(stream, guild, who);
            await RedrawAsync(stream, who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
            await RefreshEveryoneAsync(guild, who);

            var inviter = SessionRegistry.FindByCharacter(invitation.Inviter);
            if (inviter != null)
            {
                string name = SessionContext.State.CharacterName;
                await inviter.SendAsync(ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.GuildMemberJoined, name)));
                await inviter.SendAsync(ConnectionProtocol.Push(Op.Jin,
                    GuildProtocol.BuildApplicationStatus(name, who, GuildProtocol.StatusJoined)));
            }
        }

        /// <summary>Sends the whole guild to whoever has just joined, if he is connected.</summary>
        private static async Task SendGuildToNewMemberAsync(long characterId, GuildStore.Guild guild)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);

            await session.SendAsync(ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
            await session.SendAsync(ConnectionProtocol.Push(Op.Jgw, GuildProtocol.BuildGuildJoined(guild, rank)));
            await session.SendAsync(MembersFrame(members));
            await session.SendAsync(ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
            await RedrawAsync(null, characterId);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
        }

        /// <summary>Sending an application to a guild by its name, from the chat command.</summary>
        public static async Task<string> ApplyAsync(long characterId, string guildName, string message)
        {
            var guild = GuildStore.ByName(guildName);
            if (guild == null) return "guild.apply.notfound";
            return await ApplyAsync(characterId, guild, message);
        }

        /// <summary>
        /// Sending an application, from the client's directory or from the command: the guild's
        /// members get the notice (jma, measured), whoever manages the applications sees it come
        /// into the list (jly), and the applicant his pending application (jme).
        /// </summary>
        private static async Task<string> ApplyAsync(long characterId, GuildStore.Guild guild, string message)
        {
            if (GuildStore.GuildOf(characterId) != null) return "guild.apply.hasguild";

            foreach (var earlier in GuildStore.ApplicationsBy(characterId))
            {
                if (earlier.GuildId == guild.Id) continue;
                GuildStore.DropApplication(earlier.GuildId, characterId);
                var other = GuildStore.ById(earlier.GuildId);
                if (other != null) await ApplicationMovedAsync(other, earlier, GuildProtocol.ApplicationGone, characterId);
            }

            var character = DatabaseManager.GetCharacterById(characterId);
            var application = GuildStore.Apply(characterId, guild.Id, Trimmed(message));

            byte[] aviso = ConnectionProtocol.Push(Op.Jma,
                GuildProtocol.BuildApplicationArrived(characterId, character?.Name ?? ""));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(aviso);
            }
            await ApplicationMovedAsync(guild, application, GuildProtocol.ApplicationNew, characterId);

            var applicant = SessionRegistry.FindByCharacter(characterId);
            if (applicant != null) await applicant.SendAsync(ConnectionProtocol.Push(Op.Jme, OwnApplicationOf(characterId)));
            return null;
        }

        /// <summary>A field's text, or empty.</summary>
        private static string TextField(byte[] payload, int number)
        {
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 2) return System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }
            return "";
        }

        /// <summary>A field's first varint, or zero.</summary>
        private static long FieldValue(byte[] payload, int number)
        {
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
        }
    }
}
