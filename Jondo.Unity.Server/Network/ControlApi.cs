using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Where the launcher speaks to the server through.
    ///
    /// This existed and was deleted. The comment left in <see cref="HaapiServer"/> says so: the
    /// routes /api/login, /api/register, /api/launch, /api/status and /api/logs lived there and were
    /// removed when moving from the web interface to the native window, because the window called
    /// LauncherService directly and that was dead weight. As soon as the launcher and the server
    /// are two processes it is needed again, so it comes back.
    ///
    /// It hangs off the HAAPI, on 8888, and not on a new port, for three reasons:
    ///
    ///   * it is the port the client mod probes to decide whether to redirect to the emulator
    ///     (JondoFix/Class1.cs:471), so «8888 answers» is exactly the sign of life the
    ///     launcher needs before starting a client;
    ///   * the HAAPI is already an HttpListener bound to localhost and 127.0.0.1, and nothing else;
    ///   * it is where it was.
    ///
    /// WHAT IS DECIDED HERE BELONGS TO THE SERVER. This file knows nothing about windows: it receives text,
    /// calls the base and the launch registry, and returns text. The messages for the
    /// user are NOT translated here —they travel as a code— because the language belongs to the launcher.
    /// </summary>
    public static partial class ControlApi
    {
        /// <summary>The routes and the header come from the contract, which is what both share.</summary>
        public const string Prefijo = Jondo.Unity.Launcher.Contract.Prefijo;

        // ─── The secret ─────────────────────────────────────────────────────────────────────
        //
        // One per start: that way a launcher from a previous session does not keep a key to the
        // current one. The contract keeps it, which is what knows where it is written and who reads it.

        private static string _secreto = "";

        /// <summary>Hands out a new secret and leaves it written. The server calls it on starting.</summary>
        public static void NuevoSecreto() => _secreto = Contract.NuevoSecreto();

        /// <summary>
        /// Whether the request carries the secret this startup handed out.
        /// </summary>
        /// <remarks>
        /// <b>Nothing calls this today</b>, and it is worth saying here because three places claimed
        /// otherwise: HaapiServer said "without the secret this answers 403", Contract described it
        /// as what protects these routes, and Program prints its file on every startup. None of them
        /// reached this method, so the header the launcher sends was decoration.
        ///
        /// It stays unwired because enforcing it would close the remote-launcher mode, which is
        /// supported: the launcher only sends the header if it can READ the file, and on another
        /// machine it cannot. And it is not what matters — the admin routes go through ConRol, which
        /// wants a token the database recognises plus the administrator role, checked server-side on
        /// every request.
        ///
        /// Kept for the day this channel stops being local-only. If that day does not come, the
        /// honest move is to delete this, _secreto, NuevoSecreto and the line in Program.
        /// </remarks>
        private static bool Autorizada(string? traido) => Contract.MismoSecreto(traido, _secreto);

        // ─── The responses ──────────────────────────────────────────────────────────────────

        /// <summary>What goes out on the wire: a status code and a JSON body.</summary>
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
        /// Answers a control request. Returns null if the route is not from here, so that the
        /// HAAPI carries on with its own.
        /// </summary>
        public static Respuesta? Responder(string ruta, string metodo, string cuerpo, string? secreto,
                                           string ip = "")
        {
            if (!ruta.StartsWith(Prefijo, StringComparison.Ordinal)) return null;

            try
            {
                switch (ruta)
                {
                    // ─── Open ───────────────────────────────────────────────────────────────
                    // Anyone can call them, because one has to be able to log in before having anything
                    // to prove who one is with.
                    case Prefijo + "estado": return Estado();
                    case Prefijo + "entrar": return Entrar(cuerpo, ip);
                    case Prefijo + "crear-cuenta": return CrearCuenta(cuerpo, ip);

                    // ─── With a session ─────────────────────────────────────────────────────
                    // A token the base recognises is needed. The role does not matter: they are things
                    // any player does with his own account.
                    case Prefijo + "activos": return ConSesion(cuerpo, _ => Activos());
                    case Prefijo + "personajes": return ConSesion(cuerpo, Personajes);
                    case Prefijo + "recordar-token": return RecordarToken(cuerpo);
                    case Prefijo + "lanzamiento": return ConSesion(cuerpo, cuenta => Lanzamiento(cuerpo, cuenta, ip));
                    case Prefijo + "fin-de-lanzamiento":
                        return ConSesion(cuerpo, cuenta => FinDeLanzamiento(cuenta));

                    // ─── Administration ─────────────────────────────────────────────────────
                    // They command the server, not an account. Role 4 and it is checked here,
                    // on the server, every time. Whether the launcher shows the button or not is cosmetic:
                    // the launcher is on the player's computer and nothing is trusted there.
                    case Prefijo + "registro": return ConRol(cuerpo, Roles.Administrador, _ => Registro(cuerpo));
                    case Prefijo + "apagar": return ConRol(cuerpo, Roles.Administrador, Apagar);
                    case Prefijo + "rol": return ConRol(cuerpo, Roles.Administrador,
                        cuenta => CambiarRol(cuerpo, cuenta));
                    case Prefijo + "conectados": return ConRol(cuerpo, Roles.Administrador, Conectados);
                    // The administrator's window: his map, what he puts on it and takes off it,
                    // and the jail. See ControlApiWorld.cs.
                    case Prefijo + "mapa": return ConRol(cuerpo, Roles.Administrador, Mapa);
                    case Prefijo + "invocar": return ConRol(cuerpo, Roles.Administrador, cuenta => Invocar(cuerpo, cuenta));
                    case Prefijo + "quitar": return ConRol(cuerpo, Roles.Administrador, cuenta => Quitar(cuerpo, cuenta));
                    case Prefijo + "carcel": return ConRol(cuerpo, Roles.Administrador, cuenta => Carcel(cuerpo, cuenta));
                    case Prefijo + "liberar": return ConRol(cuerpo, Roles.Administrador, cuenta => Liberar(cuerpo, cuenta));
                    case Prefijo + "presos": return ConRol(cuerpo, Roles.Administrador, Presos);
                    case Prefijo + "coordenadas": return ConRol(cuerpo, Roles.Administrador, _ => Coordenadas(cuerpo));
                    case Prefijo + "buscar-mapas": return ConRol(cuerpo, Roles.Administrador, _ => BuscarMapas(cuerpo));
                    case Prefijo + "ficha": return ConRol(cuerpo, Roles.Administrador, cuenta => Ficha(cuerpo, cuenta));
                    case Prefijo + "visitar-carcel": return ConRol(cuerpo, Roles.Administrador, VisitarCarcel);
                    case Prefijo + "niveles-monstruos": return ConRol(cuerpo, Roles.Administrador, _ => NivelesMonstruos());
                    case Prefijo + "personaje":
                        return !metodo.Equals("POST", StringComparison.OrdinalIgnoreCase)
                            ? Mal(405, "metodo")
                            : ConRol(cuerpo, Roles.Administrador,
                                cuenta => AdministrarPersonaje(cuerpo, cuenta));

                    default: return Mal(404, "ruta");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] {ruta} ha reventado: {ex.Message}");
                return Mal(500, ex.Message);
            }
        }

        // ─── Who is calling ─────────────────────────────────────────────────────────────────
        //
        // Before, this was guarded by a secret the server wrote in %APPDATA% and the launcher
        // read from there. It worked while both were on the same machine, and stops working as
        // soon as the launcher is handed out: on another player's computer that file does not exist.
        //
        // Now the ACCOUNT rules. The token the launcher already has from logging in says who it is,
        // and the base says what it can do. It works the same locally as over Hamachi as against a
        // VPS, and there is no secret to hand out.

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
                ActivityJournal.Current.Write("admin.denied", cuenta,
                    details: new { role = rol, requiredRole = haceFalta });
                return Mal(403, "rol");
            }
            return hacer(cuenta);
        }

        /// <summary>Raises or lowers an account's role. Only an administrator gets here.</summary>
        private static Respuesta CambiarRol(string cuerpo, long administrador)
        {
            string quien = Texto(cuerpo, "cuenta");
            int rol = (int)Numero(cuerpo, "rol");
            bool bien = DatabaseManager.SetAccountRole(quien, rol, out int cuantas);
            if (bien) Console.WriteLine($"[Control] {quien} pasa a {Roles.Nombre(rol)}.");
            ActivityJournal.Current.Write("admin.role.changed", administrador,
                details: new { account = quien, requestedRole = rol, changed = bien, rows = cuantas });
            return Bien(new { bien, cuantas });
        }

        /// <summary>
        /// Changes the base characteristics and the kamas of a connected character, stores them and
        /// refreshes the sheet without forcing him to leave or restarting the server.
        ///
        /// The route goes through <see cref="ConRol"/> and only admits administrators. The session's
        /// turn keeps an HTTP order from trampling a movement, a fight or any other packet
        /// the client is handling at the same time.
        /// </summary>
        private static Respuesta AdministrarPersonaje(string cuerpo, long administrador)
        {
            if (!LiveCharacterUpdate.TryParse(cuerpo, out var update, out string error)
                || update == null)
                return Mal(400, error);
            if (!update.HasChanges) return Mal(400, "sin-cambios");

            // No name is the caller's own character: the in-game panel gives to oneself without
            // having to know what the client calls its character.
            var sesion = update.Character.Length > 0
                ? SessionRegistry.FindByName(update.Character)
                : SessionRegistry.InWorld().FirstOrDefault(s => s.AccountId == administrador);
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
            // A rolled item is one row each, so a careless quantity is that many inserts and that
            // many messages to the client: the same cap as the .item command's.
            if (update.ItemGid.HasValue && update.RandomStats
                && CommandHandler.TooManyRolled((int)update.ItemGid.Value, update.Quantity ?? 1))
                return Mal(400, "cantidad-excesiva");
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

                    // A delivery failure has to be stated. GrantItemAsync returns null when
                    // the template does not exist or the INSERT fails, and that null was not checked: the
                    // response went out with HTTP 200 and bien=true, only with objetoUid null, and
                    // the caller was left believing it had delivered something.
                    Managers.HavenBagStore.StoredItem? granted = null;
                    if (update.ItemGid.HasValue)
                    {
                        int gid = (int)update.ItemGid.Value;
                        int quantity = (int)(update.Quantity ?? 1);
                        if (update.RandomStats)
                        {
                            // Rolled the way a craft rolls it, and by the same code.
                            if (!WorkshopHandler.GiveAsync(sesion.Stream, gid, quantity)
                                    .GetAwaiter().GetResult())
                                return Mal(422, "objeto-no-entregado");
                        }
                        else
                        {
                            granted = CommandHandler.GrantItemAsync(sesion.Stream, gid, quantity)
                                .GetAwaiter().GetResult();
                            if (granted == null) return Mal(422, "objeto-no-entregado");
                        }
                        ActivityJournal.Current.Write("item.granted", administrador,
                            estado.CharacterId,
                            new { source = "control", gid, quantity, random = update.RandomStats });

                        // And the weight, as the .item command does: without this the pods bar
                        // stays stale until the next inventory movement.
                        sesion.SendAsync(ConnectionProtocol.Push(Op.Iun,
                            ConnectionProtocol.BuildPods(0, 1000 + 5L * estado.StatStrength)))
                            .GetAwaiter().GetResult();
                    }

                    Managers.HavenBagStore.StoredItem? mount = null;
                    if (update.MountGid.HasValue)
                    {
                        mount = CommandHandler.GrantItemAsync(sesion.Stream,
                            (int)update.MountGid.Value, 1).GetAwaiter().GetResult();
                        if (mount == null) return Mal(422, "montura-no-entregada");

                        byte[] move = ConnectionProtocol.Push(Op.Iuk, Pb.New()
                            .Var(1, 1).Var(2, mount.Uid).Var(3, Managers.Mounts.Slot).Build());
                        EquipmentHandler.MoveAsync(sesion.Stream, move, sesion.AccountId)
                            .GetAwaiter().GetResult();
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
                                      $"kamas {estado.Kamas}" +
                                      (levelChange != null ? $", nivel {estado.CharacterLevel}" : "") +
                                      (granted != null ? $", objeto {granted.Gid} (uid {granted.Uid})" : "") +
                                      (mount != null ? $", montura {mount.Gid}" : "") +
                                      (landed != null ? $", mapa {estado.MapId} celda {landed}" : "") + ".");
                    ActivityJournal.Current.Write("admin.character.updated", administrador,
                        estado.CharacterId,
                        new
                        {
                            character = estado.CharacterName,
                            vitality = estado.StatVitality,
                            wisdom = estado.StatWisdom,
                            strength = estado.StatStrength,
                            intelligence = estado.StatIntelligence,
                            chance = estado.StatChance,
                            agility = estado.StatAgility,
                            kamas = estado.Kamas,
                        });
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
                        objeto = update.ItemGid,
                        cantidad = update.ItemGid.HasValue ? update.Quantity ?? 1 : (long?)null,
                        aleatorio = update.RandomStats,
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

        /// <summary>
        /// Who is in the world, for whoever is about to give something to one of them: the name
        /// /api/personaje takes, the level, and which of them is the caller's own.
        /// </summary>
        private static Respuesta Conectados(long administrador)
        {
            var conectados = new List<object>();
            foreach (var sesion in SessionRegistry.InWorld())
            {
                conectados.Add(new
                {
                    nombre = sesion.State.CharacterName ?? "",
                    nivel = sesion.State.CharacterLevel,
                    propio = sesion.AccountId == administrador,
                    // Where he is, for "go to him" and "bring him here", and whether he is in jail.
                    mapa = sesion.State.MapId,
                    celda = sesion.State.CellId,
                    preso = Managers.Jail.IsJailed(sesion.CharacterId),
                });
            }
            return Bien(new { conectados });
        }

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

        // ─── Each verb ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// If this answers, the server is alive. Even so what is inside is stated, because a
        /// half-done base with the ports open is also a problem.
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

        /// <summary>Who has a client open right now, and how many fit.</summary>
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

        /// <summary>
        /// The calling account's characters, so that the launcher draws its team.
        /// </summary>
        /// <remarks>
        /// The launcher draws each character's portrait taking it from the bones of the Dofus
        /// client itself, just as Studio does with the NPCs. But for that it needs to know WHAT
        /// to draw, and the look string lives in the database: the launcher does not touch it on
        /// purpose —it is what is handed out to players and only carries the contract—, so we
        /// give it here.
        ///
        /// From its OWN account and no other: the account comes from the token
        /// <see cref="ConSesion"/> validates, not from anything written in the body. With the body's,
        /// anyone could ask for the neighbour's characters just by changing a number.
        /// </remarks>
        private static Respuesta Personajes(long cuenta)
        {
            var suyos = new List<object>();
            foreach (var personaje in DatabaseManager.GetCharactersByAccountId(cuenta))
            {
                suyos.Add(new
                {
                    id = personaje.Id,
                    nombre = personaje.Name ?? "",
                    nivel = personaje.Level,
                    raza = personaje.Breed,
                    sexo = personaje.Sex,
                    aspecto = Managers.BreedLookTable.Drawable(personaje),
                });
            }

            return Bien(new { personajes = suyos });
        }

        /// <summary>The console lines from the one the launcher already has.</summary>
        private static Respuesta Registro(string cuerpo)
        {
            long desde = Numero(cuerpo, "desde");
            return new Respuesta(200, ConsoleLogBuffer.GetLogsJson(desde));
        }

        /// <summary>
        /// Log in with username and password.
        ///
        /// The IP is THE SOCKET'S, not the one written in the body. With the body's, the
        /// base's brake —five failed attempts and a minute's wait, and it counts per
        /// IP— was skipped by changing a field of the JSON on each attempt, so it braked nothing.
        /// This could only be exploited from the machine itself, because the HAAPI listens on
        /// localhost and 127.0.0.1 and nothing else, but a brake that does not brake is worse than none:
        /// it makes one believe there is one.
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

            // And the same one, separately, as the launcher's session. They go together now and split as
            // soon as the player starts a client, which rotates the game one: without this
            // second copy, that rotation would leave the launcher without a session for next time.
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

        /// <summary>Create an account. The IP, again the socket's and not whatever the body says.</summary>
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
        /// The launcher remembers a session from the previous time and wants the server to accept
        /// that token again. It is only accepted if the base recognises it: the launcher cannot
        /// invent whatever account it likes.
        /// </summary>
        private static Respuesta RecordarToken(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            string token = Texto(cuerpo, "token");
            if (cuenta <= 0 || token.Length == 0) return Bien(new { bien = false });

            // Both the launcher's session and the game token are valid: the bases from before there
            // was a column of its own only have the second.
            long deLaBase = DatabaseManager.GetAccountIdByLauncherToken(token);
            if (deLaBase == 0) deLaBase = DatabaseManager.GetAccountIdByToken(token);
            if (deLaBase != cuenta) return Bien(new { bien = false });

            // And it is accepted for whatever comes, even if the game one has already been rotated.
            DatabaseManager.SetLauncherToken(cuenta, token);

            ClientLaunchRegistry.RegisterToken(cuenta, token);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Gives the launcher the instanceId and the hash to start a client with.
        ///
        /// Here was the knot: the launcher invented the hash and recorded it in an in-memory
        /// dictionary that the Zaap then reads. With two processes, the launcher recorded in its memory and
        /// the Zaap looked in its own. Now whoever is going to check it hands it out.
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
                // Register rejects for two reasons and both are messages for the user. They travel
                // as a code; the launcher puts the sentence in its language.
                return Bien(new { bien = false, motivo = ex.Message });
            }
        }

        /// <summary>The launcher reports that the client of that account has closed.</summary>
        private static Respuesta FinDeLanzamiento(long cuenta)
        {
            // The account comes from the token, not from the body: if it came in the body, anyone could
            // throw someone else's account out of the registry just by writing its number.
            if (cuenta > 0) ClientLaunchRegistry.RemoveByAccount(cuenta);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Shuts the server down, but AFTER having answered.
        ///
        /// Calling RequestShutdown right here, the process died —and with it the HttpListener—
        /// before the response went out on the wire: the launcher was left waiting and considered
        /// the shutdown failed just when it had worked. Measured: the request came back with an
        /// empty body.
        /// </summary>
        private static Respuesta Apagar(long administrador)
        {
            Console.WriteLine("[Control] El lanzador pide apagar el servidor.");
            ActivityJournal.Current.Write("admin.server.shutdown", administrador);
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                Program.RequestShutdown("orden del lanzador");
            });
            return Bien(new { bien = true });
        }

        /// <summary>Reason codes. They are codes, not sentences: the launcher supplies the language.</summary>
        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── Reading the body without ceremony ──────────────────────────────────────────────

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
