using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// An entry of a spell's EffectsJson, just as it comes, uninterpreted.
    ///
    /// The emulator already read this JSON, but kept only three things —the damage, the push and the
    /// characteristics— and threw the rest away: the trigger, the target mask and the
    /// effect's identifier. Without those three nothing of what the dofus and the
    /// boosts do can be done, so the whole entry is kept here.
    /// </summary>
    public sealed class SpellEffect
    {
        public int EffectId { get; init; }

        /// <summary>A copy of the row, for changing what firing or arming it reads -- its delay, its mask.</summary>
        internal SpellEffect Copia() => (SpellEffect)MemberwiseClone();

        /// <summary>
        /// The same row under another effect number, everything else kept: a share of a blow
        /// goes out as the one of its family in the blow's element (1223 as 1227).
        /// </summary>
        internal SpellEffect ComoEfecto(int effectId) => new SpellEffect
        {
            EffectId = effectId,
            EffectUid = EffectUid,
            Value = Value,
            DiceNum = DiceNum,
            DiceSide = DiceSide,
            Duration = Duration,
            Delay = Delay,
            Element = Element,
            Dispellable = Dispellable,
            Triggers = Triggers,
            TargetMask = TargetMask,
            CeldasFijas = CeldasFijas,
            Forma = Forma,
            Tamano = Tamano,
            TamanoMinimo = TamanoMinimo,
            ParaEnElObjetivo = ParaEnElObjetivo,
            PasoDeCaida = PasoDeCaida,
            TopeDeCaida = TopeDeCaida,
            MaxStack = MaxStack,
            Probabilidad = Probabilidad,
            Sorteo = Sorteo,
            Flags = Flags,
        };
        public int EffectUid { get; init; }
        public int Value { get; init; }
        public int DiceNum { get; init; }
        public int DiceSide { get; init; }
        public int Duration { get; init; }

        /// <summary>
        /// The DELAY in turns: the effect does not start on casting, but that many rounds later.
        ///
        /// This catalogue key was not read anywhere in the emulator, and that is why the Flecha
        /// Castigadora was broken: its delayed effects were applied on the spot and dropped on the
        /// next round, so since the spell can only be cast once per turn, the
        /// bonus was born and died within the same turn and was of absolutely no use.
        ///
        /// Measured against spells whose text says so: Precipitación carries delay 1 —«en el turno
        /// siguiente»— and Palabra Secreta delay 2 —«dentro de 2 turnos»—.
        /// </summary>
        public int Delay { get; set; }

        public int Element { get; init; }

        /// <summary>Whether it can be dispelled. It goes to the client minus one, which is how it was measured.</summary>
        public int Dispellable { get; init; }

        /// <summary>When it fires: "I" on the spot, "TB" at the start of the turn, "TE" at its end,
        /// "DBE" when he is hit... An effect can bring several separated by bars.</summary>
        public string Triggers { get; init; } = "I";

        /// <summary>Who it goes to: "C" the caster, "a"/"A" the opponents, and with "e519" or
        /// "E519" attached, only if he does NOT have or DOES have that state.</summary>
        public string TargetMask { get; set; } = "";

        /// <summary>
        /// The SHAPE of the zone, which is a letter stored as its code: 'P' a point, 'C' a
        /// circle, 'X' a cross, 'L' a line... and <see cref="Tamano"/> is its radius or its length.
        ///
        /// Without this, a zone spell only touched whoever was right on the targeted cell:
        /// Ojo de Topo showed the preview over the two pious and then did nothing
        /// to the second.
        /// </summary>
        /// <summary>The cells a ';' zone names, map cells; empty for every other shape.</summary>
        public IReadOnlyList<int> CeldasFijas { get; init; } = Array.Empty<int>();

        public int Forma { get; init; } = 'P';
        public int Tamano { get; init; } = 1;

        /// <summary>
        /// The zone's inner edge (<c>param2</c>): cells nearer than this to the centre are left
        /// out. Patada's three rings are X3/3, X2/2 and X1/1 -- the push grows as the ring
        /// shrinks -- and Imantación's X6/1 is a cross without its centre, which is where the
        /// bomb it is cast on stands and "no afecta al lanzador". See Zone.Casillas.
        /// </summary>
        public int TamanoMinimo { get; init; }

        /// <summary>Whether the zone is cut on reaching the target, for lines.</summary>
        public bool ParaEnElObjetivo { get; init; }

        /// <summary>
        /// How much damage it loses for each cell one is away from the zone's centre, as a
        /// percentage, and how many cells at most are counted.
        ///
        /// It comes from the <c>zoneDescr</c> and goes PER SPELL: sixteen of the Cra's zone effects
        /// carry ten per cent with a cap of four steps, and another seven carry zero, that is
        /// they hit the same across their whole reach —Diamantes Destructores is one of those—.
        /// </summary>
        public int PasoDeCaida { get; init; }
        public int TopeDeCaida { get; init; }

        /// <summary>
        /// Maximum number of identical rows the spell level allows to coexist. Values at or below
        /// one use refresh semantics; a larger value is a real stack limit.
        /// </summary>
        public int MaxStack { get; init; }

        /// <summary>
        /// The probability of this effect coming up, as a percentage, and the draw it
        /// belongs to.
        ///
        /// It is what makes Invocación de Arakna bring out an ordinary Arakna eighty per
        /// cent of the time and a greater Arakna twenty: they are TWO 181 effects, one with
        /// template 246 and a random of 80, and another with 2630 and a random of 20. Without looking at this
        /// both came out at once.
        /// </summary>
        public double Probabilidad { get; init; }
        public int Sorteo { get; init; }

        /// <summary>
        /// The client's <c>EffectInstanceFlags</c> of the row: 1 visible in the tooltip, 2 in
        /// the buff panel, 4 in the fight log, 8 on the terrain, 16 for the client only.
        /// </summary>
        public int Flags { get; init; }

        /// <summary>The bit of <see cref="Flags"/> that marks a row the server never runs.</summary>
        public const int ForClientOnlyFlag = 16;

        /// <summary>
        /// A row that is the SHEET'S COPY of something the spell really does elsewhere, and
        /// that the server never runs.
        /// </summary>
        /// <remarks>
        /// The client's enum names the bit ForClientOnly, and the catalogue is written on it:
        /// Furor carries a "+20 de daños básicos" with the bit next to a 1160 that casts 28604,
        /// where the real +20 lives; Vitalidad its two "+N% vitalidad" next to the 1160s that
        /// cast 25215; Manticolmillo its "+15 huida" next to the 1160 that casts 24012 on each
        /// enemy; Virtud its shield and its "-50 potencia" next to 29723. In the Furor capture
        /// the rows that go out are 28604's alone -- the state, the +20, the hooked 1160 --
        /// and never 13156's; in the Vitalidad capture only 25215's +230; in the Virtud
        /// capture only 29723's. Run, the copy doubled every one of them: Furor gave +60.
        /// Remisión's push "under DM" is the same thing, which is what the engine had already
        /// read off its capture case by case.
        /// </remarks>
        public bool ForClientOnly => (Flags & ForClientOnlyFlag) != 0;

        public IEnumerable<string> Disparadores()
        {
            if (string.IsNullOrEmpty(Triggers)) { yield return "I"; yield break; }
            foreach (var t in Triggers.Split('|'))
            {
                string limpio = t.Trim();
                if (limpio.Length > 0) yield return limpio;
            }
        }
    }

    /// <summary>
    /// A spell's effects, at one grade, read from SpellLevels.
    ///
    /// It is cached by (spell, grade) because during a fight they are asked for many times and the table does not
    /// change while the server is up.
    /// </summary>
    public static class SpellEffects
    {
        private static readonly Dictionary<(int, int), List<SpellEffect>> _cache
            = new Dictionary<(int, int), List<SpellEffect>>();

        private static readonly Dictionary<(int, int), List<SpellEffect>> _criticos
            = new Dictionary<(int, int), List<SpellEffect>>();

        private static readonly object _candado = new object();

        public static IReadOnlyList<SpellEffect> De(int hechizo, int grado)
            => Leer(hechizo, grado).Normales;

        public static IReadOnlyList<SpellEffect> Criticos(int hechizo, int grado)
            => Leer(hechizo, grado).Criticos;

        private static (List<SpellEffect> Normales, List<SpellEffect> Criticos)
            Leer(int hechizo, int grado)
        {
            var clave = (hechizo, Math.Max(1, grado));
            lock (_candado)
            {
                if (_cache.TryGetValue(clave, out var ya)) return (ya, _criticos[clave]);

                var normales = new List<SpellEffect>();
                var criticos = new List<SpellEffect>();
                try
                {
                    using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                    conexion.Open();

                    var orden = conexion.CreateCommand();
                    orden.CommandText =
                        "SELECT EffectsJson, CriticalEffectsJson, MaxStack FROM SpellLevels " +
                        "WHERE SpellId = $id AND Grade = $g LIMIT 1;";
                    orden.Parameters.AddWithValue("$id", hechizo);
                    orden.Parameters.AddWithValue("$g", clave.Item2);

                    using var lector = orden.ExecuteReader();
                    if (lector.Read())
                    {
                        int maxStack = lector.IsDBNull(2) ? -1 : lector.GetInt32(2);
                        Parsear(lector.IsDBNull(0) ? "" : lector.GetString(0), normales, maxStack);
                        Parsear(lector.IsDBNull(1) ? "" : lector.GetString(1), criticos, maxStack);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Efectos] No se pudieron leer los del hechizo {hechizo} " +
                                     $"grado {grado}: {ex.Message}");
                }

                _cache[clave] = normales;
                _criticos[clave] = criticos;
                return (normales, criticos);
            }
        }

        private static void Parsear(string json, List<SpellEffect> donde, int maxStack)
        {
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    // The sheet's copies stay on the sheet: a row for the client only is not
                    // read into the list at all, so nothing downstream can run it by mistake.
                    int flags = Entero(e, "m_flags");
                    if ((flags & SpellEffect.ForClientOnlyFlag) != 0) continue;

                    int forma = 'P', tamano = 1, minimo = 0, paso = 0, tope = 0;
                    bool para = false;
                    var fijas = new List<int>();
                    if (e.TryGetProperty("zoneDescr", out var z) && z.ValueKind == JsonValueKind.Object)
                    {
                        // The ';' zone names its cells outright, map cells: the summons a boss
                        // puts on fixed cells, its runes, its fixed-cell blows.
                        if (z.TryGetProperty("cellIds", out var celdas))
                        {
                            var lista = celdas.ValueKind == JsonValueKind.Object && celdas.TryGetProperty("Array", out var dentro)
                                ? dentro : celdas;
                            if (lista.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in lista.EnumerateArray())
                                    if (c.ValueKind == JsonValueKind.Number) fijas.Add(c.GetInt32());
                            }
                        }
                        int f = Entero(z, "shape");
                        if (f > 0) forma = f;
                        tamano = Entero(z, "param1");
                        minimo = Entero(z, "param2");
                        para = Entero(z, "isStopAtTarget") != 0;
                        paso = Entero(z, "damageDecreaseStepPercent");
                        tope = Entero(z, "maxDamageDecreaseApplyCount");
                    }

                    donde.Add(new SpellEffect
                    {
                        EffectId = Entero(e, "effectId"),
                        EffectUid = Entero(e, "effectUid"),
                        Value = Entero(e, "value"),
                        DiceNum = Entero(e, "diceNum"),
                        DiceSide = Entero(e, "diceSide"),
                        Duration = Entero(e, "duration"),
                        Delay = Entero(e, "delay"),
                        Dispellable = Entero(e, "dispellable"),
                        Element = e.TryGetProperty("effectElement", out var el) && el.TryGetInt32(out int v) ? v : -1,
                        Triggers = Texto(e, "triggers", "I"),
                        TargetMask = Texto(e, "targetMask", ""),
                        CeldasFijas = fijas,
                        Forma = forma,
                        Tamano = tamano,
                        TamanoMinimo = minimo,
                        ParaEnElObjetivo = para,
                        PasoDeCaida = paso,
                        TopeDeCaida = tope,
                        MaxStack = maxStack,
                        Probabilidad = e.TryGetProperty("random", out var rnd) &&
                                       rnd.TryGetDouble(out double p) ? p : 0,
                        Sorteo = Entero(e, "group"),
                        Flags = flags,
                    });
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] JSON de efectos ilegible: {ex.Message}");
            }
        }

        private static int Entero(JsonElement e, string nombre)
            => e.TryGetProperty(nombre, out var v) && v.TryGetInt32(out int n) ? n : 0;

        private static string Texto(JsonElement e, string nombre, string porDefecto)
            => e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? porDefecto)
                : porDefecto;

        /// <summary>
        /// The grade a character has unlocked of a spell, and that row's identifier.
        /// It comes from SpellLevels by MinPlayerLevel, which is where the client itself takes it from.
        /// </summary>
        public static (int Grado, int NivelId, int Coste) GradoDe(int hechizo, int nivelDelPersonaje)
        {
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();
                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT Grade, Id, APCost FROM SpellLevels WHERE SpellId = $id " +
                                    "AND MinPlayerLevel <= $lvl ORDER BY Grade DESC LIMIT 1;";
                orden.Parameters.AddWithValue("$id", hechizo);
                orden.Parameters.AddWithValue("$lvl", Math.Max(1, nivelDelPersonaje));
                using var lector = orden.ExecuteReader();
                if (lector.Read())
                {
                    return ((int)lector.GetInt64(0), (int)lector.GetInt64(1), (int)lector.GetInt64(2));
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] No se pudo mirar el grado del hechizo {hechizo}: {ex.Message}");
            }
            return (1, 0, 0);
        }

        /// <summary>
        /// The attitudes the equipped items give away: each one's 1175 effect carries in its
        /// <c>diceNum</c> the spell it gives. From there come those of the six dofus and those of the
        /// trophies, and with them the Ochre's rule —"at the start of each turn, an AP if you have not been
        /// hit"— without writing a single line about the Ochre.
        /// </summary>
        public const int EfectoQueRegalaHechizo = 1175;

        /// <summary>The slots that are real equipment; from 63 onwards it is the bag.</summary>
        private const int UltimaCasillaDeEquipo = 62;

        public static List<int> ActitudesDelEquipo(long personaje)
        {
            var fuera = new List<int>();
            try
            {
                using var conexion = new SqliteConnection(DatabaseManager.WorldConnectionString);
                conexion.Open();
                var orden = conexion.CreateCommand();
                orden.CommandText = "SELECT Effects FROM CharacterItems WHERE CharacterId = $id " +
                                    "AND Position >= 0 AND Position <= $ultima;";
                orden.Parameters.AddWithValue("$id", personaje);
                orden.Parameters.AddWithValue("$ultima", UltimaCasillaDeEquipo);

                using var lector = orden.ExecuteReader();
                while (lector.Read())
                {
                    // The raw effect is needed and not the inventory summary: the spell the item
                    // gives away travels in the DIE, not in the value.
                    foreach (var efecto in Equipment.ParseEffects(lector.IsDBNull(0) ? "" : lector.GetString(0)))
                    {
                        if (efecto.Effect != EfectoQueRegalaHechizo) continue;
                        int hechizo = (int)efecto.DiceNum;
                        if (hechizo > 0 && !fuera.Contains(hechizo)) fuera.Add(hechizo);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Efectos] No se pudieron mirar las actitudes del equipo: {ex.Message}");
            }
            return fuera;
        }
    }
}
