using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A criterion judged strictly for whoever is in session: met only when every letter it asks
    /// is one this knows and holds. What the monsters' own loot tables gate their quest items with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 8,639 rows of the monsters' «drops» carry a criterion, and most are quest items:
    /// «Qa=385&amp;PO&lt;2269,10» (quest 385 under way, fewer than ten in the bag),
    /// «Qs=1237&amp;Qo&gt;4096&amp;Qo&lt;4098&amp;PO!11683» (its step, an objective of it pending, none held yet),
    /// «(Pm=190578688|…)&amp;Qo=14179&amp;PO&lt;19756,10» (on those maps). The letters are the client's own
    /// (mw.ood, the criteria factory: PO ObjectCriterion, Qa/Qf/Qc QuestCriterion, Qo
    /// QuestObjectiveCriterion, Pm MapCriterion, PB SubareaCriterion, Po AreaCriterion, PL
    /// LevelCriterion, PZ SubscribeCriterion, HA AlterationCriterion, OA AchievementCriterion) but
    /// for Qs, which the client does not have and the data uses for a quest's step.
    /// </para>
    /// <para>
    /// In a drop, Qo asks about the objectives still to do in the quests' current steps: «Qo=14179»
    /// is «kill ten larvae» pending, and «Qo&gt;4096&amp;Qo&lt;4098» is objective 4097 among those pending
    /// -- step 1237's objectives run 4074 to 4103. INFERRED, as the order of an objective's ids.
    /// Mt is about where the fight began (a dungeon room or not) and Ms about the fight's source (see Resolve); Sc, the
    /// yearly events and content switches, only where its meaning is known (SeasonalEvents.IsKnown).
    /// The character's other letters -- the server's type and season, the date, the days an item
    /// has been held -- are asked of the achievement engine (ByCharacter). What nobody answers --
    /// Sv, the server's own letters, and the Sc only the loot carries -- leaves the row Unknown, and an Unknown row does not drop: «never dropping them is preferable to
    /// always dropping them» still holds for them.
    /// </para>
    /// <para>
    /// The meats: «Pj&gt;41,9&amp;CU&gt;0» on «Carne picada», «Pj&gt;41,19» on «Carne manida» -- job 41 is
    /// the Cazador, above level 9, 19. INFERRED: CU is a hunting weapon in hand, effect 795 «Arma
    /// de caza», which 43 weapons carry: a hunter's meat, as the game always had it.
    /// </para>
    /// </remarks>
    public static class PlayerCriteria
    {
        /// <summary>
        /// Whether whoever is in session meets the criterion, standing on <paramref name="mapId"/> --
        /// a fight's roleplay map, not its arena -- and holding what <paramref name="held"/> counts.
        /// </summary>
        public static bool Met(string criterion, long mapId, Func<int, int>? held = null, int monsterId = 0)
            => Criterion.Met(criterion, ResolverFor(mapId, held, monsterId));

        /// <summary>The resolver itself, for whoever is in session.</summary>
        /// <remarks>
        /// <paramref name="monsterId"/>: the monster whose loot is judged, for monster-specific criteria.
        /// <paramref name="playerAlive"/>: the player's actual post-fight outcome, for Ga.
        /// <paramref name="soulStoneFight"/>: whether the fight came from a soul stone, for Ms.
        /// </remarks>
        public static Criterion.Resolver ResolverFor(long mapId, Func<int, int>? held = null, int monsterId = 0,
                                                     bool? playerAlive = null, bool? soulStoneFight = null)
        {
            var state = SessionContext.State;
            held ??= Equipment.HowMany;
            var facts = new Facts(state, Quests.Log, Quests.Book, mapId, held, state.JobLevel, HuntingWeaponInHand,
                                  Equipment.HowManyWorn, monsterId, playerAlive, soulStoneFight,
                                  titleId => Titles.Knows(state.CharacterId, titleId),
                                  slot => Equipment.InSlot(slot) is { Quantity: > 0 },
                                  () => Equipment.Worn == 0,
                                  MobSpawnManager.IsMonsterAliveInSubarea,
                                  () => SessionContext.Actual is { IsAuthenticated: true } session
                                      && DatabaseManager.TryGetAccountRole(session.AccountId, out int role)
                                          ? role >= Jondo.Unity.Launcher.Roles.Administrador
                                          : null);
            return condition => Resolve(condition, facts);
        }

        /// <summary>«Arma de caza».</summary>
        public const int HuntingWeaponEffect = 795;

        /// <summary>The weapon's slot.</summary>
        private const int WeaponSlot = 1;

        private static bool HuntingWeaponInHand()
            => Equipment.InSlot(WeaponSlot)?.Effects.Any(e => e.Effect == HuntingWeaponEffect) ?? false;

        /// <summary>Whether the roleplay map is one of the Bonta or Brakmar arenas.</summary>
        private static bool? IsBontaOrBrakmarArena(long mapId)
        {
            var map = MapManager.GetMapInfo(mapId);
            if (map == null) return null;

            string mapName = NormalizeMapName(map.Name ?? "");
            if (IsNamedCityArena(mapName)) return true;
            string subArea = NormalizeMapName(DatabaseManager.GetSubAreaName(map.SubAreaId));
            if (string.IsNullOrWhiteSpace(mapName) && string.IsNullOrWhiteSpace(subArea)) return null;
            return IsNamedCityArena(mapName + " " + subArea);
        }

        private static bool IsNamedCityArena(string identity)
        {
            bool city = identity.Contains("bonta", StringComparison.Ordinal)
                || identity.Contains("brakmar", StringComparison.Ordinal);
            bool arena = identity.Contains("arena", StringComparison.Ordinal)
                || identity.Contains("arene", StringComparison.Ordinal);
            return city && arena;
        }

        private static string NormalizeMapName(string name)
        {
            string decomposed = name.Normalize(NormalizationForm.FormD);
            var normalized = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    normalized.Append(char.ToLowerInvariant(character));
            return normalized.ToString();
        }

        /// <summary>What a condition asks of a character, as plain facts, so it can be answered without a session.</summary>
        internal readonly record struct Facts(SessionState? State, QuestLog? Log, QuestCatalogue? Book, long MapId,
                                              Func<int, int> Held, Func<int, int>? JobLevel = null,
                                              Func<bool>? HuntingWeapon = null, Func<int, int>? Worn = null,
                                              int MonsterId = 0, bool? PlayerAlive = null, bool? SoulStoneFight = null,
                                              Func<int, bool?>? KnownTitle = null,
                                              Func<int, bool?>? OccupiedEquipmentSlot = null,
                                              Func<bool?>? EmptyEquipment = null,
                                              Func<int, int, bool?>? LiveMonsterInSubarea = null,
                                              Func<bool?>? IsAdministrator = null);

        internal static Answer Resolve(Condition condition, Facts facts)
        {
            char op = condition.Operator;
            long value = condition.Value;
            switch (condition.Code)
            {
                case "PL": return facts.State == null ? Answer.Unknown : Compare(facts.State.CharacterLevel, op, value);
                case "Pm": return Compare(facts.MapId, op, value);
                case "PB": return Compare(DatabaseManager.SubAreaOfMap(facts.MapId), op, value);
                case "Po": return Compare(Interactives.AreaOfSubArea(DatabaseManager.SubAreaOfMap(facts.MapId)), op, value);

                // Every account is subscribed here (Subscription).
                case "PZ": return Compare(1, op, value);

                // The character's sex, 0 a man and 1 a woman; an attitude he knows; the pods he has free
                // (what he carries weighs nothing here, so his capacity): the guardian of the bridge of
                // death's questions.
                case "PS": return facts.State == null ? Answer.Unknown : Compare(facts.State.Sex, op, value);
                case "PE": return facts.State == null ? Answer.Unknown : Truth(facts.State.Emotes.Contains((int)value), op);
                case "Pw": return facts.State == null ? Answer.Unknown : Compare(Jondo.Unity.Server.Network.ConnectionProtocol.Capacity(facts.State), op, value);

                // The kamas he carries: Abel Bobel's «Llorar.» is for whoever cannot pay his 3000.
                case "PK": return facts.State == null ? Answer.Unknown : Compare(facts.State.Kamas, op, value);

                // His alignment's side: 0 neutral, 1 Bonta, 2 Brakmar (Alignments).
                case "Ps": return facts.State == null ? Answer.Unknown : Compare(Alignments.CriterionSide(facts.State.CharacterId), op, value);

                // PX is categorical: A selects the administrator role. Read the account's
                // current auth-database role; neither the selector nor character data grants it.
                case "PX":
                    if (condition.Args.Count != 1 || condition.Args[0] != "A" || facts.IsAdministrator == null)
                        return Answer.Unknown;
                    bool? isAdministrator = facts.IsAdministrator();
                    return isAdministrator.HasValue ? Truth(isAdministrator.Value, op) : Answer.Unknown;

                // Its level and rank, from the quests he finished (Alignments.Progression).
                case "Pa":
                case "Pr":
                    if (facts.State == null || facts.Log == null) return Answer.Unknown;
                    {
                        var finished = facts.Log.Runs.Values.Where(r => r.Finished).Select(r => r.QuestId);
                        int side = Alignments.CriterionSide(facts.State.CharacterId);
                        return Compare(condition.Code == "Pa" ? Alignments.Index().Level(side, finished)
                                                              : Alignments.Index().Rank(side, finished), op, value);
                    }

                case "PO":
                    // Worn, «E», or not worn, «X»: the Kama Baraka asks for the Dados de la Suerte on.
                    if (condition.Operator is 'E' or 'X')
                    {
                        if (facts.Worn == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int worn))
                            return Answer.Unknown;
                        return Truth(facts.Worn(worn) > 0, condition.Operator == 'E' ? '=' : '!');
                    }
                    return Holding(condition, facts.Held);

                // Pn carries an equipment slot: «Pn!1» means the weapon slot is empty. The
                // observed «Pn!-1» form is the whole-equipment-empty sentinel, not item template
                // -1. Equipment occupancy comes from the session's worn positions.
                case "Pn": return EquipmentState(condition, facts);

                // BI marks unobtainable content; compare its value against zero.
                case "BI":
                    if (condition.Args.Count != 1 || !long.TryParse(condition.Args[0], out long biValue))
                        return Answer.Unknown;
                    return Compare(0, op, biValue);

                case "Qa": return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Active((int)value), op);
                case "Qf": return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Finished((int)value), op);
                // Qc asks whether the quest can be taken now, which depends on its catalogue
                // prerequisites and this character's run/repeat state; completion is Qf.
                case "Qc":
                    if (facts.Log == null || facts.Book == null || !facts.Book.Ready
                        || value < int.MinValue || value > int.MaxValue) return Answer.Unknown;
                    int questId = (int)value;
                    var catalogueQuest = facts.Book.Of(questId);
                    if (catalogueQuest == null || catalogueQuest.Steps.Count == 0) return Truth(false, op);
                    bool available = facts.Log.CanStart(questId, out var availability);
                    return availability.FullyJudged ? Truth(available, op) : Answer.Unknown;

                // How many times a quest was finished: «QF>1423,0» at least once, which Cuerbax asks
                // before his evaluations. One run is kept per quest, so it counts as once.
                case "QF":
                    if (facts.Log == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int quest))
                        return Answer.Unknown;
                    return condition.Args.Count == 1
                        ? Truth(facts.Log.Finished(quest), op)
                        : Compare(facts.Log.Finished(quest) ? 1 : 0, op, value);
                case "Qs":
                    return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Doing().Any(r => r.StepId == value), op);
                case "Qo": return Pending(facts, op, value);

                // A job: «PJ=28» had at all, «Pj>41,9» above a level.
                case "PJ":
                case "Pj":
                    return Job(condition, facts.JobLevel);
                case "CU":
                    return facts.HuntingWeapon == null ? Answer.Unknown : Compare(facts.HuntingWeapon() ? 1 : 0, op, value);

                case "HA":
                    return facts.State == null ? Answer.Unknown : Truth(Alterations.Has(facts.State.CharacterId, (int)value), op);
                // Ga: whether the player is alive at the end of the fight.
                case "Ga": return facts.PlayerAlive.HasValue ? Compare(facts.PlayerAlive.Value ? 1 : 0, op, value) : Answer.Unknown;
                // Mt is whether the fight began on a dungeon room map, not whether the monster is a
                // boss. Read as the boss flag, nearly every row that asks it would say nothing: of
                // the 1,308 «Mt=1» rows, 1,262 sit on bosses' own drops, and of the 496 «Mt=0»,
                // 495 on monsters that are not bosses. Read as the dungeon, each one is a rule: a
                // boss's resources only in his dungeon, not off a soul stone fought elsewhere, and
                // 410 rows of monsters that also live in dungeons only outside them. A missing
                // origin map or an unloaded dungeon catalogue leaves it unknown.
                case "Mt": return facts.MapId == 0 || !DungeonManager.IsLoaded
                    ? Answer.Unknown
                    : Compare(DungeonManager.OfRoom(facts.MapId) != null ? 1 : 0, op, value);
                // Ss asks whether this monster is currently alive in the specified subarea.
                // Its two arguments identify the monster and subarea; the operator negates or
                // asserts live presence. Spawn catalog possibilities are not live presence.
                case "Ss":
                    if (condition.Args.Count != 2
                        || !int.TryParse(condition.Args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ssMonster)
                        || !int.TryParse(condition.Args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int subAreaId)
                        || ssMonster <= 0 || subAreaId <= 0 || facts.LiveMonsterInSubarea == null)
                        return Answer.Unknown;
                    bool? live = facts.LiveMonsterInSubarea(ssMonster, subAreaId);
                    return live.HasValue ? Truth(live.Value, op) : Answer.Unknown;
                case "Ms":
                    if (!facts.SoulStoneFight.HasValue) return Answer.Unknown;
                    if (!facts.SoulStoneFight.Value) return Compare(0, op, value);
                    bool? arenaFight = IsBontaOrBrakmarArena(facts.MapId);
                    return arenaFight.HasValue ? Compare(arenaFight.Value ? 1 : 0, op, value) : Answer.Unknown;
                case "OA":
                    return facts.State?.Achievements == null ? Answer.Unknown : Truth(facts.State.Achievements.Has((int)value), op);
                // Ot asks whether the character owns this title, not whether it exists in the catalog.
                case "Ot":
                    if (condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int titleId)
                        || facts.KnownTitle == null) return Answer.Unknown;
                    bool? knowsTitle = facts.KnownTitle(titleId);
                    return knowsTitle.HasValue ? Truth(knowsTitle.Value, op) : Answer.Unknown;
                // OO names an achievement objective, not a quest objective. The achievement log
                // judges its catalogued criterion against this character's facts.
                case "OO":
                    if (facts.State?.Achievements == null || value < int.MinValue || value > int.MaxValue)
                        return Answer.Unknown;
                    Answer objectiveState = facts.State.Achievements.ObjectiveState((int)value);
                    return objectiveState == Answer.Unknown ? Answer.Unknown : Truth(objectiveState == Answer.True, op);

                // Sv is the server-wide, week-long world quest where players gather items to open
                // the Grozilla and Grasmera door. Its event ID and state are not mapped yet, so keep
                // it Unknown until both are authoritative.
                case "Sv": return Answer.Unknown;

                // A yearly event or a content switch, the client's StaticCriterion, which only the
                // real server judges: answered only where its meaning is known (SeasonalEvents.IsKnown),
                // the events by their dates and the switches the quests carry as on. The loot's own
                // stay unknown, and a met answer must still respect the operator: the bosses' shields'
                // «Sc=3049&Mt=1&PO!30414», each watched by an invisible «[!] DR» marker the real
                // server hands out with it, would otherwise drop at 100 % on every win; «Sc=947» is
                // the kings' cake, a January event; «Sc!629» guards the 3,854 global rows of
                // equipment (976 items) off every monster.
                case "Sc":
                    return SeasonalEvents.IsKnown((int)value) ? Truth(SeasonalEvents.IsOn((int)value, Almanax.Clock()), op) : Answer.Unknown;

                default: return ByCharacter(condition, facts);
            }
        }

        /// <summary>
        /// A letter the loot does not judge itself, as the achievement engine answers it for the
        /// character in session (<see cref="Achievements.CharacterFacts"/>, which the quests and the
        /// items' criteria read too); Unknown when it does not know it either.
        /// </summary>
        /// <remarks>
        /// The 117 pieces of equipment the monsters drop to the young, «(SC!5|ST!5)&amp;PL&gt;0&amp;PL&lt;59»
        /// on the piwis and the like, never fell: SC and ST were nobody's here, and an unknown row
        /// does not drop. SC is the server's game type, ST its season (the client's
        /// ServerTypeCriterion and SeasonCriterion): not the fifth Temporis on this classic server.
        /// The same reader answers the days an item has been held («DD&lt;91,30071», false without
        /// the item), the world events' «WE» and the date's «SG»/«Sd», with the shapes
        /// <see cref="Jondo.Unity.World.Quests.QuestCriterion"/> gives them: «DD&lt;days,item»,
        /// «OP&gt;key,n» a tally compared with n, «OP=n» a number, or «OP=key» at least one held.
        /// Only for the character in session: the reader asks the session, not these facts.
        /// </remarks>
        private static Answer ByCharacter(Condition condition, Facts facts)
        {
            // Uppercase SD is a day-of-week number (Sunday=1 through Saturday=7). Keep it
            // separate from lowercase Sd, which the achievement facts use for day-of-month.
            if (condition.Code == "SD")
            {
                if (condition.Args.Count == 0 || !long.TryParse(condition.Args[0], out long day))
                    return Answer.Unknown;
                var parisDay = OsavoraSeasons.InParis(new DateTimeOffset(Almanax.Clock())).DayOfWeek;
                long today = (int)parisDay + 1;
                return Compare(today, condition.Operator, day);
            }

            if (facts.State == null || !ReferenceEquals(facts.State, SessionContext.State) || condition.Args.Count == 0)
                return Answer.Unknown;
            if (!long.TryParse(condition.Args[0], out long first)) return Answer.Unknown;
            var character = new Achievements.CharacterFacts();
            string code = condition.Code;
            char op = condition.Operator;

            // Without the item there is no date to count from: «PL<21&DD<30,21172&Sc=944», the
            // piwis' pollen, falls only to whoever holds a pollen net received less than thirty days
            // ago -- counting the days of a net he does not have as none gave it to every newcomer.
            if (code is "DD" or "DH" or "DM" && condition.Args.Count >= 2)
            {
                if (!int.TryParse(condition.Args[1], out int item)) return Answer.Unknown;
                if (facts.Held(item) == 0) return Answer.False;
                long? since = character.Count(code, item, "");
                return since.HasValue ? Compare(since.Value, op, first) : Answer.Unknown;
            }

            if (condition.Args.Count >= 2)
            {
                if (!long.TryParse(condition.Args[1], out long threshold)) return Answer.Unknown;
                long? tally = character.Count(code, first, condition.Args.Count >= 3 ? condition.Args[2] : "");
                return tally.HasValue ? Compare(tally.Value, op, threshold) : Answer.Unknown;
            }

            long? scalar = character.Scalar(code);
            if (scalar.HasValue) return Compare(scalar.Value, op, first);

            long? held = character.Count(code, first, "");
            if (!held.HasValue) return Answer.Unknown;
            return op switch
            {
                '=' => Bool(held.Value > 0),
                '!' => Bool(held.Value == 0),
                _ => Answer.Unknown,
            };
        }

        /// <summary>
        /// «PO!19649» none held, «PO=21619» one at least, «PO&lt;2269,10» fewer than ten, «PO&gt;x,n» more
        /// than n.
        /// </summary>
        private static Answer Holding(Condition condition, Func<int, int> held)
        {
            if (condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int item)) return Answer.Unknown;
            int have = held(item);
            if (condition.Args.Count == 1)
            {
                return condition.Operator switch
                {
                    '=' => Bool(have > 0),
                    '!' => Bool(have == 0),
                    _ => Answer.Unknown,
                };
            }
            return long.TryParse(condition.Args[1], out long count) ? Compare(have, condition.Operator, count) : Answer.Unknown;
        }

        private static Answer Job(Condition condition, Func<int, int>? level)
        {
            if (level == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int job)) return Answer.Unknown;
            int have = level(job);
            if (condition.Args.Count == 1)
            {
                return condition.Operator switch
                {
                    '=' => Bool(have > 0),
                    '!' => Bool(have == 0),
                    _ => Answer.Unknown,
                };
            }
            return long.TryParse(condition.Args[1], out long wanted) ? Compare(have, condition.Operator, wanted) : Answer.Unknown;
        }

        /// <summary>The objectives still to do in the current steps of the quests under way, compared.</summary>
        private static Answer Pending(Facts facts, char op, long value)
        {
            if (facts.Log == null || facts.Book == null) return Answer.Unknown;
            var pending = new List<int>();
            foreach (var run in facts.Log.Doing())
            {
                var step = facts.Book.Step(run.StepId);
                if (step == null) continue;
                pending.AddRange(step.Objectives.Select(o => o.Id).Where(id => !run.Done.Contains(id)));
            }
            return op switch
            {
                '=' => Bool(pending.Contains((int)value)),
                '!' => Bool(!pending.Contains((int)value)),
                // '>' and '<' are «done» and «not done», as the client words them (its texts
                // ui.grimoire.quest.objective done / notDone beside pending / notPending): the
                // samples of «Genio en movimiento» drop on «Qo<13805», the step's very first
                // objective, which no other reading lets through.
                '>' => Bool(facts.Log.ObjectiveDone((int)value)),
                '<' => Bool(!facts.Log.ObjectiveDone((int)value)),
                _ => Answer.Unknown,
            };
        }

        private static Answer EquipmentState(Condition condition, Facts facts)
        {
            if (condition.Args.Count != 1 || !int.TryParse(condition.Args[0], out int slot))
                return Answer.Unknown;

            if (slot == -1)
            {
                if (facts.EmptyEquipment == null) return Answer.Unknown;
                bool? empty = facts.EmptyEquipment();
                if (!empty.HasValue) return Answer.Unknown;
                return condition.Operator switch
                {
                    '!' => Bool(empty.Value),
                    '=' => Bool(!empty.Value),
                    _ => Answer.Unknown,
                };
            }

            if (slot < 0 || slot > Equipment.LastWornSlot || facts.OccupiedEquipmentSlot == null)
                return Answer.Unknown;

            bool? occupied = facts.OccupiedEquipmentSlot(slot);
            if (!occupied.HasValue) return Answer.Unknown;
            return condition.Operator switch
            {
                '=' => Bool(occupied.Value),
                '!' => Bool(!occupied.Value),
                _ => Answer.Unknown,
            };
        }

        private static Answer Compare(long have, char op, long value) => op switch
        {
            '=' => Bool(have == value),
            '!' => Bool(have != value),
            '>' => Bool(have > value),
            '<' => Bool(have < value),
            _ => Answer.Unknown,
        };

        private static Answer Truth(bool have, char op) => op switch
        {
            '=' => Bool(have),
            '!' => Bool(!have),
            _ => Answer.Unknown,
        };

        private static Answer Bool(bool yes) => yes ? Answer.True : Answer.False;
    }
}
