# Migración 3.6.10.10 — autenticación, servidores y personajes

Todo lo de aquí sale de las capturas reales de
`Wireshark captures from real game/Autenticacion-Servidor-Personaje/`.

Los nombres de personaje y de cuenta que aparecen en esas capturas no se escriben aquí ni en
el código: solo interesa la estructura.

---

## Herramientas de decodificación

Están en `tools/`. Filtran por los puertos 5555/5556, que es donde va el tráfico en claro;
el resto de la captura es HAAPI/Zaap sobre TLS y no se puede leer.

```
py tools/pcap.py <captura.pcapng>                 -> los mensajes de cada flujo, por orden
py tools/pcap.py <captura.pcapng> kvi kra --raw   -> vuelca esos mensajes enteros
py tools/timeline.py <captura.pcapng>             -> las dos direcciones intercaladas, con
                                                     el reloj en milisegundos desde el inicio
```

`timeline.py` es el que hace falta para saber **qué contesta el servidor a qué**: `pcap.py`
reensambla cada dirección por separado y ahí se pierde el orden entre ellas.

### Probar sin abrir el juego

`tools/cliente_falso.py` habla el protocolo de verdad contra el emulador ya arrancado: se
autentica, pide ticket, elige personaje, entra al mundo y manda latidos. Imprime lo que
contesta el servidor a cada cosa.

```
py tools/cliente_falso.py
```

Usa el `GameToken` de la cuenta que haya en `auth.db`.

Además de imprimir, comprueba: que el `kub` lleva cada característica en su contenedor, que los
hechizos son los de la clase del personaje, que `jqi` se contesta con un `jsq` **en campo raíz 3**,
que `jqk` arranca la secuencia de cambio de mapa, que el reparto de puntos y el equiparse
contestan lo que deben, y que la base de datos queda como corresponde.

Toca la base de datos —gasta puntos, reinicia la hoja, cambia de mapa—, así que **guarda el
personaje al empezar y lo devuelve tal cual al terminar**, pase lo que pase.

### El cliente escribe su propio log

`%LOCALAPPDATA%Low\Ankama\Dofus\Player.log`. Unity lo deja ahí, fuera de la carpeta del juego, y
es el diario del cliente: cuando un mensaje nuestro le revienta, apunta la excepción **con el
nombre del mensaje**.

```
NullReferenceException
  at giq.bkjt (llp a)      <- una entrada de característica
  at ees.wuc (kub a)       <- procesando nuestro kub
```

Es la única fuente que dice qué le pasa al cliente por dentro. Después de cada cambio de
protocolo, mirar ahí:

```
grep -E "^NullReferenceException|at [a-z]+\.[a-z]+ \([a-z]{3} a\)" Player.log
```

De las seis excepciones de la sesión del 14/08 quedan resueltas cuatro: dos de `kub` (contenedores
mal) y dos de `jhh`/`jhk` (hablaban de un gremio cuyo mensaje no mandábamos). Sigue pendiente la
de `jss`, que en realidad es `MapInfoUI.SetInfoFromSubarea` — el widget del nombre del mapa — y
que sí trae los nombres sin ofuscar.

### Sacar datos del cliente

Dos extractores, los dos con UnityPy:

```
py tools/extract_breed_stats.py       -> breed_stats.json       cuánto cuesta subir cada
                                                                característica en cada clase
py tools/extract_characteristics.py   -> characteristics.json   qué es cada id de característica,
                                                                con su nombre en castellano
```

El segundo cruza el `nameId` de cada característica con la tabla `Translations` de `world.db`, que
tiene 339.175 entradas. Es lo que hay que mirar antes de suponer para qué sirve un id.

---

## La secuencia completa

```
   ── conexión al servidor de conexión (5555), mensajes sin envoltorio ──
C->S  126 B   f1 { f1: idioma, f3 { f1: token, f3 { f1: id de instalación }, f5: "3.6.10.10" } }
S->C  727 B   autenticación aceptada + lista de servidores + personajes por servidor
C->S   10 B   f1 { f1: idioma, f4 { f1: id de servidor } }
S->C   86 B   f2 { f1: idioma, f4 { f1 { f1: ticket, f2: host, f3: puertos } } }

   ── el cliente cierra y abre una conexión nueva, ya con envoltorio ──
C->S  kqz     f2: ticket, f3: idioma
C->S  krt     vacío
S->C  kra lqu hoy kqu mgq mgt hpd krs      (una sola ráfaga)
S->C  mgz kqp kqp kqp
S->C  kvi     LISTA DE PERSONAJES
S->C  jtg     catálogo de artículos de tienda; no hace falta para la lista

   ── el jugador elige personaje, sobre la MISMA conexión ──
C->S  kvw     f1: id de personaje
S->C  kqp kub jbf kuf ipc kva ...  y la entrada al mundo
```

**El campo raíz importa.** En 3.6.10.10 los mensajes que empuja el servidor van en el campo 1
de la trama: `f1 { f1 { f1: type_url, f2: carga } }`. Comprobado en la captura de entrada al
mundo: 390 de 391 mensajes del servidor usan el campo 1. El cliente usa el campo 2. El
`NetworkEnvelope.BuildGameNodePacket` que arrastra el emulador envuelve en el campo 3, que es
lo que hacía la versión anterior del protocolo: **la ruta del mundo sigue pendiente de migrar**.

---

## Estructuras

### Autenticación aceptada

```
f2 { f1: idioma
     f3 { f1 { f1: id de cuenta
               f2: apodo
               f3: etiqueta
               f4 { f1 (repetido): servidor
                    f2 (repetido): { f1: categoría, f2: plazas } }   7 entradas, categorías 0..6, 5 plazas
               f5: fin de suscripción
               f6: {} } } }
```

Y cada servidor:

```
f1 { f1 { f1: id, f3: categoría }
     f3 (repetido) { f1: nombre, f2: raza-1, f3: sexo, f4: nivel, f5: última conexión } }
```

Ojo con dos detalles que solo se ven comparando mensajes:

- Aquí la **raza va con base cero** (una menos que en el resto del protocolo).
- La **categoría** agrupa los servidores; no es el estado. En la captura los servidores del
  rango 29x son categoría 1, los 35x son 2 y 3, y los dos antiguos son 4 y 5. El color del
  servidor en la pantalla de selección no viaja por aquí, sino por HTTP.

### Lista de personajes (kvi)

```
f1 (repetido) { f1 { f2: nombre
                     f3: nivel
                     f4 { f2: { f3: sexo }   presente y VACÍO si el sexo es 0
                          f6: aspecto
                          f7: raza } }
                f2: id de personaje }
```

El nivel puede pasar de 200: son los niveles Omega.

### Aspecto

Mismo bloque en kvi, en kva y en los mensajes del mundo:

```
f1 : colores indexados, varints empaquetados, cada uno (índice << 24) | rgb
f2 : 3          constante en todas las muestras
f3 : bonesId    1 en un personaje normal; otro valor si lleva un disfraz
f5 : escalas, empaquetadas
f6 : skins, empaquetados
f7 : subentidades (montura, mascota), con la misma forma anidada
```

