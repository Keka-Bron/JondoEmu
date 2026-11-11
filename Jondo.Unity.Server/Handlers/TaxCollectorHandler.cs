using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The alliances' tax collectors: placing one with a potion, talking to it, attacking it,
    /// defending it from the alliance's fights window, and collecting what it gathered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is done from the client, as the game does it. A potion of the bag places one (its
    /// effect 1153, «Invoca un recaudador»); the tax collector's own menu (TaxCollectorMenuMaker)
    /// talks to it (iov 3), attacks it (hkc) and collects it (kbu); and the members come to defend
    /// it from the alliance window's fights tab (meq, then mer), which the popup of the attack
    /// opens (fgv.bext).
    /// </para>
    /// <para>
    /// A tax collector gathers on its map: every fight won there against monsters rolls their loot
    /// tables once more for it, at its prospecting, until its pods are full. Its attackers share
    /// what it carried; collecting it gives it all to whoever collects, and takes it away.
    /// </para>
    /// </remarks>
    public static partial class TaxCollectorHandler
    {
        /// <summary>The potions' effect: 1153, «Invoca un recaudador».</summary>
        public const int PlaceEffect = 1153;

        /// <summary>
        /// How many an alliance may have in one group of territories: «No podéis poner más de {0}
        /// recaudador(es) por zona» (info 1/168); «una alianza solo puede controlar a un recaudador
        /// por grupo de territorios. Un grupo suele componerse de un territorio y su mazmorra»
        /// (Translations 1149321). See <see cref="GroupOf"/>.
        /// </summary>
        public const int PerSubArea = 1;

        /// <summary>
        /// How many attackers and how many defenders a tax collector's fight takes: «Un combate
        /// contra un recaudador puede tener como máximo 5 atacantes y 5 defensores» (1149321).
        /// </summary>
        public const int FightPeopleCap = 5;
        private const int TooManyInZone = 168;

        /// <summary>«Uno de tus recaudadores ha sido destruido.», info 0/1579, to whoever placed it.</summary>
        private const int OneOfYoursDestroyed = 1579;

        // ─── Placing ────────────────────────────────────────────────────────────

        /// <summary>Whether this template places a tax collector.</summary>
        public static bool IsPotion(int template)
            => DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(template))
                              .Any(e => e.EffectId == PlaceEffect);

        /// <summary>
        /// A potion used: the tax collector stands where its user stands, if the potion's criterion
        /// holds there -- the alliance right and the subarea's level, «Ox=10&amp;(SL&gt;40&amp;SL&lt;61)» --
        /// and the map takes one. False when the item is no such potion.
        /// </summary>
        public static async Task<bool> TryPlaceAsync(NetworkStream stream, Equipment.Item item)
        {
            if (!IsPotion(item.Template)) return false;

            var me = SessionContext.State;
            var alliance = AllianceStore.AllianceOf(me.CharacterId);
            var map = MapManager.GetMapInfo(me.MapId);
            int reason = WhyNotHere(alliance, me.CharacterId, map, item.Template);
            if (reason >= 0)
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(reason));
                return true;
            }
            int group = GroupOf(map.MapId, map.SubAreaId);
            if (TaxCollectorStore.OfAlliance(alliance.Id).Count(t => GroupOf(t.MapId, t.SubAreaId) == group) >= PerSubArea)
            {
                await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                    InfoMessages.Warning, TooManyInZone, PerSubArea.ToString()));
                return true;
            }

            var (first, last) = Names();
            var tc = TaxCollectorStore.Place(alliance.Id, me.CharacterId, me.CharacterName, me.MapId, map.SubAreaId,
                                             me.CellId, me.Orientation, first, last);
            if (tc == null)
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(TaxCollectorProtocol.ErrorAlreadyOnMap));
                return true;
            }
            await ItemUseHandler.SpendOneAsync(stream, item.Uid);

            // It starts with the alliance's default spells, the personalization tab's preset.
            TaxCollectorStore.SetSpells(tc, TaxCollectorStore.PresetOf(alliance.Id));

            byte[] shown = ConnectionProtocol.Push(Op.Jsn, TaxCollectorProtocol.BuildShown(tc, alliance));
            await SendAsync(stream, shown);
            await SessionRegistry.BroadcastToMapAsync(tc.MapId, shown, SessionContext.Current.Id, fightId: 0);
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hjq,
                TaxCollectorProtocol.BuildAdded(LeaOf(tc, alliance))));

            Program.LogDebug($"[TaxCollector] {me.CharacterName} places {tc.Uuid} of «{alliance.Name}» on map " +
                             $"{tc.MapId}, cell {tc.CellId}.");
            return true;
        }

        /// <summary>
        /// Why a tax collector cannot be placed here (an hla reason), or -1 when it can. The map
        /// must be one fought on: outdoors or a dungeon's room, not a dream's or a raid's.
        /// </summary>
        /// <remarks>
        /// Dungeons take them: «Si se coloca a un recaudador en una mazmorra, solo se contarán los
        /// combates dentro de esa mazmorra» (1149321), and the hks of the temple's board carries ten
        /// in dungeons. Their rooms are almost all indoors (753 of 763), so they pass on their own.
        /// </remarks>
        internal static int WhyNotHere(AllianceStore.Alliance alliance, long characterId, MapInfo map, int potion)
        {
            if (alliance == null || !AllianceStore.HasRight(characterId, AllianceData.PlaceTaxCollector))
                return TaxCollectorProtocol.ErrorNoRights;
            if (map == null || map.SubAreaId <= 0 || (!map.Outdoor && !DungeonManager.IsRoom(map.MapId))
                || Dreams.IsDreamMap(map.MapId) || GuildRaidManager.IsRaidMap(map.MapId))
                return TaxCollectorProtocol.ErrorCantHireHere;
            if (!CriterionHolds(DatabaseManager.GetItemTemplateCriterion(potion), characterId,
                                Interactives.LevelOfSubArea(map.SubAreaId)))
                return TaxCollectorProtocol.ErrorCantHireHere;
            if (TaxCollectorStore.OnMap(map.MapId) != null) return TaxCollectorProtocol.ErrorAlreadyOnMap;
            return -1;
        }

        /// <summary>
        /// The group of territories a map is in, for the one tax collector an alliance may have in
        /// each: its subarea, or for a dungeon's room the subarea of the dungeon's entrance -- «un
        /// grupo suele componerse de un territorio y su mazmorra».
        /// </summary>
        internal static int GroupOf(long mapId, int subAreaId)
        {
            var dungeon = DungeonManager.OfRoom(mapId);
            if (dungeon == null || dungeon.EntranceMapId == 0) return subAreaId;
            return MapManager.GetMapInfo(dungeon.EntranceMapId)?.SubAreaId ?? subAreaId;
        }

        /// <summary>
        /// A potion's or a prism's criterion, answered for its user: Ox an alliance right he holds,
        /// SL the level of the subarea he stands in. Anything else is not known and does not hold.
        /// </summary>
        internal static bool CriterionHolds(string criterion, long characterId, int subAreaLevel)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return true;
            return Criterion.Met(criterion, condition => condition.Code switch
            {
                "Ox" => condition.Operator == '='
                    ? (AllianceStore.HasRight(characterId, (int)condition.Value) ? Answer.True : Answer.False)
                    : Answer.Unknown,
                "SL" => Criterion.Compare(condition.Operator, subAreaLevel, condition.Value),
                _ => Answer.Unknown,
            });
        }

        /// <summary>Two names out of the client's lists, at random.</summary>
        private static (int First, int Last) Names()
        {
            var firsts = AllianceData.TaxCollectorFirstNames;
            var lasts = AllianceData.TaxCollectorNames;
            int first = firsts.Count == 0 ? 1 : firsts[Random.Shared.Next(firsts.Count)].Id;
            int last = lasts.Count == 0 ? 1 : lasts[Random.Shared.Next(lasts.Count)].Id;
            return (first, last);
        }

        // ─── On the map ─────────────────────────────────────────────────────────

        /// <summary>The map's tax collector among its actors, unless it is being fought.</summary>
        public static void AddToMap(Pb jss, long mapId)
        {
            var tc = TaxCollectorStore.OnMap(mapId);
            if (tc == null || FightHandler.IsGroupFighting(tc.ContextId)) return;
            var alliance = AllianceStore.ById(tc.AllianceId);
            if (alliance == null) return;
            jss.Msg(5, TaxCollectorProtocol.Actor(tc, alliance));
        }

        /// <summary>
        /// Talking to it (iov 3 on its actor): the dialogue opens (ioc) and it says what it carries
        /// (ion), as in the capture. False when the actor is no tax collector.
        /// </summary>
        public static async Task<bool> TalkAsync(NetworkStream stream, long mapId, long contextId)
        {
            var tc = TaxCollectorStore.ByContextId(contextId);
            if (tc == null || tc.MapId != mapId) return false;
            var alliance = AllianceStore.ById(tc.AllianceId);
            if (alliance == null) return false;

            var items = TaxCollectorStore.Items(tc);
            SessionContext.State.OpenActorDialogue = contextId;
            await SendAsync(stream, Op.Ioc, ConnectionProtocol.BuildNpcDialog(mapId, contextId));
            await SendAsync(stream, Op.Ion, TaxCollectorProtocol.BuildDialog(alliance, PodsOf(items),
                TaxCollectorStore.OfAlliance(alliance.Id).Count, WorthOf(items), MaxPodsOf(tc), ProspectingOf(tc)));
            return true;
        }

        /// <summary>
        /// «Leave» on a tax collector's or a prism's dialogue (kla): it closes with the kld of a
        /// conversation, reason 1. False when none is being talked to.
        /// </summary>
        /// <remarks>
        /// It used to fall through to the zaap list's close, whose kld carries reason 10, and the
        /// client keeps a conversation up on that one: «Leave» did nothing.
        /// </remarks>
        public static async Task<bool> CloseTalkAsync(NetworkStream stream)
        {
            if (SessionContext.State.OpenActorDialogue == 0) return false;
            SessionContext.State.OpenActorDialogue = 0;
            await SendAsync(stream, Op.Kld, ConnectionProtocol.BuildDialogClosed(
                ConnectionProtocol.NpcDialogCloseReason));
            return true;
        }

        private static int PodsOf(IEnumerable<(int ItemId, int Quantity)> items)
            => items.Sum(i => DatabaseManager.GetItemRealWeight(i.ItemId) * i.Quantity);

        private static long WorthOf(IEnumerable<(int ItemId, int Quantity)> items)
            => items.Sum(i => NpcShops.PriceOf(i.ItemId) * i.Quantity);

        // ─── The tops ───────────────────────────────────────────────────────────

        /// <summary>The board's map: the Temple of Alliances' square.</summary>
        public const long TopsMap = AllianceHandler.TempleMap;

        /// <summary>The board, element 488131 at cell 118 of the square.</summary>
        public const int TopsBoard = 488131;

        /// <summary>Its skill, 293 «Consultar».</summary>
        public const int TopsSkill = 293;

        /// <summary>How many each top shows: ten and ten in the capture.</summary>
        public const int TopSize = 10;

        /// <summary>
        /// «Consultar» on the board (iwo): the element in use (iwn) and the two tops (hks), the
        /// world's and the dungeons'.
        /// </summary>
        public static async Task ShowTopsAsync(NetworkStream stream, int elementId, int skillId)
        {
            await SendAsync(stream, Op.Iwn, ConnectionProtocol.BuildElementInUse(elementId, skillId,
                                                                               SessionContext.State.CharacterId));
            var all = TaxCollectorStore.All().Where(t => AllianceStore.ById(t.AllianceId) != null).ToList();
            var world = TopOf(all.Where(t => DungeonManager.OfRoom(t.MapId) == null));
            var dungeons = TopOf(all.Where(t => DungeonManager.OfRoom(t.MapId) != null));
            await SendAsync(stream, Op.Hks, TaxCollectorProtocol.BuildTops(Leas(world), Leas(dungeons)));
        }

        /// <summary>
        /// The richest <see cref="TopSize"/>: by the worth of what they carry -- the column the
        /// window shows, «valor estimado» --, the oldest first between equals.
        /// </summary>
        internal static List<TaxCollectorStore.TaxCollector> TopOf(IEnumerable<TaxCollectorStore.TaxCollector> taxCollectors)
            => taxCollectors.Select(tc => (Tc: tc, Worth: WorthOf(TaxCollectorStore.Items(tc))))
                            .OrderByDescending(r => r.Worth).ThenBy(r => r.Tc.Id)
                            .Take(TopSize)
                            .Select(r => r.Tc)
                            .ToList();

        private static IEnumerable<Pb> Leas(IEnumerable<TaxCollectorStore.TaxCollector> taxCollectors)
        {
            foreach (var tc in taxCollectors)
            {
                var alliance = AllianceStore.ById(tc.AllianceId);
                if (alliance != null) yield return LeaOf(tc, alliance);
            }
        }

        // ─── Gathering ──────────────────────────────────────────────────────────

        /// <summary>
        /// A fight won against monsters in a tax collector's territory: their loot tables rolled
        /// once more for each such tax collector, at its prospecting, while it has pods left. Its
        /// chests raise the one and its saddlebags the other.
        /// </summary>
        /// <remarks>
        /// The territory, not the map: <see cref="TaxCollectorStore.GatheringFor"/> says which,
        /// after the client's guide. Only its map counted before, and a tax collector left for
        /// days on a quiet map of a busy territory came back with 0 pods.
        /// </remarks>
        /// <returns>What each tax collector took, for the end screen (FightRewards).</returns>
        internal static List<(TaxCollectorStore.TaxCollector Collector, List<int> Items)> Gather(FightInstance fight)
        {
            int subArea = MapManager.GetMapInfo(fight.RoleplayMapId)?.SubAreaId ?? 0;
            var taken = new List<(TaxCollectorStore.TaxCollector, List<int>)>();
            foreach (var tc in TaxCollectorStore.GatheringFor(fight.RoleplayMapId, subArea))
                taken.Add((tc, GatherFor(tc, fight)));
            return taken;
        }

        private static List<int> GatherFor(TaxCollectorStore.TaxCollector tc, FightInstance fight)
        {
            int prospecting = ProspectingOf(tc);
            int maxPods = MaxPodsOf(tc);
            int pods = PodsOf(TaxCollectorStore.Items(tc));
            var gathered = new List<int>();
            bool inDungeon = DungeonManager.IsRoom(fight.RoleplayMapId);
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster && !m.EsInvocado))
            {
                // Today's Almanax may make the collectors' loot richer here (type 6).
                int almanax = Almanax.BonusPercent(Almanax.BonusType.TaxCollectorLoot,
                    Almanax.ForFight(fight.RoleplayMapId, new[] { monster.MonsterId }, Array.Empty<int>(), inDungeon));
                foreach (var drop in DatabaseManager.GetMonsterDrops(monster.MonsterId, monster.GradeIndex))
                {
                    // A quest's item is for whoever is on the quest, not for the collector.
                    if (drop.ReceiverCriterion.Length > 0) continue;
                    double chance = Math.Min(100.0, drop.PercentDrop * prospecting / 100.0 * (100.0 + almanax) / 100.0);
                    if (Random.Shared.NextDouble() * 100.0 >= chance) continue;
                    int weight = Math.Max(1, DatabaseManager.GetItemRealWeight(drop.ObjectId));
                    if (pods + weight > maxPods) continue;
                    TaxCollectorStore.AddItem(tc, drop.ObjectId, 1);
                    pods += weight;
                    gathered.Add(drop.ObjectId);
                }
            }
            if (gathered.Count > 0)
                Program.LogDebug($"[TaxCollector] {tc.Uuid} gathers {gathered.Count} item(s) on map {tc.MapId}: {pods} pods.");
            return gathered;
        }

        // ─── Collecting ─────────────────────────────────────────────────────────

        /// <summary>
        /// Collecting it (kbu, the menu's «Recaudar»): what it carries goes to whoever collects --
        /// through the recycling window, where it can become nuggets shared with the alliance's pot
        /// (RecycleHandler) --, the alliance is told (kdc, hjt, hlc) and it leaves its map.
        /// </summary>
        public static async Task CollectAsync(NetworkStream stream, byte[] payload)
        {
            byte[] kbu = ConnectionProtocol.ReadPayload(payload, Op.Kbu);
            var tc = TaxCollectorStore.ByUuid(TaxCollectorProtocol.ReadUuid(kbu));
            var me = SessionContext.State;
            if (tc == null || tc.MapId != me.MapId)
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(TaxCollectorProtocol.ErrorUnknown));
                return;
            }
            var alliance = AllianceStore.ById(tc.AllianceId);
            if (alliance == null || AllianceStore.AllianceOf(me.CharacterId)?.Id != tc.AllianceId)
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(TaxCollectorProtocol.ErrorNotYours));
                return;
            }
            int right = tc.OwnerId == me.CharacterId
                ? AllianceData.CollectOwnTaxCollectors
                : AllianceData.CollectMembersTaxCollectors;
            if (!AllianceStore.HasRight(me.CharacterId, right))
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(TaxCollectorProtocol.ErrorNoRights));
                return;
            }
            if (FightHandler.IsGroupFighting(tc.ContextId)) return;

            var items = TaxCollectorStore.Items(tc);
            await ReturnEquipmentAsync(tc);
            TaxCollectorStore.Remove(tc);
            if (tc.OwnerId != me.CharacterId)
                await TellOwnerAsync(tc, TaxCollectorStore.AbsenceCollected, me.CharacterId, me.CharacterName);
            bool recycling = SessionContext.State.Workshop == null
                && await RecycleHandler.OpenForTaxCollectorAsync(stream, alliance.Id, tc.SubAreaId, items);
            if (!recycling)
            {
                foreach (var (item, quantity) in items)
                {
                    if (!await WorkshopHandler.GiveAsync(stream, item, quantity)) continue;
                    await SendAsync(stream, Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        InfoMessages.ItemGained, quantity.ToString(), item.ToString()));
                }
            }

            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Kdc,
                TaxCollectorProtocol.BuildCollected(tc, alliance, me.CharacterName, items)));
            await GoneAsync(tc, alliance, me.CharacterId, me.CharacterName);
            Program.LogDebug($"[TaxCollector] {me.CharacterName} collects {tc.Uuid}: {items.Sum(i => i.Quantity)} item(s).");
        }

        /// <summary>A tax collector gone: off its map (kmu) and off its alliance's list (hjt, hlc).</summary>
        private static async Task GoneAsync(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance,
                                            long whoId, string whoName)
        {
            await SessionRegistry.BroadcastToMapAsync(tc.MapId, ConnectionProtocol.Push(Op.Kmu,
                FightJoinProtocol.BuildActorHidden(tc.ContextId)), fightId: 0);
            if (alliance == null) return;
            if (whoId != 0)
            {
                await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hjt,
                    TaxCollectorProtocol.BuildRemovedBy(tc.Uuid, whoId, whoName)));
            }
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hlc,
                TaxCollectorProtocol.BuildRemoved(tc.Uuid)));
        }

        /// <summary>The alliance's tax collectors, as its sheet (mcx) lists them in its f1.</summary>
        public static IEnumerable<Pb> InformationOf(AllianceStore.Alliance alliance)
            => TaxCollectorStore.OfAlliance(alliance.Id).Select(tc => LeaOf(tc, alliance));

        private static int StateOf(TaxCollectorStore.TaxCollector tc)
        {
            var defence = AllianceFights.All.FirstOrDefault(d => d.Kind == TaxCollectorProtocol.FightOfTaxCollector
                                                                 && d.MapId == tc.MapId);
            if (defence == null) return TaxCollectorProtocol.StateCollecting;
            return defence.Fighting ? TaxCollectorProtocol.StateFighting : TaxCollectorProtocol.StateWaitingForHelp;
        }

        // ─── Attacking ──────────────────────────────────────────────────────────

        /// <summary>
        /// Attacking it (hkc, empty: a map has one tax collector at most). The fight opens as one
        /// against a group made of the tax collector alone, and its alliance is told.
        /// </summary>
        public static async Task AttackAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            var tc = TaxCollectorStore.OnMap(me.MapId);
            if (tc == null || me.IsInFight || me.FightId != 0) return;
            if (FightHandler.IsGroupFighting(tc.ContextId)) return;
            var alliance = AllianceStore.ById(tc.AllianceId);
            if (alliance == null || AllianceStore.AllianceOf(me.CharacterId)?.Id == tc.AllianceId) return;

            var monster = MobSpawnManager.GetMonsterData(TaxCollectorProtocol.Monster);
            if (monster == null || monster.Grades.Count == 0)
            {
                Program.LogDebug($"[TaxCollector] Monster {TaxCollectorProtocol.Monster} is not loaded: no fight.");
                return;
            }

            var alone = new MobSpawnManager.MobGroup
            {
                MobId = tc.ContextId,
                CellId = tc.CellId,
                Orientation = tc.Direction,
                Members = { new MobSpawnManager.MobMember { Monster = monster, GradeIndex = 0, Level = monster.Grades[0].Level } },
            };
            await FightHandler.InitiateFightFromMobCollision(stream, alone, tc.MapId, rules: FightRules.ContraRecaudador);

            var fight = FightHandler.FightOf(me.CharacterId);
            if (fight == null || fight.TaxCollectorUuid != tc.Uuid) return;
            await AttackedAsync(fight, tc, alliance);
        }

        /// <summary>
        /// A fight opening against a tax collector: the fight learns whose it is, its fighter
        /// becomes the tax collector -- its names, its look in the alliance's colours --, and the
        /// alliance's fights have a new line.
        /// </summary>
        internal static void OnFightCreated(FightInstance fight)
        {
            if (fight.Reglas != FightRules.ContraRecaudador) return;
            var tc = TaxCollectorStore.ByContextId(fight.DefenderLeaderId);
            if (tc == null) return;
            var alliance = AllianceStore.ById(tc.AllianceId);

            fight.TaxCollectorUuid = tc.Uuid;
            fight.PeopleCap = FightPeopleCap;
            foreach (var fighter in fight.Rojo.Where(f => f.IsMonster && f.MonsterId == TaxCollectorProtocol.Monster))
            {
                fighter.TaxCollectorFirstName = tc.FirstNameId;
                fighter.TaxCollectorLastName = tc.LastNameId;
                fighter.Look = TaxCollectorProtocol.LookString(alliance, TaxCollectorProtocol.FightBones);
                fighter.LookBoneId = TaxCollectorProtocol.FightBones;
                Dress(fighter, tc);
            }
            AllianceFights.Open(fight, TaxCollectorProtocol.FightOfTaxCollector, tc.MapId, tc.AllianceId,
                                () => TaxCollectorProtocol.TaxCollectorFighter(tc, alliance));
        }

        /// <summary>
        /// The alliance hears of the attack: the chat and popup (hld), the tax collector waiting
        /// for help (hkd), and the fight in its fights window.
        /// </summary>
        private static async Task AttackedAsync(FightInstance fight, TaxCollectorStore.TaxCollector tc,
                                                AllianceStore.Alliance alliance)
        {
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hld, TaxCollectorProtocol.BuildAttacked(tc)));
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hkd,
                TaxCollectorProtocol.BuildState(tc.Uuid, TaxCollectorProtocol.StateWaitingForHelp)));
            await AllianceFights.AnnounceAsync(fight);
            Program.LogDebug($"[TaxCollector] {tc.Uuid} of «{alliance.Name}» attacked: fight #{fight.FightId}.");
        }

        /// <summary>The placement is over: the alliance's list shows the tax collector fighting (hkd).</summary>
        internal static async Task FightStartedAsync(FightInstance fight)
        {
            if (fight.TaxCollectorUuid == null) return;
            var tc = TaxCollectorStore.ByUuid(fight.TaxCollectorUuid);
            var alliance = tc == null ? null : AllianceStore.ById(tc.AllianceId);
            if (alliance == null) return;
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hkd,
                TaxCollectorProtocol.BuildState(tc.Uuid, TaxCollectorProtocol.StateFighting)));
        }

        /// <summary>
        /// A tax collector's fight is over, and decides it: beaten, it is gone and its owner is
        /// told; standing, it goes back on its map. Its alliance hears which (hjw).
        /// </summary>
        internal static async Task FightEndedAsync(FightInstance fight, bool attackersWon)
        {
            var tc = TaxCollectorStore.ByUuid(fight.TaxCollectorUuid);
            if (tc == null) return;
            var alliance = AllianceStore.ById(tc.AllianceId);
            if (alliance != null)
            {
                await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hjw,
                    TaxCollectorProtocol.BuildFightResult(tc, died: attackersWon)));
            }

            if (attackersWon)
            {
                await ReturnEquipmentAsync(tc);
                TaxCollectorStore.Remove(tc);
                await TellOwnerAsync(tc, TaxCollectorStore.AbsenceDefeated, 0, "");
                await GoneAsync(tc, alliance, 0, "");
                var owner = SessionRegistry.FindByCharacter(tc.OwnerId);
                if (owner != null)
                    await owner.SendAsync(ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(OneOfYoursDestroyed)));
                Program.LogDebug($"[TaxCollector] {tc.Uuid} falls in fight #{fight.FightId}.");
                return;
            }

            if (alliance == null) return;
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hkd,
                TaxCollectorProtocol.BuildState(tc.Uuid, TaxCollectorProtocol.StateCollecting)));
            await SessionRegistry.BroadcastToMapAsync(tc.MapId, ConnectionProtocol.Push(Op.Jsn,
                TaxCollectorProtocol.BuildShown(tc, alliance)), fightId: 0);
            Program.LogDebug($"[TaxCollector] {tc.Uuid} survives fight #{fight.FightId}.");
        }

        /// <summary>
        /// What each winner of a defended fight takes: a tax collector's attackers share its bag,
        /// item by item in turns from a random one; its defenders, and the winners of a prism's
        /// fight, take nothing. Every winner is listed, with nothing if so, so that nobody rolls
        /// the tax collector's or the prism's own loot table.
        /// </summary>
        internal static Dictionary<long, Dictionary<int, int>> ShareTheBag(FightInstance fight)
        {
            var shares = new Dictionary<long, Dictionary<int, int>>();
            var winners = fight.Azul.Concat(fight.Rojo)
                .Where(f => !f.IsMonster && !f.EsInvocado && !f.EsIlusion && fight.HaGanado(f.Id))
                .Select(f => f.Id)
                .ToList();
            foreach (long winner in winners) shares[winner] = new Dictionary<int, int>();

            var tc = TaxCollectorStore.ByUuid(fight.TaxCollectorUuid);
            var attackers = winners.Where(id => fight.EquipoDe(id) == FightInstance.Azules).ToList();
            if (tc == null || attackers.Count == 0) return shares;

            int turn = Random.Shared.Next(attackers.Count);
            foreach (var (item, quantity) in TaxCollectorStore.Items(tc))
            {
                for (int i = 0; i < quantity; i++)
                {
                    var bag = shares[attackers[turn]];
                    bag.TryGetValue(item, out int had);
                    bag[item] = had + 1;
                    turn = (turn + 1) % attackers.Count;
                }
            }
            return shares;
        }

        /// <summary>An alliance dissolved: its tax collectors leave their maps.</summary>
        internal static async Task AllianceDissolvedAsync(long allianceId)
        {
            foreach (var tc in TaxCollectorStore.OfAlliance(allianceId))
            {
                await ReturnEquipmentAsync(tc);
                TaxCollectorStore.Remove(tc);
                await GoneAsync(tc, null, 0, "");
            }
        }

        // ─── Pieces ─────────────────────────────────────────────────────────────

        private static Task SendAsync(NetworkStream stream, string op, byte[] payload)
            => SendAsync(stream, ConnectionProtocol.Push(op, payload));

        private static async Task SendAsync(NetworkStream stream, byte[] frame)
        {
            if (stream == null) return;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
        }
    }
}
