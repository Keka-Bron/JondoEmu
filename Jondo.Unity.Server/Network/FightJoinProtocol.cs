using System;
using System.Collections.Generic;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// A fight in its placement as the rest of the map sees it, and the messages of coming into
    /// somebody else's.
    /// </summary>
    /// <remarks>
    /// Measured in five captures, read with tools/hilo.py (both directions merged by clock):
    ///
    ///   F  «Combate/entrar a combate con listo automatico y entrada automatica siguiendo a lider
    ///      de grupo-combate-victoria»: the recorder, Sacri-Master, stands on the map in Harmoo's
    ///      party; Harmoo attacks twice (fights 487 and 488) and Sacri is pulled in behind him.
    ///   S  «Combate/meterse en combate de otra persona haciendo click en la espadita y luego
    ///      abandonar para salirse»: KTAS5625 clicks the swords of a challenge (fight 4923) and
    ///      then leaves it from the placement.
    ///   B  «Busqueda grupo/busqueda automatica de grupo-encuentra para una dung...»: four players
    ///      into one dungeon fight (471), the recorder the fourth, by kay.
    ///   J  «Mazmorras/mazmorra de los jalatós completa»: a kay turned down (frames 855-857).
    ///   Z  «Clases/Zobal/zobal-hechizos normales»: two fights opened by strangers, seen from the
    ///      map (frames 2630-2651), without any fight option set.
    ///
    /// What the bystanders of the map get when somebody attacks a group (F 131-141, Z 2630-2636,
    /// B 80-94), in this order:
    ///
    ///   kmu { f2: the group }            the group goes off the map
    ///   kmu { f2: the attacker }         and so does the attacker
    ///   hpy { the fight on the map }     the swords: two flags, the teams, the options
    ///   jqz { f2: fights on this map }
    ///   kae { team 0, fight }            the attackers, with their names and levels
    ///   kae { team 1, fight }            the monsters' side, empty
    ///   kae { team 1, fight } x N        and once per monster, growing one by one
    ///
    /// Somebody coming in (F 143-157 seen by the one coming, B 96-119 seen from the map):
    ///
    ///   C  kay { f1: a fighter of the team, f2: the fight }     only when he clicks himself
    ///   S  kml kmp(1) kub jru lqu kuq lva                       his entry, as the attacker's
    ///   S  kmk { his enemies, him }                             to everybody in the fight
    ///   S  kae { his team with its people, fight }
    ///   S  kxa kwk                                              the challenges, against monsters
    ///   S  jxg { him }   jzu { the carousel }
    ///   C  ijm kmv   S  the board, as for anyone               FightHandler.SendPreparationAsync
    ///
    /// and on the map: kmu { him }, kae { his team }. A dungeon's monster side is then rebuilt
    /// (B 100-119 and 140-167): for each monster that goes, kar + kmu to the fighters and jzw to
    /// the map; for each that comes, kmk twice, jxg and jzu to the fighters and a growing kae to
    /// the map.
    ///
    /// When the placement is over the swords go: hpr { f1: the fight } (Z 2650-2651, 1.7 s and
    /// 2.2 s after their hpy, the time it took those two fighters to press ready). The count of
    /// jqz does not change then; it drops when the fight is over (Z 7345-7358).
    /// </remarks>
    public static class FightJoinProtocol
    {
        // ─── Asking to come in ──────────────────────────────────────────────────

        /// <summary>
        /// A player asks to come into a fight (kay):
        ///
        ///   f1: a fighter of the team he wants, the one whose flag he clicked
        ///   f2: the fight
        ///
        /// "08e380ecc4d40110bb26" in S: Inu-no-Kami's side of fight 4923. "08a282bc96e619109022"
        /// in J. Zeros when the message is not there.
        /// </summary>
        public static (long Fighter, long FightId) ReadJoinRequest(byte[] frame)
        {
            byte[]? kay = ConnectionProtocol.ReadPayload(frame, Op.Kay);
            if (kay == null || kay.Length == 0) return (0, 0);

            long fighter = 0, fight = 0;
            foreach (var field in ProtoMessage.Parse(kay).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) fighter = field.VarIntValue;
                else if (field.FieldNumber == 2) fight = field.VarIntValue;
            }
            return (fighter, fight);
        }

        /// <summary>
        /// "You cannot come in" (jxs): { f1: the fighter the kay named, f2: why }.
        /// </summary>
        /// <remarks>
        /// ONE reason is measured: 16, in J at frame 856, "08a282bc96e6191010". The fight asked
        /// for (4368) had its attackers' side restricted to their party -- its options block in
        /// the jss reads { f4: 1 }, the same as behind the kau { f3: 1, f4: 1 } of F -- and the
        /// recorder was not in it. That is the only thing standing in his way that the capture
        /// shows, so 16 is read as "restricted to the party": an inference. No other reason has
        /// been seen, and none is made up: a refusal without a measured reason is not answered.
        /// </remarks>
        public static byte[] BuildJoinRefused(long fighter, int reason)
            => Pb.New().Var(1, fighter).VarIfNotZero(2, reason).Build();

        /// <summary>The one refusal reason measured: the side is restricted to its party (J 856).</summary>
        public const int RefusedPartyOnly = 16;

        // ─── The teams, as the map shows them ───────────────────────────────────

        /// <summary>
        /// A person on a team (inside kae and hpy):
        ///
        ///   f1 { f1: level, f2: name }   f2: id
        ///
        /// The level is the one the party sheet carries -- 447 for Harmoo in both. Some entries
        /// carry an f3 { f1: account uuid, f2: nickname } inside f1 (Ocrazy's in B, "WABIT");
        /// most do not (Harmoo, Sacri-Master, KTAS5625, Uvok, Venom-Vi, Riyuga) and it is not
        /// sent.
        /// </summary>
        public static Pb PersonOnTeam(long id, int level, string name)
            => Pb.New()
                .Msg(1, Pb.New().VarIfNotZero(1, level).Str(2, name ?? ""))
                .Var(2, id);

        /// <summary>
        /// A monster on a team: { f2: its fighter id, f5 { f1: monster, f2: grade } }. The grade
        /// is 1-based: the level 200 Puch Ingball of Z goes as 6, the tofus of B as 1.
        /// </summary>
        public static Pb MonsterOnTeam(long id, int monster, int grade)
            => Pb.New()
                .Var(2, id)
                .Msg(5, Pb.New().Var(1, monster).VarIfNotZero(2, grade));

        /// <summary>The kind of side a team is: people (0, left out) or monsters (1).</summary>
        public const int MonsterSide = 1;

        /// <summary>
        /// A team (the f1 of kae, and each f3 of hpy):
        ///
        ///   f2: 1 for a monsters' side   f3: its leader   f4: 1
        ///   f5 { f1 (repeated): a member }                present even when empty
        ///   f6: which team, 1 for the second
        ///
        /// f4 is 1 in every team of the five captures and is sent as it comes. What f2 is read
        /// as: it is 1 on the group's side of every fight against monsters and missing on both
        /// sides of the challenge of S (inference: the kind of side). A challenge's second team
        /// carries f6 = 1 without f2 (S, frame 9); the attackers' side carries neither.
        /// </summary>
        public static Pb Team(int team, long leader, bool monsters, IEnumerable<Pb>? members)
        {
            var list = Pb.New();
            if (members != null)
            {
                foreach (var member in members) list.Msg(1, member);
            }

            return Pb.New()
                .VarIfNotZero(2, monsters ? MonsterSide : 0)
                .Var(3, leader)
                .Var(4, 1)
                .Msg(5, list)
                .VarIfNotZero(6, team);
        }

        /// <summary>
        /// A team as it stands now (kae): { f1: the team, f2: the fight }. F 139-141, 153, 168.
        /// </summary>
        /// <remarks>
        /// The same message carries the member list to the map and, EMPTY, the board's "this is
        /// your side" (F 168, "0a0b18a282f0a6c40820012a0010e703"): one kae, for the receiver's
        /// own side, in each of the six boards of F, S and B and in both views of the challenge.
        /// </remarks>
        public static byte[] BuildTeamUpdate(Pb team, long fightId)
            => Pb.New().Msg(1, team).Var(2, fightId).Build();

        /// <summary>
        /// A fight in its placement, as it lies on the map: what hpy carries in its f1 and what
        /// each f12 of a jss is (S 53, J 840).
        ///
        ///   f1: the two flags' cells, packed, the attackers' first
        ///   f2: the kind of fight, the same number as the kam's -- 4 against monsters; missing
        ///       for a challenge
        ///   f3: the attackers' team     f3: the defenders' team
        ///   f5: the attackers' options  f5: the defenders' options
        ///   f6: the fight
        ///
        /// The options are empty for a side nobody locked, as Z carries them ("2a002a00"). Of what
        /// goes in them only one thing is measured: a side restricted to its party is { f4: 1 } (the
        /// fight 4368 of J 840); the other options' fields are not known and are left out. The
        /// flag cells are the attacker's cell and the group's (F: [71, 73] with the group drawn on
        /// 71 before it went off; B: [228, 200], Ocrazy's path ending on 228 and the group on 200).
        /// </summary>
        public static Pb FightOnMap(long fightId, int kind, int attackersFlag, int defendersFlag,
                                    Pb attackers, Pb defenders,
                                    bool attackersPartyOnly = false, bool defendersPartyOnly = false)
            => Pb.New()
                .Packed(1, new long[] { attackersFlag, defendersFlag })
                .VarIfNotZero(2, kind)
                .Msg(3, attackers)
                .Msg(3, defenders)
                .Msg(5, Pb.New().VarIfNotZero(4, attackersPartyOnly ? 1 : 0))
                .Msg(5, Pb.New().VarIfNotZero(4, defendersPartyOnly ? 1 : 0))
                .Var(6, fightId);

        /// <summary>The swords appear on the map (hpy): { f1: the fight on the map }.</summary>
        public static byte[] BuildFightShown(Pb fight) => Pb.New().Msg(1, fight).Build();

        /// <summary>
        /// The swords go (hpr): { f1: the fight }. When the placement is over, not when the fight
        /// is: "08841a" at Z 2650, "089022" at J 857. A fight in progress is only counted.
        /// </summary>
        public static byte[] BuildFightHidden(long fightId) => Pb.New().Var(1, fightId).Build();

        /// <summary>
        /// How many fights the map has (jqz): { f2: how many }, right behind the jss of a map
        /// that has any and on the map whenever the number changes. "1001" in F 138, "1003"
        /// behind the jss of S 53, and EMPTY when it drops to nothing (Hipermago 4984).
        /// </summary>
        public static byte[] BuildFightCount(int count) => Pb.New().VarIfNotZero(2, count).Build();

        /// <summary>
        /// A member taken off a team, for the map (jzw): { f1: the fight, f2: the team, f4: who }.
        /// "08d703100120ffffffffffffffffff01" at B 100: monster -1 off team 1 of fight 471.
        /// </summary>
        public static byte[] BuildTeamMemberRemoved(long fightId, int team, long fighter)
            => Pb.New().Var(1, fightId).VarIfNotZero(2, team).Var(4, fighter).Build();

        /// <summary>
        /// A fighter taken off the board, for the fighters (kar): { f1: who }.
        /// "08f7ffffffffffffffff01" at B 140, monster -9 going when the fourth player came in.
        /// </summary>
        public static byte[] BuildFighterRemoved(long fighter) => Pb.New().Var(1, fighter).Build();

        /// <summary>
        /// An actor goes off the map (kmu): { f2: who }. The group and the attacker when a fight
        /// opens (F 132-133), each one who joins (B 96, 108), and the monster going with its kar
        /// (B 141, "10f7ffffffffffffffff01"). The same shape as FightProtocol.BuildFightAgainst.
        /// </summary>
        public static byte[] BuildActorHidden(long actor) => Pb.New().Var(2, actor).Build();

        /// <summary>
        /// "You are out of the fight" (jxa), empty: the first thing the one who leaves from the
        /// placement gets, before his way back to the map (S 37: jxa kml kmp ktz iom jru).
        /// </summary>
        public static byte[] BuildLeftFight() => Array.Empty<byte>();

        // ─── The party ──────────────────────────────────────────────────────────

        /// <summary>
        /// A party member has gone into a fight (ilh), for the members who do not follow him in:
        ///
        ///   f1: who                    f2: tenths left of the placement, missing without one
        ///   f3: his account            f4: his name
        ///   f5: the fight              f6: the party
        ///   f7: 1 against monsters, 4 a challenge
        ///   f8 { f1: map, f2: x, f4: subarea, f5: y }   where the fight is
        ///
        /// Measured three times: B 95 (Ocrazy, 449 tenths left, f7 = 1) and the two challenge
        /// captures (Harmoo, no f2, f7 = 4). f3 is 63000188 for Harmoo in both challenges and a
        /// different eight-digit number for Ocrazy: read as the account (inference). f8 is the
        /// party sheet's position block; its numbers are the MapPositions row of the map, to
        /// the last digit, in both.
        /// </summary>
        public static byte[] BuildPartyMemberInFight(long member, int placementLeft, long account,
                                                     string name, long fightId, long partyId,
                                                     int reason, long mapId, int x, int y, int subArea)
            => Pb.New()
                .Var(1, member)
                .VarIfNotZero(2, placementLeft)
                .VarIfNotZero(3, account)
                .Str(4, name ?? "")
                .Var(5, fightId)
                .Var(6, partyId)
                .VarIfNotZero(7, reason)
                .Msg(8, Pb.New()
                    .Var(1, mapId)
                    .VarIfNotZero(2, x)
                    .VarIfNotZero(4, subArea)
                    .VarIfNotZero(5, y))
                .Build();

        /// <summary>The f7 of ilh against monsters (B 95).</summary>
        public const int PartyFightMonsters = 1;

        /// <summary>The f7 of ilh in a challenge (both challenge captures).</summary>
        public const int PartyFightChallenge = 4;
    }
}
