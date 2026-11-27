using System.Text.Json;
using Jondo.Unity.ServerMods;

namespace Cazacabezas.Server;

internal sealed class HuntResponses(IAlignmentWorld world, HeadHunts hunts)
{
    public string StatusJson(SelectedCharacter me)
    {
        long id = me.Id;
        int level = me.Level;
        var alignment = world.AlignmentOf(id);
        var record = hunts.Of(id);
        var order = hunts.OrderOf(id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        object? hunt = null;
        if (order != null)
        {
            var prey = world.FindCharacter(order.Target);
            var preySession = world.FindPresence(order.Target);
            var seen = order.Open(now) ? hunts.SeenAt(order, preySession) : null;
            hunt = new
            {
                target = order.Target,
                name = prey?.Name ?? "",
                level = prey?.Level ?? 0,
                breed = prey?.Breed ?? 0,
                grade = Math.Max(1, world.AlignmentOf(order.Target).HonourGrade),
                online = preySession != null && preySession.IsInWorld,
                open = order.Open(now),
                openSeconds = order.Open(now) ? (int)((order.OpenUntilMs - now) / 1000) : 0,
                restSeconds = order.RestUntilMs > now ? (int)((order.RestUntilMs - now) / 1000) : 0,
                expiresSeconds = (int)Math.Max(0, (order.TakenMs + (long)HeadHunts.OrderLife.TotalMilliseconds - now) / 1000),
                seenX = seen?.X,
                seenY = seen?.Y,
                bounty = hunts.Bounty(order.Target, prey?.Level ?? 1, level),
            };
        }

        var onMe = hunts.OrdersOn(id);
        return JsonSerializer.Serialize(new
        {
            on = hunts.On,
            character = me.Name,
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
            cannot = hunts.CannotHunt(id, level),
            order = hunt,
            huntedBy = onMe.Count,
            hunted = onMe.Any(o => o.Open(now)),
        });
    }

    public string BoardJson(SelectedCharacter me)
    {
        var board = hunts.CandidatesFor(me.Id, me.Level);
        return JsonSerializer.Serialize(new
        {
            prey = board.Select(c => new
            {
                id = c.Id, name = c.Name, level = c.Level, breed = c.Breed, grade = c.Grade,
                online = c.Online, bounty = c.Bounty, hunters = c.Hunters,
            }),
        });
    }
}
