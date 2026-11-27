using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.ServerMods;

namespace Jondo.Unity.Server.Network
{
    internal static class CompanionPresence
    {
        /// <summary>A companion drawn on a map: who, where, facing where, and his account.</summary>
        internal readonly record struct Shown(DbCharacter Character, int Cell, int Facing, long AccountId);

        /// <summary>The companions standing on a map: those of the people on it, out of any fight.</summary>
        public static IEnumerable<Shown> On(long mapId)
        {
            var movement = ServerModHooks.Get<ICompanionMovement>();
            if (movement == null) yield break;

            foreach (var master in SessionRegistry.OnMap(mapId).ToList())
            {
                foreach (var companion in movement.VisibleFollowers(master, mapId))
                {
                    var position = companion.Session.Position;
                    var character = DatabaseManager.GetCharacterById(companion.CharacterId);
                    if (character != null) yield return new Shown(character, position.CellId, position.Orientation, master.AccountId);
                }
            }
        }

        public static Task MasterMovedAsync(GameSession master, long mapLeft, IReadOnlyList<ICompanion>? admitted = null)
            => admitted == null
                ? ServerModHooks.Get<ICompanionMovement>()?.MasterMovedAsync(master, mapLeft) ?? Task.CompletedTask
                : ServerModHooks.Get<IDungeonPartyPolicy>()?.MoveDungeonFollowersAsync(master, mapLeft, admitted)
                    ?? Task.CompletedTask;

        public static Task MasterWalkedAsync(GameSession master, IReadOnlyList<int> cells)
            => ServerModHooks.Get<ICompanionMovement>()?.MasterWalkedAsync(master, cells) ?? Task.CompletedTask;
    }
}
