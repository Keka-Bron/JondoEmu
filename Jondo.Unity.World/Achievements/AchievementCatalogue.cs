using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.World.Achievements
{
    /// <summary>
    /// What finishes an objective the client names but does not describe.
    /// </summary>
    /// <remarks>
    /// 322 achievements point at objective ids that are not in the client's objective table: the
    /// server judges them by itself. What they want is still written in the achievement's own name
    /// and description, and <c>datos/achievement_links_3.6.10.10.json</c> is that, read out.
    /// </remarks>
    public sealed class AchievementLink
    {
        public const string Explore = "explore";
        public const string Level = "level";
        public const string JobLevel = "jobLevel";
        public const string Craft = "craft";
        public const string Quest = "quest";
        public const string Monster = "monster";

        public string Kind { get; init; } = "";

        /// <summary>The subarea to enter, for <see cref="Explore"/>.</summary>
        public int Subarea { get; init; }

        /// <summary>The level to reach, for <see cref="Level"/> and <see cref="JobLevel"/>.</summary>
        public int LevelNeeded { get; init; }

        /// <summary>How many jobs at that level, for <see cref="JobLevel"/>.</summary>
        public int Jobs { get; init; }

        /// <summary>How many items to craft, for <see cref="Craft"/>.</summary>
        public int Count { get; init; }

        public int QuestId { get; init; }
        public int MonsterId { get; init; }

        public override string ToString() => Kind switch
        {
            Explore => $"explore subarea {Subarea}",
            Level => $"reach level {LevelNeeded}",
            JobLevel => $"reach level {LevelNeeded} in {Jobs} job(s)",
            Craft => $"craft {Count} item(s)",
            Quest => $"finish quest {QuestId}",
            Monster => $"beat monster {MonsterId}",
            _ => Kind,
        };
    }

    /// <summary>One thing an achievement wants done. All of them have to hold.</summary>
    public sealed class AchievementObjective
    {
        public int Id { get; init; }
        public int AchievementId { get; init; }
        public string Name { get; init; } = "";

        /// <summary>Written in the same language a quest's start condition is.</summary>
        public string Criterion { get; init; } = "";

        /// <summary>The criterion's terms, read once at load.</summary>
        public IReadOnlyList<CriterionTerm> Terms { get; init; } = Array.Empty<CriterionTerm>();

        /// <summary>
        /// For the objectives the client does not describe, what finishes them. Null for every
        /// objective that carries a criterion of its own.
        /// </summary>
        public AchievementLink? Link { get; init; }
    }

    /// <summary>What an achievement hands over.</summary>
    public sealed class AchievementReward
    {
        public int Id { get; init; }

        /// <summary>Who gets this one. Empty means everybody who earns the achievement.</summary>
        public string Criterion { get; init; } = "";

        public double ExperienceRatio { get; init; }
        public double KamasRatio { get; init; }

        /// <summary>Whether the kamas are worked out on the character's level rather than the achievement's.</summary>
        public bool KamasScale { get; init; }

        public int GuildPoints { get; init; }

        /// <summary>Item id and how many.</summary>
        public IReadOnlyList<(int Item, int Count)> Items { get; init; } = Array.Empty<(int, int)>();

        public IReadOnlyList<int> Spells { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Emotes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Titles { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Ornaments { get; init; } = Array.Empty<int>();

        public bool Empty => Items.Count == 0 && Spells.Count == 0 && Emotes.Count == 0
                             && Titles.Count == 0 && Ornaments.Count == 0;
    }

    /// <summary>One entry of the achievement window's tree.</summary>
    public sealed class AchievementCategory
    {
        public int Id { get; init; }
        public int ParentId { get; init; }
        public int Order { get; init; }
        public string Name { get; init; } = "";
    }

    /// <summary>One achievement.</summary>
    public sealed class Achievement
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public int CategoryId { get; init; }
        public string Category { get; init; } = "";

        /// <summary>The level it is meant for, which is not the level needed to earn it.</summary>
        public int Level { get; init; }

        /// <summary>What it is worth. The client adds these up into a score.</summary>
        public int Points { get; init; }

        /// <summary>Where it sits inside its category.</summary>
        public int Order { get; init; }

        /// <summary>Earned once for the whole account rather than per character.</summary>
        public bool AccountWide { get; init; }

        public IReadOnlyList<AchievementObjective> Objectives { get; init; }
            = Array.Empty<AchievementObjective>();

        public IReadOnlyList<AchievementReward> Rewards { get; init; }
            = Array.Empty<AchievementReward>();

        /// <summary>The quests that have to be finished for it, read out of the objectives.</summary>
        public IReadOnlyList<int> FromQuests { get; init; } = Array.Empty<int>();

        /// <summary>The achievements it is built on, read out of the objectives.</summary>
        public IReadOnlyList<int> FromAchievements { get; init; } = Array.Empty<int>();

        public override string ToString() => Name.Length > 0 ? Name : $"achievement {Id}";
    }

    /// <summary>
    /// The achievement catalogue: 2,780 of them, with 8,946 objectives and 6,394 rewards.
    /// </summary>
    /// <remarks>
    /// Read out of <c>datos/achievements_3.6.10.10.json</c>, which
    /// <c>tools/extract_achievements.py</c> flattens from four Unity dumps the repository does not
    /// carry, plus <c>datos/achievement_links_3.6.10.10.json</c> for the 272 objectives the client
    /// names but does not describe. Shared between the server, which grants them, and the editor,
    /// which shows them.
    ///
    /// <b>What an achievement is, in one line:</b> a list of criteria, all of which must hold, and
    /// a list of rewards, each with its own criterion saying who gets it. The criteria are the same
    /// language a quest's start condition is written in, which is why <see cref="QuestCriterion"/>
    /// reads both.
    ///
    /// The indexes are built at load because the engine asks the questions backwards from the way
    /// the data is written: when a quest is finished it needs the achievements that were waiting on
    /// that quest, when a monster falls the ones that count that monster, and when an achievement
    /// is earned the ones built on <em>it</em> — 2,157 objectives are nothing but <c>OA</c>.
    /// Without them, one fight would mean walking 8,946 criterion strings.
    /// </remarks>
    public sealed class AchievementCatalogue
    {
        /// <summary>Index key for an explored subarea: not an operator of the client's, ours.</summary>
        public const string ExploreKey = "Xs";

        /// <summary>Index key for "that many jobs at that level": ours, see <see cref="ExploreKey"/>.</summary>
        public const string JobKey = "Xj";

        /// <summary>Index key for "items crafted": ours, see <see cref="ExploreKey"/>.</summary>
        public const string CraftKey = "Xc";

        private readonly Dictionary<int, Achievement> _byId = new Dictionary<int, Achievement>();
        private readonly Dictionary<int, AchievementCategory> _categories = new Dictionary<int, AchievementCategory>();
        private readonly Dictionary<int, List<Achievement>> _byCategory = new Dictionary<int, List<Achievement>>();

        /// <summary>(operator, key) to the achievements whose objectives mention it.</summary>
        private readonly Dictionary<(string, long), List<int>> _waitingOnKey
            = new Dictionary<(string, long), List<int>>();

        /// <summary>Operator to every achievement whose objectives mention it, whatever the key.</summary>
        private readonly Dictionary<string, List<int>> _waitingOnOp = new Dictionary<string, List<int>>();

        private List<Achievement>? _all;

        public AchievementCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            Read(text, report);
        }

        public bool Ready => _byId.Count > 0;

        public int Count => _byId.Count;
        public int ObjectiveCount { get; private set; }
        public int RewardCount { get; private set; }

        /// <summary>How many objectives the client does not describe were tied to something.</summary>
        public int LinkedCount { get; private set; }

        /// <summary>How many are earned by finishing quests. The join the whole thing hangs on.</summary>
        public int FromQuestsCount { get; private set; }

        public Achievement? Of(int id) => _byId.TryGetValue(id, out var a) ? a : null;

        public AchievementCategory? CategoryOf(int id) => _categories.TryGetValue(id, out var c) ? c : null;

        public IReadOnlyCollection<AchievementCategory> Categories => _categories.Values;

        /// <summary>The achievements of one category, in the order the category lists them.</summary>
        public IReadOnlyList<Achievement> InCategory(int categoryId)
            => _byCategory.TryGetValue(categoryId, out var list) ? list : (IReadOnlyList<Achievement>)Array.Empty<Achievement>();

        public List<Achievement> All()
        {
            if (_all != null) return _all;

            _all = new List<Achievement>(_byId.Values);
            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _all;
        }

        /// <summary>The achievements that could have become earnable now that this quest is done.</summary>
        public IReadOnlyList<int> WaitingOnQuest(int questId) => WaitingOn("Qf", questId);

        /// <summary>The achievements built on this one.</summary>
        public IReadOnlyList<int> WaitingOnAchievement(int achievementId) => WaitingOn("OA", achievementId);

        /// <summary>The achievements with an objective that mentions this operator and this key.</summary>
        public IReadOnlyList<int> WaitingOn(string op, long key)
            => _waitingOnKey.TryGetValue((op, key), out var list) ? list : (IReadOnlyList<int>)Array.Empty<int>();

        /// <summary>The achievements with an objective that mentions this operator at all.</summary>
        public IReadOnlyList<int> WaitingOn(string op)
            => _waitingOnOp.TryGetValue(op, out var list) ? list : (IReadOnlyList<int>)Array.Empty<int>();

        // ─── Reading ──────────────────────────────────────────────────────────────

        private void Read(ClientText? text, Action<string>? report)
        {
            string path = Paths.AchievementsJson;
            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; no achievement will ever be " +
                               "granted. Run tools/extract_achievements.py to build it.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                string Say(long key) => text?.Of(key) ?? "";

                if (root.TryGetProperty("categories", out var cats))
                {
                    foreach (var entry in cats.EnumerateObject())
                    {
                        if (!int.TryParse(entry.Name, out int id)) continue;
                        _categories[id] = new AchievementCategory
                        {
                            Id = id,
                            ParentId = (int)Long(entry.Value, "parent"),
                            Order = (int)Long(entry.Value, "order"),
                            Name = Say(Long(entry.Value, "name")),
                        };
                    }
                }

                var objectives = ReadObjectives(root, Say);
                var links = ReadLinks(report);
                var rewards = ReadRewards(root);

                if (!root.TryGetProperty("achievements", out var table)) return;

                foreach (var entry in table.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var row = entry.Value;

                    var mine = new List<AchievementObjective>();
                    foreach (int objectiveId in Numbers(row, "objectives"))
                    {
                        if (objectives.TryGetValue(objectiveId, out var objective))
                        {
                            mine.Add(objective);
                        }
                        else if (links.TryGetValue(objectiveId, out var link))
                        {
                            mine.Add(new AchievementObjective { Id = objectiveId, AchievementId = id, Link = link });
                            LinkedCount++;
                        }
                    }

                    var pays = new List<AchievementReward>();
                    foreach (int rewardId in Numbers(row, "rewards"))
                    {
                        if (rewards.TryGetValue(rewardId, out var reward)) pays.Add(reward);
                    }

                    // Read once at load rather than every time a quest finishes. The same reader the
                    // start conditions use, so an achievement that says Qf!123 — "and you have NOT
                    // done that one" — does not end up counted as needing it.
                    var quests = new List<int>();
                    var badges = new List<int>();
                    foreach (var objective in mine)
                    {
                        foreach (int quest in QuestCatalogue.FinishedQuests(objective.Criterion))
                        {
                            if (!quests.Contains(quest)) quests.Add(quest);
                        }

                        if (objective.Link?.Kind == AchievementLink.Quest && !quests.Contains(objective.Link.QuestId))
                        {
                            quests.Add(objective.Link.QuestId);
                        }

                        foreach (int badge in Obtained(objective.Criterion))
                        {
                            if (!badges.Contains(badge)) badges.Add(badge);
                        }
                    }

                    int categoryId = (int)Long(row, "category");
                    var achievement = new Achievement
                    {
                        Id = id,
                        Name = Say(Long(row, "name")),
                        Description = Say(Long(row, "description")),
                        CategoryId = categoryId,
                        Category = _categories.TryGetValue(categoryId, out var said) ? said.Name : "",
                        Level = (int)Long(row, "level"),
                        Points = (int)Long(row, "points"),
                        Order = (int)Long(row, "order"),
                        AccountWide = Long(row, "accountWide") != 0,
                        Objectives = mine,
                        Rewards = pays,
                        FromQuests = quests,
                        FromAchievements = badges,
                    };

                    _byId[id] = achievement;
                    if (quests.Count > 0) FromQuestsCount++;

                    if (!_byCategory.TryGetValue(categoryId, out var inCategory))
                    {
                        _byCategory[categoryId] = inCategory = new List<Achievement>();
                    }

                    inCategory.Add(achievement);
                    IndexObjectives(achievement);
                }

                foreach (var list in _byCategory.Values)
                {
                    list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));
                }

                ObjectiveCount = objectives.Count;
                RewardCount = rewards.Count;
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        /// <summary>Files the achievement under everything its objectives mention.</summary>
        private void IndexObjectives(Achievement achievement)
        {
            foreach (var objective in achievement.Objectives)
            {
                foreach (var term in objective.Terms)
                {
                    // "Not having it" is not waiting on it: OA!8518 must not make an achievement
                    // look for 8518 being earned, any more than Qf!123 waits for quest 123.
                    if (term.Comparison == '!') continue;
                    Index(term.Op, term.Key, achievement.Id);
                }

                var link = objective.Link;
                if (link == null) continue;

                switch (link.Kind)
                {
                    case AchievementLink.Explore: Index(ExploreKey, link.Subarea, achievement.Id); break;
                    case AchievementLink.Level: Index("PL", 0, achievement.Id); break;
                    case AchievementLink.JobLevel: Index(JobKey, 0, achievement.Id); break;
                    case AchievementLink.Craft: Index(CraftKey, 0, achievement.Id); break;
                    case AchievementLink.Quest: Index("Qf", link.QuestId, achievement.Id); break;
                    case AchievementLink.Monster: Index("EM", link.MonsterId, achievement.Id); break;
                }
            }
        }

        private void Index(string op, long key, int achievementId)
        {
            if (!_waitingOnKey.TryGetValue((op, key), out var byKey)) _waitingOnKey[(op, key)] = byKey = new List<int>();
            if (!byKey.Contains(achievementId)) byKey.Add(achievementId);

            if (!_waitingOnOp.TryGetValue(op, out var byOp)) _waitingOnOp[op] = byOp = new List<int>();
            if (!byOp.Contains(achievementId)) byOp.Add(achievementId);
        }

        /// <summary>
        /// The achievements a criterion says must already be earned.
        /// </summary>
        /// <remarks>
        /// The <c>OA</c> twin of <see cref="QuestCatalogue.FinishedQuests"/>, and equality
        /// only for the same reason: <c>OA!8518</c> means the opposite, and reading it as a
        /// prerequisite would make an achievement wait for the very thing that rules it out.
        /// </remarks>
        public static List<int> Obtained(string criterion)
        {
            var found = new List<int>();
            if (string.IsNullOrEmpty(criterion)) return found;

            int at = 0;
            while (at < criterion.Length)
            {
                int mark = criterion.IndexOf("OA", at, StringComparison.Ordinal);
                if (mark < 0) break;

                at = mark + 2;
                if (at >= criterion.Length || criterion[at] != '=') continue;
                at++;

                int start = at;
                while (at < criterion.Length && char.IsDigit(criterion[at])) at++;
                if (at > start && int.TryParse(criterion.Substring(start, at - start), out int badge))
                {
                    if (!found.Contains(badge)) found.Add(badge);
                }
            }

            return found;
        }

        private static Dictionary<int, AchievementObjective> ReadObjectives(
            JsonElement root, Func<long, string> say)
        {
            var objectives = new Dictionary<int, AchievementObjective>();
            if (!root.TryGetProperty("objectives", out var table)) return objectives;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                string criterion = Text(entry.Value, "criterion");
                objectives[id] = new AchievementObjective
                {
                    Id = id,
                    AchievementId = (int)Long(entry.Value, "achievement"),
                    Name = say(Long(entry.Value, "name")),
                    Criterion = criterion,
                    Terms = QuestCriterion.Terms(criterion),
                };
            }

            return objectives;
        }

        /// <summary>The objectives the client does not describe, from the link file. Empty when it is not there.</summary>
        private static Dictionary<int, AchievementLink> ReadLinks(Action<string>? report)
        {
            var links = new Dictionary<int, AchievementLink>();
            string path = Paths.AchievementLinksJson;
            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; exploration, level and job " +
                               "achievements cannot be earned. Run tools/extract_achievement_links.py.");
                return links;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("objectives", out var table)) return links;

                foreach (var entry in table.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var row = entry.Value;
                    links[id] = new AchievementLink
                    {
                        Kind = Text(row, "kind"),
                        Subarea = (int)Long(row, "subarea"),
                        LevelNeeded = (int)Long(row, "level"),
                        Jobs = (int)Long(row, "jobs"),
                        Count = (int)Long(row, "count"),
                        QuestId = (int)Long(row, "quest"),
                        MonsterId = (int)Long(row, "monster"),
                    };
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }

            return links;
        }

        private static Dictionary<int, AchievementReward> ReadRewards(JsonElement root)
        {
            var rewards = new Dictionary<int, AchievementReward>();
            if (!root.TryGetProperty("rewards", out var table)) return rewards;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                var row = entry.Value;

                var items = new List<(int, int)>();
                if (row.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in list.EnumerateArray())
                    {
                        var numbers = Numbers(pair);
                        if (numbers.Count >= 2) items.Add((numbers[0], Math.Max(1, numbers[1])));
                        else if (numbers.Count == 1) items.Add((numbers[0], 1));
                    }
                }

                rewards[id] = new AchievementReward
                {
                    Id = id,
                    Criterion = Text(row, "criterion"),
                    ExperienceRatio = Double(row, "experienceRatio"),
                    KamasRatio = Double(row, "kamasRatio"),
                    KamasScale = Long(row, "kamasScale") != 0,
                    GuildPoints = (int)Long(row, "guildPoints"),
                    Items = items,
                    Spells = Numbers(row, "spells"),
                    Emotes = Numbers(row, "emotes"),
                    Titles = Numbers(row, "titles"),
                    Ornaments = Numbers(row, "ornaments"),
                };
            }

            return rewards;
        }

        private static string Text(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "")
                : "";

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;

        private static double Double(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.TryGetDouble(out double n) ? n : 0;

        private static List<int> Numbers(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) ? Numbers(v) : new List<int>();

        private static List<int> Numbers(JsonElement value)
        {
            var numbers = new List<int>();
            if (value.ValueKind != JsonValueKind.Array) return numbers;
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetInt32(out int n)) numbers.Add(n);
            }

            return numbers;
        }
    }
}
