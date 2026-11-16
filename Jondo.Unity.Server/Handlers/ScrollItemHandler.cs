using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The items that raise a characteristic for good: the scrolls, and the breads, meats, fish and
    /// potions that do it on top of giving back life or energy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Effects 606 to 611, «+#1 sabiduría / de fuerza adicional / suerte / de agilidad adicional /
    /// vitalidad / inteligencia», give scroll points -- the «adicional» the sheet shows apart from
    /// the points spent. Each item says in its criterion how far it can take them, and its
    /// description says the same: the small scroll «solo si tienes menos de 25 puntos de fuerza
    /// adicionales» (cs&lt;25), the medium 50, the large 80, the powerful 100; the breads go in
    /// steps of five or ten. Nobody goes past 100: the «Pergamino acelerador» says so in its own
    /// words, «con un límite de 100 puntos máximo por característica».
    /// </para>
    /// <para>
    /// Measured: the refusal. «intentar usar pergamino agilidad-rechazado por tener ya mas base»
    /// answers «Tus características no te lo permiten.» (lqn { f1: 1, f2: 19 }) and the scroll stays.
    /// What a scroll that works says is not captured: it goes as a life item does -- the item
    /// leaves, the kub with the new figures, the bag's weight -- with «Has ganado {0} puntos de
    /// …» (info 9 to 14) for each characteristic.
    /// </para>
    /// </remarks>
    public static class ScrollItemHandler
    {
        /// <summary>The most scroll points a characteristic takes.</summary>
        public const int Cap = 100;

        /// <summary>A characteristic a scroll raises: its effect and its «Has ganado» message.</summary>
        internal sealed record Characteristic(int Effect, int Gained, Func<SessionState, int> Get, Action<SessionState, int> Set);

        internal static readonly IReadOnlyList<Characteristic> Characteristics = new[]
        {
            new Characteristic(606, InfoMessages.WisdomGained, s => s.ScrolledWisdom, (s, v) => s.ScrolledWisdom = v),
            new Characteristic(607, InfoMessages.StrengthGained, s => s.ScrolledStrength, (s, v) => s.ScrolledStrength = v),
            new Characteristic(608, InfoMessages.ChanceGained, s => s.ScrolledChance, (s, v) => s.ScrolledChance = v),
            new Characteristic(609, InfoMessages.AgilityGained, s => s.ScrolledAgility, (s, v) => s.ScrolledAgility = v),
            new Characteristic(610, InfoMessages.VitalityGained, s => s.ScrolledVitality, (s, v) => s.ScrolledVitality = v),
            new Characteristic(611, InfoMessages.IntelligenceGained, s => s.ScrolledIntelligence, (s, v) => s.ScrolledIntelligence = v),
        };

        /// <summary>Effects that say something of the item and do nothing on use.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        private static Characteristic CharacteristicOf(int effect) => Characteristics.FirstOrDefault(c => c.Effect == effect);

        /// <summary>
        /// Whether an item raises characteristics and does nothing besides but give back life or
        /// energy: a scroll or a bread of the kind.
        /// </summary>
        internal static bool IsScroll(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
            => effects.Any(e => CharacteristicOf(e.EffectId) != null)
               && effects.All(e => CharacteristicOf(e.EffectId) != null
                                   || e.EffectId is RecoveryItemHandler.LifeEffect or RecoveryItemHandler.EnergyEffect
                                   || Markers.Contains(e.EffectId));

        /// <summary>
        /// How many points an effect gives: the figure the item in the bag carries, as the client
        /// draws it; when it carries none, the template's -- a roll between its two numbers, the
        /// powerful scroll's 2 to 102 -- or its one figure.
        /// </summary>
        internal static int AmountOf(Equipment.Item item, DatabaseManager.ItemEffectData template, Random dice)
        {
            foreach (var effect in item?.Effects ?? new List<Equipment.ItemEffect>())
                if (effect.Effect == template.EffectId && effect.Value > 0) return (int)Math.Min(int.MaxValue, effect.Value);
            return RecoveryItemHandler.Roll(template, dice);
        }

        /// <summary>What a gain leaves a characteristic at: never past the cap, never down.</summary>
        internal static int Raised(int current, int amount) => Math.Max(current, Math.Min(Cap, current + Math.Max(0, amount)));

        /// <summary>A scroll used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            if (!IsScroll(effects)) return false;

            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            // What each characteristic gets, before anything moves.
            var gains = new List<(Characteristic Characteristic, int Before, int After)>();
            foreach (var effect in effects)
            {
                var characteristic = CharacteristicOf(effect.EffectId);
                if (characteristic == null) continue;
                int before = characteristic.Get(me);
                int after = Raised(before, AmountOf(item, effect, Random.Shared));
                if (after > before) gains.Add((characteristic, before, after));
            }

            // And the life or energy a bread gives on top.
            long character = me.CharacterId;
            var now = DateTime.UtcNow;
            int missing = RestingLife.MissingAt(character, now);
            int energy = Energy.Of(character);
            var recovery = RecoveryItemHandler.RecoveryOf(effects.Where(e => e.EffectId is RecoveryItemHandler.LifeEffect
                                                                              or RecoveryItemHandler.EnergyEffect).ToList());
            var (life, gained) = recovery == null ? (0, 0) : RecoveryItemHandler.WouldGive(recovery, missing, energy, Random.Shared);

            if (gains.Count == 0 && life == 0 && gained == 0)
            {
                // Everything already at its cap -- the accelerator scroll has no criterion to stop it.
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.CharacteristicsForbid)));
                return true;
            }

            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid, weigh: false)) return true;

            foreach (var (characteristic, _, after) in gains) characteristic.Set(me, after);
            if (life > 0)
            {
                RestingLife.Set(character, missing - life, now);
                me.RegenerationStartedUtc = now;
            }
            if (gained > 0) Energy.Set(character, energy + gained);
            DatabaseManager.SaveCurrentCharacter();

            await CharacteristicsMessages.Live.SendAsync(SessionContext.Current);
            if (life > 0)
                await WriteAsync(stream, ConnectionProtocol.BuildRegenerationStarted(ConnectionProtocol.RegenerationRateNow()));
            foreach (var (characteristic, before, after) in gains)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(characteristic.Gained, (after - before).ToString())));
            if (life > 0)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.LifeRecovered, life.ToString())));
            if (gained > 0)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.EnergyRecovered, gained.ToString())));
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} uses {item.Template}: " +
                              string.Join(", ", gains.Select(g => $"{g.Characteristic.Effect} {g.Before}->{g.After}")) +
                              (life > 0 ? $", +{life} life" : "") + (gained > 0 ? $", +{gained} energy" : "") + ".");
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
