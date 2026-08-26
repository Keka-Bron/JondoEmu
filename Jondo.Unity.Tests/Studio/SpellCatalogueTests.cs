using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Data;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Studio
{
    /// <summary>
    /// The spells, and which monster knows which.
    /// </summary>
    /// <remarks>
    /// Run against <c>world.db</c> on this machine and skip without it. The one that matters is the
    /// spellbook's source: reading it from the wrong column marked every monster in the game as
    /// having nothing to cast, and the screen looked entirely plausible while doing it.
    /// </remarks>
    public class SpellCatalogueTests
    {
        private static bool Here => File.Exists(Paths.WorldDb);

        /// <summary>
        /// Most monsters have spells.
        /// </summary>
        /// <remarks>
        /// The <c>Spells</c> column on the <c>Monsters</c> table is empty for all 5,134 of them —
        /// the importer never filled it in — so a catalogue that trusts it reports 100% of the
        /// game's monsters as toothless. Measured against <c>MonsterTemplates</c>, where the server
        /// actually reads them: 4,763 with spells, 371 without.
        /// </remarks>
        [Fact]
        public void The_spellbook_does_not_come_from_the_empty_column()
        {
            if (!Here) return;

            using var monsters = new MonsterCatalogue();
            if (!monsters.Ready) return;

            var all = monsters.All();
            int toothless = all.Count(m => m.Toothless);

            Assert.True(all.Count > 5_000, $"only {all.Count} monsters, which is too few");
            Assert.True(toothless < all.Count / 2,
                        $"{toothless} of {all.Count} monsters report no spells at all, which means " +
                        "the spellbook is being read from the wrong place again");
            Assert.True(toothless > 0,
                        "no monster at all reports an empty spellbook, and 371 of them really do " +
                        "have one — the check has stopped checking");
        }

        /// <summary>
        /// A monster's picture is filed under its <c>gfxId</c>, never its own id.
        /// </summary>
        /// <remarks>
        /// Keyed by monster id, 847 of 5,134 found a picture and every one of those 847 was
        /// somebody else's — the number existed in the set for a different creature, so it did not
        /// fail, it drew the wrong monster. Keyed by gfxId it is 5,130 of 5,134. This is the check
        /// that the two never get confused again.
        /// </remarks>
        [Fact]
        public void A_monsters_picture_is_filed_under_its_drawing()
        {
            if (!Here) return;

            using var monsters = new MonsterCatalogue();
            if (!monsters.Ready) return;

            var all = monsters.All();
            int withGfx = all.Count(m => m.GfxId > 0);
            Assert.True(withGfx > 5_000, $"only {withGfx} of {all.Count} monsters carry a gfxId");

            // Several monsters share one drawing, which is why there are fewer pictures than
            // monsters. If they were one to one the id would be the key and this would be moot.
            int drawings = all.Where(m => m.GfxId > 0).Select(m => m.GfxId).Distinct().Count();
            Assert.True(drawings < withGfx,
                        "every monster has its own drawing, so gfxId is behaving like an id — check " +
                        "which field is being read");

            if (!File.Exists(Paths.MonsterIconsBundle)) return;

            using var icons = new MonsterIcons();

            // Has, not Of: this is about the key, and decoding needs Avalonia's render backend,
            // which does not exist in a test host. The decoding itself is covered by the Studio's
            // own --selftest, which runs inside a real app.
            int looked = 0;
            int found = 0;
            foreach (var monster in all.Where(m => m.GfxId > 0))
            {
                looked++;
                if (icons.Has(monster.GfxId)) found++;
            }

            Assert.True(found >= looked * 9 / 10,
                        $"only {found} of {looked} monsters found their drawing. Keyed by monster " +
                        $"id this was 847 of 5,134, and every one of those was the wrong creature. " +
                        $"pictures in the bundle={icons.Count} {icons.Trouble}");

            Assert.False(icons.Has(0));
            Assert.Null(icons.Of(0));
        }

        [Fact]
        public void A_monster_brings_the_spells_the_server_would_give_it()
        {
            if (!Here) return;

            using var spells = new SpellCatalogue();
            if (!spells.Ready) return;

            // Monster 31 declares spells 10198 and 10199 in its template, at grade 1.
            var known = spells.Of(31);

            Assert.Contains(known, s => s.SpellId == 10198);
            Assert.Contains(known, s => s.SpellId == 10199);
            Assert.All(known, s => Assert.True(s.Grade >= 1, $"grade {s.Grade} is not a grade"));
        }

        [Fact]
        public void A_spell_level_brings_its_effects()
        {
            if (!Here) return;

            using var spells = new SpellCatalogue();
            if (!spells.Ready) return;

            var level = spells.Level(201, 1);
            if (level == null) return;

            Assert.True(level.Effects.Count > 0, "spell 201 grade 1 came back with no effects");
            Assert.All(level.Effects, e => Assert.True(e.EffectId > 0));
        }

        /// <summary>
        /// The area a spell covers is worked out by the fight engine's own code, not by a copy of
        /// it living in the editor. This is the check that the copy never appears.
        /// </summary>
        [Fact]
        public void The_area_is_the_engines_own()
        {
            // Cell 287 is row 20, column 7 of a 14 by 40 grid: well inside it. The first version
            // of this used 280, which is column 0 and therefore against the left edge, so half the
            // circle fell off the map and the test failed for a reason that had nothing to do with
            // the code it was testing.
            const int inside = 287;

            var circle = Zone.Casillas(Zone.Circulo, 1, inside - 1, inside);
            Assert.Contains(inside, circle);
            Assert.True(circle.Count >= 5,
                        $"a circle of 1 around an interior cell covered {circle.Count} cells");

            var wider = Zone.Casillas(Zone.Circulo, 2, inside - 1, inside);
            Assert.True(wider.Count > circle.Count,
                        "a circle of 2 covered no more than a circle of 1");

            // A point is one cell, whatever size is asked for.
            var point = Zone.Casillas(Zone.Punto, 3, inside - 1, inside);
            Assert.Equal(new[] { inside }, point);
        }

        /// <summary>
        /// A range of zero to zero means the spell can only land on the caster's own cell. 1,555
        /// spells are in that state and it is why they were never being cast.
        /// </summary>
        [Fact]
        public void A_spell_that_can_only_hit_its_own_cell_says_so()
        {
            if (!Here) return;

            using var spells = new SpellCatalogue();
            if (!spells.Ready) return;

            var all = spells.All();
            int onSelf = 0;
            foreach (var spell in all.Take(400))
            {
                var level = spells.Level(spell.Id, spell.Grades.Count > 0 ? spell.Grades[0] : 1);
                if (level is { OnSelfOnly: true }) onSelf++;
            }

            // Not a fixed number — the point is that the flag fires at all rather than being
            // permanently false, which is how a warning stops being a warning.
            Assert.True(onSelf > 0, "not one spell in 400 reported a 0-0 range, which cannot be right");
        }
    }
}
