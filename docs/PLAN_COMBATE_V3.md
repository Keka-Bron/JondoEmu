# Plan de implementación — Combate Jondo, iteración 3

**Fecha:** 2026-08-03
**Destinatario:** Gemini Flash Pro 3.6
**Estado de partida:** la arena carga bien, el jugador y los monstruos aparecen con sus estadísticas correctas, el handshake de turno funciona. Quedan 6 defectos.

> **Regla previa que hay que respetar en todo el documento:** el personaje real es **KEKA-BRON (Ocra)**. Todos los valores de `Fortellon`, `670668947750`, `-20003`, `3273`, `3256`, `154010883` que aparecen aquí vienen de la **captura de referencia** y son **solo documentación del formato**. Nunca deben acabar escritos en el código: todo sale de `GameState`, de `world.db` o del `FightInstance` en curso.

---

## 0. Tabla de defectos

| # | Síntoma | Causa raíz | Archivo |
|---|---|---|---|
| **D1** | El personaje se teletransporta al hacer clic y no gasta PM | Se reenvía el camino **comprimido** del cliente en vez de expandirlo celda a celda, y se manda un `kkz` que fuerza la posición | `FightHandler.HandleCombatMovementRequest` |
| **D2** | Los hechizos no hacen nada (sin animación, sin daño) | `jub` se procesa con `spellId` por defecto hardcodeado, coste de PA fijo, daño aleatorio inventado, y el daño se emite con `jwu` mal formado en vez de `jtx` | `FightHandler.HandleSpellCastRequest` |
| **D3** | Salen los hechizos del Yopuka en un Ocra | **Error de datos, no de código:** la fila del personaje tiene `Breed = 8` sembrado a mano, mientras su `Look` codifica un 9 | `DatabaseManager.cs:193` |
| **D4** | Los monstruos no hacen nada | `Monsters.Spells` está **vacío en los 5134 monstruos**, la IA cae a un ataque por distancia fija, y la geometría de distancias es incorrecta | `MonsterAI.cs`, `DatabaseManager.GetMonsterGradeStats` |
| **D5** | En combate tengo 300 PV en vez de 500, y el nombre sale "???" | La vida se recalcula con una fórmula propia sin bonus de equipo; el nombre se manda en un campo inventado en vez de en `jyf` | `FightHandler.cs:1303`, `:1156`, `:1331` |
| **D6** | El carrusel pinta el orden equivocado | `jxe` se envía **antes** de calcular el orden de turnos, con un criterio distinto al que luego se usa | `FightHandler.BuildTurnListBytes:344`, `FightInstance.StartFight` |
| **D7** | El contador de turno se va a negativo y no pasa turno | No existe temporizador de turno en el servidor | `FightHandler` |
| **D8** | *(transversal)* Datos de la captura y valores por defecto inventados por todo el código | Patrón sistemático; es la causa de fondo de D3 y D5 | `DatabaseManager`, `GameState`, `FightHandler`, `TransitionPayloads` |

---

## D0 (BLOQUEANTE) — La geometría del tablero es incorrecta

Todo lo demás (alcance de hechizos, IA, caminos) depende de esto, así que va primero.

`MonsterAI.GetManhattanDistance` hace:
```csharp
int rA = cellA / 14, cA = cellA % 14;   //  <-- INCORRECTO
return Math.Abs(rA - rB) + Math.Abs(cA - cB);
```

El tablero de Dofus es una **rejilla isométrica escalonada**, no una cuadrícula plana.

### Regla de adyacencia — RESUELTA con datos reales

Extraídos **451 pares de celdas consecutivas** de **111 caminos** (`joo`) presentes en 19 capturas distintas. Como un camino expandido tiene por definición celdas adyacentes, la frecuencia de los deltas da la regla directamente:

| Paridad de `cell / 14` | Deltas observados (8 vecinos, y solo esos) |
|---|---|
| **PAR** | `-28, -15, -14, -1, +1, +13, +14, +28` |
| **IMPAR** | `-28, -14, -13, -1, +1, +14, +15, +28` |

Frecuencias en fila par: `+14`(90) `+13`(30) `-14`(26) `-15`(22) `-28`(19) `+28`(10) `+1`(7) `-1`(5).
En fila impar: `+15`(93) `+14`(30) `-14`(26) `-13`(25) `+28`(24) `+1`(12) `-28`(9) `-1`(3).
Todo lo demás aparece 1-2 veces y es ruido (cambios de mapa, teletransportes).

