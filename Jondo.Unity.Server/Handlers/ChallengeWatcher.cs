using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Watching the challenges during the fight and saying whether they are met or broken.
    ///
    /// ─── How it is announced ────────────────────────────────────────────────────────────────
    ///
    /// With a single message, the <c>kwl { f1: which, f2: met }</c>, and with a timing rule that is
    /// measured: a FAILURE is sent the instant it happens, in the middle of the fight, and a
    /// SUCCESS at the end, less than eleven frames from the <c>jyg</c>. In a defeat all the
    /// failures arrive one after the other just before the end.
    ///
    /// There is no «still alive» heartbeat: the client considers the challenge alive from the end
    /// of placement until its kwl arrives. That is why it always has to be sent, even to say no:
    /// otherwise the challenge keeps spinning on the player's screen forever.
    ///
    /// ─── Where each rule comes from ─────────────────────────────────────────────────────────
    ///
    /// From the challenge's DESCRIPTION, which the client carries translated and says in plain
    /// words what has to be done. The other field, <c>completionCriterion</c>, is a short language
    /// with no glossary -- «TD&lt;2,hc0,e1» -- that can only be guessed at, so the description is
    /// used and the criterion is kept alongside as a check.
    ///
    /// ─── What is NOT watched here ───────────────────────────────────────────────────────────
    ///
    /// Of the 43 general challenges, the four whose activation is <c>BV=0</c> -- «Dúo», «Trío»,
    /// «Cuarteto» and «Crono» of the general list, a breach's -- which a classic server never
    /// offers. «Asesino a sueldo» (35) is watched: its target goes through the <c>kwm</c> and a new
    /// one is pointed out as each falls.
    /// </summary>
    public static class ChallengeWatcher
    {
        // The ones this watcher knows how to follow. The rest are not offered.
        public const int Primero = 3;
        public const int Ultimo = 4;
        public const int LosPequenosAntes = 30;
        public const int Imprevisible = 34;
        public const int AsesinoASueldo = 35;
        public const int Duelo = 45;
        public const int SinCorazon = 962;
        public const int AsesinoAOjo = 967;
        public const int Conquistador = 973;
        public const int Dum = 974;
        public const int Zombi = 1;
        public const int Estatua = 2;
        public const int Ahorrador = 5;
        public const int Versatil = 6;
        public const int Nomada = 8;
        public const int Barbaro = 9;
        public const int Cruel = 10;
        public const int Intocable = 17;
        public const int Elemental = 20;
        public const int Ordenado = 25;
        public const int Focalizacion = 31;
        public const int Elitista = 32;
        public const int Superviviente = 33;
        public const int Audaz = 36;
        public const int Pegajoso = 37;
        public const int Blitzkrieg = 38;
        public const int Anacoreta = 39;
        public const int Prudente = 40;
        public const int Reparto = 44;
        public const int MismoLinaje = 964;
        public const int Diagonal = 965;
        public const int SiNoSeVe = 968;
        public const int LineaDeMira = 969;
        public const int MerecidoPin = 970;
        public const int EntreLasSombras = 971;
        public const int ManosLimpias = 19;
        public const int DosPorUno = 42;
        public const int CadaUnoConSuMonstruo = 46;
        public const int Espejo = 966;

        /// <summary>
        /// The ones that can be watched, and the percentage they are given IF theirs has never come
        /// over the wire.
        ///
        /// That number is not measured and cannot be: the client's table carries no bonus, the server
        /// sets it. But it has not been picked at random either. Half of these challenges are the TWIN
        /// of one that is measured -- «decreasing order» against «increasing order», «never next to an
        /// ally» against «always next to one» -- and those are given what their twin is worth, which is
        /// the closest thing to a measurement there is. The rest get the floor of what was observed,
        /// which is 50: never asking for too much, so a challenge that turns out easier than it looks
        /// gives nothing away.
        ///
        /// Zero means «this one comes measured, do not set anything».
        /// </summary>
        public static readonly Dictionary<int, int> Watched = new Dictionary<int, int>
        {
            // Measured: the percentage comes from the wire.
            [Zombi] = 0, [Estatua] = 0, [Versatil] = 0, [Barbaro] = 0, [Cruel] = 0,
            [Intocable] = 0, [Focalizacion] = 0, [Elitista] = 0, [Audaz] = 0, [Pegajoso] = 0,
            [Prudente] = 0, [MismoLinaje] = 0, [LineaDeMira] = 0, [MerecidoPin] = 0,
            [EntreLasSombras] = 0,

            // Twins of a measured one: what their twin is worth.
            [Ordenado] = 60,        // the reverse of Cruel (10), which is at 60
            [Anacoreta] = 75,       // the reverse of Sticky (37), which is at 75
            [SiNoSeVe] = 85,        // the reverse of In Line of Sight (969), which is at 85
            [Diagonal] = 65,        // Same Lineage (964) but diagonal, which is at 65
            [Ahorrador] = 90,       // Versatile (6) stretched to the whole fight, which is at 90
            [Nomada] = 80,          // Zombie (1) reversed, spending them all; Zombie is at 80

            // No twin: the floor of what was measured.
            [Superviviente] = 50, [Reparto] = 50, [Elemental] = 50, [Blitzkrieg] = 50,

            // The ones that need the server to POINT OUT an enemy. Hitman carries its measured
            // percentage; the others go to the floor.
            [AsesinoASueldo] = 0,
            [Primero] = 50, [Ultimo] = 50, [Imprevisible] = 50, [AsesinoAOjo] = 50,
            [LosPequenosAntes] = 50, [Duelo] = 50, [Conquistador] = 50, [Dum] = 50,
            [SinCorazon] = 50,

            // Who strikes, who finishes off and from where: the floor too.
            [ManosLimpias] = 50, [DosPorUno] = 50, [CadaUnoConSuMonstruo] = 50, [Espejo] = 50,
        };

        /// <summary>
        /// The ones that need an enemy pointed out, and when it is pointed out again.
        ///
        ///   at the start      First, Last and Hitman: one for the whole fight, and Hitman also
        ///                     points out another every time its own falls
        ///   every round       Unpredictable: a new one at the start of each global turn
        ///   every turn        Assassin's Eye: the nearest to whoever is about to play
        /// </summary>
        private static readonly int[] SenalanAlEmpezar = { Primero, Ultimo, AsesinoASueldo };

        // ─── The notices the fight sends it ─────────────────────────────────────

        /// <summary>
        /// The fight starts: the targets that are needed are pointed out.
        ///
        /// It goes after the jyy, which is where the three kwm of the captures come out. The enemy is
        /// picked at random among those present, which is the only reasonable thing: which one the real
        /// server chooses cannot be known from three samples.
        /// </summary>
        public static async Task FightStartedAsync(NetworkStream stream, FightInstance fight)
        {
            if (fight.ChallengesFixed.Count == 0) return;

            // Where each ally starts from, for "Salida de ring". Once a fight, not once a player.
            var notes = Notes(fight);
            lock (notes)
            {
                if (notes.StartCells.Count == 0)
                {
                    foreach (var ally in fight.Azul) notes.StartCells[ally.Id] = ally.CellId;
                }
            }

            foreach (int reto in SenalanAlEmpezar)
            {
                if (Vivo(fight, reto)) await SenalarAsync(stream, fight, reto, UnEnemigoVivo(fight));
            }

            if (Vivo(fight, Imprevisible))
                await SenalarAsync(stream, fight, Imprevisible, UnEnemigoVivo(fight));
        }

        /// <summary>Somebody's turn starts: where he starts from and with how many MP is noted.</summary>
        public static void TurnStarted(FightInstance fight, Fighter quien)
        {
            fight.TurnStartCell = quien.CellId;
            fight.TurnStartMp = quien.CurrentMP;
            fight.TurnTackledMp = 0;
            fight.KillCells.Clear();
            Notes(fight).TurnKills = 0;

            // "Hay gente por aquí" lets one choose: begin OR end the turn in line with an enemy.
            if (quien.TeamId == 0 && fight.ChallengesFixed.Count > 0)
            {
                Notes(fight).BeganInLineWithEnemy = InLine(fight.Rojo, quien, diagonal: false);
            }
        }

        /// <summary>
        /// A new round starts: Unpredictable points out another. It is literally what its description
        /// says, «the enemy indicated at the start of each global turn».
        /// </summary>
        public static async Task RoundStartedAsync(NetworkStream stream, FightInstance fight)
        {
            // A boss's "Dúo", "Trío" and "Crono": win in FEWER than N turns. Once round N begins
            // it can no longer be done, and it is said then, which is when it happens.
            foreach (var (id, _) in fight.ChallengesFixed.ToArray())
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                int limit = Challenges.Get(id)?.TurnLimit ?? 0;
                if (limit > 0 && fight.RoundNumber >= limit)
                {
                    await BreakOneAsync(stream, fight, id,
                                        $"round {fight.RoundNumber} starts and the win had to come before round {limit}");
                }
            }

            if (Vivo(fight, Imprevisible))
                await SenalarAsync(stream, fight, Imprevisible, UnEnemigoVivo(fight));
        }

        /// <summary>
        /// An ally is about to play: Assassin's Eye points out the enemy nearest to him, which is what
        /// its description asks for, «the closest to him at the start of each turn».
        /// </summary>
        public static async Task AllyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                      Fighter quien)
        {
            if (quien.TeamId != 0 || !Vivo(fight, AsesinoAOjo)) return;

            Fighter? masCerca = null;
            int mejor = int.MaxValue;
            foreach (var enemigo in fight.Rojo)
            {
                if (!enemigo.IsAlive) continue;
                int lejos = MapGeometry.Distance(quien.CellId, enemigo.CellId);
                if (lejos < mejor) { mejor = lejos; masCerca = enemigo; }
            }
            if (masCerca != null) await SenalarAsync(stream, fight, AsesinoAOjo, masCerca);
        }

        /// <summary>
        /// Somebody's turn ends. The position challenges, which are half of them, are judged here.
        /// </summary>
        public static async Task TurnEndedAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

            // Zombi: exactly one MP a turn. The MP lost getting out of a tackle do not count, its
            // description says (1008205), so they are taken off what he spent.
            int spentMp = fight.TurnStartMp - quien.CurrentMP - fight.TurnTackledMp;
            if (Vivo(fight, Zombi) && spentMp != 1)
            {
                await BreakAsync(stream, fight, Zombi,
                                 $"{quien.Name} ha gastado {spentMp} PM");
            }

            // Statue: end where you started.
            if (Vivo(fight, Estatua) && quien.CellId != fight.TurnStartCell)
            {
                await BreakAsync(stream, fight, Estatua, $"{quien.Name} has moved");
            }

            // Nomad: the reverse of Zombie, ALL of them have to be spent.
            if (Vivo(fight, Nomada) && quien.CurrentMP > 0)
            {
                await BreakAsync(stream, fight, Nomada,
                                 $"{quien.Name} has {quien.CurrentMP} MP to spare");
            }

            bool pegadoAEnemigo = Adyacente(fight, quien, 1);
            bool pegadoAAliado = Adyacente(fight, quien, 0);

            if (Vivo(fight, Audaz) && !pegadoAEnemigo)
                await BreakAsync(stream, fight, Audaz, $"{quien.Name} ends far from every enemy");

            if (Vivo(fight, Prudente) && pegadoAEnemigo)
                await BreakAsync(stream, fight, Prudente, $"{quien.Name} ends up next to an enemy");

            if (Vivo(fight, Pegajoso) && !pegadoAAliado)
                await BreakAsync(stream, fight, Pegajoso, $"{quien.Name} ends without any ally alongside");

            if (Vivo(fight, Anacoreta) && pegadoAAliado)
                await BreakAsync(stream, fight, Anacoreta, $"{quien.Name} ends up next to an ally");

            if (Vivo(fight, MismoLinaje) && !AlineadoConUnAliado(fight, quien, diagonal: false))
                await BreakAsync(stream, fight, MismoLinaje, $"{quien.Name} ends without lining up with anybody");

            if (Vivo(fight, Diagonal) && !AlineadoConUnAliado(fight, quien, diagonal: true))
                await BreakAsync(stream, fight, Diagonal, $"{quien.Name} ends without any ally on a diagonal");

            if (Vivo(fight, EntreLasSombras) && !JuntoAObstaculo(fight, quien.CellId))
                await BreakAsync(stream, fight, EntreLasSombras, $"{quien.Name} ends in the open");

            // Two for one: whoever finishes one off in his turn finishes «un (y solo un) segundo».
            int kills = Notes(fight).TurnKills;
            if (Vivo(fight, DosPorUno) && (kills == 1 || kills > 2))
                await BreakAsync(stream, fight, DosPorUno, $"{quien.Name} finishes off {kills} in his turn");

            bool leVen = LoVeAlgunEnemigo(fight, quien);

            if (Vivo(fight, LineaDeMira) && !leVen)
                await BreakAsync(stream, fight, LineaDeMira, $"no enemy can see {quien.Name}");

            if (Vivo(fight, SiNoSeVe) && leVen)
                await BreakAsync(stream, fight, SiNoSeVe, $"an enemy can see {quien.Name}");

            // Conqueror: if you finished somebody off this turn, you end on his cell.
            if (Vivo(fight, Conquistador) && fight.KillCells.Count > 0
                && !fight.KillCells.Contains(quien.CellId))
            {
                await BreakAsync(stream, fight, Conquistador,
                                 $"{quien.Name} finished one off but does not end on the dead one's cell");
            }

            await BossPositionAsync(stream, fight, quien);
        }

        /// <summary>
        /// The boss challenges that look at where the turn ends and only say so in their
        /// description. Cousins of the ones above: in line or diagonal, near or far, but with
        /// respect to the enemies or with a number of cells of their own.
        /// </summary>
        private static async Task BossPositionAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!AnyRule(fight)) return;

            bool inLineWithEnemy = InLine(fight.Rojo, quien, diagonal: false);
            bool diagonalToEnemy = InLine(fight.Rojo, quien, diagonal: true);
            bool inLineWithAlly = InLine(fight.Azul, quien, diagonal: false);
            bool diagonalToAlly = InLine(fight.Azul, quien, diagonal: true);
            int toEnemy = Nearest(fight.Rojo, quien);
            int toAlly = Nearest(fight.Azul, quien);
            var notes = Notes(fight);

            async Task BreakIf(bool broken, Challenges.BossRule rule, string why)
            {
                if (broken) await BreakRuleAsync(stream, fight, rule, $"{quien.Name} {why}");
            }

            await BreakIf(!inLineWithEnemy, Challenges.BossRule.EndInLineWithEnemy, "ends without lining up with any enemy");
            await BreakIf(!diagonalToEnemy, Challenges.BossRule.EndDiagonalToEnemy, "ends without any enemy on a diagonal");
            await BreakIf(inLineWithEnemy, Challenges.BossRule.NeverInLineWithEnemy, "ends in line with an enemy");
            await BreakIf(inLineWithEnemy || diagonalToEnemy, Challenges.BossRule.NeverLineOrDiagonalEnemy,
                          "ends in line or on a diagonal with an enemy");
            await BreakIf(inLineWithAlly, Challenges.BossRule.NeverInLineWithAlly, "ends in line with an ally");
            await BreakIf(inLineWithEnemy || inLineWithAlly, Challenges.BossRule.NeverLineEnemyOrAlly,
                          "ends in line with somebody");
            await BreakIf(diagonalToEnemy || diagonalToAlly, Challenges.BossRule.NeverDiagonalEnemyOrAlly,
                          "ends on a diagonal with somebody");
            await BreakIf(toEnemy > 5, Challenges.BossRule.EndNearEnemy5, "ends more than 5 cells from every enemy");
            await BreakIf(toAlly <= 3, Challenges.BossRule.EndFarFromAllies3, "ends 3 cells or fewer from an ally");
            await BreakIf(toAlly <= 4, Challenges.BossRule.EndFarFromAllies4, "ends 4 cells or fewer from an ally");
            await BreakIf(!notes.BeganInLineWithEnemy && !inLineWithEnemy, Challenges.BossRule.BeginOrEndInLineWithEnemy,
                          "neither starts nor ends in line with an enemy");

            // One who was not there at the start -- a summon -- has no start cell to keep.
            await BreakIf(notes.StartCells.TryGetValue(quien.Id, out int start) && quien.CellId != start,
                          Challenges.BossRule.EndOnStartCell, "does not end on its starting cell");
        }

        /// <summary>Is he one of the lowest-level allies? Several can be tied.</summary>
        private static bool EsDeLosMasBajos(FightInstance fight, Fighter quien)
        {
            int menor = int.MaxValue;
            foreach (var uno in fight.Azul) if (uno.IsAlive && uno.Level < menor) menor = uno.Level;
            return quien.Level <= menor;
        }

        /// <summary>
        /// An ENEMY's turn starts. It is only needed for Blitzkrieg: whoever you hit, you finish off
        /// before his turn comes.
        /// </summary>
        public static async Task EnemyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                       Fighter enemigo)
        {
            if (!Alguno(fight) || enemigo.TeamId == 0) return;

            if (Vivo(fight, Blitzkrieg) && enemigo.IsAlive && fight.Wounded.Contains(enemigo.Id))
            {
                await BreakAsync(stream, fight, Blitzkrieg,
                                 $"{enemigo.Name} got to play and was still alive");
            }
        }

        /// <summary>
        /// Somebody loses life. Untouchable comes from here -- if the one losing it is an ally -- and
        /// Elemental, which looks at what the hit is made with.
        /// </summary>
        public static async Task DamagedAsync(NetworkStream stream, FightInstance fight,
                                              Fighter quien, int cuanto, Fighter quienPega, int elemento,
                                              bool direct = false)
        {
            if (!Alguno(fight) || cuanto <= 0) return;

            await BossDamageAsync(stream, fight, quien, quienPega, elemento);

            if (quien.TeamId == 0)
            {
                if (Vivo(fight, Intocable))
                    await BreakAsync(stream, fight, Intocable, $"{quien.Name} has lost {cuanto} life");
                return;
            }

            // Elemental: the first element hit with rules for the rest of the fight.
            if (Vivo(fight, Elemental) && quienPega.TeamId == 0 && elemento != 0)
            {
                if (fight.DamageElement == 0) fight.DamageElement = elemento;
                else if (fight.DamageElement != elemento)
                {
                    await BreakAsync(stream, fight, Elemental,
                                     $"{quienPega.Name} hits with element {elemento} and before it was " +
                                     $"with {fight.DamageElement}");
                }
            }

            if (quienPega == null || quienPega.TeamId != 0) return;

            // Clean hands: «sin causarles daños directos». Traps, glyphs, poisons, pushes, reflected
            // damage and summons are allowed; an ally's own spell or weapon is not.
            if (direct && !quienPega.EsInvocado && Vivo(fight, ManosLimpias))
                await BreakAsync(stream, fight, ManosLimpias, $"{quienPega.Name} hits {quien.Name} directly");

            // Each his own monster: «Cuando un luchador aliado ataca a un enemigo, ningún otro luchador
            // aliado debe atacar a dicho enemigo». A summon's blows are its master's.
            if (Vivo(fight, CadaUnoConSuMonstruo))
            {
                long owner = OwnerOf(quienPega);
                var claims = Notes(fight).Claims;
                if (claims.TryGetValue(quien.Id, out long had) && had != owner)
                    await BreakAsync(stream, fight, CadaUnoConSuMonstruo, $"{quienPega.Name} hits {quien.Name}, somebody else's");
                else
                    claims[quien.Id] = owner;
            }
        }

        /// <summary>Whose a blow or a kill is: a summon's are its master's.</summary>
        private static long OwnerOf(Fighter fighter) => fighter.EsInvocado && fighter.Invocador != 0 ? fighter.Invocador : fighter.Id;

        /// <summary>Push damage travels with no element: that is how the fight sends it.</summary>
        private const int PushElement = -1;

        /// <summary>
        /// The boss challenges that look at a blow: where it is dealt from, to whom, and with what.
        ///
        /// "Ranged" is not standing next to the one hit, at the moment of the blow. A poison or a
        /// glyph that hurts with its owner far away counts as ranged: it is the reading that breaks
        /// too often and never hands the challenge over.
        /// </summary>
        private static async Task BossDamageAsync(NetworkStream stream, FightInstance fight,
                                                  Fighter quien, Fighter quienPega, int elemento)
        {
            if (!AnyRule(fight)) return;

            if (quien.TeamId == 0)
            {
                if (elemento == PushElement)
                {
                    await BreakRuleAsync(stream, fight, Challenges.BossRule.NoPushDamageToAllies,
                                         $"{quien.Name} takes pushback damage");
                }
                return;
            }

            if (elemento == PushElement)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoPushDamageToEnemies,
                                     $"{quien.Name} takes pushback damage");
            }
            else if (quienPega is { TeamId: 0 })
            {
                bool ranged = MapGeometry.Distance(quienPega.CellId, quien.CellId) > 1;
                await BreakRuleAsync(stream, fight,
                    ranged ? Challenges.BossRule.NoRangedDamageToEnemies : Challenges.BossRule.NoMeleeDamageToEnemies,
                    $"{quienPega.Name} damages {quien.Name} " + (ranged ? "at range" : "in close combat"));

                // "Mantícoro no debe recibir daños a distancia": only its own boss.
                if (ranged)
                {
                    foreach (int id in WithRule(fight, Challenges.BossRule.NoRangedDamageToBoss))
                    {
                        if (Challenges.Get(id)!.Monsters.Contains(quien.MonsterId))
                            await BreakOneAsync(stream, fight, id, $"{quienPega.Name} damages {quien.Name} at range");
                    }
                }
            }

            if (quien.EsInvocado)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoDamageToEnemySummons,
                                     $"the summon {quien.Name} takes damage");
                return;
            }

            bool summonsStanding = false, othersStanding = false;
            foreach (var uno in fight.Rojo)
            {
                if (!uno.IsAlive || uno.Id == quien.Id) continue;
                if (uno.EsInvocado) summonsStanding = true;
                else if (uno.MonsterId != quien.MonsterId) othersStanding = true;
            }

            if (summonsStanding)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoDamageWhileEnemySummons,
                                     $"{quien.Name} takes damage with enemy summons standing");
            }

            // "La Rata Negra no debe sufrir daños antes de que los otros hayan sido eliminados."
            if (othersStanding)
            {
                foreach (int id in WithRule(fight, Challenges.BossRule.BossUntouchedUntilAlone))
                {
                    if (Challenges.Get(id)!.Monsters.Contains(quien.MonsterId))
                        await BreakOneAsync(stream, fight, id, $"{quien.Name} takes damage and there are other enemies left");
                }
            }
        }

        /// <summary>
        /// A heal. Heartless only allows healing oneself: if the healer and the healed are different
        /// allies, it breaks.
        /// </summary>
        public static async Task HealedAsync(NetworkStream stream, FightInstance fight,
                                             Fighter quienCura, Fighter curado)
        {
            if (!Alguno(fight)) return;

            // The two boss challenges that let one side be healed by nobody, whoever heals.
            await BreakRuleAsync(stream, fight,
                curado.TeamId == 0 ? Challenges.BossRule.NoHealAllies : Challenges.BossRule.NoHealEnemies,
                $"{curado.Name} gets healed by {quienCura.Name}");

            if (!Vivo(fight, SinCorazon)) return;
            if (curado.TeamId != 0 || quienCura.TeamId != 0 || quienCura.Id == curado.Id) return;

            await BreakAsync(stream, fight, SinCorazon,
                             $"{quienCura.Name} ha curado a {curado.Name}");
        }

        /// <summary>An ALLY dies. Only Survivor looks at it.</summary>
        public static async Task AllyDiedAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

            if (!quien.EsInvocado && fight.RoundNumber < 6)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NobodyKilledBeforeRound6,
                                     $"{quien.Name} falls in round {fight.RoundNumber}, before round 6");
            }

            if (Vivo(fight, Superviviente))
                await BreakAsync(stream, fight, Superviviente, $"{quien.Name} has fallen");
        }

        /// <summary>
        /// An ally casts something. The three «do not repeat» and «finish off before changing
        /// target» challenges come from here.
        /// </summary>
        public static async Task CastAsync(NetworkStream stream, FightInstance fight, Fighter quien,
                                           int hechizo, Fighter? victima, int vecesEsteTurno)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

            // Versatile: the same action only once per turn.
            if (Vivo(fight, Versatil) && vecesEsteTurno > 1)
                await BreakAsync(stream, fight, Versatil, $"{quien.Name} repeats spell {hechizo}");

            // Thrifty: the same, but for the WHOLE fight and not just for the turn. The count is kept for
            // the whole fight, not for each ally separately; with a single character it makes no
            // difference, and with several it is the stricter of the two readings.
            bool repetido = !fight.SpellsEverUsed.Add(hechizo);
            if (repetido && Vivo(fight, Ahorrador))
                await BreakAsync(stream, fight, Ahorrador, $"spell {hechizo} had already been used");

            if (victima == null || victima.TeamId == 0) return;

            // Blitzkrieg: it is noted that this one has already been hit.
            fight.Wounded.Add(victima.Id);

            // Duel: the enemy one ally starts on, nobody else touches.
            if (Vivo(fight, Duelo))
            {
                if (!fight.FirstAttacker.TryGetValue(victima.Id, out long primero))
                {
                    fight.FirstAttacker[victima.Id] = quien.Id;
                }
                else if (primero != quien.Id)
                {
                    await BreakAsync(stream, fight, Duelo,
                                     $"{quien.Name} hits {victima.Name}, who was somebody else's");
                }
            }

            // Unpredictable and Assassin's Eye: attacks go to the one pointed out and nobody else.
            foreach (int reto in new[] { Imprevisible, AsesinoAOjo })
            {
                if (!Vivo(fight, reto)) continue;
                if (!fight.ChallengeTargets.TryGetValue(reto, out long senalado)) continue;
                if (senalado == victima.Id) continue;

                await BreakAsync(stream, fight, reto,
                                 $"{quien.Name} hits {victima.Name} instead of the marked one");
            }

            // Focus and Elitist: whoever you start hitting, you finish. The difference in the real
            // game is that Elitist's target is POINTED OUT by the server; here both behave the same,
            // pointing out the first one hit.
            foreach (int reto in new[] { Focalizacion, Elitista })
            {
                if (!Vivo(fight, reto)) continue;

                if (fight.ChallengeFocus == 0 || !SigueVivo(fight, fight.ChallengeFocus))
                {
                    fight.ChallengeFocus = victima.Id;
                }
                else if (fight.ChallengeFocus != victima.Id)
                {
                    await BreakAsync(stream, fight, reto,
                                     $"{quien.Name} switches target without finishing off the previous one");
                }
            }
        }

        /// <summary>An enemy falls: order of levels, with a weapon, and next to an obstacle.</summary>
        public static async Task DiedAsync(NetworkStream stream, FightInstance fight, Fighter victima,
                                           bool conArma, Fighter? quienRemata = null)
        {
            if (!Alguno(fight) || victima.TeamId == 0) return;

            fight.Wounded.Remove(victima.Id);
            fight.KilledOnRound[victima.Id] = fight.RoundNumber;
            if (quienRemata != null && quienRemata.TeamId == 0)
            {
                fight.Killers.Add(quienRemata.Id);
                fight.KillCells.Add(victima.CellId);

                var notes = Notes(fight);
                notes.KillOwners.Add(OwnerOf(quienRemata));
                var playing = fight.CurrentFighter;
                if (playing != null && playing.TeamId == 0 && OwnerOf(playing) == OwnerOf(quienRemata)) notes.TurnKills++;

                // Mirror: «otro aliado debe estar colocado en simetría detrás de este luchador
                // enemigo» -- the killer's cell reflected through the one who falls.
                if (Vivo(fight, Espejo))
                {
                    int behind = Jondo.Unity.World.Maps.MapGeometry.Reflejar(quienRemata.CellId, victima.CellId);
                    bool mirrored = behind >= 0 && fight.Azul.Any(a => a.IsAlive && a.Id != quienRemata.Id && a.CellId == behind);
                    if (!mirrored)
                        await BreakAsync(stream, fight, Espejo, $"{quienRemata.Name} finishes {victima.Name} off with nobody behind it");
                }
            }

            bool quedanEnemigos = false;
            foreach (var uno in fight.Rojo) if (uno.IsAlive) { quedanEnemigos = true; break; }

            await KillOrderAsync(stream, fight, victima);

            // The boss challenges that look at who falls: nobody before round 6, and the enemy
            // summons, which no ally may finish off.
            if (!victima.EsInvocado && fight.RoundNumber < 6)
            {
                string early = $"{victima.Name} falls in round {fight.RoundNumber}, before round 6";
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoEnemyKilledBeforeRound6, early);
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NobodyKilledBeforeRound6, early);
            }
            if (victima.EsInvocado && quienRemata != null && quienRemata.TeamId == 0)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoEnemySummonKilledByAlly,
                                     $"{quienRemata.Name} finishes off the summon {victima.Name}");
            }

            // First: the one pointed out has to be the very first to fall. Once he has, nothing can
            // break it any more, and it is met then.
            if (Vivo(fight, Primero))
            {
                if (!EsElSenalado(fight, Primero, victima.Id))
                    await BreakAsync(stream, fight, Primero,
                                     $"{victima.Name} has fallen before the marked one");
                else
                    await MeetAsync(stream, fight, Primero, $"the marked one {victima.Name} falls first");
            }

            // Last: the one pointed out has to fall last, that is with nobody else standing.
            if (Vivo(fight, Ultimo) && EsElSenalado(fight, Ultimo, victima.Id) && quedanEnemigos)
            {
                await BreakAsync(stream, fight, Ultimo,
                                 $"the marked one {victima.Name} falls and there are enemies left");
            }

            // Hitman: the ones pointed out fall in order, and as soon as one falls another is pointed out.
            if (Vivo(fight, AsesinoASueldo))
            {
                if (!EsElSenalado(fight, AsesinoASueldo, victima.Id))
                {
                    await BreakAsync(stream, fight, AsesinoASueldo,
                                     $"{victima.Name} has fallen, and was not the marked one");
                }
                else if (quedanEnemigos)
                {
                    await SenalarAsync(stream, fight, AsesinoASueldo, UnEnemigoVivo(fight));
                }
            }

            // The little ones first: finishing off is up to the lowest-level ally.
            if (Vivo(fight, LosPequenosAntes) && quienRemata != null && !EsDeLosMasBajos(fight, quienRemata))
            {
                await BreakAsync(stream, fight, LosPequenosAntes,
                                 $"{quienRemata.Name} finishes it off, and is not the lowest level one");
            }

            // Cruel: in increasing order of level. Ordered: the reverse.
            if (Vivo(fight, Cruel) && fight.LastKilledLevel >= 0 && victima.Level < fight.LastKilledLevel)
            {
                await BreakAsync(stream, fight, Cruel,
                                 $"{victima.Name} (level {victima.Level}) falls after one of level " +
                                 $"{fight.LastKilledLevel}");
            }
            if (Vivo(fight, Ordenado) && fight.LastKilledLevel >= 0 && victima.Level > fight.LastKilledLevel)
            {
                await BreakAsync(stream, fight, Ordenado,
                                 $"{victima.Name} (level {victima.Level}) falls after one of level " +
                                 $"{fight.LastKilledLevel}");
            }
            fight.LastKilledLevel = victima.Level;

            // Barbaric: finishing off with a weapon.
            if (Vivo(fight, Barbaro) && !conArma)
                await BreakAsync(stream, fight, Barbaro, $"{victima.Name} falls to a spell");

            // Pinned: every enemy has to fall next to an obstacle.
            if (Vivo(fight, MerecidoPin) && !JuntoAObstaculo(fight, victima.CellId))
                await BreakAsync(stream, fight, MerecidoPin, $"{victima.Name} falls in the open");
        }

        /// <summary>
        /// A boss's "Primero" and "Último". They point at nobody by lot as the generic ones do:
        /// the criterion carries the monster -- "CK#147,1", the Jalató Real falls first -- and the
        /// client already names it in the description, so there is no kwm to send.
        ///
        /// Summons count on neither side: a summoned jalató that falls before the boss is not
        /// "another one before", nor is one still standing "another one after".
        /// </summary>
        private static async Task KillOrderAsync(NetworkStream stream, FightInstance fight, Fighter victima)
        {
            if (victima.EsInvocado) return;

            foreach (var (id, _) in fight.ChallengesFixed.ToArray())
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                var reto = Challenges.Get(id);
                if (reto == null) continue;

                if (reto.KillFirst.Count > 0 && !reto.KillFirst.Contains(victima.MonsterId))
                {
                    bool alreadyDown = false;
                    foreach (var uno in fight.Rojo)
                    {
                        if (uno.Id != victima.Id && !uno.IsAlive && !uno.EsInvocado
                            && reto.KillFirst.Contains(uno.MonsterId)) { alreadyDown = true; break; }
                    }
                    if (!alreadyDown)
                    {
                        await BreakOneAsync(stream, fight, id,
                                            $"{victima.Name} has fallen before the one that had to fall first");
                    }
                }
                else if (reto.KillFirst.Count > 0 && AsksOnlyToKillFirst(reto))
                {
                    // The one that had to fall first has: nothing else it asks can go wrong now.
                    await MeetOneAsync(stream, fight, id, $"{victima.Name} falls first");
                }

                if (reto.KillLast.Count > 0 && reto.KillLast.Contains(victima.MonsterId))
                {
                    bool anotherStanding = false;
                    foreach (var uno in fight.Rojo)
                    {
                        if (uno.Id != victima.Id && uno.IsAlive && !uno.EsInvocado
                            && !reto.KillLast.Contains(uno.MonsterId)) { anotherStanding = true; break; }
                    }
                    if (anotherStanding)
                    {
                        await BreakOneAsync(stream, fight, id,
                                            $"{victima.Name} had to fall last and there are enemies left");
                    }
                }
            }
        }

        /// <summary>
        /// It is over. Whatever has not been broken, met; and if the fight was lost, everything failed,
        /// which is what the real server sends: a run of failure kwl right at the end.
        ///
        /// Returns the BONUS the met ones have earned, added up, as a percentage. Challenges add up
        /// with each other: two at 80 and 65 give 145% extra.
        ///
        /// And this is where achievements are recorded. The challenges the place imposes carry an
        /// achievement and are done once: once one is met, it is written for that character and he does
        /// not get it again. Until now nobody wrote that table, because there was no way of knowing
        /// whether a challenge had been met.
        /// </summary>
        public static async Task<int> FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            if (fight.ChallengesFixed.Count == 0) return 0;

            // Every player of the fight gets the same verdicts and the same bonus. The first one to
            // reach this judges and records; the others are sent what he was sent. Judging again
            // found everything already settled: a party's second player got no kwl and no bonus.
            if (_ends.TryGetValue(fight.FightId, out var judged))
            {
                foreach (byte[] frame in judged.Frames) await WriteFrameAsync(stream, frame);
                foreach (int done in judged.Achievements) DatabaseManager.MarkChallengeDone(GameState.CharacterId, done);
                return judged.Extra;
            }
            var end = new EndVerdict();
            _ends[fight.FightId] = end;

            // These two can only be judged at the end, because until it is over it cannot be known.

            // Distribution: every ally has to have finished somebody off.
            if (won && Vivo(fight, Reparto))
            {
                foreach (var aliado in fight.Azul)
                {
                    if (fight.Killers.Contains(aliado.Id)) continue;
                    await BreakAsync(stream, fight, Reparto, $"{aliado.Name} has not finished anybody off", record: end.Frames);
                    break;
                }
            }

            // Each his own monster: every ally character finished one off, himself or by a summon.
            if (won && Vivo(fight, CadaUnoConSuMonstruo))
            {
                var owners = Notes(fight).KillOwners;
                var idle = fight.Azul.FirstOrDefault(a => !a.EsInvocado && !owners.Contains(a.Id));
                if (idle != null)
                    await BreakAsync(stream, fight, CadaUnoConSuMonstruo, $"{idle.Name} has not finished anybody off", record: end.Frames);
            }

            // Two for one: the last kill, in a turn the fight did not let end, needed its second.
            if (won && Vivo(fight, DosPorUno) && Notes(fight).TurnKills == 1)
                await BreakAsync(stream, fight, DosPorUno, "the last enemy fell alone in its turn", record: end.Frames);

            // Dum: all of them have to fall in the same round.
            if (won && Vivo(fight, Dum))
            {
                int primera = -1;
                foreach (var ronda in fight.KilledOnRound.Values)
                {
                    if (primera < 0) primera = ronda;
                    else if (ronda != primera)
                    {
                        await BreakAsync(stream, fight, Dum, record: end.Frames, porque:
                                         $"they fell in different rounds ({primera} and {ronda})");
                        break;
                    }
                }
            }

            int extra = 0;
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                // One met before the end counts as met when the fight is won. Lost, it is left as
                // it was shown: no failure comes after its success.
                bool metEarly = fight.ChallengesMet.Contains(id);
                if (fight.ChallengesBroken.Contains(id) && !metEarly) continue;
                if (metEarly && !won) continue;

                bool cumplido = won;
                fight.ChallengesBroken.Add(id);
                byte[] verdict = ConnectionProtocol.Push(Op.Kwl, Network.FightProtocol.BuildChallengeResult(id, cumplido));
                end.Frames.Add(verdict);
                // Sent again for one met before: only the player whose blow met it was told then,
                // and the client takes the same verdict twice as once (flr.bdql sets the state).
                await WriteFrameAsync(stream, verdict);

                var reto = Challenges.Get(id);
                Console.WriteLine($"[Challenges] «{reto?.Name ?? id.ToString()}» " +
                                  (cumplido ? $"CUMPLIDO (+{percent} %)."
                                            : "failed (the fight was lost)."));

                if (!cumplido) continue;
                extra += percent;
                end.Won.Add(id);

                // The ones the place imposes are the ones carrying an achievement.
                if (reto != null && reto.NeedsMonster)
                {
                    end.Achievements.Add(id);
                    DatabaseManager.MarkChallengeDone(GameState.CharacterId, id);
                    Console.WriteLine($"[Challenges] Achievement «{reto.Name}» earned; it will not come up again.");
                }
            }
            end.Extra = extra;
            return extra;
        }

        /// <summary>What the end of a fight judged, to be sent the same to each of its players.</summary>
        private sealed class EndVerdict
        {
            public List<byte[]> Frames { get; } = new List<byte[]>();
            public List<int> Achievements { get; } = new List<int>();

            /// <summary>Every challenge validated, not only the ones that carry an achievement.</summary>
            public List<int> Won { get; } = new List<int>();

            public int Extra { get; set; }
        }

        private static readonly ConcurrentDictionary<long, EndVerdict> _ends = new ConcurrentDictionary<long, EndVerdict>();

        /// <summary>
        /// The challenges validated at the end of this fight, once <see cref="FightEndedAsync"/>
        /// has judged it; empty before, and for a fight with none. The achievements that count a
        /// monster "beaten with a challenge won" (Ef) are fed from here.
        /// </summary>
        public static IReadOnlyCollection<int> WonIn(FightInstance fight)
            => _ends.TryGetValue(fight.FightId, out var end) ? end.Won : (IReadOnlyCollection<int>)System.Array.Empty<int>();

        /// <summary>The fight is over for everybody: its verdicts go.</summary>
        public static void Forget(FightInstance fight)
        {
            _ends.TryRemove(fight.FightId, out _);
            _notes.TryRemove(fight.FightId, out _);
        }

        // ─── What the boss rules need remembered ─────────────────────────────────

        /// <summary>What a fight writes down for the boss rules and nobody else needs.</summary>
        private sealed class FightNotes
        {
            /// <summary>Ally → the cell the fight started on.</summary>
            public Dictionary<long, int> StartCells { get; } = new Dictionary<long, int>();

            /// <summary>Whether the one playing began his turn in line with an enemy.</summary>
            public bool BeganInLineWithEnemy { get; set; }

            /// <summary>Enemy → the ally (a summon's master) who first hit him: Each his own monster.</summary>
            public Dictionary<long, long> Claims { get; } = new Dictionary<long, long>();

            /// <summary>The allies (summons' masters) who finished somebody off.</summary>
            public HashSet<long> KillOwners { get; } = new HashSet<long>();

            /// <summary>The kills of whoever plays, in this turn: Two for one.</summary>
            public int TurnKills { get; set; }
        }

        private static readonly ConcurrentDictionary<long, FightNotes> _notes = new ConcurrentDictionary<long, FightNotes>();

        private static FightNotes Notes(FightInstance fight) => _notes.GetOrAdd(fight.FightId, _ => new FightNotes());

        /// <summary>The challenges fixed and not broken that are judged by that rule.</summary>
        private static List<int> WithRule(FightInstance fight, Challenges.BossRule rule)
        {
            var found = new List<int>();
            foreach (var (id, _) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                if (Challenges.Get(id)?.Rule == rule) found.Add(id);
            }
            return found;
        }

        /// <summary>Whether one with a boss rule is still in play: so as not to work things out for nothing.</summary>
        private static bool AnyRule(FightInstance fight)
        {
            foreach (var (id, _) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                var reto = Challenges.Get(id);
                if (reto != null && reto.Rule != Challenges.BossRule.None) return true;
            }
            return false;
        }

        private static async Task BreakRuleAsync(NetworkStream stream, FightInstance fight,
                                                 Challenges.BossRule rule, string why)
        {
            foreach (int id in WithRule(fight, rule)) await BreakOneAsync(stream, fight, id, why);
        }

        /// <summary>Whether he is in line -- or diagonal -- with one of those still standing.</summary>
        private static bool InLine(IEnumerable<Fighter> fighters, Fighter quien, bool diagonal)
        {
            var (x, y) = MapGeometry.CellToPoint(quien.CellId);
            foreach (var otro in fighters)
            {
                if (!otro.IsAlive || otro.Id == quien.Id) continue;
                var (ox, oy) = MapGeometry.CellToPoint(otro.CellId);

                if (diagonal)
                {
                    int dx = Math.Abs(ox - x), dy = Math.Abs(oy - y);
                    if (dx != 0 && dx == dy) return true;
                }
                else if (ox == x || oy == y) return true;
            }
            return false;
        }

        /// <summary>How many cells away the nearest of those is. With none, very far.</summary>
        private static int Nearest(IEnumerable<Fighter> fighters, Fighter quien)
        {
            int best = int.MaxValue;
            foreach (var otro in fighters)
            {
                if (!otro.IsAlive || otro.Id == quien.Id) continue;
                int distance = MapGeometry.Distance(quien.CellId, otro.CellId);
                if (distance < best) best = distance;
            }
            return best;
        }

        /// <summary>
        /// The bonus a won fight's challenges will give, worked out without judging anything aloud:
        /// the ones not broken, less "Reparto" when an ally finished nobody, "Dum" when they fell
        /// on different rounds, "Cada uno con su monstruo" and "El dos por uno" likewise -- the ones
        /// <see cref="FightEndedAsync"/> can only judge at the end.
        /// </summary>
        public static int EndBonus(FightInstance fight, bool won)
        {
            if (!won) return 0;
            int extra = 0;
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id) && !fight.ChallengesMet.Contains(id)) continue;
                if (id == Reparto && fight.Azul.Any(a => !fight.Killers.Contains(a.Id))) continue;
                if (id == CadaUnoConSuMonstruo && fight.Azul.Any(a => !a.EsInvocado && !Notes(fight).KillOwners.Contains(a.Id))) continue;
                if (id == DosPorUno && Notes(fight).TurnKills == 1) continue;
                if (id == Dum && fight.KilledOnRound.Values.Distinct().Count() > 1) continue;
                extra += percent;
            }
            return extra;
        }

        // ─── Pieces ─────────────────────────────────────────────────────────────

        /// <summary>Is there anything to watch in this fight?</summary>
        private static bool Alguno(FightInstance fight)
            => fight.ChallengesFixed.Count > fight.ChallengesBroken.Count;

        /// <summary>Is that challenge in play and not broken yet?</summary>
        /// <remarks>
        /// Asked by KIND and not by number: the "Prudente" the Jalató Real imposes is 121 and is
        /// judged as 40 is, so it counts as alive if either of the two is. Which is equivalent to
        /// which, <see cref="Challenges.KindOf"/> says.
        /// </remarks>
        private static bool Vivo(FightInstance fight, int id)
        {
            foreach (var (suyo, _) in fight.ChallengesFixed)
            {
                if (Challenges.KindOf(suyo) == id && !fight.ChallengesBroken.Contains(suyo)) return true;
            }
            return false;
        }

        private static bool SigueVivo(FightInstance fight, long fighterId)
        {
            foreach (var uno in fight.Rojo) if (uno.Id == fighterId) return uno.IsAlive;
            return false;
        }

        /// <summary>
        /// Pointing out an enemy for a challenge, and telling the client with the kwm so that it puts
        /// the mark on him.
        ///
        /// The target travels with its identifier and with ITS CELL, but the cell is a snapshot of the
        /// moment: the server does NOT send it again when the target walks. It was checked by following
        /// the creature through the capture -- it moved from 262 to 218 and no challenge message came
        /// out -- and whoever keeps track of it is the client, by the identifier. The two messages that
        /// carry different cells come from two reconnections, and each one is the whole snapshot.
        ///
        /// Pointing out again in the middle of a fight IS NOT MEASURED: in the 305 captures there is
        /// not one pair of messages of the same challenge with a different target. What Hitman's
        /// description does say is that «every time the indicated enemy is eliminated, a new enemy is
        /// immediately indicated», so it is done, and through this same message, which is the only one
        /// that can carry it.
        /// </summary>
        private static async Task SenalarAsync(NetworkStream stream, FightInstance fight, int reto,
                                               Fighter? aQuien)
        {
            if (aQuien == null) return;

            fight.ChallengeTargets[reto] = aQuien.Id;

            int porcentaje = 0;
            foreach (var (id, pct) in fight.ChallengesFixed) if (id == reto) porcentaje = pct;

            byte[] ldd = Network.FightProtocol.BuildChallenge(
                reto, porcentaje, new[] { (aQuien.CellId, aQuien.Id) });

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwm,
                Network.FightProtocol.BuildChallengeObjective(ldd)));

            Console.WriteLine($"[Challenges] «{Challenges.Get(reto)?.Name ?? reto.ToString()}» marks " +
                              $"{aQuien.Name}.");
        }

        /// <summary>Any one of those still standing. At random, which is the honest thing.</summary>
        private static Fighter? UnEnemigoVivo(FightInstance fight)
        {
            var vivos = new List<Fighter>();
            foreach (var uno in fight.Rojo) if (uno.IsAlive) vivos.Add(uno);
            return vivos.Count == 0 ? null : vivos[_dado.Next(vivos.Count)];
        }

        /// <summary>The shared die: several fights and sessions draw from it at once, and a plain
        /// Random used from two threads breaks for good and draws zeros.</summary>
        private static readonly Random _dado = Random.Shared;

        /// <summary>Is that fighter pointed out for that challenge?</summary>
        private static bool EsElSenalado(FightInstance fight, int reto, long quien)
            => fight.ChallengeTargets.TryGetValue(reto, out long suyo) && suyo == quien;

        /// <summary>«No se ha conseguido el reto $challenge{1} por culpa de <b>{0}</b>.»</summary>
        private const int ChallengeFailedMessage = 188;

        /// <summary>
        /// Breaking a challenge: it is announced once, at the moment, and not looked at again.
        ///
        /// And after the kwl a chat notice goes out saying WHOSE FAULT it was, which is something that
        /// shows in the captures and we had not seen: in the dungeon, when an area spell killed three at
        /// once and blew the order challenge, the server sent
        ///
        ///   kwl 0823
        ///   lqn 10bc01 220c "Hero-Example" 2202 "35"
        ///
        /// that is message 188 with the culprit's name and the challenge number. Both in the same
        /// millisecond, and the kwl AHEAD of the three deaths: the server considers the challenge broken
        /// on the hit, not on the death.
        /// </summary>
        private static async Task BreakAsync(NetworkStream stream, FightInstance fight, int id,
                                             string porque, string culpable = "", List<byte[]>? record = null)
        {
            // Every one of that kind: the generic one and the boss's break for the same thing, together.
            var sameKind = new List<int>();
            foreach (var (suyo, _) in fight.ChallengesFixed)
            {
                if (Challenges.KindOf(suyo) == id) sameKind.Add(suyo);
            }

            foreach (int uno in sameKind) await BreakOneAsync(stream, fight, uno, porque, culpable, record);
        }

        /// <summary>Breaks ONE challenge, by its number: what <see cref="BreakAsync"/> does to each.</summary>
        private static async Task BreakOneAsync(NetworkStream stream, FightInstance fight, int id,
                                                string porque, string culpable = "", List<byte[]>? record = null)
        {
            if (!fight.ChallengesBroken.Add(id)) return;

            byte[] broken = ConnectionProtocol.Push(Op.Kwl, Network.FightProtocol.BuildChallengeResult(id, false));
            await WriteFrameAsync(stream, broken);

            if (culpable.Length == 0) culpable = GameState.CharacterName;
            byte[] notice = ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(ChallengeFailedMessage, culpable, id.ToString()));
            await WriteFrameAsync(stream, notice);
            record?.Add(broken);
            record?.Add(notice);

            Console.WriteLine($"[Challenges] «{Challenges.Get(id)?.Name ?? id.ToString()}» BROKEN: {porque}.");
        }

        /// <summary>
        /// Meeting a challenge before the end, the moment nothing left can break it: the client
        /// draws it green then, as the real game does when the one that had to fall first falls.
        /// Until then it shows «in progress», since a success otherwise only goes at the end.
        /// </summary>
        private static async Task MeetAsync(NetworkStream stream, FightInstance fight, int id, string porque)
        {
            var sameKind = new List<int>();
            foreach (var (suyo, _) in fight.ChallengesFixed)
            {
                if (Challenges.KindOf(suyo) == id) sameKind.Add(suyo);
            }

            foreach (int uno in sameKind) await MeetOneAsync(stream, fight, uno, porque);
        }

        /// <summary>Meets ONE challenge, by its number: what <see cref="MeetAsync"/> does to each.</summary>
        private static async Task MeetOneAsync(NetworkStream stream, FightInstance fight, int id, string porque)
        {
            if (!fight.ChallengesBroken.Add(id)) return;
            fight.ChallengesMet.Add(id);

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwl, Network.FightProtocol.BuildChallengeResult(id, true)));
            Console.WriteLine($"[Challenges] «{Challenges.Get(id)?.Name ?? id.ToString()}» met already: {porque}.");
        }

        /// <summary>
        /// Whether a boss's «CK#» asks for nothing but its monster falling first, and so is met
        /// when it does: not when it also wants one last, a turn limit or a rule of its own.
        /// </summary>
        private static bool AsksOnlyToKillFirst(Challenges.Challenge reto)
            => reto.KillLast.Count == 0 && reto.TurnLimit == 0 && reto.Rule == Challenges.BossRule.None
               && !reto.PartySizeOnly && Challenges.KindOf(reto.Id) == 0;

        /// <summary>Does he have somebody of that side next to him? Next to is one cell, no diagonals.</summary>
        private static bool Adyacente(FightInstance fight, Fighter quien, int bando)
        {
            var lista = bando == 0 ? fight.Azul : fight.Rojo;
            foreach (var otro in lista)
            {
                if (!otro.IsAlive || otro.Id == quien.Id) continue;
                if (MapGeometry.Distance(quien.CellId, otro.CellId) == 1) return true;
            }
            return false;
        }

        /// <summary>
        /// Is he lined up with some ally? Straight means sharing a row or a column; diagonal, being
        /// the same distance apart on both.
        /// </summary>
        private static bool AlineadoConUnAliado(FightInstance fight, Fighter quien, bool diagonal)
        {
            var (x, y) = MapGeometry.CellToPoint(quien.CellId);
            foreach (var otro in fight.Azul)
            {
                if (!otro.IsAlive || otro.Id == quien.Id) continue;
                var (ox, oy) = MapGeometry.CellToPoint(otro.CellId);

                if (diagonal)
                {
                    int dx = Math.Abs(ox - x), dy = Math.Abs(oy - y);
                    if (dx != 0 && dx == dy) return true;
                }
                else if (ox == x || oy == y) return true;
            }
            return false;
        }

        /// <summary>Can some enemy see him from where he is?</summary>
        private static bool LoVeAlgunEnemigo(FightInstance fight, Fighter quien)
        {
            MapManager.LosBlockingCells.TryGetValue(fight.MapId, out var tapan);
            foreach (var enemigo in fight.Rojo)
            {
                if (!enemigo.IsAlive) continue;
                if (MapGeometry.HasLineOfSight(enemigo.CellId, quien.CellId, tapan)) return true;
            }
            return false;
        }

        /// <summary>
        /// Is the cell next to an obstacle? Holes count and so do the map's edges, which is what the
        /// challenge's description says: an edge cell has fewer than four neighbours, and that already
        /// leaves it next to something.
        /// </summary>
        private static bool JuntoAObstaculo(FightInstance fight, int cell)
        {
            if (!MapManager.FightWalkableCells.TryGetValue(fight.MapId, out var pisables)) return false;

            int vecinas = 0;
            foreach (int vecina in MapGeometry.GetNeighbors(cell))
            {
                vecinas++;
                if (!pisables.Contains(vecina)) return true;
            }
            return vecinas < 4;
        }
    }
}
