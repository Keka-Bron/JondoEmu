using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Por dónde le habla el lanzador al servidor.
    ///
    /// Esto existió y se borró. Lo dice el comentario que quedó en <see cref="HaapiServer"/>: las
    /// rutas /api/login, /api/register, /api/launch, /api/status y /api/logs vivían ahí y se
    /// quitaron al pasar de la interfaz web a la ventana nativa, porque la ventana llamaba a
    /// LauncherService directamente y aquello era peso muerto. En cuanto el lanzador y el servidor
    /// son dos procesos vuelve a hacer falta, así que vuelve.
    ///
    /// Va colgada del HAAPI, en el 8888, y no en un puerto nuevo, por tres razones:
    ///
    ///   * es el puerto que el mod del cliente sondea para decidir si redirige al emulador
    ///     (JondoFix/Class1.cs:471), así que «el 8888 contesta» es exactamente la señal de vida que
    ///     el lanzador necesita antes de arrancar un cliente;
    ///   * el HAAPI ya es un HttpListener atado a localhost y a 127.0.0.1, y a nada más;
    ///   * es donde estaba.
    ///
    /// LO QUE AQUÍ SE DECIDE ES DEL SERVIDOR. Este fichero no sabe nada de ventanas: recibe texto,
    /// llama a la base y al registro de lanzamientos, y devuelve texto. Los mensajes para el
    /// usuario NO se traducen aquí —viajan como código— porque el idioma es del lanzador.
    /// </summary>
    public static class ControlApi
    {
        /// <summary>Las rutas y la cabecera salen del contrato, que es lo que comparten los dos.</summary>
        public const string Prefijo = Jondo.Unity.Launcher.Contract.Prefijo;

        // ─── El secreto ─────────────────────────────────────────────────────────────────────
        //
        // Uno por arranque: así un lanzador de una sesión anterior no se queda con llave de la de
        // ahora. Lo guarda el contrato, que es quien sabe dónde se escribe y quién lo lee.

        private static string _secreto = "";

        /// <summary>Reparte un secreto nuevo y lo deja escrito. Lo llama el servidor al arrancar.</summary>
        public static void NuevoSecreto() => _secreto = Contract.NuevoSecreto();

        private static bool Autorizada(string? traido) => Contract.MismoSecreto(traido, _secreto);

        // ─── Las respuestas ─────────────────────────────────────────────────────────────────

        /// <summary>Lo que sale por el cable: un código de estado y un cuerpo JSON.</summary>
        public readonly struct Respuesta
        {
            public Respuesta(int codigo, string json) { Codigo = codigo; Json = json; }
            public int Codigo { get; }
            public string Json { get; }
        }

        private static Respuesta Bien(object cuerpo)
            => new Respuesta(200, JsonSerializer.Serialize(cuerpo));

        private static Respuesta Mal(int codigo, string motivo)
            => new Respuesta(codigo, JsonSerializer.Serialize(new { error = motivo }));

        /// <summary>
        /// Contesta una petición de mando. Devuelve null si la ruta no es de aquí, para que el
        /// HAAPI siga con lo suyo.
        /// </summary>
        public static Respuesta? Responder(string ruta, string metodo, string cuerpo, string? secreto,
                                           string ip = "")
        {
            if (!ruta.StartsWith(Prefijo, StringComparison.Ordinal)) return null;

            try
            {
                switch (ruta)
                {
                    // ─── Abiertas ───────────────────────────────────────────────────────────
                    // Cualquiera puede llamarlas, porque hay que poder entrar antes de tener con
                    // qué demostrar quién eres.
                    case Prefijo + "estado": return Estado();
                    case Prefijo + "entrar": return Entrar(cuerpo, ip);
                    case Prefijo + "crear-cuenta": return CrearCuenta(cuerpo, ip);

                    // ─── Con sesión ─────────────────────────────────────────────────────────
                    // Hace falta un token que la base reconozca. Da igual el rol: son cosas que
                    // cualquier jugador hace con su propia cuenta.
                    case Prefijo + "activos": return ConSesion(cuerpo, _ => Activos());
                    case Prefijo + "recordar-token": return RecordarToken(cuerpo);
                    case Prefijo + "lanzamiento": return ConSesion(cuerpo, cuenta => Lanzamiento(cuerpo, cuenta, ip));
                    case Prefijo + "fin-de-lanzamiento":
                        return ConSesion(cuerpo, cuenta => FinDeLanzamiento(cuenta));

                    // ─── De administración ──────────────────────────────────────────────────
                    // Mandan sobre el servidor, no sobre una cuenta. Rol 4 y se comprueba aquí,
                    // en el servidor, cada vez. Que el lanzador enseñe o no el botón es cosmético:
                    // el lanzador está en el ordenador del jugador y ahí no se confía en nada.
                    case Prefijo + "registro": return ConRol(cuerpo, Roles.Administrador, _ => Registro(cuerpo));
                    case Prefijo + "apagar": return ConRol(cuerpo, Roles.Administrador, _ => Apagar());
                    case Prefijo + "rol": return ConRol(cuerpo, Roles.Administrador, _ => CambiarRol(cuerpo));
                    case Prefijo + "personaje":
                        return !metodo.Equals("POST", StringComparison.OrdinalIgnoreCase)
                            ? Mal(405, "metodo")
                            : ConRol(cuerpo, Roles.Administrador, _ => AdministrarPersonaje(cuerpo));

                    default: return Mal(404, "ruta");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] {ruta} ha reventado: {ex.Message}");
                return Mal(500, ex.Message);
            }
        }

        // ─── Quién llama ────────────────────────────────────────────────────────────────────
        //
        // Antes esto lo guardaba un secreto que el servidor escribía en %APPDATA% y el lanzador
        // leía de ahí. Servía mientras los dos estaban en la misma máquina, y deja de servir en
        // cuanto el lanzador se reparte: en el ordenador de otro jugador ese fichero no existe.
        //
        // Ahora manda la CUENTA. El token que el lanzador ya tiene de haber entrado dice quién es,
        // y la base dice qué puede hacer. Funciona igual en local que por Hamachi que contra una
        // VPS, y no hay ningún secreto que repartir.

        private static Respuesta ConSesion(string cuerpo, Func<long, Respuesta> hacer)
        {
            long cuenta = ClientLaunchRegistry.ResolveToken(Texto(cuerpo, "token"));
            if (cuenta <= 0) return Mal(401, "sesion");
            return hacer(cuenta);
        }

        private static Respuesta ConRol(string cuerpo, int haceFalta, Func<long, Respuesta> hacer)
        {
            long cuenta = ClientLaunchRegistry.ResolveToken(Texto(cuerpo, "token"));
            if (cuenta <= 0) return Mal(401, "sesion");

            int rol = DatabaseManager.GetAccountRole(cuenta);
            if (!Roles.AlMenos(rol, haceFalta))
            {
                Console.WriteLine($"[Control] La cuenta {cuenta} ({Roles.Nombre(rol)}) ha intentado algo " +
                                  $"de {Roles.Nombre(haceFalta)}. Rechazado.");
                return Mal(403, "rol");
            }
            return hacer(cuenta);
        }

        /// <summary>Sube o baja el rol de una cuenta. Sólo un administrador llega aquí.</summary>
        private static Respuesta CambiarRol(string cuerpo)
        {
            string quien = Texto(cuerpo, "cuenta");
            int rol = (int)Numero(cuerpo, "rol");
            bool bien = DatabaseManager.SetAccountRole(quien, rol, out int cuantas);
            if (bien) Console.WriteLine($"[Control] {quien} pasa a {Roles.Nombre(rol)}.");
            return Bien(new { bien, cuantas });
        }

        /// <summary>
        /// Cambia las caracteristicas base y los kamas de un personaje conectado, los guarda y
        /// refresca la ficha sin obligarle a salir ni a reiniciar el servidor.
        ///
        /// La ruta pasa por <see cref="ConRol"/> y solo admite administradores. El turno de la
        /// sesion evita que una orden HTTP pise un movimiento, un combate o cualquier otro paquete
        /// que el cliente este atendiendo a la vez.
        /// </summary>
        private static Respuesta AdministrarPersonaje(string cuerpo)
        {
            if (!LiveCharacterUpdate.TryParse(cuerpo, out var update, out string error)
                || update == null)
                return Mal(400, error);
            if (!update.HasChanges) return Mal(400, "sin-cambios");

            var sesion = SessionRegistry.FindByName(update.Character);
            if (sesion == null || !sesion.HasCharacter || !sesion.IsInWorld)
                return Mal(404, "personaje-desconectado");
            if (sesion.Stream == null) return Mal(404, "personaje-desconectado");

            // Validate every operation before touching the character. A bad mount or destination
            // must not leave the earlier fields of the same request half-applied.
            if (update.MapId.HasValue && MapManager.GetMapInfo(update.MapId.Value) == null)
                return Mal(400, "mapa-desconocido");
            if (update.ItemGid.HasValue
                && !DatabaseManager.TryGetItemTemplateEffects((int)update.ItemGid.Value, out _))
                return Mal(400, "objeto-desconocido");
            if (update.MountGid.HasValue
                && (!Managers.Mounts.IsRideable((int)update.MountGid.Value)
                    || !DatabaseManager.TryGetItemTemplateEffects((int)update.MountGid.Value, out _)))
                return Mal(400, "montura-invalida");

            // With a deadline, and not Wait() forever. This runs on the HttpListener's thread and
            // the lock is held across three socket writes: a client that has stopped reading -alt
            // F4 with the socket still open is the usual way- would otherwise park this thread for
            // good, and the control API has a small pool of them. Better a 409 than a listener
            // that stops answering.
            if (!sesion.UnoCadaVez.Wait(PlazoDelTurno)) return Mal(409, "personaje-ocupado");
            try
            {
                using (SessionContext.Push(sesion))
                {
                    var estado = sesion.State;
                    if (estado.IsInFight) return Mal(409, "personaje-en-combate");

                    bool sheetChanged = false;
                    sheetChanged |= Assign(update.Vitality, value => estado.StatVitality = value);
                    sheetChanged |= Assign(update.Wisdom, value => estado.StatWisdom = value);
                    sheetChanged |= Assign(update.Strength, value => estado.StatStrength = value);
                    sheetChanged |= Assign(update.Intelligence, value => estado.StatIntelligence = value);
                    sheetChanged |= Assign(update.Chance, value => estado.StatChance = value);
                    sheetChanged |= Assign(update.Agility, value => estado.StatAgility = value);
                    bool kamasChanged = AssignKamas(update.Kamas, value => estado.Kamas = value);

                    CommandHandler.LevelChange? levelChange = null;
                    if (update.Level.HasValue)
                    {
                        int requested = (int)Math.Clamp(update.Level.Value, int.MinValue, int.MaxValue);
                        levelChange = CommandHandler.SetLevelAsync(sesion.Stream, requested)
                            .GetAwaiter().GetResult();
                    }

                    if (sheetChanged || kamasChanged)
                    {
                        DatabaseManager.SaveCurrentCharacter();
                        sesion.SendAsync(ConnectionProtocol.Push(Op.Kub,
                            ConnectionProtocol.BuildCharacteristics())).GetAwaiter().GetResult();
                        sesion.SendAsync(ConnectionProtocol.Push(Op.Ivf,
                            ConnectionProtocol.BuildKamas(estado.Kamas))).GetAwaiter().GetResult();
                        sesion.SendAsync(ConnectionProtocol.Push(Op.Iun,
                            ConnectionProtocol.BuildPods(0, 1000 + 5L * estado.StatStrength)))
                            .GetAwaiter().GetResult();
                    }

                    Managers.HavenBagStore.StoredItem? granted = null;
                    if (update.ItemGid.HasValue)
                    {
                        granted = CommandHandler.GrantItemAsync(sesion.Stream,
                            (int)update.ItemGid.Value, (int)(update.Quantity ?? 1))
                            .GetAwaiter().GetResult();
                    }

                    Managers.HavenBagStore.StoredItem? mount = null;
                    if (update.MountGid.HasValue)
                    {
                        mount = CommandHandler.GrantItemAsync(sesion.Stream,
                            (int)update.MountGid.Value, 1).GetAwaiter().GetResult();
                        if (mount != null)
                        {
                            byte[] move = ConnectionProtocol.Push(Op.Iuk, Pb.New()
                                .Var(1, 1).Var(2, mount.Uid).Var(3, Managers.Mounts.Slot).Build());
                            EquipmentHandler.MoveAsync(sesion.Stream, move, sesion.AccountId)
                                .GetAwaiter().GetResult();
                        }
                    }

                    int? landed = null;
                    if (update.MapId.HasValue)
                    {
                        int targetCell = (int)Math.Clamp(update.Cell ?? TeleportHandler.MapCentre,
                                                         0, int.MaxValue);
                        landed = TeleportHandler.ToMapAsync(sesion.Stream, update.MapId.Value,
                                                           targetCell).GetAwaiter().GetResult();
                    }

                    Console.WriteLine($"[Control] Personaje {estado.CharacterName}: caracteristicas " +
                                      $"{estado.StatVitality}/{estado.StatWisdom}/{estado.StatStrength}/" +
                                      $"{estado.StatIntelligence}/{estado.StatChance}/{estado.StatAgility}, " +
                                      $"kamas {estado.Kamas}.");
                    return Bien(new
                    {
                        bien = true,
                        personaje = estado.CharacterName,
                        vitalidad = estado.StatVitality,
                        sabiduria = estado.StatWisdom,
                        fuerza = estado.StatStrength,
                        inteligencia = estado.StatIntelligence,
                        suerte = estado.StatChance,
                        agilidad = estado.StatAgility,
                        kamas = estado.Kamas,
                        nivel = estado.CharacterLevel,
                        experiencia = estado.Experience,
                        puntos = estado.CharacterRemainingPoints,
                        nivelAnterior = levelChange?.PreviousLevel,
                        mapa = estado.MapId,
                        celda = estado.CellId,
                        llegada = landed,
                        objetoUid = granted?.Uid,
                        monturaUid = mount?.Uid,
                    });
                }
            }
            finally
            {
                sesion.UnoCadaVez.Release();
            }
        }

        /// <summary>
        /// The ceiling a characteristic set from outside the game is clamped to.
        /// </summary>
        /// <remarks>
        /// It used to be int.MaxValue, which is the one value guaranteed to break: max HP is
        /// computed as <c>50 + level * 5 + vitality</c> in StatsHandler, in int arithmetic and
        /// with no check, so a vitality of int.MaxValue overflows to a NEGATIVE maximum. The
        /// character then has a life bar the client cannot draw and the fight engine treats as
        /// already dead.
        ///
        /// Ten million is far above anything the game reaches — the highest vitality a level 200
        /// character can hold is in the low thousands — and leaves the sum three orders of
        /// magnitude short of overflowing.
        /// </remarks>
        private const int TopeDeCaracteristica = 10_000_000;

        /// <summary>How long to wait for the session's turn before giving up on it.</summary>
        private static readonly TimeSpan PlazoDelTurno = TimeSpan.FromSeconds(5);

        private static bool Assign(long? raw, Action<int> assign)
        {
            if (!raw.HasValue) return false;
            assign((int)Math.Clamp(raw.Value, 0, TopeDeCaracteristica));
            return true;
        }

        private static bool AssignKamas(long? raw, Action<long> assign)
        {
            if (!raw.HasValue) return false;
            assign(Math.Max(0, raw.Value));
            return true;
        }

        // ─── Cada verbo ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Si esto contesta, el servidor está vivo. Aun así se dice lo que hay dentro, porque una
        /// base a medias con los puertos abiertos también es un problema.
        /// </summary>
        private static Respuesta Estado() => Bien(new
        {
            enLinea = ServiciosEnPie() && BaseEnPie(),
            base_ = BaseEnPie(),
            servicios = ServiciosEnPie(),
            version = Contract.Version,
            proceso = Environment.ProcessId,
        });

        private static bool BaseEnPie() => File.Exists(Paths.AuthDb) && File.Exists(Paths.WorldDb);

        private static bool ServiciosEnPie() => ZaapServer.IsRunning && GameServerProxy.IsRunning;

        /// <summary>Quién tiene cliente abierto ahora mismo, y cuántos caben.</summary>
        private static Respuesta Activos()
        {
            var cuentas = new List<long>(ClientLaunchRegistry.ActiveAccounts);
            return Bien(new
            {
                maximo = Contract.ClientesPorIp,
                capacidad = Contract.ClientesEnTotal,
                cuantos = cuentas.Count,
                cuentas,
            });
        }

        /// <summary>Las líneas de consola desde la que ya tenga el lanzador.</summary>
        private static Respuesta Registro(string cuerpo)
        {
            long desde = Numero(cuerpo, "desde");
            return new Respuesta(200, ConsoleLogBuffer.GetLogsJson(desde));
        }

        /// <summary>
        /// Entrar con usuario y contraseña.
        ///
        /// La IP es la DEL SOCKET, no la que venga escrita en el cuerpo. Con la del cuerpo, el
        /// freno de la base —cinco intentos fallidos y un minuto de espera, y lleva la cuenta por
        /// IP— se saltaba cambiando un campo del JSON en cada intento, así que no frenaba nada.
        /// Esto sólo se podía aprovechar desde la propia máquina, porque el HAAPI escucha en
        /// localhost y en 127.0.0.1 y en nada más, pero un freno que no frena es peor que ninguno:
        /// hace creer que hay uno.
        /// </summary>
        private static Respuesta Entrar(string cuerpo, string ip)
        {
            string usuario = Texto(cuerpo, "usuario");
            string clave = Texto(cuerpo, "clave");
            if (ip.Length == 0) ip = Contract.LocalIp;

            if (!DatabaseManager.ValidateAccountCredentials(usuario, clave, ip, out var cuenta, out string fallo)
                || cuenta == null)
            {
                return Bien(new { bien = false, motivo = fallo });
            }

            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetGameToken(cuenta.Id, token);

            // Y el mismo, aparte, como sesión del lanzador. Van a la par ahora y se separan en
            // cuanto el jugador arranque un cliente, que le rota el del juego: si no hubiera esta
            // segunda copia, esa rotación dejaría al lanzador sin sesión para la próxima vez.
            DatabaseManager.SetLauncherToken(cuenta.Id, token);
            ClientLaunchRegistry.RegisterToken(cuenta.Id, token);

            return Bien(new
            {
                bien = true,
                token,
                apodo = cuenta.Nickname ?? "",
                cuenta = cuenta.Id,
                rol = cuenta.Role
            });
        }

        /// <summary>Crear cuenta. La IP, otra vez la del socket y no la que diga el cuerpo.</summary>
        private static Respuesta CrearCuenta(string cuerpo, string ip)
        {
            string usuario = Texto(cuerpo, "usuario");
            string clave = Texto(cuerpo, "clave");
            string apodo = Texto(cuerpo, "apodo");
            if (ip.Length == 0) ip = Contract.LocalIp;

            bool bien = DatabaseManager.RegisterNewAccount(usuario, clave, apodo, ip, out string fallo);
            return Bien(new { bien, motivo = fallo });
        }

        /// <summary>
        /// El lanzador recuerda una sesión de la vez anterior y quiere que el servidor vuelva a
        /// dar por bueno ese token. Sólo se acepta si la base lo reconoce: el lanzador no puede
        /// inventarse la cuenta que quiera.
        /// </summary>
        private static Respuesta RecordarToken(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            string token = Texto(cuerpo, "token");
            if (cuenta <= 0 || token.Length == 0) return Bien(new { bien = false });

            // Vale tanto la sesión del lanzador como el token de juego: las bases de antes de que
            // hubiera columna propia sólo tienen el segundo.
            long deLaBase = DatabaseManager.GetAccountIdByLauncherToken(token);
            if (deLaBase == 0) deLaBase = DatabaseManager.GetAccountIdByToken(token);
            if (deLaBase != cuenta) return Bien(new { bien = false });

            // Y se le da por buena para lo que venga, aunque el del juego ya se haya rotado.
            DatabaseManager.SetLauncherToken(cuenta, token);

            ClientLaunchRegistry.RegisterToken(cuenta, token);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Le da al lanzador el instanceId y el hash con los que arrancar un cliente.
        ///
        /// Aquí estaba el nudo: el hash se lo inventaba el lanzador y lo apuntaba en un diccionario
        /// en memoria que luego lee el Zaap. Con dos procesos, el lanzador apuntaba en su memoria y
        /// el Zaap miraba en la suya. Ahora lo reparte quien lo va a comprobar.
        /// </summary>
        private static Respuesta Lanzamiento(string cuerpo, long cuenta, string ip)
        {
            string token = Texto(cuerpo, "token");
            string idioma = Texto(cuerpo, "idioma");
            if (idioma.Length == 0) idioma = "es";

            try
            {
                var lanzamiento = ClientLaunchRegistry.Register(cuenta, token, Guid.NewGuid().ToString("N"), idioma, ip);
                return Bien(new
                {
                    bien = true,
                    instancia = lanzamiento.InstanceId,
                    hash = lanzamiento.Hash,
                    cuenta,
                    rol = DatabaseManager.GetAccountRole(cuenta),
                    idioma,
                });
            }
            catch (InvalidOperationException ex)
            {
                // Register rechaza por dos motivos y los dos son mensajes para el usuario. Viajan
                // como código; la frase la pone el lanzador en su idioma.
                return Bien(new { bien = false, motivo = ex.Message });
            }
        }

        /// <summary>El lanzador avisa de que el cliente de esa cuenta se ha cerrado.</summary>
        private static Respuesta FinDeLanzamiento(long cuenta)
        {
            // La cuenta sale del token, no del cuerpo: si viniera en el cuerpo, cualquiera podria
            // echar del registro a la cuenta de otro con solo escribir su numero.
            if (cuenta > 0) ClientLaunchRegistry.RemoveByAccount(cuenta);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Apaga el servidor, pero DESPUÉS de haber contestado.
        ///
        /// Llamando a RequestShutdown aquí mismo, el proceso se moría —y con él el HttpListener—
        /// antes de que la respuesta saliera por el cable: el lanzador se quedaba esperando y daba
        /// el apagado por fallido justo cuando había funcionado. Medido: la petición volvía con el
        /// cuerpo vacío.
        /// </summary>
        private static Respuesta Apagar()
        {
            Console.WriteLine("[Control] El lanzador pide apagar el servidor.");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                Program.RequestShutdown("orden del lanzador");
            });
            return Bien(new { bien = true });
        }

        /// <summary>Códigos de motivo. Son códigos, no frases: el idioma lo pone el lanzador.</summary>
        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── Leer el cuerpo sin ceremonia ───────────────────────────────────────────────────

        private static string Texto(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "")
                    : "";
            }
            catch { return ""; }
        }

        private static long Numero(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64()
                    : 0;
            }
            catch { return 0; }
        }

    }
}
