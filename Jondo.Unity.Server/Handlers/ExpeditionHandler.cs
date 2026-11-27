using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Elmer Zenario's conversation: which expedition, are you sure, begin or resume it, and what
    /// an expedition is (Expeditions has the lines and which dungeon each group opens).
    /// </summary>
    /// <remarks>
    /// The expeditions have no door of their own: their catalogue entrance is a placeholder and
    /// they ask for no key. Going in is beginning one here, and coming out lands in the hall the
    /// character set off from (Expeditions.WayOut).
    /// </remarks>
    internal static class ExpeditionHandler
    {
        /// <summary>The reply was handled and the window is closed or replaced already.</summary>
        internal const long Done = -1;

        private static readonly (long, long[]) Close = (0, Array.Empty<long>());

        public static bool Owns(int npc) => npc == Expeditions.Npc && Expeditions.Lines != null && Expeditions.Offers.Count > 0;

        public static (long Question, long[] Replies) Opening(long characterId)
        {
            var talk = Expeditions.Lines!;
            var replies = new System.Collections.Generic.List<long> { talk.WhichExpeditions, talk.WhatAreThey };
            if (talk.Abandon != 0 && Expeditions.Offers.Any(o => Saved(o, characterId) != 0)) replies.Add(talk.Abandon);
            return (talk.Opening, replies.Where(r => r != 0).ToArray());
        }

        public static async Task<(long Question, long[] Replies)> ReplyAsync(NetworkStream stream, long reply)
        {
            var talk = Expeditions.Lines!;
            var state = SessionContext.State;

            if (reply == talk.WhichExpeditions) return Which();
            if (reply == talk.Thanks) return Close;
            if (reply == talk.Abandon)
            {
                foreach (var offer in Expeditions.Offers)
                {
                    if (DungeonManager.Get(offer.DungeonId) is { } dungeon && DungeonProgress.SavedRoom(state.CharacterId, dungeon) != 0)
                        DungeonProgress.SetRoom(state.CharacterId, dungeon, 0);
                }
                return Close;
            }

            // What an expedition is: four lines, each answered by the reply that goes on.
            if (reply == talk.WhatAreThey && talk.Explanation.Length > 0) return Explain(talk, 0);
            int onward = Array.IndexOf(talk.ExplanationReplies, reply);
            if (onward >= 0 && onward + 1 < talk.Explanation.Length) return Explain(talk, onward + 1);

            var chosen = Expeditions.ByReply(reply);
            if (chosen == null || DungeonManager.Get(chosen.DungeonId) is not { } expedition) return Close;

            if (reply == chosen.Other) return Which();
            if (reply == chosen.Select)
            {
                var options = new System.Collections.Generic.List<long> { chosen.Begin };
                if (chosen.Resume != 0 && Saved(chosen, state.CharacterId) != 0) options.Add(chosen.Resume);
                if (chosen.Other != 0) options.Add(chosen.Other);
                return (chosen.Confirm != 0 ? chosen.Confirm : talk.Which, options.ToArray());
            }

            bool resume = reply == chosen.Resume || reply == chosen.ReturnYes;
            if (reply != chosen.Begin && !resume) return Close;

            long hall = state.OpenDialogueMapId;
            if (state.IsInFight || hall != state.MapId) return Close;
            if (await Jail.KeepsInAsync(stream) || await PlayerPolicies.BlocksActivityAsync(stream, PlayerActivity.Dungeon))
                return Close;
            if (expedition.MinLevel > state.CharacterLevel)
            {
                Console.WriteLine($"[Expeditions] {expedition.Name}: needs level {expedition.MinLevel} and " +
                                  $"has {state.CharacterLevel}.");
                await NpcHandler.CloseAsync(stream);
                await DungeonHandler.WarnAsync(stream, InfoMessages.LevelTooLow);
                return (Done, Array.Empty<long>());
            }

            long room = resume ? Saved(chosen, state.CharacterId) : 0;
            Expeditions.SetOffFrom(state.CharacterId, hall);
            await NpcHandler.CloseAsync(stream);
            if (room != 0)
            {
                Console.WriteLine($"[Expeditions] {state.CharacterName} resumes {expedition.Name} at room " +
                                  $"{expedition.Rooms.IndexOf(room) + 1} of {expedition.Rooms.Count}.");
                await TeleportHandler.ToMapAsync(stream, room);
            }
            else
            {
                await DungeonHandler.EnterByMatchAsync(stream, expedition);
            }
            return (Done, Array.Empty<long>());
        }

        private static (long, long[]) Which()
        {
            var talk = Expeditions.Lines!;
            return (talk.Which, Expeditions.Offers.Select(o => o.Select).ToArray());
        }

        private static (long, long[]) Explain(Expeditions.Talk talk, int line)
        {
            bool last = line == talk.Explanation.Length - 1;
            long[] replies = last
                ? new[] { talk.WhichExpeditions, talk.Thanks }
                : new[] { line < talk.ExplanationReplies.Length ? talk.ExplanationReplies[line] : talk.Thanks };
            return (talk.Explanation[line], replies.Where(r => r != 0).ToArray());
        }

        private static long Saved(Expeditions.Offer offer, long characterId)
            => DungeonManager.Get(offer.DungeonId) is { } dungeon ? DungeonHandler.ResumeRoom(dungeon, characterId) : 0;
    }
}