Son las 8 direcciones de Dofus: los cuatro deltas de fila ±1 (`±13/±14/±15` según paridad) son las diagonales; `±1` es el desplazamiento horizontal en pantalla; `±28` el vertical.

### Qué implementar

1. Clase `MapGeometry` con:
   ```csharp
   static readonly int[] VecinosPar   = { -28, -15, -14, -1, +1, +13, +14, +28 };
   static readonly int[] VecinosImpar = { -28, -14, -13, -1, +1, +14, +15, +28 };

   public static IEnumerable<int> Neighbours(int cell)   // aplica la paridad de cell/14,
                                                          // descarta fuera de [0,559]
                                                          // y descarta el salto de borde
                                                          // (col 0 <-> col 13)
   ```
2. **`Distance(a, b)` por BFS sobre ese grafo**, no por fórmula. Precalcula al arrancar una matriz 560×560 con un BFS desde cada celda (≈313k operaciones, instantáneo) y cachéala: `Distance` queda O(1) y es demostrablemente correcta dada la regla de vecindad.
   *No inventes una fórmula cerrada de `(x,y)`: he probado las tres candidatas habituales (incluida la actual) y ninguna reproduce estas adyacencias.*
3. **Cuidado con el borde:** `±1` cruza de la columna 13 a la 0 de la fila siguiente, lo cual no es un vecino real. Filtra los deltas `±1` cuando `cell % 14` es 0 (para `-1`) o 13 (para `+1`).
4. **Test obligatorio** con caminos reales; si no pasa, no sigas:
   - `[428, 427, 440, 454, 467, 481, 494, 508, 521, 535]` (roleplay, 9 pares)
   - `[299, 313]` y `[313, 327]` (combate)
   - `[521, 535]`
   Cada par consecutivo debe dar `Distance == 1`.

Sustituye **todas** las llamadas a `GetManhattanDistance` por `MapGeometry.Distance`.

---

## D1 — Movimiento: expandir el camino y no forzar la posición

### Evidencia de la captura

El cliente manda `joi` con el camino **comprimido** (solo los vértices, cada uno con su dirección codificada como `celda + dirección*4096`):

```
joi { f1 = <mapId>, f3 = [16812, 12715, 12823] }
     -> descomprimido: (celda 428, dir 4), (celda 427, dir 3), (celda 535, dir 3)
```

El servidor responde con `joo` conteniendo el camino **expandido celda a celda**, en números de celda planos:

```
joo { f1 = <fighterId>,
      f2 = [428, 427, 440, 454, 467, 481, 494, 508, 521, 535],   (packed repeated int32)
      f5 = <orientación final> }
```

Y **no manda ningún `kkz`** después. La pérdida de PM va en un `jvm` dentro de una secuencia.

### Secuencia real completa de un movimiento en combate

```
jud { f1 = 4, f2 = <fighterId> }          <- inicio de secuencia de movimiento
joo { f1, f2 = <camino expandido>, f5 }   <- el desplazamiento
jud { f1 = 3, f2 = <fighterId> }          <- inicio de sub-secuencia
jvm { ...pérdida de PM... }               <- consumo de puntos
juc { f1 = 3, f2 = <fighterId>, f3 = <n> }
jtx { ... }                                <- efectos de fin de movimiento
juc { f1 = 4, f2 = <fighterId>, f3 = <n> }
```

### Estructura de `jvm` (variación de puntos), decodificada

```
jvm { f2 = <fighterId>,
      f3 = { f1 = 2,
             f4 = { f4 = { f2 = <delta acumulado, negativo>,
                           f4 = <valor máximo del stat>,
                           f8 = <valor absoluto del delta> },
                    f5 = <statId> } } }
```
`statId 23` = PM, `statId 1` = PA. Muestras reales observadas:

| delta (f2) | máximo (f4) | abs (f8) | statId |
|---|---|---|---|
| -1 | 3 | 1 | 23 |
| -2 | 3 | 2 | 23 |
| -3 | 3 | 3 | 23 |
| -2 | 6 | 2 | 1 |

Nótese que el delta es **acumulado dentro del turno** (tras tres pasos de 1 PM va `-3`, no `-1`).

### Qué implementar

