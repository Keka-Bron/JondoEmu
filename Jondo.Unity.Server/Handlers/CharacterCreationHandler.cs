using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Server.Network.Connection;

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
        /// <summary>
        /// The zaap of the city of Astrub: where whoever has no better place goes (a defeat with
        /// no save point, the simulated players' home), and where the tutorial ends up.
        /// </summary>
        public const long StartingMap = 191105026L;

        /// <summary>
        /// Where a new character wakes up: before the god's statue of the guided tutorial, the
        /// same map for every class, on cell 329 facing 1 (the capture of the whole tutorial, its
        /// first jss; the creation capture lands there too).
        /// </summary>
        public const long TutorialMap = 241437185L;
        public const int TutorialCell = 329;

        /// <summary>What he starts with: level, kamas and the scroll characteristics.</summary>
        public const int StartingLevel = 1;

        /// <summary>
        /// What the scrolls give in each characteristic by default, and it goes APART from the base.
        /// The server settings may change it for new characters (StartingScrollStats).
        /// </summary>
        /// <remarks>
        /// A hundred and not a hundred and one: in the captures of real characters each characteristic's
        /// f3 -- the scrolls field -- is 100 in 156 captures and 4,815 appearances, and 101 does not come
        /// up once. And in its own column and not in the base, because the base is the points spent: put
        /// there, a freshly made level 200 had 183 points to spend instead of 995. Characters made before
        /// those columns existed get this figure, whatever the settings say now.
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

        private static readonly (int Gid, int Slot)[] JustKeyring = {
            (Keyring, Managers.Equipment.Bag)
        };

        /// <summary>The dungeon keyring, item 10207.</summary>
        private const int Keyring = DungeonHandler.Keyring;

        /// <summary>What a new character comes out with. For the tests, and to be able to look at it.</summary>
        public static IReadOnlyList<(int Gid, int Slot)> StarterItems => ServerSettings.Current.StartingAdventurerSet
        ? AdventurerSet
        : JustKeyring;

        /// <summary>
        /// The client asks for a random name (kwd) and expects a kvk with one inside.
        /// Without an answer the dice button did nothing.
        ///
        /// The shape is set by rule 1 of the client's NamingRules:
        /// <c>^([A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2})$</c> -- a capital, lower case letters, and up to two
        /// more pieces separated by a hyphen. Here it is done with two.
        /// </summary>
        public static async Task SuggestNameAsync(GameSession session)
        {
            string name = RandomName();
            await session.SendAsync(new CharacterNameSuggestionEvent(name));

            Console.WriteLine($"[Characters] Suggested name: {name}");
        }

        /// <summary>The shared die: several fights and sessions draw from it at once, and a plain
        /// Random used from two threads breaks for good and draws zeros.</summary>
        private static readonly Random _rand = Random.Shared;
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
        public static async Task CreateAsync(GameSession session, CharacterCreationRequest request)
        {
            using var context = SessionContext.Push(session);
            long accountId = session.AccountId;
            int serverId = session.ServerId;
            if (accountId <= 0)
            {
                Console.WriteLine("[Game Node] Character creation without a resolved account: the ticket has not " +
                                  "been presented. Rejected.");
                await SendResultAsync(session, CharacterCreationResult.CreateErrorNoReason);
                return;
            }

            var modeling = request.ModelingInformation;
            int sex = modeling.Gender switch { Gender.Male => 0, Gender.Female => 1, _ => -1 };
            var (id, why) = Make(accountId, serverId, modeling.Name, modeling.BreedId, sex, modeling.Head,
                modeling.Colors.Select(color => (long)color).ToArray());
            if (id == 0)
            {
                await SendResultAsync(session, why switch
                {
                    "full" => CharacterCreationResult.CreateErrorTooManyCharacters,
                    "name" or "taken" => CharacterCreationResult.CreateErrorInvalidName,
                    _ => CharacterCreationResult.CreateErrorNoReason,
                });
                return;
            }

            await SendResultAsync(session, CharacterCreationResult.CreateOk);

            // The whole list again, and WITH THE FRESHLY CREATED ONE FIRST.
            //
            // Both halves are needed. Sending only the kvi left the client with the list from BEFORE
            // the creation. And the client does not choose afterwards: it takes THE FIRST one on the
            // list and sends its selection at once, five milliseconds later in the log -- with the
            // list in id order, the old character went into the world.
            //
            // The list goes the one played last first (ConnectionProtocol.MostRecentFirst) and the
            // new one is dated at its creation, so it heads it, as in «crear personaje - borrar
            // personaje»: the kvi that follows the kvb carries the one just created ahead of the
            // one already there.
            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);

            await CharacterListService.Live.SendAsync(session, characters);

            Console.WriteLine($"[Characters] Created {modeling.Name} (id {id}), breed {modeling.BreedId}, before the " +
                              $"tutorial's statue, {(ServerSettings.Current.StartingAdventurerSet ? "with" : "without")} the " +
                              $"adventurer's set and with {ServerSettings.Current.StartingKamas} kamas.");
        }

        /// <summary>
        /// Creates a character using the character creation screen's rules and returns its ID, or 0
        /// and why not: empty, breed, full, name, taken, failed. Colours of -1 are the breed's.
        /// </summary>
        public static (long Id, string Why) Make(long accountId, int serverId, string name, int breed, int sex,
                                                 int head, IReadOnlyList<long> colors)
        {
            if (string.IsNullOrWhiteSpace(name)) return (0, "empty");
            if (breed < 1 || sex is < 0 or > 1) return (0, "breed");

            // The slots the server list announces: so many per category of server, the client's
            // «Clásico (5/8)». The client opens the creation screen all the same, so the count
            // is kept here.
            int held = SlotsTaken(DatabaseManager.GetServers(),
                                  DatabaseManager.GetCharactersByAccountId(accountId), serverId);
            if (held >= ConnectionService.MaxCharactersPerServer)
            {
                Console.WriteLine($"[Characters] Account {accountId} already has {held} characters in that " +
                                  "category of server; no more are made.");
                return (0, "full");
            }

            if (!CharacterNames.IsValid(name))
            {
                Console.WriteLine($"[Characters] \"{name}\" breaks the naming rule.");
                return (0, "name");
            }

            if (DatabaseManager.CharacterNameTaken(name))
            {
                Console.WriteLine($"[Characters] The name \"{name}\" is already taken.");
                return (0, "taken");
            }

            long id = DatabaseManager.CreateCharacter(accountId, serverId, name, breed, sex, head,
                                                      colors, TutorialMap, StartingLevel,
                                                      ServerSettings.Current.StartingKamas, ServerSettings.Current.StartingScrollStats, StarterItems,
                                                      TutorialCell);
            return id == 0 ? (0, "failed") : (id, "");
        }

        /// <summary>A name for a new character, as the creation screen's die draws one.</summary>
        public static string SuggestName() => RandomName();

        /// <summary>
        /// How many of the account's characters live on servers of the same category as
        /// <paramref name="serverId"/>: the slots are counted per category, not per server.
        /// </summary>
        public static int SlotsTaken(IEnumerable<DatabaseManager.DbServer> servers,
                                     IEnumerable<DbCharacter> characters, int serverId)
        {
            var known = servers.ToList();
            int category = known.FirstOrDefault(s => s.Id == serverId)?.Type ?? 1;
            var sameCategory = known.Where(s => s.Type == category).Select(s => s.Id).ToHashSet();
            sameCategory.Add(serverId);
            return characters.Count(c => sameCategory.Contains(c.ServerId));
        }

        private static Task SendResultAsync(GameSession session, CharacterCreationResult result)
            => session.SendAsync(new CharacterCreationResultEvent(NameCompliance.NameOk, result));
    }
}
