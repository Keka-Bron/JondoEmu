using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The fight messages of 3.6.10.10, measured from the fifteen captures of
    /// <c>Wireshark captures from real game\Combate</c>.
    ///
    /// It was necessary to start from scratch: of the forty-eight opcodes the fight handler
    /// used only seven still exist in this version, and in the captures two hundred and
    /// seventy-one appear that the code did not name. What is reused is the state machine
    /// —teams, placement, turns, loot—: what was wrong was the wire, not the design.
    ///
    /// This class covers for now the PREPARATION, which is what there is a complete measurement of. The rest
    /// of the fight is half deciphered and will be added here; what is missing is noted in
    /// docs/fight.md.
    ///
    /// The preparation thread, read from the real order of the capture (tools/hilo.py merges both
    /// directions by clock, which is what `pcap.streams` cannot do):
    ///
    ///   client   hqa { f1: monster group id }            attack
    ///   server   jsq                                     empty, acknowledged
    ///   server   ...the normal map change: kub, jru, lva...
    ///   server   jxg   one per fighter
    ///   server   kba   the blue cells and the red ones
    ///   server   jzu   who goes in each team
    ///   server   jwq   empty
    ///   server   jrk { f2: 10, f4: map }
    ///   client   jzy { f1: who, f2: cell }               places himself
    ///   server   kmk { f1: cell, f2: orientation, f3: who }
    ///   client   kaq { f1: 1 }                           the ready button
    ///   server   kah { f1: who, f3: 1 }
    ///
    /// A detail that saves work: between the cells and the ready button the server sends NO
    /// timer —only the kqo heartbeats—, so the placement countdown is kept by
    /// the client on its own. The server only has to start the fight when the
    /// time runs out.
    /// </summary>
    public static class FightProtocol
    {
        /// <summary>
        /// The id "nobody here" or "not known yet who" travels with.
        ///
        /// During placement the enemy side travels whole as -1: the monster group has not
        /// been split into fighters until the fight really starts. And in the kmk a
        /// cell that is vacated is sent with this same -1.
        /// </summary>
        public const long Nobody = -1;

        /// <summary>What the jrk carries in its f2 in the fifteen captures.</summary>
        private const int FightMapKind = 10;

        // ─── Empezar ────────────────────────────────────────────────────────────

        /// <summary>
        /// Which monster group the client attacks (hqa).
        ///
        ///   f1: the group's contextual id, negative
        ///
        /// Returns zero if the message brings no group.
        /// </summary>
        public static long ReadFightRequest(byte[] payload)
        {
            byte[]? hqa = ConnectionProtocol.ReadPayload(payload, Op.Hqa);
            if (hqa == null) return 0;

            foreach (var field in ProtoMessage.Parse(hqa).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) return field.VarIntValue;
            }
            return 0;
        }

        /// <summary>Attack acknowledged (jsq). It goes empty.</summary>
        public static byte[] BuildFightAccepted() => Array.Empty<byte>();

        /// <summary>
        /// "The coming map is a fight one" (kmp).
        ///
        /// This is THE mark, and it goes inside the map change, before loading it. It is what makes
        /// the client ask for the fight with an empty ijm instead of asking for the contents of a normal
        /// map with jrh.
        ///
        /// The correlation leaves no room for doubt: in the thirty-nine fight entries of the
        /// twenty-three captures, the last kmp before the kam carried f1: 1 —thirty-nine out of
        /// thirty-nine— and in the seven hundred and eighteen ordinary map loads it went empty or did not
        /// go, without a single exception.
        ///
        /// Without this the client loads the tactical map but stays in normal map mode, and everything
        /// that comes after —the fighters, the cells, the teams— reaches it without a fight
        /// to belong to. What is seen then is the board drawn and nothing on it.
        /// </summary>
        public static byte[] BuildFightMapComing() => Pb.New().Var(1, 1).Build();

        /// <summary>
        /// Who is going to be fought (kmu), at the start of loading the tactical map.
        ///
        ///   f2: the group's contextual id, the same negative the kam will carry later
        ///
        /// Checked in four captures against monsters: the kmu's number and the kam.f3's are
        /// the same.
        /// </summary>
        public static byte[] BuildFightAgainst(long defender) => Pb.New().Var(2, defender).Build();

        /// <summary>End of the preparation batch (jwq). It also goes empty.</summary>
        public static byte[] BuildPlacementDone() => Array.Empty<byte>();

        /// <summary>
        /// The same jwq when a fight is already running: every live buff of everybody, each
        /// entry being exactly the payload its jxm carried. Measured in the reconnection
        /// capture, where the jwq of the burst is 1,449 bytes of jxm bodies where the one of a
        /// fresh fight is empty.
        /// </summary>
        public static byte[] BuildBuffSync(IEnumerable<byte[]> buffs)
        {
            var jwq = Pb.New();
            foreach (byte[] buff in buffs) jwq.Bytes(1, buff);
            return jwq.Build();
        }

        /// <summary>
        /// Which map the fight is on (jrk).
        ///
        ///   f2: 10      f3: empty      f4: the map
        ///
        /// f3 goes present and empty in the captures, so it is sent the same: it is a submessage with no
        /// fields, which is not the same as not sending it.
        /// </summary>
        public static byte[] BuildFightMap(long mapId)
            => Pb.New().Var(2, FightMapKind).EmptyMsg(3).Var(4, mapId).Build();

        // ─── The fight exists ───────────────────────────────────────────────────

        /// <summary>
        /// The map is ready (ijq). It goes empty and goes out right before the fight is announced.
        /// </summary>
        public static byte[] BuildMapReady() => Array.Empty<byte>();

        /// <summary>
        /// There is a fight here (kam). It is THE message that creates the fight for the client.
        ///
        ///   f2: what type           f3: against whom
        ///   f4: [the monsters' templates]
        ///   f5: the fight's id      f6: who started it
        ///
        /// Without this there is no fight to speak of and everything that comes after falls on deaf ears. It is seen
        /// in the client's own log: on receiving the jwq it blows up with
        ///
        ///   NullReferenceException
        ///     at gum.blww (Google.Protobuf.Collections.RepeatedField`1[T] a)
        ///     at guk.blvw (jwq a)
        ///
        /// which is the client walking the fighter list of a fight that does not exist.
        ///
        /// What each field carries, measured in the fourteen openings of the captures:
        ///
        ///   f2  4 against monsters, 7 in the koliseo, and absent in a challenge between players.
        ///   f3  the monster group, with its NEGATIVE contextual id, just as it travels in the jss.
        ///       In a challenge it is the other player's id, positive.
        ///   f4  a packed list with one entry PER MONSTER: against one poutch it goes [494] and
        ///       against four it goes [494, 494, 494, 494]. In a group of eight, eight different numbers
        ///       come out, and they match the creatures' templates.
        ///   f5  the fight's id, which is repeated later in the kae and in each kau.
        /// </summary>
        public static byte[] BuildFightAnnounced(int kind, long defender, IEnumerable<long> monsters,
                                                 long fightId, long starter)
        {
            var kam = Pb.New()
                .VarIfNotZero(2, kind)
                .Var(3, defender);

            var list = new List<long>(monsters);
            if (list.Count > 0) kam.Packed(4, list);

            return kam.Var(5, fightId).VarIfNotZero(6, starter).Build();
        }

        /// <summary>Against monsters. The koliseo is 7 and a challenge between players carries no type.</summary>
        public const int AgainstMonsters = 4;

        /// <summary>The koliseo. Measured: its kam arrives «100728ee0a», that is f2=7.</summary>
        public const int Koliseo = 7;

        /// <summary>
        /// How long placement lasts in the koliseo, in tenths.
        /// </summary>
        /// <remarks>
        /// The 592 of the capture's kaa, as is. It is the sixty seconds minus what the frame took to
        /// arrive, just as the 445 of an ordinary fight is the forty-five.
        /// </remarks>
        public const int KoliseoPlacementDeciseconds = 592;

        /// <summary>
        /// What accompanies the announcement (kaa).
        ///
        ///   f3: 1      f4: 1      f5: ?      f6: the fight type
        ///
        /// f3 and f4 are 1 in the thirty-nine openings and f6 repeats the kam's type.
        ///
        /// f5 is THE PLACEMENT COUNTDOWN, in tenths of a second. It was worked out with the capture's
        /// clock: in one it is 445 and twenty-seven-odd seconds later it is 173, that is 272
        /// units in 27.4 seconds, 9.92 per second. It matches the values seen —442,
        /// 444, 445, 446, which are the forty-five seconds minus what it took to arrive— and
        /// the koliseo's 592, which gives its sixty. It goes absent when the fight is already past
        /// placement.
        ///
        /// Here the time the server will really wait before starting on its own is sent,
        /// so that the client's clock and the server's count the same.
        /// </summary>
        /// <summary>
        /// A challenge's kaa: six bytes and NO countdown.
        /// </summary>
        /// <remarks>
        /// Measured in «enviar desafio y el otro acepta»: «f2=1 f3=1 f4=1» arrives and nothing else. Neither
        /// the time's f5 nor the type's f6. That is why no placement clock appears in a duel: it is not
        /// that it is hidden, it is that the real server sends none.
        /// </remarks>
        public static byte[] BuildDuelSummary()
            => Pb.New().Var(2, 1).Var(3, 1).Var(4, 1).Build();

        /// <summary>
        /// The kaa of a fight already running, for whoever comes back into it: f1 = 1 and no
        /// countdown. "0801180120013004" in both resumes of the reconnection capture; a duel's
        /// would carry no f6, which is not measured.
        /// </summary>
        public static byte[] BuildFightInProgressSummary(int kind)
            => Pb.New().Var(1, 1).Var(3, 1).Var(4, 1).VarIfNotZero(6, kind).Build();

        public static byte[] BuildFightSummary(int kind, int placementDeciseconds)
            => Pb.New()
                .Var(3, 1)
                .Var(4, 1)
                .VarIfNotZero(5, placementDeciseconds)
                .VarIfNotZero(6, kind)
                .Build();

        /// <summary>
        /// A fight option (kau): locking out spectators, closing it to the party and so on.
        ///
        ///   f3: which one   f5: the fight's id
        ///
        /// Four come out in a row at each opening, with f3 being 2, 1, 3 and the fourth without f3.
        /// </summary>
        public static byte[] BuildFightOption(int option, long fightId)
            => BuildFightOption(0, option, false, fightId);

        /// <summary>
        /// The same with its side and its state: { f1: the side, f3: which, f4: on, f5: the fight }.
        /// "08011802200128bb26" is the defenders' side closed in the sword capture (frame 34),
        /// "1801200128e703" the attackers' restricted to their party in the follow capture (136).
        /// </summary>
        public static byte[] BuildFightOption(int team, int option, bool on, long fightId)
            => Pb.New().VarIfNotZero(1, team).VarIfNotZero(3, option).VarIfNotZero(4, on ? 1 : 0).Var(5, fightId).Build();

        /// <summary>The four the real server sends, in their order.</summary>
        public static readonly int[] FightOptions = { 2, 1, 3, 0 };

        // ─── Who fights ─────────────────────────────────────────────────────────

        /// <summary>
        /// A fighter (jxg).
        ///
        ///   f2 { f1 { f1: cell, f2: orientation, f4: 0 }
        ///        f2 { f2: the sheet, f3: the look }
        ///        f3: who it is }
        ///
        /// Mind the outer f2, which wraps EVERYTHING and is easy to overlook: without it the client
        /// parses the message, finds no fighter inside and draws nothing. The board
        /// comes out with its blue and red cells and there is nobody on it.
        ///
        /// The envelope is THE SAME as that of a map actor in the jss: cell and orientation
        /// in front, the body in the middle and the id behind. That is why the client knows how to draw a
        /// fighter with the code it already has, and why here it can be passed the look
        /// block the map already builds without touching it.
        ///
        /// The sheet is a list of characteristics with the same numbering the emulator uses in
        /// datos/characteristics.json: 0 life, 1 AP, 23 MP, 27 and 28 dodges, 33 to 37 the
        /// resistances. During placement almost all travel empty —the real value does not
        /// arrive until the fight starts—, so replicating it is sending the slot set and with no
        /// number inside.
        /// </summary>
        public static byte[] BuildFighter(int cell, int orientation, long fighterId,
                                          IEnumerable<(int Characteristic, long Base, long Gear)> sheet,
                                          byte[] look, Pb identity, bool isMonster)
            => Pb.New()
                .Msg(2, FighterBlock(cell, orientation, fighterId, sheet, look, identity, isMonster))
                .Build();

        /// <summary>
        /// All the fighters at once, with the sheet full (jxb).
        ///
        ///   f1 (repeated): a fighter, the SAME block that goes inside the jxg
        ///
        /// It is what is sent on starting the real fight, and again whole on reconnecting to
        /// one in progress. The difference from placement is not the shape but what the
        /// characteristics carry inside: in a monster's jxg they go empty and here their values arrive.
        ///
        /// The order they go in is NOT the initiative one —they come out by side, the monsters and then
        /// the player—, so the carousel is not ordered from here.
        /// </summary>
        public static byte[] BuildAllFighters(IEnumerable<Pb> fighters)
        {
            var jxb = Pb.New();
            foreach (var fighter in fighters) jxb.Msg(1, fighter);
            return jxb.Build();
        }

        /// <summary>A fighter's block, which is reused in the jxg and in the jxb.</summary>
        /// <summary>
        /// A sheet characteristic, with the value in the slot it belongs to.
        ///
        /// Here was the reason the damage preview was not seen. The emulator put
        /// ALL the characteristics in the same mould, <c>f5 { f1: value }</c>, which is that of the
        /// action and movement points —that is why those two were drawn right and nothing else—. The
        /// real server uses three different moulds, and it is seen byte by byte in the capture's jxb:
        ///
        ///   monster, all             f2 { f2: value }         and an empty f2 if it is zero
        ///   player, AP(1) and MP(23) f5 { f1: base, f5: from equipment }
        ///   player, the rest         f4 { f2: base, f3: 100, f7: from equipment }   f4 empty if zero
        ///
        /// The capture character's power travels as <c>2a 07 08 19 22 03 38 96 01</c>, that
        /// is f5 { f1: 25, f4 { f7: 150 } }; the shape the emulator emitted for that same thing,
        /// <c>2a 07 08 19 2a 03 08 96 01</c>, does not appear once in the whole capture.
        ///
        /// The f3 with the hundred is only carried by the five that are distributed with points —strength,
        /// vitality, chance, agility and intelligence—, just as it was measured.
        /// </summary>
        private static readonly HashSet<int> ConMultiplicadorBase = new HashSet<int> { 10, 11, 13, 14, 15 };

        /// <summary>The two that go in the points mould.</summary>
        private const int ActionPoints = 1;
        private const int MovementPoints = 23;

        /// <summary>
        /// «Malus de vida temporal»: THE LIFE THE CHARACTER THE CLIENT CONTROLS IS MISSING.
        ///
        /// And it is the only way the client has of knowing it. For the monsters and the player
        /// opposite it keeps subtracting the life of the hits it sees go by; ITS OWN no, its own it
        /// takes from the cap plus this characteristic. It is measured without a single exception: of the 23
        /// times it appears in the 305 captures, all 23 are addressed to one's own character. Not one
        /// to a monster, not one to the duel's rival, who takes hits the whole fight.
        ///
        /// It carries two numbers:
        ///
        ///   f2 = current life minus ORIGINAL maximum life   (goes up with heals, down with hits)
        ///   f8 = it is not the erosion
        ///
        /// Checked against a whole capture: −104 after taking 104, −5 after healing 99,
        /// +128 after healing another 133. The arithmetic matches to the point all three times.
        ///
        /// And with the complete challenge: f2 = −1567 with 1567 of accumulated damage and the cap already
        /// eroded by 159, that is the cap of the subtraction is the STARTING one, not the current one. The
        /// f8 that was read here as erosion is −1122 in that same message and −3324 in the
        /// next, with 236 of erosion: it is not that. In the jalatós dungeon it comes out in all
        /// eight 97 with values between −114 and −338 and f2 even becomes POSITIVE (+220) after a
        /// streak of heals. It is the general mould's buff slot and it is not sent until it is known
        /// what goes inside.
        ///
        /// The emulator sent it once, empty, on starting the fight, and did not touch it again:
        /// that is why the player was hit the whole fight and his bar stayed full.
        /// </summary>
        public const int TemporaryLifeMalus = 97;

        /// <param name="delEmbrujo">
        /// THE BUFF SLOT, f8. It is what spells set and remove during the fight, and it
        /// goes SEPARATE from the base and the equipment: the client keeps all three and adds them itself.
        ///
        /// Without this there was no way to refresh a characteristic without trampling the rest, and it was the
        /// cause of the damage preview coming out wrong. Measured over the 401 captures:
        /// 2,830 of the 3,279 jxw entries with a detailed mould (86.3 %) carry it, and we did not
        /// write it once in 1,713.
        /// </param>
        public static Pb SheetEntry(int characteristic, long baseValue, long fromGear, bool isMonster,
                                    long delEmbrujo = 0)
        {
            var entry = Pb.New().VarIfNotZero(1, characteristic);

            if (isMonster)
            {
                long total = baseValue + fromGear + delEmbrujo;
                if (total == 0) entry.EmptyMsg(2);
                else entry.Msg(2, Pb.New().Var(2, total));
                return entry;
            }

            if (characteristic == ActionPoints || characteristic == MovementPoints)
            {
                return entry.Msg(5, Pb.New().VarIfNotZero(1, baseValue).VarIfNotZero(5, fromGear));
            }

            // 97 has its own mould: f4 { f2, f8 }, and not f4 { f2, f7 } like the rest. Measured
            // on the 23 appearances in the 305 captures, and three of them carry ONLY
            // f8. See TemporaryLifeMalus.
            if (characteristic == TemporaryLifeMalus)
            {
                if (baseValue == 0 && fromGear == 0) return entry.EmptyMsg(4);
                return entry.Msg(4, Pb.New().VarIfNotZero(2, baseValue).VarIfNotZero(8, fromGear));
            }

            if (baseValue == 0 && fromGear == 0 && delEmbrujo == 0)
            {
                return entry.EmptyMsg(4);
            }

            // The f3 with the hundred is NOT sent, even though the real server carries it in five of them.
            // This client ADDS it instead of taking it as a percentage: with it set, the sheet
            // showed 568 strength where there are 468, and a hundred too many in intelligence, chance and
            // agility. Until it is known what exactly it expects, better not to send it.
            var valor = Pb.New().VarIfNotZero(2, baseValue).VarIfNotZero(7, fromGear)
                          .VarIfNotZero(8, delEmbrujo);
            return entry.Msg(4, valor);
        }

        public static Pb FighterBlock(int cell, int orientation, long fighterId,
                                      IEnumerable<(int Characteristic, long Base, long Gear)> sheet,
                                      byte[] look, Pb identity, bool isMonster)
        {
            var stats = Pb.New().Var(3, SheetKind);
            foreach (var (characteristic, baseValue, gear) in sheet)
            {
                stats.Msg(5, SheetEntry(characteristic, baseValue, gear, isMonster));
            }

            // Who it is, with its place repeated. It appears in both, with f2 only in the monsters.
            var where = Pb.New().Var(1, cell).VarIfNotZero(2, orientation).Var(4, 0);
            var again = Pb.New()
                .VarIfNotZero(2, isMonster ? 1 : 0)
                .Var(3, 1)
                .Msg(4, Pb.New().Msg(1, where).Var(3, fighterId));

            // The fighter's block: the identifier in front, the sheet, what says what it is —f3 in
            // a monster, f6 in a player— and the place again in f7.
            var fighter = Pb.New()
                .Var(1, isMonster ? 0 : fighterId)
                .Msg(2, stats);
            if (isMonster) fighter.Msg(3, identity);
            else fighter.Msg(6, identity);
            fighter.Msg(7, again);

            return Pb.New()
                .Msg(1, Pb.New().Var(1, cell).VarIfNotZero(2, orientation).Var(4, 0))
                .Msg(2, Pb.New()
                    .Msg(2, fighter)
                    .Bytes(3, look ?? Array.Empty<byte>()))
                .Var(3, fighterId);
        }

        // ─── The real fight ─────────────────────────────────────────────────────

        /// <summary>
        /// "Placement is over" (kai). It goes empty all ten times it appears.
        ///
        /// It is the only clean cut between the two phases: everything before is placement and everything
        /// after —jyy, jxz, jxc, jto, jxb, jwi— is the loading of the fight already started.
        /// </summary>
        public static byte[] BuildFightBegins() => Array.Empty<byte>();

        /// <summary>Which round we are on (jxz). <c>f2</c> is the round number, starting at 1.</summary>
        public static byte[] BuildRound(int round) => Pb.New().Var(2, round).Build();

        /// <summary>
        /// How long each spell has left until it can be cast again (jxc).
        ///
        ///   f1 (repeated) { f1: the spell, f2: rounds left }
        ///   f4: whose list it is
        ///
        /// It is not the initiative order, even though it looks like it for carrying a list. On starting the
        /// fight only the spells born with a wait come out, and they match SpellLevels's
        /// InitialCooldown. For monsters and summons the list goes empty and only f4 travels.
        /// </summary>
        public static byte[] BuildCooldowns(long fighterId,
                                            IEnumerable<(int Spell, int Rounds)> cooldowns)
        {
            var jxc = Pb.New();
            foreach (var (spell, rounds) in cooldowns)
            {
                jxc.Msg(1, Pb.New().Var(1, spell).VarIfNotZero(2, rounds));
            }
            return jxc.Var(4, fighterId).Build();
        }

        /// <summary>
        /// Opens a sequence (jto): <c>f1</c> who causes it and <c>f2</c> what type.
        ///
        /// Everything that happens in a fight goes between a jto and its jwi. 2,229 of each come out
        /// in the fifteen captures, exactly the same, which is what gives them away as a pair.
        /// </summary>
        public static byte[] BuildSequenceStart(long author, int kind)
            => Pb.New().Var(1, author).Var(2, kind).Build();

        /// <summary>
        /// Closes a sequence (jwi): <c>f1</c> the action number, <c>f2</c> who and <c>f3</c>
        /// the same type it was opened with. The client acknowledges each closing with a jti.
        /// </summary>
        public static byte[] BuildSequenceEnd(int actionId, long author, int kind)
            => Pb.New().Var(1, actionId).Var(2, author).Var(3, kind).Build();

        /// <summary>
        /// The sequence a fight is abandoned with.
        /// </summary>
        /// <remarks>
        /// Measured in the three captures carrying a kme, and it is not the normal action one. In
        /// «combate contra poutch nivel 75 ... hechizos sacro-rendirse.pcapng» the jto answering
        /// the kme is 08a28280c8e7081005 —f2 = 5— and its jwi is 080410a28280c8e7081805 —f3 = 5—; in
        /// «aceptar desafio-combate completo-abandonar al final.pcapng», 08a282f0a6c4081005 and
        /// 080210a282f0a6c4081805. The 5 appears exactly once per capture and only there.
        /// </remarks>
        public const int SurrenderSequence = 5;

        /// <summary>The fight's start sequence.</summary>
        public const int OpeningSequence = 8;

        /// <summary>The end-of-turn one.</summary>
        public const int TurnEndSequence = 7;

        /// <summary>
        /// "Confirm to me" (jxh). The server sends it before each turn and waits for the client's jwz
        /// to go on.
        /// </summary>
        public static byte[] BuildConfirmTurn(long fighterId) => Pb.New().Var(2, fighterId).Build();

        /// <summary>
        /// Whose turn it is (jzc).
        ///
        ///   f1: who        f2: how long it lasts, in TENTHS of a second
        ///   f4: what it carries over from the previous turn      f7: what position it holds in the round
        ///   f8: the round
        ///
        /// The duration was checked against the captures' clock: with f2 = 410, 41.002
        /// seconds pass until the end of the turn, with 350, 35.001 and with 420, 42.002. And it is not the same
        /// for everyone: characters go between 350 and 430, monsters at 290 in the twelve captures
        /// without exception, and summons at 150.
        ///
        /// f7 is what orders the carousel: there is no separate initiative list, each
        /// turn says what position whoever plays it holds.
        /// </summary>
        /// <param name="carried">The tenths he kept from his last turn (f4): see <see cref="SavedAfter"/>.</param>
        public static byte[] BuildTurnStart(long fighterId, int deciseconds, int index, int round, int carried = 0)
            => Pb.New()
                .Var(1, fighterId)
                .Var(2, deciseconds)
                .VarIfNotZero(4, carried)
                .VarIfNotZero(7, index)
                .VarIfNotZero(8, round)
                .Build();

        /// <summary>
        /// What a character keeps of the turn he passes: half of what was left of it, and never so
        /// much that his next turn, with it, goes beyond <see cref="MaxTurnDeciseconds"/>.
        /// </summary>
        /// <remarks>
        /// Measured in "bastante pelea con hipermago", a turn of 360 after another: he passes with
        /// 83 tenths left and the jyt keeps 41, the next jzc carries them in its f4; 401 to use,
        /// 158 used, 121 kept; 481, 137, 172. Across the captures f2 + f4 never goes beyond 600 --
        /// a turn of 370 carries 230 at most, one of 430 carries 170 -- and this server goes to
        /// 900, a minute and a half, which is what its owner asked for. Passed by the clock,
        /// nothing is left and nothing is kept.
        /// </remarks>
        public static int SavedAfter(int remainingDeciseconds, int baseDeciseconds)
            => Math.Max(0, Math.Min(remainingDeciseconds / 2, MaxTurnDeciseconds - baseDeciseconds));

        /// <summary>The longest a character's turn can be, carried time and all: a minute and a half.</summary>
        public const int MaxTurnDeciseconds = 900;

        /// <summary>
        /// The same jzc for somebody who comes back in the middle of the turn: f6 is what is
        /// left of it, in tenths. One sample, and it adds up to the tenth: in the reconnection
        /// capture the turn of 350 had started 21.8 seconds before the burst and f6 says 132.
        /// That frame carries no f7, so neither does this one.
        /// </summary>
        public static byte[] BuildTurnResumed(long fighterId, int deciseconds, int remaining, int round, int carried = 0)
            => Pb.New()
                .Var(1, fighterId)
                .Var(2, deciseconds)
                .VarIfNotZero(4, carried)
                .VarIfNotZero(6, remaining)
                .VarIfNotZero(8, round)
                .Build();

        /// <summary>How long a turn lasts, in tenths: a character, a monster and a summon.</summary>
        public const int PlayerTurnDeciseconds = 400;
        public const int MonsterTurnDeciseconds = 290;

        /// <summary>
        /// How long a summon's turn lasts. It is shorter than a monster's, and it is measured:
        /// in the Cra captures the beacon receives a jzc with 150 where the pious carry 290 and
        /// the player 370.
        /// </summary>
        public const int SummonTurnDeciseconds = 150;

        /// <summary>
        /// "You can play now" (jyj). It goes empty, and is ONLY sent if whoever plays is one of those
        /// that client controls: in a monster's turn this step does not exist.
        /// </summary>
        public static byte[] BuildYourTurn() => Array.Empty<byte>();

        // ─── Moving and casting ─────────────────────────────────────────────────

        /// <summary>
        /// Where the player wants to walk (jrw).
        ///
        ///   f1: the map
        ///   f2: packed varints, each one <c>(direction &lt;&lt; 12) | cell</c>
        ///
        /// And careful, it is not the whole path: only the POINTS WHERE IT TURNS go. The first is
        /// where it starts from and the last is where it goes, and that last one's direction is which way it
        /// wants to end up facing. A straight path is two numbers.
        ///
        /// It is the same message used to walk around the map outside a fight.
        /// </summary>
        public static (long MapId, List<int> Corners, int Facing) ReadMove(byte[] payload)
        {
            var corners = new List<int>();
            long mapId = 0;
            int facing = 0;

            byte[]? jrw = ConnectionProtocol.ReadPayload(payload, Op.Jrw);
            if (jrw == null) return (0, corners, 0);

            foreach (var field in ProtoMessage.Parse(jrw).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) mapId = field.VarIntValue;
                else if (field.FieldNumber == 2 && field.WireType == 2)
                {
                    foreach (long key in Unpack(field.BytesValue))
                    {
                        corners.Add((int)(key & 0xFFF));
                        facing = (int)(key >> 12);
                    }
                }
            }
            return (mapId, corners, facing);
        }

        private static List<long> Unpack(byte[] bytes)
        {
            var fuera = new List<long>();
            int i = 0;
            while (i < bytes.Length)
            {
                long value = 0;
                int shift = 0;
                while (i < bytes.Length)
                {
                    byte b = bytes[i++];
                    value |= (long)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                fuera.Add(value);
            }
            return fuera;
        }

        /// <summary>
        /// Which cell is cast at and with what (jwh).
        ///
        ///   f1: the target cell          f4: the spell, and if it does not come it is a weapon hit
        /// </summary>
        public static (int Cell, int Spell) ReadCast(byte[] payload)
        {
            byte[]? jwh = ConnectionProtocol.ReadPayload(payload, Op.Jwh);
            if (jwh == null) return (0, 0);

            int cell = 0, spell = 0;
            foreach (var field in ProtoMessage.Parse(jwh).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) cell = (int)field.VarIntValue;
                else if (field.FieldNumber == 4) spell = (int)field.VarIntValue;
            }
            return (cell, spell);
        }

        /// <summary>
        /// Casting by targeting FROM THE CAROUSEL (jwn): { f1: at whom, f2: the spell }.
        ///
        /// The id comes WITH A SIGN —monsters have it negative— and in sixty-four-bit two's
        /// complement, so it has to be read as a <c>long</c> and not as an <c>int</c>:
        /// two of the four real samples are minus one.
        ///
        ///   08ffffffffffffffffff01 10ca63   =  at fighter −1, spell 12746
        ///   08a28280c8e708 10b21b           =  at oneself, spell 3506
        /// </summary>
        public static (long Fighter, int Spell) ReadCastAtFighter(byte[] payload)
        {
            byte[]? jwn = ConnectionProtocol.ReadPayload(payload, Op.Jwn);
            if (jwn == null) return (0, 0);

            long quien = 0;
            int spell = 0;
            foreach (var field in ProtoMessage.Parse(jwn).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) quien = unchecked((long)field.VarIntValue);
                else if (field.FieldNumber == 2) spell = (int)field.VarIntValue;
            }
            return (quien, spell);
        }

        /// <summary>
        /// A buff placed on someone (jxm), which is what fills the "Efectos" panel.
        ///
        ///   f1 { f1 { f1: the effect's die, if it brings one
        ///             f2: on whom          f3: the buff number, consecutive from one
        ///             f4: 1                f6 { f2: -1 }
        ///             f7: the trigger      f8: the effect's identifier (effectUid)
        ///             f10: the value       f12 { f2: -1, f3: -1 }
        ///             f13: 1, only those bringing a die
        ///             f14: the spell that placed it
        ///             f15: 7 those bringing a die, 2 the rest     f16: 2 }
        ///        f2: on whom               f3: the effect number }
        ///
        /// All this is measured against the level 50 poutch capture and matches the data:
        /// f8 is exactly the <c>effectUid</c> the spell carries in its EffectsJson —299043
        /// for Transposición's 950, 220298 for La Sangre de Sacrogrito's 792—, f10 is
        /// its <c>value</c> and f7 its <c>triggers</c>. And when an effect brings several triggers
        /// separated by bars, the server sends ONE jxm for each; in the capture nine come out
        /// in a row for the same effect, with "TB", "D", "TE", "VE", "VM", "PD", "LPU", "DV" and "V".
        /// </summary>
        /// <summary>
        /// The buff's FAMILY, which goes in f15 and decides whether the client DRAWS it or not.
        ///
        /// Here was the reason the panel always came out empty with the bytes right. The
        /// builder was calibrated against a single specimen —Transposición's effect 950, which is
        /// a state— and from there came a "seven if it brings a die, two if not", which is exactly the opposite
        /// of what is needed: SEVEN is the internal machinery one, the one the panel does not
        /// show. All the boosts came out labelled as machinery.
        ///
        /// The rule comes from the client's catalogue, from two columns of the Effects table:
        ///
        ///   Category == 3           -> 4          modifier of a specific spell, IT IS DRAWN
        ///                                         ("Flecha Helada: +8 de daños básicos")
        ///   effect 950              -> 2          sets a state, drawn as an icon
        ///   Boost == 0              -> 7          internal machinery, NOT drawn
        ///   Boost == 1, Category 0  -> not sent   characteristic bonus, IT IS DRAWN
        /// </summary>
        public static int FamiliaDelEmbrujo(int efecto, int categoria, int boost)
        {
            const int PoneEstado = 950;
            const int DesactivaEstado = 952;
            const int ModificaUnHechizo = 3;

            if (categoria == ModificaUnHechizo) return 4;
            // 952 is a state row too -- the state it switches off rides in its value -- so it is
            // drawn with them. No capture shows one; the family is the reading, not a measure.
            if (efecto == PoneEstado || efecto == DesactivaEstado) return 2;
            if (boost == 0) return HiddenFamily;
            return 0;                 // characteristic bonus: f15 does not travel
        }

        /// <summary>
        /// The family the panel does not draw. A waiting row travels with it whatever its
        /// effect -- the +1 MP of Paso de Cacería included -- and turns visible when it goes off.
        /// </summary>
        public const int HiddenFamily = 7;

        /// <param name="grado">
        /// The grade of the spell that places it. It was nailed to one; measured against the 1,297 jxm of the
        /// Cra captures, the field is the grade and it matches in all 1,297.
        /// </param>
        /// <param name="rondas">
        /// The round IN WHICH IT DROPS, counted from the start of the fight, not what it has left.
        /// Flecha Helada leaves three turns of basic damage: cast in round 5 the real server
        /// sends an eight, and in round 6, a nine. Minus one is "until the fight ends".
        /// </param>
        /// <param name="padre">
        /// The waiting row this one came out of, in f11, for the rows a delayed effect turns
        /// into when its round comes. Measured on Paso de Cacería: the live +1 MP row names
        /// the "Y" row of the cast. Zero when there is none.
        /// </param>
        /// <param name="activacion">
        /// For a waiting row, the round it goes off in, carried in f12 in place of the two
        /// "nobody" of an ordinary row: "f12{f2=27}" on the beacon's delayed kill, "f12{f2=2}"
        /// on Paso de Cacería's. Negative for an ordinary row.
        /// </param>
        public static byte[] BuildBuff(long sobre, long quien, int numero, int efecto, int effectUid,
                                       int valor, int dado, int cara, int hechizo, string disparador,
                                       int rondas, int dispellable, int familia, int grado = 1,
                                       bool critico = false, int padre = 0, int activacion = -1)
        {
            var dentro = Pb.New()
                .VarIfNotZero(1, dado)
                .Var(2, sobre)
                .Var(3, numero)
                .Var(4, grado)
                .Msg(6, Pb.New().Var(2, rondas))
                .Str(7, WireTrigger(disparador))
                .VarIfNotZero(8, effectUid)
                // One if the cast was critical. Measured in the Flecha Helada capture: the
                // six buffs of effect 293 are identical except the one of the critical cast, which
                // is the only one bringing this field.
                .VarIfNotZero(9, critico ? 1 : 0)
                .VarIfNotZero(10, valor)
                .VarIfNotZero(11, padre)
                .Msg(12, activacion >= 0
                    ? Pb.New().Var(2, activacion)
                    : Pb.New().Var(2, Nobody).Var(3, Nobody))
                .VarIfNotZero(13, cara)
                .Var(14, hechizo)
                .VarIfNotZero(15, familia)
                // Whether it can be dispelled, and how much: it is the effect's own dispellable minus one.
                .VarIfNotZero(16, Math.Max(0, dispellable - 1));

            // The inner one is WHO CARRIES IT and the outer one WHO PLACED IT. They are not the same except
            // when one buffs oneself, and that is why the bug was not seen with the first
            // capture measured. In the Cra's Ojo de Topo, which removes three range from the
            // enemy, the real server sends the pío inside and the player outside.
            return Pb.New()
                .Msg(1, Pb.New()
                    .Msg(1, dentro)
                    .Var(2, quien)
                    .Var(3, efecto))
                .Build();
        }

        /// <summary>
        /// A trigger as a jxm carries it: what an EON or an EOFF waits on and the mask of an EK stay
        /// behind -- "EON", "EOFF" and "EK" in all 581 of them in the class captures, never a state:
        /// Lazo Espiritual's "EON8" and "EOFF8" go out as "EON" and "EOFF" at frames 2114-2115 of
        /// the Osamodas capture, the Sram's "EK:m" as "EK".
        /// </summary>
        public static string WireTrigger(string trigger)
        {
            if (string.IsNullOrEmpty(trigger)) return "I";
            if (trigger.StartsWith("EOFF", StringComparison.Ordinal)) return "EOFF";
            if (trigger.StartsWith("EON", StringComparison.Ordinal)) return "EON";
            int colon = trigger.IndexOf(':');
            return colon > 0 ? trigger.Substring(0, colon) : trigger;
        }

        /// <summary>
        /// A buff drops (jya): <c>f1</c> from whom and <c>f2</c> the buff number, the same
        /// it was given in the jxm. One goes for each that expires.
        /// </summary>
        public static byte[] BuildBuffGone(long dequien, int numero)
            => Pb.New().Var(1, dequien).Var(2, numero).Build();

        /// <summary>
        /// The twin notice that a buff has dropped (jwe with f14 = 514):
        ///
        ///   f3: from whom    f23 { f1: the buff number, f5: from whom again }
        ///
        /// It goes immediately after the jya and with the same number. Measured in the Flecha
        /// Helada capture, where each recast removes the previous buff: jya {f1: who, f2: 6} and
        /// right after it this jwe with f23 { f1: 6, f5: who }.
        /// </summary>
        public static byte[] BuildBuffExpired(long dequien, int numero)
            => Pb.New()
                .Var(3, dequien)
                .Var(14, EmbrujoCaido)
                .Msg(23, Pb.New().Var(1, numero).Var(5, dequien))
                .Build();

        public const int EmbrujoCaido = 514;

        /// <summary>
        /// A spell's rows taken off by a 406 (jwe with f14 = 406):
        ///
        ///   f3: who cast the 406     f33 { f2: the spell whose rows went, f4: off whom }
        ///
        /// Behind the jya of each row. Measured on the Furor capture -- "jwe f3=53721170019
        /// f14=406 f33{f2=28604 f4=53721170019}" after jya 36, 37 and 38 -- and eleven times
        /// on Tempestad de Potencia's, on the enemies.
        /// </summary>
        /// <param name="grade">
        /// The grade a 1406 took off, in f3, with <paramref name="effect"/> 1406: "jwe 1406
        /// f33{f2=30842 f3=6 f4=-3}" in Aguja's capture. Zero for a 406, which takes every grade.
        /// </param>
        /// <param name="shown">
        /// The 406 row is visible in the fight log -- the bit 4 of its m_flags: an f5 of 1 then, as
        /// 17 of the 17 such removals of the class captures have it -- Resonancia's "jwe 406
        /// f33{f2=14611 f4=-1 f5=1}" at frame 223 -- and none of the 1,397 whose row lacks the bit.
        /// </param>
        public static byte[] BuildSpellEffectsRemoved(long author, int spell, long fromWhom, int grade = 0,
                                                      int effect = SpellEffectsRemoved, bool shown = false)
            => BuildAction(author, effect,
                           Pb.New().Var(2, spell).VarIfNotZero(3, grade).Var(4, fromWhom).VarIfNotZero(5, shown ? 1 : 0),
                           detailField: 33);

        /// <summary>The row's "visible in the fight log" bit, which puts an f5 of 1 on its 406: see BuildSpellEffectsRemoved.</summary>
        public const int ShownRowFlag = 4;

        public const int SpellEffectsRemoved = 406;

        /// <summary>
        /// Which sequence the client acknowledges (jti): <c>f2</c> carries the same action number it
        /// was closed with, that of the jwi's <c>f1</c>. Returns zero if it does not come.
        /// </summary>
        public static int ReadSequenceAck(byte[] payload)
        {
            byte[]? jti = ConnectionProtocol.ReadPayload(payload, Op.Jti);
            if (jti == null) return 0;

            foreach (var field in ProtoMessage.Parse(jti).Fields)
            {
                if (field.WireType == 0 && field.FieldNumber == 2) return (int)field.VarIntValue;
            }
            return 0;
        }

        /// <summary>
        /// What happens (jwe). <c>f14</c> says what it is about:
        ///
        ///   129  has walked, and f20 carries the steps spent as a negative
        ///   300  has cast something (303 if it is the weapon), with f7 saying what and where
        ///   102  has spent action points, again in f20 and as a negative
        ///   89 to 100  damage, with f40 saying to whom, how much and of what element
        ///   103  someone has died
        /// </summary>
        public static byte[] BuildAction(long author, int kind, Pb? detail = null,
                                         int detailField = 0)
        {
            var jwe = Pb.New().Var(3, author);
            if (detail != null && detailField != 0) jwe.Msg(detailField, detail);
            return jwe.Var(14, kind).Build();
        }

        /// <summary>
        /// What is planted on the ground (the f32 of a jwe with f14 = 401).
        ///
        ///   f1 { f1 { f1 { f2: the colour in RGB, f3: the cell }
        ///             f4: the glyph's number     f5: the size of the footprint
        ///             f6: the spell's grade      f9: the spell it casts
        ///             f10: the cell again        f11: 1
        ///             f12: whose it is } }
        /// </summary>
        /// <remarks>
        /// Measured in the Rogue captures: the wall spells come out 143 times and all 143 go
        /// inside a jwe f14 = 401, ONE PER CELL. Two in a row of the fire wall:
        ///
        /// <code>
        ///   f3=53721497699 f14=401 f32{f1{f1{f2=16711680 f3=260} f4=1 f5=2 f6=3
        ///                                 f9=13458 f10=260 f11=1 f12=53721497699}}
        ///   ... the next one the same with f3=274, f4=2 and f10=274
        /// </code>
        ///
        /// The colour 16711680 is 0xFF0000, pure red, and f6 is 3, which is the grade the
        /// wall hits with -- the only thing in all this that had already been guessed right.
        /// </remarks>
        public static byte[] BuildGlyph(long owner, int glyphId, int cell, int spell, int grade,
                                        int size, int colour)
            => BuildAction(owner, PlacedGlyph,
                Pb.New().Msg(1, Pb.New()
                    .Msg(1, Pb.New().Var(2, colour).Var(3, cell))
                    .Var(4, glyphId)
                    .Var(5, size)
                    .Var(6, grade)
                    .Var(9, spell)
                    .Var(10, cell)
                    .Var(11, 1)
                    .Var(12, owner)),
                detailField: 32);

        /// <summary>
        /// And how it is removed (the f22 of a jwe with f14 = 310): only the glyph's number.
        /// </summary>
        /// <remarks>
        /// Measured in the same capture as the 401, and it is as short as it looks: fifteen bytes with the
        /// owner in f3 and <c>f22{f1 = 1}</c>, and the next the same with 2. The numbers are the
        /// same ones the 401s handed out on placing them.
        ///
        /// Without this the wall stayed drawn forever: the server removed it from its list
        /// -- it is seen in the log, «se cae el glifo 4» -- and nobody told the client.
        /// </remarks>
        public static byte[] BuildGlyphGone(long owner, int glyphId)
            => BuildAction(owner, RemovedGlyph, Pb.New().Var(1, glyphId), detailField: 22);

        public const int RemovedGlyph = 310;

        /// <summary>
        /// A portal laid (the f32 of a jwe 401, as for any mark, with the f14 before it):
        ///
        ///   f1 { f1 { f2: 255, f3: the cell }     f2: the row's diceSide    f3: its diceNum
        ///        f4: the mark number   f5: 3   f6: the grade   f9: the spell that laid it
        ///        f10: the cell   f11: 1 when it is on   f12: whose it is }
        /// </summary>
        /// <remarks>
        /// Measured, byte for byte, on the 21 portals of the six Selatrop captures: "poner portales
        /// de selatrop" frame 109 is the first one, alone and off, with no f11; frame 131 the
        /// second, on. The dice go where a spell glyph carries the spell it casts and its grade --
        /// f3 the 2 of "+2% per cell", f2 the 44338 that is Teleportal's level. The colour is 255
        /// on all 21 and in no data: a constant here. The f5 is the kind of mark -- 2 on the bomb
        /// walls, none on the glyphs, 3 on every portal.
        /// </remarks>
        public static byte[] BuildPortal(long owner, int portalId, int cell, int diceNum, int diceSide,
                                         int grade, int layingSpell, bool active)
            => Pb.New()
                .Var(3, owner)
                .Var(14, PlacedGlyph)
                .Msg(32, Pb.New().Msg(1, Pb.New()
                    .Msg(1, Pb.New().Var(2, PortalColour).Var(3, cell))
                    .VarIfNotZero(2, diceSide)
                    .VarIfNotZero(3, diceNum)
                    .Var(4, portalId)
                    .Var(5, PortalMark)
                    .Var(6, grade)
                    .Var(9, layingSpell)
                    .Var(10, cell)
                    .VarIfNotZero(11, active ? 1 : 0)
                    .Var(12, owner)))
                .Build();

        /// <summary>The colour every portal of the captures carries: 0x0000FF.</summary>
        public const int PortalColour = 255;

        /// <summary>The kind of mark of a portal, the f5 of its jwe 401.</summary>
        public const int PortalMark = 3;

        /// <summary>
        /// A portal turned on or off (jwe 1181): f3 who does it, f17 { f1: the portal, f2: 1 when
        /// it is on }. 58 of them across the Selatrop captures; see Jondo.Unity.World.Fights.PortalNetwork
        /// for when each goes out.
        /// </summary>
        public static byte[] BuildPortalState(long author, int portalId, bool active)
            => Pb.New()
                .Var(3, author)
                .Var(14, PortalState)
                .Msg(17, Pb.New().Var(1, portalId).VarIfNotZero(2, active ? 1 : 0))
                .Build();

        public const int PortalState = 1181;

        /// <summary>
        /// A glyph a SPELL lays down -- a trap, a turn-start or turn-end glyph, an aura: the same
        /// jwe 401 as the bomb wall, with a body of its own.
        ///
        ///   f1 { f1 (repeated) { f2: colour, f3: cell }   one per cell of its footprint
        ///        f2: 1        f3: the spell it casts     f4: the glyph's number
        ///        f6: grade    f7: colour                  f9: the spell that laid it
        ///        f10: the aimed cell    f11: 1           f12: whose it is }
        /// </summary>
        /// <remarks>
        /// Measured on the 58 of them the captures hold outside the Rogue's walls: Feca, Anutrof,
        /// Ocra, the troll fair's. The Anutrof's 29575 lists its twelve cells, a ring of three, in
        /// the f1s. The colour is the placing row's <c>value</c> in RGB, in every one of them --
        /// 5718180 is Excursión's and 3222918 Caza's -- so it is the data's colour and not a
        /// choice: Conde Kontatrás's time glyph carries 0, black. What the f2 counts is not
        /// clear: 1 in most, 2 to 4 in some, 1 to 12 across the twelve glyphs of one cast of the
        /// troll fair. One is sent. The size of the bomb wall's f5 does not appear.
        /// </remarks>
        public static byte[] BuildSpellGlyph(long owner, int glyphId, IEnumerable<int> cells, int aimedCell,
                                             int castSpell, int layingSpell, int grade, int colour)
        {
            var body = Pb.New();
            foreach (int cell in cells) body.Msg(1, Pb.New().VarIfNotZero(2, colour).Var(3, cell));
            body.Var(2, 1)
                .Var(3, castSpell)
                .Var(4, glyphId)
                .Var(6, grade)
                .VarIfNotZero(7, colour)
                .Var(9, layingSpell)
                .Var(10, aimedCell)
                .Var(11, 1)
                .Var(12, owner);
            return BuildAction(owner, PlacedGlyph, Pb.New().Msg(1, body), detailField: 32);
        }

        /// <summary>
        /// And how one GOES OFF (a jwe with f14 = 306 or 307):
        ///
        ///   f3: whose glyph it is
        ///   f9 { f1: the cell, f2: who it caught, f4: the glyph number }
        ///
        /// 306 is walking into it and 307 is starting the turn on top of it. Same payload in both:
        /// 411 messages across the class captures and all 411 carry exactly f1, f2 and f4.
        /// </summary>
        /// <remarks>
        /// THIS IS WHAT WAS MISSING for the bomb wall to be seen hitting, and it was not the
        /// damage: the damage already went out. The real server does NOT announce the cast of a
        /// glyph spell -- the four wall spells appear 143 times in the Rogue captures and all 143
        /// sit inside an f14 = 401, not one inside an f14 = 300. What it sends is this, and the
        /// blow right behind it:
        ///
        /// <code>
        ///   jzc  f1=-1 f2=290 f7=1 f8=14           the turn of -1 begins
        ///   jto  f1=-1 f2=2                        opens the sequence, IN THE VICTIM NAME
        ///   jwe  f3=53721497699 f14=307 f9{f1=274 f2=-1 f4=6}
        ///   jwe  f3=53721497699 f14=99  f40{f2=-1 f3=44 f4=2 f5=4}
        ///   jwi  f1=3 f2=-1 f3=2                   and closes it
        /// </code>
        ///
        /// Counted: 292 of the 307 and 119 of the 306 across the class captures. The 307 follows a
        /// jzc in 199 of the 292, and the 306 follows a jwe f14 = 129 -- the movement points of
        /// walking -- or the 401s that have just raised a wall under somebody feet.
        ///
        /// 308 and 309 also show up in the captures and are NOT this: they carry no f9 and appear
        /// in the Eniripsa words. Left alone.
        /// </remarks>
        public static byte[] BuildGlyphTriggered(long owner, int glyphId, int cell, long victim,
                                                 bool walkedIn)
            => BuildAction(owner, walkedIn ? EnteredGlyph : StartedTurnOnGlyph,
                Pb.New().Var(1, cell).Var(2, victim).Var(4, glyphId),
                detailField: 9);

        public const int EnteredGlyph = 306;
        public const int StartedTurnOnGlyph = 307;

        /// <summary>
        /// The sequence the real server puts a turn-start glyph trigger in: a jto with f2 = 2,
        /// opened in the name of whoever is standing on it, not of the glyph owner.
        /// </summary>
        public const int GlyphSequence = 2;

        /// <summary>The pure red the bomb wall comes out with.</summary>
        public const int GlyphRed = 16711680;

        public const int PlacedGlyph = 401;
        public const int Walked = 129;
        public const int Cast = 300;
        public const int WeaponCast = 303;
        public const int SpentActionPoints = 102;
        public const int Died = 103;
        public const int LookChanged = 149;

        /// <summary>
        /// The 3793 script marker going off (jwe, f14 = 3793): f3 who cast the spell, f25 { f2:
        /// the grade, f3: the cell it lands on, f4: the spell, f5: the marker's value }. The
        /// shape of 187 of the 202 in the class captures; the other 15 carry one more field
        /// that is not read. Remisión sends it on the attacker's cell when its push goes off,
        /// Paso de Cacería on the cell of the cast the turn after.
        /// </summary>
        public const int ScriptMarker = 3793;

        public static byte[] BuildScriptMarker(long author, int grade, int cell, int spell, int value)
            => Pb.New()
                .Var(3, author)
                .Var(14, ScriptMarker)
                .Msg(25, Pb.New().Var(2, grade).Var(3, cell).Var(4, spell).Var(5, value))
                .Build();

        /// <summary>The field where the detail of each thing goes inside the jwe.</summary>
        public const int CastDetail = 7;
        public const int PointsDetail = 20;
        public const int DamageDetail = 40;

        /// <summary>
        /// Changes a fighter's look (jwe, f14 = 149). This fight action is the one that
        /// makes the client animate the transformation; a jsn actor refresh only redraws it.
        /// </summary>
        public static byte[] BuildLookChanged(long fighter, byte[] look)
            => Pb.New()
                .Var(3, fighter)
                .Var(14, LookChanged)
                .Msg(26, Pb.New().Var(1, fighter).Bytes(3, look))
                .Build();

        /// <summary>Copies an EntityLook replacing only its root's bones.</summary>
        public static byte[] WithRootBones(byte[] look, int bones)
        {
            if (look == null || look.Length == 0 || bones <= 0) return look ?? Array.Empty<byte>();

            var parsed = ProtoMessage.Parse(look);
            var field = parsed.Fields.Find(f => f.FieldNumber == 3 && f.WireType == 0);
            if (field != null) field.VarIntValue = bones;
            else parsed.Fields.Add(new ProtoField
            {
                FieldNumber = 3,
                WireType = 0,
                VarIntValue = bones,
            });
            return parsed.ToByteArray();
        }

        /// <summary>
        /// The same look with a different SCALE (the packed repeated f5 of the look root).
        /// </summary>
        /// <remarks>
        /// This is how a bomb grows. Measured on the look change of a bomb climbing its combo:
        /// the whole message is <c>f26 { f1 = -5, f3 { f2 = 3, f3 = 1562, f5 = 69 } }</c> and the
        /// only thing that ever moves between one rung and the next is that last byte -- 0x69 is
        /// 105, then 110, 125, 130... The field is length-delimited holding one varint, which is
        /// how protobuf packs a <c>repeated int32</c> of one element.
        /// </remarks>
        public static byte[] WithScale(byte[] look, int scale)
        {
            if (look == null || look.Length == 0 || scale <= 0) return look ?? Array.Empty<byte>();

            var packed = Pb.New().Var(1, scale).Build();
            // Pb writes a tag; the packed payload is the varint alone, so drop the tag byte.
            packed = packed[1..];

            var parsed = ProtoMessage.Parse(look);
            var field = parsed.Fields.Find(f => f.FieldNumber == 5 && f.WireType == 2);
            if (field != null) field.BytesValue = packed;
            else parsed.Fields.Add(new ProtoField
            {
                FieldNumber = 5,
                WireType = 2,
                BytesValue = packed,
            });
            return parsed.ToByteArray();
        }

        /// <summary>
        /// A bomb announcing that it cast a combo spell on itself (a jwe with f14 = 300).
        /// </summary>
        /// <remarks>
        /// Nothing about the combo showed up on screen, and this was why. The server climbed the
        /// ladder and told the client about the state -- our jxm is byte for byte the real one --
        /// but never announced the CAST, and the client redraws the bomb off the cast, not off the
        /// buff. Measured in "tymador-explobomba resiliente", frames 262 and 264, one rung apart:
        ///
        /// <code>
        ///   18 fbffffffffffffffff01           f3  = -5, the bomb
        ///   3a 2e                             f7
        ///      10 fbffffffffffffffff01        f2  = -5, itself
        ///      22 0b 20 fbffffffffffffffff01  f4 { f4 = -5 }
        ///      22 07 20 e380b490c801          f4 { f4 = the Rogue }
        ///      30 d801                        f6  = 216, its cell
        ///      3a 08 10 91a001 18 d3a603      f7 { f2 = 20497, f3 = 54099 }
        ///   70 ac02                           f14 = 300
        /// </code>
        ///
        /// TWO f4 and no f8, which is why this does not go through <see cref="CastAt"/>: that one
        /// writes a single f4 and closes with f8 = 1, and the bytes would not match. The pair of
        /// f4 is the bomb and its summoner, both of them.
        ///
        /// One of these goes out per combo granted, naming 20497; and when the rung actually
        /// moves, a second one right behind naming the grade of 20500 that pays for it.
        /// </remarks>
        public static byte[] BuildComboCast(long bomb, long owner, int cell, int spell, int levelId)
            => Pb.New()
                .Var(3, bomb)
                .Msg(7, Pb.New()
                    .Var(2, bomb)
                    .Msg(4, Pb.New().Var(4, bomb))
                    .Msg(4, Pb.New().Var(4, owner))
                    .Var(6, cell)
                    .Msg(7, Pb.New().Var(2, spell).Var(3, levelId)))
                .Var(14, Cast)
                .Build();

        /// <summary>The points spent, as a negative, as the real server sends them.</summary>
        public static Pb Spent(long fighterId, int amount)
            => Pb.New().Var(1, -amount).Var(2, fighterId);

        /// <summary>
        /// What was cast and where: the f7 of the jwe with f14 = 300 (or 303 if it is the weapon).
        ///
        ///   f2: at whom            f4 { f4: who casts it }
        ///   f5: 1 if critical      f6: the cell
        ///   f7 { f2: the spell, f3: that spell's level }
        ///   f8: 1
        ///
        /// The spell goes in f7, IN TWO NUMBERS, and not in f8. The latter is what was done
        /// here and that is why the client drew a punch instead of the spell: it received a
        /// cast without saying of what, and the punch is what it falls back on when it does not know. The
        /// f8 is always 1 in the captures, it is not the spell.
        ///
        /// The two numbers of f7 come from the base as is: f2 is SpellTemplates.Id and f3 is
        /// the SpellLevels.Id of its grade. Checked against five casts of the level 50
        /// poutch capture: (25188, 63926), (21976, 57060), (18647, 51206), (12718, 43038) and
        /// (6828, 28035); in the base, SpellLevels.Id 63926 belongs to spell 25188, and so on for all five.
        ///
        /// The weapon hit carries no f7: it carries an f10 with the weapon and that is it.
        /// </summary>
        /// <param name="sobreEseObjetivo">
        /// How many times it has been cast on that target. It only travels if the spell has a per-target
        /// cap.
        /// </param>
        /// <param name="esteTurno">
        /// How many this turn. Only if the spell has a per-turn cap.
        /// </param>
        /// <param name="intervalo">
        /// The waiting rounds just set. It is the grade's <c>MinCastInterval</c>, and
        /// it is measured: Agudeza Absoluta sends a 4 and its column is 4; Represalias a 3 and it is
        /// 3; Paso de Cacería, Disparos Lejanos, Tiros Potentes and Flecha de Expiación send a 2
        /// and are 2 at the grade the capture's character plays.
        /// </param>
        /// <param name="noTarget">
        /// A cast on an empty cell names nobody: no f2 at all, where a target of zero otherwise
        /// stands for the caster himself. "poner portales de selatrop", frames 104 to 352, and the
        /// Osamodas' teleport at frame 1906 -- every cast aimed at an empty cell of the captures.
        /// </param>
        /// <param name="chained">
        /// A spell another one set off (792, 1160...): no f8. See FightHandler.AnunciarElEncadenadoAsync.
        /// </param>
        /// <param name="portals">
        /// The portals a cast went through, the one aimed at first, packed in the spell's f1; the
        /// cell is then where it landed. "jwe 300 f6=344 f7{f1=[10,9,8,7] f2=14593}" for Audacia
        /// aimed at the portal on 303, "pegar a traves de diferentes portales", frame 16.
        /// </param>
        public static Pb CastAt(long caster, long target, int cell, int spell, int spellLevel,
                                bool critical, int sobreEseObjetivo = 0, int esteTurno = 0,
                                int intervalo = 0, int arma = 0, bool noTarget = false,
                                IReadOnlyList<int> portals = null, bool chained = false)
        {
            var suyo = Pb.New();
            if (sobreEseObjetivo > 0 && target != 0)
            {
                suyo.Msg(1, Pb.New().Var(1, sobreEseObjetivo).Var(2, target));
            }
            suyo.VarIfNotZero(2, esteTurno)
                .VarIfNotZero(3, intervalo)
                .Var(4, caster);

            var detalle = Pb.New();
            if (target != 0 || !noTarget) detalle.Var(2, target != 0 ? target : caster);
            detalle.Msg(4, suyo)
                .VarIfNotZero(5, critical ? 1 : 0)
                .Var(6, cell);
            if (spell != 0)
            {
                // A SPELL carries the spell and does NOT carry the weapon field. Writing it even
                // at zero changed the bytes, and the protocol self-test caught it at the
                // first go comparing against the capture: that is why the if wraps both.
                var delHechizo = Pb.New();
                if (portals != null && portals.Count > 0) delHechizo.Packed(1, portals.Select(p => (long)p));
                detalle.Msg(7, delHechizo.Var(2, spell).VarIfNotZero(3, spellLevel));
                // The f8 is a cast somebody made; a chained one goes without it.
                return chained ? detalle : detalle.Var(8, 1);
            }

            // And a MELEE hit carries the opposite: no spell, and with the weapon.
            //
            // It is the only thing telling a sword blow from a punch, and that is why the chat said
            // «Puñetazo» on attacking with the sword. Sending spell 0 was right —the real server
            // does not send any weapon spell either—; what was missing was this.
            //
            // f10 carries the ItemTemplates Id of the equipped weapon, and the punch is the same
            // message with f10 at a WRITTEN ZERO, not absent: that is why it goes with Var and not with
            // VarIfNotZero. Measured in the captures: Lavacha 19593, Cocobur 20353, Garras de la
            // Despedazadora 31759, Garra de Gargandias 31786; and the punch, «5000» on the wire,
            // which is field 10's tag followed by a zero.
            return detalle.Var(8, 1).Var(10, arma);
        }

        /// <summary>
        /// One's sheet, to refresh it on its own (jxw).
        ///
        ///   f1: who        f3 { f3: 2, f5 x N: the characteristics }
        ///
        /// It is the same sheet that goes inside the jxg and the jxb, alone here. It is used to update the
        /// movement and action points as they are spent.
        /// </summary>
        /// <summary>The two characteristics that go in the points mould, and only them.</summary>
        private const int PuntosDeAccion = 1;
        private const int PuntosDeMovimiento = 23;

        public static byte[] BuildFighterSheet(long fighterId,
                                               IEnumerable<(int Characteristic, long Base,
                                                            long Gear, long Buff)> sheet,
                                               bool esElPersonajeControlado)
        {
            // A jxw ENTRY REPLACES THE jxb ONE, IT IS NOT ADDED TO IT. Everything follows from that.
            //
            // Here an ABSOLUTE VALUE was sent put in the base slot, and with that each
            // refresh erased the equipment and the rest of the slots the jxb had sent right. The
            // real server does the opposite: it writes the COMPLETE entry again —the same
            // fields as in the jxb, repeated even if they have not changed— and adds the buff in its
            // own slot, f8.
            //
            // Measured in the Zobal capture, on the same fighter and the same characteristics:
            //
            //   jxb   107: f4 { f2: 100 }          25: f4 { f7: 740 }
            //   jxw   107: f4 { f2: 100, f8: +1 }  25: f4 { f7: 740, f8: +100 }
            //   jxw   107: f4 { f2: 100 }          25: f4 { f7: 740 }      on the buff expiring
            //
            // The base's 100 is NOT touched in any of the 1,699 detailed entries of the
            // captures. We sent «f4 { f2: 10 }» —only the buff, and in the wrong
            // slot— fifty-five milliseconds after having sent the good 100. And 107 is
            // a damage MULTIPLIER: the client estimates the hit by multiplying by it, so
            // leaving it at 10 where it is 100 is the preview divided by ten. That was the
            // bug seen while playing.
            //
            // The points mould —f5— is still only for 1 and 23; no other
            // characteristic ever uses it in the captures.
            var stats = Pb.New().Var(3, SheetKind);
            foreach (var (characteristic, baseValue, gear, buff) in sheet)
            {
                stats.Msg(5, SheetEntry(characteristic, baseValue, gear,
                                        isMonster: !esElPersonajeControlado, delEmbrujo: buff));
            }
            return Pb.New().Var(1, fighterId).Msg(3, stats).Build();
        }

        /// <summary>
        /// The sheet with the life the character is missing (jxw with characteristic 97).
        ///
        /// It goes apart from <see cref="BuildFighterSheet"/> because that one only knows how to write the
        /// points mould —f5 { f1 }— and 97 uses its own. It is only sent to the character the
        /// client controls; see <see cref="TemporaryLifeMalus"/>.
        ///
        ///   08a28280c8e708 1a1e 1802 2a1a 0861 2216 1098ffffffffffffffff01 40b0feffffffffffffff01
        ///   = to the player, he is missing 104 life and carries 208 eroded
        /// </summary>
        public static byte[] BuildLifeSheet(long fighterId, long deficit, long erosion)
            => Pb.New()
                .Var(1, fighterId)
                .Msg(3, Pb.New()
                    .Var(3, SheetKind)
                    .Msg(5, SheetEntry(TemporaryLifeMalus, deficit, -Math.Abs(erosion), false)))
                .Build();

        /// <summary>The walking sequence and that of any action.</summary>
        public const int WalkSequence = 4;
        public const int ActionSequence = 3;

        /// <summary>
        /// The short sequence the real server wraps each loose sheet in: in the capture,
        /// around all the jxw there is a jto with f2 = 3 and its jwi with f3 = 3.
        /// </summary>
        public const int SheetSequence = 3;

        /// <summary>
        /// The sequence each turn starts with, that of giving back the points: in the capture,
        /// after the jzc goes a jto with f2 = 7 that wraps the two sheets —first the movement
        /// points, then the action ones— and is closed with a jwi of f3 = 7.
        /// </summary>
        public const int TurnSequence = 7;

        /// <summary>
        /// A hit (jwe with f14 between 89 and 100).
        ///
        ///   f3: who hits        f14: of what element
        ///   f40 { f2: at whom, f3: how much }
        ///
        /// Measured: with f14 = 91 f40 carries { -1, 134 } and with f14 = 93, { -1, 121 }. f40 also has
        /// an f4 and an f5 that change from one hit to another and are not deciphered; they are left
        /// out, because what the client draws —at whom and how much— is there.
        /// </summary>
        /// <param name="efecto">
        /// The hit's EFFECT NUMBER, which is what goes in f14: 91 is water steal, 96
        /// water damage, 99 fire damage. It is not a separate element code.
        /// </param>
        /// <param name="elemento">
        /// The element, which goes in the detail's f4. Measured: water hits carry a 3 there and
        /// earth ones a 1, the same numbers as the catalogue's ElementId column.
        /// </param>
        public static byte[] BuildDamage(long author, int efecto, long victim, int amount,
                                         int elemento = -1, int erosion = 0)
        {
            // No amount is no field: the blow on an invulnerable target travels as f40 with the
            // victim and the element only (Influencia's capture), the way proto3 leaves a zero.
            var detalle = Pb.New().Var(2, victim).VarIfNotZero(3, amount);
            if (elemento >= 0) detalle.Var(4, elemento);

            // The EROSION, which was missing. It goes in f5 and is what the hit takes from the life CAP,
            // not from the current life. It appears in 977 of the 986 damage blocks of the captures, and in 727
            // of them it is exactly a tenth of the damage:
            //
            //   c2020e 10a28280c8e708 18ce03 2003 282e   =  462 damage, 46 erosion
            //
            // The server already computed it —it is in Fighter.Erosionar— and did not send it, so the
            // client never found out that the cap had gone down.
            detalle.VarIfNotZero(5, erosion);

            return Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(40, detalle)
                .Build();
        }

        /// <summary>
        /// The effect number of the COLLISION DAMAGE on pushing.
        ///
        /// In the client's catalogue it is called <c>CharacterLifePointsLostFromPush</c>, it has an
        /// empty description in all five languages and not one of the 34,685 spell levels uses it:
        /// no spell writes it, the engine makes it. The end-of-fight screen
        /// counts it separately, on its own line.
        /// </summary>
        public const int PushDamage = 80;

        /// <summary>
        /// The damage of having collided on being pushed (jwe with f14 at 80).
        ///
        ///   f3: who pushed        f14: 80
        ///   f40 { f2: at whom, f3: the life lost, f4: -1, f5: the erosion }
        ///
        /// It goes apart from <see cref="BuildDamage"/> because that one has the convention «if the element
        /// is less than zero, do not write f4», and here f4 has to go AND be MINUS ONE: it is
        /// so in the 127 messages of the 401 captures, without an exception. Minus one means
        /// «no element», which is not the same as neutral's zero.
        ///
        /// It is sent within the same sequence as the cast and right after the displacement;
        /// and when the pushed one has not a single cell left, the displacement is not sent and this one goes
        /// alone.
        /// </summary>
        public static byte[] BuildPushDamage(long author, long victim, int amount, int erosion = 0)
            => Pb.New()
                .Var(3, author)
                .Var(14, PushDamage)
                .Msg(40, Pb.New()
                    .Var(2, victim)
                    .Var(3, amount)
                    .Var(4, -1)
                    .VarIfNotZero(5, erosion))
                .Build();

        /// <summary>
        /// REMOVING action points from someone else. Not to be confused with 102, which is one's own cost of
        /// casting a spell: in the 1,796 samples of the captures, 102 and 129 ALWAYS carry
        /// the same id as author and as victim, and here the author is someone else.
        /// </summary>
        public const int ActionPointsLost = 101;

        /// <summary>Removing movement points from someone else. 129 is walking, which is one's own business.</summary>
        public const int MovementPointsLost = 127;

        /// <summary>
        /// Points of a removal the target DODGED (jwe 308 for AP, 309 for MP): { f3: who cast,
        /// f28 { f1: how many, f3: who dodged } }. The shape of all 401 in the class captures
        /// -- 157 of AP, 244 of MP -- and always before the sheet and the row of what did land,
        /// when anything did: "jwe 309 f28{f1=1 f3=-5}", then the -5 sheet at two MP, then the
        /// jxm 169 with f1=1 of a Palabra Juguetona that asked for two.
        /// </summary>
        public const int ActionPointsDodged = 308;
        public const int MovementPointsDodged = 309;

        public static byte[] BuildPointsDodged(long author, int characteristic, long quien, int cuantos)
            => Pb.New()
                .Var(3, author)
                .Var(14, characteristic == 1 ? ActionPointsDodged : MovementPointsDodged)
                .Msg(28, Pb.New().Var(1, cuantos).Var(3, quien))
                .Build();

        /// <summary>
        /// Points given (jwe 120, "devuelve N PA"): the same f20 as a loss with the amount
        /// positive. "18..70 78 a201 09 0801 10.." at frame 11 of "usar neutral en portales".
        /// </summary>
        public static byte[] BuildPointsGiven(long author, int efecto, long quien, int cuantos)
            => Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(20, Pb.New().Var(1, Math.Abs(cuantos)).Var(2, quien))
                .Build();

        /// <summary>
        /// Points have been removed from someone (jwe): { f3: who, f14: which, f20 { f1: how many,
        /// f2: from whom } }.
        ///
        /// The quantity goes as a NEGATIVE, in 64-bit two's complement:
        ///
        ///   a20112 08fcffffffffffffffff01 10a282f0a6c408   =  minus four AP
        ///
        /// This is what brings up the little number floating over the fighter, just like with
        /// life. Without it, the server removed the points internally and on screen nothing
        /// moved: the player saw the creature run out of AP without anything telling him.
        /// </summary>
        public static byte[] BuildPointsLost(long author, int efecto, long victim, int cuantos)
            => Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(20, Pb.New().Var(1, -Math.Abs(cuantos)).Var(2, victim))
                .Build();

        /// <summary>
        /// The two element codes that are measured. The rest fall in the 89 to 100 range but
        /// which is which could not be worked out, so 91 is used meanwhile.
        /// </summary>
        public const int SomeDamage = 91;
        public const int OtherDamage = 93;

        /// <summary>
        /// A heal (jwe with f14 = 3001, "neutral heals"):
        ///
        ///   f3: who heals      f6 { f1: how much, f4: WHOM }
        ///
        /// f4 is the HEALED one, and this corrects what was said here before. The earlier comment
        /// held that the minus two of the Beacon capture was none of the three
        /// fighters and therefore not the recipient; it was false, minus two is the id
        /// of a monster in that fight. Counting the 94 heals of the 305 captures: f4
        /// always carries a real fighter identifier —the player 52 times, other
        /// players 19, monsters the rest— and in 40 of the 94 it does NOT match the healer.
        ///
        /// Sending it nailed to minus two made every heal be drawn on top of fighter
        /// minus two, which in most fights exists and is some random creature.
        ///
        /// The heal reaches the wire already resolved into points: in the base the effect is 1109,
        /// "Cura: #1% de los PdV máximos", and here the concrete number travels. It is the same workaround as
        /// with the point steal, where 1080 is announced as 169 with the quantity that came out.
        /// </summary>
        public static byte[] BuildHeal(long author, int cuanto, long curado)
            => Pb.New()
                .Var(3, author)
                .Msg(6, Pb.New().Var(1, cuanto).Var(4, curado))
                .Var(14, Curacion)
                .Build();

        public const int Curacion = 3001;

        /// <summary>
        /// A summon comes out onto the board (jwe with f14 = 181, "Invoca: #1").
        ///
        /// The effect does not carry a number: it carries A WHOLE FIGHTER, with three f1 wrappers
        /// on top. Measured byte by byte against the Cra's Baliza de Supervivencia:
        ///
        ///   f1 { f1 { f1 {
        ///     f1 { f3: 1, f4 { f1 { f1: cell, f2: orientation, f4: 0 }, f3: who it is } }
        ///     f2: 0                          the identifier slot, zero as in a monster
        ///     f3 { f2: 3, f3: the LOOK template }
        ///     f5 { f3 { f2: the CREATURE template, f3: its grade } }
        ///     f6 { f1: whose it is, f3: 2, f4: 1, f5 x N: the sheet }
        ///   } } }
        ///
        /// The two templates are not the same: the beacon is 8348 and its look comes from 8152,
        /// because in the creature table 8348's Look is literally "{8152}".
        ///
        /// The sheet goes with the monsters' mould, <c>f2 { f2: value }</c>, which is the one
        /// <see cref="SheetEntry"/> already builds with <c>isMonster</c>.
        /// </summary>
        /// <param name="efecto">
        /// The effect that summoned it, which is the f14: 181 for an ordinary summon, 1008 for
        /// a bomb, 1011 for one the owner plays. Every summon went out as 181 until now.
        /// </param>
        public static byte[] BuildSummon(long quienInvoca, long quienEs, int celda, int orientacion,
                                         int plantillaDelAspecto, int plantillaDelBicho, int grado,
                                         IEnumerable<(int Characteristic, long Base, long Gear)> ficha,
                                         int efecto = Invoca)
        {
            var stats = Pb.New()
                .Var(1, quienInvoca)
                .Var(3, SheetKind)
                .Var(4, 1);
            foreach (var (caracteristica, valor, equipo) in ficha)
            {
                stats.Msg(5, SheetEntry(caracteristica, valor, equipo, isMonster: true));
            }

            var cuerpo = Pb.New()
                .Msg(1, Pb.New()
                    .Var(3, 1)
                    .Msg(4, Pb.New()
                        .Msg(1, Pb.New().Var(1, celda).VarIfNotZero(2, orientacion).Var(4, 0))
                        .Var(3, quienEs)))
                .Var(2, 0)
                .Msg(3, Pb.New().Var(2, 3).Var(3, plantillaDelAspecto))
                .Msg(5, Pb.New().Msg(3, Pb.New().Var(2, plantillaDelBicho).Var(3, grado)))
                .Msg(6, stats);

            return Pb.New()
                .Msg(1, Pb.New().Msg(1, Pb.New().Msg(1, cuerpo)))
                .Var(3, quienInvoca)
                .Var(14, efecto)
                .Build();
        }

        /// <summary>The effect number of "Invoca: #1" in the catalogue.</summary>
        public const int Invoca = 181;

        /// <summary>
        /// A double of a character comes out (jwe 180): the summon's block, with the character's
        /// look where a monster's names its template and his name and level where a monster
        /// names template and grade --
        ///
        ///   f3 { the look, as the character's own fighter block carries it }
        ///   f5 { f2 { f1: name, f2: level } }
        ///
        /// -- and the sheet in the monsters' mould, as for any summon. Frame 16 of "sram-doble":
        /// the Sram's look and "KTAS5625" at 200, on 258 facing 5, as -4.
        /// </summary>
        public static byte[] BuildDouble(long quienInvoca, long quienEs, int celda, int orientacion,
                                         byte[] look, string nombre, int nivel,
                                         IEnumerable<(int Characteristic, long Base, long Gear)> ficha)
        {
            var stats = Pb.New()
                .Var(1, quienInvoca)
                .Var(3, SheetKind)
                .Var(4, 1);
            foreach (var (caracteristica, valor, equipo) in ficha)
            {
                stats.Msg(5, SheetEntry(caracteristica, valor, equipo, isMonster: true));
            }

            var cuerpo = Pb.New()
                .Msg(1, Pb.New()
                    .Var(3, 1)
                    .Msg(4, Pb.New()
                        .Msg(1, Pb.New().Var(1, celda).VarIfNotZero(2, orientacion).Var(4, 0))
                        .Var(3, quienEs)))
                .Var(2, 0)
                .Bytes(3, look ?? Array.Empty<byte>())
                .Msg(5, Pb.New().Msg(2, Pb.New().Str(1, nombre ?? "").Var(2, nivel)))
                .Msg(6, stats);

            return Pb.New()
                .Msg(1, Pb.New().Msg(1, Pb.New().Msg(1, cuerpo)))
                .Var(3, quienInvoca)
                .Var(14, InvocaUnDoble)
                .Build();
        }

        /// <summary>"Invoca un doble del lanzador".</summary>
        public const int InvocaUnDoble = 180;

        /// <summary>
        /// Someone is moved without walking (jwe with f14 at the effect's number):
        ///
        ///   f3: who causes it      f14: 5 if pushing, 6 if pulling
        ///   f38 { f1: from which cell, f2: whom, f3: to which }
        ///
        /// Measured over the 76 displacements of the Cra captures. <c>f14</c> is not an
        /// engine code: it is the catalogue's effect number as is, the same 5 as
        /// "Empuja #1 casilla".
        /// </summary>
        /// <summary>The two numbers a displacement travels with.</summary>
        public const int Alejarse = 5;
        public const int Acercarse = 6;

        public static byte[] BuildDisplacement(long author, int efecto, long quien,
                                               int desde, int hasta)
            => Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(38, Pb.New().Var(1, desde).Var(2, quien).Var(3, hasta))
                .Build();

        /// <summary>
        /// A teleport (jwe 4): f3 who does it, f35 { f1: where, f2: who lands }. Every one of the
        /// 581 teleports in the captures travels like this and not as a 5 with from and to,
        /// which is what we sent for them until now.
        /// </summary>
        public static byte[] BuildTeleport(long author, long quien, int hasta)
            => Pb.New()
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.Teleport)
                .Msg(35, Pb.New().Var(1, hasta).Var(2, quien))
                .Build();

        /// <summary>
        /// Two fighters swap places (jwe 8): f2 { f1: the caster's old cell, f2: the other, f3:
        /// the other's old cell }, f3 the caster. One frame for the two of them: Jugarreta from
        /// 260 onto the bomb at 341 is "121108840210f5…0118d502 18… 7008".
        /// </summary>
        public static byte[] BuildSwap(long author, int deDondeElAutor, long otro, int deDondeElOtro)
            => Pb.New()
                .Msg(2, Pb.New().Var(1, deDondeElAutor).Var(2, otro).Var(3, deDondeElOtro))
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.SwapPositions)
                .Build();

        /// <summary>The visibility switch (jwe 150): f34 { f1: state, f4: who }. 1 as the illusions appear, 2 as they go.</summary>
        public static byte[] BuildVisibility(long author, long quien, int state)
            => Pb.New()
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.Visibility)
                .Msg(34, Pb.New().Var(1, state).Var(4, quien))
                .Build();

        public const int Hidden = 1;
        public const int Visible = 2;

        /// <summary>
        /// An illusion appears (jwe 1097): a fighter block of the copy, with its cell, a sheet of
        /// its own in the monster mould (f2 {f2 = value}), a pointer to the original at the cell he LEFT, and the original's look.
        /// </summary>
        /// <remarks>
        /// The block is the jxg's, with the copy's id in f3 and, inside the fighter, no id and no
        /// identity: only the sheet (f2) and the f7 "again" whose disposition is the ORIGINAL's --
        /// 230, facing 3 -- and whose f3 is the original's id. Measured on the three copies of
        /// the capture, byte for byte.
        /// </remarks>
        /// <param name="identity">
        /// Who the copy claims to be, for the side that must not tell it apart. The captured
        /// block -- the Tymador's own client -- carries no identity and a monster's mould of a
        /// sheet, and the client names nothing on hovering such a copy while it names the
        /// original: enough of a tell for an enemy. What the enemy's client is sent is not
        /// measured, so it gets the copy dressed as the person -- his identity, his own sheet
        /// with his life as it stands, his look -- and the copy's own id where the person's
        /// would go. Null keeps the captured shape.
        /// </param>
        public static byte[] BuildIllusion(long author, long illusionId, int cell, int orientation,
                                           int originalCell, int originalOrientation,
                                           IEnumerable<(int Characteristic, long Base, long Gear)> sheet,
                                           byte[] look, Pb identity = null)
        {
            var stats = Pb.New().Var(3, SheetKind);
            foreach (var (characteristic, baseValue, gear) in sheet)
            {
                stats.Msg(5, SheetEntry(characteristic, baseValue, gear, isMonster: identity == null));
            }
            var original = Pb.New()
                .Var(3, 1)
                .Msg(4, Pb.New()
                    .Msg(1, Pb.New().Var(1, originalCell).VarIfNotZero(2, originalOrientation).Var(4, 0))
                    .Var(3, author));
            var fighter = Pb.New();
            if (identity != null) fighter.Var(1, illusionId);
            fighter.Msg(2, stats);
            if (identity != null) fighter.Msg(6, identity);
            fighter.Msg(7, original);
            var block = Pb.New()
                .Msg(1, Pb.New().Var(1, cell).VarIfNotZero(2, orientation).Var(4, 0))
                .Msg(2, Pb.New().Msg(2, fighter).Bytes(3, look))
                .Var(3, illusionId);
            return Pb.New()
                .Msg(1, Pb.New().Msg(2, Pb.New().Msg(2, block)))
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.Illusions)
                .Build();
        }

        /// <summary>An illusion goes (jwe 1029): f3 its owner, f10 { f1: which }.</summary>
        public static byte[] BuildIllusionGone(long author, long illusionId)
            => Pb.New()
                .Var(3, author)
                .Msg(10, Pb.New().Var(1, illusionId))
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.IllusionGone)
                .Build();

        /// <summary>
        /// The sheet of an illusion, as the three of the capture carry it: 5 AP, 4 MP, the life
        /// of the level, a hundred in the five elements and in the multipliers, zero elsewhere.
        /// Not the original's numbers -- his are 7 AP and 3 MP -- so it is what the client
        /// draws for a copy, and the copy never uses it.
        /// </summary>
        public static IEnumerable<(int Characteristic, long Base, long Gear)> IllusionSheet(int level)
        {
            int[] order = { 1, 23, 37, 33, 35, 36, 34, 58, 54, 56, 57, 55, 85, 87, 101, 27, 28, 93, 79, 78, 0,
                            10, 11, 13, 14, 15, 16, 18, 19, 25, 26, 50, 75, 88, 89, 90, 91, 92, 95, 96, 97, 102,
                            107, 150, 120, 121, 122, 123, 124, 125, 141, 142, 143 };
            foreach (int c in order)
            {
                long value = c switch
                {
                    1 => 5,
                    23 => 4,
                    0 => 50 + 5 * Math.Max(1, level),
                    10 or 11 or 13 or 14 or 15 => 100,
                    19 or 26 => 1,
                    107 or 150 or 120 or 121 or 122 or 123 or 124 or 125 or 141 or 142 or 143 => 100,
                    27 or 28 or 79 or 78 or 75 => 10,
                    93 => 3,
                    _ => 0,
                };
                yield return (c, value, 0);
            }
        }

        /// <summary>The sequence the illusions go in at the owner's turn start: jto 6 in the capture.</summary>
        public const int TurnStartSequence = 6;

        /// <summary>
        /// Somebody is picked up (jwe 50): f3 who carries, f18 { f1: the cell he was on, f3: who }.
        /// Byte for byte the Pinzas of the Tymobot capture: "18f0ff…01 7032 92010e 089102 18f3ff…01".
        /// </summary>
        public static byte[] BuildCarry(long author, int desde, long quien)
            => Pb.New()
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.Carry)
                .Msg(18, Pb.New().Var(1, desde).Var(3, quien))
                .Build();

        /// <summary>
        /// Somebody is thrown (jwe 51): f3 who throws, f27 { f1: who, f2: where he lands }.
        /// </summary>
        public static byte[] BuildThrow(long author, long quien, int hasta)
            => Pb.New()
                .Var(3, author)
                .Var(14, Jondo.Unity.World.Combat.EffectSupport.Throw)
                .Msg(27, Pb.New().Var(1, quien).Var(2, hasta))
                .Build();

        /// <summary>
        /// Someone has died (jwe with f14 = 103): <c>f4 { f1: who }</c>.
        /// </summary>
        public static byte[] BuildDeath(long author, long victim)
            => Pb.New()
                .Var(3, author)
                .Msg(4, Pb.New().Var(1, victim))
                .Var(14, Died)
                .Build();

        // ─── It is over ─────────────────────────────────────────────────────────

        /// <summary>The fight is over (kuf). It goes empty.</summary>
        public static byte[] BuildFightOver() => Array.Empty<byte>();

        /// <summary>
        /// How it ended (jyg), one per fighter.
        ///
        ///   f2 (repeated) { f3 { f1: who, f3: 1 if alive }, f4: the result }
        ///
        /// The real one carries quite a lot more inside —the level, the experience, the account— and of that
        /// only the wrapper is deciphered, so here goes the minimum with which the client can
        /// close the fight and return the player to the map. The rewards panel will stay poor
        /// until it is measured whole.
        /// </summary>
        /// <summary>What one takes from the fight.</summary>
        public sealed class Spoils
        {
            public long Kamas { get; set; }
            public List<(int Quantity, int Gid)> Items { get; } = new List<(int, int)>();
        }

        /// <summary>How one ends the fight: a row of the end-of-fight screen.</summary>
        public sealed class FightResult
        {
            public long Fighter { get; set; }
            public bool Winner { get; set; }

            /// <summary>His level. At zero it is understood to be a monster and carries no sheet.</summary>
            public int Level { get; set; }

            /// <summary>The experience accumulated AFTER the fight, and the one just earned.</summary>
            public long Xp { get; set; }
            public long XpGained { get; set; }

            public Spoils Spoils { get; set; }
        }

        /// <summary>
        /// How each one ended (jyg), which is what fills the end-of-fight screen.
        ///
        ///   f2 (repeated, one per fighter) {
        ///       f2 { f1: the kamas, f2 { f1 { f2: how many, f4: the item } ... } }   the loot
        ///       f3 { f1: who
        ///            f2 { f1 { f2 { f1: the experience earned
        ///                           f2: what is needed for the next level
        ///                           f5: what he has now
        ///                           f6: that of the level he is at
        ///                           f3, f4, f7, f8, f9: ones } }
        ///                 f2: his level }
        ///            f3: 1 }
        ///       f4: 2, and only in the rows of the winning side }
        ///   f4: how long it lasted, in milliseconds      f8: -1
        ///
        /// The experience block is not guessed. Four characters come out of the captures
        /// and in all four it matches the client's table (character_xp.json): with f2 = 354,
        /// f6 is 23,700,657,518, which is exactly what level 354 asks for, and the inner f2 is
        /// 23,932,109,854, which is what 355 asks for; f5 falls between the two. The same with 227, 447
        /// and 290. In the duel, where no experience is earned, the whole block does not go and only
        /// the level remains.
        ///
        /// The loot too: in the koliseo capture the winner takes f1 { f2: 260, f4: 12736 }
        /// and f1 { f2: 2, f4: 34478 }, and 12736 and 34478 are the Kolicha and the Vitoricha, so f4
        /// is the item and f2 how many. Careful, that is the ONLY thing taken from there: which
        /// field is which. The kolichas and vitorichas belong to the koliseo, to PvP, and are not handed out here;
        /// against monsters what goes in this list is what their drop tables release, and the
        /// kamas in f1.
        ///
        /// The loot's f2 ALWAYS goes, even if empty: in the captures the monsters carry a
        /// zero-byte f2, they do not skip it.
        /// </summary>
        public static byte[] BuildFightResults(IEnumerable<FightResult> results, int durationMs)
        {
            var jyg = Pb.New();
            foreach (var result in results)
            {
                var entrada = Pb.New();

                var botin = Pb.New();
                if (result.Spoils != null)
                {
                    botin.VarIfNotZero(1, result.Spoils.Kamas);
                    if (result.Spoils.Items.Count > 0)
                    {
                        var objetos = Pb.New();
                        foreach (var (quantity, gid) in result.Spoils.Items)
                        {
                            objetos.Msg(1, Pb.New().Var(2, quantity).Var(4, gid));
                        }
                        botin.Msg(2, objetos);
                    }
                }
                entrada.Msg(2, botin);

                var quien = Pb.New().Var(1, result.Fighter);
                if (result.Level > 0)
                {
                    var experiencia = Pb.New()
                        .VarIfNotZero(1, result.XpGained)
                        .Var(2, ExperienceTable.NextLevelFloor(result.Level))
                        .Var(3, 1)
                        .Var(4, 1)
                        .Var(5, result.Xp)
                        .Var(6, ExperienceTable.LevelFloor(result.Level))
                        .VarIfNotZero(7, result.XpGained > 0 ? 1 : 0)
                        .Var(8, 1)
                        .Var(9, 1);

                    quien.Msg(2, Pb.New()
                        .Msg(1, Pb.New().Msg(2, experiencia))
                        .Var(2, result.Level));
                }
                // This f3 ALWAYS goes, win or lose.
                //
                // I removed it thinking it was the winner's: in the koliseo's jyg its four entries
                // split two and two, and the two PEOPLE who lose do not bring it. But the
                // regression guard catches it against a capture of a fight against monsters, and
                // there the monster that loses DOES carry it -- the server would not start because of this.
                //
                // So f3 does not mean «has won». What it means I do not know, and with two
                // captures contradicting each other under my reading, the reading that has to go is mine: it is
                // left as it was, which is the only thing measured end to end.
                quien.Var(3, 1);
                entrada.Msg(3, quien);

                if (result.Winner) entrada.Var(4, Victory);
                jyg.Msg(2, entrada);
            }
            return jyg.VarIfNotZero(4, durationMs).Var(8, Nobody).Build();
        }

        /// <summary>The result the jyg carries in the capture of a victory.</summary>
        public const int Victory = 2;

        /// <summary>
        /// One person's end-of-fight statistics (jxo), which the client shows on the
        /// "Personaje" and "Estadísticas" tabs of the fight-over window. Sent right behind
        /// the jyg, to each person with his own numbers only.
        /// </summary>
        /// <remarks>
        /// Shape, as the real server sends it in the 30 fights measured (see
        /// <see cref="Jondo.Unity.World.Fights.FightStatistics"/> for which field is what):
        ///
        ///   f1 { f1: the character           f2 {
        ///        f1 { f2: '', f4: the character }
        ///        f2 { f2: enemies defeated, f4: the same }
        ///        f3 { f4: AP per turn (float) }
        ///        f4 ''
        ///        f5 { f3: taken per turn, f4: taken, f9: taken }        only when hit
        ///        f6 { f1: shields given, f3: per turn }                 only when any
        ///        f8 { f4: MP per turn }                                 '' when none
        ///        f9 ''
        ///        f10 { f3: heals per turn, f4: given, f5: received }    only when any
        ///        f11 { f1: on triggers, f2: the summons', f3: own per AP, f4: total,
        ///              f5: pushes, f6: total per turn, f7: glyphs, f9: direct } } }
        ///   f2 { f1: enemies, f3: taken, f4: heals given, f5: total dealt, f8: shields }
        ///
        /// Zero counters are left out of their block and an empty block is sent as such,
        /// which is how the captures have them: "f5='' f6='' f9='' f10=''" on a fight with
        /// nothing taken, shielded or healed, and "f8=''" when no MP was spent. Floats are
        /// the client's own float32.
        /// </remarks>
        public static byte[] BuildFightStatistics(long character,
                                                  Jondo.Unity.World.Fights.FightStatistics s,
                                                  int enemiesDefeated)
        {
            static byte[] F(float value) => BitConverter.GetBytes(value);

            var mine = Pb.New()
                .Msg(1, Pb.New().Bytes(2, Array.Empty<byte>()).Var(4, character))
                .Msg(2, Pb.New().VarIfNotZero(2, enemiesDefeated).VarIfNotZero(4, enemiesDefeated))
                .Msg(3, Pb.New().Fixed32(4, F(s.PerTurn(s.ActionPointsSpent))))
                .EmptyMsg(4);

            if (s.DamageTaken > 0)
                mine.Msg(5, Pb.New().Fixed32(3, F(s.PerTurn(s.DamageTaken))).Var(4, s.DamageTaken).Var(9, s.DamageTaken));
            else mine.EmptyMsg(5);

            if (s.ShieldsGiven > 0)
                mine.Msg(6, Pb.New().Var(1, s.ShieldsGiven).Fixed32(3, F(s.PerTurn(s.ShieldsGiven))));
            else mine.EmptyMsg(6);

            if (s.MovementPointsSpent > 0)
                mine.Msg(8, Pb.New().Fixed32(4, F(s.PerTurn(s.MovementPointsSpent))));
            else mine.EmptyMsg(8);

            mine.EmptyMsg(9);

            if (s.HealsGiven > 0 || s.HealsReceived > 0)
                mine.Msg(10, Pb.New().Fixed32(3, F(s.PerTurn(s.HealsGiven)))
                                     .VarIfNotZero(4, s.HealsGiven).VarIfNotZero(5, s.HealsReceived));
            else mine.EmptyMsg(10);

            var dealt = Pb.New()
                .VarIfNotZero(1, s.TriggerDamage)
                .VarIfNotZero(2, s.SummonDamage)
                .Fixed32(3, F(s.OwnDamage / (float)Math.Max(1, s.ActionPointsOnDamage)))
                .Var(4, s.TotalDamage)
                .VarIfNotZero(5, s.PushDamage)
                .Fixed32(6, F(s.PerTurn(s.TotalDamage)))
                .VarIfNotZero(7, s.GlyphDamage)
                .VarIfNotZero(9, s.DirectDamage);
            mine.Msg(11, dealt);

            var totals = Pb.New()
                .VarIfNotZero(1, enemiesDefeated)
                .VarIfNotZero(3, s.DamageTaken)
                .VarIfNotZero(4, s.HealsGiven)
                .VarIfNotZero(5, s.TotalDamage)
                .VarIfNotZero(8, s.ShieldsGiven);

            return Pb.New()
                .Msg(1, Pb.New().Var(1, character).Msg(2, mine))
                .Msg(2, totals)
                .Build();
        }

        /// <summary>
        /// The turn is over (jyt).
        ///
        ///   f1: the tenths left over, which are kept for his next turn (omitted if
        ///       zero)      f2: whose it was
        /// </summary>
        public static byte[] BuildTurnEnd(long fighterId, int savedDeciseconds = 0)
            => Pb.New().VarIfNotZero(1, savedDeciseconds).Var(2, fighterId).Build();

        /// <summary>What the sheet's f3 carries in the captures' fighters.</summary>
        private const int SheetKind = 2;

        /// <summary>
        /// Which monster it is: <c>f2 { f1: grade, f2: the template, f3: the level }</c>.
        ///
        /// From the capture's poutch come 3, 494 and 50, and that 50 is precisely its level.
        /// </summary>
        public static Pb MonsterIdentity(int grade, int monsterId, int level)
            => Pb.New().Msg(2, Pb.New()
                .VarIfNotZero(1, grade)
                .Var(2, monsterId)
                .VarIfNotZero(3, level));

        /// <summary>
        /// Who the player is: the breed, his name and little else.
        ///
        ///   f2 { f1: the breed, f3: ?, f4 { f1: 100, f2: 3, f5: 200 } }
        ///   f4: -1        f7: the name        f8 { f1: 1 }
        ///
        /// The inner block's f3 was 354 in the capture and could not be explained; it is
        /// left out. f4 looks like a look block because of that 3 in the middle, but it is not
        /// deciphered either, so it is sent just as it was measured.
        /// </summary>
        public static Pb PlayerIdentity(int breed, string name, int sex = 0, int level = 0)
            => Pb.New()
                .Msg(2, Pb.New()
                    .VarIfNotZero(1, breed)
                    .VarIfNotZero(2, sex)
                    .VarIfNotZero(3, level)
                    .Msg(4, Pb.New().Var(1, 100).Var(2, 3).Var(5, 200)))
                .Var(4, Nobody)
                .Str(7, name ?? "")
                .Msg(8, Pb.New().Var(1, 1));

        /// <summary>
        /// The characteristics the real server sends in the placement sheet, in their
        /// order. Almost all go at zero there: what matters in this phase is that the slot exists.
        /// </summary>
        public static readonly int[] PlacementSheet =
        {
            1,   // PA
            23,  // PM
            27,  // esquiva PA
            28,  // esquiva PM
            33,  // resistencia tierra
            34,  // resistencia fuego
            35,  // resistencia agua
            36,  // resistencia aire
            37,  // resistencia neutral
        };

        /// <summary>
        /// Who fights (jzu).
        ///
        ///   f2 (repeated, one per FIGHTER) { f3 { f2: who } }
        ///
        /// Careful, this is easy to read the wrong way round: the repeated f2 is NOT a team, it is a
        /// fighter. Against a single monster two blocks come out and each looks like a team;
        /// against four poutchs FIVE come out, with the player and then -1, -2, -3 and -4. The
        /// monsters carry their own negative identifier, not all the same one.
        ///
        /// The order is the player first and the monsters after.
        /// </summary>
        public static byte[] BuildTeams(IEnumerable<long> fighters)
        {
            var jzu = Pb.New();
            foreach (long fighter in fighters)
            {
                jzu.Msg(2, Pb.New().Msg(3, Pb.New().Var(2, fighter)));
            }
            return jzu.Build();
        }

        /// <summary>
        /// Where one can be placed (kba).
        ///
        ///   f1 { f1: [team 0's cells], f2: [team 1's] }
        ///
        /// Both lists go packed, and in the captures they are sixteen cells per side.
        /// </summary>
        public static byte[] BuildPlacementCells(IEnumerable<long> blue, IEnumerable<long> red)
            => Pb.New()
                .Msg(1, Pb.New().Packed(1, blue).Packed(2, red))
                .Build();

        // ─── Colocarse ──────────────────────────────────────────────────────────

        /// <summary>
        /// Which cell the player wants to move to during placement (jzy).
        ///
        ///   f1: who        f2: the cell
        /// </summary>
        public static (long Fighter, int Cell) ReadPlacementMove(byte[] payload)
        {
            byte[]? jzy = ConnectionProtocol.ReadPayload(payload, Op.Jzy);
            if (jzy == null) return (0, 0);

            long fighter = 0;
            int cell = 0;
            foreach (var field in ProtoMessage.Parse(jzy).Fields)
            {
                if (field.WireType != 0) continue;
                if (field.FieldNumber == 1) fighter = field.VarIntValue;
                else if (field.FieldNumber == 2) cell = (int)field.VarIntValue;
            }
            return (fighter, cell);
        }

        /// <summary>
        /// Who is on which cell (kmk).
        ///
        ///   f2 (repeated) { f1: cell, f2: orientation, f3: who }
        ///
        /// Moving during placement travels as TWO entries in a single message: the cell
        /// left, with <see cref="Nobody"/>, and the one taken, with who takes it. Sending only the
        /// new one leaves the onlooker seeing the same one twice.
        /// </summary>
        public static byte[] BuildFightersPlaced(IEnumerable<(int Cell, int Orientation, long Fighter)> spots)
        {
            var kmk = Pb.New();
            foreach (var (cell, orientation, fighter) in spots)
            {
                kmk.Msg(2, Pb.New()
                    .Var(1, cell)
                    .VarIfNotZero(2, orientation)
                    .Var(3, fighter));
            }
            return kmk.Build();
        }

        /// <summary>A single one, which is the ordinary case.</summary>
        public static byte[] BuildFighterPlaced(int cell, int orientation, long fighter)
            => BuildFightersPlaced(new[] { (cell, orientation, fighter) });

        // ─── Listo ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The ready button (kaq).
        ///
        ///   f1: 1
        ///
        /// Returns whether the player declares himself ready. In the captures it always arrives with 1; zero
        /// would be withdrawing ready, but that is not measured.
        /// </summary>
        public static bool ReadReady(byte[] payload)
        {
            byte[]? kaq = ConnectionProtocol.ReadPayload(payload, Op.Kaq);
            if (kaq == null) return false;

            foreach (var field in ProtoMessage.Parse(kaq).Fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 0) return field.VarIntValue != 0;
            }
            return false;
        }

        /// <summary>
        /// Ready acknowledged (kah).
        ///
        ///   f1: who        f3: 1
        ///
        /// f2 does not appear in any of the captures.
        /// </summary>
        /// <summary>
        /// The challenge offered (hqc): who challenges, whom, and the id.
        /// </summary>
        /// <remarks>
        /// Byte by byte from the four challenge captures. The four measured hqc have the same
        /// shape and their three fields always set:
        ///
        ///   08a28280c8e708 10a282f0a6c408 18ee03    challenger, challenged, 494
        /// </remarks>
        public static byte[] BuildChallengeOffered(long challengerId, long targetId, int challengeId)
            => Pb.New().Var(1, challengerId).Var(2, targetId).Var(3, challengeId).Build();

        /// <summary>
        /// How the challenge ended (hpv). f3 only travels when it was accepted.
        /// </summary>
        /// <remarks>
        /// And that is the whole difference between the two endings, measured in the four captures:
        ///
        ///   accepted   08a28280c8e708 10ee03 1801 20a282f0a6c408
        ///   declined   08a28280c8e708 10e903      20a282f0a6c408
        ///
        /// The challenged one's field is FOUR, not three, and that is not a misreading: in
        /// the two declined ones the 20 goes right after the id, with nothing in between.
        /// </remarks>
        public static byte[] BuildChallengeAnswered(long challengerId, int challengeId,
                                                    bool accepted, long targetId)
        {
            var hpv = Pb.New().Var(1, challengerId).Var(2, challengeId);
            if (accepted) hpv.Var(3, 1);
            return hpv.Var(4, targetId).Build();
        }

        public static byte[] BuildReadyAck(long fighterId, bool ready = true)
            => Pb.New().Var(1, fighterId).Var(3, ready ? 1 : 0).Build();

        // ─── The spell bar ──────────────────────────────────────────────────────

        /// <summary>
        /// The spells one fights with (jyy).
        ///
        ///   f3: who        f4: who (the same)
        ///   f6 (repeated) { f1: the grade, f3: the spell, f4: 1 }
        ///
        /// f3 and f4 carry the same fighter in the captures; what separates them is not known,
        /// because they have never been seen different.
        /// </summary>
        /// <summary>The spell's origin: 1 the class ones, 2 the ones that are not —melee—.</summary>
        private const int OrigenQueNoEsDeClase = 2;
        private const int GradoDelCuerpoACuerpo = 1;

        /// <summary>Melee is spell ZERO, "Puñetazo".</summary>
        public const int HechizoCuerpoACuerpo = 0;

        /// <param name="conArma">
        /// Whether it carries the melee entry. The PLAYERS' bars carry it, all 27
        /// of the captures; the summons', which bring one or two entries, do not.
        /// </param>
        /// <summary>
        /// The spell bar of a summon, for the player who controls it (jyy): f3 the summon, f4
        /// the owner, one f6 per spell with its grade and origin 6, one f7 per slot, no melee.
        /// Measured on the Tymobot and on the Osamodas' animals: "f3=-12 f4=owner f6{f1=3
        /// f3=13451 f4=6} ... f7{f6{f2=13451}} f7{f2=1 f6{f2=13452}} ...".
        /// </summary>
        public static byte[] BuildSummonSpellBar(long summonId, long ownerId,
                                                 IEnumerable<(int Spell, int Grade)> spells)
        {
            var jyy = Pb.New().Var(3, summonId).Var(4, ownerId);
            var lista = new List<(int Spell, int Grade)>(spells);
            foreach (var (spell, grade) in lista)
            {
                jyy.Msg(6, Pb.New().VarIfNotZero(1, grade).Var(3, spell).Var(4, OrigenDeInvocado));
            }
            for (int slot = 0; slot < lista.Count; slot++)
            {
                jyy.Msg(7, Pb.New().VarIfNotZero(2, slot).Msg(6, Pb.New().Var(2, lista[slot].Spell)));
            }
            return jyy.Build();
        }

        /// <summary>The origin of a summon's spell in its jyy: 6 in all 24 summon bars of the captures.</summary>
        private const int OrigenDeInvocado = 6;

        public static byte[] BuildSpellBar(long fighterId, IEnumerable<(int Spell, int Grade)> spells,
                                           IEnumerable<(int Slot, int Spell)> bar, bool conArma = true)
        {
            var jyy = Pb.New().Var(3, fighterId).Var(4, fighterId);

            // Melee goes first in the list, with the number omitted —it is spell
            // ZERO, "Puñetazo", which is in the base as SpellTemplates.Id 0— and the origin at 2.
            // The bytes are 08 01 20 02, and they appear in the 27 player bars of the captures; those
            // of summons do not carry it.
            if (conArma)
            {
                jyy.Msg(6, Pb.New().Var(1, GradoDelCuerpoACuerpo).Var(4, OrigenQueNoEsDeClase));
            }

            // Which spells it has.
            foreach (var (spell, grade) in spells)
            {
                jyy.Msg(6, Pb.New()
                    .VarIfNotZero(1, grade)
                    .Var(3, spell)
                    .Var(4, 1));
            }

            // And WHERE they are placed, which is another list and goes separately. Sending only the first, the
            // client knows which spells you have but leaves the "Mis hechizos" panel blank, and
            // without icons there is no way to cast anything.
            //
            // And the slots. Melee has to go in BOTH PLACES: in the list
            // above, so the client knows what it is, and here in the bar, so it has somewhere to
            // draw it. The three previous attempts always sent one of the two and never
            // both, and that is why it came out greyed, or did not come out.
            //
            // Its entry in the bar is a PRESENT AND EMPTY f6 —bytes 3a 02 32 00—, which is
            // exactly how proto3 writes "spell zero". It was read at the time as "a slot
            // the player left unfilled", and that is false: an unfilled slot is simply
            // not sent. It is in 100% of the player bars —13 of 13 itg and 51 of 51 jyy—
            // and in 0% of the summon ones, 0 of 24. And the tutorial character, with an
            // empty inventory and no weapon at all, carries it the same: the slot belongs to the fist.
            foreach (var (slot, spell) in bar)
            {
                var hueco = Pb.New().VarIfNotZero(2, slot);
                if (spell == HechizoCuerpoACuerpo) hueco.EmptyMsg(6);
                else hueco.Msg(6, Pb.New().Var(2, spell));
                jyy.Msg(7, hueco);
            }

            return jyy.Build();
        }

        // ─── The challenges ─────────────────────────────────────────────────────

        /// <summary>
        /// The state every challenge carries on the wire. It is two in one hundred per cent of those
        /// seen —in the proposal, in the final list and in the mid-fight ones—,
        /// so nothing is known about the enum's other two values.
        /// </summary>
        public const int ChallengeState = 2;

        /// <summary>
        /// How long the proposal lasts. It is fifteen in all nine appearances and has not been seen
        /// to change; the client has an <c>OnChallengeProposalUpdateTimer</c>, so it is a
        /// timer, but from what is seen it could be any constant.
        /// </summary>
        public const int ChallengeTimer = 15;

        /// <summary>
        /// A challenge (ldd): { f1: %, f2: which, f3 (repeated): targets, f4: %, f5: state }.
        ///
        /// The two percentages are the experience one and the loot one, and in the twenty-seven
        /// different challenges of the captures they ALWAYS have the same value, so there is no way of knowing which
        /// is which. The client does not help either: its window draws a single number.
        ///
        /// When the extra is zero both fields disappear —proto3 does not send zero—, which is
        /// what happens with the challenges an anomaly imposes.
        ///
        ///   085f1011205f2802   =   95 %, challenge 17, 95 %, state 2
        /// </summary>
        public static byte[] BuildChallenge(int id, int percent, IEnumerable<(int Cell, long Fighter)>? targets = null)
        {
            var ldd = Pb.New().VarIfNotZero(1, percent).Var(2, id);

            if (targets != null)
            {
                foreach (var (cell, fighter) in targets)
                {
                    // With no target yet the cell goes at minus one: in the preparation, a challenge
                    // pointing at where you end the turn does not yet know where you will be.
                    ldd.Msg(3, Pb.New().Var(2, cell).VarIfNotZero(3, fighter));
                }
            }

            return ldd.VarIfNotZero(4, percent).Var(5, ChallengeState).Build();
        }

        /// <summary>How many challenges have to be chosen (kxa): { f1: n }. One outside a dungeon.</summary>
        public static byte[] BuildChallengeCount(int howMany) => Pb.New().Var(1, howMany).Build();

        /// <summary>
        /// The candidate list (kwx): { f1: the timer, f2 (repeated): the challenges }.
        ///
        /// There are always two, and they are alternatives: in the captures two were offered together that the
        /// client's table marks as incompatible with each other.
        /// </summary>
        public static byte[] BuildChallengeList(IEnumerable<byte[]> challenges)
        {
            var kwx = Pb.New().Var(1, ChallengeTimer);
            foreach (byte[] uno in challenges) kwx.Bytes(2, uno);
            return kwx.Build();
        }

        /// <summary>Un reto queda fijado (kww): { f1: el reto }.</summary>
        public static byte[] BuildChallengeChosen(byte[] challenge)
            => Pb.New().Bytes(1, challenge).Build();

        /// <summary>The final list (kwu): { f2 (repeated): the challenges }. It goes stuck to the jyy.</summary>
        public static byte[] BuildChallengeFinalList(IEnumerable<byte[]> challenges)
        {
            var kwu = Pb.New();
            foreach (byte[] uno in challenges) kwu.Bytes(2, uno);
            return kwu.Build();
        }

        /// <summary>The confirmation of the panel setting (kwn), with the same value that arrived.</summary>
        public static byte[] BuildChallengeSettings(long value)
            => Pb.New().VarIfNotZero(1, value).Build();

        /// <summary>
        /// A challenge's TARGET (kwm): { f2: the challenge, with its target inside }.
        ///
        /// It is the only message carrying whom to kill, and its f1 has never travelled. In
        /// the captures it appears three times, all three stuck to the jyy that starts the fight; sending
        /// it again when the target changes is the natural reading —there is no other message that
        /// could carry it— but that is no longer measured.
        ///
        ///   1218084610231a0e10860218fdffffffffffffffff0120462802
        ///   = challenge 35 at 70 %, target on cell 262, fighter −3
        /// </summary>
        public static byte[] BuildChallengeObjective(byte[] challenge)
            => Pb.New().Bytes(2, challenge).Build();

        /// <summary>
        /// A challenge's RESULT (kwl): { f1: which, f2: met }.
        ///
        /// Without f2 it is FAILED, which is how proto3 writes a false boolean. The client
        /// draws it green or red, and until this reaches it, it considers it alive: if it is never
        /// sent, the challenge stays running forever on the player's screen.
        ///
        ///   08111001   =   challenge 17 met
        ///   0801       =   challenge 1 failed
        /// </summary>
        public static byte[] BuildChallengeResult(int id, bool completed)
            => Pb.New().Var(1, id).VarIfNotZero(2, completed ? 1 : 0).Build();

        /// <summary>
        /// What a modifier is worth RIGHT NOW for a specific spell (hnd).
        ///
        /// The client does not compute a spell's range from the panel's buffs: it
        /// takes it from here. Without this message, Disparos Lejanos came out in the effects list with its
        /// «+6 de alcance máximo» and the lit cells stayed the same, because the
        /// jxm is for drawing and this is for computing.
        ///
        ///   f1 { f2: 1, f3: how much, f4: which modifier, f5: the spell }   f2: whose
        ///
        /// Measured in «ocra-disparos lejanos»: f4 = 13 with f3 = 3, and f4 = 12 with f3 = 6, which are
        /// exactly that spell's «+3 de alcance mínimo» and «+6 de alcance máximo».
        /// </summary>
        /// <param name="accion">
        /// Add (1), take away (2) or set (3): Bestialidad's pinned ranges go out as
        /// "f2=3 f3=2 f4=12", and a pin to zero with no f3 at all -- a zero total is not written.
        /// See <see cref="Managers.SpellModifiers"/>.
        /// </param>
        public static byte[] BuildSpellModifier(long quien, int modificador, int hechizo, long cuanto,
                                                int accion = Managers.SpellModifiers.Add)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Var(2, accion)
                    .VarIfNotZero(3, cuanto)
                    .Var(4, modificador)
                    .Var(5, hechizo))
                .Var(2, quien)
                .Build();

        /// <summary>
        /// The declaration that goes with <see cref="BuildSpellModifier"/> (hnk): it says that
        /// spell has that modifier on. Both go, one after the other and in the same
        /// number: 272 and 272 in the capture.
        ///
        ///   f1: which modifier      f2: 1     f3: the spell     f5: whose
        /// </summary>
        public static byte[] BuildSpellModifierDeclared(long quien, int modificador, int hechizo,
                                                        int accion = Managers.SpellModifiers.Add)
            => Pb.New()
                .Var(1, modificador)
                .Var(2, accion)
                .Var(3, hechizo)
                .Var(5, quien)
                .Build();

        /// <summary>A spell's maximum range, as the hnd/hnk numbers it.</summary>
        public const int SpellMaxRange = 12;

        /// <summary>And the minimum. CAREFUL: the minimum is 13 and the maximum 12, not the other way round.</summary>
        public const int SpellMinRange = 13;
    }
}
