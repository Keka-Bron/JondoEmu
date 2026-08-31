using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Dos reglas del motor de combate, comprobadas sobre su propio código fuente.
    /// </summary>
    /// <remarks>
    /// Es fea y da igual: de las cuatro cosas que se hicieron para separar PvM de PvP, ésta es la
    /// única que sigue trabajando dentro de seis meses. Las otras tres arreglan lo que hay; ésta
    /// impide que vuelva.
    ///
    /// Las dos reglas salen de las dos clases de fallo que dieron catorce errores en dos tardes de
    /// desafíos, y las dos son invisibles en revisión: el código compila, las pruebas de siempre
    /// pasan, y el fallo sólo se ve desde la pantalla del OTRO jugador.
    ///
    /// Si una de las dos salta y estás seguro de que tu caso es bueno, añádelo a su lista de
    /// excepciones CON el motivo escrito. Que cueste un comentario es parte del asunto.
    /// </remarks>
    public class FightArchitectureTests
    {
        private static string Fuente(string ruta)
        {
            var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
            while (carpeta != null && !File.Exists(Path.Combine(carpeta.FullName, "Jondo.Unity.sln")))
            {
                carpeta = carpeta.Parent;
            }

            Assert.True(carpeta != null, "No se encuentra la raíz de la solución desde " + AppContext.BaseDirectory);
            string fichero = Path.Combine(carpeta!.FullName, ruta);
            Assert.True(File.Exists(fichero), "No está " + fichero);
            return File.ReadAllText(fichero);
        }

        private const string ElMotor = "Jondo.Unity.Server/Handlers/FightHandler.cs";

        // ═══════════════════════════════════════════════════════════════════
        //  Regla 1: nadie busca a alguien en un solo bando
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void Nadie_busca_combatientes_en_un_solo_bando()
        {
            // «fight.Azul.Find(f => f.Id == quien)» es la forma de este fallo. Contra monstruos
            // acierta siempre porque el único humano está en el azul; en un desafío deja al retado
            // sin poder recolocarse, sin sus esperas iniciales y sin poder abandonar.
            //
            // Para buscar a alguien está fight.Buscar(id); para saber de qué lado es, EquipoDe(id);
            // para los suyos y los otros, Aliados(id) y Enemigos(id).
            var prohibido = new Regex(@"\.(Azul|Rojo)\.(Find|Exists|FirstOrDefault|Any|All)\b");

            var culpables = Culpables(Fuente(ElMotor), linea => prohibido.IsMatch(linea));

            Assert.True(culpables.Count == 0,
                "Búsquedas en un solo bando:" + Environment.NewLine + string.Join(Environment.NewLine, culpables));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Regla 2: lo que se difunde no puede depender de quién mira
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Las difusiones que SÍ pueden leer GameState, y por qué.
        /// </summary>
        /// <remarks>
        /// El kah dice quién acaba de declararse listo. Corre en el contexto de quien pulsó, así
        /// que ese GameState.CharacterId es el SUJETO de la trama y no quien la recibe: la trama
        /// es la misma para los dos y difundirla es lo correcto.
        /// </remarks>
        private static readonly string[] Permitidas = { "Op.Kah" };

        [Fact]
        public void Lo_que_se_difunde_no_se_construye_desde_una_sesion()
        {
            // Ésta es la que encontró el fallo del jxw: una trama que lleva dentro «este
            // combatiente eres tú», difundida UNA vez con esa marca calculada contra la sesión que
            // estuviera corriendo. El otro recibía sus propios puntos marcados como ajenos.
            //
            // Si el contenido cambia según quién lo reciba, no es una difusión: es una trama por
            // persona, y para eso está ACadaUnoAsync -- o un ayudante como FichaATodosAsync.
            var malas = new List<string>();

            foreach (var (numero, llamada) in Difusiones(Fuente(ElMotor)))
            {
                if (!llamada.Contains("GameState")) continue;
                if (Permitidas.Any(llamada.Contains)) continue;

                malas.Add($"  línea {numero}: {llamada.Split('\n')[0].Trim()}");
            }

            Assert.True(malas.Count == 0,
                "Difusiones cuyo contenido depende de quién mira:" + Environment.NewLine
                + string.Join(Environment.NewLine, malas));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Regla 3: escribir a un socket suelto es la excepción, no el atajo
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Los métodos que SÍ construyen la vista de una sola persona, y por eso escriben a su
        /// socket. Cualquier otro que lo haga se está saltando la regla del destinatario.
        /// </summary>
        /// <remarks>
        /// Los cinco primeros son las ráfagas por cliente: la entrada al combate, la preparación,
        /// el arranque, el final y el reenvío del mapa. Los <c>Send…</c> son trozos de esas mismas
        /// ráfagas. Los dos últimos son casos sueltos con su motivo:
        ///
        ///   AttackAsync                   su jsq es el permiso de cambio de mapa de quien ataca
        ///   HandleFightOptionToggle       los interruptores del panel; SIN MEDIR si el rival los ve
        ///   AnnounceAppearanceAsync       no recibe el combate; hace falta pasárselo para difundir
        ///   RefreshPlayerSpellBarAsync    la barra de hechizos es de quien la mira
        /// </remarks>
        private static readonly string[] VistaDeUnaPersona =
        {
            "SendFightEntryAsync", "SendPreparationAsync", "ArrancarParaUnoAsync",
            "TerminarParaUnoAsync", "HandleFightMapLoad", "ResendFightMapBurst3",
            "SendFighterShow", "SendFightStarting", "SendTurnList",
            "SendPlacementTurnStart", "SendPlacementPositionsList",
            "RefreshPlayerSpellBarAsync", "AttackAsync",
            "HandleFightOptionToggleRequest", "AnnounceAppearanceAsync",
        };

        [Fact]
        public void Solo_escriben_a_un_socket_los_que_pintan_la_vista_de_uno()
        {
            // La regla del destinatario: lo que pasa en el tablero va a todos, y la vista de una
            // persona va a cada uno desde su contexto. Mientras escribir al socket suelto esté a
            // mano dentro del motor, el próximo método nuevo se la salta sin querer -- que es
            // exactamente lo que pasó con el «listo», con el arranque y con el final del combate.
            var metodo = new Regex(@"^\s*(?:private|public|internal).*\sTask[<\w>]*\s+(\w+)\s*\(");
            var lineas = Fuente(ElMotor).Split('\n');

            string actual = "";
            var malos = new List<string>();

            for (int i = 0; i < lineas.Length; i++)
            {
                var m = metodo.Match(lineas[i]);
                if (m.Success) actual = m.Groups[1].Value;

                if (!lineas[i].Contains("await WriteFrameAsync(stream,")) continue;
                if (VistaDeUnaPersona.Contains(actual)) continue;

                malos.Add($"  línea {i + 1}, en {actual}: {lineas[i].Trim()}");
            }

            Assert.True(malos.Count == 0,
                "Escriben a un socket suelto sin ser la vista de una persona:" + Environment.NewLine
                + string.Join(Environment.NewLine, malos));
        }

        [Fact]
        public void La_guardia_esta_mirando_de_verdad()
        {
            // Que las dos de arriba no pasen por no haber encontrado el fichero, o por haber
            // renombrado el ayudante y quedarse sin nada que revisar.
            string motor = Fuente(ElMotor);

            Assert.Contains("ATodosAsync", motor);
            Assert.True(Difusiones(motor).Count >= 50,
                "Se esperaban decenas de difusiones y se han visto " + Difusiones(motor).Count);
        }

        // ─── las dos herramientas ───────────────────────────────────────────

        /// <summary>Las líneas que cumplen el filtro, con su número y sin contar las comentadas.</summary>
        private static List<string> Culpables(string fuente, Func<string, bool> filtro)
        {
            var salida = new List<string>();
            var lineas = fuente.Split('\n');

            for (int i = 0; i < lineas.Length; i++)
            {
                string limpia = lineas[i].TrimStart();
                if (limpia.StartsWith("//") || limpia.StartsWith("///")) continue;
                if (filtro(lineas[i])) salida.Add($"  línea {i + 1}: {limpia.TrimEnd()}");
            }
            return salida;
        }

        /// <summary>Cada llamada a ATodosAsync entera, con su número de línea.</summary>
        /// <remarks>
        /// Se cuentan los paréntesis para coger la llamada completa: el contenido interesante casi
        /// siempre está en las líneas de debajo, no en la primera.
        /// </remarks>
        private static List<(int Numero, string Llamada)> Difusiones(string fuente)
        {
            var salida = new List<(int, string)>();
            var lineas = fuente.Split('\n');

            for (int i = 0; i < lineas.Length; i++)
            {
                if (!lineas[i].Contains("ATodosAsync(fight,")) continue;

                var trozo = new List<string>();
                int profundidad = 0;
                for (int j = i; j < lineas.Length && j < i + 12; j++)
                {
                    trozo.Add(lineas[j]);
                    profundidad += lineas[j].Count(c => c == '(') - lineas[j].Count(c => c == ')');
                    if (j > i && profundidad <= 0) break;
                }
                salida.Add((i + 1, string.Join("\n", trozo)));
            }
            return salida;
        }
    }
}