1. **Descomprimir el `joi`**: para cada elemento del camino recibido, `celda = v % 4096`, `dirección = v / 4096`.
2. **Validar el camino** contra las celdas transitables de la arena y contra los PM disponibles. Si pide más pasos que PM, recortar el camino a los PM disponibles (no rechazarlo entero).
3. **Expandir** los vértices a celdas adyacentes consecutivas con `MapGeometry` (algoritmo A* o línea recta entre vértices; los vértices son los cambios de dirección).
4. Emitir la secuencia completa de arriba. **Eliminar el `kkz` posterior** (`FightHandler.cs:654-656`): es lo que provoca el salto.
5. Descontar `PM = PM - (celdas del camino expandido - 1)` y emitir el `jvm` con el delta **acumulado del turno**.
6. Al empezar cada turno, resetear el acumulado a 0 (además de `Fighter.StartTurn`).

---

## D2 — Hechizos: leer la BD y emitir el daño de verdad

`HandleSpellCastRequest` tiene tres problemas encadenados:

```csharp
long spellId = 32435;              // 1. valor por defecto hardcodeado
int apCost = 3;                    // 2. coste fijo, no lee SpellLevels
int damage = 20 + new Random().Next(5, 15);   // 3. daño inventado
await SendLifePointsVariation(...) // 4. usa jwu con campos que no existen
```

`SendLifePointsVariation` emite `jwu {f1,f2,f3,f4}`, pero el `jwu` real es **`{ f3 = fighterId }`** y no tiene nada que ver con el daño. Por eso no ves ni animación ni pérdida de vida.

### Estructura real de `jtx` — DECODIFICADA

`jtx` es un **mensaje con variantes seleccionadas por `f13` (id de acción)**. Siempre lleva `f13` (acción) y `f29` (autor), y **un único campo más** que cambia según la acción. Variantes observadas en la captura:

**a) Lanzamiento de hechizo — `f13 = 300`, carga en `f34`** (frames 254 y 269):
```
jtx { f13 = 300,
      f29 = <id del lanzador>,
      f34 = { f1  = 1,
              f4  = <celda objetivo>,          (411)
              f5  = { f1 = <uid del efecto>,   (41870)
                      f4 = <spellId> },        (13425 — uno de los de su jvn)
              f7  = { f2 = <13 bytes>,
                      f3 = <id del lanzador>,
                      f5 = <nº de lanzamiento en el turno> },   (1, luego 2)
              f8  = <id del objetivo> } }      (-1)
```

**b) Variación de puntos — `f13 = 129` ó `102`, carga en `f6`:**
```
jtx { f6 = { f1 = <fighterId>, f2 = <delta, negativo> },
      f13 = <129 tras moverse | 102 tras lanzar>,
      f29 = <autorId> }
```
Muestras: `f2 = -1` y `-2` tras moverse (`f13=129`); `f2 = -2` tras lanzar (`f13=102`).

**c) Tercera variante — `f13 = 99`, carga en `f25`:**
```
jtx { f13 = 99, f25 = { f1 = 2, f4 = -1, f5 = 7 }, f29 = <autorId> }
```

> **Implementa primero la variante (a)**, que es la que hace visible el hechizo y el daño. Las (b) y (c) acompañan pero no son imprescindibles para ver el efecto. **No mezcles campos entre variantes** y no inventes ninguno: si necesitas una que no está aquí, vuélcala con `py C:\Jondo\scripts\fightdump.py jtx` y replícala.

### Qué implementar

1. Parsear `jub` correctamente: **`{ f1 = spellId, f2 = celdaObjetivo }`**. Si no se puede parsear, **abortar**, nunca usar un id por defecto.
2. Leer el hechizo real de `SpellLevels` (`DatabaseManager.GetSpellCombatData`): `APCost`, `MinRange`, `MaxRange`, efectos.
3. **Validar**: ¿es el turno de ese luchador? ¿tiene PA suficientes? ¿la celda está dentro de `[MinRange, MaxRange]` según `MapGeometry.Distance`? ¿quedan lanzamientos por turno? Si algo falla, no descontar nada y no emitir la secuencia.
4. Resolver los objetivos por la **zona de efecto** del hechizo (la captura de pantalla muestra "Zona: cruz de tamaño 3"), no por "el monstruo más cercano".
5. Calcular el daño con `DamageCalculator` usando el elemento del hechizo, la característica correspondiente del lanzador y la resistencia del objetivo. Nada de `Random` sobre una base fija.
6. Emitir la secuencia: `jud{f1, f2=lanzador}` → `jtx`(daño, uno por objetivo) → `jvm`(pérdida de PA) → `juc{f1, f2=lanzador, f3}`.
7. Eliminar `SendLifePointsVariation` y `SendPointsVariation` en su forma actual: usan `jwu`/`jys`/`kkz` con campos inventados.

