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
    /// On the mod's one request the server sends, mid-session, the burst a character's selection
    /// sends -- the «character selected» with the hero's id, his sheet, his bag, his spells, his
    /// shortcuts -- and the map's actors anew; the client takes a second selection while in the
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

            // The party stays as it is: the same characters in it, let go and
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

            // The master saved and off his map; the hero taken let go and saved. The other heroes
            // stay as they stand, his from now on: nobody else is taken off the map and drawn again.
            DatabaseManager.SaveCurrentCharacter();
            // With no map loaded again his own client is told too: the actor it played goes.
            bool reload = ServerSettings.Current.HeroSwitchReloadsMap;
            await MapPresenceService.Live.RemoveAsync(session.Map, masterId, reload ? session.Id : Guid.Empty);
            int place = Heroes.HandOver(masterId, heroId);
            if (place < 0) return false;

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

            if (!reload) ZaapDiscoveries.Discover(heroId, map);
            await backend.SendInitialStateAsync(session, character);
            await backend.AnnounceMapPresenceAsync(session, character);
            // The map is not loaded again: the actors on it are only
            // told anew -- the one played before gone, the one played now drawn where he stood --,
            // which is what makes a switch quick. The whole map again when the setting asks for it.
            if (reload) await backend.SendMapInitializationAsync(session, character);
            else
                await session.SendAsync(MapActorMessages.Refreshed(character, cell, facing, session.AccountId));
            // His look told as a change of gear tells it: an inventory left open kept drawing the
            // one played before over the new one's bag.
            await session.SendAsync(ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));
            // His journal, the quests he follows, the NPCs' marks his quests put on this map and his
            // achievements: the map's block sends them, and with no map loaded again the client kept
            // showing the journal and the achievements of the one played before until the next login.
            if (!reload)
            {
                await Quests.SendSwitchedJournalAsync(session, masterId);
                await Quests.SendMarksAsync(session, map);
                await Achievements.SendListAsync(session);
            }

            // Sync wardrobe, titles, ornaments and outfits to prevent outfit mixing between heroes
            session.State.IsWardrobeDraftLoaded = false;
            await WardrobeStateService.Live.SendOwnedAsync(session);
            await session.SendAsync(ConnectionProtocol.Push(Op.Lxc, ConnectionProtocol.BuildLookChanged(character)));

            // Send spells, spell shortcut bar, and characteristics for the newly active hero
            await session.SendAsync(SpellMessages.Live.Snapshot(session));
            await ShortcutBarMessages.Live.SendSpellAsync(session);
            await session.SendAsync(CharacteristicsMessages.Live.Snapshot(session));

            // The one played before back as a hero, where he was in the group's round: the others
            // are in it from the one after the hero taken (Heroes.HandOver). Taking the first hero
            // in hand each time -- the mod's Tab -- goes round them all, not back and forth.
            if (Heroes.Connect(session, masterId) == "ok") Heroes.PlaceAt(heroId, masterId, place);
            if (lootTo != 0) Heroes.SetLootTo(heroId, lootTo);

            Console.WriteLine($"[Heroes] {state.CharacterName} ({heroId}) taken in hand in place of {masterId}, " +
                              $"map {map} cell {cell}, {Heroes.Of(heroId).Count} hero(es).");
            return true;
        }
    }
}
