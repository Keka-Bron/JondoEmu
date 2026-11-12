using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// «Informaciones» on a player's menu: two lines the client writes in the chat, the game's date
    /// and who the player is.
    /// </summary>
    /// <remarks>
    /// MEASURED in «Amigos/abrir amigos-agregara mi lista de amigos-apodo cuenta-enviar invitacion»,
    /// frames 59-61, the morning of 10 August 2026:
    /// <code>
    ///   C->S  lqw  { 2 lgf { 4 { 1 the character's name } }, 3 1 }
    ///   S->C  lqq  { 2 656, 3 7, 4 10 }              the game's date: year - 1370, month from 0, day
    ///   S->C  lqe  { 1 nickname, 2 area, 3 1, 4 tag, 6 1, 7 character, 8 server, 9 account,
    ///                10 1, 12 guild, 13 -1, 14 the character's name }
    /// </code>
    /// What each field is, the client's hbr says: lqq's month goes to MonthsDataRoot; lqe's f2 to
    /// AreasDataRoot, f8 and f13 to ServersDataRoot (f13 the server of origin, -1 for none), f3 picks
    /// ui.common.whois when it is not 0 and ui.common.disconnected when it is, f10 is the account's
    /// role (1 a player; moderator, game master and the rest above it), f11 the alliance and f12 the
    /// guild, each made a link. f6 is not read there; it is sent as measured.
    /// </remarks>
    public static class WhoisHandler
    {
        /// <summary>The Dofus year is the real one less this: 2026 is 656.</summary>
        public const int YearOffset = 1370;

        /// <summary>lbb: a player's account, with no role of its own.</summary>
        public const int PlayerRole = 1;

        public static async Task AskAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? lqw = ConnectionProtocol.ReadPayload(payload, "lqw");
            if (lqw == null) return;

            var character = CharacterOf(lqw);
            if (character == null)
            {
                Console.WriteLine("[Whois] Nobody by that name.");
                return;
            }

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push("lqq", BuildDate(DateTime.Now)));
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push("lqe", Build(character)));
        }

        /// <summary>lqq { 2 year, 3 month from 0, 4 day }: the game's calendar of that day.</summary>
        public static byte[] BuildDate(DateTime when)
            => Pb.New().Var(2, when.Year - YearOffset).Var(3, when.Month - 1).Var(4, when.Day).Build();

        /// <summary>lqe: who the character is, where he plays, and his guild and alliance.</summary>
        public static byte[] Build(DbCharacter character)
        {
            var session = SessionRegistry.FindByCharacter(character.Id);
            bool online = session != null && session.IsInWorld;
            var (nickname, tag) = HouseHandler.AccountTagOf(character.AccountId);

            int area = 0;
            if (online)
            {
                int subArea = DatabaseManager.SubAreaOfMap(session!.State.MapId);
                area = subArea > 0 ? Interactives.AreaOfSubArea(subArea) : 0;
            }

            GuildStore.Guild? guild = null;
            AllianceStore.Alliance? alliance = null;
            try { guild = GuildStore.GuildOf(character.Id); } catch (Exception) { /* no guild table */ }
            try { alliance = AllianceStore.AllianceOf(character.Id); } catch (Exception) { /* no alliance table */ }

            var lqe = Pb.New()
                .Str(1, nickname)
                .VarIfNotZero(2, online ? area : 0)
                .VarIfNotZero(3, online ? 1 : 0)
                .Str(4, tag)
                .Var(6, 1)
                .Var(7, character.Id)
                .Var(8, character.ServerId)
                .Var(9, character.AccountId)
                .Var(10, PlayerRole);
            if (alliance != null) lqe.Msg(11, AllianceProtocol.Block(alliance));
            if (guild != null) lqe.Msg(12, GuildProtocol.GuildBlock(guild));
            return lqe.Var(13, -1).Str(14, character.Name).Build();
        }

        /// <summary>The character an lqw's f2 names: by his name, or the one an account has playing.</summary>
        private static DbCharacter? CharacterOf(byte[] lqw)
        {
            var lgf = ProtoMessage.Parse(lqw).Fields.FirstOrDefault(f => f.FieldNumber == 2 && f.WireType == 2);
            if (lgf == null) return null;

            foreach (var field in ProtoMessage.Parse(lgf.BytesValue).Fields)
            {
                if (field.FieldNumber != 4 || field.WireType != 2) continue;
                string name = ProtoMessage.Parse(field.BytesValue).Fields
                    .Where(f => f.FieldNumber == 1 && f.WireType == 2)
                    .Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue)).FirstOrDefault() ?? "";
                if (name.Length == 0) return null;
                var playing = SessionRegistry.FindByName(name);
                long id = playing?.State.CharacterId ?? DatabaseManager.CharacterIdOfName(name);
                return id > 0 ? DatabaseManager.GetCharacterById(id) : null;
            }

            long account = FriendsHandler.AccountOf(lgf.BytesValue);
            var session = account > 0 ? SessionRegistry.All().FirstOrDefault(s => s.AccountId == account && s.IsInWorld) : null;
            return session == null ? null : DatabaseManager.GetCharacterById(session.State.CharacterId);
        }
    }
}
