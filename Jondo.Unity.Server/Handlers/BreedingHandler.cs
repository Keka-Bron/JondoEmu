using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The breeding window: the stable, the paddocks and their gauges, read off the client in Ghidra.
    /// </summary>
    /// <remarks>
    /// THE WINDOW (BreedingUi) asks for its content as it is built (hqx → hql: the open paddock's
    /// content and the stable); its paddock tab (PaddockUi) for the paddocks the breeder may open
    /// (hue → hus, then hrl → hrc for the one it selects: OnPaddockSelected sends its index + 1).
    /// A paddock's gate opens the window on it (hsb).
    ///
    /// THE RIDES move between the bag, the stable and a paddock by dragging them or by the lines'
    /// menus ("Transferir al establo", "al cercado", "al inventario"): items of the bag go in with
    /// hse (BreedingLine.DragAndDropReceived → dfz) and are answered with hty; rides go back to the
    /// bag with hsn (dge) → hti; and from one line to the other with huh (dga) → hur. The paddock
    /// line is locked unless the character is on that paddock's map (OpenPaddockState GaugeOnly:
    /// "Debes estar en el mapa de un cercado para poder transferir monturas").
    ///
    /// THE GAUGES: a click on one switches it on (hun → htr) or off (hqw → hsh); fuel dropped on the
    /// paddock or poured from the fill window goes in (hub → htf), and that window previews it first
    /// (htl → hum). While the paddock tab is shown its content follows the gauges (hua).
    /// </remarks>
    public static class BreedingHandler
    {
        /// <summary>The paddock each character has the window open on.</summary>
        private static readonly ConcurrentDictionary<long, int> _openOn = new();

        /// <summary>The characters with a paddock tab shown, which the clock keeps up to date.</summary>
        private static readonly ConcurrentDictionary<long, int> _watching = new();

        private static Timer? _clock;

        /// <summary>The fuels' effects, by the paddock gauge they fill: BoostPaddockSlapper (3810) to BoostPaddockFeeder (3820).</summary>
        public static readonly IReadOnlyDictionary<int, int> FuelEffects = new Dictionary<int, int>
        {
            [3810] = Stable.Slapper,
            [3812] = Stable.Patter,
            [3814] = Stable.Blaster,
            [3816] = Stable.DrinkingTrough,
            [3818] = Stable.DragoButt,
            [3820] = Stable.Feeder,
        };

        /// <summary>A fuel's cap travels in hundreds: "+#3 (máx. #2)", 400 for an extract, up to 40,000.</summary>
        public const int FuelCapUnit = 100;

        private static async Task WriteAsync(NetworkStream stream, byte[] frame)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        private static long Me => SessionContext.State.CharacterId;

        private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ─── Opening ─────────────────────────────────────────────────────────────────────

        /// <summary>A paddock's gate used: its paddock opens, if the breeder's level reaches it.</summary>
        public static async Task UseGateAsync(NetworkStream stream, int elementId, int skillId)
        {
            var paddock = Breeding.PaddockBySkill(skillId);
            if (paddock == null) return;
            int level = SessionContext.State.JobLevel(Breeding.BreederJob);
            if (!Breeding.Opens(paddock, level))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, InfoMessages.JobLevelTooLow)));
                Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} is breeder level {level}: " +
                                  $"«{paddock.Name}» asks for {paddock.MinLevel}.");
                return;
            }

            _openOn[Me] = paddock.Id;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, Me)));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hsb, BreedingProtocol.BuildOpenWindow(paddock.Id)));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} opens «{paddock.Name}».");
        }

        /// <summary>hqx: the window is built and asks for its content.</summary>
        public static async Task WindowOpenedAsync(NetworkStream stream)
        {
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hql,
                BreedingProtocol.BuildWindowContent(ContentOf(paddock), Stable.In(Me, Stable.StablePlace))));
        }

        /// <summary>hue: the paddock tab is shown; it gets the paddocks and the one open.</summary>
        public static async Task PaddockTabShownAsync(NetworkStream stream)
        {
            int level = SessionContext.State.JobLevel(Breeding.BreederJob);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hus, BreedingProtocol.BuildAccessiblePaddocks(
                Breeding.Paddocks.Select(p => (p.Id, Breeding.Opens(p, level))))));
            int paddock = OpenPaddock();
            _watching[Me] = paddock;
            EnsureClock();
            await SendPaddockAsync(stream, paddock);
        }

        /// <summary>hrl: a paddock picked in the tab.</summary>
        public static async Task PaddockPickedAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hrl);
            if (payload == null) return;
            var paddock = Breeding.PaddockOf(BreedingProtocol.ReadPickedPaddock(payload));
            if (paddock == null) return;
            if (!Breeding.Opens(paddock, SessionContext.State.JobLevel(Breeding.BreederJob))) return;
            _openOn[Me] = paddock.Id;
            if (_watching.ContainsKey(Me)) _watching[Me] = paddock.Id;
            await SendPaddockAsync(stream, paddock.Id);
        }

        /// <summary>hqq: the paddock tab is hidden; its clock stops.</summary>
        public static void PaddockTabHidden() => _watching.TryRemove(Me, out _);

        private static int OpenPaddock()
            => _openOn.TryGetValue(Me, out int paddock) ? paddock
             : Breeding.PaddockOnMap(SessionContext.State.MapId)?.Id ?? Breeding.Paddocks.FirstOrDefault()?.Id ?? 1;

        /// <summary>Whether the character may move rides in and out of a paddock: on its map.</summary>
        public static bool OnPaddock(long mapId, int paddock) => Breeding.PaddockOf(paddock)?.MapId == mapId && mapId != 0;

        private static async Task SendPaddockAsync(NetworkStream stream, int paddock)
        {
            Stable.Advance(Me, paddock, NowMs);
            int state = OnPaddock(SessionContext.State.MapId, paddock) ? BreedingProtocol.FullAccess : BreedingProtocol.GaugeOnly;
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hrc, BreedingProtocol.BuildPaddock(paddock, state, ContentOf(paddock))));
        }

        private static Pb ContentOf(int paddock)
            => BreedingProtocol.PaddockContent(Stable.GaugesOf(Me, paddock), Stable.In(Me, paddock));

        // ─── The rides ───────────────────────────────────────────────────────────────────

        /// <summary>The place a line is: the stable, or the paddock the window is open on.</summary>
        private static int PlaceOf(int line) => line == BreedingProtocol.StableLine ? Stable.StablePlace : OpenPaddock();

        private static int CapacityOf(int place) => place == Stable.StablePlace ? Breeding.StableSize : Breeding.RidesPerPaddock;

        /// <summary>Whether rides may go in or out of a place now: the stable always, a paddock only from its map.</summary>
        private static bool CanUse(int place) => place == Stable.StablePlace || OnPaddock(SessionContext.State.MapId, place);

        /// <summary>hse: rides of the bag put in the stable or the open paddock.</summary>
        public static async Task TransferFromBagAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hse);
            if (payload == null) return;
            var (items, line) = BreedingProtocol.ReadTransferFromBag(payload);
            int place = PlaceOf(line);
            int room = CanUse(place) ? CapacityOf(place) - Stable.In(Me, place).Count : 0;

            var added = new List<(long, Stable.StoredRide)>();
            var failed = new List<long>();
            foreach (long uid in items.Distinct())
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag || !Rides.IsRide(item.Template) || room <= 0)
                {
                    failed.Add(uid);
                    continue;
                }
                var ride = new Stable.StoredRide
                {
                    Uid = uid,
                    CharacterId = Me,
                    Place = place,
                    Gid = item.Template,
                    Effects = Rides.Normalized(item.Template, item.Effects, Random.Shared),
                };
                if (!DatabaseManager.DestroyCharacterItem(Me, uid, item.Quantity) || !Stable.Save(ride))
                {
                    failed.Add(uid);
                    continue;
                }
                Equipment.Remove(uid, item.Quantity);
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
                added.Add((uid, ride));
                room--;
            }
            if (place != Stable.StablePlace && added.Count > 0) Stable.Advance(Me, place, NowMs);
            if (added.Count > 0) await ItemUseHandler.WeighAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hty, BreedingProtocol.BuildRidesAdded(added, failed),
                                                               ConnectionProtocol.RequestId(frame)));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} puts {added.Count} ride(s) in place {place}" +
                              (failed.Count > 0 ? $", {failed.Count} refused." : "."));
        }

        /// <summary>hsn: rides of a line sent back to the bag.</summary>
        public static async Task SendToBagAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hsn);
            if (payload == null) return;
            var (line, ids) = BreedingProtocol.ReadSendToBag(payload);
            int place = PlaceOf(line);
            if (place != Stable.StablePlace) Stable.Advance(Me, place, NowMs);
            var rides = Stable.In(Me, place);
            var results = new List<(string, bool)>();
            foreach (string id in ids.Distinct())
            {
                var ride = long.TryParse(id, out long uid) ? rides.FirstOrDefault(r => r.Uid == uid) : null;
                bool done = ride != null && CanUse(place) && await ToBagAsync(stream, ride);
                results.Add((id, done));
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hti, BreedingProtocol.BuildRideResults(1, results),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>A ride back in the bag, the same item it was.</summary>
        public static async Task<bool> ToBagAsync(NetworkStream stream, Stable.StoredRide ride)
        {
            string effects = Forgemagic.Serialize(ride.Effects);
            if (!DatabaseManager.InsertCharacterItem(ride.Uid, Me, ride.Gid, 1, Equipment.Bag, effects)) return false;
            if (!Stable.Remove(Me, ride.Uid))
            {
                DatabaseManager.DestroyCharacterItem(Me, ride.Uid, 1);
                return false;
            }
            Equipment.Add(ride.Uid, ride.Gid, 1, Equipment.Bag, effects);
            var legacy = new PlayerItem { Uid = ride.Uid, ItemId = ride.Gid, Quantity = 1, Position = Equipment.Bag, RawEffects = effects };
            foreach (var effect in ride.Effects)
            {
                legacy.Effects.TryGetValue(effect.Effect, out int had);
                legacy.Effects[effect.Effect] = had + (int)effect.Value;
            }
            GameState.AddInventoryItem(legacy);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3,
                new HavenBagStore.StoredItem { Uid = ride.Uid, Gid = ride.Gid, Quantity = 1, Effects = effects })));
            return true;
        }

        /// <summary>huh: rides moved into the other line, the stable or the open paddock.</summary>
        public static async Task MoveAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Huh);
            if (payload == null) return;
            var (ids, line) = BreedingProtocol.ReadMove(payload);
            int place = PlaceOf(line);
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            var all = Stable.Of(Me);
            int room = CapacityOf(place) - all.Count(r => r.Place == place);
            var results = new List<(string, bool)>();
            foreach (string id in ids.Distinct())
            {
                var ride = long.TryParse(id, out long uid) ? all.FirstOrDefault(r => r.Uid == uid) : null;
                bool done = ride != null && ride.Place != place && room > 0 && CanUse(place) && CanUse(ride.Place);
                if (done)
                {
                    ride!.Place = place;
                    done = Stable.Save(ride);
                    if (done) room--;
                }
                results.Add((id, done));
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hur, BreedingProtocol.BuildRideResults(2, results),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// A ride's name as the client's rules have it (ui.mount.renameInfoTooltip): up to twenty
        /// characters, unaccented Latin letters, spaces and hyphens. Empty takes the name off.
        /// </summary>
        public static bool IsValidName(string name)
            => name.Length <= 20 && Regex.IsMatch(name, "^[A-Za-z -]*$");

        /// <summary>hut: a ride renamed.</summary>
        public static async Task RenameAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hut);
            if (payload == null) return;
            var (id, _, name) = BreedingProtocol.ReadRename(payload);
            name = name.Trim();
            var ride = long.TryParse(id, out long uid) ? Stable.Get(Me, uid) : null;
            int result = ride == null ? BreedingProtocol.Failed
                       : !IsValidName(name) ? BreedingProtocol.InvalidName
                       : BreedingProtocol.Ok;
            if (result == BreedingProtocol.Ok)
            {
                ride!.Effects = name.Length == 0 ? Rides.Without(ride.Effects, Rides.Name) : Rides.With(ride.Effects, Rides.Name, 0, name);
                if (!Stable.Save(ride)) result = BreedingProtocol.Failed;
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hqp, BreedingProtocol.BuildRenamed(result),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        // ─── The gauges ──────────────────────────────────────────────────────────────────

        /// <summary>Whether a gauge may be switched on beside the ones on: two at most, and the slapper never with the patter.</summary>
        public static bool CanEnable(IReadOnlyList<int> active, int gauge)
        {
            if (Breeding.GaugeOf(gauge) == null || active.Contains(gauge)) return false;
            if (active.Count >= Breeding.ActiveGauges) return false;
            int opposite = gauge == Stable.Slapper ? Stable.Patter : gauge == Stable.Patter ? Stable.Slapper : 0;
            return opposite == 0 || !active.Contains(opposite);
        }

        /// <summary>hun: a gauge switched on.</summary>
        public static async Task EnableGaugeAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hun);
            if (payload == null) return;
            int gauge = BreedingProtocol.ReadGauge(payload);
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            var gauges = Stable.GaugesOf(Me, paddock);
            int error = BreedingProtocol.Failed;
            if (CanEnable(gauges.Active, gauge))
            {
                gauges.Active.Add(gauge);
                if (gauges.TickedMs <= 0) gauges.TickedMs = NowMs;
                Stable.SaveGauges(Me, paddock, gauges);
                error = BreedingProtocol.Ok;
            }
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Htr, BreedingProtocol.BuildGaugeEnabled(gauges.Active, error),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>hqw: a gauge switched off.</summary>
        public static async Task DisableGaugeAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hqw);
            if (payload == null) return;
            int gauge = BreedingProtocol.ReadGauge(payload);
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            var gauges = Stable.GaugesOf(Me, paddock);
            bool was = gauges.Active.Remove(gauge);
            if (was) Stable.SaveGauges(Me, paddock, gauges);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hsh,
                BreedingProtocol.BuildGaugeDisabled(was ? BreedingProtocol.Ok : BreedingProtocol.Failed),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>
        /// The paddock gauge a fuel fills, what one unit adds and the most it fills it to, from its
        /// template ("Extracto de aporreadora": +3000, up to 400 hundreds; an elixir up to the top);
        /// null for no fuel.
        /// </summary>
        public static (int Gauge, int Amount, int Cap)? FuelOf(int gid)
        {
            foreach (var effect in DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(gid)))
            {
                if (!FuelEffects.TryGetValue(effect.EffectId, out int gauge)) continue;
                int top = Stable.GaugeMax(gauge);
                int cap = effect.DiceSide > 0 ? effect.DiceSide * FuelCapUnit : top;
                return (gauge, effect.Value, Math.Min(cap, top));
            }
            return null;
        }

        /// <summary>
        /// Fuel poured into a paddock's gauges: unit by unit, while each fills its gauge below its
        /// cap. What each item spends, by uid.
        /// </summary>
        public static Dictionary<long, int> Pour(Stable.Gauges gauges, IReadOnlyDictionary<long, long> fuel)
        {
            var used = new Dictionary<long, int>();
            foreach (var (uid, asked) in fuel)
            {
                var item = Equipment.ByUid(uid);
                if (item == null || item.Position != Equipment.Bag) continue;
                var data = FuelOf(item.Template);
                if (data is not { } f || f.Amount <= 0) continue;
                long units = Math.Min(asked, item.Quantity);
                int spent = 0;
                while (spent < units && gauges.ValueOf(f.Gauge) < f.Cap)
                {
                    gauges.Values[f.Gauge] = Math.Min(f.Cap, gauges.ValueOf(f.Gauge) + f.Amount);
                    spent++;
                }
                if (spent > 0) used[uid] = spent;
            }
            return used;
        }

        /// <summary>hub: fuel poured in, dropped on the paddock or from the fill window.</summary>
        public static async Task FillGaugesAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hub);
            if (payload == null) return;
            var fuel = BreedingProtocol.ReadFuel(payload);
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            var gauges = Stable.GaugesOf(Me, paddock);
            if (fuel.Keys.Any(uid => Equipment.ByUid(uid) == null))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Htf,
                    BreedingProtocol.BuildGaugesFilled(gauges, BreedingProtocol.GaugeBoostMissingItem)));
                return;
            }
            var used = Pour(gauges, fuel);
            if (used.Count == 0)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Htf,
                    BreedingProtocol.BuildGaugesFilled(gauges, BreedingProtocol.GaugeBoostNoChange)));
                return;
            }
            foreach (var (uid, count) in used) await SpendAsync(stream, uid, count);
            await ItemUseHandler.WeighAsync(stream);
            if (gauges.TickedMs <= 0) gauges.TickedMs = NowMs;
            Stable.SaveGauges(Me, paddock, gauges);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Htf, BreedingProtocol.BuildGaugesFilled(gauges, BreedingProtocol.Ok)));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} pours {used.Values.Sum()} fuel unit(s) into paddock {paddock}.");
        }

        /// <summary>htl: what the fill window's fuel would do, previewed.</summary>
        public static async Task SimulateFuelAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Htl);
            if (payload == null) return;
            int paddock = OpenPaddock();
            Stable.Advance(Me, paddock, NowMs);
            var gauges = Stable.GaugesOf(Me, paddock);
            var used = Pour(gauges, BreedingProtocol.ReadFuel(payload));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hum, BreedingProtocol.BuildGaugesSimulated(gauges, used)));
        }

        /// <summary>
        /// Units of an item of the bag spent: gone when none are left, its count told otherwise. The
        /// bag's weight goes after (ItemUseHandler.WeighAsync): without it the open bag keeps drawing
        /// an item gone -- the paddock's fuel stayed there.
        /// </summary>
        private static async Task SpendAsync(NetworkStream stream, long uid, int count)
        {
            var item = Equipment.ByUid(uid);
            if (item == null || count <= 0) return;
            bool whole = count >= item.Quantity;
            if (!DatabaseManager.DestroyCharacterItem(Me, uid, count)) return;
            Equipment.Remove(uid, count);
            if (whole)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(uid)));
            else
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(uid, item.Quantity)));
        }

        // ─── Extraction, cloning and mating ──────────────────────────────────────────────

        /// <summary>A ride of the character's by the id the window knows it by, in the stable or a paddock.</summary>
        private static Stable.StoredRide? FindRide(string id)
            => long.TryParse(id, out long uid) ? Stable.Get(Me, uid) : null;

        /// <summary>hsv: rides extracted, destroyed for their species' resource.</summary>
        public static async Task ExtractAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hsv);
            if (payload == null) return;
            var items = new Dictionary<int, int>();
            var gone = new List<string>();
            foreach (string id in BreedingProtocol.ReadExtract(payload).Distinct())
            {
                var ride = FindRide(id);
                if (ride == null || (ride.Place != Stable.StablePlace && !CanUse(ride.Place))) continue;
                if (Mating.ExtractionOf(ride) is not { } reward) continue;
                if (!Stable.Remove(Me, ride.Uid)) continue;
                items[reward.Item] = (items.TryGetValue(reward.Item, out int had) ? had : 0) + reward.Quantity;
                gone.Add(id);
            }
            if (gone.Count == 0)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hrh, BreedingProtocol.BuildExtractionFailed()));
                return;
            }
            foreach (var (item, quantity) in items) await Equipment.GiveAsync(stream, item, quantity);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hrh, BreedingProtocol.BuildExtracted(items, gone)));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} extracts {gone.Count} ride(s).");
        }

        /// <summary>hqv: two rides cloned into one, the first's, in the stable.</summary>
        public static async Task CloneAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hqv);
            if (payload == null) return;
            var (first, second) = BreedingProtocol.ReadClone(payload);
            var a = FindRide(first);
            var b = FindRide(second);
            var refusal = Mating.CheckClone(a, b);
            if (refusal == Mating.CloneRefusal.None && !Stable.Remove(Me, b!.Uid)) refusal = Mating.CloneRefusal.Unknown;
            if (refusal != Mating.CloneRefusal.None)
            {
                await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hsm, BreedingProtocol.BuildCloneFailed(refusal),
                                                                   ConnectionProtocol.RequestId(frame)));
                return;
            }
            a!.Effects = Mating.CloneOf(a);
            a.Place = Stable.StablePlace;
            Stable.Save(a);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hsm, BreedingProtocol.BuildCloned(a),
                                                               ConnectionProtocol.RequestId(frame)));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} clones ride {a.Uid} out of {b!.Uid}.");
        }

        /// <summary>The mákina of the bag the mating tab holds, if any.</summary>
        private static (Equipment.Item Item, Mating.Makina Makina)? MakinaIn(long uid)
        {
            if (uid < 0) return null;
            var item = Equipment.ByUid(uid);
            if (item == null) return null;
            var makina = Mating.MakinaOf(item.Template);
            return makina == null ? null : (item, makina);
        }

        private static long BreederExperience
            => SessionContext.State.Jobs.TryGetValue(Breeding.BreederJob, out var progress) ? progress.Experience : 0;

        /// <summary>hsa: what two rides would give, previewed in the mating tab.</summary>
        public static async Task PreviewMatingAsync(NetworkStream stream, byte[] frame)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Hsa);
            if (payload == null) return;
            var (first, second, makinaUid) = BreedingProtocol.ReadMatingPreview(payload);
            var a = FindRide(first);
            var b = FindRide(second);
            if (Mating.Check(a, b) != Mating.Refusal.None)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hrz, BreedingProtocol.BuildMatingPreviewFailed()));
                return;
            }
            var preview = Mating.PreviewOf(a!, b!, MakinaIn(makinaUid)?.Makina);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hrz, BreedingProtocol.BuildMatingPreview(preview, BreederExperience)));
        }

        /// <summary>htu: two rides bred: their children in the stable, the parents sterile, the rewards given.</summary>
        /// <summary>A child's capacity: its mákina's, or else the one today's Almanax gives the mounts born today.</summary>
        private static int CapacityOf(Mating.Preview preview, Random dice)
        {
            int capacity = Mating.DrawCapacity(preview.Capacities, dice);
            return capacity != 0 ? capacity : Almanax.NewbornCapacity(dice);
        }

        public static async Task BreedAsync(GameSession session, byte[] frame)
        {
            using var context = SessionContext.Push(session);
            var stream = session.Stream;
            byte[] payload = ConnectionProtocol.ReadPayload(frame, Op.Htu);
            if (payload == null) return;
            var (first, second, makinaUid) = BreedingProtocol.ReadMating(payload);
            var a = FindRide(first);
            var b = FindRide(second);
            var refusal = Mating.Check(a, b);
            var makina = MakinaIn(makinaUid);
            var preview = refusal == Mating.Refusal.None ? Mating.PreviewOf(a!, b!, makina?.Makina) : null;
            if (preview != null && Breeding.StableSize - Stable.In(Me, Stable.StablePlace).Count < preview.Children)
                refusal = Mating.Refusal.StableFull;
            if (refusal != Mating.Refusal.None || preview == null)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hsu, BreedingProtocol.BuildBreedFailed(refusal)));
                return;
            }

            if (makina is { } m && preview.MakinaFits) await SpendAsync(stream, m.Item.Uid, 1);
            var dice = Random.Shared;
            var children = new List<Stable.StoredRide>();
            bool target = false;
            for (int i = 0; i < preview.Children; i++)
            {
                int colour = Mating.DrawColour(preview, dice);
                target |= colour == preview.TargetRide && colour != 0;
                var ride = Breeding.RideOf(colour);
                if (ride == null || ride.Item <= 0) continue;
                var child = new Stable.StoredRide
                {
                    Uid = DatabaseManager.NextItemUid(),
                    CharacterId = Me,
                    Place = Stable.StablePlace,
                    Gid = ride.Item,
                    Effects = Mating.ChildOf(colour, a!, b!, CapacityOf(preview, dice), dice),
                };
                if (Stable.Save(child)) children.Add(child);
            }
            a!.Effects = Mating.AfterBreeding(a.Effects);
            b!.Effects = Mating.AfterBreeding(b.Effects);
            Stable.Save(a);
            Stable.Save(b);

            await Achievements.CountAsync(session, "EB", 0, children.Count, defer: stream == null);   // «Hacer que nazcan 10 monturas»
            int tokens = target ? preview.Tokens : 0;
            if (tokens > 0) await Equipment.GiveAsync(stream, Mating.Geneticha, tokens);
            if (preview.Experience > 0) await WorkshopHandler.GiveJobExperienceAsync(session, Breeding.BreederJob, preview.Experience);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hsu,
                BreedingProtocol.BuildBred(tokens, children, preview.Experience, target, new[] { a, b })));
            Console.WriteLine($"[Breeding] {SessionContext.State.CharacterName} breeds {a.Uid} and {b.Uid}: " +
                              $"{children.Count} child(ren){(target ? ", the target generation" : "")}, {tokens} genetichas.");
        }

        // ─── The clock ───────────────────────────────────────────────────────────────────

        private static void EnsureClock()
        {
            if (_clock != null) return;
            lock (_watching)
            {
                _clock ??= new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(Breeding.TickSeconds),
                                     TimeSpan.FromSeconds(Breeding.TickSeconds));
            }
        }

        /// <summary>Every ten seconds, each shown paddock brought up to now and sent again if it moved.</summary>
        private static async Task TickAsync()
        {
            foreach (var (character, paddock) in _watching.ToArray())
            {
                try
                {
                    var session = SessionRegistry.FindByCharacter(character);
                    if (session == null)
                    {
                        _watching.TryRemove(character, out _);
                        continue;
                    }
                    if (!Stable.Advance(character, paddock, NowMs)) continue;
                    var content = BreedingProtocol.PaddockContent(Stable.GaugesOf(character, paddock), Stable.In(character, paddock));
                    await session.SendAsync(ConnectionProtocol.Push(Op.Hua, BreedingProtocol.BuildPaddockUpdate(content)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Breeding] Could not bring paddock {paddock} of {character} up to now: {ex.Message}");
                }
            }
        }
    }
}
