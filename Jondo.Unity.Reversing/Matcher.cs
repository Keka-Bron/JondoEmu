using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Reversing;

/// <summary>
/// Matching one version's protocol with the next one's, when the names have changed.
///
/// The problem, put short: Ankama rotates the three-letter names on every patch. Today's jsd
/// will be called something else tomorrow, and there are two thousand messages. Matching by hand is unfeasible.
///
/// ─── Why looking at each message is not enough ──────────────────────────────────────────
///
/// A message of { int64 f2 } is identical to three hundred others. Comparing messages one by one,
/// the big ones match on their own and the small ones are impossible: it is the conclusion
/// the Cadernis thread reaches, and it is correct AS LONG AS they are looked at in isolation.
///
/// But a message is not isolated: it is a node of a graph. What identifies an { int64 } is not
/// its shape, it is WHO POINTS TO IT. If it only appears as field 7 of a huge message already
/// matched with certainty, it is determined even though inside it has nothing distinctive.
///
/// ─── How ────────────────────────────────────────────────────────────────────────────────
///
/// Refinement by rounds, which is what is done to compare graphs:
///
///   round 0   a message's fingerprint is its fields: number, type and whether it is a list. Of those
///             pointing to another message only that they point is noted, not to whom.
///   round k   the fingerprint becomes the previous round's PLUS the fingerprints of those it
///             points to. That way the neighbours' information spreads.
///
/// After a few rounds, two messages have the same fingerprint only if they have the same shape AND the
/// same neighbourhood up to that distance. The ones left alone with their fingerprint in both versions
/// are matched without hesitation.
///
/// Then it is propagated through the fields: if a and b are matched and their field 7 points to ta and tb,
/// then ta and tb are the same message. That drags the small ones along, which is where the
/// problem was.
/// </summary>
public static class Matcher
{
    public sealed record Model(List<ProtoWriter.Message> Messages, List<ProtoWriter.Enumeration> Enums);

    public sealed record Result(
        Dictionary<string, string> Pairs,
        List<string> Ambiguous,
        List<string> Alone,
        Dictionary<string, List<string>> Candidates);

    /// <summary>Matches the messages of the two versions.</summary>
    public static Result Match(Model a, Model b, int rounds = 5)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        var takenB = new HashSet<string>(StringComparer.Ordinal);

        var signA = Signatures(a, rounds);
        var signB = Signatures(b, rounds);

        // ─── The seeds: a unique fingerprint on both sides ──────────────────────────────
        //
        // It goes from the deepest round to the shallowest: the more neighbourhood a fingerprint carries
        // inside, the less of a coincidence it is that it matches.
        for (int round = rounds; round >= 0; round--)
        {
            var byA = Group(signA, round);
            var byB = Group(signB, round);

            foreach (var (fingerprint, ones) in byA)
            {
                if (ones.Count != 1) continue;
                if (!byB.TryGetValue(fingerprint, out var others) || others.Count != 1) continue;

                string from = ones[0], to = others[0];
                if (pairs.ContainsKey(from) || takenB.Contains(to)) continue;

                pairs[from] = to;
                takenB.Add(to);
            }
        }

