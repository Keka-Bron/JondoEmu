using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Protocol;
using Game = Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Game-phase message builders, including the character list and world entry.
    ///
    /// The whole shape of these messages was taken from the real 3.6.10.10 client captures
    /// under Wireshark captures from real game/Authentication-Server-Character. Nothing here
    /// is built from memory: if a field does not show up in a capture, we do not send it.
    ///
    /// Two different protocols share the same port:
    ///
    ///   1. Connection server. Bare messages, no envelope. The client presents the account
    ///      token and receives the server list; then it picks one and receives a ticket
    ///      along with the address to reconnect to.
    ///   2. Game server. Messages wrapped in type.ankama.com/xxx. The client presents the
    ///      ticket (kqz) and receives the burst that ends with the character list (kvi).
    ///
    /// In 3.6.10.10 the messages pushed by the server travel in field 1 of the frame
    /// (390 out of 391 in the enter-world capture); field 3 is the one the client uses.
    /// </summary>
    public static class ConnectionProtocol
    {
        public const string UriPrefix = "type.ankama.com/";

        // ─── Envelope ───────────────────────────────────────────────────────────

        /// <summary>
        /// Wraps a message pushed by the server: f1 { f1 { f1: type_url, f2: payload } }.
        /// </summary>
        public static byte[] Push(string opcode, byte[]? payload = null)
        {
            var any = Pb.New().Str(1, UriPrefix + opcode);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);

            return Pb.New()
                .Msg(1, Pb.New().Bytes(1, any.Build()))
                .Build();
        }

        // ─── Game server: welcome burst ─────────────────────────────────────────

        /// <summary>
        /// Sends welcome messages through the owning session. Ordinary login includes
        /// the character-list refresh; a resumable fight waits for the list request.
        /// </summary>
        internal static async System.Threading.Tasks.Task SendWelcomeAsync(GameSession session,
            IReadOnlyList<DbCharacter> characters, bool withList)
        {
            await session.SendAsync(new Game.AuthenticationTicketAcceptedEvent());
            await session.SendAsync(ServerTimeMessages.Snapshot());
            // Community is not a subscription marker. The unnamed settings extra is
            // omitted by policy; see docs/populated-unmapped-fields.md#welcome-settings-omission-policy.
            await session.SendAsync(new Game.ServerSettingsEvent(
                ArenaLeaveBanTime: 30, GameType: Game.ServerType.Classical,
                HasFreeAutopilot: true, IsMonoAccount: false, Community: 0,
                Language: session.State.Language));
            await session.SendAsync(new Game.OptionalFeaturesEvent(ActiveFeatures.Select(x => (int)x).ToArray()));
            // The account's own rank: the client keeps its console and admin menu for the staff's.
            // Roles 1 to 5 are the protocol's ranks in the same order (RolePlayer .. RoleAdmin).
            int role = Jondo.Unity.Server.DatabaseManager.GetAccountRole(session.AccountId);
            var rank = role is >= (int)Game.Hierarchy.RolePlayer and <= (int)Game.Hierarchy.RoleAdmin
                ? (Game.Hierarchy)role
                : Game.Hierarchy.RolePlayer;
            await session.SendAsync(new Game.AccountCapabilitiesEvent(Game.CharacterCreationRights.Allowed,
                true, rank));
            // ShowConsole: the client's own staff console, for a moderator and above.
            await session.SendAsync(new Game.AccountRightsEvent(new Game.AccountRights(
                ShowConsole: rank >= Game.Hierarchy.RoleModerator, false, false, false)));
            await session.SendAsync(new Game.TrustStatusEvent(Certified: true));
            await session.SendAsync(new Game.ServerSessionReadyEvent());
            await session.SendAsync(new Game.AccountSubscriptionElapsedDurationUpdateEvent(SubscriptionElapsedSeconds));
            if (withList) await SendCharacterListAsync(session, characters);
        }

        /// <summary>
        /// Queue updates, the semantic list, then current gifts. Sending only the list
        /// after creation once left the client selecting its previously cached character.
        /// </summary>
        internal static System.Threading.Tasks.Task SendCharacterListAsync(GameSession session,
            IReadOnlyList<DbCharacter> characters)
            => CharacterListService.Live.SendAsync(session, characters);

        /// <summary>
        /// Elapsed subscribed seconds are not stored by the account backend.
        /// Zero is the explicit default, not a calculation from subscription expiry.
        /// </summary>
        private const long SubscriptionElapsedSeconds = 0;

        /// <summary>
        /// List of features enabled on the server. Copied verbatim from the capture, with no
        /// interpretation: they are opaque identifiers.
        /// </summary>
        private static readonly long[] ActiveFeatures =
            { 3, 7, 13, 20, 23, 105, 124, 125, 126, 136, 143, 145, 150 };

        /// <summary>The language the client is launched with.</summary>
        public const string ClientLanguage = "es";

        // ─── Character list (kvi) ───────────────────────────────────────────────

        /// <summary>
        /// The order the real server sends the list in, which the client shows as it comes.
        /// </summary>
        /// <remarks>
        /// Measured in «desde eleccion servidor a eleccion personaje»: the four characters of a
        /// server come out by their last connection, newest first, while the server-selection reply
        /// carries the same four in another order. In «crear personaje - borrar personaje» the one
        /// just created, already dated, heads the list. A character with no date goes last; equal
        /// dates keep the order they came in.
        /// </remarks>
        internal static List<DbCharacter> MostRecentFirst(IEnumerable<DbCharacter> characters)
            => characters.OrderByDescending(c => System.DateTimeOffset.TryParse(c.LastConnection,
                       System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                       out var at) ? at : System.DateTimeOffset.MinValue)
                   .ToList();

        // ─── World: map load ────────────────────────────────────────────────────

        /// <summary>
        /// Closes a map load (lva). It carries nothing: it is the "that is every actor" mark.
        ///
        /// It goes immediately behind jss in every capture where a map is loaded — the four
        /// movement ones, the entry into the world and the tutorial. Without it the client never
        /// finishes loading the map: it waits about two seconds, asks again with knm, kno and kny,
        /// and starts over.
        /// </summary>
        public static byte[] BuildActorsComplete() => Push(Op.Lva);

        // ─── World: life regeneration ────────────────────────────────────────

        /// <summary>
        /// The regeneration rate the real server hands out on every return to roleplay, in
        /// tenths of a second per life point: 135 of the 143 ktz in the captures carry it.
        /// </summary>
        /// <remarks>
        /// The other eight carry 1 -- the Trool fair and one world entry with a guild raid -- and
        /// what makes a rate fast is not measured, so nothing here decides it.
        /// </remarks>
        public const int RegenerationRate = 5;

        /// <summary>One regeneration tick, in milliseconds: the rate is in tenths of a second.</summary>
        public const int RegenerationTickMs = RegenerationRate * 100;

        /// <summary>
        /// The rate handed out now: <see cref="RegenerationRate"/>, faster on a day the Almanax says
        /// «La regeneración de puntos de vida aumenta un 300%» -- 5 tenths become 1, the rate eight of
        /// the captures' ktz carry.
        /// </summary>
        public static int RegenerationRateNow()
        {
            int bonus = Managers.Almanax.BonusPercent(Managers.Almanax.BonusType.LifeRegeneration, Managers.Almanax.Where.Anywhere);
            return bonus <= 0 ? RegenerationRate : Math.Max(1, (int)Math.Round(RegenerationRate * 100.0 / (100 + bonus)));
        }

        /// <summary>
        /// Life regeneration begins (ktz): f1 the rate. Right behind every "kml kmp" back to
        /// roleplay -- the world entry replays the captured one, and the fight end builds this.
        /// </summary>
        public static byte[] BuildRegenerationStarted(int rate)
            => Push(Op.Ktz, Pb.New().Var(1, rate).Build());

        /// <summary>
        /// Life regeneration ends (kuq): f1 the life, f2 the ticks the counter had run, f4 the
        /// maximum. Between the lqu and the lva of the tactical map load, at every fight entry.
        /// </summary>
        /// <remarks>
        /// This is what stops the client's own counter. Without it the counter started at the
        /// world entry keeps adding one point every tick THROUGH the fight, to the local player's
        /// bar only, which is what "the Ocra recovers life tick by tick" was: the roleplay
        /// regeneration drawn over a fight. Measured against the two challenge captures of the
        /// 9th of August: 08db2810970120bb29 is {5211, 151, 5307}.
        /// </remarks>
        public static byte[] BuildRegenerationEnded(int life, int ticks, int maxLife)
            => Push(Op.Kuq, Pb.New().Var(1, life).VarIfNotZero(2, ticks).Var(4, maxLife).Build());

        /// <summary>
        /// How many ticks a counter started at <paramref name="startedUtc"/> has run by
        /// <paramref name="nowUtc"/>: the f2 of the kuq. Zero when it was never started.
        /// </summary>
        public static int RegenerationTicksSince(DateTime startedUtc, DateTime nowUtc)
        {
            if (startedUtc == default || nowUtc <= startedUtc) return 0;
            return (int)Math.Min(int.MaxValue, (long)(nowUtc - startedUtc).TotalMilliseconds / (RegenerationRateNow() * 100));
        }

        // ─── World: actors on the map ───────────────────────────────────────────

        /// <summary>
        /// A monster's look as a group carries it: its colours, bones, scale and skins, the same
        /// block an NPC's look is (<see cref="MapActorMessages.NpcLook"/>).
        /// </summary>
        /// <remarks>
        /// It used to be the bones alone, read off a belief that the captures sent nothing else.
        /// They do: of the 2,372 monster groups in the 756 captures' jss, 1,799 carry their scale
        /// (f5) and 113 their colours (f1) -- "{706|...|115}" goes out with f5 = 115. Without it the
        /// client draws every monster at 100: the Conde Kontatrás, "{2069|||150}", came out a third
        /// smaller than a person, and 2,478 other monsters with a scale of their own were drawn off
        /// it too.
        /// </remarks>
        internal static Pb MonsterLook(string look, bool hardcore)
            => CharacterLookBuilder.WriteLook(MonsterMessages.Look(look, hardcore));

        // ─── World: spells ──────────────────────────────────────────────────────

        /// <summary>Which bar it is: 0 the item one, 1 the spell one.</summary>
        public const int SpellBar = 1;

        /// <summary>
        /// The spell that replaces its pair (hng), and the bar slot where it ends up (iuq).
        ///
        /// Read from four real captures of changing variant, from the panel and from the bar:
        ///
        ///   client   hmt { f1: the spell it wants }
        ///   server   iuq { f2 { f2: slot, f6 { f2: spell } }, f3: which bar }   one per slot
        ///   server   hng { f2: spell, f3: grade }
        ///
        /// The iuq go first and there is one for each slot the old half had: in the capture
        /// of Liberación por Magnetismo two came out, because the spell was placed twice.
        /// </summary>
        public static byte[] BuildSpellSwapped(int spellId, int grade)
            => Pb.New().Var(2, spellId).Var(3, grade).Build();

        public static byte[] BuildShortcutChanged(int slot, int spellId)
            => Pb.New()
                .Msg(2, Pb.New().VarIfNotZero(2, slot).Msg(6, Pb.New().Var(2, spellId)))
                .Var(3, SpellBar)
                .Build();

        /// <summary>
        /// A shortcut removed from the bar (<c>ivr</c> / ShortcutBarRemovedEvent).
        /// Flat: f1 the bar, f2 the slot. An emptied <c>ivk</c> leaves the client's icon in place.
        /// </summary>
        public static byte[] BuildShortcutRemoved(int slot)
            => Pb.New()
                .Var(1, SpellBar)
                .VarIfNotZero(2, slot)
                .Build();

        /// <summary>The same on either bar: the item bar's 0 is left out (ivr { 2 18 } in the capture).</summary>
        public static byte[] BuildShortcutRemoved(int slot, int bar)
            => Pb.New()
                .VarIfNotZero(1, bar)
                .VarIfNotZero(2, slot)
                .Build();

        /// <summary>How many slots of the bar we fill. The captured one runs from 0 to 48.</summary>

        // ─── World: weight carried ──────────────────────────────────────────────

        /// <summary>
        /// Pods (iun): f1 what the character is carrying, f3 what it can carry.
        ///
        /// Identified by arithmetic rather than by name: distributing five points of strength
        /// moved f3 by exactly 25, and characteristic 40 by the same 25. Five pods a point of
        /// strength is the game's own rule, and f3 is a thousand above characteristic 40, which
        /// is the base every character has.
        /// </summary>
        public static byte[] BuildPods(long carried, long capacity)
            => Pb.New().VarIfNotZero(1, carried).VarIfNotZero(3, capacity).Build();

        /// <summary>Pods a level brings: the character's, Omega ones too, and each job's.</summary>
        public const int PodsPerLevel = 5;

        /// <summary>Carrying capacity from levels, jobs, strength, equipment and alterations.</summary>
        public static long Capacity(SessionState state)
        {
            if (state == null) return CharacteristicRules.BasePods;
            var gear = Managers.Equipment.BonusesOf(state.EquipmentItems.Values.ToArray().Select(item => (item.Position, item)));
            var temporary = Managers.Alterations.Bonuses(state.CharacterId);
            // Widened before the sum: points and scrolls near int.MaxValue add up past it.
            long strength = (long)state.StatStrength + state.ScrolledStrength
                + gear.GetValueOrDefault(Stat.Strength) + temporary.GetValueOrDefault(Stat.Strength);
            return PodsBeforeStrength(state) + PodsPerLevel * strength
                + gear.GetValueOrDefault(Stat.Pods) + temporary.GetValueOrDefault(Stat.Pods);
        }

        /// <summary>The sheet's pods base, before its derived strength contribution.</summary>
        public static long PodsBeforeStrength(SessionState state)
        {
            if (state == null) return CharacteristicRules.BasePods;
            long jobs = 0;
            foreach (var job in state.Jobs.Values.ToArray()) jobs = checked(jobs + Math.Max(0, job.Level - 1));
            return checked(CharacteristicRules.BasePods + PodsPerLevel * (Math.Max(1L, state.CharacterLevel) - 1) + PodsPerLevel * jobs);
        }

        /// <summary>The real weight of visible inventory items, including worn items, by quantity.</summary>
        public static long Carried(SessionState state)
        {
            if (state == null) return 0;
            long carried = 0;
            foreach (var item in state.EquipmentItems.Values.ToArray())
                // Not the invisible items (type 203): the client cannot see them, so their pod would
                // be a weight with nothing to show for it.
                if (!Managers.Equipment.IsInvisible(item.Template))
                    carried = checked(carried + (long)DatabaseManager.GetItemRealWeight(item.Template) * Math.Max(1, item.Quantity));
            return carried;
        }

        // ─── Gathering ──────────────────────────────────────────────────────────

        /// <summary>
        /// A resource's state (iwf): { f1 { f2: cell, f3: element, f4: state } }.
        ///
        /// Zero is full and the real server does not send the field; 1 depleted and 2 in use. They are the
        /// same field numbers as the jss's f15, without its f1.
        /// </summary>
        public static byte[] BuildElementState(int cell, int elementId, int state)
            => Pb.New().Msg(1, Pb.New()
                .Var(2, cell)
                .Var(3, elementId)
                .VarIfNotZero(4, state)).Build();

        /// <summary>
        /// Declares a resource again (iwm) so that its skill can no longer be used, or
        /// can again: { f3 { the same shape as the jss's f11 } }.
        ///
        /// It is the message that switches off the freshly reaped wheat without having to resend the whole map.
        /// </summary>
        public static byte[] BuildElementRedeclared(int skillInstanceId, int skillId,
                                                    int elementId, int type, bool usable, int skillNameId = 0)
        {
            var declaration = Pb.New().Var(1, 1);
            if (usable) declaration.Var(2, 0);
            declaration.Msg(usable ? 4 : 3, Pb.New().Var(1, skillInstanceId).Var(2, skillId).VarIfNotZero(3, skillNameId));
            return Pb.New().Msg(3, declaration.Var(5, elementId).Var(6, type)).Build();
        }

        /// <summary>
        /// The gathering gesture (iwn): { f2: element, f3: tenths, f4: skill, f5: who }.
        ///
        /// CAREFUL, it is not the same iwn as that of using a zaap or a workshop. That one carries f1 = 1 and does not
        /// carry a duration; this one is the other way round: without f1 and with f3. Measured in the four profession
        /// captures, and f3 is 30 in all four —three seconds— with the real time between this
        /// message and the end one measuring 2,996, 2,999, 3,037 and 3,064 milliseconds.
        /// </summary>
        public static byte[] BuildGatherStarted(int elementId, int tenths, int skillId,
                                                long characterId)
            => Pb.New()
                .Var(2, elementId)
                .Var(3, tenths)
                .Var(4, skillId)
                .Var(5, characterId)
                .Build();

        /// <summary>The gesture is over (iwi): { f1: element, f3: skill }.</summary>
        public static byte[] BuildGatherFinished(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>What was gathered in this pass (itn): { f1: item, f2: quantity }.</summary>
        public static byte[] BuildGathered(int itemId, int quantity)
            => Pb.New().Var(1, itemId).Var(2, quantity).Build();

        /// <summary>
        /// A profession's experience (irq): { f1 { f1: profession, f2: next level, f3: level,
        /// f4: level floor, f5: accumulated } }.
        ///
        /// It sends TOTALS, not increments. f2 disappears when the profession is at the cap, which is
        /// how the level 200 lumberjack came out in the wood capture.
        /// </summary>
        public static byte[] BuildJobExperience(int jobId, long next, int level, long floor,
                                                long experience)
            => Pb.New().Msg(1, Pb.New()
                .Var(1, jobId)
                .VarIfNotZero(2, next)
                .VarIfNotZero(3, level)
                .VarIfNotZero(4, floor)
                .VarIfNotZero(5, experience)).Build();

        /// <summary>
        /// Several jobs in one irq, the way the entry into the world lists them all: one f1 entry
        /// each, the same five fields as <see cref="BuildJobExperience"/>.
        /// </summary>
        public static byte[] BuildJobsExperience(IEnumerable<(int JobId, long Next, int Level, long Floor, long Experience)> jobs)
        {
            var irq = Pb.New();
            foreach (var (jobId, next, level, floor, experience) in jobs)
                irq.Msg(1, Pb.New()
                    .Var(1, jobId)
                    .VarIfNotZero(2, next)
                    .VarIfNotZero(3, level)
                    .VarIfNotZero(4, floor)
                    .VarIfNotZero(5, experience));
            return irq.Build();
        }

        /// <summary>Changes the quantity of an item that was already in the bag (ivj).</summary>
        public static byte[] BuildItemQuantity(long uid, int total)
            => Pb.New().Msg(3, Pb.New().Var(2, uid).Var(3, total)).Build();

        // ─── World: appearance ──────────────────────────────────────────────────

        /// <summary>
        /// "Your look has changed" (lxc), which is what the server sends on equipping something.
        ///
        ///   f1: an identifier shaped like a UUID
        ///   f2: the new look
        ///
        /// The UUID comes out the same in all the lxc of one same session and changes between characters, so
        /// it seems to identify the look's owner. Where the client learns it has not been found
        /// —in the jss the only UUID there is is an alliance's, not this one— so here it is
        /// derived from the character's id: constant for him and different from anyone else's. If the
        /// client does not check it, it does not matter; if it checks it, at least it is consistent.
        /// </summary>
        public static byte[] BuildLookChanged(DbCharacter character, IReadOnlyList<long>? draftColors = null)
            => Pb.New()
                .Str(1, LookIdOf(character.Id))
                .Bytes(2, CharacterLookBuilder.BuildLook(character, paraLaVentana: true, onFoot: false, draftColors))
                .Build();

        /// <summary>
        /// The appearance window's state (lxo), the answer to the lyy.
        ///
        ///   f1: 1
        ///   f3 { f3: when, f5: breed, f7: preview uuid, f8: 3, f10: title,
        ///        f11: level, f12: the look, f15: -1, f16: ornament,
        ///        f17 (repeated) { f1: slot, f2 { f2: garment } } }
        ///
        /// f7 is the same uuid the preview's lxc carries, and f12 its same look:
        /// that is how the panel knows that what reaches it is its own.
        /// </summary>
        public static byte[] BuildAppearanceState(DbCharacter character, string draftId)
            => Pb.New().Var(1, 1).Bytes(3, AppearanceBody(character, draftId)).Build();

        /// <summary>lwq { f1: the outfit being drafted }, the same block the lxo carries in its f3.</summary>
        public static byte[] BuildDraftOutfit(DbCharacter character, string draftId)
            => Pb.New().Bytes(1, AppearanceBody(character, draftId)).Build();

        /// <summary>
        /// lym { f1: aura }, empty for none: the character's main aura, the one the client asks to
        /// show (khl f2) when he faces the screen. See docs/populated-unmapped-fields.md#after-selection-omission-policy.
        /// </summary>
        public static byte[] BuildAura(int aura) => Pb.New().VarIfNotZero(1, aura).Build();

        /// <summary>The worn outfit as the window drafts it, under the draft's own uuid.</summary>
        private static byte[] AppearanceBody(DbCharacter character, string draftId)
            => WardrobeMessages.Write(WardrobeMessages.Live.Outfit(character,
                Managers.Outfits.Of(character.Id)[0] with { Kept = null }, draftId));

        /// <summary>One of the character's outfits: the worn one from the live tables, the others as kept.</summary>
        public static byte[] BuildOutfit(DbCharacter character, Managers.Outfits.Outfit outfit)
            => WardrobeMessages.Write(WardrobeMessages.Live.Outfit(character, outfit));

        /// <summary>The character's first outfit's uuid, different from the preview's.</summary>
        internal static string OutfitIdOf(long characterId) => LookIdOf(characterId * 17 + 3);

        /// <summary>A stable UUID from the character's id.</summary>
        public static string LookIdOf(long characterId)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(characterId).CopyTo(bytes, 0);
            BitConverter.GetBytes(characterId * 2654435761L).CopyTo(bytes, 8);
            return new Guid(bytes).ToString();
        }

        // ─── World: zaaps ───────────────────────────────────────────────────────

        /// <summary>
        /// "That element is in use" (iwn), the immediate answer to the click on a zaap.
        ///
        ///   f1: 1, f2: THE ELEMENT, f4: the skill, f5: who uses it
        ///
        /// f2 is the element, not the skill instance identifier. It is seen by crossing
        /// the iwn with the iwo that causes it in the same capture:
        ///
        ///   iwo  f1: 14110  f2: 538795
        ///   iwn  f1: 1      f2: 538795   f4: 114
        ///
        /// The client sends both numbers and the server returns the second. Sending it the
        /// first leaves the client marking as busy an element that does not exist.
        /// </summary>
        public static byte[] BuildElementInUse(int elementId, int skillId, long who)
            => Pb.New()
                .Var(1, 1)
                .Var(2, elementId)
                .Var(4, skillId)
                .Var(5, who)
                .Build();

        /// <summary>
        /// An interactive has finished being used (iwi). Same shape as the end of gathering: f1 is
        /// the element and f3 the skill.
        ///
        /// A teleport is instant, but it has to be released all the same BEFORE the jru: otherwise, the
        /// client can be left with the element marked as busy in its cache and on coming back
        /// to the map its graphic no longer appears.
        /// </summary>
        public static byte[] BuildInteractiveUseEnded(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>A destination of the zaap list.</summary>
        public readonly struct ZaapDestination
        {
            public ZaapDestination(long mapId, int subAreaId, int level, long cost,
                                   int kind = 0, int minutesLeft = 0, int duration = 0)
            {
                MapId = mapId; SubAreaId = subAreaId; Level = level; Cost = cost;
                Kind = kind; MinutesLeft = minutesLeft; Duration = duration;
            }

            public long MapId { get; }
            public int SubAreaId { get; }
            public int Level { get; }
            public long Cost { get; }

            /// <summary>
            /// Which tab the client puts it in: 0 the zaap, 1 the zaapi, 4 the anomaly.
            ///
            /// The normal zaap does not send the field —proto3 swallows the zero— and that is why for a
            /// while it seemed not to exist. It appears in the 69 entries of the zaapi captures
            /// with value 1 and in the 27 anomaly ones with value 4.
            /// </summary>
            public int Kind { get; }

            /// <summary>Minutes the anomaly has left. Only anomalies carry it.</summary>
            public int MinutesLeft { get; }

            /// <summary>Minutes it lasts. Zero in everything that is not an anomaly.</summary>
            public int Duration { get; }
        }

        /// <summary>
        /// The zaap list (hjj).
        ///
        ///   f2: the map where the opened zaap is
        ///   f3 (repeated) { f1: zone level, f2: what it costs, f3: tab,
        ///                   f4 { f2: minutes left, f3: minutes it lasts },
        ///                   f5: map, f6: subzone }
        ///
        /// The destination one is already at travels without f2, which in proto3 is zero: going where you already
        /// are costs nothing. Checked against the twenty-five entries of the capture, where
        /// f6 matches MapPositions's subzone in all of them.
        ///
        /// The three tabs go in this same list, not in different messages: f3 says which one
        /// each entry falls in and the entry's f4 is only carried by anomalies, which expire. See
        /// <see cref="Managers.Anomalies"/>.
        ///
        /// ─── The ROOT's f4: which window the client opens ───────────────────────────────────
        ///
        /// Sending the right destinations is not enough: the client decides WHICH WINDOW it draws by this
        /// field, and not by the type of the element clicked. It is 0 for the zaap —proto3 swallows
        /// it and it does not travel—, 1 the zaapi and 3 the boat. It comes out the same in the twelve captured lists:
        ///
        ///   3 zaapi captures      f4 = 1, and without f2
        ///   8 zaap captures       without f4, and with f2
        ///   1 boat capture        f4 = 3 AND f2: the two fields are independent
        ///
        /// Without this 1, on clicking a zaapi the client opens the ZAAP window —tabs Zaap,
        /// Anomalía and Prisma— and since all the destinations reaching it are zaapi ones, none of
        /// those tabs picks them up and the window comes out with «Ningún destino». The zaapi one is different:
        /// it is titled «Zaapi» and its tabs are Talleres, Mercadillos and Varios.
        ///
        /// It goes at the END of the message, after all the destinations, which is where the
        /// real server puts it.
        /// </summary>
        public static byte[] BuildZaapList(long here, IEnumerable<ZaapDestination> destinations,
                                           int teleporter = 0)
        {
            var hjj = Pb.New().VarIfNotZero(2, here);
            foreach (var destination in destinations)
            {
                var entry = Pb.New()
                    .VarIfNotZero(1, destination.Level)
                    .VarIfNotZero(2, destination.Cost)
                    .VarIfNotZero(3, destination.Kind);

                // The clock, only if it has one: the real server does not send it for the zaap and the zaapi.
                if (destination.Duration > 0)
                {
                    entry.Msg(4, Pb.New()
                        .VarIfNotZero(2, destination.MinutesLeft)
                        .Var(3, destination.Duration));
                }

                hjj.Msg(3, entry
                    .Var(5, destination.MapId)
                    .VarIfNotZero(6, destination.SubAreaId));
            }

            return hjj.VarIfNotZero(4, teleporter).Build();
        }

        /// <summary>The kamas the character has left (ivf).</summary>
        public static byte[] BuildKamas(long kamas) => Pb.New().Var(1, kamas).Build();

        /// <summary>
        /// "Close the dialogue" (kld).
        ///
        /// The client does NOT close the zaap window on its own: it waits for the server to tell it
        /// to. In the captures it appears twice with the same value —on reaching the destination, right
        /// before the jss, and as the answer to the empty kla the close button sends— so
        /// f1 is a fixed reason and not something that has to be computed.
        /// </summary>
        public static byte[] BuildDialogClosed(int reason = DialogCloseReason)
            => Pb.New().Var(1, reason).Build();

        private const int DialogCloseReason = 10;

        /// <summary>
        /// The reason an NPC's dialogue is closed with, which is not the zaap's.
        ///
        /// In the tournament server capture the kld that closes the conversation with the kama
        /// mountain carries f1: 1, and it appears all four times —both on accepting and on declining—. 10
        /// is the zaap's and there is a 5 measuring another window, so they are different closing reasons
        /// and not a fixed value; the rule that separates them has not been deciphered.
        /// </summary>
        public const int NpcDialogCloseReason = 1;

        // ─── World: NPCs, their dialogue and their shops ────────────────────────

        /// <summary>
        /// The server opens the dialogue window (ioc). It only returns who is being spoken to
        /// and where; it carries neither f1 nor f2 nor f3.
        ///
        ///   f4: map      f5: the NPC's contextual id
        /// </summary>
        public static byte[] BuildNpcDialog(long mapId, long contextualId)
            => Pb.New().Var(4, mapId).Var(5, contextualId).Build();

        /// <summary>
        /// The question and its replies (ios).
        ///
        ///   f1: message id
        ///   f2 (repeated) { f1: reply id, f3 (repeated) { f1: effect id } }
        ///
        /// Each reply's f3 is what the reply promises: at the kama mountain the one that
        /// pays announces effects 194 ("+#1{{~1~2 a }}#2 kamas"), 193 and 351, and the one that declines goes
        /// with none. Here it is sent without f3, which is how the decline reply travels in the
        /// capture: the little prize icon is lost and nothing else. The effect ids are not in
        /// NpcTemplates, so putting them would be inventing them.
        /// </summary>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies)
            => BuildNpcQuestion(messageId, replies, null);

        /// <summary>
        /// The same question, with the PARAMETERS some replies carry inside.
        /// </summary>
        /// <remarks>
        /// A reply is not always just an identifier: it can bring numbers the client
        /// puts in its text. The fountain of the Infinite Dreams is the clear case —the Rey Gob—, and in
        /// the long capture its reply travels like this:
        ///
        ///   f2 { f1: 82314, f3 { f1: 4037 }, f3 { f1: 4035 } }
        ///
        /// f3 is repeated and each one carries a number. Without them the reply is offered all the same,
        /// but bare: the client draws the number's slot empty.
        /// </remarks>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies,
                                              IReadOnlyDictionary<long, IReadOnlyList<long>> parametros)
            => BuildNpcQuestion(messageId, replies, parametros, (IReadOnlyDictionary<long, IReadOnlyList<IReadOnlyList<long>>>?)null);

        /// <summary>
        /// The same question, with what each reply's effects act on: f3 { f1: effect, f2: [args] }.
        /// </summary>
        /// <remarks>
        /// The guided tutorial's Ganymed hands over the starter equipment with
        /// f2 { f1: 77737, f3 { f1: 193 } ×6, f3 { f1: 34, f2: [2511] }, f3 { f1: 2192 } }:
        /// the 34 is «Empieza una misión» and its f2 the quest the client names beside the reply.
        /// f2 is packed, as the capture carries it.
        /// </remarks>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies,
                                              IReadOnlyDictionary<long, IReadOnlyList<long>>? parametros,
                                              IReadOnlyDictionary<long, IReadOnlyList<IReadOnlyList<long>>>? arguments)
        {
            replies = replies.ToList();
            if (SessionContext.Actual is { } asked)
            {
                asked.State.LastNpcQuestion = messageId;
                asked.State.LastNpcReplies = (List<long>)replies;
            }
            var ios = Pb.New().Var(1, messageId);
            foreach (long reply in replies)
            {
                var una = Pb.New().Var(1, reply);

                if (parametros != null && parametros.TryGetValue(reply, out var suyos))
                {
                    IReadOnlyList<IReadOnlyList<long>>? args = null;
                    arguments?.TryGetValue(reply, out args);
                    for (int i = 0; i < suyos.Count; i++)
                    {
                        var effect = Pb.New().Var(1, suyos[i]);
                        if (args != null && i < args.Count && args[i].Count > 0) effect.Packed(2, args[i]);
                        una.Msg(3, effect);
                    }
                }

                ios.Msg(2, una);
            }
            return ios.Build();
        }

        /// <summary>
        /// The same question, with the texts its own line fills in: f3, one string per «#n». The
        /// banker's greeting is the measured one -- «te costará #1 kamas» goes with f3 "1397",
        /// frame 74 of the bank capture --, and Ontoral's «Haré esta ofrenda en tu nombre, #1»
        /// goes the same way with the character's name. The replies keep their effects' arguments.
        /// </summary>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies,
                                              IReadOnlyDictionary<long, IReadOnlyList<long>>? parametros,
                                              IEnumerable<string> lineParameters,
                                              IReadOnlyDictionary<long, IReadOnlyList<IReadOnlyList<long>>>? arguments = null)
        {
            var ios = Pb.New();
            foreach (string parameter in lineParameters) ios.Str(3, parameter ?? "");
            byte[] question = BuildNpcQuestion(messageId, replies, parametros, arguments);
            byte[] tail = ios.Build();
            var both = new byte[question.Length + tail.Length];
            Buffer.BlockCopy(question, 0, both, 0, question.Length);
            Buffer.BlockCopy(tail, 0, both, question.Length, tail.Length);
            return both;
        }

        /// <summary>
        /// A shop's whole catalogue (kbd), in one go.
        ///
        ///   f1 (repeated) { f1: item
        ///                   f3 { f2: price, f3: -1, f4: criterion }
        ///                   f4 (repeated): an effect, with the id in f11 }
        ///   f2: the same contextual id the iov asked for
        ///
        /// It is not paginated: in the capture there are fifty-six shop iov and fifty-six
        /// kbd, one to one, and the biggest is 26,902 bytes with 444 entries. Since there is not a single case of
        /// two kbd for one same shop, there is no proof either that the client knows how to join them, so
        /// the split into small sellers the real server does is also the safe thing.
        ///
        /// f3.f3 is -1 in the 6,106 measured entries. It is sent all the same even though what it
        /// means exactly is not known: the only sure thing is that the client always receives it that way.
        ///
        /// The text criterion —"(SC=3|Sc=3500)" on the tournament server— is NOT sent. SC=3
        /// is "tournament server" and the server itself validates it: the fourteen entries with the
        /// hardest criterion gave error 243 on buying them. Here we are not a tournament server, and
        /// there are twelve measured entries travelling with no criterion at all, so it is omitted.
        ///
        /// f3 is THE CURRENCY, and with it a shop charges in an item instead of in kamas. Nothing
        /// has to be invented: the client already knows how to do it. Measured over the 305 captures, there are 60
        /// kbd and 58 carry only f1 and f2 —those charge in kamas—; the other two also carry
        /// f3, with the id of the item acting as currency:
        ///
        ///   f3 = 13052   «Sebuscalón»   (the Travellers' Tower shop)
        ///   f3 = 30529   «Fidelicha»    (one in Pandala)
        ///
        /// If f3 is not there, it charges in kamas, which is why it goes with VarIfNotZero: a normal
        /// shop still sends exactly the same bytes as before.
        ///
        /// MIND THE PRICE. The whole shop is received and not only the currency's id, and it is on
        /// purpose: the first version sent f3 with the token but kept putting in each
        /// entry the price IN KAMAS, so the client showed a cape at «1 token» and on
        /// buying it the server charged 150. The price shown and the price charged have
        /// to come from the same place, and that is why both come from here.
        /// </summary>
        public static byte[] BuildShop(long contextualId, IEnumerable<int> gids,
                                       Managers.TokenShops.Shop? tokenShop = null, int npcId = 0)
        {
            var kbd = Pb.New();
            foreach (int gid in gids)
            {
                var entry = Pb.New()
                    .Var(1, gid)
                    .Msg(3, Pb.New()
                        .VarIfNotZero(2, tokenShop == null
                            ? Managers.NpcShops.PriceOf(npcId, gid)
                            : Managers.TokenShops.PriceOf(tokenShop, gid))
                        .Var(3, ShopUnlimited));

                foreach (var effect in Managers.Equipment.ParseEffects(Managers.NpcShops.EffectsOf(gid)))
                {
                    var value = EffectEntry(effect);
                    if (value != null) entry.Msg(4, value);
                }

                kbd.Msg(1, entry);
            }
            return kbd.Var(2, contextualId).VarIfNotZero(3, tokenShop?.TokenGid ?? 0).Build();
        }

        /// <summary>The shop's stock. Constant in the 6,106 entries of the capture.</summary>
        private const int ShopUnlimited = -1;

        /// <summary>
        /// The shop has been closed (khd). f3 is 11 in all fifty-six of the capture.
        /// </summary>
        public static byte[] BuildShopClosed() => Pb.New().Var(3, ShopClosedKind).Build();

        private const int ShopClosedKind = 11;

        /// <summary>
        /// An information message (lqn), which is HOW THE PLAYER IS SPOKEN TO.
        ///
        ///   f1: the type          f2: which message     f4 (repeated): its parameters
        ///
        /// The server does not send text: it sends two numbers and the client supplies the sentence, already translated,
        /// taking it from InfoMessagesDataRoot. The type decides how it draws it —0 information, 1 warning—
        /// and proto3 swallows the zero, which is why in the captures some lqn carry f1 and others
        /// do not. See <see cref="Managers.InfoMessages"/>.
        ///
        /// This and not a chat line: the chat goes out through the general channel and everybody reads it.
        /// </summary>
        public static byte[] BuildSystemMessage(int messageId, params string[] parameters)
            => BuildInfoMessage(Managers.InfoMessages.Info, messageId, parameters);

        /// <summary>
        /// A line of free text as an information message (lqn), only to the one it is for: the
        /// answer of a command, or what a window we have no measured frame for would have said.
        /// Before this those went as a chat line in the player's own name, on the channel they
        /// wrote in -- the general one, most of the time -- so it read as them talking.
        /// </summary>
        public static byte[] BuildNotice(string text)
            => BuildInfoMessage(Managers.InfoMessages.Info, Managers.InfoMessages.FreeText, text ?? "");

        /// <summary>The same, saying what type it is.</summary>
        public static byte[] BuildInfoMessage(int type, int messageId, params string[] parameters)
        {
            var lqn = Pb.New().VarIfNotZero(1, type).VarIfNotZero(2, messageId);
            foreach (string parameter in parameters) lqn.Str(4, parameter);
            return lqn.Build();
        }

        /// <summary>The "you have received kamas" message, with the figure as a parameter.</summary>
        public const int KamasReceivedMessage = 45;

        /// <summary>The "bought" one: item, uid, quantity and price.</summary>
        public const int PurchaseMessage = 252;

        /// <summary>
        /// The same notice but when it was paid in tokens. Measured in the Travellers' Tower
        /// shop: six parameters, «798, 1055401001, 1, 20, 13052, 0», which are the item
        /// bought and its uid, the quantity, the price, and the currency's id and uid.
        /// </summary>
        public const int TokenPurchaseMessage = 364;

        // ─── World: changing map ────────────────────────────────────────────────

        /// <summary>
        /// Removes an actor from the map (jsd): who is leaving and WHICH WAY.
        ///
        /// The which way was missing, and it is what left the character standing on the edge on the
        /// others' screens instead of disappearing. The notice reached them —it is measured in the
        /// server log— and the client did nothing with it.
        ///
        /// The capture of a party following the leader through nearby maps shows it clearly, with
        /// twenty-five of these: field 3 is only 2, 4 or 6, which are Dofus's cardinal
        /// directions (0 right, 2 down, 4 left, 6 up). The client takes the
        /// figure out walking towards that side and then deletes it.
        ///
        ///   10 83a8e3aab808 18 06     who, and he left through the top
        ///   10 83a8e3aab808           who, and he left to the east (0, off the wire)
        /// </summary>
        /// <remarks>
        /// The one without f3 is direction 0, east, which proto3 leaves off the wire; it is not an
        /// exit with no direction. Frame 5 of that capture is the leader walking from [1,-32] to
        /// [2,-32], and the member's own jsd of frame 19 is the same walk. So a 0 is written the
        /// same way here, and a jump -- which has no way out at all -- sends no jsd: see
        /// <see cref="MapPresenceService"/>.
        /// </remarks>
        public static byte[] BuildActorLeft(long contextualId, int? porDonde = null)
        {
            var pb = Pb.New().Var(2, contextualId);
            if (porDonde.HasValue) pb.VarIfNotZero(3, porDonde.Value);
            return Push(Op.Jsd, pb.Build());
        }

        /// <summary>
        /// Takes an actor off the map in the client of someone watching (kmu): only who.
        /// </summary>
        /// <remarks>
        /// This, and not the jsd, is what makes a character who left disappear from the others'
        /// screens. In "Movimiento/captura otro personaje saliendo del mapa" two characters walk
        /// off the observer's map, and each time the server sends their jsj to the edge and then
        /// kmu { f2: their id } -- no jsd at all. The jsd goes to the one leaving, before his jru,
        /// and to his party: in "Grupos/con grupo seguir desplazamiento del lider..." the member
        /// watching gets the leader's jsd and, right behind it, the same kmu. With the jsd alone
        /// the character walked to the edge on the others' screens and stayed there.
        /// </remarks>
        public static byte[] BuildActorRemoved(long contextualId)
            => Push(Op.Kmu, Pb.New().Var(2, contextualId).Build());

        /// <summary>"Load this map" (jru).</summary>
        public static byte[] BuildLoadMap(long mapId)
            => Push(Op.Jru, Pb.New().Var(2, mapId).Build());

        /// <summary>
        /// The two that travel with jru on every map change of the capture: lqu, which carries a
        /// 120 and the server clock in milliseconds, and hjk, which carries the map id in a packed
        /// list. lqn goes out between them in the capture and does not go out here: its one field
        /// is a number we have not been able to explain (197 on entering the world, 24 on changing
        /// map, 470 after a characteristics reset), and inventing it is worse than leaving it out.
        /// </summary>
        public static byte[] BuildMapClock()
            => Push(Op.Lqu, Pb.New()
                .Var(1, 120)
                .Var(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Build());

        /// <summary>
        /// Movement along a map (jsj), which is what the server sends back to a jrw.
        ///
        ///   f1: the cells walked, packed
        ///   f2: how the actor ends up facing
        ///   f5: whose movement it is
        /// </summary>
        public static byte[] BuildActorMoved(long contextualId, IEnumerable<long> cells, int facing)
            => Push(Op.Jsj, Pb.New()
                .Packed(1, cells)
                .VarIfNotZero(2, facing)
                .Var(5, contextualId)
                .Build());

        // ─── World: inventory ───────────────────────────────────────────────────

        /// <summary>
        /// An effect entry: the id in f11 and the value in whichever field it belongs.
        ///
        /// The field is not just any slot, it is the one saying what type the effect is. f4 carries a
        /// loose number, f5 a range and f6 three numbers, and the last two are SUBMESSAGES. Putting
        /// a varint where the client expects a submessage is not an odd value, it is a wire
        /// type that does not match: the client does not find the parameters and draws the weapon without damage
        /// and the dofus with "{spellNoLvl,,}" instead of the spell's name.
        /// </summary>
        internal static Pb? EffectEntry(Managers.Equipment.ItemEffect effect)
            => ItemEffectMessages.FromCatalogue.Effect(effect) is { } entry ? ItemEffectMessages.Write(entry) : null;

        // ─── World: titles and ornaments ────────────────────────────────────────

        public static byte[] BuildOrnamentUpdated(int ornamentId)
            // Unused by project policy; see docs/populated-unmapped-fields.md#after-selection-omission-policy.
            => Array.Empty<byte>();

        // ─── World: merkasako ───────────────────────────────────────────────────

        /// <summary>
        /// One's rooms (jbf): f1 (repeated) { f1 theme, f2 room }. The captured account's
        /// «0a0208200a04080410010a04080110020a0408011003»: room 0 in theme 32, 1 in 4, 2 and 3 in 1.
        /// </summary>
        public static byte[] BuildHavenBagRooms(IEnumerable<(int Room, int Theme)> rooms)
        {
            var jbf = Pb.New();
            foreach (var (room, theme) in rooms)
                jbf.Msg(1, Pb.New().Var(1, theme).VarIfNotZero(2, room));
            return jbf.Build();
        }

        // ─── World: chest ───────────────────────────────────────────────────────

        // "El cofre está abierto" (kci) is built per kind of storage -- a house chest's is not a
        // bin's nor the haven bag's -- in StorageProtocol.BuildOpened.

        /// <summary>
        /// What is inside the chest (iwb). Same shape as the inventory, with the bag as
        /// the position of everything: inside a chest nothing is equipped.
        /// </summary>
        public static byte[] BuildStorageContent(IEnumerable<Managers.HavenBagStore.StoredItem> items)
        {
            var iwb = Pb.New();
            foreach (var item in items)
            {
                if (Managers.InvisibleItems.Conceal(item.Gid, item.Uid)) continue;

                var body = Pb.New().Var(1, item.Gid);
                foreach (var effect in Managers.Equipment.ParseEffects(item.Effects))
                {
                    var entry = EffectEntry(effect);
                    if (entry != null) body.Msg(2, entry);
                }
                body.Var(3, Math.Max(1, item.Quantity)).Var(4, item.Uid);

                iwb.Msg(1, Pb.New().Var(1, Managers.Equipment.Bag).Msg(5, body));
            }
            return iwb.Build();
        }

        /// <summary>An item going into a place (iua for the chest, itd for the bag).</summary>
        public static byte[] BuildItemArrived(int field, Managers.HavenBagStore.StoredItem item)
        {
            var body = Pb.New().Var(1, item.Gid);
            foreach (var effect in Managers.Equipment.ParseEffects(item.Effects))
            {
                var entry = EffectEntry(effect);
                if (entry != null) body.Msg(2, entry);
            }
            body.Var(3, Math.Max(1, item.Quantity)).Var(4, item.Uid);

            return Pb.New()
                .Msg(field, Pb.New().Var(1, Managers.Equipment.Bag).Msg(5, body))
                .Build();
        }

        /// <summary>
        /// An item the way every item message carries it: { f1: template, f2: effects, f3: how
        /// many, f4: uid }. The uid is left out when it is zero, as the kdr of a craft of several
        /// does.
        /// </summary>
        internal static Pb ItemBody(int gid, IEnumerable<Managers.Equipment.ItemEffect> effects, int quantity, long uid)
        {
            var body = Pb.New().Var(1, gid);
            foreach (var effect in effects)
            {
                var entry = EffectEntry(effect);
                if (entry != null) body.Msg(2, entry);
            }
            return body.Var(3, Math.Max(1, quantity)).VarIfNotZero(4, uid);
        }

        /// <summary>
        /// An item's effects, one entry each under the field given, the way <see cref="ItemBody"/>
        /// writes them under f2. The marketplace's offers carry them under f4 (kbt, kgp) and f1
        /// (kfi), with nothing else of the item around them.
        /// </summary>
        internal static Pb AddEffects(Pb target, int field, IEnumerable<Managers.Equipment.ItemEffect> effects)
        {
            foreach (var effect in effects)
            {
                var entry = EffectEntry(effect);
                if (entry != null) target.Msg(field, entry);
            }
            return target;
        }

        /// <summary>An item that leaves (itc from the chest, ium from the bag): only its identifier.</summary>
        public static byte[] BuildItemGone(long uid) => Pb.New().Var(1, uid).Build();

        /// <summary>
        /// What the lottery machine answers (jbs).
        ///
        ///   f2: the prize       f3: the reason for refusal
        ///
        /// From the two captures: one comes out with f2 and the other, the "you have already used it today" one, with f3: 1.
        /// Here one always wins, so f2 always goes.
        /// </summary>
        public static byte[] BuildLotteryResult(long prizeUid)
            => Pb.New().Var(2, prizeUid).Build();

        /// <summary>The chest closed (khd). In the capture it carries f3: 11.</summary>
        public static byte[] BuildStorageClosed() => Pb.New().Var(3, StorageCloseReason).Build();

        private const int StorageCloseReason = 11;

        // ─── World: chat ────────────────────────────────────────────────────────

        /// <summary>
        /// A line of chat coming back (kti). What the client sends is
        /// ktm { f2: the text, f3: the channel }, and this is the answer:
        ///
        ///   f3: when, as "2026-08-09T20:28:01+02:00"
        ///   f4: who said it
        ///   f5: their character
        ///   f6: their account
        ///   f7: what they said
        ///   f8: what the line's links stand for (ktp) -- empty in every line of every capture,
        ///       which has none; see <see cref="ChatLinks"/>
        ///   f9: the channel
        ///
        /// Channels, from the capture that goes through all of them in one sitting: 0 general
        /// (left out, being zero), 1 team, 2 guild, 3 alliance, 4 party, 5 trade, 6 recruitment,
        /// and 9, 11, 16, 18 and 19 for the rest. A private message is a different message, ktb,
        /// and it carries who it is for.
        /// </summary>
        public static byte[] BuildChatLine(string who, long characterId, long accountId,
                                           string text, int channel, Pb? links = null)
            => Pb.New()
                .Str(3, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"))
                .Str(4, who ?? "")
                .Var(5, characterId)
                .VarIfNotZero(6, accountId)
                .Str(7, text ?? "")
                .Msg(8, links ?? Pb.New())
                .VarIfNotZero(9, channel)
                .Build();

        // ─── Groups ─────────────────────────────────────────────────────────────

        /// <summary>
        /// You have been invited to a party (ijz): it brings up the little window.
        ///
        ///   f1: who is invited    f2: who invites   f3: places
        ///   f5: the party         f6: ?             f7: the inviter's name
        ///
        /// Measured: 0881f092cbdd08 1083a8e3aab808 1808 28e8ac04 3001 3a064c6561646572, that is
        /// invitee 300000000001, host 290000000003, eight places, party 71272, and «Leader».
        /// f6 is 1 in one capture and 2 in another and what distinguishes them has not been worked out; 1 is sent,
        /// which is that of the invitation that gets accepted.
        /// </summary>
        public static byte[] BuildPartyInvitation(long guestId, long hostId, string hostName,
                                                  int partyId, int seats)
            => Pb.New()
                .Var(1, guestId)
                .Var(2, hostId)
                .Var(3, seats)
                .Var(5, partyId)
                .Var(6, 1)
                .Str(7, hostName ?? "")
                .Build();

        /// <summary>The invitation is over, for whoever declines it (ilo): { f1: party, f2: who was inviting }.</summary>
        public static byte[] BuildInvitationClosed(int partyId, long hostId)
            => Pb.New().Var(1, partyId).Var(2, hostId).Build();

        /// <summary>Removes the invitee from the list, for whoever invited (iko): { f1: invitee, f2: party }.</summary>
        public static byte[] BuildInvitationWithdrawn(long guestId, int partyId)
            => Pb.New().Var(1, guestId).Var(2, partyId).Build();

        /// <summary>The party has broken up (imy): { f1: party }.</summary>
        public static byte[] BuildPartyDissolved(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>You have left (ils): { f1: party }.</summary>
        public static byte[] BuildPartyLeft(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>
        /// There is a new leader (ilx): { f1: the new leader, f2: the party }.
        ///
        /// Eleven bytes, and the whole party is NOT resent: it was checked by comparing the sheet of the same
        /// party before and after the change, and the only thing that changes is its field 4.
        /// </summary>
        public static byte[] BuildPartyLeader(long leaderId, int partyId)
            => Pb.New().Var(1, leaderId).Var(2, partyId).Build();

        /// <summary>
        /// A private message (kth): { f1: date, f4: empty, f5: the other's id, f6: his name,
        /// f7: the text }.
        ///
        /// Measured from the guild capture, where the whisper to «Player-Alpha» DID arrive:
        ///
        ///   0a19 «2026-08-12T22:54:29+02:00»  2200  28 8590d8c69e05
        ///   320c «Player-Alpha»  3a04 «hola»
        ///
        /// Mind two things. It carries no CHANNEL: the client knows it is private by the message
        /// itself, and that is why sending it as a kti through channel 9 draws nothing. And what it carries is
        /// not who speaks but THE OTHER —in your copy, whom you are saying it to—, so the same
        /// message serves both sides by changing whose identity is put in.
        /// </summary>
        public static byte[] BuildPrivateMessage(string when, long otherId, string otherName,
                                                 string text)
            => Pb.New()
                .Str(1, when)
                .EmptyMsg(4)
                .Var(5, otherId)
                .Str(6, otherName ?? "")
                .Str(7, text ?? "")
                .Build();

        /// <summary>
        /// The level-up window (kua): { f1: the new level }.
        ///
        /// Two bytes, and with that the client brings up the whole window —music, animation and the level's
        /// data— and leaves it open until the player closes it. It answers nothing on
        /// closing it, so there is nothing to listen for.
        ///
        /// It appears exactly twice in the 305 captures, both in the tutorial and in the exact
        /// millisecond of each level-up: 0802 on reaching level 2 and 0803 on reaching level 3.
        /// After it go iun, kub and kfe, but those three also come out on entering the world without levelling
        /// up, so the only message belonging to the level-up is this one.
        ///
        /// What the window shows —points earned, life, spells— the client takes from the kub
        /// that comes after, not from here. That is why the kua has to be sent BEFORE the new
        /// characteristics, which is the order of the capture.
        /// </summary>
        public static byte[] BuildLevelUp(int level) => Pb.New().Var(1, level).Build();

        /// <summary>
        /// The chat could not manage something (ktl), with the reason in its only field. Measured: 0802 is
        /// what the real server answers on whispering to oneself.
        /// </summary>
        public static byte[] BuildChatError(int reason) => Pb.New().Var(1, reason).Build();

        // ─── Envelope for answers ───────────────────────────────────────────────

        /// <summary>
        /// Wraps an answer to something the client asked for: f3 { f1 { f1: type_url, f2: payload },
        /// f2: the id the request came with }.
        ///
        /// Three different root fields are in use and they are not interchangeable. Field 1 is a
        /// message the server pushes on its own; field 2 is what the client sends; field 3 is an
        /// answer, and it repeats the request's id so the client can pair them. jsq, the go-ahead
        /// for a map change, is the one that made this necessary.
        /// </summary>
        public static byte[] Answer(string opcode, byte[]? payload, long requestId)
        {
            var any = Pb.New().Str(1, UriPrefix + opcode);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);

            return Pb.New()
                .Msg(3, Pb.New().Bytes(1, any.Build()).Var(2, requestId))
                .Build();
        }

        /// <summary>
        /// The id a client request carries, in field 2 of the root. It is -1 for everything seen
        /// so far, and it is read rather than assumed because the answer has to echo it.
        /// </summary>
        public static long RequestId(byte[] frame)
        {
            try
            {
                foreach (var f in ProtoMessage.Parse(frame).Fields)
                {
                    if (f.FieldNumber != 2 || f.WireType != 2) continue;
                    foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                    {
                        if (g.FieldNumber == 2 && g.WireType == 0) return g.VarIntValue;
                    }
                }
            }
            catch { }
            return -1;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Pulls the payload out of a wrapped message by looking for its type_url in the frame.
        ///
        /// Scanning for the raw marker is deliberate: that way it does not matter which root
        /// field the message is wrapped in, which differs between client and server messages.
        /// Returns an empty array when the message is there but carries no payload.
        /// </summary>
        public static byte[]? ReadPayload(byte[] frame, string opcode)
        {
            if (frame == null) return null;
            byte[] marker = Encoding.ASCII.GetBytes(UriPrefix + opcode);

            for (int i = 0; i + marker.Length <= frame.Length; i++)
            {
                bool matches = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (frame[i + j] != marker[j]) { matches = false; break; }
                }
                if (!matches) continue;

                // Right behind the type_url comes field 2 of the Any, holding the payload.
                int p = i + marker.Length;
                if (p >= frame.Length || frame[p] != 0x12) return Array.Empty<byte>();

                p++;
                int length = 0, shift = 0;
                while (p < frame.Length)
                {
                    byte b = frame[p++];
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (length < 0 || p + length > frame.Length) return Array.Empty<byte>();
                byte[] payload = new byte[length];
                Array.Copy(frame, p, payload, 0, length);
                return payload;
            }
            return null;
        }

        /// <summary>
        /// Pulls the three-letter opcode out of a wrapped frame, or null if it has no envelope.
        /// </summary>
        public static string? ReadOpcode(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return null;
            byte[] marker = Encoding.ASCII.GetBytes(UriPrefix);
            for (int i = 0; i + marker.Length + 3 <= frame.Length; i++)
            {
                bool matches = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (frame[i + j] != marker[j]) { matches = false; break; }
                }
                if (matches)
                {
                    return Encoding.ASCII.GetString(frame, i + marker.Length, 3);
                }
            }
            return null;
        }
    }
}
