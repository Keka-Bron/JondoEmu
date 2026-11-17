using System;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>
    /// The staff's commands (CommandHandler.Admin.cs): who a command is for, how its arguments are
    /// read, and which role each asks for. What they do to a session needs a connected client and
    /// is tried in the game; what is decided before that is here.
    /// </summary>
    public class AdminCommandTests
    {
        private static bool Online(string name)
            => name.Equals("Cible-Un", StringComparison.OrdinalIgnoreCase) || name.Equals("Max", StringComparison.OrdinalIgnoreCase);

        // ─── Who the command is for ─────────────────────────────────────────────

        [Theory]
        [InlineData("1000000 Cible-Un", "1000000", "Cible-Un")]
        [InlineData("-1000000 cible-un", "-1000000", "cible-un")]
        [InlineData("start 619 Cible-Un", "start 619", "Cible-Un")]
        [InlineData("39 5 @Cible-Un", "39 5", "Cible-Un")]
        [InlineData("Cible-Un", "", "Cible-Un")]
        public void The_last_word_is_the_target_when_it_is_a_connected_player(string typed, string rest, string target)
        {
            var (arguments, name) = CommandHandler.SplitTarget(typed, Online);
            Assert.Equal(rest, arguments);
            Assert.Equal(target, name);
        }

        [Theory]
        [InlineData("1000000")]
        [InlineData("39 5 max")]
        [InlineData("39 5 random")]
        [InlineData("[4,-19]")]
        [InlineData("start 619")]
        [InlineData("1000000 Nobody")]
        [InlineData("")]
        public void Anything_else_is_the_commands_own_and_stays_whole(string typed)
        {
            var (arguments, name) = CommandHandler.SplitTarget(typed, Online);
            Assert.Null(name);
            Assert.Equal(typed.Trim(), arguments);
        }

        /// <summary>A player called Max must not turn ".item 39 5 max" into an item for him.</summary>
        [Fact]
        public void An_option_word_is_never_a_player_unless_it_is_written_with_an_at_sign()
        {
            Assert.Null(CommandHandler.SplitTarget("39 5 max", Online).Target);
            Assert.Equal("Max", CommandHandler.SplitTarget("39 5 @Max", Online).Target);
        }

        [Fact]
        public void The_first_word_and_what_follows()
        {
            Assert.Equal("Cible-Un", CommandHandler.FirstWord("Cible-Un 30m  trop de bruit", out string after));
            Assert.Equal("30m  trop de bruit", after);
            Assert.Equal("", CommandHandler.FirstWord("  ", out after));
            Assert.Equal("", after);
        }

        // ─── Which role ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(".who")]
        [InlineData(".whoami")]
        [InlineData(".whois")]
        [InlineData(".where")]
        [InlineData(".mapinfo")]
        [InlineData(".version")]
        [InlineData(".goto")]
        [InlineData(".summon")]
        [InlineData(".jail")]
        [InlineData(".unjail")]
        [InlineData(".jailed")]
        [InlineData(".kick")]
        [InlineData(".mute")]
        [InlineData(".unmute")]
        [InlineData(".warn")]
        [InlineData(".teleport")]
        public void A_moderator_moves_and_sanctions(string command)
            => Assert.Equal(Roles.Moderador, CommandHandler.RequiredRole(command));

        [Theory]
        [InlineData(".quest")]
        [InlineData(".quests")]
        [InlineData(".energy")]
        [InlineData(".heal")]
        [InlineData(".stats")]
        [InlineData(".spawn")]
        [InlineData(".remove")]
        [InlineData(".kamas")]
        [InlineData(".level")]
        public void A_game_master_touches_the_character_and_the_map(string command)
            => Assert.Equal(Roles.GameMaster, CommandHandler.RequiredRole(command));

        [Theory]
        [InlineData(".ban")]
        [InlineData(".unban")]
        [InlineData(".bans")]
        [InlineData(".gift")]
        [InlineData(".announce")]
        [InlineData(".save")]
        [InlineData(".reload")]
        [InlineData(".shutdown")]
        public void An_administrator_rules_the_server(string command)
            => Assert.Equal(Roles.Administrador, CommandHandler.RequiredRole(command));

        [Theory]
        [InlineData(Roles.Moderador, Roles.Jugador, true)]
        [InlineData(Roles.GameMaster, Roles.Moderador, true)]
        [InlineData(Roles.Administrador, Roles.GameMaster, true)]
        [InlineData(Roles.Moderador, Roles.Moderador, false)]
        [InlineData(Roles.Moderador, Roles.Administrador, false)]
        [InlineData(Roles.GameMaster, Roles.Administrador, false)]
        [InlineData(Roles.Administrador, Roles.Administrador, false)]
        public void Staff_act_only_on_lower_roles(int staff, int target, bool allowed)
            => Assert.Equal(allowed, CommandHandler.MayActOn(staff, 1, target, 2));

        [Fact]
        public void Staff_always_act_on_their_own_account()
            => Assert.True(CommandHandler.MayActOn(Roles.Moderador, 7, Roles.Moderador, 7));

        [Fact]
        public void The_last_word_is_read_for_a_forced_name()
        {
            Assert.Equal("@Cible", CommandHandler.LastWordOf("100000 @Cible"));
            Assert.Equal("", CommandHandler.LastWordOf("   "));
        }

        /// <summary>A command nobody gave a role to is the administrator's: the safe side to be wrong on.</summary>
        [Fact]
        public void A_command_without_a_role_is_an_administrators()
            => Assert.Equal(Roles.Administrador, CommandHandler.RequiredRole(".nobody-wrote-this"));

        [Fact]
        public void Every_command_the_menu_can_run_has_a_usage_text_in_the_three_languages()
        {
            foreach (var (command, key) in CommandHandler.UsageKeys)
            {
                foreach (string language in new[] { "es", "en", "fr" })
                    Assert.False(string.IsNullOrWhiteSpace(CommandTexts.Get(language, key)), $"{command} in {language}");
            }
        }

        // ─── The arguments ──────────────────────────────────────────────────────

        [Theory]
        [InlineData("start 619", "start", 619)]
        [InlineData("FINISH 12", "finish", 12)]
        [InlineData("forget 1", "forget", 1)]
        public void A_quest_op_and_its_id(string typed, string op, int quest)
        {
            Assert.True(CommandHandler.TryParseQuest(typed, out string gotOp, out int gotQuest));
            Assert.Equal(op, gotOp);
            Assert.Equal(quest, gotQuest);
        }

        [Theory]
        [InlineData("")]
        [InlineData("start")]
        [InlineData("start 0")]
        [InlineData("start -3")]
        [InlineData("tick 12")]
        [InlineData("start 12 13")]
        public void A_quest_op_wrongly_written_is_refused(string typed)
            => Assert.False(CommandHandler.TryParseQuest(typed, out _, out _));

        [Theory]
        [InlineData("vit", "vitality")]
        [InlineData("SAG", "wisdom")]
        [InlineData("force", "strength")]
        [InlineData("int", "intelligence")]
        [InlineData("cha", "chance")]
        [InlineData("agilidad", "agility")]
        public void A_characteristic_by_the_names_staff_type(string typed, string stat)
        {
            Assert.True(CommandHandler.TryParseStat(typed, out string got));
            Assert.Equal(stat, got);
        }

        [Fact]
        public void An_unknown_characteristic_is_refused() => Assert.False(CommandHandler.TryParseStat("luck", out _));

        [Fact]
        public void Monsters_with_and_without_a_grade()
        {
            Assert.True(CommandHandler.TryParseMonsters("31 31:2 62:6", out var members));
            Assert.Equal(new[] { (31, 1), (31, 2), (62, 6) }, members.ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("abc")]
        [InlineData("31:0")]
        [InlineData("31:7")]
        [InlineData("31:2:3")]
        [InlineData("1 2 3 4 5 6 7 8 9")]
        public void Monsters_wrongly_written_are_refused(string typed)
            => Assert.False(CommandHandler.TryParseMonsters(typed, out _));

        [Theory]
        [InlineData("30", 30)]
        [InlineData("30m", 30)]
        [InlineData("12h", 720)]
        [InlineData("7d", 10080)]
        [InlineData("0", 0)]
        public void A_duration_in_minutes_hours_or_days(string typed, int minutes)
        {
            Assert.True(Sanctions.TryParseDuration(typed, out var time));
            Assert.Equal(TimeSpan.FromMinutes(minutes), time);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-5")]
        [InlineData("5x")]
        [InlineData("h")]
        [InlineData("1.5h")]
        [InlineData("99999999d")]
        public void A_duration_wrongly_written_is_refused(string typed)
            => Assert.False(Sanctions.TryParseDuration(typed, out _));

        // ─── Mutes (in memory: no database) ─────────────────────────────────────

        [Fact]
        public void A_mute_holds_until_it_is_lifted()
        {
            const long character = 987_654_321_001;
            Assert.False(Sanctions.IsMuted(character));
            Sanctions.Mute(character, TimeSpan.FromMinutes(5));
            Assert.True(Sanctions.IsMuted(character));
            Assert.InRange(Sanctions.MinutesLeft(character), 1, 5);
            Assert.True(Sanctions.Unmute(character));
            Assert.False(Sanctions.IsMuted(character));
            Assert.False(Sanctions.Unmute(character));
        }

        [Fact]
        public void A_mute_that_has_run_out_no_longer_holds()
        {
            const long character = 987_654_321_002;
            Sanctions.Mute(character, TimeSpan.FromMilliseconds(-1));
            Assert.False(Sanctions.IsMuted(character));
            Assert.Equal(0, Sanctions.MinutesLeft(character));
            Sanctions.Unmute(character);
        }

        [Fact]
        public void A_ban_that_ends_holds_only_until_then()
        {
            var forever = new Sanctions.Ban(1, "A", null, 9);
            var ended = new Sanctions.Ban(2, "B", DateTime.UtcNow.AddMinutes(-1), 9);
            var running = new Sanctions.Ban(3, "C", DateTime.UtcNow.AddMinutes(1), 9);
            Assert.True(forever.Holds(DateTime.UtcNow));
            Assert.False(ended.Holds(DateTime.UtcNow));
            Assert.True(running.Holds(DateTime.UtcNow));
        }
    }
}
