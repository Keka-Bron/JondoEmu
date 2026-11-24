using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Handlers
{
    public static partial class FightHandler
    {
        /// <summary>
        /// A fighter marks a cell for his side (kmh { 2 cell }), and his side sees it marked by him:
        /// kmj { 1 fighter, 2 cell }.
        /// </summary>
        /// <remarks>
        /// MEASURED in «Combate/combate contra poutch nivel 75…-marcadores permanentes»: kmh 10 a0 02,
        /// cell 288, answered kmj { 300000000001, 288 }. The client's fight frame (fml) sends it from
        /// five places, each with the cell. Only his own side gets it, as the side's own messages go
        /// (ASuBandoAsync); out of a fight there is nothing to mark.
        /// </remarks>
        internal static Task MarkCellAsync(GameSession session, ShowCellRequest request)
            => InParticipantTurnAsync(session, fight => fight.State is FightState.Placement or FightState.Ongoing
                ? ASuBandoAsync(fight, fight.EquipoDe(session.CharacterId), new ShowCellEvent(session.CharacterId, request.CellId))
                : Task.CompletedTask);
    }
}
