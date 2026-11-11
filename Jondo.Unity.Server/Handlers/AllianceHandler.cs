using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The alliance: founding it in the Temple of Alliances, finding one in the directory, joining
    /// it, leaving it, expelling, and the window's sheet.
    /// </summary>
    /// <remarks>
    /// What is measured comes from the two captures of Alianzas/: the temple's door refusing a
    /// character without the alliance gem, and a character joining «E X A M P L E» from the
    /// directory and leaving it. What no capture shows -- founding, expelling someone else -- is
    /// built from the client's own frames and texts: the editor it opens (mcw), the request it sends
    /// (mdk) and the results its editor knows how to say (mch).
    /// </remarks>
    public static class AllianceHandler
    {
        /// <summary>The alliance gem, «Alianzalogema», the stone founding spends.</summary>
        public const int AllianceGemTemplate = 14290;

        /// <summary>The Temple of Alliances, at [13,35].</summary>
        public const long TempleMap = 115083777;

        /// <summary>
        /// The temple's blue door, the one the capture presses without a gem: iwo {18601, 481519} →
        /// iwn with skill 184, then lqn {1, 393}, «Solo los personajes de nivel 50 o más que posean
        /// una alianzalogema están autorizados a entrar». With the gem it lets him in.
        /// </summary>
        public const int TempleDoor = 481519;
        public const int TempleDoorSkill = 184;

        /// <summary>
        /// The room the door lets into, 115081731 at [13,34]: a lectern with an open book in the
        /// middle (element 530184, cell 355) and two exits back to the temple at its foot (504482
        /// and 504483, cells 500 and 514). One comes in in front of the exits.
        /// </summary>
        public const long InnerRoom = 115081731;
        public const int InnerRoomArrival = 486;

        /// <summary>
        /// The room's book, which opens the founding editor: the alliance's altar, as the guild has
        /// its own. No capture presses it; its skill is the guild altar's, «Utilizar».
        /// </summary>
        public const int FoundingBook = 530184;
        public const int FoundingBookSkill = 184;

        /// <summary>«Solo los personajes de nivel 50 o más que posean una alianzalogema están autorizados a entrar.»</summary>
        public const int DoorRefused = 393;

        /// <summary>«Ya no formas parte de la alianza &lt;b&gt;{0}&lt;/b&gt;.» What the capture says on leaving.</summary>
        public const int NoLongerMember = 410;

        /// <summary>«Has expulsado a &lt;b&gt;{0}&lt;/b&gt; de tu alianza.»</summary>
        public const int YouExpelled = 1506;

        /// <summary>«&lt;b&gt;{0}&lt;/b&gt; ya no forma parte de la alianza.» The others, about an expelled member.</summary>
        public const int WasExpelled = 1530;

        /// <summary>«{0} ha abandonado la alianza.» The others, about one who left.</summary>
        public const int HasLeft = 1531;

        /// <summary>«Solo los personajes de nivel 50 o superior podrán unirse a una alianza.»</summary>
        public const int JoinTooLow = 698;

        /// <summary>«No dispones de los derechos de alianza requeridos.»</summary>
        public const int MissingRight = 383;

        /// <summary>The directory's page when the request names none, the capture's.</summary>
        private const int DefaultPageSize = 40;

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        // ─── Founding ───────────────────────────────────────────────────────────

        /// <summary>
        /// The temple's door or the room's book: the element is in use, and then the door says who
        /// may enter -- as it did in the capture to a character with no gem -- or lets him into the
        /// room, and the book opens the editor (mcw).
        /// </summary>
        public static async Task UseTempleAsync(NetworkStream stream, int elementId, int skillId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, SessionContext.State.CharacterId)));

            if (!MayFound())
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, DoorRefused)));
                return;
            }

            if (elementId == TempleDoor)
            {
                var door = Interactives.ElementsOf(TempleMap).FirstOrDefault(e => e.Id == TempleDoor);
                await TeleportHandler.ReleaseAsync(stream, elementId, skillId, door.Cell, Interactives.TypeOfGfx(door.Gfx));
                await TeleportHandler.ToMapAsync(stream, InnerRoom, InnerRoomArrival);
                Console.WriteLine($"[Alliance] {SessionContext.State.CharacterName} enters the Temple of Alliances' room.");
                return;
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mcw, AllianceProtocol.BuildCreationStarted()));
            Console.WriteLine($"[Alliance] {SessionContext.State.CharacterName} opens the founding editor.");
        }

        /// <summary>What the door asks: level 50 and an alliance gem in the bag.</summary>
        private static bool MayFound()
            => SessionContext.State.CharacterLevel >= AllianceStore.MinimumLevel &&
               Equipment.HowMany(AllianceGemTemplate) > 0;

        /// <summary>
        /// Founding an alliance (mdk): f1 the emblem {symbol, symbol colour, background, shape,
        /// symbol RGB}, f2 the name, f3 the tag, as the editor leaves them.
        /// </summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mdk = ConnectionProtocol.ReadPayload(frame, Op.Mdk);
            if (mdk == null) return;

            string name = "", tag = "";
            var emblem = new int[6];
            foreach (var field in ProtoMessage.Parse(mdk).Fields)
            {
                if (field.WireType != 2) continue;
                if (field.FieldNumber == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 3) tag = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 1)
                {
                    foreach (var e in ProtoMessage.Parse(field.BytesValue).Fields)
                        if (e.WireType == 0 && e.FieldNumber is >= 1 and <= 5) emblem[e.FieldNumber] = (int)e.VarIntValue;
                }
            }

            int result = await FoundAsync(stream, SessionContext.State.CharacterId, name, tag,
                                          emblem[1], emblem[2], emblem[3], emblem[4], emblem[5]);
            if (result != AllianceProtocol.CreationOk)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Mch, AllianceProtocol.BuildCreationResult(result)));
                Console.WriteLine($"[Alliance] Founding of «{name}» [{tag}] refused: result {result}.");
            }
        }

        /// <summary>
        /// The founding: everything is checked, the gem is spent, the alliance is stored and the
        /// founder is sent his part. Returns the creation result (lbv), 0 when it was founded.
        /// </summary>
        /// <remarks>
        /// The gem goes LAST, once nothing can fail: a name already taken must not cost the stone.
        /// What follows success is what joining sends in the capture -- the ranks, the alliance,
        /// the sheet, the banner emote and the redrawn character --, behind the empty mch the
        /// editor waits for to say «enhorabuena».
        /// </remarks>
        public static async Task<int> FoundAsync(NetworkStream stream, long founder, string name, string tag,
                                                 int symbol, int symbolColor, int background, int shape, int symbolRgb)
        {
            if (founder == 0) return AllianceProtocol.CreationRequirementUnmet;
            if (AllianceStore.AllianceOf(founder) != null) return AllianceProtocol.CreationAlreadyInAlliance;

            name = (name ?? "").Trim();
            tag = (tag ?? "").Trim();
            if (!IsValidName(name)) return AllianceProtocol.CreationInvalidName;
            if (!IsValidTag(tag)) return AllianceProtocol.CreationInvalidTag;
            if (AllianceStore.ByName(name) != null) return AllianceProtocol.CreationNameUsed;
            if (AllianceStore.ByTag(tag) != null) return AllianceProtocol.CreationTagUsed;
            if (!MayFound()) return AllianceProtocol.CreationRequirementUnmet;
            if (!await Equipment.TakeAsync(stream, AllianceGemTemplate, 1)) return AllianceProtocol.CreationRequirementUnmet;

            var alliance = AllianceStore.Create(founder, name, tag, symbol, symbolColor, background, shape, symbolRgb);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mch, AllianceProtocol.BuildCreationResult(AllianceProtocol.CreationOk)));
            await SendJoinedAsync(SessionContext.Current, alliance);

            Console.WriteLine($"[Alliance] {SessionContext.State.CharacterName} founds «{name}» [{tag}] " +
                              "and spends their alliance gem.");
            return AllianceProtocol.CreationOk;
        }

        /// <summary>
        /// An alliance name: 3 to 30 letters, spaces, dashes or apostrophes (the client's naming
        /// rule 2), and what the editor itself refuses -- a dash first or last, two dashes together,
        /// the same character three times running.
        /// </summary>
        internal static bool IsValidName(string name)
        {
            if (name.Length < 3 || name.Length > 30) return false;
            if (!Regex.IsMatch(name, "^[a-zA-Z' -]+$")) return false;
            if (name.StartsWith('-') || name.EndsWith('-') || name.Contains("--")) return false;
            return !Regex.IsMatch(name, @"(.)\1\1");
        }

        /// <summary>An alliance tag: 3 to 5 letters, the client's naming rule 8.</summary>
        internal static bool IsValidTag(string tag)
            => tag.Length >= 3 && tag.Length <= 5 && Regex.IsMatch(tag, "^[a-zA-Z]+$");

        // ─── Name and emblem, with a potion ─────────────────────────────────────

        /// <summary>«Pócima de cambio de nombre de alianza» and «…de emblema de alianza», sold by Gremia.</summary>
        public const int RenamePotionTemplate = 14291;
        public const int ReEmblemPotionTemplate = 14292;

        /// <summary>The potions' effects: «Cambiar el nombre de la alianza» (and its moderation twin), «Cambiar el emblema».</summary>
        public const int RenameEffect = 1146;
        public const int RenameByModerationEffect = 2993;
        public const int ReEmblemEffect = 1145;

        /// <summary>«Para hacer está acción, debes pertenecer a una alianza.»</summary>
        public const int NeedsAlliance = 405;

        /// <summary>«Se ha cambiado el nombre de la alianza.» / «Se ha cambiado el emblema de la alianza.»</summary>
        public const int NameChanged = 408;
        public const int EmblemChanged = 409;

        /// <summary>The potion the leader is changing his alliance with, until the editor answers.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long Uid, bool Name, bool Emblem)> _potions = new();

        /// <summary>
        /// Using a potion of the alliance's (14291 «Pócima de cambio de nombre de alianza», 14292 «…de
        /// emblema», 28330 the moderation's): the leader gets the editor in its mode (mce). The potion
        /// goes only once the change is made. False if the item is not one of them.
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
            if (AllianceStore.AllianceOf(who) == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, NeedsAlliance)));
                return true;
            }
            if (AllianceStore.RankOf(who) != AllianceStore.RankLeader)
            {
                await RefusedAsync(stream);
                return true;
            }

            _potions[who] = (item.Uid, name, emblem);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mce, AllianceProtocol.BuildModificationStarted(name, emblem)));
            return true;
        }

        /// <summary>The new name and tag (mcg): f1 the name, f2 the tag.</summary>
        public static async Task RenameAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mcg = ConnectionProtocol.ReadPayload(frame, Op.Mcg);
            if (mcg == null) return;
            string name = "", tag = "";
            foreach (var field in ProtoMessage.Parse(mcg).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 2 && field.WireType == 2) tag = System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }
            await ModifyAsync(stream, name, tag, null);
        }

        /// <summary>The new emblem (mcz): f1 the lld.</summary>
        public static async Task ReEmblemAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mcz = ConnectionProtocol.ReadPayload(frame, Op.Mcz);
            if (mcz == null) return;
            var lld = ProtoMessage.Parse(mcz).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            await ModifyAsync(stream, null, null, lld == null ? new int[6] : ReadEmblem(lld.BytesValue));
        }

        /// <summary>A new name, tag and emblem at once (mcy): f1 the lld, f2 the name, f4 the tag.</summary>
        public static async Task ModifyAllAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mcy = ConnectionProtocol.ReadPayload(frame, Op.Mcy);
            if (mcy == null) return;
            string name = "", tag = "";
            int[] emblem = new int[6];
            foreach (var field in ProtoMessage.Parse(mcy).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 4 && field.WireType == 2) tag = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 1 && field.WireType == 2) emblem = ReadEmblem(field.BytesValue);
            }
            await ModifyAsync(stream, name, tag, emblem);
        }

        private static int[] ReadEmblem(byte[] lld)
        {
            var emblem = new int[6];
            foreach (var e in ProtoMessage.Parse(lld).Fields)
                if (e.WireType == 0 && e.FieldNumber is >= 1 and <= 5) emblem[e.FieldNumber] = (int)e.VarIntValue;
            return emblem;
        }

        /// <summary>
        /// The modification, checked as founding is and against the potion in use: a name or tag
        /// needs the name potion, an emblem the emblem one. Done, the potion goes, the editor hears
        /// success (mcn), and every member connected gets the alliance anew -- belonging, his
        /// character redrawn -- with «Se ha cambiado el nombre / el emblema de la alianza».
        /// </summary>
        private static async Task ModifyAsync(NetworkStream stream, string name, string tag, int[] emblem)
        {
            long who = SessionContext.State.CharacterId;
            var alliance = AllianceStore.AllianceOf(who);
            if (alliance == null || !_potions.TryGetValue(who, out var potion)) return;

            int result = AllianceProtocol.CreationOk;
            if (name != null)
            {
                name = name.Trim();
                tag = (tag ?? "").Trim();
                var byName = AllianceStore.ByName(name);
                var byTag = AllianceStore.ByTag(tag);
                if (!potion.Name || AllianceStore.RankOf(who) != AllianceStore.RankLeader) result = AllianceProtocol.CreationRequirementUnmet;
                else if (!IsValidName(name)) result = AllianceProtocol.CreationInvalidName;
                else if (!IsValidTag(tag)) result = AllianceProtocol.CreationInvalidTag;
                else if (byName != null && byName.Id != alliance.Id) result = AllianceProtocol.CreationNameUsed;
                else if (byTag != null && byTag.Id != alliance.Id) result = AllianceProtocol.CreationTagUsed;
            }
            if (result == AllianceProtocol.CreationOk && emblem != null && !potion.Emblem)
                result = AllianceProtocol.CreationRequirementUnmet;
            if (result == AllianceProtocol.CreationOk && !await ItemUseHandler.SpendOneAsync(stream, potion.Uid))
                result = AllianceProtocol.CreationRequirementUnmet;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mcn, AllianceProtocol.BuildModificationResult(result)));
            if (result != AllianceProtocol.CreationOk) return;

            _potions.TryRemove(who, out _);
            if (name != null) alliance = AllianceStore.Rename(alliance.Id, name, tag);
            if (emblem != null)
                alliance = AllianceStore.SetEmblem(alliance.Id, emblem[1], emblem[2], emblem[3], emblem[4], emblem[5]);

            byte[] said = ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                name != null ? NameChanged : EmblemChanged));
            foreach (var member in AllianceStore.Members(alliance.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session?.Stream == null) continue;
                await session.SendAsync(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, member.Rank)));
                await session.SendAsync(said);
                await RedrawAsync(session.Stream, member.CharacterId);
            }
            Console.WriteLine($"[Alliance] «{alliance.Name}» [{alliance.Tag}] changed by {SessionContext.State.CharacterName}.");
        }

        // ─── Joining ────────────────────────────────────────────────────────────

        /// <summary>
        /// What whoever joins an alliance receives, in the capture's order: the ranks, belonging,
        /// the sheet, joining (with its chat line), the banner emote, the redrawn character, and the
        /// message and the bulletin.
        /// </summary>
        public static async Task SendJoinedAsync(GameSession session, AllianceStore.Alliance alliance)
        {
            using var context = SessionContext.Push(session);
            long characterId = session.CharacterId;
            int rank = AllianceStore.RankOf(characterId);
            await AllianceRankService.Live.SendAsync(session, alliance.Id);
            await session.SendAsync(ConnectionProtocol.Push(Op.Mbz, AllianceProtocol.BuildMembership(alliance, rank)));
            await session.SendAsync(InformationFrame(alliance));
            await session.SendAsync(ConnectionProtocol.Push(Op.Mca, AllianceProtocol.BuildJoined(alliance, rank)));
            await session.SendAsync(ConnectionProtocol.Push(Op.Khi, AllianceProtocol.BuildEmote()));
            await RedrawAsync(session.Stream, characterId);
            await SendNoticesAsync(session, alliance);
        }

        /// <summary>The alliance message and the bulletin, as they stand.</summary>
        private static Task SendNoticesAsync(GameSession session, AllianceStore.Alliance alliance)
            => AllianceNoticeService.Live.SendAsync(session, alliance);

        /// <summary>
        /// Somebody has joined: he gets his part, whoever was there his line (mbv), and a pending
        /// application of his is done with.
        /// </summary>
        private static async Task WelcomeAsync(GameSession session, AllianceStore.Alliance alliance)
        {
            using var context = SessionContext.Push(session);
            long characterId = session.CharacterId;
            AllianceStore.DropApplication(characterId);
            await SendJoinedAsync(session, alliance);
            var row = RowOf(AllianceStore.MemberOf(characterId));
            if (row != null)
                await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mbv, AllianceProtocol.BuildMemberUpdate(row.Value)), characterId);
        }

        /// <summary>
        /// Joining from the directory (mac): f1 the alliance's uuid. «E X A M P L E» took the
        /// character at once, as an alliance with an open recruitment does.
        /// </summary>
        public static async Task JoinAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mac = ConnectionProtocol.ReadPayload(frame, Op.Mac);
            if (mac == null) return;

            string uuid = "";
            foreach (var field in ProtoMessage.Parse(mac).Fields)
                if (field.FieldNumber == 1 && field.WireType == 2) uuid = System.Text.Encoding.UTF8.GetString(field.BytesValue);

            long who = SessionContext.State.CharacterId;
            var alliance = AllianceStore.ByUuid(uuid);
            if (who == 0 || alliance == null || AllianceStore.AllianceOf(who) != null) return;

            var profile = AllianceStore.ProfileOf(alliance.Id);
            if (SessionContext.State.CharacterLevel < Math.Max(AllianceStore.MinimumLevel, profile.MinLevel))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, JoinTooLow)));
                return;
            }
            if (profile.Recruitment != AllianceStore.RecruitmentOpen) return;

            AllianceStore.Join(who, alliance.Id);
            await WelcomeAsync(SessionContext.Current, alliance);
            Console.WriteLine($"[Alliance] {SessionContext.State.CharacterName} joins «{alliance.Name}».");
        }

        // ─── Leaving and expelling ──────────────────────────────────────────────

        /// <summary>
        /// Expelling (mcc): f1 the member. With one's own id it is leaving, which is how the capture
        /// leaves «E X A M P L E». Anybody else takes the right to expel.
        /// </summary>
        public static async Task ExpelAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mcc = ConnectionProtocol.ReadPayload(frame, Op.Mcc);
            if (mcc == null) return;

            long target = 0;
            foreach (var field in ProtoMessage.Parse(mcc).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) target = field.VarIntValue;

            long who = SessionContext.State.CharacterId;
            if (who == 0 || target == 0) return;
            if (target == who)
            {
                await LeaveAsync(stream, who);
                return;
            }

            var alliance = AllianceStore.AllianceOf(who);
            var victim = AllianceStore.MemberOf(target);
            if (alliance == null || victim == null || victim.AllianceId != alliance.Id) return;
            if (!AllianceStore.HasRight(who, AllianceData.Expel) || victim.Rank == AllianceStore.RankLeader)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, MissingRight)));
                return;
            }

            string name = DatabaseManager.GetCharacterById(target)?.Name ?? "";
            AllianceStore.Leave(target);

            var expelled = SessionRegistry.FindByCharacter(target);
            if (expelled != null)
            {
                using (SessionContext.Push(expelled))
                {
                    await GoneAsync(expelled.Stream, alliance, target);
                }
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(YouExpelled, name)));
            await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mby, AllianceProtocol.BuildMemberGone(target)));
            await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(WasExpelled, name)), who);
            Console.WriteLine($"[Alliance] {name} expelled from «{alliance.Name}».");
        }

        /// <summary>
        /// Leaving one's alliance. A leader hands it on -- the new leader learns his rank, and the
        /// others his line --; the last one out takes it with him.
        /// </summary>
        public static async Task LeaveAsync(NetworkStream stream, long who)
        {
            var departure = AllianceStore.Leave(who);
            if (departure == null) return;

            await GoneAsync(stream, departure.Alliance, who);
            if (departure.Dissolved)
            {
                await TaxCollectorHandler.AllianceDissolvedAsync(departure.Alliance.Id);
                await PrismHandler.AllianceDissolvedAsync(departure.Alliance.Id);
            }
            if (!departure.Dissolved)
            {
                var alliance = departure.Alliance;
                await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mby, AllianceProtocol.BuildMemberGone(who)));
                await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(HasLeft, SessionContext.State.CharacterName ?? "")));
                if (departure.NewLeader != 0) await RankChangedAsync(alliance, departure.NewLeader);
            }
            Console.WriteLine($"[Alliance] {SessionContext.State.CharacterName} leaves «{departure.Alliance.Name}»" +
                              (departure.Dissolved ? ", which is dissolved." : "."));
        }

        /// <summary>
        /// What whoever is left without an alliance receives, in the capture's order: the banner
        /// emote taken (khj 98), «Ya no formas parte de la alianza» with its name, the empty mbt that
        /// empties the window, and the character redrawn without the tag.
        /// </summary>
        private static async Task GoneAsync(NetworkStream stream, AllianceStore.Alliance alliance, long characterId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Khj, AllianceProtocol.BuildEmote()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(NoLongerMember, alliance.Name)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mbt, Array.Empty<byte>()));
            await RedrawAsync(stream, characterId);
        }

        // ─── The window and the directory ───────────────────────────────────────

        /// <summary>The alliance window's sheet is asked for (mcf): the mcx with the card and the members.</summary>
        public static async Task SheetAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var alliance = who == 0 ? null : AllianceStore.AllianceOf(who);
            if (alliance == null) return;
            await WriteAsync(stream, InformationFrame(alliance));
        }

        // ─── Ranks and rights ───────────────────────────────────────────────────

        /// <summary>The ranks are asked for (mbg), when the rights window loads: the mbm.</summary>
        public static async Task RanksAsync(NetworkStream stream)
        {
            var alliance = MyAlliance();
            if (alliance == null) return;
            await AllianceRankService.Live.SendAsync(SessionContext.Current, alliance.Id);
        }

        /// <summary>
        /// Whether a member may touch a rank: the leader any, whoever holds «Gestionar los rangos y
        /// derechos inferiores al mío» those below his own.
        /// </summary>
        internal static bool MayManageRank(long who, AllianceStore.Rank rank)
        {
            var member = AllianceStore.MemberOf(who);
            if (member == null || rank == null || rank.AllianceId != member.AllianceId) return false;
            if (member.Rank == AllianceStore.RankLeader) return true;
            if (!AllianceStore.HasRight(who, AllianceData.ManageRanks)) return false;
            var own = AllianceStore.Ranks(member.AllianceId).FirstOrDefault(r => r.Id == member.Rank);
            return own != null && rank.Order > own.Order;
        }

        /// <summary>
        /// Creating a rank (mbj): f1 its icon, f3 its name, f4 the rank it is created after -- the
        /// window's m_currentRankIconId, name and m_parentRank, read in Ghidra.
        /// </summary>
        public static async Task CreateRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbj = ConnectionProtocol.ReadPayload(frame, Op.Mbj);
            var alliance = MyAlliance();
            if (mbj == null || alliance == null) return;

            int icon = 0, after = 0;
            string name = "";
            foreach (var field in ProtoMessage.Parse(mbj).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) icon = (int)field.VarIntValue;
                else if (field.FieldNumber == 4 && field.WireType == 0) after = (int)field.VarIntValue;
                else if (field.FieldNumber == 3 && field.WireType == 2) name = System.Text.Encoding.UTF8.GetString(field.BytesValue).Trim();
            }

            long who = SessionContext.State.CharacterId;
            var ranks = AllianceStore.Ranks(alliance.Id);
            var parent = ranks.FirstOrDefault(r => r.Id == after);
            var own = ranks.FirstOrDefault(r => r.Id == AllianceStore.RankOf(who));
            bool may = own != null && (own.Id == AllianceStore.RankLeader ||
                                       (AllianceStore.HasRight(who, AllianceData.ManageRanks) &&
                                        parent != null && parent.Order >= own.Order));
            if (!IsValidRankName(name) || parent == null || parent.Id == AllianceStore.RankNewcomer || !may)
            {
                await RefusedAsync(stream);
                return;
            }

            var created = AllianceStore.CreateRank(alliance.Id, name, icon, after);
            await RanksChangedAsync(alliance);
            Console.WriteLine($"[Alliance] Rank {created.Id} «{created.Name}» created in «{alliance.Name}».");
        }

        /// <summary>A rank name: what the editor lets through, not empty and not too long.</summary>
        internal static bool IsValidRankName(string name)
            => name.Length > 0 && name.Length <= AllianceStore.MaxRankNameLength &&
               !name.Contains(AllianceStore.RankKeyPrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>A rank renamed or given another icon (mbd): f1 the rank as the editor leaves it.</summary>
        public static async Task EditRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbd = ConnectionProtocol.ReadPayload(frame, Op.Mbd);
            var alliance = MyAlliance();
            if (mbd == null || alliance == null) return;
            var inner = ProtoMessage.Parse(mbd).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            if (inner == null) return;

            var sent = AllianceProtocol.ReadRank(inner.BytesValue);
            var rank = AllianceStore.Ranks(alliance.Id).FirstOrDefault(r => r.Id == sent.Id);
            string name = (sent.Name ?? "").Trim();
            if (rank == null || !rank.Flag || !IsValidRankName(name) ||
                !MayManageRank(SessionContext.State.CharacterId, rank))
            {
                await RefusedAsync(stream);
                return;
            }

            rank.Name = name;
            rank.Icon = sent.Icon;
            AllianceStore.SaveRank(rank);
            await RanksChangedAsync(alliance);
            Console.WriteLine($"[Alliance] Rank {rank.Id} of «{alliance.Name}» is now «{rank.Name}».");
        }

        /// <summary>
        /// Removing a rank (mbk): f1 the rank its members go to, f2 the rank removed. Neither the
        /// leader's nor the newcomers' can go.
        /// </summary>
        public static async Task RemoveRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbk = ConnectionProtocol.ReadPayload(frame, Op.Mbk);
            var alliance = MyAlliance();
            if (mbk == null || alliance == null) return;

            int replacement = 0, removed = 0;
            foreach (var field in ProtoMessage.Parse(mbk).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) replacement = (int)field.VarIntValue;
                else if (field.FieldNumber == 2 && field.WireType == 0) removed = (int)field.VarIntValue;
            }

            var ranks = AllianceStore.Ranks(alliance.Id);
            long who = SessionContext.State.CharacterId;
            var gone = ranks.FirstOrDefault(r => r.Id == removed);
            var into = ranks.FirstOrDefault(r => r.Id == replacement);
            if (gone == null || into == null || !MayManageRank(who, gone) ||
                (into.Id != AllianceStore.RankNewcomer && !MayManageRank(who, into)) ||
                !AllianceStore.RemoveRank(alliance.Id, removed, replacement))
            {
                await RefusedAsync(stream);
                return;
            }

            await RanksChangedAsync(alliance);
            foreach (var member in AllianceStore.Members(alliance.Id).Where(m => m.Rank == replacement))
                await RankChangedAsync(alliance, member.CharacterId);
            Console.WriteLine($"[Alliance] Rank {removed} of «{alliance.Name}» removed; its members are now {replacement}.");
        }

        /// <summary>
        /// Every rank as the window orders them (mbn): a rank dragged into another place. The order
        /// is taken for the ranks the member may manage, and the names and icons of the modifiable
        /// ones; the leader's stays first and the newcomers' last.
        /// </summary>
        public static async Task ReorderRanksAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbn = ConnectionProtocol.ReadPayload(frame, Op.Mbn);
            var alliance = MyAlliance();
            if (mbn == null || alliance == null) return;

            long who = SessionContext.State.CharacterId;
            var ranks = AllianceStore.Ranks(alliance.Id);
            var sentOrder = new Dictionary<int, int>();
            foreach (var field in ProtoMessage.Parse(mbn).Fields)
            {
                if (field.FieldNumber != 1 || field.WireType != 2) continue;
                var sent = AllianceProtocol.ReadRank(field.BytesValue);
                var rank = ranks.FirstOrDefault(r => r.Id == sent.Id);
                if (rank == null || !MayManageRank(who, rank)) continue;
                sentOrder[rank.Id] = sent.Order;
                if (rank.Flag && IsValidRankName((sent.Name ?? "").Trim()))
                {
                    rank.Name = sent.Name.Trim();
                    rank.Icon = sent.Icon;
                }
            }

            // The new order, as a ranking: what was sent for the ranks it may move, what they had
            // for the others; the leader first and the newcomers last whatever came.
            int position = 0;
            foreach (var rank in ranks
                         .OrderBy(r => r.Id == AllianceStore.RankLeader ? int.MinValue
                                     : r.Id == AllianceStore.RankNewcomer ? int.MaxValue
                                     : sentOrder.TryGetValue(r.Id, out int o) ? o : r.Order)
                         .ThenBy(r => r.Order))
            {
                rank.Order = position++;
                AllianceStore.SaveRank(rank);
            }

            await RanksChangedAsync(alliance);
        }

        /// <summary>A rank's rights (mbl): f2 the rights, f3 the rank. The leader's keep every one.</summary>
        public static async Task SetRightsAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbl = ConnectionProtocol.ReadPayload(frame, Op.Mbl);
            var alliance = MyAlliance();
            if (mbl == null || alliance == null) return;

            int rankId = 0;
            var rights = new List<int>();
            foreach (var field in ProtoMessage.Parse(mbl).Fields)
            {
                if (field.FieldNumber == 3 && field.WireType == 0) rankId = (int)field.VarIntValue;
                else if (field.FieldNumber == 2 && field.WireType == 0) rights.Add((int)field.VarIntValue);
                else if (field.FieldNumber == 2 && field.WireType == 2) rights.AddRange(PackedInts(field.BytesValue));
            }

            var rank = AllianceStore.Ranks(alliance.Id).FirstOrDefault(r => r.Id == rankId);
            if (rank == null || rank.Id == AllianceStore.RankLeader ||
                !MayManageRank(SessionContext.State.CharacterId, rank))
            {
                await RefusedAsync(stream);
                return;
            }

            var known = AllianceData.Rights.Select(r => r.Id).ToHashSet();
            rank.Rights = rights.Distinct().Where(r => known.Count == 0 || known.Contains(r)).ToList();
            AllianceStore.SaveRank(rank);
            await RanksChangedAsync(alliance);
            Console.WriteLine($"[Alliance] Rank {rank.Id} of «{alliance.Name}»: rights {string.Join(",", rank.Rights)}.");
        }

        private static IEnumerable<int> PackedInts(byte[] bytes)
        {
            int i = 0;
            while (i < bytes.Length)
            {
                ulong value = 0;
                int shift = 0;
                while (i < bytes.Length)
                {
                    byte b = bytes[i++];
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                yield return (int)value;
            }
        }

        /// <summary>
        /// Giving a member a rank (mbo): f1 the rank, f2 the member. It takes «Atribuir rangos
        /// inferiores al mío», and both the member's rank and the new one below one's own. The
        /// leader giving his own rank hands the alliance over, and takes the rank under it.
        /// </summary>
        public static async Task SetMemberRankAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbo = ConnectionProtocol.ReadPayload(frame, Op.Mbo);
            var alliance = MyAlliance();
            if (mbo == null || alliance == null) return;

            int rankId = 0;
            long target = 0;
            foreach (var field in ProtoMessage.Parse(mbo).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) rankId = (int)field.VarIntValue;
                else if (field.FieldNumber == 2 && field.WireType == 0) target = field.VarIntValue;
            }

            long who = SessionContext.State.CharacterId;
            var member = AllianceStore.MemberOf(target);
            var ranks = AllianceStore.Ranks(alliance.Id);
            var newRank = ranks.FirstOrDefault(r => r.Id == rankId);
            var oldRank = member == null ? null : ranks.FirstOrDefault(r => r.Id == member.Rank);
            if (member == null || member.AllianceId != alliance.Id || newRank == null || target == who) return;

            bool leader = AllianceStore.RankOf(who) == AllianceStore.RankLeader;
            if (newRank.Id == AllianceStore.RankLeader)
            {
                if (!leader) { await RefusedAsync(stream); return; }
                var second = ranks.Where(r => r.Id != AllianceStore.RankLeader).OrderBy(r => r.Order).First();
                AllianceStore.SetRank(target, AllianceStore.RankLeader);
                AllianceStore.SetRank(who, second.Id);
                await RankChangedAsync(alliance, target);
                await RankChangedAsync(alliance, who);
                Console.WriteLine($"[Alliance] «{alliance.Name}» handed over to {target}.");
                return;
            }

            if (!leader && (!AllianceStore.HasRight(who, AllianceData.AssignRanks) ||
                            !MayManageRankOrder(who, newRank) || !MayManageRankOrder(who, oldRank)))
            {
                await RefusedAsync(stream);
                return;
            }

            AllianceStore.SetRank(target, newRank.Id);
            await RankChangedAsync(alliance, target);
        }

        /// <summary>Whether a rank is below the member's own (giving ranks needs no rank right of its own).</summary>
        private static bool MayManageRankOrder(long who, AllianceStore.Rank rank)
        {
            var member = AllianceStore.MemberOf(who);
            if (member == null || rank == null) return false;
            var own = AllianceStore.Ranks(member.AllianceId).FirstOrDefault(r => r.Id == member.Rank);
            return own != null && rank.Order > own.Order;
        }

        /// <summary>The ranks have changed: the mbm, to everybody connected.</summary>
        private static Task RanksChangedAsync(AllianceStore.Alliance alliance)
            => AllianceRankService.Live.BroadcastAsync(alliance.Id);

        /// <summary>
        /// A member's rank has changed: his line to everybody (mbv), and to him his belonging again
        /// (mbz), which is where the client reads his own rank and its rights.
        /// </summary>
        private static async Task RankChangedAsync(AllianceStore.Alliance alliance, long characterId)
        {
            var row = RowOf(AllianceStore.MemberOf(characterId));
            if (row != null)
                await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mbv, AllianceProtocol.BuildMemberUpdate(row.Value)));
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session != null)
                await session.SendAsync(ConnectionProtocol.Push(Op.Mbz,
                    AllianceProtocol.BuildMembership(alliance, AllianceStore.RankOf(characterId))));
        }

        /// <summary>«No dispones de los derechos de alianza requeridos.»</summary>
        private static Task RefusedAsync(NetworkStream stream)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, MissingRight)));

        // ─── The alliance message and the bulletin ──────────────────────────────

        /// <summary>The alliance message (mcs): f1 the text. Everybody connected gets the mcm.</summary>
        public static Task SetMessageAsync(NetworkStream stream, byte[] frame)
            => SetNoticeAsync(stream, frame, Op.Mcs, AllianceStore.NoticeMessage, Op.Mdf);

        /// <summary>The bulletin (mdg): f1 the text. Everybody connected gets the mcr.</summary>
        public static Task SetBulletinAsync(NetworkStream stream, byte[] frame)
            => SetNoticeAsync(stream, frame, Op.Mdg, AllianceStore.NoticeBulletin, Op.Mco);

        /// <summary>
        /// A notice written: it takes «Modificar el mensaje de alianza», and without it the window is
        /// told why (mdf / mco, «no rights»).
        /// </summary>
        private static async Task SetNoticeAsync(NetworkStream stream, byte[] frame, string request, int kind,
                                                 string refusal)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, request);
            var alliance = MyAlliance();
            if (payload == null || alliance == null) return;

            string text = "";
            foreach (var field in ProtoMessage.Parse(payload).Fields)
                if (field.FieldNumber == 1 && field.WireType == 2) text = System.Text.Encoding.UTF8.GetString(field.BytesValue);

            long who = SessionContext.State.CharacterId;
            if (!AllianceStore.HasRight(who, AllianceData.EditMessage))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(refusal, AllianceProtocol.BuildNoticeRefused(AllianceProtocol.NoticeNoRights)));
                return;
            }

            var notice = new AllianceStore.Notice(text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                                  SessionContext.State.CharacterName ?? "", who);
            AllianceStore.SetNotice(alliance.Id, kind, notice);
            await AllianceNoticeService.Live.BroadcastAsync(alliance.Id, kind, notice);
        }

        // ─── Applications ───────────────────────────────────────────────────────

        /// <summary>«Tu candidatura ha sido enviada correctamente.»</summary>
        public const int ApplicationSent = 1572;

        /// <summary>«Has cancelado el envío de tu candidatura.»</summary>
        public const int ApplicationCancelled = 1573;

        /// <summary>The page of applications the window asks for when it does not say.</summary>
        private const int ApplicationsPage = 8;

        /// <summary>
        /// Applying to an alliance from its card (mai): f1 its uuid, f2 the message. The applicant
        /// gets his application back (lzx) and the line; whoever manages applications, the
        /// notification (mad) and the new line of the list (mbb).
        /// </summary>
        public static async Task ApplyAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mai = ConnectionProtocol.ReadPayload(frame, Op.Mai);
            if (mai == null) return;
            string uuid = "", message = "";
            foreach (var field in ProtoMessage.Parse(mai).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) uuid = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 2 && field.WireType == 2) message = System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }

            long who = SessionContext.State.CharacterId;
            var alliance = AllianceStore.ByUuid(uuid);
            if (who == 0 || alliance == null || AllianceStore.AllianceOf(who) != null) return;
            var profile = AllianceStore.ProfileOf(alliance.Id);
            if (SessionContext.State.CharacterLevel < Math.Max(AllianceStore.MinimumLevel, profile.MinLevel))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, JoinTooLow)));
                return;
            }

            var previous = AllianceStore.ApplicationOf(who);
            var application = AllianceStore.Apply(who, alliance.Id, message);
            if (previous != null && previous.AllianceId != alliance.Id)
                await ApplicationGoneAsync(previous, who);

            var row = RowOf(application);
            if (row == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzx, AllianceProtocol.BuildOwnApplication(row.Value, alliance)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(ApplicationSent)));
            await TellManagersAsync(alliance, ConnectionProtocol.Push(Op.Mad,
                AllianceProtocol.BuildApplicationArrived(who, row.Value.Name)));
            await TellManagersAsync(alliance, ConnectionProtocol.Push(Op.Mbb,
                AllianceProtocol.BuildApplicationsMoved(AllianceProtocol.ApplicationAdded, row.Value, 0)));
            Console.WriteLine($"[Alliance] {row.Value.Name} applies to «{alliance.Name}».");
        }

        /// <summary>Rewriting one's application (mae): f1 the message, f2 the alliance's uuid.</summary>
        public static async Task RewriteApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mae = ConnectionProtocol.ReadPayload(frame, Op.Mae);
            if (mae == null) return;
            string message = "", uuid = "";
            foreach (var field in ProtoMessage.Parse(mae).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2) message = System.Text.Encoding.UTF8.GetString(field.BytesValue);
                else if (field.FieldNumber == 2 && field.WireType == 2) uuid = System.Text.Encoding.UTF8.GetString(field.BytesValue);
            }

            long who = SessionContext.State.CharacterId;
            var alliance = AllianceStore.ByUuid(uuid);
            var previous = AllianceStore.ApplicationOf(who);
            if (alliance == null || previous == null || previous.AllianceId != alliance.Id) return;

            var row = RowOf(AllianceStore.Apply(who, alliance.Id, message));
            if (row == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzx, AllianceProtocol.BuildOwnApplication(row.Value, alliance)));
            await TellManagersAsync(alliance, ConnectionProtocol.Push(Op.Mbb,
                AllianceProtocol.BuildApplicationsMoved(AllianceProtocol.ApplicationRewritten, row.Value, 0)));
        }

        /// <summary>Deleting one's application (mau): mav says it went, and the list loses it.</summary>
        public static async Task DeleteApplicationAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var application = AllianceStore.ApplicationOf(who);
            if (application == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Mav, AllianceProtocol.BuildApplicationDeleted(false)));
                return;
            }

            AllianceStore.DropApplication(who);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mav, AllianceProtocol.BuildApplicationDeleted(true)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(ApplicationCancelled)));
            await ApplicationGoneAsync(application, who);
        }

        /// <summary>An application no longer pending, for whoever manages its alliance's.</summary>
        private static async Task ApplicationGoneAsync(AllianceStore.Application application, long handledBy)
        {
            var alliance = AllianceStore.ById(application.AllianceId);
            var row = RowOf(application);
            if (alliance == null || row == null) return;
            await TellManagersAsync(alliance, ConnectionProtocol.Push(Op.Mbb,
                AllianceProtocol.BuildApplicationsMoved(AllianceProtocol.ApplicationGone, row.Value, handledBy)));
        }

        /// <summary>
        /// A page of the applications (mah): f2 after which one, f3 how many -- the window asks for
        /// 8 from the start, then 8 after the last it shows. Answered with the maz.
        /// </summary>
        public static async Task ApplicationsAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mah = ConnectionProtocol.ReadPayload(frame, Op.Mah);
            var alliance = MyAlliance();
            if (alliance == null) return;

            long after = 0, asked = ApplicationsPage;
            if (mah != null)
            {
                foreach (var field in ProtoMessage.Parse(mah).Fields)
                {
                    if (field.FieldNumber == 2 && field.WireType == 0) after = field.VarIntValue;
                    else if (field.FieldNumber == 3 && field.WireType == 0) asked = field.VarIntValue;
                }
            }
            if (asked <= 0) asked = ApplicationsPage;

            if (!AllianceStore.HasRight(SessionContext.State.CharacterId, AllianceData.ManageApplications))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Maz, Array.Empty<byte>()));
                return;
            }

            var all = AllianceStore.Applications(alliance.Id);
            var page = all.Where(a => a.WhenMs > after).Take((int)asked).Select(RowOf)
                          .Where(r => r != null).Select(r => r.Value).ToList();
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Maz,
                AllianceProtocol.BuildApplications(all.Count, page, asked, after)));
        }

        /// <summary>
        /// Answering an application (maf): f1 accepted, f2 the applicant. It takes «Gestionar las
        /// candidaturas e invitaciones». Accepted, the applicant joins as a newcomer; either way he
        /// is told (maq) if he is connected, and the list loses the application.
        /// </summary>
        public static async Task AnswerApplicationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] maf = ConnectionProtocol.ReadPayload(frame, Op.Maf);
            var alliance = MyAlliance();
            if (maf == null || alliance == null) return;

            bool accepted = false;
            long applicant = 0;
            foreach (var field in ProtoMessage.Parse(maf).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) accepted = field.VarIntValue != 0;
                else if (field.FieldNumber == 2 && field.WireType == 0) applicant = field.VarIntValue;
            }

            long who = SessionContext.State.CharacterId;
            var application = AllianceStore.ApplicationOf(applicant);
            if (application == null || application.AllianceId != alliance.Id) return;
            if (!AllianceStore.HasRight(who, AllianceData.ManageApplications))
            {
                await RefusedAsync(stream);
                return;
            }

            AllianceStore.DropApplication(applicant);
            await ApplicationGoneAsync(application, who);

            bool joins = accepted && AllianceStore.AllianceOf(applicant) == null;
            if (joins) AllianceStore.Join(applicant, alliance.Id);

            var session = SessionRegistry.FindByCharacter(applicant);
            if (session?.Stream != null)
            {
                using (SessionContext.Push(session))
                {
                    await WriteAsync(session.Stream, ConnectionProtocol.Push(Op.Maq,
                        AllianceProtocol.BuildApplicationAnswered(alliance, accepted)));
                    if (joins) await WelcomeAsync(session, alliance);
                }
            }
            else if (joins)
            {
                var row = RowOf(AllianceStore.MemberOf(applicant));
                if (row != null)
                    await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mbv, AllianceProtocol.BuildMemberUpdate(row.Value)));
            }
            Console.WriteLine($"[Alliance] Application of {applicant} to «{alliance.Name}» " +
                              (accepted ? "accepted." : "refused."));
        }

        /// <summary>An alliance's card from the directory (mck): f1 its uuid; the mcl with its leader.</summary>
        public static async Task CardAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mck = ConnectionProtocol.ReadPayload(frame, Op.Mck);
            if (mck == null) return;
            string uuid = "";
            foreach (var field in ProtoMessage.Parse(mck).Fields)
                if (field.FieldNumber == 1 && field.WireType == 2) uuid = System.Text.Encoding.UTF8.GetString(field.BytesValue);

            var alliance = AllianceStore.ByUuid(uuid);
            if (alliance == null) return;
            var leader = RowOf(AllianceStore.LeaderOf(alliance.Id));
            var shown = leader == null
                ? Array.Empty<(AllianceProtocol.MemberRow, AllianceStore.Rank)>()
                : new[] { (leader.Value, AllianceStore.Ranks(alliance.Id).FirstOrDefault(r => r.Id == leader.Value.Member.Rank)) };
            var prisms = PrismStore.OfAlliance(alliance.Id).Select(p => p.SubAreaId).ToList();
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mcl, AllianceProtocol.BuildCardView(
                alliance, CardOf(alliance), shown, prisms, leader?.Member.CharacterId ?? 0)));
        }

        // ─── Invitations ────────────────────────────────────────────────────────

        /// <summary>«No tienes los derechos necesarios para invitar a otros jugadores a que se unan a tu alianza.»</summary>
        public const int InviteNoRights = 389;

        /// <summary>«Acción imposible: este jugador no cumple las condiciones para poder invitarle a formar parte de una alianza.»</summary>
        public const int InviteNotEligible = 390;

        /// <summary>The invitations waiting for an answer: the invited, and who invites into which alliance.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long Inviter, long AllianceId)> _invitations = new();

        /// <summary>
        /// Inviting a player from his menu (mbr): f1 the player. It takes «Gestionar las candidaturas e
        /// invitaciones»; the player must be connected, of level 50 and without an alliance. He gets
        /// the popup (mbs) and whoever invites waits for him (mbq 1).
        /// </summary>
        public static async Task InviteAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbr = ConnectionProtocol.ReadPayload(frame, Op.Mbr);
            var alliance = MyAlliance();
            if (mbr == null || alliance == null) return;

            long target = 0;
            foreach (var field in ProtoMessage.Parse(mbr).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) target = field.VarIntValue;

            long who = SessionContext.State.CharacterId;
            if (!AllianceStore.HasRight(who, AllianceData.ManageApplications))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InviteNoRights)));
                return;
            }

            var invited = SessionRegistry.FindByCharacter(target);
            var character = DatabaseManager.GetCharacterById(target);
            if (invited?.Stream == null || character == null || target == who ||
                AllianceStore.AllianceOf(target) != null || character.Level < AllianceStore.MinimumLevel ||
                _invitations.ContainsKey(target))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InviteNotEligible)));
                return;
            }

            string inviterName = SessionContext.State.CharacterName ?? "";
            _invitations[target] = (who, alliance.Id);
            await invited.SendAsync(ConnectionProtocol.Push(Op.Mbs, AllianceProtocol.BuildInvited(inviterName, alliance)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mbq,
                AllianceProtocol.BuildInvitationForInviter(character.Name, inviterName, AllianceProtocol.InvitationSent)));
            Console.WriteLine($"[Alliance] {inviterName} invites {character.Name} into «{alliance.Name}».");
        }

        /// <summary>
        /// The answer to an invitation (mbu): f1 accepted. From the invited it accepts or refuses;
        /// from whoever invited, it is his «cancel». Both popups close (mbq / mcb 2 or 3).
        /// </summary>
        public static async Task AnswerInvitationAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mbu = ConnectionProtocol.ReadPayload(frame, Op.Mbu);
            if (mbu == null) return;
            bool accepted = false;
            foreach (var field in ProtoMessage.Parse(mbu).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) accepted = field.VarIntValue != 0;

            long who = SessionContext.State.CharacterId;
            long invitedId = who;
            if (!_invitations.ContainsKey(who))
            {
                // Whoever invited, cancelling.
                invitedId = _invitations.FirstOrDefault(i => i.Value.Inviter == who).Key;
                accepted = false;
            }
            if (invitedId == 0 || !_invitations.TryRemove(invitedId, out var invitation)) return;

            var alliance = AllianceStore.ById(invitation.AllianceId);
            var invitedSession = SessionRegistry.FindByCharacter(invitedId);
            var inviterSession = SessionRegistry.FindByCharacter(invitation.Inviter);
            string invitedName = DatabaseManager.GetCharacterById(invitedId)?.Name ?? "";
            string inviterName = DatabaseManager.GetCharacterById(invitation.Inviter)?.Name ?? "";
            bool joins = accepted && alliance != null && AllianceStore.AllianceOf(invitedId) == null;
            int state = joins ? AllianceProtocol.InvitationAccepted : AllianceProtocol.InvitationRefused;

            if (inviterSession != null)
                await inviterSession.SendAsync(ConnectionProtocol.Push(Op.Mbq,
                    AllianceProtocol.BuildInvitationForInviter(invitedName, inviterName, state)));
            if (invitedSession?.Stream != null)
            {
                await invitedSession.SendAsync(ConnectionProtocol.Push(Op.Mcb, AllianceProtocol.BuildInvitationForInvited(state)));
                if (joins)
                {
                    AllianceStore.Join(invitedId, alliance.Id);
                    using (SessionContext.Push(invitedSession))
                    {
                        await WelcomeAsync(invitedSession, alliance);
                    }
                }
            }
            Console.WriteLine($"[Alliance] Invitation of {invitedName} into «{alliance?.Name}»: " +
                              (joins ? "accepted." : "refused."));
        }

        /// <summary>Whoever disconnects with an invitation pending leaves it unanswered: it goes.</summary>
        private static async Task ForgetInvitationsAsync(long characterId)
        {
            foreach (var pending in _invitations.Where(i => i.Key == characterId || i.Value.Inviter == characterId).ToList())
            {
                if (!_invitations.TryRemove(pending.Key, out var invitation)) continue;
                var other = SessionRegistry.FindByCharacter(pending.Key == characterId ? invitation.Inviter : pending.Key);
                if (other == null) continue;
                await other.SendAsync(pending.Key == characterId
                    ? ConnectionProtocol.Push(Op.Mbq, AllianceProtocol.BuildInvitationForInviter(
                          DatabaseManager.GetCharacterById(characterId)?.Name ?? "", "", AllianceProtocol.InvitationRefused))
                    : ConnectionProtocol.Push(Op.Mcb, AllianceProtocol.BuildInvitationForInvited(AllianceProtocol.InvitationRefused)));
            }
        }

        // ─── The recruitment card ───────────────────────────────────────────────

        /// <summary>The recruitment card is asked for (mas): the mar.</summary>
        public static async Task RecruitmentCardAsync(NetworkStream stream)
        {
            var alliance = MyAlliance();
            if (alliance == null) return;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mar, RecruitmentCardOf(alliance)));
        }

        /// <summary>
        /// The recruitment card as the window leaves it (lzy): f1 the lmn. It takes «Gestionar el
        /// reclutamiento»; the card goes back (mar) as it is now, and to the directory.
        /// </summary>
        public static async Task SetRecruitmentAsync(NetworkStream stream, byte[] frame)
        {
            byte[] lzy = ConnectionProtocol.ReadPayload(frame, Op.Lzy);
            var alliance = MyAlliance();
            if (lzy == null || alliance == null) return;
            var inner = ProtoMessage.Parse(lzy).Fields.Find(f => f.FieldNumber == 1 && f.WireType == 2);
            if (inner == null) return;

            if (!AllianceStore.HasRight(SessionContext.State.CharacterId, AllianceData.ManageRecruitment))
            {
                await RefusedAsync(stream);
                return;
            }

            var profile = AllianceStore.ProfileOf(alliance.Id);
            AllianceProtocol.ReadRecruitment(inner.BytesValue, profile);
            profile.MinLevel = Math.Max(AllianceStore.MinimumLevel, profile.MinLevel);
            profile.WhenMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            AllianceStore.SaveProfile(profile);
            await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mar, RecruitmentCardOf(alliance)));
            Console.WriteLine($"[Alliance] Recruitment of «{alliance.Name}»: {profile.Recruitment}, level {profile.MinLevel}.");
        }

        private static byte[] RecruitmentCardOf(AllianceStore.Alliance alliance)
        {
            var leader = AllianceStore.LeaderOf(alliance.Id);
            string leaderName = leader == null ? "" : DatabaseManager.GetCharacterById(leader.CharacterId)?.Name ?? "";
            return AllianceProtocol.BuildRecruitmentCard(alliance, AllianceStore.ProfileOf(alliance.Id), leaderName);
        }

        // ─── Warning when a member connects ─────────────────────────────────────

        /// <summary>«Un miembro de tu alianza, {{player,{0},{1}}}, está en línea.»</summary>
        public const int MemberOnline = 1580;

        /// <summary>
        /// The option «avisar cuando un miembro se conecte» (mdj on, mda off). The client sends it
        /// on every login, right behind the mbz, so the server always knows it.
        /// </summary>
        public static void SetConnectionWarnings(bool on)
        {
            long who = SessionContext.State.CharacterId;
            if (who != 0) AllianceStore.SetWarnsOnConnection(who, on);
        }

        /// <summary>
        /// The alliance window's «Alliance fight» switch (mes { f1 on }): taking part in AvA, kept
        /// per character. The switch only moves with the answer, the hon (eht.gmh raises the event
        /// AllianceUI.OnPvpAvaStateChange listens to); unanswered, it stayed off however often it
        /// was clicked. From then on the map's actor carries the status too.
        /// </summary>
        public static async Task AvaSwitchAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mes = ConnectionProtocol.ReadPayload(frame, Op.Mes);
            long who = SessionContext.State.CharacterId;
            if (mes == null || who == 0 || AllianceStore.AllianceOf(who) == null) return;

            bool on = false;
            foreach (var field in ProtoMessage.Parse(mes).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) on = field.VarIntValue != 0;

            // Not in the middle of a conquest (info 1/399): the switch goes back where it was.
            if (ConquestHandler.InWarZone(who))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, ConquestHandler.NoSwitchInAWar)));
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hon,
                    AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AggressableStatus(who))));
                return;
            }

            AllianceStore.SetTakesPartInAva(who, on);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hon,
                AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AggressableStatus(who))));
        }

        /// <summary>The directory opens (mba): the empty mag the capture answers with.</summary>
        public static Task DirectoryOpenedAsync(NetworkStream stream)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Mag, Array.Empty<byte>()));

        /// <summary>
        /// A page of the directory (mde): f5 where it starts, f7 a name to look for, f10 how many.
        /// The capture asks {f4 2, f8 200, f9 50, f10 40} and gets 40 alliances of 1,999.
        /// </summary>
        public static async Task DirectoryAsync(NetworkStream stream, byte[] frame)
        {
            byte[] mde = ConnectionProtocol.ReadPayload(frame, Op.Mde);
            if (mde == null) return;

            long offset = 0, size = DefaultPageSize;
            string search = "";
            foreach (var field in ProtoMessage.Parse(mde).Fields)
            {
                if (field.FieldNumber == 5 && field.WireType == 0) offset = field.VarIntValue;
                else if (field.FieldNumber == 10 && field.WireType == 0) size = field.VarIntValue;
                else if (field.FieldNumber == 7 && field.WireType == 2) search = System.Text.Encoding.UTF8.GetString(field.BytesValue).Trim();
            }
            if (size <= 0) size = DefaultPageSize;

            var found = AllianceStore.All()
                .Where(a => search.Length == 0 ||
                            a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            a.Tag.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var page = found.Skip((int)Math.Max(0, offset)).Take((int)size)
                .Select(a => new AllianceProtocol.DirectoryEntry(a, CardOf(a)))
                .ToList();

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Mcv, AllianceProtocol.BuildDirectory(page, found.Count, size)));
        }

        // ─── World entry ────────────────────────────────────────────────────────

        /// <summary>
        /// A member entering the world: the ranks and belonging (mbz), silently -- the mca is
        /// joining, and would print "you have joined" at every login --, the message and the
        /// bulletin. The others see him connected (mbv), and whoever asked to be warned is. One
        /// without an alliance but with an application gets it back, for the directory.
        /// </summary>
        public static async Task OnWorldEntryAsync(GameSession session)
        {
            using var context = SessionContext.Push(session);
            long characterId = session.CharacterId;
            var alliance = AllianceStore.AllianceOf(characterId);
            if (alliance == null)
            {
                var application = AllianceStore.ApplicationOf(characterId);
                var applied = application == null ? null : AllianceStore.ById(application.AllianceId);
                var row = application == null ? null : RowOf(application);
                if (applied != null && row != null)
                    await session.SendAsync(ConnectionProtocol.Push(Op.Lzx, AllianceProtocol.BuildOwnApplication(row.Value, applied)));
                return;
            }

            await AllianceRankService.Live.SendAsync(session, alliance.Id);
            await session.SendAsync(ConnectionProtocol.Push(Op.Mbz,
                AllianceProtocol.BuildMembership(alliance, AllianceStore.RankOf(characterId))));
            await SendNoticesAsync(session, alliance);
            await ConnectionChangedAsync(alliance, characterId, online: true);
        }

        /// <summary>A member who disconnects: the others see him so; his invitations go.</summary>
        public static async Task DisconnectedAsync(long characterId)
        {
            await ForgetInvitationsAsync(characterId);
            var alliance = AllianceStore.AllianceOf(characterId);
            if (alliance != null) await ConnectionChangedAsync(alliance, characterId, online: false);
        }

        /// <summary>
        /// A member's connection seen by the others: his line with his state (mbv), and on
        /// connecting, «Un miembro de tu alianza, X, está en línea» to whoever wants it.
        /// </summary>
        private static async Task ConnectionChangedAsync(AllianceStore.Alliance alliance, long characterId, bool online)
        {
            var row = RowOf(AllianceStore.MemberOf(characterId), online);
            if (row == null) return;
            await TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Mbv, AllianceProtocol.BuildMemberUpdate(row.Value)), characterId);
            if (!online) return;

            byte[] warning = ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(MemberOnline, row.Value.Name, characterId.ToString()));
            foreach (var member in AllianceStore.Members(alliance.Id))
            {
                if (member.CharacterId == characterId || !AllianceStore.WarnsOnConnection(member.CharacterId)) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(warning);
            }
        }

        /// <summary>
        /// A line said on the alliance channel (3) by a member: to the other members connected. It
        /// went back only to whoever said it.
        /// </summary>
        public static async Task<int> SayAsync(long characterId, byte[] line)
        {
            var alliance = AllianceStore.AllianceOf(characterId);
            return alliance == null ? 0 : await TellMembersAsync(alliance, line, characterId);
        }

        // ─── Shared pieces ──────────────────────────────────────────────────────

        /// <summary>The alliance of whoever is speaking, or null.</summary>
        private static AllianceStore.Alliance MyAlliance()
        {
            long who = SessionContext.State.CharacterId;
            return who == 0 ? null : AllianceStore.AllianceOf(who);
        }

        /// <summary>A member's line, with his sheet; online as the registry says unless told.</summary>
        internal static AllianceProtocol.MemberRow? RowOf(AllianceStore.Member member, bool? online = null)
        {
            if (member == null) return null;
            var character = DatabaseManager.GetCharacterById(member.CharacterId);
            if (character == null) return null;
            return new AllianceProtocol.MemberRow(member, character.Name, character.Level, character.AccountId,
                                                  character.Breed, character.Sex,
                                                  Achievements.PointsOf(member.CharacterId),
                                                  online ?? SessionRegistry.FindByCharacter(member.CharacterId) != null);
        }

        /// <summary>An application with the applicant's sheet.</summary>
        internal static AllianceProtocol.ApplicationRow? RowOf(AllianceStore.Application application)
        {
            if (application == null) return null;
            var character = DatabaseManager.GetCharacterById(application.CharacterId);
            if (character == null) return null;
            return new AllianceProtocol.ApplicationRow(application, character.Name, character.Level, character.AccountId,
                                                       character.Breed, character.Sex,
                                                       SessionRegistry.FindByCharacter(application.CharacterId) != null);
        }

        /// <summary>A frame for every member connected but one. Returns how many got it.</summary>
        internal static async Task<int> TellMembersAsync(AllianceStore.Alliance alliance, byte[] frame, long except = 0)
        {
            int told = 0;
            foreach (var member in AllianceStore.Members(alliance.Id))
            {
                if (member.CharacterId == except) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null) continue;
                await session.SendAsync(frame);
                told++;
            }
            return told;
        }

        /// <summary>A frame for every member connected who manages applications.</summary>
        private static async Task TellManagersAsync(AllianceStore.Alliance alliance, byte[] frame)
        {
            foreach (var member in AllianceStore.Members(alliance.Id))
            {
                if (!AllianceStore.HasRight(member.CharacterId, AllianceData.ManageApplications)) continue;
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session != null) await session.SendAsync(frame);
            }
        }

        /// <summary>
        /// The alliance's card: its recruitment, its founding, how many it is, the territories its
        /// prisms hold and its tax collectors.
        /// </summary>
        internal static Pb CardOf(AllianceStore.Alliance alliance)
        {
            var leader = AllianceStore.LeaderOf(alliance.Id);
            string leaderName = leader == null ? "" : DatabaseManager.GetCharacterById(leader.CharacterId)?.Name ?? "";
            int territories = PrismStore.OfAlliance(alliance.Id).Select(p => p.SubAreaId).Distinct().Count();
            return AllianceProtocol.Card(alliance, AllianceStore.ProfileOf(alliance.Id), leaderName,
                                         AllianceStore.Members(alliance.Id).Count, territories,
                                         TaxCollectorStore.OfAlliance(alliance.Id).Count);
        }

        /// <summary>The sheet (mcx) of an alliance: its tax collectors, its card and a line per member.</summary>
        public static byte[] InformationFrame(AllianceStore.Alliance alliance)
        {
            var rows = AllianceStore.Members(alliance.Id).Select(m => RowOf(m)).Where(r => r != null).Select(r => r.Value);
            return ConnectionProtocol.Push(Op.Mcx, AllianceProtocol.BuildInformation(alliance, CardOf(alliance), rows,
                                                                                    TaxCollectorHandler.InformationOf(alliance),
                                                                                    PrismHandler.InformationOf(alliance)));
        }

        /// <summary>
        /// The character drawn again (jsn) -- for him and for whoever shares his map --, so the
        /// alliance's tag appears or goes under his name.
        /// </summary>
        private static async Task RedrawAsync(NetworkStream stream, long characterId)
        {
            var session = SessionRegistry.FindByCharacter(characterId);
            var character = DatabaseManager.GetCharacterById(characterId);
            if (session == null || character == null) return;

            byte[] jsn = ConnectionProtocol.Push(Op.Jsn, ConnectionProtocol.BuildActorRefreshed(
                character, session.State.CellId, session.State.Orientation, session.AccountId));
            await WriteAsync(stream, jsn);
            await SessionRegistry.BroadcastToMapAsync(session.MapId, jsn, session.Id);
        }
    }
}
