# World Editor — planificación de arquitectura

> Documento de arquitectura. Empezó como exploración, antes de que hubiera una línea escrita; las
> fases 0, 1, 2 y 3 ya están hechas y lo que aprendieron está anotado abajo, junto a lo que este
> documento decía y no se cumplió.
>
> **Decidido:** el armazón es **Avalonia**, el editor es un **ejecutable propio** que funciona sin
> servidor, la capa nuestra son **ficheros de texto en `content/`**, y el **lanzador hereda ese
> mismo armazón**.

---

## 1. Qué se quiere

Una herramienta que permita **crear contenido para el emulador sin tocar código ni regenerar
ficheros a mano**. En concreto:

| | |
|---|---|
| **Tráfico** | esnifar, ver el diálogo cliente-servidor en vivo, y registrar los paquetes que no sabemos atender |
| **Mapas** | pintar casillas —pisable, visión, bloqueada en combate—, ver vecinos, editar el decorado |
| **Interactivos** | crear elementos de cualquier tipo, sobre todo **teletransportes**, y **atarlos a otro mapa** (las casas) |
| **NPCs** | colocarlos, darles aspecto, acciones y **diálogos** |
| **Misiones** | crearlas de cero, con sus etapas, objetivos y recompensas |
| **Hechizos** | editar hechizos y sus efectos |
| **Monstruos** | colocar grupos, editar plantillas |
| **Lanzador** | y de paso, uno más profesional |

---

## 2. El problema de fondo, que hay que resolver antes que nada

Hoy los datos del emulador viven en **tres sitios que no se hablan entre sí**:

```
dofus3_data/          436 MB de volcado crudo del cliente. Verdad de Ankama. Sólo lectura.
   ↓  tools/*.py
datos/*.json          63 ficheros GENERADOS. Se pueden rehacer en cualquier momento.
bases/world.db        240 MB, 41 tablas. GENERADA. Se distribuye comprimida.
```

**Ninguno de los tres es editable a mano de forma segura.** Si alguien edita `datos/npcs_reales.json`
y mañana se vuelve a correr `tools/extraer_npcs_reales.py`, el trabajo desaparece sin avisar. Y
`world.db` es un binario de 240 MB: editarla es invisible en git, no se puede revisar en una pull
request y no se puede fusionar si dos personas tocan cosas distintas.

Esto ya ha pasado: la advertencia del README sobre el daño de empuje se perdió en un commit que
reescribía una sección, y nadie se enteró hasta que se buscó a propósito catorce días después.

### La decisión que lo desbloquea todo: tres capas y procedencia por fila

```
  capa 1   BASE        generada del cliente          se puede rehacer, nadie la edita
  capa 2   MEDIDA      aprendida de las capturas     se puede rehacer, nadie la edita
  capa 3   NUESTRA     decidida por una persona      SÓLO ESTO escribe el editor
                                                     nunca se regenera, siempre gana
```

Se fusionan al arrancar, en ese orden. Y **cada fila lleva de dónde vino**, que es exactamente lo
que hay que enseñar en la interfaz:

```
  MAPA         CASILLA  ORIENTACIÓN  PROCEDENCIA
  241438721    260      3            captura banque-20260820-091807
  241439745    246      3            decidido aquí, 24/08/2026
```

Sin esa columna, en seis meses nadie sabrá si un número es una medición o una invención, y ése es
justo el error que este proyecto lleva un año evitando.

### Dónde vive la capa nuestra

**Propuesta: ficheros de texto en un `contenido/` nuevo, versionado en git.** No en `world.db`.

Razones:

- Un diff de git legible es lo que permite que DragonLord —o cualquiera— mande contenido por pull
  request y se pueda revisar. Un blob de 240 MB, no.
- Dos personas pueden tocar mapas distintos sin pisarse.
- Es pequeño: lo que se decide a mano son cientos de filas, no millones.
- Si algo sale mal, se revierte un commit en vez de restaurar una base entera.

