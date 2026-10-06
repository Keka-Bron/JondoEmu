namespace Jondo.Unity.Reversing;

/// <summary>
/// The mapping walked patch by patch instead of one long jump.
///
/// The direct jump from 3.6.4.3 to 3.6.10.10 comes out at 11.3%. The matcher's ceiling, measured against
/// itself with the names shuffled, is 68.3%. The distance between the two numbers is the six
/// patches in between: each one moves the shape a little and six chained moves erase
/// the signal. With the intermediate clients the jump splits into jumps of one.
///
/// ─── This is a hypothesis, and that is why it measures ──────────────────────────────────
///
/// Chaining can go wrong, and it is worth saying before looking at the result. If on each jump
/// a third is lost and the losses were independent, seven jumps would leave 6% —
/// worse than the direct jump. It only works if it is always the same group that gets lost: the
/// small messages without a neighbourhood, which do not match on any jump. The number will say which of
/// the two happens; we do not take it as known.
///
/// ─── What can really be checked ─────────────────────────────────────────────────────────
///
/// Until now everything measured was synthetic: shuffling the names of a version and seeing how many
/// fall back into place. Here there is something better. The matcher does not look at the names at any point —only
/// field numbers, field kinds and neighbourhood; the names are the dictionary's key and nothing
/// else—, so a message that has the same name in both versions is a known answer
/// the matcher cannot have copied.
///
/// From there come the three numbers that matter for each jump: of those keeping their name,
/// how many it gets right, how many it gets wrong and how many it does not dare to call. Getting it wrong is what is serious —a wrong
/// match poisons the whole chain and nobody notices—; keeping quiet only costs coverage.
/// </summary>
public sealed class Relay
{
    /// <summary>What happens on a jump from one version to the next.</summary>
    /// <param name="Rotated">
    /// Whether Ankama has handed out the names again in this patch. It is known because the WHOLE
    /// old set of names stops being there: while it does not rotate, the 2,169 names are still there one by one.
    /// </param>
    /// <param name="SameName">How many of the old one's names still exist in the new one.</param>
    /// <param name="SameShape">How many of the old one's shapes still exist in the new one.</param>
    /// <param name="Right">Without rotation: how many it matches with themselves.</param>
    /// <param name="Wrong">Without rotation: how many it matches with another. Each one is poison.</param>
    /// <param name="Unsure">Without rotation: how many it does not dare to say anything about.</param>
    public sealed record Hop(
        string From, string To,
        int OldCount, int NewCount,
        int Paired, int Doubtful, int Gone,
        bool Rotated, int SameName, int SameShape, int Seeds,
        int Right, int Wrong, int Unsure);

    /// <summary>The result of walking the whole chain.</summary>
    /// <param name="Chain">From the name in the first version to the name in the last.</param>
    /// <param name="Died">From the name in the first version to the jump where it was lost.</param>
    public sealed record Outcome(
        List<Hop> Hops,
        Dictionary<string, string> Chain,
        Dictionary<string, string> Died);