Los valores base salen del propio cliente: `tools/extract_breed_looks.py` lee el bundle de
razas y genera `breed_looks.json` con bonesId, skins, escalas y los seis colores por defecto de
cada raza y sexo. Contrastado con la captura de creación de personaje: la raza 11 masculina da
skin 110 y escala 55, y eso es exactamente lo que el servidor real devolvió.

**Pendiente**: los skins de los objetos equipados. El aspecto que se manda ahora es el base de
la raza, así que un sombrero o un escudo equipados no se ven. `ItemTemplates.Data` tiene un
campo `appearanceId`, pero solo lo llevan monturas y mascotas, no las piezas de equipo. Hay
capturas de la interfaz de apariencias y cosméticos sin analizar, y están los JSON de
dofusdude para 3.6.10.10; de ahí debería salir.

### Volver atrás

Las dos vueltas atrás son el mismo trabajo para el servidor:

```
C->S  kqq   vacío
S->C  kqr   f1: identificador de sesión, f4: 1
   ── el cliente cierra la conexión y rehace el saludo desde el principio ──
```

Que se quede en la lista de personajes o en la de servidores lo decide el cliente.

### Creación de personaje

```
C->S  kwd   vacío                  pide un nombre sugerido
S->C  kvk   f1: nombre sugerido
C->S  kvz   f1 { f1: nombre, f2: cosmético, f3: colores elegidos (-1 = por defecto),
                 f5: ?, f7: raza }
S->C  kvb   vacío
S->C  kqp x3, kvi (ya con el personaje nuevo), kvl { f1: 1, f2: id }
```

Sin implementar todavía.

---

## Lo que ya está hecho

- `Network/Pb.cs` — escritor de protobuf que respeta el orden de los campos, que hace falta
  porque estos mensajes repiten el mismo número de campo.
- `Network/ConnectionProtocol.cs` — todos los constructores de esta fase.
- `Network/ConnectionProtocolSelfTest.cs` — comprueba al arrancar que los mensajes salen con la
  forma capturada. Los tamaños de las trece tramas de la ráfaga coinciden byte a byte con los
  de la captura.
- `Network/SessionRegistry.cs` — tickets de un solo uso que atan la segunda conexión a una
  cuenta y a un servidor.
- `Managers/BreedLookTable.cs` — aspecto base por raza y sexo.
- Tabla `Servers` y columnas `ServerId` y `LastConnection` en `Characters`.

## Lo que queda

1. Creación de personaje (éxito y fallo por límite).
2. Skins de los objetos equipados en el aspecto.
3. Migrar la ruta del mundo: campo raíz 1 en vez de 3, opcodes nuevos (`ktw` pasa a `kva`,
   `kkr` a `jru`) y las tramas pregrabadas de `BasePayloads`, que son de la versión anterior.
4. `Jondo.Unity.Protocol/OpcodeRegistry.cs` existe pero no lo usa nadie; el Launcher sigue
   con unos 145 opcodes escritos a mano, casi todos de la versión anterior.


---

## Entrada al mundo (3.6.10.10)

**Funciona.** El cliente carga el mapa, el HUD, el chat y el minimapa.

### Cómo

Se reproducen los mensajes reales de la captura, extraídos con `extraer_world.py` en tres
ficheros (`world_etapa*.bin`). No van seguidos: el servidor real manda un bloque, espera
confirmación del cliente y sigue.

```
cliente kvw  ->  bloque 1: personaje, características, misiones...  (330 mensajes)
                 bloque 2: los cuatro catálogos grandes             (4 mensajes)
cliente kqo  ->  bloque 3: el mapa                                  (38 mensajes), UNA sola vez
cliente kmv+jrh -> jss (actores) y lva
```

El `lqc` sí llega, pero tarde: el cliente lo manda al terminar de digerir el bloque 1, cuando
el nuestro ya ha soltado el 2. Por eso los bloques 1 y 2 van seguidos y el `lqc` se ignora.

### `kqo` es un latido, no una petición

Esto costó una tarde. El cliente manda `kqo` **cada cinco segundos mientras está en el mundo**,
y el servidor real le contesta **un único mensaje**, `kqy` (`0801`), y nada más. En la captura
del tutorial hay veinticuatro seguidos, separados 5.000 ms clavados.

El emulador contestaba a cada latido con el bloque del mapa entero. Como ese bloque lleva
`jru`, y `jru` significa "carga este mapa", el cliente rehacía la carga del mundo cada cinco
segundos: pantalla de carga, mapa, HUD, todo otra vez. En una sesión de cinco minutos se vieron
veinte vueltas.

El bloque del mapa sale ahora en el primer `kqo` de la entrada y a partir de ahí el latido se
contesta solo con `kqy`. El bloque ya empieza por un `kqy` propio, así que el primero tampoco
se contesta dos veces.

### `lva` cierra la lista de actores

Detrás de `jss` va siempre `lva`, un mensaje vacío que quiere decir "no hay más actores". Está
en las cuatro capturas de movimiento, en la entrada al mundo y en el tutorial, siempre pegado.
El emulador no lo mandaba: mandaba `jss` y se callaba. El cliente esperaba unos dos segundos,
volvía a preguntar con `knm`, `kno` y `kny`, y no daba el mapa por cargado.

### Actores del mapa (`jss`)

```
f2: id del mapa
f5 (repetido)  un actor
   f1 { f1: celda, f2: orientación }
   f2 { f1 { ...qué es... }
        f3 { f1: colores, f2: 3, f3: bonesId, f5: escalas, f6: skins } }   el aspecto
   f3: id contextual   (negativo para monstruos y NPC)
```

Lo que hay dentro de `f2.f1` dice qué es: `f5` jugador, `f7` NPC, `f4` grupo de monstruos.
**Los tres llevan su aspecto en `f2.f3`**, el grupo incluido.

El grupo es **un** mensaje, no uno por monstruo:

```
f4 { f1: 1
     f2 { f1 (repetido): secuaz  { f1: id, f2: nivel, f3: aspecto, f4: grado }
          f2:            líder   { f1: id, f2: nivel,              f4: grado } }
     f5: -1 }
```

El líder aparece una sola vez y sin aspecto propio, porque su aspecto es el del grupo, el de
`f2.f3`: ese es el sprite que dibuja el cliente. Comprobado en nueve grupos de las capturas de
combate y movimiento, y la cuenta siempre sale a un líder más los secuaces que haya.

**Esto tuvo la lista de actores vacía desde el principio.** Se mandaba un `f2` por monstruo
colgando de `f4`, o sea un varint donde el cliente espera un submensaje. Un parser generado no
perdona eso: lanza excepción y tira el `jss` entero. Por eso no se dibujaba nada — ni los
monstruos, ni los NPC, ni el propio personaje, aunque su entrada estuviera bien.

Dos detalles fáciles de invertir: el **nivel va en `f2` y el grado (1..5) en `f4`**, no al
revés, y el grupo cierra con `f5 = -1`.

El jugador lleva además, en las capturas, `f3.f1 {…}`, `f3.f5 {f7:1}` (repetido, opciones) y
`f3.f7`, que nosotros no mandamos. No parecen impedir el dibujado, pero están sin identificar.
**Los NPC no se mandan todavía**: `BuildMapActors` no los construye.

