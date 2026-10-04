// GENERADO por «protocolbuilder capa». No editar a mano.
//
// Opcodes de 3.6.10.10. El día del parche esto se vuelve a generar y el emulador
// no se toca: los identificadores no cambian, cambia lo que valen.
//
// El identificador es el nombre real del mensaje cuando se sabe, y el opcode que tenía en
// 3.6.10.10 cuando no. Un identificador como «Hjk» es una etiqueta histórica, no una promesa
// de que el opcode siga llamándose así.

namespace Jondo.Unity.Protocol;

/// <summary>
/// Los 254 opcodes que el emulador usa de verdad, con nombre.
///
/// Son <c>const</c> y no propiedades a propósito: hay etiquetas de <c>switch</c> por medio, y
/// una etiqueta de <c>case</c> exige una constante de tiempo de compilación.
/// </summary>
public static class Op
{
    /// <summary>Lo que Ankama pone delante del opcode en el sobre.</summary>
    public const string Prefix = "type.ankama.com/";

    /// <summary>El opcode tal y como viaja: con su prefijo delante.</summary>
    public static string Uri(string opcode) => Prefix + opcode;

    /// <summary>Solo alcanzable desde isi, que nunca llega. 1 mensaje en 1 fichero.</summary>
    public const string Hhf = "hhf";

    /// <summary>Sin identificar. 4 usos en el emulador.</summary>
    public const string Hhh = "hhh";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Hhq = "hhq";

    /// <summary>Lo que la cuenta posee; el cliente ya tiene el catalogo entero y lo que no este en esta lista sale en gris. Se envia una vez al entrar al mundo. En el replay se sustituye por los de la cuenta que juega.</summary>
    public const string Hhy = "hhy";

    /// <summary>El titulo que se lleva ahora; vacio significa ninguno, no un cero dentro.</summary>
    public const string Hid = "hid";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hie = "hie";

    /// <summary>El ornamento que se lleva ahora, con la misma regla.</summary>
    public const string Hif = "hif";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hii = "hii";

    /// <summary>Destino elegido.</summary>
    public const string Hjc = "hjc";

    /// <summary>La lista de destinos del zaap; f6 casa con MapPositions en las 25 entradas de la captura y el destino donde ya estas viaja sin f2, o sea coste cero.</summary>
    public const string Hjj = "hjj";

    /// <summary>Viaja con jru en cada cambio de mapa.</summary>
    public const string Hjk = "hjk";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hke = "hke";

    /// <summary>Parte de la rafaga de inicializacion de 3.6.4.3 que dispara kkn. No aparece en ninguna de las 242 capturas.</summary>
    public const string Hmd = "hmd";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Hmj = "hmj";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Hml = "hml";

    /// <summary>Los hechizos que tiene el personaje, cada uno al grado que abre su nivel.</summary>
    public const string Hms = "hms";

    /// <summary>Cambiar un hechizo por su variante, desde el panel o desde la barra.</summary>
    public const string Hmt = "hmt";

    /// <summary>El hechizo nuevo y el grado que abre el nivel del personaje.</summary>
    public const string Hng = "hng";

    /// <summary>El builder no se llama nunca; la rama hmv usa un payload crudo y hmv nunca llega. 243 mensajes en 9 ficheros.</summary>
    public const string Hnk = "hnk";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hnn = "hnn";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hnp = "hnp";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hnv = "hnv";

    /// <summary>Conyuge y gremio de la cuenta grabada; se descarta. 4 mensajes.</summary>
    public const string Hol = "hol";

    /// <summary>Saludo del servidor de juego; f5 se omite a proposito porque no aparece en ninguna de las tres capturas de arranque.</summary>
    public const string Hoy = "hoy";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Hpd = "hpd";

    // ─── Desafios entre jugadores ───────────────────────────────────────────────────────────
    //
    // Medidos en las cuatro capturas de desafio de la carpeta Combate. Los cuatro mensajes y sus
    // cuerpos, tal cual:
    //
    //   C->S  hph { f1: a quien se reta, f2: 1, f3: n }
    //   S->C  hqc { f1: retador, f2: retado, f3: id del desafio }
    //   C->S  hpu { f1: id }              rechazar
    //   C->S  hpu { f1: id, f2: 1 }       ACEPTAR
    //   S->C  hpv { f1: retador, f2: id, f3: 1 si se acepto, f4: retado }
    //
    // La diferencia entre aceptar y rechazar es ese f2 del hpu, y se ve en las cuatro sin
    // excepcion: las dos capturas de rechazo mandan «08e903» y «08ea03», a secas, y la de aceptar
    // manda «08ec031001». El hpv lo repite en su f3: esta en las dos aceptadas y en ninguna de
    // las rechazadas.

    // ─── Koliseo ────────────────────────────────────────────────────────────────────────────
    //
    // Medidos en «koliseo completo con invitacion-koli 2vs2». El cliente pide la tabla de
    // modalidades con un lux vacio y el servidor contesta con el ltd:
    //
    //   f1 (repetido) { f1: indice, f2 { f1: 1, f4: cuantos por equipo }, f3: 1 si esta abierta }
    //
    // Los cuatro que trae la captura, tal cual:
    //
    //   f1{      f2{f1=1 f4=1} f3=1 }    1 contra 1, abierta
    //   f1{f1=1  f2{f1=1 f4=2} f3=1 }    2 contra 2, abierta
    //   f1{f1=2  f2{f1=1 f4=3} f3=1 }    3 contra 3, abierta
    //   f1{f1=3  f2{     f4=3}      }    otra de tres, SIN el f3: cerrada
    //
    // El indice cero no viaja, que es lo normal en protobuf; y la cuarta se distingue por no
    // llevar el f3 ni el f1 de dentro.

    /// <summary>El cliente pide la tabla de modalidades del koliseo. Viaja vacio.</summary>
    public const string Lux = "lux";

    /// <summary>Las modalidades del koliseo y cuales estan abiertas.</summary>
    public const string Ltd = "ltd";

    /// <summary>El cliente confirma que se apunto a una modalidad. Devuelve el mismo indice.</summary>
    /// <remarks>
    /// Es la respuesta al luy y llega a los 38 ms: se manda «1001» y vuelve «1001». El eco del
    /// indice es todo lo que trae.
    /// </remarks>
    public const string Lth = "lth";

    /// <summary>Apuntarse al koliseo ESTANDO EN GRUPO. El indice de modalidad va en el f1.</summary>
    /// <remarks>
    /// MEDIDO sobre nuestro propio cliente: con un grupo formado, el boton de encontrar partida no
    /// manda el luy sino esto, «0801» -- f1 = 1, que es la entrada 1 del ltd, o sea el 2 contra 2,
    /// el mismo indice y la misma numeracion que lleva el luy en la captura.
    ///
    /// INFERENCIA, y conviene saberlo: no hay captura del camino en grupo. La del koliseo completo
    /// es de alguien que se apunta SOLO -- el ilw del equipo aparece despues del emparejamiento, no
    /// antes --, asi que del lsm sabemos lo que manda el cliente y no lo que contesta el servidor
    /// de verdad. Se le contesta el mismo lth que al luy porque es el acuse que saca la ventana de
    /// su estado de espera, y porque las dos peticiones son la misma cosa con y sin grupo.
    /// </remarks>
    public const string Lsm = "lsm";

    /// <summary>El cliente vuelve del koliseo. Viaja vacio.</summary>
    /// <remarks>
    /// Antes ponia aqui que era salirse de la cola, y era falso. Ordenando las dos mitades de la
    /// conexion por marca de tiempo se ve que el luy y el lte estan a CINCO MINUTOS Y MEDIO uno
    /// del otro -- 109,6 s y 446,0 s --, con el combate entero en medio. El lte llega al volver,
    /// y el servidor le contesta con lty, lsr y lsx en 80 ms.
    /// </remarks>
    public const string Lte = "lte";

    /// <summary>Leaving the Koliseo queue: the window's button while it is searching. Travels empty.</summary>
    /// <remarks>
    /// Read from the client, with no capture of it: fff::bdyu, on the window's leave event, sets
    /// its registered mode to -1 and sends an lsi with nothing in it. What turns the window back
    /// to "search a fight" is an lsx with f1 false and reason 3 (fff::bdyy passes f3 on;
    /// PvpArenaFightsUi.OnArenaRegistrationStatusUpdate: 0 searching, 3 idle).
    /// </remarks>
    public const string Lsi = "lsi";

    /// <summary>Algo del koliseo que el servidor empuja. No esta descifrado.</summary>
    /// <remarks>
    /// Dos apariciones y las dos distintas: «08012001» sin que el cliente pida nada, 27 s despues
    /// de entrar, y «18032001» contestando al lte de la vuelta. f1=1/f4=1 frente a f3=3/f4=1: que
    /// significa cada campo no se sabe. Se manda el segundo tal cual al recibir el lte, que es lo
    /// unico medido; no se inventa el primero.
    /// </remarks>
    public const string Lsx = "lsx";

    /// <summary>El koliseo ha encontrado partida: «acepta o rechaza». Los segundos van en el f2.</summary>
    /// <remarks>
    /// MEDIDO dos veces, en dos capturas de servidores distintos y de modalidades distintas -- un
    /// 2 contra 2 y un 3 contra 3 -- y las dos traen exactamente «103b», o sea f2 = 59. En la del
    /// 3 contra 3 el jugador dejo vencer el plazo y entre el lsh y lo que manda el servidor al
    /// vencer pasan 60.014 ms: son segundos, no un identificador.
    ///
    /// La forma completa, leida del cliente, es { int32, int32, repeated int64 }. Del f1 y del f3
    /// no se sabe nada porque en las dos muestras no viajan.
    /// </remarks>
    public const string Lsh = "lsh";

    /// <summary>
    /// Server to client: the Koliseo ladder, { f2: season start, f3 (repeated): per mode { f1: mode,
    /// f3: league, f4: placement fights left, f5/f6: season wins/fights, f7: day wins, f8 { f1:
    /// best league }, f10: day fights } }. At world entry and after the lte of a Koliseo's end.
    /// </summary>
    public const string Lty = "lty";

    /// <summary>Acompana al lte de la vuelta del koliseo. Viaja vacio, en la raiz f3.</summary>
    public const string Lsr = "lsr";

    /// <summary>El servidor manda al cliente a OTRO servidor, el del koliseo.</summary>
    /// <remarks>
    /// Este es el hallazgo gordo de la captura y cambia la forma del sistema entero. Siete
    /// segundos despues del luy, cuando ya hay partida, el servidor de mundo manda:
    ///
    ///   lst { f1=502, f2="dofus2-ko-tynril.ankama-games.com", f3=&lt;ip&gt;, f4=&lt;billete de 32&gt; }
    ///
    /// y el cliente ABRE UNA SEGUNDA CONEXION a esa maquina, se autentica con el billete y pelea
    /// alli: el kam, el kaa y los jxg del koliseo viajan por el otro socket, no por este. Al
    /// acabar vuelve al de mundo y manda el lte.
    ///
    /// Jondo es un solo servidor y no hace ese salto: el combate de koliseo se monta en el mismo
    /// sitio. Es una diferencia de arquitectura, no un detalle, y por eso esta escrita aqui.
    /// </remarks>
    public const string Lst = "lst";


    /// <summary>El cliente reta a alguien. Lleva a quien reta en el campo 1.</summary>
    public const string Hph = "hph";

    /// <summary>El servidor anuncia el desafio: retador, retado y su id.</summary>
    public const string Hqc = "hqc";

    /// <summary>La respuesta del retado. Con f2=1 acepta; sin el, rechaza.</summary>
    public const string Hpu = "hpu";

    /// <summary>Como acabo el desafio. El f3 a uno quiere decir que se acepto.</summary>
    public const string Hpv = "hpv";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Hqa = "hqa";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ibo = "ibo";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Idf = "idf";

    /// <summary>
    /// El paso en el que va una mision, con sus objetivos y si estan hechos. Respuesta al ieo.
    /// </summary>
    /// <remarks>
    /// Los seis opcodes de misiones —ieo, idu, idz, idw, ief, iec— salen de medir las 401
    /// capturas, no de suponer. La forma es 1{2{1*{2:objetivo,4:estado}, 2:paso}, 3:mision}, y lo
    /// que la sostiene es que cuadra sola: en las 448 tramas idu de las capturas, el paso pertenece
    /// de verdad a esa mision las 448 veces, y los 1.479 objetivos pertenecen de verdad a ese paso.
    ///
    /// Los documentos del repositorio archivaban ieo/idu como la pareja de elementos interactivos
    /// (docs/NOTAS_MIGRACION_AUTH.md) y idz/idw como «extras de conexion» (docs/opcodes.md). Era
    /// una suposicion vieja, del mismo tipo que la que puso nombres de misiones a lry/isf/lol/izu,
    /// que no aparecen en ninguna de las tres capturas de misiones.
    /// </remarks>
    public const string Idu = "idu";