        // ─── The watering: resemblance, not equality ────────────────────────────────────
        //
        // The seeds above require the fingerprint to match EXACTLY, and that only happens when
        // the message has not changed at all between versions. Between 3.6.4.3 and 3.6.10.10 half have
        // changed —a new field here, a changed type there— and with exact equality a measly
        // 5% was matched.
        //
        // So from the seeds on it is watered: each message without a pair is scored against the
        // candidates that resemble it, adding two things —how much they resemble each other inside and how many
        // of their neighbours are already matched with each other— and the best is accepted if it beats the
        // second by a margin. Each round produces new pairs that improve the next one's score,
        // until it stops moving.
        var messagesA = a.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var messagesB = b.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);

        for (int round = 0; round < 12; round++)
        {
            // ─── Dragging through the parents ───────────────────────────────────────────
            //
            // This is the part that really moves the needle, and the one that was missing. If a and b are the
            // same message and both have a field 3 pointing to another message, then those
            // two are the same message too, however they resemble each other inside.
            //
            // Without this, a single-field message is indistinguishable from four hundred others: its
            // shape says nothing and neither does its neighbourhood, because it points to nobody. What
            // identifies it is WHO POINTS TO IT, and that is only known going from the parents to the children.
            // Hence the first version matched 5%: it only looked downwards.
            bool dragged = true;
            while (dragged)
            {
                dragged = false;
                foreach (var (from, to) in pairs.ToList())
                {
                    if (!messagesA.TryGetValue(from, out var parentA)) continue;
                    if (!messagesB.TryGetValue(to, out var parentB)) continue;

                    foreach (var field in parentA.Fields)
                    {
                        var twin = parentB.Fields.FirstOrDefault(f => f.Number == field.Number);
                        if (twin == null) continue;
                        if (!messagesA.TryGetValue(field.Type, out var childA)) continue;
                        if (!messagesB.TryGetValue(twin.Type, out var childB)) continue;
                        if (pairs.ContainsKey(field.Type) || takenB.Contains(twin.Type)) continue;
                        if (field.Repeated != twin.Repeated) continue;

                        // A minimum of resemblance, so that a field that changed type between
                        // versions does not drag in a false pair and that one another behind it.
                        if (Similar(childA, childB, messagesA, messagesB, pairs) < 0.3 &&
                            childA.Fields.Count != childB.Fields.Count) continue;

                        pairs[field.Type] = twin.Type;
                        takenB.Add(twin.Type);
                        dragged = true;
                    }
                }
            }

            var found = new List<(string From, string To, double Score)>();

            foreach (var one in a.Messages)
            {
                if (pairs.ContainsKey(one.Name)) continue;

                double best = 0, second = 0;
                string? winner = null;

                foreach (var other in b.Messages)
                {
                    if (takenB.Contains(other.Name)) continue;

                    double score = Similar(one, other, messagesA, messagesB, pairs);
                    if (score > best) { second = best; best = score; winner = other.Name; }
                    else if (score > second) second = score;
                }

                // Two bars at once: resembling enough, and resembling it MORE THAN ANY OTHER. Without
                // the second, single-field messages would match each other at random.
                if (winner != null && best >= 0.55 && best - second >= 0.08)
                {
                    found.Add((one.Name, winner, best));
                }
            }

            if (found.Count == 0) break;

            // The best first: if two aim for the same one, the one that resembles it most gets it.
            foreach (var (from, to, _) in found.OrderByDescending(f => f.Score))
            {
                if (pairs.ContainsKey(from) || takenB.Contains(to)) continue;
                pairs[from] = to;
                takenB.Add(to);
            }
        }

        // ─── Each doubt's candidates ────────────────────────────────────────────────────
        //
        // Before, this only counted how many were left unresolved. Counting them is no use: what
        // is needed is the LIST of whom they resemble, because an ambiguous message is not a mystery,
        // it is a choice among three or five. With that short list —and only with it— it makes sense
        // to ask a model which it is; with the two thousand candidates in front, it does not.
        //
        // The ones already taken by another pair are discarded: if the candidate has an owner, it is not a
        // candidate.
        var ambiguous = new List<string>();
        var alone = new List<string>();
        var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var message in a.Messages)
        {
            if (pairs.ContainsKey(message.Name)) continue;

            var mates = b.Messages
                .Where(m => !takenB.Contains(m.Name) && signB[m.Name][0] == signA[message.Name][0])
                .Select(m => m.Name)
                .ToList();

            if (mates.Count > 0)
            {
                ambiguous.Add(message.Name);
                candidates[message.Name] = mates;
            }
            else
            {
                alone.Add(message.Name);
            }
        }

        return new Result(pairs, ambiguous, alone, candidates);
    }

    /// <summary>
    /// How much two messages resemble each other, between 0 and 1.
    ///
    /// Half and half:
    ///
    ///   the shape        which fields it has: number, class and whether it is a list. A field matching in
    ///                    both adds; the ones left over on one side or the other subtract.
    ///   the neighbourhood of the fields pointing to another message, how many point to messages already
    ///                    matched WITH EACH OTHER. This is what saves the small ones: a message
    ///                    of a single field is not told apart from three hundred others by its shape, but it is
    ///                    by who points to it.
    ///
    /// When neither points to anyone, the neighbourhood says nothing and it is scored only by the
    /// shape; that is why the graph's leaves are the ones left ambiguous, and it is unavoidable.
    /// </summary>
    private static double Similar(ProtoWriter.Message one, ProtoWriter.Message other,
                                  Dictionary<string, ProtoWriter.Message> messagesA,
                                  Dictionary<string, ProtoWriter.Message> messagesB,
                                  Dictionary<string, string> pairs)
    {
        if (one.Fields.Count == 0 && other.Fields.Count == 0) return 0;   // empty: nothing to say

        int shared = 0;
        int neighbours = 0, agree = 0;

        foreach (var field in one.Fields)
        {
            var twin = other.Fields.FirstOrDefault(f => f.Number == field.Number);
            if (twin == null) continue;

            bool oneIsMessage = messagesA.ContainsKey(field.Type);
            bool otherIsMessage = messagesB.ContainsKey(twin.Type);

            // Same number and same kind of content.
            if (oneIsMessage != otherIsMessage) continue;
            if (!oneIsMessage && field.Type != twin.Type) continue;
            if (field.Repeated != twin.Repeated) continue;
            shared++;

            if (!oneIsMessage) continue;
            neighbours++;
            if (pairs.TryGetValue(field.Type, out string? already) && already == twin.Type) agree++;
        }

        double shape = 2.0 * shared / (one.Fields.Count + other.Fields.Count);
        if (neighbours == 0) return shape;

        double neighbourhood = (double)agree / neighbours;
        return 0.5 * shape + 0.5 * neighbourhood;
    }

    /// <summary>Each message's fingerprint in each round.</summary>
    /// <summary>
    /// Each message's shape without looking at the neighbours: round 0 of the fingerprint.
    ///
    /// It is what has to be counted to know whether between two versions the PROTOCOL has changed or only
    /// the names. If the shapes are still there and what dances around are the names, the matcher should
    /// work; if the shapes have gone too, the patch really changed the messages
    /// and no name will do.
    /// </summary>
    public static Dictionary<string, string> Shapes(Model model)
        => Signatures(model, 0).ToDictionary(p => p.Key, p => p.Value[0], StringComparer.Ordinal);

    private static Dictionary<string, string[]> Signatures(Model model, int rounds)
    {
        var messages = model.Messages.ToDictionary(m => m.Name, StringComparer.Ordinal);
        var enums = model.Enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var signatures = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // Round 0: only its own. Of a field pointing to another message it is noted that it points, not to
        // whom: the name is rotated and says nothing.
        foreach (var message in model.Messages)
        {
            var parts = message.Fields
                .OrderBy(f => f.Number)
                .Select(f => $"{f.Number}:{Kind(f, messages, enums)}{(f.Repeated ? "+" : "")}");
            signatures[message.Name] = new string[rounds + 1];
            signatures[message.Name][0] = Hash(string.Join(",", parts));
        }

        // An enum does not change shape between versions —its values are the same— so its
        // fingerprint can be computed once and serves as an anchor for whoever uses it.
        var enumSignature = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in model.Enums)
        {
            enumSignature[e.Name] = Hash("E" + string.Join(",", e.Values.Select(v => v.Value).OrderBy(v => v)));
        }

        for (int round = 1; round <= rounds; round++)
        {
            foreach (var message in model.Messages)
            {
                var neighbours = message.Fields
                    .OrderBy(f => f.Number)
                    .Select(f =>
                    {
                        if (messages.ContainsKey(f.Type))
                            return $"{f.Number}>{signatures[f.Type][round - 1]}";
                        if (enumSignature.TryGetValue(f.Type, out string? e))
                            return $"{f.Number}={e}";
                        return $"{f.Number}.{f.Type}";
                    });

                signatures[message.Name][round] =
                    Hash(signatures[message.Name][round - 1] + "|" + string.Join(",", neighbours));
            }
        }

        return signatures;
    }

    /// <summary>What kind a field is, without looking at rotated names.</summary>
    private static string Kind(ProtoWriter.Field field,
                               Dictionary<string, ProtoWriter.Message> messages,
                               Dictionary<string, ProtoWriter.Enumeration> enums)
    {
        if (messages.ContainsKey(field.Type)) return "M";
        if (enums.ContainsKey(field.Type)) return "E";
        return field.Type;      // the usual types —int64, string— do not change name
    }

    private static Dictionary<string, List<string>> Group(Dictionary<string, string[]> signatures, int round)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, rounds) in signatures)
        {
            if (!groups.TryGetValue(rounds[round], out var list))
            {
                list = new List<string>();
                groups[rounds[round]] = list;
            }
            list.Add(name);
        }
        return groups;
    }

    private static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
