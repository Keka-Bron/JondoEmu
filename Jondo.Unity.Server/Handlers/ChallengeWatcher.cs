using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Managers;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Protocol;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Launcher.Handlers
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
            fight.KillCells.Clear();
        }

        /// <summary>
        /// Empieza ronda nueva: el Imprevisible señala otro. Es literalmente lo que dice su
        /// descripción, «el enemigo indicado al principio de cada turno global».
        /// </summary>
        public static async Task RoundStartedAsync(NetworkStream stream, FightInstance fight)
        {
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
            foreach (var enemigo in fight.Team1)
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

            // Zombi: exactamente un PM por turno. Los PM que se pierden al zafarse de un placaje
            // no cuentan, dice la descripción; aquí no hay placajes, así que no hay excepción que
            // hacer.
            if (Vivo(fight, Zombi) && fight.TurnStartMp - quien.CurrentMP != 1)
            {
                await BreakAsync(stream, fight, Zombi,
                                 $"{quien.Name} ha gastado {fight.TurnStartMp - quien.CurrentMP} PM");
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
        }

        /// <summary>¿Es de los aliados de menor nivel? Puede haber varios empatados.</summary>
        private static bool EsDeLosMasBajos(FightInstance fight, Fighter quien)
        {
            int menor = int.MaxValue;
            foreach (var uno in fight.Team0) if (uno.IsAlive && uno.Level < menor) menor = uno.Level;
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

        /// <summary>
        /// Una curación. El Sin Corazón sólo deja curarse a uno mismo: si el que cura y el curado
        /// son aliados distintos, se rompe.
        /// </summary>
        public static async Task HealedAsync(NetworkStream stream, FightInstance fight,
                                             Fighter quienCura, Fighter curado)
        {
            if (!Alguno(fight) || !Vivo(fight, SinCorazon)) return;
            if (curado.TeamId != 0 || quienCura.TeamId != 0 || quienCura.Id == curado.Id) return;

            await BreakAsync(stream, fight, SinCorazon,
                             $"{quienCura.Name} ha curado a {curado.Name}");
        }

        /// <summary>Muere un ALIADO. Sólo lo mira el Superviviente.</summary>
        public static async Task AllyDiedAsync(NetworkStream stream, FightInstance fight, Fighter quien)
        {
            if (!Alguno(fight) || quien.TeamId != 0) return;

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
            foreach (var uno in fight.Team1) if (uno.IsAlive) { quedanEnemigos = true; break; }

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

            // Estos dos sólo se pueden juzgar al final, porque hasta que no se acaba no se sabe.

            // Reparto: cada aliado tiene que haber rematado a alguien.
            if (won && Vivo(fight, Reparto))
            {
                foreach (var aliado in fight.Team0)
                {
                    if (fight.Killers.Contains(aliado.Id)) continue;
                    await BreakAsync(stream, fight, Reparto, $"{aliado.Name} no ha rematado a nadie");
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
                        await BreakAsync(stream, fight, Dum,
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
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwl,
                    Network.FightProtocol.BuildChallengeResult(id, cumplido)));

                var reto = Challenges.Get(id);
                Console.WriteLine($"[Retos] «{reto?.Name ?? id.ToString()}» " +
                                  (cumplido ? $"CUMPLIDO (+{percent} %)."
                                            : "fallado (se ha perdido el combate)."));

                if (!cumplido) continue;
                extra += percent;

                // Los que impone el sitio son los que llevan logro.
                if (reto != null && reto.NeedsMonster)
                {
                    DatabaseManager.MarkChallengeDone(GameState.CharacterId, id);
                    Console.WriteLine($"[Retos] Logro «{reto.Name}» conseguido; no volverá a salir.");
                }
            }
            return extra;
        }

        // ─── Piezas ─────────────────────────────────────────────────────────────

        /// <summary>¿Hay algo que vigilar en este combate?</summary>
        private static bool Alguno(FightInstance fight)
            => fight.ChallengesFixed.Count > fight.ChallengesBroken.Count;

        /// <summary>¿Está ese reto en juego y todavía sin romper?</summary>
        private static bool Vivo(FightInstance fight, int id)
        {
            if (fight.ChallengesBroken.Contains(id)) return false;
            foreach (var (suyo, _) in fight.ChallengesFixed) if (suyo == id) return true;
            return false;
        }

        private static bool SigueVivo(FightInstance fight, long fighterId)
        {
            foreach (var uno in fight.Team1) if (uno.Id == fighterId) return uno.IsAlive;
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
            foreach (var uno in fight.Team1) if (uno.IsAlive) vivos.Add(uno);
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
                                             string porque, string culpable = "")
        {
            if (!fight.ChallengesBroken.Add(id)) return;

            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Kwl,
                Network.FightProtocol.BuildChallengeResult(id, false)));

            if (culpable.Length == 0) culpable = GameState.CharacterName;
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                ConnectionProtocol.BuildSystemMessage(ChallengeFailedMessage,
                                                      culpable, id.ToString())));

            Console.WriteLine($"[Retos] «{Challenges.Get(id)?.Name ?? id.ToString()}» ROTO: {porque}.");
        }

        /// <summary>¿Tiene a alguien de ese bando pegado? Pegado es a una casilla, sin diagonales.</summary>
        private static bool Adyacente(FightInstance fight, Fighter quien, int bando)
        {
            var lista = bando == 0 ? fight.Team0 : fight.Team1;
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
            foreach (var otro in fight.Team0)
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
            foreach (var enemigo in fight.Team1)
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
