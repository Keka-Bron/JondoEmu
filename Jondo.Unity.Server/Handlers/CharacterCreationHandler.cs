using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Handlers
{
    /// <summary>
    /// Crear un personaje.
    ///
    /// De la captura de una creación que sale bien, en un servidor vacío:
    ///
    ///   cliente  kvz { f1 { f1: nombre, f2: cara, f3: colores, f5: 26, f7: raza } }
    ///   servidor kvb            VACÍO, que es como se dice que sí
    ///   servidor kvi            la lista otra vez, ya con el personaje dentro
    ///   cliente  kvl            "juego con ése"
    ///
    /// Y de la que sale mal, la del límite de personajes: <c>kvb { f2: 3 }</c>. O sea que el mismo
    /// mensaje sirve para las dos cosas y lo que distingue es que lleve motivo o no.
    ///
    /// Los colores llegan como varints con signo, y el -1 significa "el que traiga la raza". El
    /// cliente los manda todos a -1 cuando no se toca la paleta.
    /// </summary>
    public static class CharacterCreationHandler
    {
        /// <summary>
        /// Fresh characters begin in Incarnam before the tutorial, not at an Astrub zaap.
        /// Tutorial rewards must be earned through the tutorial flow; creation never grants them.
        /// </summary>
        public const long StartingMap = DatabaseManager.StartingMap;
        public const int StartingCell = DatabaseManager.StartingCell;

        /// <summary>Creation baseline observed by the client: level one, no money and no scrolls.</summary>
        public const int StartingLevel = 1;
        public const long StartingKamas = 0L;
        public const int StartingStat = 0;

        /// <summary>
        /// El cliente pide un nombre al azar (kvk) y espera el mismo mensaje de vuelta con uno
        /// dentro. Sin respuesta el botón del dado no hacía nada.
        ///
        /// La forma la manda la regla 1 de NamingRules del cliente:
        /// <c>^([A-Z][a-z]+(\-[a-zA-Z][a-z]*){0,2})$</c> — mayúscula, minúsculas, y hasta dos
        /// trozos más separados por guión. Aquí se hace con dos.
        /// </summary>
        public static async Task SuggestNameAsync(NetworkStream stream)
        {
            string name = RandomName();
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharacterNameSuggestionSuccessMessage, Pb.New().Str(1, name).Build()));

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

        /// <summary>El cliente ha pulsado JUGAR en la pantalla de creación.</summary>
        public static async Task CreateAsync(NetworkStream stream, byte[] payload, long accountId,
                                             int serverId)
        {
            byte[]? kvz = ConnectionProtocol.ReadPayload(payload, Op.CharacterCreationRequestMessage);
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

            // kvz can be forged independently of the preceding kwb.  Keep the capacity check
            // here as well, so a client cannot create characters for an unbound session or slip
            // past the number of slots announced at login.
            if (!HasAvailableCharacterSlot(accountId, serverId))
            {
                await RefuseAsync(stream, accountId > 0 && serverId > 0
                    ? CharacterLimitReached
                    : CreationRefused);
                return;
            }

            if (DatabaseManager.CharacterNameTaken(name))
            {
                Console.WriteLine($"[Personajes] El nombre \"{name}\" ya está cogido.");
                await RefuseAsync(stream, NameAlreadyTaken);
                return;
            }

            long id = DatabaseManager.CreateCharacter(accountId, serverId, name, breed, sex, head,
                                                      colors, StartingMap, StartingCell, StartingLevel,
                                                      StartingKamas, StartingStat);
            if (id == 0)
            {
                await RefuseAsync(stream, CreationRefused);
                return;
            }

            // Que sí: el kvb va vacío. Con motivo dentro es que no.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharacterCreationResultMessage));

            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListMessage, ConnectionProtocol.BuildCharactersList(characters)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListEndMessage));

            Console.WriteLine($"[Personajes] Creado {name} (id {id}), raza {breed}, en Incarnam " +
                              "sin kamas, equipo ni progreso de cuenta. " +
                              $"La lista ya contiene {characters.Count} personaje(s).");
        }

        /// <summary>
        /// The client sends empty kwb from the character-selection UI before opening an extra
        /// character slot.  It appears after every completed kvi/kvd list in the captured
        /// traffic.  A second empty kvd is the continuation the UI waits for; without it, the
        /// first character can be created but the Add Character control stays pending thereafter.
        /// </summary>
        public static async Task ConfirmCanCreateAsync(NetworkStream stream, long accountId, int serverId)
        {
            if (!HasAvailableCharacterSlot(accountId, serverId))
            {
                if (accountId > 0 && serverId > 0)
                    await RefuseAsync(stream, CharacterLimitReached);
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListEndMessage));
            Console.WriteLine($"[Personajes] Hueco adicional confirmado para la cuenta {accountId} " +
                              $"en el servidor {serverId}.");
        }

        /// <summary>Pure capacity rule, kept separate so the protocol guard can test its edges.</summary>
        internal static bool HasAvailableCharacterSlot(int characterCount)
            => characterCount >= 0 && characterCount < ConnectionProtocol.MaxCharactersPerServer;

        private static bool HasAvailableCharacterSlot(long accountId, int serverId)
        {
            if (accountId <= 0 || serverId <= 0) return false;
            return HasAvailableCharacterSlot(DatabaseManager.GetCharactersByAccountId(accountId, serverId).Count);
        }

        /// <summary>Los motivos que lleva el kvb cuando dice que no.</summary>
        private const int CreationRefused = 1;
        private const int NameAlreadyTaken = 2;
        private const int CharacterLimitReached = 3;

        private static async Task RefuseAsync(NetworkStream stream, int reason)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharacterCreationResultMessage, Pb.New().Var(2, reason).Build()));
        }

        /// <summary>Varints seguidos, que es como viajan los colores. El -1 es "el de la raza".</summary>
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
