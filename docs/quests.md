# Misiones

Cómo se coge una misión, cómo avanza y cómo termina. Todo lo que hay aquí sale de medir las 401
capturas de Wireshark y el volcado del cliente; lo que no está medido se dice que no lo está.

---

## 1. Los seis opcodes

```
S->C  ief  {1: mision}                                   arranca
C->S  ieo  {2: mision}                    ->  S->C  idu  el paso y sus objetivos
C->S  idw  {1: mision, 2: objetivo}                      el cliente da un objetivo por hecho
S->C  idz  {1: mision, 2: paso}                          el servidor valida el paso
C->S  iec  {1: mision}                                   pregunta por una misión suya
```

Las direcciones están comprobadas **por puertos**, no sólo por el campo raíz del sobre.

### Por qué se sabe que son misiones y no otra cosa

Los documentos de este repositorio archivaban `ieo`/`idu` como la pareja de elementos interactivos
y `idz`/`idw` como «extras de conexión». Era una suposición vieja, del mismo tipo que la que puso
nombres de misiones a `lry`, `isf`, `lol` e `izu`, que **no aparecen en ninguna de las tres capturas
de misiones**.

Lo que lo zanja no es que los números «parezcan» ids de misión —un número pequeño lo parece por
casualidad— sino que **cuadran entre sí**:

| | |
|---|---|
| Tramas `idu` en las 401 capturas | 448 |
| …cuyo paso pertenece de verdad a la misión que nombran | **448 de 448** |
| Objetivos dentro de ellas | 1.479 |
| …que pertenecen de verdad a ese paso | **1.479 de 1.479** |
| Tramas `idz` coherentes | 21 de 21 |
| Tramas `idw` coherentes | 16 de 16 |

448 parejas (misión, paso) sacadas de una lectura equivocada del formato no salen coherentes.

### El campo que significa lo contrario de lo que parece

En cada objetivo de un `idu`, el campo 4 vale 1 o no está. Lo obvio sería que 1 fuese «hecho». Es al
revés. Siguiendo el paso 2249 a lo largo de la captura del tutorial:

```
[(9655, 1)]                                        9655 es lo que hay que hacer
[(9655, ·), (9656, 1)]                             9655 hecho, ahora el 9656
[(9655, ·), (9656, ·), (9657..9661, 1)]            los dos hechos, cinco más
```

**La marca abandona al objetivo que se cumple.** Y varios pueden estar pendientes a la vez, así que
un paso no se puede modelar como un puntero dentro de una lista.

`Jondo.Unity.Tests/Protocol/QuestProtocolTests.cs` compara byte a byte lo que construye este
servidor con esas tramas.

### Una diferencia a propósito

El servidor de Ankama manda un **prefijo creciente** de los objetivos del paso: el 3183 declara
cuatro y la captura enseña dos, luego cuatro. Éste los manda todos de una vez, porque cuáles
considera Ankama «revelados» no está en ningún dato que tengamos, y enseñar de más es menos malo que
esconder un objetivo que hace falta.

---

## 2. Cómo se coge

El enganche es el diálogo, y está en los datos del propio cliente: **un paso de misión declara la
frase de NPC con la que se entrega**.

```
quest.startPosition  ->  npcId + mapId     quién la reparte y dónde
step.dialogId        ->  id de frase       la frase exacta con la que se da
```

**1.260 de los 2.225 pasos** traen `dialogId`, y **los 1.260 resuelven a texto de verdad**. De ésos,
1.177 pertenecen a una misión que arranca en un NPC con nombre y mapa.

La captura `Misiones\hablar con NPC y aceptar una mision` enseña la cadena entera: el cliente abre
el diálogo en el mapa 212863492, el servidor lo baja hasta la frase 50071, el jugador elige la
respuesta 66788, y **entonces** sale `ief {2432}`. La misión 2432 dice que la reparte el NPC 6617 en
el mapa 212863492 y su único paso declara `dialogId 50071`. Tres números independientes, una sola
historia.

**Después de elegir la respuesta, no al llegar a la frase.** Ése es el orden de la captura y es el
que copia el motor.

### Cuál de las respuestas es la que acepta

**No se puede sacar de las capturas.** La respuesta que daba la misión llevaba un campo extra, pero
ese campo sale en **184 de las 429 respuestas capturadas** y casi ninguna es de misión: no es una
marca de misión.