---

## D3 — Hechizos de la clase equivocada — DIAGNOSTICADO: es un error de DATOS

**No hay ningún fallo en el código de hechizos.** Lo he verificado:

1. La tabla `SpellVariants` es **correcta**: `BreedId 9` contiene "Flecha Helada, Flecha Acosante, Flecha de Pelea, Diamantes Destructores…" (Ocra) y `BreedId 8` contiene "Machete, Acumulación, Intimidación, Conquista, Salto…" (Yopuka). Las 20 clases están bien.
2. `GetBreedSpellIds` funciona bien.
3. **La fila del personaje en `world.db` tiene `Breed = 8`:**
   ```
   id 13825558   nombre [#KEKA-BRON#]   breed 8   sexo 1   nivel 50
   ```
4. Su `Look`, en cambio, lleva un **9**: decodificado da `f1=1, f3=3, f4=<6 colores>, f7=9, …`. El modelo 3D es de Ocra.

Es decir: **el visual dice Ocra y la columna dice Yopuka**. En roleplay no se nota porque la barra de hechizos sale de la ráfaga de entrada al mundo (capturada y hardcodeada), no de la BD; el combate es el primer sitio donde esa columna se usa de verdad.

**Origen:** `DatabaseManager.cs:193`, la semilla del personaje lleva el breed escrito a mano:
```sql
VALUES (13825558, 188940901, $name, 8, 1, 40, 154010884, 280, ...)
                                    ^-- 8 hardcodeado, pero $look es de Ocra
```

### Qué implementar

1. **Corregir la semilla** en `DatabaseManager.cs:193`: `8` → `9`. Y como la fila ya existe, añadir una migración idempotente que corrija el dato ya guardado:
   ```sql
   UPDATE Characters SET Breed = 9 WHERE Id = 13825558 AND Breed = 8;
   ```
   (Sigue el patrón de las migraciones que ya hay en `DatabaseManager.cs:311-327`.)
2. **Eliminar el valor por defecto** `Breed = 9` de `GameState.cs:12`: que sea `0` y se cargue obligatoriamente de la fila del personaje al seleccionarlo. Un valor por defecto que "acierta por casualidad" es justo lo que ocultó este error.
3. Cuando exista creación de personaje real, el `breed` debe salir del mensaje del cliente y **debe coincidir con el que se codifica en el `Look`**. Añade una comprobación al arrancar: si `Characters.Breed` no coincide con el breed del `Look`, escribe un `WARN` en el log.
4. Filtrar los hechizos por nivel: solo los de `MinPlayerLevel <= nivel del personaje`.
5. Quitar el `.Take(10)` de `FightHandler.cs:736`: el número de hechizos lo decide la clase y el nivel, no un tope arbitrario.

---

## D4 — IA de monstruos

### Por qué ahora no hacen nada

1. **`Monsters.Spells` está vacío en los 5134 monstruos de `world.db`** (verificado con `SELECT COUNT(*) ... WHERE Spells <> '[]'` → 0). Así que `monster.SpellIds` siempre viene vacío y el bucle de hechizos nunca se ejecuta.
2. El *fallback* ataca solo si `distancia <= 6` con la geometría incorrecta (D0), así que falla la mitad de las veces.
3. Aunque calcule daño, lo emite con `jwu` mal formado (D2) → invisible.
4. El movimiento del monstruo no emite `joo`, así que aunque se mueva internamente, el cliente no lo ve.

### Fuente de hechizos de monstruo

El campo `startingSpellId` **sí existe** dentro del JSON de `Grades` de cada monstruo (verificado: `La ardilla Peazo Beyota` tiene `startingSpellId: 21636`). Orden de búsqueda a implementar:

1. `Monsters.Spells` (hoy vacío, pero por si se rellena).
2. `startingSpellId` del grado concreto que se está usando.
3. **Comprobar `MonsterTemplates.Data`**: es el JSON crudo completo del monstruo y puede contener la lista de hechizos. Míralo antes de dar el paso 4.
4. Último recurso: un ataque cuerpo a cuerpo por defecto de alcance 1, **declarado explícitamente en el log** como "sin hechizos en BD".

### Algoritmo pedido

Sustituir `MonsterAI.ExecuteTurn` entero por esta lógica. Nada de distancias fijas: **todo alcance sale del hechizo**.

