# Documentación técnica Jondo — Parte 2: mapas, actores y movimiento

**Versión del cliente:** Dofus 3.6.4.3
**Alcance:** desde que el cliente pide un mapa hasta que el personaje se mueve, cambia de mapa y colisiona con un grupo de monstruos.
**Continúa:** [DOC_01_ARRANQUE_Y_CONEXION.md](DOC_01_ARRANQUE_Y_CONEXION.md)

---

## 1. Modelo de datos

### 1.1. Fuentes

| Fuente | Contenido | Filas |
|---|---|---|
| `world.db` → `MapPositions` | `MapId, PosX, PosY, SubAreaId, Outdoor, Name` | 15.360 |
| `world.db` → `MapTemplates` | `Id, SubAreaId, Data` (JSON con `m_flags`, `posX`, `posY`, `worldMap`) | 15.360 |
| `world.db` → `MapScrolls` | `MapId, RightMapId, BottomMapId, LeftMapId, TopMapId` | — |
| `world.db` → `NpcSpawns` | NPC por mapa: `MapId, NpcId, CellId, Orientation, BoneId, Look` | — |
| `world.db` → `MapMobs` | grupos de monstruos: `MapId, MobId, CellId, MembersJson` | 38.765 |
| `C:\Jondo\map_walkable_cells.json` | celdas transitables por mapa | 17.211 mapas |

### 1.2. Qué carga `MapManager.Initialize()`

Tres diccionarios en memoria:

```csharp
Dictionary<long, MapInfo>         Maps            // posición, subárea, outdoor, nombre, flags
Dictionary<long, MapScrollAction> ScrollActions   // mapas vecinos en las 4 direcciones
Dictionary<long, List<int>>       WalkableCells   // celdas transitables
```

`MapInfo.Flags` sale del campo `m_flags` del JSON de `MapTemplates`. Se usa para identificar arenas de combate (§8).

> **Parche a documentar:** la subárea **444 se reescribe a 20663** en dos sitios (`MapManager.cs:87` y `MapLoadHandler.cs:59`). Es un apaño para que el cliente no rechace esa subárea; no está explicado en el código y conviene revisarlo.

---

## 2. Geometría del tablero

Un mapa de Dofus son **560 celdas** numeradas de 0 a 559, en una **rejilla isométrica escalonada**: las filas van alternadas media celda, no es una cuadrícula plana.

### 2.1. Regla de adyacencia (deducida de datos reales)

Extraída de **451 pares de celdas consecutivas** de 111 caminos (`joo`) presentes en 19 capturas. Como un camino expandido tiene por definición celdas adyacentes, la frecuencia de los deltas da la regla:

| Paridad de `cell / 14` | Los 8 vecinos |
|---|---|
| **PAR** | `-28, -15, -14, -1, +1, +13, +14, +28` |
| **IMPAR** | `-28, -14, -13, -1, +1, +14, +15, +28` |

Son las 8 direcciones de Dofus: los deltas de fila ±1 (`±13/±14/±15` según paridad) son las diagonales de pantalla, `±1` el desplazamiento horizontal y `±28` el vertical.

**Cuidado con el borde:** `+1` desde la columna 13 salta a la columna 0 de la fila siguiente, y no es un vecino real. Hay que filtrar `±1` cuando `cell % 14` vale 13 o 0 respectivamente.

### 2.2. ⚠️ El código actual usa la geometría incorrecta

Tres funciones tratan el tablero como una cuadrícula plana `(fila = cell/14, columna = cell%14)` con distancia Manhattan:

| Función | Archivo | Consecuencia |
|---|---|---|
| `MonsterAI.GetManhattanDistance` | `MonsterAI.cs:143` | los alcances de hechizo se calculan mal (la mitad de los pasos cuentan como 2) |
| `MapManager.GetNearestWalkableCell` | `MapManager.cs:230` | la celda "más cercana" no siempre lo es |
| `MapChangeHandler.GetTransitionSpawnCell` | `MapChangeHandler.cs:174` | la celda de aparición al cambiar de mapa se calcula sobre coordenadas falsas |

Lo correcto es una clase `MapGeometry` con la tabla de vecinos de §2.1 y **distancia por BFS** (matriz 560×560 precalculada al arrancar: ~313k operaciones, instantáneo). Está detallado como tarea D0 en [PLAN_COMBATE_V3.md](PLAN_COMBATE_V3.md).

---

## 3. Carga de mapa — el ciclo `kkr`

Cuando el cliente entra en un mapa (o vuelve de un combate) pide su contenido con **`kkr`**. Lo atiende `MapLoadHandler.HandleMapLoadRequest`.