    /// <summary>
    /// El diario de misiones entero, de una vez. Es lo que rellena la ventana al entrar al mundo.
    /// </summary>
    /// <remarks>
    /// Medido sobre el bloque de entrada al mundo, que trae una sola trama de 11.667 bytes:
    ///
    ///   f2 { f1 (repetido) : una mision EN CURSO, con la misma forma que el idu
    ///        f3 (repetido) { f1: 1, f2: id de mision }  : una mision TERMINADA
    ///        f4 : uno, vacio }
    ///
    /// En la captura van 261 del primero y 548 del segundo, que es exactamente lo que el cliente
    /// enseña en sus dos pestanas. Los 261 ids del f1 son misiones de verdad, los 261; y de los
    /// 548 del f3 lo son los 548.
    ///
    /// No confundirlo con el idu, que dice donde ha llegado UNA mision y se manda de una en una.
    /// El bloque capturado trae las dos cosas: 261 tramas idu sueltas Y este idr con las mismas
    /// 261 dentro. Quitar solo el idu dejaba el diario ajeno entrando igual por aqui.
    /// </remarks>
    public const string Idr = "idr";

    /// <summary>
    /// Las misiones que el jugador lleva EN SEGUIMIENTO, la cajita de la esquina.
    /// </summary>
    /// <remarks>
    /// Distinta del diario. El idr dice cuales estan empezadas; este dice cuales se muestran, y
    /// llega reemplazando la lista entera, no anadiendo a ella.
    ///
    ///   f2 (repetido) { f1 { f2 { f1 (repetido): un objetivo, f2: el paso }
    ///                        f3: el id de la mision } }
    ///
    /// El bloque capturado trae una sola trama suya con las misiones 1869 y 2406 —«El dano de
    /// Buril» y «Cuando el despertar no es mas que un sueno»— del jugador grabado. Como reemplaza,
    /// borraba de la caja la mision que el personaje si tenia: el diario iba bien y la caja
    /// mostraba las dos ajenas y ninguna propia.
    /// </remarks>
    public const string Iel = "iel";

    /// <summary>
    /// Los logros ganados, todos de una vez: f2 { f1 (repetido) { f2: cuando, f3: id del logro } }.
    /// </summary>
    /// <remarks>
    /// 954 entradas en la captura, y los 954 ids del f3 son logros de verdad. El f2 es un numero
    /// grande y constante por tandas —302677754146, 879988113698— que tiene pinta de fecha.
    ///
    /// Es la pareja de <see cref="Mfu"/>, que anuncia UNO recien ganado.
    /// </remarks>
    public const string Mft = "mft";

    /// <summary>El cliente da un objetivo por cumplido: 1:mision, 2:objetivo.</summary>
    /// <remarks>
    /// Va del cliente al servidor, comprobado por puertos y no solo por el campo del sobre. Es asi
    /// porque los objetivos de tipo 0 —5.670 de los 15.547— son texto libre que pide pulsar algo de
    /// la interfaz, y de eso el servidor no se entera nunca.
    /// </remarks>
    public const string Idw = "idw";

    /// <summary>El servidor da un paso por validado: 1:mision, 2:paso.</summary>
    public const string Idz = "idz";

    /// <summary>El cliente pregunta por una mision suya: 1:mision.</summary>
    public const string Iec = "iec";

    /// <summary>Arranca una mision: 1:mision.</summary>
    /// <remarks>
    /// El unico de los seis que ya estaba documentado en el repositorio. Sale en 12 tramas de 5
    /// capturas y las 5 son de coger una mision hablando con un NPC.
    /// </remarks>
    public const string Ief = "ief";

    /// <summary>El cliente pide en que paso va una mision: 2:mision. Se contesta con idu.</summary>
    public const string Ieo = "ieo";

    /// <summary>Alianzas, por nombre y tag; se descarta. 9 mensajes.</summary>
    public const string Ife = "ife";

    /// <summary>Un logro conseguido: 2 = estado, 4 = el logro. Del servidor al cliente.</summary>
    /// <remarks>
    /// Los tres opcodes de logros —mfs, mfu, mga— salen de las capturas. Los ocho ids que lleva mfs
    /// y los veinte de mfu son logros de verdad, y encima cuadran con lo que estaba pasando: en la
    /// captura del tutorial sale el 8518 «Primer tiempo», cuyo objetivo es exactamente (Qf=2511),
    /// justo despues de acabar la mision 2511 «Primeras armas».
    /// </remarks>
    public const string Mfs = "mfs";

    /// <summary>Un logro conseguido, con quien y a que nivel: 1{1 nivel, 2 personaje, 3 logro}.</summary>
    public const string Mfu = "mfu";

    /// <summary>El cliente pide la recompensa de un logro: 1 = el logro, o -1 para todos.</summary>
    public const string Mga = "mga";

    /// <summary>
    /// C→S, empty: sent as the achievement window opens, first of three (mfe, mfp, mff). Answered
    /// here with <see cref="Mgb"/>, the order the one capture of the window gives the answers in.
    /// </summary>
    public const string Mfe = "mfe";

    /// <summary>C→S, empty: the second request of an opening achievement window. Answered with <see cref="Mfx"/>.</summary>
    public const string Mfp = "mfp";

    /// <summary>C→S: one category of the achievement window, f1 the category. Answered with <see cref="Mfo"/>.</summary>
    public const string Mff = "mff";

    /// <summary>C→S: one achievement's details, f1 the achievement (INFERRED, never captured). Answered with <see cref="Mfg"/>.</summary>
    public const string Mfm = "mfm";

    /// <summary>
    /// S→C: a category's achievements with every objective's progress. f2 (repeated) { f2 id,
    /// f3 (repeated) { f1 objective, f2 out of, f4 so far, present even at zero while not done } }.
    /// </summary>
    public const string Mfo = "mfo";

    /// <summary>S→C: the achievements closest to being earned, f1 (repeated) the same record as <see cref="Mfo"/>.</summary>
    public const string Mgb = "mgb";

    /// <summary>S→C: one achievement's details, f1 the same record as <see cref="Mfo"/>. INFERRED.</summary>
    public const string Mfg = "mfg";

    /// <summary>S→C, root 3 and empty in the one capture: the answer to <see cref="Mfp"/>. f1 would be a list of number pairs.</summary>
    public const string Mfx = "mfx";

    /// <summary>C→S: play an emote, f1 the emote. Answered by <see cref="Khh"/> to the whole map, or not at all.</summary>
    public const string Khl = "khl";

    /// <summary>S→C: an emote played. f1 character, f3 emote, f5 account, f6 animation name.</summary>
    public const string Khh = "khh";

    /// <summary>S→C: the character's emotes, f1 packed. Sent on entering the world.</summary>
    public const string Khn = "khn";

    /// <summary>C→S: a smiley over the head, f2 the smiley.</summary>
    public const string Hov = "hov";

    /// <summary>S→C: a smiley over somebody's head. f2 character, f3 account, f4 smiley.</summary>
    public const string Hoc = "hoc";

    /// <summary>C→S: the mood smiley, f5 the smiley, or empty to clear it.</summary>
    public const string Hor = "hor";

    /// <summary>S→C: the mood smiley is now f3, or cleared when empty.</summary>
    public const string Hns = "hns";

    /// <summary>Catorce conjuntos guardados, cada uno con un look; se descarta. 7 mensajes.</summary>
    public const string Ihb = "ihb";

    /// <summary>
    /// El cliente pide el tablero de un combate: va vacio, en pareja con el kmv del mapa, en
    /// cuanto carga el mapa tactico que le anuncio un kmp con f1 = 1. Es lo que contesta la
    /// preparacion (ijq kam kaa jxg...), y en una reconexion, el combate tal cual va.
    /// </summary>
    public const string Ijm = "ijm";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ijq = "ijq";

    /// <summary>Solo se llama desde la rama kkn, que nunca llega. 5 mensajes en 3 ficheros.</summary>
    public const string Ilc = "ilc";

    /// <summary>
    /// El cliente pide los detalles de un grupo al que le han invitado: { f2: id del grupo }.
    /// Antes estaba apuntado como "InventoryWeightMessage", que era falso: se midio en la captura
    /// de recibir invitacion, entre el aviso ijz y el ijx de aceptar.
    /// </summary>
    public const string Imd = "imd";

    // ─── Grupos ─────────────────────────────────────────────────────────────
    //
    // Medido de las seis capturas de la carpeta Grupos, con los dos puntos de vista: en unas
    // graba quien invita y en otras el invitado, y los mensajes no son los mismos.
    //
    // Se invita por NOMBRE y se acepta por ID DE GRUPO, que es lo que mas despista: el ime lleva
    // "Uber-Black" en texto y el ijx lleva 71272. El grupo tiene identificador propio, un contador
    // del servidor -69145, 69158, 69186, 71272 en las cuatro capturas-.

    /// <summary>Invitar a alguien al grupo, POR SU NOMBRE: { f1 { f4 { f1: nombre } }, f3: true }.</summary>
    public const string Ime = "ime";

    /// <summary>
    /// El grupo entero: { f1 (repetido): miembro { f1: su hoja, f2: su id }, f4: EL JEFE,
    /// f7: id del grupo, f10: plazas }. Es el unico mensaje grande de todo el sistema.
    /// </summary>
    public const string Ing = "ing";

    /// <summary>
    /// Un invitado pendiente se anade a la lista: { f2: id del grupo, f3: su ficha }, y la ficha
    /// es { f1: su aspecto, f2: su nombre, f3: su id, f5: quien invita, f6 { f1: 1 }, f8: su raza }.
    /// </summary>
    public const string Imf = "imf";

    /// <summary>
    /// Ha entrado alguien nuevo: { f1: id del grupo, f2: el miembro { f1: su hoja, f2: su id } }.
    /// El miembro es EXACTAMENTE el mismo bloque que va repetido dentro del ing.
    /// </summary>
    public const string Ink = "ink";

    /// <summary>
    /// Te han invitado: { f1: tu, f2: quien invita, f3: plazas, f5: id del grupo, f7: su nombre }.
    /// Es el que saca la ventanita.
    /// </summary>
    public const string Ijz = "ijz";

    /// <summary>ACEPTAR la invitacion: { f1: id del grupo }.</summary>
    public const string Ijx = "ijx";

    /// <summary>RECHAZAR la invitacion: { f2: id del grupo }.</summary>
    public const string Iki = "iki";

    /// <summary>A quien rechaza: se acabo la invitacion { f1: id del grupo, f2: quien invitaba }.</summary>
    public const string Ilo = "ilo";

    /// <summary>Al que invito: quitale de la lista de invitados { f1: el invitado, f2: el grupo }.</summary>
    public const string Iko = "iko";

    /// <summary>ABANDONAR el grupo: { f2: id del grupo }. El f1 es un bool y no viaja.</summary>
    public const string Inh = "inh";

    /// <summary>Te has salido: { f1: id del grupo }. El cliente le quita la ventana.</summary>
    public const string Ils = "ils";

    /// <summary>El grupo se ha deshecho: { f1: id del grupo }. Llega pegado al iko.</summary>
    public const string Imy = "imy";

    /// <summary>Ceder el mando: { f1: el nuevo jefe, f3: id del grupo }.</summary>
    public const string Ima = "ima";

    /// <summary>Hay jefe nuevo: { f1: el nuevo jefe, f2: id del grupo }. Once bytes; NO se reenvia el grupo.</summary>
    public const string Ilx = "ilx";

    /// <summary>
    /// Following the leader switched on or off: { f1: on }. Empty when off, 0801 when on; the
    /// member's client answers the first with an imo and the second with an imh. Measured in
    /// "Grupos/con grupo seguir desplazamiento del lider...": empty in the same segment as the
    /// kmu of the leader leaving by zaap. The ima of "nombrar a otro jugador jefe" gets it empty.
    /// </summary>
    public const string Imk = "imk";

    /// <summary>
    /// Where the leader is, to the members following him: { f1: leader, f3 { f1: map, f2: x,
    /// f4: subarea, f5: y }, f4: cell }. After every walk of his and every map change; it is
    /// what makes the member's client walk after him.
    /// </summary>
    public const string Ikv = "ikv";

    /// <summary>A member asks to follow the leader: empty. Answered with lqn 1662, ikv and iln.</summary>
    public const string Imh = "imh";

    /// <summary>The answer to imh, empty, on root field 3 with the request id.</summary>
    public const string Iln = "iln";

    /// <summary>A member stopped following: { f1: the member }. Sent to him, between lqn 1661 and inb.</summary>
    public const string Ika = "ika";

    // Los siguientes los nombra Akuma en su tabla y encajan con los que salen sueltos en las
    // capturas, pero NO se han medido campo a campo aqui: no hay ninguna captura donde se expulse
    // a nadie ni donde un miembro entre en combate. Se dejan escritos para no volver a buscarlos.

    /// <summary>
    /// EXPULSAR a un miembro: { f1: id del grupo, f2: a quien se echa }. Medido del cliente
    /// de verdad, que lo manda al pulsar el boton de la ficha del miembro.
    /// </summary>
    public const string Ili = "ili";

    /// <summary>
    /// Un miembro sale del grupo. El cliente SI lo maneja -tiene su metodo en la clase de la
    /// interfaz de grupo- pero no aparece en ninguna captura, asi que su forma esta SIN MEDIR:
    /// el proto le da { int64, bool, int32 } y ese fichero se equivoca de numeracion a menudo.
    /// </summary>
    public const string Inc = "inc";

    /// <summary>
    /// Actualizacion completa de un miembro: { f1: id del grupo, f2: su hoja }. Misma forma que
    /// el ink. Sale en la captura del koliseo y en la de busqueda automatica de grupo.
    /// </summary>
    public const string Ilw = "ilw";