```
ExecuteTurn(monstruo, luchadores):
  1. Cargar los hechizos del monstruo con sus (coste PA, alcance mín, alcance máx, elemento, daño).
     Descartar los que no pueda pagar con sus PA actuales.

  2. modoHuida = monstruo.CurrentHP < 0.30 * monstruo.MaxHP

  3. Elegir objetivo entre los enemigos vivos, por puntuación:
       + prioridad alta: menor  vida absoluta restante  (rematar)
       + prioridad alta: menor  vida en %               (herido)
       + bonificación:   objetivo AISLADO  (pocos aliados suyos a distancia <= 3)
       + penalización:   distancia (que no cruce el mapa entero)
     Los pesos deben ser constantes con nombre, no números sueltos.

  4. FASE DE ATAQUE (siempre antes de moverse si ya está en rango):
     para cada hechizo, de mayor a menor daño esperado:
        si Distance(monstruo, objetivo) está en [alcanceMin, alcanceMax] y hay PA:
           lanzar, descontar PA, emitir jud/jtx/jvm/juc
           repetir mientras queden PA y lanzamientos por turno

  5. FASE DE MOVIMIENTO:
     si NO modoHuida y no pudo atacar:
        calcular la celda alcanzable (con sus PM) que:
           - deje al objetivo dentro del alcance de ALGÚN hechizo que pueda pagar
           - a igualdad, la que gaste MENOS PM   <- "acercarse lo imprescindible"
           - a igualdad, la que rodee al objetivo (contacto con más aliados)
        moverse ahí, emitir joo + jvm, y volver a la FASE DE ATAQUE
        si ninguna celda cumple, acercarse todo lo posible al objetivo

     si modoHuida:
        primero atacar si puede (paso 4)
        después moverse a la celda alcanzable que MAXIMICE la distancia a los enemigos
        (y que no quede en callejón sin salida)

  6. Terminar el turno con EndTurnAsync(monstruo)
```

### Requisitos de implementación

- Cálculo de celdas alcanzables con **búsqueda en anchura** limitada por PM sobre las celdas transitables de la **arena** (no del mapa de roleplay), evitando celdas ocupadas por otros luchadores.
- "Aislado" = número de aliados del objetivo a distancia ≤ 3. Parametrizable.
- Todo movimiento del monstruo debe emitir su `joo` con el camino expandido y su `jvm`, igual que el del jugador (D1). Si no, es invisible.
- El monstruo debe terminar su turno **siempre**, aunque no pueda hacer nada, o el combate se queda colgado.

---

## D5 — Vida a 300 y nombre "???"

### Vida

`FightHandler.cs:1303`:
```csharp
AddBaseBonusVal(null, 50 + (fighter.Level * 5) + GameState.StatVitality, 0);
                                                                          ^-- bonus de equipo = 0
```
Fórmula propia que **ignora los bonus de equipo**, mientras que fuera de combate la vida la publica `kri` con `StatsHandler` (que sí los suma).

**Confirmado con los datos reales:** la fila del personaje tiene `Level = 50` y `Vitality = 0`, así que `50 + 50*5 + 0 = 300` — exactamente los PV que ves al empezar el combate. Los 500 de fuera vienen del mismo cálculo **más** `GetEquipBonus(0)`. Es la prueba de que la única diferencia es el bonus de equipo.

**Corrección:** extraer a `StatsHandler` un único método `GetPlayerMaxHp()` y usarlo en **los dos sitios** (`kri` y el bloque de características del combate). Una sola fuente de verdad. Y `Fighter.MaxHP` del jugador (`FightHandler.cs:56`, otra fórmula distinta: `55 + level*5 + vit`) debe salir también de ahí.

Además, la vida **actual** no debe resetearse al máximo al entrar en combate: si entras con 440/500, el combate empieza con 440.

### Nombre

El nombre del jugador **no va en `jxx`**. En la captura viaja en el `jyf` del equipo, dentro del miembro:

```
jyf { f1 = { f2 = <leaderId>, f7 = 1,
             f8 = { f2 = { f2 = <playerId>,
                           f4 = { f2 = "<NOMBRE>",   <- string UTF-8
                                  f3 = <breed> } } } },
      f2 = 300 }
```

En `FightHandler.cs:1156` se está metiendo `playerLookBytes` en ese `f2` en vez del nombre:
```csharp
lookBreedSub.Fields.Add(new ProtoField { FieldNumber = 2, ..., BytesValue = playerLookBytes });  // MAL
```
Debe ser `System.Text.Encoding.UTF8.GetBytes(GameState.CharacterName)` — para KEKA-BRON, 9 caracteres.