Formato sugerido, un fichero por dominio y por zona para que los diffs sean chicos:

```
contenido/
  npcs/         spawns.json, dialogos.json
  mapas/        celdas.json          (sólo las casillas CAMBIADAS, no las 560)
  interactivos/ elementos.json, teleports.json
  misiones/     *.json
  hechizos/     retoques.json
  monstruos/    grupos.json
```

**Regla dura: la capa nuestra guarda DELTAS, no copias.** Si un mapa tiene 560 casillas y se cambian
tres, el fichero lleva tres. Copiar el mapa entero hace que la próxima regeneración de la base no
llegue nunca a ese mapa.

---

## 3. Dónde vive el editor

**Decidido: un ejecutable propio, que sabe trabajar sin servidor y que llama a la puerta si lo hay.**

Es el tercer ejecutable de la casa, junto al lanzador y al servidor:

```
  Jondo Emulator Launcher.exe   la ventana del jugador
  Jondo Server.exe              el mundo
  Jondo Studio.exe              el editor            <- nuevo
```

Y funciona en **dos modos**, que es lo que lo hace cómodo:

| modo | qué hace | cuándo |
|---|---|---|
| **suelto** | abre `contenido/` y las bases, edita y guarda. No necesita que haya nada corriendo. | la mayor parte del tiempo |
| **enganchado** | además avisa a un servidor vivo de que recargue lo que acabas de tocar | cuando quieres verlo en el juego sin reiniciar |

El canal de administración es **fino a propósito**: no lleva la API de edición entera, sólo
«recarga este dominio». Todo lo que se edita pasa por los ficheros, y el servidor los relee. Eso
mantiene la superficie mínima y hace que el editor no pueda dejar al servidor en un estado que los
ficheros no expliquen.

Condiciones no negociables para ese canal:

- **Sólo `127.0.0.1`.** Nada de `0.0.0.0`, ni siquiera detrás de un cortafuegos.
- **Un testigo por arranque**, escrito en la consola del servidor, que el editor tiene que
  presentar.
- **Apagado por defecto**, y se enciende con un argumento (`--estudio`) o desde la ventana del
  servidor.
- **Puerto distinto del juego**, y ni una ruta de administración colgando del socket del juego.
- La guardia de regresión que ya barre el código buscando marcas de seguridad debería aprender una
  novena: que ninguna ruta de administración se registre sin comprobación de testigo.

---

## 4. Con qué se dibuja

El editor tiene que pintar **retículas isométricas de 560 casillas con capas de color, arrastre para
pintar en tandas, y grafos de diálogo**. Eso descarta WinForms, que es lo que hay hoy.

| | mapa isométrico | grafo de diálogo | multiplataforma | curva |
|---|---|---|---|---|
| **WinForms** | a mano sobre `Graphics`, doloroso | a mano, muy doloroso | no | ya se conoce |
| **WPF** | decente | decente | no | media |
| **Avalonia** | decente | decente | sí | media |
| **Web local** | `<canvas>`, es su terreno | librerías hechas | sí, gratis | media, pero conocida |

**Decidido: Avalonia.** Y la razón de peso no es el dibujo, es otra:

**No hay frontera de serialización.** El editor referencia `Jondo.Unity.World` y
`Jondo.Unity.Contract` como proyectos y usa `MapGeometry`, `Fighter`, `SpellEffect` y `Outcome`
**directamente**. Con una interfaz web, cada uno de esos tipos hay que espejarlo en JSON a mano y
mantener los dos lados sincronizados para siempre: el día que `SpellEffect` gane un campo —como
ganó `Delay` esta semana—, el editor se entera al ejecutarse, no al compilar.

Y de los ocho módulos de este documento, **siete manipulan objetos del dominio** y sólo uno pinta
píxeles. No compensa montar una capa HTTP entera por un módulo de ocho.

Lo que se pierde, y conviene saberlo de antemano:

- El pintado isométrico y el grafo de diálogos son **más verbosos** que en un `<canvas>`. Se hacen
  con `DrawingContext` sobre Skia y 560 rombos no son ningún problema de rendimiento, pero hay que
  escribirlos.
