using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What the character is wearing, and what it adds up to.
    ///
    /// The inventory itself still comes out of the capture, in ivx, and every item there carries
    /// its slot and its effects:
    ///
    ///   ivx: f3 (repeated) { f1: slot, f5 { f1: template, f2 (repeated) { f4: value, f11: effect },
    ///                                       f3: how many, f4: uid } }
    ///
    /// Slots 0 to 15 are worn — amulet, weapon, the two rings, belt, boots, hat, cloak, pet, the
    /// six dofus and the shield, one item in each — and 63 is the bag, which holds the other 593.
    ///
    /// Reading it here is what finally puts the equipment on the sheet. Until now an item could be
    /// moved into a slot and nothing happened: no damage, no resistances, not the AP and MP of an
    /// exomaged ring, not the set bonuses. The effects go into field 7 of each characteristic of
    /// the kub, which is the one the client shows as coming from equipment.
    ///
    /// Set bonuses are NOT in here. They depend on how many pieces of a set are worn at once, and
    /// that lives in item_sets.json, which we do not have.
    /// </summary>
    public static class Equipment
    {
        /// <summary>
        /// Un efecto tal y como lo declara el objeto: el valor fijo y el par de dados.
        ///
        /// Son tres números y no uno porque el protocolo distingue entre ellos. Un daño de arma
        /// viaja como rango, el hechizo que da un dofus viaja como dos ids, y una vitalidad viaja
        /// como un número suelto; guardar solo "el valor" era lo que dejaba a las armas sin daños.
        /// <see cref="EffectFields.Shape"/> decide con estos tres cómo sale al cable.
        /// </summary>
        public readonly struct ItemEffect
        {
            public ItemEffect(int effect, long value, long diceNum, long diceSide, string? text = null)
            {
                Effect = effect; Value = value; DiceNum = diceNum; DiceSide = diceSide; Text = text;
            }

            public int Effect { get; }
            public long Value { get; }
            public long DiceNum { get; }
            public long DiceSide { get; }

            /// <summary>
            /// Lo que llevan los efectos que no son un número: "Fabricado por: #4" (el 988) y los
            /// dos que se le parecen. El cliente los pinta metiendo esta cadena donde va el #4.
            /// </summary>
            public string? Text { get; }

            /// <summary>Lo que suma a la ficha, que no es lo mismo que lo que viaja.</summary>
            public long Sheet => EffectFields.SheetValue(Effect, Value, DiceNum, DiceSide);
        }

        public sealed class Item
        {
            public long Uid { get; set; }
            public int Template { get; set; }
            public int Position { get; set; }
            public int Quantity { get; set; } = 1;
            /// <summary>Los efectos que el objeto declara, con sus tres números.</summary>
            public List<ItemEffect> Effects { get; } = new List<ItemEffect>();
        }

        /// <summary>The bag. Anything above the worn slots and not worn.</summary>
        public const int Bag = 63;

        /// <summary>Slots that count as being worn.</summary>
        public const int LastWornSlot = 15;

        private static Dictionary<long, Item> Items => SessionContext.State.EquipmentItems;

        public static int Count => Items.Count;
        public static System.Collections.Generic.IEnumerable<Item> All => Items.Values;
        public static int Worn
        {
            get
            {
                int n = 0;
                foreach (var item in Items.Values) if (IsWorn(item.Position)) n++;
                return n;
            }
        }

        public static bool IsWorn(int position) => position >= 0 && position <= LastWornSlot;

        /// <summary>
        /// Reads the character's inventory out of the database, which is where it belongs.
        ///
        /// Until now the inventory was replayed from the capture, so it was somebody else's: the
        /// items could not be moved for good, they were not the player's to keep, and every uid in
        /// them belonged to another account. This reads CharacterItems, and what the client sees
        /// is what the database says.
        /// </summary>
        public static void LoadFrom(long characterId)
        {
            Items.Clear();
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    DatabaseManager.WorldConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = "SELECT Uid, Gid, Quantity, Position, Effects " +
                                      "FROM CharacterItems WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var item = new Item
                    {
                        Uid = reader.GetInt64(0),
                        Template = reader.GetInt32(1),
                        Quantity = reader.IsDBNull(2) ? 1 : reader.GetInt32(2),
                        Position = reader.IsDBNull(3) ? Bag : reader.GetInt32(3),
                    };

                    if (!reader.IsDBNull(4)) ReadEffects(reader.GetString(4), item);

                    if (item.Uid != 0) Items[item.Uid] = item;
                }

                var doubled = RepairDoubledSlots();
                if (doubled.Count > 0)
                {
                    Console.WriteLine($"[Equipment] {doubled.Count} objetos estaban compartiendo hueco " +
                                      "con otro; se han devuelto a la bolsa.");
                }

                Console.WriteLine($"[Equipment] {Items.Count} items from the database, {Worn} worn.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Equipment] Could not read the inventory: {ex.Message}");
            }
        }

        /// <summary>
        /// Los efectos de un objeto, como los guarda la base de datos.
        ///
        ///   [[efecto, valor, diceNum, diceSide], ...]
        ///
        /// Se admite también la forma vieja de dos números, [[efecto, valor]], que es como se
        /// guardaban antes de que se supiera que el protocolo distingue entre valor y dados.
        /// </summary>
        /// <summary>Los efectos de una cadena guardada, sueltos, sin objeto que los sostenga.</summary>
        public static IReadOnlyList<ItemEffect> ParseEffects(string? json)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<ItemEffect>();

            var prestado = new Item();
            ReadEffects(json, prestado);
            return prestado.Effects;
        }

        private static void ReadEffects(string json, Item item)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                Span<long> numbers = stackalloc long[4];
                foreach (var entry in doc.RootElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array) continue;

                    numbers.Clear();
                    int n = 0;
                    string? text = null;
                    foreach (var number in entry.EnumerateArray())
                    {
                        // Un quinto elemento de texto: lo llevan los efectos que no son un número,
                        // como el 988, "Fabricado por".
                        if (number.ValueKind == JsonValueKind.String) { text = number.GetString(); continue; }
                        if (n >= 4) break;
                        numbers[n++] = number.TryGetInt64(out long v) ? v : 0;
                    }
                    if (n < 2) continue;

                    item.Effects.Add(new ItemEffect((int)numbers[0], numbers[1],
                                                    n > 2 ? numbers[2] : 0,
                                                    n > 3 ? numbers[3] : 0,
                                                    text));
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads the inventory out of an ivx as it goes past. Kept for the capture's own inventory;
        /// <see cref="LoadFrom"/> is what the emulator uses now.
        /// </summary>
        public static void LearnFrom(byte[] ivx)
        {
            if (ivx == null || ivx.Length == 0) return;

            if (Items.Count > 0) return;   // la base de datos manda sobre la captura
            var found = new Dictionary<long, Item>();
            foreach (var entry in ProtoMessage.Parse(ivx).Fields)
            {
                if (entry.FieldNumber != 3 || entry.WireType != 2) continue;

                // Position zero and not the bag, for the same reason the amulet could not be put
                // on: its slot IS zero, so proto3 leaves the field out and the item arrives with
                // no position at all. Defaulting to the bag quietly dropped the amulet — thirteen
                // effects of it — out of everything the sheet added up.
                var item = new Item { Position = 0 };
                foreach (var f in ProtoMessage.Parse(entry.BytesValue).Fields)
                {
                    if (f.FieldNumber == 1 && f.WireType == 0) item.Position = (int)f.VarIntValue;
                    else if (f.FieldNumber == 5 && f.WireType == 2) ReadBody(f.BytesValue, item);
                }
                if (item.Uid != 0) found[item.Uid] = item;
            }

            if (found.Count == 0) return;
            Items.Clear();
            foreach (var pair in found) Items[pair.Key] = pair.Value;

            Console.WriteLine($"[Equipment] {Items.Count} items read from the inventory, " +
                              $"{Worn} of them worn.");
        }

        private static void ReadBody(byte[] body, Item item)
        {
            foreach (var f in ProtoMessage.Parse(body).Fields)
            {
                if (f.FieldNumber == 1 && f.WireType == 0) item.Template = (int)f.VarIntValue;
                else if (f.FieldNumber == 4 && f.WireType == 0) item.Uid = f.VarIntValue;
                else if (f.FieldNumber == 2 && f.WireType == 2)
                {
                    long value = 0, diceNum = 0, diceSide = 0;
                    int effect = 0;
                    foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                    {
                        if (g.FieldNumber == 11 && g.WireType == 0) effect = (int)g.VarIntValue;
                        else if (g.FieldNumber == 4 && g.WireType == 0) value = g.VarIntValue;
                        else if (g.FieldNumber == EffectFields.AsRange && g.WireType == 2)
                        {
                            // f5 { f1: máximo, f2: mínimo }: se guarda como el rango que es.
                            foreach (var h in ProtoMessage.Parse(g.BytesValue).Fields)
                            {
                                if (h.WireType != 0) continue;
                                if (h.FieldNumber == 1) diceSide = h.VarIntValue;
                                else if (h.FieldNumber == 2) diceNum = h.VarIntValue;
                            }
                        }
                        else if (g.FieldNumber == EffectFields.AsDice && g.WireType == 2)
                        {
                            foreach (var h in ProtoMessage.Parse(g.BytesValue).Fields)
                            {
                                if (h.WireType != 0) continue;
                                if (h.FieldNumber == 1) value = h.VarIntValue;
                                else if (h.FieldNumber == 2) diceNum = h.VarIntValue;
                                else if (h.FieldNumber == 3) diceSide = h.VarIntValue;
                            }
                        }
                    }
                    if (effect != 0) item.Effects.Add(new ItemEffect(effect, value, diceNum, diceSide));
                }
            }
        }

        /// <summary>Moves an item, and says whether we knew about it at all.</summary>
        public static bool Move(long uid, int position)
        {
            if (!Items.TryGetValue(uid, out var item)) return false;
            item.Position = position;
            return true;
        }

        /// <summary>El objeto con ese identificador, si lo tenemos.</summary>
        public static Item? ByUid(long uid) => Items.TryGetValue(uid, out var item) ? item : null;

        /// <summary>
        /// Quita del inventario en memoria lo que se ha ido a otro sitio —al cofre del merkasako,
        /// por ejemplo—. Si quedan unidades, se resta; si no, desaparece.
        /// </summary>
        public static void Remove(long uid, int quantity)
        {
            if (!Items.TryGetValue(uid, out var item)) return;

            if (quantity <= 0 || quantity >= item.Quantity) Items.Remove(uid);
            else item.Quantity -= quantity;
        }

        /// <summary>Mete en el inventario en memoria algo que viene de fuera.</summary>
        public static Item Add(long uid, int template, int quantity, int position, string? effects)
        {
            if (Items.TryGetValue(uid, out var existing))
            {
                existing.Quantity += Math.Max(1, quantity);
                return existing;
            }

            var item = new Item
            {
                Uid = uid,
                Template = template,
                Quantity = Math.Max(1, quantity),
                Position = position,
            };
            if (!string.IsNullOrEmpty(effects)) ReadEffects(effects, item);

            Items[uid] = item;
            return item;
        }

        /// <summary>
        /// Lo que ya ocupa un hueco, sin contar el objeto que va a entrar en él.
        ///
        /// En un hueco cabe una cosa. Mover algo a un hueco ocupado tiene que echar lo que hubiera,
        /// y eso no se hacía: se guardaba la posición nueva y punto, así que dos monturas —o tres—
        /// acababan las dos en el hueco 8 a la vez. Como el aspecto se decide mirando quién está en
        /// el 8, el que mandaba era siempre el primero que se encontrase, y de ahí que cambiar de
        /// montura no cambiara nada y que quitárselas todas no bajara al personaje al suelo.
        ///
        /// La bolsa no cuenta: ahí caben las mil y pico.
        /// </summary>
        public static List<Item> Occupants(int position, long exceptUid = 0)
        {
            var busy = new List<Item>();
            if (!IsWorn(position)) return busy;

            foreach (var item in Items.Values)
            {
                if (item.Position == position && item.Uid != exceptUid) busy.Add(item);
            }
            return busy;
        }

        /// <summary>
        /// Deja un solo objeto por hueco, y devuelve los que ha tenido que mandar a la bolsa.
        ///
        /// Es para reparar lo que quedó guardado mientras el hueco no se vaciaba: quien tenga tres
        /// monturas puestas en el 8 no las va a poder quitar de una en una, porque el cliente solo
        /// sabe de la última. Se hace al cargar el inventario y se escribe en la base de datos, así
        /// que se paga una vez.
        /// </summary>
        private static List<Item> RepairDoubledSlots()
        {
            var moved = new List<Item>();
            var taken = new HashSet<int>();

            foreach (var item in Items.Values)
            {
                if (!IsWorn(item.Position)) continue;
                if (taken.Add(item.Position)) continue;

                item.Position = Bag;
                moved.Add(item);
            }

            foreach (var item in moved)
                DatabaseManager.SaveItemPosition(item.Uid, Bag, SessionContext.State.CharacterId);
            return moved;
        }

        /// <summary>
        /// What everything worn adds up to, characteristic by characteristic. An effect that takes
        /// away counts as taking away: BonusType is -1 on those and the value it carries is
        /// positive, so adding it blind turns a penalty into a bonus.
        ///
        /// The set bonuses go in here too, on top of what each piece gives on its own. Without
        /// them the sheet was short by a fixed amount everywhere: the captured account's strength
        /// came out at 575 against the 745 the real server sends.
        /// </summary>
        public static Dictionary<int, long> Bonuses()
        {
            var total = new Dictionary<int, long>();
            var wornTemplates = new List<int>();

            foreach (var item in Items.Values)
            {
                if (!IsWorn(item.Position)) continue;
                wornTemplates.Add(item.Template);

                foreach (var entry in item.Effects)
                {
                    if (!EffectTable.TryGet(entry.Effect, out var what)) continue;

                    // Lo que suma a la ficha no es lo que viaja: un efecto compuesto —el hechizo de
                    // un dofus, un título— nombra algo, no mueve una característica.
                    long value = entry.Sheet;
                    if (value == 0) continue;

                    // Not life. Several effects point at characteristic 0 and adding them up gave
                    // the character twenty-five thousand hit points from its gear; the real kub of
                    // that same account puts nothing in field 7 of characteristic 0 at all. Life
                    // is the base plus vitality and the client works it out.
                    if (what.Characteristic == 0) continue;

                    total.TryGetValue(what.Characteristic, out long had);
                    total[what.Characteristic] = had + what.Sign * value;
                }
            }

            foreach (var (effect, value) in ItemSets.BonusesFor(wornTemplates))
            {
                if (!EffectTable.TryGet(effect, out var what)) continue;
                if (what.Characteristic == 0) continue;

                total.TryGetValue(what.Characteristic, out long had);
                total[what.Characteristic] = had + what.Sign * value;
            }

            return total;
        }
        /// <summary>
        /// Puts one item in the bag: database, memory and the client, in that order.
        /// </summary>
        /// <remarks>
        /// Here rather than in the chat-command handler it grew up in, because it is not about chat
        /// commands: quest rewards hand out items too, and 5,582 of the 6,707 rewards in the game
        /// carry one. Two copies of this would be two answers to "was the client told", and the one
        /// that forgot would show an inventory that only fills in after a relog.
        /// </remarks>
        public static async Task<bool> GiveAsync(NetworkStream stream, int gid, int quantity)
            => await GrantAsync(stream, gid, quantity) != null;

        /// <summary>
        /// The same, giving back what was created instead of only whether it worked.
        /// </summary>
        /// <remarks>
        /// For the callers that have to say WHAT they handed over — the live administration API
        /// answers with the uid and the effects — and it is the same method rather than a second
        /// one so that the two can never disagree about whether the client was told.
        /// </remarks>
        public static async Task<HavenBagStore.StoredItem?> GrantAsync(NetworkStream stream,
                                                                       int gid, int quantity)
        {
            if (!DatabaseManager.TryGetItemTemplateEffects(gid, out string effects)) return null;

            long uid = DatabaseManager.NextItemUid();
            if (!DatabaseManager.InsertCharacterItem(uid, SessionContext.State.CharacterId, gid, quantity,
                                                     Bag, effects))
                return null;

            Add(uid, gid, quantity, Bag, effects);

            var legacy = new PlayerItem
            {
                Uid = uid,
                ItemId = gid,
                Quantity = quantity,
                Position = Bag,
                RawEffects = effects,
            };
            foreach (var effect in ParseEffects(effects))
            {
                legacy.Effects.TryGetValue(effect.Effect, out int had);
                legacy.Effects[effect.Effect] = had + (int)effect.Value;
            }
            GameState.AddInventoryItem(legacy);

            var granted = new HavenBagStore.StoredItem
            {
                Uid = uid,
                Gid = gid,
                Quantity = quantity,
                Effects = effects,
            };

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iua, ConnectionProtocol.BuildItemArrived(3, granted)));
            return granted;
        }

        /// <summary>
        /// The one place the worn-item cache is written.
        /// </summary>
        /// <remarks>
        /// That cache — <c>SessionState.EquippedItems</c> — feeds <c>StatsHandler.GetEquipBonus</c>,
        /// and through it the COMBAT sheet: maximum health, initiative, the lot. It is not what
        /// builds the characteristic sheet, which comes from <see cref="Bonuses"/> over the real
        /// inventory; getting those two the wrong way round is easy and the comment that used to
        /// live on EquipmentHandler had them swapped.
        ///
        /// It exists because there were THREE places filling that one dictionary — character
        /// selection, the inventory handler and the equipment handler — and the three disagreed
        /// about both of the things that matter:
        ///
        ///   what counts as worn   two said "0 to 62", which is the whole bag, so an item sitting
        ///                         in slot 35 counted towards combat stats until it was moved
        ///   which value           two summed the raw parameter and one summed <c>Sheet</c>. An
        ///                         item stored as [[100, 0, 2, 7]] is worth 0 by one and 7 by the
        ///                         other, so the same ring gave different stats depending on
        ///                         whether it had been re-equipped since logging in — and went
        ///                         back to the first answer on the next relog.
        ///
        /// <c>Sheet</c> is the right one: it is what <see cref="Bonuses"/> uses, so the combat
        /// sheet and the characteristic sheet finally agree about the same item.
        /// </remarks>
        public static void RememberWorn(long uid, int position, IEnumerable<ItemEffect>? effects)
        {
            if (!IsWorn(position))
            {
                SessionContext.State.RemoveEquippedItem(uid);
                return;
            }

            if (effects == null)
            {
                // Worn, but nothing known about it. Leaving the cache alone beats writing an entry
                // with no stats, which would silently strip whatever the item was giving.
                return;
            }

            var worn = new EquippedItemInfo { Slot = position };
            foreach (var effect in effects)
            {
                worn.Stats.TryGetValue(effect.Effect, out int had);
                worn.Stats[effect.Effect] = had + (int)Math.Clamp(effect.Sheet, int.MinValue, int.MaxValue);
            }

            SessionContext.State.SetEquippedItem(uid, worn);
        }

        /// <summary>The same, from the effects as the database stored them.</summary>
        /// <remarks>
        /// For the two callers that run before <see cref="LoadFrom"/> has read this character's
        /// inventory — character selection is one — and therefore have a <c>PlayerItem</c> rather
        /// than an <see cref="Item"/>. It parses the raw json so that they arrive at the same
        /// numbers as everybody else instead of at the summed parameter.
        /// </remarks>
        public static void RememberWorn(long uid, int position, string? rawEffects)
            => RememberWorn(uid, position, string.IsNullOrEmpty(rawEffects) ? null : ParseEffects(rawEffects));

        /// <summary>
        /// How many of a template the character is carrying, counting every stack of it.
        /// </summary>
        /// <remarks>
        /// Every stack, because the same template can sit in several: <see cref="Add"/> merges by
        /// uid and not by template, so two arrivals of the same thing from different places are two
        /// entries. Counting only the first would tell a player who has five that they have three.
        ///
        /// Worn items count. A quest that asks for a ring the player is wearing is asking for the
        /// ring, and refusing it because it is on a finger would be a rule nobody wrote.
        /// </remarks>
        public static int HowMany(int template)
        {
            int total = 0;
            foreach (var item in Items.Values)
            {
                if (item.Template == template) total += item.Quantity;
            }

            return total;
        }

        /// <summary>
        /// Takes a number of one template away and tells the client, or takes nothing at all.
        /// </summary>
        /// <remarks>
        /// All or nothing on purpose. A quest handover that ate three of the five ortie it wanted
        /// and then found there was no fifth would leave the player poorer and the quest where it
        /// was, so the count is checked before anything is touched.
        ///
        /// The wire half mirrors <see cref="Handlers.DestroyItemHandler"/> rather than inventing a
        /// second answer to "was the client told": a stack that goes entirely is <c>ium</c>, one
        /// that is only reduced is sent again with its new quantity. Getting that backwards leaves
        /// a phantom in the bag until the next relog.
        /// </remarks>
        public static async Task<bool> TakeAsync(NetworkStream stream, int template, int count)
        {
            if (count <= 0) return true;
            if (HowMany(template) < count) return false;

            long characterId = SessionContext.State.CharacterId;
            int left = count;

            // A copy, because the loop removes from the dictionary it would otherwise be walking.
            var stacks = new List<Item>(Items.Values);
            foreach (var item in stacks)
            {
                if (left <= 0) break;
                if (item.Template != template) continue;

                int taking = Math.Min(left, item.Quantity);
                bool whole = taking >= item.Quantity;

                if (!DatabaseManager.DestroyCharacterItem(characterId, item.Uid, taking)) return false;
                Equipment.Remove(item.Uid, taking);
                left -= taking;

                if (whole)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Op.Ium, ConnectionProtocol.BuildItemGone(item.Uid)));
                    continue;
                }

                var rest = HavenBagStore.FromInventory(characterId, item.Uid);
                if (rest != null)
                {
                    await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                        ConnectionProtocol.Push(Handlers.ChestHandler.ArrivesInBag,
                            ConnectionProtocol.BuildItemArrived(
                                Handlers.ChestHandler.FieldOf(Handlers.ChestHandler.ArrivesInBag), rest)));
                }
            }

            return left == 0;
        }
    }
}