Y hay que **quitar** el bloque `f6` inventado de `FightHandler.cs:1328-1334` (nombre + nivel dentro de `jxx`): no existe en el protocolo real.

### Nivel

`FightHandler.cs:1314` sigue enviando `AddSimpleVal(70, fighter.Level)`. **El statId 70 no existe** en el bloque de características real. El nivel del jugador no se transmite ahí; el de los monstruos va en el `f7` de la info de luchador (que ya funciona: los píos muestran su nivel bien).

---

## D6 — Orden del carrusel

`BuildTurnListBytes` (`FightHandler.cs:344`):
```csharp
var fighters = (fight.TurnOrder.Count > 0)
    ? fight.TurnOrder
    : fight.Team0.Concat(fight.Team1).OrderByDescending(f => f.Initiative).ToList();
```

En la ráfaga 3 todavía no se ha llamado a `StartFight()`, así que `TurnOrder` está **vacío** y se usa el orden por iniciativa pura. Al pulsar LISTO, `StartFight()` genera `BuildAlternatingTurnOrder()`, que **alterna equipos** y da un orden distinto. El carrusel se pinta con el primero y los turnos usan el segundo.

**Corrección:**
1. Calcular el orden de turnos **una sola vez, al crear el `FightInstance`**, con `BuildAlternatingTurnOrder()`.
2. `BuildTurnListBytes` usa siempre `fight.TurnOrder`, sin rama alternativa.
3. `StartFight()` ya no recalcula el orden: solo pone `CurrentTurnIndex = 0`.
4. Reenviar `jxe` cuando el orden cambie (cuando muere un luchador). En la captura hay un `jxe` de 11 bytes a mitad de combate, justo con esa función.

---

## D7 — Temporizador de turno

No existe. `jut.f1 = 300` solo le dice al cliente cuántas décimas de segundo dura el turno; el servidor tiene que hacer cumplir el plazo.

**Corrección:**
1. Al enviar `jut`, arrancar un `CancellationTokenSource` con 30 s.
2. Si expira y el turno sigue siendo del mismo luchador y el mismo número de ronda, llamar a `EndTurnAsync(actual)` — exactamente el mismo camino que el `jxw` del cliente.
3. Cancelar el temporizador en cuanto llegue `jxw`, o cuando el turno cambie por cualquier otra vía.
4. Guardar el token en el `FightInstance`, no en un estático, para que no se cruce entre combates.
5. Lo mismo con el temporizador de colocación (45 s): hoy es un `Task.Delay(45000)` suelto en `FightHandler.cs:229` que **no se cancela** si el jugador pulsa LISTO antes. Cancélalo.

---

## D8 — Erradicar los datos hardcodeados

El caso del `Breed = 8` no es una anécdota: es el síntoma de un patrón que va a seguir produciendo bugs invisibles. Un dato escrito a mano que **casualmente funciona** es peor que uno que falla, porque oculta el error hasta que otra parte del sistema lo usa de verdad — que es exactamente lo que ha pasado aquí (nadie leyó `Characters.Breed` hasta que el combate construyó `jvn`).

### Inventario de lo que hay hoy

**Categoría 1 — La semilla del personaje es incoherente consigo misma** (`DatabaseManager.cs:189-195`)

```sql
VALUES (13825558, 188940901, $name, 8, 1, 40, 154010884, 280, 195, 0,0,0,0,0,0, $look, 50000)
        ^id       ^cuenta            ^breed ^sexo ^nivel ^mapa    ^celda ^puntos      ^look
```
- `breed = 8` (Yopuka) mientras `$look` codifica un 9 (Ocra) → **el bug de D3**.
- `nivel = 40` pero la fila actual dice 50 (la actualiza `SaveCurrentCharacter`).
- `mapId = 154010884` es un mapa de **Incarnam** en una subárea sin nombre, mientras el personaje se mueve realmente por **Astrub** (`191104002`).

**Categoría 2 — Valores por defecto en `GameState` que enmascaran fallos de carga**

| Campo | Valor | Riesgo |
|---|---|---|
| `Breed = 9` (`GameState.cs:12`) | acierta por casualidad | oculta que no se cargó de la BD |
| `MapId = 154010884L` (`:18`) | mapa de Incarnam | el emulador cree estar donde no está |
| `CellId = 280` (`:19`) | — | posición inventada |
| `CharacterRemainingPoints = 195` (`:28`) | — | capital de puntos falso |
| `Kamas = 50000` (`:21`) | — | dinero inventado |

