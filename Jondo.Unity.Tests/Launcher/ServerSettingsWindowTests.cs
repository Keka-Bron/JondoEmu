using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Server.UI;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The server's Settings window laid out at a size: opened, it is two columns that scroll on a
    /// short screen; maximized, every card is in sight at once -- on a small screen with the hints in
    /// tooltips.
    /// </summary>
    public class ServerSettingsWindowTests
    {
        /// <summary>The window at a client size, built and measured on a thread of its own, as WinForms wants.</summary>
        private static void At(Size client, Action<Panel, SettingsCard[]> check)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var window = new ServerSettingsWindow(Language.Es);
                    window.ClientSize = client;
                    var body = window.Controls.OfType<Panel>().Single();
                    check(body, body.Controls.OfType<SettingsCard>().ToArray());
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        [Fact]
        public void Maximized_every_setting_is_in_sight()
        {
            // A 1280 x 800 screen (1920 x 1200 at 150 %) maximized: its 752 above the taskbar, less the title bar.
            At(new Size(1264, 713), (body, cards) =>
            {
                Assert.True(cards.Length >= 8);
                Assert.True(cards.Max(c => c.Bottom) <= body.ClientSize.Height, $"{cards.Select(c => c.Left).Distinct().Count()} columns, the lowest card ends at {cards.Max(c => c.Bottom)} of {body.ClientSize.Height}: " + string.Join(", ", cards.Select(c => $"{c.Text}={c.Height}@{c.Left},{c.Top}")));
                Assert.All(cards, c => Assert.True(c.Bottom <= body.ClientSize.Height,
                    $"a card ends at {c.Bottom}, below the {body.ClientSize.Height} in sight"));
                Assert.All(cards, c => Assert.True(c.Right <= body.ClientSize.Width));
                foreach (var a in cards)
                    foreach (var b in cards.Where(b => b != a))
                        Assert.False(a.Bounds.IntersectsWith(b.Bounds), "two cards overlap");
            });
        }

        [Fact]
        public void Narrow_it_keeps_one_column_and_scrolls()
        {
            At(new Size(520, 500), (body, cards) =>
            {
                Assert.Single(cards.Select(c => c.Left).Distinct());
                Assert.True(cards.Max(c => c.Bottom) > body.ClientSize.Height);
                // Scrolling, the hints stay under their settings.
                Assert.DoesNotContain(Rows(cards), r => r.Compact);
            });
        }

        [Fact]
        public void On_a_big_screen_the_hints_stay_under_their_settings()
        {
            // 1920 x 1080 at 100 % maximized: room for every card with its hints; the tooltips are
            // only for a small screen.
            At(new Size(1904, 1001), (body, cards) =>
            {
                Assert.True(cards.Max(c => c.Bottom) <= body.ClientSize.Height);
                Assert.DoesNotContain(Rows(cards), r => r.Compact);
            });
        }

        private static SettingRow[] Rows(SettingsCard[] cards)
            => cards.SelectMany(c => c.Controls.OfType<SettingRow>()).ToArray();
    }
}
