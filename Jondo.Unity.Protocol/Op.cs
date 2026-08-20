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
/// Los 253 opcodes que el emulador usa de verdad, con nombre.
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

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Hqa = "hqa";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Ibo = "ibo";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Idf = "idf";

    /// <summary>Alianzas, por nombre y tag; se descarta. 9 mensajes.</summary>
    public const string Ife = "ife";

    /// <summary>Catorce conjuntos guardados, cada uno con un look; se descarta. 7 mensajes.</summary>
    public const string Ihb = "ihb";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ijq = "ijq";

    /// <summary>Solo se llama desde la rama kkn, que nunca llega. 5 mensajes en 3 ficheros.</summary>
    public const string Ilc = "ilc";

    /// <summary>Lo envia el cliente (1 vez); el fichero datos/dofus3_mappings.json lo llama InventoryWeightMessage y eso es falso, un informe de peso seria push del servidor.</summary>
    public const string Imd = "imd";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ioc = "ioc";

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Ios = "ios";

    /// <summary>Sin identificar. 2 usos en el emulador.</summary>
    public const string Iov = "iov";

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

    /// <summary>Sin identificar. 1 uso en el emulador.</summary>
    public const string Jol = "jol";

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

    /// <summary>Solo alcanzable desde isi, que nunca llega. 2 mensajes en 2 ficheros.</summary>
    public const string Kku = "kku";

    /// <summary>Cierra el dialogo; el cliente no cierra la ventana del zaap por si mismo. f1 es un motivo fijo, no algo que calcular.</summary>
    public const string Kld = "kld";

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
    public const string Kti = "kti";

    /// <summary>Una linea que escribio el jugador.</summary>
    public const string Ktm = "ktm";

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

    /// <summary>Cierra la lista de personajes; vacio, justo detras de kvi en la rafaga real. Candidato principal a la causa del boton de crear personaje muerto.</summary>
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

    /// <summary>Lo envia el cliente (1 vez) pero el emulador lo construye como mensaje de servidor y el builder no se llama nunca. Lleva id de peticion real. El fichero de mapeos lo llama JobDescriptionMessage, y eso es falso.</summary>
    public const string Luy = "luy";

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