- **No hay herramientas de desarrollo de navegador** para inspeccionar la interfaz.
- Añade una dependencia de unos 30-40 MB al despliegue.

Lo que se gana además de los tipos: un solo lenguaje, una sola solución, un solo `dotnet build`, y
**el mismo armazón sirve para el lanzador**, que es la otra mitad del encargo.

Sobre el estilo: Avalonia admite XAML y también construir la interfaz **desde código**, que es como
está hecho el lanzador de hoy. Se puede empezar por ahí y no aprender XAML hasta que haga falta.

---

## 5. Los módulos, uno a uno

Para cada uno: **qué hay ya** —que es más de lo que parece— y **qué falta**.

### 5.1 Tráfico y paquetes desconocidos

**Ya hay.** `GameNodeProxy` ve todas las tramas. `Op.cs` sabe el nombre de cada opcode.
`Network/UnknownPackets.cs` ya deduplica lo desconocido por **firma de forma** del protobuf.
`logs/gameserver_traffic.log` guarda 108 MB de tráfico con hexadecimal. `tools/pcap.py` decodifica
capturas de Wireshark. `tools/timeline.py` pinta cronologías.

**Falta.** Un grifo en el proxy que emita cada trama por *server-sent events* al navegador, y una
vista de cronología con filtro por opcode, dirección y sesión. Y que el registro de desconocidos
pase de ser una lista en memoria a una tabla con: forma, cuántas veces, primera y última vez,
muestra en crudo, y un **estado** — desconocido → nombrado → documentado → atendido.

**La idea que hace esto valioso a largo plazo: la clave es la FORMA, no las tres letras.** Ankama
renombra los opcodes en algunos parches. Si el registro se guarda por `jxw`, todo el conocimiento
acumulado se evapora el día del parche. Guardado por firma de forma, **sobrevive**, y de hecho se
convierte en una entrada más para el emparejador de `protocolbuilder`: un mensaje que ya sabemos
identificar por su forma es un ancla gratis. Ese código ya existe y ya calcula la firma.

Y comparar en vivo contra una captura real: coger un opcode nuestro y el mismo de las capturas y
enseñarlos campo a campo. Es exactamente lo que se ha hecho a mano cinco veces esta semana.

### 5.2 Mapas y casillas

**Ya hay.** `MapGeometry` con la retícula, los vecinos y las distancias precalculadas.
`datos/map_walkable_cells.json` con 17.211 mapas, `map_fight_cells.json` con 17.222,
`map_neighbours.json` con las conexiones. `MapManager` los sirve.

**Falta.** Pintar. Cuatro capas independientes sobre la misma retícula —pisable, visión, bloqueada
en combate, bloqueada fuera de combate—, clic para alternar y arrastre para pintar una tirada. Y
saltar a los cuatro vecinos, que es como se recorre el mundo de verdad.

**Aviso**: el decorado —los 2.181 elementos de un mapa, cada uno con su gráfico y su matriz— es un
módulo aparte y mucho más caro. **No entra en la primera versión.** Se puede ver sin poder editarlo.

### 5.3 Interactivos y teleports

Éste es, con diferencia, **el de mayor valor por hora invertida**, y el que el propio emulador está
pidiendo a gritos: hoy declaramos los 3.719 pasajes con la habilidad del zaap (114) y tipo 0,
cuando el servidor real usa 184, 339 y 361 con sus propios tipos; y 1.010 de 1.124 pasajes que
faltan se descartan por no tener elemento de vuelta.

**Ya hay.** La tabla `InteractiveTeleports` con 3.815 filas, `TeleportManager`,
`datos/interactive_elements.json` con 9.840 mapas, `datos/tipos_interactivos_3.6.10.10.json`, y dos
catálogos de grafo de navegación.

