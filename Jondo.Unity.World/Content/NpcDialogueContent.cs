using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>One thing the player can say, and where saying it leads.</summary>
    public sealed class DialogueChoice
    {
        /// <summary>The reply's id, out of the ones the NPC's template declares.</summary>
        public long Reply { get; init; }

        /// <summary>The line it leads to. Zero ends the conversation, which is most of them.</summary>
        public long Next { get; init; }

        /// <summary>
        /// The quest this reply belongs to, or zero when it belongs to no quest.
        /// </summary>
        /// <remarks>
        /// Without this the server has no way to hide a reply that has no business being there, and
        /// the result is what the fallback does today: Snori Nairb offers all thirty-nine of his
        /// replies at once, including the ones that only make sense halfway through a quest nobody
        /// has started.
        ///
        /// The rule the two fields express, in the order they are checked:
        ///
        ///   quest only        the quest has to be under way, or finished if <see cref="AfterQuest"/>
        ///   quest and step    it has to be under way AND on that step
        ///   neither           always offered
        /// </remarks>
        public int Quest { get; init; }

        /// <summary>The step of that quest it belongs to. Zero means any step.</summary>
        public int Step { get; init; }

        /// <summary>
        /// The quest this reply hands over, or zero.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Quest"/> and it has to be: <see cref="Quest"/> hides a reply
        /// until the quest is under way, and this one is the reply that <em>puts</em> it under way,
        /// so marking it the same would hide the only way of starting it.
        ///
        /// Before this the engine started a quest on any reply of the line the step names, which
        /// meant "no thanks" handed it over too. The capture cannot say which reply is the yes —
        /// the extra field Ankama's replies carry is on 184 of the 429 captured ones and almost
        /// none are quest replies — so it is said here, where a person writing the tree knows.
        /// </remarks>
        public int StartsQuest { get; init; }

        /// <summary>True when the reply is for somebody who has already finished the quest.</summary>
        public bool AfterQuest { get; init; }

        /// <summary>The objective that has to have been met for the reply to be offered.</summary>
        /// <remarks>
        /// The step is not enough. A single-step quest with four objectives --«Matarratas» is one--
        /// is always on that step, so conditioning by step distinguishes nothing and the innkeeper
        /// offered «He neutralizado a la rata» from minute one, with the rat alive.
        /// </remarks>
        public int AfterObjective { get; init; }

        /// <summary>
        /// Objectives that all have to have been met: «Ya he visitado a varios habitantes del
        /// pueblo» once the five visits of «Un pueblo sobre las nubes» are done, in any order.
        /// </summary>
        public IReadOnlyList<int> AfterObjectives { get; init; } = Array.Empty<int>();

        /// <summary>
        /// The objective that must still be to do for the reply to be offered: Ternette's
        /// «Presentarte.» is said once, while 1632's visit to her is pending.
        /// </summary>
        public int BeforeObjective { get; init; }

        /// <summary>
        /// A quest neither started nor finished for the reply to be offered: Ternette's «Estoy
        /// impaciente por explorar la isla celeste» leads to 1639 only for whoever has not had it.
        /// </summary>
        public int BeforeQuest { get; init; }

        /// <summary>
        /// Replies of the same group are one reply, picked at random each time the line is said:
        /// Azar's four «Lanzar los dados sobre la mesa.» each lead to a different throw.
        /// </summary>
        public int RandomGroup { get; init; }

        /// <summary>The interactive that has to have been used for the reply to be offered.</summary>
        /// <remarks>
        /// «He visto el anuncio que has puesto» cannot exist before having read the notice.
        /// It is not an objective -- the quest has not started yet, this very reply starts it --
        /// so the condition cannot come from the journal: it comes from having pressed the sign.
        /// </remarks>
        public int AfterElement { get; init; }

        /// <summary>What this reply buys, and for how much. Zero when it buys nothing.</summary>
        /// <remarks>
        /// The price goes in the reply's text --«Ponme una limonada. Toma, 1 kama.»-- and
        /// until now that was all: a sentence. Without this, pressing it neither gave the item nor charged,
        /// and the quest that needs that lemonade could not be finished.
        /// </remarks>
        public int BuysItem { get; init; }
        public int BuysCount { get; init; } = 1;
        public long BuysPrice { get; init; }

        /// <summary>The map this reply moves the player to, or zero when it moves nobody.</summary>
        /// <remarks>
        /// A reply is not always something said: Draconiros' "Join the dream crucible" ends the
        /// conversation with a map change, and the capture shows it as a kld followed straight by
        /// a jru. Without this the reply is offered, chosen, and does nothing at all - which is
        /// the failure this project keeps meeting, the one that raises no error anywhere.
        /// </remarks>
        public long TeleportsTo { get; init; }

        /// <summary>A passage key in interactives/teleports.json, owned by this dialogue's NPC on this map.</summary>
        public int TeleportPassage { get; init; }

        /// <summary>
        /// The numbers this reply carries inside its own message, for the client to read into
        /// its text. Empty for almost every reply.
        /// </summary>
        /// <remarks>
        /// Measured on the Rey Gob of the Infinite Dreams, whose reply travels as
        /// f2 { f1: 82314, f3 { f1: 4037 }, f3 { f1: 4035 } }. Without them the reply is still
        /// offered, but with an empty hole where its number should be.
        /// </remarks>
        public IReadOnlyList<long> Parameters { get; init; } = Array.Empty<long>();

        /// <summary>
        /// The arguments each of <see cref="Parameters"/> carries, by position: what the client
        /// draws beside the reply is an effect, and some name what they act on. The guided
        /// tutorial's Ganymed offers «Recibir el equipo» with six 193 «Gana x#2 #3» and a 34
        /// «Empieza una misión» whose f2 is the quest, 2511: f3 { f1: 34, f2: [2511] }. Written in
        /// the file as an array, [34, 2511]; a bare number has none.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<long>> ParameterArgs { get; init; } = Array.Empty<IReadOnlyList<long>>();

        /// <summary>
        /// What this reply does to the player's dream points, as a percentage. Zero for the
        /// replies of everybody who is not inside a dream.
        /// </summary>
        /// <remarks>
        /// The Rey Gob of the Infinite Dreams says it in his own words — "Multiplicar los puntos
        /// de sueño por 1,5" — so it is 150 here. It is a percentage and not a factor because
        /// there is no reason to carry a decimal through a JSON file for one NPC.
        ///
        /// He stands at a fountain, one in four, and is not the guide's Faveur Onirique: that is
        /// a room of its own with its own NPC, see <see cref="DreamFavor"/>.
        /// </remarks>
        public int DreamPointsPercent { get; init; }

        /// <summary>
        /// True when this reply opens the dream favour of the room: the Dispensador de favores'
        /// "Acepto el favor.", which ends the conversation on the favour's three choices.
        /// </summary>
        public bool DreamFavor { get; init; }

        /// <summary>
        /// The mutation item this reply gives, or zero: the Orbalantir de Meriana's «Pensar en Dark
        /// Vlad y tocar las escamas.» takes the player to the epic fight and mutates him (15019).
        /// </summary>
        public int GivesMutation { get; init; }

        /// <summary>True when the reply takes the player's mutation away: the Orbalantir's «Pensar en la casa de Meriana.».</summary>
        public bool EndsMutation { get; init; }

        /// <summary>
        /// With <see cref="CompletesAll"/>: the reply hands over whatever of its objectives' items is
        /// carried, and is offered as soon as one of them is -- Kaponor's «Toma, esto es lo que he
        /// encontrado», the raft's materials brought one by one.
        /// </summary>
        public bool Partial { get; init; }

        /// <summary>
        /// A quest the reply ends at once, started if need be, with none of its rewards: siding with
        /// the bworks against Gobatchef in «Les Gobelins de la Manufacture».
        /// </summary>
        public int Forfeits { get; init; }

        /// <summary>Other items the reply hands over with <see cref="Gives"/>, one of each.</summary>
        public IReadOnlyList<int> AlsoGives { get; init; } = Array.Empty<int>();

        /// <summary>Kamas the reply pays: the mulou's for the goblin of «Titi Gobelait le magobelin».</summary>
        public long GivesKamas { get; init; }

        /// <summary>
        /// A condition on the character, in the client's criterion language, for the reply to be
        /// offered: «PS=1» a woman, «PE=14» who knows the crossed-arms attitude, «Pw>1499» with that
        /// many pods free -- the right answers to the guardian of the bridge of death, which are not
        /// the same for everybody. Empty for none.
        /// </summary>
        public string Criterion { get; init; } = "";

        /// <summary>
        /// Stagings (hpm) the reply plays: the guided tutorial's 901 and 102 on «Ver cómo se
        /// materializa un camino», the path to the next island (Managers.MapStagings).
        /// </summary>
        public IReadOnlyList<int> Stagings { get; init; } = Array.Empty<int>();

        /// <summary>True when the reply puts the player back where they came in from.</summary>
        /// <remarks>
        /// Separate from <see cref="TeleportsTo"/> because there is no map to write down: the
        /// destination is wherever this particular player was standing before they went in. In the
        /// capture Draconiros' "Leave the dream plane" answers with a jru to the Astrub inn, which
        /// is not a property of the dream at all - it is where that player had been.
        ///
        /// One sample, so this is an inference rather than a measurement: a fixed destination
        /// would fit the bytes just as well. It is the reading that makes the exit an exit for
        /// everybody instead of only for someone who happened to start in that inn.
        /// </remarks>
        public bool ReturnsHome { get; init; }

        /// <summary>
        /// The conquest role this reply takes, or zero: Hal Ylanza's «Quiero ser explorador.» hands
        /// over the role's certificate and goes on to his «¡Enhorabuena!».
        /// </summary>
        public int KothRole { get; init; }

        /// <summary>
        /// Where a <see cref="KothRole"/> reply leads when the role cannot be changed yet -- «Lo
        /// siento, aún no estás listo para cambiar de rol. Vuelve a verme dentro de unos minutos.»
        /// --, or where a <see cref="Chapeuvepes"/> trade leads when they are not enough -- «No
        /// tienes chapeuvepés suficientes para que podamos hacer un intercambio.» --, or where a
        /// purchase leads when the kamas are not enough, or zero to end the conversation.
        /// </summary>
        public long RefusedNext { get; init; }

        /// <summary>
        /// The chapeuvepés this reply costs (Managers.HeadHunts), or zero: Ema and Tomás Sacre's
        /// trades, «Tomar una gema que contiene el alma del Escarajefe Dorado a cambio de 8
        /// chapeuvepés.». It hands over <see cref="Trades"/>, <see cref="TradeCount"/> of them.
        /// </summary>
        public int Chapeuvepes { get; init; }
        public int Trades { get; init; }
        public int TradeCount { get; init; } = 1;

        /// <summary>
        /// An NPC of the map who comes in and opens his own conversation once this reply has
        /// closed the window, or zero. Master Darmas's «Sobresaltarte al ver aparecer a Ganymed»:
        /// kld, then jsn with Ganymed and his ioc and ios (the tutorial capture).
        /// </summary>
        public int ThenNpc { get; init; }

        /// <summary>
        /// The conversation objective of <see cref="Quest"/> this reply does, or zero. «Matarratas»'
        /// capture: Grobid's «He neutralizado a la rata.» carries effect 30 with objective 9771,
        /// «go back and see Grobid», and the quest ends when it is chosen, not on hello. An
        /// objective a reply does is not done on opening the conversation.
        /// </summary>
        public int Completes { get; init; }

        /// <summary>
        /// An NPC who comes into the map once <see cref="Completes"/> is done, or zero: Al Etsop
        /// breaks off at «¿Qué pasa, Abel?» and Abel Hoz is there («Ça tombe à l'eau»). His own
        /// placement holds the criterion that keeps him there afterwards.
        /// </summary>
        public int ShowsNpc { get; init; }

        /// <summary>
        /// Others who come in with <see cref="ShowsNpc"/> («alsoShows»): the flying and the swift
        /// blades of «Cabeza de turco», drawn out together by Nibe Lula's diuretic.
        /// </summary>
        public IReadOnlyList<int> AlsoShows { get; init; } = Array.Empty<int>();

        /// <summary>
        /// More objectives of <see cref="Quest"/> this reply does, beside <see cref="Completes"/>:
        /// Vey Gana's «Darle los ingredientes.» hands over the four slobbers, four objectives.
        /// </summary>
        public IReadOnlyList<int> CompletesAll { get; init; } = Array.Empty<int>();

        /// <summary>
        /// An item the NPC hands over with this reply, or zero; when the reply also does an
        /// objective, only once it is done: Kotorra's jam for the five nettles.
        /// </summary>
        public int Gives { get; init; }

        /// <summary>How many of <see cref="Gives"/>.</summary>
        public int GivesCount { get; init; } = 1;

        /// <summary>
        /// The character's level the reply asks for, or zero: Archi's «Claro, cuenta conmigo.» from
        /// level 10, the level «Eso va a costar un huevo» asks (PL>9).
        /// </summary>
        public int MinLevel { get; init; }

        /// <summary>
        /// The level under which the reply is offered, or zero: «Aún no me noto con fuerzas para una
        /// misión así.», for whoever is not yet level 10.
        /// </summary>
        public int BelowLevel { get; init; }

        /// <summary>
        /// An item the reply hands to the NPC, or zero: offered only to whoever carries one, and
        /// taken when chosen -- «Darle una cerveza a Acidrik.».
        /// </summary>
        public int Takes { get; init; }

        /// <summary>How many of <see cref="Takes"/>: the meadow's mercenary asks for ten pieces of larva.</summary>
        public int TakesCount { get; init; } = 1;

        /// <summary>
        /// Items the reply hands to the NPC, one of each, all or none: Nibe Lula's five contaminated
        /// samples. Without <see cref="RefusedNext"/> it is offered only to whoever carries them all;
        /// with it, it is always offered and leads there when one is missing -- «Faltan una o más
        /// muestras. Tráemelas todas a la vez.».
        /// </summary>
        public IReadOnlyList<int> TakesAll { get; init; } = Array.Empty<int>();

        /// <summary>
        /// An item the character must carry, <see cref="HoldsCount"/> of them, for the reply to be
        /// offered, or zero. Nothing is taken: the objective the reply does hands them over --
        /// Cindy Cato's fifteen boxes of knick-knacks.
        /// </summary>
        public int Holds { get; init; }

        /// <summary>How many of <see cref="Holds"/>.</summary>
        public int HoldsCount { get; init; } = 1;

        /// <summary>Whether a character of that level is offered the reply.</summary>
        public bool ForLevel(int level) => (MinLevel == 0 || level >= MinLevel) && (BelowLevel == 0 || level < BelowLevel);

        /// <summary>Whether any reply of this tree does that objective.</summary>
        internal bool Does(int objectiveId)
            => objectiveId != 0 && (Completes == objectiveId || CompletesAll.Contains(objectiveId));

        /// <summary>Every objective this reply does: <see cref="Completes"/> and <see cref="CompletesAll"/>.</summary>
        public IEnumerable<int> Objectives()
        {
            if (Completes != 0) yield return Completes;
            foreach (int objective in CompletesAll) yield return objective;
        }

        public bool Ends => Next == 0;

        /// <summary>Whether this reply is offered to nobody in particular.</summary>
        public bool Always => Quest == 0;

        /// <summary>
        /// Whether something besides its quest decides who is offered it: a criterion, a level, an
        /// item to carry or hand over, a quest it starts or comes before, an interactive used.
        /// </summary>
        public bool Gated => Criterion.Length > 0 || MinLevel != 0 || BelowLevel != 0 || Holds != 0
                             || Takes != 0 || TakesAll.Count > 0 || StartsQuest != 0 || BeforeQuest != 0
                             || AfterElement != 0;

        public override string ToString()
        {
            string where = Ends ? "✕" : "→ " + Next;
            if (Quest == 0) return $"{Reply} {where}";
            return Step == 0
                ? $"{Reply} {where} (quest {Quest})"
                : $"{Reply} {where} (quest {Quest} step {Step})";
        }
    }

    /// <summary>One thing the NPC says, and what can be said back.</summary>
    public sealed class DialogueLine
    {
        public long Message { get; init; }

        /// <summary>
        /// True when the line names the character -- «¡Has trabajado muy bien, #1!» -- and the
        /// name goes with it, as ios f3: Master Curro's 20681 in the tutorial capture.
        /// </summary>
        public bool NamesTheCharacter { get; init; }

        public IReadOnlyList<DialogueChoice> Choices { get; init; } = Array.Empty<DialogueChoice>();

        public DialogueChoice? Choice(long reply)
        {
            foreach (var choice in Choices)
            {
                if (choice.Reply == reply) return choice;
            }

            return null;
        }

        /// <summary>
        /// The choice a reply stands for when the line offers it twice (DialogueChoice.Always): the
        /// first one this character is shown, else the first one.
        /// </summary>
        public DialogueChoice? Choice(long reply, Func<DialogueChoice, bool> shown)
        {
            DialogueChoice? first = null;
            foreach (var choice in Choices)
            {
                if (choice.Reply != reply) continue;
                if (shown(choice)) return choice;
                first ??= choice;
            }

            return first;
        }

        /// <summary>
        /// Whether one choice is offered to a character, his quests read through the callbacks:
        /// the filter of <see cref="RepliesFor"/>, one choice at a time.
        /// </summary>
        /// <remarks>
        /// The interactive is checked before anything else, because it holds the same for a reply
        /// that belongs to a quest and for one that starts it -- and the one that starts it is
        /// «Always» in the filter's eyes, so if this came later it would not be checked. Without
        /// <paramref name="objectiveDone"/>, a reply tied to an objective is not offered, which is
        /// the prudent side: better not to see it than to see it too early.
        /// </remarks>
        public static bool Shows(DialogueChoice choice, Func<int, bool> active, Func<int, bool> finished,
                                 Func<int, int, bool> onStep, Func<int, int, bool>? objectiveDone = null,
                                 Func<int, bool>? elementUsed = null)
        {
            if (choice.AfterElement != 0
                && (elementUsed == null || !elementUsed(choice.AfterElement))) return false;

            if (choice.BeforeQuest != 0 && (active(choice.BeforeQuest) || finished(choice.BeforeQuest))) return false;

            if (choice.Always) return true;

            if (choice.AfterQuest) return finished(choice.Quest);

            if (!active(choice.Quest)) return false;
            if (choice.Step != 0 && !onStep(choice.Quest, choice.Step)) return false;
            if (choice.BeforeObjective != 0 && objectiveDone != null && objectiveDone(choice.Quest, choice.BeforeObjective))
                return false;
            // What it does is done: Erty's «Darle la flor» kept coming back after the flower was his.
            if (objectiveDone != null && choice.Completes != 0 && objectiveDone(choice.Quest, choice.Completes))
                return false;
            if (objectiveDone != null && choice.CompletesAll.Count > 0
                && choice.CompletesAll.All(o => objectiveDone(choice.Quest, o)))
                return false;
            if (choice.AfterObjective != 0
                && (objectiveDone == null || !objectiveDone(choice.Quest, choice.AfterObjective)))
                return false;
            if (choice.AfterObjectives.Count > 0
                && (objectiveDone == null || choice.AfterObjectives.Any(o => !objectiveDone(choice.Quest, o))))
                return false;

            return true;
        }

        public long[] Replies()
        {
            var replies = new long[Choices.Count];
            for (int i = 0; i < Choices.Count; i++) replies[i] = Choices[i].Reply;
            return replies;
        }

        /// <summary>
        /// The replies this particular character should be shown.
        /// </summary>
        /// <remarks>
        /// The whole point of <see cref="DialogueChoice.Quest"/>. A reply that belongs to a quest
        /// is not offered to somebody who has not started it, and one that belongs to a step is not
        /// offered before that step is the one in hand — which is what stops a conversation from
        /// showing every answer to every question at once.
        ///
        /// <paramref name="onStep"/> answers "is this quest on that step". It is a callback rather
        /// than the quest log itself so that this file goes on knowing nothing about the server.
        /// </remarks>
        public long[] RepliesFor(Func<int, bool> active, Func<int, bool> finished,
                                 Func<int, int, bool> onStep, Func<int, int, bool>? objectiveDone = null,
                                 Func<int, bool>? elementUsed = null)
        {
            var replies = new List<long>(Choices.Count);
            foreach (var choice in Choices)
            {
                // A reply a quest has its own way through comes first, and the rest of the world's
                // after it: only one of the two is offered (see Choice(reply, shown)).
                if (replies.Contains(choice.Reply)) continue;
                if (Shows(choice, active, finished, onStep, objectiveDone, elementUsed)) replies.Add(choice.Reply);
            }

            // One of each random group, the dice of the line.
            if (Choices.Any(c => c.RandomGroup != 0))
            {
                foreach (var group in Choices.Where(c => c.RandomGroup != 0 && replies.Contains(c.Reply))
                                             .GroupBy(c => c.RandomGroup).ToList())
                {
                    var members = group.ToList();
                    var kept = members[Random.Shared.Next(members.Count)];
                    foreach (var other in members)
                    {
                        if (other != kept) replies.Remove(other.Reply);
                    }
                }
            }

            return replies.ToArray();
        }

        public override string ToString() => $"message {Message}, {Choices.Count} replies";
    }

    /// <summary>
    /// A line an NPC opens with when the character stands somewhere particular in a quest: before
    /// it, on a step of it, after it, or with one of its objectives done -- and per class, when
    /// the line is the god of the character's class.
    /// </summary>
    /// <remarks>
    /// One opening per NPC was not enough for any NPC a quest goes through. The guided tutorial's
    /// Ganymed opens four ways in the capture of it -- 57134 to welcome, 57133 back from the first
    /// fight, 57132 after the second, 60411 once the level is up -- and what decided which was the
    /// character's quests, which is server knowledge. Read in order; the first that holds wins;
    /// none, and the NPC's <see cref="NpcDialogue.Opening"/>.
    /// </remarks>
    public sealed class DialogueOpening
    {
        /// <summary>The line, for every class but those in <see cref="ByBreed"/>.</summary>
        public long Message { get; init; }

        /// <summary>The line for a class, by breed id: the Mysterious Voice presents each class's god.</summary>
        public IReadOnlyDictionary<int, long> ByBreed { get; init; } = new Dictionary<int, long>();

        /// <summary>Only while this quest is under way.</summary>
        public int Quest { get; init; }

        /// <summary>And on this step of it.</summary>
        public int Step { get; init; }

        /// <summary>And with this objective of it done.</summary>
        public int Objective { get; init; }

        /// <summary>And with this objective of it still to do: Curro welcomes before he is talked to.</summary>
        public int Pending { get; init; }

        /// <summary>Only to somebody who has neither started nor finished this quest.</summary>
        public int Before { get; init; }

        /// <summary>Only once this quest is finished.</summary>
        public int After { get; init; }

        /// <summary>
        /// Only to somebody who may start this quest now, its criterion met: the prince of Wey's
        /// «ya ewes suficientemente fuewte» for «Suite Woyale» (PL>49), his «vuélvete un poco más
        /// fuewte» for anyone else after «Le Pwince Monseigneuw».
        /// </summary>
        public int CanStart { get; init; }

        /// <summary>
        /// Only to somebody carrying this item: valet Doz's «Oh, ¡una librea! ¡Gracias!» once Pako
        /// Rábano has sewn it, his «no tengo librea adecuada» before.
        /// </summary>
        public int HoldsItem { get; init; }

        /// <summary>The line for a character of this class.</summary>
        public long MessageFor(int breed) => ByBreed.TryGetValue(breed, out long line) ? line : Message;

        /// <summary>Whether it holds for a character, his quests read through the callbacks.</summary>
        public bool Holds(Func<int, bool> active, Func<int, bool> finished, Func<int, int, bool> onStep,
                          Func<int, int, bool> objectiveDone, Func<int, bool>? canStart = null,
                          Func<int, bool>? carries = null)
        {
            if (CanStart != 0 && (canStart == null || !canStart(CanStart))) return false;
            if (HoldsItem != 0 && (carries == null || !carries(HoldsItem))) return false;
            if (Before != 0 && (active(Before) || finished(Before))) return false;
            if (After != 0 && !finished(After)) return false;
            if (Quest != 0 && !active(Quest)) return false;
            if (Step != 0 && !onStep(Quest, Step)) return false;
            if (Objective != 0 && !objectiveDone(Quest, Objective)) return false;
            if (Pending != 0 && objectiveDone(Quest, Pending)) return false;
            return true;
        }
    }

    /// <summary>
    /// A line the NPC says without being clicked, the moment a step of a quest is done -- the
    /// tutorial's voice presenting the class's god as soon as the first steps are taken (the
    /// capture's idz 3226 is followed by the ioc and the ios, with no iov between).
    /// </summary>
    public sealed class DialoguePush
    {
        /// <summary>The step whose end opens it.</summary>
        public int AfterStep { get; init; }

        /// <summary>The line it opens on, per class as an opening can be.</summary>
        public DialogueOpening Line { get; init; } = new DialogueOpening();

        /// <summary>
        /// Stagings (hpm) played before the line and after it: the tutorial's 912 and 302 around
        /// the voice's presenting the god's statue.
        /// </summary>
        public IReadOnlyList<int> StagingsBefore { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Stagings { get; init; } = Array.Empty<int>();
    }

    /// <summary>A whole conversation with one NPC.</summary>
    public sealed class NpcDialogue
    {
        /// <summary>The lines it opens with by quest, in order. See <see cref="DialogueOpening"/>.</summary>
        public IReadOnlyList<DialogueOpening> Openings { get; init; } = Array.Empty<DialogueOpening>();

        /// <summary>The lines it opens on by itself when a step ends. See <see cref="DialoguePush"/>.</summary>
        public IReadOnlyList<DialoguePush> Pushes { get; init; } = Array.Empty<DialoguePush>();

        /// <summary>
        /// The line it opens with for this character: the first opening that holds, or else
        /// <see cref="First"/>. A line it opens on that is not one of its lines comes back bare,
        /// with no replies -- the client draws its Leave.
        /// </summary>
        public DialogueLine? OpeningFor(int breed, Func<int, bool> active, Func<int, bool> finished,
                                        Func<int, int, bool> onStep, Func<int, int, bool> objectiveDone,
                                        Func<int, bool>? canStart = null, Func<int, bool>? carries = null)
        {
            foreach (var opening in Openings)
            {
                if (!opening.Holds(active, finished, onStep, objectiveDone, canStart, carries)) continue;
                long message = opening.MessageFor(breed);
                if (message == 0) continue;
                return Line(message) ?? new DialogueLine { Message = message };
            }
            return First();
        }

        public int NpcId { get; init; }

        /// <summary>Which map this is for. Zero means wherever the NPC stands.</summary>
        public long MapId { get; init; }

        /// <summary>The line it opens with. Zero falls back to the template's.</summary>
        public long Opening { get; init; }

        public IReadOnlyList<DialogueLine> Lines { get; init; } = Array.Empty<DialogueLine>();

        /// <summary>Whether a reply of this tree does that objective (<see cref="DialogueChoice.Completes"/>).</summary>
        public bool HasReplyFor(int objectiveId)
        {
            foreach (var line in Lines)
            {
                foreach (var choice in line.Choices)
                {
                    if (choice.Does(objectiveId)) return true;
                }
            }
            return false;
        }

        public NpcDialogueKey Key => new NpcDialogueKey(NpcId, MapId);

        public DialogueLine? Line(long message)
        {
            foreach (var line in Lines)
            {
                if (line.Message == message) return line;
            }

            return null;
        }

        /// <summary>The line it starts on, whether or not <see cref="Opening"/> was filled in.</summary>
        public DialogueLine? First()
            => Opening != 0 ? Line(Opening) : (Lines.Count > 0 ? Lines[0] : null);

        public override string ToString()
            => MapId == 0 ? $"npc {NpcId}" : $"npc {NpcId} on map {MapId}";
    }

    /// <summary>
    /// Which conversation this is: one NPC, on one map, or on any of them.
    /// </summary>
    /// <remarks>
    /// The map is in the key because the opening line is per map: the same character standing in
    /// two places has no reason to say the same thing, and the real game does not make it. A
    /// dialogue written with map 0 is the one used wherever nothing more specific was written,
    /// which is what most NPCs will ever need.
    /// </remarks>
    public readonly struct NpcDialogueKey : IEquatable<NpcDialogueKey>
    {
        /// <summary>Map 0: this conversation goes wherever the NPC does.</summary>
        public const long AnyMap = 0;

        public NpcDialogueKey(int npcId, long mapId)
        {
            NpcId = npcId;
            MapId = mapId;
        }

        public int NpcId { get; }

        public long MapId { get; }

        public bool IsForEveryMap => MapId == AnyMap;

        public bool Equals(NpcDialogueKey other) => NpcId == other.NpcId && MapId == other.MapId;
        public override bool Equals(object? obj) => obj is NpcDialogueKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(NpcId, MapId);
        public override string ToString() => MapId == 0 ? NpcId.ToString() : $"{NpcId}@{MapId}";
    }

    /// <summary>
    /// Which reply leads to which line: the one thing about an NPC the client has never held.
    /// </summary>
    /// <remarks>
    /// This is the clearest case in the whole project for why the authored layer exists. The client
    /// ships every line an NPC can say and every reply it can be given — 6,467 NPCs' worth — and
    /// <b>nowhere does it say which goes with which</b>. That pairing has always lived on Ankama's
    /// server, so it cannot be extracted, measured or inferred. It has to be decided.
    ///
    /// What it costs not to have it is visible in the game: Snori Nairb has three lines and
    /// thirty-nine replies, and without a tree all thirty-nine are offered at once, under the first
    /// line, in the order the client happened to list them.
    ///
    /// So there is exactly one layer here and it is the authored one. There is no measured layer to
    /// merge underneath, because there is nothing to measure — and that is worth saying out loud
    /// rather than leaving as an empty slot somebody fills in later with a guess.
    /// </remarks>
    public static class NpcDialogueContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "npcs/dialogues.json";

        /// <summary>A list of numbers on a JSON entry, or an empty one.</summary>
        private static IReadOnlyList<long> Numbers(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<long>();

            var salen = new List<long>();
            foreach (var item in list.EnumerateArray())
            {
                // TryGetInt64 throws on anything that is not a number, so the kind is asked first.
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long valor)) salen.Add(valor);
                else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                         && item[0].ValueKind == JsonValueKind.Number && item[0].TryGetInt64(out long effect))
                    salen.Add(effect);
            }
            return salen;
        }

        /// <summary>
        /// The arguments of each entry of "params", by position: an entry written as an array,
        /// [34, 2511], is the effect and then what it carries; a bare number carries nothing.
        /// Empty when no entry carries anything, which is nearly always.
        /// </summary>
        private static IReadOnlyList<IReadOnlyList<long>> ParameterArguments(JsonElement element)
        {
            if (!element.TryGetProperty("params", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<IReadOnlyList<long>>();

            var args = new List<IReadOnlyList<long>>();
            bool any = false;
            foreach (var item in list.EnumerateArray())
            {
                var these = new List<long>();
                if (item.ValueKind == JsonValueKind.Array)
                {
                    bool first = true;
                    foreach (var part in item.EnumerateArray())
                    {
                        if (first) { first = false; continue; }
                        if (part.ValueKind == JsonValueKind.Number && part.TryGetInt64(out long value)) these.Add(value);
                    }
                }
                if (these.Count > 0) any = true;
                args.Add(these);
            }
            return any ? args : Array.Empty<IReadOnlyList<long>>();
        }

        /// <summary>An opening, or a push's line: the message, per class, and the quest state it holds in.</summary>
        private static DialogueOpening ReadOpening(JsonElement entry)
        {
            var byBreed = new Dictionary<int, long>();
            if (entry.TryGetProperty("byBreed", out var breeds) && breeds.ValueKind == JsonValueKind.Object)
            {
                foreach (var breed in breeds.EnumerateObject())
                {
                    if (int.TryParse(breed.Name, out int id) && breed.Value.ValueKind == JsonValueKind.Number
                        && breed.Value.TryGetInt64(out long line))
                        byBreed[id] = line;
                }
            }

            return new DialogueOpening
            {
                Message = Number(entry, "message"),
                ByBreed = byBreed,
                Quest = (int)Number(entry, "quest"),
                Step = (int)Number(entry, "step"),
                Objective = (int)Number(entry, "objective"),
                Pending = (int)Number(entry, "pending"),
                Before = (int)Number(entry, "before"),
                After = (int)Number(entry, "after"),
                CanStart = (int)Number(entry, "canStart"),
                HoldsItem = (int)Number(entry, "holds"),
            };
        }

        private static DialogueOpening[] ReadOpenings(JsonElement entry)
        {
            if (!entry.TryGetProperty("openings", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<DialogueOpening>();
            var read = new List<DialogueOpening>();
            foreach (var item in list.EnumerateArray()) read.Add(ReadOpening(item));
            return read.ToArray();
        }

        private static DialoguePush[] ReadPushes(JsonElement entry)
        {
            if (!entry.TryGetProperty("pushes", out var list) || list.ValueKind != JsonValueKind.Array)
                return Array.Empty<DialoguePush>();
            var read = new List<DialoguePush>();
            foreach (var item in list.EnumerateArray())
            {
                int step = (int)Number(item, "afterStep");
                if (step == 0) continue;
                read.Add(new DialoguePush
                {
                    AfterStep = step,
                    Line = ReadOpening(item),
                    StagingsBefore = Numbers(item, "stagingsBefore").Select(o => (int)o).ToArray(),
                    Stagings = Numbers(item, "stagings").Select(o => (int)o).ToArray(),
                });
            }
            return read.ToArray();
        }

        private static void WriteOpening(Utf8JsonWriter writer, DialogueOpening opening, int afterStep = 0,
                                         IReadOnlyList<int>? stagingsBefore = null, IReadOnlyList<int>? stagings = null)
        {
            writer.WriteStartObject();
            if (afterStep != 0) writer.WriteNumber("afterStep", afterStep);
            if (opening.Message != 0) writer.WriteNumber("message", opening.Message);
            if (opening.Before != 0) writer.WriteNumber("before", opening.Before);
            if (opening.Quest != 0) writer.WriteNumber("quest", opening.Quest);
            if (opening.Step != 0) writer.WriteNumber("step", opening.Step);
            if (opening.Objective != 0) writer.WriteNumber("objective", opening.Objective);
            if (opening.Pending != 0) writer.WriteNumber("pending", opening.Pending);
            if (opening.After != 0) writer.WriteNumber("after", opening.After);
            if (opening.CanStart != 0) writer.WriteNumber("canStart", opening.CanStart);
            if (opening.HoldsItem != 0) writer.WriteNumber("holds", opening.HoldsItem);
            if (opening.ByBreed.Count > 0)
            {
                writer.WriteStartObject("byBreed");
                var breeds = new List<int>(opening.ByBreed.Keys);
                breeds.Sort();
                foreach (int breed in breeds) writer.WriteNumber(breed.ToString(), opening.ByBreed[breed]);
                writer.WriteEndObject();
            }
            WriteIds(writer, "stagingsBefore", stagingsBefore);
            WriteIds(writer, "stagings", stagings);
            writer.WriteEndObject();
        }

        private static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<int>? ids)
        {
            if (ids == null || ids.Count == 0) return;
            writer.WriteStartArray(name);
            foreach (int id in ids) writer.WriteNumberValue(id);
            writer.WriteEndArray();
        }

        public static ContentStore<NpcDialogueKey, NpcDialogue> Load(string? authoredPath,
                                                                     Action<string>? report = null)
        {
            var store = new ContentStore<NpcDialogueKey, NpcDialogue>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("dialogues", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    int npcId = (int)Number(entry, "npc");
                    if (npcId == 0) continue;

                    long mapId = Number(entry, "map");
                    var key = new NpcDialogueKey(npcId, mapId);

                    if (entry.TryGetProperty("remove", out var gone) && gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    store.Put(key, new NpcDialogue
                    {
                        NpcId = npcId,
                        MapId = mapId,
                        Opening = Number(entry, "opening"),
                        Openings = ReadOpenings(entry),
                        Pushes = ReadPushes(entry),
                        Lines = ReadLines(entry, report, npcId),
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        private static DialogueLine[] ReadLines(JsonElement entry, Action<string>? report, int npcId)
        {
            if (!entry.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DialogueLine>();
            }

            var read = new List<DialogueLine>();
            foreach (var line in lines.EnumerateArray())
            {
                long message = Number(line, "message");
                if (message == 0)
                {
                    report?.Invoke($"[Content] npc {npcId} has a dialogue line with no message; skipped.");
                    continue;
                }

                var choices = new List<DialogueChoice>();
                if (line.TryGetProperty("choices", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in list.EnumerateArray())
                    {
                        long reply = Number(choice, "reply");
                        if (reply == 0) continue;
                        choices.Add(new DialogueChoice
                        {
                            Reply = reply,
                            Next = Number(choice, "next"),
                            Quest = (int)Number(choice, "quest"),
                            Step = (int)Number(choice, "step"),
                            StartsQuest = (int)Number(choice, "startsQuest"),
                            AfterObjective = (int)Number(choice, "afterObjective"),
                            AfterObjectives = Numbers(choice, "afterObjectives").Select(o => (int)o).ToArray(),
                            BeforeObjective = (int)Number(choice, "beforeObjective"),
                            BeforeQuest = (int)Number(choice, "beforeQuest"),
                            RandomGroup = (int)Number(choice, "random"),
                            AfterElement = (int)Number(choice, "afterElement"),
                            BuysItem = (int)Number(choice, "buyItem"),
                            BuysCount = (int)Math.Max(1, Number(choice, "buyCount")),
                            BuysPrice = Number(choice, "buyPrice"),
                            TeleportsTo = Number(choice, "teleport"),
                            TeleportPassage = (int)Number(choice, "passage"),
                            Parameters = Numbers(choice, "params"),
                            ParameterArgs = ParameterArguments(choice),
                            DreamPointsPercent = (int)Number(choice, "dreamPointsPercent"),
                            DreamFavor = choice.TryGetProperty("dreamFavor", out var favor)
                                         && favor.ValueKind == JsonValueKind.True,
                            ReturnsHome = choice.TryGetProperty("teleportBack", out var back)
                                          && back.ValueKind == JsonValueKind.True,
                            AfterQuest = choice.TryGetProperty("afterQuest", out var after)
                                         && after.ValueKind == JsonValueKind.True,
                            KothRole = (int)Number(choice, "kothRole"),
                            RefusedNext = Number(choice, "refused"),
                            Chapeuvepes = (int)Number(choice, "chapeuvepes"),
                            Trades = (int)Number(choice, "trade"),
                            TradeCount = (int)Math.Max(1, Number(choice, "tradeCount")),
                            GivesMutation = (int)Number(choice, "mutation"),
                            ThenNpc = (int)Number(choice, "thenNpc"),
                            Completes = (int)Number(choice, "completes"),
                            ShowsNpc = (int)Number(choice, "showsNpc"),
                            AlsoShows = Numbers(choice, "alsoShows").Select(o => (int)o).ToArray(),
                            CompletesAll = Numbers(choice, "completesAll").Select(o => (int)o).ToArray(),
                            Gives = (int)Number(choice, "gives"),
                            MinLevel = (int)Number(choice, "minLevel"),
                            BelowLevel = (int)Number(choice, "belowLevel"),
                            Takes = (int)Number(choice, "takes"),
                            TakesCount = Math.Max(1, (int)Number(choice, "takesCount")),
                            TakesAll = Numbers(choice, "takesAll").Select(o => (int)o).ToArray(),
                            Holds = (int)Number(choice, "holds"),
                            HoldsCount = Math.Max(1, (int)Number(choice, "holdsCount")),
                            GivesCount = Math.Max(1, (int)Number(choice, "givesCount")),
                            EndsMutation = choice.TryGetProperty("endsMutation", out var ends)
                                           && ends.ValueKind == JsonValueKind.True,
                            Partial = choice.TryGetProperty("partial", out var partial)
                                      && partial.ValueKind == JsonValueKind.True,
                            Forfeits = (int)Number(choice, "forfeits"),
                            AlsoGives = Numbers(choice, "alsoGives").Select(o => (int)o).ToArray(),
                            GivesKamas = Number(choice, "givesKamas"),
                            Criterion = choice.TryGetProperty("criterion", out var crit) && crit.ValueKind == JsonValueKind.String
                                        ? crit.GetString() ?? "" : "",
                            Stagings = Numbers(choice, "stagings").Select(o => (int)o).ToArray(),
                        });
                    }
                }

                read.Add(new DialogueLine
                {
                    Message = message,
                    Choices = choices.ToArray(),
                    NamesTheCharacter = line.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.True,
                });
            }

            return read.ToArray();
        }

        /// <summary>
        /// The conversation for this NPC here: the one written for this map, or the one written for
        /// all of them, or nothing.
        /// </summary>
        public static NpcDialogue? For(ContentStore<NpcDialogueKey, NpcDialogue> store, int npcId, long mapId)
        {
            if (store.TryGet(new NpcDialogueKey(npcId, mapId), out var here)) return here.Value;
            return store.TryGet(new NpcDialogueKey(npcId, NpcDialogueKey.AnyMap), out var anywhere)
                ? anywhere.Value
                : null;
        }

        /// <summary>
        /// Writes the authored file back out, in a fixed order and through a temporary file.
        /// </summary>
        /// <remarks>
        /// Same reasoning as every other authored file: the order is fixed so that changing one
        /// line gives a one-line diff, and the write goes through a temporary file so that closing
        /// the editor mid-save cannot leave half a JSON file where the server will look for one.
        /// </remarks>
        public static void Save(string path, IEnumerable<NpcDialogue> dialogues,
                                IEnumerable<string>? comment = null)
        {
            var ordered = new List<NpcDialogue>(dialogues);
            ordered.Sort((a, b) =>
            {
                int byNpc = a.NpcId.CompareTo(b.NpcId);
                return byNpc != 0 ? byNpc : a.MapId.CompareTo(b.MapId);
            });

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("dialogues");
                writer.WriteStartArray();
                foreach (var dialogue in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("npc", dialogue.NpcId);
                    writer.WriteNumber("map", dialogue.MapId);
                    if (dialogue.Opening != 0) writer.WriteNumber("opening", dialogue.Opening);
                    if (dialogue.Openings.Count > 0)
                    {
                        writer.WriteStartArray("openings");
                        foreach (var opening in dialogue.Openings) WriteOpening(writer, opening);
                        writer.WriteEndArray();
                    }
                    if (dialogue.Pushes.Count > 0)
                    {
                        writer.WriteStartArray("pushes");
                        foreach (var push in dialogue.Pushes) WriteOpening(writer, push.Line, push.AfterStep, push.StagingsBefore, push.Stagings);
                        writer.WriteEndArray();
                    }

                    writer.WritePropertyName("lines");
                    writer.WriteStartArray();
                    foreach (var line in dialogue.Lines)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("message", line.Message);
                        if (line.NamesTheCharacter) writer.WriteBoolean("names", true);

                        writer.WritePropertyName("choices");
                        writer.WriteStartArray();
                        foreach (var choice in line.Choices)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("reply", choice.Reply);
                            if (!choice.Ends) writer.WriteNumber("next", choice.Next);
                            if (choice.Quest != 0) writer.WriteNumber("quest", choice.Quest);
                            if (choice.Step != 0) writer.WriteNumber("step", choice.Step);
                            if (choice.StartsQuest != 0) writer.WriteNumber("startsQuest", choice.StartsQuest);
                            if (choice.AfterQuest) writer.WriteBoolean("afterQuest", true);
                            if (choice.AfterObjective != 0)
                                writer.WriteNumber("afterObjective", choice.AfterObjective);
                            if (choice.AfterObjectives.Count > 0)
                            {
                                writer.WriteStartArray("afterObjectives");
                                foreach (int o in choice.AfterObjectives) writer.WriteNumberValue(o);
                                writer.WriteEndArray();
                            }
                            if (choice.BeforeObjective != 0)
                                writer.WriteNumber("beforeObjective", choice.BeforeObjective);
                            if (choice.BeforeQuest != 0)
                                writer.WriteNumber("beforeQuest", choice.BeforeQuest);
                            if (choice.RandomGroup != 0)
                                writer.WriteNumber("random", choice.RandomGroup);
                            if (choice.AfterElement != 0)
                                writer.WriteNumber("afterElement", choice.AfterElement);
                            if (choice.BuysItem != 0)
                            {
                                writer.WriteNumber("buyItem", choice.BuysItem);
                                if (choice.BuysCount != 1) writer.WriteNumber("buyCount", choice.BuysCount);
                                if (choice.BuysPrice != 0) writer.WriteNumber("buyPrice", choice.BuysPrice);
                            }
                            else if (choice.BuysPrice != 0)
                                writer.WriteNumber("buyPrice", choice.BuysPrice);
                            if (choice.TeleportsTo != 0)
                                writer.WriteNumber("teleport", choice.TeleportsTo);
                            if (choice.TeleportPassage != 0)
                                writer.WriteNumber("passage", choice.TeleportPassage);
                            if (choice.DreamPointsPercent != 0)
                                writer.WriteNumber("dreamPointsPercent", choice.DreamPointsPercent);
                            if (choice.DreamFavor) writer.WriteBoolean("dreamFavor", true);
                            if (choice.Parameters.Count > 0)
                            {
                                writer.WriteStartArray("params");
                                for (int i = 0; i < choice.Parameters.Count; i++)
                                {
                                    var args = i < choice.ParameterArgs.Count ? choice.ParameterArgs[i] : null;
                                    if (args == null || args.Count == 0)
                                    {
                                        writer.WriteNumberValue(choice.Parameters[i]);
                                        continue;
                                    }
                                    writer.WriteStartArray();
                                    writer.WriteNumberValue(choice.Parameters[i]);
                                    foreach (long arg in args) writer.WriteNumberValue(arg);
                                    writer.WriteEndArray();
                                }
                                writer.WriteEndArray();
                            }
                            if (choice.ReturnsHome) writer.WriteBoolean("teleportBack", true);
                            if (choice.KothRole != 0) writer.WriteNumber("kothRole", choice.KothRole);
                            if (choice.RefusedNext != 0) writer.WriteNumber("refused", choice.RefusedNext);
                            if (choice.Chapeuvepes != 0) writer.WriteNumber("chapeuvepes", choice.Chapeuvepes);
                            if (choice.Trades != 0)
                            {
                                writer.WriteNumber("trade", choice.Trades);
                                if (choice.TradeCount != 1) writer.WriteNumber("tradeCount", choice.TradeCount);
                            }
                            if (choice.GivesMutation != 0) writer.WriteNumber("mutation", choice.GivesMutation);
                            if (choice.ThenNpc != 0) writer.WriteNumber("thenNpc", choice.ThenNpc);
                            if (choice.Completes != 0) writer.WriteNumber("completes", choice.Completes);
                            if (choice.ShowsNpc != 0) writer.WriteNumber("showsNpc", choice.ShowsNpc);
                            if (choice.AlsoShows.Count > 0)
                            {
                                writer.WriteStartArray("alsoShows");
                                foreach (int npc in choice.AlsoShows) writer.WriteNumberValue(npc);
                                writer.WriteEndArray();
                            }
                            if (choice.CompletesAll.Count > 0)
                            {
                                writer.WriteStartArray("completesAll");
                                foreach (int o in choice.CompletesAll) writer.WriteNumberValue(o);
                                writer.WriteEndArray();
                            }
                            if (choice.Gives != 0) writer.WriteNumber("gives", choice.Gives);
                            if (choice.MinLevel != 0) writer.WriteNumber("minLevel", choice.MinLevel);
                            if (choice.BelowLevel != 0) writer.WriteNumber("belowLevel", choice.BelowLevel);
                            if (choice.Takes != 0) writer.WriteNumber("takes", choice.Takes);
                            if (choice.Takes != 0 && choice.TakesCount != 1) writer.WriteNumber("takesCount", choice.TakesCount);
                            if (choice.TakesAll.Count > 0)
                            {
                                writer.WriteStartArray("takesAll");
                                foreach (int o in choice.TakesAll) writer.WriteNumberValue(o);
                                writer.WriteEndArray();
                            }
                            if (choice.Holds != 0) writer.WriteNumber("holds", choice.Holds);
                            if (choice.Holds != 0 && choice.HoldsCount != 1) writer.WriteNumber("holdsCount", choice.HoldsCount);
                            if (choice.Gives != 0 && choice.GivesCount != 1) writer.WriteNumber("givesCount", choice.GivesCount);
                            if (choice.EndsMutation) writer.WriteBoolean("endsMutation", true);
                            if (choice.Partial) writer.WriteBoolean("partial", true);
                            if (choice.Forfeits != 0) writer.WriteNumber("forfeits", choice.Forfeits);
                            if (choice.AlsoGives.Count > 0)
                            {
                                writer.WriteStartArray("alsoGives");
                                foreach (int o in choice.AlsoGives) writer.WriteNumberValue(o);
                                writer.WriteEndArray();
                            }
                            if (choice.GivesKamas != 0) writer.WriteNumber("givesKamas", choice.GivesKamas);
                            if (choice.Criterion.Length > 0) writer.WriteString("criterion", choice.Criterion);
                            WriteIds(writer, "stagings", choice.Stagings);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            string temporary = path + ".writing";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>
        /// What is wrong with a dialogue, in words, before it is saved.
        /// </summary>
        /// <remarks>
        /// Checked rather than trusted because the failure is silent and remote: a reply pointing at
        /// a line that is not there leaves the player looking at a window with no way out, on
        /// somebody else's machine, days later. Cheap to check here, expensive to find there.
        /// </remarks>
        public static List<string> Complaints(NpcDialogue dialogue)
        {
            var wrong = new List<string>();
            var known = new HashSet<long>();

            foreach (var line in dialogue.Lines)
            {
                if (!known.Add(line.Message))
                {
                    wrong.Add($"message {line.Message} is written twice; only the first would be used.");
                }
            }

            if (dialogue.Opening != 0 && !known.Contains(dialogue.Opening))
            {
                wrong.Add($"it opens on message {dialogue.Opening}, which is not one of its lines.");
            }

            foreach (var line in dialogue.Lines)
            {
                // A line with no replies used to be a complaint, and it is not one any more. The
                // client draws its own "Leave" when the list is empty and that button does not
                // answer back — but the X does, with kla, and the server did not use to handle it.
                // It does now, so a line with nothing to say back is a dead end the player can
                // still walk out of.

                // The same reply twice under one line is a mistake, unless the earlier one belongs to
                // a quest: then it is that quest's way through the line, and the later one is the
                // rest of the world's (Tek Abir's «Aquí tienes los 40 kamas.» buys the beer anybody
                // asks for, and Bajofrente's own while «La cerveza compartida» wants it). Or unless
                // the earlier one is offered only to some: the guardian of the bridge of death's
                // right answers depend on the character's sex, attitudes and pods, and the same
                // words lead on for one and into the fight for another.
                var seen = new Dictionary<long, DialogueChoice>();
                foreach (var choice in line.Choices)
                {
                    if (seen.TryGetValue(choice.Reply, out var earlier) && earlier.Always && !earlier.Gated)
                    {
                        wrong.Add($"message {line.Message} offers reply {choice.Reply} twice.");
                    }
                    seen[choice.Reply] = choice;

                    if (!choice.Ends && !known.Contains(choice.Next))
                    {
                        wrong.Add($"reply {choice.Reply} leads to message {choice.Next}, which is " +
                                  "not one of its lines.");
                    }

                    if (choice.RefusedNext != 0 && !known.Contains(choice.RefusedNext))
                    {
                        wrong.Add($"reply {choice.Reply} is refused onto message {choice.RefusedNext}, which is " +
                                  "not one of its lines.");
                    }
                }
            }

            return wrong;
        }

        private static long Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt64(out long number) ? number : 0;

        private static readonly string[] DefaultComment =
        {
            "Which reply leads to which line. The one thing about an NPC the client has never held.",
            "",
            "The client ships every line an NPC can say and every reply it can be given, and",
            "nowhere does it say which goes with which - measured across all 6,467 of them. That",
            "pairing has always lived on Ankama's server, so it cannot be extracted or measured.",
            "It has to be decided, and this is where the decision goes.",
            "",
            "  npc      the NPC's id",
            "  map      0 for wherever it stands; a map id to say something different there",
            "  opening  which line it starts on; the first one when left out",
            "  lines    one per thing it says, each with what can be said back",
            "  next     the line that reply leads to; left out, the conversation ends",
            "",
            "Every line needs at least one reply. With an empty list the client draws its own",
            "Leave button, and that button never answers back: the window stays up and there is no",
            "way out but reconnecting.",
        };
    }
}
