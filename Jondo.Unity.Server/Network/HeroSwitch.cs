using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Taking a hero in hand at once, in the same connection: the master's client becomes the
    /// hero's, and the master one of his heroes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How Auralis's server does it, read off a capture of theirs (2026-11-15, two switches): on
    /// their mod's one request the server sends, mid-session, the burst a character's selection
    /// sends -- the «character selected» with the hero's id, his sheet, his bag, his spells, his
    /// shortcuts -- and the map's actors again; the client takes a second selection while in the
    /// world and is the hero from then on. Nothing goes back to the character list.
    /// </para>
    /// <para>
    /// Here the same, out of this server's own way into the world: the master saved and taken
    /// off his map, the hero selected into the same session where the master stood, the
    /// selection's initial state, then the same map's block again -- the client loads it once
    /// more and asks for its actors, himself among them under his new id --; and the group
    /// around the hero, the master a hero of his.
    /// </para>
    /// </remarks>
    internal static class HeroSwitch
    {
        /// <summary>"ok", or why not: hero (not one of his), fight.</summary>
        public static async Task<string> SwitchAsync(GameSession session, long heroId)
        {
            using var turn = await session.EnterTurnAsync();
            using var context = SessionContext.Push(session);
            var state = session.State;
            long masterId = state.CharacterId;
            var hero = Heroes.AsHero(heroId);
            if (hero == null || hero.MasterId != masterId) return "hero";
            if (state.IsInFight || hero.Session.State.IsInFight) return "fight";

            // The group as it stands, to give back to the hero once he is the one in the world.
            var group = Heroes.Of(masterId).Select(h => (h.CharacterId, h.AutoPass)).ToList();
            long lootTo = Heroes.LootTo(masterId);
            long map = state.MapId;
            int cell = state.CellId, facing = state.Orientation;

            // The master saved and off his map; his heroes let go and saved, the hero among them.
            DatabaseManager.SaveCurrentCharacter();
            await SessionRegistry.RemoveFromMapAsync(map, masterId, session.Id);
            Heroes.DisconnectAll(masterId);

            var backend = new GameEntryBackend();
            if (!backend.SelectCharacter(session, heroId))
            {
                Console.WriteLine($"[Heroes] {heroId} could not be selected in place of {masterId}.");
                return "hero";
            }
            // Where the master stood: a hero is at his side, not where he was last saved.
            state.MapId = map;
            state.CellId = cell;
            state.Orientation = facing;
            var character = backend.FindCharacter(heroId);
            if (character == null) return "hero";
            backend.LoadCharacterState(session, character);
            DatabaseManager.SaveCurrentCharacter();

            await backend.SendInitialStateAsync(session, character);
            await backend.AnnounceMapPresenceAsync(session, character);
            await backend.SendMapInitializationAsync(session, character);

            // The group back, around the hero now: the one who led it first.
            var members = new[] { (masterId, false) }.Concat(group.Where(g => g.CharacterId != heroId));
            foreach (var (member, autoPass) in members)
                if (Heroes.Connect(session, member) == "ok" && autoPass) Heroes.SetAutoPass(heroId, member, true);
            if (lootTo != 0) Heroes.SetLootTo(heroId, lootTo);

            Console.WriteLine($"[Heroes] {state.CharacterName} ({heroId}) taken in hand in place of {masterId}, " +
                              $"map {map} cell {cell}, {Heroes.Of(heroId).Count} hero(es).");
            return "ok";
        }
    }
}