#### `f6` es la subárea, y no es decoración

El `jss` no lleva solo actores. Fuera del `f5` repetido hay:

```
f2   id del mapa
f6   id de la subárea
f11  repetido: los elementos interactivos del mapa (puertas, recursos)
f14  1
f15  repetido: en qué estado está cada uno de ellos
```

`f6` es lo que el cliente llama `MapComplementaryInformations`, y sin él revienta:

```
at MapInfoUI.SetInfoFromSubarea (System.Int16 subAreaId)
at MapInfoUI.SetMapInfoData (System.Int64 mapId, System.Int16 subAreaId, ...)
at MapInfoUI.OnMapComplementaryInformationsData (ccn message)
at ehl.xxt (jss a)
```

Busca la subárea, no encuentra nada porque le mandábamos cero, y lanza. Y con ella se va todo lo
que pone ese widget: **el nombre del área y la subárea, las coordenadas, el nivel del área, el
bono de botín**, y el muñequito del minimapa — que por eso se quedaba pintado en el zaap por
mucho que anduviera el personaje.

El valor sale de `MapPositions`, comprobado contra la captura: el mapa 154010371 viaja con 450 y
el 154010882 con 442, que son exactamente sus subáreas.

`f11` y `f15`, los interactivos, siguen sin mandarse.

### Características (`kub`)

La captura de **creación de personaje** es la pieza que faltaba: el `kub` de un personaje recién
creado enseña los valores por defecto del juego sin nada encima.

```
id 0                      55      vida base. 50 + 5 por nivel; con nivel 1 da 55 clavado
id 1                      6 PA    en f5{f1}
id 23                     3 PM    en f5{f1}
id 47                     10000   energía, en f2{f2}, no en f4
id 48, 107, 150           100
id 120..125, 141..143     100
id 75                     10       id 97   -55
```

**Los `-100 %` y las resistencias al 50 % eran esto.** Toda esa familia de características son
porcentajes que arrancan en 100 y las mandábamos a 0; el cliente lee el 0 y pinta la diferencia
contra el 100 que espera. Y la vida 0/0 era la **característica 0**, que no se mandaba: es la
única entrada del mensaje real que **no lleva id**, porque proto3 se salta el campo cuando vale
cero y cero es su id. `LearnCharacteristicIds` solo leía las entradas con id, así que se perdía
justo esa.

Contenedores, que no son todos iguales, y **esto no es cosmética**:

```
f4 { f2: base, f3: de pergaminos, f7: del equipo }   casi todas
f5 { f1: base, f5: bonus }                           1 (PA) y 23 (PM)
f2 { f2: valor }                                     29, 47 (energía) y 96
```

Mandábamos 29 y 96 en `f4`. El cliente lee un campo que no está, lanza `NullReferenceException`
en `giq.bkjt (llp a)` dentro de `ees.wuc (kub a)` y **se queda sin hoja de personaje entera**. Eso
es lo que tenía el botón de características en gris mientras la tecla C sí abría el panel: el
panel lo monta el cliente con sus datos, el botón lo habilita el manejador que nunca terminaba.

Qué id va en qué contenedor **se lee del `kub` capturado**, no está escrito en el código:
`WorldEntry.ContainerOf`. Al arrancar lo dice por consola.

El `f3` tampoco era el 100 constante que parecía. El personaje capturado tenía exactamente 100 en
las seis primarias porque se había bebido **todos los pergaminos** del juego, que es el tope.
Copiarlo hacía que cada característica nuestra saliera cien puntos por encima de lo que dice la
base de datos, y la barra de vida con ella (300 de base → 400 en pantalla).

**Característica 3 = puntos por repartir.** Demostrado por resta: 995 antes de gastar quince en la
hoja, 980 después. No la mandábamos, y por eso "PUNTOS RESTANTES" salía a 0.

**Y ya no hace falta adivinar ninguna.** El cliente trae la tabla entera en
`data_assets_characteristicsdataroot.asset.bundle`: id, `nameId` y si es repartible o visible.
Cruzando el `nameId` con la tabla `Translations` de `world.db` salen los 122 nombres en castellano.
`tools/extract_characteristics.py` → `characteristics.json`.

De ahí salen las que faltaban:

```
 3 Puntos de característica    27 Esquiva PA      28 Esquiva PM
 4 Puntos de hechizo           82 Retirada de PA  83 Retirada de PM
40 Pods                        78 Huida           79 Placaje
44 Iniciativa                  96 Escudo          97 Malus de vida temporal
48 Prospección                 46 Rango de alineamiento
```

Ojo con esas dos últimas: la prospección es la **48**, no la 46, que llevaba tiempo apuntada mal.

Las seis derivadas las calcula el servidor, porque no las manda nadie más y el cliente no las
deduce solo: **diez de sabiduría dan uno de cada esquiva y de cada retirada, y diez de agilidad
uno de huida y uno de placaje**. Sin eso el panel las enseña a cero por mucha sabiduría que se
reparta.

**Sin identificar**: el `f4` del cuerpo (5 en un personaje nuevo, 30 en el de nivel 154) y el `f9`
(`{f2: 2, f3{f3: 500}, f5: 1}` recién creado, `{f1: 100, f2: 3, f3{f3: 500}, f5: 200}` en el de
154). Se mandan con el valor del personaje recién creado: es lo único honesto que tenemos.

### Reparto de puntos

```
C->S  kum   f1: inteligencia, f2: suerte, f3: vitalidad,
            f4: sabiduría,   f5: agilidad, f6: fuerza
S->C  iun { f1: lo que carga, f3: lo que puede cargar }
S->C  kub (entero, otra vez)

C->S  kuh {}          el botón de reiniciar
S->C  iun, kub
```

El orden de campos sale de las seis capturas de `Caracteristicas/`: repartir cinco puntos en cada
una manda `{f1:5, f2:5, f3:5, f4:15, f5:5, f6:5}` y la característica 3 baja **cuarenta**, la suma.

Y lo importante: **`kum` lleva lo que se PAGA, no lo que sube**. La sabiduría son esos quince,
porque cuesta tres puntos cada uno. Así que el servidor necesita la tabla de precios, y tiene que
ser la del cliente, porque el cliente ya le ha enseñado el resultado al jugador antes de mandar
nada. Está en los bundles y no es la de siempre: los tramos van de cien en cien, no de cincuenta
en cincuenta.

**Y son totales, no incrementos.** Eso las capturas no lo dicen, porque el personaje que grabaron
acababa de reiniciar la hoja y en él las dos lecturas dan lo mismo. Una sesión del cliente real sí
lo dice. Cuatro confirmaciones seguidas, con un reinicio justo antes de la primera:

```
kum { vitalidad 10 }
kum { vitalidad 10, agilidad 5 }
kum { vitalidad 20, sabiduría 15, agilidad 5 }
kum { vitalidad 40, sabiduría 15, agilidad 10, fuerza 5 }
```

Leído como incrementos, ese personaje compró vitalidad cuatro veces y el capital bajó cuarenta
puntos cuando el jugador solo había pedido los quince de sabiduría — que es exactamente lo que
pasó. Leído como totales, cada mensaje es el reparto entero tal y como está, el panel simplemente
se repite, y cada número acaba donde el jugador lo puso.