**Falta.** Una vista de dos mapas lado a lado: elegir una casilla en uno, otra en el otro, elegir el
tipo y la habilidad, y **atarlos** — con la vuelta creada automáticamente, que es justo lo que falta
en los 1.010 descartados. Ésa es la pieza que hace posibles las casas con interior propio, los
pasajes nuevos y cualquier contenido personalizado que no exista en el mapa de Ankama.

### 5.4 NPCs, acciones y diálogos

**Ya hay.** 6.468 plantillas, 422 colocados donde los tiene Ankama en 202 mapas con casilla y
orientación de las capturas, `Npcs.cs`, `Vendors.cs`, `TokenShops.cs`, y `datos/npc_shops.json`.

**Falta.** Colocar uno nuevo con el ratón. Cambiarle la acción — y ahí hay algo que ya se aprendió
en este proyecto: **un mismo NPC se puede spawnear con acciones distintas**, así que la acción es
del *spawn*, no de la plantilla, y el modelo de datos tiene que reflejarlo.

Y los diálogos, que tienen un matiz importante y que la propia herramienta rival señala bien: **el
cliente guarda todas las frases que un NPC puede decir y todas las respuestas que se le pueden dar,
pero nunca cuál va con cuál.** Ese emparejamiento siempre ha sido del servidor. Es decir: el editor
de diálogos no es un lujo, es el único sitio donde ese dato puede existir.

Además, **la frase de apertura es por mapa**: el mismo personaje en dos sitios no tiene por qué
decir lo mismo.

### 5.5 Misiones

**Ya hay.** Los catálogos: `quests.json`, `quest_steps.json`, `quest_objectives.json`,
`quest_objective_types.json`, `quest_step_rewards.json`, `quest_categories.json`.

**Falta.** Todo lo demás. No hay ni motor de misiones ni tabla de progreso por personaje. Esto **no
es un módulo del editor, es una funcionalidad del servidor** que además necesita editor. Es el
apartado más caro de la lista y conviene tratarlo como proyecto propio, no como una pestaña más.

### 5.6 Hechizos y efectos

**Ya hay.** 17.113 hechizos, 34.823 niveles, `SpellLevels.EffectsJson`, el catálogo de efectos, y un
motor que no tiene ni un hechizo escrito a mano: todo sale de los datos.

**Falta.** Editar el `EffectsJson` con una interfaz en vez de a mano, y —lo realmente útil— una
vista que diga **qué efectos sabe aplicar el motor y cuáles caen en la rama de «sólo para el
panel»**. Hoy eso sólo se sabe leyendo `EffectEngine.cs`, y es la información que decide si un
hechizo funciona de verdad. El efecto 108, la curación, es el ejemplo: parece que funciona y no cura
a nadie.

Un simulador —lanzar un hechizo contra un objetivo de prueba y ver las consecuencias sin montar un
combate— vale más que el propio editor.

### 5.7 Monstruos y grupos

**Ya hay.** 5.134 monstruos, 38.744 grupos colocados, respawn, validación de radio 2 al aparecer.

**Falta.** Colocar un grupo a mano en un mapa concreto y elegir sus miembros. Y algo que la
medición de esta semana dejó claro: una vista de **qué monstruos no pueden hacer nada** —los 401 sin
hechizos, y los que tienen todo su arsenal fuera de alcance— porque son bugs de contenido que no se
ven jugando hasta que te toca uno.

---

## 6. El lanzador

Va aparte. Hoy es WinForms dibujado a mano, sólo Windows, y hace su trabajo: cadena de identidad por
cliente, ocho cuentas, registro embebido, tres idiomas.

Qué significaría «más profesional», en concreto y por orden de valor:

1. **Multiplataforma.** Hoy no arranca fuera de Windows.
2. **Actualización automática.** Hoy se distribuye a mano.
3. **Clasificación y estado del mundo** — cuánta gente hay conectada, quién va primero. Datos que el
   servidor ya tiene y no expone.
4. **Noticias o parte del servidor**, para contar qué ha cambiado sin escribirlo por Discord.
5. **Gestión de cuentas** decente: recuperación, cambio de contraseña, roles.

