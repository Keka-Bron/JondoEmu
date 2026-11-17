using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// The admin menu of the client (JondoFix/Admin/menuadmin.xml) against the server: every command
    /// it can send is one the server knows, a menu the client's AdminMenu cannot read is refused,
    /// and what is dangerous sits behind a confirmation. The same guard Wax keeps on its own menu.
    /// </summary>
    public class AdminMenuTests
    {
        /// <summary>The types the client's AdminMenu parses (Core.UILogic.Admin.AdminMenu, 3.6.10.11).</summary>
        private static readonly HashSet<string> ClientTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "menu", "static", "separator", "sendcommand", "preparecommand", "sendchat", "batch", "startup", "loadxml",
        };

        /// <summary>The windows of selection the client opens for these tokens (AdminSelectItemUI).</summary>
        private static readonly HashSet<string> Selectors = new(StringComparer.Ordinal)
        {
            "#item", "#monster", "#look", "#fireworks", "#spell", "#npc",
        };

        /// <summary>Commands that stop or lock out: only behind a "Confirmer" menu, but cancelling a stop.</summary>
        private static readonly string[] Dangerous = { "ban", "shutdown" };

        /// <summary>The menu exists in the client's three languages; JondoFix installs the one it starts in.</summary>
        public static readonly TheoryData<string> Languages = new() { "fr", "en", "es" };

        /// <summary>What a confirmation submenu and an administrator's menu are called in each language.</summary>
        private static readonly Dictionary<string, (string Confirm, string Administrator)> Words = new()
        {
            ["fr"] = ("Confirmer", "administrateur"),
            ["en"] = ("Confirm", "administrator"),
            ["es"] = ("Confirmar", "administrador"),
        };

        private static string MenuPath(string language)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "JondoFix", "Admin", $"menuadmin.{language}.xml");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException($"JondoFix/Admin/menuadmin.{language}.xml not found above " + AppContext.BaseDirectory);
        }

        private static IEnumerable<(XElement Item, string Type, string Command, string[] Path)> Items(string language)
        {
            var root = XDocument.Load(MenuPath(language)).Root!;
            return Walk(root, Array.Empty<string>());

            static IEnumerable<(XElement, string, string, string[])> Walk(XElement parent, string[] path)
            {
                foreach (var item in parent.Elements("item"))
                {
                    string type = (string?)item.Attribute("type") ?? "";
                    yield return (item, type, (string?)item.Attribute("command") ?? "", path);
                    string[] inside = type.Equals("menu", StringComparison.OrdinalIgnoreCase)
                        ? path.Append((string?)item.Attribute("label") ?? "").ToArray() : path;
                    foreach (var child in Walk(item, inside)) yield return child;
                }
            }
        }

        private static string Plain(string label) => System.Text.RegularExpressions.Regex.Replace(label, "<[^>]+>", "");

        private static string Verb(string command)
        {
            string word = command.Trim().Split(' ', 2)[0];
            return word.TrimStart('.').ToLowerInvariant();
        }

        private static readonly HashSet<string> Known = new(
            CommandHandler.Names.Select(n => n.TrimStart('.').ToLowerInvariant()).Append("help"));

        [Theory, MemberData(nameof(Languages))]
        public void Every_command_of_the_menu_is_a_command_the_server_knows(string language)
        {
            var unknown = Items(language)
                .Where(i => i.Command.Length > 0 && i.Type is not "static")
                .Where(i => !Known.Contains(Verb(i.Command)))
                .Select(i => $"{Plain((string?)i.Item.Attribute("label") ?? "")} => {i.Command}")
                .ToList();
            Assert.True(unknown.Count == 0, "Not commands of CommandHandler: " + string.Join(" | ", unknown));
        }

        [Theory, MemberData(nameof(Languages))]
        public void Every_item_is_of_a_type_the_client_reads_and_without_a_rank(string language)
        {
            foreach (var (item, type, _, _) in Items(language))
            {
                Assert.True(ClientTypes.Contains(type), $"type '{type}' is not one the client's AdminMenu parses");
                // The client hides an item whose rank is above the player's, and the player's is 0 here.
                Assert.Null(item.Attribute("rank"));
            }
        }

        [Theory, MemberData(nameof(Languages))]
        public void Commands_send_what_they_must_and_chat_lines_are_commands_with_a_dot(string language)
        {
            foreach (var (item, type, command, _) in Items(language))
            {
                if (type is "sendcommand" or "preparecommand")
                    Assert.False(command.StartsWith('.'), $"'{command}': the console takes the command without its dot");
                if (type is "sendchat")
                    Assert.True(command.StartsWith('.'), $"'{command}': a chat line is a command only with its dot");
                if (type is "sendcommand" or "sendchat" or "preparecommand")
                    Assert.True(command.Length > 0, "an item that sends has a command");
            }
        }

        [Theory, MemberData(nameof(Languages))]
        public void Parameters_and_selectors_are_the_ones_the_client_replaces(string language)
        {
            foreach (var (_, _, command, _) in Items(language).Where(i => i.Command.Length > 0))
            {
                foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith('#')) Assert.True(Selectors.Contains(token), $"'{token}' opens no selector");
                    if (token.StartsWith('%')) Assert.Equal("%p", token);
                }
            }
        }

        [Theory, MemberData(nameof(Languages))]
        public void A_selector_comes_with_the_command_that_takes_its_answer(string language)
        {
            // "#item" gives an id, "#npc" an id, "#monster" an id: the commands read them first and the player last.
            var withSelector = Items(language).Where(i => i.Command.Contains('#')).Select(i => i.Command).ToList();
            Assert.Contains("item #item %p", withSelector);
            Assert.All(withSelector, c => Assert.True(c.StartsWith("item ") || c.StartsWith("spawn "), c));
        }

        [Theory, MemberData(nameof(Languages))]
        public void The_dangerous_commands_sit_behind_a_confirmation(string language)
        {
            var unsafeOnes = Items(language)
                .Where(i => i.Type is "sendcommand" && Dangerous.Contains(Verb(i.Command)) && !i.Command.Contains("cancel"))
                .Where(i => !i.Path.Any(p => Plain(p).StartsWith(Words[language].Confirm, StringComparison.OrdinalIgnoreCase)))
                .Select(i => i.Command)
                .ToList();
            Assert.True(unsafeOnes.Count == 0, "No confirmation for: " + string.Join(", ", unsafeOnes));
        }

        [Theory, MemberData(nameof(Languages))]
        public void Commands_only_an_administrator_may_run_are_under_an_administrators_menu(string language)
        {
            var shown = Items(language)
                .Where(i => i.Type is "sendcommand" or "preparecommand" or "sendchat" && i.Command.Length > 0)
                // "help" is answered by the staff console itself, to any moderator.
                .Where(i => Verb(i.Command) != "help")
                .Where(i => CommandHandler.RequiredRole("." + Verb(i.Command)) >= Roles.Administrador)
                .Where(i => !i.Path.Concat(new[] { (string?)i.Item.Attribute("label") ?? "" })
                    .Any(p => Plain(p).Contains(Words[language].Administrator, StringComparison.OrdinalIgnoreCase)))
                .Select(i => i.Command)
                .ToList();
            Assert.True(shown.Count == 0, "Administrator commands outside an administrator menu: " + string.Join(", ", shown));
        }

        /// <summary>Menus nest to any depth, but a context menu with more than four levels cannot be navigated.</summary>
        [Theory, MemberData(nameof(Languages))]
        public void The_menu_is_not_nested_deeper_than_it_can_be_used(string language)
            => Assert.All(Items(language), i => Assert.InRange(i.Path.Length, 0, 4));

        [Theory, MemberData(nameof(Languages))]
        public void The_menu_is_embedded_in_the_mod_that_installs_it(string language)
        {
            string project = File.ReadAllText(Path.Combine(Path.GetDirectoryName(MenuPath(language))!, "..", "JondoFix.csproj"));
            Assert.Contains($"Admin\\menuadmin.{language}.xml", project);
        }

        /// <summary>
        /// The translations are the French menu's labels and nothing else: the same items, in the same
        /// places, with the same types and commands -- but for the ready-made text a warning or an
        /// announcement carries, which is the admin's language too.
        /// </summary>
        [Theory]
        [InlineData("en")]
        [InlineData("es")]
        public void A_translation_has_the_french_menus_items_and_commands(string language)
        {
            static string Shape((XElement Item, string Type, string Command, string[] Path) i)
            {
                string verb = i.Command.StartsWith("warn %p ") || i.Command.StartsWith("announce ")
                    ? string.Join(' ', i.Command.Split(' ').Take(i.Command.StartsWith("warn") ? 2 : 1))
                    : i.Command;
                return $"{i.Path.Length}|{i.Type}|{verb}";
            }
            Assert.Equal(Items("fr").Select(Shape).ToList(), Items(language).Select(Shape).ToList());
        }
    }
}
