using System;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// What happens to a player who loses a fight against monsters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on the four defeats against monsters of the captures -- the numbers are in
    /// <see cref="DefeatPenalty"/> -- whose end all runs the same way. "ruta muy larga zonas
    /// pandala-sidimote", frames 8824-8844:
    /// </para>
    /// <code>
    ///   kuf, jyg, jxo                          the end screen, as for any fight
    ///   kub                                    energy 10000 -> 8000, 97 (life missing) -6192 -> -3096
    ///   lqn { f2: 34, f4: "2000" }             "Has perdido 2000 puntos de energía."
    ///   kml, kmp, ktz 5                        back to roleplay, regeneration starting
    ///   jru 73400320, lqu, kub                 the map of his saved zaap
    ///   jss                                    and him on cell 299, beside its zaap
    /// </code>
    /// <para>
    /// Where he comes back is where the zaap window says his position is saved: 73400320, the
    /// Pueblo de los ganaderos zaap, is the hjj's f2 in the eight zaap captures wherever they are
    /// opened from, and it is where the defeats in Tainela, in the Foggernaut temple, on the
    /// Peldaños magmáticos and against the collector all send him -- jss frames 303, 2516, 8844
    /// and 6359 -- on cell 299, the step of (0, -1) off the zaap's 286, the same side the zaap itself
    /// lands its travellers on (272 off 259 in the Castillo de Amakna, 286 off 272, and 315 off
    /// Bonta's 300, where (0, -1) is not floor). The client's help says the same: "es
    /// teletransportado a su última posición guardada" (Translations 1183174).
    /// </para>
    /// <para>
    /// INFERRED: which save point. How a position is saved is in no capture -- the zaaps of
    /// 3.6.10 offer skill 114 only, never 44 "Guardar la posición" -- so this server has no saved
    /// position to keep, and every character's is the zaap it starts next to,
    /// <see cref="CharacterCreationHandler.StartingMap"/>. The order of the zaap's neighbours
    /// past the first two is inferred too.
    /// </para>
    /// <para>
    /// NOT DONE: the anomaly's defeat, which sends the loser back to the anomaly's vestige
    /// (54162249 in its capture) and not to his save point; the collector's, which is a fight of
    /// its own kind; the ghost, the tomb and the phoenix, which the energy never reaches.
    /// </para>
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>What a lost fight took from one player, for the frames that tell him.</summary>
        internal sealed record DefeatOutcome(int EnergyLost, int EnergyLeft, int MissingLife,
                                             long SavePointMap, int SavePointCell);

        /// <summary>
        /// Whether losing this fight costs anything: the rulebook says, and a dream never does --
        /// "entrar a sueño nuevo nivel Sueño III-pelear-morir" loses its room's fight with the
        /// energy at 10000 and the life full, back in the dream (DreamHandler decides where).
        /// </summary>
        internal static bool DefeatCostsIn(FightInstance fight)
            => fight != null && fight.Reglas.DefeatCosts && !Dreams.IsDreamMap(fight.RoleplayMapId);

        /// <summary>
        /// The defeat, applied to the character of the session being served: the energy spent
        /// and stored, half his life missing from now on, and where he goes back to.
        /// </summary>
        internal static DefeatOutcome ApplyDefeat()
        {
            long character = GameState.CharacterId;
            int energy = Energy.Of(character);
            int lost = DefeatPenalty.EnergyLost(GameState.CharacterLevel, energy);
            Energy.Set(character, energy - lost);

            int missing = DefeatPenalty.MissingLifeAfter(StatsHandler.GetPlayerMaxHp());
            RestingLife.Set(character, missing, DateTime.UtcNow);

            var (map, cell) = SavePointOf(character);
            Program.LogDebug($"[Fight] {character} lost: -{lost} energy ({energy - lost} left), " +
                             $"{missing} life missing, back to map {map} cell {cell}.");
            return new DefeatOutcome(lost, energy - lost, missing, map, cell);
        }

        /// <summary>
        /// Where a character's position is saved: the zaap every character starts beside. See
        /// the remarks above for why it is the same for everybody.
        /// </summary>
        internal static (long Map, int Cell) SavePointOf(long characterId)
        {
            long map = CharacterCreationHandler.StartingMap;
            return (map, ZaapArrivalCell(map));
        }

        /// <summary>
        /// The cell beside a map's zaap one lands on: the first walkable of its neighbours in the
        /// order (0, -1), (1, 0), (-1, 0), (0, 1). The first two are measured -- four zaap
        /// arrivals and four defeats on (0, -1), the two arrivals at Bonta on (1, 0), where
        /// (0, -1) is not floor --, the last two are the order left.
        /// </summary>
        internal static int ZaapArrivalCell(long mapId)
        {
            var zaaps = Interactives.ZaapElements(mapId);
            if (zaaps.Count == 0) return MapManager.GetNearestWalkableCell(mapId, TeleportHandler.MapCentre);

            int zaap = zaaps[0].Cell;
            var (x, y) = MapGeometry.CellToPoint(zaap);
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (-1, 0), (0, 1) })
            {
                int cell = MapGeometry.PointToCell(x + dx, y + dy);
                if (cell >= 0 && MapManager.IsCellWalkable(mapId, cell)) return cell;
            }
            return MapManager.GetNearestWalkableCell(mapId, zaap);
        }
    }
}
