using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The hero mode: the other characters of one's account connected beside one's own -- the
    /// master -- fighting at his side and played by hand from his client in their turns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ankama's Dofus 3 has none (« pas de mode Héros avant 2027 »); Wakfu's and Dofus Touch's
    /// are the model. A hero has no client of his own: his character is loaded into a session
    /// with no socket (<see cref="GameSession.SinSocket"/>), the way a player's is on selecting
    /// him, so that everything written against a session's state -- his sheet, his gear, his
    /// spells, his experience and his bag -- reads and writes his. That session is NOT in the
    /// registry: nobody sends to it, no map draws it, and a fight's people (Publico) leave it out.
    /// </para>
    /// <para>
    /// In a fight the master's client plays him as it plays a summon of its own -- his bar and
    /// his turns go to it (FightHandler.PlayAsHero, FightHeroes.cs) --, which the 3.6.10.11
    /// client does with no patch: measured on «.heros», 2026-11-15.
    /// </para>
    /// </remarks>
    public static class Heroes
    {
        /// <summary>The most heroes the settings may allow; the window's grid has eight places with the master.</summary>
        public const int Most = 7;

        /// <summary>One hero connected: his character and the session that holds it.</summary>
        public sealed class Hero
        {
            public long CharacterId { get; init; }
            public long MasterId { get; init; }
            public GameSession Session { get; init; } = null!;
            public DateTime Since { get; init; } = DateTime.UtcNow;
        }

        /// <summary>Each master's heroes, in the order they were connected.</summary>
        private static readonly ConcurrentDictionary<long, List<Hero>> _byMaster = new();

        /// <summary>Whose hero each connected hero is.</summary>
        private static readonly ConcurrentDictionary<long, Hero> _byHero = new();

        private static readonly object _lock = new();

        public static bool On => ServerSettings.Current.HeroMode;

        public static int Max => Math.Clamp(ServerSettings.Current.HeroesMax, 1, Most);

        /// <summary>A master's heroes, in their order.</summary>
        public static IReadOnlyList<Hero> Of(long masterId)
        {
            lock (_lock) return _byMaster.TryGetValue(masterId, out var list) ? list.ToList() : new List<Hero>();
        }

        /// <summary>The hero a character is connected as, or null.</summary>
        public static Hero? AsHero(long characterId) => _byHero.TryGetValue(characterId, out var hero) ? hero : null;

        /// <summary>The session of a hero, or null for anybody who is not one.</summary>
        public static GameSession? SessionOf(long characterId) => AsHero(characterId)?.Session;

        /// <summary>
        /// Connects one of the master's account's characters as his hero. "ok", or why not:
        /// off, self, account, online, already, full, fight, missing.
        /// </summary>
        public static string Connect(GameSession master, long heroId)
        {
            if (!On) return "off";
            long masterId = master.State.CharacterId;
            if (heroId == masterId) return "self";
            if (master.State.IsInFight) return "fight";
            if (!DatabaseManager.CharacterBelongsToAccount(heroId, master.AccountId)) return "account";
            // Somebody playing him from a client of his own: he is not free to be anybody's hero.
            if (SessionRegistry.FindByCharacter(heroId) != null) return "online";

            lock (_lock)
            {
                if (_byHero.ContainsKey(heroId)) return "already";
                var list = _byMaster.GetOrAdd(masterId, _ => new List<Hero>());
                if (list.Count >= Max) return "full";

                var session = GameSession.SinSocket();
                session.BindAccount(master.AccountId, master.ServerId, master.State.Language);
                using (SessionContext.Push(session))
                {
                    if (!Handlers.CharacterSelectionHandler.LoadForHero(heroId)) return "missing";
                }
                var hero = new Hero { CharacterId = heroId, MasterId = masterId, Session = session };
                list.Add(hero);
                _byHero[heroId] = hero;
            }
            Console.WriteLine($"[Heroes] {master.State.CharacterName} connects {SessionOf(heroId)?.State.CharacterName} ({heroId}) as a hero.");
            return "ok";
        }

        /// <summary>Disconnects a hero of his: saved as he stands. False when he is not one of his.</summary>
        public static bool Disconnect(long masterId, long heroId)
        {
            Hero? hero;
            lock (_lock)
            {
                if (!_byHero.TryGetValue(heroId, out hero) || hero.MasterId != masterId) return false;
                if (hero.Session.State.IsInFight) return false;
                Forget(hero);
            }
            Save(hero);
            return true;
        }

        /// <summary>
        /// Every hero of a master let go: he is leaving the world. A hero in a fight goes with
        /// it as his master does; he is saved off the arena all the same.
        /// </summary>
        public static void DisconnectAll(long masterId)
        {
            List<Hero> gone;
            lock (_lock)
            {
                if (!_byMaster.TryGetValue(masterId, out var list)) return;
                gone = list.ToList();
                foreach (var hero in gone) Forget(hero);
            }
            foreach (var hero in gone) Save(hero);
        }

        /// <summary>
        /// A character chosen to play from a client of his own stops being anybody's hero: he
        /// cannot be in the world twice.
        /// </summary>
        public static void Release(long characterId)
        {
            var hero = AsHero(characterId);
            if (hero == null) return;
            lock (_lock) Forget(hero);
            Save(hero);
        }

        private static void Forget(Hero hero)
        {
            _byHero.TryRemove(hero.CharacterId, out _);
            if (_byMaster.TryGetValue(hero.MasterId, out var list))
            {
                list.RemoveAll(h => h.CharacterId == hero.CharacterId);
                if (list.Count == 0) _byMaster.TryRemove(hero.MasterId, out _);
            }
        }

        private static void Save(Hero hero)
        {
            try
            {
                using (SessionContext.Push(hero.Session))
                {
                    hero.Session.State.IsInFight = false;
                    DatabaseManager.SaveCurrentCharacter();
                }
                Console.WriteLine($"[Heroes] {hero.Session.State.CharacterName} ({hero.CharacterId}) disconnected and saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Heroes] Could not save hero {hero.CharacterId}: {ex.Message}");
            }
        }
    }
}
