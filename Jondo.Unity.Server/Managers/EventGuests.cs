using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using static Jondo.Unity.Server.Managers.MobSpawnManager;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The monsters a yearly event brings out in the wild (content/world/event_monsters.json): an
    /// Al Howin Gobball in a Gobball's group while Al Howin runs, a Kwismas Minotoball among
    /// Frigost's monsters while Kwismas does.
    /// </summary>
    /// <remarks>
    /// Judged on a map's load, as the unique monsters are placed: each group once per event, with
    /// the Events page's chance (<see cref="EventSettings.EventMonsterChance"/>). A group keeps its
    /// guest while the event runs and gets its own member back on the first load after it closes.
    /// A group in a fight, a dungeon room's and a composed group (its leaders) are never touched.
    /// </remarks>
    public static class EventGuests
    {
        public const string AuthoredFile = "world/event_monsters.json";

        /// <summary>One event's monsters.</summary>
        internal sealed class Rule
        {
            public int Event { get; init; }

            /// <summary>Monster -> the event's version of it.</summary>
            public Dictionary<int, int> Replace { get; init; } = new();

            /// <summary>The area whose groups take one of <see cref="Joins"/>; -1 for none.</summary>
            public int Area { get; init; } = -1;

            public int[] Joins { get; init; } = Array.Empty<int>();
        }

        /// <summary>The guest a group wears and the member it stands for.</summary>
        private sealed record Wearing(int Event, int Slot, int Guest, MobMember Original);

        /// <summary>An event's monster the world itself put in a group, kept out while its event is shut.</summary>
        private sealed record Hiding(int Event, int Slot, MobMember Guest, int StandIn);

        private static readonly object Gate = new();
        private static List<Rule>? _rules;
        private static HashSet<int> _guests = new();

        /// <summary>(group, event) -> whether the dice gave that group a guest.</summary>
        private static readonly Dictionary<(long Group, int Event), bool> Decided = new();
        private static readonly Dictionary<long, Wearing> Worn = new();
        private static readonly Dictionary<long, List<Hiding>> Hidden = new();

        /// <summary>Guest -> its event, and guest -> the monster it is the event's version of.</summary>
        private static Dictionary<int, int> _eventOf = new();
        private static Dictionary<int, int> _originalOf = new();

        /// <summary>A number from 0 to n-1. The tests load the dice.</summary>
        internal static Func<int, int> Dice = n => Random.Shared.Next(n);

        internal static IReadOnlyList<Rule> Rules
        {
            get
            {
                lock (Gate)
                {
                    if (_rules == null) Use(Load());
                    return _rules!;
                }
            }
        }

        /// <summary>
        /// Reads content/world/event_monsters.json again on next use (a content reload). The groups
        /// already dressed or hidden keep what they stood for, so they still go back as they were.
        /// </summary>
        public static void Forget()
        {
            lock (Gate) _rules = null;
        }

        /// <summary>Whether that monster is an event's guest.</summary>
        public static bool IsGuest(int monster)
        {
            _ = Rules;
            lock (Gate) return _guests.Contains(monster);
        }

        /// <summary>For the tests: these rules, or null to read the file again; forgets every group.</summary>
        internal static void UseForTests(List<Rule>? rules)
        {
            lock (Gate)
            {
                _rules = null;
                if (rules != null) Use(rules);
                Decided.Clear();
                Worn.Clear();
                Hidden.Clear();
            }
        }

        private static void Use(List<Rule> rules)
        {
            _rules = rules;
            _guests = rules.SelectMany(r => r.Replace.Values.Concat(r.Joins)).ToHashSet();
            _eventOf = new();
            _originalOf = new();
            foreach (var rule in rules)
            {
                foreach (var (from, to) in rule.Replace)
                {
                    _eventOf.TryAdd(to, rule.Event);
                    _originalOf.TryAdd(to, from);
                }
                foreach (int guest in rule.Joins) _eventOf.TryAdd(guest, rule.Event);
            }
        }

        private static List<Rule> Load()
        {
            var rules = new List<Rule>();
            string file = Paths.ContentFile(AuthoredFile);
            if (!File.Exists(file)) return rules;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("events", out var events)) return rules;
                foreach (var e in events.EnumerateArray())
                {
                    var replace = new Dictionary<int, int>();
                    if (e.TryGetProperty("replace", out var r))
                        foreach (var p in r.EnumerateObject())
                            if (int.TryParse(p.Name, out int from)) replace[from] = p.Value.GetInt32();
                    rules.Add(new Rule
                    {
                        Event = e.GetProperty("event").GetInt32(),
                        Replace = replace,
                        Area = e.TryGetProperty("area", out var a) ? a.GetInt32() : -1,
                        Joins = e.TryGetProperty("joins", out var j) ? j.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>(),
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Events] {AuthoredFile} could not be read ({ex.Message}); no event monsters.");
            }
            return rules;
        }

        /// <summary>
        /// Gives the groups of a map being loaded their event guests, and takes back those whose
        /// event closed. Under <see cref="MobSpawnManager"/>'s lock. Returns how many groups changed.
        /// </summary>
        /// <param name="area">The map's area, for the rules that join an area's groups.</param>
        /// <param name="monster">The monster with that id, or null.</param>
        /// <param name="fighting">Whether that group is in a fight.</param>
        /// <param name="isOn">Whether that event runs now.</param>
        /// <param name="ordinary">Whether a member may give way to a guest: not a unique or a wanted monster.</param>
        /// <param name="zone">The monsters of the map's zone, for an event monster with nobody to stand in for it.</param>
        internal static int Dress(IEnumerable<MobGroup> groups, int area, EventSettings settings,
                                  Func<int, MonsterData?> monster, Func<long, bool> fighting,
                                  Func<int, bool> isOn, Func<int, bool> ordinary,
                                  Func<IReadOnlyList<int>>? zone = null)
        {
            var rules = Rules;
            int changed = 0;
            bool Shows(int ev) => settings.EventMonsters && isOn(ev);
            lock (Gate)
            {
                foreach (var group in groups)
                {
                    if (group.Modular || group.Leaders > 0 || group.Members.Count == 0 || fighting(group.MobId)) continue;

                    bool wearing = false;
                    if (Worn.TryGetValue(group.MobId, out var worn))
                    {
                        if (Shows(worn.Event)) wearing = true;
                        else
                        {
                            Worn.Remove(group.MobId);
                            if (worn.Slot < group.Members.Count && group.Members[worn.Slot].Monster?.Id == worn.Guest)
                            {
                                var members = new List<MobMember>(group.Members) { [worn.Slot] = worn.Original };
                                group.Members = members;
                                changed++;
                            }
                        }
                    }

                    if (Uncover(group, Shows, monster, zone, ordinary)) changed++;

                    if (wearing || !settings.EventMonsters || settings.EventMonsterChance <= 0) continue;
                    foreach (var rule in rules)
                    {
                        if (!isOn(rule.Event)) continue;
                        var slots = Enumerable.Range(0, group.Members.Count).Where(i =>
                        {
                            int id = group.Members[i].Monster?.Id ?? 0;
                            if (rule.Replace.Count > 0) return rule.Replace.ContainsKey(id);
                            return rule.Area >= 0 && rule.Area == area && rule.Joins.Length > 0
                                   && !_guests.Contains(id) && ordinary(id);
                        }).ToList();
                        if (slots.Count == 0) continue;

                        var key = (group.MobId, rule.Event);
                        if (!Decided.TryGetValue(key, out bool lucky))
                        {
                            lucky = Dice(100) < settings.EventMonsterChance;
                            Decided[key] = lucky;
                        }
                        if (!lucky) continue;

                        int slot = slots[Dice(slots.Count)];
                        var original = group.Members[slot];
                        int guestId = rule.Replace.Count > 0
                            ? rule.Replace[original.Monster!.Id]
                            : rule.Joins[Dice(rule.Joins.Length)];
                        var data = monster(guestId);
                        if (data == null || data.Grades.Count == 0) continue;

                        int grade = NearestGrade(data, original.Level);
                        var dressed = new List<MobMember>(group.Members)
                        {
                            [slot] = new MobMember { Monster = data, GradeIndex = grade, Level = data.Grades[grade].Level },
                        };
                        group.Members = dressed;
                        Worn[group.MobId] = new Wearing(rule.Event, slot, guestId, original);
                        changed++;
                        break;
                    }
                }
            }
            return changed;
        }

        /// <summary>
        /// The event monsters the world's own groups hold: the generated groups take their members
        /// from each subarea's list, and those lists name the event's monsters too (a Kwismas
        /// Minotoball in Frigost, an Al Howin Gobball among Gobballs), so they would roam all year.
        /// While their event is shut, or the event monsters are off, each gives way to a member of
        /// its own group (or the monster it is the event's version of, or else one of the map's zone)
        /// and comes back when its event
        /// shows again. Under the gate. Whether the group changed.
        /// </summary>
        private static bool Uncover(MobGroup group, Func<int, bool> shows, Func<int, MonsterData?> monster,
                                    Func<IReadOnlyList<int>>? zone, Func<int, bool> ordinary)
        {
            bool changed = false;
            List<MobMember>? members = null;
            if (Hidden.TryGetValue(group.MobId, out var hidden))
            {
                for (int i = hidden.Count - 1; i >= 0; i--)
                {
                    var h = hidden[i];
                    if (!shows(h.Event)) continue;
                    hidden.RemoveAt(i);
                    var list = members ?? group.Members;
                    if (h.Slot >= list.Count || list[h.Slot].Monster?.Id != h.StandIn) continue;
                    members ??= new List<MobMember>(group.Members);
                    members[h.Slot] = h.Guest;
                    changed = true;
                }
                if (hidden.Count == 0) Hidden.Remove(group.MobId);
            }

            var current = members ?? group.Members;
            for (int slot = 0; slot < current.Count; slot++)
            {
                int id = current[slot].Monster?.Id ?? 0;
                if (!_eventOf.TryGetValue(id, out int ev) || shows(ev)) continue;
                if (Worn.TryGetValue(group.MobId, out var w) && w.Slot == slot) continue;

                MonsterData? standIn = current.FirstOrDefault(m => m.Monster != null && !_guests.Contains(m.Monster.Id))?.Monster;
                if (standIn == null && _originalOf.TryGetValue(id, out int original)) standIn = monster(original);
                if (standIn == null && zone != null)
                    foreach (int other in zone())
                        if (!_guests.Contains(other) && ordinary(other) && monster(other) is { Grades.Count: > 0 } found)
                        {
                            standIn = found;
                            break;
                        }
                if (standIn == null || standIn.Grades.Count == 0) continue;

                int grade = NearestGrade(standIn, current[slot].Level);
                members ??= new List<MobMember>(group.Members);
                var guest = members[slot];
                members[slot] = new MobMember { Monster = standIn, GradeIndex = grade, Level = standIn.Grades[grade].Level };
                if (!Hidden.TryGetValue(group.MobId, out var list)) Hidden[group.MobId] = list = new List<Hiding>();
                list.Add(new Hiding(ev, slot, guest, standIn.Id));
                current = members;
                changed = true;
            }

            if (members != null) group.Members = members;
            return changed;
        }

        /// <summary>The grade, among the first five, whose level is nearest that one.</summary>
        internal static int NearestGrade(MonsterData data, int level)
        {
            int best = 0;
            int count = Math.Min(data.Grades.Count, MobMember.MaxGradesPerMonster);
            for (int i = 1; i < count; i++)
                if (Math.Abs(data.Grades[i].Level - level) < Math.Abs(data.Grades[best].Level - level))
                    best = i;
            return best;
        }
    }
}
