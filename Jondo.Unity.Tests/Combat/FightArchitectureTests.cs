using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Two rules of the fight engine, checked against its own source code.
    /// </summary>
    /// <remarks>
    /// It is ugly and it does not matter: of the four things done to separate PvM from PvP, this is the
    /// only one still working six months from now. The other three fix what there is; this one
    /// keeps it from coming back.
    ///
    /// The two rules come from the two kinds of bug that gave fourteen errors in two afternoons of
    /// challenges, and both are invisible in review: the code compiles, the usual tests
    /// pass, and the bug is only seen from the OTHER player's screen.
    ///
    /// If one of the two fires and you are sure your case is good, add it to its list of
    /// exceptions WITH the reason written. That it costs a comment is part of the point.
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

        /// <summary>
        /// The engine is one class in two files since joining a fight got its own: the same rules
        /// read both, or the second one is a way round them.
        /// </summary>
        private static readonly string[] ElMotorEntero =
        {
            ElMotor,
            "Jondo.Unity.Server/Handlers/FightJoin.cs",
            // Walking and its tackles, and what a defeat costs: partials of the same class.
            "Jondo.Unity.Server/Handlers/FightTackle.cs",
            "Jondo.Unity.Server/Handlers/FightDefeat.cs",
        };

        private static string Motor() => string.Join("\n", ElMotorEntero.Select(Fuente));

        // ═══════════════════════════════════════════════════════════════════
        //  Regla 1: nadie busca a alguien en un solo bando
        // ═══════════════════════════════════════════════════════════════════

        [Fact]
        public void Nadie_busca_combatientes_en_un_solo_bando()
        {
            // «fight.Azul.Find(f => f.Id == quien)» is the shape of this bug. Against monsters
            // it always hits because the only human is on blue; in a challenge it leaves the challenged
            // unable to reposition, without his initial waits and unable to abandon.
            //
            // To look someone up there is fight.Buscar(id); to know which side he is on, EquipoDe(id);
            // for his own and the others, Aliados(id) and Enemigos(id).
            var prohibido = new Regex(@"\.(Azul|Rojo)\.(Find|Exists|FirstOrDefault|Any|All)\b");

            var culpables = Culpables(Motor(), linea => prohibido.IsMatch(linea));

            Assert.True(culpables.Count == 0,
                "Búsquedas en un solo bando:" + Environment.NewLine + string.Join(Environment.NewLine, culpables));
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Rule 2: what is broadcast cannot depend on who is looking
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The broadcasts that MAY read GameState, and why.
        /// </summary>
        /// <remarks>
        /// The kah says who has just declared himself ready. It runs in the context of whoever pressed, so
        /// that GameState.CharacterId is the frame's SUBJECT and not whoever receives it: the frame
        /// is the same for both and broadcasting it is right.
        /// </remarks>
        private static readonly string[] Permitidas = { "Op.Kah" };

        [Fact]
        public void Lo_que_se_difunde_no_se_construye_desde_una_sesion()
        {
            // This is the one that found the jxw bug: a frame that carries inside «this
            // fighter is you», broadcast ONCE with that mark computed against whichever session
            // happened to be running. The other received his own points marked as someone else's.
            //
            // If the content changes depending on who receives it, it is not a broadcast: it is one frame per
            // person, and that is what ACadaUnoAsync is for -- or a helper like FichaATodosAsync.
            var malas = new List<string>();

            foreach (var (numero, llamada) in Difusiones(Motor()))
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
        //  Rule 3: writing to a loose socket is the exception, not the shortcut
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The methods that DO build a single person's view, and that is why they write to his
        /// socket. Any other doing so is skipping the recipient rule.
        /// </summary>
        /// <remarks>
        /// The first six are the per-client bursts: the entry into the fight, the preparation,
        /// the start, the return to a fight in progress, the end and the map resend. The <c>Send…</c> are pieces of those same
        /// bursts. The last two are loose cases with their reason:
        ///
        ///   AttackAsync                   its jsq is the map change permission of the attacker
        ///   HandleFightOptionToggle       the panel's switches; NOT MEASURED whether the rival sees them
        ///   AnnounceAppearanceAsync       does not receive the fight; it has to be passed to broadcast
        ///   RefreshPlayerSpellBarAsync    the spell bar belongs to whoever looks at it
        /// </remarks>
        private static readonly string[] VistaDeUnaPersona =
        {
            "SendFightEntryAsync", "SendPreparationAsync", "ArrancarParaUnoAsync",
            "ResumeForOneAsync", "TerminarParaUnoAsync", "HandleFightMapLoad", "ResendFightMapBurst3",
            "SendFighterShow", "SendFightStarting", "SendTurnList",
            "SendPlacementTurnStart", "SendPlacementPositionsList",
            "RefreshPlayerSpellBarAsync", "AttackAsync",
            "HandleFightOptionToggleRequest", "AnnounceAppearanceAsync",
            // FightJoin.cs: the jqz behind the jss of the one loading a map, the answer to the
            // party window's switch of the one who clicked it, and the way back of the one who
            // leaves the placement -- all three are one person's own view.
            "SendFightCountAsync", "AutoOptionAsync", "LeavePlacementAsync",
        };

        [Fact]
        public void Solo_escriben_a_un_socket_los_que_pintan_la_vista_de_uno()
        {
            // The recipient rule: what happens on the board goes to everyone, and a person's view
            // goes to each one from his own context. As long as writing to the loose socket is at
            // hand inside the engine, the next new method skips it without meaning to -- which is
            // exactly what happened with the «ready», with the start and with the end of the fight.
            var metodo = new Regex(@"^\s*(?:private|public|internal).*\sTask[<\w>]*\s+(\w+)\s*\(");
            var lineas = Motor().Split('\n');

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
            // That the two above do not pass for not having found the file, or for having
            // renamed the helper and being left with nothing to check.
            string motor = Fuente(ElMotor);

            Assert.Contains("ATodosAsync", motor);
            Assert.True(Difusiones(motor).Count >= 50,
                "Se esperaban decenas de difusiones y se han visto " + Difusiones(motor).Count);
        }

        // ─── the two tools ──────────────────────────────────────────────────

        /// <summary>The lines that match the filter, with their number and not counting the commented ones.</summary>
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

        /// <summary>Each whole call to ATodosAsync, with its line number.</summary>
        /// <remarks>
        /// The parentheses are counted to take the complete call: the interesting content is almost
        /// always in the lines below, not in the first.
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