Lo dice el árbol, con `startsQuest`. Antes de eso, cualquier respuesta de la frase arrancaba la
misión —también «No, gracias»—, que es lo que pasa todavía en las frases sin árbol escrito. Ver la
sección 7.

---

## 3. La condición de arranque

Ankama la escribe como una cadena por misión. La gramática está medida sobre las 1.976:

```
condicion := termino | condicion '&' condicion | condicion '|' condicion | '(' condicion ')'
termino   := OP CMP VALOR (',' VALOR)*
OP        := dos letras            29 distintos
CMP       := '=' | '!' | '>' | '<'
```

Tres cosas fáciles de equivocar, y las tres comprobadas:

- **«Distinto» es `!` a secas, nunca `!=`.** `Qa!496` es «la misión 496 no está en curso». No hay ni
  un `!=` en todo el fichero, y tratar el `!` como ruido invertiría 236 condiciones.
- **Hay paréntesis y anidan hasta tres niveles.** 170 misiones los usan.
- **La precedencia da igual en la práctica.** 168 condiciones mezclan `&` y `|` y todas las mezclas
  van entre paréntesis. `&` liga más fuerte, como en C, que es la lectura que concuerda con las 168.

Y dos rarezas de Ankama que hay que leer sin atragantarse: **`E` como quinto comparador** (2 usos,
`POE14271` y `POE11563`) y **un valor con letra**, `PJ>a,199` (1 uso).

### Lo que sabe juzgar y lo que no

Seis operadores: `PL` nivel, `Qf` misión terminada, `Qa` misión en curso, `Qc` terminada también,
`Qo` objetivo cumplido, `Pm` mapa actual. Cubren **todos** los términos de 935 de las 1.976
condiciones.

`Qc` se lee como «terminada» por lo que aparece a su lado: `(Qa=890|Qc=890)` es «la 890 está en curso
o ya se hizo». `Qo` lleva ids de objetivo, 116 de 116.

Lo que no entiende —alineamiento, gremio, banderas de servidor— **lo deja pasar y lo dice**.
Rechazarlo dejaría el 53% de las misiones fuera del alcance de cualquiera, que es peor respuesta que
ofrecerlas antes de tiempo. Los términos que sí entiende se siguen exigiendo.

### La cadena

990 misiones exigen otra antes, y ahí está la cadena de Astrub tal cual:

```
mision 56  Ps=1&Pa=1&PL>29&Qf=55
mision 57  Ps=1&Pa=2&PL>29&Qf=56
mision 58  Ps=1&Pa=3&PL>29&Qf=57
```

---

## 4. Cómo se cumple un objetivo

Hay 18 tipos. El motor los cierra por dos caminos:

**Lo dice el cliente** (`idw`). Los de tipo 0 son texto libre —5.670 de los 15.547— y piden pulsar
algo de la interfaz, de lo que el servidor no se entera nunca. Se le cree, y el riesgo se acota en
`QuestLog.Tick`: sólo acepta un objetivo **del paso en el que el personaje está de verdad**, así que
lo peor que puede hacer un cliente mentiroso es terminarse una misión que ya tiene, en el orden en
que esa misión está escrita.

**Lo cuenta el servidor** (fin de combate). Tres tipos nombran un monstruo, y en los tres
`parameter0` es el monstruo y `parameter1` cuántos:

| Tipo | Qué | Cuántos |
|---|---|---|
| 6 | vencer N en un solo combate | 776 de 788 con monstruo real |
| 14 | vencer N, acumulando entre combates | 143 de 143 |
| 16 | vencer N en un mapa concreto, en un combate | 88 de 88 |

Las invocaciones no cuentan. Están en el bando contrario con `IsMonster` puesto, y este proyecto ya
tropezó dos veces con eso: un monstruo invocador estaba pagando kamas por criaturas que se fabricaba
él mismo.

---

## 5. Lo que se guarda

```sql
CREATE TABLE CharacterQuests (
    CharacterId INTEGER NOT NULL,
    QuestId     INTEGER NOT NULL,
    StepId      INTEGER NOT NULL DEFAULT 0,
    Objectives  TEXT    NOT NULL DEFAULT '',
    Completed   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, QuestId)
);
```

