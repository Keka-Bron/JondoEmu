using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher
{
    public static class RegressionGuardTests
    {
        private static readonly string[] ForbiddenLiterals = new string[]
        {
            "670668947750",
            "-20003",
            "\"Fortellon\""
        };

        public static void Run()
        {
            // The connection phase messages are always checked, in deployment too: that is where
            // structural bugs used to slip through, and all the client shows there is a blank
            // screen with no error at all.
            Network.ConnectionProtocolSelfTest.Run();
            Network.ClientLaunchRegistry.AssertTwoClientsAreIsolated();
            Network.ClientLaunchRegistry.AssertEightClientLimit();
            AssertPerSessionPlayerCaches();
            AssertSocketWritesAreSerialized();
            AssertProfessionCatalog();
            AssertRelativeMapLookup();
            AssertInteractiveRegistry();

            // Las que prueban lo que el emulador HACE cuando le llega algo que no debería. No
            // hace falta el código fuente al lado para éstas, así que corren siempre, también en
            // el despliegue.
            AssertCharacterSelectionNeedsAnAccount();
            AssertOversizedFramesAreRefused();
            AssertMalformedProtobufIsSurvivable();
            AssertPasswordsAreHashed();
            AssertSecretsAreCensored();
            AssertFightLockIsPerSession();
            AssertItemUidsAreServerWide();

            // El barrido del código fuente llevaba muerto desde que se reorganizaron las
            // carpetas: subía tres niveles desde el binario y bajaba a "Jondo.Unity.Launcher",
            // que no da con nada ni corriendo desde bin\<config>\<tfw>\ ni desde el despliegue.
            // Se salía siempre por el return de la ruta que no existe, sin comprobar una línea,
            // y encima escribiendo que había pasado.
            //
            // Ahora la raíz se busca subiendo hasta dar con el .sln, que es el único punto fijo
            // en las dos formas de arrancar. Si no aparece —porque se reparta el binario sin el
            // código al lado— se salta callando, que es lo honrado: no hay nada que mirar, no
            // es que esté todo bien.
            string? raiz = BuscarLaRaizDelCodigo();
            if (raiz == null) return;

            BarrerElCodigo(raiz);
        }

        /// <summary>
        /// La carpeta que tiene el .sln, subiendo desde donde está el binario.
        ///
        /// Es el único punto fijo que sirve para las dos formas de arrancar: desde
        /// bin\&lt;config&gt;\&lt;tfw&gt;\&lt;rid&gt;\ hay que subir cuatro carpetas, y desde el despliegue
        /// —el .exe está en la raíz— ninguna. Contar niveles a mano falla en una de las dos, y
        /// falla en silencio, que es exactamente lo que llevaba pasando.
        /// </summary>
        private static string? BuscarLaRaizDelCodigo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            for (int subidas = 0; dir != null && subidas < 8; subidas++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Jondo.Unity.sln"))) return dir.FullName;
            }
            return null;
        }

        /// <summary>
        /// Lee todo el código de los tres proyectos y se lo pasa a las guardias que miran texto.
        ///
        /// Se lee una vez y se reparte, en vez de que cada guardia recorra el disco por su cuenta:
        /// son unos cuantos cientos de ficheros y esto corre en cada arranque.
        /// </summary>
        private static void BarrerElCodigo(string raiz)
        {
            var fuentes = new List<SecurityGuardTests.Fuente>();

            foreach (string proyecto in new[] { "Jondo.Unity.Server", "Jondo.Unity.World", "Jondo.Unity.Launcher" })
            {
                string carpeta = Path.Combine(raiz, proyecto);
                if (!Directory.Exists(carpeta)) continue;

                foreach (string fichero in Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories))
                {
                    // bin y obj llevan copias y código generado por el compilador: dan falsos
                    // positivos y además multiplican por tres lo que hay que leer.
                    string sep = Path.DirectorySeparatorChar.ToString();
                    if (fichero.Contains(sep + "obj" + sep) || fichero.Contains(sep + "bin" + sep)) continue;

                    string nombre = Path.GetFileName(fichero);
                    if (nombre == "BasePayloads.cs" || nombre == "TransitionPayloads.cs" ||
                        nombre == "RegressionGuardTests.cs" || nombre == "SecurityGuardTests.cs")
                        continue;

                    fuentes.Add(new SecurityGuardTests.Fuente(fichero, File.ReadAllText(fichero)));
                }
            }

            // Sin ficheros no se dice que haya pasado: no se ha mirado nada.
            if (fuentes.Count == 0) return;

            foreach (var fuente in fuentes)
            {
                foreach (var literal in ForbiddenLiterals)
                {
                    if (fuente.Texto.Contains(literal))
                        throw new InvalidOperationException(
                            $"[RegressionGuard FAILED] '{fuente.Nombre}' lleva el literal de captura " +
                            $"prohibido '{literal}'.");
                }
            }

            SecurityGuardTests.Run(fuentes);

            Console.WriteLine($"[RegressionGuard] {fuentes.Count} ficheros de código barridos: sin " +
                              "literales de captura y sin ninguna de las ocho marcas de seguridad.");
        }

        /// <summary>
        /// Sin cuenta resuelta no se selecciona personaje.
        ///
        /// Esto se comprueba EJECUTÁNDOLO, no leyendo el código: se le manda un kvw de verdad
        /// con cuenta cero, que es lo que llega si alguien abre un socket y manda la selección
        /// sin presentar antes el ticket del kqz. Tiene que decir que no.
        ///
        /// La comprobación estaba escrita como «accountId > 0 && !EsSuyo(...)», así que con
        /// cuenta cero se saltaba entera y cargaba la ficha de quien fuera —y después se
        /// escribía encima al guardar, con lo que no era sólo mirar—. Ninguna partida se rompía
        /// por eso, así que sin esta prueba no había forma de enterarse.
        /// </summary>
        private static void AssertCharacterSelectionNeedsAnAccount()
        {
            // Un kvw como el que manda el cliente: el id del personaje en el f1.
            byte[] kvw = Network.ConnectionProtocol.Push(
                Protocol.Op.Kvw, Network.Pb.New().Var(1, 302677754146L).Build());

            if (Handlers.CharacterSelectionHandler.HandleCharacterSelectionRequest(kvw, 0))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La selección de personaje sale bien con cuenta cero. " +
                    "Un socket que no ha presentado el ticket puede cargar la ficha de cualquiera.");

            // Y con una cuenta que existe pero no es la dueña, tampoco.
            if (Handlers.CharacterSelectionHandler.HandleCharacterSelectionRequest(kvw, -1))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La selección de personaje sale bien con una cuenta " +
                    "que no es la dueña del personaje.");
        }

        /// <summary>
        /// Una trama con la longitud inventada no reserva memoria.
        ///
        /// Cinco bytes «FF FF FF FF 07» pedían un array de 2 GB antes de leer un solo byte de
        /// contenido, y ocho conexiones bastaban para tumbar el servidor sin autenticarse. Se
        /// prueba con el varint más grande que cabe y con uno que no termina nunca.
        /// </summary>
        private static void AssertOversizedFramesAreRefused()
        {
            // 0xFFFFFFFF07 = 2147483647. Antes esto era un new byte[2147483647].
            var enorme = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
            if (Jondo.Protocol.NetworkMessage.ReadFrameAsync(enorme).GetAwaiter().GetResult() != null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Una trama de 2 GB se acepta. El varint de longitud " +
                    "tiene que mirar NetworkMessage.MaxFrameLength.");

            // Un varint que no acaba: sin el corte por número de bytes, el bucle lee para siempre.
            var sinFin = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            if (Jondo.Protocol.NetworkMessage.ReadFrameAsync(sinFin).GetAwaiter().GetResult() != null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Un varint de longitud sin terminar se acepta.");

            // Y lo que sí es una trama tiene que seguir pasando, no vaya a ser que el tope
            // se haya quedado corto.
            byte[] buena = new byte[] { 0x03, 0x0A, 0x01, 0x41 };
            byte[]? leida = Jondo.Protocol.NetworkMessage.ReadFrameAsync(new MemoryStream(buena))
                                                         .GetAwaiter().GetResult();
            if (leida == null || leida.Length != 3)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] El tope de tamaño se ha comido una trama normal.");
        }

        /// <summary>
        /// Un protobuf mal formado no revienta ni se lleva la memoria.
        ///
        /// Lo que entra en ProtoMessage.Parse viene del socket, así que puede estar mal a
        /// propósito. Un campo con longitud mayor que lo que queda pedía el array igual —hasta
        /// 4 GB con cinco bytes— y un tipo de cable de los que no se usan lanzaba una excepción
        /// que nadie recoge, en mitad del enrutado.
        /// </summary>
        private static void AssertMalformedProtobufIsSurvivable()
        {
            // f1, con longitud 0x7FFFFFFF y dos bytes detrás.
            var mentira = new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07, 0x41, 0x42 };
            var leido = Network.ProtoMessage.Parse(mentira);
            if (leido.Fields.Count != 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Un campo que dice medir más que el mensaje se acepta.");

            // Tipo de cable 6, que no existe.
            var inventado = new byte[] { 0x0E, 0x01, 0x02 };
            Network.ProtoMessage.Parse(inventado);

            // Y un mensaje bueno se sigue leyendo entero.
            var bueno = Network.Pb.New().Var(1, 7).Var(2, 9).Build();
            var salida = Network.ProtoMessage.Parse(bueno);
            if (salida.Fields.Count != 2 || salida.Fields[0].VarIntValue != 7 ||
                salida.Fields[1].VarIntValue != 9)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Las cotas se han comido un protobuf correcto.");
        }

        /// <summary>
        /// Las contraseñas van cifradas, y las de antes se siguen aceptando.
        ///
        /// Se guardaban tal cual se escribían y la comparación la hacía el propio SQL. Lo que se
        /// comprueba aquí es lo que hace que el cambio no deje a nadie fuera: una clave escrita
        /// en claro sigue valiendo UNA vez, y en esa vez avisa de que hay que reescribirla.
        /// </summary>
        private static void AssertPasswordsAreHashed()
        {
            string cifrada = Managers.Claves.Cifrar("perro verde");

            if (cifrada.Contains("perro verde"))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La contraseña cifrada lleva dentro la original.");

            if (!Managers.Claves.Comprueba("perro verde", cifrada, out bool reescribir) || reescribir)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Una contraseña recién cifrada no se reconoce.");

            if (Managers.Claves.Comprueba("perro rojo", cifrada, out _))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Una contraseña que no es se da por buena.");

            // Dos veces la misma clave tienen que dar cosas distintas: si no, no hay sal, y con
            // una tabla de resúmenes se sacan todas de golpe.
            if (Managers.Claves.Cifrar("perro verde") == cifrada)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Cifrar la misma contraseña dos veces da lo mismo: " +
                    "falta la sal.");

            // Y la de antes, en claro, tiene que valer y pedir que se reescriba.
            if (!Managers.Claves.Comprueba("test", "test", out bool convertir) || !convertir)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Una contraseña guardada en claro deja de valer, o no " +
                    "avisa de que hay que cifrarla. Eso deja a las cuentas viejas fuera.");
        }

        /// <summary>
        /// Del registro no se saca una contraseña.
        ///
        /// El registro va a la consola, a logs\emulator_console.log y al buffer que sirve
        /// /api/registro. Por ahí pasaban en claro las claves de entrar y de crear cuenta.
        /// </summary>
        private static void AssertSecretsAreCensored()
        {
            string entrada = "{\"usuario\":\"keka\",\"clave\":\"la de verdad\"}";
            string tapado = Network.Censura.Cuerpo(entrada);

            if (tapado.Contains("la de verdad"))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La contraseña se escribe en el registro tal cual.");

            if (!tapado.Contains("usuario") || !tapado.Contains("keka"))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La censura se ha llevado por delante lo que sí hay " +
                    "que ver: el registro deja de servir para nada.");

            if (Network.Censura.Valor("2f9c1a4b8e").Contains("1a4b8e"))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Un identificador de sesión se escribe entero.");
        }

        /// <summary>
        /// El candado de combate es de cada sesión, no de todas.
        ///
        /// Era un SemaphoreSlim estático en FightHandler, uno para las ocho conexiones, y se
        /// sostiene durante la escritura en el socket: un cliente lento dejaba a todos los demás
        /// sin poder mover ficha en su propio combate. Se comprueba que dos sesiones puedan
        /// tenerlo a la vez, que es exactamente lo que antes no pasaba.
        /// </summary>
        private static void AssertFightLockIsPerSession()
        {
            var uno = Network.GameSession.SinSocket();
            var otro = Network.GameSession.SinSocket();

            if (ReferenceEquals(uno.UnoCadaVez, otro.UnoCadaVez))
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Dos sesiones comparten el candado de combate.");

            uno.UnoCadaVez.Wait();
            try
            {
                if (!otro.UnoCadaVez.Wait(0))
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Una sesión con el candado cogido bloquea a otra. " +
                        "Un cliente lento congela los combates de todos los demás.");
                otro.UnoCadaVez.Release();
            }
            finally
            {
                uno.UnoCadaVez.Release();
            }
        }

        /// <summary>
        /// Los uid de objeto se reparten para todo el servidor, no por personaje.
        ///
        /// Esto era una pérdida de objetos de verdad, comprobada sobre una copia de la base. El
        /// uid es único en toda la tabla —hay índice único, y SaveInventoryItem hace
        /// ON CONFLICT(Uid) DO UPDATE— pero se repartía mirando SÓLO el inventario del personaje:
        /// «el mayor uid que tengo yo, más uno». Dos personajes nuevos empiezan los dos por el 1,
        /// así que el segundo que looteara no añadía su objeto: PISABA el del primero. Al primero
        /// se le convertía la pluma de piwi en la semilla del otro y al segundo no le llegaba
        /// nada, sin un solo mensaje de error.
        /// </summary>
        private static void AssertItemUidsAreServerWide()
        {
            long primero = DatabaseManager.NextItemUid();
            long segundo = DatabaseManager.NextItemUid();

            if (segundo <= primero)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] El repartidor de uid devuelve el mismo número dos " +
                    "veces. Dos objetos con el mismo uid se pisan en la base.");

            // Y por encima de lo que ya hay escrito, que es lo que evita chocar con lo existente.
            long enUso = DatabaseManager.MayorUidGuardado();
            if (primero <= enUso)
                throw new InvalidOperationException(
                    $"[RegressionGuard FAILED] El repartidor de uid da {primero} y en la base ya hay " +
                    $"hasta {enUso}. El siguiente objeto que se guarde pisará uno que existe.");
        }

        private static void AssertPerSessionPlayerCaches()
        {
            var first = Network.GameSession.SinSocket();
            var second = Network.GameSession.SinSocket();

            first.State.EquipmentItems[101] = new Managers.Equipment.Item { Uid = 101 };
            first.State.ChosenSpells[1] = 1001;
            first.State.SpellBar[0] = 1001;
            first.State.OpenNpcShopId = 11;

            second.State.EquipmentItems[202] = new Managers.Equipment.Item { Uid = 202 };
            second.State.ChosenSpells[1] = 2002;
            second.State.SpellBar[0] = 2002;
            second.State.OpenNpcShopId = 22;

            using (Network.SessionContext.Push(first))
            {
                if (Managers.Equipment.ByUid(101) == null || Managers.Equipment.ByUid(202) != null ||
                    Managers.SpellChoices.Chosen[1] != 1001 || Managers.SpellChoices.Bar[0] != 1001 ||
                    first.State.OpenNpcShopId != 11)
                {
                    throw new InvalidOperationException("[RegressionGuard FAILED] First player cache leaked across sessions.");
                }
            }

            using (Network.SessionContext.Push(second))
            {
                if (Managers.Equipment.ByUid(202) == null || Managers.Equipment.ByUid(101) != null ||
                    Managers.SpellChoices.Chosen[1] != 2002 || Managers.SpellChoices.Bar[0] != 2002 ||
                    second.State.OpenNpcShopId != 22)
                {
                    throw new InvalidOperationException("[RegressionGuard FAILED] Second player cache leaked across sessions.");
                }
            }
        }

        private static void AssertSocketWritesAreSerialized()
        {
            var stream = new OverlapDetectingStream();
            Task.WhenAll(
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 1, 2, 3 }),
                Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, new byte[] { 4, 5, 6 }),
                Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, new byte[] { 7, 8, 9 }))
                .GetAwaiter().GetResult();

            if (stream.OverlapDetected)
                throw new InvalidOperationException("[RegressionGuard FAILED] Packet writes overlapped on one socket.");
        }

        private static void AssertInteractiveRegistry()
        {
            var expected = new HashSet<(long MapId, int ElementId)>();
            foreach (long mapId in Managers.Interactives.MapIds)
            {
                foreach (var zaap in Managers.Interactives.ZaapElements(mapId))
                    expected.Add((mapId, zaap.Id));

                var chest = Managers.Merkasako.ChestOf(mapId);
                if (chest.Id != 0) expected.Add((mapId, chest.Id));

                var lottery = Managers.Lottery.Of(mapId);
                if (lottery.Id != 0) expected.Add((mapId, lottery.Id));

                // Los zaapis y las papeleras se reconocen por su gráfico y son decenas, así que van
                // en bloque. Sin esto la cuenta no cuadra y el servidor no arranca — que es lo que
                // pasó al añadirlos: el guardia los vio antes que nadie.
                foreach (var zaapi in Managers.Zaapis.ElementsOn(mapId))
                    expected.Add((mapId, zaapi.Id));

                foreach (var bin in Managers.Bins.On(mapId))
                    expected.Add((mapId, bin.Id));

                foreach (var door in Managers.Houses.On(mapId))
                    expected.Add((mapId, door.ElementId));

                foreach (var teleport in Managers.TeleportManager.On(mapId))
                {
                    expected.Add((mapId, teleport.ElementId));
                    if (Managers.Houses.TryGetDoor(mapId, teleport.ElementId, out _))
                        throw new InvalidOperationException(
                            "[RegressionGuard FAILED] Un paso genérico pisa una puerta de casa.");
                }

                foreach (var resource in Managers.Resources.On(mapId))
                    expected.Add((mapId, resource.ElementId));

                foreach (var interactive in Managers.InteractiveRegistry.OnMap(mapId))
                {
                    foreach (var action in interactive.Actions)
                    {
                        if (!Managers.InteractiveRegistry.TryResolveUse(
                                mapId, interactive.Element.Id, action.SkillInstanceId,
                                out var resolved, out var resolvedAction) ||
                            !ReferenceEquals(interactive, resolved) ||
                            !ReferenceEquals(action, resolvedAction))
                        {
                            throw new InvalidOperationException(
                                "[RegressionGuard FAILED] Interactive registry cannot resolve its own declaration.");
                        }
                    }

                    if (Managers.InteractiveRegistry.TryResolveUse(
                            mapId, interactive.Element.Id, int.MaxValue, out _, out _))
                    {
                        throw new InvalidOperationException(
                            "[RegressionGuard FAILED] Interactive registry accepted a mismatched skill instance.");
                    }
                }
            }

            // Los interiores de casa no estan en Interactives.MapIds -son mapas sin elementos
            // en el volcado del cliente- asi que se cuentan aparte.
            foreach (long interior in Managers.Houses.Interiors)
            {
                if (Managers.Houses.TryGetExit(interior, out var exit))
                    expected.Add((interior, exit.ElementId));
            }

            // Regla de Giny: cada paso es un elemento clicable con USE114, y la misma ruta se
            // encuentra por su casilla. Lo de la casilla ya no dispara nada —el enganche al jqi
            // se quedó fuera— pero el índice sigue existiendo y esto es lo que caza que dos rutas
            // acaben en la misma casilla, que es un dato malo, no una casualidad.
            foreach (var teleport in Managers.TeleportManager.All)
            {
                if (!Managers.InteractiveRegistry.TryResolveUse(
                        teleport.SourceMapId, teleport.ElementId,
                        Managers.Interactives.SkillInstanceOf(teleport.ElementId),
                        out var interactive, out var action) ||
                    interactive.Element.Id != teleport.ElementId ||
                    interactive.Element.Cell != teleport.SourceCellId ||
                    interactive.Element.Gfx != teleport.GfxId ||
                    interactive.Type != Managers.TeleportManager.GenericTeleportType ||
                    teleport.InteractiveType != Managers.TeleportManager.GenericTeleportType ||
                    action.Kind != Managers.InteractiveActionKind.Teleport ||
                    action.SkillId != 114 || teleport.SkillId != 114 ||
                    !Managers.TeleportManager.TryGetCellTrigger(
                        teleport.SourceMapId, teleport.SourceCellId, out var cellRoute) ||
                    !ReferenceEquals(teleport, cellRoute))
                {
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] El paso {teleport.SourceMapId}/" +
                        $"{teleport.ElementId} no es un interactivo clicable f11/f15.");
                }
            }

            // Tres rutas concretas, con sus números, para que un cambio en el catálogo no pase
            // desapercibido: la de Astrub al templo, la ida y vuelta del taller del joyero, y una
            // escalera —gfx 62018— que va exactamente por el mismo camino que un sol.
            if (!Managers.TeleportManager.TryGet(191106048, 515837, out var astrub) ||
                astrub.DestinationMapId != 192416776 || astrub.DestinationCellId != 534)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Falta o ha cambiado la ruta de Astrub al templo.");
            }

            if (!Managers.TeleportManager.TryGet(188746247, 515801, out var jewellerEntrance) ||
                jewellerEntrance.DestinationMapId != 192937990 ||
                jewellerEntrance.DestinationCellId != 400 ||
                !Managers.TeleportManager.TryGet(192937990, 515742, out var jewellerExit) ||
                jewellerExit.DestinationMapId != 188746247 ||
                jewellerExit.SourceCellId != 414 || jewellerExit.DestinationCellId != 358 ||
                !Managers.TeleportManager.TryGetCellTrigger(192937990, 414, out var cellExit) ||
                !ReferenceEquals(jewellerExit, cellExit) ||
                !Managers.InteractiveRegistry.TryResolveUse(
                    192937990, 515742, Managers.Interactives.SkillInstanceOf(515742),
                    out var jewellerInteractive, out var jewellerAction) ||
                jewellerInteractive.Element.Cell != 414 || jewellerInteractive.Element.Gfx != 3520 ||
                jewellerInteractive.Type != Managers.TeleportManager.GenericTeleportType ||
                jewellerAction.Kind != Managers.InteractiveActionKind.Teleport ||
                jewellerAction.SkillId != 114)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Falta o ha cambiado la ida y vuelta del taller del " +
                    "joyero de Astrub.");
            }

            if (!Managers.TeleportManager.TryGet(192940038, 515691, out var stairExit) ||
                stairExit.SourceCellId != 327 || stairExit.DestinationMapId != 188744711 ||
                stairExit.DestinationCellId != 427 || stairExit.GfxId != 62018 ||
                stairExit.InteractiveType != Managers.TeleportManager.GenericTeleportType ||
                stairExit.SkillId != 114 ||
                !Managers.TeleportManager.TryGetCellTrigger(192940038, 327, out var stairCellRoute) ||
                !ReferenceEquals(stairExit, stairCellRoute) ||
                !Managers.InteractiveRegistry.TryResolveUse(
                    192940038, 515691, Managers.Interactives.SkillInstanceOf(515691),
                    out var stairInteractive, out var stairAction) ||
                stairInteractive.Element.Gfx != 62018 || stairInteractive.Element.Cell != 327 ||
                stairInteractive.Type != Managers.TeleportManager.GenericTeleportType ||
                stairAction.Kind != Managers.InteractiveActionKind.Teleport ||
                stairAction.SkillId != 114)
            {
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La escalera 192940038/515691 tiene que ser un paso " +
                    "clicable.");
            }

            if (Managers.InteractiveRegistry.Count != expected.Count)
            {
                throw new InvalidOperationException(
                    $"[RegressionGuard FAILED] Expected {expected.Count} interactives, got " +
                    $"{Managers.InteractiveRegistry.Count}.");
            }
        }

        private static void AssertProfessionCatalog()
        {
            if (Managers.JobManager.Count == 0 || Managers.SkillManager.Count == 0 ||
                Managers.RecipeManager.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Profession catalogue is empty.");

            foreach (var skill in Managers.SkillManager.All)
            {
                if (!Managers.JobManager.TryGet(skill.ParentJobId, out _))
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] Skill {skill.Id} references missing job {skill.ParentJobId}.");
            }

            foreach (var recipe in Managers.RecipeManager.All)
            {
                if (!Managers.JobManager.TryGet(recipe.JobId, out _) ||
                    !Managers.SkillManager.TryGet(recipe.SkillId, out _) ||
                    recipe.Ingredients.Count == 0 ||
                    recipe.Ingredients.Any(i => i.ItemId <= 0 || i.Quantity <= 0))
                    throw new InvalidOperationException(
                        $"[RegressionGuard FAILED] Recipe {recipe.ResultId} is inconsistent.");
            }

            // El catalogo tiene que traer habilidades de recoleccion y el mundo tiene que tener
            // recursos con esas habilidades. Antes esto llamaba a GatheringHandler.TryResolve,
            // que solo cruzaba tablas en memoria y no mandaba un byte; ahora el handler recolecta
            // de verdad y lo que hay que comprobar es que haya algo que recolectar.
            var gathering = Managers.SkillManager.All.FirstOrDefault(s => s.IsGathering);
            if (gathering == null)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] El catalogo no trae ninguna habilidad de recoleccion.");

            if (Managers.Resources.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] No hay ningun recurso recolectable en el mundo.");

            // Y la cantidad tiene que respetar lo medido: un oficio al tope sobre el recurso mas
            // facil da veinte, y uno recien empezado da cuatro.
            if (Handlers.GatheringHandler.Ceiling(200, 1) != 20 ||
                Handlers.GatheringHandler.Ceiling(200, 80) != 13 ||
                Handlers.GatheringHandler.Ceiling(1, 1) != 4)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La cantidad recolectada no cuadra con las capturas.");

            // Y la curva de experiencia, con los tres puntos que dan las capturas.
            if (Managers.JobExperience.Floor(2) != 20 || Managers.JobExperience.Floor(3) != 60 ||
                Managers.JobExperience.Floor(200) != 398000 ||
                Managers.JobExperience.LevelOf(20) != 2 || Managers.JobExperience.LevelOf(19) != 1)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] La curva de experiencia de oficio no cuadra.");

            var craft = Managers.RecipeManager.All.First();
            if (!Handlers.CraftHandler.TryResolve(craft.SkillId, out _, out _, out var recipes, out _) ||
                !Handlers.CraftHandler.TryResolveRecipe(craft.SkillId, craft.ResultId, out _, out _) ||
                recipes.Count == 0)
                throw new InvalidOperationException(
                    "[RegressionGuard FAILED] Craft handler cannot resolve a known recipe.");
        }

        private static void AssertRelativeMapLookup()
        {
            var group = MapManager.Maps.Values
                .Where(m => m.MapId > 0)
                .GroupBy(m => (m.PosX, m.PosY))
                .FirstOrDefault(g => g.Count() > 1);
            if (group == null) return;

            var ordered = group.OrderBy(m => m.MapId).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var match = Managers.MapLookup.NextRelative(ordered[i].MapId);
                long expected = ordered[(i + 1) % ordered.Count].MapId;
                if (match == null || match.Map.MapId != expected ||
                    match.Candidates != ordered.Count ||
                    match.Wrapped != (i == ordered.Count - 1))
                    throw new InvalidOperationException(
                        "[RegressionGuard FAILED] Relative map cycle is not stable.");
            }
        }

        private sealed class OverlapDetectingStream : Stream
        {
            private int _activeWrites;
            public bool OverlapDetected { get; private set; }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async Task WriteAsync(byte[] buffer, int offset, int count,
                                                  CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref _activeWrites) > 1) OverlapDetected = true;
                try
                {
                    await Task.Delay(10, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeWrites);
                }
            }
        }
    }
}
