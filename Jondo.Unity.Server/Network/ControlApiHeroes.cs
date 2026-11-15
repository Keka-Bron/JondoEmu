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
            var characters = DatabaseManager.GetCharactersByAccountId(account, me.ServerId)
                .Select(c =>
                {
                    bool master = c.Id == masterId;
                    var hero = Heroes.SessionOf(c.Id);
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
                    };
                })
                .OrderBy(c => c.master ? 0 : c.hero ? 1 : 2).ThenBy(c => c.order).ThenByDescending(c => c.level)
                .ToList();
            return Bien(new
            {
                on = Heroes.On,
                max = Heroes.Max,
                connected = 1 + heroes.Count,
                inFight = me.State.IsInFight,
                characters,
            });
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
            byte[]? png = HeroPortraits.Of(BreedLookTable.Drawable(character));
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
