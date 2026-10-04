using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las raids de gremio en marcha: comprarlas, lanzarlas, el reloj, y contestar a los
    /// criterios del contenido que ya está en la base.
    ///
    /// Una raid comprada se guarda -sobrevive al reinicio-, pero una EN MARCHA no: vive en
    /// memoria y se cae con el servidor, y el que estuviera dentro se queda en el mapa donde
    /// estaba. Es lo honesto mientras el reloj y la puntuación no se hayan medido en ninguna
    /// captura: guardar a medias una raid a medio jugar sería peor que no guardarla.
    ///
    /// Lo que NO se puede hacer todavía, y va dicho: el panel de la raid -el reloj, la
    /// puntuación y la sal en pantalla- necesita sus propios mensajes, y ninguna captura los
    /// trae. Mientras tanto lo que hay se cuenta por el chat.
    /// </summary>
    public static class GuildRaidManager
    {
        /// <summary>Las que están corriendo, una por gremio como mucho.</summary>
        private static readonly ConcurrentDictionary<long, RaidInstance> _running = new();

        /// <summary>De dónde salió cada uno, para devolverlo ahí cuando se acabe.</summary>
        private static readonly ConcurrentDictionary<long, (long MapId, int CellId)> _cameFrom = new();

        /// <summary>El reloj de cada raid, para poder pararlo si acaba antes.</summary>
        private static readonly ConcurrentDictionary<long, CancellationTokenSource> _clocks = new();

        private static long _nextId;

        // ─── Comprar ────────────────────────────────────────────────────────────

        /// <summary>
        /// Compra una raid con los kamas del gremio. Devuelve la clave de por qué no se pudo, o
        /// null cuando sí.
        /// </summary>
        public static string Buy(long characterId, int raidId)
        {
            var kind = Raids.Of(raidId);
            if (kind == null) return "raid.unknown";
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return "raid.noguild";
            if (GuildStore.OwnsRaid(guild.Id, raidId)) return "raid.owned";
            if (!GuildStore.SpendGuildKamas(guild.Id, kind.Price)) return "raid.nokamas";

            GuildStore.BuyRaid(guild.Id, raidId);
            return null;
        }

        // ─── Lanzar y entrar ────────────────────────────────────────────────────

        /// <summary>La raid que está jugando el gremio de este personaje, o null.</summary>
        public static RaidInstance RunningOf(long characterId)
        {
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return null;
            return _running.TryGetValue(guild.Id, out var raid) && raid.Running ? raid : null;
        }

        /// <summary>La raid en la que está METIDO este personaje, que no es lo mismo.</summary>
        public static RaidInstance RaidOf(long characterId)
        {
            var raid = RunningOf(characterId);
            return raid != null && raid.Has(characterId) ? raid : null;
        }

        /// <summary>
        /// Lanza la raid: la instancia, el capitán dentro, y a la primera planta.
        /// </summary>
        /// <remarks>
        /// El mínimo de ocho jugadores de la ficha del juego NO se exige aquí. Con ocho clientes
        /// haría falta un gremio entero conectado para probar una línea de código, y lo que se
        /// gana exigiéndolo es cero: el mínimo protege el equilibrio de un servidor con gente, no
        /// la corrección de esto. Queda escrito para el día que haya gente.
        /// </remarks>
        public static async Task<string> LaunchAsync(long captainId, int raidId)
        {
            var kind = Raids.Of(raidId);
            if (kind == null) return "raid.unknown";
            var guild = GuildStore.GuildOf(captainId);
            if (guild == null) return "raid.noguild";
            if (!GuildStore.OwnsRaid(guild.Id, raidId)) return "raid.notbought";
            if (_running.TryGetValue(guild.Id, out var already) && already.Running) return "raid.already";

            long id = Interlocked.Increment(ref _nextId);
            var raid = new RaidInstance(id, raidId, guild.Id, captainId, DateTimeOffset.UtcNow, kind.RunsFor);
            _running[guild.Id] = raid;

            // Se gasta al lanzarla: una raid comprada es un uso, no una llave permanente.
            GuildStore.DropRaid(guild.Id, raidId);

            StartClock(raid, kind);
            string fallo = await EnterAsync(captainId);
            return fallo ?? null;
        }

        /// <summary>Mete a alguien en la raid de su gremio y lo lleva a la primera planta.</summary>
        public static async Task<string> EnterAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null) return "raid.none";
            var kind = Raids.Of(raid.RaidId);
            if (raid.Has(characterId)) return "raid.inside";
            if (raid.Members.Count >= kind.MaxPlayers) return "raid.full";

            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return "raid.offline";

            raid.Add(characterId);
            await MoveAsync(session, EntryMapOf(kind), remember: true);
            return null;
        }

        /// <summary>Saca a alguien de la raid y lo devuelve a donde estaba.</summary>
        public static async Task<string> LeaveAsync(long characterId)
        {
            var raid = RaidOf(characterId);
            if (raid == null) return "raid.notinside";
            raid.Remove(characterId);
            await SendHomeAsync(characterId);
            return null;
        }

        /// <summary>
        /// Cierra la raid: por el reloj, porque el capitán la cierra o porque se ha ganado. Todo
        /// el que esté dentro vuelve a donde estaba y se queda la puntuación.
        /// </summary>
        public static async Task FinishAsync(RaidInstance raid, RaidInstance.Ending how)
        {
            if (raid == null || !raid.Running) return;
            var now = DateTimeOffset.UtcNow;
            raid.Finish(how, now);

            // Y a la clasificación de la semana, como acabara: una raid que se corta por el reloj
            // con veinte mil puntos vale esos veinte mil. Lo que no cuenta es no haber jugado.
            if (raid.Score > 0)
            {
                GuildStore.RecordRaidScore(raid.GuildId, raid.RaidId, raid.Score, now);
            }

            if (_clocks.TryRemove(raid.GuildId, out var clock))
            {
                try { clock.Cancel(); } catch (ObjectDisposedException) { }
                clock.Dispose();
            }
            _running.TryRemove(raid.GuildId, out _);

            foreach (long member in raid.Members.ToList())
            {
                await SendHomeAsync(member);
            }

            int puesto = GuildStore.PlaceOf(raid.GuildId, raid.RaidId, now);
            Console.WriteLine($"[Raid] {Raids.Of(raid.RaidId)?.Name} del gremio {raid.GuildId} " +
                              $"acabada ({how}), {raid.Score} puntos" +
                              (puesto > 0 ? $", puesto {puesto} de la semana." : "."));
        }

        /// <summary>El capitán la cierra antes de tiempo, que es lo que le deja hacer su ficha.</summary>
        public static async Task<string> CloseAsync(long characterId)
        {
            var raid = RunningOf(characterId);
            if (raid == null) return "raid.none";
            if (raid.CaptainId != characterId) return "raid.notcaptain";
            await FinishAsync(raid, RaidInstance.Ending.Captain);
            return null;
        }

        // ─── El reloj ───────────────────────────────────────────────────────────

        /// <summary>
        /// El reloj de la raid: una hora la del Gigalodón, dos la del Santuario. Cuando salta,
        /// la raid se acaba con la puntuación que tenga.
        /// </summary>
        private static void StartClock(RaidInstance raid, RaidKind kind)
        {
            var clock = new CancellationTokenSource();
            _clocks[raid.GuildId] = clock;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(kind.RunsFor, clock.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                try
                {
                    await FinishAsync(raid, RaidInstance.Ending.TimeUp);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Raid] El reloj no pudo cerrarla: {ex.Message}");
                }
            });
        }

        // ─── El resolvedor ──────────────────────────────────────────────────────

        /// <summary>
        /// Lo que contesta a los criterios del contenido para este personaje: las variables de su
        /// raid y la subárea donde está. Sin raid no contesta a nada, que es lo correcto: fuera
        /// de una raid, un criterio de raid no se cumple ni se incumple, no se sabe.
        /// </summary>
        public static Criterion.Resolver ResolverFor(long characterId)
        {
            var raid = RaidOf(characterId);
            if (raid == null) return _ => Answer.Unknown;
            return raid.ResolverFor(SubAreaOf(SessionRegistry.FindByCharacter(characterId)?.State.MapId ?? 0));
        }

        /// <summary>
        /// Si los monstruos de donde está este personaje le van a saltar encima, leyendo el
        /// criterio QUE TRAE EL PROPIO MONSTRUO en la base.
        /// </summary>
        /// <remarks>
        /// Los ocho de la Sima llevan escrito
        /// <c>(PB=1131&amp;RV!7,n1_worldlight,0)|…</c>: son inmunes a la agresión mientras la luz
        /// de su planta no sea cero. Aquí no hay ninguna regla de raid escrita a mano -la regla
        /// está en la base, como todo lo demás-, sólo quien contesta a sus preguntas.
        ///
        /// El emulador todavía no tiene monstruos que agredan, así que de momento esto sólo se
        /// consulta y se cuenta; el día que los tenga, esta es la puerta.
        /// </remarks>
        public static bool MonsterWouldAggress(long characterId, int monsterTemplate)
        {
            string criterion = DatabaseManager.MonsterAggressiveImmunity(monsterTemplate);
            if (string.IsNullOrWhiteSpace(criterion)) return false;

            // El criterio dice cuándo es INMUNE: agrede justo cuando no se cumple. Y lo que no se
            // sabe no agrede, que es el lado por el que conviene equivocarse.
            return Criterion.Evaluate(criterion, ResolverFor(characterId)) == Answer.False;
        }

        // ─── Los mapas ──────────────────────────────────────────────────────────

        /// <summary>
        /// El mapa por donde se entra a una raid.
        /// </summary>
        /// <remarks>
        /// NO está medido: ninguna captura entra en una raid, y ni los PNJ ni los interactivos de
        /// esas plantas están en la base, así que no hay una puerta que señalar. Se coge el
        /// primero de la primera planta, por número, que es determinista y se puede andar desde
        /// ahí. El día que se mida, se cambia esta línea.
        /// </remarks>
        public static long EntryMapOf(RaidKind kind)
        {
            if (kind == null || kind.Floors.Count == 0) return 0;
            var maps = DatabaseManager.MapsOfSubArea(kind.Floors[0]);
            return maps.Count == 0 ? 0 : maps.Min();
        }

        /// <summary>La subárea de un mapa, que es lo que pregunta el criterio «PB».</summary>
        public static int SubAreaOf(long mapId) => DatabaseManager.SubAreaOfMap(mapId);

        /// <summary>Lleva a alguien a un mapa, en su propia sesión.</summary>
        private static async Task MoveAsync(GameSession session, long mapId, bool remember)
        {
            if (session == null || mapId <= 0) return;
            using (SessionContext.Push(session))
            {
                if (remember)
                {
                    _cameFrom[session.CharacterId] = (SessionContext.State.MapId, SessionContext.State.CellId);
                }
                await TeleportHandler.ToMapAsync(session.Stream, mapId);
            }
        }

        /// <summary>Lo devuelve al mapa del que salió, si se sabe cuál era y sigue conectado.</summary>
        private static async Task SendHomeAsync(long characterId)
        {
            if (!_cameFrom.TryRemove(characterId, out var where)) return;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return;
            using (SessionContext.Push(session))
            {
                await TeleportHandler.ToMapAsync(session.Stream, where.MapId, where.CellId);
            }
        }

        /// <summary>Para las pruebas: olvida todo lo que haya en marcha.</summary>
        internal static void Forget()
        {
            foreach (var clock in _clocks.Values)
            {
                try { clock.Cancel(); clock.Dispose(); } catch (ObjectDisposedException) { }
            }
            _clocks.Clear();
            _running.Clear();
            _cameFrom.Clear();
        }

        /// <summary>Para las pruebas: mete una raid ya montada.</summary>
        internal static void Remember(RaidInstance raid) => _running[raid.GuildId] = raid;
    }
}