    /// <summary>
    /// Walks the chain. The folders have to come in order, from the old to the new.
    ///
    /// Each client is opened only once even if it takes part in two jumps: rebuilding the assembly
    /// is half a minute and with eight versions that is four minutes there is no reason to spend twice.
    /// </summary>
    public Outcome Run(IReadOnlyList<string> clients, Action<string> report)
    {
        if (clients.Count < 2) throw new ArgumentException("una cadena necesita al menos dos versiones");

        var models = new Matcher.Model[clients.Count];
        for (int i = 0; i < clients.Count; i++)
        {
            string dll = Dumper.Protocol(clients[i], report);
            models[i] = ProtoWriter.Model(dll);
            report($"  {Name(clients[i])}: {models[i].Messages.Count:N0} mensajes");
        }

        var hops = new List<Hop>();

        // The chain starts as the identity on the first version: at the beginning each message
        // is itself, and each jump moves it one link or cuts it.
        var chain = models[0].Messages.ToDictionary(m => m.Name, m => m.Name, StringComparer.Ordinal);
        var died = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i + 1 < clients.Count; i++)
        {
            string from = Name(clients[i]), to = Name(clients[i + 1]);
            report($"  {from} → {to}: emparejando…");

            var result = Matcher.Match(models[i], models[i + 1]);

            var newNames = models[i + 1].Messages.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            int sameName = models[i].Messages.Count(m => newNames.Contains(m.Name));

            var oldShapes = Matcher.Shapes(models[i]);
            var newShapes = Matcher.Shapes(models[i + 1]);
            var newSet = newShapes.Values.ToHashSet(StringComparer.Ordinal);
            int sameShape = oldShapes.Values.Count(s => newSet.Contains(s));

            // The UNIQUE shapes on both sides: the matcher's seeds.
            //
            // Counting shapes plainly is misleading. Half the protocol is single-field messages, and
            // «2:int64» is going to exist in any version by chance; those shapes always survive
            // and are of no use at all. What seeds the matching is a shape that
            // points to a single message in each version, and those are the ones to count.
            var oldOnce = Once(oldShapes);
            var newOnce = Once(newShapes);
            int seeds = oldOnce.Count(s => newOnce.Contains(s));

            // While Ankama does not hand out the names again, they are ALL there: the old one's 2,169
            // are still in the new one. As soon as it rotates they no longer are, and that is the warning.
            bool rotated = sameName < models[i].Messages.Count;

            // Without rotation there is a known answer and the matcher cannot have copied it, because it does not
            // look at the names at any point.
            //
            // With rotation there is NOT, and this was my mistake in the first measurement: I counted as a
            // failure every time an old name pointed to another message in the new one. But after
            // a rotation that is exactly what HAS to happen —the name has been taken by another—,
            // so that was not measuring the matcher's hits but my own assumption. With
            // rotation nothing is scored: there is nothing to score against.
            int right = 0, wrong = 0, unsure = 0;
            if (!rotated)
            {
                foreach (var message in models[i].Messages)
                {
                    if (!result.Pairs.TryGetValue(message.Name, out string? twin)) unsure++;
                    else if (twin == message.Name) right++;
                    else wrong++;
                }
            }

            hops.Add(new Hop(
                from, to,
                models[i].Messages.Count, models[i + 1].Messages.Count,
                result.Pairs.Count, result.Candidates.Count, result.Alone.Count,
                rotated, sameName, sameShape, seeds,
                right, wrong, unsure));

            // Only certainty travels. Dragging a doubt along would multiply the doubt by the next
            // jump's, and at the end of the chain nobody would know what to trust.
            foreach (string start in chain.Keys.ToList())
            {
                if (result.Pairs.TryGetValue(chain[start], out string? next)) chain[start] = next;
                else
                {
                    died[start] = $"{from} → {to}";
                    chain.Remove(start);
                }
            }
        }

        return new Outcome(hops, chain, died);
    }

    /// <summary>The direct jump, without stops, to have something to compare with.</summary>
    public static Dictionary<string, string> Direct(string oldClient, string newClient, Action<string> report)
    {
        var a = ProtoWriter.Model(Dumper.Protocol(oldClient, report));
        var b = ProtoWriter.Model(Dumper.Protocol(newClient, report));
        return Matcher.Match(a, b).Pairs;
    }

    /// <summary>The shapes only one message has in the whole version.</summary>
    private static HashSet<string> Once(Dictionary<string, string> shapes)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string shape in shapes.Values) count[shape] = count.GetValueOrDefault(shape) + 1;
        return count.Where(p => p.Value == 1).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static string Name(string clientFolder)
        => Mapper.VersionOf(Path.GetFileName(clientFolder.TrimEnd(Path.DirectorySeparatorChar)));
}
