using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los Sueños Infinitos: el mapa de un sueño y por dónde va cada jugador.
    /// </summary>
    /// <remarks>
    /// Es la versión del POZO, la refundición que convirtió los Sueños en un roguelite: eliges
    /// dificultad, te dan un mapa de salas con bifurcaciones, y en cada sala hay un grupo y una
    /// modificación. Las anteriores funcionaban de otra manera y no valen de referencia.
    ///
    /// Todo lo de aquí sale de las trece capturas de <c>Sueños Infinitos/</c>. El mensaje que abre
    /// la ventana, el iyj, trae DOS listas y son la clave del asunto:
    ///
    /// <code>
    ///   las salas    f1 = "0".."10"
    ///                  f6   la fila del grafo
    ///                  f4   what the room gives: a Reward, whose f10 is its InfiniteDreamRewardData row
    ///   el grafo     0 -> 1,2   1 -> 3,4   2 -> 4,5   3 -> 6,7
    ///                4 -> 7,8   5 -> 8,9   6..9 -> 10
    /// </code>
    ///
    /// Que dibuja un rombo de once salas en cinco filas —1, 2, 3, 4, 1— y no un árbol: a la sala 4
    /// se llega desde la 1 y desde la 2.
    ///
    ///   MEDIDO en la captura de Paradoja I, sala por sala: la fila que dice el f6 de cada una
    ///   coincide exactamente con la que le toca en el grafo. The f9 of those rooms -- 14931,
    ///   14812, 15026, 14798, 14797 -- were read here as MapMobs groups, and they are the
    ///   rewards' icons: 14798 is +20% vitality in every room it appears in, while the bestiary
    ///   lists different monsters each time. The group a room is fought against is chosen here
    ///   from MapMobs and does not travel in the graph.
    ///
    /// Rooms are of five kinds, the f5 of a room: 0 the entrance, 1 a fight, 2 a dream favour,
    /// 3 a fountain, 4 the Fin du rêve -- READ in the client's door tooltip,
    /// InfiniteDreamGateTooltipBuilder.SetupViewWithContent @0x182D80E58, which names them
    /// startRoom, fightRoom, dreamFavor, dreamFountain and bossRoom in that order.
    ///
    /// La dificultad va de 1 a 10 y la numeración también está medida, comparando el ixf de nueve
    /// capturas contra el nombre que el jugador eligió en cada una:
    ///
    /// <code>
    ///   1..3   Sueño I, II, III            8..10  Pesadilla I, II, III
    ///   4..7   Paradoja I, II, III, IV
    /// </code>
    /// </remarks>
    public static class Dreams
    {
        /// <summary>Cuántas salas hay en cada fila del rombo. Medido sobre el grafo del iyj.</summary>
        private static readonly int[] Filas = { 1, 3, 3, 3, 1 };

        /// <summary>Lo que puede medir una fila de en medio. Medido de 2 a 4 en nueve capturas.</summary>
        private const int MinimoPorFila = 2;
        private const int MaximoPorFila = 4;

        /// <summary>Y lo que suman las tres juntas: de 7 a 9, o sea sueños de 9, 10 u 11 salas.</summary>
        private const int MinimoDeEnMedio = 7;
        private const int MaximoDeEnMedio = 9;

        /// <summary>
        /// The bonus of each difficulty to experience and loot, in percent: the f22 of the izg,
        /// and the f8 it starts from.
        /// </summary>
        /// <remarks>
        /// Measured in the f22 of the izg of the captures, one difficulty, one value:
        ///
        ///   1: 50   2: 75   3: 100   4: 120   5: 140
        ///   6: 160  7: 190  8: 220   9: 250  10: 300
        ///
        /// And the client's InfiniteDreamIntensitiesDataRoot says the same, as a dropBonus of 0.5
        /// to 3.0 (see <see cref="DreamData"/>).
        ///
        /// These were read for a time as the dream points a dream starts with, and they are not:
        /// the client paints f8 and f22 as the two percentages under the dream's name -- "220%
        /// 220%" in a Pesadilla I -- and the dream points are the f11. What gave it away is the
        /// Rey Gob: "multiply the dream points by 1.5" takes f11 from 25 to 38 in the long capture
        /// and leaves f8 where it was.
        ///
        /// f22 never moves. f8 does: it is the loot bonus of the room one fights in -- see
        /// <see cref="LootBonusOf"/>.
        /// </remarks>
        private static readonly int[] BonusByDifficulty =
        {
            0, 50, 75, 100, 120, 140, 160, 190, 220, 250, 300,
        };

        /// <summary>A difficulty's bonus to experience and loot, in percent: the f22 of the izg.</summary>
        public static int BonusOf(int difficulty)
            => difficulty >= 1 && difficulty < BonusByDifficulty.Length
                ? BonusByDifficulty[difficulty]
                : BonusByDifficulty[1];

        /// <summary>
        /// The palier a row is in, from 1: the dofuspourlesnoobs guide's paliers, I rooms 1 to 3,
        /// II 4 to 9, III 10 to 15, IV 16 to 21, V 22 to 26. The fountain of row 4 opens palier II, as the
        /// f12 of 1 the long capture sends while standing in it says, and row 22 is palier V's --
        /// it gives the 10 dream points of palier V in the invitation capture.
        /// </summary>
        public static int PalierOf(int row) => row < 4 ? 1 : row < 10 ? 2 : row < 16 ? 3 : row < 22 ? 4 : 5;

        /// <summary>
        /// The loot bonus of a room, in percent: the f8 of the izg while one stands in it. The
        /// difficulty's bonus, plus a tenth of it for every palier past the first and a
        /// twentieth more in a marked room.
        /// </summary>
        /// <remarks>
        /// INFERRED from the three rooms of the captures where f8 is not the difficulty's own, and
        /// the one example of the guide, which the rule gives all four of:
        ///
        ///   Sueño I, rows 5 and 6 of the long capture (palier II)       50 x 1.10 = 55
        ///   Paradoja I, rows 23 and 24 of the invitation (palier V)    120 x 1.40 = 168
        ///   Pesadilla III, a marked room of row 1 (palier I)           300 x 1.05 = 315
        ///   the guide's example of 238 %, 24 reflections               190 x 1.25 = 237.5
        ///
        /// the last a Paradoja IV in a marked room of palier III. Growing by a tenth a palier
        /// compounded would give 175.7 and not 168. The guide says the same in words: "le bonus
        /// de butin est lié à la difficulté de la salle, plus la salle sera difficile, plus vous
        /// obtiendrez de reflets".
        /// </remarks>
        public static int LootBonusOf(int difficulty, int row, bool marked)
        {
            double factor = 1 + 0.1 * (PalierOf(row) - 1) + (marked ? 0.05 : 0);
            return (int)Math.Round(BonusOf(difficulty) * factor, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The dream reflections (item <see cref="ReflectionItem"/>) a fight pays each of its
        /// winners: ten, times the loot bonus, rounded up.
        /// </summary>
        /// <remarks>
        /// MEASURED. In the izo of the five captures that open the loot table the reflections line
        /// is x5, x16, x17 and x19 at an f8 of 50, 160, 168 and 190; and at the end of the
        /// invitation capture's fight, with f8 at 168, each of the four players gets 17 in the jyg
        /// (frame 5572). The guide: "De base, chaque salle rapporte 10 reflets oniriques qu'il faut
        /// ensuite adapter avec le bonus de butin de la salle ... La valeur est arrondie au
        /// supérieur", 24 at 238 %. Every winner gets them whole: the four of the jyg get 17 each.
        /// </remarks>
        public static int ReflectionsFor(int lootBonus) => (int)Math.Ceiling(10 * Math.Max(0, lootBonus) / 100.0);

        /// <summary>"Reflejo onírico", the dream's reflection: the item 32079 of the jyg.</summary>
        public const int ReflectionItem = 32079;

        /// <summary>
        /// "Retazo de sueño", the guide's "Bribe de rêve": item 32080, what the waves of the Fin du
        /// rêve pay. Its bag, "Bolsa de retazos de sueño", is 34274.
        /// </summary>
        public const int FragmentItem = 32080;

        /// <summary>
        /// The dream fragments a finished dream pays: its intensity's dreamFragments for every
        /// wave of the Fin du rêve that fell. Zero when the dream was not finished.
        /// </summary>
        /// <remarks>
        /// The client's InfiniteDreamIntensitiesDataRoot gives 25, 50, 75, 75, 100, 150, 200, 300,
        /// 500 and 1000 for the ten intensities -- the guide's table of "bribes par vague", number
        /// for number -- and the guide says when: "Les Bribes de rêves s'obtiennent uniquement en
        /// finissant entièrement un songe", each defeated wave adding its share.
        /// </remarks>
        public static int FragmentsFor(int difficulty, int wavesCleared)
            => Math.Max(0, wavesCleared) * (DreamData.IntensityOf(difficulty)?.DreamFragments ?? 0);

        /// <summary>
        /// What a won fight of a dream drops for ONE of its winners: the reflections, whole, and a
        /// roll of every other line of the dream's loot table (<see cref="DreamData.Loot"/>) at
        /// its percent times the room's loot bonus. <paramref name="roll"/> gives a number in
        /// [0, 100); a line drops when it is under the line's chance.
        /// </summary>
        /// <remarks>
        /// The table is the izo's, measured; that its chances are the ones rolled is INFERRED --
        /// the client's loot table window shows them, scaled by the bonus as the reflections are.
        /// Each winner rolls on his own: in the jyg of the invitation capture the four players get
        /// the same 17 reflections and only one of them a Runa astral legendaria (21968), the rune
        /// the table gives 3.36 % at that room's bonus of 168.
        ///
        /// A line drops only when its criterion is met. Wp is the palier and Wi the intensity --
        /// INFERRED: the client has an InfiniteDreamStageCriterion and an
        /// InfiniteDreamIntensityCriterion, whose letters its string obfuscation hides, and the
        /// lines only make sense so: the Wp=1..5 rune lines are the guide's runes palier by palier
        /// (minor and average in I and II, major and astounding in III, ...), and the "Wi&gt;3" of
        /// every rune line keeps them from the three Rêve intensities, the guide's no legends and
        /// no astral runes there and the client's droplegend 0. Anything else in a criterion --
        /// the quests' Qa and Qo, Wv, Sc -- is not answered, and those lines do not drop.
        /// </remarks>
        public static Dictionary<int, int> LootOf(int difficulty, int row, int lootBonus, Func<double> roll)
        {
            var loot = new Dictionary<int, int>();
            void Add(int item, int count)
            {
                if (count <= 0) return;
                loot.TryGetValue(item, out int had);
                loot[item] = had + count;
            }

            int palier = PalierOf(row);
            foreach (var line in DreamData.Loot)
            {
                if (line.Reflections)
                {
                    Add(line.Item, ReflectionsFor(lootBonus));
                    continue;
                }
                if (!Jondo.Unity.World.Content.Criterion.Met(line.Criterion, c => AnswerOf(c, palier, difficulty)))
                    continue;
                double chance = line.Scaled ? line.Percent * lootBonus / 100.0 : line.Percent;
                if (roll() < Math.Min(100.0, chance)) Add(line.Item, 1);
            }
            return loot;
        }

        /// <summary>The loot table as the client's window shows it at a bonus: izo's lines.</summary>
        public static List<(string Criterion, int Item, int Quantity, double Percent)> LootTableAt(int lootBonus)
        {
            var lines = new List<(string, int, int, double)>();
            foreach (var line in DreamData.Loot)
            {
                if (line.Reflections) lines.Add(("", line.Item, ReflectionsFor(lootBonus), 100.0));
                else lines.Add((line.Criterion, line.Item, 1,
                                Math.Min(100.0, line.Scaled ? line.Percent * lootBonus / 100.0 : line.Percent)));
            }
            return lines;
        }

        /// <summary>The two letters of a dream's criteria this server answers: Wp, the palier, and Wi, the intensity.</summary>
        private static Jondo.Unity.World.Content.Answer AnswerOf(Jondo.Unity.World.Content.Condition condition,
                                                                 int palier, int intensity)
            => condition.Code switch
            {
                "Wp" => Jondo.Unity.World.Content.Criterion.Compare(condition.Operator, palier, condition.Value),
                "Wi" => Jondo.Unity.World.Content.Criterion.Compare(condition.Operator, intensity, condition.Value),
                _ => Jondo.Unity.World.Content.Answer.Unknown,
            };

        /// <summary>
        /// The dream points a dream starts with: the f11 of the izg at the entrance. Ten in the
        /// five captured dreams of Sueño I to III, five in the four of Paradoja, none in the three
        /// of Pesadilla, where f11 is left out.
        /// </summary>
        public static int StartingDreamPoints(int difficulty)
            => difficulty <= LastSueno ? 10 : difficulty <= LastParadoja ? 5 : 0;

        /// <summary>
        /// The Draconiros arenas a dream starts with, the retries: the f17. One in the Sueño
        /// dreams -- the five izg of the captures that start one carry it -- and none in
        /// Paradoja or Pesadilla, whose izg never do. Dying spends it: it is gone from the izg
        /// that follows a death in "Sueño III-pelear-morir", and from the Sueño I that the player
        /// of "Sueño II-descartar" had going.
        /// </summary>
        public static int StartingArenas(int difficulty) => difficulty <= LastSueno ? 1 : 0;

        /// <summary>The last Sueño (3) and the last Paradoja (7) of the ladder.</summary>
        private const int LastSueno = 3;
        private const int LastParadoja = 7;

        /// <summary>La dificultad más alta, Pesadilla III.</summary>
        public const int MaximaDificultad = 10;

        /// <summary>El mapa del Plano Astral, que es donde está el pozo.</summary>
        /// <remarks>
        /// Medido: es a donde lleva el jru que sigue al iyc del botón del menú, y en nuestra propia
        /// base es la subárea 938, «Dominios de Draconiros».
        /// </remarks>
        public const long MapaDelPozo = 238551040;

        /// <summary>El pozo, que en los datos del cliente es un elemento más de ese mapa.</summary>
        /// <remarks>
        /// El 539616, gráfico 90166, casilla 370. Está en el mapa desde siempre; lo que faltaba era
        /// declararle una acción, porque sin ella el cliente no lo deja pulsar y queda de adorno.
        /// </remarks>
        public const int ElementoDelPozo = 539616;

        /// <summary>La habilidad con la que se usa el pozo.</summary>
        /// <remarks>
        /// El 20743 del iwo «0887a20110e0f720» NO es esto. Es el uid de instancia, y confundir uno
        /// con otro es lo que dejó el pozo sin pulsar: anunciábamos la habilidad 20743, que el
        /// cliente no conoce, y un elemento cuya habilidad no existe no se puede clicar y no da un
        /// solo error. El f11 del jss real del mapa lo dice campo a campo:
        ///
        ///   f11 { f1: 1, f4 { f1: 20744, f2: 360 }, f4 { f1: 20743, f2: 184 }, f5: 539616, f6: -1 }
        ///
        /// El f4.f1 es el uid —lo que el cliente devuelve en el iwo— y el f4.f2 la habilidad. La
        /// 184 es la misma con la que ya se entra en una casa y se usa la lotería, así que el
        /// cliente la conoce de sobra. El uid nuestro lo pone Interactives.SkillInstanceOf y el
        /// cliente lo devuelve tal cual, así que no hace falta copiar el suyo.
        /// </remarks>
        public const int HabilidadDelPozo = 184;

        /// <summary>El tipo de interactivo del pozo y de las arcadas: el f6 del f11, medido en -1.</summary>
        public const int TipoDelPozo = -1;

        /// <summary>La segunda acción del pozo, la del f4 { 20744, 360 }.</summary>
        /// <remarks>
        /// El pozo ofrece DOS cosas, no una: en las 22 tramas jss del mapa 238551040 que hay en las
        /// trece capturas —las 22 idénticas— van dos f4, el de la habilidad 184 y éste. Declarar
        /// sólo uno deja al jugador con media carta.
        ///
        /// Qué contesta el servidor real a ésta no se ha medido: en las capturas nadie la pulsa,
        /// las once veces que se usa el pozo van por la 184. Aquí abre la misma ventana, que es lo
        /// único que sabemos hacer con el pozo, y queda dicho que es una suposición.
        /// </remarks>
        public const int SegundaHabilidadDelPozo = 360;

        /// <summary>El mapa de la sala de entrada de todo sueño.</summary>
        /// <remarks>
        /// Medido en las diez capturas que empiezan un sueño: el jru que sigue al primer izg lleva
        /// siempre aquí, sin excepción. Las salas de pelea vienen después y ésas sí cambian.
        /// </remarks>
        public const long MapaDeEntrada = 237897728;

        /// <summary>La subárea donde viven las salas: 484 mapas hechos para esto.</summary>
        /// <remarks>
        /// Los nueve mapas de sala que aparecen en las capturas —237764608, 237765632, 237766656,
        /// 237767680, 237768704, 237765684, 237765686, 237777980 y 237774854— están todos aquí, y
        /// también la entrada. Cada uno lleva EXACTAMENTE tres elementos interactivos con el
        /// gráfico 90166, que son las tres puertas a la fila de abajo.
        ///
        /// Esto es lo que faltaba para que entrar en un sueño no fuese un viaje a Frigost: se
        /// estaba mandando al jugador al mapa del grupo de monstruos, que es un mapa del mundo.
        /// </remarks>
        public const int SubareaDeLasSalas = 904;

        /// <summary>Cuántas puertas tiene una sala. Tres en los 100 mapas que las traen.</summary>
        public const int PuertasPorSala = 3;

        /// <summary>La sala de Draconiros, al otro lado de cualquiera de las cuatro arcadas.</summary>
        /// <remarks>
        /// No es vecina de la del pozo en la rejilla —una está en (0,0) y la otra en (1,-1)— así que
        /// no se llega andando: se llega pulsando una arcada. Sin declararlas, Draconiros está bien
        /// colocado y es inalcanzable, que para el jugador es lo mismo que no estar.
        /// </remarks>
        public const long MapaDeDraconiros = 238553348;

        /// <summary>Las dos clases de sala del f3: 5 en 63 salas medidas, 15 en 8.</summary>
        private const int ClaseNormal = 5;
        private const int ClaseSenalada = 15;

        /// <summary>From row 22 on the rooms give 10: every one of band V in the invitation capture.</summary>
        private const int ClaseDeLaVenta = 10;
        private const int FilaDeLaVenta = 22;

        /// <summary>Cuántos sueños se le han ofrecido a cada personaje, para el f13.</summary>
        private static readonly Dictionary<long, int> _cuenta = new Dictionary<long, int>();

        /// <summary>
        /// Un potenciador de los Sueños: lo que una sala regala al entrar.
        /// </summary>
        /// <remarks>
        /// Censados los 196 f15 de las quince capturas, y sólo hay dos formas:
        ///
        ///   f15 { f1 { f4: el valor,        f11: el efecto }, f2: 1 }   132 veces
        ///   f15 { f1 { f6 { f1: cuántos },  f11: el efecto }, f2: 1 }    64 veces
        ///
        /// El f11 es un id del catálogo de efectos del propio cliente —2844 es «% vitalidad», 111
        /// «PA», 128 «PM», 117 «alcance»—, así que no hay nada que inventar: el cliente sabe
        /// escribir la línea él solo. La segunda forma es la de los efectos cuyo texto nombra un
        /// hechizo, como el 281 «+#3 de alcance máximo».
        /// </remarks>
        public sealed class Bono
        {
            public Bono(int efecto, int valor, bool anidado = false)
            {
                Efecto = efecto;
                Valor = valor;
                Anidado = anidado;
            }

            /// <summary>El id del catálogo de efectos: el f11.</summary>
            public int Efecto { get; }

            /// <summary>Cuánto da.</summary>
            public int Valor { get; }

            /// <summary>
            /// Si el valor viaja dentro del f6 en vez de en el f4.
            /// </summary>
            /// <remarks>
            /// Es sólo dónde va el número, no a qué se aplica. El «+alcance máximo» del 281 sube
            /// el alcance de TODOS los hechizos, no el de uno; leerlo como una referencia a un
            /// hechizo concreto sería equivocarse con el mismo campo por segunda vez.
            /// </remarks>
            public bool Anidado { get; }
        }

        /// <summary>
        /// A reward of the dreams: what a room gives on entering, or what the fountain sells. The
        /// iww of the wire -- the f4 of a room in the graph, an f6 of the izg at a fountain.
        /// </summary>
        /// <remarks>
        /// Measured field by field in the captures, zeros written:
        ///
        ///   { f1: astral storms, f2: 0, f3 (repeated) { a bonus }, f4: dreamer levels,
        ///     f5: dream points, f7: rarity, f8: price, f9: icon, f10: reward id, f11: 0 }
        ///
        /// f10 is the row of the client's own InfiniteDreamRewardsDataRoot: the client looks the
        /// reward up by it (eft::xhm @0x181665F49 reads it for GetInfiniteDreamRewardsById) and
        /// buys by it -- clicking "Psst Psst" sends iym { f1: 149 }. And the rows agree: 125 is
        /// "Fogosidad", one AP; 119 "Vitalidad", % vitality; 118 "Tormenta astral", an astral
        /// storm and five dream points.
        ///
        /// f9 is the reward's icon: the shop line hands it to an AddressableEntry for its
        /// tx_itemicon (InfiniteDreamShopUi.BindRewardLine @0x182887184). That is why 14798 is on
        /// both vitality rewards, 119 and 65, and 14808 on the two spell rewards, 140 and 157.
        /// It was read for a time as a MapMobs group, and then as the reward row.
        ///
        /// f1, f4 and f5 are what the reward's actions add up to, read against the rows: 118 --
        /// one storm action and five dream-point actions -- is f1 1 and f5 5, and entering its
        /// room takes the storms from 1 to 2 in three captures; 4, fifteen dream-point actions, is
        /// f5 15; 76, thirty, is f5 30; 154 "50 niveles de soñador" is f4 50, and the izg's f14
        /// is 50 once its room is behind. f2 is INFERRED to be the Draconiros sand the same way:
        /// the one reward-shaped count left, never above zero in the captures. f7 is 1, 2 or 3
        /// where it is written, the rarity classes the shop paints (common, rare, epic,
        /// legendary).
        /// </remarks>
        public sealed class Reward
        {
            /// <summary>The icon: the f9.</summary>
            public int Id { get; init; }

            /// <summary>
            /// The row of InfiniteDreamRewardsDataRoot: the f10, and what the client buys it by.
            /// </summary>
            public int Tag { get; init; }

            /// <summary>The f7: left out when zero.</summary>
            public int Rarity { get; init; }

            /// <summary>The astral storms it gives: the f1.</summary>
            public int Storms { get; init; }

            /// <summary>The Draconiros sand it gives: the f2, INFERRED.</summary>
            public int Sand { get; init; }

            /// <summary>The dreamer levels it gives: the f4.</summary>
            public int Levels { get; init; }

            /// <summary>The dream points it gives: the f5.</summary>
            public int Points { get; init; }

            /// <summary>What it costs in dream points at the fountain: the f8. Nothing in a room.</summary>
            public int Price { get; init; }

            /// <summary>The bonuses it gives: the f3, one or two.</summary>
            public IReadOnlyList<Bono> Bonuses { get; init; } = Array.Empty<Bono>();
        }

        /// <summary>
        /// What rooms give: the twelve rewards the rooms of the captures' graphs offer, each with
        /// the icon, the row and the rarity it always carries there.
        /// </summary>
        /// <remarks>
        /// Counted over every room of every graph of the fifteen captures -- 14798 in 270 rooms,
        /// 14804 in 194, 14931 in 152 and so on down to 14808 in 16. The last three -- 15 and 30
        /// dream points, 50 dreamer levels -- used to be left out because what they give was not
        /// known; the client's reward rows say it (see <see cref="Reward"/>), and the guide lists
        /// them among its special rooms: 15 or 30 dream points, the 30 rare, or a storm and 5.
        /// </remarks>
        internal static readonly Reward[] RoomRewards =
        {
            new Reward { Id = 14797, Tag = 125, Rarity = 2, Bonuses = new[] { new Bono(111, 1) } },     // AP
            new Reward { Id = 14798, Tag = 119, Bonuses = new[] { new Bono(2844, 20) } },               // % vitality
            new Reward { Id = 14804, Tag = 133, Bonuses = new[] { new Bono(4041, 5) } },                // % damage
            new Reward { Id = 14805, Tag = 127, Rarity = 1, Bonuses = new[] { new Bono(117, 2) } },     // range
            new Reward { Id = 14808, Tag = 140, Rarity = 3, Bonuses = new[] { new Bono(286, 1, true) } },
            new Reward { Id = 14808, Tag = 157, Rarity = 2, Bonuses = new[] { new Bono(291, 1, true) } },
            new Reward { Id = 14850, Tag = 111, Rarity = 2, Bonuses = new[] { new Bono(281, 1, true) } }, // max range
            new Reward { Id = 15026, Tag = 126, Rarity = 2, Bonuses = new[] { new Bono(128, 1) } },     // MP
            new Reward { Id = 14931, Tag = 118, Rarity = 1, Storms = 1, Points = 5 },                    // a storm
            new Reward { Id = 14812, Tag = 4, Points = 15 },                                             // "Puntos de sueño"
            new Reward { Id = 14811, Tag = 76, Points = 30 },                                            // "Bolsa de puntos de sueño"
            new Reward { Id = 14895, Tag = 154, Rarity = 1, Levels = 50 },                              // "50 niveles de soñador"
        };

        /// <summary>
        /// The dream favour's third choice, always the last: "L'un des 3 choix (le dernier) est
        /// forcément une petite bourse qui donne 10 points de rêve", the guide says. It is the
        /// client's reward 143, "Bolsa de puntos de sueño", ten dream-point actions.
        /// </summary>
        /// <remarks>
        /// Its icon is INFERRED: 14811, the icon of reward 76, the thirty-point "Bolsa de puntos
        /// de sueño" of the same name. No capture shows 143 on the wire.
        /// </remarks>
        internal static readonly Reward FavorPurse = new Reward { Id = 14811, Tag = 143, Points = 10 };

        /// <summary>
        /// What the dream favour draws its two free bonuses from: the bonuses of the captures whose
        /// values are measured and that a fight here knows how to apply (<see cref="ApplyTo"/>) --
        /// the rooms' eight and the shop's critical hits. The guide: "un choix entre 3 bonus
        /// gratuits". Which rewards the real favour draws from is not captured.
        /// </summary>
        internal static readonly Reward[] FavorBonuses =
            RoomRewards.Where(r => r.Bonuses.Count > 0)
                       .Append(new Reward { Id = 14813, Tag = 124, Rarity = 2, Bonuses = new[] { new Bono(115, 25) } })
                       .ToArray();

        /// <summary>
        /// What the fountain sells: the five offers of the one fountain of the captures, in their
        /// order, 15 dream points each. The long capture's room 9, twice with the same five.
        /// </summary>
        internal static readonly Reward[] ShopOffers =
        {
            new Reward { Id = 14798, Tag = 65, Price = 15, Bonuses = new[] { new Bono(2971, 20), new Bono(2844, 40) } },
            new Reward { Id = 15389, Tag = 149, Rarity = 2, Price = 15, Bonuses = new[] { new Bono(3405, 85231) } },
            new Reward { Id = 14799, Tag = 103, Price = 15, Bonuses = new[] { new Bono(2850, 100), new Bono(2852, 100) } },
            new Reward { Id = 15382, Tag = 89, Rarity = 2, Price = 15, Bonuses = new[] { new Bono(3405, 83685) } },
            new Reward { Id = 14813, Tag = 124, Rarity = 2, Price = 15, Bonuses = new[] { new Bono(115, 25) } },  // % critical
        };

        public sealed class Sala
        {
            /// <summary>Su número, que en el cable viaja como CADENA: «0», «1»…</summary>
            public int Id { get; init; }

            /// <summary>La fila del rombo, de 0 a 4. Es el f6 del iyj.</summary>
            public int Fila { get; init; }

            /// <summary>A qué salas se puede ir desde aquí.</summary>
            [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
            public List<int> Salidas { get; } = new List<int>();

            /// <summary>La fila de MapMobs que se pelea aquí. Cero en la entrada.</summary>
            public int Grupo { get; set; }

            /// <summary>Los monstruos de ese grupo, con su grado, para plantarlos en la sala.</summary>
            [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
            public List<(int Monstruo, int Grado)> Miembros { get; } = new List<(int, int)>();

            /// <summary>El grupo ya plantado en el mapa de la sala, para poder quitarlo.</summary>
            /// <remarks>Not saved: after a restart nothing is planted, and the room plants anew.</remarks>
            [JsonIgnore]
            public long Plantado { get; set; }

            /// <summary>El mapa del mundo donde vive ese grupo. NO es a donde se va el jugador.</summary>
            /// <remarks>
            /// Se guarda para poder plantar la pelea con los monstruos que le tocan; mandarle a él
            /// allí es lo que le dejaba en mitad de Frigost con el minimapa apagado.
            /// </remarks>
            public long MapaId { get; set; }

            /// <summary>El mapa de la subárea 904 en el que ocurre esta sala.</summary>
            public long MapaDeLaSala { get; set; }

            /// <summary>La casilla donde está plantado el grupo.</summary>
            public int Casilla { get; set; }

            /// <summary>What the room gives on entering. Null at the entrance and at a fountain.</summary>
            public Reward? Reward { get; set; }

            /// <summary>The room's bonus, when its reward is one. Null at the entrance and at a fountain.</summary>
            [JsonIgnore]
            public Bono? Regalo => Reward != null && Reward.Bonuses.Count > 0 ? Reward.Bonuses[0] : null;

            /// <summary>
            /// What a fountain has left to sell, or what a dream favour offers: stocked the first
            /// time the room is entered, a fountain's offers each gone once bought, a favour's all
            /// gone once one is chosen. Null anywhere else.
            /// </summary>
            public List<Reward>? Offers { get; set; }

            /// <summary>Whether the Rey Gob stands in this fountain. See <see cref="ReyGobOneIn"/>.</summary>
            public bool HasReyGob { get; set; }

            /// <summary>Whether the Rey Gob's favor -- the dream points times one and a half -- was taken here.</summary>
            public bool FavorTaken { get; set; }

            /// <summary>
            /// A dream favour, the guide's Faveur Onirique: a room with no fight and one NPC, the
            /// Dispensador de favores, who offers three free rewards. Room kind 2.
            /// </summary>
            /// <remarks>
            /// MEASURED in the invitation capture's five-band graph: rooms "15", "29" and "41", on
            /// rows 7, 12 and 17, each { f5: 2, f6: the row, f7: 0 } and nothing else -- no score,
            /// no dream points, no reward -- and each with a single exit. The player's path runs
            /// through all three.
            /// </remarks>
            public bool EsFavor { get; set; }

            /// <summary>Whether the favour of this room was chosen: until then its doors stay shut.</summary>
            public bool FavorChosen { get; set; }

            /// <summary>El efecto que modifica la sala, y cuánto. Cero: sin modificación.</summary>
            [JsonIgnore] public int Efecto => Regalo?.Efecto ?? 0;
            [JsonIgnore] public int Valor => Regalo?.Valor ?? 0;

            /// <summary>Si ya se ha peleado aquí.</summary>
            public bool Hecha { get; set; }

            /// <summary>Si ya se cobró su potenciador. Se vuelve a entrar al continuar un sueño.</summary>
            public bool Cobrada { get; set; }

            /// <summary>The room's score: its f1 in the graph.</summary>
            /// <remarks>
            /// Measured from 4 to 41 over the rooms of the captures, low in a Sueño and high in a
            /// Pesadilla, with no rule tying it to the row. It is not the dream points: those are
            /// the f3, which is what the door's tooltip says. Handed out by row here.
            /// </remarks>
            public int Score { get; set; }

            /// <summary>
            /// The dream points the room gives when it is entered: its f3, the "5 Puntos de sueño"
            /// of the door's tooltip. 5 in most rooms, 15 in the marked ones, 10 deep in a dream.
            /// </summary>
            public int DreamPoints { get; set; }

            /// <summary>The band the room was made in, from 1. A fountain closes its band and opens the next.</summary>
            public int Franja { get; set; } = 1;

            /// <summary>Sala señalada. El f7, que vale 1 en 8 de las 89 y siempre con Clase 15.</summary>
            public bool Senalada { get; set; }

            /// <summary>Si esta sala es la Fuente Onírica: la tienda, y siempre la última de su franja.</summary>
            /// <remarks>
            /// The kind, the f5, over the 293 distinct rooms of the captures' graphs: 244 of kind 1
            /// (fights), 24 of kind 3 (fountains, the last row of every band but band IV's), 6 of
            /// kind 2 (the three dream favours of the invitation capture, in two captures) and 2 of
            /// kind 4 (its Fin du rêve). A census of rows 1 to 4 alone, 665 rooms of kinds 1 and 3,
            /// was taken for the whole dream for a time.
            ///
            /// El propio cliente lo dice al pasar el ratón: «Fuente onírica - TIENDA - te permite
            /// intercambiar tus puntos de sueño por bonus». Lo saca de este número.
            /// </remarks>
            public bool EsFuente { get; set; }

            /// <summary>
            /// The room that closes its band and opens the next: the fountains of bands I to III,
            /// and the lone fight room of row 22 that closes band IV -- "56" in the invitation
            /// capture, in the graphs of both bands as the fountains are.
            /// </summary>
            public bool Cierre { get; set; }

            /// <summary>The last room of the dream, row 26: the Fin du rêve, fought in waves. Type 4.</summary>
            public bool EsFinal { get; set; }
        }

        public sealed class Sueno
        {
            public long CharacterId { get; init; }
            public string Nombre { get; init; } = "";
            public int Nivel { get; init; }
            public int Dificultad { get; init; }

            [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
            public List<Sala> Salas { get; } = new List<Sala>();

            /// <summary>En qué sala está. Empieza en la cero, que es la entrada.</summary>
            public int Actual { get; set; }

            /// <summary>The breed of the dreamer: the f4 of the izg's f1, the portrait.</summary>
            public int Breed { get; init; }

            /// <summary>
            /// The dream points: the f11. What the rooms give on entering, what the Rey Gob
            /// multiplies, what the fountain's shop is paid with.
            /// </summary>
            public int DreamPoints { get; set; }

            /// <summary>
            /// The bonus to experience and loot now, in percent: the f8. The loot bonus of the
            /// last room with a fight one entered (<see cref="LootBonusOf"/>), the difficulty's at
            /// first.
            /// </summary>
            public int Bonus { get; set; }

            /// <summary>The difficulty's bonus, which never moves: the f22.</summary>
            public int BaseBonus { get; init; }

            /// <summary>
            /// The rooms behind: the f3 of every graph, newest first. The entrance and a fountain
            /// count from the moment they are entered, a fight room from the moment it is left:
            /// "0" at the entrance and still "0" in the first fight room, "4", "2", "0" once in
            /// the third -- and the fountain on the list while one stands in it.
            /// </summary>
            [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
            public List<int> Visited { get; } = new List<int>();

            /// <summary>Tormentas astrales que quedan. Es el f7, y el número del botón.</summary>
            public int Tormentas { get; set; } = 1;

            /// <summary>
            /// The dreamer levels gained: the f14 of the izg, 50 in the invitation capture once
            /// the room of reward 154, "50 niveles de soñador", is behind. What they do in a fight
            /// is not known ("Mejora los efectos de los soñadores vinculados al nivel"), so they are
            /// counted and shown, and applied nowhere.
            /// </summary>
            public int DreamerLevels { get; set; }

            /// <summary>Draconiros arenas, the retries: the f17. See <see cref="StartingArenas"/>.</summary>
            /// <remarks>
            /// It was sent as the f19 for a time, and the f19 is something else: whether the room
            /// one stands in is clear. Spending it on a death is not implemented.
            /// </remarks>
            public int Arena { get; set; }

            /// <summary>Los potenciadores ya cobrados, en el orden en que cayeron.</summary>
            /// <remarks>
            /// Se cobra al ENTRAR en la sala, no al ganarla: la guía dice que los bonos se
            /// recogen al entrar y que el combate empieza inmediatamente después.
            /// </remarks>
            [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
            public List<Bono> Ganados { get; } = new List<Bono>();

            /// <summary>Por qué franja va, empezando por la I.</summary>
            /// <remarks>
            /// La guía del juego lo dice con todas las letras: «Chaque palier (à l'exception du
            /// premier et du dernier) commencera toujours par une Fontaine Onirique». O sea que la
            /// fuente que ABRE una franja es la última sala de la anterior: la misma vista desde
            /// los dos lados, que es justo lo que se mide —fila 4 con tipo 3 en 68 de 68—.
            ///
            /// Por eso al entrar en la fuente el sueño no se acaba: se le añade la franja
            /// siguiente y se sigue bajando. En la captura larga se ve el grafo creciendo, con
            /// salas de fila 5 y más dentro del mismo f16.
            /// </remarks>
            public int Franja { get; set; } = 1;

            /// <summary>Cuántos sueños se le han ofrecido ya. Es el f13 del iyj.</summary>
            /// <remarks>
            /// Las nueve capturas son del mismo personaje y el f13 vale 1, 2, 3, 4, 5, 6, 8, 9 y
            /// 10, en el orden en que se grabaron. O sea: una cuenta, no un identificador.
            /// </remarks>
            public int Cuenta { get; init; }

            /// <summary>Dónde estaba en el mundo antes de entrar, para devolverlo al salir.</summary>
            public long MapaDeVuelta { get; init; }
            public int CasillaDeVuelta { get; init; }

            [JsonIgnore]
            public Sala? SalaActual => Buscar(Actual);

            public Sala? Buscar(int id)
            {
                foreach (var s in Salas) if (s.Id == id) return s;
                return null;
            }
        }

        private static readonly ConcurrentDictionary<long, Sueno> _enCurso = new();
        private static readonly Random _azar = new Random();

        /// <summary>Los grupos que se pueden plantar en una sala, por nivel.</summary>
        /// <remarks>
        /// Se leen una vez y se quedan: son 38.744 filas y consultarlas por sala sería una lectura
        /// completa por bifurcación. Sólo interesan el mapa, la casilla y el nivel del grupo.
        /// </remarks>
        private static List<(int Id, long MapaId, int Casilla, int Nivel, string Miembros)>? _grupos;
        private static readonly object _candado = new object();

        public static int Activos => _enCurso.Count;

        /// <summary>
        /// A character's dream: the one in memory, or the one it saved -- a dream outlives a
        /// disconnection and a restart, and it used to vanish with either, leaving the player in
        /// a room with no dream around it and no way out.
        /// </summary>
        public static Sueno? De(long characterId)
        {
            if (_enCurso.TryGetValue(characterId, out var s)) return s;
            if (characterId == 0 || !_leidos.TryAdd(characterId, true)) return null;

            var saved = Deserialize(DatabaseManager.LoadDream(characterId));
            if (saved == null) return null;
            _enCurso[characterId] = saved;
            return saved;
        }

        /// <summary>The characters whose saved dream was looked for already: one read each.</summary>
        private static readonly ConcurrentDictionary<long, bool> _leidos = new();

        /// <summary>Fields included: a room's members are (monster, grade) tuples.</summary>
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };

        /// <summary>A dream as JSON, for the base.</summary>
        public static string Serialize(Sueno dream) => JsonSerializer.Serialize(dream, Json);

        /// <summary>A dream from its JSON, or null when there is none or it cannot be read.</summary>
        public static Sueno? Deserialize(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<Sueno>(json, Json); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sueños] A saved dream could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>Whether a map is one of the dream's: the entrance, a fight room or a fountain.</summary>
        public static bool IsDreamMap(long mapId)
        {
            if (_mapasDelSueno != null) return _mapasDelSueno.Contains(mapId);
            var todos = new HashSet<long>(TodosLosMapasDeSala());
            // Kept only once the interactives are read -- the fountain and end rooms are known by
            // their elements. Asked before (a test of another collection, a call at start-up), the
            // set was the entrance and little else, and it stayed so for good.
            if (MapasDeFuente().Count > 0 && MapasDeFinal().Count > 0) _mapasDelSueno = todos;
            return todos.Contains(mapId);
        }

        private static HashSet<long>? _mapasDelSueno;

        /// <summary>
        /// Takes the dream's groups off their maps, and keeps the dream: leaving a dream does not
        /// end it. The captures show it -- the well offers to continue the dream that was left.
        /// </summary>
        public static void Unplant(Sueno sueno)
        {
            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado == 0) continue;
                MobSpawnManager.RemoveMobGroup(sala.MapaDeLaSala, sala.Plantado);
                sala.Plantado = 0;
            }
        }

        /// <summary>
        /// The astral storm on a fight room: another group, on another map, the same room. In the
        /// Paradoja II capture the room stays "1" across both storms, its bestiary changes and the
        /// jru goes to another map; f7, the storms left, goes from 2 to 1 to nothing.
        /// </summary>
        public static void Reroll(Sueno sueno, Sala sala)
        {
            if (sala.Plantado != 0)
            {
                MobSpawnManager.RemoveMobGroup(sala.MapaDeLaSala, sala.Plantado);
                sala.Plantado = 0;
            }

            Cargar();
            sala.Miembros.Clear();
            Poblar(sala, sueno.Nivel, sueno.Dificultad, keepReward: true);

            var mapas = MapasDeSala().Where(m => m != sala.MapaDeLaSala).ToList();
            if (mapas.Count > 0) lock (_azar) sala.MapaDeLaSala = mapas[_azar.Next(mapas.Count)];
        }

        /// <summary>Se acabó el sueño: se olvida, y con él los grupos que dejó plantados.</summary>
        /// <remarks>
        /// Lo segundo importa tanto como lo primero. Los mapas de sala son cien y se reparten
        /// entre todos los sueños; un grupo que no se quita se queda ahí para el siguiente que
        /// caiga en ese mapa, y se va acumulando sala tras sala hasta que la sala tiene monstruos
        /// de tres sueños ajenos.
        /// </remarks>
        public static void Olvidar(long characterId)
        {
            if (!_enCurso.TryRemove(characterId, out var sueno)) return;

            foreach (var sala in sueno.Salas)
            {
                if (sala.Plantado == 0) continue;
                MobSpawnManager.RemoveMobGroup(sala.MapaDeLaSala, sala.Plantado);
                sala.Plantado = 0;
            }
        }

        /// <summary>De donde salio cada uno hacia el Plano Astral.</summary>
        /// <remarks>
        /// Se apunta al pulsar el boton del menu, que es el ultimo momento en que se sabe: dentro
        /// del plano y de las salas el mapa de la sesion ya es otro. Sin esto, salir del sueno
        /// dejaria al jugador en el plano en vez de donde estaba.
        /// </remarks>
        private static readonly ConcurrentDictionary<long, (long Mapa, int Casilla)> _deDonde = new();

        public static void RecordarDeDondeViene(long characterId, long mapa, int casilla)
            => _deDonde[characterId] = (mapa, casilla);

        public static (long Mapa, int Casilla) DeDondeViene(long characterId)
            => _deDonde.TryGetValue(characterId, out var d) ? d : (0, 0);

        /// <summary>Cuántos grupos hay disponibles para plantar en las salas.</summary>
        public static int GruposDisponibles { get { Cargar(); return _grupos?.Count ?? 0; } }

        private static void Cargar()
        {
            if (_grupos != null) return;
            lock (_candado)
            {
                if (_grupos != null) return;
                var grupos = new List<(int, long, int, int, string)>();

                try
                {
                    using var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                        DatabaseManager.WorldConnectionString);
                    conexion.Open();

                    var orden = conexion.CreateCommand();
                    orden.CommandText = "SELECT Id, MapId, CellId, MembersJson FROM MapMobs;";

                    using var lector = orden.ExecuteReader();
                    while (lector.Read())
                    {
                        if (lector.IsDBNull(3)) continue;

                        int nivel = NivelDe(lector.GetString(3));
                        if (nivel <= 0) continue;

                        grupos.Add((lector.GetInt32(0), lector.GetInt64(1),
                                    lector.IsDBNull(2) ? 0 : lector.GetInt32(2), nivel,
                                    lector.GetString(3)));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Sueños] No se pudieron leer los grupos: {ex.Message}");
                }

                _grupos = grupos;
            }
        }

        /// <summary>El nivel de un grupo: el del miembro más alto, que es lo que lo hace difícil.</summary>
        /// <summary>Los monstruos de un grupo, con el grado con el que salen en el mundo.</summary>
        private static List<(int Monstruo, int Grado)> MiembrosDe(string miembros)
        {
            var salen = new List<(int, int)>();
            try
            {
                using var doc = JsonDocument.Parse(miembros);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (!m.TryGetProperty("id", out var id) || !id.TryGetInt32(out int monstruo)) continue;
                    int grado = m.TryGetProperty("grade", out var g) && g.TryGetInt32(out int n) ? n : 0;
                    salen.Add((monstruo, grado));
                }
            }
            catch (Exception) { salen.Clear(); }
            return salen;
        }

        private static int NivelDe(string miembros)
        {
            int mayor = 0;
            try
            {
                using var doc = JsonDocument.Parse(miembros);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    if (m.TryGetProperty("level", out var n) && n.TryGetInt32(out int nivel))
                    {
                        if (nivel > mayor) mayor = nivel;
                    }
                }
            }
            catch (Exception) { return 0; }
            return mayor;
        }

        public static void Initialize()
        {
            Cargar();
            Console.WriteLine($"[Sueños] {GruposDisponibles} grupos para plantar en las salas; " +
                              $"{DreamData.Loot.Count} lines of dream loot.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Montar un sueño
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Genera un sueño nuevo: el rombo de once salas, con su grupo y su modificación.
        /// </summary>
        /// <remarks>
        /// La entrada y la última no llevan grupo — en la captura la sala «0» viaja con un solo
        /// campo y la «10» sin f9 —, así que sólo se puebla lo de en medio.
        /// </remarks>
        public static Sueno Crear(long characterId, string nombre, int nivel, int dificultad,
                                  long mapaDeVuelta, int casillaDeVuelta, int breed = 0)
        {
            Cargar();

            // Empezar uno nuevo tira el anterior, que es lo que hace el cliente al confirmar. Va
            // por Olvidar para que se lleve por delante los grupos que dejó plantados.
            Olvidar(characterId);

            _cuenta.TryGetValue(characterId, out int cuenta);
            _cuenta[characterId] = ++cuenta;

            var sueno = new Sueno
            {
                CharacterId = characterId,
                Nombre = nombre,
                Nivel = nivel,
                Dificultad = Math.Clamp(dificultad, 1, MaximaDificultad),
                Cuenta = cuenta,
                Breed = breed,
                BaseBonus = BonusOf(Math.Clamp(dificultad, 1, MaximaDificultad)),
                Bonus = BonusOf(Math.Clamp(dificultad, 1, MaximaDificultad)),
                DreamPoints = StartingDreamPoints(Math.Clamp(dificultad, 1, MaximaDificultad)),
                Arena = StartingArenas(Math.Clamp(dificultad, 1, MaximaDificultad)),
                MapaDeVuelta = mapaDeVuelta,
                CasillaDeVuelta = casillaDeVuelta,
            };

            // Cinco filas: la entrada, tres de entre dos y cuatro salas, y la última. El ancho de
            // las de en medio cambia de un sueño a otro —nueve capturas y siete repartos
            // distintos— así que se sortea, con una semilla que hace el sueño reproducible.
            var dado = new Random(HashCode.Combine(characterId, cuenta));

            // El total de las tres filas de en medio va de siete a nueve —los sueños medidos
            // tienen nueve, diez u once salas—, así que no vale sortear cada fila por su cuenta:
            // tres tiradas libres de 2 a 4 dan de seis a doce. Se reparte un total. Y la primera
            // fila nunca pasa de tres: la entrada abre a TODAS sus salas y un mapa sólo trae tres
            // puertas, así que con cuatro una quedaría sin puerta que la abriese.
            var anchos = AnchosDeLasFilas(dado, FightRowsOf(1));

            MontarUnaFranja(sueno, anchos, nivel, primera: true);

            _enCurso[characterId] = sueno;
            return sueno;
        }

        /// <summary>
        /// Añade la franja siguiente al sueño y devuelve por dónde se entra en ella.
        /// </summary>
        /// <remarks>
        /// Se llama al pisar la Fuente, que es la última sala de la franja en curso y a la vez la
        /// primera de la que viene. Sin esto el jugador se queda encerrado ahí: la fuente no tiene
        /// salidas y el sueño no tiene forma de seguir ni de acabarse.
        /// </remarks>
        public static void AnadirFranja(Sueno sueno)
        {
            Cargar();

            if (sueno.Franja >= Bands) return;
            var dado = new Random(HashCode.Combine(sueno.CharacterId, sueno.Cuenta, sueno.Franja));
            var anchos = AnchosDeLasFilas(dado, FightRowsOf(sueno.Franja + 1));

            sueno.Franja++;
            MontarUnaFranja(sueno, anchos, sueno.Nivel, primera: false);
        }

        /// <summary>
        /// The five bands of a dream, measured whole in the invitation capture, whose player is in
        /// the fifth: 26 rows of rooms, the dofuspourlesnoobs guide's "un songe est constitué de 26
        /// salles" in depth.
        ///
        ///   I     row 0 the entrance, rows 1-3 fights, row 4 a fountain
        ///   II    the fountain of 4, rows 5-9 fights, row 10 a fountain
        ///   III   the fountain of 10, rows 11-15 fights, row 16 a fountain
        ///   IV    the fountain of 16, rows 17-21 fights, row 22 one fight room alone
        ///   V     that room, rows 23-24 fights, row 25 a fountain, row 26 the Fin du rêve
        ///
        /// Fountains at depths 4, 10, 16 and 25, as the guide lists them.
        /// </summary>
        public const int Bands = 5;

        /// <summary>How many rows of fights a band has between its entry and what closes it.</summary>
        public static int FightRowsOf(int franja) => franja <= 1 ? 3 : franja >= Bands ? 2 : 5;

        /// <summary>Whether a room closes its band -- saved dreams from before the mark count their fountains.</summary>
        public static bool Closes(Sala sala) => sala.Cierre || (sala.EsFuente && sala.Franja < Bands);

        /// <summary>Las tres filas de en medio, con el total que sale medido.</summary>
        private static int[] AnchosDeLasFilas(Random dado, int filas = 3)
        {
            // Two to four a row, the first no more than three -- its entry has three doors. The
            // totals are the measured ones: 7 to 9 over the three rows of band I; 14, 15 and 16
            // over the five of bands II to IV; 3 and 3 over the two of band V.
            var anchos = Enumerable.Repeat(MinimoPorFila, filas).ToArray();
            int total = filas == 3 ? dado.Next(MinimoDeEnMedio, MaximoDeEnMedio + 1)
                      : filas == 5 ? dado.Next(13, 18)
                      : dado.Next(filas * MinimoPorFila, filas * 3 + 1);
            int sobran = total - MinimoPorFila * filas;
            while (sobran > 0)
            {
                int donde = dado.Next(anchos.Length);
                int tope = donde == 0 ? PuertasPorSala : MaximoPorFila;
                if (anchos[donde] >= tope) continue;
                anchos[donde]++;
                sobran--;
            }
            return anchos;
        }

        /// <summary>
        /// Monta una franja: tres filas de pelea y una Fuente al final.
        /// </summary>
        /// <remarks>
        /// La primera lleva además su sala de entrada; las demás entran por la fuente de la
        /// anterior, que ya está puesta y sólo hay que colgarle las salidas nuevas.
        /// </remarks>
        private static void MontarUnaFranja(Sueno sueno, int[] anchos, int nivel, bool primera)
        {
            int siguiente = 0;
            foreach (var puesta in sueno.Salas) siguiente = Math.Max(siguiente, puesta.Id + 1);

            int filaBase = 0;
            foreach (var puesta in sueno.Salas) filaBase = Math.Max(filaBase, puesta.Fila + 1);

            var porFila = new List<List<Sala>>();

            if (primera)
            {
                var entrada = new Sala { Id = siguiente++, Fila = filaBase++ };
                sueno.Salas.Add(entrada);
                porFila.Add(new List<Sala> { entrada });
            }
            else
            {
                // What closed the band before is the door of this one: a fountain -- which stays a
                // fountain, room 9 of the long capture is of type 3 in both its graphs -- or, for
                // band V, the lone fight room of row 22. The newest is the last on the list.
                Sala? fuente = null;
                foreach (var puesta in sueno.Salas) if (Closes(puesta) && puesta.Franja == sueno.Franja - 1) fuente = puesta;
                if (fuente == null) return;
                porFila.Add(new List<Sala> { fuente });
            }

            for (int i = 0; i < anchos.Length; i++)
            {
                var deLaFila = new List<Sala>();
                for (int j = 0; j < anchos[i]; j++)
                {
                    var sala = new Sala { Id = siguiente++, Fila = filaBase, Franja = sueno.Franja };
                    deLaFila.Add(sala);
                    sueno.Salas.Add(sala);
                }
                filaBase++;
                porFila.Add(deLaFila);
            }

            // A dream favour: in bands II to IV, three bands in four, one room of a fight row. In
            // the invitation capture's five bands it is rooms "15", "29" and "41", one in each of
            // bands II, III and IV and on their third, second and first fight rows; bands I --
            // eleven of them in the captures -- and V have none, and the long capture's band II
            // has none either. The guide: "De manière plus rare, vous pouvez également croiser une
            // salle où se trouve un seul PNJ". Three in four is those four middle bands, no more.
            if (sueno.Franja >= FirstFavorBand && sueno.Franja <= LastFavorBand && anchos.Length > 0)
            {
                bool favour;
                int row, which;
                lock (_azar)
                {
                    favour = _azar.Next(FavorBandsOutOf) < FavorBandsWith;
                    row = 1 + _azar.Next(anchos.Length);
                    which = _azar.Next(porFila[row].Count);
                }
                if (favour) porFila[row][which].EsFavor = true;
            }

            if (sueno.Franja == Bands - 1)
            {
                // Band IV closes on one fight room alone, which opens band V.
                var sola = new Sala { Id = siguiente++, Fila = filaBase++, Cierre = true, Franja = sueno.Franja };
                sueno.Salas.Add(sola);
                porFila.Add(new List<Sala> { sola });
            }
            else
            {
                var laFuente = new Sala
                {
                    Id = siguiente++, Fila = filaBase++, EsFuente = true, Franja = sueno.Franja,
                    Cierre = sueno.Franja < Bands,
                };
                lock (_azar) laFuente.HasReyGob = _azar.Next(ReyGobOneIn) == 0;
                sueno.Salas.Add(laFuente);
                porFila.Add(new List<Sala> { laFuente });
            }

            if (sueno.Franja == Bands)
            {
                // And after band V's fountain, the end of the dream.
                var fin = new Sala { Id = siguiente++, Fila = filaBase++, EsFinal = true, Franja = sueno.Franja, Senalada = true };
                fin.Miembros.AddRange(FinalWave(1));
                sueno.Salas.Add(fin);
                porFila.Add(new List<Sala> { fin });
            }

            // Y las salidas. Cada sala se abre a la de su misma posición en la fila siguiente y a
            // la de al lado, que es lo que hace que la de en medio se alcance por dos caminos: en
            // la captura a la 4 se llega desde la 1 y desde la 2.
            for (int fila = 0; fila + 1 < porFila.Count; fila++)
            {
                var esta = porFila[fila];
                var abajo = porFila[fila + 1];

                // La entrada abre a TODA la fila siguiente —«0 -> 1,2,3» en la captura— y la fila
                // de encima de la última lleva entera a la última —«7,8,9 -> 10»—. Las dos cosas
                // están en las nueve.
                if (esta.Count == 1 || abajo.Count == 1)
                {
                    foreach (var origen in esta)
                    {
                        foreach (var destino in abajo) origen.Salidas.Add(destino.Id);
                    }
                    continue;
                }

                // A dream favour opens one way on: "15 -> 19", "29 -> 32", "41 -> 44", the three of
                // the invitation capture.
                for (int i = 0; i < esta.Count; i++)
                {
                    int primero = i * abajo.Count / esta.Count;
                    esta[i].Salidas.Add(abajo[primero].Id);
                    if (primero + 1 < abajo.Count && !esta[i].EsFavor) esta[i].Salidas.Add(abajo[primero + 1].Id);
                }

                // Y que no quede ninguna sin padre. Una sala a la que no se puede llegar se dibuja
                // igual en la ventana, y el jugador la ve y no entiende por qué no la alcanza.
                for (int j = 0; j < abajo.Count; j++)
                {
                    if (esta.Exists(x => x.Salidas.Contains(abajo[j].Id))) continue;

                    // Al que tenga sitio: ninguna sala puede ofrecer más salidas que puertas hay
                    // en su mapa, o la de más no se podría pulsar. Not a favour: it keeps its one.
                    var padre = esta.Find(x => !x.EsFavor && x.Salidas.Count < PuertasPorSala)
                                ?? esta.Find(x => !x.EsFavor)
                                ?? esta[Math.Min(j, esta.Count - 1)];
                    padre.Salidas.Add(abajo[j].Id);
                }
            }

            RepartirMapas(sueno);

            // Every room past the entry fights but a fountain, a favour and the end: the band's
            // middle rows, and band IV's lone closing room. Band V's give 10 dream points, row
            // 22's included -- "dp10" in all of them in the invitation capture -- where the others
            // give 5, and 15 the marked ones. A favour gives none: its f3 is not written.
            for (int i = 1; i < porFila.Count; i++)
            {
                foreach (var sala in porFila[i])
                {
                    if (sala.EsFuente || sala.EsFinal || sala.EsFavor) continue;
                    if (sala.Miembros.Count > 0) continue;
                    Poblar(sala, nivel, sueno.Dificultad);

                    sala.Senalada = i == porFila.Count - 2 && sala.Id % 3 == 0;
                    sala.DreamPoints = sala.Senalada ? ClaseSenalada : sala.Fila >= FilaDeLaVenta ? ClaseDeLaVenta : ClaseNormal;
                    sala.Score = i * 5 + (sala.Senalada ? 15 : 5) + 4 * (sueno.Franja - 1);
                }
            }
            // The end scores five over the best room of its band: 32 after rooms of 26 and 27.
            foreach (var fin in porFila.SelectMany(f => f).Where(s => s.EsFinal))
                fin.Score = porFila.SelectMany(f => f).Where(s => !s.EsFinal).Max(s => s.Score) + 5;
        }

        /// <summary>
        /// The dream moves into a room: the room left behind goes on the path, and a room pays
        /// out the first time it is entered -- its bonus and its dream points, before the fight,
        /// which is when the captures show f15 and f11 growing and not after the win. Null for a
        /// room the dream does not have; the bonus it paid, when it paid one.
        /// </summary>
        public static Sala? Enter(Sueno dream, int roomId, out Bono? gained)
        {
            gained = null;
            var room = dream.Buscar(roomId);
            if (room == null) return null;

            if (dream.Actual != roomId && dream.Buscar(dream.Actual) != null) Visit(dream, dream.Actual);
            dream.Actual = roomId;
            if (room.Miembros.Count == 0) Visit(dream, roomId);

            if (!room.Cobrada)
            {
                room.Cobrada = true;
                dream.DreamPoints += room.DreamPoints;
                if (room.Reward != null)
                {
                    Pay(dream, room.Reward);
                    gained = room.Regalo;
                }
            }

            // The loot bonus is the room's where there is a fight: the fountain of row 4 of the
            // long capture leaves f8 at the 50 of row 3, and row 5 takes it to 55.
            if (room.Miembros.Count > 0 || room.EsFinal)
                dream.Bonus = LootBonusOf(dream.Dificultad, room.Fila, room.Senalada);

            if (room.EsFuente && room.Offers == null) room.Offers = new List<Reward>(ShopOffers);
            if (room.EsFavor && !room.FavorChosen && room.Offers == null) room.Offers = DrawFavor();
            return room;
        }

        /// <summary>
        /// What a reward gives, into the dream: its dream points, its storms, its sand, its
        /// dreamer levels and its bonuses. A room's on entering, a fountain's on buying, a
        /// favour's on choosing.
        /// </summary>
        private static void Pay(Sueno dream, Reward reward)
        {
            dream.DreamPoints += reward.Points;
            dream.Tormentas += reward.Storms;
            dream.Arena += reward.Sand;
            dream.DreamerLevels += reward.Levels;
            foreach (var bonus in reward.Bonuses) Gain(dream, bonus);
        }

        /// <summary>
        /// The three choices of a dream favour: two bonuses drawn from <see cref="FavorBonuses"/>,
        /// different ones, and the purse of ten dream points last -- "Le choix n°3 est toujours
        /// une petite bourse de 10 Points de rêve". All three free: the shop window the client
        /// opens on them says "Gratis" where a fountain says its price
        /// (InfiniteDreamShopUi.BindRewardLine, "ui.shop.free").
        /// </summary>
        internal static List<Reward> DrawFavor()
        {
            var pool = FavorBonuses.ToList();
            var drawn = new List<Reward>();
            lock (_azar)
            {
                while (drawn.Count < FavorBonusesOffered && pool.Count > 0)
                {
                    var pick = pool[_azar.Next(pool.Count)];
                    drawn.Add(pick);
                    pool.RemoveAll(r => r.Tag == pick.Tag);
                }
            }
            drawn.Add(FavorPurse);
            return drawn;
        }

        /// <summary>How many bonuses a favour offers before its purse: "3 bonus gratuits", the last the purse.</summary>
        public const int FavorBonusesOffered = 2;

        /// <summary>
        /// Whether one may leave a room: a room with a fight once it is won, a dream favour once
        /// its favour is chosen -- the guide: one of the three has to be chosen to go on -- and
        /// any other room at once. A fight room whose group is no longer planted counts as won,
        /// the way a dream continued after a disconnection finds it.
        /// </summary>
        public static bool CanLeave(Sala room)
        {
            if (room.EsFavor) return room.FavorChosen;
            if (room.Miembros.Count == 0) return true;
            if (room.Hecha) return true;
            return room.Plantado == 0;
        }

        /// <summary>What <see cref="SkipTo"/> made of it.</summary>
        public enum SkipOutcome { Done, NoRoom, Behind, Past }

        /// <summary>
        /// The dream carried forward, to test what lies deep in it without the fights first: its
        /// bands opened as far as needed, and a way down the exits from the room one stands in to
        /// the nearest room of <paramref name="row"/> -- or to the Fin du rêve, with no row. Every
        /// room on the way is entered and won as if it had been fought: its bonus and its dream
        /// points paid, its step drawn on the path. The room stood in counts as won too. The room
        /// reached is returned, not entered: entering it is the handler's, with its map and group.
        /// </summary>
        /// <returns>
        /// Behind when the row is not past the room stood in -- a dream only goes forward -- and
        /// Past when the dream has no such row. Skipped counts the fights won on the way.
        /// </returns>
        public static (SkipOutcome Outcome, Sala? Room, int Skipped) SkipTo(Sueno dream, int? row)
        {
            var from = dream.SalaActual;
            if (from == null) return (SkipOutcome.NoRoom, null, 0);
            if (row.HasValue ? row.Value <= from.Fila : from.EsFinal) return (SkipOutcome.Behind, from, 0);

            bool Reached(Sala room) => row.HasValue ? room.Fila == row.Value : room.EsFinal;
            while (!dream.Salas.Any(Reached) && dream.Franja < Bands) AnadirFranja(dream);

            // Breadth first down the exits: the nearest room of the row, and the way to it.
            var cameFrom = new Dictionary<int, int> { [from.Id] = from.Id };
            var queue = new Queue<int>();
            queue.Enqueue(from.Id);
            Sala? target = null;
            while (queue.Count > 0 && target == null)
            {
                var room = dream.Buscar(queue.Dequeue());
                if (room == null) continue;
                foreach (int next in room.Salidas)
                {
                    if (cameFrom.ContainsKey(next)) continue;
                    cameFrom[next] = room.Id;
                    var nextRoom = dream.Buscar(next);
                    if (nextRoom != null && Reached(nextRoom)) { target = nextRoom; break; }
                    queue.Enqueue(next);
                }
            }
            if (target == null) return (SkipOutcome.Past, null, 0);

            var way = new List<int>();
            for (int step = cameFrom[target.Id]; step != from.Id; step = cameFrom[step]) way.Insert(0, step);

            int skipped = 0;
            if (from.Miembros.Count > 0 && !from.Hecha) { from.Hecha = true; skipped++; }
            foreach (int id in way)
            {
                var room = Enter(dream, id, out _);
                if (room == null || room.Miembros.Count == 0) continue;
                room.Hecha = true;
                skipped++;
            }
            return (SkipOutcome.Done, target, skipped);
        }

        /// <summary>
        /// Buys at the fountain one stands at, or chooses at the dream favour: the offer named by
        /// its f10 -- what the client sends, measured the first time a purchase was tried -- or
        /// else by its icon or its place in the shop. Its price comes off the dream points, what it
        /// gives goes into the dream, and it leaves the shop; at a favour the other two go with it,
        /// the favour being one choice. Null when it cannot be had, and why in
        /// <paramref name="refusal"/>.
        /// </summary>
        /// <remarks>
        /// The favour's choice comes in the same way as a purchase: its three rewards are shown in
        /// the client's shop window, which the client opens in favour mode by itself when the room
        /// one stands in is of kind 2 (InfiniteDreamShopUi.Setup @0x18288A1F8 compares the current
        /// room's kind with 2 for the "isFavor" class and the "ui.infiniteDreams.dreamFavor"
        /// title). INFERRED that its button sends the fountain's iym: the window is the same one.
        /// </remarks>
        public static Reward? Buy(Sueno dream, int which, out string refusal)
        {
            refusal = "";
            var room = dream.SalaActual;
            bool favour = room != null && room.EsFavor;
            if (room == null || !(room.EsFuente || favour) || room.Offers == null)
            {
                refusal = room != null && room.EsFavor && room.FavorChosen
                    ? "the favour was chosen already"
                    : "not at a fountain or a favour";
                return null;
            }

            var offer = room.Offers.Find(o => o.Tag == which)
                        ?? room.Offers.Find(o => o.Id == which)
                        ?? (which >= 0 && which < room.Offers.Count ? room.Offers[which] : null);
            if (offer == null)
            {
                refusal = $"no offer {which}";
                return null;
            }
            if (dream.DreamPoints < offer.Price)
            {
                refusal = $"{dream.DreamPoints} dream points for a price of {offer.Price}";
                return null;
            }

            dream.DreamPoints -= offer.Price;
            Pay(dream, offer);
            room.Offers.Remove(offer);
            if (favour)
            {
                room.FavorChosen = true;
                room.Offers = null;
            }
            return offer;
        }

        /// <summary>
        /// The astral storm on a dream favour not yet chosen: its two bonuses drawn again, the
        /// purse still last. The client's reward 24, "Tormenta astral": "Permite reiniciar todos
        /// los monstruos presentes en una sala, todos los artículos de la fuente, así como el
        /// favor", and the guide: a Tempête astrale rerolls the favour's options. False where
        /// there is no favour to reroll.
        /// </summary>
        public static bool RerollFavor(Sala room)
        {
            if (!room.EsFavor || room.FavorChosen) return false;
            room.Offers = DrawFavor();
            return true;
        }

        /// <summary>
        /// A bonus joins the ones gained, added to the one of the same effect when there is one:
        /// the captures never list an effect twice but for 792, whose two carry different spells,
        /// and they do list 3 AP, 3 MP and 2 of maximum range, sums of the rooms' ones.
        /// </summary>
        public static void Gain(Sueno dream, Bono bonus)
        {
            int same = Adds(bonus.Efecto)
                ? dream.Ganados.FindIndex(b => b.Efecto == bonus.Efecto && b.Anidado == bonus.Anidado)
                : -1;
            if (same < 0)
            {
                dream.Ganados.Add(bonus);
                return;
            }
            dream.Ganados[same] = new Bono(bonus.Efecto, dream.Ganados[same].Valor + bonus.Valor, bonus.Anidado);
        }

        /// <summary>
        /// Whether two of an effect add up: not 792, whose value is not a quantity, nor 3405,
        /// the shop's spells, whose "value" is which one.
        /// </summary>
        private static bool Adds(int effect) => effect != 792 && effect != 3405;

        // The effects of the rooms' and the shop's bonuses that a fight knows how to apply.
        private const int ActionPointsEffect = 111;
        private const int MovementPointsEffect = 128;
        private const int RangeEffect = 117;
        private const int SpellsMaxRangeEffect = 281;
        private const int SpellsCooldownEffect = 286;
        private const int SpellsCastsPerTargetEffect = 291;
        private const int CriticalEffect = 115;
        private const int VitalityPercentEffect = 2844;
        private const int DamagePercentEffect = 4041;

        /// <summary>
        /// The dream's bonuses on a fighter of one of its rooms: they are fight bonuses, and the
        /// dream's guide says so -- "bonuses apply during combat". Returns what was applied, and
        /// leaves out what a fight here cannot do yet: the spells of the shop (3405), and the
        /// few f15 effects no room of ours gives.
        /// </summary>
        /// <remarks>
        /// "+N maximum range" with no spell named (281) is the range of every spell, which is what
        /// the fighter's own range is. "% vitality" is of the life the fighter starts with.
        /// </remarks>
        public static List<string> ApplyTo(Fighter fighter, Sueno dream)
        {
            var applied = new List<string>();
            foreach (var bonus in dream.Ganados)
            {
                int v = bonus.Valor;
                switch (bonus.Efecto)
                {
                    case ActionPointsEffect:
                        fighter.MaxAP += v; fighter.CurrentAP += v; break;
                    case MovementPointsEffect:
                        fighter.MaxMP += v; fighter.CurrentMP += v; break;
                    case RangeEffect:
                    case SpellsMaxRangeEffect:
                        fighter.Range += v; break;
                    case CriticalEffect:
                        fighter.CriticalBonus += v; break;
                    case VitalityPercentEffect:
                        int extra = (int)Math.Round(fighter.MaxHP * v / 100.0);
                        fighter.MaxHP += extra; fighter.CurrentHP += extra; break;
                    case DamagePercentEffect:
                        fighter.DamageDealtPercent += v; break;
                    case SpellsCooldownEffect:
                        fighter.CooldownReduction += v; break;
                    case SpellsCastsPerTargetEffect:
                        fighter.ExtraCastsPerTarget += v; break;
                    default:
                        continue;
                }
                applied.Add($"{bonus.Efecto}:{v}");
            }
            return applied;
        }

        private static readonly ConcurrentDictionary<int, bool> _bosses = new();

        /// <summary>
        /// Whether a monster is a boss: the value-4 bit of its template's m_flags. All 137 bosses
        /// of the dungeons carry it and 209 monsters of 5,134 do; it is the f4 of a monster of the
        /// bestiary, measured on the three that carry one.
        /// </summary>
        public static bool IsBoss(int monsterId)
            => _bosses.GetOrAdd(monsterId, id =>
            {
                try
                {
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                    command.Parameters.AddWithValue("$id", id);
                    if (command.ExecuteScalar() is not string data) return false;
                    using var doc = JsonDocument.Parse(data);
                    return doc.RootElement.TryGetProperty("m_flags", out var flags)
                           && flags.TryGetInt64(out long bits) && (bits & BossFlag) != 0;
                }
                catch (Exception)
                {
                    return false;
                }
            });

        private const long BossFlag = 4;

        private static void Visit(Sueno dream, int roomId)
        {
            if (!dream.Visited.Contains(roomId)) dream.Visited.Insert(0, roomId);
        }

        /// <summary>
        /// A cada sala, un mapa de los suyos.
        /// </summary>
        /// <remarks>
        /// La entrada es siempre el 237897728 —diez de diez capturas— y las demás salen del
        /// catálogo de la subárea 904, cogiendo sólo los que traen sus tres puertas. Sin repetir
        /// dentro de un mismo sueño: dos salas en el mismo mapa harían que sus puertas fueran las
        /// mismas y el camino dejaría de significar nada.
        /// </remarks>
        private static void RepartirMapas(Sueno sueno)
        {
            var libres = new List<long>(MapasDeSala());
            if (libres.Count == 0) return;

            var dado = new Random(HashCode.Combine(sueno.CharacterId, sueno.Cuenta, sueno.Franja));
            var fuentes = MapasDeFuente();

            foreach (var sala in sueno.Salas)
            {
                if (sala.MapaDeLaSala != 0) continue;
                if (sala.Fila == 0)
                {
                    sala.MapaDeLaSala = MapaDeEntrada;
                    continue;
                }

                // A fountain on one of the five maps that have the fountain itself: the real one
                // is 237783053, whose fourth element is the Fontaine onirique. On any other map
                // there is nothing to open the shop with.
                if (sala.EsFuente && fuentes.Count > 0)
                {
                    sala.MapaDeLaSala = fuentes[dado.Next(fuentes.Count)];
                    continue;
                }

                // The end of the dream on one of the three maps of the subarea with a single door:
                // a room one fights in and leaves, with no fork after it.
                var finales = MapasDeFinal();
                if (sala.EsFinal && finales.Count > 0)
                {
                    sala.MapaDeLaSala = finales[dado.Next(finales.Count)];
                    continue;
                }

                // A dream favour on the map made for one -- see MapasDeFavor.
                var favores = MapasDeFavor();
                if (sala.EsFavor && favores.Count > 0)
                {
                    sala.MapaDeLaSala = favores[dado.Next(favores.Count)];
                    continue;
                }

                int i = dado.Next(libres.Count);
                sala.MapaDeLaSala = libres[i];
                libres.RemoveAt(i);

                if (libres.Count == 0) libres.AddRange(MapasDeSala());
            }
        }

        private static List<long>? _mapasDeSala;

        /// <summary>Los mapas de sala: subárea 904 y con sus tres puertas.</summary>
        /// <remarks>
        /// La subárea trae 484 mapas y sólo 100 llevan elementos interactivos. Los que los llevan
        /// llevan exactamente tres, con el gráfico 90166 —el mismo del pozo—, que son las puertas.
        /// Un mapa de sala sin puertas sería un callejón del que no se puede salir.
        /// </remarks>
        /// <summary>Todos los mapas del sueño, la entrada incluida, para declararles las puertas.</summary>
        public static IEnumerable<long> TodosLosMapasDeSala()
        {
            yield return MapaDeEntrada;
            foreach (long mapa in MapasDeSala()) yield return mapa;
            foreach (long mapa in MapasDeFuente()) yield return mapa;
            foreach (long mapa in MapasDeFinal()) yield return mapa;
            foreach (long mapa in MapasDeFavor()) yield return mapa;
        }

        /// <summary>
        /// Whether an element of a dream's map is one the client can use: a door, or the fountain.
        /// The favour room's centrepiece is neither, and declaring it a door made it one more exit.
        /// </summary>
        public static bool IsDoorOrFountain(int gfx) => DoorGfx.Contains(gfx) || gfx == FountainGfx;

        /// <summary>
        /// The graphics of a room's doors: 90166, the pools of 69 maps, and 65148, the jets of 22
        /// -- blue or red, the colour of what is behind them.
        /// </summary>
        private static readonly HashSet<int> DoorGfx = new HashSet<int> { 90166, 65148 };

        /// <summary>
        /// The Fontaine onirique of a fountain room: graphic 94001, the fourth element of the five
        /// fountain maps -- 539708 on the long capture's 237783053, declared with skill 355,
        /// "Consultar", where the doors have 184.
        /// </summary>
        public const int FountainGfx = 94001;
        public const int FountainSkill = 355;

        /// <summary>The doors of a map, in its own order: its elements of a door's graphic.</summary>
        public static List<Interactives.Element> DoorsOf(long mapId)
            => Interactives.ElementsOf(mapId).Where(e => DoorGfx.Contains(e.Gfx)).ToList();

        /// <summary>The Fontaine onirique of a room, or zero.</summary>
        public static int FountainOf(Sala sala)
        {
            if (sala.MapaDeLaSala == 0) return 0;
            foreach (var element in Interactives.ElementsOf(sala.MapaDeLaSala))
                if (element.Gfx == FountainGfx) return element.Id;
            return 0;
        }

        private static List<long>? _mapasDeFuente;
        private static List<long>? _mapasDeFinal;
        private static List<long>? _mapasDeFavor;

        /// <summary>
        /// The maps a dream favour can be on: those of subarea 904 with their three doors and one
        /// more element that is neither a door nor the fountain. There is one, 237787188, whose
        /// fourth element, 540939 (graphic 306053), stands on cell 313 -- the fountain's own cell
        /// on the five fountain maps, of which it has the layout -- by a campfire.
        /// </summary>
        /// <remarks>
        /// INFERRED: no capture stands in a favour room, so no jru names its map. It is the one
        /// map of the dream's that no other kind of room can use, and a room "où se trouve un seul
        /// PNJ" needs a map without a fight's to stand on. With no such map, a favour goes on a
        /// fight room's map.
        /// </remarks>
        internal static List<long> MapasDeFavor()
        {
            if (_mapasDeFavor != null) return _mapasDeFavor;
            var salen = MapasDeLaSubarea()
                .Where(m => DoorsOf(m).Count == PuertasPorSala
                            && Interactives.ElementsOf(m).Count == PuertasPorSala + 1
                            && Interactives.ElementsOf(m).Any(e => !IsDoorOrFountain(e.Gfx)))
                .OrderBy(m => m).ToList();
            if (salen.Count > 0) _mapasDeFavor = salen;
            return salen;
        }

        /// <summary>The three maps of subarea 904 with one door and nothing else: 237785140, 237785159 and 237789236.</summary>
        private static List<long> MapasDeFinal()
        {
            if (_mapasDeFinal != null) return _mapasDeFinal;
            var salen = MapasDeLaSubarea()
                .Where(m => m != MapaDeEntrada && DoorsOf(m).Count == 1 && Interactives.ElementsOf(m).Count == 1)
                .OrderBy(m => m).ToList();
            if (salen.Count > 0) _mapasDeFinal = salen;
            return salen;
        }

        /// <summary>The five maps of subarea 904 with the fountain and its three doors.</summary>
        private static List<long> MapasDeFuente()
        {
            if (_mapasDeFuente != null) return _mapasDeFuente;
            var salen = MapasDeLaSubarea()
                .Where(m => DoorsOf(m).Count >= PuertasPorSala
                            && Interactives.ElementsOf(m).Any(e => e.Gfx == FountainGfx))
                .OrderBy(m => m).ToList();
            if (salen.Count > 0) _mapasDeFuente = salen;
            return salen;
        }

        /// <summary>
        /// The Rey Gob stands in one fountain in this many. Not measured: the guide only says he
        /// is "much rarer than the other" goblins, and the one capture that meets him meets him
        /// at a fountain. He used to stand in every one, where the shop is what belongs.
        /// </summary>
        public const int ReyGobOneIn = 4;

        /// <summary>
        /// The bands a dream favour can be in, II to IV, and how often one is: three bands in
        /// four -- see <see cref="MontarUnaFranja"/>.
        /// </summary>
        public const int FirstFavorBand = 2;
        public const int LastFavorBand = 4;
        public const int FavorBandsWith = 3;
        public const int FavorBandsOutOf = 4;

        /// <summary>The maps of subarea 904, the dream's.</summary>
        private static HashSet<long> MapasDeLaSubarea()
        {
            var deLaSubarea = new HashSet<long>();
            try
            {
                using var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                conexion.Open();

                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT MapId FROM MapSubareas WHERE SubAreaId = $sub;";
                orden.Parameters.AddWithValue("$sub", SubareaDeLasSalas);

                using var lector = orden.ExecuteReader();
                while (lector.Read()) deLaSubarea.Add(lector.GetInt64(0));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sueños] No se han podido leer los mapas de sala: {ex.Message}");
            }
            return deLaSubarea;
        }

        private static List<long> MapasDeSala()
        {
            if (_mapasDeSala != null) return _mapasDeSala;

            var deLaSubarea = MapasDeLaSubarea();
            var salen = new List<long>();
            foreach (long mapId in deLaSubarea)
            {
                if (mapId == MapaDeEntrada) continue;
                var elementos = Interactives.ElementsOf(mapId);
                if (elementos.Count < PuertasPorSala) continue;
                // A fight room is doors and nothing else: the fountain maps and 237787188, whose
                // fourth element (306053) is something else again, are not fight rooms.
                if (elementos.Any(e => !DoorGfx.Contains(e.Gfx))) continue;
                salen.Add(mapId);
            }

            salen.Sort();

            // Vacío NO se guarda. Si esto se pide antes de que Interactives esté cargado la lista
            // sale vacía, y cachearla dejaría todos los sueños de la sesión sin mapas de sala.
            if (salen.Count == 0) return salen;

            _mapasDeSala = salen;
            Console.WriteLine($"[Sueños] {salen.Count} mapas de sala en la subárea {SubareaDeLasSalas}.");
            return _mapasDeSala;
        }

        /// <summary>La puerta número <paramref name="cual"/> de una sala, o cero si no la tiene.</summary>
        public static int PuertaDe(Sala sala, int cual)
        {
            if (sala.MapaDeLaSala == 0) return 0;

            var puertas = DoorsOf(sala.MapaDeLaSala);
            if (cual < 0 || cual >= puertas.Count) return 0;
            return puertas[cual].Id;
        }

        /// <summary>El Rey Gob del Favor Onírico, y dónde se pone.</summary>
        /// <remarks>
        /// Medido en «sueño infinito largo»: npc 7850, casilla 232, orientación 3, con el id
        /// contextual negativo de siempre. Su diálogo está en content/npcs/dialogues.json.
        /// </remarks>
        public const int ReyGob = 7850;
        public const int CasillaDelReyGob = 232;
        public const int OrientacionDelReyGob = 3;

        /// <summary>
        /// The Dispensador de favores, who stands in a dream favour: NPC 7835 of the client's
        /// templates, named 1149844 "Dispensador de favores". The guide calls him the same.
        /// </summary>
        /// <remarks>
        /// His template has the whole conversation: 59655 "La suerte te sonríe ... Déjame hacerte
        /// un favor", answered 81584 "Acepto el favor." or 81585 "No, gracias."; 59657 "La suerte
        /// ya te ha sonreído. No puedo hacerte otro favor por ahora."; and 59656, the favour for the
        /// owner of the dream, for a group this server does not make. The tree is in
        /// content/npcs/dialogues.json.
        ///
        /// Where he stands is INFERRED: on cell 232 facing 3, where the Rey Gob stands in the
        /// fountain maps (measured), whose layout the favour map has -- its centrepiece on the
        /// fountain's cell 313. No capture stands in a favour room.
        /// </remarks>
        public const int FavorNpc = 7835;
        public const int FavorNpcCell = 232;
        public const int FavorNpcOrientation = 3;

        /// <summary>His offer, "La suerte te sonríe...", and the line once it is taken.</summary>
        public const int FavorOfferMessage = 59655;
        public const int FavorGivenMessage = 59657;

        /// <summary>"Acepto el favor.", the reply that opens the three choices.</summary>
        public const int FavorAcceptReply = 81584;

        /// <summary>Le pone a una sala su grupo y su modificación.</summary>
        /// <remarks>
        /// El grupo se elige entre los que andan por el nivel del personaje, con una banda que se
        /// abre si no hay bastantes: los Sueños se juegan a partir del 50 y hay tramos del mundo
        /// donde no hay grupos de ese nivel exacto.
        /// </remarks>
        private static void Poblar(Sala sala, int nivel, int dificultad, bool keepReward = false)
        {
            var candidatos = new List<(int Id, long MapaId, int Casilla, int Nivel, string Miembros)>();

            for (int banda = 20; banda <= 200 && candidatos.Count == 0; banda += 40)
            {
                foreach (var g in _grupos!)
                {
                    if (Math.Abs(g.Nivel - nivel) <= banda) candidatos.Add(g);
                }
            }
            if (candidatos.Count == 0) return;

            (int Id, long MapaId, int Casilla, int Nivel, string Miembros) elegido;
            lock (_azar) elegido = candidatos[_azar.Next(candidatos.Count)];

            sala.Grupo = elegido.Id;
            sala.MapaId = elegido.MapaId;
            sala.Casilla = elegido.Casilla;

            sala.Miembros.Clear();
            sala.Miembros.AddRange(MiembrosDe(elegido.Miembros));

            // Y lo que regala la sala, de las nueve recompensas que ofrecen las salas medidas.
            if (keepReward && sala.Reward != null) return;
            lock (_azar)
            {
                sala.Reward = RoomRewards[_azar.Next(RoomRewards.Length)];
            }
        }

        internal static void OlvidarTodo()
        {
            _enCurso.Clear();
            _cuenta.Clear();
        }

        /// <summary>
        /// The Fin du rêve by difficulty, as the dofuspourlesnoobs guide gives it: the level of the
        /// first wave and what each one adds -- "Niveau 250 de base + 5 niveaux à chaque vague" in
        /// a Rêve, 275 and 10 in a Paradoxe, 300 and 15 in a Cauchemar -- the waves it takes to win,
        /// 1, 3 and 3, and the most there can be, 5, 15 and no end (0).
        /// </summary>
        public sealed record FinalRules(int BaseLevel, int Step, int MinWaves, int MaxWaves);

        public static FinalRules FinalRulesOf(int difficulty)
            => difficulty <= LastSueno ? new FinalRules(250, 5, 1, 5)
             : difficulty <= LastParadoja ? new FinalRules(275, 10, 3, 15)
             : new FinalRules(300, 15, 3, 0);

        /// <summary>
        /// A wave of the Fin du rêve: "une vague peut contenir des boss, des avis de recherche et
        /// des monstres". Bosses of the dungeons, one more every third wave up to three; a wanted
        /// monster -- a template with isBounty -- one wave in two; and the rest monsters of the
        /// world's high groups, four fighters in the first wave and one more every two, up to
        /// eight. Each at its top grade: the wave's level is put on them when the fight builds them.
        /// </summary>
        public static List<(int Monstruo, int Grado)> FinalWave(int wave)
        {
            Cargar();
            var bosses = DungeonManager.All.Values.SelectMany(d => d.Bosses).Distinct().ToList();
            var bounties = Bounties();
            var strong = _strong ??= _grupos!.Where(g => g.Nivel >= 150).SelectMany(g => MiembrosDe(g.Miembros))
                                             .Select(m => m.Monstruo).Distinct().ToList();

            int size = Math.Min(8, 4 + (wave - 1) / 2);
            int bossCount = Math.Min(3, 1 + (wave - 1) / 3);
            var members = new List<(int, int)>();
            lock (_azar)
            {
                for (int i = 0; i < bossCount && bosses.Count > 0; i++) members.Add((bosses[_azar.Next(bosses.Count)], TopGrade));
                if (wave % 2 == 1 && bounties.Count > 0) members.Add((bounties[_azar.Next(bounties.Count)], TopGrade));
                while (members.Count < size && strong.Count > 0) members.Add((strong[_azar.Next(strong.Count)], TopGrade));
            }
            return members;
        }

        /// <summary>A grade past any monster's: the fight's own clamp takes it to the highest there is.</summary>
        private const int TopGrade = 99;

        private static List<int>? _bounties;

        /// <summary>The monsters of the world's groups of level 150 and more, read once.</summary>
        private static List<int>? _strong;

        /// <summary>The wanted monsters: the templates with isBounty.</summary>
        private static List<int> Bounties()
        {
            if (_bounties != null) return _bounties;
            var found = new List<int>();
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Id, Data FROM MonsterTemplates;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    if (doc.RootElement.TryGetProperty("isBounty", out var b) && b.ValueKind == JsonValueKind.Number && b.GetInt32() != 0)
                        found.Add(reader.GetInt32(0));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sueños] The wanted monsters could not be read: {ex.Message}");
            }
            _bounties = found;
            return found;
        }

        /// <summary>
        /// A fighter brought to a level: its life and its characteristics by the ratio of the
        /// levels, its points and resistances as they are. The guide says the dream's monsters
        /// grow with the dreamer and the end's are 250 and more; the grades stop around 200, so
        /// this is how they get there. Not the game's own scaling, which is not known.
        /// </summary>
        public static void ScaleTo(Fighter fighter, int level)
        {
            if (fighter.Level <= 0 || level <= fighter.Level) { fighter.Level = Math.Max(fighter.Level, level); return; }
            double ratio = (double)level / fighter.Level;
            fighter.MaxHP = (int)Math.Round(fighter.MaxHP * ratio);
            fighter.CurrentHP = fighter.MaxHP;
            fighter.Strength = (int)Math.Round(fighter.Strength * ratio);
            fighter.Intelligence = (int)Math.Round(fighter.Intelligence * ratio);
            fighter.Chance = (int)Math.Round(fighter.Chance * ratio);
            fighter.Agility = (int)Math.Round(fighter.Agility * ratio);
            fighter.Initiative = (int)Math.Round(fighter.Initiative * ratio);
            fighter.Level = level;
        }

        /// <summary>For tests: a dream in memory as if it had been read from the base.</summary>
        internal static void Remember(Sueno dream) => _enCurso[dream.CharacterId] = dream;
    }
}