**Categoría 3 — Payloads binarios capturados de la sesión original**
- `BasePayloads.WorldEnteringPackets` — la ráfaga entera de entrada al mundo, con **el mapa, las misiones activas y las fechas** de la sesión de Fortellon (se ve la fecha `2026-06-24 19:39` incrustada en `TransitionPayloads.lokList`).
- `TransitionPayloads.cs` — 17 payloads literales (`lok`, `jdj`, `kkp`, `kkm`, `krb`, `ilc`, `joh`, `lor`, `hmd`, `itp`, `lpe`, `hnk`, `kqm`, `icg`, `ith`, `klt`, `klp`).
- `CharacterSelectionHandler.cs:113-135` — tramas hex sueltas (`jtm` con fecha `2035-01-01`, `klp`, `iua`, `isj`).

**Categoría 4 — Valores de la captura dentro del código de combate**

| Ubicación | Valor | Qué es en realidad |
|---|---|---|
| `FightHandler.cs:910` | `spellId = 32435` | hechizo por defecto inventado |
| `FightHandler.cs:924` | `apCost = 3` | coste fijo; debe salir de `SpellLevels` |
| `FightHandler.cs:949` | `20 + Random(5,15)` | daño inventado |
| `FightHandler.cs:1337` | `mId = 3273` | **el monstruo de la captura** como respaldo |
| `FightHandler.cs:1153` | look hex `08-01-18-03-22-18-…` | el look de Fortellon como respaldo |
| `FightHandler.cs:1299` | `AddBaseBonusVal(44, 5, 12)` | **la iniciativa de Fortellon**, base 5 bonus 12 |
| `FightHandler.cs:1274` | `AddSimpleVal(34, 12)` | valor copiado de la captura |
| `FightHandler.cs:1288` | `AddSimpleVal(27, 1)`, `(28, 1)` | valores copiados |
| `FightHandler.cs:1292` | `AddSimpleVal(93, 3)` | valor copiado |
| `FightHandler.cs:1195`, `:1174`, `:1186` | `300` | duración de turno; debe ser una constante con nombre |
| `FightHandler.cs` (varios) | `subAreaId = 95` por defecto | subárea de la captura |
| `HaapiServer.cs:149` | `"build_override": "3.6.4"` | versión del cliente |

### Pasos a seguir

**Paso 1 — Arreglar el dato y hacerlo verificable** *(imprescindible para D3)*
1. `DatabaseManager.cs:193`: `8` → `9`.
2. Añadir migración idempotente junto a las que ya existen (`DatabaseManager.cs:311-327`):
   ```sql
   UPDATE Characters SET Breed = 9 WHERE Id = 13825558 AND Breed = 8;
   ```
3. Añadir al arranque una **comprobación de coherencia**: decodificar el `Look` de cada personaje, extraer el breed que lleva dentro y compararlo con la columna `Breed`. Si no coinciden, `WARN` bien visible en el log. Esto convierte un bug silencioso en uno ruidoso.

**Paso 2 — Quitar los valores por defecto que mienten**
1. En `GameState`, poner a `0` / `null` todo lo que deba venir de la BD: `Breed`, `MapId`, `CellId`, `CharacterRemainingPoints`, `Kamas`, `CharacterLevel`, `CharacterName`.
2. Que `CharacterSelectionHandler` los cargue **obligatoriamente** de la fila del personaje al seleccionarlo.
3. Si al entrar en combate alguno sigue a `0`, **abortar el combate con un error explícito** en vez de continuar con basura. Es preferible un fallo claro a un combate con datos inventados.

**Paso 3 — Sustituir los respaldos inventados por errores**
En todo el código de combate, cambiar el patrón:
```csharp
int mId = fighter.MonsterId > 0 ? fighter.MonsterId : 3273;   // MAL
```
por:
```csharp
if (fighter.MonsterId <= 0)
    throw new InvalidOperationException($"Luchador {fighter.Id} sin MonsterId; no se puede construir jxx");
```
Lo mismo con el look por defecto, el `spellId = 32435` y el `subAreaId = 95`. **Un respaldo silencioso convierte un fallo de datos en un bug de comportamiento.**

