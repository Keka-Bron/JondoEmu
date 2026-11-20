using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The treasure hunts' messages, as the 3.6.10.11 client reads and writes them (epx, epy).
    /// </summary>
    /// <remarks>
    /// <code>
    ///   hhk  answer to a hunt asked for    f1 type, f2 result (5 the silent one)
    ///   hhf  the hunt                       f1 stage, f4 stages, f5 flags {f1 state, f2 map},
    ///                                       f6 tries left, f7 the stage's start, f8 steps a stage,
    ///                                       f9 type, f10 the known steps
    ///   hhq  answer to a flag               f1 result, f2 step, f3 type
    ///   hht  answer to a dig                f1 result, f3 wrong flags, f4 type
    ///   hgm  the hunt over                  f2 type
    ///   hhv  a flag put           (client)  f1 type, f2 step
    ///   hhh  a flag taken away    (client)  f1 step, f2 type
    ///   hgj  a dig                (client)  f1 type
    ///   hgn  giving up            (client)  f1 type
    ///   hhg  the planning table's hunts     f1 their ids (LegendaryHuntsUi.Open)
    ///   hhn  a legendary hunt     (client)  f2 its id (epx, on the window's «start»)
    /// </code>
    /// The capture's hunt: «08021005» (hhk) and «20053004389386802a4005480252070a0508f2061002» (hhf: no
    /// stage yet, five stages, four tries, [2,4], five steps, legendary, a POI step: 882 to the south).
    /// </remarks>
    public static class TreasureHuntProtocol
    {
        public static byte[] BuildRequestAnswer(int type, int result)
            => Pb.New().VarIfNotZero(1, type).VarIfNotZero(2, result).Build();

        /// <summary>
        /// The hunt as its owner sees it: the stage's known steps, a landmark one as f1 { f1 the
        /// landmark, f2 the direction } -- the capture's «0a05 08f206 1002», 882 to the south --,
        /// a «follow the direction» one as f3 (hgx).
        /// </summary>
        public static byte[] BuildHunt(TreasureHunts.Hunt hunt)
        {
            var hhf = Pb.New()
                .VarIfNotZero(1, hunt.Checkpoint)
                .VarIfNotZero(4, hunt.Stages);
            foreach (var flag in hunt.Flags)
                hhf.Msg(5, Pb.New().VarIfNotZero(1, flag.State).VarIfNotZero(2, flag.MapId));
            hhf.VarIfNotZero(6, hunt.Retries)
               .VarIfNotZero(7, hunt.StartMap)
               .VarIfNotZero(8, hunt.Steps.Count)
               .VarIfNotZero(9, hunt.Type);
            foreach (var step in hunt.Steps.Take(hunt.Known))
                hhf.Msg(10, step.Poi > 0
                    ? Pb.New().Msg(1, Pb.New().Var(1, step.Poi).VarIfNotZero(2, step.Direction))
                    : Pb.New().Msg(3, Pb.New().VarIfNotZero(2, step.Maps).VarIfNotZero(3, step.Direction)));
            return hhf.Build();
        }

        // Current hunts store POI and map-count steps, but no hint ID or portal destination.
        internal static TreasureHuntEvent Hunt(TreasureHunts.Hunt hunt)
            => new(hunt.Checkpoint, null, hunt.Stages,
                hunt.Flags.Select(f => new TreasureHuntFlag((TreasureHuntFlagState)f.State, f.MapId)).ToArray(),
                hunt.Retries, hunt.StartMap, hunt.Steps.Count, (TreasureHuntType)hunt.Type,
                hunt.Steps.Take(hunt.Known).Select(s => new TreasureHuntStep(s.Poi > 0
                    ? new TreasureHuntStepCaseFollowDirectionToPoi(new(s.Poi, MapActorMessages.Facing(s.Direction)))
                    : new TreasureHuntStepCaseFollowDirection(new(s.Maps, MapActorMessages.Facing(s.Direction))))).ToArray());

        public static byte[] BuildFlagAnswer(int type, int index, int result)
            => Pb.New().VarIfNotZero(1, result).VarIfNotZero(2, index).VarIfNotZero(3, type).Build();

        /// <summary>The dig's answer; the wrong flags' count only when some were.</summary>
        public static byte[] BuildDigAnswer(int type, int result, int wrong)
        {
            var hht = Pb.New().VarIfNotZero(1, result);
            if (wrong > 0) hht.Var(3, wrong);
            return hht.VarIfNotZero(4, type).Build();
        }

        public static byte[] BuildFinished(int type) => Pb.New().VarIfNotZero(2, type).Build();

        private static long Field(byte[] body, int number)
            => ProtoMessage.Parse(body).Fields.FirstOrDefault(f => f.FieldNumber == number)?.VarIntValue ?? 0;

        /// <summary>A flag put (hhv) or taken away (hhh): the type and the step.</summary>
        public static (int Type, int Index) ReadFlagPut(byte[] hhv) => ((int)Field(hhv, 1), (int)Field(hhv, 2));

        public static (int Type, int Index) ReadFlagRemoved(byte[] hhh) => ((int)Field(hhh, 2), (int)Field(hhh, 1));

        /// <summary>A dig (hgj) or a give-up (hgn): the type.</summary>
        public static int ReadType(byte[] body) => (int)Field(body, 1);

        /// <summary>The legendary hunts a planning table offers (hhg).</summary>
        public static byte[] BuildLegendaryHunts(IEnumerable<int> ids) => Pb.New().Packed(1, ids.Select(id => (long)id)).Build();

        /// <summary>The legendary hunt asked for (hhn): its id.</summary>
        public static int ReadLegendaryAsked(byte[] hhn) => (int)Field(hhn, 2);
    }
}
