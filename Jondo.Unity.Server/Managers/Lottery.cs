using System;
using System.Collections.Generic;
using System.Text;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The haven bag lottery: the machine next to the chest, with drawing 51031, which only
    /// exists inside the haven bag and on one more map in the whole world.
    ///
    /// Here it has no limit on rolls, and what it drops is not junk: it takes a real piece of equipment
    /// from the game's catalogue and brings it out with exaggerated effects. A ring with +3 AP and +3 MP, a
    /// cape with 500 of an element, that sort of thing. The effects are the real ones —111 is AP,
    /// 128 MP, 118 strength and so on— with values no item of the game carries.
    ///
    /// The item that comes out is OURS: it is given a uid from the high range so it does not clash with anything in
    /// the database, and it is written to the inventory like any other.
    /// </summary>
    public static class Lottery
    {
        /// <summary>The machine's drawing.</summary>
        public const int Gfx = 51031;

        /// <summary>
        /// The type it is declared with, and the skill it offers. Both come from the real captures
        /// of using the machine, not from guessing:
        ///
        ///   client   iwo { f1: skill uid, f2: 516925 }
        ///   server   iwn { f1: 1, f2: 516925, f4: 184, f5: who }
        ///
        /// The skill is 184. And the type is -1: crossing all the jss of the captures, the
        /// elements offering 184 always come out with the type at -1, 198 times among them all.
        /// Giving it 85, the client called it "Cofre" and offered "Abrir", which is what is next
        /// to it, not it.
        /// </summary>
        public const int Type = -1;

        public const int Skill = 184;

        /// <summary>Where the items that come out are numbered from, so as not to step on anyone's.</summary>
        private const long FirstUid = 950000000L;

        private static readonly Random _rand = new Random();

        /// <summary>A prize: which effect and between which values, all above what exists.</summary>
        private readonly record struct Prize(int Effect, int Min, int Max);

        /// <summary>
        /// The big effects, with their real identifiers. The first two are the ones that make
        /// an item unthinkable: AP and MP do not go above +1 in the real game.
        /// </summary>
        private static readonly Prize[] Exotic =
        {
            new Prize(111, 3, 3),      // PA
            new Prize(128, 3, 3),      // PM
            new Prize(158, 200, 400),  // poder
            new Prize(138, 300, 600),  // potencia
            new Prize(115, 50, 100),   // % critical
            new Prize(182, 5, 8),      // invocaciones
        };

        /// <summary>The five characteristics, which come out over the top.</summary>
        private static readonly Prize[] Elemental =
        {
            new Prize(118, 400, 700),   // fuerza
            new Prize(123, 400, 700),   // suerte
            new Prize(126, 400, 700),   // inteligencia
            new Prize(119, 400, 700),   // agilidad
            new Prize(124, 200, 400),   // wisdom
            new Prize(125, 1000, 2500), // vitalidad
        };

        /// <summary>The equipment slots the piece is taken from: rings, cape, hat, belt, boots, amulet.</summary>
        private static readonly int[] WearableTypes = { 1, 9, 10, 11, 16, 17 };

        /// <summary>
        /// Who signs what comes out. An exomaged item carries the smithmage's name, and the effect
        /// that draws it is 988: "Fabricado por: #4", where the #4 is this string.
        /// </summary>
        public const string Forgemage = "#LOTTERY#";

        /// <summary>The effect that carries that name.</summary>
        private const int SignatureEffect = 988;

        public static Interactives.Element Of(long mapId)
            => Merkasako.IsHavenBag(mapId) ? Interactives.ElementByGfx(mapId, Gfx) : default;

        public static bool Is(long mapId, int elementId)
        {
            var machine = Of(mapId);
            return machine.Id != 0 && machine.Id == elementId;
        }

        /// <summary>
        /// One roll. Returns the item already written to the database and to the inventory, or null
        /// if it could not be done.
        /// </summary>
        public static HavenBagStore.StoredItem? Draw(long characterId)
        {
            int gid = PickWearable();
            if (gid == 0) return null;

            var effects = new List<int[]>();

            // One or two of the impossible ones, and two or three characteristics in a big way.
            var exotic = new List<Prize>(Exotic);
            int howManyExotic = _rand.Next(1, 3);
            for (int i = 0; i < howManyExotic && exotic.Count > 0; i++)
            {
                int pick = _rand.Next(exotic.Count);
                effects.Add(Roll(exotic[pick]));
                exotic.RemoveAt(pick);
            }

            var elemental = new List<Prize>(Elemental);
            int howManyStats = _rand.Next(2, 4);
            for (int i = 0; i < howManyStats && elemental.Count > 0; i++)
            {
                int pick = _rand.Next(elemental.Count);
                effects.Add(Roll(elemental[pick]));
                elemental.RemoveAt(pick);
            }

            long uid = NextUid();
            string json = Serialise(effects);

            if (!DatabaseManager.InsertCharacterItem(uid, characterId, gid, 1, Equipment.Bag, json))
                return null;

            Equipment.Add(uid, gid, 1, Equipment.Bag, json);

            Console.WriteLine($"[Lotería] Sale el objeto {gid} (uid {uid}) con {effects.Count} efectos.");

            return new HavenBagStore.StoredItem
            {
                Uid = uid,
                Gid = gid,
                Quantity = 1,
                Effects = json,
            };
        }

        private static int[] Roll(Prize prize)
        {
            int value = prize.Min >= prize.Max ? prize.Min : _rand.Next(prize.Min, prize.Max + 1);
            // [effect, value, die, side]: without dice, which is what a fixed bonus carries.
            return new[] { prize.Effect, value, 0, 0 };
        }

        /// <summary>
        /// The effects as the database stores them, and the signature at the end.
        ///
        ///   [[118,650,0,0], ..., [988,0,0,0,"#LOTTERY#"]]
        ///
        /// The signature's fifth element is the string: it is what tells a text effect apart from
        /// a number one, and what makes the item look exomaged and not freshly out of an
        /// anonymous workshop.
        /// </summary>
        private static string Serialise(List<int[]> effects)
        {
            var sb = new StringBuilder("[");
            foreach (var e in effects)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('[').Append(e[0]).Append(',').Append(e[1]).Append(",0,0]");
            }
            if (sb.Length > 1) sb.Append(',');
            sb.Append('[').Append(SignatureEffect).Append(",0,0,0,\"").Append(Forgemage).Append("\"]");
            return sb.Append(']').ToString();
        }

        /// <summary>Any piece of equipment from the catalogue, to hang the effects on.</summary>
        private static int PickWearable()
        {
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT Id FROM ItemTemplates WHERE Type IN (" +
                    string.Join(",", WearableTypes) + ") ORDER BY RANDOM() LIMIT 1;";
                if (command.ExecuteScalar() is long gid) return (int)gid;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lotería] No se pudo elegir objeto: {ex.Message}");
            }
            return 0;
        }

        /// <summary>The prize's uid. DatabaseManager hands it out, one for the whole server.</summary>
        private static long NextUid() => DatabaseManager.NextItemUid();
    }
}
