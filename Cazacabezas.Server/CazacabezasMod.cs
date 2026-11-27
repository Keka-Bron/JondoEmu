using System.Text.Json;
using System.Text.Json.Nodes;
using Jondo.Unity.ServerMods;
using Jondo.Unity.Launcher;

namespace Cazacabezas.Server;

public sealed class CazacabezasMod : IServerMod
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    internal HeadHunts? Hunts { get; private set; }
    private Timer? _timer;
    private int _ticking;

    public void Initialize(IServerModHost host)
    {
        var world = host.GetService<IAlignmentWorld>();
        var settings = host.Settings.Deserialize<HuntSettings>() ?? new();
        var hunts = Hunts = new HeadHunts(world, host.GetService<IWorldStorage>(),
            host.GetService<ISimulatedPlayerWorld>(), settings, host.Log);
        var responses = new HuntResponses(world, hunts);
        Register("status", (hunter, _) => new(200, responses.StatusJson(hunter)));
        Register("board", (hunter, _) => new(200, responses.BoardJson(hunter)));
        Register("take", (hunter, body) => Take(hunts, hunter, body));
        Register("abandon", (hunter, _) => hunts.Abandon(hunter.Id)
            ? new(200, """{"abandoned":true}""")
            : Conflict("none"));
        host.RegisterHook<IPlayerActivityPolicy>(hunts);
        host.RegisterHook<IPvpEvents>(hunts);
        host.RegisterHook<IDialogueCurrency>(hunts);
        host.RegisterHook<ISimulatedPlayerEncounters>(hunts);
        host.RegisterCleanup(Shutdown);
        _timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
            try { hunts.TickAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { host.Log($"The hunt clock failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        void Register(string name, Func<SelectedCharacter, string, ServerModControlResponse> handle)
        {
            host.RegisterAuthenticatedControlRoute(Contract.Prefijo + "headhunts/" + name, request =>
            {
                var hunter = world.FindSelectedCharacter(request.AccountId);
                return hunter == null ? Conflict("world") : handle(hunter, request.Body);
            });
        }
    }

    public void StartGameplay() => _timer?.Change(TickInterval, TickInterval);

    public void Shutdown()
    {
        var timer = Interlocked.Exchange(ref _timer, null);
        if (timer != null) timer.DisposeAsync().GetAwaiter().GetResult();
    }

    private static ServerModControlResponse Take(HeadHunts hunts, SelectedCharacter hunter, string body)
    {
        long target = ReadTarget(body);
        string result = hunts.TakeAsync(hunter.Id, hunter.Level, target).GetAwaiter().GetResult();
        return result == "ok"
            ? new(200, new JsonObject { ["taken"] = target }.ToJsonString())
            : Conflict(result);
    }

    private static long ReadTarget(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("target", out var target) &&
                target.ValueKind == JsonValueKind.Number && target.TryGetInt64(out long id))
                return id;
        }
        catch (JsonException) { }
        return 0;
    }

    private static ServerModControlResponse Conflict(string error)
        => new(409, new JsonObject { ["error"] = error }.ToJsonString());
}
