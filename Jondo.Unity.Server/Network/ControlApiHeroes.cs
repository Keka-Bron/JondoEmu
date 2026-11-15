using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The Heroes mod's side of the control API: the account's characters with who is connected
    /// as a hero, connecting and disconnecting one, and each one's portrait -- for the account's
    /// character in the world, its master.
    /// </summary>
    /// <remarks>
    /// As the Cazacabezas's: asked with the session's own token (JONDO_PLAYER_TOKEN), about the
    /// account's own characters only, and every rule is <see cref="Heroes"/>'s.
    /// </remarks>
    public static partial class ControlApi
    {
        private static Respuesta HeroesList(long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long masterId = me.State.CharacterId;
            var heroes = Heroes.Of(masterId).Select(h => h.CharacterId).ToList();
            var mine = DatabaseManager.GetCharactersByAccountId(account, me.ServerId);
            // Their portraits drawn ahead, while the window is not open yet: the first drawing
            // opens the client's bundles and takes a while.
            HeroPortraits.Warm(mine.Select(CharacterLookBuilder.Drawable));
            var characters = mine
                .Select(c =>
                {
                    bool master = c.Id == masterId;
                    var hero = Heroes.SessionOf(c.Id);
                    var (life, maxLife) = master ? LifeOf(me) : hero != null ? LifeOf(hero) : (0, 0);
                    var stats = master ? StatsOf(me) : hero != null ? StatsOf(hero) : default;
                    return new
                    {
                        id = c.Id,
                        name = c.Name ?? "",
                        level = hero?.State.CharacterLevel ?? c.Level,
                        breed = c.Breed,
                        sex = c.Sex,
                        master,
                        hero = heroes.Contains(c.Id),
                        // Played from a client of his own, or somebody else's hero.
                        busy = !master && !heroes.Contains(c.Id)
                               && (SessionRegistry.FindByCharacter(c.Id) != null || Heroes.AsHero(c.Id) != null),
                        inFight = hero?.State.IsInFight ?? false,
                        order = master ? -1 : heroes.IndexOf(c.Id),
                        life,
                        maxLife,
                        ap = stats.Ap,
                        mp = stats.Mp,
                        initiative = stats.Initiative,
                        prospecting = stats.Prospecting,
                        wisdom = stats.Wisdom,
                        autoPass = Heroes.PassesOnHisOwn(c.Id),
                        // His look as the game's protocol carries it, on foot: the mod draws him
                        // with the client's own portrait, seated or standing.
                        look = LookOf(c),
                    };
                })
                .OrderBy(c => c.master ? 0 : c.hero ? 1 : 2).ThenBy(c => c.order).ThenByDescending(c => c.level)
                .ToList();
            int taken = Handlers.CharacterCreationHandler.SlotsTaken(DatabaseManager.GetServers(),
                                                                       DatabaseManager.GetCharactersByAccountId(account), me.ServerId);
            return Bien(new
            {
                on = Heroes.On,
                max = Heroes.Max,
                connected = 1 + heroes.Count,
                inFight = me.State.IsInFight,
                lootTo = Heroes.LootTo(masterId),
                // How many more characters the account may make: the window's «create» on its free places.
                slotsLeft = Math.Max(0, Connection.ConnectionService.MaxCharactersPerServer - taken),
                characters,
            });
        }

        /// <summary>
        /// A character made on the account from the window -- a name, a breed, a sex, a head and
        /// his colours, the breed's for any not sent --, and connected as a hero at once when there is
        /// room. Why not, else: the creation's (CharacterCreationHandler.Make).
        /// </summary>
        private static Respuesta HeroesCreate(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            string name = Texto(cuerpo, "name").Trim();
            // The colours the creation screen chose, c1 to c6, as it sends them itself; the breed's for any not sent.
            var colors = Enumerable.Range(1, 6)
                .Select(i => cuerpo.Contains("\"c" + i + "\"") ? Numero(cuerpo, "c" + i) : -1L).ToList();
            var (id, why) = Handlers.CharacterCreationHandler.Make(account, me.ServerId, name, (int)Numero(cuerpo, "breed"),
                                                                    (int)Numero(cuerpo, "sex"), (int)Numero(cuerpo, "head"), colors);
            if (id == 0) return Mal(409, why);
            string connected = Heroes.Connect(me, id);
            Console.WriteLine($"[Heroes] {me.State.CharacterName} makes {name} ({id}) from the window; connected: {connected}.");
            return Bien(new { id, connected = connected == "ok", why = connected });
        }

        /// <summary>
        /// The look a hero being made would have -- a breed, a sex, a head, his breed's colours --,
        /// as the protocol's EntityLook in base64: the creation screen's platform draws it.
        /// </summary>
        private static Respuesta HeroesPreview(string cuerpo)
        {
            int breed = (int)Numero(cuerpo, "breed"), sex = (int)Numero(cuerpo, "sex"), head = (int)Numero(cuerpo, "head");
            if (breed < 1 || sex is < 0 or > 1) return Mal(400, "breed");
            try
            {
                return Bien(new { look = Convert.ToBase64String(CharacterLookBuilder.BuildLook(
                    new DbCharacter { Breed = breed, Sex = sex, HeadId = head }, onFoot: true)) });
            }
            catch (Exception ex)
            {
                return Mal(500, ex.Message);
            }
        }

        /// <summary>A name for a new hero, as the creation screen's die draws one: one nobody has.</summary>
        private static Respuesta HeroesName(long account)
        {
            string name = Handlers.CharacterCreationHandler.SuggestName();
            for (int i = 0; i < 10 && DatabaseManager.CharacterNameTaken(name); i++) name = Handlers.CharacterCreationHandler.SuggestName();
            return Bien(new { name });
        }

        /// <summary>A character's EntityLook message in base64, on foot; "" when it cannot be made.</summary>
        private static string LookOf(DbCharacter character)
        {
            try { return Convert.ToBase64String(CharacterLookBuilder.BuildLook(character, onFoot: true)); }
            catch { return ""; }
        }

        /// <summary>A connected character's points and what a group's tooltip shows of him.</summary>
        private static (int Ap, int Mp, int Initiative, int Prospecting, int Wisdom) StatsOf(GameSession session)
        {
            try
            {
                var sheet = Handlers.FightHandler.SheetOf(session);
                using (SessionContext.Push(session))
                {
                    int wisdom = session.State.TotalWisdom + session.Combat.GetEquipBonus(Stat.Wisdom);
                    Equipment.Bonuses().TryGetValue(Stat.Prospecting, out long fromGear);
                    return (sheet.MaxAP, sheet.MaxMP, sheet.Initiative, 100 + session.State.TotalChance / 10 + (int)fromGear, wisdom);
                }
            }
            catch
            {
                return default;
            }
        }

        /// <summary>A connected character's life: his fighter's in a fight, his resting life outside one.</summary>
        private static (int Life, int Max) LifeOf(GameSession session)
        {
            long id = session.State.CharacterId;
            if (Handlers.FightHandler.FightOf(id)?.Buscar(id) is { } fighter) return (Math.Max(0, fighter.CurrentHP), fighter.MaxHP);
            try
            {
                using (SessionContext.Push(session))
                {
                    int max = session.Combat.GetPlayerMaxHp();
                    return (RestingLife.LifeAt(id, max, DateTime.UtcNow), max);
                }
            }
            catch
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// A hero's sheet for the mod's three windows: his characteristics, his spells and his bag,
        /// as ids the client names and draws itself. Of a hero of his only.
        /// </summary>
        private static Respuesta HeroesSheet(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long id = Numero(cuerpo, "id");
            var hero = Heroes.AsHero(id);
            if (hero == null || hero.MasterId != me.State.CharacterId) return Mal(404, "hero");
            var session = hero.Session;
            var state = session.State;
            var sheet = Handlers.FightHandler.SheetOf(session);
            int wisdom, prospecting;
            List<SpellTable.KnownSpell> spells;
            using (SessionContext.Push(session))
            {
                wisdom = state.TotalWisdom + session.Combat.GetEquipBonus(Stat.Wisdom);
                Equipment.Bonuses().TryGetValue(Stat.Prospecting, out long fromGear);
                prospecting = 100 + state.TotalChance / 10 + (int)fromGear;
                spells = SpellTable.KnownFor(state.Breed, state.CharacterLevel, SpellChoices.Chosen,
                                             LearnedSpells.For(id, session.AccountId));
            }
            var (life, maxLife) = LifeOf(session);
            int level = state.CharacterLevel;
            return Bien(new
            {
                id,
                name = state.CharacterName,
                breed = state.Breed,
                level,
                xp = state.Experience,
                xpFloor = ExperienceTable.LevelFloor(level),
                xpNext = ExperienceTable.LevelFloor(level + 1),
                kamas = state.Kamas,
                points = state.CharacterRemainingPoints,
                life,
                maxLife,
                ap = sheet.MaxAP,
                mp = sheet.MaxMP,
                initiative = sheet.Initiative,
                prospecting,
                wisdom,
                strength = sheet.Strength,
                intelligence = sheet.Intelligence,
                chance = sheet.Chance,
                agility = sheet.Agility,
                vitality = sheet.Vitality,
                range = sheet.Range,
                power = sheet.Power,
                critical = sheet.CriticalBonus,
                summons = sheet.Otra(26),
                resist = new[] { sheet.NeutralResPct, sheet.EarthResPct, sheet.FireResPct, sheet.WaterResPct, sheet.AirResPct },
                spells = spells.Select(s => new { id = s.SpellId, grade = s.Grade }),
                items = state.GetInventoryCopy().Select(i => new { gid = i.ItemId, quantity = i.Quantity, position = i.Position }),
            });
        }

        /// <summary>
        /// A hero taken in hand at once, in the same connection (HeroSwitch); with { later: 1 },
        /// at the client's next character list instead (Heroes.AskSwitch).
        /// </summary>
        private static Respuesta HeroesSwitch(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long id = Numero(cuerpo, "id");
            if (Numero(cuerpo, "later") != 0)
                return Heroes.AskSwitch(me, id) ? Bien(new { switching = true }) : Mal(409, "none");
            string result = HeroSwitch.SwitchAsync(me, id).GetAwaiter().GetResult();
            return result == "ok" ? Bien(new { switched = true }) : Mal(409, result);
        }

        private static Respuesta HeroesAutoPass(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            bool on = Numero(cuerpo, "on") != 0;
            return Heroes.SetAutoPass(me.State.CharacterId, Numero(cuerpo, "id"), on) ? Bien(new { autoPass = on }) : Mal(409, "none");
        }

        private static Respuesta HeroesLootTo(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            long masterId = me.State.CharacterId;
            return Heroes.SetLootTo(masterId, Numero(cuerpo, "id")) ? Bien(new { lootTo = Heroes.LootTo(masterId) }) : Mal(409, "none");
        }

        private static Respuesta HeroesConnect(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            string result = Heroes.Connect(me, Numero(cuerpo, "id"));
            return result == "ok" ? Bien(new { connected = true }) : Mal(409, result);
        }

        private static Respuesta HeroesDisconnect(string cuerpo, long account)
        {
            var me = InTheWorld(account);
            if (me == null) return Mal(409, "world");
            return Heroes.Disconnect(me.State.CharacterId, Numero(cuerpo, "id"))
                ? Bien(new { disconnected = true })
                : Mal(409, "none");
        }

        /// <summary>One of the account's characters drawn as the launcher draws them, as a PNG in base64.</summary>
        private static Respuesta HeroesPortrait(string cuerpo, long account)
        {
            long id = Numero(cuerpo, "id");
            if (!DatabaseManager.CharacterBelongsToAccount(id, account)) return Mal(404, "character");
            var character = DatabaseManager.GetCharacterById(id);
            if (character == null) return Mal(404, "character");
            byte[]? png = HeroPortraits.Of(CharacterLookBuilder.Drawable(character));
            return png == null ? Mal(404, "portrait") : Bien(new { id, png = Convert.ToBase64String(png) });
        }
    }

    /// <summary>
    /// The heroes' portraits, out of the Dofus client's own bones -- the launcher's drawer
    /// (Jondo.Unity.Sprites) --, kept by look: one drawing per look for the server's run.
    /// </summary>
    internal static class HeroPortraits
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, byte[]?> _drawn = new();
        private static Jondo.Unity.Sprites.NpcSprites? _drawer;

        private static readonly HashSet<string> _warming = new();

        /// <summary>The looks drawn in the background, each once, so that asking for them later is instant.</summary>
        public static void Warm(IEnumerable<string> looks)
        {
            var todo = new List<string>();
            lock (_lock)
            {
                foreach (string look in looks)
                    if (!string.IsNullOrEmpty(look) && !_drawn.ContainsKey(look) && _warming.Add(look)) todo.Add(look);
            }
            if (todo.Count == 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (string look in todo) Of(look);
            });
        }

        /// <summary>The PNG of a look, or null when it cannot be drawn here (no client, no display).</summary>
        public static byte[]? Of(string look)
        {
            if (string.IsNullOrEmpty(look)) return null;
            lock (_lock)
            {
                if (_drawn.TryGetValue(look, out var known)) return known;
                byte[]? png = null;
                try
                {
                    // Four times the card's height: the drawer does not smooth, the client's shrinking does.
                    _drawer ??= new Jondo.Unity.Sprites.NpcSprites { Height = 384 };
                    var bitmap = _drawer.Of(look);
                    if (bitmap != null)
                    {
                        using var stream = new MemoryStream();
                        bitmap.Save(stream);
                        png = stream.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Heroes] The portrait could not be drawn: {ex.Message}");
                }
                _drawn[look] = png;
                return png;
            }
        }
    }
}
