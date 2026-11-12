using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// A player's card («Mostrar perfil», the client's PlayerInfoUi): kxh { 2 character } answered
    /// lad { 2 { 3 last updated, 4 the character } }.
    /// </summary>
    /// <remarks>
    /// MEASURED in «Busqueda grupo/busqueda de grupo-crear anuncio-postularse a otro…», four times;
    /// the character block's fields read off the client's PlayerInfoBase constructor, which takes
    /// it apart:
    /// <code>
    ///   1 ornament (the one of title, ornament and aura it carries: case 1, 2 or 4)
    ///   2 account   3 breed   4 character   6 online   7 level   8 the bare look
    ///   9 the card's background   10 gender, 0 male   11 name   14 the look with its outfit
    /// </code>
    /// The capture's own card: { 1 18, 2 60000001, 3 11, 4 300000000001, 6 1, 7 354, 8 look, 9 5,
    /// 11 "Hero-Example", 14 look }. The background (f9) is the one the player picks in the card's
    /// background window, kye { 2 background } (fhf::beyt copies dkv's only field there); with none
    /// picked it is left out and the client draws its default.
    /// </remarks>
    public static class PlayerCardHandler
    {
        /// <summary>kxh: the card of a character of this server, online or not.</summary>
        public static async Task ShowAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? kxh = ConnectionProtocol.ReadPayload(payload, "kxh");
            if (kxh == null) return;
            long id = ProtoMessage.Parse(kxh).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            var character = id > 0 ? DatabaseManager.GetCharacterById(id) : null;
            if (character == null)
            {
                Console.WriteLine($"[Player card] No character {id}.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push("lad", Build(character)));
        }

        /// <summary>The highest background number taken; the client's window offers a handful.</summary>
        public const int MaxBackground = 999;

        /// <summary>kye { 2 background }: the player's own card gets that background. Nothing is answered.</summary>
        public static Task SetBackgroundAsync(byte[] payload)
        {
            byte[]? kye = ConnectionProtocol.ReadPayload(payload, "kye");
            if (kye == null) return Task.CompletedTask;
            long background = ProtoMessage.Parse(kye).Fields
                .FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 0)?.VarIntValue ?? 0;
            if (background < 0 || background > MaxBackground) return Task.CompletedTask;

            var state = SessionContext.State;
            Wardrobe.SaveCardBackground(state.CharacterId, (int)background);
            Console.WriteLine($"[Player card] {state.CharacterName} takes background {background}.");
            return Task.CompletedTask;
        }

        /// <summary>lad { 2 { 3 when, 4 the character } }.</summary>
        internal static byte[] Build(DbCharacter character)
        {
            var (_, ornament) = Wardrobe.Of(character.Id);
            var card = Pb.New()
                .VarIfNotZero(1, ornament)
                .Var(2, character.AccountId)
                .Var(3, character.Breed)
                .Var(4, character.Id)
                .VarIfNotZero(6, SessionRegistry.FindByCharacter(character.Id) != null ? 1 : 0)
                .Var(7, character.Level)
                .Bytes(8, CharacterLookBuilder.BareLook(character))
                .VarIfNotZero(9, Wardrobe.CardBackgroundOf(character.Id))
                .VarIfNotZero(10, character.Sex)
                .Str(11, character.Name)
                .Bytes(14, CharacterLookBuilder.BuildLook(character, paraLaVentana: true));
            return Pb.New().Msg(2, Pb.New().Str(3, CharacterPresets.Now()).Msg(4, card)).Build();
        }
    }
}
