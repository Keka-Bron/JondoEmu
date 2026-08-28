using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;

namespace Jondo.Unity.World.Achievements
{
    /// <summary>One thing an achievement wants done. All of them have to hold.</summary>
    public sealed class AchievementObjective
    {
        public int Id { get; init; }
        public int AchievementId { get; init; }
        public string Name { get; init; } = "";

        /// <summary>Written in the same language a quest's start condition is.</summary>
        public string Criterion { get; init; } = "";
    }

    /// <summary>What an achievement hands over.</summary>
    public sealed class AchievementReward
    {
        public int Id { get; init; }

        /// <summary>Who gets this one. Empty means everybody who earns the achievement.</summary>
        public string Criterion { get; init; } = "";

        public double ExperienceRatio { get; init; }
        public double KamasRatio { get; init; }
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
    /// carry. Shared between the server, which grants them, and the editor, which shows them.
    ///
    /// <b>What an achievement is, in one line:</b> a list of criteria, all of which must hold, and
    /// a list of rewards, each with its own criterion saying who gets it. The criteria are the same
    /// language a quest's start condition is written in, which is why <see cref="Quests.QuestCriterion"/>
    /// reads both.
    ///
    /// Two indexes are built at load because the engine asks the questions backwards from the way
    /// the data is written: when a quest is finished it needs the achievements that were waiting on
    /// that quest, and when an achievement is earned it needs the achievements that were waiting on
    /// <em>it</em> — 2,157 objectives are nothing but <c>OA</c>, an achievement needing others.
    /// Without the indexes, granting one badge would mean walking 8,946 criterion strings.
    /// </remarks>
    public sealed class AchievementCatalogue
    {
        private readonly Dictionary<int, Achievement> _byId = new Dictionary<int, Achievement>();

        /// <summary>Quest id to the achievements that are waiting on it being finished.</summary>
        private readonly Dictionary<int, List<int>> _waitingOnQuest = new Dictionary<int, List<int>>();

        /// <summary>Achievement id to the achievements that are waiting on it being earned.</summary>
        private readonly Dictionary<int, List<int>> _waitingOnAchievement
            = new Dictionary<int, List<int>>();

        private List<Achievement>? _all;

        public AchievementCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            Read(text, report);
        }

        public bool Ready => _byId.Count > 0;

        public int Count => _byId.Count;
        public int ObjectiveCount { get; private set; }
        public int RewardCount { get; private set; }

        /// <summary>How many are earned by finishing quests. The join the whole thing hangs on.</summary>
        public int FromQuestsCount { get; private set; }

        public Achievement? Of(int id) => _byId.TryGetValue(id, out var a) ? a : null;

        public List<Achievement> All()
        {
            if (_all != null) return _all;

            _all = new List<Achievement>(_byId.Values);
            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _all;
        }

        /// <summary>The achievements that could have become earnable now that this quest is done.</summary>
        public IReadOnlyList<int> WaitingOnQuest(int questId)
            => _waitingOnQuest.TryGetValue(questId, out var list) ? list : (IReadOnlyList<int>)Array.Empty<int>();

        /// <summary>The achievements built on this one.</summary>
        public IReadOnlyList<int> WaitingOnAchievement(int achievementId)
            => _waitingOnAchievement.TryGetValue(achievementId, out var list)
                ? list
                : (IReadOnlyList<int>)Array.Empty<int>();

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

                var categories = new Dictionary<int, string>();
                if (root.TryGetProperty("categories", out var cats))
                {
                    foreach (var entry in cats.EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int id)) categories[id] = Say(Long(entry.Value, "name"));
                    }
                }

                var objectives = ReadObjectives(root, Say);
                var rewards = ReadRewards(root);

                if (!root.TryGetProperty("achievements", out var table)) return;

                foreach (var entry in table.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var row = entry.Value;

                    var mine = new List<AchievementObjective>();
                    foreach (int objectiveId in Numbers(row, "objectives"))
                    {
                        if (objectives.TryGetValue(objectiveId, out var objective)) mine.Add(objective);
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
                        foreach (int quest in Quests.QuestCatalogue.FinishedQuests(objective.Criterion))
                        {
                            if (!quests.Contains(quest)) quests.Add(quest);
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
                        Category = categories.TryGetValue(categoryId, out string? said) ? said : "",
                        Level = (int)Long(row, "level"),
                        Points = (int)Long(row, "points"),
                        AccountWide = Long(row, "accountWide") != 0,
                        Objectives = mine,
                        Rewards = pays,
                        FromQuests = quests,
                        FromAchievements = badges,
                    };

                    _byId[id] = achievement;
                    if (quests.Count > 0) FromQuestsCount++;

                    foreach (int quest in quests) Index(_waitingOnQuest, quest, id);
                    foreach (int badge in badges) Index(_waitingOnAchievement, badge, id);
                }

                ObjectiveCount = objectives.Count;
                RewardCount = rewards.Count;
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        private static void Index(Dictionary<int, List<int>> into, int key, int value)
        {
            if (!into.TryGetValue(key, out var list)) into[key] = list = new List<int>();
            if (!list.Contains(value)) list.Add(value);
        }

        /// <summary>
        /// The achievements a criterion says must already be earned.
        /// </summary>
        /// <remarks>
        /// The <c>OA</c> twin of <see cref="Quests.QuestCriterion.FinishedQuests"/>, and equality
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
                objectives[id] = new AchievementObjective
                {
                    Id = id,
                    AchievementId = (int)Long(entry.Value, "achievement"),
                    Name = say(Long(entry.Value, "name")),
                    Criterion = Text(entry.Value, "criterion"),
                };
            }

            return objectives;
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