    /// <summary>
    /// Como le va a un miembro: { f1: quien, f2 { f1: 5, f3: prospeccion, f4: vida,
    /// f6: vida maxima }, f4: id del grupo }. Llega cada vez que a alguien le cambia la vida.
    /// </summary>
    public const string Ino = "ino";

    /// <summary>A member stops following the leader: empty. Answered with lqn 1661, ika and inb.</summary>
    public const string Imo = "imo";

    /// <summary>The answer to imo, empty, on root field 3 with the request id.</summary>
    public const string Inb = "inb";

    /// <summary>Los detalles de un grupo, respuesta al imd.</summary>
    public const string Ilb = "ilb";

    /// <summary>Un companero de grupo ha entrado en combate. Sin medir.</summary>
    public const string Ilh = "ilh";

    /// <summary>Unirse a un combate del grupo. Sin medir.</summary>
    public const string Kay = "kay";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ioc = "ioc";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ios = "ios";

    /// <summary>
    /// Client to server: an NPC's action, { f1: action, f2: map, f3: the NPC }. A marketplace's
    /// buy and sell buttons come through here too, with no NPC: { f1: 5 sell or 6 buy, f2: the
    /// map, f3: -1 } in the equipment marketplace capture.
    /// </summary>
    public const string Iov = "iov";

    /// <summary>
    /// Que actor del mapa tiene misiones que ofrecer: la marca verde encima del NPC.
    /// </summary>
    /// <remarks>
    /// 1 { 2 (repetido) { 2: ids de mision empaquetados, 4: actor }, 3: mapa }
    ///
    /// Medido: en las 380 tramas de las capturas, los 294 numeros que llevan dentro son ids de
    /// mision reales, los 294. Y se ve la marca apagarse: en el tutorial el actor sale primero con
    /// [2511] y despues el mismo actor con la lista vacia, que es justo cuando se coge la mision.
    /// 235 de las 380 van vacias, que es como se borra.
    /// </remarks>
    public const string Iom = "iom";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ioy = "ioy";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ipv = "ipv";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ipw = "ipw";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Irm = "irm";

    /// <summary>Los oficios; se conserva la lista de ids (es dato de juego) y se tira el progreso capturado: todos salen a nivel 1.</summary>
    public const string Irq = "irq";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Iry = "iry";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ise = "ise";

    /// <summary>
    /// One job's artisans, the answer to isr: { f1 (repeated) { f1 { f2: job, f3: minimum level,
    /// f4: free, f5: job level }, f2 { f1 { f1: 1 }, f2: name, f3: breed, f4: id, f5: sex,
    /// f7 { f1: map } } } }.
    /// </summary>
    public const string Isf = "isf";

    /// <summary>Movimiento de objeto antiguo (3.6.4.3), sustituido por iuk. No aparece en ninguna captura.</summary>
    public const string Isi = "isi";

    /// <summary>Un objeto sale del cofre.</summary>
    public const string Itc = "itc";

    /// <summary>Un objeto llega al cofre.</summary>
    public const string Itd = "itd";

    /// <summary>Las barras de atajos; el servidor envia dos y nada dentro dice cual es cual: un hueco con hechizo lleva f6, uno con objeto lleva f9, y el f2 suelto del final es el tipo de barra (1 hechizos, ausente objetos).</summary>
    public const string Itg = "itg";

    /// <summary>Parte de la rafaga final de 3.6.4.3 que dispara ibt (icg se envia tres veces). No aparece en ninguna de las 242 capturas.</summary>
    public const string Ith = "ith";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Itj = "itj";

    /// <summary>Parte de la rafaga de inicializacion de 3.6.4.3 que dispara kkn. No aparece en ninguna de las 242 capturas.</summary>
    public const string Itp = "itp";

    /// <summary>
    /// The client asks for its inventory again: { f3: 2 } after a workshop opens, { f3: 1 }
    /// elsewhere. Answered with ivx and an empty hlm in the twelve times it is captured.
    /// </summary>
    public const string Itr = "itr";

    /// <summary>
    /// Items arrive in the bag, a list of them: { f1 (repeated) { f1: 63, f5: item } }. The
    /// crafted ring of the tutorial arrives this way, not by iua.
    /// </summary>
    public const string Itf = "itf";

    /// <summary>
    /// Stacks change size, a list of them: { f1 (repeated) { f2: uid, f3: total } }. A craft whose
    /// result joins a stack already in the bag (the runes fused in the grinder's capture).
    /// </summary>
    public const string Itu = "itu";

    /// <summary>Editar un hueco de una barra de atajos; se escribe tambien en la base de datos o se pierde al salir.</summary>
    public const string Itz = "itz";

    /// <summary>Un objeto llega a la bolsa, con todo: plantilla, efectos y cantidad.</summary>
    public const string Iua = "iua";

    /// <summary>
    /// El estado de un elemento del mapa: { f1 { f2: casilla, f3: elemento, f4: estado } }.
    /// Cero lleno, 1 agotado, 2 en uso. Es el mismo juego de campos que el f15 del jss.
    /// </summary>
    public const string Iwf = "iwf";

    /// <summary>
    /// Vuelve a declarar un elemento: { f3 { la misma forma que el f11 del jss } }. Se manda
    /// cuando su habilidad deja de poder usarse o vuelve a poder, porque cambia de campo.
    /// </summary>
    public const string Iwm = "iwm";

    /// <summary>Se acaba de recolectar: { f1: elemento, f3: habilidad }.</summary>
    public const string Iwi = "iwi";

    /// <summary>Lo recogido en esta pasada: { f1: objeto, f2: cantidad }.</summary>
    public const string Itn = "itn";

    /// <summary>
    /// Sube un oficio de nivel: { f1 { f2: oficio, f4: todas sus habilidades }, f3: nivel }.
    /// No lleva experiencia; esa va en el irq.
    /// </summary>
    public const string Isz = "isz";

    /// <summary>Cambia la cantidad de un objeto que ya estaba en la bolsa: { f3 { f2: uid, f3: total } }.</summary>
    public const string Ivj = "ivj";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Iue = "iue";

    /// <summary>Mover un objeto a un hueco o de vuelta a la bolsa; la posicion cero es el amuleto, no la bolsa, y proto3 la omite.</summary>
    public const string Iuk = "iuk";

    /// <summary>Un objeto sale de la bolsa.</summary>
    public const string Ium = "ium";

    /// <summary>Los pods: peso llevado y capacidad. Identificado por aritmetica, cinco pods por punto de fuerza.</summary>
    public const string Iun = "iun";

    /// <summary>Uno por cada hueco de barra que tenia la mitad antigua del hechizo; se envia antes de hng.</summary>
    public const string Iuq = "iuq";

    /// <summary>Destruir un objeto; el cliente no quita nada por su cuenta y sin respuesta el objeto se queda. Observado contra el cliente real en el log de trafico del emulador, no en el conjunto pcapng.</summary>
    public const string Iuw = "iuw";

    /// <summary>Kamas que quedan despues de pagar.</summary>
    public const string Ivf = "ivf";

    /// <summary>El eco: la misma entrada que envio el cliente.</summary>
    public const string Ivk = "ivk";

    /// <summary>Donde acabo un objeto; un hueco solo admite un objeto, lo que hubiera se expulsa antes a la bolsa con su propio ivq.</summary>
    public const string Ivq = "ivq";

    /// <summary>El inventario, construido desde la base de datos; el hueco se omite cuando es cero porque cero es el amuleto.</summary>
    public const string Ivx = "ivx";

    /// <summary>
    /// Los contadores de una cuenta: f2 { f2 (repetido) { f1: contador, f2: valor } }.
    /// </summary>
    /// <remarks>
    /// 9.694 pares en la trama capturada, con ids del 44 al 34.352 y valores de cientos de
    /// millones. Es lo que el cliente pinta en el panel de estadisticas —partidas, monstruos,
    /// kamas— y estuvo mucho tiempo etiquetado como «el inventario», que no es: el inventario es
    /// el ivx, y estos dos opcodes se parecen lo justo para confundirse.
    /// </remarks>
    public const string Ivi = "ivi";

    /// <summary>Lo que hay dentro del cofre; misma forma que el inventario, con la bolsa como posicion de todo.</summary>
    public const string Iwb = "iwb";

    /// <summary>Ese elemento esta en uso; f2 es el elemento, no la instancia de habilidad.</summary>
    public const string Iwn = "iwn";

    /// <summary>Clic en un elemento interactivo; el zaap, el cofre y la loteria llegan todos por aqui.</summary>
    public const string Iwo = "iwo";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Iya = "iya";

    /// <summary>La TORMENTA ASTRAL de los Suenos Infinitos. Viaja vacia.</summary>
    /// <remarks>
    /// Estaba aqui como «sin identificar» y como AlmanaxDateMessage en el fichero de mapeos, que
    /// es falso. Medido en las capturas de suenos: el cliente lo manda vacio y el servidor
    /// contesta con el izg del estado, un jru de cambio de mapa y un izj «1001».
    /// </remarks>
    public const string Izh = "izh";

    /// <summary>
    /// Client to server, INFERRED as buying at a dream's fountain: { f1: int32, f2: a message of
    /// repeated int64, strings and booleans }. No capture has one. It is the only one of the
    /// client's six dream requests that carries a choice; see DreamHandler.BuyAsync.
    /// </summary>
    public const string Iym = "iym";

    /// <summary>
    /// Client to server, empty: the loot table of the dream's room (InfiniteDreamDropTableRequest).
    /// Answered by <see cref="Izo"/>, in five captures.
    /// </summary>
    public const string Ixq = "ixq";

    /// <summary>
    /// Server to client, empty: opens the fountain's shop. Read off the client, not a capture: its
    /// handlers raise the event bxv, and the dream window manager's three bxv methods are the ones
    /// that set up InfiniteDreamShopUi.
    /// </summary>
    public const string Ixm = "ixm";

    /// <summary>
    /// Server to client, by root 3: the loot table of the dream's room, one f2 per item --
    /// { f1: criterion, f2: item, f3: quantity, f5: percent as a float }.
    /// </summary>
    public const string Izo = "izo";

    /// <summary>
    /// Client to server, empty: where a fight on this map would place everybody -- the dream's
    /// bestiary preview and its "show the positions". Answered by <see cref="Jxj"/>.
    /// </summary>
    public const string Kaz = "kaz";

    /// <summary>
    /// Server to client, by root 3: { f1: the fight's map, f2: the map, f3 { f1: the attackers'
    /// cells, f2: the defenders' cells, packed } }. Measured in "Pesadilla III-...-mostrar
    /// posiciones combate" and the long capture.
    /// </summary>
    public const string Jxj = "jxj";

    // ─── Los Suenos Infinitos ───────────────────────────────────────────────────────────
    //
    // Trece capturas en «Sueños Infinitos/» cubren el ciclo entero, y este es:
    //
    //   C->S  iwo             usar el pozo, que es un interactivo de los de siempre
    //   S->C  iwn + iyj       el resultado del interactivo y LA VENTANA con el mapa del sueno
    //   C->S  ixf { f1 }      empezar, con la dificultad dentro
    //   S->C  izg + jru       el estado y el cambio de mapa a la primera sala
    //   S->C  ixa             el acuse, por la raiz 3 y vacio
    //   C->S  iwo             elegir puerta: otro interactivo
    //   S->C  izg + jru       estado nuevo y a la sala siguiente
    //   C->S  iyx             salir
    //   S->C  jru + ixg + iom
    //   S->C  iyb «0801»      el acuse de la salida, por la raiz 3

    /// <summary>El mapa de un sueno: la cabecera, las once salas y el grafo que las une.</summary>
    /// <remarks>
    /// Entre 490 y 618 bytes en las capturas. Lleva el nombre del personaje, su nivel y la
    /// dificultad, luego la lista de salas —cada una con su fila, su grupo de MapMobs y el efecto
    /// que la modifica— y por ultimo las conexiones, que dibujan un rombo de cinco filas.
    /// </remarks>
    public const string Iyj = "iyj";

    /// <summary>Empezar un sueno, o descartar el que haya en curso. Un mensaje, dos operaciones.</summary>
    /// <remarks>
    /// El campo dice cual: <c>f1</c> es empezar y lleva la dificultad en su f3, y <c>f2</c> es
    /// descartar. Medido comparando el ixf de nueve capturas contra la dificultad que el jugador
    /// eligio en cada una: 1..3 Sueno, 4..7 Paradoja, 8..10 Pesadilla.
    /// </remarks>
    public const string Ixf = "ixf";

    /// <summary>El estado del sueno en curso. Viaja en cada cambio de sala, junto al jru.</summary>
    /// <remarks>
    /// Su f2 es la dificultad y coincide con la que se mando en el ixf. Su f4 son las PUERTAS que
    /// se ofrecen ahora mismo: cada una con la sala a la que lleva y el identificador de habilidad
    /// que el cliente devolvera en el iwo.
    /// </remarks>
    public const string Izg = "izg";

    /// <summary>El acuse de haber empezado el sueno. Viaja vacio, por la raiz 3.</summary>
    public const string Ixa = "ixa";

    /// <summary>Acompana a la tormenta astral. «1001» en la captura.</summary>
    public const string Izj = "izj";

    /// <summary>Salir del sueno. Viaja vacio.</summary>
    public const string Iyx = "iyx";

    /// <summary>El acuse de la salida. «0801», por la raiz 3.</summary>
    public const string Iyb = "iyb";