Así que el campo es un OBJETIVO: esta característica ha de tener tantos puntos metidos. Mandar dos
veces lo mismo no hace nada la segunda, que es la propiedad que importa, porque el panel se
repite. Y el reparto se aplica **entero o nada**: si la suma no cabe en el capital, no se coge
la parte que quepa. El cliente calcula el coste antes de pedir, así que una suma que no cabe
significa que no estamos de acuerdo sobre la hoja, y cobrar a medias solo lo empeora.

Cuidado con el clamp por campo, que es como se rompió la primera vez: recortar cada característica
contra el capital *sin ir descontando* dejaba gastar 180 puntos teniendo 75, y el personaje acabó
con el doble de todo.

```
fuerza, inteligencia, suerte, agilidad   1 hasta 100, 2 hasta 200, 3 hasta 300, 4 en adelante
vitalidad                                1 siempre
sabiduría                                3 siempre
```

`tools/extract_breed_stats.py` → `breed_stats.json` → `Managers/BreedStatCost.cs`.

El reinicio devuelve `5 x (nivel - 1)`, que es justo el capital del personaje de la base de datos:
75 sin repartir + 170 repartidos = 245 = 5 x 49. No cobra kamas, porque en la captura los kamas de
la hoja son los mismos antes y después.

### Nada de datos de la cuenta capturada

El emulador está para compartirse, así que no puede enseñar la libreta de contactos de quien
grabó las capturas. Estos mensajes ya no se reproducen:

| Opcode | Qué llevaba |
|---|---|
| `kqg` | La lista de amigos, con apodos, niveles, gremios y alianzas reales |
| `jhe` | El gremio |
| `jhh` | El gremio otra vez: fecha de fundación, nivel, cuántos son |
| `jhk` | El nombre del gremio, escrito |
| `koj` | **Veinte cuentas de Ankama** con su id, su apodo y su tag: `f2 { f2: id, f4 { f1: apodo, f2: tag }, f5: 3 }` |
| `ife` | Las alianzas, por nombre y por siglas |
| `jjs` | Un puesto de mercader plantado en el mapa, con la cuenta detrás: `f5 { f2 { f8 { f1: apodo, f2: tag } } }` |
| `jaa` | Lo mismo en su propio mensaje |
| `hol` | El cónyuge y el gremio del personaje |
| `jgu` | El cónyuge otra vez, con su aspecto |
| `ihb` | Los catorce conjuntos guardados, cada uno con los aspectos de esa cuenta |
| `ife` | La lista de amigos Ankama (ya estaba fuera: además reventaba el cliente) |

`jhh` y `jhk` estaban saliendo hasta ahora, y el `Player.log` del cliente enseña lo que costaba:
una `NullReferenceException` por cada uno, del mismo manejador. Tiene sentido — describen un
gremio cuyo mensaje (`jhe`) no mandamos, así que no hay a qué engancharlos.

Y estos se reconstruyen desde la base de datos en lugar de reproducirse:

| Opcode | Qué sale en su lugar |
|---|---|
| `kva` | El personaje que se juega: nombre, nivel, clase y aspecto |
| `irq` | Los oficios: se quedan los ids, que son datos del juego, todos a nivel 1 sin experiencia |
| `hms` | Los hechizos de la clase y el nivel del personaje |
| `itg` | La barra de hechizos. El otro `itg`, el de objetos, se deja: apunta al inventario, que sigue siendo el de la captura |

Y hay un nombre que no viaja en ningún mensaje suyo sino **dentro del inventario**: quien magueó
cada objeto, que es el "Modificado por" del tooltip.

```
ivx: f3 (repetido) { f5 (repetido) { f2 (repetido) { f1: quien lo hizo } } }
```

Cinco de esas firmas eran el propio personaje capturado y se sustituían con el resto de su
identidad; una era de otra persona y no la tocaba nadie. Ahora se leen del bloque al arrancar y
todas pasan a ser el nombre de quien juegue. **Los tres niveles están repetidos**, y quedarse con
el primero de cada uno no encuentra ninguna: las firmas están en entradas posteriores.

#### Comprobarlo: `tools/leak.py`

La versión anterior buscaba nombres concretos que había que saberse de antemano, y por eso daba
limpio mientras el emulador mandaba veinte cuentas de Ankama con apodo y tag en un `koj` que nadie
había mirado. **Un comprobador de fugas que depende de que ya sepas lo que buscas no comprueba
nada.**

Ahora barre todo el texto legible de todo lo que manda el servidor y lo agrupa por opcode:

```
py tools/leak.py                  todo lo que ha salido, por mensaje
py tools/leak.py --op koj ife     solo esos, con todas sus cadenas
py tools/leak.py --buscar Harmoo  además avisa si aparece algo concreto
```

Hay que **mirar la lista de verdad**: un opcode nuevo con nombres propios dentro es una fuga. Así
salieron `koj`, `ife`, `jjs`, `jaa` y la firma de magueo, después de que la versión de agujas
diera limpio cuatro veces seguidas.

Ojo también con leer nombres a ojo del volcado: lo que parecía `EfesiaX` era `Efesia` más el byte
del campo siguiente. Conviene sacarlos parseando el protobuf, no con una expresión regular.

### Andar y cambiar de mapa

Las cuatro capturas de `Movimiento/` dan la secuencia entera:

```
C->S  jrw   f1: id del mapa, f2: el camino empaquetado, cada paso  orientación << 12 | casilla
S->C  jsj   f1: casillas, f2: orientación final, f5: de quién es
C->S  jqi   {}                      ha llegado al borde y quiere salir
S->C  jsq   {}                      adelante        <- campo raíz 3, no 1
C->S  jqk   f2: id del mapa destino
S->C  jsd (f2: quién), jru (f2: id), lqu, lqn, hjk (f1: ids empaquetados)
C->S  kmv, jrh   los dos con el id del mapa
S->C  jss (actores), lva
```

**Hay tres campos raíz y no son intercambiables**: el 1 es lo que el servidor empuja por su
cuenta, el 2 lo que manda el cliente, y el **3 una respuesta**, que además repite el id que
traía la petición (`-1` en todo lo visto). `jsq` es el primero que lo necesitaba, y sin él el
cliente no manda nunca el `jqk` y el personaje se queda en el borde para siempre.
`ConnectionProtocol.Answer` construye ese envoltorio; sale byte a byte como el capturado y hay
una prueba automática que lo comprueba.

**El id de mapa del `jqk` es una CONJETURA, no una orden.** El cliente lo calcula por aritmética
sobre el id en el que está, y eso solo acierta donde el mapa de al lado resulta ser el id
siguiente. Dos capturas enseñan al servidor real cargando un mapa distinto del que le pidieron, y
la sesión del 14/08 lo enseña desde el otro lado: en 191105028, en [5,-17], saliendo por abajo, el
cliente pidió **191105029, que no existe**. El mapa de abajo es 188745734, en [5,-16]. Le
devolvíamos la conjetura en el `jru`, el cliente no tenía nada que cargar y el personaje se
quedaba clavado en el borde — todos los `jrw` siguientes salen de la misma casilla 556.

