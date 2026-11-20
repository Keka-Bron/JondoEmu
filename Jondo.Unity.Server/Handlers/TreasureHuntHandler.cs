using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The treasure hunts on the wire (TreasureHunts, TreasureHuntProtocol): the divine dimensions'
    /// legendary hunts launched from the Travelers' Tower's models, their flags, digs and give-ups,
    /// and the fight against the fissure at their end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The model as the capture has it: the element in use (iwn), then the answer (hhk) -- 3 for a
    /// dimension whose portal one already has, 2 with a hunt under way -- and with 5 the hunt (hhf).
    /// A give-up within the first minute is refused with info 436, «termina tu misión actual o
    /// espera {0} minuto/s más».
    /// </para>
    /// <para>
    /// INFERRED, nothing captured: the flags' and digs' answers by the client's own texts for each
    /// code, the hunt sent again after each, its end (hgm) when lost, given up or won. The fight at
    /// the end is the hunt's fissure («Fisura de X», the legendary row's monster) and four
    /// monsters of the dimension's first zone at the hunt's level: «combat contre une fissure qui
    /// invoque 4 monstres» (DofusPourLesNoobs, Les chasses aux trésors) -- the client's fissure
    /// has no spell to summon them with, so they are there from the start. Whoever wins it gets
    /// the dimension's portal for good, those who joined included, as the guide says; the hunter,
    /// the hunt's chest. Lost, the hunt is lost: «En chasse légendaire, la chasse est perdue».
    /// </para>
    /// </remarks>
    public static class TreasureHuntHandler
    {
        /// <summary>«Imposible: termina tu misión actual o espera {0} minuto/s más.»</summary>
        public const int GiveUpTooSoon = 436;

        /// <summary>How many of the dimension's monsters come out with the fissure.</summary>
        public const int FissureCompany = 4;

        private static readonly Random _dice = new();

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        private static Task SendHuntAsync(NetworkStream stream, TreasureHunts.Hunt hunt)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Hhf, TreasureHuntProtocol.BuildHunt(hunt)));

        private static Task SendFinishedAsync(NetworkStream stream, int type)
            => WriteAsync(stream, ConnectionProtocol.Push(Op.Hgm, TreasureHuntProtocol.BuildFinished(type)));

        /// <summary>What the model answers a character: the hunt to launch, or why not.</summary>
        public static int Answer(TravelersTower.Dimension dimension, long characterId, int level,
                                 Func<long, int, bool> has, Func<long, int, TreasureHunts.Hunt?> hunting)
        {
            if (has(characterId, dimension.Alteration)) return TreasureHunts.NotAvailable;
            if (level < TravelersTower.LocateLevel) return TreasureHunts.NotAvailable;
            if (hunting(characterId, TreasureHunts.Legendary) != null) return TreasureHunts.AlreadyHaveQuest;
            if (LegendaryHunts.Of(dimension.Legendary) == null) return TreasureHunts.NoQuestFound;
            return TreasureHunts.RequestOk;
        }

        // ─── The Malle aux trésors' box ─────────────────────────────────────

        /// <summary>
        /// What the Anutrof box offers a character: «Buscar …» for each of Dhain's quests he has in
        /// hand; nothing otherwise. Null for any other element.
        /// </summary>
        public static IReadOnlyList<InteractiveAction>? VisibleActions(RegisteredInteractive interactive)
        {
            if (interactive.MapId != TreasureQuests.Room || interactive.Element.Id != TreasureQuests.Box) return null;
            var log = Quests.Log;
            return interactive.Actions
                .Where(a => TreasureQuests.OfName(a.SkillNameId) is { } kind && log != null && log.Active(kind.Quest))
                .ToList();
        }

        /// <summary>The box as its viewer sees it (iwm): f3 { f1 1, f4 each skill, f5, f6 }.</summary>
        public static byte[] BuildBox(RegisteredInteractive interactive)
        {
            var declaration = Pb.New().Var(1, 1);
            foreach (var action in VisibleActions(interactive) ?? interactive.Actions)
                declaration.Msg(4, Pb.New().Var(1, action.SkillInstanceId).Var(2, action.SkillId).VarIfNotZero(3, action.SkillNameId));
            declaration.Var(5, interactive.Element.Id).Var(6, interactive.Type);
            return Pb.New().Msg(3, declaration).Build();
        }

        /// <summary>
        /// Before Dhain speaks: his treasure quests finished on an earlier hunting day are taken out of
        /// the log, so that he offers them again -- «Une seule chasse classique par jour», from 7:00.
        /// </summary>
        public static void BeforeTalkingTo(int npcId)
        {
            if (npcId != TreasureQuests.Dhain) return;
            var log = Quests.Log;
            long who = SessionContext.State.CharacterId;
            if (log == null || who == 0) return;
            string today = TreasureQuests.DayOf(DateTimeOffset.UtcNow);
            foreach (var kind in TreasureQuests.Kinds)
            {
                if (log.Finished(kind.Quest) && TreasureQuests.HuntedOn(who, kind.Quest) != today)
                    Quests.Forget(kind.Quest);
            }
        }

        /// <summary>One of Dhain's quests taken, in his room: the box offers it at once.</summary>
        public static async Task OnQuestStartedAsync(NetworkStream stream, int questId)
        {
            if (stream == null || TreasureQuests.OfQuest(questId) == null) return;
            if (SessionContext.State.MapId != TreasureQuests.Room) return;
            var box = InteractiveRegistry.OnMap(TreasureQuests.Room).FirstOrDefault(i => i.Element.Id == TreasureQuests.Box);
            if (box != null) await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwm, BuildBox(box)));
        }

        /// <summary>
        /// The box used: the hunt of the quest its name stands for, when that quest is in hand and no
        /// other hunt of the kind is under way -- «solo se puede buscar un cofre cada vez».
        /// </summary>
        public static async Task UseBoxAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            var kind = TreasureQuests.OfName(action.SkillNameId);
            if (kind == null) return;
            await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(interactive.Element.Id, action.SkillId, state.CharacterId)));

            int answer = TreasureHunts.RequestOk;
            var now = DateTimeOffset.UtcNow;
            if (Quests.Log?.Active(kind.Quest) != true) answer = TreasureHunts.NoQuestFound;
            else if (TreasureHunts.Get(state.CharacterId, TreasureHunts.Classic) != null) answer = TreasureHunts.AlreadyHaveQuest;
            else if (TreasureQuests.HuntedOn(state.CharacterId, kind.Quest) == TreasureQuests.DayOf(now)) answer = TreasureHunts.LimitExceeded;

            TreasureHunts.Hunt? hunt = null;
            if (answer == TreasureHunts.RequestOk)
            {
                int level = state.CharacterLevel;
                hunt = TreasureHunts.Begin(TreasureHunts.World, state.CharacterId, TreasureHunts.Classic, 0,
                                           DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _dice,
                                           stages: _dice.Next(TreasureQuests.FewestStages, TreasureQuests.MostStages + 1),
                                           stepsPerStage: TreasureQuests.StepsPerStage, quest: kind.Quest,
                                           startsOn: map => TreasureQuests.StartsFor(
                                               Interactives.LevelOfSubArea(MapManager.GetMapInfo(map)?.SubAreaId ?? 0), level));
                if (hunt == null) answer = TreasureHunts.NoQuestFound;
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hhk,
                TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Classic, answer)));
            if (hunt == null) return;

            TreasureHunts.Save(hunt);
            TreasureQuests.MarkHunted(state.CharacterId, kind.Quest, TreasureQuests.DayOf(now));
            Console.WriteLine($"[Hunts] {state.CharacterName} draws a map from the Anutrof box ({kind.Name}): " +
                              $"{hunt.Stages} stages from {hunt.StartMap}.");
            await SendHuntAsync(stream, hunt);
        }

        /// <summary>
        /// The Treasure, at the hunter's grade, on the map he dug on: it summons its chests itself.
        /// Its ten grades go past the six a composed group is held to, so the grade is set after.
        /// </summary>
        private static async Task FightTheTreasureAsync(NetworkStream stream, TreasureHunts.Hunt hunt)
        {
            var data = MobSpawnManager.GetMonsterData(TreasureFight.Treasure);
            if (data == null || data.Grades.Count == 0) return;
            int grade = TreasureQuests.TreasureGrade(data.Grades.Select(g => g.Level).ToList(), SessionContext.State.CharacterLevel);
            var group = MobSpawnManager.ComposeOffMap(new[] { (TreasureFight.Treasure, grade) });
            if (group == null || group.Members.Count == 0) return;
            group.Members[0].GradeIndex = grade;
            group.Members[0].Level = data.Grades[grade].Level;
            long mapId = SessionContext.State.MapId;
            Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} digs up the Treasure (grade {grade + 1}) on {mapId}.");
            await FightHandler.InitiateFightFromMobCollision(stream, group, mapId);
        }

        /// <summary>A model's «Localizar una fisura»: its dimension's legendary hunt.</summary>
        public static async Task LocateFissureAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            var dimension = TravelersTower.ModelOf(interactive.Element.Id);
            if (dimension == null) return;

            await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(interactive.Element.Id, action.SkillId, state.CharacterId)));

            int answer = Answer(dimension, state.CharacterId, state.CharacterLevel, Alterations.Has, TreasureHunts.Get);
            TreasureHunts.Hunt? hunt = null;
            if (answer == TreasureHunts.RequestOk)
            {
                hunt = TreasureHunts.Begin(TreasureHunts.World, state.CharacterId, TreasureHunts.Legendary,
                                           dimension.Legendary, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _dice);
                if (hunt == null) answer = TreasureHunts.NoQuestFound;
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hhk,
                TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Legendary, answer)));
            if (hunt == null) return;

            TreasureHunts.Save(hunt);
            Console.WriteLine($"[Hunts] {state.CharacterName} sets out to find {dimension.Name}'s fissure from {hunt.StartMap}.");
            await SendHuntAsync(stream, hunt);
        }

        // ─── The Malle aux trésors' legendary hunts ─────────────────────────

        /// <summary>
        /// The hunts a planning table offers: every legendary hunt but the divine dimensions' --
        /// those are located in the travellers' tower. The window shows those whose map the player
        /// carries, or all of them with «show all».
        /// </summary>
        public static IReadOnlyList<int> PlanningTableHunts()
            => LegendaryHunts.All.Where(h => TravelersTower.LegendaryOf(h.Id) == null).Select(h => h.Id).OrderBy(id => id).ToList();

        /// <summary>
        /// What a planning table answers a legendary hunt asked for: «une seule à la fois», and its
        /// map in the bag, which the hunt takes -- «en cas de défaite … vous récupérez la carte» --,
        /// and the quest its map is studied for under way, when it has one (LegendaryHunts.QuestOf).
        /// </summary>
        public static int LegendaryAnswer(LegendaryHunts.Definition? legendary, bool alreadyHunting, int maps,
                                          bool questUnderWay = true)
        {
            if (legendary == null || TravelersTower.LegendaryOf(legendary.Id) != null) return TreasureHunts.NoQuestFound;
            if (alreadyHunting) return TreasureHunts.AlreadyHaveQuest;
            if (maps < 1 || !questUnderWay) return TreasureHunts.NotAvailable;
            return TreasureHunts.RequestOk;
        }

        /// <summary>
        /// Whether the quest the hunt's map asks for, if any, is under way for this character, with
        /// the objectives it asks for before the hunt done.
        /// </summary>
        private static bool QuestUnderWay(LegendaryHunts.Definition? legendary)
        {
            int quest = legendary == null ? 0 : LegendaryHunts.QuestOf(legendary);
            if (quest == 0) return true;
            var log = SessionContext.State.Quests;
            if (log == null || !log.Active(quest)) return false;
            return LegendaryHunts.ObjectivesBefore(legendary!).All(log.ObjectiveDone);
        }

        /// <summary>The planning table used: its window, with the hunts it offers (hhg).</summary>
        public static async Task OpenPlanningTableAsync(NetworkStream stream, RegisteredInteractive interactive, InteractiveAction action)
        {
            var state = SessionContext.State;
            await SessionRegistry.BroadcastToMapAsync(state.MapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(interactive.Element.Id, action.SkillId, state.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hhg, TreasureHuntProtocol.BuildLegendaryHunts(PlanningTableHunts())));
        }

        /// <summary>A legendary hunt asked for (hhn): its map spent, and the hunt under way.</summary>
        public static async Task LegendaryAskedAsync(NetworkStream stream, byte[] frame)
        {
            byte[] body = ConnectionProtocol.ReadPayload(frame, Op.Hhn);
            if (body == null) return;
            var state = SessionContext.State;
            var legendary = LegendaryHunts.Of(TreasureHuntProtocol.ReadLegendaryAsked(body));
            int answer = LegendaryAnswer(legendary, TreasureHunts.Get(state.CharacterId, TreasureHunts.Legendary) != null,
                                         legendary == null ? 0 : Equipment.HowMany(legendary.MapItem),
                                         QuestUnderWay(legendary));

            TreasureHunts.Hunt? hunt = null;
            if (answer == TreasureHunts.RequestOk && await Equipment.TakeAsync(stream, legendary!.MapItem, 1))
            {
                hunt = TreasureHunts.Begin(TreasureHunts.World, state.CharacterId, TreasureHunts.Legendary,
                                           legendary.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _dice);
                if (hunt == null)
                {
                    await Equipment.GiveAsync(stream, legendary.MapItem, 1);
                    answer = TreasureHunts.NoQuestFound;
                }
            }
            else if (answer == TreasureHunts.RequestOk) answer = TreasureHunts.NotAvailable;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hhk,
                TreasureHuntProtocol.BuildRequestAnswer(TreasureHunts.Legendary, answer)));
            if (hunt == null) return;

            TreasureHunts.Save(hunt);
            Console.WriteLine($"[Hunts] {state.CharacterName} sets out on legendary hunt {hunt.Legendary} from {hunt.StartMap}.");
            await SendHuntAsync(stream, hunt);
        }

        /// <summary>A flag put on a step (hhv), on the map one stands on.</summary>
        public static async Task FlagAsync(NetworkStream stream, byte[] frame)
        {
            byte[] body = ConnectionProtocol.ReadPayload(frame, Op.Hhv);
            if (body == null) return;
            var (type, index) = TreasureHuntProtocol.ReadFlagPut(body);
            await FlagChangedAsync(stream, type, index, hunt =>
                TreasureHunts.PutFlag(TreasureHunts.World, hunt, index, SessionContext.State.MapId));
        }

        /// <summary>A flag taken away (hhh).</summary>
        public static async Task RemoveFlagAsync(NetworkStream stream, byte[] frame)
        {
            byte[] body = ConnectionProtocol.ReadPayload(frame, Op.Hhh);
            if (body == null) return;
            var (type, index) = TreasureHuntProtocol.ReadFlagRemoved(body);
            await FlagChangedAsync(stream, type, index, hunt => TreasureHunts.RemoveFlag(hunt, index));
        }

        private static async Task FlagChangedAsync(NetworkStream stream, int type, int index, Func<TreasureHunts.Hunt, int> change)
        {
            var hunt = TreasureHunts.Get(SessionContext.State.CharacterId, type);
            int result = hunt == null || SessionContext.State.IsInFight ? TreasureHunts.FlagImpossible : change(hunt);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hhq, TreasureHuntProtocol.BuildFlagAnswer(type, index, result)));
            if (hunt == null || result != TreasureHunts.FlagOk) return;
            TreasureHunts.Save(hunt);
            await SendHuntAsync(stream, hunt);
        }

        /// <summary>
        /// A dig (hgj): the stage's flags judged. The next stage, a try spent, the hunt lost -- or, on
        /// the last stage, the fight against the fissure.
        /// </summary>
        public static async Task DigAsync(NetworkStream stream, byte[] frame)
        {
            byte[] body = ConnectionProtocol.ReadPayload(frame, Op.Hgj);
            if (body == null) return;
            int type = TreasureHuntProtocol.ReadType(body);
            var state = SessionContext.State;
            var hunt = TreasureHunts.Get(state.CharacterId, type);

            var (result, wrong) = hunt == null || state.IsInFight
                ? (TreasureHunts.DigImpossible, 0)
                : TreasureHunts.Dig(TreasureHunts.World, hunt, state.MapId, _dice);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hht, TreasureHuntProtocol.BuildDigAnswer(type, result, wrong)));
            if (hunt == null) return;

            switch (result)
            {
                case TreasureHunts.DigLost:
                    TreasureHunts.End(state.CharacterId, type);
                    Console.WriteLine($"[Hunts] {state.CharacterName} has lost the hunt.");
                    await SendFinishedAsync(stream, type);
                    return;
                case TreasureHunts.DigWrong:
                case TreasureHunts.DigNewStage:
                    TreasureHunts.Save(hunt);
                    await SendHuntAsync(stream, hunt);
                    return;
                case TreasureHunts.DigFinished:
                    TreasureHunts.Save(hunt);
                    await SendHuntAsync(stream, hunt);
                    if (hunt.Type == TreasureHunts.Classic) await FightTheTreasureAsync(stream, hunt);
                    else await FightTheFissureAsync(stream, hunt);
                    return;
            }
        }

        /// <summary>The fissure and its company on the map one dug on, the fight opened at once.</summary>
        private static async Task FightTheFissureAsync(NetworkStream stream, TreasureHunts.Hunt hunt)
        {
            var legendary = LegendaryHunts.Of(hunt.Legendary);
            var dimension = TravelersTower.LegendaryOf(hunt.Legendary);
            if (legendary == null) return;

            var roster = dimension == null ? Array.Empty<int>() : MobSpawnManager.RosterOfMap(dimension.EntryMap);
            var members = FissureFight(legendary, roster, MobSpawnManager.GetMonsterData, _dice,
                                       LegendaryHunts.CompanyOf(legendary.Id));
            var group = MobSpawnManager.ComposeOffMap(members);
            if (group == null)
            {
                Console.WriteLine($"[Hunts] The fissure {legendary.Monster} could not be put together.");
                return;
            }
            long mapId = SessionContext.State.MapId;
            Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} faces the fissure of hunt {hunt.Legendary} on {mapId}: " +
                              string.Join(", ", members.Select(m => $"{m.Monster}#{m.Grade}")) + ".");
            await FightHandler.InitiateFightFromMobCollision(stream, group, mapId);
        }

        /// <summary>
        /// The fight at a fissure hunt's end: the fissure, and up to <see cref="FissureCompany"/> of
        /// its dimension's monsters, each at the grade nearest the hunt's level.
        /// </summary>
        public static List<(int Monster, int Grade)> FissureFight(LegendaryHunts.Definition legendary, IReadOnlyList<int> roster,
                                                                  Func<int, MobSpawnManager.MonsterData?> data, Random dice,
                                                                  IReadOnlyList<int>? partners = null)
        {
            int GradeNear(MobSpawnManager.MonsterData monster)
            {
                int best = 0;
                int grades = Math.Min(monster.Grades.Count, MobSpawnManager.MobMember.MaxWrittenGrades);
                for (int i = 1; i < grades; i++)
                    if (Math.Abs(monster.Grades[i].Level - legendary.Level) < Math.Abs(monster.Grades[best].Level - legendary.Level))
                        best = i;
                return best;
            }

            var members = new List<(int Monster, int Grade)>();
            if (data(legendary.Monster) is { Grades.Count: > 0 } fissure)
                members.Add((legendary.Monster, GradeNear(fissure)));
            // Whoever always stands with it: the two other bandits of Cania (LegendaryHunts.CompanyOf).
            foreach (int partner in partners ?? Array.Empty<int>())
                if (data(partner) is { Grades.Count: > 0 } beside) members.Add((partner, GradeNear(beside)));

            var company = roster.Distinct().Select(id => data(id)).Where(m => m is { Grades.Count: > 0 }).ToList();
            for (int i = 0; i < FissureCompany && company.Count > 0; i++)
            {
                var monster = company[dice.Next(company.Count)]!;
                members.Add((monster.Id, GradeNear(monster)));
            }
            return members;
        }

        /// <summary>A give-up (hgn): refused within its first minute, otherwise the hunt over.</summary>
        public static async Task GiveUpAsync(NetworkStream stream, byte[] frame)
        {
            byte[] body = ConnectionProtocol.ReadPayload(frame, Op.Hgn);
            if (body == null) return;
            int type = TreasureHuntProtocol.ReadType(body);
            var state = SessionContext.State;
            var hunt = TreasureHunts.Get(state.CharacterId, type);
            if (hunt == null) return;

            int wait = TreasureHunts.MinutesBeforeGivingUp(hunt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (wait > 0)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, GiveUpTooSoon, wait.ToString())));
                return;
            }
            TreasureHunts.End(state.CharacterId, type);
            Console.WriteLine($"[Hunts] {state.CharacterName} gives the hunt up.");
            await SendFinishedAsync(stream, type);
        }

        /// <summary>On entering the world: the hunts under way, as they stand.</summary>
        public static async Task OnWorldEntryAsync(GameSession session)
        {
            foreach (var hunt in TreasureHunts.All(session.CharacterId))
                await session.SendAsync(TreasureHuntProtocol.Hunt(hunt));
        }

        /// <summary>
        /// The Treasure fought, for the one whose hunt dug it up: beaten, his chest and the hunt over;
        /// lost, a try spent -- «Une défaite au combat final fait perdre un essai en chasse normale» --
        /// and with none left, the hunt lost.
        /// </summary>
        private static async Task TreasureFoughtAsync(NetworkStream stream, long who, bool won)
        {
            var hunt = TreasureHunts.Get(who, TreasureHunts.Classic);
            if (hunt == null || !hunt.ReadyToFight) return;
            if (won)
            {
                TreasureHunts.End(who, TreasureHunts.Classic);
                var kind = TreasureQuests.OfQuest(hunt.Quest);
                if (kind != null) await Equipment.GiveAsync(stream, kind.Chest, 1);
                Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} has beaten the Treasure.");
                await SendFinishedAsync(stream, TreasureHunts.Classic);
                return;
            }
            hunt.Retries--;
            if (hunt.Retries <= 0)
            {
                TreasureHunts.End(who, TreasureHunts.Classic);
                Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} has lost to the Treasure: the hunt is lost.");
                await SendFinishedAsync(stream, TreasureHunts.Classic);
                return;
            }
            TreasureHunts.Save(hunt);
            await SendHuntAsync(stream, hunt);
        }

        /// <summary>
        /// A fight over, for one who fought it: a fissure beaten gives its dimension's portal, and
        /// ends the hunt of whoever was after it, with its chest; lost, his hunt is lost.
        /// </summary>
        public static async Task FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            long who = SessionContext.State.CharacterId;
            if (fight.Rojo.Any(m => m.MonsterId == TreasureFight.Treasure && !m.EsInvocado))
            {
                await TreasureFoughtAsync(stream, who, won);
                return;
            }
            foreach (var monster in fight.Rojo)
            {
                if (monster.EsInvocado || monster.MonsterId <= 0) continue;
                var legendary = LegendaryHunts.EndingWith(monster.MonsterId);
                if (legendary == null) continue;
                var dimension = TravelersTower.LegendaryOf(legendary.Id);

                // A wanted criminal's: «seul le lanceur reçoit le coffre», and «en cas de défaite, la
                // chasse est perdue, mais la carte est récupérée» (DPLN).
                if (dimension == null)
                {
                    var mine = TreasureHunts.Get(who, TreasureHunts.Legendary);
                    if (mine == null || mine.Legendary != legendary.Id || !mine.ReadyToFight) continue;
                    TreasureHunts.End(who, TreasureHunts.Legendary);
                    await Equipment.GiveAsync(stream, won ? legendary.Chest : legendary.MapItem, 1);
                    Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} has " + (won ? "beaten" : "lost to") +
                                      $" criminal {legendary.Monster} of legendary hunt {legendary.Id}.");
                    await SendFinishedAsync(stream, TreasureHunts.Legendary);
                    return;
                }

                if (!won)
                {
                    var lost = TreasureHunts.Get(who, TreasureHunts.Legendary);
                    if (lost != null && lost.Legendary == legendary!.Id && lost.ReadyToFight)
                    {
                        TreasureHunts.End(who, TreasureHunts.Legendary);
                        Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} has lost to {dimension.Name}'s fissure: the hunt is lost.");
                        await SendFinishedAsync(stream, TreasureHunts.Legendary);
                    }
                    return;
                }

                if (!Alterations.Has(who, dimension.Alteration))
                {
                    await AlterationHandler.GainAsync(stream, dimension.Alteration);
                    Console.WriteLine($"[Hunts] {SessionContext.State.CharacterName} has found {dimension.Name}'s fissure: its portal is open to him.");
                }

                var hunt = TreasureHunts.Get(who, TreasureHunts.Legendary);
                if (hunt != null && hunt.Legendary == legendary!.Id && hunt.ReadyToFight)
                {
                    TreasureHunts.End(who, TreasureHunts.Legendary);
                    if (legendary.Chest > 0) await Equipment.GiveAsync(stream, legendary.Chest, 1);
                    await SendFinishedAsync(stream, TreasureHunts.Legendary);
                }
                return;
            }
        }
    }
}
