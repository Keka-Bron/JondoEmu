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
        /// <summary>Donde empieza todo el mundo: el zaap de la ciudad de Astrub.</summary>
        public const long StartingMap = 191105026L;

        /// <summary>Con lo que empieza: nivel, kamas y las características de los pergaminos.</summary>
        public const int StartingLevel = 1;
        public const long StartingKamas = 1_000_000L;
        public const int ScrolledStat = 101;

        /// <summary>
        /// El conjunto del aventurero, que es el número 5 del juego: capa, sombrero, anillo, botas,
        /// cinturón y amuleto. Van puestos, no en la bolsa.
        /// </summary>
        private static readonly (int Gid, int Slot)[] AdventurerSet =
        {
            (2478, 0),    // amuleto
            (2475, 2),    // anillo
            (2477, 3),    // cinturón
            (2476, 4),    // botas
            (2474, 6),    // sombrero
            (2473, 7),    // capa
        };

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

            // Que sí: el kvb va vacío. Con motivo dentro es que no.
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharacterCreationResultMessage));

            var characters = DatabaseManager.GetCharactersByAccountId(accountId, serverId);
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.CharactersListMessage, ConnectionProtocol.BuildCharactersList(characters)));

            Console.WriteLine($"[Personajes] Creado {name} (id {id}), raza {breed}, en el zaap de " +
                              $"Astrub, con el conjunto del aventurero y {StartingKamas} kamas.");
        }

        /// <summary>Los motivos que lleva el kvb cuando dice que no. El 3 es el del límite.</summary>
        private const int CreationRefused = 1;
        private const int NameAlreadyTaken = 2;

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
