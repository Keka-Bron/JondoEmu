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

    /// <summary>Algo del koliseo que el servidor empuja. No esta descifrado.</summary>
    /// <remarks>
    /// Dos apariciones y las dos distintas: «08012001» sin que el cliente pida nada, 27 s despues
    /// de entrar, y «18032001» contestando al lte de la vuelta. f1=1/f4=1 frente a f3=3/f4=1: que
    /// significa cada campo no se sabe. Se manda el segundo tal cual al recibir el lte, que es lo
    /// unico medido; no se inventa el primero.
    /// </remarks>
    public const string Lsx = "lsx";

    /// <summary>Acompana al lte de la vuelta del koliseo. 151 bytes, sin descifrar.</summary>
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

    /// <summary>Catorce conjuntos guardados, cada uno con un look; se descarta. 7 mensajes.</summary>
    public const string Ihb = "ihb";

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

    /// <summary>Respuesta corta y VACIA a un ima. Ni siquiera lleva carga.</summary>
    public const string Imk = "imk";

    /// <summary>Mientras el grupo sigue al lider: { f1: a quien se sigue, mapa, coordenadas, casilla }.</summary>
    public const string Ikv = "ikv";

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

    /// <summary>Acuse del cliente sobre el grupo. Sin medir.</summary>
    public const string Imo = "imo";

    /// <summary>Acuse del servidor sobre el grupo. Sin medir.</summary>
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

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
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

    /// <summary>Solo alcanzable desde krc y desde isi, que no llegan nunca. 7 mensajes en 2 ficheros.</summary>
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

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Itr = "itr";

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

    /// <summary>Lo envia el cliente (8 veces en 7 ficheros) pero el emulador lo construye como mensaje de servidor y nunca lo llama. El fichero de mapeos lo llama AlmanaxDateMessage, y eso es falso.</summary>
    public const string Izh = "izh";

    /// <summary>El builder no se llama nunca. 2 mensajes en 2 ficheros.</summary>
    public const string Izu = "izu";

    /// <summary>Lo mismo que jjs pero en su propio mensaje; se descarta. 5 mensajes.</summary>
    public const string Jaa = "jaa";

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

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jct = "jct";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jfc = "jfc";

    /// <summary>El conyuge, con su look; se descarta. 13 mensajes.</summary>
    public const string Jgu = "jgu";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jgv = "jgv";

    /// <summary>El gremio de la cuenta grabada; se descarta. 7 mensajes.</summary>
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
    /// Va vacío y en pareja con <see cref="Lqt"/>, una sola vez por combate y justo antes del kai.
    /// Medido en «combate contra 4 poutchs nivel 25»: en 2.937 mensajes salen una vez, ahí.
    /// Aparece además en la ráfaga de entrada al mundo.
    /// </summary>
    public const string Lqg = "lqg";

    /// <summary>El compañero del <see cref="Lqg"/>. También vacío, y sólo en el inicio de combate.</summary>
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

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kau = "kau";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kba = "kba";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kbd = "kbd";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kbt = "kbt";

    /// <summary>El cofre se abre; los dos valores son constantes en la captura y el 100 parece el numero de huecos.</summary>
    public const string Kci = "kci";

    /// <summary>Mover un objeto del cofre; la direccion no viaja, se deduce de donde esta el objeto. f1 llega como -1 cuando se arrastra la pila entera.</summary>
    public const string Kcr = "kcr";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kcx = "kcx";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kda = "kda";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kdg = "kdg";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kdk = "kdk";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Kdw = "kdw";

    /// <summary>Lo envia el cliente (10 veces en 1 fichero) pero el emulador lo construye como mensaje de servidor y nunca lo llama. El fichero de mapeos lo llama AccountCapabilitiesMessage, y eso es falso.</summary>
    public const string Kdx = "kdx";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Kea = "kea";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Keh = "keh";

    /// <summary>No implementado. Exclusivo de las capturas de interactivos varios (Interactivos varios); 9 mensajes.</summary>
    public const string Kgp = "kgp";

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

    /// <summary>Gastar puntos de caracteristica; el valor es el total pagado, no un incremento, lo que hace el mensaje idempotente, y un total que no cabe se rechaza entero. Campos: 1 inteligencia, 2 suerte, 3 vitalidad, 4 sabiduria, 5 agilidad, 6 fuerza. Lleva id de peticion real (7 peticiones).</summary>
    public const string Kum = "kum";

    /// <summary>Ya estas jugando con este personaje; sin el, el cliente se queda en la pantalla de personaje con el reloj de arena.</summary>
    public const string Kva = "kva";

    /// <summary>Resultado de la creacion; vacio si va bien, f2 lleva el motivo del rechazo.</summary>
    public const string Kvb = "kvb";

    /// <summary>No te pares en la pantalla de personajes. Solo sale al entrar directo al mundo: reconexion a combate y koliseo. NO va en la rafaga normal.</summary>
    public const string Kvd = "kvd";

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

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
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

    /// <summary>Bloque 1 digerido: el servidor real espera esto antes de enviar el bloque 2.</summary>
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