Con Avalonia decidido para el editor, el lanzador **hereda el armazón**: los mismos controles, el
mismo tema, el mismo modelo de ventana. Portarlo deja de ser un proyecto y pasa a ser una tarde,
porque lo difícil —la cadena de identidad, el arranque de ocho clientes, el registro embebido— ya
está escrito y no es código de interfaz.

Y resuelve el punto 1 de la lista de arriba de golpe: Avalonia corre en macOS y en Linux.

**No es urgente.** El lanzador actual funciona; el editor no existe. Va el último, pero cuando
llegue será barato.

---

## 7. Por dónde empezar

Ordenado por *lo que desbloquea*, no por lo que apetece.

| Fase | Qué | Por qué ahí |
|---|---|---|
| **0** ✅ | La capa de contenido y la procedencia por fila. Sin interfaz: sólo el cargador que fusiona las tres capas y un par de ficheros de ejemplo escritos a mano. | Nada de lo demás se puede guardar hasta que esto exista. Si se hace después, hay que reescribir todos los módulos. |
| **1** ✅ | El armazón y vistas de **sólo lectura** de mapas y NPCs. | Riesgo cero, valor inmediato: hoy para ver por qué un bicho no ataca hay que escribir un script de Python. Y valida el armazón antes de dejarle escribir nada. |
| **2** ✅ | Tráfico en vivo y registro de desconocidos por forma. | Reutiliza lo que ya existe y es lo que más acelera el trabajo del día a día. |
| **3** ✅ | Escritura: spawns de NPC, diálogos, grupos de monstruos. Las acciones, no: ver abajo. | El contenido más barato de crear y el que más se nota jugando. |
| **4** | Interactivos y teleports, con la vuelta automática. | Desbloquea casas y contenido propio. Podría adelantarse si eso pesa más. |
| **5** | Casillas de mapa. | Útil, pero sólo cuando ya haya contenido que colocar encima. |
| **6** | Hechizos, con el simulador. | |
| **7** | Misiones. | Proyecto propio: necesita motor de servidor, no sólo editor. |
| **8** | Lanzador. | |

---

## 7 bis. Lo que la fase 2 enseñó

Tres cosas que este documento daba por buenas y no lo eran.

### El registro de desconocidos llevaba meses sin apuntar nada

El apartado 5.1 decía «`Network/UnknownPackets.cs` ya deduplica lo desconocido por firma de forma».
Deduplicaba, sí, pero sobre nada: abría el sobre con `ExtractGameNodePayload`, que **sólo mira el
campo 3 de la raíz**, y con `GetMessageTypeUrl`, que mira el 1 y el 3. Las tramas del cliente van en
el campo **2**. Medido sobre las 72.879 tramas del registro de tráfico:

```
  raíz 1 → 1 → 1     56.073   el servidor diciendo algo
  raíz 2 → 1 → 1      8.974   el cliente pidiendo
  raíz 3 → 1 → 1        481   el servidor contestando
  raíz 1, suelto      4.605   un Any registrado sin su sobre exterior
  raíz 1 → 1             41   lo mismo, una capa más abajo
```

Así que cada paquete que pasaba por ahí entraba sin opcode y con el cuerpo vacío. Después de semanas
de juego la tabla tenía **dos filas, las dos «(sin opcode)» sobre un cuerpo vacío**. El despachador
no se enteró nunca porque él busca los opcodes como *texto* dentro de la trama, y eso funciona sea
cual sea el sobre.

La lección no es el fallo, es cómo se escondió: la única prueba que había era que el código hacía lo
que estaba escrito. Ahora hay cinco que corren contra el fichero de tráfico de verdad, y una de
ellas comprueba las dos direcciones por separado, que es justo lo que una cuenta total tapaba.

### La clave no puede ser sólo la forma

El apartado 5.1 decía «la clave es la FORMA, no las tres letras». Medido, no se sostiene tal cual.
Sobre las mismas 72.879 tramas: **834 parejas (opcode, forma)** entre **242 opcodes** y **664
formas**.

