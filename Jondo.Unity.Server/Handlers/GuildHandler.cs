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

            // The editor's «both» mode of a potion sends the founding's own jjg (fgc builds it for
            // dml as for dln): with a potion in use it is the guild's new name and emblem.
            if (_potions.ContainsKey(SessionContext.State.CharacterId))
            {
                var (newName, newEmblem) = ReadNameAndEmblem(jjg);
                await ModifyAsync(stream, newName, newEmblem);
                return;
            }

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
                // The editor is told no in the capture's own jhq, its f1 the reason: the client's
                // guild frame (fft) words each with its own text and leaves the editor open.
                Console.WriteLine($"[Guild] Founding of «{name}» rejected: {fallo}.");
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhq, BuildResult(fallo switch
                {
                    "guild.create.invalidname" => ResultInvalidName,
                    "guild.create.hasguild" => ResultAlreadyInGuild,
                    "guild.create.exists" => ResultNameUsed,
                    "guild.create.nogem" => ResultRequirementUnmet,
                    _ => ResultUnknownFail,
                })));
            }
        }

        // ─── Name and emblem, with a potion ─────────────────────────────────────

        /// <summary>«Pócima de cambio de nombre de gremio» and «…de emblema de gremio», sold by Gremia.</summary>
        public const int RenamePotionTemplate = 13273;
        public const int ReEmblemPotionTemplate = 13270;

        /// <summary>
        /// The potions' effects: «Cambiar el nombre del gremio» (and 725, its moderation twin on
        /// 9903), «Cambiar el emblema del gremio».
        /// </summary>
        public const int RenameEffect = 1107;
        public const int RenameByModerationEffect = 725;
        public const int ReEmblemEffect = 1108;

        /// <summary>«Tienes que pertenecer a un gremio para aprovechar esta posibilidad.» / «No dispones de los derechos de gremio requeridos.»</summary>
        public const int NeedsGuild = 135;
        public const int NoGuildRights = 352;

        /// <summary>«El emblema de tu gremio ha sido modificado.» / «El nombre de tu gremio ha sido modificado.»</summary>
        public const int EmblemChanged = 382;
        public const int NameChanged = 383;

        /// <summary>
        /// The founding's and the modification's result (jhq f1), read off the client's switch on
        /// it (fft, its jump table): 0 done, 1 «ui.guild.invalidName», 2 «ui.guild.alreadyInGuild»,
        /// 3 «ui.guild.alreadyUsedName», 6 «ui.social.requirementUnmet», 7
        /// «ui.guild.emblemAlreadyExists», 10 «ui.common.unknownFail», 11 «ui.social.guildOnCooldown».
        /// </summary>
        public const int ResultOk = 0, ResultInvalidName = 1, ResultAlreadyInGuild = 2, ResultNameUsed = 3,
                         ResultRequirementUnmet = 6, ResultUnknownFail = 10;

        public static byte[] BuildResult(int result) => Pb.New().VarIfNotZero(1, result).Build();

        /// <summary>The potion the leader is changing his guild with, until the editor answers.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long Uid, bool Name, bool Emblem)> _potions = new();

        /// <summary>
        /// Using a potion of the guild's (13273, 13270, 9903 the moderation's): the leader -- its
        /// «GM=1» -- gets the founding editor in its mode, jje { 1 the emblem, 3 the name }, read
        /// off the client's guild frame: fft hands f3 and f1 to SocialGroupCreatorUi as its
        /// OpenSocialGroupCreator(modifyName, modifyEmblem). The potion goes only once the change
        /// is made. False if the item is not one of them.
        /// </summary>
        public static async Task<bool> TryUsePotionAsync(NetworkStream stream, Equipment.Item item)
        {
            bool name = false, emblem = false;
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template)))
            {
                if (effect.EffectId is RenameEffect or RenameByModerationEffect) name = true;
                else if (effect.EffectId == ReEmblemEffect) emblem = true;
            }
            if (!name && !emblem) return false;

            long who = SessionContext.State.CharacterId;
            if (GuildStore.GuildOf(who) == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NeedsGuild)));
                return true;
            }
            if (GuildStore.RankOf(who) != GuildStore.RankLeader)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NoGuildRights)));
                return true;
            }

            _potions[who] = (item.Uid, name, emblem);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jje,
                Pb.New().VarIfNotZero(1, emblem ? 1 : 0).VarIfNotZero(3, name ? 1 : 0).Build()));
            return true;
        }

        /// <summary>The new name (jhv): f1 the name.</summary>
        public static async Task RenameAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jhv = ConnectionProtocol.ReadPayload(frame, Op.Jhv);
            if (jhv == null) return;
            var field = ProtoMessage.Parse(jhv).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            await ModifyAsync(stream, field == null ? "" : System.Text.Encoding.UTF8.GetString(field.BytesValue), null);
        }

        /// <summary>The new emblem (jhz): f1 the lld, as the founding's jjg carries it.</summary>
        public static async Task ReEmblemAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jhz = ConnectionProtocol.ReadPayload(frame, Op.Jhz);
            if (jhz == null) return;
            var lld = ProtoMessage.Parse(jhz).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            await ModifyAsync(stream, null, ReadEmblem(lld?.BytesValue ?? System.Array.Empty<byte>()));
        }

        private static (string Name, int[] Emblem) ReadNameAndEmblem(byte[] jjg)
        {
            string name = "";
            int[] emblem = new int[4];
            foreach (var field in ProtoMessage.Parse(jjg).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 1 && field.WireType == 2) emblem = ReadEmblem(field.BytesValue);
            }
            return (name, emblem);
        }

        /// <summary>The lld's symbol (f1), symbol colour (f2), background (f3) and symbol RGB (f5).</summary>
        private static int[] ReadEmblem(byte[] lld)
        {
            var emblem = new int[4];
            foreach (var e in ProtoMessage.Parse(lld).Fields)
            {
                if (e.WireType != 0) continue;
                if (e.FieldNumber == 1) emblem[0] = (int)e.VarIntValue;
                else if (e.FieldNumber == 2) emblem[1] = (int)e.VarIntValue;
                else if (e.FieldNumber == 3) emblem[2] = (int)e.VarIntValue;
                else if (e.FieldNumber == 5) emblem[3] = (int)e.VarIntValue;
            }
            return emblem;
        }

        /// <summary>
        /// The modification, checked as founding is and against the potion in use: a name needs the
        /// name potion, an emblem the emblem one. Done, the potion goes, the editor hears success
        /// (jhq, which closes it), and every member connected gets the guild anew -- belonging, the
        /// header, his character redrawn -- with «El nombre / El emblema de tu gremio ha sido modificado».
        /// </summary>
        private static async Task ModifyAsync(NetworkStream stream, string name, int[] emblem)
        {
            long who = SessionContext.State.CharacterId;
            var guild = GuildStore.GuildOf(who);
            if (guild == null || !_potions.TryGetValue(who, out var potion)) return;

            int result = ResultOk;
            if (name != null)
            {
                name = name.Trim();
                var byName = GuildStore.ByName(name);
                if (!potion.Name || GuildStore.RankOf(who) != GuildStore.RankLeader) result = ResultRequirementUnmet;
                else if (!IsValidGuildName(name)) result = ResultInvalidName;
                else if (byName != null && byName.Id != guild.Id) result = ResultNameUsed;
            }
            if (result == ResultOk && emblem != null && !potion.Emblem) result = ResultRequirementUnmet;
            if (result == ResultOk && !await ItemUseHandler.SpendOneAsync(stream, potion.Uid)) result = ResultRequirementUnmet;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jhq, BuildResult(result)));
            if (result != ResultOk) return;

            _potions.TryRemove(who, out _);
            if (name != null) guild = GuildStore.Rename(guild.Id, name);
            if (emblem != null) guild = GuildStore.SetEmblem(guild.Id, emblem[0], emblem[1], emblem[2], emblem[3]);

            var members = GuildStore.Members(guild.Id);
            var header = GuildStateService.Live.Header(guild, members.Count);
            byte[] said = ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(name != null ? NameChanged : EmblemChanged));
            foreach (var member in members)
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session?.Stream == null) continue;
                await session.SendAsync(GuildStateService.Live.Membership(guild, member.Rank, member.CharacterId));
                await session.SendAsync(header);
                await session.SendAsync(said);
                await RedrawAsync(member.CharacterId == who ? stream : null, member.CharacterId);
            }
            Console.WriteLine($"[Guild] «{guild.Name}» changed by {SessionContext.State.CharacterName}.");
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
            await GuildRankService.Live.SendAsync(SessionContext.Current, guild.Id);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, GuildStore.RankOf(founderCharacterId))));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khi, GuildProtocol.BuildGoneNotice()));
            var members = GuildStore.Members(guild.Id);
            await WriteAsync(stream, MembersFrame(members));
            await SessionContext.Current.SendAsync(GuildStateService.Live.Header(guild, members.Count));
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
        public static async Task SendGuildToOwnerAsync(GameSession session, GuildStore.Guild guild)
        {
            int rank = GuildStore.RankOf(session.CharacterId);
            var members = GuildStore.Members(guild.Id);

            // The guild's own ranks: a member of a rank of its own is looked up among them.
            await GuildRankService.Live.SendAsync(session, guild.Id);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jgw,
                GuildProtocol.BuildGuildJoined(guild, rank)));
            await session.SendAsync(MembersFrame(members));
            await session.SendAsync(GuildStateService.Live.Header(guild, members.Count));

            // And its notices, written or not written.
            if (GuildStore.NoticeOf(guild.Id, GuildStore.NoticeKind.MessageOfTheDay) is { } motd)
                await session.SendAsync(ConnectionProtocol.Push(Op.Jhk, GuildProtocol.BuildMessageOfTheDay(motd)));
            if (GuildStore.NoticeOf(guild.Id, GuildStore.NoticeKind.Bulletin) is { } bulletin)
                await session.SendAsync(ConnectionProtocol.Push(Op.Jjf, GuildProtocol.BuildBulletin(bulletin)));
        }

        /// <summary>Entry repeats ranks before silent membership, never the joined popup.</summary>
        internal static async Task SendMembershipAtEntryAsync(GameSession session)
        {
            long characterId = session.CharacterId;
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return;
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);
            await GuildRankService.Live.SendAsync(session, guild.Id);
            await session.SendAsync(GuildStateService.Live.Membership(guild, rank, characterId));
            await session.SendAsync(GuildStateService.Live.Header(guild, members.Count));
            await session.SendAsync(MembersFrame(members));
            Console.WriteLine($"[World] Guild sent for {session.State.CharacterName}: {guild.Name} ({members.Count} members).");
        }

        /// <summary>The right to write the message of the day: the guild window binds its edit to right 7 (GuildUI.InitUiBindings). Inferred.</summary>
        public const int MessageOfTheDayRight = 7;

        /// <summary>The right to write the logbook's bulletin: GuildLogBook.EnableEditIfHasRight asks for right 15.</summary>
        public const int BulletinRight = 15;

        /// <summary>
        /// The message of the day written (jjd: f1 the text, GuildUI.RequestSetMessageOfTheDay):
        /// kept, and every member on line told (jhk).
        /// </summary>
        public static async Task SetMessageOfTheDayAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjd = ConnectionProtocol.ReadPayload(frame, Op.Jjd);
            await SetNoticeAsync(jjd, GuildStore.NoticeKind.MessageOfTheDay, MessageOfTheDayRight,
                                 notice => ConnectionProtocol.Push(Op.Jhk, GuildProtocol.BuildMessageOfTheDay(notice)));
        }

        /// <summary>The logbook's bulletin written (jik: f1 the text, GuildLogBook.OnValidBulletin): kept, and told (jjf).</summary>
        public static async Task SetBulletinAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jik = ConnectionProtocol.ReadPayload(frame, Op.Jik);
            await SetNoticeAsync(jik, GuildStore.NoticeKind.Bulletin, BulletinRight,
                                 notice => ConnectionProtocol.Push(Op.Jjf, GuildProtocol.BuildBulletin(notice)));
        }

        private static async Task SetNoticeAsync(byte[] request, GuildStore.NoticeKind kind, int right, Func<GuildStore.Notice, byte[]> frameOf)
        {
            long who = SessionContext.State.CharacterId;
            var guild = request == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;
            if (GuildStore.RankOf(who) != GuildStore.RankLeader && !GuildStore.HasRight(who, right)) return;
            var text = ProtoMessage.Parse(request).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            string written = text == null ? "" : System.Text.Encoding.UTF8.GetString(text.BytesValue);
            var notice = GuildStore.SetNotice(guild.Id, kind, written, who, SessionContext.State.CharacterName);
            await TellEveryoneAsync(guild, frameOf(notice));
            Console.WriteLine($"[Guild] «{guild.Name}»'s {(kind == GuildStore.NoticeKind.Bulletin ? "bulletin" : "message of the day")} written by {notice.AuthorName}.");
        }

        /// <summary>
        /// The member list, ONE jgu with every member's name, level and account: the client takes
        /// each jgu as the whole list (<see cref="GuildProtocol.BuildMembers"/>).
        /// </summary>
        public static byte[] MembersFrame(List<GuildStore.Member> members)
            => ConnectionProtocol.Push(Op.Jgu, GuildProtocol.BuildMembers(MemberRows(members)));

        /// <summary>The members as their lists carry them: name, level, breed, points, whether on line.</summary>
        private static List<GuildProtocol.MemberRow> MemberRows(List<GuildStore.Member> members)
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
            return rows;
        }

        /// <summary>
        /// A guild's card (jhp: f1 the guild), asked from a chat link or the friends list: its
        /// members, its founding and the guild as the directory lists it (jie), which opens it.
        /// </summary>
        public static async Task ShowGuildAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jhp = ConnectionProtocol.ReadPayload(frame, Op.Jhp);
            if (jhp == null) return;
            long id = 0;
            foreach (var field in ProtoMessage.Parse(jhp).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) id = field.VarIntValue;
            var guild = id == 0 ? null : GuildStore.ById(id);
            if (guild == null) return;

            var members = GuildStore.Members(guild.Id);
            long founded = DateTimeOffset.TryParse(guild.FoundedUtc, System.Globalization.CultureInfo.InvariantCulture,
                                                   System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUnixTimeSeconds() : 0;
            var ranks = GuildStore.Ranks(guild.Id);
            var shown = MemberRows(members).Select(row => (row, ranks.FirstOrDefault(r => r.Id == row.Member.Rank)));
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jie,
                GuildProtocol.BuildGuildCard(DirectoryEntryOf(guild, members.Count), shown, founded),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>A guild as the directory lists it: its sheet, its leader and how many they are.</summary>
        private static GuildProtocol.DirectoryEntry DirectoryEntryOf(GuildStore.Guild guild, int members)
        {
            var leader = GuildStore.LeaderOf(guild.Id);
            var leaderCharacter = leader == null ? null : DatabaseManager.GetCharacterById(leader.CharacterId);
            return new GuildProtocol.DirectoryEntry
            {
                Guild = guild,
                Profile = GuildStore.ProfileOf(guild.Id),
                LeaderId = leader?.CharacterId ?? 0,
                LeaderName = leaderCharacter?.Name ?? "",
                Members = members,
            };
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

        /// <summary>
        /// The missions' ranks and categories (jgr → jgq): the preferences the tier's choice carried
        /// (jet f4), the waiting ones when a change waits for next week -- what the client's
        /// GuildMissionRanksAndCategoriesOption shows. Outside a guild, the empty answer.
        /// </summary>
        public static async Task MissionPreferencesAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            byte[] answer = GuildProtocol.BuildEmptyTab(1);
            if (guild != null)
            {
                var week = GuildActivity.Of(guild.Id, DateTimeOffset.UtcNow);
                answer = GuildProtocol.BuildMissionPreferences(week.NextPreferences ?? week.Preferences);
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jgq, answer, ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// A member's weekly rewards (jga → jfz): his guild acknowledgments waiting, which the
        /// client's GuildWeeklyActivityRewardsUi draws as its first tile and warns about at 100. The
        /// capture asked it with tokens just earned and got "0a00": the tokens go to the bag at once.
        /// </summary>
        public static async Task WeeklyRewardsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            int waiting = who == 0 ? 0 : GuildActivity.AcknowledgmentsOf(who);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jfz, GuildProtocol.BuildWeeklyRewards(waiting),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// «Recuperar» in the weekly rewards window (jfy): the acknowledgments waiting go to the bag
        /// as their item, with the «Has conseguido» line, and the window is told it is empty (jfz).
        /// </summary>
        public static async Task CollectWeeklyRewardsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            int waiting = who == 0 ? 0 : GuildActivity.AcknowledgmentsOf(who);
            if (waiting > 0 && await WorkshopHandler.GiveAsync(stream, GuildActivity.AcknowledgmentItem, waiting))
            {
                GuildActivity.TakeAcknowledgments(who, waiting);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                    InfoMessages.ItemGained, waiting.ToString(), GuildActivity.AcknowledgmentItem.ToString())));
                await ItemUseHandler.WeighAsync(stream);
                Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} collects {waiting} guild acknowledgments.");
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jfz,
                GuildProtocol.BuildWeeklyRewards(who == 0 ? 0 : GuildActivity.AcknowledgmentsOf(who)),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>When the week starts again (jew → jez), on root 3 with the request's id.</summary>
        public static Task WeeklyResetAsync(GameSession session, int uid)
            => session.SendAsync(GuildProtocol.BuildWeeklyReset(DateTime.UtcNow), uid);

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
                await GuildStateService.Live.SendHeaderAsync(SessionContext.Current, guild);
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
            var was = GuildActivity.Of(guild.Id, now);
            var week = GuildStore.HasRight(who, GuildActivity.ManageActivityRight)
                ? GuildActivity.Choose(guild.Id, tier, preferences, now)
                : null;
            if (week != null)
            {
                Console.WriteLine($"[Guild] «{guild.Name}» chooses activity tier {tier}" +
                                  (week.PendingActivityId != 0 ? " for next week." : "."));
                LogWeekChoice(guild, was, week, preferences);
            }
            week ??= GuildActivity.Of(guild.Id, now);
            if (!week.HasActivity) return;

            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jdb,
                GuildProtocol.BuildTierChosen(week, GuildActivity.TokenCapOf(guild), GuildActivity.TokensOf(who, now),
                                              GuildMissions.Of(guild.Id, now), GuildMissions.StandingsOf(guild.Id, who, now),
                                              GuildMissionWatcher.SubscribersOf(guild.Id, now)),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// A week's choice in the journal: next week's tier when it changed, the missions'
        /// configuration when it did (a guild's first choice is neither).
        /// </summary>
        private static void LogWeekChoice(GuildStore.Guild guild, GuildActivity.Week was, GuildActivity.Week week, byte[] preferences)
        {
            long who = SessionContext.State.CharacterId;
            string name = SessionContext.State.CharacterName;
            if (week.PendingActivityId != 0 && week.PendingActivityId != was.PendingActivityId)
                GuildStore.Log(guild.Id, GuildLogProtocol.NextWeekly,
                    GuildLogProtocol.NextWeeklyLine(who, name, week.PendingActivityId), who, name);
            byte[] before = was.NextPreferences ?? was.Preferences ?? Array.Empty<byte>();
            if (was.HasActivity && !before.AsSpan().SequenceEqual(preferences ?? Array.Empty<byte>()))
                GuildStore.Log(guild.Id, GuildLogProtocol.MissionConfiguration,
                    GuildLogProtocol.MissionConfigurationLine(who, name), who, name);
        }

        /// <summary>
        /// Next week's tier saved (jey: f1 the tier). GuildMissionTierOption.OnSaveTier sends it only
        /// once the guild has a tier, its save button asking for right 40; before that the tier goes
        /// in a jet with the preferences. Kept with the preferences as they stand and answered with
        /// the week (jex), whose f5 the window shows as next week's tier; the other members on line
        /// are told the week too.
        /// </summary>
        public static async Task SaveTierAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jey = ConnectionProtocol.ReadPayload(frame, Op.Jey);
            long who = SessionContext.State.CharacterId;
            var guild = jey == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            int tier = 0;
            foreach (var field in ProtoMessage.Parse(jey).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) tier = (int)field.VarIntValue;

            var now = DateTimeOffset.UtcNow;
            var was = GuildActivity.Of(guild.Id, now);
            if (!was.HasActivity) return;
            byte[] preferences = was.NextPreferences ?? was.Preferences;
            var week = GuildStore.HasRight(who, GuildActivity.ManageActivityRight)
                ? GuildActivity.Choose(guild.Id, tier, preferences, now)
                : null;
            if (week != null)
            {
                Console.WriteLine($"[Guild] «{guild.Name}» saves activity tier {tier} for next week.");
                LogWeekChoice(guild, was, week, preferences);
            }

            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jex,
                GuildProtocol.BuildWeekChanged(week ?? was, GuildActivity.TokenCapOf(guild), GuildActivity.TokensOf(who, now)),
                ConnectionProtocol.RequestId(frame)));
            if (week != null && week.PendingActivityId != was.PendingActivityId)
                await GuildMissionWatcher.TellTheWeekAsync(guild, now, except: who);
        }

        /// <summary>
        /// The missions' ranks and categories saved (jgg: f1 jgs). GuildMissionRanksAndCategoriesOption.OnSave
        /// sends it only once the guild has a tier, its save button asking for right 39; they apply
        /// from next week, as the tier does, the tier left as it stands. Answered with jgl: f2 { the
        /// preferences as saved }, or f1 {} when they were not.
        /// </summary>
        public static async Task SaveMissionPreferencesAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jgg = ConnectionProtocol.ReadPayload(frame, Op.Jgg);
            long who = SessionContext.State.CharacterId;
            var guild = jgg == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            byte[] preferences = Array.Empty<byte>();
            foreach (var field in ProtoMessage.Parse(jgg).Fields)
                if (field.FieldNumber == 1 && field.WireType == 2) preferences = field.BytesValue ?? Array.Empty<byte>();

            var now = DateTimeOffset.UtcNow;
            var was = GuildActivity.Of(guild.Id, now);
            var week = was.HasActivity && GuildStore.HasRight(who, GuildMissions.ManageMissionsRight)
                ? GuildActivity.Choose(guild.Id, was.PendingActivityId != 0 ? was.PendingActivityId : was.ActivityId, preferences, now)
                : null;
            if (week != null)
            {
                Console.WriteLine($"[Guild] «{guild.Name}» saves its missions' ranks and categories for next week.");
                LogWeekChoice(guild, was, week, preferences);
            }

            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jgl,
                week == null ? GuildProtocol.BuildMissionConfigurationRefused()
                             : GuildProtocol.BuildMissionConfigurationSaved(week.NextPreferences ?? week.Preferences),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// The missions tab (jeu → jei): the week's twelve, drawn the first time they are asked
        /// while the guild has a tier (GuildMissions), each as the member stands on it
        /// (GuildMissionWatcher); the empty tab without one.
        /// </summary>
        public static async Task MissionsAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            var now = DateTimeOffset.UtcNow;
            var missions = guild == null ? Array.Empty<GuildMissions.Drawn>() : GuildMissions.Of(guild.Id, now);
            var standings = guild == null ? null : GuildMissions.StandingsOf(guild.Id, who, now);
            var subscribers = guild == null ? null : GuildMissionWatcher.SubscribersOf(guild.Id, now);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jei, GuildProtocol.BuildMissions(missions.ToList(), standings, subscribers),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// Marking a mission as interesting, or not (jej: f1 on, f2 its uuid): the guide's
        /// favourites, whose count shows on each card. Answered with the card as it now stands
        /// (jdk), and the card to every other member connected.
        /// </summary>
        public static async Task InterestAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jej = ConnectionProtocol.ReadPayload(frame, Op.Jej);
            long who = SessionContext.State.CharacterId;
            var guild = jej == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null) return;

            string uuid = null;
            bool interested = false;
            foreach (var field in ProtoMessage.Parse(jej).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 2) uuid = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                if (field.FieldNumber == 1 && field.WireType == 0) interested = field.VarIntValue != 0;
            }
            var now = DateTimeOffset.UtcNow;
            var mission = GuildMissions.Of(guild.Id, now).FirstOrDefault(m => m.Uuid == uuid);
            if (mission == null) return;

            GuildMissions.SetInterest(guild.Id, mission.Slot, who, interested, now);
            var standing = GuildMissions.StandingsOf(guild.Id, who, now).GetValueOrDefault(mission.Slot, GuildMissions.Standing.None);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jdk,
                GuildProtocol.BuildInterestAnswer(mission, standing, GuildMissionWatcher.SubscribersOf(guild.Id, now).GetValueOrDefault(mission.Slot)),
                ConnectionProtocol.RequestId(frame)));
            await GuildMissionWatcher.TellMissionAsync(guild, mission, now, except: who);
        }

        /// <summary>
        /// Rerolling a mission (jeq: f2 its uuid, f1 an enum the card leaves at 0): with the right «Gestionar los encargos» and the
        /// tier's price in guild kamas (50 to 750), the card's mission is swapped for another of its
        /// rank and category not seen this week (GuildMissions.Reroll). The answer (jed) goes to the
        /// one who asked and to every member connected, whose cards change too; the guild's kamas to
        /// everybody. With none left, the jed that makes the client say so.
        /// </summary>
        public static async Task RerollMissionAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jeq = ConnectionProtocol.ReadPayload(frame, Op.Jeq);
            long who = SessionContext.State.CharacterId;
            var guild = jeq == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null || !GuildStore.HasRight(who, GuildMissions.ManageMissionsRight)) return;
            var field = ProtoMessage.Parse(jeq).Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
            string uuid = field == null ? null : System.Text.Encoding.UTF8.GetString(field.BytesValue);
            if (string.IsNullOrEmpty(uuid)) return;

            var now = DateTimeOffset.UtcNow;
            var tier = GuildActivity.TierOf(GuildActivity.Of(guild.Id, now).ActivityId);
            if (tier == null) return;

            var (outcome, old, fresh) = GuildMissions.Reroll(guild.Id, uuid, now, Random.Shared,
                () => GuildStore.SpendGuildKamas(guild.Id, tier.RerollCost));
            if (outcome == GuildMissions.RerollOutcome.NoneLeft)
                await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jed, GuildProtocol.BuildRerollRefused(),
                                                                   ConnectionProtocol.RequestId(frame)));
            if (outcome != GuildMissions.RerollOutcome.Done) return;

            Console.WriteLine($"[Guild] «{guild.Name}» rerolls mission {old.MissionId} into {fresh.MissionId} " +
                              $"for {tier.RerollCost} guild kamas.");
            string rerolledBy = SessionContext.State.CharacterName;
            GuildStore.Log(guild.Id, GuildLogProtocol.MissionReroll,
                GuildLogProtocol.MissionRerollLine(who, rerolledBy, old.MissionId, fresh.MissionId), who, rerolledBy);
            byte[] jed = GuildProtocol.BuildRerolled(old.Uuid, fresh);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Jed, jed, ConnectionProtocol.RequestId(frame)));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                if (member.CharacterId == who) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(ConnectionProtocol.Push(Op.Jed, jed));
            }
            var paid = GuildStore.ById(guild.Id) ?? guild;
            await TellEveryoneAsync(paid, ConnectionProtocol.Push(Op.Jia, GuildProtocol.BuildGuildKamas(paid.GuildKamas)));
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
            => await GuildRankService.Live.SendAsync(SessionContext.Current, guild.Id);

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

            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == RankIdOf(inner.BytesValue));
            if (rank == null) return;
            ApplyRank(rank, inner.BytesValue);
            GuildStore.SaveRank(rank);
            LogRank(guild, rank, GuildLogProtocol.RankAction.Updated);

            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Guild] Rank {rank.Id} of «{guild.Name}» edited: «{rank.Name}».");
        }

        /// <summary>
        /// Every rank at once (jcu: f1 each rank, as the jco carries them), the ranks window's
        /// UpdateAllRankRequestAction after dragging them into another order: each saved, the jco back.
        /// </summary>
        public static async Task UpdateRanksAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcu = ConnectionProtocol.ReadPayload(frame, Op.Jcu);
            if (jcu == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;

            var ranks = GuildStore.Ranks(guild.Id);
            foreach (var field in ProtoMessage.Parse(jcu).Fields)
            {
                if (field.FieldNumber != 1 || field.WireType != 2) continue;
                var rank = ranks.Find(r => r.Id == RankIdOf(field.BytesValue));
                if (rank == null) continue;
                if (ApplyRank(rank, field.BytesValue)) LogRank(guild, rank, GuildLogProtocol.RankAction.Updated);
                GuildStore.SaveRank(rank);
            }
            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Guild] The ranks of «{guild.Name}» are saved in a new order.");
        }

        /// <summary>A rank's id in the client's rank (ldt f5).</summary>
        private static int RankIdOf(byte[] ldt)
            => (int)(ProtoMessage.Parse(ldt).Fields.Find(f => f.FieldNumber == 5 && f.WireType == 0)?.VarIntValue ?? 0);

        /// <summary>
        /// A rank as the client sends it (ldt: f2 name, f3 { f1 flag, f3 rights }, f4 { f2 icon,
        /// f3 order }, f5 id) over the stored one: what it leaves out stays. True when its name or
        /// rights changed.
        /// </summary>
        private static bool ApplyRank(GuildStore.Rank rank, byte[] ldt)
        {
            string name = null;
            byte[] rights = null;
            bool? flag = null;
            int icon = 0, order = -1;
            foreach (var field in ProtoMessage.Parse(ldt).Fields)
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
                }
            }

            bool changed = (name != null && name != rank.Name)
                           || (rights != null && !rights.AsSpan().SequenceEqual(rank.Rights ?? Array.Empty<byte>()));
            if (name != null) rank.Name = name;
            if (rights != null) rank.Rights = rights;
            if (flag.HasValue) rank.Flag = flag.Value;
            if (icon != 0) rank.Icon = icon;
            if (order >= 0) rank.Order = order;
            return changed;
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
            LogRank(guild, rank, GuildLogProtocol.RankAction.Updated);

            await SendRanksAsync(stream, guild);
        }

        /// <summary>
        /// Deleting a rank (jcn): f1 the rank, f2 the one its members go to -- the client's
        /// RemoveRankUi (ffr.beeb). The jco back, each member moved told to everybody (jgz).
        /// </summary>
        public static async Task DeleteRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jcn = ConnectionProtocol.ReadPayload(frame, Op.Jcn);
            if (jcn == null) return;
            var guild = GuildIfLeader(SessionContext.State.CharacterId);
            if (guild == null) return;
            int rankId = (int)FieldValue(jcn, 1);
            int replacement = (int)FieldValue(jcn, 2);
            var rank = GuildStore.Ranks(guild.Id).Find(r => r.Id == rankId);
            var moved = rank == null ? null : GuildStore.DeleteRank(guild.Id, rankId, replacement);
            if (moved == null)
            {
                await SendRanksAsync(stream, guild);
                return;
            }

            LogRank(guild, rank!, GuildLogProtocol.RankAction.Deleted);
            await SendRanksAsync(stream, guild);
            foreach (long member in moved)
            {
                var character = DatabaseManager.GetCharacterById(member);
                var updated = GuildStore.MemberOf(member);
                if (character != null && updated != null)
                    await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(updated, character)));
            }
            Console.WriteLine($"[Guild] Rank {rankId} «{rank!.Name}» of «{guild.Name}» deleted; {moved.Count} member(s) to rank {replacement}.");
        }

        /// <summary>A rank created, changed or deleted, in the journal.</summary>
        private static void LogRank(GuildStore.Guild guild, GuildStore.Rank rank, GuildLogProtocol.RankAction action)
            => GuildStore.Log(guild.Id, GuildLogProtocol.RankChange, GuildLogProtocol.RankLine(rank.Id, rank.Name, action),
                              SessionContext.State.CharacterId, SessionContext.State.CharacterName);

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
            LogRank(guild, created, GuildLogProtocol.RankAction.Created);
            await SendRanksAsync(stream, guild);
            Console.WriteLine($"[Guild] Rank {created.Id} «{created.Name}» created in «{guild.Name}».");
        }

        /// <summary>
        /// A member's rank from the members window (jgv): f2 the member, f4 the rank. Read off the client
        /// (GuildMemberRightsUI.OnModifyRank, ffr.beet), which never fills the f3. Measured in
        /// «...-intentar asignar rango a otro»: "108590d8c69e052001", rank 1 to a member who had just
        /// joined, refused with the lqn 740 and its "24" -- a member has to have been in the guild a day.
        /// </summary>
        public static async Task AssignRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jgv = ConnectionProtocol.ReadPayload(frame, Op.Jgv);
            if (jgv == null) return;
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

            // The journal: «{0} ha sido nombrado {1} por {2}», for each one whose rank changed.
            string assignerName = DatabaseManager.GetCharacterById(assignerCharacterId)?.Name ?? "";
            foreach (var member in changed)
            {
                var given = ranks.Find(r => r.Id == member.Rank);
                string name = DatabaseManager.GetCharacterById(member.CharacterId)?.Name ?? "";
                if (given != null)
                    GuildStore.Log(guild.Id, GuildLogProtocol.PlayerRank,
                        GuildLogProtocol.PlayerRankLine(given.Id, given.Name, member.CharacterId, name, assignerCharacterId, assignerName),
                        assignerCharacterId, assignerName);
            }

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
        /// <summary>The warning of members coming on line, on (jha) or off (jhf): kept for the session.</summary>
        public static Task WarnConnectionsAsync(bool on)
        {
            SessionContext.State.WarnGuildConnections = on;
            return Task.CompletedTask;
        }

        /// <summary>
        /// A member came into the world or left it: the others' member lists see it (jgz), and on
        /// coming in those who asked to be warned are (jhd).
        /// </summary>
        public static async Task TellConnectionAsync(long characterId, bool online)
        {
            var guild = GuildStore.GuildOf(characterId);
            var member = guild == null ? null : GuildStore.Members(guild.Id).Find(m => m.CharacterId == characterId);
            var character = member == null ? null : DatabaseManager.GetCharacterById(characterId);
            if (character == null) return;

            byte[] updated = ConnectionProtocol.Push(Op.Jgz, MemberUpdated(member, character));
            await GuildPresenceService.Live.BroadcastAsync(guild.Id, characterId, online, updated);
        }

        internal static async Task TellEveryoneAsync(GuildStore.Guild guild, byte[] frame)
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
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jil, GuildLogProtocol.BuildLog(GuildStore.LogOf(guild.Id))));
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
                entries.Add(DirectoryEntryOf(guild, GuildStore.Members(guild.Id).Count));

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
            var header = GuildStateService.Live.Header(guild, members.Count);

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
        /// kamas and the character's. The tokens are items in his bag (32441), told as the capture
        /// «contribuir» tells them: ivj, lqn 21, then the week (jex).
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

            // The capture's order: ivf, jgz, ivj, lqn, jex, jia, iun, (khd), jle.
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivf,
                ConnectionProtocol.BuildKamas(GameState.Kamas)));
            var me = GuildStore.MemberOf(who);
            var myself = DatabaseManager.GetCharacterById(who);
            if (me != null && myself != null)
            {
                await TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jgz, MemberUpdated(me, myself)));
            }
            await GuildMissionWatcher.GiveTokensAsync(stream, credited.Tokens, weigh: false);
            await GuildMissionWatcher.TellTheWeekAsync(guild, now);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia,
                GuildProtocol.BuildGuildKamas(GuildStore.GuildOf(who).GuildKamas)));
            if (credited.Tokens > 0) await ItemUseHandler.WeighAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jle,
                GuildProtocol.BuildContribution(GuildStore.ContributionKamas, left)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jla, GuildProtocol.BuildContributionsLeft(left)));

            // A milestone reached moves the guild's experience, and maybe its level: everybody's header.
            if (credited.Experience > 0) await GuildStateService.Live.TellHeaderAsync(GuildStore.GuildOf(who));
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
        /// accept) sends it. Measured on refusing one, "108590d8c69e05", the applicant alone,
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
            if (!accepted)
            {
                string refused = DatabaseManager.GetCharacterById(applicant)?.Name ?? "";
                GuildStore.Log(guild.Id, GuildLogProtocol.PlayerFlow,
                    GuildLogProtocol.PlayerFlowLine(applicant, refused, GuildLogProtocol.Flow.ApplicationRefused), applicant, refused);
            }

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
        /// the jlt. Measured in «...-rechazarla-invitar a gremio»: the jjn "108590d8c69e05" and a jin
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
            await SendGuildToOwnerAsync(SessionContext.Current, guild);
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

        /// <summary>The sheet's recruitment that lets anybody join at once (<see cref="GuildStore.Profile.F5"/>).</summary>
        public const int AutomaticRecruitment = 1;

        /// <summary>
        /// «Unirse al gremio» (jhn: f4 the guild), offered by a guild whose sheet recruits
        /// automatically: in at once, if he has no guild and reaches the sheet's minimum level --
        /// the two things the client's card checks (GuildCardUI.CanJoinGuild). His applications
        /// elsewhere go; the guild arrives as on accepting an invitation.
        /// </summary>
        public static async Task JoinDirectlyAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jhn = ConnectionProtocol.ReadPayload(frame, Op.Jhn);
            long who = SessionContext.State.CharacterId;
            if (jhn == null || who == 0 || GuildStore.GuildOf(who) != null) return;
            var guild = GuildStore.ById(FieldValue(jhn, 4));
            var profile = guild == null ? null : GuildStore.ProfileOf(guild.Id);
            if (guild == null || profile == null || profile.WhenMs == 0 || profile.F5 != AutomaticRecruitment)
            {
                Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} may not join guild {FieldValue(jhn, 4)} directly.");
                return;
            }
            if (SessionContext.State.CharacterLevel < profile.MinLevel) return;

            foreach (var application in GuildStore.ApplicationsBy(who))
            {
                GuildStore.DropApplication(application.GuildId, who);
                var other = GuildStore.ById(application.GuildId);
                if (other != null) await ApplicationMovedAsync(other, application, GuildProtocol.ApplicationGone, who);
            }

            GuildStore.Join(who, guild.Id);
            await SendGuildToOwnerAsync(SessionContext.Current, guild);
            await RedrawAsync(stream, who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Jij, GuildProtocol.BuildJoinDone()));
            await RefreshEveryoneAsync(guild, who);
            Console.WriteLine($"[Guild] {SessionContext.State.CharacterName} joins «{guild.Name}» directly.");
        }

        /// <summary>Sends the whole guild to whoever has just joined, if he is connected.</summary>
        private static async Task SendGuildToNewMemberAsync(long characterId, GuildStore.Guild guild)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            int rank = GuildStore.RankOf(characterId);
            var members = GuildStore.Members(guild.Id);

            await GuildRankService.Live.SendAsync(session, guild.Id);
            await session.SendAsync(ConnectionProtocol.Push(Op.Jgw, GuildProtocol.BuildGuildJoined(guild, rank)));
            await session.SendAsync(MembersFrame(members));
            await session.SendAsync(GuildStateService.Live.Header(guild, members.Count));
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