    /// <summary>Acompana a la salida del sueno. Viaja vacio.</summary>
    public const string Ixg = "ixg";

    /// <summary>El boton de Suenos Infinitos del menu, y la tecla T. Viaja vacio.</summary>
    /// <remarks>
    /// NO abre la ventana: te lleva al Plano Astral, y alli el pozo es el que la abre. Medido en
    /// la captura de «Sueño III-pelear-morir»: al iyc le siguen un jru al mapa 238551040 -la
    /// subarea 938, «Dominios de Draconiros»- y el iom de siempre. Es lo que dice el devblog, que
    /// al Plano Astral se entra desde cualquier sitio del Mundo de los Doce.
    /// </remarks>
    public const string Iyc = "iyc";

    /// <summary>
    /// Server to client: a house's sale changed, { f2: owner's nickname, f3: house, f5: tag,
    /// f6: price, f8: instance }. Frame 19 of "Casas/poner casa en venta", 23 of the withdrawal.
    /// </summary>
    public const string Izu = "izu";

    /// <summary>
    /// Server to client: the account's houses, f1 (repeated) { f2: house, f4: model, f6 { where,
    /// full plaque } }. The captured one is left out of the world entry replay; ours is built
    /// from the database (HouseHandler.SendAccountHousesAsync), empty with no house.
    /// </summary>
    public const string Jaa = "jaa";

    /// <summary>Server to client, a response: a house's full plaque, { f1: lnx }. The answer to izv.</summary>
    public const string Izr = "izr";

    /// <summary>Client to server: a house's plaque, please, { f1: instance, f2: house }.</summary>
    public const string Izv = "izv";

    /// <summary>Server to client: a house changed, { f1: plaque, f2: house, f3: packed doors }.</summary>
    public const string Izz = "izz";

    /// <summary>Client to server: the sale window's answer, { f1: price, f2: instance, f3: on sale }.</summary>
    public const string Jan = "jan";

    /// <summary>
    /// Client to server, { f1: int64 }: one of the two house requests with a lone number, built by
    /// the class that builds jan and khv. Taken as a buyer's yes by inference; see HouseHandler.BuyAsync.
    /// </summary>
    public const string Jad = "jad";

    /// <summary>The other one; see <see cref="Jad"/>.</summary>
    public const string Jal = "jal";

    /// <summary>Server to client: jjt { f3: instance, f4: house }, a house going on sale (sale frame 15).</summary>
    public const string Jjt = "jjt";

    /// <summary>Server to client: the sale window, { f1: buying, f2: house, f3: instance, f4: 1, f5: price }.</summary>
    public const string Khr = "khr";

    /// <summary>Server to client: a code's outcome, empty or { f2: 1 } for a wrong one.</summary>
    public const string Khu = "khu";

    /// <summary>Client to server: an owner's new code, { f1: code }; no f1 takes it off.</summary>
    public const string Khv = "khv";

    /// <summary>Client to server: a code typed at a locked door or chest, { f1: code }.</summary>
    public const string Khw = "khw";

    /// <summary>Server to client: the code keypad, { f1: 1 to get in, f3: 8 }.</summary>
    public const string Kia = "kia";

    /// <summary>Server to client: the guild chest's tabs, f1 (repeated) { types, tab, rights, name }.</summary>
    public const string Ivl = "ivl";

    /// <summary>Server to client: the guild chest's window, { f1: 22, f2: tab, f3: 100 }.</summary>
    public const string Kbk = "kbk";

    /// <summary>Client to server: another tab of the guild chest, { f2: tab }.</summary>
    public const string Jll = "jll";

    /// <summary>Server to client: who has the guild chest open, f1 (repeated) names.</summary>
    public const string Jlo = "jlo";

    /// <summary>Server to client: jlq { f1: name }, the one who just opened the guild chest.</summary>
    public const string Jlq = "jlq";

    /// <summary>Se envia con los muebles, entre jss y lva; significado no establecido.</summary>
    public const string Jaz = "jaz";

    /// <summary>Modo de colocacion cerrado.</summary>
    public const string Jba = "jba";

    /// <summary>Una porcion de la habitacion; llega partido en tres seguidos y cada porcion lleva la habitacion entera, no un diff.</summary>
    public const string Jbg = "jbg";

    /// <summary>Cambiar el tema de la habitacion desde dentro.</summary>
    public const string Jbl = "jbl";

    /// <summary>Modo de colocacion confirmado.</summary>
    public const string Jbm = "jbm";

    /// <summary>El boton de la bolsa de viaje y la tecla H; lleva un personaje porque se puede visitar la de otro.</summary>
    public const string Jbn = "jbn";

    /// <summary>La respuesta de la maquina de loteria; de dos capturas, una con premio en f2 y otra rechazada con f3: 1.</summary>
    public const string Jbs = "jbs";

    /// <summary>Los muebles de la habitacion, esperados detras del mapa; misma forma que jbg pero en f1 en vez de f2.</summary>
    public const string Jbu = "jbu";

    /// <summary>C→S: editar un rango del gremio. f1 { f2 nombre, f3 permisos, f4 { f2 icono, f3 orden }, f5 id }. Se contesta con el jco entero.</summary>
    public const string Jct = "jct";

    /// <summary>C→S, vacío: abrir la gestión de rangos. Se contesta con el jco.</summary>
    public const string Jcs = "jcs";

    /// <summary>C→S: los permisos de un rango. f1 la lista empaquetada, f2 el rango. Se contesta con el jco.</summary>
    public const string Jck = "jck";

    /// <summary>C→S: crear un rango. f1 el orden, f4 el nombre, f5 el icono. Se contesta con el jco.</summary>
    public const string Jcv = "jcv";

    /// <summary>C→S: la nota de un miembro. f1 el texto, f3 el personaje. Se contesta con el jgz.</summary>
    public const string Jjj = "jjj";

    /// <summary>S→C: un miembro puesto al día. f2 { la misma entrada que el jgu }. Tras la nota y tras contribuir.</summary>
    public const string Jgz = "jgz";

    /// <summary>C→S, vacío: el diario del gremio. Se contesta con el jil.</summary>
    public const string Jim = "jim";

    /// <summary>S→C: el diario. Un f1 por línea { f11 gremio, f17 '' la fundación, f19 cuándo, f20 { f2 personaje, f3 nombre, f4 } }.</summary>
    public const string Jil = "jil";

    /// <summary>C→S: escribir la ficha del anuario. f2 { f2 descripción, f3 nivel mínimo, f4 etiquetas, f5, f6, f9 nivel máximo, f10 gremio, f13 título }. Se contesta con el jci.</summary>
    public const string Jcc = "jcc";

    /// <summary>S→C, vacío: acuse de la búsqueda del anuario, delante del jiv.</summary>
    public const string Jme = "jme";

    /// <summary>S→C: el anuario. Un f1 por gremio { f1 { f1 { f1 jefe, f6 ficha, f7 miembros }, f3 emblema }, f2 id, f3 nombre, f4 nivel }.</summary>
    public const string Jiv = "jiv";

    /// <summary>S→C: al abrir la ventana, f3 '' en un gremio nuevo. Lo que lleva en uno con recorrido no está entendido.</summary>
    public const string Jff = "jff";

    /// <summary>S→C: las contribuciones que quedan esta semana. f1 = 5, 4, 3 en la captura de contribuir.</summary>
    public const string Jla = "jla";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jfc = "jfc";

    // ─── Gremio ──────────────────────────────────────────────────────────────
    //
    // Medido en las 12 capturas de Gremio/. El identificador es el opcode; el nombre va en el
    // comentario porque no lo sabemos por otra via que la captura. Ver Managers.GuildStore y
    // Network.GuildProtocol.

    /// <summary>
    /// S→C, vacío: abre el editor de fundación del cliente. Es la respuesta al iwo del altar del
    /// Templo de los Gremios (elemento 480310 del mapa 106169344), detrás del iwn. 1 mensaje en
    /// la captura de fundar «Jondo», y sin él el editor no se abre nunca.
    /// </summary>
    public const string Jjc = "jjc";

    /// <summary>C→S: crear gremio. f1 el emblema {símbolo, color símbolo, fondo, color fondo}, f2 el nombre.</summary>
    public const string Jjg = "jjg";

    /// <summary>S→C, vacío: detrás del jjg, entre el ium de la gremialogema y el jco. 1 mensaje, sin significado reconstruido.</summary>
    public const string Jhq = "jhq";

    /// <summary>
    /// S→C: an emote learned, f1 the emote. Measured as f1=97 between the jgw and the jgu when a
    /// guild is founded, and its pair khj carries the same 97 on leaving one: 97 is the guild
    /// banner emote ("Pw=2"). The client's emote frame (ern) is what takes it: it looks the emote
    /// up, adds it to the emote list and announces its name.
    /// </summary>
    public const string Khi = "khi";

    /// <summary>S→C, vacío: detrás del khj al salir del gremio, antes del jsn que redibuja al que sale. 1 mensaje.</summary>
    public const string Jhc = "jhc";

    /// <summary>S→C: el gremio al que se pertenece. f2 el puesto, f3 { f1{f3 emblema}, f2 id, f3 nombre, f4 nivel }.</summary>
    public const string Jgw = "jgw";

    /// <summary>S→C: los puestos del gremio (rangos). Uno por f2 { f2 nombre, f3 permisos, f4{f2 icono, f3 orden}, f5 id }.</summary>
    public const string Jco = "jco";

    /// <summary>C→S: abrir una pestaña de la ventana de gremio. f1 sección, f3 sub-pestaña.</summary>
    public const string Jii = "jii";

    /// <summary>C→S: pedir la lista de miembros del gremio.</summary>
    public const string Jml = "jml";

    /// <summary>C→S: acompaña la apertura de la ventana de gremio (parámetro fijo).</summary>
    public const string Jlx = "jlx";

    /// <summary>C→S: parte de la apertura de la ventana de gremio.</summary>
    public const string Jlk = "jlk";

    /// <summary>C→S: parte de la apertura de la ventana de gremio.</summary>
    public const string Jfp = "jfp";

    /// <summary>C→S: cambia de pestaña dentro de la ventana de gremio. f2 la pestaña.</summary>
    public const string Jiy = "jiy";

    // ─── The guild window's tabs, as the captures answer them for a guild with nothing ────
    // Each pair is request → answer (root 3). "Gremio/muchas acciones en mi gremio como lider".

    /// <summary>C→S: the perks tab (with jfp, jeu, jga and an empty jml). Answered with <see cref="Jfs"/>.</summary>
    public const string Jfv = "jfv";

    /// <summary>S→C: answer to the jfv, empty.</summary>
    public const string Jfs = "jfs";

    /// <summary>C→S: the perks tab. Answered with <see cref="Jei"/>, "1a00" for none.</summary>
    public const string Jeu = "jeu";

    /// <summary>S→C: answer to the jeu.</summary>
    public const string Jei = "jei";

    /// <summary>C→S: the perks tab. Answered with <see cref="Jfz"/>, "0a00" for none.</summary>
    public const string Jga = "jga";

    /// <summary>S→C: answer to the jga.</summary>
    public const string Jfz = "jfz";

    /// <summary>C→S: when the week starts again, asked at world entry and from the guild window.</summary>
    public const string Jew = "jew";

    /// <summary>
    /// S→C: answer to the jew, f1 the next weekly reset as an ISO string: Tuesday, 05:00 UTC, in
    /// all five captures ("2026-08-18T05:00:00Z" asked on Wednesday the 12th and Saturday the 15th).
    /// </summary>
    public const string Jez = "jez";

    /// <summary>C→S: asked with the jew from the guild window. Answered with <see cref="Jgq"/>, "0a00" for none.</summary>
    public const string Jgr = "jgr";

    /// <summary>S→C: answer to the jgr.</summary>
    public const string Jgq = "jgq";

    /// <summary>C→S: a guild tab's paged list ("08012200": f1 page 1). Answered with <see cref="Jdb"/>.</summary>
    public const string Jet = "jet";

    /// <summary>S→C: answer to the jet.</summary>
    public const string Jdb = "jdb";

    /// <summary>C→S: a guild tab, with hzc and hvx. Answered with <see cref="Jfr"/>, empty.</summary>
    public const string Jfw = "jfw";

    /// <summary>S→C: answer to the jfw.</summary>
    public const string Jfr = "jfr";

    /// <summary>C→S: sent with the jfw. Answered with <see cref="Ice"/>, "0a00" for none.</summary>
    public const string Hzc = "hzc";

    /// <summary>S→C: answer to the hzc.</summary>
    public const string Ice = "ice";

    /// <summary>C→S: sent with the jfw, and alone. Answered with <see cref="Hxm"/>, empty.</summary>
    public const string Hvx = "hvx";

    /// <summary>S→C: answer to the hvx.</summary>
    public const string Hxm = "hxm";

    /// <summary>C→S: abandonar el gremio. f1 el personaje.</summary>
    public const string Jho = "jho";

    /// <summary>S→C: las candidaturas al gremio. f2 el total, f3 { la candidatura }.</summary>
    public const string Jmf = "jmf";

    /// <summary>S→C: una candidatura nueva. f2 { la candidatura }, f3 la cuenta.</summary>
    public const string Jly = "jly";

    /// <summary>S→C: resultado de invitar. f1 el nombre, f2 un id, f3 el estado (1 pedida, 3 aceptada/rechazada).</summary>
    public const string Jin = "jin";

