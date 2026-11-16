using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The temporal anomalies on the map: their places (<see cref="AnomalyPlaces"/>) -- the travel
    /// to the entry, the guardian's group in the room, the exit back to the anomaly's zaap, the
    /// clocks -- and the zones they are open in.
    /// </summary>
    /// <remarks>
    /// Measured in «Movimiento/entrar a mapa con vestigio de zaap-entrar en la anomalia...»:
    /// <list type="bullet">
    ///   <item>on every map of an anomaly's subarea, lzi { f1 the subarea, f2 true, f3 when it
    ///     ends, in milliseconds };</item>
    ///   <item>on its zaap's map, the hpm of the vestige at its end (6662);</item>
    ///   <item>entering its subarea from another, «Se ha abierto una anomalía en este territorio.
    ///     Su guardián se encuentra en el {zaap, subarea}.» (info 1066);</item>
    ///   <item>the exit: iwn, and the jru to the anomaly's zaap map;</item>
    ///   <item>a clock: iwn, and its lqn 120 or 127.</item>
    /// </list>
    /// </remarks>
    public static class AnomalyHandler
    {
        /// <summary>«Se ha abierto una anomalía en este territorio...», the subarea its parameter.</summary>
        public const int AnomalyOpenHere = 1066;

        /// <summary>«El reloj indica {0}:{1}.»</summary>
        public const int ClockSays = 120;

        /// <summary>«Este reloj no marca la hora correcta, indica {0}:{1}.»</summary>
        public const int WrongClockSays = 127;

        /// <summary>The anomaly each character travelled to, by subarea: where his exit leads.</summary>
        private static readonly ConcurrentDictionary<long, int> _travelledTo = new();

        /// <summary>The subarea of the last map each character loaded.</summary>
        private static readonly ConcurrentDictionary<long, int> _lastSubArea = new();

        private static readonly Random _dice = new();

        /// <summary>
        /// Where a travel to an anomaly leaves the character in session, and noted as his: its place's
        /// entry and arrival cell (-1 when not measured), or the measured landing when there is no place.
        /// </summary>
        public static (long Map, int Cell) Travel(int subAreaId)
        {
            _travelledTo[SessionContext.State.CharacterId] = subAreaId;
            var place = AnomalyPlaces.Of(subAreaId);
            if (place == null || MapManager.GetMapInfo(place.Entry) == null) return (Anomalies.ArrivalMap, -1);
            return (place.Entry, place.Arrival > 0 ? place.Arrival : -1);
        }

        /// <summary>
        /// A map about to be drawn: a place's room gets its guardian's group when it has none -- a
        /// fight with him is never the last (the 2.64 rework: «combats illimités»).
        /// </summary>
        public static void BeforeMapLoaded(long mapId)
        {
            var place = AnomalyPlaces.ByRoom(mapId);
            if (place == null) return;
            if (MobSpawnManager.GetMobsForMap(mapId).Any(g => g.Members.Any(m => m.Monster?.Id == place.Guardian))) return;

            var members = AnomalyPlaces.GroupOf(place, _dice);
            int cell = place.Group?.Cell > 0 ? place.Group.Cell : -1;
            var group = MobSpawnManager.SpawnComposed(mapId, members.Select(m => (m, 0)), cell);
            if (group == null) return;
            if (place.Group?.Orientation > 0) group.Orientation = place.Group.Orientation;
            Console.WriteLine($"[Anomalies] {place.Name}: the guardian {place.Guardian} waits on cell {group.CellId} " +
                              $"with {string.Join(", ", members.Skip(1))}.");
        }

        /// <summary>
        /// The zone bonus an open anomaly's zone carries: «Quand l'anomalie ouvre, le bonus du
        /// territoire passe à 120% pendant toute la durée de l'anomalie» (Dofus pour les noobs).
        /// No other zone bonus is kept here.
        /// </summary>
        public const int AnomalyZoneBonus = 120;

        /// <summary>
        /// The world map's «Anomalies, bonus d'expérience et de butin» panel asks (lzh,
        /// AnomalySubareaInformationRequest, empty) each time it opens, and fills itself from the
        /// answer: lzl, AnomalySubareaInformationEvent, f1 a repeated lzj { f1 the zone's bonus, f2 the
        /// subarea, f3 whether an anomaly is open there, f4 when it closes, in milliseconds }.
        /// Nothing answered it, and the panel stayed empty.
        /// </summary>
        /// <remarks>
        /// Read off the client (Cpp2IL of 3.6.10.11): enw.badu takes the lzl into gv.mxh, which puts
        /// f2 in the field the notifications (eui.bcnk) look the subarea up with -- after testing f3 --,
        /// f1, f3 and f4 beside it. That f1 is the bonus and f4 the closing is INFERRED: Dofus 2's
        /// AnomalySubareaInformation was { subAreaId, rewardRate, hasAnomaly, anomalyClosingTime }, the
        /// same four in kind, and the lzi this server sends carries its closing in milliseconds.
        /// Only the open anomalies are listed: no other zone bonus is kept here.
        /// </remarks>
        public static Task SubareaInformationAsync(NetworkStream stream)
            => WriteAsync(stream, ConnectionProtocol.Push("lzl", BuildSubareaInformation(DateTimeOffset.UtcNow)));

        /// <summary>The lzl: each open anomaly's zone, its bonus, and when it closes.</summary>
        internal static byte[] BuildSubareaInformation(DateTimeOffset now)
        {
            var lzl = Pb.New();
            long nowMs = now.ToUnixTimeMilliseconds();
            foreach (var anomaly in Anomalies.All)
            {
                long closes = nowMs + Anomalies.MinutesLeft(anomaly.SubAreaId) * 60_000L;
                lzl.Msg(1, Pb.New().Var(1, AnomalyZoneBonus).Var(2, anomaly.SubAreaId).Var(3, 1).Var(4, closes));
            }
            return lzl.Build();
        }

        /// <summary>A map drawn: the anomaly of its zone, the vestige of its zaap, the notice on coming in.</summary>
        public static async Task OnMapLoadedAsync(NetworkStream stream, long mapId)
        {
            if (stream == null) return;
            long me = SessionContext.State.CharacterId;
            int subArea = DatabaseManager.SubAreaOfMap(mapId);
            _lastSubArea.TryGetValue(me, out int before);
            _lastSubArea[me] = subArea;

            if (Anomalies.TryGet(subArea, out _))
            {
                if (before != subArea)
                    await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildSystemMessage(AnomalyOpenHere, subArea.ToString())));
                long ends = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Anomalies.MinutesLeft(subArea) * 60_000L;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lzi,
                    Pb.New().Var(1, subArea).Var(2, 1).Var(3, ends).Build()));
            }

            if (AnomalyPlaces.VestigeStaging > 0 && Anomalies.All.Any(a => a.MapId == mapId))
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hpm,
                    GuildRaidStagings.BuildPlay(AnomalyPlaces.VestigeStaging, GuildRaidStagings.AtEnd)));
        }

        /// <summary>
        /// A place's exit: back to the zaap of the anomaly he came through, or of one that opens on
        /// this place now, or of the first.
        /// </summary>
        public static async Task ExitAsync(NetworkStream stream, Interactives.Element element, int skillId)
        {
            long me = SessionContext.State.CharacterId;
            var place = AnomalyPlaces.ByEntry(SessionContext.State.MapId);
            Anomalies.Anomaly target = default;
            bool found = _travelledTo.TryGetValue(me, out int travelled) && Anomalies.TryGet(travelled, out target)
                         && AnomalyPlaces.Of(travelled) == place;
            if (!found)
            {
                var now = Anomalies.All.Where(a => AnomalyPlaces.Of(a.SubAreaId) == place).ToList();
                if (now.Count == 0 && Anomalies.All.Count == 0) return;
                target = now.Count > 0 ? now[0] : Anomalies.All[0];
            }

            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(element.Id, skillId, me)));
            var zaap = Interactives.ZaapElements(target.MapId);
            int cell = zaap.Count > 0 ? FightHandler.CellBeside(target.MapId, zaap[0].Cell) : TeleportHandler.MapCentre;
            int landed = await TeleportHandler.ToMapAsync(stream, target.MapId, cell);
            if (landed >= 0)
                Console.WriteLine($"[Anomalies] {SessionContext.State.CharacterName} leaves {place?.Name} for the " +
                                  $"anomaly «{target.Name}»'s zaap, map {target.MapId}.");
        }

        /// <summary>A clock of Noximilian's Workshop: the hour of Paris, or four hours on for the wrong one.</summary>
        public static async Task ClockAsync(NetworkStream stream, Interactives.Element element, int skillId)
        {
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(element.Id, skillId, SessionContext.State.CharacterId)));
            var (message, hour, minute) = ClockReading(element.Gfx == AnomalyPlaces.WrongClock, DateTimeOffset.UtcNow);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(message, hour, minute)));
        }

        /// <summary>
        /// What a clock says: «00», «26» at 00:26 in Paris for the right one, «4», «26» for the
        /// wrong one -- as the capture read them at 22:26 UTC in August.
        /// </summary>
        public static (int Message, string Hour, string Minute) ClockReading(bool wrong, DateTimeOffset now)
        {
            var paris = OsavoraSeasons.InParis(now);
            string minute = paris.Minute.ToString("00");
            return wrong
                ? (WrongClockSays, ((paris.Hour + AnomalyPlaces.WrongBy) % 24).ToString(), minute)
                : (ClockSays, paris.Hour.ToString("00"), minute);
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