`Objectives` son dos mitades separadas por una barra: los objetivos ya hechos, y luego los que van a
medias con su cuenta. `18390,18391|18392:3`.

Se escribe **en el momento del cambio**, no al salir. En este servidor no hay guardado periódico y
`SaveCurrentCharacter` sólo escribe la fila de `Characters`, así que lo que espere al logout se
pierde en un cierre feo — y perder una misión de una tarde es peor que perder unos kamas.

---

## 6. El diario de otro

El bloque que el servidor reproduce al entrar al mundo llevaba **261 tramas `idu`**: el diario de
misiones entero de la cuenta que se capturó. Todo el que entraba veía las misiones de un
desconocido, y desde que hay motor además contradecían a lo que el servidor cree.

`idu` está ya en `WorldEntry.NotReplayed`, y en su lugar va el diario del personaje que se conecta.

---

## 7. Los árboles de diálogo

Sin árbol, el servidor sólo sabe mandar **la primera frase que declara la plantilla del NPC**, con
todas sus respuestas de golpe. Snori Nairb ofrece las treinta y nueve suyas a la vez, ninguna lleva
a ningún sitio, y la frase donde se entrega la misión no se alcanza nunca.

De los 1.260 pasos que se entregan hablando:

| | |
|---|---|
| Alcanzables sin árbol (la frase es la primera de la plantilla) | **21** |
| La frase existe en la plantilla pero no es la primera | 64 |
| **La frase ni siquiera está en la plantilla** | **1.092** |

### De dónde salen: dos fuentes, y la segunda es mejor

Hay dos maneras de recuperar el emparejamiento «qué respuesta va en qué frase», que es lo único que
nunca salió del servidor de Ankama.

#### a) Las guías de dofuspourlesnoobs — sirvió para Astrub

Escriben las respuestas del jugador en francés, entre comillas francesas. Funciona por dos cosas
medidas, no supuestas:

- **Dentro de un mismo NPC, el 98,3% de sus frases se identifican por su texto.** En todo el juego
  sólo es el 70% —«Hasta luego.» lo dicen cientos— pero una conversación es con un NPC.
- **Los ids de respuesta no tienen que ser los de Ankama.** El 36,4% de las respuestas de un NPC
  comparten texto con otra suya, y da igual: el servidor manda los ids y el cliente devuelve el que
  le dieron. Vale cualquiera con el texto bueno mientras el árbol sea coherente consigo mismo.

`tools/dialogue_from_guide.py` empareja frase → id. `tools/build_dialogue_trees.py` monta el árbol
y lo escribe. Los hechos a mano llevan `_byHand` y no se pisan.

**Cuidado con la caché de la web:** se la pilló sirviendo la página de otra misión bajo la misma
URL, dos respuestas del mismo tamaño y distinto contenido, **y el cambiazo depende del
user-agent** — con curl pelado sale una misión y con user-agent de Chrome otra. Se llegó a ver una
respuesta cuyo `<title>` era el bueno y cuyo encabezado del cuerpo era de otra misión, así que
comparar títulos no basta: hay que mirar el encabezado del cuerpo. La protección de fondo es que
las respuestas se emparejan contra el NPC que da la misión: si la página es otra, no casa ninguna.

#### b) La conversación del propio cliente — hizo falta para Incarnam

**Para Incarnam la guía no vale.** Se bajaron las 24 páginas y se leyeron dos veces cada una: las
24 existen, pero **21 no imprimen ni una sola opción de respuesta** —son prosa narrativa, «Parlez à
Berb Nhin», «Ramenez les Orties»— y de las tres que sí, dos atribuyen las respuestas a un NPC
distinto del que da la misión. No es un fallo de extracción: se buscó en el HTML crudo las comillas
francesas, `<i>`, `<em>` y `font-style:italic`.

La fuente buena estaba en casa. El cliente trae:

| | |
|---|---|
| las frases que declara la plantilla del NPC | con su texto |
| **todas** las respuestas que ese NPC puede dar | con su texto |
| **el texto de la frase que nombra cada paso** | aunque la plantilla no la declare |

O sea todo menos el emparejamiento. Y los textos se contestan entre sí en francés corriente: la
frase 20877 dice que la caporal Mynerve espera en lo alto de la torre, y la respuesta 25045 dice
«Accepter d'être mis à l'épreuve et se diriger vers l'escalier». Eso se lee y se escribe.

