using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What has to be known about a summoned creature: where its look comes from, its life, its
    /// resistances and —the important part— the spell it behaves with.
    ///
    /// Everything comes from <c>MonsterTemplates</c>. The whole chain is data, without a single summon
    /// written by hand:
    ///
    ///   the spell brings a 181 effect "Invoca: #1" with the TEMPLATE in its diceNum
    ///   -> MonsterTemplates.Data.grades[grade] gives life, AP, MP and resistances
    ///   -> that grade brings a startingSpellId, which is a SpellLevels.Id
    ///   -> that level belongs to a spell, and THAT is the one governing the summon
    ///
    /// For the Cra's Baliza de Supervivencia: effect 181 with diceNum 8348, template 8348
    /// grade 3 has startingSpellId 85285, which is spell 32477 grade 1, and its effects are
    /// 792 hooks —"at the start of my turn cast my grade 2"— plus a 141 that undoes it. It is the
    /// same machinery as the attitudes the dofus give away.
    /// </summary>
    public sealed class Summon
    {
        public int Plantilla { get; init; }
        public int Grado { get; init; }
        public int Nivel { get; init; }

        /// <summary>The look string, and which template it was taken from.</summary>
        public string Look { get; init; } = "";
        public int PlantillaDelAspecto { get; init; }

        public int Vida { get; init; }

        /// <summary>
        /// The life that does NOT scale with the summoner's level: the monster's own grade's.
        /// A summoned monster brings it here with the bonus at zero; a player's beacon the other way round.
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

        /// <summary>The spell that governs the creature, and at which grade.</summary>
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
        /// The scale a summon's life grows with according to the summoner's level.
        ///
        /// Measured over the 18 summons there are in ALL the captures, from six different
        /// casters: the life is always the grade's <c>bonusCharacteristics.lifePoints</c> times a
        /// factor that only depends on the summoner —17 of the 18 give exactly 10.5 and the remaining one,
        /// from another player, gives 8.75—. With <c>(level + 10) / 20</c> both come out with whole
        /// levels: 200 and 165.
        ///
        /// Checked on: beacon 8348 grade 3 with bonus 100 -> 1050; beacon 8347 grade 2 with
        /// bonus 200 -> 2100; 262 with 60 -> 630; 246 with 30 -> 315; 7220 with 70 -> 735.
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

        // ─── How long it lives ──────────────────────────────────────────────────
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

                // LIFE IS TWO NUMBERS, and reading only one left half the summons at zero.
                //
                // This whole pipeline was written measuring PLAYER summons —the Cra's
                // beacons, the Steamer's turtles, the Enutrof's chests— and those carry their life in
                // «bonusCharacteristics.lifePoints», which scales with the summoner's level.
                //
                // A summoned MONSTER carries it in the grade's «lifePoints», plain, and has the
                // bonus at zero. Nawidad's Minotobola's «Regalo animado» has 1000, 1500 and
                // 2000 depending on the grade, and bonus 0: reading only the bonus it came out with ZERO life.
                //
                // And from there everything else fell in cascade, because IsAlive is «CurrentHP > 0»: the
                // balloon showed 0/0, did not enter the turn order, did not appear in the carousel, did not
                // take up a cell —one could walk through it— and did nothing.
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

                // The spell it behaves with: the startingSpellId is a SpellLevels.Id.
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
                    // The life is left raw: the summoner knows his level and scales the part that
                    // scales. The fixed one scales with nobody.
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
        /// The look. A template can refer to another: the beacon's says <c>{8152}</c>, which
        /// is not a look string but "look at 8152's look". The trail is followed a
        /// few times in case there is more than one hop.
        /// </summary>
        private static (string Look, int DeQuien) LookDe(SqliteConnection conexion, int plantilla, int vueltas)
        {
            if (vueltas > 4) return ("", plantilla);
            string look = TextoDe(conexion, "SELECT Look FROM MonsterTemplates WHERE Id = $id;", plantilla);
            if (string.IsNullOrEmpty(look)) return ("", plantilla);

            // A REFERRAL IS NOT THE SAME AS A REAL LOOK, and telling them apart is what all this is about.
            //
            // The string is «{bone|skins|colours|scale}». There are three forms and only two of them
            // send to another template:
            //
            //   {8152}                              bare referral           -> follow
            //   {446|||120}                         referral with scale     -> follow
            //   {1|91,5239,4977|1=#FFFFFF,…|52}     the real look           -> stop here
            //
            // What separates them are the skins and the colours: a referral brings none. The
            // previous version kept the first number whatever came after it, and
            // that broke the Cra's beacon. Its trail is 8348 -> «{8152}» -> «{1|91,…}», and on
            // getting there the 1 was read as if it were another template and followed to template 1, which is
            // some other creature. The client was left without a look to draw and drew a
            // blue square.
            //
            // Measured in «ocra-baliza de supervivencia»: its jwe sends «f3{f2=3, f3=8152}», that is
            // one has to stop at 8152 and it is its string that counts.
            // And HERE IT STOPS, without following the trail. What goes in the summon packet is THE
            // NUMBER INSIDE THE BRACES, not the look it points to: measured in the capture
            // of the Baliza de Supervivencia, whose template 8348 carries «{8152}» and whose jwe sends
            // f3{f2=8152}. The client resolves the rest.
            //
            // Following it was what turned the Sismobomba into a ghost. Its template 5161
            // carries «{2865}», and it so happens that 2865 IS another template -- the Ventozador --
            // so the trail ended at its look and the client drew a Ventozador. The
            // other three bombs were saved by a miracle: their numbers -- 1561, 1562, 1563 -- are not
            // templates of anything, the lookup found no row and kept the number.
            if (EsReenvio(look, out int otra) && otra != plantilla)
            {
                // TWO DIFFERENT THINGS THAT WERE RESOLVED WITH THE SAME NUMBER, and that was the bug.
                //
                // The look STRING follows the trail to the end, because whoever draws it
                // -- the launcher, the studio -- needs real bones, skins and colours. That
                // is what the ouginak's beast form uses.
                //
                // But what goes in the summon PACKET is the number inside the
                // braces, without following it: measured on the Baliza de Supervivencia, whose template 8348
                // carries «{8152}» and whose jwe sends f3{f2=8152}.
                //
                // Following it for the packet too turned the Sismobomba into a ghost: its
                // «{2865}» points to another real template -- the Ventozador -- and the client
                // ended up drawing a Ventozador. The other three bombs were saved by a miracle,
                // because their numbers -- 1561, 1562, 1563 -- are not templates of anything.
                var (cadena, _) = LookDe(conexion, otra, vueltas + 1);
                return (cadena, otra);
            }
            return (look, plantilla);
        }

        /// <summary>Whether this look string sends to another template, and which one.</summary>
        /// <remarks>
        /// Separate so it can be tested with the base's real strings without opening it: it is a
        /// three-case rule and it is where the beacon broke.
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
