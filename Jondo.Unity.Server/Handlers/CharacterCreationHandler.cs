using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Creating a character.
    ///
    /// From the capture of a creation that goes well, on an empty server:
    ///
    ///   client  kvz { f1 { f1: name, f2: face, f3: colours, f5: 26, f7: breed } }
    ///   server  kvb            EMPTY, which is how yes is said
    ///   server  kvi            the list again, now with the character in it
    ///   client  kvl            "I play with that one"
    ///
    /// And from the one that goes wrong, the character limit one: <c>kvb { f2: 3 }</c>. So the same
    /// message serves for both things and what tells them apart is whether it carries a reason.
    ///
    /// The colours arrive as signed varints, and -1 means "the breed's own". The client sends them
    /// all as -1 when the palette is not touched.
    /// </summary>
    public static class CharacterCreationHandler
    {
        /// <summary>Where everybody starts: the zaap of the city of Astrub.</summary>
        public const long StartingMap = 191105026L;

        /// <summary>What he starts with: level, kamas and the scroll characteristics.</summary>
        public const int StartingLevel = 1;
        public const long StartingKamas = 1_000_000L;

        /// <summary>
        /// What the scrolls give in each characteristic, and it goes APART from the base.
        /// </summary>
        /// <remarks>
        /// A hundred and not a hundred and one: in the captures of real characters each characteristic's
        /// f3 -- the scrolls field -- is 100 in 156 captures and 4,815 appearances, and 101 does not come
        /// up once. And in its own column and not in the base, because the base is the points spent: put
        /// there, a freshly made level 200 had 183 points to spend instead of 995.
        /// </remarks>
        public const int ScrolledStat = 100;

        /// <summary>
        /// The adventurer's set, which is set number 5 of the game: cape, hat, ring, boots, belt and
        /// amulet.
        /// </summary>
        /// <remarks>
        /// IN THE BAG, not worn, and on purpose. The six pieces require between level 4 and level 9 --
        /// the ring 4, the amulet 5, the belt 6, the boots 7, the cape 8 and the hat 9 -- and a new
        /// character starts at 1. Giving it worn slipped it in through the back door: it is written
        /// straight into the database, without going through the level check EquipmentHandler does, so
        /// the character appeared wearing things he cannot wear and as soon as he took one off he could
        /// not put it back on.
        ///
        /// It is a gift, not a uniform: it is in the bag from minute one and each piece is put on when
        /// its time comes.
        /// </remarks>
        private static readonly (int Gid, int Slot)[] AdventurerSet =
        {
            (2478, Managers.Equipment.Bag),    // amuleto, nivel 5
            (2475, Managers.Equipment.Bag),    // anillo, nivel 4
            (2477, Managers.Equipment.Bag),    // belt, level 6
            (2476, Managers.Equipment.Bag),    // botas, nivel 7
            (2474, Managers.Equipment.Bag),    // sombrero, nivel 9
            (2473, Managers.Equipment.Bag),    // capa, nivel 8

            // And the keyring, in the BAG and not worn. In the real game the tutorial gives it -- line
            // 1111691, «Take this magic keyring: it will open the doors of the dungeons for you» -- and
            // here it is given from the start, which is what was asked for.
            //
            // It is not spent when used: it opens the 107 dungeons that accept it, one free entry
            // per dungeon and week. See DungeonHandler and DungeonKeyring.
            (Keyring, Managers.Equipment.Bag),
        };

        /// <summary>The dungeon keyring, item 10207.</summary>
        private const int Keyring = DungeonHandler.Keyring;

        /// <summary>What a new character comes out with. For the tests, and to be able to look at it.</summary>
        public static IReadOnlyList<(int Gid, int Slot)> StarterItems => AdventurerSet;

        /// <summary>
        /// The client asks for a random name (kvk) and expects the same message back with one inside.
        /// Without an answer the dice button did nothing.
        ///
        /// The shape is set by rule 1 of the client's NamingRules:
        /// <c>^([A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2})$</c> -- a capital, lower case letters, and up to two
        /// more pieces separated by a hyphen. Here it is done with two.
        /// </summary>
        public static async Task SuggestNameAsync(NetworkStream stream)
        {
            string name = RandomName();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kvk, Pb.New().Str(1, name).Build()));

            Console.WriteLine($"[Personajes] Nombre sugerido: {name}");
        }

        private static readonly Random _rand = new Random();
        private static readonly string[] Start = { "Ka", "Bro", "Zel", "Mir", "Tan", "Ork", "Fli",
                                                   "Nol", "Sar", "Dju", "Wen", "Pyr", "Gob", "Ily" };
        private static readonly string[] Middle = { "ra", "de", "lo", "ni", "sa", "tu", "ve", "mi",
                                                    "kro", "bel", "dan", "gor" };
        private static readonly string[] End = { "n", "s", "r", "l", "th", "x", "", "ne", "ka" };

        private static string RandomName()
        {
            var sb = new StringBuilder();
            sb.Append(Start[_rand.Next(Start.Length)]);
            for (int i = 0; i < _rand.Next(1, 3); i++) sb.Append(Middle[_rand.Next(Middle.Length)]);
            sb.Append(End[_rand.Next(End.Length)]);

            if (_rand.Next(3) == 0)
            {
                sb.Append('-').Append(Start[_rand.Next(Start.Length)].ToLowerInvariant())
                  .Append(Middle[_rand.Next(Middle.Length)]);
            }
            return sb.ToString();
        }

        /// <summary>The client has pressed PLAY on the creation screen.</summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] payload, long accountId,
                                             int serverId)
        {
            // Without an account nothing is created, and this was missing. The branch that gets here does
            // not look at isAuthenticated -- the one next to it does, and the selection one refuses
            // accountId<=0 and checks the owner as well --, CreateAsync did not look at the parameter, and
            // in the database AccountId is a plain INTEGER NOT NULL: no FOREIGN KEY to Accounts, so the
            // row with account 0 goes in and the transaction commits. A socket that never presented its
            // ticket could fill the table with orphan characters.
            if (accountId <= 0)
            {
                Console.WriteLine("[Game Node] Creación de personaje sin cuenta resuelta: no se ha " +
                                  "presentado el ticket. Se rechaza.");
                await RefuseAsync(stream, CreationRefused);
                return;
            }

            byte[]? kvz = ConnectionProtocol.ReadPayload(payload, Op.Kvz);
            if (kvz == null) return;

            string name = "";
            int head = 0, breed = 1, sex = 0;
            var colors = new List<long>();

            foreach (var outer in ProtoMessage.Parse(kvz).Fields)
            {
                if (outer.FieldNumber != 1 || outer.WireType != 2) continue;
                foreach (var f in ProtoMessage.Parse(outer.BytesValue).Fields)
                {
                    if (f.FieldNumber == 1 && f.WireType == 2) name = Encoding.UTF8.GetString(f.BytesValue);
                    else if (f.FieldNumber == 2 && f.WireType == 0) head = (int)f.VarIntValue;
                    else if (f.FieldNumber == 3 && f.WireType == 2) colors = Packed(f.BytesValue);
                    else if (f.FieldNumber == 4 && f.WireType == 0) sex = (int)f.VarIntValue;
                    else if (f.FieldNumber == 7 && f.WireType == 0) breed = (int)f.VarIntValue;
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await RefuseAsync(stream, CreationRefused);
                return;
            }

            if (DatabaseManager.CharacterNameTaken(name))
            {
                Console.WriteLine($"[Personajes] El nombre \"{name}\" ya está cogido.");
                await RefuseAsync(stream, NameAlreadyTaken);
                return;
            }

            long id = DatabaseManager.CreateCharacter(accountId, serverId, name, breed, sex, head,
                                                      colors, StartingMap, StartingLevel,
                                                      StartingKamas, ScrolledStat, AdventurerSet);
            if (id == 0)
            {
                await RefuseAsync(stream, CreationRefused);
                return;
            }

            // Yes: the kvb goes empty. With a reason inside it is a no.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kvb));

            // The whole list again, and WITH THE FRESHLY CREATED ONE FIRST.
            //
            // Both halves are needed and the second is the one that was missing. Sending only the kvi
            // left the client with the list from BEFORE the creation; that was already fixed. But the old
            // character still went into the world, and the log shows it beyond doubt:
            //
            //   00:16:05.798  Creado Tymaviejas (id 13825564)
            //   00:16:05.803  Selected character 13825558     <- five milliseconds later
            //
            // So the client does not choose: it takes THE FIRST one on the list and sends its selection
            // at once. And our list comes from an ORDER BY Id, so the freshly created one, which has the
            // highest id, went last.
            //
            // That the new one goes first is measured in «crear personaje - borrar personaje»: the kvi
            // that follows the kvb carries «Vos-Xx», the one just created, ahead of «Berru», who was
            // already there. It is reordered only here and not in GetCharactersByAccountId, because the
            // order of the normal selection screen is something else and has not been measured.
            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            var elNuevo = characters.Find(c => c.Id == id);
            if (elNuevo != null)
            {
                characters.Remove(elNuevo);
                characters.Insert(0, elNuevo);
            }

            foreach (byte[] frame in ConnectionProtocol.CharacterListFrames(characters))
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

            Console.WriteLine($"[Personajes] Creado {name} (id {id}), raza {breed}, en el zaap de " +
                              $"Astrub, con el conjunto del aventurero y {StartingKamas} kamas.");
        }

        /// <summary>The reasons the kvb carries when it says no. 3 is the limit one.</summary>
        private const int CreationRefused = 1;
        private const int NameAlreadyTaken = 2;

        private static async Task RefuseAsync(NetworkStream stream, int reason)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Kvb, Pb.New().Var(2, reason).Build()));
        }

        /// <summary>Varints one after another, which is how the colours travel. -1 is "the breed's".</summary>
        private static List<long> Packed(byte[] bytes)
        {
            var values = new List<long>();
            long value = 0;
            int shift = 0;
            foreach (byte b in bytes)
            {
                value |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) != 0) { shift += 7; continue; }
                values.Add(value);
                value = 0; shift = 0;
            }
            return values;
        }
    }
}