- **Sólo con la forma no vale.** Nada más 10 de las 664 formas las comparten varios opcodes — pero
  son las triviales (`(empty)`, `1:v`, `1:v,2:v`…) y entre ellas se llevan **180 de los 242
  opcodes**. Archivar por forma volcaría media protocolo en diez cajones.
- **Sólo con el opcode tampoco.** **59 de los 242** aparecen con más de una forma, y `jss` solo
  tiene **185**. Archivar por opcode escondería justo la variedad que se abre la lista para ver.

La clave es **opcode + forma**. Lo que la forma hace de verdad es *sobrevivir al parche*, pero no
siendo la clave: cuando Ankama rota los nombres, el emparejador estructural de `protocolbuilder`
saca la tabla de viejo a nuevo —de ahí salió `datos/mapeo_3.6.10.10_a_3.6.10.11.tsv`— y las claves
se reescriben con ella. Que una forma cuadre a los dos lados es lo que hace fiable ese mapeo.

Y hay una válvula: forma `*` significa «esto es sobre el opcode, lleve lo que lleve», que es la
manera sensata de decir algo sobre las 185 formas de `jss` de una vez.

### El grifo de tramas por HTTP no hacía falta

El apartado 5.1 pedía «un grifo en el proxy que emita cada trama por *server-sent events*». Es la
respuesta correcta cuando quien mira es un navegador. Aquí no lo es: el servidor **ya escribe todas
las tramas** en `logs/gameserver_traffic.log` —de ahí salen los 110 MB—, así que el grifo sería una
segunda copia de los mismos bytes, más un socket que asegurar, más un protocolo que mantener a
juego, más depender de que el servidor esté levantado.

Leer el fichero da tres cosas gratis que el grifo no tiene: **funciona con el servidor parado**,
**puede mirar lo que pasó antes de abrir el editor**, y **no añade superficie**. Lo que cuesta es un
sondeo en vez de un empujón, que para una persona leyendo una lista da igual.

Detalle medido y necesario: el registro se escribe desde dos sitios que no se ponen de acuerdo.
**27.565 de las 72.879 filas llevan prefijo de longitud** y el resto no. Leer sólo una de las dos
formas tira un tercio del fichero.

---

## 7 ter. Lo que la fase 3 enseñó

### Los textos SÍ se pueden leer, y eso cambia el editor de diálogos

El apartado 5.4 daba por hecho que un editor de diálogos trabajaría con números. Con números no
sirve: nadie puede decidir que la respuesta 6016 va debajo de la frase 3312 sin leer ninguna de las
dos. Resulta que el texto está a mano, por dos caminos distintos porque Ankama guarda las dos
mitades de forma distinta:

```
  una respuesta   dialogReplies [6016, 23739]  ->  Translations[23739]  ->  "Informarse sobre..."
  una frase       dialogData    messageId 6169 ->  NpcMessagesDataRoot  ->  Translations[...]
```

`world.db` ya lleva **339.175 traducciones** en la tabla `Translations`. La respuesta trae su clave
al lado del id y se resuelve sola. La frase no: su `messageId` es un id de `NpcMessageData`, y hace
falta pasar por `NpcMessagesDataRoot`, que son 16,8 MB del volcado. `tools/extraer_dialogos_npc.py`
lo destila a **55.037 parejas, 1 MB**, que sí se puede repartir.

Con eso, Snori Nairb deja de ser «3 mensajes y 39 respuestas» y pasa a ser una lista legible:
*«¡Alto ahí! Yo soy el que vigila esta ciudad...»* con *«¿Qué puedes contarme del conflicto entre
Bonta y Brakmar?»* debajo. Ahí ya se puede decidir.

### El árbol necesitaba estado de sesión, no sólo un fichero

