using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.ServerMods;

namespace Heroes.Server;

public sealed partial class HeroesMod : IServerMod
{
    internal HeroGroups? Groups { get; private set; }

    public void Initialize(IServerModHost host)
    {
        var world = host.GetService<ICharacterWorld>();
        var settings = host.Settings.Deserialize<HeroesSettings>() ?? new();
        var companions = host.GetService<ICompanionWorld>();
        var groups = Groups = new HeroGroups(companions, host.GetService<IPartyWorld>(),
            host.GetService<IMovementWorld>(), host.GetService<IQuestWorld>(), settings, host.Log);
        host.RegisterHook<ICompanionSessionPolicy>(groups);
        host.RegisterHook<IPartyMembershipPolicy>(groups);
        host.RegisterHook<ITradeRoutingPolicy>(groups);
        host.RegisterHook<ICompanionMovement>(groups);
        host.RegisterHook<IQuestSharingPolicy>(groups);
        host.RegisterHook<IDungeonPartyPolicy>(groups);
        host.RegisterHook<IFightParticipantPolicy>(groups);

        host.RegisterAuthenticatedControlRoute(Path("autopass"), request =>
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            bool on = Number(request.Body, "on") != 0;
            return groups.SetAutoPass(master.CharacterId, Number(request.Body, "id"), on)
                ? Reply(200, new { autoPass = on }) : Error("none");
        });
        host.RegisterAuthenticatedControlRoute(Path("loot"), request =>
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            long id = master.CharacterId;
            return groups.SetLootTo(id, Number(request.Body, "id"))
                ? Reply(200, new { lootTo = groups.LootTo(id) }) : Error("none");
        });
        RegisterCharacterRoutes(host, world, companions, groups);
        RegisterLifecycle("connect", (master, id, _) =>
        {
            string result = groups.Connect(master, id);
            return result == "ok" ? Reply(200, new { connected = true }) : Error(result);
        });
        RegisterLifecycle("disconnect", (master, id, _) =>
            groups.Disconnect(master.CharacterId, id) ? Reply(200, new { disconnected = true }) : Error("none"));
        RegisterLifecycle("switch", (master, id, later) =>
        {
            if (later)
                return groups.AskSwitch(master, id) ? Reply(200, new { switching = true }) : Error("none");
            string result = groups.SwitchAsync(master, id).GetAwaiter().GetResult();
            return result == "ok" ? Reply(200, new { switched = true }) : Error(result);
        }, takeTurn: false);

        void RegisterLifecycle(string name, Func<ICharacterSession, long, bool, ServerModControlResponse> handle,
            bool takeTurn = true)
            => host.RegisterAuthenticatedControlRoute(Path(name), request =>
            {
                var master = world.FindSelectedSession(request.AccountId);
                if (master == null) return Error("world");
                long id = Number(request.Body, "id");
                bool later = name == "switch" && Number(request.Body, "later") != 0;
                using var turn = takeTurn || later ? master.EnterTurnAsync().GetAwaiter().GetResult() : null;
                return master.IsInWorld ? handle(master, id, later) : Error("world");
            });
    }

    public void Shutdown() => Groups?.Stop();

    private static string Path(string route) => Contract.Prefijo + "heroes/" + route;
    private static ServerModControlResponse Reply(int status, object data) => new(status, JsonSerializer.Serialize(data));
    private static ServerModControlResponse Error(string reason) => Reply(409, new { error = reason });

    private static long Number(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
        }
        catch (JsonException) { return 0; }
        catch (InvalidOperationException) { return 0; }
    }
}
