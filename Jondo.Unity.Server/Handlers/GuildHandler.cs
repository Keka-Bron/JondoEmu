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
            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} abre el editor de fundación.");
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
                Console.WriteLine($"[Gremio] Fundación de «{name}» rechazada: {fallo}.");
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
            foreach (var frame in MemberFrames(members)) await WriteAsync(stream, frame);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count)));

            var character = DatabaseManager.GetCharacterById(founderCharacterId);
            if (character != null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, SessionContext.State.CellId, SessionContext.State.Orientation,
                    SessionContext.Current.AccountId)));
            }

            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} funda «{name}» " +
                              "y gasta su gremialogema.");
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
            foreach (var frame in MemberFrames(members))
                await WriteAsync(stream, frame);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
        }

        /// <summary>One jgu frame per member, with each one's name, level and account.</summary>
        public static List<byte[]> MemberFrames(List<GuildStore.Member> members)
        {
            var fuera = new List<byte[]>();
            foreach (var member in members)
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character == null) continue;
                fuera.Add(ConnectionProtocol.Push(Op.Jgu,
                    GuildProtocol.BuildMember(member, character.Name, character.Level, character.AccountId,
                                              character.Breed, Achievements.PointsOf(member.CharacterId),
                                              GuildStore.ContributedBy(member.CharacterId),
                                              SessionRegistry.FindByCharacter(member.CharacterId) != null)));
            }
            return fuera;
        }

        /// <summary>
        /// The guild window opening (jlk): the chest's tabs (ivl) and the window's header (jhh).
        /// </summary>
        /// <remarks>
        /// The window opens with a burst -- jlk, jiy{4}, jii{1}, jfp, jiy, then jiy{1}, jml{1},
        /// jlx{8} -- and the real server answers ivl, jci, jff, jhh, jla, jgu, jmf, in all six
        /// captures of it. In the invitation's one the client waits for the first half's answers
        /// before sending the second, and the header comes with the first: the jlk's, since the
        /// jii is never answered anywhere (26 of 28).
        /// </remarks>
        public static async Task OpenWindowAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivl,
                StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(guild.Id))));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, GuildStore.Members(guild.Id).Count)));
        }

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
            foreach (var member in MemberFrames(GuildStore.Members(guild.Id))) await WriteAsync(stream, member);
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
        /// without f2, the remaining contributions (jla), which is what takes its place in the opening
        /// burst: five requests, five answers, and that is the one left.
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

        /// <summary>The opening's jfp: it is answered with a new guild's jff.</summary>
        /// <remarks>
        /// As an answer, root 3 with the request's id, as in all eight captured: it went out as a
        /// push, and the window waits for its answer before asking for the members.
        /// </remarks>
        public static async Task BenefitsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            if (who == 0 || GuildStore.GuildOf(who) == null) return;
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jff, GuildProtocol.BuildNoBenefits(),
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
            Console.WriteLine($"[Gremio] Rango {id} de «{guild.Name}» editado: «{rank.Name}».");
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
            Console.WriteLine($"[Gremio] Rango {created.Id} «{created.Name}» creado en «{guild.Name}».");
        }

        /// <summary>
        /// A member's rank, through the command: there is no capture of the request the client changes
        /// it with. Returns the error's key, or null if it was changed.
        /// </summary>
        public static async Task<string> SetMemberRankAsync(long leaderCharacterId, string targetName, int rankId)
        {
            var guild = GuildStore.GuildOf(leaderCharacterId);
            if (guild == null) return "guild.noguild";
            if (GuildStore.RankOf(leaderCharacterId) != GuildStore.RankLeader) return "guild.rank.notleader";
            if (GuildStore.Ranks(guild.Id).Find(r => r.Id == rankId) == null) return "guild.rank.norank";

            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character == null || !string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (member.CharacterId == leaderCharacterId) return "guild.rank.self";

                var updated = GuildStore.SetRank(member.CharacterId, rankId);
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(updated, character)));
                return null;
            }

            return "guild.kick.notmember";
        }

        // ─── The note, the log and the directory ────────────────────────────────

        /// <summary>A member's entry brought up to date (jgz), with everything known about him.</summary>
        private static byte[] MemberUpdated(GuildStore.Member member, DatabaseManager.DbCharacter character)
            => GuildProtocol.BuildMemberUpdated(member, character.Name, character.Level, character.AccountId,
                                                character.Breed, Achievements.PointsOf(member.CharacterId),
                                                GuildStore.ContributedBy(member.CharacterId),
                                                SessionRegistry.FindByCharacter(member.CharacterId) != null);

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
            Console.WriteLine($"[Gremio] Ficha de «{guild.Name}» escrita: «{profile.Title}».");
        }

        /// <summary>
        /// Searching the directory (jjm): the empty acknowledgement (jme) and the list of guilds (jiv).
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

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jme, System.Array.Empty<byte>()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jiv, GuildProtocol.BuildDirectory(entries)));
        }

        /// <summary>
        /// Leaving the guild (jho): f1 the character. He is taken out and it is confirmed with khj,
        /// which is how the client empties the window. Measured in «salir de mi gremio».
        /// </summary>
        public static Task LeaveAsync(NetworkStream stream, byte[] frame)
            => LeaveAsync(stream, SessionContext.State.CharacterId);

        /// <summary>Leaving, whether from the jho or from the command.</summary>
        public static async Task LeaveAsync(NetworkStream stream, long who)
        {
            if (who == 0) return;
            var guild = GuildStore.GuildOf(who);
            if (GuildStore.Leave(who) == null) return;

            await GoneAsync(stream, who);
            if (guild != null) await RefreshEveryoneAsync(guild, who);
            Console.WriteLine($"[Gremio] {SessionContext.State.CharacterName} deja «{guild?.Name}».");
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

            var session = SessionRegistry.FindByCharacter(characterId);
            var character = DatabaseManager.GetCharacterById(characterId);
            if (session != null && character != null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                    character, session.State.CellId, session.State.Orientation, session.AccountId)));
            }
        }

        /// <summary>
        /// Expelling a member. Returns the error message's key, or null if he is out.
        /// </summary>
        /// <remarks>
        /// Without a capture: the request the client expels somebody with is in none, so it is done
        /// through the command. What IS measured is how each one ends up: the expelled player receives
        /// the same as whoever leaves of his own accord -- khj, jhc and his jsn without a guild --, and
        /// the others get the list and the header brought up to date.
        ///
        /// Only the leader expels, and not himself: the jho is there for leaving.
        /// </remarks>
        public static async Task<string> KickAsync(long kickerCharacterId, string targetName)
        {
            var guild = GuildStore.GuildOf(kickerCharacterId);
            if (guild == null) return "guild.noguild";
            if (GuildStore.RankOf(kickerCharacterId) != GuildStore.RankLeader) return "guild.kick.notleader";

            GuildStore.Member target = null;
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var character = DatabaseManager.GetCharacterById(member.CharacterId);
                if (character != null && string.Equals(character.Name, targetName, System.StringComparison.OrdinalIgnoreCase))
                {
                    target = member;
                    break;
                }
            }

            if (target == null) return "guild.kick.notmember";
            if (target.CharacterId == kickerCharacterId) return "guild.kick.self";

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
            Console.WriteLine($"[Gremio] {targetName} expulsado de «{guild.Name}».");
            return null;
        }

        /// <summary>
        /// Brings the window up to date for everybody in the guild who is connected: the member list and
        /// the header with how many they are. It is what is needed when somebody joins or leaves.
        /// </summary>
        private static async Task RefreshEveryoneAsync(GuildStore.Guild guild, long exceptCharacter = 0)
        {
            var members = GuildStore.Members(guild.Id);
            var frames = MemberFrames(members);
            byte[] header = ConnectionProtocol.Push(Op.Jhh, GuildProtocol.BuildGuildInfo(guild, members.Count));

            foreach (var member in members)
            {
                if (member.CharacterId == exceptCharacter) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                foreach (var frame in frames) await session.SendAsync(frame);
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

            long from = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long to = DateTimeOffset.UtcNow.AddHours(GuildOracles.HoursActive).ToUnixTimeMilliseconds();

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jkx, GuildProtocol.BuildOracleActivated(oracle)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzs,
                GuildProtocol.BuildAlteration(catalogue.Alteration, from, to)));
        }

        // ─── Contribuir ─────────────────────────────────────────────────────────

        /// <summary>
        /// Contributing (jlb): ten thousand kamas of the character for ten of the guild, five times a
        /// week at most. It is answered with the contribution made, the guild's kamas and the
        /// character's.
        /// </summary>
        public static async Task ContributeAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GameState.Kamas < GuildStore.ContributionKamas) return;

            int left = GuildStore.Contribute(who, guild.Id);
            if (left < 0) return;   // he had none left this week

            GameState.Kamas -= GuildStore.ContributionKamas;
            DatabaseManager.SaveCurrentCharacter();

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
        }

        // ─── Candidaturas ───────────────────────────────────────────────────────

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

        /// <summary>Viewing an application (jlt → jly).</summary>
        public static async Task ApplicationDetailAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jlt = ConnectionProtocol.ReadPayload(frame, Op.Jlt);
            if (jlt == null) return;
            long applicant = FieldValue(jlt, 2);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            var application = GuildStore.ApplicationOf(guild.Id, applicant);
            if (application == null) return;
            var detailed = Detailed(new[] { application });
            if (detailed.Count == 0) return;
            var (one, name, level, accountId, nick, tag) = detailed[0];

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jly,
                GuildProtocol.BuildApplication(one, name, level, accountId, nick, tag, who)));
        }

        /// <summary>
        /// Accepting an application (jjn): the applicant joins at rank 4 and whoever accepts it gets the
        /// two notices that come out in the capture, the accepted one and the one that he is already
        /// in. The one joining is sent his whole guild.
        /// </summary>
        public static async Task AcceptApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjn = ConnectionProtocol.ReadPayload(frame, Op.Jjn);
            if (jjn == null) return;
            long applicant = FieldValue(jjn, 2);

            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GuildStore.ApplicationOf(guild.Id, applicant) == null) return;
            if (GuildStore.GuildOf(applicant) != null) return;   // he is already in another

            var character = DatabaseManager.GetCharacterById(applicant);
            if (character == null) return;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jin,
                GuildProtocol.BuildApplicationStatus(character.Name, applicant, GuildProtocol.StatusAccepted)));

            GuildStore.Join(applicant, guild.Id);

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jin,
                GuildProtocol.BuildApplicationStatus(character.Name, applicant, GuildProtocol.StatusJoined)));

            await SendGuildToNewMemberAsync(applicant, guild);
            await RefreshEveryoneAsync(guild, applicant);
        }

        // ─── Invitaciones ───────────────────────────────────────────────────────

        /// <summary>
        /// Inviting somebody. The client's button appears in no capture -- the ones there are come from
        /// the receiving side --, so for now it is fired from the chat command and what travels is what
        /// was measured: the invited player gets the jiq with the guild and the inviter's name, and he
        /// answers with the jiz.
        /// </summary>
        public static async Task<string> InviteAsync(long inviterCharacterId, string targetName)
        {
            var guild = GuildStore.GuildOf(inviterCharacterId);
            if (guild == null) return "guild.invite.noguild";

            var session = SessionRegistry.FindByName(targetName);
            if (session == null) return "guild.invite.notfound";
            if (GuildStore.GuildOf(session.CharacterId) != null) return "guild.invite.hasguild";

            var inviter = DatabaseManager.GetCharacterById(inviterCharacterId);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jiq,
                GuildProtocol.BuildInvitation(guild, inviter?.Name ?? "")));
            _invitations[session.CharacterId] = guild.Id;
            return null;
        }

        /// <summary>Which guild each character has been invited to, while he does not answer.</summary>
        private static readonly Dictionary<long, long> _invitations = new();

        /// <summary>
        /// The answer to the invitation (jiz): empty refuses it, f1 = 1 accepts it. Measured in the
        /// capture of receiving it, where on accepting the whole guild and the jij arrive.
        /// </summary>
        public static async Task AnswerInvitationAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            if (who == 0 || !_invitations.TryGetValue(who, out long guildId)) return;

            byte[] jiz = ConnectionProtocol.ReadPayload(frame, Op.Jiz);
            bool accepts = jiz != null && FieldValue(jiz, 1) == 1;
            _invitations.Remove(who);
            if (!accepts) return;
            if (GuildStore.GuildOf(who) != null) return;

            GuildStore.Join(who, guildId);
            var guild = GuildStore.GuildOf(who);
            if (guild == null) return;

            await SendGuildToOwnerAsync(stream, guild, who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
            await RefreshEveryoneAsync(guild, who);
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
            foreach (var frame in MemberFrames(members)) await session.SendAsync(frame);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jhh,
                GuildProtocol.BuildGuildInfo(guild, members.Count)));
            await session.SendAsync(ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
        }

        /// <summary>
        /// Sending an application to a guild. As with the invitation, the client's button is not
        /// measured: it is sent from the chat command, and what does travel measured is the notice to
        /// the guild (jma).
        /// </summary>
        public static async Task<string> ApplyAsync(long characterId, string guildName, string message)
        {
            if (GuildStore.GuildOf(characterId) != null) return "guild.apply.hasguild";
            var guild = GuildStore.ByName(guildName);
            if (guild == null) return "guild.apply.notfound";

            var character = DatabaseManager.GetCharacterById(characterId);
            GuildStore.Apply(characterId, guild.Id, message ?? "");

            byte[] aviso = ConnectionProtocol.Push(Op.Jma,
                GuildProtocol.BuildApplicationArrived(characterId, character?.Name ?? ""));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(aviso);
            }
            return null;
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
