# Mazmorras

Entrar, avanzar de sala, matar al jefe y salir. Medido sobre
`Mazmorras\mazmorra de los jalatós completa`, que es alguien recorriendo la Corte del Jalató Real
de punta a punta, y sobre el volcado del cliente.

---

## 1. Lo que hace Ankama

La captura enseña la mazmorra 1 entera. Once mapas y cinco combates, con un patrón que se repite
exacto cinco veces:

```
120063489   entrada, con el guardián (NPC 173, «Rotabla, el pastor»)
121373185   sala 0   ┐
121373190   pasillo  ┘ combate
121374209   sala 1   ┐
121374214   pasillo  ┘ combate
121375233   sala 2   ...
121373187   sala 3
121374211   sala 4   ← última: el jefe
121374216   pasillo
121375235   pasillo
120063489   de vuelta a la entrada, que en ésta es también la salida
```

**Y el orden de `DungeonRooms` es el orden real.** Eso llevaba años en duda —el propio
`DungeonManager` lo dice: la Biblioteca del Maestro Cuerbok lista sus salas en x = -14, -13, -15,
que no es un avance— y ahora hay una mazmorra contra la que comprobarlo. Las cinco salas de la
captura salen en el orden que da la tabla. Una de 187, así que la duda sigue en pie para las otras
186; pero ya no es sólo una suposición.

### La puerta

```
C->S  iov  {1:3, 2:120063489, 3:actor}     clica al guardián, acción «hablar»
S->C  ioc                                  se abre el diálogo
S->C  ios  {1: 646, ...}                   el guardián se queja de sus jalatós
C->S  ioy                                  el jugador contesta
S->C  ios  {1: 17040, ...}                 «¿Seguro que quieres utilizar el manojo de llaves
                                            para entrar?»
C->S  ioy                                  contesta que sí
S->C  kld                                  se cierra el diálogo
S->C  iun                                  se gasta la llave
S->C  jru  {2: 121373185}                  dentro, sala 0
```

La frase 17040 no la declara ningún NPC: la pone el servidor, igual que las de misión.

---

## 2. Los datos que faltaban

`tools/extract_dungeons.py` **tiraba la mitad de lo que hacía falta**. El volcado del cliente traía
desde el principio `availableOnKeyring`, `requiredObjects`, `achievements`,
`availableInAutomaticGroupSearch` y `availableInLobby`, y el extractor se quedaba con ocho campos y
descartaba el resto. Por eso no había forma de cerrar una mazmorra con llave: el dato existía y no
llegaba.

Ampliado el extractor, de las 187:

| | |
|---|---|
| piden una llave | **126** |
| aceptan además el manojo | **107** |
| declaran jefe | **126** |
| objetos distintos usados como llave | 129 |

La 1 pide el objeto **1568, «Llave de la Corte del Jalató Real»**, y el manojo es el **10207,
«Manojo de llaves»**.

---

## 3. Lo que hace este servidor

`DungeonManager` existía —226 líneas— y **no lo llamaba nadie**; su propio comentario lo decía.
Ahora está conectado.

**Entrar.** Hablar con un NPC que esté en el mapa de entrada de una mazmorra y contestarle:
comprueba el nivel mínimo, busca la llave en la bolsa —la suya primero, el manojo después—, se la
gasta y teletransporta a la primera sala. 53 de las 187 entradas tienen ya un guardián colocado, y
los nombres cantan: «Guawdia wabbit», «Guardián koalak», «Discípulo de Ugah».

**Avanzar.** Ganar un combate en una sala mueve a la siguiente. En la última, a la salida.

**Los grupos.** Cada sala tiene UN grupo de ocho, compuesto al arrancar con los monstruos propios
de la subzona de la sala (`Subareas.Monsters`, sin los jefes de la mazmorra), y el combate coge los
primeros `clamp(jugadores, 4, 8)`: cuatro para uno a cuatro jugadores, y uno más por cada jugador
desde el quinto. Es lo que enseña la captura `Mazmorras/mazmorra de los jalatós completa`: el `jss`
de cada sala lleva el grupo de ocho y sus alternativas por número de jugadores (1 → 4, 5 → 5 … 8 → 8),
cada una los primeros N de los ocho, y el sacrógrito que entró solo peleó contra cuatro en las cinco
salas. Los cuatro primeros son cuatro especies distintas; los monstruos de la sala k van al grado k
(de 1 a 5 en esa mazmorra; en las demás, repartidos linealmente entre los grados). El mapa manda el
grupo con sus alternativas, idénticas byte a byte a las de la captura (`DungeonGroupSizeTests`).

**El jefe.** En la última sala de las 126 mazmorras con jefe declarado, el jefe encabeza el grupo, una
sola vez y a su grado más alto, con siete de los monstruos de la mazmorra detrás.

**Al ganar**, la sala se vuelve a componer igual —su jefe incluido— en vez de repoblarse al azar.

---

## 4. En qué se diferencia de Ankama, y por qué

**No hay pasillos y no se anda entre salas: se teletransporta al ganar.** No es una elección de
diseño, es lo único que la topología aguanta: **ninguna de las 187 mazmorras tiene ni uno solo de
sus pasajes internos**, ni en la tabla extraída ni en el propio grafo de mundo de Ankama. A un
jugador puesto en la sala 0 no le quedaría por dónde salir.

**Cualquier combate ganado en una sala avanza.** Cada sala tiene un solo grupo, así que ganarlo es
limpiarla.

**Cualquier respuesta al guardián entra**, porque el árbol de diálogo de esos NPCs no está escrito.
La frase de confirmación existe y es suya; ponerla es trabajo del editor.

**El grupo crece durante la colocación**, como en el juego. Cada vez que alguien entra al combate de
una sala, el bando de los monstruos se rehace entero con los primeros `clamp(jugadores, 4, 8)` de sus
ocho y con ids nuevos, aunque el número no cambie: es lo que enseña `Busqueda grupo/busqueda
automatica de grupo...`, cuatro jugadores entrando uno a uno al combate 471, con los -1..-4 quitados
(jzw) y los -5..-8 puestos (kae) a la segunda llegada, y así hasta los -13..-16. Ver
`Handlers/FightJoin.cs` y `docs/fight.md`.

---

## 5. Un fallo que había y ya no

`MobSpawnManager` arrancaba **antes** que `DungeonManager`, y lee `DungeonRooms` para no vaciar las
mazmorras con el veto de «no hay monstruos bajo techo» —753 de las 763 salas están marcadas así—.
Como `DungeonManager` es quien escribe esa tabla, lo que el sembrador leía era **lo que dejó escrito
el arranque anterior**. En un mundo estable no se nota; el día que cambia la lista de salas, sí.
Ahora van en el orden correcto.

---

## 6. Lo que falta

- **Los pasajes internos.** Es lo que separa esto de la mazmorra de verdad. Son ~1.800 puertas y el
  editor de pasajes ya sabe ponerlas; lo que no hay es de dónde sacarlas automáticamente.
- **El árbol de diálogo del guardián**, con la confirmación y un «no, gracias» que no entre.
- **Las oleadas** de algunas mazmorras nuevas (Despedazadora, Venerable, Bzupervibzor) y el altar de caza.
- **Los retos de mazmorra**: 684 de los 842 están marcados `solo_mazmorra` y no se ofrecen nunca,
  porque nada le dice al combate que está dentro de una. Ahora `DungeonHandler.IsBossRoom` y
  `DungeonManager.OfRoom` sí lo saben.
- **El emparejamiento automático** y el vestíbulo. No hay nada, y los datos ya traen las banderas.