#### CORRECCIÓN: los vecinos SÍ están en el cliente

Lo que dice el apartado siguiente sobre que la tabla de vecinos es dato de servidor **es falso**, y
se deja escrito porque el razonamiento que llevó a esa conclusión era razonable y conviene ver
dónde falló: se buscó en el data root y en los JSON de dofusdude, y no se buscó en los bundles de
geometría de cada mapa. Ahí están:

```
mapData.rightNeighbourId  bottomNeighbourId  leftNeighbourId  topNeighbourId
mapData.rightArrowCellList ...   las casillas desde las que se sale por cada lado
mapData.interactiveElements      los elementos interactivos del mapa
```

`tools/extract_map_neighbours.py` los saca: **17.353 mapas y 69.410 bordes**, contra los 3.463 que
teníamos. `MapScrolls` pasa de 2.503 filas a 17.353.

Pero no son la verdad, son **lo que el cliente adivina**:

```
data root del cliente (2.223) vs bundles:   discrepan el 32,2 %
cosechado del SERVIDOR REAL   vs bundles:   coinciden 363 de 384 (94,5 %)
```

Y se ve en la captura del tutorial: el bundle dice que a la derecha de 241437185 está 241437697,
el cliente pide exactamente eso, y el servidor real carga 241438721. O sea que el `jqk` sale del
bundle. Así que el orden de autoridad es **servidor real > tabla de excepciones > bundles**, y por
eso el extractor no pisa nunca un valor que ya esté puesto.

#### Por qué el data root solo tiene 2.223 mapas

Porque **la lista de vecinos no está en los datos del cliente**, y esto está comprobado por cuatro
vías distintas:

- El bundle `data_assets_mapscrollactionsdataroot` tiene **exactamente 2.223 entradas**. Nuestra
  tabla es copia fiel; no se perdió nada al extraerla.
- El registro de cada mapa en `MapTemplates` (y en el `maps_information.json` de dofusdude, que es
  el mismo dato) es entero esto, sin un campo de vecinos:
  `id, m_flags, nameId, posX, posY, subAreaId, tacticalModeTemplateId, worldMap`.
- En la release de dofusdude para 3.6.10.10 hay 85 JSON y **ninguno es `map_scroll_actions`**. El
  `map_references.json` son 571 puntos con nombre `{id, mapId, cellId}`, zaaps y sitios.
- Probamos si `m_flags` codificaba qué bordes están abiertos, bit a bit contra los 2.223 que sí
  sabemos: el mejor de los 32 bits acierta el 71,7 %, o sea nada.

Así que la tabla de vecinos es **dato de servidor**, como los spawns de monstruos: Ankama la tiene
y el cliente no. De ahí que el cliente adivine. Hay que observarla jugando, y para eso está
`tools/cosechar_mapas.py` (ver más abajo). Los 2.223 que trae el cliente
son genuinos —todos son mapas de 3.6.10.10 y el 99,9 % de los vecinos que citan también—, pero
están repartidos a trozos: tocan 228 subáreas de 532 y solo **6 están completas**. En la de Astrub
hay 44 mapas de 159. Es una cosecha parcial, no una extracción.

#### Cosechar los que faltan de las capturas

`tools/cosechar_mapas.py`. Cada cambio de mapa de una captura deja en el cable todo lo necesario:
el `jrw` dice por qué borde se sale (la última casilla del camino), y el `jru` del **servidor real**
dice a dónde lleva. Eso es una fila de `MapScrolls` exacta.

```
py tools/cosechar_mapas.py               mira todas las capturas y solo informa
py tools/cosechar_mapas.py --aplicar     además escribe lo nuevo en world.db
```

Nunca pisa un valor ya puesto: la tabla del cliente es dato del juego y lo cosechado solo rellena
huecos. Si algo se contradijera, lo dice y no toca nada.

Primera pasada, sobre las 215 capturas:

```
405 cambios de mapa vistos, 389 aprovechables, 384 pares distintos
350 nuevos, 34 confirman lo que ya había, 0 se contradicen
MapScrolls: 2.223 -> 2.503 filas, 3.463 -> 3.813 bordes con destino
```

**Lo que más rinde con diferencia es el autopilotaje**: dos rutas largas dieron 295 de esos 350,
cinco veces más que las otras 213 capturas juntas. Dos condiciones para que una captura sirva: que
se vaya **andando** al borde (sin el `jrw` previo no se puede saber por qué lado se salió) y que
sea **a pie y no en zaap**, porque cada tramo en zaap se salta todos los bordes intermedios, que
son justo los que se quieren.

En esas 389 transiciones la conjetura del cliente acertó el 95,1 %. Los 19 fallos son exactamente
los bordes donde el jugador se quedaba clavado, y ahora están anotados.

Medido contra los 3.463 vecinos que traía el cliente:

```
la conjetura aritmética del cliente acierta          31,8 %
la búsqueda por coordenadas acierta                  71,0 %
conjetura-si-vale + coordenadas                      71,1 %
```

Y el techo de cualquier método por coordenadas es ese, porque **el 27,2 % de los vecinos reales no
están en la casilla de al lado**: un buen cuarto de los bordes del juego llevan a un sitio que no
es simplemente un paso más allá. Para esos no hay más remedio que anotarlos.

Pero tampoco vale ignorar la conjetura: el mapa de [5,-16] es de los que casi no tienen nada
—únicamente su vecino de arriba—. Saliendo de él a la derecha, la conjetura del cliente
(188746246) era **correcta** y la tabla no tenía nada que decir.

Así que el destino se resuelve en tres pasos:

1. **La conjetura**, si nombra un mapa que existe y que además está en la casilla de al lado en la
   dirección en la que se anda. Es la única de las tres que sabe cuál de varios mapas que comparten
   coordenada quiere el jugador, y en [5,-15] hay dos al aire libre.
2. **`MapScrolls`**, que es la lista de vecinos del propio juego, donde esté rellena.
3. **Las coordenadas**, que sí tienen los 15.360. Si hay varios en la casilla gana el de exterior,
   entre esos el de la misma subárea, y entre esos el de id más cercano al que se deja: los ids se
   reparten por bloques, así que un vecino suele estar numéricamente cerca.

La conjetura sirve además para desempatar las esquinas, donde la casilla sola no dice si se sale de
lado o hacia abajo.

Por qué borde se sale, de la casilla en la que acaba el `jrw`:

```
columna 13 -> derecha      columna 0 -> izquierda
fila <= 1  -> arriba       fila >= 38 -> abajo
```

(Las salidas capturadas: 405 y 322 por las columnas 13 y 0, 23 y 542 por las filas 1 y 38.)

Y nunca se mueve a un mapa que los datos del mundo no describan: el cliente tampoco puede
cargarlo, así que se quedaría en el borde mientras la base de datos dice que está en un sitio que
no existe. Eso es justo lo que pasó con 191105029, y el personaje se quedó guardado ahí; al
cargarlo también se comprueba y se le devuelve al inicio si hace falta.

Dónde se aparece en el mapa siguiente, medido en las cuatro capturas:

```
derecha   405 -> 392    -13    orientación 0
izquierda 322 -> 335    +13    orientación 4
arriba     23 -> 555   +532    orientación 6
abajo     542 ->  10   -532    orientación 2
```

