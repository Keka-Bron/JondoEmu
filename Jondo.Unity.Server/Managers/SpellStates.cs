using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What a spell state does to the rules of the fight, out of the client's own catalogue.
    /// </summary>
    /// <remarks>
    /// The client's <c>SpellStateData</c> carries a dozen flags per state: <c>invulnerable</c>,
    /// <c>cantBeMoved</c>, <c>incurable</c>... Of its 6,375 states only 103 set any of them; the
    /// rest are markers a spell reads back through its masks. Influencia's 269 is
    /// "Invulnerable" and nothing else, and in its capture the Presión that follows it lands as
    /// a jwe 97 with the victim and the element and no amount at all.
    /// </remarks>
    public static class SpellStates
    {
        /// <summary>A flagged state, with the flags the client sets on it.</summary>
        public sealed class State
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public bool Invulnerable { get; init; }
            public bool InvulnerableMelee { get; init; }
            public bool InvulnerableRange { get; init; }
            public bool CantBeMoved { get; init; }
            public bool CantBePushed { get; init; }
            public bool CantSwitchPosition { get; init; }
            public bool CantDealDamage { get; init; }
            public bool Incurable { get; init; }
            public bool PreventsSpellCast { get; init; }
            public bool PreventsFight { get; init; }
            public bool CantTackle { get; init; }
            public bool CantBeTackled { get; init; }

            /// <summary>
            /// Keeps its bearer out of the portals: the client lists effect 33 on this state, and
            /// the one state that carries it is 678, "Teleportal Imposible" -- what the Selatrop's
            /// passive puts on every enemy for the first round, "los luchadores enemigos no pueden
            /// usar un portal en el primer turno de juego". That 33 means that is INFERRED from
            /// the name and the sheet.
            /// </summary>
            public bool CantUsePortals { get; init; }
        }

        private static Dictionary<int, State>? _states;
        private static readonly object _lock = new();

        /// <summary>The flagged state, or null when the state carries no flag.</summary>
        public static State? Of(int stateId)
        {
            Load();
            return _states!.TryGetValue(stateId, out var state) ? state : null;
        }

        /// <summary>How many flagged states the file holds.</summary>
        public static int Count
        {
            get
            {
                Load();
                return _states!.Count;
            }
        }

        /// <summary>
        /// Whether one of the fighter's states shields him from a blow dealt from
        /// <paramref name="distance"/> cells away: "invulnerable" from any, "invulnerableMelee"
        /// from next door, "invulnerableRange" from further away.
        /// </summary>
        public static bool ShieldsFromBlow(Fighter who, int distance)
        {
            foreach (int stateId in who.Buffs.Estados)
            {
                var state = Of(stateId);
                if (state == null) continue;
                if (state.Invulnerable) return true;
                if (state.InvulnerableMelee && distance <= 1) return true;
                if (state.InvulnerableRange && distance > 1) return true;
            }
            return false;
        }

        /// <summary>Whether one of the fighter's states keeps him from being pushed or pulled.</summary>
        public static bool PinsInPlace(Fighter who)
        {
            foreach (int stateId in who.Buffs.Estados)
            {
                var state = Of(stateId);
                if (state != null && (state.CantBeMoved || state.CantBePushed)) return true;
            }
            return false;
        }

        /// <summary>Whether one of the fighter's states keeps him from dealing damage: Pacifista (218) among them.</summary>
        public static bool KeepsFromDealingDamage(Fighter who)
        {
            foreach (int stateId in who.Buffs.Estados)
            {
                var state = Of(stateId);
                if (state != null && state.CantDealDamage) return true;
            }
            return false;
        }

        /// <summary>Whether one of the fighter's states turns healing away.</summary>
        public static bool IsIncurable(Fighter who)
        {
            foreach (int stateId in who.Buffs.Estados)
            {
                var state = Of(stateId);
                if (state != null && state.Incurable) return true;
            }
            return false;
        }

        /// <summary>Whether one of the fighter's states keeps him from going through a portal.</summary>
        public static bool KeepsOutOfPortals(Fighter who)
        {
            foreach (int stateId in who.Buffs.Estados)
            {
                var state = Of(stateId);
                if (state != null && state.CantUsePortals) return true;
            }
            return false;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _states = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_states != null) return;
                var states = new Dictionary<int, State>();
                try
                {
                    string path = Paths.SpellStatesJson;
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[SpellStates] {Path.GetFileName(path)} is not there; no state " +
                                          "makes anybody invulnerable. Run extract_spell_states.py.");
                    }
                    else
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.EnumerateObject())
                        {
                            if (!int.TryParse(entry.Name, out int id)) continue;
                            var v = entry.Value;
                            states[id] = new State
                            {
                                Id = id,
                                Name = v.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                                Invulnerable = Flag(v, "invulnerable"),
                                InvulnerableMelee = Flag(v, "invulnerableMelee"),
                                InvulnerableRange = Flag(v, "invulnerableRange"),
                                CantBeMoved = Flag(v, "cantBeMoved"),
                                CantBePushed = Flag(v, "cantBePushed"),
                                CantSwitchPosition = Flag(v, "cantSwitchPosition"),
                                CantDealDamage = Flag(v, "cantDealDamage"),
                                Incurable = Flag(v, "incurable"),
                                PreventsSpellCast = Flag(v, "preventsSpellCast"),
                                PreventsFight = Flag(v, "preventsFight"),
                                CantTackle = Flag(v, "cantTackle"),
                                CantBeTackled = Flag(v, "cantBeTackled"),
                                CantUsePortals = Flag(v, "cantUsePortals"),
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpellStates] Could not read spell_states.json: {ex.Message}");
                }

                _states = states;
            }
        }

        private static bool Flag(JsonElement element, string name)
            => element.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    }
}
