using System;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The Cazacabezas mod's side of the control API: the hunter's record and order, the board of
    /// prey, taking an order and giving it up -- for the account's character in the world.
    /// </summary>
    /// <remarks>
    /// Any player may ask, with his own session's token (the launcher hands it to the client as
    /// JONDO_PLAYER_TOKEN): it is his own character the answer is about, and nothing here is
    /// decided by the mod -- every rule is <see cref="HeadHunts"/>'s.
    /// </remarks>
    public static partial class ControlApi
    {
        /// <summary>The account's character in the world, or null.</summary>
        private static GameSession? InTheWorld(long account)
            => SessionRegistry.All().FirstOrDefault(s => s.AccountId == account && s.IsInWorld && s.HasCharacter);

        private static Respuesta HeadHuntStatus(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long id = me.State.CharacterId;
            int level = me.State.CharacterLevel;
            var alignment = Alignments.Of(id);
            var record = HeadHunts.Of(id);
            var order = HeadHunts.OrderOf(id);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            object? hunt = null;
            if (order != null)
            {
                var prey = DatabaseManager.GetCharacterById(order.Target);
                var preySession = SessionRegistry.FindByCharacter(order.Target);
                var preyMap = preySession != null && order.Open(now) ? MapManager.GetMapInfo(preySession.State.MapId) : null;
                hunt = new
                {
                    target = order.Target,
                    name = prey?.Name ?? "",
                    level = prey?.Level ?? 0,
                    breed = prey?.Breed ?? 0,
                    grade = Math.Max(1, Alignments.Of(order.Target).HonourGrade),
                    online = preySession != null && preySession.IsInWorld,
                    open = order.Open(now),
                    openSeconds = order.Open(now) ? (int)((order.OpenUntilMs - now) / 1000) : 0,
                    restSeconds = order.RestUntilMs > now ? (int)((order.RestUntilMs - now) / 1000) : 0,
                    expiresSeconds = (int)Math.Max(0, (order.TakenMs + (long)HeadHunts.OrderLife.TotalMilliseconds - now) / 1000),
                    seenX = preyMap?.PosX,
                    seenY = preyMap?.PosY,
                    bounty = HeadHunts.Bounty(order.Target, prey?.Level ?? 1, level),
                };
            }

            var onMe = HeadHunts.OrdersOn(id);
            return Bien(new
            {
                on = HeadHunts.On,
                character = me.State.CharacterName,
                level,
                side = alignment.Side,
                pvp = alignment.PvpOn,
                grade = alignment.HonourGrade,
                honour = alignment.Honour,
                dishonour = alignment.Dishonour,
                chapeuvepes = record.Chapeuvepes,
                kills = record.Kills,
                survivals = record.Survivals,
                failures = record.Failures,
                streak = record.Streak,
                lockedSeconds = record.LockedUntilMs > now ? (int)((record.LockedUntilMs - now) / 1000) : 0,
                cannot = HeadHunts.CannotHunt(id, level),
                order = hunt,
                huntedBy = onMe.Count,
                hunted = onMe.Any(o => o.Open(now)),
            });
        }

        private static Respuesta HeadHuntBoard(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            var board = HeadHunts.CandidatesFor(me.State.CharacterId, me.State.CharacterLevel);
            return Bien(new
            {
                prey = board.Select(c => new
                {
                    id = c.Id, name = c.Name, level = c.Level, breed = c.Breed, grade = c.Grade,
                    online = c.Online, bounty = c.Bounty, hunters = c.Hunters,
                }),
            });
        }

        private static Respuesta HeadHuntTake(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long target = Numero(cuerpo, "target");
            using (SessionContext.Push(me))
            {
                string result = HeadHunts.TakeAsync(me.State.CharacterId, me.State.CharacterLevel, target).GetAwaiter().GetResult();
                return result == "ok" ? Bien(new { taken = target }) : Mal(409, result);
            }
        }

        private static Respuesta HeadHuntAbandon(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            return HeadHunts.Abandon(me.State.CharacterId) ? Bien(new { abandoned = true }) : Mal(409, "none");
        }
    }
}
