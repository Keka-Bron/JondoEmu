using System;
using System.Globalization;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The conquest window's frames (AllianceAvaUi): the King of the Hill's score every minute
    /// (mep) and its end (mdz). No conquest is captured: all is read off the client's code.
    /// </summary>
    /// <remarks>
    /// fgv.bewm reads the mep: it needs the player's alliance, and it sorts each line by the
    /// alliance's uuid -- the prism's owner is «en defensa», one's own «mi alianza», the rest «en
    /// ataque» --. kp.nvv, the line, takes:
    /// <code>
    ///   men { f1 the alliance, f2 its roles [mei { f1 how many, f2 the role }],
    ///         f3 its minutes of domination, f5 its points [mel { f1 points, f4 the kind }],
    ///         f6 its members in the zone }
    /// </code>
    /// The total is the sum of the f5's points, each kind its column (AllianceAvaUi.ScoreType: maps,
    /// fights, prism). The men's f4 and the mel's f2 and f3 are read by nobody the window draws.
    /// The mep's f3, when the conquest began, is a date the client parses itself (gji.bkla,
    /// DateTime.Parse in the invariant culture, round trip) for the clock of how long it lasts.
    /// </remarks>
    public static class KothProtocol
    {
        internal static SelfAttackableStatusUpdateEvent SelfStatus(int status, int role = -1)
            => new(0, status == AllianceProtocol.AvaAggressable && role != -1 ? role : 0, MapActorMessages.AvaStatus(status));

        internal static KothUpdateEvent Update(Conquests.War war, Func<long, AllianceStore.Alliance?> allianceOf, int defaultRole)
        {
            var alliances = new List<KothAllianceInformation>();
            foreach (var side in war.Sides)
            {
                var alliance = allianceOf(side.AllianceId);
                if (alliance == null) continue;
                var roles = side.Roles.OrderBy(role => role.Key)
                    .Select(role => new KothAllianceRoleNumber(role.Value, role.Key)).ToList();
                long counted = roles.Sum(role => role.MemberCount);
                if (side.Present > counted && defaultRole > 0 && !side.Roles.ContainsKey(defaultRole))
                    roles.Add(new(side.Present - counted, defaultRole));
                // Preserve the scoreboard's historical column order and wire kinds.
                KothScore[] scores =
                [
                    new(checked((int)side.Scores[Conquests.ScoreOfMaps]), KothScoreType.Fight),
                    new(checked((int)side.Scores[Conquests.ScoreOfFights]), KothScoreType.Domination),
                    new(checked((int)side.Scores[Conquests.ScoreOfPrism]), KothScoreType.Prism),
                ];
                alliances.Add(new(MapActorMessages.Alliance(alliance), roles.ToArray(), side.Domination, scores, side.Present));
            }
            return new(alliances.ToArray(), war.NextTickUtcMs, DateTimeOffset.FromUnixTimeMilliseconds(war.StartedUtcMs)
                .UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        }

        /// <summary>The score (mep): { f1 each alliance, f2 when the next minute is counted, f3 when it began }.</summary>
        public static byte[] BuildUpdate(Conquests.War war, Func<long, AllianceStore.Alliance> allianceOf, int defaultRole)
        {
            var mep = Pb.New();
            foreach (var side in war.Sides)
            {
                var alliance = allianceOf(side.AllianceId);
                if (alliance != null) mep.Msg(1, Line(side, alliance, defaultRole));
            }
            return mep.Var(2, war.NextTickUtcMs)
                      .Str(3, DateTimeOffset.FromUnixTimeMilliseconds(war.StartedUtcMs).UtcDateTime
                                            .ToString("o", CultureInfo.InvariantCulture))
                      .Build();
        }

        /// <summary>An alliance's line (men). Members whose role is not known go as the default role.</summary>
        public static Pb Line(Conquests.Side side, AllianceStore.Alliance alliance, int defaultRole)
        {
            var men = Pb.New().Msg(1, AllianceProtocol.Block(alliance));
            int counted = 0;
            foreach (var role in side.Roles.OrderBy(r => r.Key))
            {
                men.Msg(2, Pb.New().Var(1, role.Value).Var(2, role.Key));
                counted += role.Value;
            }
            if (side.Present > counted && defaultRole > 0 && !side.Roles.ContainsKey(defaultRole))
                men.Msg(2, Pb.New().Var(1, side.Present - counted).Var(2, defaultRole));
            men.VarIfNotZero(3, side.Domination);
            for (int kind = 0; kind < Conquests.ScoreKinds; kind++)
                men.Msg(5, Pb.New().VarIfNotZero(1, side.Scores[kind]).VarIfNotZero(4, kind));
            return men.VarIfNotZero(6, side.Present);
        }

        /// <summary>The life states (kup.f1, the client's dus): alive, a tomb, a phantom.</summary>
        public const int Alive = 0;
        public const int Tomb = 1;
        public const int Phantom = 2;

        /// <summary>
        /// A character's life state (kup): { f1 the state, f2 a map }. ees.wup keeps the state and
        /// tells the death popup (dyx.uhp): a tomb gets «Has muerto» with «Observar» and
        /// «Resucitar», the second answered with a kug; the f2 goes with the phoenix marker, read
        /// here as where a freed soul goes back to.
        /// </summary>
        public static byte[] BuildLifeState(int state, long mapId = 0)
            => Pb.New().VarIfNotZero(1, state).VarIfNotZero(2, mapId).Build();

        /// <summary>The conquest over (mdz): { f1 the winner }, «KohEnd» to the window.</summary>
        public static byte[] BuildEnd(AllianceStore.Alliance winner)
            => Pb.New().Msg(1, AllianceProtocol.Block(winner)).Build();
    }
}