Hacia dónde se sale se decide con `MapScrolls` de la base de datos (los cuatro vecinos de cada
mapa); si el mapa que pide el cliente no es ninguno de ellos, se mira por coordenadas, y si
tampoco, se queda donde está. La casilla de llegada se valida contra la transitabilidad **de
combate**, no contra `map_walkable_cells.json`: ese recorta los bordes a propósito para que no
aparezcan monstruos en ellos, y el borde es justo donde se llega.

A `jrw` no se le contesta. El servidor real manda el `jsj` de vuelta porque es así como se
enteran **los demás** clientes; con un jugador solo no hay a quién contárselo, y el cliente anda
por su cuenta sin esperar. Lo que sí hace falta es apuntar la casilla en la que acaba, porque el
cambio de mapa se calcula desde ahí. Antes no se apuntaba: el personaje seguía oficialmente donde
se conectó.

`lqn` no se manda: su único campo es un número que no sabemos explicar (197 al entrar al mundo, 24
al cambiar de mapa, 470 tras reiniciar características) e inventarlo es peor que no mandarlo.

`MapChangeHandler` sigue ahí con los opcodes de la versión anterior (`jos`, `joh`, `joi`, `joo`),
sin uso: este cliente no los manda nunca. Lo nuevo está en `Handlers/WorldMoveHandler.cs`.

### Mazmorras

Lo único de topología de mapas que el juego sí publica además de esos 2.223.
`tools/extract_dungeons.py` lo saca de `data_assets_dungeonsdataroot.asset.bundle`:

```
187 mazmorras
763 salas distintas   (759 están en MapPositions; 4 no, y no sabemos por qué)
159 mapas de entrada y salida
```

De esas 763 salas, **solo 17 tenían fila en `MapScrolls`**, así que casi todo es nuevo.

Dos avisos que hay que tener presentes:

- **`entranceMapId` y `exitMapId` NO son vecinos de borde.** El de entrada es el mapa de fuera
  donde está la puerta, y en 152 de las 187 la salida es ese mismo mapa. Meterlos en `MapScrolls`
  sería meter basura.
- **El orden de `mapIds` es el del dato y no está demostrado que sea el del recorrido.** La
  Biblioteca del Maestro Cuerbok lista sus tres salas en x = -14, -13, -15, que no avanza hacia
  ningún lado. `DungeonManager.NextRoom` lo sigue igualmente porque es lo mejor que hay, pero está
  escrito en el código que es una suposición.

Va a `world.db` en dos tablas, `Dungeons` y `DungeonRooms` (esta con la posición de cada sala), y
`Managers/DungeonManager.cs` las indexa y ofrece `OfRoom`, `NextRoom` y `WayOut`.

**No lo llama nadie todavía.** Es la base para pasar al jugador a la sala siguiente cuando gane y
para dejarlo en la entrada y en la salida; el combate sigue en la versión anterior del protocolo,
así que ese cableado espera a que se migre.

### Lo que el equipo suma a la hoja

El inventario que se reproduce (`ivx`) ya trae todo lo necesario:

```
ivx: f3 (repetido) { f1: hueco,
                     f5 { f1: plantilla, f2 (repetido) { f4: valor, f11: id de efecto },
                          f3: cuántos, f4: uid } }
```

Los huecos **0 a 15** son lo que se lleva puesto —amuleto, arma, los dos anillos, cinturón, botas,
sombrero, capa, mascota, los seis dofus y el escudo, uno en cada uno— y el **63** es la bolsa, con
los otros 593 objetos.

Qué hace cada efecto lo dice la tabla `Effects` de `world.db`: `Characteristic` y `BonusType`. Ese
`BonusType` es 1 o −1 y **no es decorativo**: el efecto 755 quita placaje y lleva un número
positivo, así que sumarlo a ciegas convertiría un malus en un bonus.

Todo eso va al **campo 7** de cada entrada del `kub`, que es el que el cliente enseña como "del
equipo". Salen 31 características con bono, incluidas las que faltaban: daños elementales y fijos,
empuje, críticos, resistencias, esquivas y retiradas, y el PA y el PM de los anillos exomagueados.

Y encima van los **bonus de conjunto**, que dependen de cuántas piezas de un juego se llevan a la
vez: `tools/extract_item_sets.py` los saca del volcado de dofusdude. Ojo con un detalle: el valor
de un bono de conjunto está en `diceNum`, no en `value`, que viene a cero en todos.

Comprobado entero contra el `kub` real de esa misma cuenta, **15 características de 15 clavadas**:
fuerza 745, vitalidad 4.270, sabiduría 261, inteligencia 245, potencia 249, las cinco resistencias
y la iniciativa en −398.

Para llegar ahí hicieron falta dos cosas más: los conjuntos, y **el amuleto**. Su hueco es el cero,
proto3 se salta el campo, y al leer el inventario el objeto llegaba sin posición y se daba por
guardado en la bolsa. Trece efectos que no contaban. Es el mismo fallo que impedía equiparlo, en
otro sitio: donde el hueco cero sea válido, el valor por defecto tiene que ser cero, nunca la bolsa.

Y una excepción: **la característica 0, la vida, no lleva bono de equipo**. Varios efectos apuntan
a ella y sumarlos daba veinticinco mil puntos de vida; el `kub` real no pone nada en su campo 7. La
vida es la base más la vitalidad y la calcula el cliente.

### Chat

```
C->S  ktm { f2: el texto, f3: el canal }
S->C  kti { f3: cuándo "2026-08-09T20:28:01+02:00", f4: quién, f5: su personaje,
            f6: su cuenta, f7: qué dijo, f8: {}, f9: el canal }
```

Canales, de la captura que los recorre todos de una sentada: 0 general (que se omite por ser cero),
1 equipo, 2 gremio, 3 alianza, 4 grupo, 5 comercio, 6 reclutamiento, y 9, 11, 16, 18 y 19 para el
resto. El mensaje privado es otro mensaje, `ktb`, y lleva a quién va dirigido; sin implementar.

Con un solo jugador no hay a quién repartirlo, así que vuelve a quien lo dijo — que es también lo
que hace el servidor real con tus propias líneas, y es lo que las hace aparecer en la ventana.

### Archimonstruos

Un monstruo es archimonstruo cuando es el `correspondingMiniBossId` de otro: los datos del cliente
emparejan cada monstruo corriente con su versión rara. Son **306** y los 306 declaran en `subareas`
la zona a la que pertenecen.

Cómo estaba el mundo antes de tocarlo:

```
39,9 % de los grupos llevaban al menos uno      35.518 colocados
hasta 8 en un mismo grupo                        5.802 mapas con más de uno
```

Las cuatro reglas: uno por grupo, uno por mapa, uno de cada diez grupos, y **uno de cada por zona**
— si el archimonstruo está en un mapa de su subárea, no está en ningún otro hasta que lo maten.
Resultado: **298 grupos**, uno por mapa y uno por zona.

Ojo con la aritmética: el 10 % no es lo que manda, manda la unicidad. Como solo hay 306
archimonstruos y cada uno puede estar en un sitio, el mundo entero tiene como mucho 306 por muy
alto que se ponga el porcentaje.

