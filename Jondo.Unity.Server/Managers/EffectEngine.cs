using System;
using System.Collections.Generic;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Launcher.Managers
{
    /// <summary>
    /// Lo que hay que hacer con un efecto ya resuelto: aplicarlo y contárselo al cliente.
    /// El motor decide QUÉ pasa; quien lo llama decide cómo se manda por el cable.
    /// </summary>
    public sealed class Outcome
    {
        public Fighter Sobre { get; init; } = null!;
        public SpellEffect Efecto { get; init; } = null!;
        public Buff Buff { get; init; }
        public int HechizoOrigen { get; init; }
        public int NivelOrigen { get; init; }

        /// <summary>Si el efecto cambia una característica en el acto, cuál y en cuánto.</summary>
        public int Caracteristica { get; init; }
        public int Cuanto { get; init; }

        /// <summary>Si el efecto encadena otro hechizo, cuál y en qué grado.</summary>
        public int HechizoEncadenado { get; init; }
        public int GradoEncadenado { get; init; }

        /// <summary>
        /// Si el efecto mueve a alguien, de dónde a dónde. Menos uno cuando no mueve a nadie.
        /// </summary>
        public int CasillaDesde { get; init; } = -1;
        public int CasillaHasta { get; init; } = -1;
        public bool Mueve => CasillaHasta >= 0 && CasillaHasta != CasillaDesde;

        /// <summary>
        /// Verdadero cuando el motor no sabe todavía qué hace este efecto pero SÍ sabe que dura y
        /// que el cliente lo pinta. Se manda igual al panel y se anota en el registro, en vez de
        /// tirarlo en silencio, que es lo que se hacía antes con catorce familias enteras.
        /// </summary>
        public bool SoloParaElPanel { get; init; }

        /// <summary>La plantilla de bicho que hay que sacar al tablero, si el efecto invoca.</summary>
        public int Invoca { get; init; }

        /// <summary>
        /// Cuántos puntos se lleva el que lanza, cuando el efecto es de ROBO. Los mismos que se
        /// le han quitado al objetivo, que van en <see cref="Cuanto"/> en negativo.
        /// </summary>
        public int LeDaAlLanzador { get; init; }

        /// <summary>Los puntos de vida que se han devuelto, si el efecto cura.</summary>
        public int Cura { get; init; }
    }

    /// <summary>
    /// El motor de efectos: coge las entradas del EffectsJson de un hechizo y las convierte en
    /// cosas que le pasan a alguien.
    ///
    /// No hay ni un hechizo escrito a mano aquí dentro. Todo sale de dos sitios de la base:
    ///
    ///   - <c>SpellLevels.EffectsJson</c>, que dice qué efectos tiene el hechizo, con cuánto, a
    ///     quién (<c>targetMask</c>), cuándo (<c>triggers</c>) y por cuántos turnos.
    ///   - La tabla <c>Effects</c> del cliente, que dice qué característica toca cada número de
    ///     efecto y con qué signo (<c>BonusType</c>).
    ///
    /// Con eso salen solas cosas como éstas, que antes había que escribir una por una:
    ///
    ///   Flecha Helada  = 1079 (quita 2 PA) + 96 (21-24 de agua) + 293 (+8 de daños básicos de
    ///                    Flecha Helada, tres turnos, sobre uno mismo)
    ///   Disparos Lejanos = 280 y 281 repetidos (+3 de alcance mínimo y +6 de máximo) sobre una
    ///                    lista larga de hechizos, un turno
    ///   Dofus Ocre     = el objeto regala el "hechizo" 8394 por su efecto 1175; ese hechizo, en su
    ///                    grado 1, dice "cuando me peguen lanza mi grado 2" y "al empezar el turno
    ///                    lanza mi grado 3"; el grado 2 pone el estado 519 y el 3 da +1 PA si NO se
    ///                    tiene ese estado, +20 de huida si sí, y lo quita al acabar el turno.
    /// </summary>
    public static class EffectEngine
    {
        // Los números de efecto que el motor entiende de forma especial. El resto se resuelve por
        // su característica en el catálogo.
        //
        // Los que pegan son DIEZ, no cinco: del 91 al 95 son los de ROBO DE VIDA y del 96 al 100
        // los de daño a secas, uno por elemento cada tanda. El emulador sólo miraba del 96 al 100,
        // así que hechizos como Flecha Voraz —que pega con el 94, robo de fuego— o el Ojo de Topo
        // —el 91, robo de agua— no encajaban en ningún sitio y el daño salía de donde no debía.
        //
        // Y el elemento no hay que deducirlo del número: lo dice el catálogo en su columna
        // ElementId, con 0 neutral, 1 tierra, 2 fuego, 3 agua y 4 aire.
        private const int DanoPrimero = 91;
        private const int DanoUltimo = 100;

        /// <summary>
        /// Los que pegan en función de lo que el objetivo lleve EROSIONADO. Son cinco, uno por
        /// elemento, en dos tandas: la del 1092 al 1096 y la del 1118 al 1122. En su descripción
        /// el dado no es el daño sino el tanto por ciento.
        /// </summary>
        private static readonly HashSet<int> PorLoErosionado
            = new HashSet<int> { 1092, 1093, 1094, 1095, 1096, 1118, 1119, 1120, 1121, 1122 };

        public static bool PegaSegunLoErosionado(int efecto) => PorLoErosionado.Contains(efecto);

        /// <summary>¿Este efecto pega?</summary>
        public static bool EsDeDano(int efecto)
            => (efecto >= DanoPrimero && efecto <= DanoUltimo) || PegaSegunLoErosionado(efecto);

        /// <summary>
        /// Los golpes que da un hechizo: uno por cada efecto de daño que le toque al objetivo.
        ///
        /// Se resuelve con las mismas máscaras que el resto —de ahí que Flecha Voraz pegue 11-13 o
        /// 34-38 según el estado que lleve el objetivo— y se devuelve el elemento ya resuelto.
        /// Si el hechizo no tiene ni un efecto de daño, no devuelve nada: Tiro de Repliegue sólo
        /// aparta al que lanza y no debe quitarle un solo punto de vida a nadie.
        /// </summary>
        public static List<(SpellEffect Efecto, int Elemento, Fighter Sobre, int Lejos)> Golpes(
            FightInstance combate, Fighter quienLanza, int hechizo, int grado, Fighter objetivo,
            int celdaApuntada = -1, bool critico = false)
        {
            var fuera = new List<(SpellEffect, int, Fighter, int)>();
            foreach (var efecto in EfectosDeLaTirada(hechizo, grado, critico))
            {
                if (!EsDeDano(efecto.EffectId)) continue;

                // El elemento lo dice el propio hechizo en su effectElement; si no lo trae, el
                // catálogo por el número de efecto.
                int elemento = efecto.Element >= 0 ? efecto.Element
                                                   : DatabaseManager.EffectElement(efecto.EffectId);
                foreach (var sobre in AQuien(combate, quienLanza, objetivo, efecto, celdaApuntada))
                {
                    if (sobre == null || !sobre.IsAlive) continue;

                    // A cuántas casillas del centro de la zona está. El daño baja según se aleja,
                    // y cuánto lo dice el propio hechizo.
                    int lejos = celdaApuntada >= 0
                        ? Jondo.Unity.World.Maps.MapGeometry.Distance(celdaApuntada, sobre.CellId)
                        : 0;
                    fuera.Add((efecto, elemento, sobre, lejos));
                }
            }
            return fuera;
        }

        /// <summary>
        /// El daño que le queda a uno que está a <paramref name="lejos"/> casillas del centro.
        ///
        /// Se pierde un tanto por ciento por casilla, con un tope de pasos, y los dos números los
        /// trae el hechizo en su <c>zoneDescr</c>: los del Ocra que caen lo hacen al diez por
        /// ciento con tope de cuatro, o sea que del quinto anillo en adelante ya no baja más.
        ///
        /// La tirada del dado es UNA para todo el lanzamiento: si de "25 a 30" sale 26, en el
        /// centro entran 26 y a una casilla, el 90% de eso.
        /// </summary>
        public static int ConLaCaidaDeLaZona(int dano, SpellEffect efecto, int lejos)
        {
            if (dano <= 0 || lejos <= 0 || efecto.PasoDeCaida <= 0) return dano;

            int pasos = efecto.TopeDeCaida > 0 ? Math.Min(lejos, efecto.TopeDeCaida) : lejos;
            double queda = Math.Pow(1.0 - efecto.PasoDeCaida / 100.0, pasos);
            return Math.Max(0, (int)Math.Round(dano * queda));
        }
        private const int Empujar = 5;
        private const int Tirar = 6;

        /// <summary>"Retrocede #1 casillas" y "Avanza #1 casillas": mueven al QUE LANZA.</summary>
        private const int Retroceder = 1041;
        private const int Avanzar = 1042;
        private const int PonerEstado = 950;
        private const int QuitarEstado = 951;

        /// <summary>
        /// "Lanza el hechizo del dado en el grado de la cara". Es el enganche con el que las
        /// actitudes de los objetos encadenan lo que de verdad hacen.
        /// </summary>
        public const int EfectoQueLanzaHechizo = 792;
        private const int LanzarHechizo = EfectoQueLanzaHechizo;

        /// <summary>"Invoca: #1". La plantilla del bicho viaja en el dado.</summary>
        public const int Invocar = 181;

        /// <summary>
        /// Los efectos que colocan algo EN UNA CASILLA en vez de sobre alguien: una invocación,
        /// una trampa, un glifo. Su objetivo es el suelo, así que no se les busca dueño.
        ///
        ///   181  "Invoca: #1"                    400  "Coloca una trampa"
        ///   401  "Coloca un glifo de inicio de turno"
        ///   1091 "Coloca un glifo aura"
        /// </summary>
        private static readonly HashSet<int> AlSuelo = new HashSet<int> { 181, 400, 401, 1091 };

        public static bool VaAlSuelo(int efecto) => AlSuelo.Contains(efecto);

        /// <summary>"Cura: #1% de los PdV máximos". El dado es el porcentaje.</summary>
        private const int CuraPorcentual = 1109;

        /// <summary>"#1% del nivel en escudo". El nivel es el de quien lanza.</summary>
        private const int EscudoPorNivel = Buffs.EscudoPorNivel;

        /// <summary>
        /// Convierte el porcentaje del efecto 1020 en puntos. Las fichas oficiales de los
        /// hechizos describen estos escudos como porcentaje del nivel del lanzador; la división
        /// entera conserva el redondeo hacia abajo del servidor.
        /// </summary>
        public static int PuntosDeEscudo(int porcentaje, int nivelDelLanzador)
            => porcentaje > 0 && nivelDelLanzador > 0
                ? nivelDelLanzador * porcentaje / 100
                : 0;

        /// <summary>Los dos números de característica de los puntos.</summary>
        private const int PuntosDeAccion = 1;
        private const int PuntosDeMovimiento = 23;

        /// <summary>
        /// "Mata al objetivo". Es la cuenta atrás de un invocado: al nacer le cuelgan uno de
        /// éstos con su ronda, y cuando llega, se deshace.
        /// </summary>
        public const int MatarAlObjetivo = 141;

        /// <summary>Cuántos hechizos encadenados se admiten antes de sospechar de un bucle.</summary>
        private const int HondoMaximo = 4;

        /// <summary>
        /// El grado en el que vive el enganche de una actitud. Es siempre el primero: los otros los
        /// nombra él mismo por su número, así que no hay que preguntarle a nadie cuál toca.
        /// </summary>
        public const int GradoDelEnganche = 1;

        /// <summary>El disparador de "ahora mismo".</summary>
        public const string AlLanzar = "I";
        public const string AlEmpezarElTurno = "TB";
        public const string AlAcabarElTurno = "TE";
        public const string CuandoMePegan = "DBE";

        /// <summary>
        /// Cuando uno ANDA, por cada casilla. Es el disparador del Centinela del Ocra, que da
        /// alcance y daños a distancia a cambio de quedarse quieto: cada paso se lleva uno de
        /// alcance y un dos por ciento de daños.
        ///
        /// Medido en su captura: once movimientos, once bajadas, ninguna excepción.
        /// </summary>
        public const string AlAndar = "CCMPARR";

        /// <summary>
        /// Resuelve un hechizo entero y devuelve lo que hay que hacer, en orden.
        ///
        /// <paramref name="disparador"/> filtra: al lanzar se piden los "I", al empezar el turno los
        /// "TB", y así. Los efectos con otro disparador se quedan quietos hasta que les toque.
        /// </summary>
        public static List<Outcome> Resolver(FightInstance combate, Fighter quienLanza,
                                                  int hechizo, int grado, Fighter objetivo,
                                                  string disparador, int ronda, int hondo = 0,
                                                  int celdaApuntada = -1, bool critico = false)
        {
            var fuera = new List<Outcome>();
            if (hondo > HondoMaximo) return fuera;

            var lista = EfectosDeLaTirada(hechizo, grado, critico);

            // Los efectos que van a suertes: se sortean ANTES de recorrer nada, y los que no salen
            // se quedan fuera de esta resolución.
            var descartados = Sortear(lista);

            foreach (var efecto in lista)
            {
                if (descartados.Contains(efecto)) continue;

                bool leToca = false;
                foreach (var d in efecto.Disparadores())
                {
                    if (string.Equals(d, disparador, StringComparison.OrdinalIgnoreCase)) { leToca = true; break; }
                }
                if (!leToca) continue;

                // Lo que se pone EN EL SUELO no busca a nadie: va a la casilla, esté quien esté.
                //
                // Aquí se caían las balizas. El efecto 181 lleva máscara "a,A" y zona de punto, y
                // el motor buscaba un combatiente encima de la casilla apuntada para aplicárselo.
                // Pero una baliza se invoca justamente donde NO hay nadie: no había candidato, la
                // consecuencia no se creaba y no se pedía invocar nada. El paquete y el reenvío de
                // la lista estaban bien; lo que no llegaba era la orden.
                if (VaAlSuelo(efecto.EffectId))
                {
                    var puesta = Aplicar(combate, quienLanza, quienLanza, hechizo, grado, efecto,
                                         ronda, celdaApuntada);
                    if (puesta != null) fuera.Add(puesta);
                    continue;
                }

                // "Retrocede" y "Avanza" mueven al QUE LANZA, y la máscara es una condición sobre
                // el objetivo, no una lista de destinatarios: se cumple o no se cumple, y el
                // desplazamiento ocurre UNA VEZ. Si se aplicara por candidato, un hechizo que
                // alcanzara a tres bichos movería al lanzador tres veces.
                bool unaSolaVez = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;

                foreach (var sobre in AQuien(combate, quienLanza, objetivo, efecto, celdaApuntada))
                {
                    var hecho = Aplicar(combate, quienLanza, sobre, hechizo, grado, efecto, ronda,
                                        celdaApuntada);
                    if (unaSolaVez)
                    {
                        if (hecho != null) fuera.Add(hecho);
                        break;
                    }
                    if (hecho == null) continue;
                    fuera.Add(hecho);

                    // Un efecto puede encadenar otro hechizo: es como se enganchan las actitudes.
                    if (hecho.HechizoEncadenado != 0)
                    {
                        fuera.AddRange(Resolver(combate, quienLanza, hecho.HechizoEncadenado,
                                                hecho.GradoEncadenado, sobre, AlLanzar, ronda,
                                                hondo + 1, celdaApuntada));
                    }
                }
            }
            return fuera;
        }

        /// <summary>
        /// A quién le toca un efecto, según su máscara.
        ///
        ///   C          a quien lo lanza
        ///   a, A       a los del otro bando (y a los del propio, según el juego; aquí, al objetivo)
        ///   e&lt;N&gt;      sólo si NO lleva el estado N
        ///   E&lt;N&gt;      sólo si SÍ lo lleva
        ///
        /// Lo demás de la máscara —"P", "g", "F434", "*e2131"— son afinados que este motor todavía
        /// no distingue; con ellos se cae al objetivo del lanzamiento, que es lo razonable.
        /// </summary>
        private static IEnumerable<Fighter> AQuien(FightInstance combate, Fighter quienLanza,
                                                   Fighter objetivo, SpellEffect efecto,
                                                   int celdaApuntada = -1)
        {
            var mascara = efecto.TargetMask ?? "";
            bool alLanzador = false, aLosMios = false, aLosDeEnfrente = false, aLasInvocaciones = false;
            var pideEstado = new List<int>();
            var pideNoEstado = new List<int>();

            foreach (var trozo in mascara.Split(','))
            {
                string t = trozo.Trim().TrimStart('*');
                if (t.Length == 0) continue;
                if (t == "C") { alLanzador = true; continue; }

                // LA MINÚSCULA Y LA MAYÚSCULA NO SON LO MISMO: la "a" son los del propio bando y
                // la "A" los de enfrente. Estaban las dos en el mismo cubo, y eso hacía cosas
                // absurdas. Tiro de Repliegue, por ejemplo, lleva un 1041 "Retrocede" con máscara
                // "A" y un 1042 "Avanza" con "a": al no distinguirlas se cumplían las dos, el
                // lanzador se movía dos casillas atrás y otras dos adelante, y el desplazamiento
                // neto era cero. En el registro se veía tal cual, ida y vuelta a la misma casilla.
                //
                // Se sostiene en toda la base: los efectos de daño 96-100 llevan sólo "A" o "a,A"
                // —más de seis mil— y la curación 108 lleva "a", "C" o "g".
                if (t == "a") { aLosMios = true; continue; }
                if (t == "A") { aLosDeEnfrente = true; continue; }

                // Las invocaciones. Son 1.518 efectos en la base con "g" a secas, y hasta ahora se
                // caían todos en silencio: es lo que dejaba a la Baliza de Supervivencia sin curar.
                if (t == "g") { aLasInvocaciones = true; continue; }

                if (t.Length > 1 && (t[0] == 'e' || t[0] == 'E') && int.TryParse(t.Substring(1), out int estado))
                {
                    if (t[0] == 'E') pideEstado.Add(estado); else pideNoEstado.Add(estado);
                }
            }

            var candidatos = new List<Fighter>();
            if (alLanzador) candidatos.Add(quienLanza);

            if (aLosMios || aLosDeEnfrente || aLasInvocaciones)
            {
                // La zona: el efecto dice de qué FORMA coge el terreno alrededor de la casilla
                // apuntada —un punto, un círculo de radio dos, una cruz— y le toca a todo el que
                // esté encima Y cumpla la máscara.
                foreach (var quien in EnLaZona(combate, quienLanza, objetivo, efecto, celdaApuntada))
                {
                    bool suyo = quien.TeamId == quienLanza.TeamId;
                    bool esInvocado = quien.EsInvocado;

                    bool leToca = (aLosMios && suyo)
                               || (aLosDeEnfrente && !suyo)
                               || (aLasInvocaciones && esInvocado && suyo);
                    if (!leToca) continue;

                    if (!candidatos.Contains(quien)) candidatos.Add(quien);
                }

                // El que lanza NO entra en su propia zona: para eso está la "C".
                //
                // "a,A" quiere decir "los dos bandos", no "yo incluido". Es lo que hace peligroso
                // al Ojo de Topo, cuyo daño lleva esa máscara y por tanto se lleva por delante a
                // los aliados que pillen dentro del círculo. Y por eso Flecha de Dispersión tiene
                // dos máscaras distintas: sus daños de aire son "A" y sólo tocan a los de
                // enfrente, pero sus empujes son "a,A" y mueven a todo el mundo, así que un
                // aliado estampado contra un muro sí se come el golpe del choque.
                candidatos.Remove(quienLanza);
                if (alLanzador) candidatos.Insert(0, quienLanza);
            }

            // Sin máscara, al objetivo del lanzamiento. Pero si la máscara dice algo que este motor
            // todavía no sabe leer —"P" los jugadores, "F434" una familia de bichos— NO se cae al
            // objetivo: se deja pasar. Cayendo al objetivo, Flecha Voraz pegaba DOS veces.
            if (candidatos.Count == 0 && mascara.Trim().Length == 0)
            {
                candidatos.Add(objetivo ?? quienLanza);
            }

            foreach (var quien in candidatos)
            {
                if (quien == null) continue;
                bool vale = true;
                foreach (int estado in pideEstado) if (!quien.Buffs.TieneEstado(estado)) vale = false;
                foreach (int estado in pideNoEstado) if (quien.Buffs.TieneEstado(estado)) vale = false;
                if (vale) yield return quien;
            }
        }

        /// <summary>
        /// Los combatientes que pisa la zona del efecto.
        ///
        /// Si no se sabe a qué casilla se apuntó —las actitudes y los encadenados no apuntan a
        /// ninguna— se cae al objetivo de siempre, que es lo que se hacía antes de haber zonas.
        /// </summary>
        private static IEnumerable<Fighter> EnLaZona(FightInstance combate, Fighter quienLanza,
                                                     Fighter objetivo, SpellEffect efecto,
                                                     int celdaApuntada)
        {
            if (celdaApuntada < 0)
            {
                if (objetivo != null) yield return objetivo;
                yield break;
            }

            var casillas = Jondo.Unity.World.Maps.Zone.Casillas(
                efecto.Forma, efecto.Tamano, quienLanza.CellId, celdaApuntada);
            if (casillas.Count == 0)
            {
                if (objetivo != null) yield return objetivo;
                yield break;
            }

            var dentro = new HashSet<int>(casillas);
            foreach (var quien in Todos(combate))
            {
                if (quien == null || !quien.IsAlive) continue;
                if (dentro.Contains(quien.CellId)) yield return quien;
            }
        }

        /// <summary>Si uno pisa la zona de un efecto.</summary>
        private static bool EstaEnLaZona(Fighter quien, FightInstance combate, Fighter objetivo,
                                         SpellEffect efecto, int celdaApuntada)
        {
            foreach (var otro in EnLaZona(combate, quien, objetivo, efecto, celdaApuntada))
            {
                if (otro == quien) return true;
            }
            return false;
        }

        private static IEnumerable<Fighter> Todos(FightInstance combate)
        {
            foreach (var f in combate.Team0) yield return f;
            foreach (var f in combate.Team1) yield return f;
        }

        private static Outcome Aplicar(FightInstance combate, Fighter quienLanza, Fighter sobre,
                                            int hechizo, int grado, SpellEffect efecto, int ronda,
                                            int celdaApuntada = -1)
        {
            // El daño lo lleva quien ya lo llevaba; aquí no se toca.
            if (efecto.EffectId >= DanoPrimero && efecto.EffectId <= DanoUltimo) return null;

            if (efecto.EffectId == Empujar || efecto.EffectId == Tirar ||
                efecto.EffectId == Retroceder || efecto.EffectId == Avanzar)
            {
                // Cuántas casillas: el dado, y si no, el valor.
                int cuantas = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuantas <= 0) return null;
                if (efecto.EffectId == Tirar) cuantas = -cuantas;

                // "Retrocede" y "Avanza" mueven AL QUE LANZA, no al objetivo. Es lo que hace Tiro
                // de Repliegue, que da alcance y da un paso atrás; el objetivo sólo sirve para
                // saber de dónde se aleja. Medido en su captura: el Ocra estaba en la 411, lanzó
                // a la 410 y acabó en la 412, alejándose de la casilla apuntada.
                bool alLanzador = efecto.EffectId == Retroceder || efecto.EffectId == Avanzar;
                if (alLanzador)
                {
                    sobre = quienLanza;
                    if (efecto.EffectId == Avanzar) cuantas = -cuantas;
                }

                var ocupadas = new HashSet<int>();
                foreach (var otro in Todos(combate))
                    if (otro != null && otro.IsAlive && otro != sobre) ocupadas.Add(otro.CellId);

                int desde = sobre.CellId;
                // Las casillas que se pueden pisar en la arena. Iban a null, que quiere decir "no
                // mires el suelo", y por eso los pious acababan en un agujero o fuera del mapa:
                // la única frontera que quedaba era el borde de la retícula de 560 celdas, que es
                // mucho mayor que el suelo de un mapa.
                var pisables = MapManager.GetFightWalkable(combate.ArenaMapId);
                int hasta = Jondo.Unity.World.Maps.Zone.Empujar(
                    celdaApuntada, quienLanza.CellId, desde, cuantas,
                    pisables: pisables, ocupadas: ocupadas);

                if (hasta == desde) return null;
                sobre.CellId = hasta;
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    CasillaDesde = desde, CasillaHasta = hasta,
                };
            }

            if (efecto.EffectId == Invocar)
            {
                // "Invoca: #1", con la plantilla del bicho en el dado. No lo saca al tablero el
                // motor: hace falta repartir identificador, rehacer el orden de turnos y avisar
                // al cliente, y eso es del que lleva el combate.
                if (efecto.DiceNum <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Invoca = efecto.DiceNum,
                };
            }

            if (efecto.EffectId == LanzarHechizo)
            {
                // "Lanza el hechizo del dado en el grado de la cara". Es el enganche de las
                // actitudes: el grado 1 del Amarillo Ocre no hace nada por sí mismo, sólo dice
                // cuándo lanzar sus grados 2 y 3.
                if (efecto.DiceNum <= 0) return null;
                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    HechizoEncadenado = efecto.DiceNum,
                    GradoEncadenado = efecto.DiceSide > 0 ? efecto.DiceSide : 1,
                };
            }

            if (efecto.EffectId == PonerEstado || efecto.EffectId == QuitarEstado)
            {
                int estado = efecto.Value != 0 ? efecto.Value : efecto.DiceNum;
                if (estado == 0) return null;
                if (efecto.EffectId == PonerEstado) sobre.Buffs.PonerEstado(estado);
                else sobre.Buffs.QuitarEstado(estado);

                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Buff = efecto.EffectId == PonerEstado
                        ? sobre.Buffs.Poner(new Buff
                        {
                            EffectId = efecto.EffectId,
                            EffectUid = efecto.EffectUid,
                            Estado = estado,
                            HechizoOrigen = hechizo,
                            NivelOrigen = grado,
                            Quien = quienLanza.Id,
                            Disparador = AlLanzar,
                            CaducaEnRonda = Caduca(efecto, ronda),
                        }, combate.SiguienteEmbrujo)
                        : null,
                };
            }

            // Los tres que afinan un hechizo concreto: daño básico y alcance. El hechizo va en el
            // dado y lo que se suma, en el valor.
            if (Enum.IsDefined(typeof(SpellAspect), efecto.EffectId) && efecto.EffectId != 0)
            {
                var que = (SpellAspect)efecto.EffectId;
                int cuanto = efecto.Value;
                if (efecto.DiceNum <= 0 || cuanto == 0) return null;

                var puesto = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    Sobre = que,
                    HechizoAfectado = efecto.DiceNum,
                    Cuanto = cuanto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                }, combate.SiguienteEmbrujo);
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = puesto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            // Los que ROBAN puntos: se los quitan a uno y se los dan al otro.
            //
            // El robo no es un embrujo informativo, aunque el cliente lo pinte como tal: le quita
            // los puntos al objetivo ahí mismo. Flecha Inmovilizadora lleva el efecto 77, "Roba 1
            // PM", y lo que se veía en pantalla era un estado llamado "Roba 1 PM" colgado del pío
            // mientras el pío seguía con sus cuatro puntos.
            int robado = DatabaseManager.RoboDePuntos(efecto.EffectId);
            if (robado != 0)
            {
                int cuantos = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuantos <= 0) return null;

                // Nunca más de los que le quedan.
                int hay = robado == PuntosDeAccion ? sobre.CurrentAP : sobre.CurrentMP;
                cuantos = Math.Min(cuantos, hay);
                if (cuantos <= 0) return null;

                var quitado = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    Caracteristica = robado,
                    Cuanto = -cuantos,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre,
                    Efecto = efecto,
                    Buff = quitado,
                    Caracteristica = robado,
                    Cuanto = -cuantos,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    LeDaAlLanzador = cuantos,
                };
            }

            // Las curaciones por tanto por ciento de la vida máxima. El dado es el PORCENTAJE, no
            // los puntos: la Baliza de Supervivencia cura un siete por ciento del tope de quien
            // recibe, y en el cable eso viaja ya resuelto en puntos.
            if (efecto.EffectId == CuraPorcentual)
            {
                int cuanto = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuanto <= 0) return null;

                int puntos = Math.Max(1, sobre.MaxHP * cuanto / 100);
                puntos = Math.Min(puntos, Math.Max(0, sobre.MaxHP - sobre.CurrentHP));
                if (puntos <= 0) return null;

                sobre.CurrentHP += puntos;
                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Cura = puntos,
                };
            }

            // Los escudos de nivel no son una característica y por eso el catálogo trae cero.
            // El dado es un porcentaje del NIVEL DEL LANZADOR, no del destinatario. Los puntos
            // resueltos se guardan en el embrujo: al relanzar el mismo efecto, Buffs.Poner los
            // sustituye y refresca su caducidad; hechizos o lanzadores distintos sí se acumulan.
            if (efecto.EffectId == EscudoPorNivel)
            {
                int porcentaje = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                int puntos = PuntosDeEscudo(porcentaje, quienLanza.Level);
                if (puntos <= 0) return null;

                var escudo = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    Cuanto = puntos,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = escudo,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                    Cuanto = puntos,
                };
            }

            // Los que MULTIPLICAN: "daños sufridos x110%", "curas recibidas x50%". No tocan
            // ninguna característica, así que se guardan con su porcentaje en el embrujo y quien
            // calcula el golpe los busca por su número de efecto.
            if (DatabaseManager.EsMultiplicador(efecto.EffectId))
            {
                int cuanto = efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value;
                if (cuanto <= 0) return null;

                var puestoMult = sobre.Buffs.Poner(new Buff
                {
                    EffectId = efecto.EffectId,
                    EffectUid = efecto.EffectUid,
                    Cuanto = cuanto,
                    HechizoOrigen = hechizo,
                    NivelOrigen = grado,
                    Quien = quienLanza.Id,
                    Disparador = AlLanzar,
                    CaducaEnRonda = Caduca(efecto, ronda),
                    Apila = SeApila(efecto),
                }, combate.SiguienteEmbrujo);

                return new Outcome
                {
                    Sobre = sobre, Efecto = efecto, Buff = puestoMult,
                    HechizoOrigen = hechizo, NivelOrigen = grado,
                };
            }

            // Y todo lo demás: lo que toque una característica, con el signo que diga el catálogo.
            var (caracteristica, signo) = DatabaseManager.EffectMeta(efecto.EffectId);
            int cantidad = caracteristica != 0 && signo != 0
                ? (efecto.DiceNum != 0 ? efecto.DiceNum : efecto.Value) * signo
                : 0;

            // Y si NO toca ninguna característica, tampoco se tira.
            //
            // Aquí estaba el panel vacío. Este método acababa en un "si no hay característica,
            // nada", y con eso desaparecían CATORCE familias enteras de las que el servidor real
            // sí anuncia: el 1160 y el 1163 de las balizas, el 792 que encadena, el 141 que mata,
            // el 406 que quita los efectos de un hechizo, el 3793, el 1159 de las curas, el 289 de
            // la línea de visión… todas las que en el catálogo tienen Characteristic 0, que son
            // justamente las de clase. Contado sobre las capturas del Ocra: de los treinta y dos
            // efectos que el servidor manda al panel, catorce se perdían por esta línea.
            //
            // Ahora el que no se sepa aplicar se anota y SE MANDA igual, con lo que trae puesto.
            bool soloPanel = caracteristica == 0 || signo == 0 || cantidad == 0;

            var embrujo = sobre.Buffs.Poner(new Buff
            {
                EffectId = efecto.EffectId,
                EffectUid = efecto.EffectUid,
                Caracteristica = soloPanel ? 0 : caracteristica,
                Cuanto = soloPanel ? 0 : cantidad,
                HechizoOrigen = hechizo,
                NivelOrigen = grado,
                Quien = quienLanza.Id,
                Disparador = AlLanzar,
                CaducaEnRonda = Caduca(efecto, ronda),
                Apila = SeApila(efecto),
            }, combate.SiguienteEmbrujo);

            return new Outcome
            {
                Sobre = sobre,
                Efecto = efecto,
                Buff = embrujo,
                Caracteristica = soloPanel ? 0 : caracteristica,
                Cuanto = soloPanel ? 0 : cantidad,
                HechizoOrigen = hechizo,
                NivelOrigen = grado,
                SoloParaElPanel = soloPanel,
            };
        }

        /// <summary>
        /// La lista de efectos que toca, según haya salido crítico o no.
        ///
        /// Un hechizo trae DOS listas en la base y la crítica no es "la normal multiplicada": es
        /// otra tanda entera con sus propios números. Flecha Helada pega de 21 a 24 y en crítico
        /// de 25 a 29; Tiros Potentes da 250 de potencia y en crítico 300. Si la lista crítica
        /// viene vacía —hay hechizos que no la tienen— se usa la de siempre.
        /// </summary>
        public static IReadOnlyList<SpellEffect> EfectosDeLaTirada(int hechizo, int grado, bool critico)
        {
            if (!critico) return SpellEffects.De(hechizo, grado);
            var criticos = SpellEffects.Criticos(hechizo, grado);
            return criticos.Count > 0 ? criticos : SpellEffects.De(hechizo, grado);
        }

        /// <summary>
        /// El sorteo de los efectos que van a suertes, y devuelve LOS QUE NO SALEN.
        ///
        /// Un efecto puede traer una probabilidad en su campo <c>random</c>, y los que la traen se
        /// agrupan por su campo <c>group</c>. Hay dos maneras, y se distinguen por lo que suman:
        ///
        ///   suman 100  -> es un sorteo: sale UNO, con el peso de cada uno. Es lo que hace
        ///                 Invocación de Arakna, que trae dos efectos 181 —la plantilla 246 al
        ///                 ochenta por ciento y la 2630, la Arakna mayor, al veinte—. Sin esto
        ///                 salían las dos a la vez, que es lo que estaba pasando.
        ///   no suman 100 -> cada uno va por su cuenta y ocurre con su propia probabilidad.
        ///
        /// Contado sobre la base entera: hay 1.335 niveles de hechizo con efectos de este tipo, y
        /// en 1.129 de sus grupos la suma es exactamente cien.
        /// </summary>
        private static HashSet<SpellEffect> Sortear(IReadOnlyList<SpellEffect> efectos)
        {
            var fuera = new HashSet<SpellEffect>();

            var porSorteo = new Dictionary<int, List<SpellEffect>>();
            foreach (var efecto in efectos)
            {
                if (efecto.Probabilidad <= 0) continue;
                if (!porSorteo.TryGetValue(efecto.Sorteo, out var lista))
                {
                    lista = new List<SpellEffect>();
                    porSorteo[efecto.Sorteo] = lista;
                }
                lista.Add(efecto);
            }

            foreach (var (_, lista) in porSorteo)
            {
                double suma = 0;
                foreach (var e in lista) suma += e.Probabilidad;

                if (Math.Abs(suma - 100.0) < 0.5 && lista.Count > 1)
                {
                    // Sale uno: se tira una vez y se recorre el reparto.
                    double tirada = SiguienteAzar() * suma;
                    double acumulado = 0;
                    SpellEffect elegido = lista[lista.Count - 1];
                    foreach (var e in lista)
                    {
                        acumulado += e.Probabilidad;
                        if (tirada <= acumulado) { elegido = e; break; }
                    }
                    foreach (var e in lista) if (e != elegido) fuera.Add(e);
                }
                else
                {
                    // Cada uno por su cuenta.
                    foreach (var e in lista)
                    {
                        if (SiguienteAzar() * 100.0 > e.Probabilidad) fuera.Add(e);
                    }
                }
            }

            return fuera;
        }

        private static readonly Random _azar = new Random();

        private static double SiguienteAzar()
        {
            lock (_azar) return _azar.NextDouble();
        }

        /// <summary>
        /// Si lo que deja este efecto se acumula en vez de sustituir a lo que ya hubiera.
        ///
        /// Se acumula lo que salta CADA VEZ QUE PASA ALGO, o sea lo que no tiene el disparador de
        /// "al lanzar": el Centinela se come uno de alcance por paso, y tres pasos son tres menos.
        /// Lo que sí es de "al lanzar" se refresca, que es lo que hace Flecha Helada al repetirse.
        /// </summary>
        private static bool SeApila(SpellEffect efecto)
        {
            foreach (var d in efecto.Disparadores())
            {
                if (string.Equals(d, AlLanzar, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>
        /// En qué ronda se cae un efecto. Duración negativa quiere decir "mientras dure el
        /// combate"; cero, que es de un vistazo y no deja embrujo que dure.
        /// </summary>
        private static int Caduca(SpellEffect efecto, int ronda)
            => efecto.Duration < 0 ? -1 : ronda + Math.Max(1, efecto.Duration);
    }
}