> Si `GameState.IsInFight` está activo, la petición se desvía a `FightHandler.HandleFightMapLoad` y se sirve la ráfaga de combate en su lugar.

### Secuencia de respuesta

```
Cliente → kkr { f1 = mapId }        (también acepta joi como disparador alternativo)

Servidor → lxd   (wrapper vacío)
         → jpv   (el contenido del mapa: subárea, mapId y TODOS los actores)
         → lsy   { f1 = subAreaId, f3 = 45 }
         → kns   { f1 = true }
```

Antes de construir nada, el servidor **fija la posición**: toma `GameState.CellId` (o 344 por defecto) y lo pasa por `GetNearestWalkableCell` para no dejar al personaje en una celda no transitable.

### 3.1. Estructura de `jpv`

```
jpv { f1  = <subAreaId>,
      f4  = <mapId>,
      f12 = { f1 = <subAreaId> },
      f15 = <actor>,        ← repetido: uno por actor del mapa
      f15 = <actor>,
      ... }
```

### 3.2. El actor genérico

Los tres tipos de actor (jugador, NPC, grupo de monstruos) comparten envoltorio y solo cambian en `f2`:

```
actor { f1 = { f2 = <celda>, f5 = <orientación> },   ← disposición
        f2 = <detalles, según el tipo>,
        f3 = <id contextual> }
```

**Ids contextuales:** el jugador usa su `CharacterId` real. Los NPC empiezan en **-20000** y van descendiendo; los grupos de monstruos continúan la misma cuenta descendente. Ese id es el que el cliente devuelve al interactuar (por ejemplo, en `hoy` al atacar un grupo).

### 3.3. Actor jugador

```
f2 = GameState.PlayerActorDetails
```
Es un blob construido en `CharacterSelectionHandler` a partir de la fila del personaje (nombre, raza, sexo, nivel, look).

### 3.4. Actor NPC

```
f2 = { f1 = { f1 = <boneId>, f3 = 3, f8 = <escala> },     ← EntityLook
       f2 = { f5 = { f4 = 1, f6 = <npcId> } } }            ← f4 = tooltip visible
```

El `boneId` y la escala salen de la columna `Look` de `NpcSpawns`, con formato `{bone|pieles|colores|escala}`.

### 3.5. Actor grupo de monstruos

Es el más elaborado, y tiene una asimetría importante:

```
f2 = { f1 = <EntityLook del LÍDER>,          ← f1=bone, f3=3, f8=escala (solo si ≠100)
       f2 = { f1 = { f2 = -1,
                     f3 = { f1 = <miembro LÍDER>,      ← campo 1 para el líder
                            f3 = <miembro secuaz>,     ← campo 3 para los demás
                            f3 = <miembro secuaz>, ... },
                     f4 = 1 } } }
```

Cada miembro:
```
miembro { f3 = <monsterId>,
          f4 = <grado>,
          f5 = <EntityLook propio>,   ← SOLO los secuaces
          f6 = <nivel> }
```

> **Regla no evidente:** el **líder no lleva `f5`**, porque su aspecto ya está en `f1` del contenedor de detalles. Si se le añade, el cliente pinta el grupo mal. El líder va en el campo **1** de la lista y los secuaces en el **3**.

---

## 4. Movimiento — `joi` → `joo`

`MapChangeHandler.HandleMovementRequest`. Si hay combate en curso, delega en `FightHandler.HandleCombatMovementRequest`.

### 4.1. Lo que manda el cliente

```
joi { f1 = <mapId>, f3 = [<celda+dirección*4096>, ...] }
```

El camino viene **comprimido**: solo los vértices (los puntos donde cambia de dirección), y cada valor empaqueta celda y dirección:

```
celda     = valor % 4096
dirección = valor / 4096       (0..7)
```

Ejemplo real de la captura: `[16812, 12715, 12823]` → `(428, dir 4), (427, dir 3), (535, dir 3)`.

### 4.2. Lo que hace el servidor

1. Extrae la **última** celda y su dirección: `lastCell = path[^1] % 4096`.
2. Actualiza `GameState.CellId`, `MapId` y `Orientation`, y **guarda en la base de datos**.
3. Difunde el movimiento con `joo`.
4. Comprueba si la celda de destino tiene un grupo de monstruos (§6).

### 4.3. `joo` — difusión del movimiento

```
joo { f1 = <fighterId/characterId>,
      f2 = <camino EXPANDIDO celda a celda, packed>,
      f5 = <orientación final> }
```