El sorteo es **determinista**, sacado del id del propio grupo, así que un mapa se ve igual después
de reiniciar. No se reescribe nada en la base de datos: los 38.744 grupos que trae se dejan como
están y se adelgazan al leerlos. Y el que pierde su sitio no se borra sino que **se degrada**: se
cambia por el monstruo corriente del que es la versión rara, así el grupo conserva su tamaño y su
nivel. `Archimonsters.Release(mapa)` suelta el que tenía un mapa, para cuando se implemente que al
matarlo pueda reaparecer en otro sitio de su zona.

### Hechizos

```
S->C  hms   f1 repetido { f1: rango, f3: id de hechizo, f4: 1 }      los que tiene
S->C  itg   f1 repetido { f2: hueco, f6 { f2: id } }                 la barra
C->S  itz   f2 { f2: hueco, f6 { f2: id } }, f3: 1                   cambiar un hueco
S->C  ivk   lo mismo, de vuelta
```

Los dos se construyen desde la base de datos: `SpellVariants` da los hechizos de cada clase y
`SpellLevels` a qué nivel se abre cada rango. Un Ocra de nivel 50 tiene 14; el Sacrogrito de nivel
154 de la captura tenía 36, y eran los que salían antes.

El otro `itg` de la captura, el de objetos, usa `f9 { f2: id de plantilla, f3: uid }` en lugar de
`f6`, y es como se distinguen: no hay ninguna marca en el mensaje que diga de qué barra se trata.

### Equipar y desequipar

```
C->S  iuk   f1: cuántos, f2: uid del objeto, f3: a dónde va
S->C  ivq   f1: uid, f2: dónde ha quedado
S->C  lym { f1: 206 }, hie { f1: 2 }, hii { f1: 2 }     iguales en todas las capturas
S->C  iun                                               los pods, que lo puesto también pesa
```

Posiciones, de las capturas y de una sesión del cliente real: **0 el amuleto**, 2 a 5 los anillos
y el cinturón, 6 el sombrero, 12 a 14 los dofus, y **63 la bolsa** (ahí va lo que se quita).

El amuleto merece su propia línea porque fue el único que no se podía equipar. Su hueco es el
**cero**, así que proto3 se salta el campo y el mensaje llega con el uid y nada más. Leer eso como
"no me ha dicho hueco" y contestar 63 mandaba todos los amuletos a la bolsa.

Lo que **no** hace todavía: cambiar las características. El bono del equipo va en el campo 7 de
cada entrada del `kub`, y rellenarlo exige saber qué objeto es cada uid, o sea que el inventario
salga de la base de datos y no de la captura. Hasta entonces el objeto se mueve y la hoja no lo
sigue: ni los daños, ni las resistencias, ni el PA/PM de los anillos exomagueados, ni los bonus de
conjunto.

### Lo que costó encontrar

**El bloqueo era un conflicto de identidad.** Con el `kva` de la captura el cliente entraba;
con el nuestro se quedaba con el reloj de arena. Recibía dos identidades contradictorias.

No vale un reemplazo byte a byte: el id capturado ocupa 6 bytes como varint y el nuestro 4,
así que descuadra todas las longitudes anidadas. `CaptureRewriter` desmonta el protobuf,
sustituye y recalcula las longitudes hacia arriba. Solo desciende a un campo si el valor
buscado está dentro, para no destrozar bloques binarios que no son submensajes.

El nombre del personaje capturado **no está en el código**: se lee del propio `kva` al arrancar.

### Mensajes identificados

| Opcode | Qué es |
|---|---|
| `kva` | Personaje seleccionado. `f1{f1{f1: detalles, f2: id}}`, con dos fechas más que el `kvi` |
| `jru` | Id del mapa, en el campo 2. Sustituirlo funciona |
| `kub` | Características. `f1`: experiencia, `f7`/`f8`: umbrales, `f10`: kamas, `f11` repetido: una entrada por característica `{f1: id, f4{f2: valor}}` |
| `kqo` → `kqy` | Latido cada 5 s. `kqy` es siempre `0801` |
| `kmv` + `jrh` | "Estoy en el mapa X", los dos con el id en el campo 1. Se contestan con `jss` |
| `jss` | Actores del mapa. `f2`: id del mapa, `f5` repetido: un actor |
| `lva` | Vacío. Fin de la lista de actores |
| `lqc` | Confirmación del cliente tras el bloque 1 |
| `ieo` → `idu` | **CORREGIDO 27/08/2026: son misiones, no elementos interactivos.** El cliente pregunta por qué paso va una misión y el servidor le contesta con el paso y sus objetivos. Lo que lo zanja es que cuadra solo: en las 448 tramas `idu` de las 401 capturas, el paso pertenece de verdad a la misión que nombran las 448 veces, y los 1.479 objetivos pertenecen de verdad a ese paso. Ver `docs/quests.md` |
| `hjk` | Ids de mapa empaquetados en el campo 1 |
| `jrw` → `jsj` | Andar. El cliente manda el camino, el servidor lo reparte a los demás |
| `jqi` → `jsq` | "Quiero salir del mapa" y el permiso, este en campo raíz 3 |
| `jqk` | "Llévame a este mapa", con el id en el campo 2 |
| `jsd` | Quitar un actor del mapa. `f2`: quién |
| `hms` | Los hechizos que tiene el personaje: `{f1: rango, f3: id, f4: 1}` |
| `itg` / `itz` → `ivk` | La barra de accesos directos, y cambiar un hueco de ella |
| `kum` / `kuh` | Repartir puntos y reiniciar la hoja |
| `iun` | Pods: `f1` lo que carga, `f3` lo que puede cargar |
| `iuk` → `ivq` | Mover un objeto a un hueco de equipo (o a la bolsa, que es la posición 63) |
| `lqu` | `f1`: 120, `f2`: el reloj del servidor en milisegundos |

### Lo que falta respecto a la captura

Comparando trama a trama la entrada al mundo con la captura oficial (391 mensajes), el
emulador manda 370. La diferencia:

| Opcode | Por qué falta |
|---|---|
| `kqg`, `jhe`, `jhh`, `jhk`, `hol`, `jgu`, `ihb` | A propósito: nombran a personas reales. Ver más abajo |
| `kub`, `irq`, `hms`, `itg` | No faltan: se sustituyen por los que construimos desde la base de datos |
| `lzl` (2 KB) | Sin identificar. Va delante de `jss` en la entrada al mundo, pero **no** en los cambios de mapa, así que no hace falta para cargar un mapa |
| `lvb` (`0807`), `hpm` | Sin identificar. Van detrás de `lva` en la entrada al mundo y tampoco aparecen en los cambios de mapa |

### Tres que estuvieron fuera por una identificación equivocada

`itg`, `ife` e `ivi` volvieron al cable. Lo que se decía de ellos no aguantó la comprobación:

- **`itg` sí es la barra de accesos directos**: dos mensajes, `f6` para hechizos y `f9` para
  objetos. Quitarlo es lo que dejaba la barra de hechizos vacía. El motivo era bueno — se iba a
  construir desde la base de datos — y el sustituto ya está escrito: el de hechizos se reconstruye
  y el de objetos se sigue reproduciendo mientras el inventario sea el de la captura.
