using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// The guards that look at the SOURCE CODE, not at what the program does.
    ///
    /// They catch the kind of failure that gives no error anywhere: a database query not filtered
    /// by owner, a password written in the code itself, an ownership check that switches itself
    /// off. None of that breaks a game, it does not show in the log and it is not noticed playing;
    /// it is only seen by reading, and that is why it is read here at every start.
    ///
    /// It is checked on the text and not by running it, on purpose. Testing the owner part for real
    /// would need setting up a database with two characters in here, and what is to be avoided is
    /// not that a specific case fails: it is somebody WRITING the same pattern again six months from
    /// now without knowing it already happened once.
    ///
    /// Each guard says what it broke back then. If one fires, that is what has to be read before
    /// touching anything.
    /// </summary>
    public static class SecurityGuardTests
    {
        /// <summary>A code file that was read, with its path.</summary>
        public readonly struct Fuente
        {
            public Fuente(string ruta, string texto) { Ruta = ruta; Texto = texto; }
            public string Ruta { get; }
            public string Texto { get; }
            public string Nombre => Path.GetFileName(Ruta);
        }

        public static void Run(List<Fuente> fuentes)
        {
            AssertItemQueriesAreScopedToOwner(fuentes);
            AssertNoPlaintextPasswordComparison(fuentes);
            AssertNoSeededCredentials(fuentes);
            AssertOwnershipChecksAreNotSelfDisabling(fuentes);
            AssertLengthPrefixIsCapped(fuentes);
            AssertSecretsAreNotLogged(fuentes);
            AssertGuardsThrowInsteadOfReturning(fuentes);
            AssertDataFilesGoThroughPaths(fuentes);
            AssertCachedSpellDataIsNotMutated(fuentes);
        }

        // ─── Items and their owner ─────────────────────────────────────────────────────

        /// <summary>
        /// Nothing that writes to CharacterItems or to the chest can go without CharacterId.
        ///
        /// The uid is chosen by the CLIENT. SaveItemPosition did «UPDATE CharacterItems SET
        /// Position = $pos WHERE Uid = $uid» without looking whose it is, so an iuk with the number of
        /// somebody else's item moved it to another slot all the same. The uid is unique across the
        /// whole server -- there is a unique index and they are handed out with a global MAX -- so it was
        /// not half the world at once, but it was somebody else's item, and from a connection that had
        /// not presented a ticket yet.
        ///
        /// It also applies to the statements that sit behind an ownership check and so «are not
        /// needed»: the code is reordered, the guard is moved, and the statement stays as it was
        /// without anybody noticing.
        /// </summary>
        private static void AssertItemQueriesAreScopedToOwner(List<Fuente> fuentes)
        {
            var escribe = new Regex(
                @"(UPDATE|DELETE\s+FROM)\s+(?<tabla>CharacterItems|HavenBagChest)\b(?<resto>[^;]*)",
                RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                foreach (Match m in escribe.Matches(f.Texto))
                {
                    string resto = m.Groups["resto"].Value;

                    // Without a WHERE it is not that the owner is missing: everything is missing, and that shows
                    // the moment it is tried. The dangerous one is the one that DOES filter, but by the wrong thing.
                    if (resto.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (resto.IndexOf("CharacterId", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' escribe en {m.Groups["tabla"].Value} sin " +
                        $"filtrar por CharacterId: «{Recortar(m.Value)}». El uid lo elige el cliente, " +
                        "así que sin dueño se le puede tocar el objeto a otro. El patrón bueno está " +
                        "en DatabaseManager.DestroyCharacterItem.");
                }
            }
        }

        // ─── Passwords ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The password is not compared inside a SELECT.
        ///
        /// «WHERE Login = $login AND Password = $pass» only works if the password is stored as it
        /// was typed, so comparing in SQL and storing them in clear are the same decision. It is
        /// checked in Managers.Claves, against the hash.
        /// </summary>
        private static void AssertNoPlaintextPasswordComparison(List<Fuente> fuentes)
        {
            var cotejo = new Regex(@"Password\s*=\s*\$\w+", RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                foreach (Match m in cotejo.Matches(f.Texto))
                {
                    // The UPDATE that WRITES it carries «Password = $pass» and that is correct. What there
                    // cannot be is a SELECT that uses it to compare, so the query it belongs to is looked
                    // at.
                    int inicio = f.Texto.LastIndexOf("CommandText", m.Index, StringComparison.Ordinal);
                    if (inicio < 0) continue;

                    string consulta = f.Texto.Substring(inicio, m.Index - inicio + m.Length);
                    if (consulta.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' compara la contraseña dentro de un " +
                        "SELECT. Eso obliga a tenerla guardada en claro; la comprobación va por " +
                        "Managers.Claves.Comprueba.");
                }
            }
        }

        /// <summary>
        /// No accounts are seeded with the password written in the code.
        ///
        /// Every freshly made database was born with 'keka' and 'dragonlord' as ADMINISTRATOR and the
        /// password 'test' -- published in the repository, so whoever started the server had it wide
        /// open without knowing.
        /// </summary>
        private static void AssertNoSeededCredentials(List<Fuente> fuentes)
        {
            var alta = new Regex(@"INSERT[^;""]{0,200}?INTO\s+Accounts\b[^;""]*", RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                foreach (Match m in alta.Matches(f.Texto))
                {
                    if (m.Value.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // Real registration passes the password as a parameter; a seed carries it inside.
                    if (m.Value.IndexOf("$pass", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' da de alta una cuenta con la " +
                        $"contraseña escrita en el código: «{Recortar(m.Value)}».");
                }
            }
        }

        // ─── Checks that switch themselves off ─────────────────────────────────────────

        /// <summary>
        /// An ownership check cannot depend on there being an account.
        ///
        /// It was written as «accountId > 0 &amp;&amp; !CharacterBelongsToAccount(...)», so exactly in
        /// the case it had to catch -- a socket that sends the kvw before the kqz and arrives with
        /// account zero -- it was skipped entirely and loaded anybody's sheet. Without an account it is
        /// REFUSED, which is not the same as letting it through.
        ///
        /// The first version of this guard looked for «accountId &gt; 0 &amp;&amp; !» on its own and fired
        /// on ClientLaunchRegistry.RegisterToken, which is
        /// «accountId &gt; 0 &amp;&amp; !string.IsNullOrWhiteSpace(token)» and is PERFECT: there the &amp;&amp;
        /// guards the good path, not a check. What makes the pattern dangerous is not the
        /// «&amp;&amp; !», it is that what is negated is an ownership question, so both things are
        /// required.
        /// </summary>
        private static void AssertOwnershipChecksAreNotSelfDisabling(List<Fuente> fuentes)
        {
            // The questions that decide whether something is yours. If one appears behind a «there is
            // an account && !», the check switches itself off in the case that matters.
            var seApaga = new Regex(
                @"[Aa]ccountId\s*>\s*0\s*&&\s*!\s*[\w\.]*" +
                @"(Belongs|Owns|OwnedBy|Pertenece|EsDe|Puede|Allowed|Authoriz)\w*\s*\(");

            foreach (var f in fuentes)
            {
                Match m = seApaga.Match(f.Texto);
                if (!m.Success) continue;

                throw new InvalidOperationException(
                    $"[SecurityGuard FAILED] '{f.Nombre}' condiciona una comprobación de propiedad " +
                    $"a que haya cuenta resuelta («{Recortar(m.Value)}»). Eso la apaga justo cuando " +
                    "hace falta: sin cuenta hay que rechazar, no dejar pasar.");
            }
        }

        // ─── What comes in through the socket ──────────────────────────────────────────

        /// <summary>
        /// The frame length varint has a cap.
        ///
        /// Without it, five bytes «FF FF FF FF 07» asked for a 2 GB array before reading a single
        /// byte of content, and eight connections were enough to bring the server down without
        /// authenticating. It is checked that ReadFrameAsync still looks at MaxFrameLength.
        /// </summary>
        private static void AssertLengthPrefixIsCapped(List<Fuente> fuentes)
        {
            foreach (var f in fuentes)
            {
                if (f.Nombre != "NetworkMessage.cs") continue;

                int lectura = f.Texto.IndexOf("ReadFrameAsync", StringComparison.Ordinal);
                if (lectura < 0) continue;

                int reserva = f.Texto.IndexOf("new byte[length]", lectura, StringComparison.Ordinal);
                if (reserva < 0) return;

                string entreMedias = f.Texto.Substring(lectura, reserva - lectura);
                if (entreMedias.Contains("MaxFrameLength")) return;

                throw new InvalidOperationException(
                    "[SecurityGuard FAILED] NetworkMessage.ReadFrameAsync reserva la trama sin " +
                    "mirar MaxFrameLength. Un varint de longitud sin tope pide 2 GB con cinco bytes.");
            }
        }

        /// <summary>
        /// Bodies and session identifiers are not written to the log uncovered.
        ///
        /// The log goes to the console, to logs\emulator_console.log and to the buffer that serves
        /// /api/registro. The passwords for signing in and for creating an account went through it in
        /// clear.
        /// </summary>
        private static void AssertSecretsAreNotLogged(List<Fuente> fuentes)
        {
            // The places that dump something coming from the client, and what it has to be covered with.
            var vigilados = new (string Fichero, string Interpolacion, string Remedio)[]
            {
                ("HaapiServer.cs", "{body}",        "Censura.Cuerpo(body)"),
                ("ChatServer.cs",  "{ascii}",       "Censura.Cuerpo(ascii)"),
                ("ZaapServer.cs",  "{gameSession}", "Censura.Valor(gameSession)"),
                ("ZaapServer.cs",  "{hash}",        "Censura.Valor(hash)"),
            };

            foreach (var f in fuentes)
            {
                foreach (var (fichero, interpolacion, remedio) in vigilados)
                {
                    if (f.Nombre != fichero) continue;
                    if (!f.Texto.Contains(interpolacion)) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' escribe «{interpolacion}» en el " +
                        $"registro sin tapar. Eso lleva contraseñas o identificadores de sesión; " +
                        $"va con {remedio}.");
                }
            }
        }

        // ─── Guards that do not guard ──────────────────────────────────────────────────

        /// <summary>
        /// A guard that fails has to STOP the start.
        ///
        /// ConnectionProtocolSelfTest's printed its failures in red and did «return», so the server
        /// started anyway with the protocol broken -- and what shows on the client then is not an
        /// error, it is a blank screen. It was the only one of the eight that did not throw.
        /// </summary>
        private static void AssertGuardsThrowInsteadOfReturning(List<Fuente> fuentes)
        {
            foreach (var f in fuentes)
            {
                if (f.Nombre != "ConnectionProtocolSelfTest.cs") continue;

                int fallos = f.Texto.IndexOf("if (failures.Count > 0)", StringComparison.Ordinal);
                if (fallos < 0) return;

                // The block that follows: up to the closing brace, more or less.
                int fin = f.Texto.IndexOf("\n        }", fallos, StringComparison.Ordinal);
                string bloque = fin < 0 ? f.Texto.Substring(fallos) : f.Texto.Substring(fallos, fin - fallos);

                if (bloque.Contains("throw")) return;

                throw new InvalidOperationException(
                    "[SecurityGuard FAILED] ConnectionProtocolSelfTest imprime los fallos y sigue " +
                    "adelante. Una guardia de bytes que no para el arranque no guarda nada: el " +
                    "cliente se queda en negro y no dice por qué.");
            }
        }

        // ─── Data files ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Data files are opened through Paths, not by a relative path.
        ///
        /// WorldEntry and Summons opened them relative, and it worked by a miracle: only because the
        /// launcher leaves the working directory at the root. Started any other way, the
        /// characteristics sheet dropped from 120 entries to 25 -- and with it critical hits, power,
        /// range and resistances -- without a single error message.
        /// </summary>
        private static void AssertDataFilesGoThroughPaths(List<Fuente> fuentes)
        {
            // A path relative to datos\ or dofus3_data\ put by hand into a file call.
            var relativa = new Regex(
                @"File\.(ReadAllText|ReadAllBytes|ReadAllLines|OpenRead|Exists)\s*\(\s*""(datos|dofus3_data|bases)[\\/]",
                RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                Match m = relativa.Match(f.Texto);
                if (!m.Success) continue;

                throw new InvalidOperationException(
                    $"[SecurityGuard FAILED] '{f.Nombre}' abre un fichero de datos por ruta " +
                    $"relativa («{Recortar(m.Value)}»). Eso sólo funciona si el directorio de " +
                    "trabajo es la raíz; va por Paths.Resolve, que no depende de quién arranque.");
            }
        }

        // ─── ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What GetSpellCombatData returns is not touched.
        ///
        /// It is cached by (spell, grade) and ALL the server's fights share it. Writing one of its
        /// fields -- lowering the cost, raising the range -- changes it for everybody at once and for
        /// the rest of the game, and gives no error anywhere: only a spell that starts behaving oddly
        /// and nobody knows since when.
        ///
        /// Where each variable came from is looked up, and then whether anything is written to that
        /// variable. The four places that use it today only read.
        /// </summary>
        private static void AssertCachedSpellDataIsNotMutated(List<Fuente> fuentes)
        {
            var deDondeSale = new Regex(@"var\s+(?<cual>\w+)\s*=[^;]*GetSpellCombatData\s*\(");

            foreach (var f in fuentes)
            {
                foreach (Match m in deDondeSale.Matches(f.Texto))
                {
                    string cual = m.Groups["cual"].Value;

                    // «something.Whatever = » but not «==», which is a comparison.
                    var leEscribe = new Regex(@"\b" + Regex.Escape(cual) + @"\.\w+\s*(?:\+|-|\*|/)?=(?!=)");
                    Match escritura = leEscribe.Match(f.Texto);
                    if (!escritura.Success) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' escribe en lo que devuelve " +
                        $"GetSpellCombatData («{Recortar(escritura.Value)}»). Eso está cacheado y lo " +
                        "comparten todos los combates: cambiarlo aquí se lo cambia a todo el mundo.");
                }
            }
        }

        /// <summary>So that the error message fits on a console line.</summary>
        private static string Recortar(string texto)
        {
            string plano = Regex.Replace(texto.Trim(), @"\s+", " ");
            return plano.Length <= 110 ? plano : plano.Substring(0, 107) + "...";
        }
    }
}