    /// <summary>S→C: acuse de invitación por id de cuenta.</summary>
    public const string Jma = "jma";

    /// <summary>S→C: la ficha pública del gremio en la ventana (descripción, líder, reclutamiento).</summary>
    public const string Jci = "jci";

    /// <summary>S→C: el número de solicitudes pendientes. f1 el estado.</summary>
    public const string Jij = "jij";

    /// <summary>C→S: abrir la tienda del gremio. Va vacío.</summary>
    public const string Jki = "jki";

    /// <summary>S→C: la tienda del gremio (oráculos). f2 { f1 cuentas, f2 { f1 id, f2 { f1 precio } } }.</summary>
    public const string Jkh = "jkh";

    /// <summary>C→S: comprar un artículo de la tienda del gremio. f1 el artículo.</summary>
    public const string Jkw = "jkw";

    /// <summary>
    /// S→C: acuse de compra en la tienda del gremio. Con f1 el artículo comprado cuando sale
    /// bien; VACÍO cuando se rechaza, que es lo que llega en la captura donde no había kamas.
    /// </summary>
    public const string Jkj = "jkj";

    /// <summary>S→C: lo comprado que falta por activar. f1 { f2 { f3 el plazo } }, f2 el artículo.</summary>
    public const string Jkv = "jkv";

    /// <summary>C→S: activar un oráculo ya comprado. f1 el artículo.</summary>
    public const string Jky = "jky";

    /// <summary>S→C: acuse de activación. f1 el artículo.</summary>
    public const string Jkx = "jkx";

    /// <summary>S→C: una alteración puesta. f1 { f1 desde, f2 la alteración, f4 2, f5 hasta } en milisegundos.</summary>
    public const string Lzs = "lzs";

    /// <summary>C→S: contribuir al gremio. f1 el tramo (1 = 10.000 kamas).</summary>
    public const string Jlb = "jlb";

    /// <summary>S→C: la contribución hecha. f1 los kamas, f2 las que quedan esta semana.</summary>
    public const string Jle = "jle";

    /// <summary>S→C: te invitan a un gremio. f1 el bloque del gremio, f2 quién invita.</summary>
    public const string Jiq = "jiq";

    /// <summary>C→S: contestar a la invitación. Vacío rechaza; f1 = 1 acepta.</summary>
    public const string Jiz = "jiz";

    /// <summary>C→S: ver una candidatura. f2 el personaje que la mandó.</summary>
    public const string Jlt = "jlt";

    /// <summary>C→S: aceptar una candidatura. f2 el personaje que la mandó.</summary>
    public const string Jjn = "jjn";

    /// <summary>C→S: buscar en el anuario. f2, f3 y f17 niveles, f9 y f14 filtros. Se contesta con jme y jiv. (Contribuir es el jlb.)</summary>
    public const string Jjm = "jjm";

    /// <summary>S→C: los kamas de gremio que quedan. f1 la cantidad.</summary>
    public const string Jia = "jia";

    /// <summary>
    /// S→C: an emote lost, f1 the emote. Sent with f1=97, the guild banner, when leaving a guild;
    /// the client's emote frame (ern) removes it from the list. See <see cref="Khi"/>.
    /// </summary>
    public const string Khj = "khj";

    /// <summary>El conyuge, con su look; se descarta. 13 mensajes.</summary>
    public const string Jgu = "jgu";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jgv = "jgv";

    /// <summary>
    /// S→C: belonging to a guild, silently (fft::begx): f1 the guild, f2 the rank, f3 the
    /// member's contribution, f4 not known. What world entry sends -- "jco jhe jhh ... jgu" in
    /// every capture of it --; the jgw is the one of joining, with its chat line and popup.
    /// </summary>
    public const string Jhe = "jhe";

    /// <summary>El gremio otra vez: fecha de fundacion, nivel y numero de miembros. Se descarta; mientras viajaba provocaba un NullReferenceException en el cliente. 18 mensajes.</summary>
    public const string Jhh = "jhh";

    /// <summary>El nombre del gremio, escrito. Se descarta; mientras viajaba provocaba un NullReferenceException en el cliente. 2 mensajes.</summary>
    public const string Jhk = "jhk";

    /// <summary>Un puesto de jugador en el mapa, con la cuenta detras; se descarta. 10 mensajes.</summary>
    public const string Jjs = "jjs";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Joa = "joa";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jog = "jog";

    /// <summary>Parte de la rafaga de inicializacion de 3.6.4.3 que dispara kkn. No aparece en ninguna de las 242 capturas.</summary>
    public const string Joh = "joh";

    /// <summary>Movimiento por el mapa fuera de combate: el camino que pide el cliente al andar.</summary>
    public const string Joi = "joi";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jol = "jol";

    /// <summary>
    /// La sonda periódica del servidor: va vacío, en pareja con <see cref="Lqt"/>, y el cliente
    /// contesta en el acto con lqc {f1 = cuántas lleva contestadas} y lqf {f2 = milisegundos}.
    /// </summary>
    /// <remarks>
    /// Se leyó como «una vez por combate, justo antes del kai» porque en «combate contra 4
    /// poutchs nivel 25» cayó ahí. Medido en las 24 capturas que traen dos o más: el intervalo
    /// entre pares es 240 segundos clavados (52→293, 118→358, 226→466→706, 50→290→530→770,
    /// 240→480→720→960...), el f1 del lqc sube de uno en uno con cada par y el f2 del lqf
    /// vale 38-40 en la mayoría y 100-190 en las capturas de conexión lenta. Cae donde caiga
    /// el reloj: en medio de un turno, tras un emote, en la colocación. No tiene nada que ver
    /// con el combate ni con la regeneración de vida, que son ktz y kuq.
    /// </remarks>
    public const string Lqg = "lqg";

    /// <summary>El compañero del <see cref="Lqg"/>: también vacío, siempre detrás, cada 240 segundos.</summary>
    public const string Lqt = "lqt";

    /// <summary>
    /// El VALOR de un modificador que apunta a un hechizo concreto:
    /// <c>f1 { f2: 1, f3: cuánto, f4: qué, f5: el hechizo }  f2: de quién</c>
    ///
    /// Es lo que hace que el cliente vuelva a calcular las casillas donde se puede lanzar. El jxm
    /// del embrujo sólo le sirve para pintar el panel de efectos. Medido en
    /// «ocra-disparos lejanos»: 272 de éstos, dos por cada hechizo afectado.
    /// </summary>
    public const string Hnd = "hnd";


    /// <summary>Cambio de mapa antiguo (3.6.4.3), sustituido por jqk. No aparece en ninguna captura.</summary>
    public const string Jos = "jos";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jpb = "jpb";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jpg = "jpg";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jpj = "jpj";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jps = "jps";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Jpv = "jpv";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jqb = "jqb";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Jqf = "jqf";

    /// <summary>El mapa al que quiere ir; la casilla y la orientacion de llegada se calculan del lado de salida (13 de casilla lateral, 532 en vertical), medido en las capturas.</summary>
    public const string Jqk = "jqk";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Jrk = "jrk";

    /// <summary>
    /// Entrar en una casa. Es el jru de las viviendas, pero NO es el jru: el mapa va en el campo
    /// 1, no en el 2. Medido de «entrar en mi casa»: jqw { f1: 213124106, f3: 35226522202 }. El f3
    /// no se ha sabido explicar —no es el mapa, ni el personaje, ni el elemento— y sólo hay una
    /// muestra en las nueve capturas de casas, así que no se manda.
    /// </summary>
    public const string Jqw = "jqw";

    /// <summary>Carga este mapa; enviarlo dos veces hace que el cliente recargue el mundo en bucle.</summary>
    public const string Jru = "jru";

    /// <summary>Caminar por un camino de casillas; cada paso empaqueta la orientacion en los bits altos de la casilla. El map id se comprueba contra la sesion y si no cuadra se ignora.</summary>
    public const string Jrw = "jrw";

    /// <summary>Quita un actor del mapa; su propio cambio de mapa cuenta.</summary>
    public const string Jsd = "jsd";

    /// <summary>El movimiento confirmado; saltarselo deja al actor con orientacion cero.</summary>
    public const string Jsj = "jsj";

    /// <summary>Este actor ha cambiado: el bloque de actor entero, con casilla, id y el look nuevo. Es de lo que redibuja el cliente en el mapa.</summary>
    public const string Jsn = "jsn";

    /// <summary>Adelante; no lleva mas que el id de peticion repetido, en el campo raiz 3. Sin el, el cliente nunca envia jqk y el personaje se queda en el borde.</summary>
    public const string Jsq = "jsq";

    /// <summary>Los actores del mapa; f6 (subarea) es obligatorio o el cliente revienta en MapInfoUI.SetInfoFromSubarea. El tipo de actor lo da el campo presente dentro de f2.f1: f5 jugador, f7 PNJ, f4 grupo de monstruos, con ids contextuales negativos para PNJs y grupos.</summary>
    public const string Jss = "jss";

    /// <summary>Catalogo de regalos de la cuenta; se envia vacio porque nuestras cuentas no tienen ninguno.</summary>
    public const string Jtg = "jtg";

    /// <summary>Combate. 1275 mensajes, 22 ficheros.</summary>
    public const string Jti = "jti";

    /// <summary>Lo envia el servidor y el cliente nunca; el emulador lo tiene en la rama de lista de personajes, a la que solo llega kpa. 7324 mensajes en 23 ficheros.</summary>
    public const string Jto = "jto";

    /// <summary>Lo envia el servidor pero el emulador lo usa como disparador de cliente. 9463 mensajes en 23 ficheros.</summary>
    public const string Jwe = "jwe";

    /// <summary>Combate. 431 mensajes, 19 ficheros.</summary>
    public const string Jwh = "jwh";

    /// <summary>Combate. 7324 mensajes, 23 ficheros.</summary>
    public const string Jwi = "jwi";

    /// <summary>
    /// Lanzar un hechizo APUNTANDO DESDE EL CARRUSEL: { f1: a quien, f2: el hechizo }.
    ///
    /// Es el gemelo del jwh, que apunta por casilla. En las 305 capturas hay 888 jwh y ni uno
    /// lleva identificador de combatiente -solo casilla-, y cuatro jwn, que llevan el id y no la
    /// casilla. Dos de esos cuatro apuntan al propio jugador: es como se lanza uno un embrujo
    /// sobre si mismo sin buscarse en el tablero.
    ///
    /// El id va con SIGNO: los monstruos lo tienen negativo, y llega en complemento a dos de
    /// sesenta y cuatro bits.
    /// </summary>
    public const string Jwn = "jwn";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jwq = "jwq";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jxb = "jxb";

    /// <summary>Combate. 739 mensajes, 23 ficheros.</summary>
    public const string Jxc = "jxc";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Jxg = "jxg";

    /// <summary>Combate. 514 mensajes, 23 ficheros.</summary>
    public const string Jxh = "jxh";

    /// <summary>Combate. 6556 mensajes, 23 ficheros.</summary>
    public const string Jxm = "jxm";

    /// <summary>
    /// Las estadisticas de fin de combate de UN jugador -danos infligidos por fuente, recibidos,
    /// curas, escudos, enemigos derrotados y las medias por turno y por PA-, detras del jyg y
    /// antes del jxo... del siguiente: "kuf jyg jxo" en cada final de combate de las capturas.
    /// Un mapa por personaje (f1) y los totales (f2). Medido campo a campo en 30 combates.
    /// </summary>
    public const string Jxo = "jxo";

    /// <summary>Lo envia el servidor pero el emulador lo usa como disparador de cliente. 4933 mensajes en 23 ficheros.</summary>
    public const string Jxw = "jxw";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Jxz = "jxz";

    /// <summary>Solo alcanzable desde el despacho de combate muerto. 3962 mensajes en 20 ficheros.</summary>
    public const string Jya = "jya";

    /// <summary>Solo alcanzable desde el despacho de combate muerto. 36 mensajes en 22 ficheros.</summary>
    public const string Jyg = "jyg";

    /// <summary>Solo alcanzable desde el despacho de combate muerto. 178 mensajes en 21 ficheros.</summary>
    public const string Jyj = "jyj";

    /// <summary>Combate. 503 mensajes, 20 ficheros.</summary>
    public const string Jyt = "jyt";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jyy = "jyy";

    /// <summary>Combate. 538 mensajes, 23 ficheros.</summary>
    public const string Jzc = "jzc";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Jzu = "jzu";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Jzy = "jzy";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kaa = "kaa";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kae = "kae";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kah = "kah";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kai = "kai";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kam = "kam";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Kaq = "kaq";

    /// <summary>
    /// A fight option's state: { f1: the side, f3: which, f4: on, f5: the fight }. Four at every
    /// board, in the order 2, 1, 3, 0, and one whenever a side switches one (jzx).
    /// </summary>
    public const string Kau = "kau";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kba = "kba";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kbd = "kbd";

    /// <summary>
    /// Server to client, a marketplace: one item's offers, { f1: item type, f2: item, f3 (repeated)
    /// { f1: offer, f4: effects, f5: item, f6: packed price of each lot size, 0 where none,
    /// f8: item type } }. The answer to keh; without f3 when nothing is on sale, and bare, f1 and
    /// f2 alone, to the first keh that stops following the item.
    /// </summary>
    public const string Kbt = "kbt";

