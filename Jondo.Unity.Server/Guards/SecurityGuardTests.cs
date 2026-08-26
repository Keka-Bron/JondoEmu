using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// Las guardias que miran el CÓDIGO FUENTE, no lo que hace el programa.
    ///
    /// Cazan la clase de fallo que no da error por ningún lado: una consulta a la base sin
    /// filtrar por dueño, una contraseña escrita en el propio código, una comprobación de
    /// propiedad que se apaga sola. Nada de eso rompe una partida, no sale en el registro y no
    /// se nota jugando; sólo se ve leyendo, y por eso se lee aquí en cada arranque.
    ///
    /// Se comprueba sobre el texto y no ejecutando a propósito. Probar lo del dueño de verdad
    /// haría falta montar una base con dos personajes aquí dentro, y lo que se quiere evitar no
    /// es que falle un caso concreto: es que alguien vuelva a ESCRIBIR el mismo patrón dentro de
    /// seis meses sin saber que ya pasó una vez.
    ///
    /// Cada guardia lleva escrito qué rompió en su día. Si alguna salta, eso es lo que hay que
    /// leer antes de tocar nada.
    /// </summary>
    public static class SecurityGuardTests
    {
        /// <summary>Un fichero de código leído, con su ruta.</summary>
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

        // ─── Los objetos y su dueño ────────────────────────────────────────────────────

        /// <summary>
        /// Nada que escriba en CharacterItems o en el cofre puede ir sin CharacterId.
        ///
        /// El uid lo elige el CLIENTE. SaveItemPosition hacía «UPDATE CharacterItems SET
        /// Position = $pos WHERE Uid = $uid» sin mirar de quién es, así que un iuk con el número
        /// de un objeto ajeno lo cambiaba de hueco igual. El uid es único en todo el servidor
        /// —hay índice único y se reparten con un MAX global— así que no era medio mundo a la
        /// vez, pero sí el objeto de otro, y desde una conexión que aún no ha presentado ticket.
        ///
        /// Vale también para las sentencias que van detrás de una comprobación de propiedad y
        /// por eso «no hacen falta»: se reordena el código, se mueve el guardia, y la sentencia
        /// se queda igual sin que nadie lo note.
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

                    // Sin WHERE no es que le falte el dueño: es que le falta todo, y eso se ve
                    // en cuanto se prueba. Lo peligroso es el que SÍ filtra, pero por lo que no es.
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

        // ─── Las contraseñas ───────────────────────────────────────────────────────────

        /// <summary>
        /// La contraseña no se compara dentro de un SELECT.
        ///
        /// «WHERE Login = $login AND Password = $pass» sólo funciona si la clave está guardada
        /// tal cual se escribió, o sea que la comparación en SQL y el guardarlas en claro son la
        /// misma decisión. Se comprueba en Managers.Claves, contra el resumen.
        /// </summary>
        private static void AssertNoPlaintextPasswordComparison(List<Fuente> fuentes)
        {
            var cotejo = new Regex(@"Password\s*=\s*\$\w+", RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                foreach (Match m in cotejo.Matches(f.Texto))
                {
                    // El UPDATE que la ESCRIBE lleva «Password = $pass» y es correcto. Lo que no
                    // puede haber es un SELECT que la use para cotejar, así que se mira de qué
                    // consulta forma parte.
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
        /// No se siembran cuentas con la contraseña escrita en el código.
        ///
        /// Toda base recién hecha nacía con 'keka' y 'dragonlord' como ADMINISTRADOR y la clave
        /// 'test' — publicada en el repositorio, así que quien arrancase el servidor lo tenía
        /// abierto de par en par sin enterarse.
        /// </summary>
        private static void AssertNoSeededCredentials(List<Fuente> fuentes)
        {
            var alta = new Regex(@"INSERT[^;""]{0,200}?INTO\s+Accounts\b[^;""]*", RegexOptions.IgnoreCase);

            foreach (var f in fuentes)
            {
                foreach (Match m in alta.Matches(f.Texto))
                {
                    if (m.Value.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // El alta de verdad pasa la clave como parámetro; una siembra la lleva dentro.
                    if (m.Value.IndexOf("$pass", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    throw new InvalidOperationException(
                        $"[SecurityGuard FAILED] '{f.Nombre}' da de alta una cuenta con la " +
                        $"contraseña escrita en el código: «{Recortar(m.Value)}».");
                }
            }
        }

        // ─── Las comprobaciones que se apagan solas ────────────────────────────────────

        /// <summary>
        /// Una comprobación de propiedad no puede depender de que haya cuenta.
        ///
        /// Estaba escrita como «accountId > 0 &amp;&amp; !CharacterBelongsToAccount(...)», así que
        /// justo en el caso que tenía que cazar —un socket que manda el kvw antes del kqz y llega
        /// con cuenta cero— se saltaba entera y cargaba la ficha de cualquiera. Sin cuenta se
        /// RECHAZA, que no es lo mismo que dejar pasar.
        ///
        /// La primera versión de esta guardia buscaba «accountId &gt; 0 &amp;&amp; !» a secas y saltaba
        /// con ClientLaunchRegistry.RegisterToken, que es
        /// «accountId &gt; 0 &amp;&amp; !string.IsNullOrWhiteSpace(token)» y está PERFECTO: ahí el &amp;&amp;
        /// guarda el camino bueno, no una comprobación. Lo que hace peligroso al patrón no es el
        /// «&amp;&amp; !», es que lo negado sea una pregunta de propiedad, así que se piden las dos
        /// cosas.
        /// </summary>
        private static void AssertOwnershipChecksAreNotSelfDisabling(List<Fuente> fuentes)
        {
            // Las preguntas que deciden si algo es tuyo. Si aparece una detrás de un «hay cuenta
            // && !», la comprobación se apaga sola en el caso que importa.
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

        // ─── Lo que llega por el socket ────────────────────────────────────────────────

        /// <summary>
        /// El varint de longitud de trama tiene tope.
        ///
        /// Sin él, cinco bytes «FF FF FF FF 07» pedían un array de 2 GB antes de leer un solo
        /// byte de contenido, y ocho conexiones bastaban para tumbar el servidor sin
        /// autenticarse. Se comprueba que ReadFrameAsync siga mirando MaxFrameLength.
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
        /// Los cuerpos y los identificadores de sesión no se escriben en el registro sin tapar.
        ///
        /// El registro va a la consola, a logs\emulator_console.log y al buffer que sirve
        /// /api/registro. Por ahí pasaban en claro las contraseñas de entrar y de crear cuenta.
        /// </summary>
        private static void AssertSecretsAreNotLogged(List<Fuente> fuentes)
        {
            // Los sitios que vuelcan algo que viene del cliente, y con qué hay que taparlo.
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

        // ─── Guardias que no guardan ───────────────────────────────────────────────────

        /// <summary>
        /// Una guardia que falla tiene que PARAR el arranque.
        ///
        /// La de ConnectionProtocolSelfTest imprimía los fallos en rojo y hacía «return», o sea
        /// que el servidor arrancaba igual con el protocolo roto — y lo que se ve entonces en el
        /// cliente no es un error, es una pantalla en blanco. Era la única de las ocho que no
        /// lanzaba.
        /// </summary>
        private static void AssertGuardsThrowInsteadOfReturning(List<Fuente> fuentes)
        {
            foreach (var f in fuentes)
            {
                if (f.Nombre != "ConnectionProtocolSelfTest.cs") continue;

                int fallos = f.Texto.IndexOf("if (failures.Count > 0)", StringComparison.Ordinal);
                if (fallos < 0) return;

                // El bloque que sigue: hasta el cierre de la llave, más o menos.
                int fin = f.Texto.IndexOf("\n        }", fallos, StringComparison.Ordinal);
                string bloque = fin < 0 ? f.Texto.Substring(fallos) : f.Texto.Substring(fallos, fin - fallos);

                if (bloque.Contains("throw")) return;

                throw new InvalidOperationException(
                    "[SecurityGuard FAILED] ConnectionProtocolSelfTest imprime los fallos y sigue " +
                    "adelante. Una guardia de bytes que no para el arranque no guarda nada: el " +
                    "cliente se queda en negro y no dice por qué.");
            }
        }

        // ─── Los ficheros de datos ─────────────────────────────────────────────────────

        /// <summary>
        /// Los ficheros de datos se abren por Paths, no por ruta relativa.
        ///
        /// WorldEntry y Summons los abrían relativos, y funcionaba de milagro: sólo porque el
        /// lanzador deja el directorio de trabajo en la raíz. Arrancado de cualquier otra forma
        /// la ficha de características caía de 120 entradas a 25 —y con ella crítico, potencia,
        /// alcance y resistencias— sin un solo mensaje de error.
        /// </summary>
        private static void AssertDataFilesGoThroughPaths(List<Fuente> fuentes)
        {
            // Una ruta relativa a datos\ o dofus3_data\ metida a mano en una llamada de fichero.
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
        /// Lo que devuelve GetSpellCombatData no se toca.
        ///
        /// Está cacheado por (hechizo, grado) y lo comparten TODOS los combates del servidor.
        /// Escribirle un campo —bajarle el coste, subirle el alcance— se lo cambia a todo el
        /// mundo a la vez y para el resto de la partida, y no da error por ningún lado: sólo un
        /// hechizo que empieza a portarse raro y nadie sabe desde cuándo.
        ///
        /// Se busca de dónde salió cada variable y luego si a esa variable se le escribe algo.
        /// Los cuatro sitios que lo usan hoy sólo leen.
        /// </summary>
        private static void AssertCachedSpellDataIsNotMutated(List<Fuente> fuentes)
        {
            var deDondeSale = new Regex(@"var\s+(?<cual>\w+)\s*=[^;]*GetSpellCombatData\s*\(");

            foreach (var f in fuentes)
            {
                foreach (Match m in deDondeSale.Matches(f.Texto))
                {
                    string cual = m.Groups["cual"].Value;

                    // «cual.LoQueSea = » pero no «==», que es una comparación.
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

        /// <summary>Para que el mensaje de error quepa en una línea de consola.</summary>
        private static string Recortar(string texto)
        {
            string plano = Regex.Replace(texto.Trim(), @"\s+", " ");
            return plano.Length <= 110 ? plano : plano.Substring(0, 107) + "...";
        }
    }
}
