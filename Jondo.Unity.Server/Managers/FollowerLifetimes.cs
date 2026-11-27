using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The followers that walk with the character for so many minutes and no more, counted from the
    /// moment he was given them (their «Recibido el», 805), as content/quests/followers.json says. The
    /// item carries no expiry the client would show: the domesticated pingwobble (11265) of «Get Your
    /// Fresh Snapper Here!» only says it in its description, «El único incoveniente es que su
    /// digestión comienza al cabo de treinta minutos», and Al Fabeticorder says it again as he hands
    /// it over; «Vous avez cependant 30 minutes maximum pour arriver à destination» (Dofus pour les
    /// noobs). Once they are up, the follower leaves his slot (AlterationHandler.TickAsync).
    /// </summary>
    public static class FollowerLifetimes
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "quests/followers.json";

        private static Dictionary<int, int>? _minutes;
        private static readonly object _lock = new();

        /// <summary>The minutes a follower of this template walks with the character, or 0 for no limit.</summary>
        public static int MinutesOf(int template)
        {
            lock (_lock)
            {
                if (_minutes == null)
                {
                    var minutes = new Dictionary<int, int>();
                    try
                    {
                        string path = Paths.ContentFile(AuthoredFile);
                        if (File.Exists(path))
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(path));
                            foreach (var row in doc.RootElement.GetProperty("followers").EnumerateArray())
                                if (row.TryGetProperty("item", out var item) && row.TryGetProperty("minutes", out var lasts))
                                    minutes[item.GetInt32()] = lasts.GetInt32();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                    }
                    _minutes = minutes;
                }
                return _minutes.TryGetValue(template, out int found) ? found : 0;
            }
        }

        /// <summary>Whether this item's minutes are up at <paramref name="nowSeconds"/>.</summary>
        public static bool Ended(Equipment.Item item, long nowSeconds)
            => Ended(MinutesOf(item.Template), item.Effects, nowSeconds);

        /// <summary>
        /// Whether a follower that lasts <paramref name="minutes"/> and carries these effects is gone at
        /// <paramref name="nowSeconds"/>: one with no date of receipt has nothing to count from.
        /// </summary>
        internal static bool Ended(int minutes, IEnumerable<Equipment.ItemEffect> effects, long nowSeconds)
        {
            if (minutes <= 0) return false;
            long received = effects.Where(e => e.Effect == Equipment.ReceivedOn).Select(e => e.Value).DefaultIfEmpty(0).Max();
            return received > 0 && received + minutes * 60L <= nowSeconds;
        }

        /// <summary>Read the file again.</summary>
        public static void Forget()
        {
            lock (_lock) _minutes = null;
        }
    }
}