    /// <summary>
    /// Client to server, a marketplace: buy a lot, { f1: offer, f2: the price it was shown, f3:
    /// the lot size }. Seven in the runes capture, one per lot size of 1, 10, 100 and 1000.
    /// </summary>
    public const string Kbm = "kbm";

    /// <summary>
    /// Server to client: the marketplace opens to sell, { f3: the same settings as kdw's f1 }.
    /// Its f1 is the seller's own listings, a repeated { f2: item as kes's f1, f3: price, f4: the
    /// time left } by the client's own class; the capture's seller had none, so that part is
    /// read off the client and not measured.
    /// </summary>
    public const string Kby = "kby";

    /// <summary>Client to server, a marketplace in sell mode: the prices of an item, { f1: item }. Answered by kcq.</summary>
    public const string Kbz = "kbz";

    /// <summary>
    /// Server to client, a marketplace: the prices of an item for whoever sells it, { f3: item,
    /// f4: average price, f5 { f1: packed lowest price of each lot size } }.
    /// </summary>
    public const string Kcq = "kcq";

    /// <summary>El cofre se abre; los dos valores son constantes en la captura y el 100 parece el numero de huecos.</summary>
    public const string Kci = "kci";

    /// <summary>Mover un objeto del cofre; la direccion no viaja, se deduce de donde esta el objeto. f1 llega como -1 cuando se arrastra la pila entera.</summary>
    public const string Kcr = "kcr";

    /// <summary>
    /// Server to client, a marketplace: a purchase went through, { f2: offer, f4: true }. The last
    /// of what a kbm is answered with, after the lqn 252 that says what was bought.
    /// </summary>
    public const string Kcx = "kcx";

    /// <summary>
    /// Server to client, a marketplace: the items of one type on sale, { f1: packed items, f2:
    /// type }; f2 alone when there are none. The answer to kdk.
    /// </summary>
    public const string Kda = "kda";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kdg = "kdg";

    /// <summary>
    /// Client to server, a marketplace: follow an item type, { f2: true, f4: type }, answered by
    /// kda; stop following it, f4 alone, answered by nothing.
    /// </summary>
    public const string Kdk = "kdk";

    /// <summary>
    /// Server to client: a marketplace opens to buy, { f1 { f1: -1, f2: 200, f3: 708, f4: the
    /// marketplace, f5: float tax percentage, f6: hours on sale, f7: float, f9: packed item
    /// types, f10: packed lot sizes } }. f4 is the id of the client's AuctionHousesDataRoot.
    /// </summary>
    public const string Kdw = "kdw";

    /// <summary>
    /// How many times to craft the recipe in the workshop: { f1: count }, and the server says it
    /// back in kgl. Nine in a row in the grinder's capture, 2 to 10. The mappings file calls it
    /// AccountCapabilitiesMessage, and that is false.
    /// </summary>
    public const string Kdx = "kdx";

    /// <summary>
    /// A workshop opens: { f1: the skill }. It comes after iwn, the inventory and an empty hlm;
    /// the same message opens a craft station and a magus table.
    /// </summary>
    public const string Kgq = "kgq";

    /// <summary>
    /// Always empty around a workshop: after the inventory when it opens, when it closes, and in
    /// answer to itr.
    /// </summary>
    public const string Hlm = "hlm";

    /// <summary>The client picks a recipe in the workshop's list: { f2: the result }.</summary>
    public const string Kew = "kew";

    /// <summary>
    /// Ready, in an exchange or a workshop: { f1: true, f2: step }. In a workshop it is the craft
    /// button. The step counts the moves so far (25 in the tutorial, 1 to 5 in the grinder); it
    /// is not a quantity -- that one is kdx.
    /// </summary>
    public const string Kep = "kep";

    /// <summary>
    /// How many times the recipe is to be crafted, the server saying back what kdx asked:
    /// { f1: count }. After a craft of several it goes back to { f1: 1 }.
    /// </summary>
    public const string Kgl = "kgl";

    /// <summary>
    /// Something enters the workshop: { f1 { f1: 63, f5: item with its quantity in it }, f3: 0.0 }.
    /// The float goes written even at zero.
    /// </summary>
    public const string Kfb = "kfb";

    /// <summary>Something leaves the workshop: { f1: uid }.</summary>
    public const string Kfs = "kfs";

    /// <summary>Something in the workshop changes: { f2 { f1: 63, f5: item } }.</summary>
    public const string Kex = "kex";

    /// <summary>
    /// The result of a craft or of a rune: { f2 { f1: pool change, f3: pool, f4: item }, f3: 1 on
    /// a failure, 2 on a success }, and empty when the ingredients make no recipe.
    /// </summary>
    public const string Kdr = "kdr";

    /// <summary>A rune applied on the magus table: { f1: rune uid, f3: 1, f6: true }.</summary>
    public const string Kcj = "kcj";

    /// <summary>Closes every rune: { f2: true }.</summary>
    public const string Kdb = "kdb";

    // ─── A commission: a magus working on someone else's item ───────────────────────────
    // Measured from the magus' side in two sessions (Oficios/"envio invitacion a maguear ..."),
    // and from the customer's up to the moment they accept ("recibo invitacion ...").

    /// <summary>
    /// An invitation to a commission: { f1: the other character, f2: skill, f3: 10 when the one
    /// who sends it is the magus, 11 when it is the customer }.
    /// </summary>
    public const string Kbl = "kbl";

    /// <summary>
    /// A commission waiting for an answer: { f1: the other character, f2: 10 magus / 11 customer,
    /// the role of whoever receives it, f3: who sent the invitation }. Followed by an empty hlm.
    /// </summary>
    public const string Kgu = "kgu";

    /// <summary>The invitation cannot be: { f1: 3 } when the magus is too far from the workshop.</summary>
    public const string Kdv = "kdv";

    /// <summary>Accepting what was offered: a commission, a trade. Empty.</summary>
    public const string Kgi = "kgi";

    /// <summary>The magus' window opens: { f2: skill }.</summary>
    public const string Keg = "keg";

    /// <summary>The customer's window opens: { f1: the magus' job level, f2: skill }.</summary>
    public const string Kgw = "kgw";

    /// <summary>
    /// Somebody else's job experience: { f1 { job, next, level, floor, experience }, f2: whose }.
    /// The customer gets the magus' on accepting.
    /// </summary>
    public const string Iss = "iss";

    /// <summary>
    /// The customer's offer gains an item: { f3 { f1: 63, f5: item }, f4: true when the other one
    /// did it }.
    /// </summary>
    public const string Ked = "ked";

    /// <summary>The customer's offer loses an item: { f1: uid }. It went onto the table.</summary>
    public const string Keo = "keo";

    /// <summary>The magus moves an offered item onto the table or back: { f1: ±1, f4: uid }.</summary>
    public const string Kgd = "kgd";

    /// <summary>Somebody is ready, or no longer: { f3: true, f4: who }; without f3 it is "no".</summary>
    public const string Kgt = "kgt";

    /// <summary>What the customer pays: { f1: kamas }; empty once it has been paid.</summary>
    public const string Kcl = "kcl";

    /// <summary>Kamas put into an exchange: { f1: kamas }.</summary>
    public const string Kee = "kee";

    /// <summary>Client to server: asking another player to trade, { f2: whom }.</summary>
    public const string Keu = "keu";

    /// <summary>A trade asked for, to both: { f1: who asks, f2: who is asked, f4: 1 }.</summary>
    public const string Kfz = "kfz";

    /// <summary>
    /// The trade window opens, to both: { f2: asker's pods capacity, f3: 1, f4: asked's capacity,
    /// f5: asker's pods carried, f6: asker, f7: asked, f8: asked's carried }.
    /// </summary>
    public const string Kbg = "kbg";

    /// <summary>The kamas one side of a trade puts in: { f1: kamas, f3: true when the other one's }.</summary>
    public const string Ket = "ket";

    /// <summary>A side's pods once a trade is done: { f1: capacity, f2: true when the other one's, f3: carried }.</summary>
    public const string Keq = "keq";

    /// <summary>
    /// A line of the chat log: { f3: kind, f4: parameters }. The payment of a commission writes
    /// { f3: 64, f4: "+", f4: amount } beside lqn 594, "Pago: {0} kamas.".
    /// </summary>
    public const string Lqs = "lqs";

    // ─── The grinder: breaking items into runes ─────────────────────────────────────────

    /// <summary>The grinder's breaking window opens. Empty.</summary>
    public const string Kbv = "kbv";

    /// <summary>Break what is on the grinder: { f2: true, f3: step }.</summary>
    public const string Kbj = "kbj";

    /// <summary>
    /// What came out of each broken item: { f1 (repeated) { f1: uid, f3 (repeated) { f1: rune,
    /// f2: how many }, f4: coefficient, f5: coefficient } }.
    /// </summary>
    public const string Kfp = "kfp";

    // ─── The artisans' directory ────────────────────────────────────────────────────────
    // Four captures (Oficios/"abrir interfaz oficios-constar en la lista publica ...",
    // "... estar visible en lista artesanos ...", "consultar lista de artesanos en el
    // interactivo del libro", "dejar de constar en lista artesanos ...").

    /// <summary>
    /// A job's settings as a crafter: { f1 { f3: job, f4: free, f5: minimum level } }. The client
    /// sends one per job when the jobs window opens, and one each time a setting changes.
    /// </summary>
    public const string Irl = "irl";

    /// <summary>
    /// The settings of every job: { f1 (repeated) { f3: job, f4: free, f5: minimum level } }. The
    /// answer to every irl, and part of the entry into the world.
    /// </summary>
    public const string Isd = "isd";

    /// <summary>Show me in the public list, or stop: { f2: [jobs], packed }. A toggle.</summary>
    public const string Kef = "kef";

    /// <summary>Whether a job is in the public list: { f1 { f1: job, f2: listed } }.</summary>
    public const string Iro = "iro";

    /// <summary>The artisans' book opens its window: { f2: [the workshop's jobs], packed }.</summary>
    public const string Kfj = "kfj";

    /// <summary>The list of one job's artisans: { f2: job }.</summary>
    public const string Isr = "isr";

    /// <summary>
    /// One more artisan in the list, or one who changed: { f1: entry }, the entry as isf's.
    /// </summary>
    public const string Isv = "isv";

    /// <summary>An artisan gone from a job's list: { f1: who, f2: job }.</summary>
    public const string Isq = "isq";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kea = "kea";

    /// <summary>
    /// Client to server, a marketplace: follow an item, { f1: item, f2: true }, answered by kbt;
    /// stop following it, f1 alone. The client sends that one twice in a row.
    /// </summary>
    public const string Keh = "keh";

    /// <summary>
    /// Server to client, a marketplace: an offer of a followed item changed, { f2: packed price
    /// of each lot size, f3: offer, f4: effects, f5: item, f6: item type }. To the buyer after
    /// every lot bought from an offer that still has more, and to the seller behind kfi.
    /// </summary>
    public const string Kgp = "kgp";

    /// <summary>
    /// Server to client, a marketplace: an offer of a followed item is gone, { f1: item, f2: item
    /// type, f4: offer }. What the buyer gets instead of kgp when the lot bought was its last.
    /// </summary>
    public const string Kgv = "kgv";

    /// <summary>
    /// Server to client, a marketplace: an offer of a followed item appeared, { f1: effects, f2:
    /// item type, f3: item, f4: packed price of each lot size, f5: offer }, then kgp for it.
    /// </summary>
    public const string Kfi = "kfi";

    /// <summary>
    /// Client to server, a marketplace in sell mode: put a lot on sale, { f1: price, f2: the
    /// stack's uid, f3: the lot size }.
    /// </summary>
    public const string Kge = "kge";

    /// <summary>
    /// Server to client, a marketplace: the seller's lot is on sale, { f1 { f1: the listing, f2:
    /// effects, f3: item, f4: how many }, f2: price, f4: seconds on sale, 2,419,200 }.
    /// </summary>
    public const string Kes = "kes";

    /// <summary>
    /// Client to server, a marketplace in sell mode: a new price for a lot on sale, { f1: the
    /// listing, f2: the new price, f3: how many }. Read off the client: its sell window's
    /// OnConfirmModifyObject is what raises it. No capture.
    /// </summary>
    public const string Kch = "kch";

    /// <summary>
    /// Server to client, a marketplace: one of the seller's lots is off sale, { f1: the listing }.
    /// Read off the client, no capture.
    /// </summary>
    public const string Ken = "ken";

    /// <summary>
    /// Server to client: the seller's sales history, { f3 (repeated): laq { f1: kamas, f3: date,
    /// f4: 0 sold / 1 unsold, f5: the item, f6: the marketplace } }. Read off the client.
    /// </summary>
    public const string Las = "las";

    /// <summary>El cofre se cerro.</summary>
    public const string Khd = "khd";

    /// <summary>Parte de la rafaga de inicializacion de 3.6.4.3 que dispara kkn. No aparece en ninguna de las 242 capturas.</summary>
    public const string Kkm = "kkm";

    /// <summary>Peticion de carga de mapa antigua (3.6.4.3). No aparece en ninguna captura.</summary>
    public const string Kkr = "kkr";

    /// <summary>
    /// Abre un documento: un libro, un cartel, una placa.
    /// </summary>
    /// <remarks>
    /// Solo lleva el id del documento, en el campo 2. Ni titulo ni texto viajan: el cliente los
    /// tiene en su propia tabla. Medido en «abrir libro el escudo de feca y leerlo», donde el
    /// cliente manda iuu y el servidor contesta kkt { f2: 217 }, que es el documento de ese libro.
    /// </remarks>
    public const string Kkt = "kkt";

