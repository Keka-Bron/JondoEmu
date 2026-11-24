using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server
{
    /// <summary>How the operator leaves one yearly event.</summary>
    public enum EventMode
    {
        /// <summary>On its calendar: open while its days run, shut the rest of the year.</summary>
        Auto,
        /// <summary>Open whatever the day.</summary>
        On,
        /// <summary>Shut whatever the day.</summary>
        Off,
    }

    /// <summary>
    /// The yearly events as the operator leaves them (config\events.json): Kwismas Island, Al Howin,
    /// Vulkania and the rest, each on its calendar, held open or held shut, and whether the event
    /// monsters come out while it runs.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ServerSettings"/>, these apply at once: every «Sc» the game judges -- a
    /// quest, a dialogue reply, a passage, an NPC's presence, a monster's loot -- asks
    /// <see cref="SeasonalEvents.IsOn"/>, which reads <see cref="Current"/> each time. Opening an
    /// event while players are in the world needs no restart; they see its NPCs and monsters on
    /// the next map they load.
    /// </remarks>
    public sealed class EventSettings
    {
        public EventMode Kwismas { get; set; }
        public EventMode Halouine { get; set; }
        public EventMode Vulkania { get; set; }
        public EventMode SainBallotin { get; set; }
        public EventMode Pwak { get; set; }
        public EventMode Ripate { get; set; }

        /// <summary>Whether the event monsters come out while their event runs.</summary>
        public bool EventMonsters { get; set; } = true;

        /// <summary>
        /// The chance, in percent, that a group where an event monster may stand has one: a
        /// Gobball's group an Al Howin Gobball, a Frigost group a Kwismas Minotoball.
        /// </summary>
        public int EventMonsterChance { get; set; } = 35;

        /// <summary>The mode of the event a «Sc» value names; Auto for any other value.</summary>
        public EventMode ModeOf(int criterion) => SeasonalEvents.Canonical(criterion) switch
        {
            SeasonalEvents.Kwismas => Kwismas,
            SeasonalEvents.Halouine => Halouine,
            SeasonalEvents.Vulkania => Vulkania,
            SeasonalEvents.SainBallotin => SainBallotin,
            SeasonalEvents.Pwak => Pwak,
            SeasonalEvents.Ripate => Ripate,
            _ => EventMode.Auto,
        };

        /// <summary>Sets the mode of the event a «Sc» value names; any other value is ignored.</summary>
        public void SetMode(int criterion, EventMode mode)
        {
            switch (SeasonalEvents.Canonical(criterion))
            {
                case SeasonalEvents.Kwismas: Kwismas = mode; break;
                case SeasonalEvents.Halouine: Halouine = mode; break;
                case SeasonalEvents.Vulkania: Vulkania = mode; break;
                case SeasonalEvents.SainBallotin: SainBallotin = mode; break;
                case SeasonalEvents.Pwak: Pwak = mode; break;
                case SeasonalEvents.Ripate: Ripate = mode; break;
            }
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static EventSettings? _current;
        private static readonly object Gate = new();

        /// <summary>The events as they stand now.</summary>
        public static EventSettings Current
        {
            get
            {
                lock (Gate) return _current ??= Load();
            }
        }

        /// <summary>Raised after <see cref="Apply"/>, with the new settings.</summary>
        public static event Action<EventSettings>? Changed;

        /// <summary>For the tests: the settings to run with, or null to read the file again.</summary>
        internal static void UseForTests(EventSettings? settings)
        {
            lock (Gate) _current = settings;
        }

        /// <summary>The settings as the file has them, or the defaults (every event on its calendar).</summary>
        public static EventSettings Load()
        {
            string file = Paths.EventsFile;
            try
            {
                if (!File.Exists(file)) return new EventSettings();
                var read = JsonSerializer.Deserialize<EventSettings>(File.ReadAllText(file), Json) ?? new EventSettings();
                read.EventMonsterChance = Math.Clamp(read.EventMonsterChance, 0, 100);
                return read;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Events] {file} could not be read ({ex.Message}); every event follows its calendar.");
                return new EventSettings();
            }
        }

        /// <summary>Makes these the server's events at once, and writes them for the next start.</summary>
        public static void Apply(EventSettings settings, bool save = true)
        {
            settings.EventMonsterChance = Math.Clamp(settings.EventMonsterChance, 0, 100);
            lock (Gate) _current = settings;
            if (save)
            {
                try
                {
                    string file = Paths.EventsFile;
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, JsonSerializer.Serialize(settings, Json));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Events] The events could not be saved ({ex.Message}); they hold until the server stops.");
                }
            }
            Console.WriteLine("[Events] " + settings.Describe());
            Changed?.Invoke(settings);
        }

        /// <summary>A copy, for a window to edit.</summary>
        public EventSettings Copy() => (EventSettings)MemberwiseClone();

        /// <summary>One line for the log: each event's mode.</summary>
        public string Describe()
        {
            var parts = new List<string>();
            foreach (int id in SeasonalEvents.Events)
                parts.Add($"{SeasonalEvents.KeyOf(id)} {ModeOf(id)}");
            parts.Add(EventMonsters ? $"monsters {EventMonsterChance}%" : "monsters off");
            return string.Join(", ", parts);
        }
    }
}
