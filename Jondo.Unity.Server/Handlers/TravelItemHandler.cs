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
    /// The items that take whoever uses them somewhere: the recall potion, the city, class and
    /// quest potions, the home potion and the anklas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured in the captures that use one: «utilizar pocima de recuerdo», «utilizar pocima
    /// otomai», «pocima de meriana», «utilizar pocima de hogar» and the anklas of Anutropía and
    /// Sramvil. A potion that goes to a map answers the iuu with the map change (jru, lqu) and then
    /// spends itself (ivj or ium, iun); the client asks for the map (jrh) and gets it as from any
    /// other teleport.
    /// </para>
    /// <para>
    /// By effect: 600 the save point; 826 and 1155 a map reference of the client's data (189 is
    /// Bonta, 282 Brakmar); 830 at 5 one's own house and at 1 the guild's, entered with the house's
    /// jqw; 3008 a choice of destinations, which only spends the ankla once one is chosen; 4054 the
    /// guild hall. Not yet: the items that also do something else -- start a quest, give an item --,
    /// left to whoever does that.
    /// </para>
    /// </remarks>
    public static class TravelItemHandler
    {
        public const int RecallEffect = 600;
        public const int MapReferenceEffect = 826;
        public const int OtherMapReferenceEffect = 1155;
        public const int HouseEffect = 830;
        public const int ChoiceEffect = 3008;

        /// <summary>«Pócima de recibidor de gremio» (32472): into one's guild's hall.</summary>
        public const int GuildHallEffect = 4054;

        /// <summary>Effect 830's value for one's own house; 1 is the guild's.</summary>
        public const int OwnHouse = 5;
        public const int GuildHouse = 1;

        /// <summary>
        /// Effects that say something of the item and do nothing on use: 981 bound to the character,
        /// 983 exchangeable, 2818 no cooperative crafting. They do not make a potion more than a potion.
        /// </summary>
        private static readonly HashSet<int> Markers = new() { 981, 983, 2818 };

        /// <summary>
        /// The travel an item makes, if it is a travel item and nothing else: its travel effect, or
        /// null.
        /// </summary>
        internal static DatabaseManager.ItemEffectData TravelOf(IReadOnlyList<DatabaseManager.ItemEffectData> effects)
        {
            var travel = effects.FirstOrDefault(e => e.EffectId is RecallEffect or MapReferenceEffect
                or OtherMapReferenceEffect or ChoiceEffect or GuildHallEffect
                || (e.EffectId == HouseEffect && e.Value is OwnHouse or GuildHouse));
            if (travel == null) return null;
            return effects.All(e => ReferenceEquals(e, travel) || Markers.Contains(e.EffectId)) ? travel : null;
        }

        /// <summary>
        /// A travel item used: false when the item is no such thing, so the caller tries the rest.
        /// </summary>
        public static async Task<bool> TryUseAsync(NetworkStream stream, Equipment.Item item)
        {
            var effects = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(item.Template));
            var travel = TravelOf(effects);
            if (travel == null) return false;

            var me = SessionContext.State;
            if (me.IsInFight || me.FightId != 0) return true;
            if (!await ItemUseHandler.CriterionHoldsAsync(stream, item)) return true;

            switch (travel.EffectId)
            {
                case RecallEffect:
                {
                    var (map, cell) = FightHandler.SavePointOf(me.CharacterId);
                    await GoAsync(stream, item.Uid, map, cell);
                    break;
                }
                case MapReferenceEffect:
                case OtherMapReferenceEffect:
                    // The key of a dungeon without a door takes you into its first room and starts the
                    // run there (the Gelatinous Dimension's Multygely).
                    if (TravelData.TryMapReference(travel.Value, out long mapId, out int cellId)
                        && DungeonManager.KeyedInto(item.Template, mapId) is { } dungeon)
                        await DungeonHandler.EnterByItemAsync(stream, dungeon, item.Uid, cellId);
                    else if (TravelData.TryMapReference(travel.Value, out mapId, out cellId))
                        await GoAsync(stream, item.Uid, mapId, cellId);
                    else
                        Console.WriteLine($"[Travel] Item {item.Template}: map reference {travel.Value} is not in the data.");
                    break;
                case HouseEffect when travel.Value == GuildHouse:
                    await GuildHomeAsync(stream, item);
                    break;
                case HouseEffect:
                    await HomeAsync(stream, item);
                    break;
                case ChoiceEffect:
                    await ProposeAsync(stream, item, travel.Value);
                    break;
                case GuildHallEffect:
                    await GuildHallAsync(stream, item);
                    break;
            }
            return true;
        }

        /// <summary>
        /// The guild hall potion: into one's guild's hall, its Function Room by the street door's
        /// way in, as the home potion goes home -- the use (hpp, hpq), then the potion spent once
        /// there. Without a guild hall it is refused.
        /// </summary>
        private static async Task GuildHallAsync(NetworkStream stream, Equipment.Item item)
        {
            var me = SessionContext.State;
            var guild = GuildStore.GuildOf(me.CharacterId);
            var choice = guild == null ? null : GuildHalls.ChoiceOf(guild.Id);
            if (choice == null || GuildHalls.ById(choice.HallId) == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                Console.WriteLine($"[Travel] {me.CharacterName} has no guild hall to go to.");
                return;
            }
            if (await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoTeleportInAWar)) return;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpp,
                TravelProtocol.BuildDelayedUse(item.Template, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), me.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpq, TravelProtocol.BuildDelayFinished(me.CharacterId)));
            if (await GuildHallHandler.GoToOwnHallAsync(stream))
                await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid);
        }

        /// <summary>
        /// To a map, and the item spent once there: the capture's order, the jru first and the ivj
        /// after. Nothing is spent when the map change does not happen (a prisoner, a closed raid floor,
        /// a map the client does not have).
        /// </summary>
        private static async Task GoAsync(NetworkStream stream, long itemUid, long mapId, int cellId)
        {
            long from = SessionContext.State.MapId;
            int landed = await TeleportHandler.ToMapAsync(stream, mapId, cellId);
            if (landed < 0) return;
            await ItemUseHandler.SpendOneAsync(SessionContext.Current, itemUid);
            Console.WriteLine($"[Travel] {SessionContext.State.CharacterName}: {from} -> {mapId}, cell {landed}.");
        }

        // ─── Home ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The home potion: into one's own house, as the capture does it -- the item's use (hpp, hpq),
        /// the potion spent, then the house's jqw. Without a house of one's own it is refused.
        /// </summary>
        private static async Task HomeAsync(NetworkStream stream, Equipment.Item item)
        {
            var me = SessionContext.State;
            Houses.Door? door = null;
            int instance = Houses.FirstInstance;
            foreach (var house in HouseStore.OwnedBy(SessionContext.Current.AccountId))
            {
                if (Houses.TryGetDoor(house.MapId, house.ElementId, out var found) && found.InteriorMapId != 0
                    && MapManager.GetMapInfo(found.InteriorMapId) != null)
                {
                    door = found;
                    instance = house.Instance;
                    break;
                }
            }
            if (door == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                Console.WriteLine($"[Travel] {me.CharacterName} has no house to go home to.");
                return;
            }
            if (await Jail.KeepsInAsync(stream)) return;
            if (await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoTeleportInAWar)) return;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpp,
                TravelProtocol.BuildDelayedUse(item.Template, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), me.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpq, TravelProtocol.BuildDelayFinished(me.CharacterId)));
            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid)) return;
            await HouseHandler.GoHomeAsync(stream, door.Value, instance);
        }

        /// <summary>
        /// The guild home potion (830 at 1): into a house shared with one's guild whose rights let the
        /// guild teleport (128), the way «pocima hogar a gremio» goes -- hpp, hpq, the potion spent,
        /// the house's jqw. Without such a house it is refused.
        /// </summary>
        private static async Task GuildHomeAsync(NetworkStream stream, Equipment.Item item)
        {
            var me = SessionContext.State;
            var guild = GuildStore.GuildOf(me.CharacterId);
            Houses.Door? door = null;
            int instance = Houses.FirstInstance;
            foreach (var house in guild == null ? new List<HouseStore.House>() : HouseStore.SharedWith(guild.Id))
            {
                if ((house.GuildRights & HouseStore.GuildRights.Teleport) == 0) continue;
                if (Houses.TryGetDoor(house.MapId, house.ElementId, out var found) && found.InteriorMapId != 0
                    && MapManager.GetMapInfo(found.InteriorMapId) != null)
                {
                    door = found;
                    instance = house.Instance;
                    break;
                }
            }
            if (door == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.RequirementsNotMet)));
                Console.WriteLine($"[Travel] {me.CharacterName} has no guild house to go to.");
                return;
            }
            if (await Jail.KeepsInAsync(stream)) return;
            if (await ConquestHandler.HoldsInAsync(stream, ConquestHandler.NoTeleportInAWar)) return;

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpp,
                TravelProtocol.BuildDelayedUse(item.Template, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), me.CharacterId)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpq, TravelProtocol.BuildDelayFinished(me.CharacterId)));
            if (!await ItemUseHandler.SpendOneAsync(SessionContext.Current, item.Uid)) return;
            await HouseHandler.GoHomeAsync(stream, door.Value, instance);
        }

        // ─── The anklas' choice ─────────────────────────────────────────────────

        /// <summary>
        /// An ankla: its choice of destinations (kss). Nothing is spent until one is chosen.
        /// </summary>
        private static async Task ProposeAsync(NetworkStream stream, Equipment.Item item, int choiceId)
        {
            var choice = TravelData.ChoiceOf(choiceId);
            if (choice == null || choice.Options.Count == 0)
            {
                Console.WriteLine($"[Travel] Item {item.Template}: choice {choiceId} is not in the data.");
                return;
            }
            SessionContext.State.ItemChoice = (item.Uid, choice.Id);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kss, TravelProtocol.BuildChoice(choice)));
        }

        /// <summary>
        /// The option chosen (kso { f1: its place }): to its map reference, the ankla spent there;
        /// «No teletransportarse» keeps it. A kso with no choice pending is let go.
        /// </summary>
        public static async Task ChooseAsync(NetworkStream stream, byte[] frame)
        {
            var me = SessionContext.State;
            var pending = me.ItemChoice;
            me.ItemChoice = null;
            if (pending == null) return;

            byte[] kso = ConnectionProtocol.ReadPayload(frame, Op.Kso);
            int place = 0;
            if (kso != null)
                foreach (var field in ProtoMessage.Parse(kso).Fields)
                    if (field.FieldNumber == 1 && field.WireType == 0) place = (int)field.VarIntValue;

            var choice = TravelData.ChoiceOf(pending.Value.ChoiceId);
            if (choice == null || place < 0 || place >= choice.Options.Count) return;
            var option = choice.Options[place];
            if (option.RewardType != TravelData.GoToMapReference) return;

            if (Equipment.ByUid(pending.Value.ItemUid) is not { Quantity: > 0 }) return;
            if (!int.TryParse(option.RewardValue, out int reference) ||
                !TravelData.TryMapReference(reference, out long mapId, out int cellId))
            {
                Console.WriteLine($"[Travel] Choice {choice.Id}, option {option.Id}: no map reference «{option.RewardValue}».");
                return;
            }
            await GoAsync(stream, pending.Value.ItemUid, mapId, cellId);
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