    /// <summary>Solo alcanzable desde isi, que nunca llega. 2 mensajes en 2 ficheros.</summary>
    public const string Kku = "kku";


    /// <summary>La X de cerrar el dialogo. Va vacio; se contesta con kld.</summary>
    /// <remarks>
    /// Sale 192 veces en las 401 capturas y no estaba declarado, asi que la X no cerraba nada: el
    /// servidor lo veia como paquete desconocido y no contestaba. Con los NPCs que no tienen
    /// respuestas —el Bontariano enfadado, la Brakmariana enfadada— eso dejaba la ventana puesta
    /// sin manera de salir mas que reconectando.
    /// </remarks>
    public const string Kla = "kla";

    /// <summary>Cierra el dialogo; el cliente no cierra la ventana del zaap por si mismo. f1 es un motivo fijo, no algo que calcular.</summary>
    public const string Kld = "kld";

    /// <summary>Abandonar el combate. Cuerpo vacio; el servidor contesta con una secuencia de muerte de tipo 5 y un jxh.</summary>
    public const string Kme = "kme";

    /// <summary>Parte de la rafaga final de 3.6.4.3 que dispara ibt (icg se envia tres veces). No aparece en ninguna de las 242 capturas.</summary>
    public const string Klp = "klp";

    /// <summary>Combate. 264 mensajes, 22 ficheros.</summary>
    public const string Kmk = "kmk";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kml = "kml";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Kmp = "kmp";

    /// <summary>Presente en 25 de las 31 carpetas de captura (399 mensajes, 90 ficheros). Nada establecido.</summary>
    public const string Kmu = "kmu";

    // ─── A fight on the map, and coming into one (Network/FightJoinProtocol.cs) ──────────

    /// <summary>Server to client: the swords of a fight in its placement appear on the map.</summary>
    public const string Hpy = "hpy";

    /// <summary>Server to client: the swords go, the placement is over. { f1: the fight }.</summary>
    public const string Hpr = "hpr";

    /// <summary>Server to client: how many fights the map has. { f2: how many }, empty for none.</summary>
    public const string Jqz = "jqz";

    /// <summary>Server to client: a member taken off a team shown on the map.</summary>
    public const string Jzw = "jzw";

    /// <summary>Server to client: a fighter taken off the board during the placement.</summary>
    public const string Kar = "kar";

    /// <summary>Server to client: a kay turned down, { f1: the fighter named, f2: why }.</summary>
    public const string Jxs = "jxs";

    /// <summary>
    /// Client to server: a side's fight option switched, { f1: which } -- none for no spectators,
    /// 1 party only, 2 closed, 3 asking for help. Answered by kau with its new state.
    /// </summary>
    public const string Jzx = "jzx";

    /// <summary>Server to client, empty: out of the fight, sent to whoever leaves the placement.</summary>
    public const string Jxa = "jxa";

    /// <summary>Client to server, empty: the party window's automatic entry into fights, on.</summary>
    public const string Ilf = "ilf";

    /// <summary>Server to client, root 3, empty: the answer to ilf.</summary>
    public const string Ikm = "ikm";

    /// <summary>Client to server, empty: the automatic entry, off.</summary>
    public const string Int = "int";

    /// <summary>Server to client, root 3, empty: the answer to int.</summary>
    public const string Ilv = "ilv";

    /// <summary>Client to server, empty: the party window's automatic ready, on.</summary>
    public const string Ikr = "ikr";

    /// <summary>Server to client, root 3, empty: the answer to ikr.</summary>
    public const string Inn = "inn";

    /// <summary>Client to server, empty: the automatic ready, off.</summary>
    public const string Inp = "inp";

    /// <summary>Server to client, root 3, empty: the answer to inp.</summary>
    public const string Ilr = "ilr";

    /// <summary>Llega con jrh en cada carga de mapa y no espera nada de vuelta; el emulador ya lo ignora en silencio. 727 mensajes, 88 ficheros.</summary>
    public const string Kmv = "kmv";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kmw = "kmw";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Knc = "knc";

    /// <summary>La respuesta al ping kod de 3.6.4.3. No aparece en ninguna de las 242 capturas.</summary>
    public const string Kns = "kns";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Knv = "knv";

    /// <summary>El ping de 3.6.4.3, respondido con kns. Sustituido por kqo/kqy. No aparece en ninguna captura.</summary>
    public const string Kod = "kod";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kof = "kof";

    /// <summary>Veinte cuentas Ankama con id, apodo y tag; se reconoce y se descarta por privacidad. El builder no se llama nunca. El fichero de mapeos lo llama HavenBagStatusMessage, y eso es falso. 28 mensajes en 7 ficheros.</summary>
    public const string Koj = "koj";

    /// <summary>Peticion de la lista de personajes; se despacha, pero solo se ve una vez en 242 capturas.</summary>
    public const string Kpa = "kpa";

    /// <summary>La lista de contactos de la cuenta grabada; se reconoce por opcode y se descarta. 28 mensajes.</summary>
    public const string Kqg = "kqg";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kqk = "kqk";

    /// <summary>La lista que envia la rama hmv de 3.6.4.3, junto con hnk. No aparece en ninguna de las 242 capturas.</summary>
    public const string Kqm = "kqm";

    /// <summary>El latido, cada cinco segundos mientras el cliente esta en el mundo; el mensaje de cliente mas frecuente, en 235 de los 242 ficheros. El fichero de mapeos lo llama ChatChannelsReadMessage, y eso es falso.</summary>
    public const string Kqo = "kqo";

    /// <summary>Se envia tres veces seguidas con tres cargas distintas; significado no establecido.</summary>
    public const string Kqp = "kqp";

    /// <summary>Respuesta a kqq; despues el cliente cierra la conexion el mismo y rehace el handshake.</summary>
    public const string Kqr = "kqr";

    /// <summary>Funcionalidades habilitadas en el servidor, como ids opacos copiados de la captura. NO es una peticion de lista de personajes.</summary>
    public const string Kqu = "kqu";

    /// <summary>La respuesta al latido, y nada mas; viaja en el campo raiz 1, no en el de respuesta.</summary>
    public const string Kqy = "kqy";

    /// <summary>Presenta el ticket de un solo uso; vincula la sesion a una cuenta y a un servidor, y un ticket desconocido cierra la conexion.</summary>
    public const string Kqz = "kqz";

    /// <summary>Abre la rafaga de bienvenida.</summary>
    public const string Kra = "kra";

    /// <summary>Subida de caracteristicas antigua (3.6.4.3), sustituida por kum. No aparece en ninguna captura.</summary>
    public const string Krc = "krc";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Krh = "krh";

    /// <summary>Parte de la rafaga de inicializacion de 3.6.4.3 que dispara kkn. No aparece en ninguna de las 242 capturas.</summary>
    public const string Kri = "kri";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Krs = "krs";

    /// <summary>Sin identificar. 4 usos en el emulador.</summary>
    public const string Ksl = "ksl";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ksv = "ksv";

    /// <summary>Sin identificar. 4 usos en el emulador.</summary>
    public const string Ksx = "ksx";

    /// <summary>La linea de vuelta. Canales: 0 general (omitido por ser cero), 1 equipo, 2 gremio, 3 alianza, 4 grupo, 5 comercio, 6 reclutamiento, y 9, 11, 16, 18, 19 para el resto.</summary>
    /// <summary>
    /// El personaje sube de nivel: { f1: el nivel nuevo }. Dos bytes, y es TODO lo que hace falta
    /// para que el cliente saque su ventana con musica y animacion. Ni los puntos, ni la vida, ni
    /// los hechizos van aqui: eso llega en el kub de detras. El cliente no contesta nada al
    /// cerrarla.
    /// </summary>
    public const string Kua = "kua";

    public const string Kti = "kti";

    /// <summary>Una linea que escribio el jugador.</summary>
    public const string Ktm = "ktm";

    /// <summary>
    /// Susurrar a alguien: { f1: el texto, f5: a quien }. No es un canal del chat normal -eso es
    /// el ktm- sino su propio mensaje. El canal privado es el 9 segun la tabla del cliente.
    /// </summary>
    public const string Ktb = "ktb";

    /// <summary>
    /// Un mensaje privado: { f1: fecha, f5: id del otro, f6: nombre del otro, f7: texto }.
    /// El volcado de nombres reales lo llama ChatPrivateCopyMessageEvent. No lleva canal: el canal
    /// privado se deduce del propio mensaje. Y no lleva quien lo manda sino EL OTRO, o sea el
    /// destinatario en tu copia.
    /// </summary>
    public const string Kth = "kth";

    /// <summary>
    /// El chat no ha podido con algo (ChatErrorEvent): { f1: el motivo }. El 2 es el que contesta
    /// el servidor real al susurrarse a uno mismo; los demas valores vistos no se han atado a su
    /// causa.
    /// </summary>
    public const string Ktl = "ktl";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ktw = "ktw";

    /// <summary>La hoja de personaje; el campo contenedor no es el mismo para cada caracteristica y equivocarlo mata la hoja entera con NullReferenceException. Se envia dos veces (con el personaje y con el mapa) y el cliente se queda con la segunda.</summary>
    public const string Kub = "kub";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kuf = "kuf";

    /// <summary>
    /// Se acabó la regeneración de vida: f1 la vida que tiene, f2 los medios segundos que llevaba
    /// regenerando, f4 la vida máxima. Va al entrar en combate, entre el lqu y el lva de la carga
    /// del mapa táctico, y es lo que para el contador del cliente: sin él la barra del combate
    /// sigue subiendo de uno en uno como en el mapa.
    /// </summary>
    /// <remarks>
    /// 97 mensajes en 69 ficheros, 88 detrás de «kub jru lqu» y 83 delante de «lva kmk». En
    /// el desafío del 9 de agosto, {f1=5211 f2=151 f4=5307} con el ino de al lado diciendo vida
    /// 5211 de 5307: f1 y f4 son la vida y el tope. El f2 no es vida ganada —la vida no sube 151—:
    /// entre el ktz del final del combate anterior (23:23:34) y este kuq (23:24:50) pasan 76
    /// segundos, 152 tics de medio segundo, y f2 vale 151. Es lo que llevaba el contador, con el
    /// ritmo 5 del ktz (5 décimas por punto). Inferencia con dos muestras de tiempo; la forma
    /// es medida.
    /// </remarks>
    public const string Kuq = "kuq";

    /// <summary>
    /// Empieza la regeneración de vida: f1 es el ritmo, en décimas de segundo por punto de vida.
    /// Va justo detrás de cada «kml kmp» de vuelta al rol: al entrar al mundo y al salir de un
    /// combate.
    /// </summary>
    /// <remarks>
    /// 143 mensajes en 105 ficheros, todos detrás de «kml kmp» y 135 con f1=5. Los ocho con
    /// f1=1 son la feria del Trool y una entrada al mundo con raid de gremio: un ritmo rápido
    /// que no se sabe de qué depende y no se manda. El 5 cuadra con el kuq: 151 tics en 76
    /// segundos.
    /// </remarks>
    public const string Ktz = "ktz";

    /// <summary>Gastar puntos de caracteristica; el valor es el total pagado, no un incremento, lo que hace el mensaje idempotente, y un total que no cabe se rechaza entero. Campos: 1 inteligencia, 2 suerte, 3 vitalidad, 4 sabiduria, 5 agilidad, 6 fuerza. Lleva id de peticion real (7 peticiones).</summary>
    public const string Kum = "kum";

    /// <summary>Ya estas jugando con este personaje; sin el, el cliente se queda en la pantalla de personaje con el reloj de arena.</summary>
    public const string Kva = "kva";

    /// <summary>Resultado de la creacion; vacio si va bien, f2 lleva el motivo del rechazo.</summary>
    public const string Kvb = "kvb";

    /// <summary>No te pares en la pantalla de personajes. Solo sale al entrar directo al mundo: reconexion a combate y koliseo. NO va en la rafaga normal.</summary>
    public const string Kvd = "kvd";

    /// <summary>
    /// El cliente pide la lista de personajes. Va con un krv detras (una clave larga) en cuanto
    /// termina la rafaga de bienvenida. En una entrada corriente la lista ya viajo en la rafaga
    /// y no se contesta nada; en una reconexion a combate la rafaga va SIN lista y la respuesta
    /// es kvi y kvd, medido en las dos capturas de reconexion.
    /// </summary>
    public const string Kvc = "kvc";

    /// <summary>Los personajes de la cuenta en el servidor elegido.</summary>
    public const string Kvi = "kvi";

    /// <summary>Un nombre de personaje sugerido (el boton del dado).</summary>
    public const string Kvk = "kvk";

    /// <summary>El mismo paso justo despues de una creacion con exito: el cliente envia kvl detras de kvi y entra al mundo sin pasar por la lista.</summary>
    public const string Kvl = "kvl";

    /// <summary>Seleccionar un personaje; el id se comprueba contra la cuenta de la sesion porque lo elige el cliente y no es de fiar.</summary>
    public const string Kvw = "kvw";

    /// <summary>Crear un personaje.</summary>
    public const string Kvz = "kvz";

