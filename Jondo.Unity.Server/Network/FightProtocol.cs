using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Los mensajes del combate de la 3.6.10.10, medidos de las quince capturas de
    /// <c>Wireshark captures from real game\Combate</c>.
    ///
    /// Hacía falta empezar de cero: de los cuarenta y ocho opcodes que usaba el manejador de
    /// combate sólo siete siguen existiendo en esta versión, y en las capturas salen doscientos
    /// setenta y uno que el código no nombraba. Lo que sí se aprovecha es la máquina de estados
    /// —equipos, colocación, turnos, botín—: lo que estaba mal era el cable, no el diseño.
    ///
    /// Esta clase cubre de momento la PREPARACIÓN, que es de lo que hay medida completa. El resto
    /// del combate está a medio descifrar y se irá añadiendo aquí; lo que falta está apuntado en
    /// docs/fight.md.
    ///
    /// El hilo de la preparación, leído del orden real de la captura (tools/hilo.py mezcla los dos
    /// sentidos por reloj, que es lo que `pcap.streams` no sabe hacer):
    ///
    ///   cliente  hqa { f1: id del grupo de monstruos }   atacar
    ///   servidor jsq                                     vacío, enterado
    ///   servidor ...el cambio de mapa normal: kub, jru, lva...
    ///   servidor jxg   una por combatiente
    ///   servidor kba   las casillas azules y las rojas
    ///   servidor jzu   quién va en cada equipo
    ///   servidor jwq   vacío
    ///   servidor jrk { f2: 10, f4: mapa }
    ///   cliente  jzy { f1: quién, f2: casilla }          se coloca
    ///   servidor kmk { f1: casilla, f2: orientación, f3: quién }
    ///   cliente  kaq { f1: 1 }                           el botón de listo
    ///   servidor kah { f1: quién, f3: 1 }
    ///
    /// Un detalle que ahorra trabajo: entre las casillas y el botón de listo el servidor NO manda
    /// ningún temporizador —sólo los latidos del kqo—, así que la cuenta atrás de la colocación la
    /// lleva el cliente por su cuenta. Al servidor le toca únicamente empezar el combate cuando se
    /// le acaba el tiempo.
    /// </summary>
    public static class FightProtocol
    {
        /// <summary>
        /// El id con el que viaja "aquí no hay nadie" o "todavía no se sabe quién".
        ///
        /// Durante la colocación el bando enemigo viaja entero como -1: el grupo de monstruos no se
        /// ha partido en combatientes hasta que el combate empieza de verdad. Y en el kmk una
        /// casilla que se deja libre se manda con este mismo -1.
        /// </summary>
        public const long Nobody = -1;

        /// <summary>Lo que lleva el jrk en su f2 en las quince capturas.</summary>
        private const int FightMapKind = 10;

        // ─── Empezar ────────────────────────────────────────────────────────────

        /// <summary>
        /// A qué grupo de monstruos ataca el cliente (hqa).
        ///
        ///   f1: el id contextual del grupo, negativo
        ///
        /// Devuelve cero si el mensaje no trae grupo.
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

        /// <summary>Enterado del ataque (jsq). Va vacío.</summary>
        public static byte[] BuildFightAccepted() => Array.Empty<byte>();

        /// <summary>
        /// "El mapa que viene es de combate" (kmp).
        ///
        /// Esta es LA marca, y va dentro del cambio de mapa, antes de cargarlo. Es lo que hace que
        /// el cliente pida el combate con un ijm vacío en vez de pedir el contenido de un mapa
        /// normal con jrh.
        ///
        /// La correlación no deja lugar a dudas: en las treinta y nueve entradas a combate de las
        /// veintitrés capturas, el último kmp antes del kam llevaba f1: 1 —treinta y nueve de
        /// treinta y nueve— y en las setecientas dieciocho cargas de mapa corrientes iba vacío o no
        /// iba, sin una sola excepción.
        ///
        /// Sin esto el cliente carga el mapa táctico pero se queda en modo mapa normal, y todo lo
        /// que llega detrás —los combatientes, las casillas, los equipos— le entra sin un combate
        /// al que pertenecer. Lo que se ve entonces es el tablero dibujado y nada encima.
        /// </summary>
        public static byte[] BuildFightMapComing() => Pb.New().Var(1, 1).Build();

        /// <summary>
        /// Contra quién se va a pelear (kmu), al principio de la carga del mapa táctico.
        ///
        ///   f2: el id contextual del grupo, el mismo negativo que llevará luego el kam
        ///
        /// Comprobado en cuatro capturas contra monstruos: el número del kmu y el del kam.f3 son
        /// el mismo.
        /// </summary>
        public static byte[] BuildFightAgainst(long defender) => Pb.New().Var(2, defender).Build();

        /// <summary>Fin de la tanda de preparación (jwq). También va vacío.</summary>
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
        /// En qué mapa se pelea (jrk).
        ///
        ///   f2: 10      f3: vacío      f4: el mapa
        ///
        /// El f3 va presente y vacío en las capturas, así que se manda igual: es un submensaje sin
        /// campos, que no es lo mismo que no mandarlo.
        /// </summary>
        public static byte[] BuildFightMap(long mapId)
            => Pb.New().Var(2, FightMapKind).EmptyMsg(3).Var(4, mapId).Build();

        // ─── El combate existe ──────────────────────────────────────────────────

        /// <summary>
        /// El mapa está listo (ijq). Va vacío y sale justo antes de que se anuncie el combate.
        /// </summary>
        public static byte[] BuildMapReady() => Array.Empty<byte>();

        /// <summary>
        /// Aquí hay un combate (kam). Es EL mensaje que le crea el combate al cliente.
        ///
        ///   f2: de qué tipo         f3: contra quién
        ///   f4: [las plantillas de los monstruos]
        ///   f5: el id del combate   f6: quién lo empezó
        ///
        /// Sin esto no hay combate del que hablar y todo lo que va detrás cae en saco roto. Se ve
        /// en el propio registro del cliente: al llegarle el jwq revienta con
        ///
        ///   NullReferenceException
        ///     at gum.blww (Google.Protobuf.Collections.RepeatedField`1[T] a)
        ///     at guk.blvw (jwq a)
        ///
        /// que es el cliente recorriendo la lista de combatientes de un combate que no existe.
        ///
        /// Lo que lleva cada campo, medido en las catorce aperturas de las capturas:
        ///
        ///   f2  4 contra monstruos, 7 en el koliseo, y ausente en un desafío entre jugadores.
        ///   f3  el grupo de monstruos, con su id contextual NEGATIVO, tal cual viaja en el jss.
        ///       En un desafío es el id del otro jugador, positivo.
        ///   f4  una lista empaquetada con una entrada POR MONSTRUO: contra un poutch va [494] y
        ///       contra cuatro va [494, 494, 494, 494]. En un grupo de ocho salen ocho números
        ///       distintos, y cuadran con las plantillas de los bichos.
        ///   f5  el id del combate, que se repite luego en el kae y en cada kau.
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

        /// <summary>Contra monstruos. El koliseo es 7 y un desafío entre jugadores no lleva tipo.</summary>
        public const int AgainstMonsters = 4;

        /// <summary>El koliseo. Medido: su kam llega «100728ee0a», o sea f2=7.</summary>
        public const int Koliseo = 7;

        /// <summary>
        /// Lo que dura la colocación en el koliseo, en décimas.
        /// </summary>
        /// <remarks>
        /// El 592 del kaa de la captura, tal cual. Son los sesenta segundos menos lo que tardó en
        /// llegar la trama, igual que el 445 de un combate corriente son los cuarenta y cinco.
        /// </remarks>
        public const int KoliseoPlacementDeciseconds = 592;

        /// <summary>
        /// Lo que acompaña al anuncio (kaa).
        ///
        ///   f3: 1      f4: 1      f5: ?      f6: el tipo de combate
        ///
        /// El f3 y el f4 valen 1 en las treinta y nueve aperturas y el f6 repite el tipo del kam.
        ///
        /// El f5 es LA CUENTA ATRÁS DE LA COLOCACIÓN, en décimas de segundo. Se sacó con el reloj
        /// de la captura: en una vale 445 y veintisiete segundos y pico después vale 173, o sea 272
        /// unidades en 27,4 segundos, 9,92 por segundo. Cuadra con los valores que se ven —442,
        /// 444, 445, 446, que son los cuarenta y cinco segundos menos lo que tardó en llegar— y con
        /// el 592 del koliseo, que da sus sesenta. Va ausente cuando el combate ya pasó de la
        /// colocación.
        ///
        /// Aquí se manda el tiempo que de verdad va a esperar el servidor antes de empezar solo,
        /// para que el reloj del cliente y el del servidor cuenten lo mismo.
        /// </summary>
        /// <summary>
        /// El kaa de un desafio: seis bytes y SIN cuenta atras.
        /// </summary>
        /// <remarks>
        /// Medido en «enviar desafio y el otro acepta»: llega «f2=1 f3=1 f4=1» y nada mas. Ni el
        /// f5 del tiempo ni el f6 del tipo. Por eso en un duelo no sale reloj de colocacion: no es
        /// que se esconda, es que el servidor real no manda ninguno.
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
        /// Una opción del combate (kau): bloquear a los mirones, cerrarlo al grupo y demás.
        ///
        ///   f3: cuál       f5: el id del combate
        ///
        /// Salen cuatro seguidas en cada apertura, con f3 valiendo 2, 1, 3 y la cuarta sin f3.
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

        /// <summary>Las cuatro que manda el servidor real, en su orden.</summary>
        public static readonly int[] FightOptions = { 2, 1, 3, 0 };

        // ─── Quién pelea ────────────────────────────────────────────────────────

        /// <summary>
        /// Un combatiente (jxg).
        ///
        ///   f2 { f1 { f1: casilla, f2: orientación, f4: 0 }
        ///        f2 { f2: la ficha, f3: el aspecto }
        ///        f3: quién es }
        ///
        /// Ojo con el f2 de fuera, que envuelve TODO y es fácil pasarlo por alto: sin él el cliente
        /// parsea el mensaje, no encuentra ningún combatiente dentro y no dibuja nada. El tablero
        /// sale con sus casillas azules y rojas y encima no hay nadie.
        ///
        /// El sobre es EL MISMO que el de un actor del mapa en el jss: casilla y orientación
        /// delante, el cuerpo en medio y el id detrás. Por eso el cliente sabe dibujar un
        /// combatiente con el código que ya tiene, y por eso aquí se le puede pasar el bloque de
        /// aspecto que ya construye el mapa sin tocarlo.
        ///
        /// La ficha es una lista de características con la misma numeración que usa el emulador en
        /// datos/characteristics.json: 0 vida, 1 PA, 23 PM, 27 y 28 esquivas, 33 a 37 las
        /// resistencias. Durante la colocación casi todas viajan vacías —el valor de verdad no
        /// llega hasta que empieza el combate—, así que replicarlo es mandar el hueco puesto y sin
        /// número dentro.
        /// </summary>
        public static byte[] BuildFighter(int cell, int orientation, long fighterId,
                                          IEnumerable<(int Characteristic, long Base, long Gear)> sheet,
                                          byte[] look, Pb identity, bool isMonster)
            => Pb.New()
                .Msg(2, FighterBlock(cell, orientation, fighterId, sheet, look, identity, isMonster))
                .Build();

        /// <summary>
        /// Todos los combatientes de golpe, con la ficha llena (jxb).
        ///
        ///   f1 (repetido): un combatiente, el MISMO bloque que va dentro de la jxg
        ///
        /// Es lo que se manda al empezar el combate de verdad, y otra vez entero al reconectarse a
        /// uno en curso. La diferencia con la colocación no es la forma sino lo que llevan dentro
        /// las características: en la jxg de un monstruo van vacías y aquí llegan sus valores.
        ///
        /// El orden en el que van NO es el de iniciativa —salen por bandos, los monstruos y luego
        /// el jugador—, así que el carrusel no se ordena por aquí.
        /// </summary>
        public static byte[] BuildAllFighters(IEnumerable<Pb> fighters)
        {
            var jxb = Pb.New();
            foreach (var fighter in fighters) jxb.Msg(1, fighter);
            return jxb.Build();
        }

        /// <summary>El bloque de un combatiente, que se reutiliza en la jxg y en el jxb.</summary>
        /// <summary>
        /// Una característica de la ficha, con el valor en el hueco que le toca.
        ///
        /// Aquí estaba el motivo de que no se viera la previsualización de daños. El emulador metía
        /// TODAS las características en el mismo molde, <c>f5 { f1: valor }</c>, que es el de los
        /// puntos de acción y de movimiento —por eso ésos dos se pintaban bien y nada más—. El
        /// servidor real usa tres moldes distintos, y se ve byte a byte en el jxb de la captura:
        ///
        ///   monstruo, todas          f2 { f2: valor }         y un f2 vacío si es cero
        ///   jugador, PA(1) y PM(23)  f5 { f1: base, f5: del equipo }
        ///   jugador, las demás       f4 { f2: base, f3: 100, f7: del equipo }   f4 vacío si cero
        ///
        /// La potencia del personaje de la captura viaja como <c>2a 07 08 19 22 03 38 96 01</c>, o
        /// sea f5 { f1: 25, f4 { f7: 150 } }; la forma que emitía el emulador para eso mismo,
        /// <c>2a 07 08 19 2a 03 08 96 01</c>, no aparece ni una vez en toda la captura.
        ///
        /// El f3 con el cien sólo lo llevan las cinco que se reparten con puntos —fuerza,
        /// vitalidad, suerte, agilidad e inteligencia—, tal cual se midió.
        /// </summary>
        private static readonly HashSet<int> ConMultiplicadorBase = new HashSet<int> { 10, 11, 13, 14, 15 };

        /// <summary>Las dos que van en el molde de los puntos.</summary>
        private const int ActionPoints = 1;
        private const int MovementPoints = 23;

        /// <summary>
        /// «Malus de vida temporal»: LA VIDA QUE LE FALTA AL PERSONAJE QUE MANEJA EL CLIENTE.
        ///
        /// Y es la única forma que tiene el cliente de saberla. A los monstruos y al jugador de
        /// enfrente les va descontando la vida de los golpes que ve pasar; la SUYA no, la suya la
        /// saca del tope más esta característica. Está medido sin una sola excepción: de las 23
        /// veces que aparece en las 305 capturas, las 23 van dirigidas al personaje propio. Ni una
        /// a un monstruo, ni una al rival del duelo, que recibe golpes toda la pelea.
        ///
        /// Lleva dos números:
        ///
        ///   f2 = vida actual menos vida máxima ORIGINAL   (sube con las curas, baja con los golpes)
        ///   f8 = no es la erosión
        ///
        /// Comprobado contra una captura entera: −104 tras recibir 104, −5 tras curarse 99,
        /// +128 tras curarse otros 133. La cuenta cuadra al punto las tres veces.
        ///
        /// Y con el desafío completo: f2 = −1567 con 1567 de daño acumulado y el tope ya
        /// erosionado en 159, o sea que el tope de la resta es el de SALIDA, no el de ahora. El
        /// f8 que aquí se leía como erosión vale −1122 en ese mismo mensaje y −3324 en el
        /// siguiente, con 236 de erosión: no es eso. En la mazmorra de los jalatós sale en las
        /// ocho 97 con valores entre −114 y −338 y el f2 llega a ser POSITIVO (+220) tras una
        /// racha de curas. Es el hueco de embrujo del molde general y no se manda hasta saber
        /// qué va dentro.
        ///
        /// El emulador la mandaba una vez, vacía, al empezar el combate, y no la volvía a tocar:
        /// por eso al jugador le pegaban toda la pelea y su barra seguía llena.
        /// </summary>
        public const int TemporaryLifeMalus = 97;

        /// <param name="delEmbrujo">
        /// EL HUECO DEL EMBRUJO, el f8. Es lo que los hechizos ponen y quitan durante el combate, y
        /// va SEPARADO de la base y del equipo: el cliente guarda los tres y los suma él.
        ///
        /// Sin esto no había manera de refrescar una característica sin pisar lo demás, y era la
        /// causa de que la previsualización de daño saliera mal. Medido sobre las 401 capturas:
        /// 2.830 de las 3.279 entradas de jxw con molde detallado (86,3 %) lo llevan, y nosotros no
        /// lo escribimos ni una vez en 1.713.
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

            // La 97 tiene molde propio: f4 { f2, f8 }, y no f4 { f2, f7 } como las demás. Medido
            // en las 23 apariciones que hay en las 305 capturas, y tres de ellas llevan SÓLO el
            // f8. Ver TemporaryLifeMalus.
            if (characteristic == TemporaryLifeMalus)
            {
                if (baseValue == 0 && fromGear == 0) return entry.EmptyMsg(4);
                return entry.Msg(4, Pb.New().VarIfNotZero(2, baseValue).VarIfNotZero(8, fromGear));
            }

            if (baseValue == 0 && fromGear == 0 && delEmbrujo == 0)
            {
                return entry.EmptyMsg(4);
            }

            // El f3 con el cien NO se manda, aunque el servidor real lo lleve en cinco de ellas.
            // Este cliente lo SUMA en vez de tomarlo como porcentaje: con él puesto, la ficha
            // enseñaba 568 de fuerza donde hay 468, y cien de más en inteligencia, suerte y
            // agilidad. Hasta saber qué espera exactamente, mejor no mandarlo.
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

            // Quién es, con su sitio repetido. Sale en los dos, con f2 sólo en los monstruos.
            var where = Pb.New().Var(1, cell).VarIfNotZero(2, orientation).Var(4, 0);
            var again = Pb.New()
                .VarIfNotZero(2, isMonster ? 1 : 0)
                .Var(3, 1)
                .Msg(4, Pb.New().Msg(1, where).Var(3, fighterId));

            // El bloque del luchador: el identificador delante, la ficha, lo que dice qué es —f3 en
            // un monstruo, f6 en un jugador— y el sitio otra vez en f7.
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

        // ─── El combate de verdad ───────────────────────────────────────────────

        /// <summary>
        /// "Se acabó la colocación" (kai). Va vacío en las diez veces que sale.
        ///
        /// Es el único corte limpio entre las dos fases: todo lo de delante es colocación y todo lo
        /// de detrás —jyy, jxz, jxc, jto, jxb, jwi— es la carga del combate ya empezado.
        /// </summary>
        public static byte[] BuildFightBegins() => Array.Empty<byte>();

        /// <summary>En qué ronda vamos (jxz). <c>f2</c> es el número de ronda, empezando por 1.</summary>
        public static byte[] BuildRound(int round) => Pb.New().Var(2, round).Build();

        /// <summary>
        /// Cuánto le falta a cada hechizo para poder relanzarse (jxc).
        ///
        ///   f1 (repetido) { f1: el hechizo, f2: rondas que faltan }
        ///   f4: de quién es la lista
        ///
        /// No es el orden de iniciativa, aunque lo parezca por llevar una lista. Al empezar el
        /// combate sólo salen los hechizos que nacen con espera, y cuadran con el InitialCooldown
        /// de SpellLevels. En monstruos e invocaciones la lista va vacía y sólo viaja el f4.
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
        /// Abre una secuencia (jto): <c>f1</c> quién la provoca y <c>f2</c> de qué tipo.
        ///
        /// Todo lo que pasa en un combate va metido entre un jto y su jwi. Salen 2.229 de cada uno
        /// en las quince capturas, exactamente los mismos, que es lo que delata que son pareja.
        /// </summary>
        public static byte[] BuildSequenceStart(long author, int kind)
            => Pb.New().Var(1, author).Var(2, kind).Build();

        /// <summary>
        /// Cierra una secuencia (jwi): <c>f1</c> el número de acción, <c>f2</c> quién y <c>f3</c>
        /// el mismo tipo con el que se abrió. El cliente acusa cada cierre con un jti.
        /// </summary>
        public static byte[] BuildSequenceEnd(int actionId, long author, int kind)
            => Pb.New().Var(1, actionId).Var(2, author).Var(3, kind).Build();

        /// <summary>
        /// La secuencia con la que se abandona el combate.
        /// </summary>
        /// <remarks>
        /// Medida en las tres capturas que llevan un kme, y no es la de accion normal. En
        /// «combate contra poutch nivel 75 ... hechizos sacro-rendirse.pcapng» el jto que contesta
        /// al kme es 08a28280c8e7081005 —f2 = 5— y su jwi es 080410a28280c8e7081805 —f3 = 5—; en
        /// «aceptar desafio-combate completo-abandonar al final.pcapng», 08a282f0a6c4081005 y
        /// 080210a282f0a6c4081805. El 5 aparece exactamente una vez por captura y solo ahi.
        /// </remarks>
        public const int SurrenderSequence = 5;

        /// <summary>La secuencia de arranque del combate.</summary>
        public const int OpeningSequence = 8;

        /// <summary>La de cierre de turno.</summary>
        public const int TurnEndSequence = 7;

        /// <summary>
        /// "Confírmame" (jxh). El servidor lo manda antes de cada turno y espera el jwz del cliente
        /// para seguir.
        /// </summary>
        public static byte[] BuildConfirmTurn(long fighterId) => Pb.New().Var(2, fighterId).Build();

        /// <summary>
        /// De quién es el turno (jzc).
        ///
        ///   f1: quién      f2: lo que dura, en DÉCIMAS de segundo
        ///   f4: lo que arrastra del turno anterior      f7: qué puesto ocupa en la ronda
        ///   f8: la ronda
        ///
        /// La duración se comprobó contra el reloj de las capturas: con f2 = 410 pasan 41,002
        /// segundos hasta el fin de turno, con 350 pasan 35,001 y con 420, 42,002. Y no es la misma
        /// para todos: los personajes van entre 350 y 430, los monstruos a 290 en las doce capturas
        /// sin excepción, y las invocaciones a 150.
        ///
        /// El f7 es lo que ordena el carrusel: no hay ninguna lista de iniciativa aparte, cada
        /// turno dice qué puesto ocupa el que lo juega.
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

        /// <summary>Lo que dura un turno, en décimas: un personaje, un monstruo y una invocación.</summary>
        public const int PlayerTurnDeciseconds = 400;
        public const int MonsterTurnDeciseconds = 290;

        /// <summary>
        /// Lo que dura el turno de un invocado. Es más corto que el de un monstruo, y está medido:
        /// en las capturas del Ocra la baliza recibe un jzc con 150 donde los pious llevan 290 y
        /// el jugador 370.
        /// </summary>
        public const int SummonTurnDeciseconds = 150;

        /// <summary>
        /// "Ya puedes jugar" (jyj). Va vacío, y SÓLO se manda si el que juega es de los que maneja
        /// ese cliente: en el turno de un monstruo este paso no existe.
        /// </summary>
        public static byte[] BuildYourTurn() => Array.Empty<byte>();

        // ─── Moverse y lanzar ───────────────────────────────────────────────────

        /// <summary>
        /// A dónde quiere andar el jugador (jrw).
        ///
        ///   f1: el mapa
        ///   f2: varints empaquetados, cada uno <c>(dirección &lt;&lt; 12) | casilla</c>
        ///
        /// Y ojo, que no es el camino entero: sólo van los PUNTOS DONDE SE TUERCE. El primero es
        /// desde dónde sale y el último es a dónde va, y la dirección de ese último es hacia dónde
        /// quiere acabar mirando. Un camino recto son dos números.
        ///
        /// Es el mismo mensaje con el que se anda por el mapa fuera del combate.
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
        /// A qué casilla se lanza y con qué (jwh).
        ///
        ///   f1: la casilla objetivo      f4: el hechizo, y si no viene es un golpe de arma
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
        /// Lanzar apuntando DESDE EL CARRUSEL (jwn): { f1: a quién, f2: el hechizo }.
        ///
        /// El id viene CON SIGNO —los monstruos lo tienen negativo— y en complemento a dos de
        /// sesenta y cuatro bits, así que hay que leerlo como <c>long</c> y no como <c>int</c>:
        /// dos de las cuatro muestras reales valen menos uno.
        ///
        ///   08ffffffffffffffffff01 10ca63   =  al combatiente −1, hechizo 12746
        ///   08a28280c8e708 10b21b           =  a uno mismo, hechizo 3506
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
        /// Un embrujo puesto sobre alguien (jxm), que es lo que llena el panel de "Efectos".
        ///
        ///   f1 { f1 { f1: el dado del efecto, si lo trae
        ///             f2: sobre quién       f3: el número de embrujo, correlativo desde uno
        ///             f4: 1                 f6 { f2: -1 }
        ///             f7: el disparador     f8: el identificador del efecto (effectUid)
        ///             f10: el valor         f12 { f2: -1, f3: -1 }
        ///             f13: 1, sólo los que traen dado
        ///             f14: el hechizo que lo puso
        ///             f15: 7 los que traen dado, 2 el resto      f16: 2 }
        ///        f2: sobre quién            f3: el número de efecto }
        ///
        /// Todo esto está medido contra la captura del poutch de nivel 50 y cuadra con los datos:
        /// el f8 es exactamente el <c>effectUid</c> que el hechizo lleva en su EffectsJson —299043
        /// para el 950 de Transposición, 220298 para el 792 de La Sangre de Sacrogrito—, el f10 es
        /// su <c>value</c> y el f7 su <c>triggers</c>. Y cuando un efecto trae varios disparadores
        /// separados por barras, el servidor manda UN jxm por cada uno; en la captura salen nueve
        /// seguidos para el mismo efecto, con "TB", "D", "TE", "VE", "VM", "PD", "LPU", "DV" y "V".
        /// </summary>
        /// <summary>
        /// La FAMILIA del embrujo, que va en el f15 y decide si el cliente lo PINTA o no.
        ///
        /// Aquí estaba la razón de que el panel saliera siempre vacío teniendo los bytes bien. El
        /// constructor se calibró contra un único ejemplar —el efecto 950 de Transposición, que es
        /// un estado— y de ahí salió un "siete si trae dado, dos si no", que es justo lo contrario
        /// de lo que hace falta: el SIETE es el de la maquinaria interna, la que el panel no
        /// enseña. Todos los boosts salían etiquetados como maquinaria.
        ///
        /// La regla sale del catálogo del cliente, de dos columnas de la tabla Effects:
        ///
        ///   Category == 3           -> 4          modificador de un hechizo concreto, SE PINTA
        ///                                         ("Flecha Helada: +8 de daños básicos")
        ///   el efecto 950           -> 2          pone un estado, se pinta como icono
        ///   Boost == 0              -> 7          maquinaria interna, NO se pinta
        ///   Boost == 1, Category 0  -> no va      bono de característica, SE PINTA
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
            return 0;                 // bono de característica: el f15 no viaja
        }

        /// <summary>
        /// The family the panel does not draw. A waiting row travels with it whatever its
        /// effect -- the +1 MP of Paso de Cacería included -- and turns visible when it goes off.
        /// </summary>
        public const int HiddenFamily = 7;

        /// <param name="grado">
        /// El grado del hechizo que lo pone. Iba clavado a uno; medido contra los 1.297 jxm de las
        /// capturas del Ocra, el campo es el grado y cuadra en los 1.297.
        /// </param>
        /// <param name="rondas">
        /// La ronda EN LA QUE SE CAE, contada desde el principio del combate, no lo que le queda.
        /// Flecha Helada deja tres turnos de daños básicos: lanzada en la ronda 5 el servidor real
        /// manda un ocho, y en la 6, un nueve. Menos uno es "hasta que acabe el combate".
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
                // Uno si el lanzamiento salió crítico. Medido en la captura de Flecha Helada: los
                // seis embrujos del efecto 293 son idénticos salvo el del lanzamiento crítico, que
                // es el único que trae este campo.
                .VarIfNotZero(9, critico ? 1 : 0)
                .VarIfNotZero(10, valor)
                .VarIfNotZero(11, padre)
                .Msg(12, activacion >= 0
                    ? Pb.New().Var(2, activacion)
                    : Pb.New().Var(2, Nobody).Var(3, Nobody))
                .VarIfNotZero(13, cara)
                .Var(14, hechizo)
                .VarIfNotZero(15, familia)
                // Si se puede disipar, y cuánto: es el dispellable del propio efecto menos uno.
                .VarIfNotZero(16, Math.Max(0, dispellable - 1));

            // El de dentro es QUIEN LO LLEVA y el de fuera QUIEN LO PUSO. No son el mismo salvo
            // cuando uno se embruja a sí mismo, y por eso el fallo no se vio con la primera
            // captura que se midió. En el Ojo de Topo del Ocra, que le quita tres de alcance al
            // enemigo, el servidor real manda el pío dentro y al jugador fuera.
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
        /// Se cae un embrujo (jya): <c>f1</c> de quién y <c>f2</c> el número del embrujo, el mismo
        /// que se le dio en el jxm. Va uno por cada uno que caduca.
        /// </summary>
        public static byte[] BuildBuffGone(long dequien, int numero)
            => Pb.New().Var(1, dequien).Var(2, numero).Build();

        /// <summary>
        /// El aviso gemelo de que un embrujo se ha caído (jwe con f14 = 514):
        ///
        ///   f3: de quién     f23 { f1: el número del embrujo, f5: de quién otra vez }
        ///
        /// Va inmediatamente detrás del jya y con el mismo número. Medido en la captura de Flecha
        /// Helada, donde cada relanzamiento retira el embrujo anterior: jya {f1: quién, f2: 6} y
        /// acto seguido este jwe con f23 { f1: 6, f5: quién }.
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
        /// Qué secuencia acusa el cliente (jti): <c>f2</c> lleva el mismo número de acción con el
        /// que se cerró, el del <c>f1</c> del jwi. Devuelve cero si no viene.
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
        /// Lo que pasa (jwe). El <c>f14</c> dice de qué se trata:
        ///
        ///   129  ha andado, y el f20 lleva los pasos gastados en negativo
        ///   300  ha lanzado algo (303 si es el arma), con el f7 diciendo qué y dónde
        ///   102  ha gastado puntos de acción, otra vez en el f20 y en negativo
        ///   89 a 100  daños, con el f40 diciendo a quién, cuánto y de qué elemento
        ///   103  alguien se ha muerto
        /// </summary>
        public static byte[] BuildAction(long author, int kind, Pb? detail = null,
                                         int detailField = 0)
        {
            var jwe = Pb.New().Var(3, author);
            if (detail != null && detailField != 0) jwe.Msg(detailField, detail);
            return jwe.Var(14, kind).Build();
        }

        /// <summary>
        /// Lo que se planta en el suelo (el f32 de un jwe con f14 = 401).
        ///
        ///   f1 { f1 { f1 { f2: el color en RGB, f3: la casilla }
        ///             f4: el numero del glifo    f5: el tamano de la huella
        ///             f6: el grado del hechizo   f9: el hechizo que lanza
        ///             f10: la casilla otra vez   f11: 1
        ///             f12: de quien es } }
        /// </summary>
        /// <remarks>
        /// Medido en las capturas del tymador: los hechizos de muro salen 143 veces y las 143 van
        /// dentro de un jwe f14 = 401, UNA POR CASILLA. Dos seguidas del muro de fuego:
        ///
        /// <code>
        ///   f3=53721497699 f14=401 f32{f1{f1{f2=16711680 f3=260} f4=1 f5=2 f6=3
        ///                                 f9=13458 f10=260 f11=1 f12=53721497699}}
        ///   ... la siguiente igual con f3=274, f4=2 y f10=274
        /// </code>
        ///
        /// El color 16711680 es 0xFF0000, rojo puro, y el f6 vale 3, que es el grado con el que
        /// pega el muro -- lo unico de todo esto que ya se habia supuesto bien.
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
        /// Y como se quita (el f22 de un jwe con f14 = 310): sólo el número del glifo.
        /// </summary>
        /// <remarks>
        /// Medido en la misma captura que el 401, y es tan corto como parece: quince bytes con el
        /// dueño en el f3 y <c>f22{f1 = 1}</c>, y el siguiente igual con el 2. Los números son los
        /// mismos que repartieron los 401 al ponerlos.
        ///
        /// Sin esto el muro se quedaba pintado para siempre: el servidor lo quitaba de su lista
        /// -- se ve en el registro, «se cae el glifo 4» -- y al cliente no se lo decía nadie.
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

        /// <summary>El rojo puro con el que sale el muro de bombas.</summary>
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

        /// <summary>El campo donde va el detalle de cada cosa dentro del jwe.</summary>
        public const int CastDetail = 7;
        public const int PointsDetail = 20;
        public const int DamageDetail = 40;

        /// <summary>
        /// Cambia el aspecto de un combatiente (jwe, f14 = 149). Esta acción de combate es la que
        /// hace que el cliente anime la transformación; un refresco de actor jsn sólo lo redibuja.
        /// </summary>
        public static byte[] BuildLookChanged(long fighter, byte[] look)
            => Pb.New()
                .Var(3, fighter)
                .Var(14, LookChanged)
                .Msg(26, Pb.New().Var(1, fighter).Bytes(3, look))
                .Build();

        /// <summary>Copia un EntityLook sustituyendo únicamente los huesos de su raíz.</summary>
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

        /// <summary>Los puntos gastados, en negativo, como los manda el servidor real.</summary>
        public static Pb Spent(long fighterId, int amount)
            => Pb.New().Var(1, -amount).Var(2, fighterId);

        /// <summary>
        /// Qué se ha lanzado y dónde: el f7 del jwe con f14 = 300 (o 303 si es el arma).
        ///
        ///   f2: a quién va         f4 { f4: quién lo lanza }
        ///   f5: 1 si es crítico    f6: la casilla
        ///   f7 { f2: el hechizo, f3: el nivel de ese hechizo }
        ///   f8: 1
        ///
        /// El hechizo va en el f7, EN DOS NÚMEROS, y no en el f8. Eso último es lo que se hacía
        /// aquí y por eso el cliente pintaba un puñetazo en vez del hechizo: le llegaba un
        /// lanzamiento sin decir de qué, y el puñetazo es a lo que echa mano cuando no lo sabe. El
        /// f8 vale 1 siempre en las capturas, no es el hechizo.
        ///
        /// Los dos números del f7 salen de la base tal cual: el f2 es SpellTemplates.Id y el f3 es
        /// el SpellLevels.Id de su grado. Comprobado contra cinco lanzamientos de la captura del
        /// poutch de nivel 50: (25188, 63926), (21976, 57060), (18647, 51206), (12718, 43038) y
        /// (6828, 28035); en la base, SpellLevels.Id 63926 es del hechizo 25188, y así los cinco.
        ///
        /// El golpe de arma no lleva f7: lleva un f10 con el arma y ya.
        /// </summary>
        /// <param name="sobreEseObjetivo">
        /// Cuántas veces lleva lanzado sobre ese objetivo. Sólo viaja si el hechizo tiene tope por
        /// objetivo.
        /// </param>
        /// <param name="esteTurno">
        /// Cuántas lleva este turno. Sólo si el hechizo tiene tope por turno.
        /// </param>
        /// <param name="intervalo">
        /// Las rondas de espera que se acaban de poner. Es el <c>MinCastInterval</c> del grado, y
        /// está medido: Agudeza Absoluta manda un 4 y su columna vale 4; Represalias un 3 y vale
        /// 3; Paso de Cacería, Disparos Lejanos, Tiros Potentes y Flecha de Expiación mandan un 2
        /// y valen 2 en el grado que juega el personaje de la captura.
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
                // Un HECHIZO lleva el hechizo y NO lleva el campo del arma. Escribirlo aunque
                // fuera a cero cambiaba los bytes, y el auto-test del protocolo lo cazó a la
                // primera comparando contra la captura: por eso el if envuelve a los dos.
                var delHechizo = Pb.New();
                if (portals != null && portals.Count > 0) delHechizo.Packed(1, portals.Select(p => (long)p));
                detalle.Msg(7, delHechizo.Var(2, spell).VarIfNotZero(3, spellLevel));
                // The f8 is a cast somebody made; a chained one goes without it.
                return chained ? detalle : detalle.Var(8, 1);
            }

            // Y un golpe CUERPO A CUERPO lleva lo contrario: sin hechizo, y con el arma.
            //
            // Es lo único que distingue un espadazo de un puñetazo, y por eso el chat decía
            // «Puñetazo» al atacar con la espada. Mandar hechizo 0 estaba bien —el servidor real
            // tampoco manda ningún hechizo de arma—; lo que faltaba era esto.
            //
            // El f10 lleva el Id de ItemTemplates del arma equipada, y el puñetazo es el mismo
            // mensaje con el f10 a CERO ESCRITO, no ausente: por eso va con Var y no con
            // VarIfNotZero. Medido en las capturas: Lavacha 19593, Cocobur 20353, Garras de la
            // Despedazadora 31759, Garra de Gargandias 31786; y el puñetazo, «5000» en el cable,
            // que es la etiqueta del campo 10 seguida de un cero.
            return detalle.Var(8, 1).Var(10, arma);
        }

        /// <summary>
        /// La ficha de uno, para refrescarla suelta (jxw).
        ///
        ///   f1: quién      f3 { f3: 2, f5 x N: las características }
        ///
        /// Es la misma ficha que va dentro del jxg y del jxb, aquí sola. Se usa para actualizar los
        /// puntos de movimiento y de acción según se gastan.
        /// </summary>
        /// <summary>Las dos características que van en el molde de los puntos, y sólo ellas.</summary>
        private const int PuntosDeAccion = 1;
        private const int PuntosDeMovimiento = 23;

        public static byte[] BuildFighterSheet(long fighterId,
                                               IEnumerable<(int Characteristic, long Base,
                                                            long Gear, long Buff)> sheet,
                                               bool esElPersonajeControlado)
        {
            // UNA ENTRADA DE jxw SUSTITUYE A LA DEL jxb, NO SE SUMA A ELLA. De ahí sale todo.
            //
            // Aquí se mandaba un VALOR ABSOLUTO metido en el hueco de la base, y con eso cada
            // refresco borraba el equipo y el resto de huecos que el jxb había mandado bien. El
            // servidor real hace lo contrario: vuelve a escribir la entrada COMPLETA —los mismos
            // campos que en el jxb, repetidos aunque no hayan cambiado— y añade el embrujo en su
            // hueco propio, el f8.
            //
            // Medido en la captura del Zobal, sobre el mismo luchador y las mismas características:
            //
            //   jxb   107: f4 { f2: 100 }          25: f4 { f7: 740 }
            //   jxw   107: f4 { f2: 100, f8: +1 }  25: f4 { f7: 740, f8: +100 }
            //   jxw   107: f4 { f2: 100 }          25: f4 { f7: 740 }      al caducar el embrujo
            //
            // El 100 de la base NO se toca en ninguna de las 1.699 entradas detalladas de las
            // capturas. Nosotros mandábamos «f4 { f2: 10 }» —sólo el embrujo, y en el hueco que no
            // es— cincuenta y cinco milisegundos después de haber mandado el 100 bueno. Y el 107 es
            // un MULTIPLICADOR de daño: el cliente estima el golpe multiplicando por él, así que
            // dejarlo en 10 donde vale 100 es la previsualización dividida por diez. Ése era el
            // fallo que se veía jugando.
            //
            // El molde de los puntos —f5— sigue siendo sólo para la 1 y la 23; ninguna otra
            // característica lo usa jamás en las capturas.
            var stats = Pb.New().Var(3, SheetKind);
            foreach (var (characteristic, baseValue, gear, buff) in sheet)
            {
                stats.Msg(5, SheetEntry(characteristic, baseValue, gear,
                                        isMonster: !esElPersonajeControlado, delEmbrujo: buff));
            }
            return Pb.New().Var(1, fighterId).Msg(3, stats).Build();
        }

        /// <summary>
        /// La ficha con la vida que le falta al personaje (jxw con la característica 97).
        ///
        /// Va aparte de <see cref="BuildFighterSheet"/> porque ésa sólo sabe escribir el molde de
        /// los puntos —f5 { f1 }— y la 97 usa el suyo. Sólo se le manda al personaje que maneja
        /// el cliente; ver <see cref="TemporaryLifeMalus"/>.
        ///
        ///   08a28280c8e708 1a1e 1802 2a1a 0861 2216 1098ffffffffffffffff01 40b0feffffffffffffff01
        ///   = al jugador, le faltan 104 de vida y lleva 208 erosionados
        /// </summary>
        public static byte[] BuildLifeSheet(long fighterId, long deficit, long erosion)
            => Pb.New()
                .Var(1, fighterId)
                .Msg(3, Pb.New()
                    .Var(3, SheetKind)
                    .Msg(5, SheetEntry(TemporaryLifeMalus, deficit, -Math.Abs(erosion), false)))
                .Build();

        /// <summary>La secuencia de andar y la de una acción cualquiera.</summary>
        public const int WalkSequence = 4;
        public const int ActionSequence = 3;

        /// <summary>
        /// La secuencia corta en la que el servidor real mete cada ficha suelta: en la captura,
        /// alrededor de todos los jxw hay un jto con f2 = 3 y su jwi con f3 = 3.
        /// </summary>
        public const int SheetSequence = 3;

        /// <summary>
        /// La secuencia con la que empieza cada turno, la de devolver los puntos: en la captura,
        /// detrás del jzc va un jto con f2 = 7 que envuelve las dos fichas —primero los puntos de
        /// movimiento, luego los de acción— y se cierra con un jwi de f3 = 7.
        /// </summary>
        public const int TurnSequence = 7;

        /// <summary>
        /// Un golpe (jwe con el f14 entre 89 y 100).
        ///
        ///   f3: quién pega      f14: de qué elemento
        ///   f40 { f2: a quién, f3: cuánto }
        ///
        /// Medido: con f14 = 91 el f40 lleva { -1, 134 } y con f14 = 93, { -1, 121 }. El f40 tiene
        /// además un f4 y un f5 que cambian de un golpe a otro y que no están descifrados; se dejan
        /// fuera, porque lo que el cliente pinta —a quién y cuánto— sí está.
        /// </summary>
        /// <param name="efecto">
        /// El NÚMERO DE EFECTO del golpe, que es lo que va en el f14: el 91 es robo de agua, el 96
        /// daños de agua, el 99 daños de fuego. No es un código de elemento aparte.
        /// </param>
        /// <param name="elemento">
        /// El elemento, que va en el f4 del detalle. Medido: los golpes de agua llevan un 3 ahí y
        /// los de tierra un 1, los mismos números que la columna ElementId del catálogo.
        /// </param>
        public static byte[] BuildDamage(long author, int efecto, long victim, int amount,
                                         int elemento = -1, int erosion = 0)
        {
            // No amount is no field: the blow on an invulnerable target travels as f40 with the
            // victim and the element only (Influencia's capture), the way proto3 leaves a zero.
            var detalle = Pb.New().Var(2, victim).VarIfNotZero(3, amount);
            if (elemento >= 0) detalle.Var(4, elemento);

            // La EROSIÓN, que faltaba. Va en el f5 y es lo que el golpe se lleva del TOPE de vida,
            // no de la de ahora. Sale en 977 de los 986 bloques de daño de las capturas, y en 727
            // de ellos vale exactamente la décima parte del daño:
            //
            //   c2020e 10a28280c8e708 18ce03 2003 282e   =  462 de daño, 46 de erosión
            //
            // El servidor ya la calculaba —está en Fighter.Erosionar— y no la mandaba, así que el
            // cliente nunca se enteraba de que el tope había bajado.
            detalle.VarIfNotZero(5, erosion);

            return Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(40, detalle)
                .Build();
        }

        /// <summary>
        /// El número de efecto del DAÑO DE COLISIÓN al empujar.
        ///
        /// En el catálogo del cliente se llama <c>CharacterLifePointsLostFromPush</c>, tiene la
        /// descripción vacía en los cinco idiomas y no lo usa ni uno de los 34.685 niveles de
        /// hechizo: no lo escribe ningún hechizo, lo fabrica el motor. La pantalla de fin de
        /// combate lo contabiliza aparte, en su propio renglón.
        /// </summary>
        public const int PushDamage = 80;

        /// <summary>
        /// El daño de haberse chocado al empujar (jwe con el f14 en 80).
        ///
        ///   f3: quién empujó      f14: 80
        ///   f40 { f2: a quién, f3: la vida perdida, f4: -1, f5: la erosión }
        ///
        /// Va aparte de <see cref="BuildDamage"/> porque aquél tiene el convenio de «si el elemento
        /// es menor que cero, no escribas el f4», y aquí el f4 tiene que ir Y valer MENOS UNO: es
        /// así en los 127 mensajes de las 401 capturas, sin una excepción. Menos uno quiere decir
        /// «sin elemento», que no es lo mismo que el cero del neutral.
        ///
        /// Se manda dentro de la misma secuencia del lanzamiento y justo detrás del desplazamiento;
        /// y cuando al empujado no le queda ni una casilla, el desplazamiento no se manda y éste va
        /// solo.
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
        /// RETIRARLE puntos de acción a otro. No confundir con el 102, que es el gasto propio de
        /// lanzar un hechizo: en las 1.796 muestras de las capturas, el 102 y el 129 llevan
        /// SIEMPRE el mismo id como autor y como víctima, y aquí el autor es otro.
        /// </summary>
        public const int ActionPointsLost = 101;

        /// <summary>Retirarle puntos de movimiento a otro. El 129 es andar, que es cosa suya.</summary>
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
        /// Se le han quitado puntos a alguien (jwe): { f3: quién, f14: cuál, f20 { f1: cuántos,
        /// f2: a quién } }.
        ///
        /// La cantidad va en NEGATIVO, en complemento a dos de 64 bits:
        ///
        ///   a20112 08fcffffffffffffffff01 10a282f0a6c408   =  menos cuatro PA
        ///
        /// Esto es lo que hace salir el numerito flotando encima del combatiente, igual que con la
        /// vida. Sin él, el servidor le quitaba los puntos por dentro y en pantalla no se movía
        /// nada: el jugador veía al bicho quedarse sin PA sin que nada se lo dijera.
        /// </summary>
        public static byte[] BuildPointsLost(long author, int efecto, long victim, int cuantos)
            => Pb.New()
                .Var(3, author)
                .Var(14, efecto)
                .Msg(20, Pb.New().Var(1, -Math.Abs(cuantos)).Var(2, victim))
                .Build();

        /// <summary>
        /// Los dos códigos de elemento que están medidos. Los demás caen en el rango 89 a 100 pero
        /// no se ha podido saber cuál es cuál, así que se usa el 91 mientras tanto.
        /// </summary>
        public const int SomeDamage = 91;
        public const int OtherDamage = 93;

        /// <summary>
        /// Una curación (jwe con f14 = 3001, "curas neutrales"):
        ///
        ///   f3: quién cura     f6 { f1: cuánto, f4: A QUIÉN }
        ///
        /// El f4 es el CURADO, y esto corrige lo que decía aquí antes. El comentario anterior
        /// sostenía que el menos dos de la captura de la Baliza no era ninguno de los tres
        /// combatientes y que por tanto no era el destinatario; era mentira, el menos dos es el id
        /// de un monstruo de esa pelea. Contadas las 94 curaciones de las 305 capturas: el f4
        /// lleva siempre un identificador de combatiente de verdad —el jugador 52 veces, otros
        /// jugadores 19, monstruos el resto— y en 40 de las 94 NO coincide con quien cura.
        ///
        /// Mandarlo clavado a menos dos hacía que toda cura se pintara encima del combatiente
        /// menos dos, que en la mayoría de los combates existe y es un bicho cualquiera.
        ///
        /// La curación llega al cable ya resuelta en puntos: en la base el efecto es el 1109,
        /// "Cura: #1% de los PdV máximos", y aquí viaja el número concreto. Es el mismo apaño que
        /// con el robo de puntos, donde el 1080 se anuncia como 169 con la cantidad que salió.
        /// </summary>
        public static byte[] BuildHeal(long author, int cuanto, long curado)
            => Pb.New()
                .Var(3, author)
                .Msg(6, Pb.New().Var(1, cuanto).Var(4, curado))
                .Var(14, Curacion)
                .Build();

        public const int Curacion = 3001;

        /// <summary>
        /// Sale un invocado al tablero (jwe con f14 = 181, "Invoca: #1").
        ///
        /// El efecto no lleva un número: lleva UN COMBATIENTE ENTERO, con tres envoltorios de f1
        /// encima. Medido byte a byte contra la Baliza de Supervivencia del Ocra:
        ///
        ///   f1 { f1 { f1 {
        ///     f1 { f3: 1, f4 { f1 { f1: casilla, f2: orientación, f4: 0 }, f3: quién es } }
        ///     f2: 0                          el hueco del identificador, cero como en un monstruo
        ///     f3 { f2: 3, f3: la plantilla del ASPECTO }
        ///     f5 { f3 { f2: la plantilla del BICHO, f3: su grado } }
        ///     f6 { f1: de quién es, f3: 2, f4: 1, f5 x N: la ficha }
        ///   } } }
        ///
        /// Las dos plantillas no son la misma: la baliza es la 8348 y su aspecto sale de la 8152,
        /// porque en la tabla de bichos el Look de la 8348 es literalmente "{8152}".
        ///
        /// La ficha va con el molde de los monstruos, <c>f2 { f2: valor }</c>, que es el que ya
        /// arma <see cref="SheetEntry"/> con <c>isMonster</c>.
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

        /// <summary>El número de efecto de "Invoca: #1" en el catálogo.</summary>
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
        /// A alguien lo mueven de sitio sin que ande (jwe con el f14 al número del efecto):
        ///
        ///   f3: quién lo provoca    f14: 5 si empuja, 6 si atrae
        ///   f38 { f1: de qué casilla, f2: a quién, f3: a cuál }
        ///
        /// Medido sobre los 76 desplazamientos de las capturas del Ocra. El <c>f14</c> no es un
        /// código del motor: es el número de efecto del catálogo tal cual, el mismo 5 de
        /// "Empuja #1 casilla".
        /// </summary>
        /// <summary>Los dos números con los que viaja un desplazamiento.</summary>
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
        /// Uno se ha muerto (jwe con f14 = 103): <c>f4 { f1: quién }</c>.
        /// </summary>
        public static byte[] BuildDeath(long author, long victim)
            => Pb.New()
                .Var(3, author)
                .Msg(4, Pb.New().Var(1, victim))
                .Var(14, Died)
                .Build();

        // ─── Se acabó ───────────────────────────────────────────────────────────

        /// <summary>Se acabó el combate (kuf). Va vacío.</summary>
        public static byte[] BuildFightOver() => Array.Empty<byte>();

        /// <summary>
        /// Cómo ha quedado (jyg), uno por combatiente.
        ///
        ///   f2 (repetido) { f3 { f1: quién, f3: 1 si vivo }, f4: el resultado }
        ///
        /// El de verdad lleva bastante más dentro —el nivel, la experiencia, la cuenta— y de eso
        /// sólo está descifrada la envoltura, así que aquí va lo mínimo con lo que el cliente puede
        /// cerrar el combate y devolver al jugador al mapa. El panel de recompensas quedará pobre
        /// hasta que se mida entero.
        /// </summary>
        /// <summary>Lo que se lleva uno del combate.</summary>
        public sealed class Spoils
        {
            public long Kamas { get; set; }
            public List<(int Quantity, int Gid)> Items { get; } = new List<(int, int)>();
        }

        /// <summary>Cómo acaba uno el combate: una fila de la pantalla de fin de combate.</summary>
        public sealed class FightResult
        {
            public long Fighter { get; set; }
            public bool Winner { get; set; }

            /// <summary>Su nivel. En cero se entiende que es un monstruo y no lleva ficha.</summary>
            public int Level { get; set; }

            /// <summary>La experiencia acumulada DESPUÉS del combate, y la que se acaba de ganar.</summary>
            public long Xp { get; set; }
            public long XpGained { get; set; }

            public Spoils Spoils { get; set; }
        }

        /// <summary>
        /// Cómo ha quedado cada uno (jyg), que es lo que llena la pantalla de fin de combate.
        ///
        ///   f2 (repetido, uno por combatiente) {
        ///       f2 { f1: los kamas, f2 { f1 { f2: cuántos, f4: el objeto } ... } }   el botín
        ///       f3 { f1: quién
        ///            f2 { f1 { f2 { f1: la experiencia ganada
        ///                           f2: la que hace falta para el nivel siguiente
        ///                           f5: la que tiene ahora
        ///                           f6: la del nivel en el que está
        ///                           f3, f4, f7, f8, f9: unos } }
        ///                 f2: su nivel }
        ///            f3: 1 }
        ///       f4: 2, y sólo en las filas del bando que gana }
        ///   f4: lo que ha durado, en milésimas      f8: -1
        ///
        /// El bloque de la experiencia no está adivinado. Salen cuatro personajes de las capturas
        /// y en los cuatro cuadra con la tabla del cliente (character_xp.json): con f2 = 354, el
        /// f6 vale 23.700.657.518, que es justo lo que pide el nivel 354, y el f2 de dentro vale
        /// 23.932.109.854, que es lo que pide el 355; el f5 cae entre los dos. Igual con 227, 447
        /// y 290. En el duelo, donde no se gana experiencia, el bloque entero no va y sólo queda
        /// el nivel.
        ///
        /// El botín también: en la captura del koliseo el ganador se lleva f1 { f2: 260, f4: 12736 }
        /// y f1 { f2: 2, f4: 34478 }, y 12736 y 34478 son la Kolicha y la Vitoricha, así que el f4
        /// es el objeto y el f2 cuántos. Ojo, que eso es lo ÚNICO que se saca de ahí: cuál es cada
        /// campo. Las kolichas y las vitorichas son del koliseo, del PvP, y no se reparten aquí;
        /// contra monstruos lo que va en esta lista es lo que suelten sus tablas de drop, y los
        /// kamas en el f1.
        ///
        /// El f2 del botín va SIEMPRE, aunque esté vacío: en las capturas los monstruos llevan un
        /// f2 de cero bytes, no se lo saltan.
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
                // Este f3 va SIEMPRE, gane o pierda.
                //
                // Lo quite creyendo que era del ganador: en el jyg del koliseo sus cuatro entradas
                // se parten en dos y dos, y las dos PERSONAS que pierden no lo traen. Pero la
                // guardia de regresion lo caza contra una captura de combate contra monstruos, y
                // ahi el monstruo que pierde SI lo lleva -- el servidor no arrancaba por esto.
                //
                // O sea que el f3 no significa «ha ganado». Que significa no lo se, y con dos
                // capturas que se contradicen bajo mi lectura, la lectura que sobra es la mia: se
                // deja como estaba, que es lo unico medido de punta a punta.
                quien.Var(3, 1);
                entrada.Msg(3, quien);

                if (result.Winner) entrada.Var(4, Victory);
                jyg.Msg(2, entrada);
            }
            return jyg.VarIfNotZero(4, durationMs).Var(8, Nobody).Build();
        }

        /// <summary>El resultado que lleva el jyg en la captura de una victoria.</summary>
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
        /// Se acabó el turno (jyt).
        ///
        ///   f1: las décimas que sobraron, que se guardan para su siguiente turno (se omite si es
        ///       cero)      f2: de quién era
        /// </summary>
        public static byte[] BuildTurnEnd(long fighterId, int savedDeciseconds = 0)
            => Pb.New().VarIfNotZero(1, savedDeciseconds).Var(2, fighterId).Build();

        /// <summary>Lo que lleva el f3 de la ficha en los combatientes de las capturas.</summary>
        private const int SheetKind = 2;

        /// <summary>
        /// Qué monstruo es: <c>f2 { f1: grado, f2: la plantilla, f3: el nivel }</c>.
        ///
        /// Del poutch de la captura salen 3, 494 y 50, y ese 50 es justamente su nivel.
        /// </summary>
        public static Pb MonsterIdentity(int grade, int monsterId, int level)
            => Pb.New().Msg(2, Pb.New()
                .VarIfNotZero(1, grade)
                .Var(2, monsterId)
                .VarIfNotZero(3, level));

        /// <summary>
        /// Quién es el jugador: la raza, su nombre y poco más.
        ///
        ///   f2 { f1: la raza, f3: ?, f4 { f1: 100, f2: 3, f5: 200 } }
        ///   f4: -1        f7: el nombre        f8 { f1: 1 }
        ///
        /// El f3 del bloque interior valía 354 en la captura y no se ha conseguido explicar; va
        /// fuera. El f4 tiene pinta de bloque de aspecto por ese 3 en medio, pero tampoco está
        /// descifrado, así que se manda tal cual se midió.
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
        /// Las características que el servidor real manda en la ficha de la colocación, en su
        /// orden. Casi todas van a cero ahí: lo que importa en esta fase es que el hueco exista.
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
        /// Quiénes pelean (jzu).
        ///
        ///   f2 (repetido, uno por COMBATIENTE) { f3 { f2: quién } }
        ///
        /// Ojo, que esto es fácil de leer al revés: el f2 repetido NO es un equipo, es un
        /// combatiente. Contra un solo monstruo salen dos bloques y parece un equipo cada uno;
        /// contra cuatro poutchs salen CINCO, con el jugador y luego -1, -2, -3 y -4. Los
        /// monstruos llevan su propio identificador negativo, no todos el mismo.
        ///
        /// El orden es el del jugador primero y los monstruos detrás.
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
        /// Dónde se puede uno colocar (kba).
        ///
        ///   f1 { f1: [las casillas del equipo 0], f2: [las del equipo 1] }
        ///
        /// Las dos listas van empaquetadas, y en las capturas son dieciséis casillas por bando.
        /// </summary>
        public static byte[] BuildPlacementCells(IEnumerable<long> blue, IEnumerable<long> red)
            => Pb.New()
                .Msg(1, Pb.New().Packed(1, blue).Packed(2, red))
                .Build();

        // ─── Colocarse ──────────────────────────────────────────────────────────

        /// <summary>
        /// A qué casilla se quiere mover el jugador durante la colocación (jzy).
        ///
        ///   f1: quién      f2: la casilla
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
        /// Quién está en qué casilla (kmk).
        ///
        ///   f2 (repetido) { f1: casilla, f2: orientación, f3: quién }
        ///
        /// Moverse durante la colocación viaja como DOS entradas en un solo mensaje: la casilla que
        /// se deja, con <see cref="Nobody"/>, y la que se ocupa, con quién la ocupa. Mandar sólo la
        /// nueva deja al que mira viendo dos veces al mismo.
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

        /// <summary>Uno solo, que es el caso corriente.</summary>
        public static byte[] BuildFighterPlaced(int cell, int orientation, long fighter)
            => BuildFightersPlaced(new[] { (cell, orientation, fighter) });

        // ─── Listo ──────────────────────────────────────────────────────────────

        /// <summary>
        /// El botón de listo (kaq).
        ///
        ///   f1: 1
        ///
        /// Devuelve si el jugador se declara listo. En las capturas siempre llega con 1; el cero
        /// sería retirar el listo, pero eso no está medido.
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
        /// Enterado del listo (kah).
        ///
        ///   f1: quién      f3: 1
        ///
        /// El f2 no aparece en ninguna de las capturas.
        /// </summary>
        /// <summary>
        /// El desafio que se ofrece (hqc): quien reta, a quien, y el id.
        /// </summary>
        /// <remarks>
        /// Byte por byte de las cuatro capturas de desafio. Los cuatro hqc medidos tienen la misma
        /// forma y sus tres campos siempre puestos:
        ///
        ///   08a28280c8e708 10a282f0a6c408 18ee03    retador, retado, 494
        /// </remarks>
        public static byte[] BuildChallengeOffered(long challengerId, long targetId, int challengeId)
            => Pb.New().Var(1, challengerId).Var(2, targetId).Var(3, challengeId).Build();

        /// <summary>
        /// Como acabo el desafio (hpv). El f3 solo viaja cuando se acepto.
        /// </summary>
        /// <remarks>
        /// Y esa es la diferencia entera entre los dos finales, medida en las cuatro capturas:
        ///
        ///   aceptado   08a28280c8e708 10ee03 1801 20a282f0a6c408
        ///   rechazado  08a28280c8e708 10e903      20a282f0a6c408
        ///
        /// El campo del retado es el CUATRO, no el tres, y eso no es un despiste de la lectura: en
        /// las dos rechazadas el 20 va justo detras del id, sin nada en medio.
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

        // ─── La barra de hechizos ───────────────────────────────────────────────

        /// <summary>
        /// Los hechizos con los que se pelea (jyy).
        ///
        ///   f3: quién      f4: quién (el mismo)
        ///   f6 (repetido) { f1: el grado, f3: el hechizo, f4: 1 }
        ///
        /// El f3 y el f4 llevan el mismo combatiente en las capturas; lo que los separa no se sabe,
        /// porque nunca se han visto distintos.
        /// </summary>
        /// <summary>El origen del hechizo: 1 los de clase, 2 los que no —el cuerpo a cuerpo—.</summary>
        private const int OrigenQueNoEsDeClase = 2;
        private const int GradoDelCuerpoACuerpo = 1;

        /// <summary>El cuerpo a cuerpo es el hechizo CERO, "Puñetazo".</summary>
        public const int HechizoCuerpoACuerpo = 0;

        /// <param name="conArma">
        /// Si lleva la entrada del cuerpo a cuerpo. La llevan las barras de los JUGADORES, las 27
        /// de las capturas; las de los invocados, que traen una o dos entradas, no.
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

            // El cuerpo a cuerpo va el primero de la lista, con el número omitido —es el hechizo
            // CERO, "Puñetazo", que está en la base como SpellTemplates.Id 0— y el origen a 2.
            // Los bytes son 08 01 20 02, y salen en las 27 barras de jugador de las capturas; las
            // de los invocados no la llevan.
            if (conArma)
            {
                jyy.Msg(6, Pb.New().Var(1, GradoDelCuerpoACuerpo).Var(4, OrigenQueNoEsDeClase));
            }

            // Qué hechizos tiene.
            foreach (var (spell, grade) in spells)
            {
                jyy.Msg(6, Pb.New()
                    .VarIfNotZero(1, grade)
                    .Var(3, spell)
                    .Var(4, 1));
            }

            // Y DÓNDE están puestos, que es otra lista y va aparte. Mandando sólo la primera, el
            // cliente sabe qué hechizos tienes pero deja el panel de "Mis hechizos" en blanco, y
            // sin iconos no hay manera de lanzar nada.
            //
            // Y los huecos. El cuerpo a cuerpo tiene que ir en LOS DOS SITIOS: en la lista de
            // arriba, para que el cliente sepa qué es, y aquí en la barra, para que tenga dónde
            // pintarlo. Los tres intentos anteriores mandaron siempre uno de los dos y nunca los
            // dos, y por eso salía apagado, o no salía.
            //
            // Su entrada en la barra es un f6 PRESENTE Y VACÍO —los bytes 3a 02 32 00—, que es
            // exactamente como proto3 escribe "el hechizo cero". Se leyó en su día como "un hueco
            // que el jugador dejó sin llenar", y eso es falso: un hueco sin llenar sencillamente
            // no se manda. Está en el 100% de las barras de jugador —13 de 13 itg y 51 de 51 jyy—
            // y en el 0% de las de invocado, 0 de 24. Y el personaje del tutorial, con el
            // inventario vacío y sin arma ninguna, la lleva igual: la casilla es del puño.
            foreach (var (slot, spell) in bar)
            {
                var hueco = Pb.New().VarIfNotZero(2, slot);
                if (spell == HechizoCuerpoACuerpo) hueco.EmptyMsg(6);
                else hueco.Msg(6, Pb.New().Var(2, spell));
                jyy.Msg(7, hueco);
            }

            return jyy.Build();
        }

        // ─── Los retos ──────────────────────────────────────────────────────────

        /// <summary>
        /// El estado que lleva todo reto por el cable. Vale dos en el cien por cien de los que
        /// se han visto —en la propuesta, en la lista definitiva y en los de mitad de combate—,
        /// así que de los otros dos valores del enumerado no se sabe nada.
        /// </summary>
        public const int ChallengeState = 2;

        /// <summary>
        /// Cuánto dura la propuesta. Vale quince en las nueve apariciones y no se le ha visto
        /// cambiar; el cliente tiene un <c>OnChallengeProposalUpdateTimer</c>, así que es un
        /// temporizador, pero por lo que se ve podría ser cualquier constante.
        /// </summary>
        public const int ChallengeTimer = 15;

        /// <summary>
        /// Un reto (ldd): { f1: %, f2: cuál, f3 (repetido): objetivos, f4: %, f5: estado }.
        ///
        /// Los dos porcentajes son el de experiencia y el de botín, y en los veintisiete retos
        /// distintos de las capturas SIEMPRE valen lo mismo, así que no hay forma de saber cuál
        /// es cuál. El cliente tampoco ayuda: su ventana pinta un solo número.
        ///
        /// Cuando el extra es cero los dos campos desaparecen —proto3 no manda el cero—, que es
        /// lo que pasa con los retos que impone una anomalía.
        ///
        ///   085f1011205f2802   =   95 %, reto 17, 95 %, estado 2
        /// </summary>
        public static byte[] BuildChallenge(int id, int percent, IEnumerable<(int Cell, long Fighter)>? targets = null)
        {
            var ldd = Pb.New().VarIfNotZero(1, percent).Var(2, id);

            if (targets != null)
            {
                foreach (var (cell, fighter) in targets)
                {
                    // Sin objetivo todavía va la casilla a menos uno: en la preparación, un reto
                    // que apunta a dónde acabas el turno no sabe aún dónde vas a estar.
                    ldd.Msg(3, Pb.New().Var(2, cell).VarIfNotZero(3, fighter));
                }
            }

            return ldd.VarIfNotZero(4, percent).Var(5, ChallengeState).Build();
        }

        /// <summary>Cuántos retos hay que elegir (kxa): { f1: n }. Uno fuera de mazmorra.</summary>
        public static byte[] BuildChallengeCount(int howMany) => Pb.New().Var(1, howMany).Build();

        /// <summary>
        /// La lista de candidatos (kwx): { f1: el temporizador, f2 (repetido): los retos }.
        ///
        /// Siempre son dos, y son alternativas: en las capturas se ofrecieron juntos dos que la
        /// tabla del cliente marca como incompatibles entre sí.
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

        /// <summary>La lista definitiva (kwu): { f2 (repetido): los retos }. Va pegada al jyy.</summary>
        public static byte[] BuildChallengeFinalList(IEnumerable<byte[]> challenges)
        {
            var kwu = Pb.New();
            foreach (byte[] uno in challenges) kwu.Bytes(2, uno);
            return kwu.Build();
        }

        /// <summary>La confirmación del ajuste del panel (kwn), con el mismo valor que llegó.</summary>
        public static byte[] BuildChallengeSettings(long value)
            => Pb.New().VarIfNotZero(1, value).Build();

        /// <summary>
        /// El OBJETIVO de un reto (kwm): { f2: el reto, con su objetivo dentro }.
        ///
        /// Es el único mensaje que lleva a quién hay que matar, y su f1 no ha viajado nunca. En
        /// las capturas sale tres veces, las tres pegadas al jyy que arranca el combate; volver a
        /// mandarlo cuando el objetivo cambia es la lectura natural —no hay otro mensaje que
        /// pueda llevarlo— pero eso ya no está medido.
        ///
        ///   1218084610231a0e10860218fdffffffffffffffff0120462802
        ///   = reto 35 al 70 %, objetivo en la casilla 262, luchador −3
        /// </summary>
        public static byte[] BuildChallengeObjective(byte[] challenge)
            => Pb.New().Bytes(2, challenge).Build();

        /// <summary>
        /// El RESULTADO de un reto (kwl): { f1: cuál, f2: cumplido }.
        ///
        /// Sin el f2 está FALLADO, que es como proto3 escribe un booleano falso. El cliente lo
        /// pinta en verde o en rojo, y hasta que no le llega esto lo tiene por vivo: si no se
        /// manda nunca, el reto se queda para siempre en marcha en la pantalla del jugador.
        ///
        ///   08111001   =   reto 17 cumplido
        ///   0801       =   reto 1 fallado
        /// </summary>
        public static byte[] BuildChallengeResult(int id, bool completed)
            => Pb.New().Var(1, id).VarIfNotZero(2, completed ? 1 : 0).Build();

        /// <summary>
        /// Lo que un modificador vale AHORA para un hechizo concreto (hnd).
        ///
        /// El cliente no calcula el alcance de un hechizo a partir de los embrujos del panel: lo
        /// coge de aquí. Sin este mensaje, Disparos Lejanos salía en la lista de efectos con su
        /// «+6 de alcance máximo» y las casillas iluminadas seguían siendo las mismas, porque el
        /// jxm es para pintar y esto es para calcular.
        ///
        ///   f1 { f2: 1, f3: cuánto, f4: qué modificador, f5: el hechizo }   f2: de quién
        ///
        /// Medido en «ocra-disparos lejanos»: f4 = 13 con f3 = 3, y f4 = 12 con f3 = 6, que son
        /// justo el «+3 de alcance mínimo» y el «+6 de alcance máximo» de ese hechizo.
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
        /// La declaración que va con el <see cref="BuildSpellModifier"/> (hnk): dice que ese
        /// hechizo tiene ese modificador puesto. Van los dos, uno detrás de otro y en el mismo
        /// número: 272 y 272 en la captura.
        ///
        ///   f1: qué modificador     f2: 1     f3: el hechizo     f5: de quién
        /// </summary>
        public static byte[] BuildSpellModifierDeclared(long quien, int modificador, int hechizo,
                                                        int accion = Managers.SpellModifiers.Add)
            => Pb.New()
                .Var(1, modificador)
                .Var(2, accion)
                .Var(3, hechizo)
                .Var(5, quien)
                .Build();

        /// <summary>El alcance máximo de un hechizo, tal como lo numera el hnd/hnk.</summary>
        public const int SpellMaxRange = 12;

        /// <summary>Y el mínimo. OJO: el mínimo es el 13 y el máximo el 12, no al revés.</summary>
        public const int SpellMinRange = 13;
    }
}
