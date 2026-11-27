using System.Text.Json;
using Jondo.Unity.ServerMods;

namespace Heroes.Server;

public sealed partial class HeroesMod
{
    private static readonly JsonSerializerOptions SheetJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void RegisterCharacterRoutes(IServerModHost host, ICharacterWorld world, ICompanionWorld companions, HeroGroups groups)
    {
        Register("list", List);
        Register("quests", QuestSync);
        Register("sheet", Sheet);
        Register("create", Create);
        Register("name", _ => SuggestName());
        Register("preview", Preview);
        Register("portrait", Portrait);

        void Register(string name, Func<AuthenticatedControlRequest, ServerModControlResponse> handler)
            => host.RegisterAuthenticatedControlRoute(Path(name), handler);

        ServerModControlResponse List(AuthenticatedControlRequest request)
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            long masterId = master.CharacterId;
            var heroes = groups.Of(masterId).Select(hero => hero.CharacterId).ToList();
            var mine = world.CharactersOf(master);
            world.WarmPortraits(mine.Select(c => c.PortraitLook));
            var characters = mine.Select(c =>
            {
                bool isMaster = c.Id == masterId;
                var member = groups.Find(c.Id);
                var follower = member?.Session;
                bool isHero = heroes.Contains(c.Id);
                var stats = isMaster ? world.CharacterVitals(master)
                    : follower != null ? world.CharacterVitals(follower) : default;
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    level = follower != null ? world.CharacterLevel(follower) : c.Level,
                    breed = c.Breed,
                    sex = c.Sex,
                    master = isMaster,
                    hero = isHero,
                    busy = !isMaster && !isHero && (companions.IsClientOnline(c.Id) || member != null),
                    inFight = follower?.IsInFight ?? false,
                    order = isMaster ? -1 : heroes.IndexOf(c.Id),
                    life = stats.Life,
                    maxLife = stats.MaxLife,
                    ap = stats.Ap,
                    mp = stats.Mp,
                    initiative = stats.Initiative,
                    prospecting = stats.Prospecting,
                    wisdom = stats.Wisdom,
                    autoPass = member?.AutoPass ?? false,
                    questSync = isHero && groups.SharesQuests(c.Id),
                    questLag = isHero ? groups.LagsOf(master, c.Id).Select(lag => new
                    {
                        quest = lag.QuestId,
                        step = lag.StepId,
                        reason = lag.Reason.ToString().ToLowerInvariant(),
                        item = lag.ItemId,
                        count = lag.ItemCount,
                    }).ToArray() : [],
                    look = c.Look,
                };
            }).OrderBy(c => c.master ? 0 : c.hero ? 1 : 2)
                .ThenBy(c => c.order).ThenByDescending(c => c.level).ToArray();
            return Reply(200, new
            {
                on = true,
                max = groups.Max,
                connected = 1 + heroes.Count,
                inFight = master.IsInFight,
                lootTo = groups.LootTo(masterId),
                slotsLeft = world.CharacterSlotsLeft(master),
                characters,
            });
        }

        ServerModControlResponse QuestSync(AuthenticatedControlRequest request)
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            using var turn = master.EnterTurnAsync().GetAwaiter().GetResult();
            bool on = Number(request.Body, "on") != 0;
            return groups.SetQuestSync(master.CharacterId, Number(request.Body, "id"), on)
                ? Reply(200, new { questSync = on }) : Error("none");
        }

        ServerModControlResponse Sheet(AuthenticatedControlRequest request)
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            var hero = groups.Find(Number(request.Body, "id"));
            return hero == null || hero.ControllerId != master.CharacterId
                ? Reply(404, new { error = "hero" })
                : new(200, JsonSerializer.Serialize(world.ReadCharacterSheet(hero.Session), SheetJson));
        }

        ServerModControlResponse Create(AuthenticatedControlRequest request)
        {
            var master = world.FindSelectedSession(request.AccountId);
            if (master == null) return Error("world");
            var body = request.Body;
            string name = Text(body, "name").Trim();
            var colors = Enumerable.Range(1, 6)
                .Select(i => body.Contains("\"c" + i + "\"") ? Number(body, "c" + i) : -1L).ToArray();
            var (id, why) = world.CreateCharacter(master, name, (int)Number(body, "breed"),
                (int)Number(body, "sex"), (int)Number(body, "head"), colors);
            if (id == 0) return Error(why);
            using var turn = master.EnterTurnAsync().GetAwaiter().GetResult();
            string connected = groups.Connect(master, id);
            host.Log($"{master.CharacterName} makes {name} ({id}) from the window. Connected: {connected}.");
            return Reply(200, new { id, connected = connected == "ok", why = connected });
        }

        ServerModControlResponse SuggestName()
        {
            string name = world.SuggestName();
            for (int i = 0; i < 10 && world.CharacterNameTaken(name); i++) name = world.SuggestName();
            return Reply(200, new { name });
        }

        ServerModControlResponse Preview(AuthenticatedControlRequest request)
        {
            string body = request.Body;
            int breed = (int)Number(body, "breed"), sex = (int)Number(body, "sex"), head = (int)Number(body, "head");
            if (breed < 1 || sex is < 0 or > 1) return Reply(400, new { error = "breed" });
            try { return Reply(200, new { look = world.PreviewLook(breed, sex, head) }); }
            catch (Exception ex) { return Reply(500, new { error = ex.Message }); }
        }

        ServerModControlResponse Portrait(AuthenticatedControlRequest request)
        {
            long id = Number(request.Body, "id");
            if (!world.AccountOwnsCharacter(request.AccountId, id) || world.PortraitLook(id) is not { } look)
                return Reply(404, new { error = "character" });
            byte[]? png = world.RenderPortrait(look);
            return png == null ? Reply(404, new { error = "portrait" })
                : Reply(200, new { id, png = Convert.ToBase64String(png) });
        }
    }

    private static string Text(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }
}