    // ─── Borrar un personaje ────────────────────────────────────────────────────────────────
    //
    // Measured in "crear personaje - borrar personaje.pcapng", which records one deletion from
    // end to end. The client sends three messages and the server answers with five frames:
    //
    //   C->S  kwa  10de82c08e8a02                       f2: the character id
    //   C->S  kvu  08<id> 1220 "9cdab917cc3d5a4a33..."  f1: the id, f2: 32 hex characters
    //   C->S  kvh  (empty)
    //
    //   S->C  kvn  0a06 "Vos-Xx"                        f1: the name of what is gone
    //   S->C  kqp kqp kqp  kvi  jtg                     the list again, framed as always
    //   S->C  kvm  (empty)
    //
    // The 32 hex characters in kvu are what the player typed to confirm, hashed. This server has
    // no secret to check it against, so what protects a character here is that the id must belong
    // to the session's account -- see DatabaseManager.DeleteCharacter.

    /// <summary>El cliente senala el personaje que va a borrar. Lleva el id en el campo 2.</summary>
    public const string Kwa = "kwa";

    /// <summary>
    /// «Adelante»: la respuesta del cliente al <see cref="Kvd"/>, vacia. No dice que personaje
    /// porque no hace falta: el servidor sabe cual esta en combate. Medido en las tres capturas
    /// de entrada directa (dos reconexiones a combate y el koliseo): kvd, kwb, y detras el kva
    /// y la entrada al mundo de siempre.
    /// </summary>
    public const string Kwb = "kwb";

    /// <summary>Borrar un personaje: campo 1 el id, campo 2 la confirmacion escrita en 32 hex.</summary>
    public const string Kvu = "kvu";

    /// <summary>Cierra la peticion de borrado. Viaja vacio.</summary>
    public const string Kvh = "kvh";

    /// <summary>Respuesta al borrado: el nombre de lo que ya no esta.</summary>
    public const string Kvn = "kvn";

    /// <summary>Cierra el borrado, detras de la lista nueva. Viaja vacio.</summary>
    public const string Kvm = "kvm";

    // ─── Los retos del combate ──────────────────────────────────────────────────────────────
    //
    // Quince opcodes seguidos, kwh..kxb, todos de la fase de preparacion salvo kwm y kwl, que
    // son de dentro del combate. Estan medidos sobre 305 capturas con la linea de tiempo de los
    // DOS sentidos junta: pcap.streams separa por conexion, y sin volver a mezclarlos por hora
    // el orden real se pierde y no se ve quien contesta a quien.
    //
    // El baile completo:
    //
    //   kxa   S->C  cuantos hay que elegir; llega DOS VECES, antes del kaa y despues del kba
    //   kwo   C->S  ajuste del panel      kwn  S->C  su confirmacion, con el mismo valor
    //   kwr   C->S  abrir el selector (vacio)
    //   kwx   S->C  LA LISTA: { f1: 15, f2 repetido: ldd }, SIEMPRE dos candidatos
    //   kwv   C->S  seleccionar uno. El PRIMERO llega solo, 2-30 ms detras del kwx: es la
    //               preseleccion automatica del cliente, NO un clic del jugador
    //   kwi   C->S  pasar el raton por un candidato. Sin respuesta, y el cliente lo sigue
    //               mandando durante el combate, cuando ya no se puede elegir nada
    //   kwj   C->S  validar   ->   kww  S->C  el reto queda FIJADO
    //   kaq/kah     el listo; ahi el servidor manda los kww que falten y rellena huecos
    //   kai         se acabo la colocacion
    //   kwu   S->C  la lista definitiva, pegada al jyy que arranca el combate
    //
    // El mensaje-reto que va dentro de casi todos ellos es el ldd.

    /// <summary>
    /// El reto propuesto al RESTO DEL GRUPO: { f1: ldd }. Solo sale en el unico combate de
    /// grupo con retos que hay en las capturas, 45-57 ms detras del kwv de un miembro.
    /// </summary>
    public const string Kwh = "kwh";

    /// <summary>Pasar el raton por un candidato: { f1: id }. C->S y SIN respuesta.</summary>
    public const string Kwi = "kwi";

    /// <summary>Validar el reto elegido: { f1: id }. C->S; el servidor contesta con kww.</summary>
    public const string Kwj = "kwj";

    /// <summary>Ajuste del panel de retos. Siempre ha viajado vacio.</summary>
    public const string Kwk = "kwk";

    /// <summary>
    /// El RESULTADO de un reto: { f1: id, f2: cumplido }. Sin f2, esta fallado. El fallo se
    /// avisa en cuanto ocurre; el exito, al final, a menos de once tramas del jyg.
    /// </summary>
    public const string Kwl = "kwl";

    /// <summary>
    /// El OBJETIVO de un reto: { f1: ?, f2: ldd con su lda dentro }. Pegado al jyy, y solo en
    /// los retos que apuntan a un monstruo concreto.
    /// </summary>
    public const string Kwm = "kwm";

    /// <summary>Confirmacion del ajuste del panel, con el mismo valor que el kwo.</summary>
    public const string Kwn = "kwn";

    /// <summary>Ajuste del panel de retos: { f1: bool }. C->S; el servidor contesta kwn.</summary>
    public const string Kwo = "kwo";

    /// <summary>Abrir el selector de retos. Va VACIO; el servidor contesta con la lista.</summary>
    public const string Kwr = "kwr";

    /// <summary>
    /// La lista DEFINITIVA de retos: { f2 repetido: ldd }. Va pegada al jyy. Su f1 (enum kws)
    /// no ha viajado nunca.
    /// </summary>
    public const string Kwu = "kwu";

    /// <summary>Seleccionar un candidato: { f1: id }. C->S y sin respuesta.</summary>
    public const string Kwv = "kwv";

    /// <summary>Un reto queda FIJADO: { f1: ldd }. Respuesta al kwj, o al listo.</summary>
    public const string Kww = "kww";

    /// <summary>
    /// La lista de retos OFRECIDOS: { f1: 15, f2 repetido: ldd }. El f1 vale quince en las
    /// nueve apariciones y es el temporizador de la propuesta; el cliente tiene un
    /// OnChallengeProposalUpdateTimer. Siempre DOS candidatos, ni uno ni tres, y son
    /// alternativas entre si: no tienen por que ser compatibles.
    /// </summary>
    public const string Kwx = "kwx";

    /// <summary>
    /// CUANTOS retos hay que elegir: { f1: n }. Uno en combate normal, dos en mazmorra,
    /// anomalia y submarino. Su f2 (enum kwy) no ha viajado nunca.
    /// </summary>
    public const string Kxa = "kxa";

    /// <summary>Ajuste del panel de retos: { f1: int64, f2: ... }. C->S, sin respuesta.</summary>
    public const string Kxb = "kxb";

    /// <summary>
    /// Client to server, empty: the sales history window was opened, and wants its lines (las).
    /// Read off the client: the one request that window's OnEnable sends, and the marketplace
    /// sender's empty message. No capture.
    /// </summary>
    public const string Lar = "lar";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lcj = "lcj";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ley = "ley";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lfj = "lfj";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lfo = "lfo";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lfx = "lfx";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lgz = "lgz";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lhi = "lhi";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lif = "lif";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lkr = "lkr";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lkt = "lkt";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lnk = "lnk";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Lol = "lol";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Lou = "lou";

    /// <summary>Rama de 3.6.4.3: envia tramas lok y jdj hardcodeadas. No aparece en ninguna de las 242 capturas.</summary>
    public const string Loy = "loy";

    /// <summary>Lo que envia la rama lpj de 3.6.4.3. No aparece en ninguna de las 242 capturas.</summary>
    public const string Lpe = "lpe";

    /// <summary>Rama de 3.6.4.3: envia lpe. No aparece en ninguna de las 242 capturas.</summary>
    public const string Lpj = "lpj";

    /// <summary>
    /// La respuesta del cliente a la sonda <see cref="Lqg"/>+<see cref="Lqt"/>: f1 es cuántas
    /// sondas lleva contestadas en la sesión (1, 2, 3... una por par, cada 240 segundos). En la
    /// entrada al mundo el primer par cae en el bloque 1, y por eso el servidor real parecía
    /// esperar «el bloque 1 digerido» antes de mandar el 2: espera esta contestación.
    /// </summary>
    public const string Lqc = "lqc";

    /// <summary>Va entre lqu y hjk en cada cambio de mapa capturado; su unico campo vale 197 al entrar al mundo, 24 al cambiar de mapa y 470 tras un reinicio de caracteristicas, y no hay lectura que aguante. Deliberadamente no se envia. 213 mensajes, 53 ficheros.</summary>
    public const string Lqn = "lqn";

    /// <summary>Sincronizacion de reloj; se envia tambien en cada cambio de mapa.</summary>
    public const string Lqu = "lqu";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Lry = "lry";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Lsy = "lsy";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ltk = "ltk";

    /// <summary>Sin identificar. 4 usos en el emulador.</summary>
    public const string Luq = "luq";

    /// <summary>
    /// Apuntarse a una modalidad del koliseo. El campo 2 es su indice en el ltd.
    /// </summary>
    /// <remarks>
    /// El fichero de mapeos lo llama JobDescriptionMessage y eso es falso, como ya decia el
    /// comentario anterior. Lo que si es, medido en «koliseo completo con invitacion-koli 2vs2»:
    /// el cliente lo manda una sola vez, con «1001» -- f2=1 --, y esa captura es de un DOS contra
    /// dos. El indice 1 del ltd es justo la entrada de equipos de dos, asi que el campo es la
    /// modalidad y cuadra sin forzar nada.
    /// </remarks>
    public const string Luy = "luy";
    // La respuesta es el lth y no el lsx: ver la nota de aquel.

    /// <summary>Eso es todo el listado de actores; vacio, justo detras de jss. Sin el, el cliente nunca da el mapa por cargado, espera unos dos segundos y reintenta con knm, kno y kny.</summary>
    public const string Lva = "lva";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Lwb = "lwb";

    /// <summary>Elegir un ornamento; un mensaje vacio significa ninguno.</summary>
    public const string Lwm = "lwm";

    /// <summary>Acuse de recibo.</summary>
    public const string Lwx = "lwx";

    /// <summary>El hueco que eligio el servidor.</summary>
    public const string Lwz = "lwz";

    /// <summary>Acuse de recibo.</summary>
    public const string Lxa = "lxa";

    /// <summary>Tu aspecto ha cambiado: la vista previa del panel, que nadie mas ve. f1 es un uuid constante en la sesion y distinto por personaje; de donde lo aprende el cliente no se ha encontrado.</summary>
    public const string Lxc = "lxc";

    /// <summary>Sin identificar. 3 usos en el emulador.</summary>
    public const string Lxd = "lxd";

    /// <summary>Mostrar u ocultar lo que hay en un hueco; con f3 puesto esa piel desaparece del siguiente lxc, la prenda no se quita, deja de dibujarse.</summary>
    public const string Lxg = "lxg";

    /// <summary>Acuse de recibo.</summary>
    public const string Lxk = "lxk";

    /// <summary>El estado de la ventana; f7 es el mismo uuid que lleva la vista previa lxc y f12 su mismo look, que es como el panel sabe que la respuesta es suya.</summary>
    public const string Lxo = "lxo";

    /// <summary>Guardar: aqui el borrador pasa a ser lo que se lleva puesto y el look llega al resto del mapa. El fichero de mapeos lo llama AlignmentSubAreaUpdate, y eso es falso.</summary>
    public const string Lxs = "lxs";

    /// <summary>El aura.</summary>
    public const string Lxw = "lxw";

    /// <summary>Poner o vaciar un hueco concreto; sin variante, y sin objeto vacia el hueco.</summary>
    public const string Lyf = "lyf";

    /// <summary>Acuse de recibo.</summary>
    public const string Lyj = "lyj";

    /// <summary>Lleva el aura en el flujo de apariencia (f1: id de aura, vacio si ninguna); en cada captura de equipar y desequipar lleva la constante 206, cuyo significado no esta establecido.</summary>
    public const string Lym = "lym";

    /// <summary>Ponerse una prenda dejando que el servidor elija el hueco; acepta una variante, que es lo que usan los objetos vivos para imitar una prenda u otra.</summary>
    public const string Lys = "lys";

    /// <summary>Los conjuntos guardados del guardarropa; obligatorio, sin el la ventana de cosmeticos suena y no se dibuja, muriendo en CosmeticUi.DisplayOutfit. En el replay se sustituye por los del personaje que juega.</summary>
    public const string Lyt = "lyt";

    /// <summary>Guardado confirmado.</summary>
    public const string Lyu = "lyu";

    /// <summary>Acuse de recibo.</summary>
    public const string Lyv = "lyv";

    /// <summary>Elegir un titulo; solo toca el borrador. Un mensaje vacio significa ninguno.</summary>
    public const string Lze = "lze";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Mes = "mes";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Mez = "mez";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Mfa = "mfa";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Mgq = "mgq";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Mgt = "mgt";

    /// <summary>Identificador del catalogo de contenido; opaco, el cliente solo lo compara consigo mismo.</summary>
    public const string Mgz = "mgz";

    /// <summary>
    /// El nombre real del mensaje que viaja con este opcode. Se saben 0
    /// de 253; de los demás devuelve cadena vacía, que es lo honrado.
    /// </summary>
    public static string Label(string opcode) => Labels.GetValueOrDefault(opcode, "");

    /// <summary>Los que se saben, por opcode.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };
}
