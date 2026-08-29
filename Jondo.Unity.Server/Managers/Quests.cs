using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;

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
        private static QuestCatalogue? _book;

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

        public static bool Ready => _book != null && _book.Ready;

        /// <summary>This character's log. Null before entering the world.</summary>
        public static QuestLog? Log => SessionContext.State.Quests;

        /// <summary>
        /// Reads the catalogue. Once, at startup: it is 3 MB of JSON and it never changes.
        /// </summary>
        public static void Load()
        {
            if (_book != null) return;

            // No ClientText: the server has no use for translated quest names, and handing it one
            // would make it read a 339,342-entry language file it would never look at.
            _book = new QuestCatalogue(null, Console.WriteLine);
            if (!_book.Ready)
            {
                Console.WriteLine("[Misiones] No hay catálogo. Nadie podrá coger una misión.");
                return;
            }

            HandedOverBy.Clear();
            foreach (var quest in _book.All())
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
            foreach (var quest in _book.All())
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

            Console.WriteLine($"[Misiones] {_book.QuestCount:N0} misiones, {_book.StepCount:N0} pasos, " +
                              $"{HandedOverBy.Count:N0} frases que reparten una, " +
                              $"{GivenBy.Count:N0} NPCs que las dan.");

            if (Bindings.Count > 0)
            {
                Console.WriteLine($"[Misiones] {Bindings.Count} objetivo(s) atados a algo que pinchar, " +
                                  $"en {Bindings.ElementCount} elemento(s).");
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

            var log = new QuestLog(_book,
                () => SessionContext.State.CharacterLevel,
                () => SessionContext.State.MapId);

            int rows = 0;
            foreach (var row in DatabaseManager.LoadQuestProgress(characterId))
            {
                log.Restore(row.QuestId, row.StepId, row.Objectives, row.Completed);
                rows++;
            }

            SessionContext.State.Quests = log;
            if (rows > 0) Console.WriteLine($"[Misiones] {rows} en el diario del personaje {characterId}.");
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
        public static async Task OnReplyAsync(NetworkStream stream, long dialogId)
        {
            var log = Log;
            if (log == null || dialogId <= 0) return;

            foreach (var step in StepsHandedOverBy(dialogId))
            {
                await StartAsync(stream, step.QuestId);
            }
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
        public static async Task OnTalkingToAsync(NetworkStream stream, int npcId)
        {
            var log = Log;
            if (log == null || _book == null || npcId == 0) return;

            foreach (var run in new List<QuestRun>(log.Doing()))
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;

                foreach (var objective in step.Objectives)
                {
                    if (objective.NpcId != npcId) continue;
                    if (objective.MapId != 0 && objective.MapId != SessionContext.State.MapId) continue;

                    // "Bring me five ortie" and "show me the ring" name the same NPC as "come and
                    // see me", and turning up empty-handed must not tick them off.
                    if (objective.ItemId != 0 && !await HandOverAsync(stream, objective)) continue;

                    await TickAsync(stream, run.QuestId, objective.Id);
                }

                // And the free-text ones that are a conversation in all but the catalogue's type:
                // "Parler à un vieux de la vieille" is a talk objective Ankama wrote as prose.
                foreach (var objective in step.Objectives)
                {
                    var binding = Bindings.Of(objective.Id);
                    if (binding == null || binding.Kind != QuestBindingKind.Talk) continue;
                    if (binding.NpcId != npcId) continue;
                    if (binding.MapId != 0 && binding.MapId != SessionContext.State.MapId) continue;

                    await CloseAsync(stream, binding);
                }
            }
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
        private static async Task<bool> HandOverAsync(NetworkStream stream, QuestObjective objective)
        {
            int wanted = Math.Max(1, objective.ItemCount);
            if (Equipment.HowMany(objective.ItemId) < wanted) return false;

            if (!objective.ConsumesItems) return true;

            bool taken = await Equipment.TakeAsync(stream, objective.ItemId, wanted);
            if (!taken)
            {
                Console.WriteLine($"[Misiones] No se ha podido cobrar {wanted}x{objective.ItemId} " +
                                  $"del objetivo {objective.Id}; se deja sin marcar.");
            }

            return taken;
        }

        /// <summary>The bindings from free-text objectives to the things that finish them.</summary>
        public static QuestBindingBook Bindings { get; private set; } = new QuestBindingBook();

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
        public static async Task<bool> OnInteractiveUsedAsync(NetworkStream stream, long mapId, int elementId)
        {
            var log = Log;
            if (log == null || _book == null) return false;

            bool moved = false;
            foreach (var binding in Bindings.At(mapId, elementId))
            {
                if (binding.Kind != QuestBindingKind.Click) continue;
                if (await CloseAsync(stream, binding)) moved = true;
            }

            return moved;
        }

        /// <summary>
        /// Marks a bound objective off, if this character is really on it.
        /// </summary>
        /// <remarks>
        /// The guard is the whole safety of the binding file. A row is a claim that clicking
        /// something finishes an objective; without checking the run, a player who has never taken
        /// the quest would finish an objective of it by walking past a stele, and one who finished
        /// it last week would do it again.
        /// </remarks>
        private static async Task<bool> CloseAsync(NetworkStream stream, QuestBinding binding)
        {
            var log = Log;
            var run = log?.Run(binding.QuestId);
            if (log == null || _book == null || run == null || run.Finished) return false;
            if (run.Done.Contains(binding.ObjectiveId)) return false;

            var step = _book.Step(run.StepId);
            if (step == null) return false;

            bool wanted = false;
            foreach (var objective in step.Objectives)
            {
                if (objective.Id == binding.ObjectiveId) { wanted = true; break; }
            }

            if (!wanted) return false;

            // What it puts in the bag, before the tick: the item is the reason the objective is
            // finished, so a hand-out that fails must not leave it marked done.
            foreach (var (item, count) in binding.Gives)
            {
                if (await Equipment.GiveAsync(stream, item, count)) continue;

                Console.WriteLine($"[Misiones] El objetivo {binding.ObjectiveId} debía dar " +
                                  $"{count}x{item} y no se ha podido; no se marca.");
                return false;
            }

            await TickAsync(stream, binding.QuestId, binding.ObjectiveId);
            return true;
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
        public static async Task OnMapEnteredAsync(NetworkStream stream, long mapId, int subAreaId)
        {
            bool advanced = false;
            var log = Log;
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

                        if (here)
                        {
                            await TickAsync(stream, run.QuestId, objective.Id);
                            advanced = true;
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

                        await CloseAsync(stream, binding);
                        advanced = true;
                    }
                }
            }

            // Only when arriving actually finished something. The marks for this map went out
            // BEFORE the actor list, which is where the captures put them -- see the caller in
            // GameNodeProxy. Sending them again here every time would be a second frame per step
            // saying exactly what the first one said.
            if (advanced) await SendMarksAsync(stream, mapId);
        }

        /// <summary>
        /// Starts a quest and tells the client, if the character is allowed it.
        /// </summary>
        public static async Task<bool> StartAsync(NetworkStream stream, int questId)
        {
            var log = Log;
            if (log == null) return false;

            if (!log.CanStart(questId, out var verdict))
            {
                if (verdict.Broke)
                {
                    Console.WriteLine($"[Misiones] La condición de la misión {questId} no se entiende: " +
                                      $"{_book?.Of(questId)?.Criterion}");
                }

                return false;
            }

            var run = log.Start(questId);
            if (run == null) return false;

            // Said once per start rather than swallowed: a quest handed out on a condition that was
            // only half read is a different thing from one whose condition passed, and the person
            // running the server is the only one who can judge whether it matters.
            if (!verdict.FullyJudged)
            {
                Console.WriteLine($"[Misiones] La {questId} se da sin comprobar {string.Join(", ", verdict.Skipped)}: " +
                                  "este emulador no modela eso.");
            }

            Save(questId, run);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Ief, QuestProtocol.BuildQuestStarted(questId)));

            await SendStepAsync(stream, questId);

            // Y se apaga la marca verde del que la acaba de dar.
            await SendMarksAsync(stream, SessionContext.State.MapId);

            Console.WriteLine($"[Misiones] Empieza la {questId}, por el paso {run.StepId}.");
            return true;
        }

        /// <summary>
        /// Tells the client where a quest has got to (idu), which is what the ieo asks for.
        /// </summary>
        public static async Task SendStepAsync(NetworkStream stream, int questId)
        {
            var log = Log;
            var run = log?.Run(questId);
            if (log == null || run == null || run.Finished || _book == null) return;

            var step = _book.Step(run.StepId);
            if (step == null) return;

            var objectives = new List<int>(step.Objectives.Count);
            foreach (var objective in step.Objectives) objectives.Add(objective.Id);

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Idu,
                    QuestProtocol.BuildQuestStep(questId, run.StepId, objectives, run.Done)));
        }

        /// <summary>
        /// Puts the green mark over the NPCs of a map that have something to offer.
        /// </summary>
        /// <remarks>
        /// Sent on arriving at a map and again whenever a quest starts, because the mark going out
        /// is the same message with an empty list — that is how the captures do it, and 235 of the
        /// 380 of them are exactly that.
        ///
        /// Every NPC on the map is named, including the ones with nothing: an actor left out is an
        /// actor the client has heard nothing about, so it goes on drawing whatever it was told
        /// last time. Saying "this one has none" is the only way to take a mark away.
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
        public static async Task SendMarksAsync(NetworkStream stream, long mapId)
        {
            var log = Log;
            if (log == null || _book == null || mapId == 0) return;

            var here = Npcs.OnMap(mapId);
            if (here.Count == 0)
            {
                // An EMPTY frame, not silence. "iom (0)" is what the real server sends on entering
                // a map with nobody to mark, and it turns up in 20 of the captures -- every single
                // map load has one. Saying nothing is not the same thing: the client would go on
                // drawing whatever the previous map told it, so a mark could follow the player
                // around. The payload is genuinely zero bytes, map id included, which is why this
                // does not go through BuildQuestMarks.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iom, Array.Empty<byte>()));
                return;
            }

            // Solo los que tienen algo. El iom es un indice, no un censo: en las 145 tramas
            // reales no hay ni un actor nombrado con la lista vacia. Quien deja de tener nada
            // desaparece del indice, y eso es lo que le quita la marca.
            var marks = new List<(long Actor, IReadOnlyList<int> Quests)>(here.Count);
            foreach (var npc in here)
            {
                var offers = OfferedRightNowBy(npc.NpcId, mapId);
                if (offers.Count > 0) marks.Add((npc.ContextualId, offers));
            }

            // Y si en este mapa no queda nadie con nada, la trama vacia, que es como lo dice
            // Ankama al entrar en un mapa sin marcas: "iom (0)", cero bytes de cuerpo.
            byte[] cuerpo = marks.Count > 0
                ? QuestProtocol.BuildQuestMarks(mapId, marks)
                : Array.Empty<byte>();

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iom, cuerpo));

            // Se dice en voz alta porque la ausencia de marca tiene DOS causas que se ven igual —no
            // se ha mandado nada, o se ha mandado la lista vacía— y distinguirlas costaba abrir el
            // registro de tráfico y decodificar el iom a mano. Con esta línea se ve de un vistazo si
            // el NPC no tiene nada que ofrecer o si es el envío el que no llega.
            int conMarca = 0, ofrecidas = 0;
            foreach (var (_, quests) in marks)
            {
                if (quests.Count == 0) continue;
                conMarca++;
                ofrecidas += quests.Count;
            }

            Console.WriteLine($"[Misiones] Marcas del mapa {mapId}: {conMarca} de {marks.Count} NPCs " +
                              $"con algo que ofrecer, {ofrecidas} misión(es).");
        }

        /// <summary>
        /// What this NPC can hand over to THIS character right now, conditions included.
        /// </summary>
        /// <remarks>
        /// Two callers and they must never disagree: the green mark travels in the NPC's own actor
        /// record inside the jss -- see <c>ConnectionProtocol.AddNpcs</c>, which is what actually
        /// draws it -- and the iom carries the same list again as a sub-area index. Two copies of
        /// this filter would be two chances to drift.
        /// </remarks>
        public static List<int> OfferedRightNowBy(int npcId, long mapId)
        {
            var log = Log;
            var offers = new List<int>();
            if (log == null) return offers;

            foreach (int questId in QuestsOfferedBy(npcId, mapId))
            {
                if (log.CanStart(questId, out _)) offers.Add(questId);
            }

            return offers;
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
                        && (objective.MapId == 0 || objective.MapId == mapId))
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
        /// Sends this character's whole quest journal, one idu per quest under way.
        /// </summary>
        /// <remarks>
        /// Sent on entering the world, in place of what the replayed capture used to carry. That
        /// capture holds 261 idu frames in the first block and 4 more in the map block, and every
        /// one of them belongs to the account somebody recorded — so before this, anybody who
        /// logged in was shown a stranger's quest journal, and once there was an engine those
        /// quests would also have been ones the server did not think they had.
        ///
        /// Nothing is sent for a character with no quests, which is what a new one looks like.
        /// </remarks>
        public static async Task SendJournalAsync(NetworkStream stream)
        {
            var log = Log;
            if (log == null) return;

            if (_book == null) return;

            // One idr with the lot, which is how the captured block does it, rather than one idu
            // per quest. Sending it even when it is empty matters: the client fills the window from
            // this message, and an empty one is what says "this character has no journal".
            var doing = new List<(int, int, IReadOnlyList<int>, IReadOnlyCollection<int>)>();
            foreach (var run in log.Doing())
            {
                var step = _book.Step(run.StepId);
                if (step == null) continue;

                var objectives = new List<int>(step.Objectives.Count);
                foreach (var objective in step.Objectives) objectives.Add(objective.Id);

                doing.Add((run.QuestId, run.StepId, objectives, run.Done));
            }

            var finished = new List<int>();
            foreach (var pair in log.Runs)
            {
                if (pair.Value.Finished) finished.Add(pair.Key);
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Idr, QuestProtocol.BuildJournal(doing, finished)));

            Console.WriteLine($"[Misiones] Diario enviado: {doing.Count} en curso, {finished.Count} hechas.");
        }

        /// <summary>
        /// Ticks an objective off and sends whatever that changed.
        /// </summary>
        /// <remarks>
        /// One method for both ways an objective can be finished — the client saying so, and the
        /// server working it out — so that the packets that follow cannot differ between them.
        /// </remarks>
        public static async Task TickAsync(NetworkStream stream, int questId, int objectiveId,
                                           int amount = 0)
        {
            var log = Log;
            if (log == null) return;

            var move = log.Tick(questId, objectiveId, amount);
            if (move.Nothing) return;

            await AfterMoveAsync(stream, questId, move);
        }

        /// <summary>Finishes the step in hand outright and sends what that changed.</summary>
        public static async Task FinishStepAsync(NetworkStream stream, int questId)
        {
            var log = Log;
            if (log == null) return;

            var move = log.FinishStep(questId);
            if (move.Nothing) return;

            await AfterMoveAsync(stream, questId, move);
        }

        private static async Task AfterMoveAsync(NetworkStream stream, int questId, QuestMove move)
        {
            var run = Log?.Run(questId);
            if (run == null) return;

            Save(questId, run);

            if (move.StepFinished)
            {
                // idz names the step that WAS in hand, not the one now in hand, and it goes out
                // before the next step does. The tutorial shows that order 21 times.
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Idz,
                        QuestProtocol.BuildStepValidated(questId, move.FinishedStep)));

                await PayAsync(stream, move.FinishedStep);
            }

            if (!move.QuestFinished)
            {
                await SendStepAsync(stream, questId);
            }
            else
            {
                Console.WriteLine($"[Misiones] Terminada la {questId}.");

                // Y lo que esa misión acabada haya ganado. 259 logros cuelgan de terminar una, y
                // el 8518 «Primer tiempo» del tutorial es literalmente (Qf=2511).
                await Achievements.AfterQuestAsync(stream, questId);
            }
        }

        /// <summary>
        /// Hands over what a finished step promised.
        /// </summary>
        /// <remarks>
        /// <b>The items only, and that is a real gap rather than an oversight.</b> A reward carries
        /// items with their quantity — exact numbers, and 5,582 of the 6,707 rewards have some —
        /// but the experience and the kamas are <em>ratios</em>: 2, or 1.2, or 0.035. A ratio is a
        /// multiplier on a base this emulator does not have, and the base is not in the client's
        /// data anywhere anybody has looked. Inventing a formula would put a number on screen that
        /// looks right and is not, which is worse than nothing, so what happens instead is that the
        /// ratio is written to the log and a person can decide.
        ///
        /// Nothing here can fail the step. A bag with no room, or an item id the database does not
        /// know, must not leave a quest half advanced — the step is already validated by the time
        /// this runs, on purpose.
        /// </remarks>
        private static async Task PayAsync(NetworkStream stream, int stepId)
        {
            var step = _book?.Step(stepId);
            if (step == null || step.Rewards.Count == 0) return;

            foreach (var reward in step.Rewards)
            {
                foreach (var (item, count) in reward.Items)
                {
                    bool given = await Equipment.GiveAsync(stream, item, Math.Max(1, count));
                    if (!given)
                    {
                        Console.WriteLine($"[Misiones] El objeto {item} del paso {stepId} no se ha " +
                                          "podido dar.");
                    }
                }

                if (reward.ExperienceRatio > 0 || reward.KamasRatio > 0)
                {
                    Console.WriteLine($"[Misiones] El paso {stepId} promete experiencia x" +
                                      $"{reward.ExperienceRatio} y kamas x{reward.KamasRatio}. Son " +
                                      "multiplicadores y no se sabe sobre qué, así que no se pagan.");
                }
            }
        }

        private static void Save(int questId, QuestRun run)
        {
            long characterId = SessionContext.State.CharacterId;
            if (characterId == 0) return;

            DatabaseManager.SaveQuestProgress(characterId, new DatabaseManager.QuestRow(
                questId, run.StepId, QuestLog.Pack(run), run.Finished));
        }
    }
}