> ### ⚠️ Diferencia crítica con el servidor real
> El servidor oficial **expande** el camino: convierte los 3 vértices en la lista completa de celdas adyacentes. Ejemplo real: `[428, 427, 440, 454, 467, 481, 494, 508, 521, 535]` — 10 celdas, cada una vecina de la siguiente.
>
> El emulador **reenvía los vértices tal cual**. Como no son adyacentes, el cliente no puede animar el desplazamiento y **teletransporta** al personaje. Es el bug D1 de [PLAN_COMBATE_V3.md](PLAN_COMBATE_V3.md), y afecta igual al roleplay.
>
> Además, el emulador usa la clase generada `Messages.joo` con los campos `Funv`/`Funw`/`Funz`, poniendo `Funz = 2`; en la captura real ese tercer campo es **`f5` = la orientación final**, no un 2 fijo.

### 4.4. `jpp` — confirmación de llegada

Cuando el cliente termina de recorrer el camino manda `jpp`. El servidor responde con **`joq`**:

```
joq { f3 = -1 }
```

Construido en `MapChangeHandler.HandleMovementConfirm`. En combate, ese mismo `jpp` es lo que dispara la ráfaga de inicio de pelea.

---

## 5. Cambio de mapa — `jos` → `joh`

`MapChangeHandler.HandleMapChangeRequest`.

```
Cliente → jos { f1 = <mapId destino> }
Servidor → joh { f2 = <mapId> }
```

### Pasos

1. Si el mapa pedido es el actual, se ignora.
2. **Deduce la dirección** comparando las coordenadas de los dos mapas en `MapManager.Maps`:
   - `PosX` mayor → `Right`; menor → `Left`
   - `PosY` mayor → `Down`; menor → `Up`
3. **Calcula la orientación** de llegada: Right→1, Left→5, Down→3, Up→7.
4. **Calcula la celda de aparición** con `GetTransitionSpawnCell`, que coloca al personaje en el borde opuesto:

   | Dirección | Celda de aparición |
   |---|---|
   | Right | `fila * 14 + 2` (columna 2, borde izquierdo) |
   | Left | `fila * 14 + 11` (columna 11, borde derecho) |
   | Down | `8 * 14 + columna` (fila 8, borde superior) |
   | Up | `28 * 14 + columna` (fila 28, borde inferior) |

   La fila/columna de origen se recorta a `fila ∈ [10,26]`, `columna ∈ [4,9]` para no salirse. El resultado pasa por `GetNearestWalkableCell`.

5. Guarda mapa, celda y orientación en la base de datos.
6. Envía `joh`. El cliente responderá con un `kkr` del mapa nuevo, y se repite el ciclo de §3.

> `MapScrolls` (los mapas vecinos en las 4 direcciones) está cargado en `MapManager.ScrollActions` y accesible con `GetScrollAction`, pero **el flujo actual no lo usa**: se fía del `mapId` que manda el cliente. Sería la forma de validar que la transición es legítima.

> La celda de aparición se calcula con la geometría plana incorrecta (§2.2), así que puede caer en un sitio raro y ser corregida después por `GetNearestWalkableCell`.

---

## 6. Colisión con monstruos

Al final de `HandleMovementRequest`, si no hay combate y la celda de destino es válida:

```csharp
var mob = MobSpawnManager.GetMobAtCell(mapId, lastCell);
if (mob != null) await FightHandler.InitiateFightFromMobCollision(stream, mob, mapId);
```

`GetMobAtCell` busca coincidencia exacta y, si falla, acepta **proximidad de ±1 o ±14 celdas** para absorber el redondeo del pathfinding.

Existe una segunda vía de entrada al combate: el mensaje **`hoy`** del cliente (atacar el grupo pulsando sobre él), que lleva `{ f1 = <id contextual del grupo> }` — el mismo id negativo que se asignó en `jpv`.

---

## 7. Generación de monstruos

`MobSpawnManager`:

1. Carga los **5.134 monstruos** de la tabla `Monsters` (id, nameId, look, y el nivel de cada grado).
2. Carga los **38.765 grupos** de `MapMobs`, con su `MembersJson`: `[{"id":493,"grade":3,"level":13}, ...]`.
3. Si un mapa no tiene grupos en la base de datos, **los genera al vuelo**: entre 2 y 4 grupos, de 1 a 8 miembros, elegidos de una lista de ids de píos (491, 492, 493, 463, 2341-2347), colocados en celdas "interiores".

`GetInnerWalkableCells` selecciona celdas cuyos 12 vecinos en radio 2 son todos transitables, excluyendo bordes (`fila ∈ [8,28]`, `columna ∈ [2,11]`) — otra función que usa la geometría plana.