`tools/npc_conversation.py <npc>` vuelca eso, y `--category 19` lo hace de todos los que reparten
misiones de una categoría. `tools/merge_authored_trees.py` comprueba el árbol escrito contra la
plantilla y el catálogo antes de dejarlo entrar, porque escribir a mano gana en fidelidad y pierde
lo único que da un generador: no poder equivocarse de lectura. Comprueba que la respuesta sea del
NPC, que no se repita, que todo `next` caiga en una frase del árbol, que la frase sea real, que la
entrega esté donde el paso dice, que ninguna respuesta se esconda a sí misma, que quede una salida,
y que colocadas + descartadas den todas las del NPC.

Estos árboles se guardan con `_byHand`, como los escritos con el editor.

### Para qué es cada respuesta

| | |
|---|---|
| `startsQuest` | esta respuesta **da** la misión |
| `quest` | sólo se ofrece con esa misión en curso |
| `step` | y sólo en ese paso |
| `afterQuest` | sólo una vez terminada |

`startsQuest` va aparte de `quest` y tiene que estarlo: marcar como «de la misión» la respuesta que
la **empieza** la escondería hasta tenerla, y entonces no la podría coger nadie.

Antes de esto, **cualquier** respuesta de la frase daba la misión, así que «No, gracias» también.

## 8. La marca verde sobre el NPC

Es el opcode `iom`:

```
1 { 2 (repetido) { 2: <ids de misión empaquetados>, 4: actor }, 3: mapa }
```

Los **294 números** que llevan las 380 tramas capturadas son ids de misión, los 294. Y se ve
apagarse: en el tutorial un actor llega con `[2511]` y más tarde el mismo actor llega con la lista
vacía, que es justo cuando se coge. **235 de las 380 van vacías** — así se borra la marca.

Se manda al llegar a un mapa y otra vez al coger una misión. **Todos los NPCs del mapa se nombran**,
también los que no tienen nada: dejar uno fuera no dice nada de él y el cliente seguiría pintando lo
de la última vez.

### Se marca lo que se puede coger, no lo que el catálogo promete

El catálogo nombra repartidor en **1.958** parejas misión/NPC. De ésas, sólo **70** se pueden
entregar con una conversación que este servidor sepa tener: 43 porque hay un árbol escrito que las
da, y 27 porque la frase que el paso nombra resulta ser la de apertura de la plantilla. Marcar las
otras 1.888 pondría una marca verde sobre casi todo el mundo que **no se apaga nunca**, por mucho
que hable el jugador, porque a esa frase no se llega.

Así que la marca sale de `QuestsOfferedBy`, no del catálogo. Y va en los dos sentidos: el árbol
también puede ofrecer una misión que el catálogo no le asigna a nadie —hay **155 sin repartidor**, y
«Mort au rat !» es una de ellas aunque el tabernero Grobid declare la respuesta «Dire que vous avez
vu l'affiche placardée dehors», que es justo el cartel con el que empieza.

Cuidado con una consecuencia que muerde: **escribir un árbol le quita al NPC las misiones que no
marques en él.** Sin árbol vale cualquier respuesta de la frase que el paso nombra; en cuanto hay
árbol, `NpcHandler` deja de preguntarle al catálogo y sólo da lo que lleve `startsQuest`. Un árbol
que se lleve la frase de entrega sin marcar nada en ella deja una conversación impecable que no
entrega nada. Lo vigila `AuthoredDialoguesTests`.

## 9. Lo que falta


- **La experiencia y los kamas de las recompensas.** Los objetos sí se dan —son exactos, y 5.582 de
  las 6.707 recompensas llevan alguno— pero la XP y los kamas son **multiplicadores**: 2, o 1,2, o
  0,035. Un multiplicador sobre una base que no tenemos. Se escribe en el registro y no se paga,
  porque inventar la fórmula pondría en pantalla un número que parece bueno y no lo es.
- **Editar misiones.** El Studio las enseña; no las escribe.
- **Los objetivos de recolectar, fabricar y escoltar.** Tipos 2, 3, 12 y 17.
- **`repeatLimit`**, que necesita contar cuántas veces se ha hecho una misión y eso no se guarda.