- **`ife` no es la lista de amigos.** Los contactos están en `kqg`. `ife` son 180 entradas con
  nombres y siglas de gremios. El cuelgue que se le achacaba pudo ser real, pero iba pegado al
  mensaje equivocado.
- **`ivi` no es el inventario.** Son 9.694 pares `{id, valor}`, ids del 44 al 34352 y valores
  hasta 651 millones: tiene pinta de ser el contador de estadísticas y logros de la cuenta.

La lista de exclusión larga que había antes (`mft`, `idr`, `ivx`, `isw`, `irq`, `isd`, `jco`,
`hjk`, `jtg`, `koj`...) tampoco está: esos mensajes vuelven a salir.

### Estado

**Hecho en esta ronda**: contenedores del `kub` (y con ellos el botón de características),
característica 3, el `f3` de pergaminos, hechizos y barra desde la base de datos, andar y cambiar
de mapa (con el destino resuelto por nosotros, no por la conjetura del cliente), la subárea en el
`jss`, reparto y reinicio de puntos, equipar y desequipar.

**Cuidado al compartir**: `logs/gameserver_traffic*.log` guarda todo lo que ha salido por el cable,
y los ficheros anteriores al filtro de privacidad tienen dentro los nombres reales de la captura.
Vaciar `logs/` antes de empaquetar nada.

**Pendiente**, en orden:

1. Quitar el replay. Todo lo que sale de `world_etapa*.bin` son los datos de otra cuenta:
   sirve como referencia de qué espera el cliente, no como respuesta. Lo que ya se construye
   desde la base de datos es `kva`, `kub`, `jru`, `jss`, `irq`, `hms` y `itg`.
2. **Inventario desde la base de datos**, y con él **todo lo que el equipo suma a la hoja**: el
   campo 7 de cada entrada del `kub`. Ahora mismo no sube nada — ni daños elementales, fijos, de
   empuje o críticos, ni resistencias, ni el PA y el PM de los anillos exomagueados, ni los bonus
   de conjunto. Los ids de todas esas características ya están en `characteristics.json`; lo que
   falta es saber qué objeto es cada uid. Los objetos ya están en `CharacterItems`
   (`Uid`, `Gid`, `Quantity`, `Position`, `Effects`) y se cargan al entrar, pero el que se ve
   en el cliente es el de la captura. Falta identificar el mensaje: el candidato es `lwt`
   (153 entradas `{f1: id de plantilla, f2 repetido: efectos, f3: cantidad, f4: uid}`), pero
   **no lleva posición**, así que o los equipados viajan aparte o el mensaje es otro.
   De esto cuelga que el equipo sume en la hoja (campo 7 de cada entrada del `kub`) y que
   equiparse persista.
   `InventoryHandler` sigue entero en la versión anterior (`isi`, `iry`, `luy`, `kku`...) y con
   el envoltorio de campo 3.
3. **Elementos interactivos del mapa**, que es el agujero más grande que queda: sin ellos no hay
   casas, ni kanojedo, ni tabernas, ni zaaps, ni zaapis. Investigado pero **sin implementar**.
   Lo que ya se sabe:

   ```
   jss f11 { f1: 1, f3 { f1: uid de instancia, f2: id de habilidad },
             f5: id del elemento, f6: tipo }
   jss f15 { f1: 1, f2: casilla, f3: id del elemento, f4: estado }
   ```

   Y de dónde salen los datos: `mapData.interactiveElements` de los bundles de mapa da, por cada
   elemento, `m_interactionId` (el id), `cellId` y `gfxId`. **La casilla del bundle coincide con la
   que manda el servidor real 12 de 12**, así que esa parte es de fiar.

   Lo que falta es la **habilidad**: `f3.f2`. En las capturas solo hay 36 elementos en 16 mapas,
   muy poco para deducirla. El `gfxId` sí determina el tipo (25 de 26 llevan uno solo), así que la
   vía es cruzar `gfxId` con `skills.json` (368 habilidades, ya descargado, con `elementActionId`
   y `parentJobId`). No se implementó nada a medias a propósito: tocar el `jss` sin poder probarlo
   con el cliente arriesga romper la carga del mapa, que ahora funciona.
4. **El menú de clic izquierdo sobre el propio personaje** (darse una bofetada, reorientarse). Sin
   mirar: el actor del `jss` lleva en las capturas `f3.f1`, `f3.f5` (repetido, opciones) y `f3.f7`
   que nosotros no mandamos, y las opciones podrían salir de ahí.
6. Misiones: siguen siendo las de la captura, sus opcodes sin identificar.
7. El apodo de cuenta del título sigue siendo el capturado.
8. Sueños infinitos y merkasako infinitos y merkasako. Todos acaban en un cambio de mapa, que ya
   funciona, pero cada uno tiene además su propio mensaje; hay capturas de los cuatro.
9. NPC en `jss`.
10. Combate, todavía en la versión anterior del protocolo.
11. Sin identificar: `lzl`, `lvb`, `hpm`, `lqn`, y el `f4` y el `f9` del cuerpo del `kub`.

### Pruebas automáticas

`ConnectionProtocolSelfTest` corre al arrancar —**después** de cargar los bloques, que es de
donde lee parte de lo que compara— y contrasta `kqy`, `lva` y `jsq` byte a byte con la captura,
comprueba que las cinco características de contenedor raro (1, 23, 29, 47, 96) siguen donde
deben, y revisa la forma de los mensajes de la fase de conexión.

`tools/cliente_falso.py` recorre la sesión entera contra el emulador arrancado: bloque del mapa una
sola vez, `lva` detrás de `jss`, latidos contestados solo con `kqy`, contenedores del `kub`,
hechizos de la clase correcta, la subárea en el `jss`, `jqi` → `jsq` en campo raíz 3, el cambio de
mapa **con las dos ramas** (una conjetura que no existe y otra que sí), el reparto de puntos con
sus derivadas, que repetir el mismo `kum` no mueva nada, que un reparto imposible se rechace
entero, y el equiparse **incluido el amuleto sin campo de hueco**. Deja el `jss` en `jss_emu.hex`
para abrirlo con `tools/pcap.py`.

Y `tools/leak.py` después de cada cambio en lo que se reproduce.

### La consola del launcher

Si el panel de eventos se queda clavado, no es que no pase nada: es que
`ConsoleLogBuffer.GetLogsJson` ha producido un JSON que `LauncherService.GetLogs` no ha podido
leer. La ventana solo adelanta su cursor con lo que le dan, así que vuelve a pedir el mismo
lote roto para siempre. Pasó dos veces por lo mismo: caracteres de control crudos del volcado
de paquetes, y un subrogado suelto (que además hay que **sustituir**, no escapar: escapado el
documento parsea pero revienta al sacar esa cadena, después de haber recogido las anteriores).
Ahora el escape es completo y cada línea se lee por separado, así que una mala se pierde ella
sola.

Otro detalle: el trazador de paquetes monta cada línea con varios `Write` y la cierra con un
`WriteLine`. `InterceptWriter` los junta; quedarse solo con el `WriteLine` dejaba en la ventana
la cola de la línea y nada más.
