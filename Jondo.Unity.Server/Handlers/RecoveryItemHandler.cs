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
    /// The items that give back life or energy outside a fight: breads, meats, fish, potions,
    /// beers. Effect 110 «Devuelve #1 a #2 puntos de vida» and 139 «Devuelve #1 a #2 puntos de energía».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured: only the refusal. «ruta muy larga» uses an energy item with the gauge full and the
    /// real server answers «Los puntos de energía ya están al máximo.» (lqn { f2: 253 }) and nothing
    /// else -- the item stays. The life's twin is «Ya tienes todos tus puntos de vida» (225).
    /// </para>
    /// <para>
    /// Read off the client for what a use that works has to say: the kub writes the characteristics
    /// back (eet.xaz), the life lost among them (97, <c>hitPointLoss</c>), and the ktz starts the
    /// regeneration counter again from zero (eet.xbf: hl.ndn(0), then the rate). So the life given
    /// back goes in the kub as less life lost, the counter is restarted behind it, and the client
    /// regenerates on from there without counting twice what it had already counted. The order
    /// around them is the Trool fair potion's, the one capture where an item changes the kub: the
    /// item leaves (ium), the kub, the bag's weight (iun). And «Has recuperado {0} puntos de
    /// vida / energía» (info 1 and 7) say how much.
    /// </para>
    /// <para>
    /// Only the items that do nothing else: the breads that also raise a characteristic for good
    /// («cv&lt;5») go with the scrolls, and the ones that start a quest or give an item with whoever
    /// does that.
    /// </para>
    /// </remarks>
    public static class RecoveryItemHandler
    {
        public const int LifeEffect = 110;
        public const int EnergyEffect = 139;

        /// <summary>Effects that say something of the item and do nothing on use: bound, exchangeable, no cooperative crafting.</summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>What an item gives back, if it gives back life or energy and nothing else.</summary>
        internal sealed record Recovery(DatabaseManager.ItemEffectData Life, DatabaseManager.ItemEffectData Energy);

        internal static Recovery RecoveryOf(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            var life = effects.FirstOrDefault(e => e.EffectId == LifeEffect);
            var energy = effects.FirstOrDefault(e => e.EffectId == EnergyEffect);
            if (life == null && energy == null) return null;
            return effects.All(e => e.EffectId is LifeEffect or EnergyEffect || Markers.Contains(e.EffectId))
                ? new Recovery(life, energy)
                : null;
        }

        /// <summary>
        /// An effect's amount: one figure, or a roll between the two of «#1 a #2» -- the 16 to 30 of a
        /// beer that gives both.
        /// </summary>
        internal static int Roll(DatabaseManager.ItemEffectData effect, Random dice)
            => effect.DiceSide > effect.DiceNum ? dice.Next(effect.DiceNum, effect.DiceSide + 1) : effect.DiceNum;

        /// <summary>
        /// What using it would give back now: the life healed and the energy, each up to what is
        /// missing. Both zero means there is nothing to give back.
        /// </summary>
        /// <remarks>
        /// The life is as much of the roll as the energy is of its gauge: «La energía incrementa la
        /// eficacia de los consumibles que regeneran vida del personaje, en un rango del 0 al 100%»
        /// (Translations 1156704). INFERRED: in a straight line, and never below a point while some
        /// life is missing -- the energy here never goes below 1, and nothing healed would read as
        /// «la vida ya está al máximo».
        /// </remarks>
        internal static (int Life, int Energy) WouldGive(Recovery recovery, int lifeMissing, int energy, Random dice,
                                                         int lifeBonus = 0, int energyBonus = 0)
        {
            int life = 0;
            if (recovery.Life != null && lifeMissing > 0)
            {
                long rolled = Roll(recovery.Life, dice);
                rolled += rolled * Math.Max(0, lifeBonus) / 100;
                long scaled = rolled * Math.Clamp(energy, 0, DefeatPenalty.MaxEnergy) / DefeatPenalty.MaxEnergy;
                life = (int)Math.Min(Math.Max(rolled > 0 ? 1 : 0, scaled), lifeMissing);
            }
            int toFull = Math.Max(0, DefeatPenalty.MaxEnergy - energy);
            int gained = recovery.Energy == null || toFull == 0
                ? 0
                : Math.Min(Roll(recovery.Energy, dice) * (100 + Math.Max(0, energyBonus)) / 100, toFull);
            return (Math.Max(0, life), Math.Max(0, gained));
        }

        /// <summary>A life or energy item used: false when the item is no such thing.</summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var recovery = RecoveryOf(effects);
            if (recovery == null) return false;

            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            long character = me.CharacterId;
            var now = DateTime.UtcNow;
            int missing = RestingLife.MissingAt(character, now);
            int energy = Energy.Of(character);
            // Today's Almanax may make them heal more: «las curas con consumibles (vida y energía)
            // aumenta un 100%».
            var (life, gained) = WouldGive(recovery, missing, energy, Random.Shared,
                Almanax.BonusPercent(Almanax.BonusType.ConsumableLife, Almanax.Where.Anywhere),
                Almanax.BonusPercent(Almanax.BonusType.ConsumableEnergy, Almanax.Where.Anywhere));

            if (life == 0 && gained == 0)
            {
                // Nothing to give back: the item stays, and the gauge it fills is named.
                int full = recovery.Energy != null ? InfoMessages.EnergyAlreadyFull : InfoMessages.LifeAlreadyFull;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(full)));
                return true;
            }

            if (!await ItemUseHandler.SpendOneAsync(stream, item.Uid, weigh: false)) return true;

            RestingLife.Set(character, missing - life, now);
            me.RegenerationStartedUtc = now;
            if (gained > 0) Energy.Set(character, energy + gained);

            await CharacteristicsMessages.Live.SendAsync(SessionContext.Current);
            await WriteAsync(stream, ConnectionProtocol.BuildRegenerationStarted(ConnectionProtocol.RegenerationRateNow()));
            if (life > 0)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.LifeRecovered, life.ToString())));
            if (gained > 0)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.EnergyRecovered, gained.ToString())));
            await ItemUseHandler.WeighAsync(stream);

            Console.WriteLine($"[Items] {me.CharacterName} uses {item.Template}: +{life} life " +
                              $"({missing - life} still missing), +{gained} energy ({energy + gained}).");
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
