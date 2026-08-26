using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>Everything known about one NPC, for the panel that says what it is.</summary>
    public sealed class NpcDetail
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public string Look { get; init; } = "";
        public int Gender { get; init; }

        /// <summary>What the right-click menu offers. The first one is what the client sends.</summary>
        public IReadOnlyList<int> Actions { get; init; } = Array.Empty<int>();

        public int Messages { get; init; }
        public int Replies { get; init; }

        /// <summary>What it sells, by name. Empty when it sells nothing.</summary>
        public IReadOnlyList<string> Sells { get; init; } = Array.Empty<string>();

        /// <summary>How many items in total, which can be more than <see cref="Sells"/> shows.</summary>
        public int SellsCount { get; init; }
    }

    /// <summary>
    /// Reads what an NPC is: what it does, what it says, what it sells.
    /// </summary>
    /// <remarks>
    /// Clicking a placement used to say nothing but its coordinates, which is the least interesting
    /// thing about it. What somebody wants to know is whether this is a merchant, whether it talks,
    /// and what it has.
    ///
    /// <b>The action ids are shown raw except for the two that are measured.</b> Action 3 is on
    /// 5,915 of the 6,468 NPCs and is the plain talk action. Action 1 is on 109, and 45 of the 51
    /// NPCs we have a shop for carry it — so it is the shop action, and the server already relies
    /// on that: the <c>iov</c> the client sends carries <c>actions[0]</c>, checked on 51 of 51
    /// shop NPCs in the captures. The rest — 11, 17, 2, 9, 14, 15, 16 — have not been measured and
    /// are shown as numbers rather than guessed at.
    /// </remarks>
    public sealed class NpcDetails : IDisposable
    {
        private readonly SqliteConnection? _world;
        private readonly ClientText? _text;

        private Dictionary<int, List<int>>? _shops;
        private Dictionary<int, string>? _itemNames;

        public NpcDetails(ClientText? text = null, Action<string>? report = null)
        {
            _text = text;

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }
        }

        public bool Ready => _world != null;

        /// <summary>How many of a shop's items are listed by name. The rest are counted.</summary>
        private const int Listed = 12;

        public NpcDetail? Of(int npcId)
        {
            if (_world == null) return null;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT n.NameId, t.Text, n.Data
                FROM NpcTemplates n
                LEFT JOIN Translations t ON t.Key = CAST(n.NameId AS TEXT)
                WHERE n.Id = $id;";
            command.Parameters.AddWithValue("$id", npcId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            string name = "";
            if (!reader.IsDBNull(0) && _text != null) name = _text.Of(reader.GetInt64(0));
            if (name.Length == 0 && !reader.IsDBNull(1)) name = reader.GetString(1);

            string data = reader.IsDBNull(2) ? "" : reader.GetString(2);

            var actions = new List<int>();
            string look = "";
            int gender = 0;
            int messages = 0;
            int replies = 0;

            if (data.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    look = root.TryGetProperty("look", out var l) ? l.GetString() ?? "" : "";
                    gender = root.TryGetProperty("gender", out var g) && g.TryGetInt32(out int which) ? which : 0;
                    messages = Length(root, "dialogData");
                    replies = Length(root, "dialogReplies");

                    if (root.TryGetProperty("actions", out var acts) &&
                        acts.TryGetProperty("Array", out var list))
                    {
                        foreach (var action in list.EnumerateArray())
                        {
                            if (action.TryGetInt32(out int id)) actions.Add(id);
                        }
                    }
                }
                catch (JsonException)
                {
                    // A template that will not parse is a panel with less on it, not a crash.
                }
            }

            var sells = new List<string>();
            int sellsCount = 0;
            if (Shops().TryGetValue(npcId, out var stock))
            {
                sellsCount = stock.Count;
                var names = ItemNames();

                foreach (int item in stock)
                {
                    if (sells.Count >= Listed) break;
                    sells.Add(names.TryGetValue(item, out string? said) && said.Length > 0
                        ? said
                        : item.ToString());
                }
            }

            return new NpcDetail
            {
                Id = npcId,
                Name = name,
                Look = look,
                Gender = gender,
                Actions = actions,
                Messages = messages,
                Replies = replies,
                Sells = sells,
                SellsCount = sellsCount,
            };
        }

        /// <summary>The two action ids that have been measured. Everything else stays a number.</summary>
        public static string Say(int action) => action switch
        {
            1 => Words.Shop,
            3 => Words.Talk,
            _ => "",
        };

        private static class Words
        {
            public static string Shop => Ui.Words.T("npc.actionShop");
            public static string Talk => Ui.Words.T("npc.actionTalk");
        }

        private static int Length(JsonElement root, string name)
            => root.TryGetProperty(name, out var holder) && holder.TryGetProperty("Array", out var array)
               && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;

        /// <summary>Who sells what, out of the generated shop file. Read once.</summary>
        private Dictionary<int, List<int>> Shops()
        {
            if (_shops != null) return _shops;

            _shops = new Dictionary<int, List<int>>();
            string path = Paths.NpcShopsJson;
            if (!File.Exists(path)) return _shops;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("npcs", out var npcs)) return _shops;

                foreach (var entry in npcs.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int npcId)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Array) continue;

                    var items = new List<int>();
                    foreach (var item in entry.Value.EnumerateArray())
                    {
                        if (item.TryGetInt32(out int id)) items.Add(id);
                    }

                    _shops[npcId] = items;
                }
            }
            catch (Exception)
            {
                // An unreadable shop file is a panel without a shop on it.
            }

            return _shops;
        }

        private Dictionary<int, string> ItemNames()
        {
            if (_itemNames != null) return _itemNames;

            _itemNames = new Dictionary<int, string>();
            if (_world == null) return _itemNames;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT i.Id, i.NameId, t.Text
                FROM ItemTemplates i
                LEFT JOIN Translations t ON t.Key = CAST(i.NameId AS TEXT);";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string said = "";
                if (!reader.IsDBNull(1) && _text != null) said = _text.Of(reader.GetInt64(1));
                if (said.Length == 0 && !reader.IsDBNull(2)) said = reader.GetString(2);
                if (said.Length > 0) _itemNames[reader.GetInt32(0)] = said;
            }

            return _itemNames;
        }

        public void Dispose() => _world?.Dispose();
    }
}
