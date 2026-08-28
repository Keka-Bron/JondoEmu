using System;
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Studio
{
    /// <summary>
    /// Builds every section once, off screen, and says which ones came up.
    /// </summary>
    /// <remarks>
    /// The logic behind these sections is covered by <c>Jondo.Unity.Tests</c> — the frame decoder,
    /// the log reader, the shapes, the content layers. What no test there reaches is the building
    /// of the controls themselves, and that is not a theoretical gap: a malformed column
    /// specification or a null in a template throws at construction, and the only way it had of
    /// showing up was somebody clicking that section.
    ///
    /// <c>Jondo Studio.exe --selftest</c> constructs all of them against the real data on this
    /// machine and exits non-zero if any threw. It does not show a window and it does not touch a
    /// file.
    ///
    /// What it does not cover, and should be said out loud: nothing here is attached to a visual
    /// tree, so anything that only happens once a section is on screen — the traffic view's poll,
    /// for one — is not exercised.
    /// </remarks>
    internal static class SelfTest
    {
        public static int Run()
        {
            var clock = Stopwatch.StartNew();
            var report = new StringBuilder();
            int broken = 0;

            WorldData world;
            try
            {
                world = WorldData.Load();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[selftest] the data would not load at all: " + ex);
                return 2;
            }

            report.AppendLine($"[selftest] npc placements {world.NpcPlacements.Count:N0}, " +
                              $"maps {world.MapCount:N0}, " +
                              $"protocol messages {world.Protocol.MessageCount:N0}, " +
                              $"packet notes {world.PacketNotes.Count:N0}");

            // The quest catalogue, and specifically the join it exists for. A step that says which
            // line hands it over is the only thing tying a quest to an NPC, so a run where that
            // number collapses has lost the point of the section without breaking anything.
            var quests = new QuestCatalogue(world.Text, null);
            report.AppendLine($"[selftest] quests {quests.QuestCount:N0}, " +
                              $"steps {quests.StepCount:N0} of which {quests.SpokenSteps:N0} spoken, " +
                              $"objectives {quests.ObjectiveCount:N0}, " +
                              $"gated behind another quest {quests.GatedQuests:N0}");

            // The map catalogue, timed. This is where the editor froze: two correlated subqueries
            // over unindexed tables took 67 seconds and read as a hang. A number here means the
            // next person to make that mistake finds out in the self test.
            try
            {
                var clockMaps = Stopwatch.StartNew();
                using var maps = new MapCatalogue(world.Text, null, world.NpcsPerMap());
                int howMany = maps.All().Count;
                long loaded = clockMaps.ElapsedMilliseconds;

                clockMaps.Restart();
                int found = maps.Find("[-2,0]").Count + maps.Find("241438721").Count;
                report.AppendLine($"[selftest] maps {howMany:N0} in {loaded} ms, two searches in " +
                                  $"{clockMaps.ElapsedMilliseconds} ms, {found} hits");
            }
            catch (Exception ex)
            {
                broken++;
                report.AppendLine("[selftest] BROKE the map catalogue: " + ex.Message);
            }

            // The pictures come out of the client's own bundles, which is the part most likely to
            // break on a patch, so the self test says out loud whether it could read them.
            try
            {
                using var icons = new MonsterIcons();
                using var monsters = new MonsterCatalogue(world.Text);

                // Counted rather than sampled, because the sample is what hid the bug: asking for
                // monster 31 came back with a picture and it was the wrong creature's. The number
                // that matters is how many of the 5,134 monsters find their own drawing.
                int covered = 0;
                int firstGfx = 0;
                if (monsters.Ready)
                {
                    foreach (var monster in monsters.All())
                    {
                        if (monster.GfxId <= 0) continue;
                        if (icons.Of(monster.GfxId) == null) continue;

                        covered++;
                        if (firstGfx == 0) firstGfx = monster.GfxId;
                    }
                }

                var one = firstGfx == 0 ? null : icons.Of(firstGfx);
                report.AppendLine($"[selftest] monster icons {icons.Count:N0} in the bundle, " +
                                  $"{covered:N0} monsters covered" +
                                  (one != null ? $", decoded at {one.PixelSize.Width}x{one.PixelSize.Height}" : ", none decoded") +
                                  (icons.Trouble.Length > 0 ? "  ·  " + icons.Trouble : ""));
            }
            catch (Exception ex)
            {
                report.AppendLine("[selftest] monster icons could not be read: " + ex.Message);
            }

            // The NPCs, drawn out of the client's own bones. This is the number that tells whether
            // the whole path still works after a patch: it is a long chain, and every link of it
            // fails quietly.
            try
            {
                var clockNpcs = Stopwatch.StartNew();
                using var sprites = new NpcSprites();
                using var npcs = new NpcCatalogue(world.Text);

                // Counted over distinct looks, not over NPCs: the cache means asking twice for the
                // same look is one drawing, and counting NPCs made the first version of this report
                // "43 of 60" when it had actually tried 49 things and drawn 43 of them.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int tried = 0;
                int drawn = 0;
                int humanoid = 0;
                int humanoidDrawn = 0;

                if (npcs.Ready)
                {
                    foreach (var npc in npcs.All())
                    {
                        if (npc.Look.Length == 0) continue;
                        if (!seen.Add(npc.Look)) continue;

                        bool isHumanoid = NpcLook.Parse(npc.Look).Humanoid;
                        if (isHumanoid && humanoid >= 12) continue;
                        if (!isHumanoid && tried >= 60) continue;

                        if (isHumanoid) humanoid++; else tried++;

                        if (sprites.Of(npc.Look) == null) continue;

                        if (isHumanoid) humanoidDrawn++; else drawn++;
                    }
                }

                report.AppendLine($"[selftest] breed table {Breeds.Count:N0} skins");

                // Written out so a person can look at it. "not null" is not the same as "a
                // picture of somebody", and the difference is exactly the bug that would otherwise
                // take an hour of staring at a grid.
                foreach (var npc in npcs.All())
                {
                    if (npc.Look.Length == 0 || !NpcLook.Parse(npc.Look).Humanoid) continue;

                    var shown = sprites.Of(npc.Look);
                    if (shown == null) continue;

                    string into = System.IO.Path.Combine(Jondo.Unity.Launcher.Paths.LogsDir, "studio_npc_sample.png");
                    shown.Save(into);
                    report.AppendLine($"[selftest] wrote {npc.Name} ({npc.Look}) " +
                                      $"{shown.PixelSize.Width}x{shown.PixelSize.Height} to {into}");
                    report.AppendLine($"[selftest]   made of {sprites.LastMakeup}");
                    break;
                }

                report.AppendLine($"[selftest] npc sprites {drawn}/{tried} drawn, " +
                                  $"humanoid {humanoidDrawn}/{humanoid}, in {clockNpcs.ElapsedMilliseconds} ms" +
                                  (sprites.Why.Count > 0 ? "  ·  " + sprites.Reasons() : "") +
                                  (sprites.Trouble.Length > 0 ? "  ·  " + sprites.Trouble : ""));
            }
            catch (Exception ex)
            {
                broken++;
                report.AppendLine("[selftest] BROKE the npc sprites: " + ex.Message);
            }

            foreach (string complaint in world.Complaints)
            {
                report.AppendLine("[selftest] complaint: " + complaint);
            }

            // Every section in every language. Building them once in Spanish would miss the half
            // of the work that only happens on a change of language: reopening the client's text
            // table, and every string that is looked up while a page is built rather than before.
            foreach (var language in Ui.Words.Offered)
            {
                Ui.Words.Use(language);
                world.UseLanguage(language);

                string tag = Ui.Words.TagOf(language);
                report.AppendLine($"[selftest] {tag}: game texts " +
                                  (world.Text == null ? "not there" : world.Text.Count.ToString("N0")));

                foreach (var page in Shell.Sections(world))
                {
                    try
                    {
                        Control built = page.Build();
                        report.AppendLine($"[selftest] ok    {tag}  {page.Title} ({built.GetType().Name})");
                    }
                    catch (Exception ex)
                    {
                        broken++;
                        report.AppendLine($"[selftest] BROKE {tag}  {page.Title}: {ex.GetType().Name}: {ex.Message}");
                        report.AppendLine(ex.StackTrace);
                    }
                }
            }

            Ui.Words.Use(Jondo.Unity.World.Client.GameLanguage.Spanish);

            report.AppendLine($"[selftest] {broken} broken, {clock.ElapsedMilliseconds} ms");
            Publish(report.ToString());
            return broken == 0 ? 0 : 1;
        }

        /// <summary>
        /// Puts the report where it can be read.
        /// </summary>
        /// <remarks>
        /// To a file as well as to the console, and the file is the one that matters: this is a
        /// windowed application, so there is no console attached when it is started from a shell
        /// and everything written to standard output goes nowhere. Finding that out by running the
        /// self test and getting an empty screen is a good way to conclude it did not run.
        /// </remarks>
        private static void Publish(string report)
        {
            Console.Write(report);

            try
            {
                string path = System.IO.Path.Combine(Jondo.Unity.Launcher.Paths.LogsDir,
                                                     "studio_selftest.log");
                System.IO.Directory.CreateDirectory(Jondo.Unity.Launcher.Paths.LogsDir);
                System.IO.File.WriteAllText(path, report);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[selftest] the report could not be written: " + ex.Message);
            }
        }
    }
}
