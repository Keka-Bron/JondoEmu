using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Fight challenges, placement phase: offering them and letting the player choose.
    ///
    /// ─── The script, measured over 305 captures ─────────────────────────────────────────────
    ///
    /// With the timeline of BOTH directions put together, which is what it took: the captures are
    /// read per connection, and without merging the two sides again by time it cannot be seen who
    /// answers whom and everything seems to arrive loose.
    ///
    ///   kxa   S→C  how many have to be chosen. It arrives TWICE, with the same number: once on
    ///              entering placement and again after the cells
    ///   kwo   C→S  panel setting               →  kwn  S→C  with the same value
    ///   kwr   C→S  open the selector (empty)   →  kwx  S→C  THE LIST, always two candidates
    ///   kwv   C→S  mark one
    ///   kwi   C→S  hover. No answer
    ///   kwj   C→S  validate                    →  kww  S→C  the challenge is LOCKED IN
    ///   kaq   C→S  ready                       →  kah, and there go the missing kww
    ///   kai   S→C  placement is over
    ///   kwu   S→C  the final list, next to the jyy
    ///
    /// ─── Two traps that took understanding the trace ────────────────────────────────────────
    ///
    /// The first: the FIRST <c>kwv</c> is not a click. It arrives on its own, between two and
    /// thirty milliseconds after the list, and always with the first candidate's id: it is the
    /// client marking one by itself. Nine times out of nine. If it were taken for the player's
    /// choice, the challenge would be locked in without anybody having touched it.
    ///
    /// The second: the two candidates are ALTERNATIVES, not a compatible pair. In the captures two
    /// challenges that the client's own table marks as incompatible were offered together.
    /// Incompatibility rules among the ones already LOCKED IN, not among those on the table.
    ///
    /// ─── What is not here ───────────────────────────────────────────────────────────────────
    ///
    /// Checking during the fight whether the challenge is met, and applying the percentage on
    /// winning. This is only placement: challenges are chosen, locked in and travel, but they do
    /// not watch anything yet. The result message is <c>kwl</c> and it is measured -- { f1: which,
    /// f2: met }, and without f2 it is failed --, but nobody sends it yet.
    /// </summary>
    public static class ChallengeHandler
    {
        /// <summary>How many are chosen in a normal fight. In a dungeon it is two.</summary>
        private const int NormalCount = 1;

        private static readonly Random _dado = new Random();

        /// <summary>
        /// The group's level, which is what decides whether a challenge can be offered: the sum of the
        /// monsters' levels. That explains why none comes up against a poutch, which is what happens in
        /// the four poutch captures: not one kxa, not one kwx.
        /// </summary>
        private static int GroupLevel(FightInstance fight)
        {
            int total = 0;
            foreach (var bicho in fight.Rojo) total += bicho.Level;
            return total;
        }

        /// <summary>Are there challenges to offer in this fight?</summary>
        public static bool Any(FightInstance fight)
            => Challenges.Pair(GroupLevel(fight), NoneFixed, _dado).Count > 0;

        private static readonly int[] NoneFixed = Array.Empty<int>();

        /// <summary>
        /// How many have to be chosen (kxa). The real server sends it TWICE with the same number,
        /// so this is called twice from placement.
        /// </summary>
        public static async Task SendCountAsync(NetworkStream stream, FightInstance fight,
                                                bool primeraVez = false)
        {
            if (!Any(fight)) return;

            fight.ChallengesToPick = NormalCount;
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kxa,
                Network.FightProtocol.BuildChallengeCount(fight.ChallengesToPick)));

            // And right after the FIRST kxa, an empty kwk. It shows up six times in the captures, always
            // without a payload and always in this same spot, between the first kxa and the first jxg.
            // What it says is unknown -- it is empty, there is nothing to read --, but it is the only thing
            // the real server sends there and it was missing here.
            if (primeraVez) await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwk));
        }

        /// <summary>
        /// Sending the pair of candidates (kwx).
        ///
        /// The pair is DRAWN ONCE and kept. It used to be drawn again on every call, so if the player
        /// opened the selector with two challenges already on screen, they were swapped for two others
        /// in front of his eyes. The real server keeps the one that was not chosen.
        /// </summary>
        public static async Task OpenAsync(NetworkStream stream, FightInstance fight)
        {
            if (!fight.ChallengesPending) return;

            // If there is already a pair on the table, the same one is sent again.
            var lista = new List<byte[]>();
            var nombres = new List<Challenges.Challenge>();

            if (fight.ChallengesOffered.Count > 0)
            {
                foreach (int id in fight.ChallengesOffered)
                {
                    var reto = Challenges.Get(id);
                    if (reto == null) continue;
                    nombres.Add(reto);
                    lista.Add(Network.FightProtocol.BuildChallenge(reto.Id, reto.Percent));
                }
            }
            else
            {
                var pareja = Challenges.Pair(GroupLevel(fight), FixedIds(fight), _dado);
                if (pareja.Count == 0) return;

                fight.ChallengeMarked = 0;
                foreach (var reto in pareja)
                {
                    fight.ChallengesOffered.Add(reto.Id);
                    nombres.Add(reto);
                    lista.Add(Network.FightProtocol.BuildChallenge(reto.Id, reto.Percent));
                }
            }

            if (lista.Count == 0) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwx,
                Network.FightProtocol.BuildChallengeList(lista)));

            Console.WriteLine($"[Retos] Se ofrecen {Names(nombres)} en el combate #{fight.FightId}.");
        }

        /// <summary>
        /// The player marks a candidate (kwv). Nothing is answered: in the captures the server stays
        /// silent. Only which one is noted, to know what to lock in when he validates.
        /// </summary>
        public static void Mark(FightInstance fight, byte[] payload)
        {
            byte[]? kwv = ConnectionProtocol.ReadPayload(payload, Op.Kwv);
            if (kwv == null) return;

            int id = (int)VarField(kwv, 1);
            if (id != 0 && fight.ChallengesOffered.Contains(id)) fight.ChallengeMarked = id;
        }

        /// <summary>
        /// The player validates (kwj): the challenge is locked in and he is answered with the kww. If
        /// there are still challenges to choose, another list follows with the next pair, which is
        /// exactly what the real server does in a dungeon.
        /// </summary>
        public static async Task ValidateAsync(NetworkStream stream, FightInstance fight, byte[] payload)
        {
            byte[]? kwj = ConnectionProtocol.ReadPayload(payload, Op.Kwj);
            if (kwj == null) return;

            int id = (int)VarField(kwj, 1);
            if (id == 0) id = fight.ChallengeMarked;
            if (id == 0 || !fight.ChallengesOffered.Contains(id)) return;

            await FixAsync(stream, fight, id);

            if (fight.ChallengesPending) await OpenAsync(stream, fight);
        }

        /// <summary>
        /// The panel setting (kwo): it is sent back as it came in a kwn, and the list goes AFTER it.
        ///
        /// The order matters and is measured: in the twelve times the real server sends the list of
        /// candidates, all twelve go AFTER the client's kwo -- including the two that arrive without
        /// anybody asking, in the automatic entry capture. The emulator sent it at the end of
        /// placement, before the client had said anything about its panel.
        /// </summary>
        public static async Task SettingsAsync(NetworkStream stream, FightInstance? fight, byte[] payload)
        {
            byte[]? kwo = ConnectionProtocol.ReadPayload(payload, Op.Kwo);
            if (kwo == null) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwn,
                Network.FightProtocol.BuildChallengeSettings(VarField(kwo, 1))));

            if (fight != null) await OpenAsync(stream, fight);
        }

        /// <summary>
        /// The player has pressed ready and there are challenges left unchosen: the server fills them
        /// in by itself. It is measured in the anomaly, where the player chose one of the two and the
        /// server sent the missing one without ever having offered it.
        ///
        /// It goes BEFORE the kai, which is the cut between placement and the fight.
        /// </summary>
        public static async Task FillAsync(NetworkStream stream, FightInstance fight)
        {
            // Whatever was marked without being validated counts: the player chose it, he just did not
            // get to press the button before declaring himself ready.
            if (fight.ChallengeMarked != 0 && fight.ChallengesPending)
            {
                await FixAsync(stream, fight, fight.ChallengeMarked);
            }

            while (fight.ChallengesPending)
            {
                var pareja = Challenges.Pair(GroupLevel(fight), FixedIds(fight), _dado);
                if (pareja.Count == 0) break;
                await FixAsync(stream, fight, pareja[0].Id);
            }

            await ImposeAsync(stream, fight);
        }

        /// <summary>
        /// And after them, the ones the CONTENT imposes, which are something else.
        ///
        /// In a dungeon or an anomaly it is not only the two normal challenges: one to three
        /// challenges of that place come on top, the ones with an achievement behind them. They are
        /// neither proposed nor chosen -- the player does not see them in the selector -- and they arrive
        /// with the bonus at ZERO.
        ///
        /// It is measured in the anomaly: after the two normal ones three more kww arrived, 772, 773
        /// and 774, which had never been offered, all three without a percentage, and all three
        /// requiring monster 5781, which was that anomaly's.
        ///
        /// Since they carry an achievement, they are done once: a character who has already completed
        /// them does not get them again. And only the ones the watcher can judge and that fit the party
        /// there is -- no "Dúo" for three -- are imposed: <see cref="Challenges.Imposed"/> decides.
        /// </summary>
        private static async Task ImposeAsync(NetworkStream stream, FightInstance fight)
        {
            // Only in the boss room. These are the challenges with an achievement behind them, the reward
            // for having done the whole dungeon, and they came out in every room because the only thing
            // looked at was whether the fight had monsters with a challenge. Coming out in the fourth of
            // five also misleads: the player reads them as «this is the last one» and stops advancing.
            //
            // A fight outside a dungeon does not get here -- IsBossRoom answers no when the map is no
            // dungeon's room --, which is what already happened before by another road.
            //
            // The fight's ROLEPLAY map is asked about, not the session's: on entering a fight the
            // session moves to the arena's map, which is no dungeon's room, and with that one the
            // answer was always no. Not one imposed challenge came out since.
            if (!DungeonHandler.IsBossRoom(fight.RoleplayMapId)) return;

            var bichos = new List<int>();
            foreach (var uno in fight.Rojo)
            {
                if (uno.IsMonster && uno.MonsterId != 0) bichos.Add(uno.MonsterId);
            }
            if (bichos.Count == 0) return;

            int players = 0, monsterCount = 0;
            foreach (var uno in fight.Azul) if (!uno.EsInvocado) players++;
            foreach (var uno in fight.Rojo) if (!uno.EsInvocado) monsterCount++;

            var cumplidos = DatabaseManager.LoadChallengesDone(GameState.CharacterId);
            var puestos = Challenges.Imposed(bichos, cumplidos, players, monsterCount);
            if (puestos.Count == 0) return;

            var alreadyFixed = FixedIds(fight);
            foreach (var reto in puestos)
            {
                // One fight, one list: the party's second player is sent the same challenge, but
                // it is not written down twice.
                if (Array.IndexOf(alreadyFixed, reto.Id) < 0) fight.ChallengesFixed.Add((reto.Id, 0));
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kww,
                    Network.FightProtocol.BuildChallengeChosen(
                        Network.FightProtocol.BuildChallenge(reto.Id, 0))));
            }

            Console.WriteLine($"[Retos] El sitio impone {puestos.Count} reto(s) más en el " +
                              $"combate #{fight.FightId}.");
        }

        /// <summary>The final list (kwu). It goes between the kai and the jyy.</summary>
        public static async Task SendFinalListAsync(NetworkStream stream, FightInstance fight)
        {
            if (fight.ChallengesFixed.Count == 0) return;

            var lista = new List<byte[]>();
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                lista.Add(Network.FightProtocol.BuildChallenge(id, percent));
            }

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwu,
                Network.FightProtocol.BuildChallengeFinalList(lista)));

            Console.WriteLine($"[Retos] El combate #{fight.FightId} se pelea con " +
                              $"{Fixed(fight)}.");
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        private static async Task FixAsync(NetworkStream stream, FightInstance fight, int id)
        {
            var reto = Challenges.Get(id);
            if (reto == null) return;

            fight.ChallengesFixed.Add((id, reto.Percent));
            fight.ChallengesOffered.Clear();
            fight.ChallengeMarked = 0;

            byte[] ldd = Network.FightProtocol.BuildChallenge(id, reto.Percent);
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kww,
                Network.FightProtocol.BuildChallengeChosen(ldd)));
        }

        private static int[] FixedIds(FightInstance fight)
        {
            var ids = new int[fight.ChallengesFixed.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = fight.ChallengesFixed[i].Id;
            return ids;
        }

        private static string Names(IReadOnlyList<Challenges.Challenge> retos)
        {
            var trozos = new List<string>();
            foreach (var reto in retos) trozos.Add($"«{reto.Name}» al {reto.Percent} %");
            return string.Join(" o ", trozos);
        }

        private static string Fixed(FightInstance fight)
        {
            var trozos = new List<string>();
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                trozos.Add($"«{Challenges.Get(id)?.Name ?? id.ToString()}» al {percent} %");
            }
            return string.Join(" y ", trozos);
        }

        private static long VarField(byte[] payload, int number)
        {
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
        }
    }
}
