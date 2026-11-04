using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;
using Microsoft.Data.Sqlite;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The day's offering, as Ontoral Zo runs it: his conversation, the altar of the day's room and
    /// the saint, in the order his own lines tell them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// INFERRED: no capture sets foot in the sanctuary. Every line and reply is Ontoral's own (NPC
    /// 1625's template), and the order is the one they say out loud: «Decirle que estás listo para
    /// celebrar el día de los santos», «Imagino que sabes a qué santo le rezamos hoy», the saint
    /// named, «¡Sí, es ella! Kacinka… Si deseas hablar con el espíritu de Kacinka, debes hacerle una
    /// ofrenda», «Decirle que estás dispuesto a hacer una ofrenda»; then «Entregarle la ofrenda que
    /// te pidió» -- «Haré esta ofrenda en tu nombre, #1. Ahora puedes rezar ante el altar que se
    /// encuentra en la sala de las estaciones. Si tu devoción es sincera, el espíritu que guarda este
    /// día se manifestará. Ven a verme cuando hayas hablado con él.» --, and «Responderle que has
    /// hablado con el espíritu del día» for the reward. Dofus pour les noobs tells «L'année du
    /// dragon» the same way: taken by saying one is there to honour the saints, finished by showing
    /// the 365 pages.
    /// </para>
    /// <para>
    /// The day's quest changes every day, so this is not a written tree: what Ontoral offers is the
    /// character's state of the day's offering. Before this any reply of his took the offering, and
    /// his opening and farewell were all there was to take it and to finish it.
    /// </para>
    /// <para>
    /// The offering's four objectives go in their order -- bring it, pray, see the saint, come back
    /// (types 3, 0, 1, 9) -- and none of them is closed by a mere hello any more
    /// (<see cref="QuestEngine.OnTalkingToAsync"/> leaves them to this).
    /// </para>
    /// </remarks>
    public static class AlmanaxOfferingHandler
    {
        // ─── Ontoral's lines ──────────────────────────────────────────────────────────────────

        public const long Welcome = 12047, TooYoung = 12048, WhichSaint = 12049, OfferingMade = 12055,
                          Rewarded = 12056, Listening = 12057, AboutSaintsLine = 12058,
                          AboutAlmanichasLine = 12059, AboutAlmanaxLine = 12060, HimselfLine = 12050,
                          TooMany = 54732, Proud = 16040, DolmanaxGiven = 16041;

        // ─── And his replies ──────────────────────────────────────────────────────────────────

        public const long ReadyToCelebrate = 13557, WillingToOffer = 13551, HandTheOffering = 13563,
                          SpokeWithTheSpirit = 13564, AskQuestions = 13569, ShowThePages = 18971,
                          GoAndPray = 13562, AboutSaints = 13568, AboutAlmanichas = 13572,
                          AboutAlmanax = 13577, Introduce = 13556, Accept = 14636, MissionOver = 18970,
                          Goodbye = 13565, NoThanks = 14901;

        /// <summary>The objective types of an offering beside the item and the prayer: see the saint, come back.</summary>
        private const int SeeSomebody = 1, GoBackTo = 9;

        private static readonly long[] Questions = { AboutSaints, AboutAlmanichas, AboutAlmanax, Introduce };

        private static readonly Dictionary<long, long> Answers = new()
        {
            [AboutSaints] = AboutSaintsLine, [AboutAlmanichas] = AboutAlmanichasLine,
            [AboutAlmanax] = AboutAlmanaxLine, [Introduce] = HimselfLine,
        };

        private static readonly (long, long[], string[]) Close = (0, Array.Empty<long>(), Array.Empty<string>());

        public static bool Owns(int npc) => npc != 0 && npc == Almanax.Giver;

        private static string Me => SessionContext.State.CharacterName ?? "";

        // ─── What the day's offering is at ────────────────────────────────────────────────────

        /// <summary>The day's offering quest's four objectives, in their order; null when there is none today.</summary>
        internal sealed record Offering(int Quest, QuestObjective Bring, QuestObjective? Prayer,
                                        QuestObjective? Saint, QuestObjective? Back);

        /// <summary>The day's offering, the one to take.</summary>
        internal static Offering? Today() => OfferingOf(Almanax.TodaysOffering());

        /// <summary>The offering in hand, of whichever day it was taken; else the day's.</summary>
        internal static Offering? Current()
        {
            var log = QuestEngine.Log;
            if (log != null)
                foreach (var run in log.Doing())
                    if (Almanax.IsOffering(run.QuestId) && OfferingOf(run.QuestId) is { } taken) return taken;
            return Today();
        }

        internal static Offering? OfferingOf(int quest)
        {
            var step = QuestEngine.Book?.Of(quest)?.Steps.FirstOrDefault();
            if (step == null) return null;
            var bring = step.Objectives.FirstOrDefault(o => o.ConsumesItems && o.NpcId == Almanax.Giver);
            if (bring == null) return null;
            return new Offering(quest, bring,
                step.Objectives.FirstOrDefault(o => o.TypeId == QuestCatalogue.FreeText),
                step.Objectives.FirstOrDefault(o => o.TypeId == SeeSomebody),
                step.Objectives.FirstOrDefault(o => o.TypeId == GoBackTo && o.NpcId == Almanax.Giver));
        }

        private static QuestRun? RunOf(int quest)
        {
            var run = QuestEngine.Log?.Run(quest);
            return run is { Finished: false } ? run : null;
        }

        private static bool Done(QuestRun run, QuestObjective? objective)
            => objective == null || run.Done.Contains(objective.Id);

        /// <summary>Whether the character may pray at an altar now: the offering made, the prayer still to say.</summary>
        internal static bool MayPray()
        {
            var today = Current();
            var run = today == null ? null : RunOf(today.Quest);
            return run != null && today!.Prayer != null && Done(run, today.Bring) && !Done(run, today.Prayer);
        }

        /// <summary>
        /// Whether «Ve a ver a …» may close on talking to the saint: the prayer said. «Si tu devoción es
        /// sincera, el espíritu que guarda este día se manifestará»: before it, nobody has shown up.
        /// </summary>
        internal static bool MaySeeTheSaint(int questId)
        {
            var today = OfferingOf(questId);
            if (today == null) return false;
            var run = RunOf(questId);
            return run != null && Done(run, today.Bring) && Done(run, today.Prayer);
        }

        // ─── The conversation ─────────────────────────────────────────────────────────────────

        /// <summary>What Ontoral opens with for the character in session.</summary>
        public static (long Question, long[] Replies, string[] Parameters) Opening()
        {
            var state = SessionContext.State;
            var replies = new List<long>();

            var today = Current();
            var run = today == null ? null : RunOf(today.Quest);

            if (DragonReady()) replies.Add(ShowThePages);

            if (run != null)
            {
                if (!Done(run, today!.Bring) && Equipment.HowMany(today.Bring.ItemId) >= Math.Max(1, today.Bring.ItemCount))
                    replies.Add(HandTheOffering);
                if (Done(run, today.Bring) && Done(run, today.Prayer) && Done(run, today.Saint) && !Done(run, today.Back))
                    replies.Add(SpokeWithTheSpirit);
            }
            else if (today != null && QuestEngine.MayTake(today.Quest))
            {
                replies.Add(ReadyToCelebrate);
            }
            else if (today != null && QuestEngine.Log?.Run(today.Quest) == null && TooYoungFor(today.Quest, state.CharacterLevel))
            {
                return (TooYoung, new[] { Goodbye }, Array.Empty<string>());
            }

            replies.Add(AskQuestions);
            replies.Add(NoThanks);
            return (Welcome, replies.ToArray(), Array.Empty<string>());
        }

        private static bool TooYoungFor(int quest, int level)
            => QuestEngine.Book?.Of(quest) is { } q && q.LevelMin > 0 && level < q.LevelMin;

        /// <summary>A reply: the next line, its replies and the texts it fills in; a line of zero closes.</summary>
        public static async Task<(long Question, long[] Replies, string[] Parameters)> ReplyAsync(NetworkStream stream, long reply)
        {
            var today = Today();
            var current = Current();

            if (reply == ReadyToCelebrate && today != null && QuestEngine.MayTake(today.Quest))
            {
                var lines = SaintLinesOf(Almanax.Today()?.Saint ?? 0);
                // No line of his names today's saint: the offering is taken on the spot.
                if (lines == null) return await TakeAsync(stream, today);
                return (WhichSaint, new[] { lines.Guess, Goodbye }, Array.Empty<string>());
            }

            var saint = SaintLinesOf(Almanax.Today()?.Saint ?? 0);
            if (saint != null && today != null && QuestEngine.MayTake(today.Quest))
            {
                if (reply == saint.Guess)
                {
                    var next = new List<long> { WillingToOffer };
                    if (saint.Ask != 0) next.Add(saint.Ask);
                    next.Add(Goodbye);
                    return (saint.Confirm, next.ToArray(), Array.Empty<string>());
                }
                if (reply == saint.Ask && saint.Ask != 0)
                    return (saint.Offering, new[] { WillingToOffer, Goodbye }, Array.Empty<string>());
            }

            if (reply == WillingToOffer && today != null && QuestEngine.MayTake(today.Quest))
                return await TakeAsync(stream, today);

            if (reply == HandTheOffering && current != null && RunOf(current.Quest) is { } run && !Done(run, current.Bring))
            {
                int wanted = Math.Max(1, current.Bring.ItemCount), carried = Equipment.HowMany(current.Bring.ItemId);
                if (carried < wanted) return Close;
                // «Retira los que quieras conservar y luego vuelve a verme con la cantidad necesaria
                // -ni más, ni menos-»: more than asked, and he does not know which to take.
                if (carried > wanted) return (TooMany, new[] { Goodbye }, Array.Empty<string>());
                if (!await Equipment.TakeAsync(stream, current.Bring.ItemId, wanted)) return Close;
                await QuestEngine.TickAsync(stream, current.Quest, current.Bring.Id);
                Console.WriteLine($"[Almanax] {Me} makes the offering of quest {current.Quest}.");
                return (OfferingMade, new[] { GoAndPray }, new[] { Me });
            }

            if (reply == SpokeWithTheSpirit && current?.Back != null && RunOf(current.Quest) is { } back
                && Done(back, current.Bring) && Done(back, current.Prayer) && Done(back, current.Saint) && !Done(back, current.Back))
            {
                await QuestEngine.TickAsync(stream, current.Quest, current.Back.Id);
                return (Rewarded, new[] { Goodbye }, Array.Empty<string>());
            }

            if (reply == ShowThePages && DragonReady()) return (Proud, new[] { Accept }, new[] { Me });
            if (reply == Accept && DragonReady())
            {
                var dragon = Almanax.Dragon;
                var objective = QuestEngine.Book?.Of(dragon.Quest)?.Steps.SelectMany(s => s.Objectives)
                                           .FirstOrDefault(o => o.ConsumesItems && o.ItemId == dragon.Page);
                if (objective == null || !await Equipment.TakeAsync(stream, dragon.Page, dragon.Pages)) return Close;
                await QuestEngine.TickAsync(stream, dragon.Quest, objective.Id);
                return (DolmanaxGiven, new[] { MissionOver }, Array.Empty<string>());
            }

            if (reply == AskQuestions) return (Listening, Questions.Append(Goodbye).ToArray(), Array.Empty<string>());
            if (Answers.TryGetValue(reply, out long answer))
                return (answer, Questions.Where(q => q != reply).Append(Goodbye).ToArray(), Array.Empty<string>());

            return Close;
        }

        /// <summary>The day's offering taken, and «El año del dragón» with the first one: «Si deseas hablar con el espíritu…, debes hacerle una ofrenda».</summary>
        private static async Task<(long, long[], string[])> TakeAsync(NetworkStream stream, Offering today)
        {
            if (!await QuestEngine.StartAsync(stream, today.Quest)) return Close;

            int dragon = Almanax.Dragon.Quest;
            if (dragon != 0 && QuestEngine.Log?.Run(dragon) == null) await QuestEngine.StartAsync(stream, dragon);

            // And what the saint wants, in his words, if a line of his says it.
            var saint = SaintLinesOf(Almanax.Today()?.Saint ?? 0);
            return saint == null ? Close : (saint.Offering, new[] { Goodbye }, Array.Empty<string>());
        }

        /// <summary>«El año del dragón» in hand and its 365 pages carried.</summary>
        private static bool DragonReady()
        {
            var dragon = Almanax.Dragon;
            return dragon.Quest != 0 && RunOf(dragon.Quest) != null && Equipment.HowMany(dragon.Page) >= dragon.Pages;
        }

        // ─── The altar ────────────────────────────────────────────────────────────────────────

        /// <summary>The day's room's altar, if this is the day's room: the element the prayer is said at.</summary>
        internal static int AltarOn(long mapId)
            => mapId == AlmanaxTemple.RoomOf(Almanax.Clock()) ? AlmanaxTemple.AltarOf(mapId) : 0;

        /// <summary>
        /// The altar used: «Reza ante el altar de …» said, when the offering has been made. True when
        /// it was the altar, whatever came of it.
        /// </summary>
        public static async Task<bool> OnAltarUsedAsync(NetworkStream stream, long mapId, int elementId)
        {
            int altar = AltarOn(mapId);
            if (altar == 0 || elementId != altar) return false;

            var today = Current();
            if (today?.Prayer == null || !MayPray()) return true;

            await QuestEngine.TickAsync(stream, today.Quest, today.Prayer.Id);
            Console.WriteLine($"[Almanax] {Me} prays at the altar of map {mapId}.");
            return true;
        }

        // ─── Which of Ontoral's lines are the day's saint's ───────────────────────────────────

        /// <summary>
        /// A saint's lines in Ontoral's mouth: the reply naming him, the question about his
        /// offering, the line that says «¡Sí, es él!» and the one right behind it that says what he
        /// wants.
        /// </summary>
        /// <remarks>
        /// The client keeps them as ids with no saint attached, so they are found by the saint's
        /// name, as the dungeon guardians' key replies are found by their wording: the reply that
        /// ends «… se trata de… Kacinka.», the question with his name and «ofrenda», and the line
        /// that names him first. In the client's data the line saying what a saint wants always
        /// comes right after the one that names him (12721 «¡Sí, es ella! Kacinka…», 12722 «La santa
        /// quiere bajar la tasa de criminalidad… fibra criminal…»). Read off the 373 saints of the
        /// calendar, 371 come out whole; the other two, Bryss and Zeg, have no reply or no line,
        /// and their offering is taken straight away.
        /// </remarks>
        internal sealed record SaintLines(long Guess, long Ask, long Confirm, long Offering);

        private static readonly Dictionary<int, SaintLines?> _saintLines = new();
        private static readonly object _gate = new();
        private static Dictionary<long, string>? _ontoralTexts;

        internal static SaintLines? SaintLinesOf(int saint)
        {
            if (saint == 0) return null;
            lock (_gate)
            {
                if (_saintLines.TryGetValue(saint, out var known)) return known;
                var found = Find(saint);
                _saintLines[saint] = found;
                return found;
            }
        }

        private static SaintLines? Find(int saint)
        {
            var ontoral = Npcs.TemplateOf(Almanax.Giver);
            string name = NameOf(saint);
            if (ontoral == null || name.Length == 0) return null;
            var texts = _ontoralTexts ??= Texts(ontoral.ReplyTexts.Concat(ontoral.MessageTexts));

            var word = new Regex(@"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}])");
            string ReplyText(int i) => i < ontoral.ReplyTexts.Length && texts.TryGetValue(ontoral.ReplyTexts[i], out var t) ? t.Trim() : "";
            string LineText(int i) => i < ontoral.MessageTexts.Length && texts.TryGetValue(ontoral.MessageTexts[i], out var t) ? t : "";

            long guess = 0, ask = 0;
            for (int i = 0; i < ontoral.Replies.Length; i++)
            {
                string text = ReplyText(i);
                if (guess == 0 && text.Contains("...") && text.EndsWith(" " + name + ".", StringComparison.Ordinal)) guess = ontoral.Replies[i];
                if (ask == 0 && text.EndsWith("?", StringComparison.Ordinal) && text.Contains("ofrenda") && word.IsMatch(text)) ask = ontoral.Replies[i];
            }

            var lines = new HashSet<long>(ontoral.Messages);
            long confirm = 0;
            int earliest = int.MaxValue;
            for (int i = 0; i < ontoral.Messages.Length; i++)
            {
                var hit = word.Match(LineText(i));
                if (!hit.Success || !lines.Contains(ontoral.Messages[i] + 1) || hit.Index >= earliest) continue;
                earliest = hit.Index;
                confirm = ontoral.Messages[i];
            }

            return guess == 0 || confirm == 0 ? null : new SaintLines(guess, ask, confirm, confirm + 1);
        }

        private static string NameOf(int npc)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT t.Text FROM NpcTemplates n JOIN Translations t ON t.Key = n.NameId WHERE n.Id = $id;";
                command.Parameters.AddWithValue("$id", npc);
                return command.ExecuteScalar() as string ?? "";
            }
            catch (SqliteException) { return ""; }
        }

        private static Dictionary<long, string> Texts(IEnumerable<long> keys)
        {
            var texts = new Dictionary<long, string>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                foreach (var chunk in keys.Distinct().Chunk(500))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT Key, Text FROM Translations WHERE Key IN ({string.Join(",", chunk)});";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) texts[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
            catch (SqliteException) { }
            return texts;
        }

        /// <summary>Forgets what was read, for a test that swaps the calendar or the templates.</summary>
        internal static void Reset()
        {
            lock (_gate)
            {
                _saintLines.Clear();
                _ontoralTexts = null;
            }
        }
    }
}
