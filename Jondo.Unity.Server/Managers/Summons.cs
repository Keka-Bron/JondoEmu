using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Lo que hay que saber de un bicho invocado: de dónde sale su aspecto, su vida, sus
    /// resistencias y —lo importante— el hechizo con el que se porta.
    ///
    /// Todo sale de <c>MonsterTemplates</c>. La cadena entera es de datos, sin una sola invocación
    /// escrita a mano:
    ///
    ///   el hechizo trae un efecto 181 "Invoca: #1" con la PLANTILLA en su diceNum
    ///   -> MonsterTemplates.Data.grades[grado] da vida, PA, PM y resistencias
    ///   -> ese grado trae un startingSpellId, que es un SpellLevels.Id
    ///   -> ese nivel pertenece a un hechizo, y ESE es el que gobierna al invocado
    ///
    /// Para la Baliza de Supervivencia del Ocra: efecto 181 con diceNum 8348, la plantilla 8348
    /// grado 3 tiene startingSpellId 85285, que es el hechizo 32477 grado 1, y sus efectos son
    /// enganches 792 —"al empezar mi turno lanza mi grado 2"— más un 141 que la deshace. Es la
    /// misma maquinaria de las actitudes que regalan los dofus.
    /// </summary>
    public sealed class Summon
    {
        public int Plantilla { get; init; }
        public int Grado { get; init; }
        public int Nivel { get; init; }

        /// <summary>La cadena de aspecto, y de qué plantilla se ha sacado.</summary>
        public string Look { get; init; } = "";
        public int PlantillaDelAspecto { get; init; }

        public int Vida { get; init; }

        /// <summary>
        /// La vida que NO escala con el nivel del que invoca: la del propio grado del monstruo.
        /// Un monstruo invocado la trae aqui y con el bonus a cero; una baliza de jugador al reves.
        /// </summary>
        public int VidaFija { get; init; }
        public int PuntosDeAccion { get; init; }
        public int PuntosDeMovimiento { get; init; }

        public int ResistenciaNeutral { get; init; }
        public int ResistenciaTierra { get; init; }
        public int ResistenciaFuego { get; init; }
        public int ResistenciaAgua { get; init; }
        public int ResistenciaAire { get; init; }

        /// <summary>
        /// The grade's four characteristics (<c>strength</c>, <c>intelligence</c>, <c>chance</c>,
        /// <c>agility</c>) and its <c>bonusCharacteristics</c> ones, and the best of its bonus
        /// damages: what <see cref="CaracteristicaDelInvocado"/> and
        /// <see cref="PotenciaDelInvocado"/> make of them at the summoner's level.
        /// </summary>
        public int Fuerza { get; init; }
        public int Inteligencia { get; init; }
        public int Suerte { get; init; }
        public int Agilidad { get; init; }
        public int BonusFuerza { get; init; }
        public int BonusInteligencia { get; init; }
        public int BonusSuerte { get; init; }
        public int BonusAgilidad { get; init; }
        public int BonusDeDanos { get; init; }

        /// <summary>El hechizo que gobierna al bicho, y en qué grado.</summary>
        public int HechizoPropio { get; init; }
        public int GradoDelHechizoPropio { get; init; }

        /// <summary>
        /// Whether it gets a turn of its own. Bit 6 of the template's <c>m_flags</c>, read
        /// against every case the captures settle: the Tymobot, the Bomba Ambulante, the
        /// Megabomba and the Baliza de Supervivencia carry it and play; the four bombs and the
        /// Baliza Táctica do not carry it and never appear in the carousel. Every ordinary
        /// monster carries it; what does not is the scenery -- trees, totems, pillars, the
        /// Dofus Ébano, the Bambú.
        /// </summary>
        public bool Juega { get; init; } = true;

        /// <summary>
        /// Whether it tackles: the CanTackle bit of the template's <c>m_flags</c>, see
        /// <see cref="MonsterFlags"/>. The Osamodas' summon 5192 carries it, and in three
        /// captures its tackle of 60 holds whoever walks away from it.
        /// </summary>
        public bool AllowsTackle { get; init; } = true;

        /// <summary>
        /// The spells it casts, with the grade its own grade opens: the template's <c>spells</c>
        /// against its <c>spellGrades</c> ("1,1;2,2;3,3;3,4;3,5" is spell grade 1 at monster
        /// grade 1, 2 at 2, 3 from 3 on). They go to whoever controls it as a jyy of its own:
        /// the Tymobot at grade 3 gets 13451 g3, 13452 g3, 30938 g1 and 13453 g3 in the capture.
        /// </summary>
        public IReadOnlyList<(int Spell, int Grade)> Hechizos { get; init; } = Array.Empty<(int, int)>();

        /// <summary>
        /// How much of the caster's summon capacity this creature occupies, from the template's
        /// own <c>summonCost</c>. Not every summon costs one: of the 5,134 templates in
        /// world.db, 4,640 cost 1, <b>485 cost ZERO</b>, four cost 2 and five cost 3.
        ///
        /// The zero ones are why the Rogue could only place a single bomb. Explobomba (3112),
        /// Tornabomba (3113) and Bomba de agua (3114) all read 0, so three of them fit next to a
        /// real summon; the Osamodas' Gorditofu reads 2 and his Crujintesco 3, so one of those
        /// fills a capacity of three on its own.
        /// </summary>
        public int SummonCost { get; init; } = 1;
    }

    public static class Summons
    {
        private static readonly Dictionary<(int, int), Summon> _cache
            = new Dictionary<(int, int), Summon>();
        private static readonly object _candado = new object();

        /// <summary>
        /// La escala con la que crece la vida de un invocado según el nivel del que lo invoca.
        ///
        /// Medido sobre las 18 invocaciones que hay en TODAS las capturas, de seis lanzadores
        /// distintos: la vida es siempre <c>bonusCharacteristics.lifePoints</c> del grado por un
        /// factor que sólo depende del que invoca —17 de las 18 dan 10,5 exacto y la restante,
        /// de otro jugador, da 8,75—. Con <c>(nivel + 10) / 20</c> salen los dos con niveles
        /// enteros: 200 y 165.
        ///
        /// Comprobado en: baliza 8348 grado 3 con bonus 100 -> 1050; baliza 8347 grado 2 con
        /// bonus 200 -> 2100; 262 con 60 -> 630; 246 con 30 -> 315; 7220 con 70 -> 735.
        /// </summary>
        public static int VidaDelInvocado(int bonusDeVida, int nivelDelInvocador, int vidaFija = 0)
            => vidaFija + (int)(bonusDeVida * (Math.Max(1, nivelDelInvocador) + 10) / 20.0);

        /// <summary>
        /// A summon's characteristic at its summoner's level: the grade's own, times one plus a
        /// hundredth of the level, and its bonus on top as it is.
        /// </summary>
        /// <remarks>
        /// Measured on the sheets the real server sends with every summon (the jwe 181), summoners
        /// of level 200: the grade's 300 comes out 900 -- Aniripsa's 7370, Hipermago's 5129, the
        /// Ocra's 2630 --, 220 comes out 660 (246, 262), 400 and 200 come out 1,200 and 600 (the
        /// Sacrógrito's sword 434), 135 comes out 405 (5845), 350 and 100 come out 1,050 and 300
        /// (5898), 250 comes out 750 (5840). The Osamodas' animals carry theirs in the bonus only
        /// and it comes out as it is: the Tofu's 50 of agility is 50, the 75 and 100 of the
        /// others 75 and 100. They were all zero here, and a JondoBot Osamodas' Tofu pecked for ten.
        /// </remarks>
        public static int CaracteristicaDelInvocado(int propia, int bonus, int nivelDelInvocador)
            => propia * (100 + Math.Max(1, nivelDelInvocador)) / 100 + bonus;

        /// <summary>
        /// A summon's power: three fifths of the grade's bonus damage.
        /// </summary>
        /// <remarks>
        /// The Osamodas' animals, on the same sheets: 50 of air damage make 30 of power (the Tofu
        /// 8070), 75 make 45 (8071), 100 make 60 (8078) -- the twelve of them alike. The
        /// Aniripsa's flask (7371, 100 of earth damage) shows none in its one capture, of another
        /// player: it goes by what the animals say.
        /// </remarks>
        public static int PotenciaDelInvocado(int bonusDeDanos) => bonusDeDanos * 3 / 5;

        // ─── Cuánto vive ────────────────────────────────────────────────────────
        //
        // Nothing here any more: how long a summon lives is the DELAY of the 141 its own spell
        // hangs on it at birth -- two rounds for the Baliza de Supervivencia, three for the
        // Táctica, the numbers a hand-measured table used to carry -- and the engine's waiting
        // rows collect it through the ordinary death. See EffectEngine.Pendiente.

        public static Summon De(int plantilla, int grado)
        {
            var clave = (plantilla, Math.Max(1, grado));
            lock (_candado)
            {
                if (_cache.TryGetValue(clave, out var ya)) return ya;
                var leido = Leer(clave.Item1, clave.Item2);
                _cache[clave] = leido;
                return leido;
            }
        }

        private static Summon Leer(int plantilla, int grado)
        {
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();

                var (look, deQuien) = LookDe(conexion, plantilla, 0);
                string datos = TextoDe(conexion, "SELECT Data FROM MonsterTemplates WHERE Id = $id;", plantilla);
                if (string.IsNullOrEmpty(datos)) return null;

                using var doc = JsonDocument.Parse(datos);
                if (!doc.RootElement.TryGetProperty("grades", out var grados) ||
                    !grados.TryGetProperty("Array", out var lista)) return null;

                JsonElement? elegido = null;
                foreach (var g in lista.EnumerateArray())
                {
                    if (Entero(g, "grade") == grado) { elegido = g; break; }
                    elegido ??= g;
                }
                if (elegido == null) return null;
                var gr = elegido.Value;

                // LA VIDA SON DOS NÚMEROS, y leer sólo uno dejaba a media invocación en cero.
                //
                // Toda esta tubería se escribió midiendo invocaciones de JUGADOR —las balizas del
                // Ocra, las tortugas del Steamer, los cofres del Anutrof— y ésas llevan la vida en
                // «bonusCharacteristics.lifePoints», que escala con el nivel del que invoca.
                //
                // Un MONSTRUO invocado la lleva en el «lifePoints» del grado, a secas, y tiene el
                // bonus a cero. El «Regalo animado» del Minotobola de Nawidad tiene 1000, 1500 y
                // 2000 según el grado, y bonus 0: leyendo sólo el bonus salía con CERO de vida.
                //
                // Y de ahí caía todo lo demás en cascada, porque IsAlive es «CurrentHP > 0»: el
                // globo pintaba 0/0, no entraba en el orden de turnos, no salía en el carrusel, no
                // ocupaba casilla —se podía andar a través de él— y no hacía nada.
                int vidaFija = Math.Max(0, Entero(gr, "lifePoints"));
                int bonusVida = 0, bonusFuerza = 0, bonusInteligencia = 0, bonusSuerte = 0, bonusAgilidad = 0, bonusDanos = 0;
                if (gr.TryGetProperty("bonusCharacteristics", out var bonus))
                {
                    bonusVida = Entero(bonus, "lifePoints");
                    bonusFuerza = Entero(bonus, "strength");
                    bonusInteligencia = Entero(bonus, "intelligence");
                    bonusSuerte = Entero(bonus, "chance");
                    bonusAgilidad = Entero(bonus, "agility");
                    bonusDanos = Math.Max(Math.Max(Entero(bonus, "bonusEarthDamage"), Entero(bonus, "bonusFireDamage")),
                                          Math.Max(Entero(bonus, "bonusWaterDamage"), Entero(bonus, "bonusAirDamage")));
                }

                // El hechizo con el que se porta: el startingSpellId es un SpellLevels.Id.
                int nivelDelHechizo = Entero(gr, "startingSpellId");
                var (hechizo, gradoDelHechizo) = HechizoDe(conexion, nivelDelHechizo);

                bool juega = (Entero(doc.RootElement, "m_flags") & CanPlayFlag) != 0;
                bool tackles = (Entero(doc.RootElement, "m_flags") & MonsterFlags.CanTackle) != 0;
                var hechizos = HechizosDe(doc.RootElement, grado);

                return new Summon
                {
                    Plantilla = plantilla,
                    Grado = grado,
                    Nivel = Math.Max(1, Entero(gr, "level")),
                    Look = look,
                    PlantillaDelAspecto = deQuien,
                    // La vida se deja en bruto: quien invoca sabe su nivel y escala la parte que
                    // escala. La fija no escala con nadie.
                    Vida = bonusVida,
                    VidaFija = vidaFija,
                    PuntosDeAccion = Math.Max(0, Entero(gr, "actionPoints")),
                    PuntosDeMovimiento = Math.Max(0, Entero(gr, "movementPoints")),
                    ResistenciaNeutral = Entero(gr, "neutralResistance"),
                    ResistenciaTierra = Entero(gr, "earthResistance"),
                    ResistenciaFuego = Entero(gr, "fireResistance"),
                    ResistenciaAgua = Entero(gr, "waterResistance"),
                    ResistenciaAire = Entero(gr, "airResistance"),
                    Fuerza = Math.Max(0, Entero(gr, "strength")),
                    Inteligencia = Math.Max(0, Entero(gr, "intelligence")),
                    Suerte = Math.Max(0, Entero(gr, "chance")),
                    Agilidad = Math.Max(0, Entero(gr, "agility")),
                    BonusFuerza = Math.Max(0, bonusFuerza),
                    BonusInteligencia = Math.Max(0, bonusInteligencia),
                    BonusSuerte = Math.Max(0, bonusSuerte),
                    BonusAgilidad = Math.Max(0, bonusAgilidad),
                    BonusDeDanos = Math.Max(0, bonusDanos),
                    HechizoPropio = hechizo,
                    GradoDelHechizoPropio = gradoDelHechizo,
                    Juega = juega,
                    AllowsTackle = tackles,
                    Hechizos = hechizos,
                    // Absent means one, not free: a template with no summonCost at all is an
                    // ordinary summon. Every template in world.db carries the key, so this
                    // default only guards against a future dump that drops it.
                    SummonCost = doc.RootElement.TryGetProperty("summonCost", out _)
                        ? Math.Max(0, Entero(doc.RootElement, "summonCost"))
                        : 1,
                };
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Summons] No se pudo leer la plantilla {plantilla} " +
                                 $"grado {grado}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bit 6 of a template's m_flags: it plays a turn. See <see cref="Summon.Juega"/>.</summary>
        public const int CanPlayFlag = 64;

        /// <summary>
        /// The template's spells at the grade this summon's grade opens. "spells" is the list of
        /// spell ids and "spellGrades" one string per spell, "s,g;s,g;..." pairs of spell grade
        /// and monster grade. A spell with no usable pair is cast at grade 1.
        /// </summary>
        private static IReadOnlyList<(int Spell, int Grade)> HechizosDe(JsonElement raiz, int grado)
        {
            var salida = new List<(int, int)>();
            if (!raiz.TryGetProperty("spells", out var hechizos) ||
                !hechizos.TryGetProperty("Array", out var lista)) return salida;

            var grados = new List<string>();
            if (raiz.TryGetProperty("spellGrades", out var g) && g.TryGetProperty("Array", out var gl))
            {
                foreach (var x in gl.EnumerateArray()) grados.Add(x.GetString() ?? "");
            }

            int i = 0;
            foreach (var h in lista.EnumerateArray())
            {
                if (!h.TryGetInt32(out int hechizo)) { i++; continue; }
                int gradoDelHechizo = 1;
                if (i < grados.Count)
                {
                    foreach (string par in grados[i].Split(';'))
                    {
                        var partes = par.Split(',');
                        if (partes.Length == 2 && int.TryParse(partes[0], out int sg) &&
                            int.TryParse(partes[1], out int mg) && mg == grado)
                        {
                            gradoDelHechizo = Math.Max(1, sg);
                        }
                    }
                }
                salida.Add((hechizo, gradoDelHechizo));
                i++;
            }
            return salida;
        }

        /// <summary>
        /// El aspecto. Una plantilla puede remitir a otra: la de la baliza pone <c>{8152}</c>, que
        /// no es una cadena de aspecto sino "mírale el aspecto a la 8152". Se sigue el rastro unas
        /// pocas veces por si hay más de un salto.
        /// </summary>
        private static (string Look, int DeQuien) LookDe(SqliteConnection conexion, int plantilla, int vueltas)
        {
            if (vueltas > 4) return ("", plantilla);
            string look = TextoDe(conexion, "SELECT Look FROM MonsterTemplates WHERE Id = $id;", plantilla);
            if (string.IsNullOrEmpty(look)) return ("", plantilla);

            // UN REENVÍO NO ES LO MISMO QUE UN ASPECTO DE VERDAD, y distinguirlos es todo esto.
            //
            // La cadena es «{hueso|pieles|colores|escala}». Hay tres formas y sólo dos de ellas
            // mandan a otra plantilla:
            //
            //   {8152}                              reenvío pelado          -> seguir
            //   {446|||120}                         reenvío con escala      -> seguir
            //   {1|91,5239,4977|1=#FFFFFF,…|52}     el aspecto de verdad    -> parar aquí
            //
            // Lo que los separa son las pieles y los colores: un reenvío no trae ninguno. La
            // versión anterior se quedaba con el primer número hubiera lo que hubiera detrás, y
            // eso rompió la baliza del Ocra. Su rastro es 8348 -> «{8152}» -> «{1|91,…}», y al
            // llegar ahí se leía el 1 como si fuera otra plantilla y se seguía hasta la 1, que es
            // otro bicho cualquiera. El cliente se quedaba sin aspecto que dibujar y pintaba un
            // cuadrado azul.
            //
            // Medido en «ocra-baliza de supervivencia»: su jwe manda «f3{f2=3, f3=8152}», o sea
            // que hay que pararse en la 8152 y es su cadena la que vale.
            // Y AQUI SE PARA, sin seguir el rastro. Lo que va en el paquete de invocacion es EL
            // NUMERO DE DENTRO DE LAS LLAVES, no el aspecto al que apunte: medido en la captura
            // de la Baliza de Supervivencia, cuya plantilla 8348 lleva «{8152}» y cuyo jwe manda
            // f3{f2=8152}. El cliente resuelve el resto.
            //
            // Seguirlo era lo que convertia a la Sismobomba en un fantasma. Su plantilla 5161
            // lleva «{2865}», y da la casualidad de que 2865 ES otra plantilla -- el Ventozador --
            // asi que el rastro acababa en su aspecto y el cliente pintaba un Ventozador. Las
            // otras tres bombas se salvaban de milagro: sus numeros -- 1561, 1562, 1563 -- no son
            // plantillas de nada, la busqueda no encontraba fila y se quedaba con el numero.
            if (EsReenvio(look, out int otra) && otra != plantilla)
            {
                // DOS COSAS DISTINTAS QUE SE RESOLVIAN CON EL MISMO NUMERO, y ahi estaba el fallo.
                //
                // La CADENA de aspecto sigue el rastro hasta el final, porque quien la dibuja
                // -- el lanzador, el estudio -- necesita huesos, pieles y colores de verdad. Eso
                // es lo que usa la forma bestial del ouginak.
                //
                // Pero lo que va en el PAQUETE de invocacion es el numero de dentro de las
                // llaves, sin seguirlo: medido en la Baliza de Supervivencia, cuya plantilla 8348
                // lleva «{8152}» y cuyo jwe manda f3{f2=8152}.
                //
                // Seguirlo tambien para el paquete convertia a la Sismobomba en un fantasma: su
                // «{2865}» apunta a otra plantilla de verdad -- el Ventozador -- y el cliente
                // acababa pintando un Ventozador. Las otras tres bombas se salvaban de milagro,
                // porque sus numeros -- 1561, 1562, 1563 -- no son plantillas de nada.
                var (cadena, _) = LookDe(conexion, otra, vueltas + 1);
                return (cadena, otra);
            }
            return (look, plantilla);
        }

        /// <summary>Si esta cadena de aspecto manda a otra plantilla, y a cuál.</summary>
        /// <remarks>
        /// Aparte para poder probarla con las cadenas de verdad de la base sin abrirla: es una
        /// regla de tres casos y es donde se rompió la baliza.
        /// </remarks>
        internal static bool EsReenvio(string look, out int hacia)
        {
            hacia = 0;
            if (string.IsNullOrWhiteSpace(look)) return false;

            string[] partes = look.Trim().Trim('{', '}').Split('|');
            bool reenvio = partes.Length == 1
                           || (partes.Length >= 3 && partes[1].Length == 0 && partes[2].Length == 0);

            return reenvio && int.TryParse(partes[0], out hacia) && hacia > 0;
        }

        private static (int Hechizo, int Grado) HechizoDe(SqliteConnection conexion, int nivelId)
        {
            if (nivelId <= 0) return (0, 0);
            var orden = conexion.CreateCommand();
            orden.CommandText = "SELECT SpellId, Grade FROM SpellLevels WHERE Id = $id LIMIT 1;";
            orden.Parameters.AddWithValue("$id", nivelId);
            using var lector = orden.ExecuteReader();
            if (lector.Read()) return ((int)lector.GetInt64(0), (int)lector.GetInt64(1));
            return (0, 0);
        }

        private static string TextoDe(SqliteConnection conexion, string sql, int id)
        {
            var orden = conexion.CreateCommand();
            orden.CommandText = sql;
            orden.Parameters.AddWithValue("$id", id);
            using var lector = orden.ExecuteReader();
            return lector.Read() && !lector.IsDBNull(0) ? lector.GetString(0) : "";
        }

        private static int Entero(JsonElement e, string nombre)
            => e.TryGetProperty(nombre, out var v) && v.TryGetInt32(out int n) ? n : 0;
    }
}