El `ioy` con el que el cliente elige una respuesta trae **el id de la respuesta y nada más**: ni de
qué NPC viene ni de qué frase. Sin apuntar por dónde va la conversación no hay manera de saber a
qué línea lleva, y por eso el diálogo sólo podía tener una frase por mucho árbol que hubiera
escrito.

Va en el estado de sesión y no en un estático: con ocho clientes a la vez, un estático haría que la
respuesta de un jugador avanzara la conversación de otro.

### Las acciones por spawn no son lo que este documento decía

El apartado 5.4 sostenía que «un mismo NPC se puede spawnear con acciones distintas, así que la
acción es del *spawn*». Medido contra el cable, eso **no se puede hacer desde el servidor tal cual
está**: el menú del botón derecho lo pinta el cliente con el `actions[]` de la *plantilla*, y el
`f1` del `iov` es uno de esos números —cuadra en los 51 NPCs de tienda de la captura, 51 de 51—.
Un NPC que no declare la acción ni siquiera la ofrece.

O sea que una acción por spawn sólo puede **quitar** de lo que la plantilla ya declara, no añadir.
Añadir requeriría que la carga de mapa llevara acciones por actor, y eso hay que medirlo en una
captura antes de escribir una línea. Queda pendiente y marcado como tal, en vez de implementado a
medias.

### Los grupos de monstruos: dos números, no la resta

Detalle pequeño y real. El arranque decía «N grupos de content/» con la resta de puestos menos
quitados, y en la primera prueba de verdad —un grupo puesto y otro quitado— la resta dio cero y la
línea no salió. Justo el arranque en el que más falta hace ver que `content/` ha tocado algo.

---

## 8. Riesgos, y qué los desactiva

| Riesgo | Qué lo desactiva |
|---|---|
| Una regeneración borra el trabajo a mano | La capa nuestra nunca se regenera, y guarda deltas |
| `world.db` se convierte en el sitio donde se edita | Regla explícita: el editor **no escribe en `world.db`** |
| Se mezcla lo medido con lo inventado | Procedencia por fila, visible en la interfaz, no en un comentario |
| El editor queda expuesto | Localhost, testigo, apagado por defecto, guardia de regresión |
| El registro de opcodes muere en el próximo parche | Clave por **forma**, no por las tres letras |
| El editor se vuelve un segundo emulador | Sólo lee el estado del servidor; no reimplementa reglas de juego |
| Dos personas editan a la vez | Fuera de alcance: un solo usuario, y git resuelve los choques |
| Se empieza por lo vistoso —el decorado, el mapa— y se abandona | La fase 0 y la 1 no tienen nada vistoso, y son las que sostienen el resto |

---

## 9. Lo que NO debe hacer

- **No reimplementar el catálogo del cliente.** Los 21.748 objetos, los 17.113 hechizos y los 5.134
  monstruos son de Ankama y se leen. El editor edita **decisiones**, no hechos.
- **No convertirse en un panel de administración del juego en marcha** —dar kamas, teletransportar
  jugadores—. Eso son comandos, ya existen, y mezclarlo hace del editor un blanco.
- **No sustituir a `tools/`.** Los scripts de Python que extraen del volcado del cliente siguen
  siendo la forma de rehacer la capa base. El editor es la capa de encima.
- **No inventar datos que se pueden medir.** Si algo está en las capturas, se mide; el editor es para
  lo que Ankama no dice.

---

## 10. Lo que hay que decidir antes de escribir código

1. ~~¿Web local o Avalonia?~~ **Avalonia**, por los tipos compartidos. Lo único que lo giraría:
   querer abrir el editor desde otra máquina, o pasárselo a alguien sin instalarle nada.
2. ~~¿El lanzador comparte armazón?~~ **Sí**, se cae solo de la decisión anterior.
3. **¿La capa de contenido en JSON versionado o en una `estudio.db`?** La recomendación es JSON, por
   las pull requests.
4. **¿Interactivos antes que NPCs?** Depende de si pesa más poder construir casas o poblar el mundo.
5. **¿Las misiones entran en este proyecto o son otro?** Aquí se sostiene que son otro.
