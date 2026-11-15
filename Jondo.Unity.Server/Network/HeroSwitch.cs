using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
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

            // The party stays as it is, as on Auralis: the same characters in it, let go and
            // taken back here without it being told; the one played now is given it afterwards.
            var held = Parties.IsInParty(masterId)
                ? group.Select(g => g.CharacterId).Append(masterId).ToList()
                : new List<long>();
            Heroes.HoldParty(held);
            bool done = false;
            try
            {
                done = await SwitchHeldAsync(session, masterId, heroId, group, lootTo, map, cell, facing);
            }
            finally
            {
                Heroes.ReleaseParty(held);
            }
            await Handlers.PartyHandler.SwitchedAsync(session, masterId, held);
            return done ? "ok" : "hero";
        }

        /// <summary>The switch itself, the party held: true when the hero is the one in the world now.</summary>
        private static async Task<bool> SwitchHeldAsync(GameSession session, long masterId, long heroId,
                                                        List<(long CharacterId, bool AutoPass)> group, long lootTo,
                                                        long map, int cell, int facing)
        {
            var state = session.State;

            // The master saved and off his map; his heroes let go and saved, the hero among them.
            DatabaseManager.SaveCurrentCharacter();
            // With no map loaded again his own client is told too: the actor it played goes.
            bool reload = ServerSettings.Current.HeroSwitchReloadsMap;
            await SessionRegistry.RemoveFromMapAsync(map, masterId, reload ? session.Id : Guid.Empty);
            Heroes.DisconnectAll(masterId);

            var backend = new GameEntryBackend();
            if (!backend.SelectCharacter(session, heroId))
            {
                Console.WriteLine($"[Heroes] {heroId} could not be selected in place of {masterId}.");
                return false;
            }
            // Where the master stood: a hero is at his side, not where he was last saved.
            state.MapId = map;
            state.CellId = cell;
            state.Orientation = facing;
            var character = backend.FindCharacter(heroId);
            if (character == null) return false;
            backend.LoadCharacterState(session, character);
            DatabaseManager.SaveCurrentCharacter();

            await backend.SendInitialStateAsync(session, character);
            await backend.AnnounceMapPresenceAsync(session, character);
            // As Auralis's capture has it, the map is not loaded again: the actors on it are only
            // told anew -- the one played before gone, the one played now drawn where he stood --,
            // which is what makes a switch quick. The whole map again when the setting asks for it.
            if (reload) await backend.SendMapInitializationAsync(session, character);
            else
                await session.SendAsync(ConnectionProtocol.Push(Op.Jsn,
                    ConnectionProtocol.BuildActorRefreshed(character, cell, facing, session.AccountId)));

            // The group back, around the hero now, in the round it makes: from the one after him to
            // the one before, the master where he was in it. Taking the first hero in hand each time
            // -- the mod's Tab -- goes round them all, not back and forth between two.
            var ring = new List<(long CharacterId, bool AutoPass)> { (masterId, false) };
            ring.AddRange(group);
            int at = ring.FindIndex(m => m.CharacterId == heroId);
            var members = Enumerable.Range(1, ring.Count - 1).Select(k => ring[(at + k) % ring.Count]);
            foreach (var (member, autoPass) in members)
                if (Heroes.Connect(session, member) == "ok" && autoPass) Heroes.SetAutoPass(heroId, member, true);
            if (lootTo != 0) Heroes.SetLootTo(heroId, lootTo);

            Console.WriteLine($"[Heroes] {state.CharacterName} ({heroId}) taken in hand in place of {masterId}, " +
                              $"map {map} cell {cell}, {Heroes.Of(heroId).Count} hero(es).");
            return true;
        }
    }
}
