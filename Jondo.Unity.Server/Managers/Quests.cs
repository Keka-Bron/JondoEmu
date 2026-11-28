using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;
using Game = Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The quest engine: what a character has in hand, and what moves it on.
    /// </summary>
    /// <remarks>
    /// Two halves that must not be confused. The <b>catalogue</b> is Ankama's — 1,976 quests, the
    /// same for everybody, read once at startup and never written — so it is static. The
    /// <b>progress</b> is one player's, so it lives on <see cref="SessionState"/> and is reached
    /// through <c>SessionContext.State</c>. Putting progress in a static field is the exact bug
    /// the August 2026 session refactor removed, and it would not show up with one client
    /// connected: talking to an NPC would advance somebody else's quest.
    ///
    /// The rules about what happens live in <see cref="QuestLog"/>, in the shared project, where
    /// they can be tested without a server. This class is the join between those rules, the
    /// database and the wire, and it owns exactly one thing of its own: the decision of which
    /// packets each change is worth.
    /// </remarks>
    public static class Quests
    {
        /// <summary>
        /// Volatile, and assigned only once <see cref="QuestCatalogue"/> is fully built: readers
        /// take no lock, so this assignment is what publishes the catalogue to them. Without it a
        /// reader could hold a non-null Book whose innards were still being filled in.
        /// </summary>
        private static volatile QuestCatalogue? _book;
        private static readonly object _loadLock = new object();

        /// <summary>«Misión terminada»: the lqn every finished quest of the captures opens its close with, the quest as its parameter.</summary>
        public const int QuestFinishedMessage = 56;

        /// <summary>«Nueva misión»: the lqn before every ief of the tutorial capture, the quest as its parameter.</summary>
        public const int QuestStartedMessage = 54;

        /// <summary>«Misión actualizada»: the lqn before every idz and ieb of the tutorial capture.</summary>
        public const int QuestUpdatedMessage = 55;

        /// <summary>An info line naming a quest: lqn 54, 55 or 56.</summary>
        private static Task TellAsync(GameSession session, int message, int questId)
            => session.SendAsync(new Game.TextInformationEvent(Game.TextInformationType.TextInformationMessage,
                message, [questId.ToString(CultureInfo.InvariantCulture)]));

        /// <summary>Line of dialogue to the steps it hands over.</summary>
        /// <remarks>
        /// Built once at load. The catalogue itself has no index from a dialogue line back to a
        /// step — only <c>Of(questId)</c> and <c>Step(stepId)</c> — and asking the question the
        /// other way round on every line of every conversation would walk 2,225 steps each time.
        ///
        /// A list rather than a single step because nothing guarantees a line belongs to one quest,
        /// and 1,260 steps declare one. Measured on the real catalogue it is one step per line
        /// everywhere, but a data file is not a promise.
        /// </remarks>
        private static readonly Dictionary<long, List<QuestStep>> HandedOverBy
            = new Dictionary<long, List<QuestStep>>();

        /// <summary>Ankama's catalogue. Null until <see cref="Load"/> has run.</summary>
        public static QuestCatalogue? Book => _book;

        /// <summary>This character's log. Null before entering the world.</summary>
        public static QuestLog? Log => SessionContext.State.Quests;

        /// <summary>
        /// Reads the catalogue. Once, at startup: it is 3 MB of JSON and it never changes.
        /// </summary>
        /// <remarks>
        /// THE CATALOGUE IS PUBLISHED LAST, once its two side tables are built. It used to be
        /// assigned the moment it was constructed, several lines before HandedOverBy and GivenBy
        /// were filled from it, so anybody who checked Ready in that window got a catalogue whose
        /// indexes were still empty -- an NPC that hands over a quest step would have had nothing
        /// to hand over, and said nothing about it.
        /// </remarks>
        public static void Load()
        {
            if (_book != null) return;
            lock (_loadLock)
            {
                if (_book != null) return;

                // No ClientText: the server has no use for translated quest names, and handing it
                // one would make it read a 339,342-entry language file it would never look at.
                var book = new QuestCatalogue(null, Console.WriteLine);
                if (!book.Ready)
                {
                    Console.WriteLine("[Quests] There is no catalogue. Nobody will be able to take a quest.");
                    _book = book;   // published anyway: Ready stays false and Load does not retry
                    return;
                }

                Build(book);
                _book = book;
            }
        }

        /// <summary>Fills the indexes that hang off the catalogue, before it is published.</summary>
        private static void Build(QuestCatalogue book)
        {
            HandedOverBy.Clear();
            foreach (var quest in book.All())
            {
                foreach (var step in quest.Steps)
                {
                    if (step.DialogId <= 0) continue;

                    if (!HandedOverBy.TryGetValue(step.DialogId, out var steps))
                    {
                        HandedOverBy[step.DialogId] = steps = new List<QuestStep>();
                    }

                    steps.Add(step);
                }
            }

            GivenBy.Clear();
            foreach (var quest in book.All())
            {
                foreach (var giver in quest.Givers)
                {
                    if (giver.NpcId == 0) continue;
                    if (!GivenBy.TryGetValue(giver.NpcId, out var mine))
                    {
                        GivenBy[giver.NpcId] = mine = new List<(int, long)>();
                    }

                    mine.Add((quest.Id, giver.MapId));
                }
            }

            Bindings = QuestBindingContent.Load(
                Jondo.Unity.Launcher.Paths.ContentFile(QuestBindingContent.AuthoredFile),
                Console.WriteLine);

            Console.WriteLine($"[Quests] {book.QuestCount:N0} quests, {book.StepCount:N0} steps, " +
                              $"{HandedOverBy.Count:N0} lines that hand one out, " +
                              $"{GivenBy.Count:N0} NPCs that give them.");

            if (Bindings.Count > 0)
            {
                Console.WriteLine($"[Quests] {Bindings.Count} objective(s) tied to something to click, " +
                                  $"on {Bindings.ElementCount} element(s).");
            }
        }

        /// <summary>
        /// Puts this character's quest log on, from the database.
        /// </summary>
        /// <remarks>
        /// Called on entering the world. It replaces the log rather than adding to it, because the
        /// same connection can go back to the character list and come in with a different
        /// character of the same account — and a log that was merged instead of replaced would give
        /// the second one the first one's quests.
        /// </remarks>
        public static void LoadFrom(long characterId)
        {
            if (_book == null || !_book.Ready)
            {
                SessionContext.State.Quests = null;
                return;
            }

            // The session is read through when asked, not captured: the same connection can come
            // back in with another character, and these must answer for whoever is playing.
            // And the character as the achievements know him: his class (PG, the class quests),
            // his jobs (PJ), what he holds (PO), the server's type (SC) and his alignment.
            var facts = new Achievements.CharacterFacts();
            var log = new QuestLog(_book,
                () => SessionContext.State.CharacterLevel,
                () => SessionContext.State.MapId,
                op => Almanax.Scalar(op) ?? facts.Scalar(op),
                achievement => SessionContext.State.Achievements?.Has(achievement) ?? false,
                (op, key, flag) => facts.Count(op, key, flag));

            int rows = 0;
            foreach (var row in DatabaseManager.LoadQuestProgress(characterId))
            {
                log.Restore(row.QuestId, row.StepId, row.Objectives, row.Completed,
                    row.CompletedAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(row.CompletedAt).LocalDateTime : default);
                rows++;
            }

            SessionContext.State.Quests = log;
            if (rows > 0) Console.WriteLine($"[Quests] {rows} in the journal of character {characterId}.");
        }

        /// <summary>The steps a line of dialogue hands over. Empty for the other 53,777 lines.</summary>
        public static IReadOnlyList<QuestStep> StepsHandedOverBy(long dialogId)
            => HandedOverBy.TryGetValue(dialogId, out var steps)
                ? steps
                : (IReadOnlyList<QuestStep>)Array.Empty<QuestStep>();

        /// <summary>
        /// The player has picked a reply while on a line that hands a quest over.
        /// </summary>
        /// <remarks>
        /// The capture puts the start after the reply, not on arriving at the line: the server
        /// walks the conversation to line 50071, the player picks reply 66788, and only then does
        /// <c>ief {2432}</c> go out.
        ///
        /// <b>Any reply on that line starts it, and that is a simplification.</b> A real
        /// conversation could offer "yes" and "no thanks" on the same line and only one of them
        /// ought to hand the quest over, and nothing in Ankama's data says which — the extra field
        /// their replies carry is not a quest marker, it is on 184 of the 429 captured replies and
        /// almost none of them are quest lines. Since the dialogue trees here are authored by hand,
        /// the honest fix when it matters is to say so in the tree; until then a quest line should
        /// be written with one reply.
        /// </remarks>
        public static async Task OnReplyAsync(GameSession session, long dialogId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || dialogId <= 0) return;

            int npcId = SessionContext.State.OpenDialogueNpcId;
            foreach (var step in StepsHandedOverBy(dialogId))
            {
                await StartSharedAsync(session, step.QuestId, () => CanShareNpc(npcId));
            }

            // The Almanax offering and «El año del dragón» are Ontoral's own replies now
            // (Handlers.AlmanaxOfferingHandler): any reply of his used to take them, his farewell too.
        }

        /// <summary>
        /// The player is talking to an NPC. Tick off any objective that just wanted that.
        /// </summary>
        /// <remarks>
        /// Types 1 and 9 — "go and see #1" and "go back and see #1" — are 5,012 of the 15,547
        /// objectives, the second biggest kind after free text, and nothing was closing them.
        /// Without this a quest whose next step is "go and see Tek Abir" stops there for ever,
        /// however faithfully its dialogue is written.
        ///
        /// Only the step in hand is looked at, which <see cref="QuestLog.Tick"/> enforces anyway:
        /// walking past an NPC a later step will want must not skip the steps in between.
        /// </remarks>
        public static async Task OnTalkingToAsync(GameSession session, int npcId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null || npcId == 0) return;

            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;

                foreach (var objective in step.Objectives)
                {
                    if (objective.NpcId != npcId) continue;
                    if (!await CompleteNpcObjectiveAsync(session, step, objective, run, npcId,
                            team: () => HeroQuestItems.PoolAsync(session, new[] { (objective.ItemId, objective.ItemCount) },
                                hero => CanShareNpc(npcId) && SharedObjective(run.QuestId, step.Id, objective.Id, out _))))
                        continue;

                    await Heroes.ShareQuestActionAsync(async hero =>
                    {
                        if (CanShareNpc(npcId) && SharedObjective(run.QuestId, step.Id, objective.Id, out var pending))
                            await CompleteNpcObjectiveAsync(hero, step, objective, pending, npcId);
                    },
                    quests: new[] { run.QuestId },
                    explain: hero => MissingFor(hero, objective));
                }

                if (run.QuestId == Almanax.Dragon.Quest) continue;

                // And the free-text ones that are a conversation in all but the catalogue's type:
                // "Parler à un vieux de la vieille" is a talk objective Ankama wrote as prose.
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Talk) continue;
                    if (binding.NpcId != npcId) continue;
                    if (binding.MapId != 0 && binding.MapId != SessionContext.State.MapId) continue;

                    await CloseSharedAsync(session, binding, () => CanShareNpc(npcId));
                }
            }
        }

        internal static bool CanShareNpc(int npcId)
            => Npcs.OnMap(SessionContext.State.MapId).Any(spawn => spawn.NpcId == npcId);

        private static async Task<bool> CompleteNpcObjectiveAsync(GameSession session, QuestStep step,
                                                                  QuestObjective objective, QuestRun run, int npcId,
                                                                  Func<Task<bool>>? team = null)
        {
            if (run.Finished || run.StepId != step.Id || run.Done.Contains(objective.Id)) return false;
            if (run.QuestId == Almanax.Dragon.Quest || !Revealed(step, objective, run)) return false;

            long mapId = SessionContext.State.MapId;
            var tree = NpcDialogues.For(npcId, mapId);
            // Authored replies and the Almanax offering handle their own hand-ins.
            if (tree?.HasReplyFor(objective.Id) == true || NpcDialogues.AnyHasReplyFor(npcId, objective.Id)) return false;
            if (Bindings.Of(objective.Id)?.Kind == QuestBindingKind.Reply) return false;
            if (Almanax.IsOffering(run.QuestId)
                && (npcId == Almanax.Giver || !Handlers.AlmanaxOfferingHandler.MaySeeTheSaint(run.QuestId))) return false;
            if (objective.MapId != 0 && objective.MapId != mapId && StandsOn(npcId, objective.MapId)) return false;

            if (objective.ItemId != 0 && objective.ConsumesItems
                && Handlers.DungeonHandler.TakesAtTheDoor(npcId, mapId, objective.ItemId)) return false;
            // The master's: what he and the heroes following him lack, out of the team's bags.
            if (objective.ItemId != 0 && objective.ConsumesItems && team != null && !await team()) return false;
            if (objective.ItemId != 0 && !await HandOverAsync(session, objective)) return false;
            if (objective.SoulMonster != 0 && !await HandOverSoulsAsync(session, objective)) return false;

            await TickAsync(session, run.QuestId, objective.Id);
            return true;
        }

        /// <summary>
        /// Whether the NPC stands on that map at all. A conversation objective naming a map he is
        /// never on -- 1631's «go back and see Ganymed» on the temple's book room -- is the catalogue
        /// being older than the world, and talking to him wherever he stands does it.
        /// </summary>
        private static bool StandsOn(int npcId, long mapId)
        {
            foreach (var spawn in Npcs.Of(mapId))
            {
                if (spawn.NpcId == npcId) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a conversation objective has come up yet in its step.
        /// </summary>
        /// <remarks>
        /// The real server reveals a step's objectives as they become relevant (a growing prefix,
        /// see Shown), and a hello only closes the ones that are there. 1629
        /// asks to see Master Curro twice -- 9656 on arriving, 10015 with the ring -- and one hello
        /// closed both, the quest done before a single resource was gathered. So «go back and see
        /// #1» (type 9) waits for everything listed before it, and «go and see #1» (type 1) for
        /// whatever before it is not another conversation: five people to visit in one step
        /// («Un pueblo sobre las nubes») are still visited in any order.
        /// </remarks>
        private static bool Revealed(QuestStep step, QuestObjective objective, QuestRun run)
        {
            // What its binding asks to come first: the castle's gate of «Jamais deux sans trois» is
            // gone to once the three wabbits have been heard out, though the catalogue lists it
            // after three conversations, which never hold anything back -- a walk past the castle
            // ticked it before the first wabbit.
            var own = Bindings.Of(objective.Id);
            if (own != null && own.Kind != QuestBindingKind.Objectives
                && own.AfterObjectives.Any(before => !run.Done.Contains(before) && Log?.ObjectiveDone(before) != true))
                return false;

            bool anyOrder = own?.AnyOrder == true;
            foreach (var before in step.Objectives)
            {
                if (before.Id == objective.Id) return true;
                if (run.Done.Contains(before.Id)) continue;
                if (anyOrder && Bindings.Of(before.Id)?.AnyOrder == true) continue;
                if (objective.TypeId == QuestCatalogue.GoBackToSee) return false;
                if (before.TypeId != QuestCatalogue.GoToSee) return false;
            }
            return true;
        }

        /// <summary>
        /// The items an objective asks for, checked and — when it says so — taken.
        /// </summary>
        /// <remarks>
        /// The difference between the two types is the whole of it. "Montrer à X" is shown and kept;
        /// "Ramener à X" is handed over and gone. Treating them alike either robs a player of
        /// something they were only asked to display, or lets one objective be finished five times
        /// with the same five ortie.
        ///
        /// Silent when the items are not there, because this runs on every hello: saying "you are
        /// missing three ortie" to somebody who only walked past would be chat noise on every step
        /// of every quest that ends at an NPC.
        /// </remarks>
        /// <summary>
        /// Whether an objective of a quest in hand is still behind others of its step: a reply
        /// that does it is not offered yet. Erty's «Hablarle de la conversación con Ramón Santo»
        /// showed from the first talk and did nothing. False for an objective of no quest in hand,
        /// so the replies written to be always there keep their old behaviour.
        /// </summary>
        public static bool NotYetRevealed(int objectiveId)
        {
            var log = Log;
            if (log == null || _book == null || objectiveId == 0) return false;
            foreach (var run in log.Doing())
            {
                var step = _book.Step(run.StepId);
                var objective = step?.Objectives.FirstOrDefault(o => o.Id == objectiveId);
                if (step == null || objective == null) continue;
                return !run.Done.Contains(objectiveId) && !Revealed(step, objective, run);
            }
            return false;
        }

        /// <summary>
        /// Whether what an objective asks to hand over is carried: true for one that asks for no
        /// item nor soul. A reply that does a «bring» objective is offered only then -- Cindy Cato's
        /// fifteen boxes, Kerubim's purse for Primir, the Professor's soul of Thor Pestruz.
        /// </summary>
        public static bool CanHandOver(int objectiveId)
        {
            if (_book == null || objectiveId == 0) return true;
            if (Log?.ObjectiveDone(objectiveId) == true) return true;
            var objective = _book.Objective(objectiveId);
            if (objective == null) return true;
            if (objective.SoulMonster != 0) return SoulsCarried(objective.SoulMonster) >= objective.SoulCount;
            if (objective.ItemId == 0) return true;
            return Equipment.HowMany(objective.ItemId) >= Math.Max(1, objective.ItemCount);
        }

        /// <summary>How many souls of a monster the full soul stones carried hold.</summary>
        private static int SoulsCarried(int monster)
            => Equipment.All.Sum(stone => stone.Effects.Count(e => e.Effect == SoulCapture.SoulEffect && e.Value == monster)
                                          * Math.Max(1, stone.Quantity));

        /// <summary>
        /// Whether a reply that does these objectives can be offered for what is carried: all their
        /// items, or -- a partial hand-over -- the items or souls of one still to do: Tungusa takes
        /// whichever of the five souls for Talma Esmya are brought, the rest another time.
        /// </summary>
        public static bool HandsOverEnough(IEnumerable<int> objectiveIds, bool partial)
        {
            if (!partial) return objectiveIds.All(CanHandOver);
            var log = Log;
            return objectiveIds.Any(id =>
            {
                if (log?.ObjectiveDone(id) == true) return false;
                var objective = _book?.Objective(id);
                if (objective == null) return false;
                if (objective.SoulMonster != 0) return SoulsCarried(objective.SoulMonster) >= objective.SoulCount;
                return objective.ItemId != 0
                       && Equipment.HowMany(objective.ItemId) >= Math.Max(1, objective.ItemCount);
            });
        }

        private static async Task<bool> HandOverAsync(GameSession session, QuestObjective objective)
        {
            var stream = session.Stream;
            int wanted = Math.Max(1, objective.ItemCount);
            if (Equipment.HowMany(objective.ItemId) < wanted) return false;

            if (!objective.ConsumesItems) return true;

            // A follower handed over leaves his slot and the character is drawn without him:
            // Mimicandy given to Riberico («Tesorfanato») kept walking behind the player.
            bool taken = await QuestFollowers.TakeAsync(stream, objective.ItemId, wanted);
            if (!taken)
            {
                Console.WriteLine($"[Quests] Could not collect {wanted}x{objective.ItemId} " +
                                  $"for objective {objective.Id}; it is left unmarked.");
            }

            return taken;
        }

        /// <summary>
        /// The souls an objective asks for, taken with the full soul stones that hold them.
        /// </summary>
        /// <remarks>
        /// A full stone keeps one 623 «#3 (#2)» a soul (SoulCapture): the monster in the value. The
        /// stones go whole -- a soul is not taken out of one -- the ones holding the fewest souls
        /// first, so that a stone full of others is the last to go. Silent when they are not there,
        /// as the items are.
        /// </remarks>
        private static async Task<bool> HandOverSoulsAsync(GameSession session, QuestObjective objective)
        {
            var stream = session.Stream;
            int monster = objective.SoulMonster, wanted = objective.SoulCount;
            int SoulsIn(Equipment.Item stone) => stone.Effects.Count(e => e.Effect == SoulCapture.SoulEffect && e.Value == monster);

            var stones = Equipment.All.Where(i => SoulsIn(i) > 0)
                                      .OrderBy(i => i.Effects.Count(e => e.Effect == SoulCapture.SoulEffect))
                                      .ToList();
            if (SoulsCarried(monster) < wanted) return false;

            int still = wanted;
            foreach (var stone in stones)
            {
                int each = SoulsIn(stone);
                for (int n = Math.Max(1, stone.Quantity); n > 0 && still > 0; n--)
                {
                    if (!await Handlers.ItemUseHandler.SpendOneAsync(session, stone.Uid)) return false;
                    still -= each;
                }
                if (still <= 0) break;
            }
            Console.WriteLine($"[Quests] {wanted} soul(s) of {monster} handed over for objective {objective.Id}.");
            return true;
        }

        /// <summary>The bindings from free-text objectives to the things that finish them.</summary>
        public static QuestBindingBook Bindings { get; private set; } = new QuestBindingBook();

        /// <summary>
        /// The quests' authored content read again -- the bindings and the criteria -- while the
        /// server runs: the harness's «reload», so that a quest being written can be played without
        /// a restart throwing every client out. The catalogue itself is the client's and stays.
        /// </summary>
        public static void ReloadContent()
        {
            Bindings = QuestBindingContent.Load(
                Jondo.Unity.Launcher.Paths.ContentFile(QuestBindingContent.AuthoredFile),
                Console.WriteLine);
            Jondo.Unity.World.Quests.QuestCriteria.Forget();
        }

        /// <summary>
        /// The player has clicked something a quest cares about.
        /// </summary>
        /// <remarks>
        /// This is what the tutorial capture shows and what nothing did before. Six clicks on the
        /// six elements 541424-541429, and after each one the client asks <c>ieo {1629}</c> — where
        /// has quest 1629 got to. It asks because the click moved it on.
        ///
        /// Only the step in hand counts, which <see cref="QuestLog.Tick"/> enforces: clicking a
        /// stele a later step will want must not skip the steps in between, and clicking one from a
        /// quest already finished must do nothing at all.
        ///
        /// Returns whether anything happened, so the caller knows whether the click was answered.
        /// </remarks>
        public static async Task<bool> OnInteractiveUsedAsync(GameSession session, long mapId, int elementId, int skillInstanceId = 0)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            if (log == null || _book == null) return false;

            bool moved = false;

            // A prop of the scene: its use is already noted down (InteractiveActionHandler), and it may
            // be what another element of the row was waiting for, so the map's elements go out again.
            if (Bindings.PropAt(mapId, elementId).Any(ShouldSee))
            {
                var ficha = DatabaseManager.GetCharacterById(GameState.CharacterId);
                if (ficha != null)
                {
                    await MapInformationService.Live.RefreshActorsAsync(session, ficha);
                }
                Console.WriteLine($"[Quests] Element {elementId} of map {mapId} used for the scene.");
                return true;
            }

            // One objective per press: the cellar's tap is looked at first and filled with lemonade
            // on a second press, two iwo in «Matarratas»' capture.
            foreach (var binding in Bindings.At(mapId, elementId))
            {
                if (binding.AfterElement != 0 && !SessionContext.State.ElementsUsed.Contains(binding.AfterElement)) continue;
                // A fight fought elsewhere: the Purple Dofus's stele of «Como alma en pena» takes the
                // character to the labyrinth room where the age-old minogolem waits.
                if (binding.Kind == QuestBindingKind.Beat && binding.Fight != null)
                {
                    if (await FightElsewhereAsync(session, binding)) { moved = true; break; }
                    continue;
                }
                // A fight's element: pressing it brings the monsters out, and beating them is what
                // does the objective (the old stump of «Restauración rápida» and its two milimulous).
                if (binding.Kind == QuestBindingKind.Beat && binding.SpawnsMonster != 0)
                {
                    // Won already, and its element pressed again: what the fight earned is picked
                    // up («marks»), the soul dust of a tomb of «Examen de paso».
                    // Ticked here and not closed: closing a row with monsters brings them out again.
                    if (binding.Marks != 0 && SessionContext.State.ElementsUsed.Contains(binding.Marks))
                    {
                        if (!InHand(binding)) continue;
                        await TickAsync(session, binding.QuestId, binding.ObjectiveId);
                        moved = true;
                        break;
                    }
                    if (await BringOutAsync(session, binding)) { moved = true; break; }
                    continue;
                }
                if (binding.Kind != QuestBindingKind.Click) continue;
                // Only the named action counts: «Ir a Astrub.» of a temple's statue is a way, not a prayer.
                if (binding.SkillName != 0
                    && (!InteractiveRegistry.TryResolveUse(mapId, elementId, skillInstanceId, out _, out var picked)
                        || picked.SkillNameId != binding.SkillName)) continue;
                // A room to clear first: with a group still on the map the element does nothing.
                if (binding.MapCleared && MobSpawnManager.HasGroups(mapId))
                {
                    Console.WriteLine($"[Quests] Objective {binding.ObjectiveId}: element {elementId} of map {mapId} pressed with monsters still there.");
                    continue;
                }
                // One item per element, up to the row's count: a leaf from each of the four bushes.
                if (binding.GivesEach)
                {
                    if (SessionContext.State.QuestElementsTaken.Contains((binding.ObjectiveId, elementId))) continue;
                    if (await GiveEachAsync(session, binding, mapId, elementId))
                    {
                        SessionContext.State.QuestElementsTaken.Add((binding.ObjectiveId, elementId));
                        // What guards the element: the dostrogo a dodoune egg wakes up, now and then.
                        if (binding.SpawnsMonster != 0
                            && (binding.SpawnsChance == 0 || Random.Shared.Next(100) < binding.SpawnsChance))
                            await SpawnAsync(session, binding);
                        // Without «onlyGives» the gathering IS the objective: «Recoge tres maderas de
                        // cuerbok» is done once each of the three trunks' wood is in the bag.
                        if (!binding.OnlyGives && CarriesEveryOne(binding))
                            await TickAsync(session, binding.QuestId, binding.ObjectiveId);
                        moved = true;
                        break;
                    }
                    // Nothing left to give because it is all carried already -- a quest taken
                    // again keeps what the bag held --: the gathering is done all the same. Without
                    // this the element said nothing and the objective never moved.
                    if (!binding.OnlyGives && InHand(binding) && CarriesEveryOne(binding))
                    {
                        await TickAsync(session, binding.QuestId, binding.ObjectiveId);
                        moved = true;
                        break;
                    }
                    continue;
                }
                // One press per map of the row's list, and the objective is done with the last map's:
                // the four bushes of «Sigue la pista de las bayas». Noted for the session only, as the
                // elements of a «givesEach» row are; a character who comes back inspects them again.
                if (binding.AllMaps)
                {
                    if (!InHand(binding)) continue;
                    var taken = SessionContext.State.QuestElementsTaken;
                    taken.Add((binding.ObjectiveId, elementId));
                    bool everyMap = binding.Elements.GroupBy(e => e.MapId)
                        .All(maps => maps.Any(e => taken.Contains((binding.ObjectiveId, e.ElementId))));
                    if (!everyMap)
                    {
                        Console.WriteLine($"[Quests] Objective {binding.ObjectiveId}: element {elementId} of map {mapId} inspected, more to go.");
                        moved = true;
                        break;
                    }
                }
                if (await CloseSharedAsync(session, binding,
                    () => binding.AfterElement == 0 || SessionContext.State.ElementsUsed.Contains(binding.AfterElement)))
                {
                    moved = true;
                    break;
                }
            }

            return moved;
        }

        /// <summary>
        /// Whether a binding's objective is the character's to do now: its quest under way, the objective
        /// on the step in hand, revealed, not done, and every objective it waits for done.
        /// </summary>
        private static bool InHand(QuestBinding binding)
        {
            var log = Log;
            if (log == null || _book == null) return false;
            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished || run.Done.Contains(binding.ObjectiveId)) return false;
            var step = _book.Step(run.StepId);
            var objective = step?.Objectives.FirstOrDefault(o => o.Id == binding.ObjectiveId);
            if (step == null || objective == null || !Revealed(step, objective, run)) return false;
            foreach (int before in binding.AfterObjectives)
            {
                if (!run.Done.Contains(before) && !log.ObjectiveDone(before)) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether every item a «givesEach» row hands over is in the bag: one of each element's own,
        /// or the row's count of what it gives.
        /// </summary>
        private static bool CarriesEveryOne(QuestBinding binding)
            => binding.ElementItems.Count > 0
                ? binding.ElementItems.Values.Distinct().All(item => Equipment.HowMany(item) >= 1)
                : binding.Gives.All(g => Equipment.HowMany(g.Item) >= g.Count);

        /// <summary>
        /// One of a «givesEach» row's items, when its objective is the character's to do and fewer
        /// than the row's count are carried. Nothing is marked.
        /// </summary>
        private static async Task<bool> GiveEachAsync(GameSession session, QuestBinding binding, long mapId, int elementId)
        {
            var stream = session.Stream;
            var log = Log;
            if (log == null || _book == null) return false;
            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished || run.Done.Contains(binding.ObjectiveId)) return false;
            var step = _book.Step(run.StepId);
            var objective = step?.Objectives.FirstOrDefault(o => o.Id == binding.ObjectiveId);
            if (step == null || objective == null || !Revealed(step, objective, run)) return false;
            // What the gathering is done with: the eternales' powder comes off with Dlim's dust catcher.
            foreach (var (needed, count) in binding.Requires)
            {
                if (Equipment.HowMany(needed) >= count) continue;
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.MissingItem)));
                return false;
            }
            bool gave = false;
            var gives = binding.ElementItems.TryGetValue((mapId, elementId), out int own)
                ? new[] { (own, 1) }
                : binding.Gives.ToArray();
            foreach (var (item, count) in gives)
            {
                if (Equipment.HowMany(item) >= count) continue;
                if (!await QuestFollowers.GiveAsync(stream, item, 1)) continue;
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        InfoMessages.ItemGained, "1", item.ToString(CultureInfo.InvariantCulture))));
                gave = true;
            }
            // What the place says when it gives: each of the goblin's four hiding places has its own line.
            int says = binding.ElementSays.TryGetValue((mapId, elementId), out int line) ? line : binding.Says;
            if (gave && says != 0)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(0, says)));
            }
            return gave;
        }

        /// <summary>Whether a row's «waited» objective is done and its minutes have gone by; true without one.</summary>
        internal static bool WaitedEnough(QuestBinding binding, QuestLog log)
            => binding.Waited.Count != 2 || log.WaitedSince(binding.Waited[0], binding.Waited[1]);

        /// <summary>
        /// Marks a bound objective off, if this character is really on it.
        /// </summary>
        /// <remarks>
        /// The guard is the whole safety of the binding file. A row is a claim that clicking
        /// something finishes an objective; without checking the run, a player who has never taken
        /// the quest would finish an objective of it by walking past a stele, and one who finished
        /// it last week would do it again.
        /// </remarks>
        /// <summary>
        /// Brings out a beat binding's monsters, when its objective is the character's to do now:
        /// nothing is marked, the fight does it.
        /// </summary>
        private static async Task<bool> BringOutAsync(GameSession session, QuestBinding binding)
        {
            var stream = session.Stream;
            var log = Log;
            if (log == null || _book == null) return false;
            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished || run.Done.Contains(binding.ObjectiveId)) return false;
            var step = _book.Step(run.StepId);
            var objective = step?.Objectives.FirstOrDefault(o => o.Id == binding.ObjectiveId);
            if (step == null || objective == null || !Revealed(step, objective, run)) return false;
            foreach (int before in binding.AfterObjectives)
            {
                if (!run.Done.Contains(before) && !log.ObjectiveDone(before)) return false;
            }
            await SpawnAsync(session, binding);
            return true;
        }

        /// <summary>
        /// Starts the fight a «beat» row's element names («fight»): its monsters, on its arena, the
        /// character as the monster it incarnates; a win leaves him on the row's win map, a loss --
        /// free when the row says so -- where he pressed. Only while the objective is his to do.
        /// </summary>
        private static async Task<bool> FightElsewhereAsync(GameSession session, QuestBinding binding)
        {
            var stream = session.Stream;
            var log = Log;
            var fight = binding.Fight;
            if (log == null || _book == null || fight == null || binding.AnyOfMonsters.Count == 0) return false;
            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished || run.Done.Contains(binding.ObjectiveId)) return false;
            var step = _book.Step(run.StepId);
            var objective = step?.Objectives.FirstOrDefault(o => o.Id == binding.ObjectiveId);
            if (step == null || objective == null || !Revealed(step, objective, run)) return false;
            foreach (int before in binding.AfterObjectives)
            {
                if (!run.Done.Contains(before) && !log.ObjectiveDone(before)) return false;
            }

            var group = MobSpawnManager.ComposeOffMap(binding.AnyOfMonsters.Select(m => (m, 0)));
            if (group == null) return false;
            if (fight.Incarnates != 0)
                Handlers.FightHandler.IncarnateInNextFight(GameState.CharacterId, fight.Incarnates,
                                                           fight.IncarnationGrade, false);
            await Handlers.FightHandler.InitiateFightFromMobCollision(stream, group, SessionContext.State.MapId,
                arenaMapId: fight.ArenaMapId, winMapId: fight.WinMapId, winCellId: fight.WinCellId,
                defeatFree: fight.DefeatFree, solo: null,
                loseMapId: fight.LoseMapId, loseCellId: fight.LoseCellId);
            // Taken by the fight when it was built; one that did not start leaves none for the next.
            Handlers.FightHandler.TakeNextIncarnation(GameState.CharacterId);
            Console.WriteLine($"[Quests] Objective {binding.ObjectiveId}: its fight on {fight.ArenaMapId}.");
            return true;
        }

        private static async Task<bool> CloseSharedAsync(GameSession session, QuestBinding binding,
                                                          Func<bool>? canShare = null)
        {
            int stepId = Log?.Run(binding.QuestId)?.StepId ?? 0;
            bool shared = binding.SpawnsMonster == 0 && !binding.OnlyGives;
            Func<Task<bool>>? team = shared && binding.Starts == 0
                ? () => HeroQuestItems.PoolAsync(session, binding.Requires,
                    hero => canShare?.Invoke() != false && SharedObjective(binding.QuestId, stepId, binding.ObjectiveId, out _))
                : null;
            if (!await CloseAsync(session, binding, team)) return false;

            if (shared)
            {
                await Heroes.ShareQuestActionAsync(async hero =>
                {
                    if (canShare?.Invoke() == false) return;

                    if (binding.Starts != 0)
                    {
                        await CloseAsync(hero, binding);
                    }
                    else if (SharedObjective(binding.QuestId, stepId, binding.ObjectiveId, out _))
                    {
                        await CloseAsync(hero, binding);
                    }
                },
                quests: new[] { binding.Starts != 0 ? binding.Starts : binding.QuestId },
                explain: hero => MissingFor(hero, binding)
                                 ?? (binding.Starts != 0 ? Heroes.CouldNotStart(hero, binding.Starts) : null));
            }
            return true;
        }

        /// <summary>
        /// How far a character is in a quest, to compare a hero's with his master's: -1 without it,
        /// the step's place in the quest then the objectives ticked on it, the most once it is over.
        /// </summary>
        internal static int ProgressOf(GameSession session, int questId)
        {
            var run = session.State.Quests?.Run(questId);
            if (run == null) return -1;
            if (run.Finished) return int.MaxValue;
            var steps = _book?.Of(questId)?.Steps;
            int index = 0;
            if (steps != null)
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    if (steps[i].Id == run.StepId) { index = i; break; }
                }
            }
            return index * 1000 + Math.Min(999, run.Done.Count);
        }

        /// <summary>
        /// Why a hero could not hand an objective over with his master: the items it asks for, when
        /// he has fewer of them than it takes. Null when that is not what stopped him.
        /// </summary>
        internal static Heroes.LagCause? MissingFor(GameSession hero, QuestObjective objective)
        {
            if (objective.ItemId == 0) return null;
            int wanted = Math.Max(1, objective.ItemCount);
            int has;
            using (SessionContext.Push(hero)) has = Equipment.HowMany(objective.ItemId);
            return has < wanted
                ? new Heroes.LagCause(Heroes.QuestLagReason.MissingItems, objective.ItemId, wanted - has)
                : null;
        }

        /// <summary>The same for an element or a binding that asks to carry items: the first one he lacks.</summary>
        internal static Heroes.LagCause? MissingFor(GameSession hero, QuestBinding binding)
        {
            using (SessionContext.Push(hero))
            {
                foreach (var (item, count) in binding.Requires)
                {
                    int has = Equipment.HowMany(item);
                    if (has < count) return new Heroes.LagCause(Heroes.QuestLagReason.MissingItems, item, count - has);
                }
            }
            return null;
        }

        internal static bool SharedObjective(int questId, int stepId, int objectiveId, out QuestRun run)
        {
            run = Log?.Run(questId)!;
            if (run == null || run.Finished || run.StepId != stepId || run.Done.Contains(objectiveId))
                return false;

            var step = _book?.Step(stepId);
            var objective = step?.Objectives.FirstOrDefault(o => o.Id == objectiveId);
            return step != null && objective != null && AppliesTo(Log!, objectiveId)
                   && Revealed(step, objective, run);
        }

        private static async Task<bool> CloseAsync(GameSession session, QuestBinding binding,
                                                   Func<Task<bool>>? team = null)
        {
            var stream = session.Stream;
            var log = Log;
            if (log == null || _book == null) return false;

            // What has to be carried: if it is not met, the element does NOTHING -- neither hands out,
            // nor marks, nor brings out the creature --, which is the difference between «pressing the
            // tap with the lemonade» and «pressing the tap». Asked only once the objective is this
            // character's to do: a cawotte clicked long after «Ça sent la cawotte» says nothing about
            // the syringe.
            async Task<bool> CarriesWhatItAsksAsync()
            {
                foreach (var (item, count) in binding.Requires)
                {
                    if (Equipment.HowMany(item) >= count) continue;

                    Console.WriteLine($"[Quests] Objective {binding.ObjectiveId} asks for {count}x{item} " +
                                      $"and it is not being carried.");
                    // A hello says nothing about it: valet Doz greeted before the livery is sewn is not
                    // a tap pressed without the lemonade.
                    if (binding.Kind != QuestBindingKind.Talk && binding.Kind != QuestBindingKind.Enter)
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                                InfoMessages.Warning, InfoMessages.MissingItem)));
                    return false;
                }
                foreach (var (item, count) in binding.Worn)
                {
                    if (Equipment.HowManyWorn(item) >= count) continue;
                    Console.WriteLine($"[Quests] Objective {binding.ObjectiveId} asks for {item} worn and it is not.");
                    // Silent on a hello or on arriving, as for what is carried.
                    if (binding.Kind != QuestBindingKind.Talk && binding.Kind != QuestBindingKind.Enter)
                        await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                            ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                                InfoMessages.Warning, InfoMessages.MissingItem)));
                    return false;
                }
                return true;
            }

            // An element that hands out the quest: it is taken here and that is it: there is no objective to
            // mark yet, because the first step starts right now.
            if (binding.Starts != 0)
            {
                if (log.Run(binding.Starts) != null) return false;
                if (!await CarriesWhatItAsksAsync()) return false;

                Console.WriteLine($"[Quests] Quest {binding.Starts} starts on clicking an element.");
                if (!await StartAsync(session, binding.Starts)) return false;
                // And what it hands over with the quest: the Moonies' first parchment, found among
                // the wreck's books.
                foreach (var (item, count) in binding.Gives)
                {
                    if (Equipment.HowMany(item) > 0 || !await QuestFollowers.GiveAsync(stream, item, count)) continue;
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            InfoMessages.ItemGained, count.ToString(CultureInfo.InvariantCulture),
                            item.ToString(CultureInfo.InvariantCulture))));
                }

                // A quest that ends as it starts: the straw parasol of the castaways' island hands
                // «El secreto de los charcosos» out and is also its only objective, so the same
                // press does both (Dofus pour les noobs: «la quête se termine aussitôt lancée»).
                foreach (var (where, element) in binding.Elements)
                {
                    foreach (var same in Bindings.At(where, element))
                    {
                        if (same.QuestId == binding.Starts && same.Starts == 0 && same.Kind == QuestBindingKind.Click)
                            await CloseAsync(session, same);
                    }
                }
                return true;
            }

            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished) return false;
            if (run.Done.Contains(binding.ObjectiveId)) return false;

            var step = _book.Step(run.StepId);
            if (step == null) return false;

            bool wanted = false;
            foreach (var objective in step.Objectives)
            {
                if (objective.Id == binding.ObjectiveId) { wanted = true; break; }
            }

            if (!wanted) return false;

            // What has to come first, for a kind that is not itself waiting on them.
            if (binding.Kind != QuestBindingKind.Objectives)
            {
                foreach (int before in binding.AfterObjectives)
                {
                    if (!run.Done.Contains(before) && !log.ObjectiveDone(before)) return false;
                }
            }

            // And the time it waits after one of them: the brew ferments for an hour first.
            if (!WaitedEnough(binding, log)) return false;

            // The master's: what he and the heroes following him lack, out of the team's bags.
            if (team != null && binding.SpendsRequired && binding.Requires.Count > 0 && !await team()) return false;
            if (!await CarriesWhatItAsksAsync()) return false;

            int says = binding.SaysFor(Equipment.HowMany);
            if (says != 0)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(0, says)));
            }

            // What it puts in the bag, before the tick: the item is the reason the objective is
            // finished, so a hand-out that fails must not leave it marked done. Not so for a
            // conversation that hands things over: the conversation is the reason, and the
            // tutorial capture validates Ganymed's step (idz 3169) before his six items arrive.
            // An element that only hands something over: given once, the objective left alone.
            if (binding.OnlyGives)
            {
                // Or that only takes what it asks for: each of the four pillars of Furyha's rock
                // takes its totem («Placez vos totems sur chaque piliers», DPLN, «Une âme en
                // colère»), and the step is the conch's, used once no totem is carried.
                if (binding.SpendsRequired && (binding.Gives?.Count ?? 0) == 0 && binding.SpawnsMonster == 0)
                {
                    await SpendAsync(session, binding);
                    return true;
                }

                bool gave = false;
                foreach (var (item, count) in binding.Gives)
                {
                    if (Equipment.HowMany(item) > 0) continue;
                    if (!await QuestFollowers.GiveAsync(stream, item, count)) continue;
                    gave = true;
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            InfoMessages.ItemGained, count.ToString(CultureInfo.InvariantCulture),
                            item.ToString(CultureInfo.InvariantCulture))));
                }
                // Or that only wakes what guards it: the broken crate of «Remordimientos de eternala»
                // brings out the araknespíritu whose drop is the spiritual cloth, and the objective is
                // still the handing over to Dlim. Not once the cloth is carried, nor a second one.
                if (binding.SpawnsMonster != 0 && !gave)
                {
                    var aimed = step.Objectives.First(o => o.Id == binding.ObjectiveId);
                    if (aimed.ItemId != 0 && Equipment.HowMany(aimed.ItemId) >= Math.Max(1, aimed.ItemCount)) return false;
                    if (MobSpawnManager.GetMobsForMap(SessionContext.State.MapId)
                        .Any(g => g.Members.Any(m => m.Monster != null && m.Monster.Id == binding.SpawnsMonster)))
                        return false;
                    await SpawnAsync(session, binding);
                    return true;
                }
                return gave;
            }

            // A conversation closes its objective before handing anything over: Ganymed's «Buscar en tu
            // bolsa los objetos y equipártelos» in the tutorial capture is idz 3169, then the six items,
            // and the client only validates «Equipa a tu personaje» for items that arrive with that
            // step already in hand.
            bool talkFirst = binding.Kind == QuestBindingKind.Talk || binding.Kind == QuestBindingKind.Reply;
            if (talkFirst) await TickAsync(session, binding.QuestId, binding.ObjectiveId);
            foreach (var (item, count) in binding.Gives)
            {
                if (await QuestFollowers.GiveAsync(stream, item, count))
                {
                    // «Has conseguido 1 '...'», after each one, as the capture has it (lqn 21).
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            InfoMessages.ItemGained, count.ToString(CultureInfo.InvariantCulture),
                            item.ToString(CultureInfo.InvariantCulture))));
                    continue;
                }

                Console.WriteLine($"[Quests] Objective {binding.ObjectiveId} had to give " +
                                  $"{count}x{item} and could not" + (talkFirst ? "." : "; it is not marked."));
                if (!talkFirst) return false;
            }

            // And what is required is spent, if the row says so. After handing out and before marking,
            // for the same reason: spending the lemonade and then having the rest fail would be the worst of all.
            if (binding.SpendsRequired) await SpendAsync(session, binding);

            if (!talkFirst) await TickAsync(session, binding.QuestId, binding.ObjectiveId);

            if (binding.SpawnsMonster != 0)
                await SpawnAsync(session, binding);

            if (binding.ShowsNpc != 0)
                await Handlers.NpcHandler.ComesInAsync(session, binding.ShowsNpc);

            if (binding.ThenStarts != 0 && log.Run(binding.ThenStarts) == null)
                await StartAsync(session, binding.ThenStarts);

            return true;
        }

        /// <summary>
        /// Brings out the monster an element makes appear, and makes the client see it.
        /// </summary>
        /// <remarks>
        /// The actor list has to be sent again. The group is drawn when the map is loaded, and here
        /// the player is already standing on it: without this the creature exists for the server and is not
        /// on the screen, which is the worse of the two halves.
        ///
        /// It is only sent to whoever triggered it. It is his quest, his element and his creature; nothing
        /// appears for the others in the middle of the map.
        /// </remarks>
        private static async Task SpawnAsync(GameSession session, QuestBinding binding)
        {
            var stream = session.Stream;
            long mapId = SessionContext.State.MapId;
            var grupo = MobSpawnManager.SpawnNamed(mapId, binding.SpawnsMonster, binding.SpawnsCount, binding.SpawnsWithAll.ToArray());

            if (grupo == null)
            {
                Console.WriteLine($"[Quests] Objective {binding.ObjectiveId} had to bring out " +
                                  $"monster {binding.SpawnsMonster} and could not.");
                return;
            }

            var ficha = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (ficha == null) return;

            await MapInformationService.Live.RefreshActorsAsync(session, ficha);

            Console.WriteLine($"[Quests] Monster {binding.SpawnsMonster} comes out " +
                              $"(x{binding.SpawnsCount}) on cell {grupo.CellId}.");

            // And, when they come out attacking, the fight starts right there: the yellow puddle
            // of «Cien por cien natural» is a fight, not a group left standing to be clicked.
            if (binding.SpawnsAttack)
                await Handlers.FightHandler.InitiateFightFromMobCollision(stream, grupo, mapId);
        }

        /// <summary>
        /// Whether this character should be shown a quest element on this map at all.
        /// </summary>
        /// <remarks>
        /// The stele appears when the quest is taken and goes when it is done, which is how the
        /// real game behaves and the reason this is asked per player rather than baked into the
        /// map. Answered from the step in hand, so it also goes as soon as that objective is
        /// ticked off rather than lingering until the whole quest ends.
        /// </remarks>
        public static bool ShouldSee(QuestBinding binding)
        {
            var log = Log;
            if (log == null || _book == null) return false;

            // An element that HANDS OUT the quest goes the other way round from all the rest: it is shown
            // while one does not have it, and disappears as soon as it is taken. The tavern notice
            // cannot ask for the quest to be in progress to let itself be pressed, because it is the one that
            // puts it in progress.
            if (binding.Starts != 0)
            {
                var yaVa = log.Run(binding.Starts);
                return yaVa == null;
            }

            var run = log.Run(binding.QuestId);
            if (run == null || run.Finished) return false;
            if (run.Done.Contains(binding.ObjectiveId)) return false;

            var step = _book.Step(run.StepId);
            if (step == null) return false;

            foreach (var objective in step.Objectives)
            {
                if (objective.Id == binding.ObjectiveId) return true;
            }

            return false;
        }

        /// <summary>
        /// The character has arrived on a map: whatever that finishes, and the marks for here.
        /// </summary>
        /// <remarks>
        /// Two kinds close by walking, and neither closed before:
        ///
        ///   4  "Découvrir la carte : X"   765 of the 874 carry the map on the objective itself.
        ///                                 The other 109 name it only as text and stay open.
        ///   5  "Découvrir la zone X"      five in the game, all naming a real subarea.
        ///
        /// The green marks go out from here too. They used to be sent once, on entering the world,
        /// so walking to the next map left every NPC on it unmarked until the next relog — which
        /// looked exactly like "this map has no quests".
        /// </remarks>
        public static async Task OnMapEnteredAsync(GameSession session, long mapId, int subAreaId)
        {
            using var context = SessionContext.Push(session);
            await OldCharacterReachesTheDofusariumAsync(session, mapId);
            var log = Log;

            // A quest the map itself hands out: «Des pwinces pas très charmants» starts on going
            // through the door of the wabbit guardian's ledge, with no giver («Position de lancement :
            // Aucune - Il suffit de franchir la porte», Dofus pour les noobs). Its own condition says
            // who: whoever has finished the quest before.
            if (log != null && mapId != 0)
            {
                foreach (var starter in Bindings.StartedOn(mapId))
                {
                    if (log.Run(starter.Starts) != null) continue;
                    if (!await StartSharedAsync(session, starter.Starts, () => Log?.Run(starter.Starts) == null)) continue;
                    Console.WriteLine($"[Quests] Quest {starter.Starts} starts on arriving on {mapId}.");
                }
            }
            if (log != null && _book != null && mapId != 0)
            {
                foreach (var run in new List<QuestRun>(log.Doing()))
                {
                    var step = _book.Step(run.StepId);
                    if (step == null) continue;

                    foreach (var objective in step.Objectives)
                    {
                        bool here = objective.DiscoverMapId == mapId
                                    || (subAreaId != 0 && objective.DiscoverAreaId == subAreaId);

                        // Only once it is the character's to do: Erty's cellar, «Quien siembra vientos
                        // recoge Artempestad», is watered at the start and must be gone down to again
                        // for the rose much later -- it used to tick on the first visit.
                        if (here && !run.Done.Contains(objective.Id) && Revealed(step, objective, run))
                        {
                            await TickAsync(session, run.QuestId, objective.Id);
                            await Heroes.ShareQuestActionAsync(async hero =>
                            {
                                if (SharedObjective(run.QuestId, step.Id, objective.Id, out _))
                                    await TickAsync(hero, run.QuestId, objective.Id);
                            },
                            quests: new[] { run.QuestId });
                        }
                    }

                    // And the free-text ones that are an arrival: "Pénétrer dans l'antre du
                    // Milimilou", "Entrer dans la taverne d'Astrub". These are the safest kind of
                    // binding there is, because the map is not guessed — the objective carries it.
                    foreach (var objective in step.Objectives)
                    {
                        var binding = Bindings.Of(objective.Id);
                        if (binding == null || binding.Kind != QuestBindingKind.Enter) continue;
                        if (binding.MapId != mapId) continue;
                        // Once it is the character's to do, as a map to discover: the tavern of «El
                        // receptáculo de los dofus» (16271), reached by the Pandala zaap before the
                        // bridge is crossed and the palace seen, ticked ahead of both.
                        if (run.Done.Contains(objective.Id) || !Revealed(step, objective, run)) continue;

                        await CloseSharedAsync(session, binding);
                    }

                    // And a «beat» row with no element to press: its monsters come out when the
                    // character arrives on its map, once it is theirs to do -- the old gorkoko of «Un
                    // parfum de vacances», who only shows himself once three of his kind are beaten.
                    foreach (var objective in step.Objectives)
                    {
                        var binding = Bindings.Of(objective.Id);
                        if (binding == null || binding.Kind != QuestBindingKind.Beat || binding.SpawnsMonster == 0) continue;
                        if (binding.Elements.Count > 0 || binding.MapId != mapId) continue;
                        if (MobSpawnManager.GetMobsForMap(mapId).Any(g => g.Members.Any(m => m.Monster != null && m.Monster.Id == binding.SpawnsMonster)))
                            continue;
                        await BringOutAsync(session, binding);
                    }
                }
            }

            // The marks for this map went out BEFORE the actor list, which is where the captures
            // put them -- see the caller in GameNodeProxy. What arriving finishes or starts sends
            // them again itself (TickAsync, StartAsync), as the captures do after every ieb.
        }

        /// <summary>
        /// The character has finished a walk on a map: an objective bound as «walk» there is done.
        /// The guided tutorial's 19534 closes this way, server-side, after the client's jqi.
        /// </summary>
        public static async Task OnWalkedAsync(GameSession session, long mapId, int cellId = -1)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null || mapId == 0) return;

            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Walk || binding.MapId != mapId) continue;
                    if (!EndsOn(binding, cellId)) continue;
                    await CloseSharedAsync(session, binding, () => EndsOn(binding, SessionContext.State.CellId));
                }
            }
        }

        /// <summary>Whether a walk that ended on this cell is the one a «walk» binding asks for.</summary>
        internal static bool EndsOn(QuestBinding binding, int cellId)
            => binding.Cells.Count == 0 || binding.Cells.Contains(cellId);

        /// <summary>
        /// A document has been read: an objective bound to reading every one of its elements closes
        /// once the last is read (the Dofusarium's statues, the temple's book).
        /// </summary>
        public static async Task OnReadAsync(GameSession session, long mapId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null) return;
            var used = SessionContext.State.ElementsUsed;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Read || binding.Elements.Count == 0) continue;
                    if (run.Done.Contains(objective.Id)) continue;
                    bool all = true;
                    foreach (var (_, element) in binding.Elements)
                    {
                        if (!used.Contains(element)) { all = false; break; }
                    }
                    // Read beside him by the heroes who followed him from one to the next: they have
                    // no client to open the statues with, and stayed behind at each.
                    if (all) await CloseSharedAsync(session, binding);
                }
            }
        }

        /// <summary>
        /// A book of the bag has been read: an objective bound to reading it («read» with an «item»)
        /// closes. The blank book of «Las primeras impresiones son engañosas», leafed through to its
        /// empty last page, sends the character back to complain to Primir.
        /// </summary>
        public static async Task OnItemReadAsync(GameSession session, int itemId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null || itemId == 0) return;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Read || binding.ItemId != itemId) continue;
                    if (run.Done.Contains(objective.Id) || !Revealed(step, objective, run)) continue;
                    if (!await CloseAsync(session, binding)) continue;
                    // And by the heroes beside him who carry the book too, each his own copy: as the
                    // recipe of Incarnam's cake (ReadingItemHandler).
                    int stepId = run.StepId;
                    await Heroes.ShareQuestActionAsync(async hero =>
                    {
                        if (Equipment.HowMany(itemId) > 0 && SharedObjective(binding.QuestId, stepId, binding.ObjectiveId, out _))
                            await CloseAsync(hero, binding);
                    },
                    quests: new[] { binding.QuestId },
                    explain: hero => Equipment.HowMany(itemId) > 0 ? null
                        : new Heroes.LagCause(Heroes.QuestLagReason.MissingItems, itemId, 1));
                }
            }
        }

        /// <summary>
        /// A dungeon guardian has taken the key at his door: an objective that asks for it to be
        /// handed to him is done, with nothing more taken -- the door has spent the key already.
        /// </summary>
        /// <remarks>
        /// The keyring does not count: no key changed hands. Tassa wants the laboratory's key the
        /// first time, and the keyring does not stand in for it.
        /// </remarks>
        public static async Task OnKeyHandedOverAsync(GameSession session, int npcId, int itemId)
        {
            using var context = SessionContext.Push(session);
            if (KeyObjective(npcId, itemId) is { } awaited) await TickAsync(session, awaited.QuestId, awaited.ObjectiveId);
        }

        /// <summary>
        /// Whether a quest of whoever is in session waits for that key to be handed to that guardian:
        /// a hero who has it goes in on a key rather than by his keyring (DungeonHeroes).
        /// </summary>
        internal static bool AwaitsKey(int npcId, int itemId) => KeyObjective(npcId, itemId) != null;

        private static (int QuestId, int ObjectiveId)? KeyObjective(int npcId, int itemId)
        {
            var log = Log;
            if (log == null || _book == null || npcId == 0 || itemId == 0) return null;
            foreach (var run in log.Doing())
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    if (objective.NpcId != npcId || objective.ItemId != itemId || !objective.ConsumesItems) continue;
                    if (run.Done.Contains(objective.Id) || !Revealed(step, objective, run)) continue;
                    return (run.QuestId, objective.Id);
                }
            }
            return null;
        }

        /// <summary>
        /// A reply that does a conversation objective (DialogueChoice.Completes) has been chosen:
        /// it is done if it is in hand and has come up, and what it asks to be handed over is.
        /// </summary>
        public static async Task<bool> CompleteByReplyAsync(GameSession session, int questId, int objectiveId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null || objectiveId == 0) return false;

            // A reply everyone is offered names no quest: the one in hand that has the objective.
            if (questId == 0)
            {
                foreach (var doing in log.Doing())
                {
                    if (_book.Step(doing.StepId)?.Objectives.Any(o => o.Id == objectiveId) == true)
                    {
                        questId = doing.QuestId;
                        break;
                    }
                }
                if (questId == 0) return false;
            }

            var run = log.Run(questId);
            if (run == null || run.Finished || run.Done.Contains(objectiveId)) return false;
            var step = _book.Step(run.StepId);
            if (step == null) return false;
            foreach (var objective in step.Objectives)
            {
                if (objective.Id != objectiveId) continue;
                if (!Revealed(step, objective, run)) return false;
                if (objective.ItemId != 0 && !await HandOverAsync(session, objective)) return false;
                if (objective.SoulMonster != 0 && !await HandOverSoulsAsync(session, objective)) return false;
                if (!await UsedWithWhatItTakesAsync(session, objectiveId)) return false;
                await TickAsync(session, questId, objectiveId);
                // Whoever the use brings out: the spirit the holy water calls up.
                var used = Bindings.Of(objectiveId);
                if (used != null && used.Kind == QuestBindingKind.Use && used.ShowsNpc != 0)
                    await Handlers.NpcHandler.ComesInAsync(session, used.ShowsNpc);
                return true;
            }
            return false;
        }

        /// <summary>
        /// What a «use» binding asks to be carried when its item is used, checked and -- when it
        /// says so -- spent (<see cref="QuestBindingKind.Use"/>). True when the objective has no
        /// such binding.
        /// </summary>
        private static async Task<bool> UsedWithWhatItTakesAsync(GameSession session, int objectiveId)
        {
            var stream = session.Stream;
            var binding = Bindings.Of(objectiveId);
            if (binding == null || binding.Kind != QuestBindingKind.Use) return true;
            // Where the objective says, when it names a place the item carries no condition for:
            // «Activa la pikosonda cerca del templo de Pandawa» (7979) at [4,-2], not anywhere.
            if (binding.MapId != 0 && SessionContext.State.MapId != binding.MapId)
            {
                Console.WriteLine($"[Quests] Objective {objectiveId} is done on map {binding.MapId}, " +
                                  $"not on {SessionContext.State.MapId}.");
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                return false;
            }
            foreach (var (item, count) in binding.Requires)
            {
                if (Equipment.HowMany(item) >= count) continue;
                Console.WriteLine($"[Quests] Objective {objectiveId} asks for {count}x{item} and it is not being carried.");
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildInfoMessage(
                        InfoMessages.Warning, InfoMessages.MissingItem)));
                return false;
            }
            if (binding.SpendsRequired) await SpendAsync(session, binding);
            return true;
        }

        /// <summary>
        /// Takes what a binding spends, each with the «Has perdido» the client words (lqn 22),
        /// as when a conversation takes an item: the Bonta honey left in the bear-man's barrels.
        /// </summary>
        private static async Task SpendAsync(GameSession session, QuestBinding binding)
        {
            var stream = session.Stream;
            foreach (var (item, count) in binding.Requires)
            {
                if (!await QuestFollowers.TakeAsync(stream, item, count)) continue;
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                        InfoMessages.ItemLost, count.ToString(CultureInfo.InvariantCulture),
                        item.ToString(CultureInfo.InvariantCulture))));
            }
        }

        /// <summary>
        /// A reply has been chosen in a conversation: an objective bound to it closes.
        /// </summary>
        /// <returns>The objectives it closed, so a reply that also «completes» one of them still hands its gift.</returns>
        public static async Task<List<int>> OnReplyChosenAsync(GameSession session, long reply, Func<bool>? canShare = null)
        {
            using var context = SessionContext.Push(session);
            var closed = new List<int>();
            var log = Log;
            if (log == null || _book == null || reply <= 0) return closed;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Reply) continue;
                    if (!binding.Replies.Contains((int)reply)) continue;
                    bool done = canShare == null ? await CloseAsync(session, binding)
                                                 : await CloseSharedAsync(session, binding, canShare);
                    if (done) closed.Add(objective.Id);
                }
            }
            return closed;
        }

        /// <summary>
        /// Something has been gathered, or has dropped in a fight, on a map: an objective bound to
        /// obtaining it there closes (the tutorial's five resources of the jobs room, the
        /// P'Koutoulou of 1765).
        /// </summary>
        public static async Task OnGatheredAsync(GameSession session, int itemId, long mapId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null) return;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Gather || binding.ItemId != itemId) continue;
                    if (binding.MapId != 0 && binding.MapId != mapId) continue;
                    await CloseAsync(session, binding);
                }
            }
        }

        /// <summary>
        /// A workshop has been closed after making these items: «Fabricar #2 #1 y cerrar la
        /// interfaz» (type 17) is done for each the bench made enough of.
        /// </summary>
        public static async Task OnCraftedAsync(GameSession session, IReadOnlyDictionary<int, int> made)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null) return;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    if (objective.TypeId != QuestCatalogue.CraftItem) continue;
                    // After what comes before it: the cake made before the recipe is read does not
                    // count (Dofus pour les noobs warns of exactly that).
                    if (!Revealed(step, objective, run)) continue;
                    if (!made.TryGetValue(objective.ItemId, out int count) || count < objective.ItemCount) continue;
                    await TickAsync(session, run.QuestId, objective.Id);
                }
            }
        }

        /// <summary>
        /// Closes the objectives in hand bound to others being done (QuestBindingKind.Objectives):
        /// the tutorial's 19253, once 1629's five resources are gathered.
        /// </summary>
        private static async Task AfterObjectivesAsync(GameSession session)
        {
            var log = Log;
            if (log == null || _book == null) return;
            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Objectives || binding.AfterObjectives.Count == 0) continue;
                    if (run.Done.Contains(objective.Id)) continue;
                    bool all = true;
                    foreach (int needed in binding.AfterObjectives)
                    {
                        if (!log.ObjectiveDone(needed)) { all = false; break; }
                    }
                    if (all) await CloseAsync(session, binding);
                }
            }
        }

        /// <summary>
        /// «Abandonar el tutorial», confirmed (idt): every quest of the tutorial validated in the
        /// journal, the «Set del intrépido» given, and the character put where the tutorial ends
        /// (content/quests/tutorial.json).
        /// </summary>
        /// <remarks>
        /// The client's confirmation says it: «Conseguirás inmediatamente el set del intrépido»; and
        /// Dofus pour les noobs, that the tutorial's quests are then all validated, done or not. No
        /// capture shows where the real server puts the character; the tutorial's own end is used.
        /// A character with nothing of the tutorial left to do is not touched.
        /// </remarks>
        /// <summary>
        /// A quest ended there and then with none of its rewards, started first if it was not in hand:
        /// «La quête se termine dès que vous lancez le combat. Pour ce choix vous obtenez aucune
        /// récompense» (Dofus pour les noobs, «Les Gobelins de la Manufacture»).
        /// </summary>
        public static async Task<bool> ForfeitAsync(GameSession session, int questId)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            if (log == null || questId == 0 || log.Finished(questId)) return false;
            var run = log.Run(questId) ?? log.Start(questId);
            if (run == null) return false;
            for (int guard = 0; guard < 40 && log.Active(questId); guard++) log.FinishStep(questId);
            Save(questId, run);
            await TellAsync(session, QuestFinishedMessage, questId);
            await session.SendAsync(new Game.QuestValidatedEvent(questId));
            FollowedQuests.Unfollow(SessionContext.State.CharacterId, questId);
            await SendJournalAsync(session);
            await Achievements.AfterQuestAsync(session, questId);
            Console.WriteLine($"[Quests] Quest {questId} ends with nothing paid.");
            return true;
        }

        public static async Task LeaveTutorialAsync(GameSession session)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            var tutorial = TutorialContent.Load();
            if (log == null || tutorial == null) return;
            if (tutorial.Quests.TrueForAll(log.Finished)) return;
            if (!InTheTutorial(log, tutorial))
            {
                Console.WriteLine($"[Quests] {SessionContext.State.CharacterName} is not in the tutorial: " +
                                  "its «Abandonar el tutorial» is ignored.");
                return;
            }

            foreach (int questId in tutorial.Quests)
            {
                if (log.Finished(questId)) continue;
                var run = log.Run(questId) ?? log.Start(questId);
                if (run == null) continue;
                for (int guard = 0; guard < 40 && log.Active(questId); guard++) log.FinishStep(questId);
                Save(questId, run);
                await TellAsync(session, QuestFinishedMessage, questId);
                await session.SendAsync(new Game.QuestValidatedEvent(questId));
                FollowedQuests.Unfollow(SessionContext.State.CharacterId, questId);
            }
            await SendJournalAsync(session);

            foreach (int item in tutorial.Items)
            {
                if (Equipment.HowMany(item) > 0) continue;
                if (await Equipment.GiveAsync(session, item, 1) && !Equipment.IsInvisible(item))
                    await session.SendAsync(new Game.TextInformationEvent(Game.TextInformationType.TextInformationMessage,
                        InfoMessages.ItemGained, ["1", item.ToString(CultureInfo.InvariantCulture)]));
            }

            foreach (int questId in tutorial.Quests)
                await Achievements.AfterQuestAsync(session, questId);

            // The mysterious voice's line is still on screen: the client takes it down only on a kld,
            // and in the Dofusarium it would hang over the new character for good.
            if (SessionContext.State.OpenDialogueNpcId != 0)
                await Handlers.NpcHandler.CloseAsync(session);

            if (tutorial.MapId != 0)
                await Handlers.TeleportHandler.ToMapAsync(session, tutorial.MapId, tutorial.Cell);
            Console.WriteLine($"[Quests] {SessionContext.State.CharacterName} leaves the tutorial.");
        }

        /// <summary>
        /// Whether the character playing is in the middle of the tutorial: one of its quests in hand,
        /// or none started yet and the character on the maps where a new one wakes up.
        /// </summary>
        /// <remarks>
        /// The client sends its idt again on the next character's entry when the one before was
        /// switched out mid-tutorial (main menu, «Cambiar de personaje»). Honoured as it came, a
        /// level-200 character in Cania had the tutorial validated, the set given and was put in
        /// the Dofusarium while its client confirmed the map it stood on, which closed the
        /// connection.
        /// </remarks>
        private static bool InTheTutorial(QuestLog log, TutorialContent tutorial)
        {
            if (tutorial.Quests.Exists(log.Active)) return true;
            if (tutorial.Quests.Exists(log.Finished)) return false;
            long mapId = SessionContext.State.MapId;
            return mapId == Handlers.CharacterCreationHandler.TutorialMap
                   || (mapId != 0 && DatabaseManager.SubAreaOfMap(mapId)
                       == DatabaseManager.SubAreaOfMap(Handlers.CharacterCreationHandler.TutorialMap));
        }

        /// <summary>
        /// A character who did the tutorial before 3.6 validates the quests 3.6 added to it on
        /// reaching the Dofusarium (content/quests/tutorial.json, «oldCharacters»).
        /// </summary>
        private static async Task OldCharacterReachesTheDofusariumAsync(GameSession session, long mapId)
        {
            var stream = session.Stream;
            var log = Log;
            if (log == null || mapId == 0) return;
            var tutorial = TutorialContent.Load();
            if (tutorial == null || tutorial.OldCharactersMap != mapId || tutorial.OldCharactersQuests.Count == 0) return;
            if (tutorial.OldCharactersQuests.TrueForAll(log.Finished)) return;
            var verdict = QuestCriterion.Judge(tutorial.OldCharactersWhen, log);
            if (verdict.Broke || !verdict.Met) return;

            foreach (int questId in tutorial.OldCharactersQuests)
            {
                if (log.Finished(questId)) continue;
                var run = log.Run(questId) ?? log.Start(questId);
                if (run == null) continue;
                for (int guard = 0; guard < 40 && log.Active(questId); guard++) log.FinishStep(questId);
                Save(questId, run);
                await TellAsync(session, QuestFinishedMessage, questId);
                await session.SendAsync(new Game.QuestValidatedEvent(questId));
                await Achievements.AfterQuestAsync(session, questId);
            }
            Console.WriteLine($"[Quests] {SessionContext.State.CharacterName} validates the tutorial's new quests in the Dofusarium.");
        }

        /// <summary>The guided tutorial as «Abandonar el tutorial» ends it (content/quests/tutorial.json).</summary>
        public sealed class TutorialContent
        {
            public List<int> Quests { get; init; } = new();
            public List<int> Items { get; init; } = new();
            public long MapId { get; init; }
            public int Cell { get; init; }

            /// <summary>Where a character from before 3.6 validates the tutorial's new quests, when, and which.</summary>
            public long OldCharactersMap { get; init; }
            public string OldCharactersWhen { get; init; } = "";
            public List<int> OldCharactersQuests { get; init; } = new();

            public static TutorialContent? Load()
            {
                string path = Jondo.Unity.Launcher.Paths.ContentFile("quests/tutorial.json");
                if (!System.IO.File.Exists(path)) return null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                    var root = doc.RootElement;
                    var quests = new List<int>();
                    foreach (var q in root.GetProperty("quests").EnumerateArray()) quests.Add(q.GetInt32());
                    var items = new List<int>();
                    foreach (var i in root.GetProperty("items").EnumerateArray()) items.Add(i.GetInt32());
                    var old = new List<int>();
                    long oldMap = 0;
                    string when = "";
                    if (root.TryGetProperty("oldCharacters", out var o))
                    {
                        oldMap = o.TryGetProperty("map", out var om) ? om.GetInt64() : 0;
                        when = o.TryGetProperty("when", out var ow) ? ow.GetString() ?? "" : "";
                        if (o.TryGetProperty("quests", out var oq))
                            foreach (var q in oq.EnumerateArray()) old.Add(q.GetInt32());
                    }
                    return new TutorialContent
                    {
                        Quests = quests,
                        Items = items,
                        MapId = root.TryGetProperty("map", out var m) ? m.GetInt64() : 0,
                        Cell = root.TryGetProperty("cell", out var c) ? c.GetInt32() : -1,
                        OldCharactersMap = oldMap,
                        OldCharactersWhen = when,
                        OldCharactersQuests = old,
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] quests/tutorial.json is unreadable: {ex.Message}");
                    return null;
                }
            }
        }

        internal static async Task<bool> StartSharedAsync(GameSession session, int questId, Func<bool>? canShare = null)
        {
            using var context = SessionContext.Push(session);
            if (!await StartAsync(session, questId)) return false;
            await Heroes.ShareQuestActionAsync(async hero =>
            {
                if (canShare?.Invoke() != false)
                    await StartAsync(hero, questId);
            },
            quests: new[] { questId },
            explain: hero => Heroes.CouldNotStart(hero, questId));
            return true;
        }

        /// <summary>
        /// Starts a quest and tells the client, if the character is allowed it.
        /// </summary>
        public static async Task<bool> StartAsync(GameSession session, int questId)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            if (log == null) return false;

            if (OfferedAlreadyToday(questId)) return false;

            if (!log.CanStart(questId, out var verdict))
            {
                if (verdict.Broke)
                {
                    Console.WriteLine($"[Quests] The condition of quest {questId} is not understood: " +
                                      $"{_book?.Of(questId)?.Criterion}");
                }

                return false;
            }

            var run = log.Start(questId);
            if (run == null) return false;

            // A quest taken again gathers afresh: the fresh carrots of a daily are there for the
            // picking the next day too, and were remembered as taken for the whole session.
            SessionContext.State.QuestElementsTaken.RemoveWhere(taken => Bindings.Of(taken.Objective)?.QuestId == questId);

            // Said once per start rather than swallowed: a quest handed out on a condition that was
            // only half read is a different thing from one whose condition passed, and the person
            // running the server is the only one who can judge whether it matters.
            if (!verdict.FullyJudged)
            {
                Console.WriteLine($"[Quests] Quest {questId} is given without checking {string.Join(", ", verdict.Skipped)}: " +
                                  "this emulator does not model that.");
            }

            Save(questId, run);

            await TellAsync(session, QuestStartedMessage, questId);
            await session.SendAsync(new Game.QuestStartedEvent(questId));

            // A hero's run, socketless, settles the objectives that are not his all the same
            // (SendStepAsync writes nothing without a stream): «Consejo de clase» started for a
            // hero by his master's talk asks his own class's master only.
            await SendStepAsync(session, questId);
            if (!session.HasTransport) return true;

            // Objectives that wait on others already done: «Bienvenido a Frigost» (649) is taken once
            // the mayor's four quests are finished, and their four objectives close as it starts, since
            // no later tick will ever come to close them.
            await AfterObjectivesAsync(session);

            // And the green mark of the one who just gave it is switched off.
            await SendMarksAsync(session, session.MapId);
            await RedrawQuestElementsAsync(session, questId);

            // A treasure quest of Dhain's: the box beside him offers its hunt.
            await Handlers.TreasureHuntHandler.OnQuestStartedAsync(stream, questId);

            Console.WriteLine($"[Quests] Quest {questId} starts, at step {run.StepId}.");
            return true;
        }

        /// <summary>Whether an objective is this character's (QuestCriteria.OfObjective).</summary>
        private static bool AppliesTo(QuestLog log, int objectiveId)
        {
            string criterion = QuestCriteria.OfObjective(objectiveId);
            return criterion.Length == 0 || QuestCriterion.Judge(criterion, log).Met;
        }

        /// <summary>
        /// The objectives of a step the client is shown: the done ones, and the outstanding ones the
        /// character has reached -- one whose binding waits for others (afterObjectives) is held back
        /// until they are done.
        /// </summary>
        /// <remarks>
        /// The real server reveals a step's objectives as they become relevant (a growing prefix).
        /// Which ones it holds back is not in the client's data, but a
        /// binding that waits for others says it for the quests written here: «En la encrucijada de los
        /// mundos» listed the audience with the Daimia and the shield to craft from the start, while
        /// the game only lets them be done after the three recommendations. An «objectives» binding
        /// (closed by others being done) is shown as before. If holding back would leave the step with
        /// nothing outstanding on screen, the whole list goes out: a step that looks finished and is
        /// not is worse than one shown early.
        /// </remarks>
        internal static List<int> Shown(IReadOnlyList<int> objectives, IReadOnlyCollection<int> done,
                                        Func<int, QuestBinding?> bindingOf, Func<int, bool> doneElsewhere)
        {
            bool Reached(int objective)
            {
                var binding = bindingOf(objective);
                return binding == null || binding.Kind == QuestBindingKind.Objectives
                    || binding.AfterObjectives.All(before => done.Contains(before) || doneElsewhere(before));
            }

            var shown = objectives.Where(o => done.Contains(o) || Reached(o)).ToList();
            return shown.Any(o => !done.Contains(o)) ? shown : objectives.ToList();
        }

        /// <summary>The same, for a quest of this character's journal.</summary>
        private static List<int> Shown(QuestLog log, QuestRun run, IReadOnlyList<int> objectives)
            => Shown(objectives, run.Done, Bindings.Of, before => log.ObjectiveDone(before));

        /// <summary>
        /// Tells the client where a quest has got to (idu), which is what the ieo asks for.
        /// </summary>
        public static async Task SendStepAsync(GameSession session, int questId)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            var run = log?.Run(questId);
            if (log == null || run == null || run.Finished || _book == null) return;

            var step = _book.Step(run.StepId);
            if (step == null) return;

            // The ones that are not this character's are out of his step, done without a word:
            // «Consejo de clase» asks each class for its own master.
            var objectives = new List<int>(step.Objectives.Count);
            bool settled = false;
            foreach (var objective in step.Objectives)
            {
                if (!AppliesTo(log, objective.Id))
                {
                    settled |= run.Done.Add(objective.Id);
                    continue;
                }
                objectives.Add(objective.Id);
            }
            if (settled) Save(questId, run);

            await session.SendAsync(new Game.QuestStepInformationEvent(
                QuestMessages.Active(questId, run.StepId, Shown(log, run, objectives), run.Done)));
        }

        /// <summary>
        /// The map's elements again, when the quest that just started or moved has one on the map
        /// the character stands on.
        /// </summary>
        /// <remarks>
        /// A quest's elements go out with the map, on arrival (MapInteractiveMessages),
        /// and only to whoever should see them then. Taking «A la leche caliente, batir de repente»
        /// from Apísafe is done beside the golden milk bucket it asks for, and the bucket stayed
        /// unclickable until the character left the refuge and came back. The same for an element
        /// a later objective of the same map reveals.
        /// </remarks>
        private static async Task RedrawQuestElementsAsync(GameSession session, int questId)
        {
            var stream = session.Stream;
            long mapId = SessionContext.State.MapId;
            if (mapId == 0 || SessionContext.State.IsInFight) return;
            bool hasOne = false;
            foreach (var binding in Bindings.OnMap(mapId))
            {
                if (binding.QuestId != questId) continue;
                foreach (var (where, _) in binding.Elements) hasOne |= where == mapId;
                foreach (var (where, _) in binding.Props) hasOne |= where == mapId;
            }
            if (!hasOne) return;

            var ficha = DatabaseManager.GetCharacterById(GameState.CharacterId);
            if (ficha == null) return;
            await MapInformationService.Live.RefreshActorsAsync(session, ficha);
            Console.WriteLine($"[Quests] Elements of map {mapId} sent again for quest {questId}.");
        }

        /// <summary>
        /// Puts the green mark over the NPCs of a map that have something to offer.
        /// </summary>
        /// <remarks>
        /// Sent on arriving at a map and again whenever a quest starts, because the mark going out
        /// is the same message with an empty list — that is how the captures do it, and 235 of the
        /// 380 of them are exactly that.
        ///
        /// Only NPCs with offered or in-progress quests are named. An empty replacement clears
        /// the index; NPC appearances carry their own markers separately.
        ///
        /// The question asked per NPC is the real one — <see cref="QuestLog.CanStart"/> — so a
        /// quest whose level or prerequisite is not met does not light the mark up.
        ///
        /// What lights it up is <see cref="QuestsOfferedBy"/> and not the catalogue, which is the
        /// difference between a mark and a promise. The catalogue names a giver for 1,958
        /// quest-and-NPC pairs and only 70 of them can be handed over by a conversation this server
        /// can hold; marking the other 1,888 would put a green mark over most of the world that
        /// never goes out however long the player talks, because the line that hands the quest over
        /// is not one anybody can reach.
        /// </remarks>
        public static Task SendMarksAsync(GameSession session, long mapId)
            => QuestStateService.Live.SendMarksAsync(session, mapId);

        internal static Game.NpcsMapQuestStatusUpdateEvent? MarksSnapshot(GameSession session, long mapId)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            if (log == null || _book == null || mapId == 0) return null;

            var here = Npcs.OnMap(mapId);
            // Only the ones that have something. The iom is an index, not a census: in the 145 real
            // frames there is not one named actor with an empty list. Whoever stops having anything
            // disappears from the index, and that is what removes his mark.
            // With the quests under way that want something from each, as the captures carry them
            // (f3). The question mark over the head is the actor list's, drawn as the map loads.
            var marks = new List<(long Actor, IReadOnlyList<int> Offers, IReadOnlyList<int> InProgress)>(here.Count);
            foreach (var npc in here)
            {
                var offers = OfferedRightNowBy(npc.NpcId, mapId);
                var waiting = InProgressWith(npc.NpcId, mapId);
                if (offers.Count > 0 || waiting.Count > 0) marks.Add((npc.ContextualId, offers, waiting));
            }

            return QuestMessages.Marks(mapId, marks);
        }

        /// <summary>
        /// What this NPC can hand over to THIS character right now, conditions included.
        /// </summary>
        /// <remarks>
        /// Two callers and they must never disagree: the green mark travels in the NPC's own actor
        /// record inside the jss -- see <c>MapActorMessages.Npc</c>, which is what actually
        /// draws it -- and the iom carries the same list again as a sub-area index. Two copies of
        /// this filter would be two chances to drift.
        /// </remarks>
        public static List<int> OfferedRightNowBy(int npcId, long mapId)
        {
            var log = Log;
            var offers = new List<int>();
            if (log == null) return offers;

            HashSet<int>? reachable = ReachableNow(npcId, mapId, log);
            foreach (int questId in QuestsOfferedBy(npcId, mapId))
            {
                if (reachable != null && !reachable.Contains(questId)) continue;
                if (!OfferedAlreadyToday(questId) && log.CanStart(questId, out _)) offers.Add(questId);
            }

            return offers;
        }

        /// <summary>
        /// The quests the NPC's conversation can hand over right now, following it from the line
        /// it opens on for this character; null when it opens on the same line for everybody.
        /// </summary>
        /// <remarks>
        /// A tree whose opening depends on the quests marked the NPC for every quest any of its
        /// lines hands over: the tutorial's Ganymed showed 2511's mark from the welcome on, though
        /// only his line after the first fight gives it.
        /// </remarks>
        private static HashSet<int>? ReachableNow(int npcId, long mapId, QuestLog log)
        {
            var tree = NpcDialogues.For(npcId, mapId);
            if (tree == null || tree.Openings.Count == 0) return null;

            var start = tree.OpeningFor(SessionContext.State.Breed, log.Active, log.Finished,
                (quest, step) => log.Run(quest)?.StepId == step,
                (quest, objective) => log.ObjectiveDone(objective),
                quest => log.CanStart(quest, out _),
                item => Equipment.HowMany(item) > 0,
                criterion => PlayerCriteria.Met(criterion, SessionContext.State.MapId),
                SessionContext.State.CellId,
                element => SessionContext.State.ElementsUsed.Contains(element));
            var found = new HashSet<int>();
            var seen = new HashSet<long>();
            var queue = new Queue<Jondo.Unity.World.Content.DialogueLine>();
            if (start != null) queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var line = queue.Dequeue();
                if (!seen.Add(line.Message)) continue;
                foreach (var choice in line.Choices)
                {
                    if (choice.StartsQuest != 0) found.Add(choice.StartsQuest);
                    if (!choice.Ends && tree.Line(choice.Next) is { } next) queue.Enqueue(next);
                }
            }
            return found;
        }

        /// <summary>
        /// Whether this is an Almanax offering this character already made today. The offerings
        /// are repeatable — a saint's day comes back every year — so the quest log alone would let
        /// the same one be taken again the moment it was handed in.
        /// </summary>
        private static bool OfferedAlreadyToday(int questId)
            => Almanax.IsOffering(questId) && Almanax.OfferedToday(SessionContext.State.AchievementTallies, questId);

        /// <summary>Whether this character may take that quest now: not in hand, allowed, and not an offering already made today.</summary>
        public static bool MayTake(int questId)
        {
            var log = Log;
            return log != null && questId != 0 && log.Run(questId) is not { Finished: false }
                   && !OfferedAlreadyToday(questId) && log.CanStart(questId, out _);
        }

        /// <summary>
        /// Quests already in hand whose current step wants something from this NPC here.
        /// </summary>
        /// <remarks>
        /// The other half of the marker over an NPC's head: measured in the captures as f1 of the
        /// same block that carries the offered list in f3, and it is what turns the mark into the
        /// "come back and tell me" one rather than the "I have work for you" one.
        ///
        /// The question is the same one <see cref="OnTalkingToAsync"/> asks when the player
        /// actually walks up, so the mark cannot promise a conversation that then does nothing.
        /// </remarks>
        public static List<int> InProgressWith(int npcId, long mapId)
        {
            var log = Log;
            var doing = new List<int>();
            if (log == null || _book == null || npcId == 0) return doing;

            foreach (var run in log.Doing())
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;

                bool wants = false;
                foreach (var objective in step.Objectives)
                {
                    if (run.Done.Contains(objective.Id)) continue;

                    if (objective.NpcId == npcId
                        && (objective.MapId == 0 || objective.MapId == mapId || !StandsOn(npcId, objective.MapId))
                        && Revealed(step, objective, run))
                    {
                        wants = true;
                        break;
                    }

                    var binding = Bindings.Of(objective.Id);
                    if (binding != null && binding.Kind == QuestBindingKind.Talk
                        && binding.NpcId == npcId
                        && (binding.MapId == 0 || binding.MapId == mapId))
                    {
                        wants = true;
                        break;
                    }
                }

                if (wants) doing.Add(run.QuestId);
            }

            return doing;
        }

        /// <summary>
        /// What this NPC will really hand over here: what the mark is drawn from.
        /// </summary>
        /// <remarks>
        /// Two sources, and the tree is the one that decides. The catalogue names a giver, which is
        /// where the list starts, but a name is not a conversation and most of those quests cannot
        /// be reached — see <see cref="CanBeTakenFrom"/>. And the tree can offer a quest the
        /// catalogue names nobody for: 155 quests have no giver at all, and "Mort au rat !" is one
        /// of them even though the tavern keeper declares the reply "Dire que vous avez vu
        /// l'affiche placardée dehors". Where the catalogue has no opinion, the tree is the only
        /// thing that knows.
        /// </remarks>
        public static List<int> QuestsOfferedBy(int npcId, long mapId)
        {
            var offers = new List<int>();

            foreach (int questId in QuestsGivenBy(npcId, mapId))
            {
                if (CanBeTakenFrom(npcId, mapId, questId)) offers.Add(questId);
            }

            var written = NpcDialogues.For(npcId, mapId);
            if (written != null)
            {
                foreach (var line in written.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (choice.StartsQuest != 0 && !offers.Contains(choice.StartsQuest))
                        {
                            offers.Add(choice.StartsQuest);
                        }
                    }
                }
            }

            return offers;
        }

        /// <summary>
        /// Whether a conversation with this NPC can really hand that quest over.
        /// </summary>
        /// <remarks>
        /// The catalogue says who gives what. It does not say whether the giving can happen, and on
        /// this server it usually cannot: a quest is handed over on the line its first step names,
        /// and 1,092 of those 1,260 lines are not declared in any NPC template, so without a
        /// written tree there is no way to walk to them.
        ///
        /// Which of the two rules applies is decided by whether anything is written, and
        /// <see cref="Handlers.NpcHandler.ReplyAsync"/> is where that happens:
        ///
        ///   a tree is written   only a reply marked startsQuest hands anything over. The step's
        ///                       own dialogId is not consulted, which is deliberate — a written
        ///                       tree is meant to be exact — and it is also how a quest whose
        ///                       first step names no line can be given at all.
        ///   nothing is written  the old rule: any reply on the line the step names. The only line
        ///                       anybody can reach is then the template's opening, since every
        ///                       reply is dumped underneath it, so the quest is takeable exactly
        ///                       when that opening happens to be the line its step declares. That
        ///                       is 70 quests in the whole game.
        ///
        /// So a tree that carries the declared line without marking a reply on it loses the quest,
        /// silently. <c>AuthoredDialoguesTests</c> refuses that combination for the trees on disk.
        /// </remarks>
        public static bool CanBeTakenFrom(int npcId, long mapId, int questId)
        {
            var quest = _book?.Of(questId);
            if (quest == null) return false;

            var written = NpcDialogues.For(npcId, mapId);
            if (written != null)
            {
                foreach (var line in written.Lines)
                {
                    foreach (var choice in line.Choices)
                    {
                        if (choice.StartsQuest == questId) return true;
                    }
                }

                return false;
            }

            long declared = quest.Steps.Count > 0 ? quest.Steps[0].DialogId : 0;

            // The Almanax offerings declare no line at all; their giver hands over today's on his
            // opening conversation, and only today's. See OnReplyAsync.
            if (declared == 0 && npcId == Almanax.Giver && Almanax.IsOffering(questId))
            {
                return questId == Almanax.TodaysOffering();
            }

            var template = Npcs.TemplateOf(npcId);
            return declared != 0 && template != null && template.DialogMessageId == declared;
        }

        /// <summary>Which quests an NPC hands out, on that map or anywhere.</summary>
        /// <remarks>
        /// Built once at load, like the dialogue index and for the same reason: asking it the other
        /// way round would walk 1,976 quests every time somebody steps onto a map.
        /// </remarks>
        public static IReadOnlyList<int> QuestsGivenBy(int npcId, long mapId)
        {
            if (!GivenBy.TryGetValue(npcId, out var mine)) return Array.Empty<int>();

            var here = new List<int>();
            foreach (var (questId, where) in mine)
            {
                if (where == 0 || where == mapId) here.Add(questId);
            }

            return here;
        }

        /// <summary>NPC to the quests it hands out, with the map each one is offered on.</summary>
        private static readonly Dictionary<int, List<(int Quest, long Map)>> GivenBy
            = new Dictionary<int, List<(int, long)>>();

        /// <summary>
        /// Sends this character's whole journal, then the followed tracker only at entry.
        /// </summary>
        /// <remarks>
        /// Sent on entering the world, in place of what the replayed capture used to carry. That
        /// capture holds 261 idu frames in the first block and 4 more in the map block, and every
        /// one of them belongs to the account somebody recorded — so before this, anybody who
        /// logged in was shown a stranger's quest journal, and once there was an engine those
        /// quests would also have been ones the server did not think they had.
        ///
        /// A loaded but empty journal is still sent as a replacement.
        ///
        /// The followed quests' box (iel) goes with it only on entering the world («box»): later
        /// in the session the client adds what an iel brings to what its box already shows, and
        /// each journal sent again -- a quest forfeited, the tutorial left -- showed the followed
        /// quests once more. Once in the world the client keeps the box itself: it follows a quest
        /// as it starts, and the ied of a finished one takes it out.
        /// </remarks>
        public static Task SendJournalAsync(GameSession session, bool box = false)
            => QuestStateService.Live.SendJournalAsync(session, box);

        /// <summary>The journal of a hero just taken in hand, the box the one played before left made his.</summary>
        public static Task SendSwitchedJournalAsync(GameSession session, long before)
            => QuestStateService.Live.SendSwitchedJournalAsync(session, before);

        internal static Game.QuestsEvent? JournalSnapshot(GameSession session)
        {
            using var context = SessionContext.Push(session);
            var log = Log;
            var book = _book;
            if (log == null || book == null) return null;

            // One idr with the lot, which is how the captured block does it, rather than one idu
            // per quest. Sending it even when it is empty matters: the client fills the window from
            // this message, and an empty one is what says "this character has no journal".
            var doing = new List<Game.QuestActive>();
            foreach (var run in log.Doing())
            {
                var step = book.Step(run.StepId);
                if (step == null) continue;

                var objectives = step.Objectives.Select(objective => objective.Id).ToArray();
                doing.Add(QuestMessages.Active(run.QuestId, run.StepId, Shown(log, run, objectives), run.Done));
            }

            var finished = new List<int>();
            foreach (var pair in log.Runs)
            {
                if (pair.Value.Finished) finished.Add(pair.Key);
            }

            return QuestMessages.Journal(doing, finished);
        }

        /// <summary>
        /// Ticks an objective off and sends whatever that changed.
        /// </summary>
        /// <remarks>
        /// One method for both ways an objective can be finished — the client saying so, and the
        /// server working it out — so that the packets that follow cannot differ between them.
        /// </remarks>
        /// <summary>
        /// One more towards an objective that wants several of something its catalogue does not
        /// count (a beat binding's «times»): saved, and true once it is the last one.
        /// </summary>
        public static bool CountTowards(QuestRun run, int objectiveId, int needed)
        {
            run.Counted.TryGetValue(objectiveId, out int already);
            if (already + 1 >= needed)
            {
                run.Counted.Remove(objectiveId);
                return true;
            }
            run.Counted[objectiveId] = already + 1;
            Save(run.QuestId, run);
            return false;
        }

        public static async Task TickAsync(GameSession session, int questId, int objectiveId,
                                           int amount = 0)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            var log = Log;
            if (log == null) return;

            var move = log.Tick(questId, objectiveId, amount);
            if (move.Nothing) return;

            await AfterMoveAsync(session, questId, move);

            // What an «order» row says comes of the objective once done: Escualo El Rojo's
            // boot-licking potion, brewed from the ingredients handed over on a hello. And what a
            // fight wins: the burnt book saved from the forge's fire, the sniffing tofu's feather.
            var binding = Bindings.Of(objectiveId);
            if (!move.Nothing && binding?.Kind is QuestBindingKind.Order or QuestBindingKind.Beat)
            {
                // What it hands over is taken, when the row says so: the fat tofu's egg and the
                // fattening potion left with Vil Smis («Tomber sur un bec»).
                if (binding.SpendsRequired) await SpendAsync(session, binding);
                foreach (var (item, count) in binding.Gives)
                {
                    if (!await QuestFollowers.GiveAsync(stream, item, count)) continue;
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            InfoMessages.ItemGained, count.ToString(CultureInfo.InvariantCulture),
                            item.ToString(CultureInfo.InvariantCulture))));
                }
            }
        }

        private static async Task AfterMoveAsync(GameSession session, int questId, QuestMove move)
        {
            var stream = session.Stream;
            var run = Log?.Run(questId);
            if (run == null) return;

            Save(questId, run);

            if (move.StepFinished && !move.QuestFinished)
            {
                // idz names the step that WAS in hand, not the one now in hand, and it goes out
                // before the next step does. The tutorial shows that order 21 times, each after
                // the lqn 55 that names the quest.
                await TellAsync(session, QuestUpdatedMessage, questId);
                await session.SendAsync(new Game.QuestStepValidatedEvent(questId, move.FinishedStep));
            }
            else if (move.ObjectiveTicked && !move.StepFinished && move.TickedObjective != 0)
            {
                // An objective done with more of the step to go: lqn 55 and ieb, as the tutorial's
                // 2511 and 1629 go, objective by objective.
                await TellAsync(session, QuestUpdatedMessage, questId);
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Ieb,
                        QuestProtocol.BuildObjectiveValidated(questId, move.TickedObjective)));
            }

            // A step that leaves the quest under way pays before the next step goes out; the quest's
            // last one pays after «misión terminada» and ied, as «Matarratas»' capture closes:
            // lqn 56, ied 1633, then lqn 45 with the kamas and lqn 21 with the items.
            if (move.StepFinished && !move.QuestFinished) await PayAsync(session, move.FinishedStep);

            if (!move.QuestFinished)
            {
                await SendStepAsync(session, questId);   // settles a hero's foreign objectives too

                // And the marks again, as after every ieb and idz of the captures.
                if (session.HasTransport && (move.ObjectiveTicked || move.StepFinished))
                {
                    await SendMarksAsync(session, session.MapId);
                    await RedrawQuestElementsAsync(session, questId);
                }
            }
            else
            {
                Console.WriteLine($"[Quests] Quest {questId} finished.");

                // As every finished quest of the captures closes: «misión terminada» (lqn 56 with
                // the quest) and ied, which takes it out of the client's quests under way and out
                // of its followed box -- without it the box went on asking for an objective long
                // done. Then the marks, which the quest done may change.
                await TellAsync(session, QuestFinishedMessage, questId);
                await session.SendAsync(new Game.QuestValidatedEvent(questId));
                if (move.StepFinished) await PayAsync(session, move.FinishedStep);
                FollowedQuests.Unfollow(SessionContext.State.CharacterId, questId);
                if (session.HasTransport) await SendMarksAsync(session, session.MapId);

                // An Almanax offering is made once a day: the day is written down, so the same
                // quest is not handed out again until the saint comes round next year.
                if (Almanax.IsOffering(questId))
                {
                    Achievements.SetTally(Almanax.OfferingKind, questId, Almanax.DayKey(Almanax.Clock()));

                    // A page of the calendar for whoever is on «El año del dragón».
                    var dragon = Almanax.Dragon;
                    if (dragon.Quest != 0 && Log?.Run(dragon.Quest) is { Finished: false }
                        && !await Equipment.GiveAsync(stream, dragon.Page, 1))
                        Console.WriteLine($"[Almanax] The calendar page {dragon.Page} could not be given.");
                }

                // The 365 pages brought: the Dolmanax, as Ontoral hands it over.
                if (questId == Almanax.Dragon.Quest && !await Equipment.GiveAsync(stream, Almanax.Dolmanax, 1))
                    Console.WriteLine($"[Almanax] The Dolmanax {Almanax.Dolmanax} could not be given.");

                // An alignment quest raises the alignment level, an order's apprenticeship the rank.
                await Handlers.AlignmentHandler.AfterQuestAsync(stream, questId);

                // And whatever that finished quest has earned. 259 achievements hang from finishing one, and
                // the tutorial's 8518 «Primer tiempo» is literally (Qf=2511).
                await Achievements.AfterQuestAsync(session, questId);
            }

            // What a validated objective earns (Qo): the tutorial's «Primer tiempo» follows idz 3173.
            if (move.TickedObjective != 0)
            {
                await Achievements.AfterObjectiveAsync(session, move.TickedObjective);
                await AfterObjectivesAsync(session);
            }

            // And an NPC of the map who speaks on his own once that step is done (the tutorial's
            // voice, idz 3226 then ioc and ios with no iov between).
            if (move.StepFinished && stream != null)
                await Handlers.NpcHandler.PushAfterStepAsync(session, move.FinishedStep);
        }

        /// <summary>
        /// Hands over what a finished step promised.
        /// </summary>
        /// <remarks>
        /// Items with their quantity, emotes, and the experience and kamas the reward's ratios are
        /// ratios of. The base is the client's own formula (<see cref="RewardFormula"/>), with the
        /// step's optimal level and duration: the tutorial's quest 1629 pays 141 experience at
        /// level 2 in the capture, and that is what it gives here.
        ///
        /// Only the rewards whose level bracket holds the character are paid. 4,555 of the 6,707
        /// rewards carry one, and the Almanax offerings list ten, from level 9-29 to 190-200 — paid
        /// all together they would be every bracket's items at once.
        ///
        /// Nothing here can fail the step. A bag with no room, or an item id the database does not
        /// know, must not leave a quest half advanced — the step is already validated by the time
        /// this runs, on purpose.
        /// </remarks>
        private static async Task PayAsync(GameSession session, int stepId)
        {
            var stream = session.Stream;
            var step = _book?.Step(stepId);
            if (step == null || step.Rewards.Count == 0) return;

            int level = SessionContext.State.CharacterLevel;
            long experience = 0, kamas = 0;
            var items = new List<(int Item, int Count)>();

            foreach (var reward in step.Rewards)
            {
                if (!reward.For(level)) continue;

                // With the character's experience bonus, as the client reckons it (the capture's
                // 141 for 1629's step 2249 at level 2, 135 without it).
                experience += RewardFormula.Experience(level, step.OptimalLevel, reward.ExperienceRatio,
                                                       step.Duration, CombatExperience.CharacterBonusPercent);
                kamas += RewardFormula.Kamas(reward.KamasScale ? level : step.OptimalLevel,
                                             reward.KamasRatio, step.Duration);
                items.AddRange(reward.Items);
                foreach (int emote in reward.Emotes) await Emotes.LearnAsync(session, emote);
            }

            // The Almanax days that raise what quests pay, when they do so unconditionally.
            experience = Almanax.WithBonus(Almanax.BonusType.QuestExperience, experience);
            kamas = Almanax.WithBonus(Almanax.BonusType.QuestKamas, kamas);

            bool paid = await CharacterRewards.GiveKamasAsync(stream, kamas);
            // Under the player's limit: the client warns about it beside a quest's experience.
            bool levelled = await CharacterRewards.GiveExperienceAsync(stream, experience, limited: true);
            paid |= experience > 0;

            {
                foreach (var (item, count) in items)
                {
                    bool given = await Equipment.GiveAsync(stream, item, Math.Max(1, count));
                    if (!given)
                    {
                        Console.WriteLine($"[Quests] Item {item} of step {stepId} could not " +
                                          "be given.");
                        continue;
                    }

                    // «Has conseguido 5 'Cenizas eternas'», as for what a conversation hands over.
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(
                            InfoMessages.ItemGained, Math.Max(1, count).ToString(CultureInfo.InvariantCulture),
                            item.ToString(CultureInfo.InvariantCulture))));
                }
            }

            if (paid)
            {
                CharacterRewards.Save();
                Console.WriteLine($"[Quests] Step {stepId} pays {experience} experience and {kamas} kamas.");
            }

            if (levelled) await Achievements.AfterLevelAsync(session);
        }

        /// <summary>A finished quest taken out of the character's log, so that it is offered again.</summary>
        public static void Forget(int questId)
        {
            var log = Log;
            long characterId = SessionContext.State.CharacterId;
            if (log == null || characterId == 0 || !log.Forget(questId)) return;
            DatabaseManager.DeleteQuestProgress(characterId, questId);
            // And the marks its conversations and fights left, numbered after it (-894001 is the
            // first of «Las primeras impresiones son engañosas»): the heaps of filings would never
            // fight again on the quest played anew.
            int first = -(questId * QuestMarkSpan + QuestMarkSpan - 1), last = -(questId * QuestMarkSpan);
            int cleared = SessionContext.State.ElementsUsed.RemoveWhere(e => e >= first && e <= last);
            if (cleared > 0) DatabaseManager.ForgetElements(characterId, first, last);
            Console.WriteLine($"[Quests] Quest {questId} is offered again" +
                              (cleared > 0 ? $", its {cleared} mark(s) wiped." : "."));
        }

        /// <summary>How many marks a quest numbers after itself: -894001 to -894999 are 894's.</summary>
        private const int QuestMarkSpan = 1000;

        /// <summary>
        /// A quest's run written to the base as the log holds it now: the test routes' «finish»
        /// closes steps on the log alone, and after a restart the character found those quests
        /// still under way -- every quest it asked for undone again.
        /// </summary>
        internal static void SaveAsItStands(int questId)
        {
            var run = Log?.Run(questId);
            if (run != null) Save(questId, run);
        }

        /// <summary>
        /// The test API's «untick»: an objective of the step in hand marked undone again, to play
        /// it once more after changing how it is done. False when it was not done.
        /// </summary>
        internal static bool UntickForTest(int questId, int objectiveId)
        {
            var run = Log?.Run(questId);
            if (run == null || run.Finished || !run.Done.Remove(objectiveId)) return false;
            Save(questId, run);
            return true;
        }

        private static void Save(int questId, QuestRun run)
        {
            long characterId = SessionContext.State.CharacterId;
            if (characterId == 0) return;

            long finishedAt = run.Finished && run.FinishedAt != default
                ? new DateTimeOffset(run.FinishedAt).ToUnixTimeSeconds()
                : 0;
            DatabaseManager.SaveQuestProgress(characterId, new DatabaseManager.QuestRow(
                questId, run.StepId, QuestLog.Pack(run), run.Finished, finishedAt));
        }
    }
}