Al ganar un combate, `RemoveMobGroup` borra el grupo del mapa en memoria (no de la base de datos).

---

## 8. La arena de combate

Descubrimiento clave documentado en la memoria del proyecto: **el combate no se libra en el mapa de roleplay**, sino en una *arena*, un mapa distinto de la misma subárea con `PosX = 0, PosY = 0` y `m_flags = 69262589`.

**Prueba:** de la captura de referencia, 6 de 6 posiciones de luchadores son transitables en la arena `153891076` y **0 de 6** en el mapa de roleplay `154010883`.

`MapManager.ResolveArenaMapId` implementa la selección:

```csharp
// 1. Si el mapa es exterior y tiene coordenadas reales -> se pelea en ese mismo mapa
if (info.Outdoor && (info.PosX != 0 || info.PosY != 0)) return roleplayMapId;

// 2. Candidatas: misma subárea, (0,0), outdoor, sin nombre, flags == 69262589,
//    y con al menos 40 celdas transitables
// 3. Si existe roleplayMapId + 4 entre las candidatas, se usa esa
// 4. Si no, se elige de forma determinista: arenas[|mapId| % arenas.Count]
```

> El `+4` cubre solo una fracción de los mapas (las parejas confirmadas del tutorial); **no hay regla aritmética general** y la asociación oficial mapa↔arena no está en la base de datos. Basta con elegir una arena válida de forma determinista y ser coherente: el cliente carga la que se le diga.
>
> La regla 1 (pelear en el mismo mapa si es exterior) es una decisión del emulador, no del protocolo oficial.

El flag **69262589** es el discriminador: ningún mapa de roleplay lo tiene, y descarta los 688 interiores que también están en (0,0).

---

## 9. Otros mensajes del ciclo de mapa

| Mensaje | Dirección | Respuesta del servidor |
|---|---|---|
| `loy` (world load ack) | C→S | `kmw` vacío |
| `lpj` (hilos secundarios listos) | C→S | `jfc` vacío |
| `lsy` | S→C | `{ f1 = subAreaId, f3 = 45 }` — alineamiento de subárea |
| `kns` | S→C | `{ f1 = true }` — latido/ack |
| `kod` | C→S | latido; se ignora |

`kmw` y `jfc` se envían como **tramas hex literales** (`MapLoadHandler.cs:187` y `:195`) porque son mensajes vacíos; equivale a `BuildGameNodePacket(url, Array.Empty<byte>())`.

---

## 10. Recorrido completo de un cambio de mapa

```
 1. El jugador camina hacia el borde
 2. Cliente → joi { mapId, [vértices comprimidos] }
 3. Servidor: actualiza GameState, guarda en BD
             → joo { charId, camino, orientación }
             comprueba colisión con mobs
 4. Cliente → jpp        (ha llegado)
 5. Servidor → joq { f3 = -1 }
 6. Cliente → jos { mapId destino }
 7. Servidor: deduce dirección (Right/Left/Down/Up) por PosX/PosY
             calcula celda de aparición en el borde opuesto
             la ajusta con GetNearestWalkableCell
             guarda en BD
             → joh { mapId }
 8. Cliente → kkr { mapId }
 9. Servidor → lxd (vacío)
             → jpv (subárea, mapId, jugador + NPCs + grupos de mobs)
             → lsy { subAreaId, 45 }
             → kns { true }
10. El cliente pinta el mapa nuevo con todos sus actores
```

---

## 11. Deudas técnicas de esta parte

1. **Geometría plana en tres funciones** (§2.2). Afecta a alcances, celdas de aparición y selección de celdas de mob.
2. **`joo` no expande el camino** → el personaje se teletransporta en vez de caminar (§4.3).
3. **`joo.Funz = 2` fijo** donde el real lleva la orientación final.
4. **`MapScrolls` cargado pero sin usar**: no se valida que la transición de mapa sea legítima; se acepta el `mapId` que diga el cliente.
5. **La subárea 444 se reescribe a 20663** en dos sitios sin explicación.
6. **`GameState.MapId` se sobrescribe con el `mapId` del cliente** en cada `joi` (`MapChangeHandler.cs:115`). Es un parche contra la desincronización, pero significa que el cliente manda sobre la posición.
7. **`GetInnerWalkableCells` genera mobs con criterios de cuadrícula plana**, así que los grupos pueden quedar en sitios extraños.
8. **Los mobs generados al vuelo no se persisten**: cada reinicio del emulador cambia la población de los mapas sin entrada en `MapMobs`.

---

*Siguiente parte prevista: personaje, características e inventario (`kri`, `krc`, `irm`, `isi`, `isf`).*
