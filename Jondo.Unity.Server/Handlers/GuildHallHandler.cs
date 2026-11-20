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
    /// The guild halls (see <see cref="GuildHalls"/>): choosing one from inside it, giving it a look,
    /// the banners each visitor sees, and the potion that takes a member there.
    /// </summary>
    /// <remarks>
    /// Measured in «en boton de gestion-elegir este recibidor para mi gremio»: the jjz, then the room
    /// again (jru), jjv { f2 the hall } -- the client's «El recibidor de tu gremio ahora es el {0}» --,
    /// the room's jss with the guild's hall in its f4 and the theme's staging. And in «entrar en
    /// recibidor de gremio en amakna», before: every room's jss with f4 f3 and «hall neutre» (10).
    /// </remarks>
    public static class GuildHallHandler
    {
        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        /// <summary>
        /// «Elegir este recibidor para mi gremio» (jjz, empty; MapInfoUI.SelectGuildHall): the hall the
        /// character stands in becomes his guild's. Right 41. He sees the room again as his guild's,
        /// and every member on line is told (jjv).
        /// </summary>
        public static async Task ChooseHallAsync(NetworkStream stream, byte[] frame)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            var hall = GuildHalls.OfRoom(SessionContext.State.MapId);
            if (guild == null || hall == null) return;
            if (!GuildStore.HasRight(who, GuildHalls.ManageRight))
            {
                Console.WriteLine($"[Guild halls] {SessionContext.State.CharacterName} may not choose the guild's hall.");
                return;
            }

            GuildHalls.Choose(guild.Id, hall.Id, DateTimeOffset.UtcNow);
            Console.WriteLine($"[Guild halls] «{guild.Name}» chooses the {hall.Name} ({hall.Id}).");

            await TeleportHandler.ToMapAsync(stream, SessionContext.State.MapId, SessionContext.State.CellId);
            await GuildHandler.TellEveryoneAsync(guild, ConnectionProtocol.Push(Op.Jjv, GuildProtocol.BuildHallChanged(hall.Id)));
        }

        /// <summary>
        /// A look chosen for the guild's hall (jjw { f1 the theme }; MapInfoUI.SelectTheme). Right 41,
        /// and a look the guild's level has opened. Whoever of the guild stands in the hall sees it
        /// (jqf, and the theme's staging).
        /// </summary>
        public static async Task ChooseThemeAsync(NetworkStream stream, byte[] frame)
        {
            byte[] jjw = ConnectionProtocol.ReadPayload(frame, Op.Jjw);
            long who = SessionContext.State.CharacterId;
            var guild = jjw == null || who == 0 ? null : GuildStore.GuildOf(who);
            if (guild == null || !GuildStore.HasRight(who, GuildHalls.ManageRight)) return;

            int theme = 0;
            foreach (var field in ProtoMessage.Parse(jjw).Fields)
                if (field.FieldNumber == 1 && field.WireType == 0) theme = (int)field.VarIntValue;
            if (!GuildHalls.ThemesAtLevel(guild.Level).Contains(theme)) return;

            var choice = GuildHalls.SetTheme(guild.Id, theme);
            if (choice == null) return;
            Console.WriteLine($"[Guild halls] «{guild.Name}» gives its hall theme {theme}.");

            var hall = GuildHalls.ById(choice.HallId);
            byte[] told = ConnectionProtocol.Push(Op.Jqf, GuildProtocol.BuildHallTheme(theme));
            byte[] staged = ConnectionProtocol.Push(Op.Hpm, GuildRaidStagings.BuildPlay(GuildHalls.StagingOf(theme), GuildRaidStagings.FromStart));
            foreach (var member in GuildStore.Members(guild.Id))
            {
                var session = SessionRegistry.FindByCharacter(member.CharacterId);
                if (session == null || hall == null || GuildHalls.OfRoom(session.State.MapId) != hall) continue;
                await session.SendAsync(told);
                await session.SendAsync(staged);
            }
        }

        /// <summary>A hall room loaded: its banners as this visitor is to see them (hpm), at their end.</summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            var visit = GuildHalls.Visit(session.MapId, session.CharacterId);
            if (visit == null) return;
            int staging = visit.Value.Own ? GuildHalls.StagingOf(visit.Value.Theme) : GuildHalls.NeutralStaging;
            await session.SendAsync(GuildRaidStagings.StandingEvent(staging));
        }

        /// <summary>
        /// The guild hall potion (effect 4054): into the Function Room of one's guild's hall, by its
        /// street door's way in. Refused without a hall.
        /// </summary>
        internal static async Task<bool> GoToOwnHallAsync(NetworkStream stream)
        {
            long who = SessionContext.State.CharacterId;
            var guild = who == 0 ? null : GuildStore.GuildOf(who);
            var choice = guild == null ? null : GuildHalls.ChoiceOf(guild.Id);
            var hall = choice == null ? null : GuildHalls.ById(choice.HallId);
            if (hall == null) return false;
            int cell = hall.EntranceCellId >= 0 ? hall.EntranceCellId : TeleportHandler.MapCentre;
            return await TeleportHandler.ToMapAsync(stream, hall.EntranceMapId, cell) >= 0;
        }
    }
}
