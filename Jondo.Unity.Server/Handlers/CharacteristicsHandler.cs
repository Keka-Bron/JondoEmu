using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Spending and giving back characteristic points.
    ///
    ///   C  kum { one field per characteristic, the points spent on it IN TOTAL }
    ///   S  iun { f1: carried, f3: capacity }
    ///   S  kub                                    the sheet again, with the new numbers
    ///
    ///   C  kuh {}                                 the reset button
    ///   S  iun, kub
    ///
    /// The six capture files under Caracteristicas/ settle the field order and the units.
    /// Distributing five points into every characteristic sends
    /// { f1: 5, f2: 5, f3: 5, f4: 15, f5: 5, f6: 5 } and characteristic 3, the points left, drops
    /// by forty — the sum. Wisdom is the fifteen: it costs three points each, which is what the
    /// capture's own file name says. So the message carries what the player PAYS, not what the
    /// characteristic gains, and the server has to know the price to work out the other half.
    ///
    /// What the captures do NOT settle, because the character they were recorded on had just
    /// reset its sheet, is whether those numbers are what the player is spending NOW or what it
    /// has spent ALTOGETHER. A session of the real client does settle it. Four confirmations in a
    /// row, with a reset just before the first:
    ///
    ///   kum { vitality 10 }
    ///   kum { vitality 10, agility 5 }
    ///   kum { vitality 20, wisdom 15, agility 5 }
    ///   kum { vitality 40, wisdom 15, agility 10, strength 5 }
    ///
    /// Read as increments, that character bought vitality four times over and the points left
    /// dropped by forty when the player had only asked for fifteen of wisdom — which is exactly
    /// what happened. Read as totals, each one is the whole distribution as it stands, the panel
    /// simply repeats itself, and every number lands where the player put it.
    ///
    /// So the field is a TARGET: this characteristic is to have this many points in it. Sending
    /// the same message twice does nothing the second time, which is the property that matters —
    /// the panel does repeat itself.
    /// </summary>
    public static class CharacteristicsHandler
    {
        // Which field of kum belongs to which characteristic. Read off the capture: after the
        // exchange above, characteristics 15, 13, 11, 12, 14 and 10 had all gained five.
        private const int FieldIntelligence = 1;
        private const int FieldChance = 2;
        private const int FieldVitality = 3;
        private const int FieldWisdom = 4;
        private const int FieldAgility = 5;
        private const int FieldStrength = 6;

        public static async Task SpendAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kum = ConnectionProtocol.ReadPayload(payload, Op.Kum);
            if (kum == null) return;

            // What the whole sheet is meant to look like: the characteristics the message does not
            // mention are the ones with nothing spent on them.
            var wanted = new Dictionary<int, int>();
            foreach (var f in ProtoMessage.Parse(kum).Fields)
            {
                if (f.WireType != 0 || f.VarIntValue <= 0) continue;
                if (NameOf(f.FieldNumber) == null) continue;
                wanted[f.FieldNumber] = (int)f.VarIntValue;
            }
            if (wanted.Count == 0) return;

            int capital = Capital();
            int asked = 0;
            foreach (var pair in wanted) asked += pair.Value;

            if (asked > capital)
            {
                // Not a partial charge. The client works the cost out itself before asking, so a
                // total that does not fit means the two of us disagree about the sheet, and taking
                // half the points would only make that worse. It gets the sheet back untouched.
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Stats] The client wants {asked} points spent and the character " +
                                  $"only has {capital}. Nothing applied.");
                Console.ResetColor();
                await AnswerAsync(stream);
                return;
            }

            foreach (int field in new[] { FieldIntelligence, FieldChance, FieldVitality,
                                          FieldWisdom, FieldAgility, FieldStrength })
            {
                Apply(field, wanted.TryGetValue(field, out int points) ? points : 0);
            }

            Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints = capital - asked;
            DatabaseManager.SaveCurrentCharacter();

            Console.WriteLine($"[Stats] {asked} of {capital} points spent; " +
                              $"{Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints} left.");
            await AnswerAsync(stream);
        }

        /// <summary>
        /// Every point the character has ever had to spend: five a level from the second on. It is
        /// worked out rather than stored, so that a character that levels up gets its points
        /// without anything having to remember to hand them over.
        /// </summary>
        private static int Capital() => 5 * Math.Max(0, Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel - 1);

        /// <summary>
        /// Gives every point back. What a character has to spend over its life is five a level
        /// from the second one on, which is exactly what the database holds today: the character
        /// at level 50 has 75 left and 170 spread across its characteristics, and 75 + 170 is
        /// 5 x 49.
        ///
        /// The capture charges nothing for this: the kamas in the sheet are the same before and
        /// after, so no fee is taken here either.
        /// </summary>
        public static async Task ResetAsync(NetworkStream stream)
        {
            Jondo.Unity.Server.Network.SessionContext.State.StatVitality = 0;
            Jondo.Unity.Server.Network.SessionContext.State.StatWisdom = 0;
            Jondo.Unity.Server.Network.SessionContext.State.StatStrength = 0;
            Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence = 0;
            Jondo.Unity.Server.Network.SessionContext.State.StatChance = 0;
            Jondo.Unity.Server.Network.SessionContext.State.StatAgility = 0;
            Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints = Capital();
            DatabaseManager.SaveCurrentCharacter();

            Console.WriteLine($"[Stats] Characteristics reset: {Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints} points to spend.");
            await AnswerAsync(stream);
        }

        private static async Task AnswerAsync(NetworkStream stream)
        {
            long capacity = Pods();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iun, ConnectionProtocol.BuildPods(0, capacity)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
        }

        /// <summary>
        /// What the character can carry: a thousand to start with and five more per point of
        /// strength. Confirmed by the capture, where five points of strength moved both the pods
        /// characteristic and the capacity in iun by exactly twenty-five.
        ///
        /// What it is CARRYING goes out as zero, because nothing here weighs the inventory yet.
        /// </summary>
        private static long Pods() => 1000 + 5L * Jondo.Unity.Server.Network.SessionContext.State.StatStrength;

        /// <summary>
        /// Sets a characteristic to whatever <paramref name="points"/> buys, counting from zero.
        ///
        /// From zero, not from where it is: the message says how much has been spent altogether,
        /// so the characteristic can go down as well as up and the answer has to be the same
        /// however many times the client repeats itself.
        ///
        /// The prices come out of the client's own data through
        /// <see cref="Managers.BreedStatCost"/>. They have to: the client works the cost out
        /// before it sends kum and has already shown the player the result.
        /// </summary>
        private static void Apply(int kumField, int points)
        {
            string? characteristic = NameOf(kumField);
            if (characteristic == null) return;

            int value = 0, paid = 0;
            while (paid < points)
            {
                int price = Managers.BreedStatCost.PriceOf(Jondo.Unity.Server.Network.SessionContext.State.Breed, characteristic, value);
                if (price <= 0 || paid + price > points) break;
                paid += price;
                value++;
            }
            Set(kumField, value);
        }

        private static string? NameOf(int kumField) => kumField switch
        {
            FieldVitality => "vitality",
            FieldWisdom => "wisdom",
            FieldStrength => "strength",
            FieldIntelligence => "intelligence",
            FieldChance => "chance",
            FieldAgility => "agility",
            _ => null,
        };

        private static void Set(int kumField, int value)
        {
            switch (kumField)
            {
                case FieldVitality: Jondo.Unity.Server.Network.SessionContext.State.StatVitality = value; break;
                case FieldWisdom: Jondo.Unity.Server.Network.SessionContext.State.StatWisdom = value; break;
                case FieldStrength: Jondo.Unity.Server.Network.SessionContext.State.StatStrength = value; break;
                case FieldIntelligence: Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence = value; break;
                case FieldChance: Jondo.Unity.Server.Network.SessionContext.State.StatChance = value; break;
                case FieldAgility: Jondo.Unity.Server.Network.SessionContext.State.StatAgility = value; break;
            }
        }
    }
}
