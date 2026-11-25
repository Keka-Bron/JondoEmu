using System.Collections.Generic;
using System;
using System.Globalization;
using System.Linq;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The frames of the Infinite Dreams, with the shape the captures bring.
    /// </summary>
    /// <remarks>
    /// Two messages carry the weight, and both identify the rooms <b>by string</b> — «0», «1»,
    /// «2» — not by number. That is not a matter of style: sending them as a varint leaves the client
    /// without a map and without a single error.
    /// </remarks>
    public static class DreamProtocol
    {
        /// <summary>
        /// The iyj: the dream's window — header, rooms and graph.
        /// </summary>
        /// <remarks>
        /// The whole run goes INSIDE f17. That is the detail that matters and the one that was wrong:
        /// sending the rooms as repeated f17 of the parent and the graph as sibling f4, the
        /// server answered, the client did not complain and nothing opened.
        ///
        /// <code>
        ///   f1 {  the dream saved to continue -- none, zero bytes, when there is none
        ///     f1   arenas left       f2   dream points        f3   the name
        ///     f4   storms left       f7 (repeated)  the bonuses gained     f8   the level
        ///     f13  the difficulty    f14  1     f15  1     f16  the room it is in, as a string
        ///     f17 {
        ///       f1 (repeated)  a ROOM:    f1 its number as a string
        ///                                 f2 { f1 score, f3 dream points, f4 { the reward },
        ///                                      f5 1, f6 the row, f7 marked }
        ///       f2   the band, from 0, left out for the first
        ///       f3 (repeated)  the rooms behind, as strings
        ///       f4 (repeated)  an EDGE:   f1 the origin, f2 { f1 each destination }
        ///     }
        ///   }
        /// </code>
        ///
        /// The three room shapes, counted over the 89 of the nine captures:
        ///
        ///   the entrance (9 of 9)  f2 { f7: 0 } and nothing else
        ///   the fight ones (71)    f1 4..40, f3 5 or 15, f4 the group, f5 1, f6 the row, f7 0 or 1
        ///   the last (9 of 9)      f2 { f5: 3, f6: 4, f7: 0 }
        ///
        /// And always the five rows: one room, then two to four per row, then one.
        /// </remarks>
        public static byte[] BuildDreamMap(Dreams.Sueno? sueno)
        {
            if (sueno == null) return Array.Empty<byte>();

            var dentro = Pb.New()
                .VarIfNotZero(1, sueno.Arena)
                .VarIfNotZero(2, sueno.DreamPoints)
                .Str(3, sueno.Nombre)
                .VarIfNotZero(4, sueno.Tormentas);
            foreach (var bono in sueno.Ganados) dentro.Msg(7, BonusEntry(bono));
            dentro.Var(8, Dreams.DreamerLevel(sueno))
                  .VarIfNotZero(10, sueno.DreamerLevels)
                  .Var(13, sueno.Dificultad)
                  .Var(14, 1)
                  .Var(15, 1)
                  .Str(16, Texto(sueno.Actual));

            return Pb.New().Msg(1, dentro.Msg(17, Graph(sueno, sueno.Franja))).Build();
        }

        /// <summary>
        /// One band of the dream: its rooms, the rooms behind and its edges.
        /// </summary>
        /// <remarks>
        /// It travels TWICE and with the same shape: as the iyj's f17 —the window offering the
        /// dream— and as the izg's f16 —the state inside—. That it repeats is not an oversight by
        /// Ankama: they are two different moments and the second is the one that feeds the dream's map and
        /// the panels while playing.
        ///
        /// ONE PER BAND, measured in the long capture once its first fountain is reached: two f16,
        /// the first with rooms 0 to 9 and the second with 9 to 24 and an f2 of 1. The fountain
        /// is in both, closing the one and opening the other, and its edges go with the band it
        /// opens. The f3 is not the room one stands in but the rooms behind -- "0" in the first
        /// fight room, "4", "2", "0" in the third -- the same list in every band.
        /// </remarks>
        private static List<Dreams.Sala> RoomsInBand(Dreams.Sueno sueno, int franja)
        {
            var rooms = new List<Dreams.Sala>();
            foreach (var sala in sueno.Salas)
            {
                bool opensThisBand = Dreams.Closes(sala) && sala.Franja == franja - 1;
                if (sala.Franja == franja || opensThisBand) rooms.Add(sala);
            }
            // The fountain that opens the band goes first, as in the capture.
            rooms.Sort((a, b) => a.Fila != b.Fila ? a.Fila.CompareTo(b.Fila) : a.Id.CompareTo(b.Id));
            return rooms;
        }

        private static Pb Graph(Dreams.Sueno sueno, int franja)
        {
            var rooms = RoomsInBand(sueno, franja);
            var graph = Pb.New();
            foreach (var sala in rooms)
            {
                graph.Msg(1, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, RoomBody(sala)));
            }

            graph.VarIfNotZero(2, franja - 1);
            foreach (int visited in sueno.Visited) graph.Str(3, Texto(visited));

            foreach (var sala in rooms)
            {
                // The fountain that closes the band leads into the next one: its edges are there.
                bool closesThisBand = Dreams.Closes(sala) && sala.Franja == franja;
                if (sala.Salidas.Count == 0 || closesThisBand) continue;

                var destinos = Pb.New();
                foreach (int destino in sala.Salidas) destinos.Str(1, Texto(destino));

                graph.Msg(4, Pb.New()
                    .Str(1, Texto(sala.Id))
                    .Msg(2, destinos));
            }

            return graph;
        }

        /// <summary>
        /// What a room is, in the shapes of the captures, byte for byte:
        ///
        ///   the entrance   { f7: 0 }
        ///   a fountain     { f5: 3, f6: the row, f7: 0 }
        ///   a favour       { f5: 2, f6: the row, f7: 0 }     "15", "29", "41" of the invitation
        ///   a fight        { f1: score, f3: dream points, f4 { the group }, f5: 1, f6: the row,
        ///                    f7: marked }
        /// </summary>
        private static Pb RoomBody(Dreams.Sala sala)
        {
            if (sala.Fila == 0) return Pb.New().Var(7, 0);
            if (sala.EsFuente) return Pb.New().Var(5, TipoDeFuente).Var(6, sala.Fila).Var(7, 0);
            if (sala.EsFavor) return Pb.New().Var(5, TipoDeFavor).Var(6, sala.Fila).Var(7, 0);
            // The end of the dream: "64" of the invitation capture, { f1: 32, f5: 4, f6: 26, f7: 1 }.
            if (sala.EsFinal) return Pb.New().Var(1, sala.Score).Var(5, TipoDeFinal).Var(6, sala.Fila).Var(7, 1);

            return Pb.New()
                .Var(1, sala.Score)
                .Var(3, sala.DreamPoints)
                .Msg(4, Group(sala))
                .Var(5, TipoDeCombate)
                .Var(6, sala.Fila)
                .Var(7, sala.Senalada ? 1 : 0);
        }

        /// <summary>A player of a dream's result: who, and how many of its dreams he has won.</summary>
        public sealed record ResultPlayer(long Id, string Name, int Sex, int Breed, int Wins);

        /// <summary>
        /// The end of a dream, won or lost (iya): what the client's result window,
        /// InfiniteDreamResultUi, is made of -- with the dream's code to copy, "à la fin de chaque
        /// songe, que vous perdiez ou que vous gagniez, vous pouvez récupérer un code".
        /// </summary>
        public static InfiniteDreamEndEvent BuildResult(Dreams.Sueno sueno, bool won, IReadOnlyList<ResultPlayer> players,
                                                        int lastWave = 0, int lastWaveTurn = 0)
        {
            var ownerGender = (Gender)(players.FirstOrDefault(p => p.Id == sueno.CharacterId)?.Sex ?? 0);
            return new(Life: sueno.Arena, Intensity: sueno.Dificultad, KilledWaveNumber: lastWave,
                ScaledLevel: Dreams.DreamerLevel(sueno), Seed: sueno.Seed, Maps: SnapshotMaps(sueno),
                Bonuses: sueno.Ganados.Select(SnapshotBonus).ToArray(), Rerolls: sueno.Tormentas,
                DreamPoints: sueno.DreamPoints, BonusLevels: sueno.DreamerLevels, KilledWaveGlobalTurn: lastWaveTurn,
                CumulatedDuration: sueno.FightMs, Owner: new(sueno.Nombre, ownerGender, sueno.CharacterId, sueno.Breed),
                Outcome: won
                    ? new InfiniteDreamEndEventCaseWin(new(players.Select(p => new InfiniteDreamWinPlayer(
                        new(p.Name, (Gender)p.Sex, p.Id, p.Breed), p.Wins)).ToArray()))
                    : new InfiniteDreamEndEventCaseLoss(new(Texto(sueno.Actual), StageOfBand(sueno.Franja))));
        }

        /// <summary>A room's kind, the f5 of its body: see <see cref="TipoDeCombate"/>.</summary>
        private static int KindOf(Dreams.Sala sala)
            => sala.Fila == 0 ? 0 : sala.EsFuente ? TipoDeFuente : sala.EsFavor ? TipoDeFavor : sala.EsFinal ? TipoDeFinal : TipoDeCombate;

        /// <summary>For tests: a room's body as it goes in the graph.</summary>
        internal static byte[] BuildRoom(Dreams.Sala sala) => RoomBody(sala).Build();

        /// <summary>
        /// What the room gives, the door's tooltip: its reward, in the shape of every reward.
        /// A fight room with none -- it cannot happen, the rooms are all given one -- goes empty.
        /// </summary>
        private static Pb Group(Dreams.Sala sala)
            => sala.Reward != null ? RewardEntry(sala.Reward) : Pb.New();

        /// <summary>
        /// A reward, room's or shop's: see <see cref="Dreams.Reward"/>. The zeros are written, the
        /// fields being optional ones, and the rarity is left out when there is none -- all of it
        /// as in the bytes of the captures.
        /// </summary>
        private static Pb RewardEntry(Dreams.Reward reward)
        {
            var entry = Pb.New().Var(1, reward.Storms).Var(2, reward.Sand);
            foreach (var bonus in reward.Bonuses) entry.Msg(3, BonusEntry(bonus));
            return entry.Var(4, reward.Levels)
                        .Var(5, reward.Points)
                        .VarIfNotZero(7, reward.Rarity)
                        .Var(8, reward.Price)
                        .Var(9, reward.Id)
                        .Var(10, reward.Tag)
                        .Var(11, 0);
        }

        /// <summary>For tests: a reward as it goes on the wire.</summary>
        internal static byte[] BuildReward(Dreams.Reward reward) => RewardEntry(reward).Build();

        /// <summary>
        /// A bonus: { f1 { f4: value, f11: effect }, f2: how many }, or with the value inside an f6
        /// for the effects whose text names a spell. The f15 of the izg and the f3 of a room's group.
        /// </summary>
        private static Pb BonusEntry(Dreams.Bono bono)
        {
            var dentro = Pb.New();
            // A dream passive (792) names its spell and grade: f6 { f2: the spell, f3: 1 }, as the
            // invitation capture's two do -- 31735 "Muñecas de Sadida" and 31752 "Revolución".
            if (bono.Efecto == Dreams.PassiveEffect) dentro.Msg(6, Pb.New().Var(2, bono.Valor).Var(3, 1));
            else if (bono.Anidado) dentro.Msg(6, Pb.New().VarIfNotZero(1, bono.Valor));
            else dentro.Var(4, bono.Valor);
            return Pb.New().Msg(1, dentro.Var(11, bono.Efecto)).Var(2, bono.Count);
        }

        /// <summary>
        /// El tipo de sala: el f5. The client's own names for the five, READ in its door tooltip
        /// (InfiniteDreamGateTooltipBuilder.SetupViewWithContent, the switch at 0x182D80E58):
        /// </summary>
        /// <remarks>
        ///   0  ui.infiniteDreams.startRoom        the entrance, which leaves it out
        ///   1  ui.infiniteDreams.fightRoom        244 of the 293 distinct rooms of the captures
        ///   2  ui.infiniteDreams.dreamFavor       6: "15", "29" and "41" of the invitation, twice
        ///   3  ui.infiniteDreams.dreamFountain    24, the last row of a band
        ///   4  ui.infiniteDreams.bossRoom         2: the Fin du rêve, row 26
        ///
        /// The keys are behind the client's string obfuscation: each name is an a::xxx() that
        /// decodes a slice of one byte array, XORed with (i &amp; 0xff) ^ 0xAA by a::.cctor
        /// @0x182008B30 -- the array sits at offset 20795824 of global-metadata.dat.
        /// </remarks>
        private const int TipoDeCombate = 1;
        private const int TipoDeFavor = 2;
        private const int TipoDeFuente = 3;

        /// <summary>The Fin du rêve: type 4, the one room of row 26 in the invitation capture.</summary>
        private const int TipoDeFinal = 4;

        /// <summary>
        /// The izg: the state of the dream in progress.
        /// </summary>
        /// <remarks>
        /// Measured over the 57 izg of the captures, field by field:
        ///
        /// <code>
        ///   f1 { f1 the name, f3 the character's id, f4 its breed }
        ///   f2   the difficulty
        ///   f4 (repeated)  a DOOR:      f1 the room it leads to, as a string
        ///                               f2 the interactive element the client will press
        ///   f7   astral storms left          f8   the bonus to xp and loot, in percent
        ///   f11  the dream points            f12  the band, from 0
        ///   f13  the room one is in, as a string               f14  the dreamer levels gained
        ///   f15 (repeated)  the bonuses gained      f16 (repeated)  one graph per band
        ///   f17  Draconiros arenas left      f18  1: the room's fight is still to be won
        ///   f19  1: the room is clear        f20  the level     f22  the difficulty's bonus
        /// </code>
        ///
        /// f11, f12, f18 and the breed were missing, the f17 went as the f19 and the f19 was
        /// always 1, and the f8 grew with every room won. The dream points, the score and the
        /// bonuses are what the dream's panel shows, and that panel is what did not appear.
        ///
        /// f3 is the bestiary, sent while the room's fight is to be won; f6 the fountain's shop,
        /// sent at a fountain, and a favour's three choices while none is chosen. f14 is 50 in
        /// the invitation capture, whose path goes through a room of "50 niveles de soñador". Not
        /// sent: f5 the party.
        /// </remarks>
        public static InfiniteDreamStateEvent BuildDreamState(Dreams.Sueno sueno, IReadOnlyList<InfiniteDreamMonster>? bestiary = null)
        {
            // The room one stands in: a fight still to win is f18, a clear room is f19 -- the
            // entrance, a room won, a fountain. Measured over the 57: f18 alone on entering a
            // fight room, f19 alone at the entrance and after the win, both at the fountain.
            // A favour not chosen yet is the fight to win of its room: f18 alone, its doors shut
            // -- INFERRED, no capture stands in one -- and a chosen one is clear, f19 alone.
            var room = sueno.SalaActual;
            bool favourPending = room != null && room.EsFavor && !room.FavorChosen;
            bool fightPending = room != null && room.Miembros.Count > 0 && !room.Hecha;
            bool fountain = room != null && room.EsFuente;

            // THE THREE DOORS, not only the ones leading somewhere. In the Pesadilla
            // II capture the entrance room lists all three and the middle one goes with no destination:
            //
            //   f4 { f1: "1", f2: 539509,          f5: 3 }
            //   f4 {          f2: 539510, f4: 1          }   ← this one leads nowhere
            //   f4 { f1: "2", f2: 539511, f4: 2,   f5: 3 }
            //
            // The inner f4 is the door number, and zero is not written. Sending only the
            // ones with a destination, the client does not know which of the three is dead and draws
            // all three the same: you press one and nothing happens, without knowing why.
            var doors = new List<InfiniteDreamDoor>();
            if (room != null)
            {
                for (int cual = 0; cual < Dreams.PuertasPorSala; cual++)
                {
                    int puerta = Dreams.PuertaDe(room, cual);
                    if (puerta == 0) continue;

                    bool lleva = cual < room.Salidas.Count;
                    doors.Add(new(lleva ? Texto(room.Salidas[cual]) : "", puerta, (InfiniteDreamDoorPosition)cual,
                        lleva ? (InfiniteDreamPortalType)PortalOf(sueno.Buscar(room.Salidas[cual])) : InfiniteDreamPortalType.None));
                }
            }

            // The shop, at a fountain: what is left of its offers, after the doors and before the
            // storms, as in the izg of the long capture's room 9. And a favour's three choices,
            // while none is chosen: the client's shop window reads the same list.
            return new(Dreamer: new(sueno.Nombre, default, sueno.CharacterId, sueno.Breed), Intensity: sueno.Dificultad,
                Monsters: fightPending && bestiary != null ? bestiary : [], Doors: doors, Party: [],
                Rewards: (fountain || favourPending) && room?.Offers is { } offers ? offers.Select(SnapshotReward).ToArray() : [],
                Rerolls: sueno.Tormentas, LootBonusPercent: sueno.Bonus, DreamPoints: sueno.DreamPoints,
                Stage: StageOfBand(sueno.Franja), CurrentRoomId: Texto(sueno.Actual), BonusLevels: sueno.DreamerLevels,
                Bonuses: sueno.Ganados.Select(SnapshotBonus).ToArray(),
                Maps: SnapshotMaps(sueno), Life: sueno.Arena,
                CanReroll: fightPending || fountain || favourPending, RoomCleared: !fightPending && !favourPending,
                ScaledLevel: Dreams.DreamerLevel(sueno), Training: false, ExperienceBonusPercent: sueno.BaseBonus);
        }

        private static InfiniteDreamStage StageOfBand(int band) => (InfiniteDreamStage)(band - 1);

        private static InfiniteDreamMap[] SnapshotMaps(Dreams.Sueno dream)
            => Enumerable.Range(1, dream.Franja).Select(band => SnapshotMap(dream, band)).ToArray();

        private static InfiniteDreamMap SnapshotMap(Dreams.Sueno dream, int band)
        {
            var rooms = RoomsInBand(dream, band);
            return new(rooms.ToDictionary(r => Texto(r.Id), SnapshotRoom), StageOfBand(band),
                dream.Visited.Select(Texto).ToArray(),
                rooms.Where(r => r.Salidas.Count != 0 && !(Dreams.Closes(r) && r.Franja == band))
                     .ToDictionary(r => Texto(r.Id), r => new InfiniteDreamRoomLinks(r.Salidas.Select(Texto).ToArray())));
        }

        private static InfiniteDreamRoom SnapshotRoom(Dreams.Sala room)
        {
            var kind = (InfiniteDreamRoomType)KindOf(room);
            if (kind == InfiniteDreamRoomType.Boss)
                return new(room.Score, 0, null, kind, room.Fila, InfiniteDreamRoomDifficulty.Hard);
            if (kind != InfiniteDreamRoomType.Fight)
                return new(0, 0, null, kind, room.Fila, InfiniteDreamRoomDifficulty.Normal);

            // The fight-room tooltip has a reward message even when the room has no reward data.
            InfiniteDreamReward reward = room.Reward is { } offer ? SnapshotReward(offer)
                : new(null, null, [], null, null, default, null, 0, 0, null);
            return new(room.Score, room.DreamPoints, reward, kind, room.Fila,
                room.Senalada ? InfiniteDreamRoomDifficulty.Hard : InfiniteDreamRoomDifficulty.Normal);
        }

        private static InfiniteDreamReward SnapshotReward(Dreams.Reward reward) => new(
            reward.Storms, reward.Sand, reward.Bonuses.Select(SnapshotBonus).ToArray(), reward.Levels, reward.Points,
            (InfiniteDreamRewardRarity)reward.Rarity, reward.Price, IconId: reward.Id, RewardId: reward.Tag, Unavailable: false);

        private static InfiniteDreamBonus SnapshotBonus(Dreams.Bono bonus)
        {
            ObjectEffectCase value = bonus.Efecto == Dreams.PassiveEffect ? new ObjectEffectCaseValueDice(new(0, bonus.Valor, 1))
                : bonus.Anidado ? new ObjectEffectCaseValueDice(new(bonus.Valor, 0, 0)) : new ObjectEffectCaseValueInt(bonus.Valor);
            return new(new(bonus.Efecto, value), bonus.Count);
        }

        /// <summary>
        /// El f5 de una puerta que lleva a algún sitio: the look of its portal, by what is behind it.
        /// </summary>
        /// <remarks>
        /// READ in the client. Its dream frame (eft::xhh @0x181664B30, called for every izg) turns
        /// each door into the id of a staging sequence of the room's map -- 10 x (the door's f4 +
        /// 1) + this f5, or 69 for a 6 -- and plays it. The maps of subarea 904 name their fifteen
        /// sequences (mapdata_assets_world_907.bundle, stagingSequences):
        ///
        ///   11 A_shop   12 A_bonus   13 A_combatFacile   14 A_combatDifficile   15 A_boss
        ///   21 B_...    31 C_...                          and 69 SORTIE on the one-door maps
        ///
        /// So 1 is a fountain, 2 a favour -- "bonus", the guide's green portal --, 3 a fight,
        /// 4 a difficult one, 5 the Fin du rêve, 6 a way out. And the captures agree where they
        /// can: in their izg a door to a fight room is 3 all 96 times, to a marked one 4 all 4
        /// times, to a fountain 1 all 5 times. This used to be 3 for every door, a fountain's
        /// included.
        /// </remarks>
        internal static int PortalOf(Dreams.Sala? destination)
            => destination == null ? PortalFight
             : destination.EsFuente ? PortalFountain
             : destination.EsFavor ? PortalFavor
             : destination.EsFinal ? PortalEnd
             : destination.Senalada ? PortalHardFight
             : PortalFight;

        internal const int PortalFountain = 1;
        internal const int PortalFavor = 2;
        internal const int PortalFight = 3;
        internal const int PortalHardFight = 4;
        internal const int PortalEnd = 5;

        /// <summary>
        /// izo: the loot table of the room, one f2 per item. Measured on the 61 lines of the
        /// capture's: { f1: the criterion, left out when there is none, f2: the item, f3: how
        /// many, f5: the percent as a float }.
        /// </summary>
        public static byte[] BuildDropTable(IEnumerable<(string Criterion, int Item, int Quantity, double Percent)> drops)
        {
            var izo = Pb.New();
            foreach (var (criterion, item, quantity, percent) in drops)
                izo.Msg(2, Pb.New()
                    .StrIfNotEmpty(1, criterion)
                    .Var(2, item)
                    .Var(3, quantity)
                    .Fixed32(5, BitConverter.GetBytes((float)percent)));
            return izo.Build();
        }

        /// <summary>The izj accompanying the astral storm: «1001» from the capture.</summary>
        public static byte[] BuildStorm() => Pb.New().Var(2, 1).Build();

        /// <summary>The exit's iyb: «0801» from the capture.</summary>
        public static byte[] BuildLeft() => Pb.New().Var(1, 1).Build();

        /// <summary>
        /// Room numbers travel as a STRING, and that is why they go through here.
        /// </summary>
        /// <remarks>
        /// With the invariant culture on purpose: with a culture that uses another separator, a
        /// room number would come out written differently and the client would not match it with its graph.
        /// </remarks>
        private static string Texto(int n) => n.ToString(CultureInfo.InvariantCulture);

    }
}
