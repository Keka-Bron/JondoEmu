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
        /// Caracteristicas de combate del grado, incluido su bonusCharacteristics. Estas ultimas
        /// no son decorativas: por ejemplo la Dragoune lleva aqui 50 de inteligencia, que aumenta
        /// tanto su golpe de fuego como la cura de su hechizo mixto.
        /// </summary>
        public int Fuerza { get; init; }
        public int Inteligencia { get; init; }
        public int Suerte { get; init; }
        public int Agilidad { get; init; }
        public int DanoTierra { get; init; }
        public int DanoFuego { get; init; }
        public int DanoAgua { get; init; }
        public int DanoAire { get; init; }

        /// <summary>El hechizo que gobierna al bicho, y en qué grado.</summary>
        public int HechizoPropio { get; init; }
        public int GradoDelHechizoPropio { get; init; }
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

        // ─── Cuánto vive ────────────────────────────────────────────────────────

        private static Dictionary<int, int> _duraciones;

        /// <summary>
        /// Las rondas que aguanta un invocado antes de deshacerse solo.
        ///
        /// Esto NO está en la base. El efecto 141 que le cuelgan al nacer viene con
        /// <c>duration</c> a cero en los cuatro grados de los dos hechizos, y MonsterTemplates
        /// tampoco lo trae. Así que está medido de las capturas y guardado en
        /// <c>datos/invocaciones_duracion.json</c>, que es como se hizo con las mascoturas y con
        /// los colores de las monturas: la baliza táctica dura 3 rondas —seis medidas iguales— y
        /// la de supervivencia 2 —dos medidas—.
        ///
        /// Lo que no esté medido devuelve cero, y entonces no se le pone cuenta atrás: es
        /// preferible a inventarse un número.
        /// </summary>
        public static int RondasQueVive(int plantilla)
        {
            if (_duraciones == null)
            {
                _duraciones = new Dictionary<int, int>();
                try
                {
                    // Por Paths, no relativo: si no aparece, las invocaciones dejan de caducar
                    // y se quedan en el combate para siempre.
                    string ruta = Paths.SummonDurationsJson;
                    if (System.IO.File.Exists(ruta))
                    {
                        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(ruta));
                        foreach (var entrada in doc.RootElement.EnumerateObject())
                        {
                            if (int.TryParse(entrada.Name, out int id))
                                _duraciones[id] = Entero(entrada.Value, "rondas");
                        }
                    }
                    Program.LogDebug($"[Summons] {_duraciones.Count} duración(es) medida(s).");
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Summons] No se pudo leer la tabla de duraciones: {ex.Message}");
                }
            }
            return _duraciones.TryGetValue(plantilla, out int rondas) ? rondas : 0;
        }

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
                int bonusVida = 0;
                int bonusFuerza = 0, bonusInteligencia = 0, bonusSuerte = 0, bonusAgilidad = 0;
                int danoTierra = 0, danoFuego = 0, danoAgua = 0, danoAire = 0;
                if (gr.TryGetProperty("bonusCharacteristics", out var bonus))
                {
                    bonusVida = Entero(bonus, "lifePoints");
                    bonusFuerza = Entero(bonus, "strength");
                    bonusInteligencia = Entero(bonus, "intelligence");
                    bonusSuerte = Entero(bonus, "chance");
                    bonusAgilidad = Entero(bonus, "agility");
                    danoTierra = Entero(bonus, "bonusEarthDamage");
                    danoFuego = Entero(bonus, "bonusFireDamage");
                    danoAgua = Entero(bonus, "bonusWaterDamage");
                    danoAire = Entero(bonus, "bonusAirDamage");
                }

                // El hechizo con el que se porta: el startingSpellId es un SpellLevels.Id.
                int nivelDelHechizo = Entero(gr, "startingSpellId");
                var (hechizo, gradoDelHechizo) = HechizoDe(conexion, nivelDelHechizo);

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
                    Fuerza = Entero(gr, "strength") + bonusFuerza,
                    Inteligencia = Entero(gr, "intelligence") + bonusInteligencia,
                    Suerte = Entero(gr, "chance") + bonusSuerte,
                    Agilidad = Entero(gr, "agility") + bonusAgilidad,
                    DanoTierra = danoTierra,
                    DanoFuego = danoFuego,
                    DanoAgua = danoAgua,
                    DanoAire = danoAire,
                    HechizoPropio = hechizo,
                    GradoDelHechizoPropio = gradoDelHechizo,
                };
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Summons] No se pudo leer la plantilla {plantilla} " +
                                 $"grado {grado}: {ex.Message}");
                return null;
            }
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

            // El aspecto es el PRIMER número de entre las llaves, y antes se exigía que fuera lo
            // único que hubiera dentro.
            //
            // Con «{8348}» funcionaba, pero el del «Regalo animado» es «{446|||120}» y ahí el
            // int.TryParse fallaba, así que se devolvía la propia plantilla —3106— como aspecto y
            // el cliente dibujaba otro bicho. Es la misma regla que ya usa FightHandler para el
            // hueso de un monstruo normal. Medido: 21 de 21 aciertos contra las capturas, frente a
            // 17 de 21 de la versión anterior.
            string limpio = look.Trim().Trim('{', '}');
            int barra = limpio.IndexOf('|');
            string primero = barra >= 0 ? limpio.Substring(0, barra) : limpio;
            if (int.TryParse(primero, out int otra) && otra > 0 && otra != plantilla)
            {
                return LookDe(conexion, otra, vueltas + 1);
            }
            return (look, plantilla);
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
