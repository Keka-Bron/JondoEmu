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
    /// Vigilar los retos durante el combate y decir si se cumplen o se rompen.
    ///
    /// ─── Cómo se avisa ──────────────────────────────────────────────────────────────────────
    ///
    /// Con un solo mensaje, el <c>kwl { f1: cuál, f2: cumplido }</c>, y con una regla de tiempo
    /// que está medida: el FALLO se manda en el instante en que ocurre, a mitad del combate, y el
    /// ÉXITO al final, a menos de once tramas del <c>jyg</c>. En una derrota llegan todos los
    /// fallos seguidos justo antes del final.
    ///
    /// No hay latido de «sigue vivo»: el cliente da el reto por vivo desde que se cierra la
    /// preparación hasta que le llega su kwl. Por eso hay que mandarlo siempre, aunque sea para
    /// decir que no: si no, el reto se queda girando en la pantalla del jugador para siempre.
    ///
    /// ─── De dónde sale cada regla ───────────────────────────────────────────────────────────
    ///
    /// De la DESCRIPCIÓN del reto, que el cliente trae traducida y dice en castellano llano lo que
    /// hay que hacer. El otro campo, <c>completionCriterion</c>, es un idioma corto sin glosario
    /// —«TD&lt;2,hc0,e1»— del que sólo se puede adivinar, así que se usa la descripción y el
    /// criterio queda al lado como comprobación.
    ///
    /// ─── Lo que aquí NO se vigila ───────────────────────────────────────────────────────────
    ///
    /// El reto 35, «Asesino a sueldo», que exige matar en un orden que el servidor va señalando
    /// sobre la marcha. Eso necesita mandar el objetivo por el <c>kwm</c> y volver a señalarlo
    /// cada vez que cae uno, y el objetivo señalado no se ha medido nunca en preparación —viaja
    /// con la casilla a menos uno—. Se queda fuera, y por eso ni se ofrece.
    /// </summary>
    public static class ChallengeWatcher
    {
        // Los que este vigilante sabe llevar. El resto no se ofrece.
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

        /// <summary>
        /// Los que se saben vigilar, y con el porcentaje que se les pone SI el suyo no ha pasado
        /// nunca por el cable.
        ///
        /// Ese número no está medido y no puede estarlo: la tabla del cliente no trae ninguna
        /// bonificación, la pone el servidor. Pero tampoco se ha echado a suertes. La mitad de
        /// estos retos son el MELLIZO de uno que sí está medido —«orden decreciente» frente a
        /// «orden creciente», «nunca pegado a un aliado» frente a «siempre pegado»— y a ésos se
        /// les da lo que vale su gemelo, que es lo más parecido a una medida que hay. Al resto,
        /// el suelo de lo observado, que es 50: pedir de más nunca, y así un reto que resulte ser
        /// más fácil de lo que parece no regala nada.
        ///
        /// Cero significa «éste ya lo trae medido, no le pongas nada».
        /// </summary>
        public static readonly Dictionary<int, int> Watched = new Dictionary<int, int>
        {
            // Medidos: el porcentaje sale del cable.
            [Zombi] = 0, [Estatua] = 0, [Versatil] = 0, [Barbaro] = 0, [Cruel] = 0,
            [Intocable] = 0, [Focalizacion] = 0, [Elitista] = 0, [Audaz] = 0, [Pegajoso] = 0,
            [Prudente] = 0, [MismoLinaje] = 0, [LineaDeMira] = 0, [MerecidoPin] = 0,
            [EntreLasSombras] = 0,

            // Mellizos de uno medido: lo que vale su gemelo.
            [Ordenado] = 60,        // el revés del Cruel (10), que va a 60
            [Anacoreta] = 75,       // el revés del Pegajoso (37), que va a 75
            [SiNoSeVe] = 85,        // el revés de En línea de mira (969), que va a 85
            [Diagonal] = 65,        // el Mismo linaje (964) pero en diagonal, que va a 65
            [Ahorrador] = 90,       // el Versátil (6) llevado a todo el combate, que va a 90
            [Nomada] = 80,          // el Zombi (1) al revés, gastarlos todos; el Zombi va a 80

            // Sin gemelo: el suelo de lo medido.
            [Superviviente] = 50, [Reparto] = 50, [Elemental] = 50, [Blitzkrieg] = 50,

            // Los que necesitan que el servidor SEÑALE un enemigo. El Asesino a sueldo trae su
            // porcentaje medido; los demás van al suelo.
            [AsesinoASueldo] = 0,
            [Primero] = 50, [Ultimo] = 50, [Imprevisible] = 50, [AsesinoAOjo] = 50,
            [LosPequenosAntes] = 50, [Duelo] = 50, [Conquistador] = 50, [Dum] = 50,
            [SinCorazon] = 50,
        };

        /// <summary>
        /// Los que necesitan un enemigo señalado, y cuándo se vuelve a señalar.
        ///
        ///   al empezar   Primero, Último y Asesino a sueldo: uno para todo el combate, y el
        ///                Asesino a sueldo además señala otro cada vez que cae el suyo
        ///   cada ronda   Imprevisible: uno nuevo al principio de cada turno global
        ///   cada turno   Asesino a ojo: el más cercano al que va a jugar
        /// </summary>
        private static readonly int[] SenalanAlEmpezar = { Primero, Ultimo, AsesinoASueldo };

        // ─── Los avisos que le manda el combate ─────────────────────────────────

        /// <summary>
        /// Arranca el combate: se señalan los objetivos que hagan falta.
        ///
        /// Va detrás del jyy, que es donde salen los tres kwm de las capturas. El enemigo se elige
        /// a suertes entre los que hay, que es lo único razonable: cuál escoge el servidor real no
        /// se puede saber con tres muestras.
        /// </summary>
        public static async Task FightStartedAsync(NetworkStream stream, FightInstance fight)
        {
            if (fight.ChallengesFixed.Count == 0) return;

            // De dónde sale cada aliado, para «Salida de ring». Una vez por combate, no por jugador.
            var apuntes = Notes(fight);
            lock (apuntes)
            {
                if (apuntes.StartCells.Count == 0)
                {
                    foreach (var aliado in fight.Azul) apuntes.StartCells[aliado.Id] = aliado.CellId;
                }
            }

            foreach (int reto in SenalanAlEmpezar)
            {
                if (Vivo(fight, reto)) await SenalarAsync(stream, fight, reto, UnEnemigoVivo(fight));
            }

            if (Vivo(fight, Imprevisible))
                await SenalarAsync(stream, fight, Imprevisible, UnEnemigoVivo(fight));
        }

        /// <summary>Empieza el turno de alguien: se apunta de dónde sale y con cuántos PM.</summary>
        public static void TurnStarted(FightInstance fight, Fighter quien)
        {
            fight.TurnStartCell = quien.CellId;
            fight.TurnStartMp = quien.CurrentMP;
            fight.TurnTackledMp = 0;
            fight.KillCells.Clear();

            // «Hay gente por aquí» deja elegir: empezar O acabar el turno en línea con un enemigo.
            if (quien.TeamId == 0 && fight.ChallengesFixed.Count > 0)
            {
                Notes(fight).BeganInLineWithEnemy = Alineado(fight.Rojo, quien, diagonal: false);
            }
        }

        /// <summary>
        /// Empieza ronda nueva: el Imprevisible señala otro. Es literalmente lo que dice su
        /// descripción, «el enemigo indicado al principio de cada turno global».
        /// </summary>
        public static async Task RoundStartedAsync(NetworkStream stream, FightInstance fight)
        {
            // El «Dúo», el «Trío» y el «Crono» de un jefe: ganar en MENOS de N turnos. En cuanto
            // empieza la ronda N ya no se puede, y se dice entonces, que es cuando ocurre.
            foreach (var (id, _) in fight.ChallengesFixed.ToArray())
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                int tope = Challenges.Get(id)?.TurnLimit ?? 0;
                if (tope > 0 && fight.RoundNumber >= tope)
                {
                    await BreakOneAsync(stream, fight, id,
                                        $"empieza la ronda {fight.RoundNumber} y había que ganar antes de la {tope}");
                }
            }

            if (Vivo(fight, Imprevisible))
                await SenalarAsync(stream, fight, Imprevisible, UnEnemigoVivo(fight));
        }

        /// <summary>
        /// Le toca jugar a un aliado: el Asesino a ojo señala al enemigo que tenga más cerca, que
        /// es lo que pide su descripción, «el más cercano a él al principio de cada turno».
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
        /// Acaba el turno de alguien. Aquí se juzgan los retos de posición, que son la mitad.
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

            // Estatua: acabar donde empezaste.
            if (Vivo(fight, Estatua) && quien.CellId != fight.TurnStartCell)
            {
                await BreakAsync(stream, fight, Estatua, $"{quien.Name} se ha movido");
            }

            // Nómada: al revés que el Zombi, hay que gastarlos TODOS.
            if (Vivo(fight, Nomada) && quien.CurrentMP > 0)
            {
                await BreakAsync(stream, fight, Nomada,
                                 $"a {quien.Name} le sobran {quien.CurrentMP} PM");
            }

            bool pegadoAEnemigo = Adyacente(fight, quien, 1);
            bool pegadoAAliado = Adyacente(fight, quien, 0);

            if (Vivo(fight, Audaz) && !pegadoAEnemigo)
                await BreakAsync(stream, fight, Audaz, $"{quien.Name} acaba lejos de todo enemigo");

            if (Vivo(fight, Prudente) && pegadoAEnemigo)
                await BreakAsync(stream, fight, Prudente, $"{quien.Name} acaba pegado a un enemigo");

            if (Vivo(fight, Pegajoso) && !pegadoAAliado)
                await BreakAsync(stream, fight, Pegajoso, $"{quien.Name} acaba sin ningún aliado al lado");

            if (Vivo(fight, Anacoreta) && pegadoAAliado)
                await BreakAsync(stream, fight, Anacoreta, $"{quien.Name} acaba pegado a un aliado");

            if (Vivo(fight, MismoLinaje) && !AlineadoConUnAliado(fight, quien, diagonal: false))
                await BreakAsync(stream, fight, MismoLinaje, $"{quien.Name} acaba sin alinearse con nadie");

            if (Vivo(fight, Diagonal) && !AlineadoConUnAliado(fight, quien, diagonal: true))
                await BreakAsync(stream, fight, Diagonal, $"{quien.Name} acaba sin ningún aliado en diagonal");

            if (Vivo(fight, EntreLasSombras) && !JuntoAObstaculo(fight, quien.CellId))
                await BreakAsync(stream, fight, EntreLasSombras, $"{quien.Name} acaba al descubierto");

            bool leVen = LoVeAlgunEnemigo(fight, quien);

            if (Vivo(fight, LineaDeMira) && !leVen)
                await BreakAsync(stream, fight, LineaDeMira, $"a {quien.Name} no le ve ningún enemigo");

            if (Vivo(fight, SiNoSeVe) && leVen)
                await BreakAsync(stream, fight, SiNoSeVe, $"a {quien.Name} le ve un enemigo");

            // Conquistador: si has rematado a alguien este turno, acabas en su casilla.
            if (Vivo(fight, Conquistador) && fight.KillCells.Count > 0
                && !fight.KillCells.Contains(quien.CellId))
            {
                await BreakAsync(stream, fight, Conquistador,
                                 $"{quien.Name} remató pero no acaba en la casilla del muerto");
            }

            await BossPositionAsync(stream, fight, quien);
        }

        /// <summary>
        /// Los retos de jefe que miran dónde se acaba el turno y que sólo lo dicen en su
        /// descripción. Son primos de los de arriba: en línea o en diagonal, cerca o lejos, pero
        /// respecto a los enemigos o con un número de casillas propio.
        /// </summary>
        private static async Task BossPositionAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!AlgunaRegla(fight)) return;

            bool lineaEnemigo = Alineado(fight.Rojo, quien, diagonal: false);
            bool diagonalEnemigo = Alineado(fight.Rojo, quien, diagonal: true);
            bool lineaAliado = Alineado(fight.Azul, quien, diagonal: false);
            bool diagonalAliado = Alineado(fight.Azul, quien, diagonal: true);
            int alEnemigo = MasCercano(fight.Rojo, quien);
            int alAliado = MasCercano(fight.Azul, quien);
            var apuntes = Notes(fight);

            async Task Si(bool roto, Challenges.BossRule regla, string porque)
            {
                if (roto) await BreakRuleAsync(stream, fight, regla, $"{quien.Name} {porque}");
            }

            await Si(!lineaEnemigo, Challenges.BossRule.EndInLineWithEnemy, "acaba sin alinearse con ningún enemigo");
            await Si(!diagonalEnemigo, Challenges.BossRule.EndDiagonalToEnemy, "acaba sin ningún enemigo en diagonal");
            await Si(lineaEnemigo, Challenges.BossRule.NeverInLineWithEnemy, "acaba en línea con un enemigo");
            await Si(lineaEnemigo || diagonalEnemigo, Challenges.BossRule.NeverLineOrDiagonalEnemy,
                     "acaba en línea o en diagonal con un enemigo");
            await Si(lineaAliado, Challenges.BossRule.NeverInLineWithAlly, "acaba en línea con un aliado");
            await Si(lineaEnemigo || lineaAliado, Challenges.BossRule.NeverLineEnemyOrAlly,
                     "acaba en línea con alguien");
            await Si(diagonalEnemigo || diagonalAliado, Challenges.BossRule.NeverDiagonalEnemyOrAlly,
                     "acaba en diagonal con alguien");
            await Si(alEnemigo > 5, Challenges.BossRule.EndNearEnemy5, "acaba a más de 5 casillas de todo enemigo");
            await Si(alAliado <= 3, Challenges.BossRule.EndFarFromAllies3, "acaba a 3 casillas o menos de un aliado");
            await Si(alAliado <= 4, Challenges.BossRule.EndFarFromAllies4, "acaba a 4 casillas o menos de un aliado");
            await Si(!apuntes.BeganInLineWithEnemy && !lineaEnemigo, Challenges.BossRule.BeginOrEndInLineWithEnemy,
                     "ni empieza ni acaba en línea con un enemigo");

            // El que no estaba al empezar —una invocación— no tiene casilla de salida que guardar.
            await Si(apuntes.StartCells.TryGetValue(quien.Id, out int salida) && quien.CellId != salida,
                     Challenges.BossRule.EndOnStartCell, "no acaba en su casilla de inicio");
        }

        /// <summary>¿Es de los aliados de menor nivel? Puede haber varios empatados.</summary>
        private static bool EsDeLosMasBajos(FightInstance fight, Fighter quien)
        {
            int menor = int.MaxValue;
            foreach (var uno in fight.Azul) if (uno.IsAlive && uno.Level < menor) menor = uno.Level;
            return quien.Level <= menor;
        }

        /// <summary>
        /// Empieza el turno de un ENEMIGO. Sólo hace falta para el Blitzkrieg: al que le pegas,
        /// lo rematas antes de que le toque jugar.
        /// </summary>
        public static async Task EnemyTurnStartedAsync(NetworkStream stream, FightInstance fight,
                                                       Fighter enemigo)
        {
            if (!Alguno(fight) || enemigo.TeamId == 0) return;

            if (Vivo(fight, Blitzkrieg) && enemigo.IsAlive && fight.Wounded.Contains(enemigo.Id))
            {
                await BreakAsync(stream, fight, Blitzkrieg,
                                 $"a {enemigo.Name} le tocó jugar y seguía vivo");
            }
        }

        /// <summary>
        /// Alguien pierde vida. De aquí salen el Intocable —si el que la pierde es aliado— y el
        /// Elemental, que mira con qué se pega.
        /// </summary>
        public static async Task DamagedAsync(NetworkStream stream, FightInstance fight,
                                              Fighter quien, int cuanto, Fighter quienPega, int elemento)
        {
            if (!Alguno(fight) || cuanto <= 0) return;

            await BossDamageAsync(stream, fight, quien, quienPega, elemento);

            if (quien.TeamId == 0)
            {
                if (Vivo(fight, Intocable))
                    await BreakAsync(stream, fight, Intocable, $"{quien.Name} ha perdido {cuanto} de vida");
                return;
            }

            // Elemental: el primer elemento con el que se pega manda para el resto del combate.
            if (Vivo(fight, Elemental) && quienPega.TeamId == 0 && elemento != 0)
            {
                if (fight.DamageElement == 0) fight.DamageElement = elemento;
                else if (fight.DamageElement != elemento)
                {
                    await BreakAsync(stream, fight, Elemental,
                                     $"{quienPega.Name} pega con el elemento {elemento} y antes fue " +
                                     $"con el {fight.DamageElement}");
                }
            }
        }

        /// <summary>El daño de empuje viaja sin elemento: así lo manda el combate.</summary>
        private const int Empuje = -1;

        /// <summary>
        /// Los retos de jefe que miran un daño: desde dónde se pega, a quién, y con qué.
        ///
        /// «A distancia» es no estar pegado al que recibe, en el momento del golpe. Un veneno o
        /// un glifo que hace daño con su dueño lejos cuenta como a distancia: es la lectura que
        /// rompe de más y nunca regala el reto.
        /// </summary>
        private static async Task BossDamageAsync(NetworkStream stream, FightInstance fight,
                                                  Fighter quien, Fighter quienPega, int elemento)
        {
            if (!AlgunaRegla(fight)) return;

            if (quien.TeamId == 0)
            {
                if (elemento == Empuje)
                {
                    await BreakRuleAsync(stream, fight, Challenges.BossRule.NoPushDamageToAllies,
                                         $"{quien.Name} sufre daños de empuje");
                }
                return;
            }

            if (elemento == Empuje)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoPushDamageToEnemies,
                                     $"{quien.Name} sufre daños de empuje");
            }
            else if (quienPega is { TeamId: 0 })
            {
                bool lejos = MapGeometry.Distance(quienPega.CellId, quien.CellId) > 1;
                await BreakRuleAsync(stream, fight,
                    lejos ? Challenges.BossRule.NoRangedDamageToEnemies : Challenges.BossRule.NoMeleeDamageToEnemies,
                    $"{quienPega.Name} daña a {quien.Name} " + (lejos ? "a distancia" : "cuerpo a cuerpo"));

                // «Mantícoro no debe recibir daños a distancia»: sólo el suyo.
                if (lejos)
                {
                    foreach (int id in ConRegla(fight, Challenges.BossRule.NoRangedDamageToBoss))
                    {
                        if (Challenges.Get(id)!.Monsters.Contains(quien.MonsterId))
                            await BreakOneAsync(stream, fight, id, $"{quienPega.Name} daña a {quien.Name} a distancia");
                    }
                }
            }

            if (quien.EsInvocado)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoDamageToEnemySummons,
                                     $"la invocación {quien.Name} sufre daños");
                return;
            }

            bool hayInvocaciones = false, hayOtros = false;
            foreach (var uno in fight.Rojo)
            {
                if (!uno.IsAlive || uno.Id == quien.Id) continue;
                if (uno.EsInvocado) hayInvocaciones = true;
                else if (uno.MonsterId != quien.MonsterId) hayOtros = true;
            }

            if (hayInvocaciones)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoDamageWhileEnemySummons,
                                     $"{quien.Name} sufre daños con invocaciones enemigas en pie");
            }

            // «La Rata Negra no debe sufrir daños antes de que los otros hayan sido eliminados.»
            if (hayOtros)
            {
                foreach (int id in ConRegla(fight, Challenges.BossRule.BossUntouchedUntilAlone))
                {
                    if (Challenges.Get(id)!.Monsters.Contains(quien.MonsterId))
                        await BreakOneAsync(stream, fight, id, $"{quien.Name} sufre daños y aún quedan otros enemigos");
                }
            }
        }

        /// <summary>
        /// Una curación. El Sin Corazón sólo deja curarse a uno mismo: si el que cura y el curado
        /// son aliados distintos, se rompe.
        /// </summary>
        public static async Task HealedAsync(NetworkStream stream, FightInstance fight,
                                             Fighter quienCura, Fighter curado)
        {
            if (!Alguno(fight)) return;

            // Los dos de jefe que no dejan curar a un bando, venga la cura de quien venga.
            await BreakRuleAsync(stream, fight,
                curado.TeamId == 0 ? Challenges.BossRule.NoHealAllies : Challenges.BossRule.NoHealEnemies,
                $"{curado.Name} recibe una cura de {quienCura.Name}");

            if (!Vivo(fight, SinCorazon)) return;
            if (curado.TeamId != 0 || quienCura.TeamId != 0 || quienCura.Id == curado.Id) return;

            await BreakAsync(stream, fight, SinCorazon,
                             $"{quienCura.Name} ha curado a {curado.Name}");
        }

        /// <summary>Muere un ALIADO. Sólo lo mira el Superviviente.</summary>
        public static async Task AllyDiedAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

            if (!quien.EsInvocado && fight.RoundNumber < 6)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NobodyKilledBeforeRound6,
                                     $"{quien.Name} cae en la ronda {fight.RoundNumber}, antes de la 6");
            }

            if (Vivo(fight, Superviviente))
                await BreakAsync(stream, fight, Superviviente, $"{quien.Name} ha caído");
        }

        /// <summary>
        /// Un aliado lanza algo. De aquí salen los tres retos de «no repitas» y «remata antes de
        /// cambiar de objetivo».
        /// </summary>
        public static async Task CastAsync(NetworkStream stream, FightInstance fight, Fighter quien,
                                           int hechizo, Fighter? victima, int vecesEsteTurno)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

            // Versátil: la misma acción una sola vez por turno.
            if (Vivo(fight, Versatil) && vecesEsteTurno > 1)
                await BreakAsync(stream, fight, Versatil, $"{quien.Name} repite el hechizo {hechizo}");

            // Ahorrador: lo mismo, pero para TODO el combate y no sólo para el turno. Se lleva la
            // cuenta del combate entero, no de cada aliado por separado; con un solo personaje da
            // igual, y con varios es la lectura más dura de las dos.
            bool repetido = !fight.SpellsEverUsed.Add(hechizo);
            if (repetido && Vivo(fight, Ahorrador))
                await BreakAsync(stream, fight, Ahorrador, $"el hechizo {hechizo} ya se había usado");

            if (victima == null || victima.TeamId == 0) return;

            // Blitzkrieg: queda apuntado que a éste ya se le ha pegado.
            fight.Wounded.Add(victima.Id);

            // Duelo: al enemigo que empieza uno, no le toca nadie más.
            if (Vivo(fight, Duelo))
            {
                if (!fight.FirstAttacker.TryGetValue(victima.Id, out long primero))
                {
                    fight.FirstAttacker[victima.Id] = quien.Id;
                }
                else if (primero != quien.Id)
                {
                    await BreakAsync(stream, fight, Duelo,
                                     $"{quien.Name} le pega a {victima.Name}, que era de otro");
                }
            }

            // Imprevisible y Asesino a ojo: los ataques van al señalado y a nadie más.
            foreach (int reto in new[] { Imprevisible, AsesinoAOjo })
            {
                if (!Vivo(fight, reto)) continue;
                if (!fight.ChallengeTargets.TryGetValue(reto, out long senalado)) continue;
                if (senalado == victima.Id) continue;

                await BreakAsync(stream, fight, reto,
                                 $"{quien.Name} pega a {victima.Name} en vez de al señalado");
            }

            // Focalización y Elitista: al que empiezas a pegar, lo terminas. La diferencia en el
            // juego real es que el Elitista lo SEÑALA el servidor; aquí los dos se llevan igual,
            // señalando al primero al que se le pega.
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
                                     $"{quien.Name} cambia de objetivo sin rematar al anterior");
                }
            }
        }

        /// <summary>Cae un enemigo: orden de niveles, con arma, y junto a un obstáculo.</summary>
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
            }

            bool quedanEnemigos = false;
            foreach (var uno in fight.Rojo) if (uno.IsAlive) { quedanEnemigos = true; break; }

            await KillOrderAsync(stream, fight, victima);

            // Los de jefe que miran quién cae: nadie antes de la ronda 6, y las invocaciones
            // enemigas, que no las remate un aliado.
            if (!victima.EsInvocado && fight.RoundNumber < 6)
            {
                string pronto = $"{victima.Name} cae en la ronda {fight.RoundNumber}, antes de la 6";
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoEnemyKilledBeforeRound6, pronto);
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NobodyKilledBeforeRound6, pronto);
            }
            if (victima.EsInvocado && quienRemata != null && quienRemata.TeamId == 0)
            {
                await BreakRuleAsync(stream, fight, Challenges.BossRule.NoEnemySummonKilledByAlly,
                                     $"{quienRemata.Name} remata a la invocación {victima.Name}");
            }

            // Primero: el señalado tiene que caer el primero de todos.
            if (Vivo(fight, Primero) && !EsElSenalado(fight, Primero, victima.Id))
            {
                await BreakAsync(stream, fight, Primero,
                                 $"ha caído {victima.Name} antes que el señalado");
            }

            // Último: el señalado tiene que caer el último, o sea con nadie más en pie.
            if (Vivo(fight, Ultimo) && EsElSenalado(fight, Ultimo, victima.Id) && quedanEnemigos)
            {
                await BreakAsync(stream, fight, Ultimo,
                                 $"el señalado {victima.Name} cae y aún quedan enemigos");
            }

            // Asesino a sueldo: el señalado va cayendo por orden, y en cuanto cae se señala otro.
            if (Vivo(fight, AsesinoASueldo))
            {
                if (!EsElSenalado(fight, AsesinoASueldo, victima.Id))
                {
                    await BreakAsync(stream, fight, AsesinoASueldo,
                                     $"ha caído {victima.Name}, que no era el señalado");
                }
                else if (quedanEnemigos)
                {
                    await SenalarAsync(stream, fight, AsesinoASueldo, UnEnemigoVivo(fight));
                }
            }

            // Los pequeños antes: rematar es cosa del aliado de menor nivel.
            if (Vivo(fight, LosPequenosAntes) && quienRemata != null && !EsDeLosMasBajos(fight, quienRemata))
            {
                await BreakAsync(stream, fight, LosPequenosAntes,
                                 $"remata {quienRemata.Name}, que no es el de menor nivel");
            }

            // Cruel: en orden creciente de nivel. Ordenado: al revés.
            if (Vivo(fight, Cruel) && fight.LastKilledLevel >= 0 && victima.Level < fight.LastKilledLevel)
            {
                await BreakAsync(stream, fight, Cruel,
                                 $"{victima.Name} (nivel {victima.Level}) cae tras uno de nivel " +
                                 $"{fight.LastKilledLevel}");
            }
            if (Vivo(fight, Ordenado) && fight.LastKilledLevel >= 0 && victima.Level > fight.LastKilledLevel)
            {
                await BreakAsync(stream, fight, Ordenado,
                                 $"{victima.Name} (nivel {victima.Level}) cae tras uno de nivel " +
                                 $"{fight.LastKilledLevel}");
            }
            fight.LastKilledLevel = victima.Level;

            // Bárbaro: rematar con arma.
            if (Vivo(fight, Barbaro) && !conArma)
                await BreakAsync(stream, fight, Barbaro, $"{victima.Name} cae por un hechizo");

            // Un merecido pin: cada enemigo tiene que caer pegado a un obstáculo.
            if (Vivo(fight, MerecidoPin) && !JuntoAObstaculo(fight, victima.CellId))
                await BreakAsync(stream, fight, MerecidoPin, $"{victima.Name} cae al descubierto");
        }

        /// <summary>
        /// El «Primero» y el «Último» de un jefe. No señalan a nadie a suertes como los normales:
        /// el criterio trae el monstruo —«CK#147,1», que el Jalató Real caiga el primero— y el
        /// cliente ya lo nombra en la descripción, así que no hay kwm que mandar.
        ///
        /// Las invocaciones no cuentan ni para un lado ni para el otro: un jalató invocado que
        /// cae antes que el jefe no es «otro antes», ni uno que queda en pie es «otro después».
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
                    bool yaCayo = false;
                    foreach (var uno in fight.Rojo)
                    {
                        if (uno.Id != victima.Id && !uno.IsAlive && !uno.EsInvocado
                            && reto.KillFirst.Contains(uno.MonsterId)) { yaCayo = true; break; }
                    }
                    if (!yaCayo)
                    {
                        await BreakOneAsync(stream, fight, id,
                                            $"ha caído {victima.Name} antes que el que tenía que caer primero");
                    }
                }

                if (reto.KillLast.Count > 0 && reto.KillLast.Contains(victima.MonsterId))
                {
                    bool quedaOtro = false;
                    foreach (var uno in fight.Rojo)
                    {
                        if (uno.Id != victima.Id && uno.IsAlive && !uno.EsInvocado
                            && !reto.KillLast.Contains(uno.MonsterId)) { quedaOtro = true; break; }
                    }
                    if (quedaOtro)
                    {
                        await BreakOneAsync(stream, fight, id,
                                            $"{victima.Name} tenía que caer el último y aún quedan enemigos");
                    }
                }
            }
        }

        /// <summary>
        /// Se acabó. Lo que no se haya roto, cumplido; y si se ha perdido, todo fallado, que es
        /// lo que manda el servidor real: una tanda de kwl de fallo pegada al final.
        ///
        /// Devuelve el EXTRA que han ganado los cumplidos, sumado, en tanto por ciento. Los retos
        /// suman entre sí: dos al 80 y al 65 dan un 145 % de más.
        ///
        /// Y aquí es donde se apuntan los logros. Los retos que impone el sitio llevan logro
        /// detrás y se hacen una vez: cumplido uno, queda escrito para ese personaje y no se le
        /// vuelve a poner. Hasta ahora esa tabla no la escribía nadie, porque no había forma de
        /// saber si un reto se había cumplido.
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

            // Estos dos sólo se pueden juzgar al final, porque hasta que no se acaba no se sabe.

            // Reparto: cada aliado tiene que haber rematado a alguien.
            if (won && Vivo(fight, Reparto))
            {
                foreach (var aliado in fight.Azul)
                {
                    if (fight.Killers.Contains(aliado.Id)) continue;
                    await BreakAsync(stream, fight, Reparto, $"{aliado.Name} no ha rematado a nadie", record: end.Frames);
                    break;
                }
            }

            // Dum: todos tienen que caer en la misma ronda.
            if (won && Vivo(fight, Dum))
            {
                int primera = -1;
                foreach (var ronda in fight.KilledOnRound.Values)
                {
                    if (primera < 0) primera = ronda;
                    else if (ronda != primera)
                    {
                        await BreakAsync(stream, fight, Dum, record: end.Frames, porque:
                                         $"han caído en rondas distintas ({primera} y {ronda})");
                        break;
                    }
                }
            }

            int extra = 0;
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id)) continue;

                bool cumplido = won;
                fight.ChallengesBroken.Add(id);
                byte[] verdict = ConnectionProtocol.Push(Op.Kwl, Network.FightProtocol.BuildChallengeResult(id, cumplido));
                end.Frames.Add(verdict);
                await WriteFrameAsync(stream, verdict);

                var reto = Challenges.Get(id);
                Console.WriteLine($"[Retos] «{reto?.Name ?? id.ToString()}» " +
                                  (cumplido ? $"CUMPLIDO (+{percent} %)."
                                            : "fallado (se ha perdido el combate)."));

                if (!cumplido) continue;
                extra += percent;
                end.Won.Add(id);

                // Los que impone el sitio son los que llevan logro.
                if (reto != null && reto.NeedsMonster)
                {
                    end.Achievements.Add(id);
                    DatabaseManager.MarkChallengeDone(GameState.CharacterId, id);
                    Console.WriteLine($"[Retos] Logro «{reto.Name}» conseguido; no volverá a salir.");
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

        // ─── Lo que hace falta recordar para las reglas de jefe ─────────────────

        /// <summary>Lo que un combate apunta para las reglas de jefe y nadie más necesita.</summary>
        private sealed class FightNotes
        {
            /// <summary>Aliado → la casilla en la que empezó el combate.</summary>
            public Dictionary<long, int> StartCells { get; } = new Dictionary<long, int>();

            /// <summary>¿Empezó su turno en línea con un enemigo el que está jugando?</summary>
            public bool BeganInLineWithEnemy { get; set; }
        }

        private static readonly ConcurrentDictionary<long, FightNotes> _notes = new ConcurrentDictionary<long, FightNotes>();

        private static FightNotes Notes(FightInstance fight) => _notes.GetOrAdd(fight.FightId, _ => new FightNotes());

        /// <summary>Los retos fijados y sin romper que se juzgan con esa regla.</summary>
        private static List<int> ConRegla(FightInstance fight, Challenges.BossRule regla)
        {
            var salida = new List<int>();
            foreach (var (id, _) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                if (Challenges.Get(id)?.Rule == regla) salida.Add(id);
            }
            return salida;
        }

        /// <summary>¿Queda en juego alguno con regla de jefe? Para no echar cuentas en balde.</summary>
        private static bool AlgunaRegla(FightInstance fight)
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
                                                 Challenges.BossRule regla, string porque)
        {
            foreach (int id in ConRegla(fight, regla)) await BreakOneAsync(stream, fight, id, porque);
        }

        /// <summary>¿Está en línea —o en diagonal— con alguno de ésos que siga en pie?</summary>
        private static bool Alineado(IEnumerable<Fighter> lista, Fighter quien, bool diagonal)
        {
            var (x, y) = MapGeometry.CellToPoint(quien.CellId);
            foreach (var otro in lista)
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

        /// <summary>A cuántas casillas tiene al más cercano de ésos. Si no hay ninguno, lejísimos.</summary>
        private static int MasCercano(IEnumerable<Fighter> lista, Fighter quien)
        {
            int mejor = int.MaxValue;
            foreach (var otro in lista)
            {
                if (!otro.IsAlive || otro.Id == quien.Id) continue;
                int lejos = MapGeometry.Distance(quien.CellId, otro.CellId);
                if (lejos < mejor) mejor = lejos;
            }
            return mejor;
        }

        /// <summary>
        /// The bonus a won fight's challenges will give, worked out without judging anything aloud:
        /// the ones not broken, less "Reparto" when an ally finished nobody and "Dum" when they fell
        /// on different rounds -- the two <see cref="FightEndedAsync"/> can only judge at the end.
        /// </summary>
        public static int EndBonus(FightInstance fight, bool won)
        {
            if (!won) return 0;
            int extra = 0;
            foreach (var (id, percent) in fight.ChallengesFixed)
            {
                if (fight.ChallengesBroken.Contains(id)) continue;
                if (id == Reparto && fight.Azul.Any(a => !fight.Killers.Contains(a.Id))) continue;
                if (id == Dum && fight.KilledOnRound.Values.Distinct().Count() > 1) continue;
                extra += percent;
            }
            return extra;
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        /// <summary>¿Hay algo que vigilar en este combate?</summary>
        private static bool Alguno(FightInstance fight)
            => fight.ChallengesFixed.Count > fight.ChallengesBroken.Count;

        /// <summary>¿Está ese reto en juego y todavía sin romper?</summary>
        /// <remarks>
        /// Se pregunta por la CLASE y no por el número: el «Prudente» que impone el Jalató Real
        /// es el 121 y se juzga igual que el 40, así que cuenta como vivo si lo está cualquiera
        /// de los dos. Quién equivale a quién lo dice <see cref="Challenges.KindOf"/>.
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
        /// Señalar a un enemigo para un reto, y decírselo al cliente con el kwm para que le ponga
        /// la marca encima.
        ///
        /// El objetivo viaja con su identificador y con SU CASILLA, pero la casilla es una foto
        /// del momento: el servidor NO la reemite cuando el señalado anda. Se comprobó siguiendo
        /// al bicho por la captura —se movió de la 262 a la 218 y no salió ningún mensaje de
        /// retos— y quien le sigue la pista es el cliente, por el identificador. Los dos mensajes
        /// que llevan casillas distintas son de dos reconexiones, y cada uno es la foto entera.
        ///
        /// Volver a señalar a mitad de combate NO ESTÁ MEDIDO: en las 305 capturas no hay ni una
        /// pareja de mensajes del mismo reto con distinto objetivo. Lo que sí dice la descripción
        /// del Asesino a sueldo es que «cada vez que se elimine al enemigo indicado,
        /// inmediatamente se indica un nuevo enemigo», así que se hace, y por este mismo mensaje,
        /// que es el único que puede llevarlo.
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

            Console.WriteLine($"[Retos] «{Challenges.Get(reto)?.Name ?? reto.ToString()}» señala " +
                              $"a {aQuien.Name}.");
        }

        /// <summary>Uno cualquiera de los que siguen en pie. A suertes, que es lo honrado.</summary>
        private static Fighter? UnEnemigoVivo(FightInstance fight)
        {
            var vivos = new List<Fighter>();
            foreach (var uno in fight.Rojo) if (uno.IsAlive) vivos.Add(uno);
            return vivos.Count == 0 ? null : vivos[_dado.Next(vivos.Count)];
        }

        private static readonly Random _dado = new Random();

        /// <summary>¿Está señalado ese luchador para ese reto?</summary>
        private static bool EsElSenalado(FightInstance fight, int reto, long quien)
            => fight.ChallengeTargets.TryGetValue(reto, out long suyo) && suyo == quien;

        /// <summary>«No se ha conseguido el reto $challenge{1} por culpa de <b>{0}</b>.»</summary>
        private const int ChallengeFailedMessage = 188;

        /// <summary>
        /// Romper un reto: se avisa una vez, en el momento, y no se vuelve a mirar.
        ///
        /// Y detrás del kwl va un aviso al chat diciendo POR CULPA DE QUIÉN, que es algo que sale
        /// en las capturas y no habíamos visto: en la mazmorra, cuando un hechizo de zona mató a
        /// tres de golpe y reventó el reto del orden, el servidor mandó
        ///
        ///   kwl 0823
        ///   lqn 10bc01 220c "Sacri-Master" 2202 "35"
        ///
        /// o sea el mensaje 188 con el nombre del culpable y el número del reto. Los dos en la
        /// misma milésima, y el kwl POR DELANTE de las tres muertes: el servidor da el reto por
        /// roto en el golpe, no en la muerte.
        /// </summary>
        private static async Task BreakAsync(NetworkStream stream, FightInstance fight, int id,
                                             string porque, string culpable = "", List<byte[]>? record = null)
        {
            // Todos los de esa clase: el normal y el de jefe se rompen por lo mismo y a la vez.
            var deEsaClase = new List<int>();
            foreach (var (suyo, _) in fight.ChallengesFixed)
            {
                if (Challenges.KindOf(suyo) == id) deEsaClase.Add(suyo);
            }

            foreach (int uno in deEsaClase) await BreakOneAsync(stream, fight, uno, porque, culpable, record);
        }

        /// <summary>Romper UN reto, por su número. Lo que hace <see cref="BreakAsync"/> con cada uno.</summary>
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

            Console.WriteLine($"[Retos] «{Challenges.Get(id)?.Name ?? id.ToString()}» ROTO: {porque}.");
        }

        /// <summary>¿Tiene a alguien de ese bando pegado? Pegado es a una casilla, sin diagonales.</summary>
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
        /// ¿Está alineado con algún aliado? En recto es compartir fila o columna; en diagonal, que
        /// se aparten lo mismo en las dos.
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

        /// <summary>¿Le ve algún enemigo desde donde está?</summary>
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
        /// ¿Está la casilla pegada a un obstáculo? Cuentan los agujeros y también los bordes del
        /// mapa, que es lo que dice la descripción del reto: una casilla del borde tiene menos de
        /// cuatro vecinas, y eso ya la deja pegada a algo.
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