**Paso 4 — Constantes con nombre para lo que sí es constante del protocolo**
Algunos valores del bloque de características (`34=12`, `27=1`, `28=1`, `93=3`, `107/120-125/141-143=100`) y la duración de turno (`300`) sí son constantes del protocolo, no datos del personaje. Sácalos a una clase `ProtocolConstants` con nombres y un comentario que diga **de qué frame de la captura salen**. Así se distingue de un vistazo "constante del protocolo" de "dato que se me olvidó parametrizar".

**Paso 5 — Guardia contra regresiones**
Añadir un test que recorra los `.cs` del Launcher y **falle** si encuentra cualquiera de estos literales fuera de `BasePayloads.cs` y `TransitionPayloads.cs` (que están pendientes de migrar y se excluyen explícitamente):

```
670668947750   -20003   3273   3256   32435   "Fortellon"   154010883   154010884
```

Es la única forma de que estos datos no vuelvan a colarse en la siguiente iteración.

**Paso 6 — (fuera del alcance del combate, anotado)** `BasePayloads.WorldEnteringPackets` y `TransitionPayloads.cs` deben migrarse a construcción orgánica desde SQLite, igual que se hizo con `kri`, `irm` e `isf`. Mientras sigan siendo volcados literales, el cliente seguirá rotulando el mapa y las misiones de otra sesión. Está detallado en la §2.bis de [PLAN_MIGRACION_3.6.8.8.md](PLAN_MIGRACION_3.6.8.8.md).

---

## Reglas invariantes (no romper)

1. **Nada de `Task.Delay` entre paquetes de una ráfaga.** Hay tres ahora mismo (`FightHandler.cs:1015`, `:1024`, `:1061`) que hay que quitar. El servidor real emite las ráfagas con microsegundos de separación. Los únicos temporizadores legítimos son los de turno y colocación (D7).
2. **Ningún valor de la captura hardcodeado.** Nada de `Fortellon`, `670668947750`, `3273`, `32435`, `-20003`. Todo de `GameState`, `world.db` o el `FightInstance`.
3. **Opcodes de 3 letras** siempre.
4. **Un solo `kkz` con todos los luchadores**, nunca uno por luchador.
5. Los ids de luchador de monstruo son secuenciales negativos por combate (`-1, -2, -3…`); el `contextId` del grupo solo va en `kkq.f1`, `jya.f6`, `jpf.f1.f3` y el `jyf` del equipo de monstruos.
6. **Loguear a `Program.LogDebug`**, no a `Console.WriteLine`, o no llega a `emulator_debug.log`.

---

## Orden de trabajo y criterios de verificación

| Orden | Tarea | Criterio de aceptación |
|---|---|---|
| 1 | **D3 paso 1** (corregir `Breed` en BD) | 2 líneas; desbloquea la verificación de D2 |
| 2 | **D0** geometría | El test con los caminos reales pasa: todas las distancias consecutivas = 1 |
| 3 | **D6** carrusel | El orden pintado coincide con el orden real de turnos |
| 4 | **D7** temporizador | Al agotarse los 30 s el turno pasa solo al siguiente; el contador no baja de 0 |
| 5 | **D5** vida y nombre | Al entrar en combate mantienes tus PV reales y al pasar el ratón sale KEKA-BRON |
| 6 | **D1** movimiento | El personaje **camina** celda a celda hasta el destino y los PM bajan en el marcador |
| 7 | **D2** lanzar hechizo | Se ve la animación, el monstruo pierde vida, tus PA bajan |
| 8 | **D4** IA | Un pío se acerca lo justo, ataca, y con poca vida ataca y huye |
| 9 | **D8** resto (pasos 2-5) | El test de literales prohibidos pasa; ningún valor por defecto enmascara un fallo de carga |

> **D3 paso 1 va el primero** porque es trivial (dos líneas) y sin él no puedes verificar D2: lanzarías hechizos de una clase que no es la tuya. Los pasos 2-5 de D8 son limpieza de fondo y van al final, cuando el combate ya funcione.

Con la instrumentación S→C de la Fase 0 del plan anterior activa, cada punto se diagnostica leyendo el log; sin ella se trabaja a ciegas.

---

## Apéndice — Cómo volcar cualquier estructura de la captura

```bash
py C:/Jondo/scripts/fightdump.py jtx jvm joo jud juc
```

Imprime el árbol protobuf de cada aparición de esos opcodes en la captura de referencia, con su número de frame. **Ante cualquier duda sobre un campo, vuelca y replica; no inventes.**
