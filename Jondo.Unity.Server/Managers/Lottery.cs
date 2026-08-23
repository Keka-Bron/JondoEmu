using System;
using System.Collections.Generic;
using System.Text;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// La lotería del merkasako: la máquina que hay al lado del cofre, con el dibujo 51031, que solo
    /// existe dentro del merkasako y en un mapa más de todo el mundo.
    ///
    /// Aquí no tiene límite de tiradas, y lo que suelta no es basura: coge una pieza de equipo real
    /// del catálogo del juego y la saca con los efectos exagerados. Un anillo con +3 PA y +3 PM, una
    /// capa con 500 de un elemento, ese tipo de cosas. Los efectos son los de verdad —111 es PA,
    /// 128 PM, 118 fuerza y así— con valores que ningún objeto del juego lleva.
    ///
    /// El objeto que sale es NUESTRO: se le da un uid del rango alto para que no choque con nada de
    /// la base de datos, y se escribe en el inventario como cualquier otro.
    /// </summary>
    public static class Lottery
    {
        /// <summary>El dibujo de la máquina.</summary>
        public const int Gfx = 51031;

        /// <summary>
        /// El tipo con el que se declara, y la habilidad que ofrece. Los dos salen de las capturas
        /// reales de usar la máquina, no de suponer:
        ///
        ///   cliente  iwo { f1: uid de habilidad, f2: 516925 }
        ///   servidor iwn { f1: 1, f2: 516925, f4: 184, f5: quién }
        ///
        /// La habilidad es la 184. Y el tipo es -1: cruzando todos los jss de las capturas, los
        /// elementos que ofrecen la 184 salen siempre con el tipo a -1, 198 veces entre todos.
        /// Poniéndole el 85 el cliente la llamaba "Cofre" y ofrecía "Abrir", que es lo que hay al
        /// lado, no ella.
        /// </summary>
        public const int Type = -1;

        public const int Skill = 184;

        /// <summary>Desde dónde se numeran los objetos que salen, para no pisar los de nadie.</summary>
        private const long FirstUid = 950000000L;

        private static readonly Random _rand = new Random();

        /// <summary>Un premio: qué efecto y entre qué valores, todos por encima de lo que existe.</summary>
        private readonly record struct Prize(int Effect, int Min, int Max);

        /// <summary>
        /// Los efectos gordos, con sus identificadores de verdad. Los dos primeros son los que hacen
        /// que un objeto sea impensable: PA y PM no suben de +1 en el juego real.
        /// </summary>
        private static readonly Prize[] Exotic =
        {
            new Prize(111, 3, 3),      // PA
            new Prize(128, 3, 3),      // PM
            new Prize(158, 200, 400),  // poder
            new Prize(138, 300, 600),  // potencia
            new Prize(115, 50, 100),   // % crítico
            new Prize(182, 5, 8),      // invocaciones
        };

        /// <summary>Las cinco características, que salen a lo bestia.</summary>
        private static readonly Prize[] Elemental =
        {
            new Prize(118, 400, 700),   // fuerza
            new Prize(123, 400, 700),   // suerte
            new Prize(126, 400, 700),   // inteligencia
            new Prize(119, 400, 700),   // agilidad
            new Prize(124, 200, 400),   // sabiduría
            new Prize(125, 1000, 2500), // vitalidad
        };

        /// <summary>Los huecos de equipo de los que se saca la pieza: anillos, capa, sombrero, cinturón, botas, amuleto.</summary>
        private static readonly int[] WearableTypes = { 1, 9, 10, 11, 16, 17 };

        /// <summary>
        /// Quién firma lo que sale. Un objeto exomagueado lleva el nombre del forjamago, y el efecto
        /// que lo pinta es el 988: "Fabricado por: #4", donde el #4 es esta cadena.
        /// </summary>
        public const string Forgemage = "#LOTTERY#";

        /// <summary>El efecto que lleva ese nombre.</summary>
        private const int SignatureEffect = 988;

        public static Interactives.Element Of(long mapId)
            => Merkasako.IsHavenBag(mapId) ? Interactives.ElementByGfx(mapId, Gfx) : default;

        public static bool Is(long mapId, int elementId)
        {
            var machine = Of(mapId);
            return machine.Id != 0 && machine.Id == elementId;
        }

        /// <summary>
        /// Una tirada. Devuelve el objeto ya escrito en la base de datos y en el inventario, o null
        /// si no se ha podido.
        /// </summary>
        public static HavenBagStore.StoredItem? Draw(long characterId)
        {
            int gid = PickWearable();
            if (gid == 0) return null;

            var effects = new List<int[]>();

            // Uno o dos de los imposibles, y dos o tres características a lo grande.
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
            // [efecto, valor, dado, cara]: sin dados, que es lo que lleva un bonus fijo.
            return new[] { prize.Effect, value, 0, 0 };
        }

        /// <summary>
        /// Los efectos como los guarda la base de datos, y al final la firma.
        ///
        ///   [[118,650,0,0], ..., [988,0,0,0,"#LOTTERY#"]]
        ///
        /// El quinto elemento de la firma es la cadena: es lo que distingue a un efecto de texto de
        /// uno de número, y lo que hace que el objeto se vea exomagueado y no recién salido de un
        /// taller anónimo.
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

        /// <summary>Una pieza de equipo cualquiera del catálogo, para colgarle los efectos.</summary>
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

        /// <summary>El uid del premio. Lo reparte DatabaseManager, uno para todo el servidor.</summary>
        private static long NextUid() => DatabaseManager.NextItemUid();
    }
}
