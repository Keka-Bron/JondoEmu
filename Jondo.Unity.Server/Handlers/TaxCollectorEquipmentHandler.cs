using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// What a tax collector is besides its place: what it wears, the six spells it fights with,
    /// its characteristics, and the alliance's default that each new one starts with. And what
    /// happened to one's tax collectors while away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All read off the client, as no capture opens these windows. The menu's «Consultar» sends
    /// hkt and the answer is hkb, the window read-only (TaxCollectorEquipment in consult mode).
    /// «Modificar» sends ker and the answer is an exchange, kds: the same window editable beside
    /// the bag. Items go on and off it with the kcr of every storage -- f1 +1 on, -1 off -- and
    /// each change is told with hkh; the spells are set (hle), taken off (hjx) and moved between
    /// slots (hke), and told with hki. The window closes with the kla of every exchange (khd).
    /// </para>
    /// <para>
    /// The alliance's default, the personalization tab, goes the same way with its preset: hjy
    /// asks for it when the tab opens (hlb), hko, hli and hkw change its spells (hlh), and hkl
    /// closes it. A new tax collector starts with the preset's spells -- every tax collector of an
    /// alliance in the hks has the same six.
    /// </para>
    /// <para>
    /// A tax collector wears seven kinds of item, the item types of superType 69: they add to its
    /// characteristics in a fight, and the chests and saddlebags to its prospecting and pods.
    /// Beaten or collected, what it wore goes to its owner's bank: «Uno de tus recaudadores ha
    /// sido destruido. Su equipo está en tu banco.» (info 0/1579).
    /// </para>
    /// </remarks>
    public static partial class TaxCollectorHandler
    {
        // ─── What it is ─────────────────────────────────────────────────────────

        /// <summary>Everything the lea carries of a tax collector beyond its place.</summary>
        internal static TaxCollectorProtocol.Details DetailsOf(TaxCollectorStore.TaxCollector tc)
        {
            var items = TaxCollectorStore.Items(tc);
            var equipment = TaxCollectorStore.EquipmentOf(tc);
            return new TaxCollectorProtocol.Details
            {
                Pods = PodsOf(items),
                Worth = WorthOf(items),
                Equipment = equipment,
                Spells = TaxCollectorStore.SpellsOf(tc),
                Sheet = SheetOf(equipment),
            };
        }

        /// <summary>A tax collector's lea, whole.</summary>
        internal static Pb LeaOf(TaxCollectorStore.TaxCollector tc, AllianceStore.Alliance alliance)
            => TaxCollectorProtocol.Information(tc, alliance, StateOf(tc), DetailsOf(tc));

        /// <summary>What its equipment adds, by characteristic, as the player's equipment is summed.</summary>
        internal static Dictionary<int, long> BonusesOf(IEnumerable<HavenBagStore.StoredItem> equipment)
        {
            var total = new Dictionary<int, long>();
            foreach (var item in equipment)
            {
                foreach (var effect in Equipment.ParseEffects(item.Effects))
                {
                    if (!EffectTable.TryGet(effect.Effect, out var what) || what.Characteristic == 0) continue;
                    long value = effect.Sheet;
                    if (value == 0) continue;
                    total.TryGetValue(what.Characteristic, out long had);
                    total[what.Characteristic] = had + what.Sign * value * Math.Max(1, item.Quantity);
                }
            }
            return total;
        }

        /// <summary>Its prospecting: the 200 every tax collector has and what its chests add.</summary>
        internal static int ProspectingOf(TaxCollectorStore.TaxCollector tc)
            => TaxCollectorProtocol.Prospecting
               + (int)BonusesOf(TaxCollectorStore.EquipmentOf(tc)).GetValueOrDefault(ConnectionProtocol.Stat.Prospecting);

        /// <summary>What it can carry: the 1000 every tax collector has and what its saddlebags add.</summary>
        internal static int MaxPodsOf(TaxCollectorStore.TaxCollector tc)
            => TaxCollectorProtocol.MaxPods
               + (int)BonusesOf(TaxCollectorStore.EquipmentOf(tc)).GetValueOrDefault(ConnectionProtocol.Stat.Pods);

        /// <summary>
        /// Its characteristics (lns), as the kub carries the player's: what the monster
        /// «Recaudador» is, its fight sheet, with what the equipment adds in the bonus column.
        /// </summary>
        internal static List<Pb> SheetOf(IEnumerable<HavenBagStore.StoredItem> equipment)
        {
            var sheet = new List<Pb>();
            var fighter = BareFighter();
            if (fighter == null) return sheet;
            var bonuses = BonusesOf(equipment);
            var seen = new HashSet<int>();
            foreach (var (characteristic, baseValue, gear) in FightHandler.FichaParaLaGuardia(fighter))
            {
                if (!seen.Add(characteristic)) continue;
                long bonus = gear + bonuses.GetValueOrDefault(characteristic);
                sheet.Add(TaxCollectorProtocol.Characteristic(characteristic, baseValue, bonus,
                                                             WorldEntry.ContainerOf(characteristic)));
            }
            foreach (int characteristic in new[] { ConnectionProtocol.Stat.Prospecting, ConnectionProtocol.Stat.Pods })
            {
                if (!seen.Add(characteristic)) continue;
                long baseValue = characteristic == ConnectionProtocol.Stat.Pods
                    ? TaxCollectorProtocol.MaxPods
                    : TaxCollectorProtocol.Prospecting;
                sheet.Add(TaxCollectorProtocol.Characteristic(characteristic, baseValue,
                    bonuses.GetValueOrDefault(characteristic), WorldEntry.ContainerOf(characteristic)));
            }
            return sheet;
        }

        /// <summary>The monster «Recaudador» as a fighter, before anything of its own is put on it.</summary>
        private static Fighter BareFighter()
        {
            var monster = MobSpawnManager.GetMonsterData(TaxCollectorProtocol.Monster);
            if (monster == null || monster.Grades.Count == 0) return null;
            return FightHandler.BuildMonsterFighter(new MobSpawnManager.MobMember
            {
                Monster = monster, GradeIndex = 0, Level = monster.Grades[0].Level,
            }, -1, 0);
        }

        /// <summary>
        /// The tax collector made ready for its fight: the spells it was given -- its six, or the
        /// monster's own when it was given none -- and what its equipment adds.
        /// </summary>
        internal static void Dress(Fighter fighter, TaxCollectorStore.TaxCollector tc)
        {
            var spells = TaxCollectorStore.SpellsOf(tc);
            if (spells.Count > 0)
            {
                fighter.SpellIds = new List<int>();
                fighter.SpellGrades = new Dictionary<int, int>();
                foreach (int spellLevel in spells.Values)
                {
                    var (spell, grade) = SpellOfLevel(spellLevel);
                    if (spell == 0 || fighter.SpellIds.Contains(spell)) continue;
                    fighter.SpellIds.Add(spell);
                    fighter.SpellGrades[spell] = Math.Max(1, grade);
                }
            }
            foreach (var (characteristic, value) in BonusesOf(TaxCollectorStore.EquipmentOf(tc)))
                AddTo(fighter, characteristic, (int)value);
        }

        /// <summary>One characteristic's bonus put on a fighter, in the field the engine reads it from.</summary>
        internal static void AddTo(Fighter fighter, int characteristic, int value)
        {
            switch (characteristic)
            {
                case ConnectionProtocol.Stat.ActionPoints: fighter.MaxAP += value; fighter.CurrentAP += value; break;
                case ConnectionProtocol.Stat.MovementPoints: fighter.MaxMP += value; fighter.CurrentMP += value; break;
                case ConnectionProtocol.Stat.Vitality:
                    fighter.Vitality += value; fighter.MaxHP += value; fighter.CurrentHP += value; break;
                case ConnectionProtocol.Stat.Strength: fighter.Strength += value; break;
                case ConnectionProtocol.Stat.Intelligence: fighter.Intelligence += value; break;
                case ConnectionProtocol.Stat.Chance: fighter.Chance += value; break;
                case ConnectionProtocol.Stat.Agility: fighter.Agility += value; break;
                case ConnectionProtocol.Stat.Critical: fighter.CriticalBonus += value; break;
                case ConnectionProtocol.Stat.Range: fighter.Range += value; break;
                case ConnectionProtocol.Stat.Power: fighter.Power += value; break;
                case ConnectionProtocol.Stat.Initiative: fighter.Initiative += value; break;
                case 16: fighter.FlatDamage += value; break;
                case 86: fighter.CriticalDamage += value; break;
                case 84: fighter.PushDamage += value; break;
                case 88: fighter.EarthDamage += value; break;
                case 89: fighter.FireDamage += value; break;
                case 90: fighter.WaterDamage += value; break;
                case 91: fighter.AirDamage += value; break;
                case 92: fighter.NeutralDamage += value; break;
                case 33: fighter.EarthResPct += value; break;
                case 34: fighter.FireResPct += value; break;
                case 35: fighter.WaterResPct += value; break;
                case 36: fighter.AirResPct += value; break;
                case 37: fighter.NeutralResPct += value; break;
                case ConnectionProtocol.Stat.Prospecting:
                case ConnectionProtocol.Stat.Pods:
                    break;
                default:
                    fighter.Otras.TryGetValue(characteristic, out int had);
                    fighter.Otras[characteristic] = had + value;
                    break;
            }
        }

        // ─── Its spells ─────────────────────────────────────────────────────────

        private static HashSet<int> _spellLevels;
        private static readonly Dictionary<int, (int Spell, int Grade)> _levels = new();
        private static readonly object _spellsLock = new();

        /// <summary>
        /// The SpellLevels a tax collector may be given: those of the spells of type 25, the forty
        /// TaxCollectorEquipment.InitUIEvents lists -- the same forty the monster «Recaudador» has.
        /// </summary>
        internal static bool IsTaxCollectorSpell(int spellLevel)
        {
            lock (_spellsLock)
            {
                if (_spellLevels == null)
                {
                    _spellLevels = new HashSet<int>();
                    try
                    {
                        using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                        connection.Open();
                        var select = connection.CreateCommand();
                        select.CommandText = "SELECT Data FROM SpellTemplates;";
                        using var reader = select.ExecuteReader();
                        while (reader.Read())
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(reader.GetString(0));
                            if (!doc.RootElement.TryGetProperty("typeId", out var type) || type.GetInt32() != TaxCollectorSpellType)
                                continue;
                            if (!doc.RootElement.TryGetProperty("spellLevels", out var levels)
                                || !levels.TryGetProperty("Array", out var array))
                                continue;
                            foreach (var level in array.EnumerateArray()) _spellLevels.Add(level.GetInt32());
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TaxCollector] Could not read its spells: {ex.Message}");
                    }
                }
                return _spellLevels.Contains(spellLevel);
            }
        }

        /// <summary>The spells' type a tax collector's are: 25 (SpellData.typeId, read at +0x2c by the window).</summary>
        private const int TaxCollectorSpellType = 25;

        /// <summary>The spell and grade of a SpellLevels id.</summary>
        internal static (int Spell, int Grade) SpellOfLevel(int spellLevel)
        {
            lock (_spellsLock)
            {
                if (_levels.TryGetValue(spellLevel, out var known)) return known;
            }
            (int, int) found = (0, 0);
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var select = connection.CreateCommand();
                select.CommandText = "SELECT SpellId, Grade FROM SpellLevels WHERE Id = $id;";
                select.Parameters.AddWithValue("$id", spellLevel);
                using var reader = select.ExecuteReader();
                if (reader.Read()) found = (reader.GetInt32(0), reader.GetInt32(1));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TaxCollector] Could not read spell level {spellLevel}: {ex.Message}");
            }
            lock (_spellsLock) _levels[spellLevel] = found;
            return found;
        }

        /// <summary>A spell put in a slot: out of any other slot it was in, and over what was there.</summary>
        internal static SortedDictionary<int, int> WithSpell(IReadOnlyDictionary<int, int> spells, int slot, int spellLevel)
        {
            var changed = new SortedDictionary<int, int>(spells.Where(s => s.Value != spellLevel)
                                                               .ToDictionary(s => s.Key, s => s.Value));
            if (slot >= 0 && slot < TaxCollectorStore.SpellSlots) changed[slot] = spellLevel;
            return changed;
        }

        /// <summary>The spells of two slots swapped, or one moved to an empty one.</summary>
        internal static SortedDictionary<int, int> Swapped(IReadOnlyDictionary<int, int> spells, int from, int to)
        {
            var changed = new SortedDictionary<int, int>(spells.ToDictionary(s => s.Key, s => s.Value));
            if (from < 0 || to < 0 || from >= TaxCollectorStore.SpellSlots || to >= TaxCollectorStore.SpellSlots) return changed;
            bool hadFrom = changed.TryGetValue(from, out int a);
            bool hadTo = changed.TryGetValue(to, out int b);
            changed.Remove(from);
            changed.Remove(to);
            if (hadFrom) changed[to] = a;
            if (hadTo) changed[from] = b;
            return changed;
        }

        // ─── Consulting ─────────────────────────────────────────────────────────

        /// <summary>«Consultar» (hkt { f1 uuid }): the tax collector whole, read-only (hkb).</summary>
        public static async Task ConsultAsync(NetworkStream stream, byte[] payload)
        {
            var (_, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hkt));
            var tc = TaxCollectorStore.ByUuid(texts.GetValueOrDefault(1));
            var alliance = tc == null ? null : AllianceStore.ById(tc.AllianceId);
            if (alliance == null || AllianceStore.AllianceOf(SessionContext.State.CharacterId)?.Id != alliance.Id) return;
            await SendAsync(stream, Op.Hkb, TaxCollectorProtocol.BuildConsult(LeaOf(tc, alliance)));
        }

        // ─── Modifying ──────────────────────────────────────────────────────────

        /// <summary>
        /// «Modificar» (ker { f1 uuid }): standing by it, with the right to modify one's own (12) or
        /// the members' (11), the exchange opens (kds); from elsewhere it is only consulted, and the
        /// window says to go and talk to it («ui.alliance.taxCollectorTalkToModify»).
        /// </summary>
        public static async Task ModifyAsync(NetworkStream stream, byte[] payload)
        {
            var me = SessionContext.State;
            var (_, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Ker));
            var tc = TaxCollectorStore.ByUuid(texts.GetValueOrDefault(1));
            var alliance = tc == null ? null : AllianceStore.ById(tc.AllianceId);
            if (alliance == null || AllianceStore.AllianceOf(me.CharacterId)?.Id != alliance.Id) return;

            int right = tc.OwnerId == me.CharacterId ? ModifyOwn : ModifyMembers;
            if (!AllianceStore.HasRight(me.CharacterId, right))
            {
                await SendAsync(stream, Op.Hla, TaxCollectorProtocol.BuildError(TaxCollectorProtocol.ErrorNoRights));
                return;
            }
            if (tc.MapId != me.MapId || FightHandler.IsGroupFighting(tc.ContextId) || me.IsInFight)
            {
                await SendAsync(stream, Op.Hkb, TaxCollectorProtocol.BuildConsult(LeaOf(tc, alliance)));
                return;
            }

            me.TaxCollectorExchange = tc.Uuid;
            await SendAsync(stream, Op.Kds, TaxCollectorProtocol.BuildModificationStarted(LeaOf(tc, alliance)));
        }

        /// <summary>The rights to modify one's own tax collectors and the members'.</summary>
        private const int ModifyOwn = 12;
        private const int ModifyMembers = 11;

        /// <summary>The right to set the alliance's default tax collector.</summary>
        private const int ConfigureDefault = 8;

        /// <summary>The tax collector whose equipment this session has open, if it is still there and he by it.</summary>
        private static TaxCollectorStore.TaxCollector OpenExchange()
        {
            var me = SessionContext.State;
            if (string.IsNullOrEmpty(me.TaxCollectorExchange)) return null;
            var tc = TaxCollectorStore.ByUuid(me.TaxCollectorExchange);
            if (tc == null || tc.MapId != me.MapId)
            {
                me.TaxCollectorExchange = null;
                return null;
            }
            return tc;
        }

        /// <summary>
        /// kcr with its equipment open: f1 +1 puts the bag's item on it, in the slot of its type --
        /// what was there goes back to the bag first --; -1 takes the item it wears off. False when
        /// no tax collector's equipment is open, so the next window in line gets it.
        /// </summary>
        public static async Task<bool> MoveItemAsync(NetworkStream stream, byte[] payload)
        {
            var tc = OpenExchange();
            if (tc == null) return false;
            if (!StorageHandler.ReadMove(payload, out int quantity, out long uid) || quantity == 0) return true;

            if (quantity > 0) await PutOnAsync(stream, tc, uid);
            else await TakeOffAsync(stream, tc, uid);
            await SendAsync(stream, Op.Iun, ConnectionProtocol.BuildPods(ConnectionProtocol.Carried(Jondo.Unity.Server.Network.SessionContext.State), ConnectionProtocol.Capacity(Jondo.Unity.Server.Network.SessionContext.State)));
            return true;
        }

        private static async Task PutOnAsync(NetworkStream stream, TaxCollectorStore.TaxCollector tc, long uid)
        {
            var item = Equipment.ByUid(uid);
            if (item == null) return;
            int type = Forgemagic.TemplateOf(item.Template)?.Type ?? 0;
            if (!TaxCollectorProtocol.SlotOfType.ContainsKey(type)) return;

            var worn = TaxCollectorStore.EquipmentOf(tc).FirstOrDefault(i => (Forgemagic.TemplateOf(i.Gid)?.Type ?? 0) == type);
            if (worn != null) await TakeOffAsync(stream, tc, worn.Uid);

            long me = SessionContext.State.CharacterId;
            var deposit = StorageStacks.Put(TaxCollectorStore.EquipmentPlace(tc), me, uid, 1);
            if (deposit == null) return;
            Equipment.Remove(deposit.FromUid, deposit.Moved);
            RefreshInventory(me);
            await SendAsync(stream, deposit.LeftInBag == 0
                ? ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(deposit.FromUid))
                : ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(deposit.FromUid, deposit.LeftInBag)));
            await SendAsync(stream, Op.Hkh, TaxCollectorProtocol.BuildEquipmentChanged(tc.Uuid, true,
                TaxCollectorProtocol.Worn(deposit.InStorage), SheetOf(TaxCollectorStore.EquipmentOf(tc))));
        }

        private static async Task TakeOffAsync(NetworkStream stream, TaxCollectorStore.TaxCollector tc, long uid)
        {
            long me = SessionContext.State.CharacterId;
            var place = TaxCollectorStore.EquipmentPlace(tc);
            var worn = StorageStacks.Find(place, uid);
            var withdrawal = worn == null ? null : StorageStacks.Take(place, me, uid, Math.Max(1, worn.Quantity));
            if (withdrawal == null) return;
            Equipment.Add(withdrawal.InBag.Uid, withdrawal.InBag.Gid, withdrawal.Moved, Equipment.Bag, withdrawal.InBag.Effects);
            RefreshInventory(me);
            await SendAsync(stream, withdrawal.Merged
                ? ConnectionProtocol.Push(Op.Ivj, ConnectionProtocol.BuildItemQuantity(withdrawal.InBag.Uid, withdrawal.InBag.Quantity))
                : ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, withdrawal.InBag)));
            await SendAsync(stream, Op.Hkh, TaxCollectorProtocol.BuildEquipmentChanged(tc.Uuid, false,
                TaxCollectorProtocol.Worn(worn), SheetOf(TaxCollectorStore.EquipmentOf(tc))));
        }

        private static void RefreshInventory(long characterId)
        {
            try { GameState.SetInventory(DatabaseManager.LoadInventory(characterId)); }
            catch (Exception ex) { Console.WriteLine($"[TaxCollector] The inventory list could not be read again: {ex.Message}"); }
        }

        /// <summary>kla with its equipment open: the exchange closes (khd), and the alliance sees it anew (hjq).</summary>
        public static async Task<bool> CloseExchangeAsync(NetworkStream stream)
        {
            var me = SessionContext.State;
            if (string.IsNullOrEmpty(me.TaxCollectorExchange)) return false;
            var tc = TaxCollectorStore.ByUuid(me.TaxCollectorExchange);
            me.TaxCollectorExchange = null;
            await SendAsync(stream, Op.Khd, ConnectionProtocol.BuildStorageClosed());

            var alliance = tc == null ? null : AllianceStore.ById(tc.AllianceId);
            if (alliance != null)
                await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hjq,
                    TaxCollectorProtocol.BuildAdded(LeaOf(tc, alliance))));
            return true;
        }

        /// <summary>
        /// A spell set in a slot (hle { f1 uuid, f2 { f2 spell level, f3 slot } }), with its
        /// equipment open: one of the forty of the tax collectors, out of any other slot it was in.
        /// </summary>
        public static async Task SetSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (_, texts, blocks) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hle));
            var tc = ModifiedOne(texts.GetValueOrDefault(1));
            if (tc == null || !blocks.TryGetValue(2, out var lli)) return;
            var (spellLevel, slot) = TaxCollectorProtocol.ReadSpell(lli);
            if (!IsTaxCollectorSpell(spellLevel)) return;
            await SpellsChangedAsync(stream, tc, WithSpell(TaxCollectorStore.SpellsOf(tc), slot, spellLevel));
        }

        /// <summary>A slot emptied (hjx { f1 uuid, f2 slot }).</summary>
        public static async Task RemoveSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (numbers, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hjx));
            var tc = ModifiedOne(texts.GetValueOrDefault(1));
            if (tc == null) return;
            var spells = TaxCollectorStore.SpellsOf(tc);
            spells.Remove((int)numbers.GetValueOrDefault(2));
            await SpellsChangedAsync(stream, tc, spells);
        }

        /// <summary>A spell moved to another slot (hke { f1 to, f2 uuid, f3 from }, fgu.beuk).</summary>
        public static async Task MoveSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (numbers, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hke));
            var tc = ModifiedOne(texts.GetValueOrDefault(2));
            if (tc == null) return;
            await SpellsChangedAsync(stream, tc, Swapped(TaxCollectorStore.SpellsOf(tc),
                                                         (int)numbers.GetValueOrDefault(3), (int)numbers.GetValueOrDefault(1)));
        }

        /// <summary>The tax collector a spell request names, if it is the one this session is modifying.</summary>
        private static TaxCollectorStore.TaxCollector ModifiedOne(string uuid)
        {
            var tc = OpenExchange();
            return tc != null && tc.Uuid == uuid ? tc : null;
        }

        private static async Task SpellsChangedAsync(NetworkStream stream, TaxCollectorStore.TaxCollector tc,
                                                     IReadOnlyDictionary<int, int> spells)
        {
            TaxCollectorStore.SetSpells(tc, spells);
            await SendAsync(stream, Op.Hki, TaxCollectorProtocol.BuildSpells(tc.Uuid, TaxCollectorStore.SpellsOf(tc)));
        }

        // ─── The alliance's default tax collector ───────────────────────────────

        /// <summary>The alliance's preset, named by the alliance's uuid: one per alliance.</summary>
        private static string PresetIdOf(AllianceStore.Alliance alliance) => alliance.Uuid;

        /// <summary>The tax collectors' tab or the personalization one opened (hjy): the preset (hlb).</summary>
        public static async Task PresetsAsync(NetworkStream stream)
        {
            var alliance = AllianceStore.AllianceOf(SessionContext.State.CharacterId);
            if (alliance == null) return;
            await SendAsync(stream, Op.Hlb, TaxCollectorProtocol.BuildPresets(PresetIdOf(alliance),
                TaxCollectorStore.PresetOf(alliance.Id), SheetOf(Array.Empty<HavenBagStore.StoredItem>())));
        }

        /// <summary>A spell set in the preset (hko { f1 { f2 spell level, f3 slot }, f4 preset }).</summary>
        public static async Task SetPresetSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (_, texts, blocks) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hko));
            var alliance = PresetAlliance(texts.GetValueOrDefault(4));
            if (alliance == null || !blocks.TryGetValue(1, out var lli)) return;
            var (spellLevel, slot) = TaxCollectorProtocol.ReadSpell(lli);
            if (!IsTaxCollectorSpell(spellLevel)) return;
            await PresetChangedAsync(stream, alliance, WithSpell(TaxCollectorStore.PresetOf(alliance.Id), slot, spellLevel));
        }

        /// <summary>A slot of the preset emptied (hli { f1 slot, f2 preset }).</summary>
        public static async Task RemovePresetSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (numbers, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hli));
            var alliance = PresetAlliance(texts.GetValueOrDefault(2));
            if (alliance == null) return;
            var spells = TaxCollectorStore.PresetOf(alliance.Id);
            spells.Remove((int)numbers.GetValueOrDefault(1));
            await PresetChangedAsync(stream, alliance, spells);
        }

        /// <summary>A preset spell moved (hkw { f1 from, f2 preset, f3 to }, fgu.beup).</summary>
        public static async Task MovePresetSpellAsync(NetworkStream stream, byte[] payload)
        {
            var (numbers, texts, _) = TaxCollectorProtocol.Read(ConnectionProtocol.ReadPayload(payload, Op.Hkw));
            var alliance = PresetAlliance(texts.GetValueOrDefault(2));
            if (alliance == null) return;
            await PresetChangedAsync(stream, alliance, Swapped(TaxCollectorStore.PresetOf(alliance.Id),
                                                               (int)numbers.GetValueOrDefault(1), (int)numbers.GetValueOrDefault(3)));
        }

        /// <summary>The alliance whose preset a request names, if this character may set it.</summary>
        private static AllianceStore.Alliance PresetAlliance(string presetId)
        {
            long me = SessionContext.State.CharacterId;
            var alliance = AllianceStore.AllianceOf(me);
            if (alliance == null || PresetIdOf(alliance) != presetId) return null;
            return AllianceStore.HasRight(me, ConfigureDefault) ? alliance : null;
        }

        private static async Task PresetChangedAsync(NetworkStream stream, AllianceStore.Alliance alliance,
                                                     IReadOnlyDictionary<int, int> spells)
        {
            TaxCollectorStore.SetPreset(alliance.Id, spells);
            await AllianceHandler.TellMembersAsync(alliance, ConnectionProtocol.Push(Op.Hlh,
                TaxCollectorProtocol.BuildPresetSpells(PresetIdOf(alliance), TaxCollectorStore.PresetOf(alliance.Id))));
        }

        // ─── What goes with it when it goes ─────────────────────────────────────

        /// <summary>
        /// What a tax collector wore goes to its owner's bank (info 0/1579's «Su equipo está en tu
        /// banco»). Called before it is taken away.
        /// </summary>
        internal static async Task ReturnEquipmentAsync(TaxCollectorStore.TaxCollector tc)
        {
            var worn = StorageStacks.TakeAll(TaxCollectorStore.EquipmentPrefix(tc));
            if (worn.Count == 0) return;
            var owner = DatabaseManager.GetCharacterById(tc.OwnerId);
            if (owner == null) return;
            foreach (var item in worn)
                await Bank.AddItemAsync(owner.AccountId, item.Gid, Math.Max(1, item.Quantity), item.Effects);
            Program.LogDebug($"[TaxCollector] {worn.Count} item(s) of {tc.Uuid} go to the bank of {tc.OwnerName}.");
        }

        /// <summary>
        /// Tells its owner what happened to a tax collector, now if he is connected -- the chat line
        /// of hkx -- or at his next entry.
        /// </summary>
        internal static async Task TellOwnerAsync(TaxCollectorStore.TaxCollector tc, int kind, long whoId, string whoName)
        {
            var absence = new TaxCollectorStore.Absence(kind, tc.FirstNameId, tc.LastNameId, tc.MapId, tc.SubAreaId,
                                                        whoId, whoName ?? "");
            var owner = SessionRegistry.FindByCharacter(tc.OwnerId);
            if (owner != null) return;
            TaxCollectorStore.RecordAbsence(tc.OwnerId, absence);
            await Task.CompletedTask;
        }

        /// <summary>At world entry: what happened to his tax collectors while he was away (hkx).</summary>
        public static async Task OnWorldEntryAsync(NetworkStream stream, long characterId)
        {
            var absences = TaxCollectorStore.TakeAbsences(characterId);
            if (absences.Count == 0) return;
            await SendAsync(stream, Op.Hkx, TaxCollectorProtocol.BuildAbsences(absences));
        }
    }
}
