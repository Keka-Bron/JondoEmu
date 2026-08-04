# Plan de implementación — Combate PvM en el emulador Jondo (Dofus 3.6.4.3)

**Fecha:** 2026-08-03
**Autor del análisis:** sesión de auditoría sobre el pcap de referencia + `world.db`
**Destinatario:** el modelo que implementará los cambios (Gemini Flash Pro 3.6)
**Estado de partida:** el combate arranca (se ve la rejilla azul/roja, el carrusel de turnos, el botón LISTO y la cuenta atrás de 45 s), pero:
1. no hay música de combate;
2. no se carga el mapa de combate (se sigue viendo el mapa de roleplay con sus decorados y elementos interactivos);
3. los retratos del carrusel de turnos salen recortados/con zoom;
4. al agotarse los 45 s la pelea se congela: no llega el turno de nadie y el emulador vuelve a emitir paquetes de roleplay;
5. las estadísticas son incorrectas (monstruos con 0 PV y nivel 0, "esquiva PA/PM = 64").

Este documento es **autocontenido**. Todo lo que afirma está verificado contra evidencia reproducible (§9).

---

## 0. Reglas de oro (no negociables)

1. **Única fuente de verdad del protocolo:** `C:\Jondo\lanzar combate y combatir hasta ganar y cerrar pantalla fin combate.pcapng`, **stream TCP 1**, puerto servidor **5555**. Es la captura compatible con el cliente instalado (v3.6.4.3).
2. **PROHIBIDO usar** `entrar en combate-esperar segundos de preparacion-...pcapng` (ofuscación de v3.6.8.8: opcodes disjuntos, `jxm/jzy/kaf/jyg/...`). Todo lo que se "aprendió" de ahí es falso para este cliente.
3. **PROHIBIDO usar `dofus3_sniffer_complete.proto` como verdad absoluta.** Sirve para conocer el *tipo* de cada campo (varint / longitud / enum), pero está extraído de otro build: hay campos desplazados (p. ej. `jtx` usa el campo **34** en la captura y el `.proto` declara el 35). **Ante conflicto, manda el pcap.**
4. Los opcodes de Dofus 3 tienen **siempre 3 letras**. `NetworkEnvelope.BuildGameNodePacket` ya emite un `[WARN]` si no es así; no silenciarlo.
5. **Nada de plantillas binarias ni parcheo por offsets.** Todo se construye con `ProtoMessage` desde SQLite + estado. (Los varints son de longitud variable: parchear offsets corrompe el mensaje.)
6. **Nada de `Task.Delay` entre paquetes.** El servidor real emite ráfagas back-to-back con microsegundos de separación. Lo que sí es estricto es el modelo petición/respuesta (§3).
7. Cuando este documento dice "vacío" significa **`Any` sin campo 2** (solo `type_url`). `BuildGameNodePacket` ya lo hace bien si se le pasa `Array.Empty<byte>()`.

---

## 1. Resumen de defectos encontrados

| # | Defecto | Archivo | Síntoma que causa | Sev. |
|---|---|---|---|---|
| **D1** | El combate se libra en el **mapa de roleplay**. En el juego real se libra en una **arena** (otro `mapId` distinto, ya presente en `world.db`). | `FightHandler.InitiateFightFromMobCollision` | Síntomas 1 y 2 (sin música, sin mapa de combate) | **Crítica** |
| **D2** | El arranque de turno usa `jwo` + `jox`. El cliente espera el handshake `juu` → `jwe` → `jut` → `jwl`. | `FightHandler.SendTurnStart` / `HandleTurnReady` | Síntoma 4 (pelea congelada) | **Crítica** |
| **D3** | La vida se manda en los statId **27/28** (que son otra cosa). La vida real es el **statId 0** (entrada sin campo `f5`). | `FightHandler.BuildFighterShowBytes` | 0 PV + "esquiva PA/PM = 64" | **Crítica** |
| **D4** | Se manda statId **70** como nivel. **No existe.** El nivel/grado del monstruo va en `f7` de la info de luchador. | idem | "Nivel 0" | Alta |
| **D5** | `f7` (id de monstruo + grado) se coloca en el nodo equivocado y contiene el **boneId** en vez del **monsterId**. | idem | Síntoma 3 (retratos con zoom) | Alta |
| **D6** | El jugador se serializa con la **variante de monstruo** del submensaje de característica. | idem | Estadísticas raras del jugador | Alta |
| **D7** | Solo se envían 6 características; el servidor real envía **36**. | idem | Resistencias/daños a 0 o basura | Media |
| **D8** | `jyf`: las casillas de colocación se escriben como varints en `f1.f8`, que es una **lista de miembros de equipo**. | `BuildPlacementPossiblePositionsPackets` | Equipos sin miembros en la UI | Alta |
| **D9** | `jyi`: los equipos están **invertidos** (f1 = monstruos, f2 = jugador en el real). | `BuildPlacementPositionsListBytes` | El jugador ve el cluster equivocado | Alta |
| **D10** | `igs` se rellena; en el real va **vacío**. | `BuildIgsPacket` | Ruido / posible descarte | Media |
| **D11** | `jya.f4` se envía como submensaje (subárea); en el real no lo es. | `SendFightStarting` | Campo descartado | Media |
| **D12** | Se envía `jyg` inventado (viene de la captura prohibida). | `HandleFightMapLoad` | Ruido | Media |
| **D13** | `kkz` se envía **uno por luchador**; el real manda **uno con todos**. | varios | Desincronización de posiciones | Media |
| **D14** | `hoy` se parsea como estructura anidada; en realidad es `{f1 = contextId}` plano → siempre sale 0 → se pelea contra un grupo de mobs **al azar**. | `HandleFightOptionToggleRequest` | Combate contra el mob equivocado | Alta |
| **D15** | Mapeo de mensajes de cliente equivocado: `jwb` se trata como "pasar turno" (es S→C), `jza` como "lanzar hechizo" (es LISTO). No se manejan `jub`, `jxw`, `jwe`, `jrb`. | `HandleFightMessageAsync`, `GameNodeProxy` | No se puede lanzar hechizos ni pasar turno | **Crítica** |
| **D16** | `jvn` (lista de hechizos de combate) se envía **vacío**. | `HandleTurnReady` | El jugador no tiene hechizos en combate | Alta |
| **D17** | No se envía `jwm` (resincronización completa de luchadores al empezar). | `HandleTurnReady` | Stats sin refrescar al arrancar | Alta |
| **D18** | Daño/variación de puntos se emiten con `jwu`/`jys`/`kkz` con campos inventados. Lo real es `jtx`/`jvm` dentro de secuencias `jud … juc`. | `SendLifePointsVariation`, `SendPointsVariation` | Ni daño ni PA/PM visibles | Alta |
| **D19** | `Monsters.Spells` está **vacío en los 5134 monstruos** de `world.db`. | `DatabaseManager.GetMonsterGradeStats` | La IA no puede atacar nunca | Alta |
| **D20** | `BuildJpfPacket` lleva incrustados los datos de la sesión capturada (`535`, `3256`, `3273`, `3`, `3`). | `BuildJpfPacket` | Destruye un contexto con datos ajenos | Media |
| **D21** | Las casillas de colocación se generan repartidas por todo el mapa; en el real son **dos clusters de 8 celdas** enfrentados. | `FightInstance.GeneratePlacementCells` | Colocación absurda | Media |
| **D22** | `FightHandler` loguea con `Console.WriteLine` → no llega a `emulator_debug.log`. | todo el archivo | Imposible diagnosticar | Media |
| **D23** | `GradeIndex` se usa como índice 0-based del array `Grades`, pero `MembersJson` trae el campo `grade` con la numeración del juego. | `FightHandler` / `GetMonsterGradeStats` | Grado y stats desplazados | Baja |

---

## 2. HALLAZGO CAPITAL — El combate ocurre en otro mapa (la arena)

Esto es lo que explica los síntomas 1 y 2, y nunca se había detectado.

### Evidencia

En la captura, el servidor manda en la **ráfaga 1**:

```
joh { f2 = 153891076 }        <- mapId DURANTE el combate
```

y el cliente responde pidiendo **ese** mapa: `kkr { f1 = 153891076 }`.
Al terminar la pelea, el servidor manda `joh { f2 = 154010883 }` y el cliente pide `kkr`+`jqf` de **154010883** (vuelta al roleplay).

En `world.db`:

| mapId | PosX | PosY | SubAreaId | celdas transitables |
|---|---|---|---|---|
| **154010883** (roleplay) | -2 | -3 | 450 | 101 |
| **153891076** (arena) | 0 | 0 | 450 | 71 |

La subárea 450 es **"Camino de las Almas"** (Incarnam) — es decir, la captura de referencia se hizo exactamente en el mismo mapa que aparece en las capturas de pantalla del problema.

**Prueba definitiva** (cruzando las celdas de la captura con `map_walkable_cells.json`):

```
casillas de colocación del jyi real: 8/8 transitables en la ARENA, solo 3/8 en el mapa de roleplay
posiciones reales de los luchadores: 6/6 transitables en la ARENA, 0/6 en el mapa de roleplay
```

Es decir: **los luchadores estaban en celdas que ni siquiera existen en el mapa de roleplay.** El combate se libra en otro mapa.

### Cómo se identifica una arena (regla verificada)

Una arena es un mapa que cumple **las tres condiciones a la vez**:

1. `MapPositions.PosX == 0 && PosY == 0`
2. `MapTemplates.Data.m_flags == 69262589`
3. pertenece a la **misma subárea** que el mapa de roleplay

El `m_flags` es el discriminador fino y está comprobado: en las subáreas 450 (Camino de las Almas) y 95 (Ciudad de Astrub), **las 8 y las 21 arenas respectivamente tienen ese flag exacto**, y **ningún** mapa de roleplay lo tiene (los de roleplay llevan 68277437, 70382781, 78771389, 70374557, 68277405…). A escala global: 2.738 mapas tienen `m_flags == 69262589`, de los cuales 2.669 están en (0,0); y de los 3.357 mapas en (0,0), 688 llevan otro flag (interiores de casas, talleres, salas de mazmorra) y hay que **descartarlos**, porque si no acabaríamos peleando dentro de una casa.

Ejemplos de pools:

| Subárea | Arenas | Ids |
|---|---|---|
| 450 · Camino de las Almas | 8 | 153883648 (62 celdas), 153883906 (69), 153883908 (58), 153883910 (65), 153890816 (68), 153891074 (60), **153891076 (71)**, 153891078 (87) |
| 95 · Ciudad de Astrub | 21 | 69993731 (119), 188749315 (134), 188750339 (105), 188751363 (107), 188752387 (77), 188753411 (82), 191102984 (136), 191104008 (111), 191105032 (102), 191106056 (107), 191107080 (102), 192419842 (96), 192419844 (84), 192419846 (89), 192419850 (138), 192679426 (81), 192808964 (51), 192809218 (54), 192809988 (58), 192941570 (76), 196089354 (239) |

### Parejas confirmadas (4)

De dos capturas distintas del juego real:

| Captura | Mapa de roleplay | Subárea | Arena usada | Δ |
|---|---|---|---|---|
| `lanzar combate…` | 154010883 (-2,-3) | 450 | **153891076** | −119807 |
| `creacion personaje y tutorial…` | 241438721 (1,1) | 536 | **241438725** | +4 |
| `creacion personaje y tutorial…` | 241439745 (1,1) | 536 | **241439749** | +4 |
| `creacion personaje y tutorial…` | 241441793 (1,1) | 536 | **241441797** | +4 |

**4/4 cumplen el invariante** (arena = mapa (0,0), flag 69262589, misma subárea).

**No hay regla aritmética.** El `+4` que se ve en el tutorial es local: solo se cumple en el **2,6 %** de los mapas del juego, y en la subárea 450 no se cumple en ninguno (además, el tutorial está en el Templo de Incarnam, una zona pequeña y especial). Es decir: **la asociación oficial mapa↔arena no se puede derivar de los datos disponibles**; el servidor real la tiene en una tabla que no está en `world.db`.

### Qué implementar

En `FightInstance` añadir `RoleplayMapId` y `ArenaMapId`, y una función de selección:

```csharp
// Devuelve el mapId de la arena de combate para un mapa de roleplay.
// Arena = mapa de la MISMA subárea, en (0,0), con m_flags 69262589 y celdas cargadas.
// (m_flags debe cachearse al arrancar; hoy MapInfo no lo guarda: añadirlo.)
public static long ResolveArenaMapId(long roleplayMapId)
{
    if (!MapManager.Maps.TryGetValue(roleplayMapId, out var info)) return roleplayMapId;

    var arenas = MapManager.Maps.Values
        .Where(m => m.SubAreaId == info.SubAreaId
                 && m.PosX == 0 && m.PosY == 0
                 && m.Flags == 69262589)                       // <- discriminador clave
        .Select(m => m.MapId)
        .Where(id => MapManager.WalkableCells.TryGetValue(id, out var c) && c.Count >= 40)
        .OrderBy(id => id)
        .ToList();

    if (arenas.Count == 0) return roleplayMapId;                // sin arena: degradar

    // Preferencia observada en 3 de las 4 parejas conocidas.
    if (arenas.Contains(roleplayMapId + 4)) return roleplayMapId + 4;

    // Si no, elección determinista: el mismo mapa usa siempre la misma arena.
    return arenas[(int)(Math.Abs(roleplayMapId) % arenas.Count)];
}
```

> **Nota honesta y consecuencia práctica:** al no poder derivar la asociación oficial, el emulador elegirá una arena **válida pero no necesariamente la misma que el servidor de Ankama**. Y no pasa nada: el cliente carga la arena que le digamos en `joh`, nos la pide con `kkr` y la renderiza. Lo único innegociable es la **coherencia interna** — que todas las celdas que enviemos (`jyi`, `kkz`, `jxx`, `jox.f3`) sean transitables **en la arena elegida**.
> Si en el futuro se quiere clavar la oficial, basta con capturar 3-4 combates más en zonas normales (no el tutorial) y buscar el patrón con `scripts/johtrace.py`.

**Todo lo que usa el mapId durante el combate debe usar `ArenaMapId`, no el de roleplay:**
- `joh` de la ráfaga 1 → `ArenaMapId`
- `jox.f3` (colocación y turnos) → `ArenaMapId`
- las celdas de colocación → `MapManager.WalkableCells[ArenaMapId]`
- el `joh` del **fin** del combate → mapa de roleplay original (guardarlo en `FightInstance.RoleplayMapId`)
- `MapLoadHandler.HandleMapLoadRequest` en modo combate ya delega en `FightHandler.HandleFightMapLoad`; asegurarse de que el `kkr` que llega trae el id de la arena.

---

## 2.bis. REQUISITO PREVIO P0 — el emulador y el cliente no están en el mismo mapa

Antes de tocar nada del combate hay que resolver esto, porque **envenena todo el cálculo de la arena y de las casillas**.

### El problema

La entrada al mundo se sirve reproduciendo una **ráfaga capturada del juego real** (`BasePayloads.WorldEnteringPackets`, un `byte[]` literal usado en `GameNodeProxy.cs:107`), a la que solo se le parchean algunas cosas al vuelo (id de personaje, `joh`, `irm`…). Todo lo demás sigue siendo el estado de la sesión original: el mapa, el nombre de zona, las misiones activas…

Consecuencia observable: **el cliente rotula "Incarnam (Camino de las Almas) -2,-3" aunque el jugador esté en otro sitio**, porque ese dato viene incrustado en la ráfaga.

Y hay una divergencia numérica comprobable en el propio código:

| Fuente | mapId | Posición | Subárea |
|---|---|---|---|
| `GameState.MapId` (valor por defecto, `GameState.cs:18`) | **154010884** | (-2, -4) | 20663 (sin nombre) |
| Ráfaga capturada + NPC sembrado (`DatabaseManager.cs:234-241`) | **154010883** | (-2, -3) | 450 · Camino de las Almas |

Es decir: el emulador cree que el jugador está en un mapa y el cliente ha cargado **otro** (justo el de al lado).

### Por qué rompe el combate

`InitiateFightFromMobCollision` recibe el `mapId` desde `MapChangeHandler`/`GameState`. Si ese id no es **exactamente** el que el cliente tiene cargado:

- `ResolveArenaMapId` elegirá la arena de la subárea equivocada;
- las casillas de colocación se calcularán sobre celdas que no existen en el mapa del cliente;
- y volveremos al mismo síntoma de ahora (rejilla suelta sobre un mapa que no le corresponde), pero con otra causa.

### Qué hacer

1. **`joh` es la única autoridad.** El emulador debe registrar el `mapId` que envía en `joh` como "mapa cargado por el cliente" y usar **ese** en todo el flujo de combate. Como comprobación cruzada, el `kkr` que responde el cliente trae ese mismo id: si no coincide con `GameState.MapId`, emitir un `WARN` bien visible (con la Fase 0 ya se verá en el log).
2. **Alinear el valor por defecto**: `GameState.MapId` debe salir de la fila del personaje en `Characters`, no de un literal. Si el personaje no tiene mapa guardado, usar el mismo id que va en la ráfaga de entrada (154010883), nunca uno distinto.
3. **A medio plazo** (fuera del alcance del combate, pero anotado): migrar `BasePayloads.WorldEnteringPackets` a construcción orgánica desde SQLite, empezando por el mapa/celda y las misiones activas, siguiendo el patrón que ya usan `kri`, `irm` e `isf`. Mientras siga siendo un volcado literal, cualquier funcionalidad que dependa del estado real del jugador arrastrará este mismo error.

**Verificación:** en el log de la Fase 0, el `mapId` de `joh` (S→C), el de `kkr` (C→S) y `GameState.MapId` deben ser el mismo número antes y después de entrar en combate.

---

## 3. Secuencia completa verificada (S→C y C→S)

Extraída frame a frame del stream TCP 1. `S` = servidor, `C` = cliente.

```
 88  C  jpp {}                                   <- el cliente lanza el combate
 89  C  hoy { f1 = -20003 }                      <- contextId del grupo de mobs (¡campo PLANO!)

        ══ RÁFAGA 1 (respuesta inmediata) ══
 90  S  joq {}                                    (vacío)
 91  S  jpf { ... contexto de roleplay a destruir ... }
 92  S  kkq { f1 = -20003 }
 93  S  kkp {}
 94  S  kkm { f1 = 1 }
 95  S  kri  (818 B, hoja de personaje completa)
 96  S  joh { f2 = 153891076 }                   <- ¡ID DE LA ARENA!
 96  S  lor { f1 = 120, f2 = <epoch ms> }
 97  S  krp { f1 = 278, f2 = 77, f3 = 77 }
 98  S  lsy {}
 99  S  kkz { f1{ f2=286, f3=<playerId>, f5=3 } }  (solo el jugador)

        ══ RÁFAGA 2 (+40 ms, todo en un segmento) ══
101  S  jyf  (equipo jugador, 40 B, CON miembros)
101  S  jyf  (equipo monstruos, 24 B)
101  S  kkz  (32 B: TODOS los luchadores)
101  S  kkz  (32 B: idéntico, se repite)

107  C  igx {}                                    <- el cliente pide el mapa de combate
107  C  kkr { f1 = 153891076 }

        ══ RÁFAGA 3 (disparada por kkr) ══
108  S  igs {}                                    <- ¡VACÍO!
109  S  jya { f1=300, f2=<playerId>, f3=4, f4=<2B>, f6=-20003 }
110  S  jyj { f2=1, f4=4, f5=443, f6=1 }
110  S  jxx  (400 B, jugador)
110  S  jxx  (336 B, monstruo)
110  S  jyi  (38 B, celdas de colocación de los dos equipos)
110  S  jyf  (16 B, equipo jugador, f8 vacío)
110  S  jyk ×4 { f3=<0..3>, f5=300 }
110  S  jxe  (lista de turnos)
111  S  jwo {}
112  S  jox { f1=450, f2{f1=-3, f2=-2}, f3=153891076 }   <- cuenta atrás 45,0 s

        ══ FASE DE COLOCACIÓN (el cliente conduce) ══
131  C  jyz { f1 = 299, f2 = <playerId> }        <- cambiar de casilla
132  S  kkz  (32 B: TODOS los luchadores)         <- respuesta 1:1, se repite ×7

182  C  jza { f1 = 1 }                            <- botón LISTO

        ══ ARRANQUE DEL COMBATE ══
183  S  jys { f1 = 1, f2 = <playerId> }
184  S  jwu { f3 = <playerId> }
185  S  lsy {}
186  S  kkz  (todos)
187  S  jyn {}
188  S  jvn  (96 B: LISTA DE HECHIZOS del jugador + barra de accesos)
189  S  jwb { f1 = 1 }                            <- número de RONDA
190  S  jwu { f3 = <playerId> }
191  S  jud { f1=8, f2=<playerId> }
191  S  jwm  (944 B: RESINCRONIZACIÓN COMPLETA DE LUCHADORES)
191  S  juc { f1=8, f2=<playerId>, f3=3 }
192  S  jud / jtx (45 B) / jxf (79 B) / juc      <- modificador de combate (Bejerit)

        ══ HANDSHAKE DE TURNO (¡esto es lo que falta!) ══
196  S  juu { f1 = <fighterId> }                  <- fin/aviso de turno
197  C  jwe { f2 = 1 }                            <- EL CLIENTE CONFIRMA
200  S  jut { f1 = 300, f4 = 1, f5 = <fighterId> } <- EMPIEZA EL TURNO
201  S  jwl {}                                    <- ya se puede jugar

        ══ ACCIONES DEL JUGADOR ══
223  C  joi  (mover)
224  S  jud{f1=4} / joo / jud{f1=3} / jvm / juc / jtx / juc
252  C  jub { f1 = 13391, f2 = 371 }              <- LANZAR HECHIZO (id, celda)
254  S  jud / jtx / jud / jvm / juc / jtx / jtx / jud / jvm / juc / jxf / juc
279  C  jxw {}                                    <- PASAR TURNO

        ══ CAMBIO DE TURNO ══
281  S  jwk { f3 = <playerId> }                   <- fin de turno
282  S  jud / jwu / jud / jvm / juc / jud / jvm / juc / juc
283  S  juu { f1 = <playerId> }
285  C  jrb { f1 = 12, f2 = 1 }                   <- ack de secuencia
286  C  jwe { f2 = 1 }
288  S  jut { f1 = 290, f3 = 1, f4 = 1, f5 = -1 } <- turno del MONSTRUO
289  S  jud / joo / jud / jvm / juc / jtx / juc   <- el monstruo se mueve
290  S  jwk { f3 = -1 }
291  S  jud / jwu / jud / jvm / juc / jud / jvm / juc / juc
292  S  juu { f1 = -1 }
294  C  jwe { f2 = 1 }
295  S  jwb { f1 = 2 }                            <- RONDA 2
296  S  jwu { f3 = <playerId> }
297  S  jut { f1=300, f2=91, f4=2, f5=<playerId> }
299  S  jwl {}

        ══ FIN DEL COMBATE ══
328-339 S  irx, isf, irx, isf, kri, krh, jwf (101 B), juo (76 B), lxs, kkp,
           kkm, krb, ilc, joh { f2 = 154010883 }, lor, kri, jpf ×2
341  C  kkr { f1 = 154010883 } + jqf { f1 = 154010883 }
```

### Tabla de opcodes de cliente (C→S) — **corrige D15**

| Opcode | Significado | Carga | Manejador correcto |
|---|---|---|---|
| `jpp` | dispara el combate (tras el movimiento) | vacío | ya existe (`MapChangeHandler.HandleMovementConfirm`) |
| `hoy` | confirmación de interacción con el grupo | `{ f1 = contextId }` **plano** | `HandleFightOptionToggleRequest` |
| `igx` + `kkr` | pide el mapa de combate | `kkr{f1=mapId}` | `MapLoadHandler` → `HandleFightMapLoad` |
| `jyz` | cambiar casilla en colocación | `{ f1 = celda, f2 = fighterId }` | `HandlePlacementCellChangeRequest` |
| `jza` | botón LISTO | `{ f1 = 1 }` | `HandleTurnReady` |
| `jwe` | **confirma el turno** (respuesta a `juu`) | `{ f2 = 1 }` | **NUEVO** → `HandleTurnReadyAck` |
| `jrb` | ack genérico de secuencia | `{ f1 = n, f2 = 1 }` | **NUEVO** → ignorar sin romper |
| `joi` | mover en combate | ruta | `HandleCombatMovementRequest` ✔ |
| `jub` | **lanzar hechizo** | `{ f1 = spellId, f2 = celdaObjetivo }` | **NUEVO** → `HandleSpellCastRequest` |
| `jxw` | **pasar turno** | vacío | **NUEVO** → `HandlePassTurnRequest` |
| `kod` | keepalive | — | ignorar |

En `GameNodeProxy.cs:432` la condición de enrutado debe pasar a incluir `jyz, jza, jwe, jrb, jub, jxw, hoy` (y quitar `jwb`, que es S→C).

---

## 4. Estructuras exactas de los mensajes (decodificadas del pcap)

> Todos los árboles son el **contenido del campo `value` del `Any`**; el envoltorio lo pone `BuildGameNodePacket`.

```
joq  = vacío          kkp = vacío          lsy = vacío
igs  = vacío          jwo = vacío          jyn = vacío          jwl = vacío

kkm  { f1 = 1 }
lor  { f1 = 120, f2 = <epoch ms> }
krp  { f1 = 278, f2 = 77, f3 = 77 }
kkq  { f1 = <contextId del grupo de mobs> }
joh  { f2 = <mapId> }                       <- ARENA durante el combate
jyj  { f2 = 1, f4 = 4, f5 = 443, f6 = 1 }
jyk  { f3 = <0..3>, f5 = 300 }              (4 mensajes, uno por opción)
jys  { f1 = 1, f2 = <playerId> }
jwb  { f1 = <número de ronda> }
jwu  { f3 = <fighterId> }                   <- ¡el campo es el 3, no el 1!
jwk  { f3 = <fighterId> }                   fin de turno
juu  { f1 = <fighterId> }
jut  { f1 = 300, f4 = <ronda>, f5 = <fighterId> }   (f1 = décimas de segundo)
jox  { f1 = 450, f2 = { f1 = -3, f2 = -2 }, f3 = <arenaMapId> }   colocación
jud  { f1 = <tipo de secuencia>, f2 = <autorId> }
juc  { f1 = <tipo>, f2 = <autorId>, f3 = <n> }
jya  { f1 = 300, f2 = <playerId>, f3 = 4, f6 = <contextId> }
     ^ f3 = enum tipo de combate (4 = PvM). NO enviar f4 como submensaje.
kkz  { repeated f1 = { f2 = <celda>, f3 = <fighterId>, f5 = <dirección> } }
     ^ UN SOLO mensaje con TODOS los luchadores (D13)
jxe  { repeated f3 = { f2 = { f1 = <fighterId> } } }   (orden de turnos)
jyi  { f1 = { f1 = <celdas equipo 1 / MONSTRUOS>,
              f2 = <celdas equipo 0 / JUGADOR> } }
     ^ los dos campos son varints de celda CONCATENADOS (packed sin tag por elemento).
       ¡Observado invertido respecto a lo que hace el emulador! (D9)
       Valores reales: f1 = 411,424,439,397,410,426,438,453
                       f2 = 286,298,326,271,285,299,312,313
jyf (equipo jugador, ráfaga 2)
     { f1 = { f2 = <playerId>, f7 = 1,
               f8 = { f2 = { f2 = <playerId>,
                             f4 = { f2 = <bytes del look>, f3 = <breed> } } } },
       f2 = 300 }
jyf (equipo monstruos, ráfaga 2)
     { f1 = { f2 = <contextId>, f4 = 1, f6 = 1, f7 = 1, f8 = {} }, f2 = 300 }
jyf (ráfaga 3, uno solo)
     { f1 = { f2 = <playerId>, f7 = 1, f8 = {} }, f2 = 300 }
     ^ f8 = LISTA DE MIEMBROS, nunca casillas (D8)
```

### `jxx` (mostrar luchador) — estructura real

```
jxx = { f2 = {
          f1 = { f1 = 0, f2 = <celda>, f5 = <dirección> },         // posición
          f2 = {                                                    // entidad
                 f1 = { f1 = <boneId>, f3 = 3 },                    // look (lkr)
                 f3 = {                                             // info de luchador
                        f1 = { f2 = <teamId>, f3 = 1,
                               f4 = { f1 = <posición>, f3 = <fighterId> } },
                        f2 = <playerId ó 0 si es monstruo>,
                        f4 = { ... bloque de características ... },
                        f7 = { f2 = { f1 = <monsterId>, f2 = <grado>, f5 = 3 } }
                      }                                             // ^ SOLO monstruos
               },
          f3 = <fighterId>
        } }
```

**Correcciones respecto al código actual (`BuildFighterShowBytes`):**
- `f7` va **dentro de `f3`** (info de luchador), no dentro de la entidad. Y lleva el **`monsterId` de la tabla `Monsters`**, no el boneId.
  *Verificado:* la captura lleva `f7{f2{f1=3273, f2=3}}` y `f1{f1=3256}`; en `world.db`, `Monsters.Id = 3273` tiene `Look = '{3256}'`. Es decir **f1 = bone del Look, f7.f2.f1 = Id del monstruo, f7.f2.f2 = grado**. Ese es el dato con el que el cliente busca el retrato del carrusel: al recibir un boneId en vez de un monsterId, no encuentra retrato y renderiza el modelo 3D en crudo → **imagen recortada/con zoom (síntoma 3)**.
- **Quitar `f4 = 100` del mensaje de look**: en el `.proto`, `lkr.f4` es de tipo `bytes`; escribir un varint ahí es un desajuste de wire type (el cliente lo descarta como campo desconocido). La escala del `Look` (`{637|||100}`) **no se envía**; el real no la lleva.
- Los IDs de luchador de monstruo siguen siendo secuenciales negativos por combate: `-1, -2, -3…` (**correcto en el código actual, no tocar**). El `contextId` del grupo (p. ej. `-20003`) se usa **solo** en `kkq.f1`, `jya.f6`, `jpf.f1.f3` y en el `jyf` del equipo de monstruos.

---

## 5. El bloque de características (`jxx.f2.f2.f3.f4` y el mismo dentro de `jwm`)

Esta es la causa exacta de los síntomas de estadísticas. **Diagnóstico confirmado numéricamente:**

> El cliente muestra "esquiva PA 64 / esquiva PM 64" en los píos. En `world.db`, el monstruo **493 "Pío amarillo"** (look `{637|||100}`) tiene en el **grado 3**: `lifePoints = 64`, `actionPoints = 3`, `movementPoints = 3`, `level = 13`.
> Es decir: **el 64 es la VIDA del pío**, que el emulador está escribiendo en los statId 27 y 28 (que no son vida). Los PA/PM salen bien (3/3) porque los statId 1 y 23 sí son correctos. Y el nivel sale 0 porque se envía en el statId 70, que no existe.

### Formato

```
f4 = { f1 = 2, repeated f4 = <entrada> }
```

Cada entrada es `{ <variante>, f5 = <statId> }`, y **si se omite `f5` el statId es 0 = PUNTOS DE VIDA**.

| Variante | Cuándo | Forma |
|---|---|---|
| `f2 = { f2 = valor }` | **monstruos** (todas sus características) | valor simple |
| `f3 = { f2 = base, f7 = bonusEquipo }` | **jugador**, características normales | base + bonus |
| `f4 = { f4 = valor, f7 = bonusEquipo }` | **jugador**, solo statId 1 (PA) y 23 (PM) | innato + bonus |

> Es exactamente el mismo esquema que ya usa `StatsHandler.CreateStatField` / `CreateInnateStatField` para el `kri`. **Reutilizar esos helpers** (extrayéndolos a un sitio común) en vez de duplicar lógica: así la hoja de personaje y el combate no pueden divergir.
> Si el valor es 0 se emite el submensaje **vacío** (`f3 = {}`), igual que hace el servidor oficial.

### Lista canónica de las 36 entradas, **en este orden exacto**

(Idéntico en el `jxx` del jugador y en el del monstruo.)

| # | statId | Valor del monstruo en la captura | Valor del jugador en la captura | Qué poner |
|---|---|---|---|---|
| 1 | 1 | 2 | 6 | PA (`MaxAP`) |
| 2 | 23 | 3 | 3 | PM (`MaxMP`) |
| 3 | 37 | — | — | vacío |
| 4 | 33 | — | — | vacío |
| 5 | 35 | — | — | vacío |
| 6 | 36 | — | — | vacío |
| 7 | 34 | 12 | — | copiar tal cual (12 en monstruos, vacío en jugador) |
| 8 | 58 | — | — | vacío |
| 9 | 54 | — | — | vacío |
| 10 | 56 | — | — | vacío |
| 11 | 57 | — | — | vacío |
| 12 | 55 | — | — | vacío |
| 13 | 85 | — | — | vacío |
| 14 | 87 | — | — | vacío |
| 15 | 101 | — | — | vacío |
| 16 | 27 | 1 | — | **NO es vida**. Monstruo: 1. Jugador: vacío |
| 17 | 28 | 1 | — | **NO es vida**. Monstruo: 1. Jugador: vacío |
| 18 | 93 | 3 | 3 | 3 |
| 19 | 79 | — | — | vacío |
| 20 | 78 | — | — | vacío |
| 21 | 44 | — | base 5, bonus 12 | **Iniciativa**. Monstruos: vacío |
| 22 | **(sin f5 → 0)** | **21** | **65** | **PUNTOS DE VIDA** ← aquí va la vida |
| 23 | 11 | — | bonus 12 | Vitalidad |
| 24 | 95 | — | — | vacío |
| 25 | 97 | — | — | vacío |
| 26 | 107 | 100 | 100 | 100 |
| 27 | 150 | 100 | 100 | 100 |
| 28-33 | 120,121,122,123,124,125 | 100 | 100 | 100 (resistencias) |
| 34-36 | 141,142,143 | 100 | 100 | 100 |

**Reglas de relleno:**
- **statId 0 (sin `f5`) = `MaxHP`.** Para el monstruo, `MonsterGradeStats.LifePoints`; para el jugador, el **mismo valor que `kri` publica en su statId 0** (`50 + 5*nivel + vitalidad`), para que la hoja y el combate no se contradigan.
- statId 1 / 23: `MaxAP` / `MaxMP` de la BD (variante `f4` para el jugador, `f2` para el monstruo).
- statId 44 (iniciativa): solo jugador, con la **misma fórmula que `kri`** (`Str+Int+Cha+Agi` + bonus de equipo). Los monstruos lo dejan vacío en el real → dejarlo vacío (la iniciativa interna para ordenar turnos se calcula en el servidor, no hace falta publicarla).
- statId 11 (vitalidad): jugador, valor de `GameState.StatVitality` + bonus.
- statId 120-125 / 141-143 / 107 / 150 = **100** (son porcentajes base; poner 0 los rompe).
- Eliminar el statId **70**: no existe. El nivel del monstruo se transmite por `f7` (§4).

### `jwm` (resincronización, D17)

Al arrancar el combate el servidor reenvía **todos los luchadores** con la misma estructura que `jxx` (bloque de características de 360 B), envuelto en una secuencia `jud{f1=8, f2=playerId} … juc{f1=8, f2=playerId, f3=3}`.
Implementación mínima aceptable: `jwm { f1 = <lista de luchadores con la misma estructura de `jxx.f2`> }` reutilizando `BuildFighterShowBytes` para cada luchador. Sin esto los PV no se refrescan al pasar de colocación a combate.

---

## 6. Fases de implementación

### FASE 0 — Observabilidad (hacer primero, 30 min)

1. Sustituir **todos** los `Console.WriteLine` de `FightHandler.cs` por `Program.LogDebug` (o duplicar) para que el log llegue a `emulator_debug.log` (D22).
2. Instrumentar `Jondo.Protocol.NetworkMessage.WriteFrameAsync` (`Jondo.Unity.Launcher/Protocol/NetworkMessage.cs`) para volcar **S→C**: `timestamp | S->C | type_url | tamaño`. Hoy `gameserver_traffic.log` solo registra C→S. **No** volcar el árbol protobuf completo por defecto (bloqueaba el socket 50-100 ms/paquete).
3. Loguear también los C→S desconocidos con su `type_url`, para detectar mensajes no manejados (`jwe`, `jrb`, `jub`, `jxw`…).

**Verificación:** provocar un combate y obtener en el log la secuencia S→C completa comparable con §3.

---

### FASE 1 — La arena (D1) → arregla síntomas 1 y 2

1. `FightInstance`: añadir `RoleplayMapId` y `ArenaMapId`. `MapId` pasa a ser la arena.
2. Implementar `ResolveArenaMapId` (§2).
3. `InitiateFightFromMobCollision`: calcular la arena y usarla en `joh`, en `jox.f3` y como fuente de celdas.
4. `GeneratePlacementCells` debe recibir `MapManager.WalkableCells[ArenaMapId]`.
5. `SendFightEnd`: emitir `joh { f2 = RoleplayMapId }` antes de restaurar el contexto de roleplay.

**Verificación:** al atacar, el cliente carga un mapa distinto (sin el taller ni los decorados), suena la música de combate y las celdas azules/rojas caen sobre suelo válido.

---

### FASE 2 — Handshake de turno (D2, D15) → arregla el síntoma 4

1. **Borrar** `SendTurnStart` tal como está (`jwo` + `jox` con fighterId). `jwo` solo va en la ráfaga 3 y `jox` solo en la colocación.
2. Implementar el ciclo:

```csharp
// Cierra el turno del luchador actual y pide confirmación al cliente.
async Task EndTurnAsync(stream, Fighter ending)
{
    await Send("jwk", new { f3 = ending.Id });
    // (aquí irían las secuencias de fin de turno: jud/jvm/juc)
    await Send("juu", new { f1 = ending.Id });
    fight.AwaitingTurnAck = true;          // esperar el jwe del cliente
}

// Llamado al recibir jwe del cliente.
async Task HandleTurnReadyAck(stream)
{
    if (!fight.AwaitingTurnAck) return;
    fight.AwaitingTurnAck = false;
    var next = fight.NextTurn();
    if (next == null) { await SendFightEnd(...); return; }

    if (fight.StartsNewRound) await Send("jwb", new { f1 = fight.RoundNumber });
    await Send("jwu", new { f3 = next.Id });
    await Send("jut", new { f1 = 300, f4 = fight.RoundNumber, f5 = next.Id });
    await Send("jwl", empty);

    if (next.IsMonster) await RunMonsterTurnAsync(stream, next);
}
```

3. `HandleTurnReady` (tras `jza`) debe terminar en: `jwb{f1=1}` → `jwu{f3=primero}` → `jud/jwm/juc` → `juu{f1=primero}` → **esperar `jwe`** → `jut{f1=300,f4=1,f5=primero}` → `jwl{}`.
   *Nota:* en la captura, tras `jza` el `juu` lleva el id del **primer** luchador y `jut` también; así que en el turno 1 `juu` y `jut` coinciden.
4. **Salvaguarda anti-bloqueo:** si el `jwe` no llega en 3 s, continuar igualmente (el cliente real siempre contesta, pero no se puede colgar la pelea por eso).
5. Enrutar `jub` (hechizo), `jxw` (pasar), `jwe` (ack), `jrb` (ignorar) en `HandleFightMessageAsync` y en `GameNodeProxy.cs:432`. Quitar `jwb` del enrutado de entrada.
6. La IA de monstruo (`RunMonsterTurnAsync`) debe: emitir sus acciones y luego llamar a `EndTurnAsync(monstruo)` — **nunca** encadenar dos turnos en la misma llamada como hace hoy `HandlePassTurnRequest`.
7. Mantener el temporizador de 45 s de colocación (dispara el mismo camino que `jza`), pero cancelarlo si llega `jza` antes.

**Verificación:** tras el LISTO (o los 45 s) el pío con más iniciativa se mueve, y después le llega el turno al jugador con su reloj de 30 s y sus PA/PM llenos.

---

### FASE 3 — Características de los luchadores (D3, D4, D5, D6, D7)

1. Reescribir `BuildFighterShowBytes` según §4 y §5.
2. Extraer los helpers de `StatsHandler` (`CreateStatField`, `CreateInnateStatField`) a una clase compartida, añadiendo la variante de monstruo (`f2 = { f2 = valor }`).
3. Crear una tabla estática con las 36 entradas en orden (§5) y rellenarla desde `Fighter`.
4. Enviar `jwm` al arrancar (D17).

**Verificación:** al pasar el ratón por un pío, el cliente muestra su nivel real (p. ej. 13), sus PV reales (p. ej. 64/64), 3 PA / 3 PM y resistencias coherentes. El retrato del carrusel muestra el pío completo, no recortado.

---

### FASE 4 — Mensajes de equipo y colocación (D8, D9, D10, D11, D12, D13, D21)

1. `BuildPlacementPossiblePositionsPackets`: reconstruir los `jyf` según §4 (miembros en `f8`, nunca casillas).
2. `BuildPlacementPositionsListBytes` (`jyi`): **f1 = celdas del equipo 1 (monstruos), f2 = celdas del equipo 0 (jugador)**.
3. `BuildIgsPacket` → devolver `BuildGameNodePacket("type.ankama.com/igs", Array.Empty<byte>())`.
4. `SendFightStarting` (`jya`): quitar el submensaje de subárea del campo 4.
5. Eliminar el envío de `jyg` de `HandleFightMapLoad`.
6. Sustituir los `kkz` individuales por un único `kkz` con todos los luchadores (`BuildKkzAllPacket(fight)`); usarlo también como respuesta a cada `jyz`.
7. `GeneratePlacementCells`: generar **dos clusters de 8 celdas** enfrentados dentro de la arena, no celdas repartidas. Referencia real: `[411,424,439,397,410,426,438,453]` (monstruos) y `[286,298,326,271,285,299,312,313]` (jugador) — dos bloques compactos separados ~8 filas.
8. `ResendFightMapBurst3` debe reenviar la ráfaga 3 **completa**, no un subconjunto.

**Verificación:** el cliente muestra los dos equipos en el panel lateral con sus miembros; el jugador puede recolocarse en las casillas azules y las fichas se mueven.

---

### FASE 5 — Disparo del combate y contexto (D14, D20)

1. `HandleFightOptionToggleRequest`: leer `hoy` como **`{ f1 = contextId }` plano** (varint). Localizar el `MobGroup` por ese id; si no se encuentra, **abortar** en vez de coger `mobs.FirstOrDefault()`.
2. `BuildJpfPacket`: construirlo desde el grupo real (`535`/`5` → celda y dirección reales del grupo; `3256`→ bone del líder; `3273`/`3` → `monsterId`/`grade` del líder; `f3` → contextId).
3. Asegurar que el combate se inicia por **una sola** vía (colisión de movimiento **o** `hoy`), nunca dos veces.

**Verificación:** atacando dos grupos distintos del mismo mapa, cada combate contiene los monstruos correctos.

---

### FASE 6 — Acciones de combate (D16, D18, D19)

1. **`jvn` (lista de hechizos)** — construir desde la BD del personaje:
   `{ repeated f1 = { f1 = <spellId>, f3 = 1, f4 = 1 }, f4 = <playerId>, f5 = <playerId>, repeated f6 = { f3 = <slot>, f4 = { f1 = <spellId> } } }`.
2. **`jub` (lanzar hechizo)**: `{ f1 = spellId, f2 = celdaObjetivo }`. Validar PA/alcance con `SpellLevels`, calcular daño con `DamageCalculator` y emitir la secuencia:
   `jud{f1=<tipo>, f2=<autor>}` → `jtx` (daño) → `jvm` (variación de PA/PM) → `juc{f1=<tipo>, f2=<autor>, f3=<n>}`.
   Estructura observada de `jtx` de daño: `{ f13 = 300, f29 = <autorId>, f34 = { f1 = 1, f4 = <celda>, f5 = { f1 = <n>, f4 = <spellLevelId> }, f7 = { f3 = <objetivoId> }, f8 = <objetivoId> } }`.
   `jvm` observado: `{ f2 = <fighterId>, f3 = { f1 = 2, f4 = { … } } }`.
   *Estos dos hay que refinarlos volcando más ejemplos del pcap (§9) antes de dar el daño por bueno.*
3. **`jxw` (pasar turno)** → `EndTurnAsync(actual)`.
4. **Hechizos de monstruo (D19):** `Monsters.Spells` está **vacío en los 5134 registros**. Usar como fuente el campo `startingSpellId` del grado (existe en el JSON de `Grades`) y, si es 0, un ataque cuerpo a cuerpo por defecto. Alternativa a explorar: la columna `MonsterTemplates.Data` (JSON crudo completo del monstruo) puede contener la lista de hechizos — comprobarlo antes de inventar nada.
5. **D23:** al resolver el grado, buscar en el array `Grades` la entrada cuyo campo `grade` **coincida** con el `grade` de `MembersJson`, y solo si no existe caer al índice posicional.
6. `SendLifePointsVariation` / `SendPointsVariation`: eliminarlos en su forma actual (usan `jwu`/`jys`/`kkz` con campos inventados; `jwu` real es `{f3=fighterId}`).

**Verificación:** el jugador lanza un hechizo, se ve el daño flotante sobre el pío, su barra de vida baja y sus PA se descuentan; el pío responde en su turno.

---

### FASE 7 — Fin de combate

Replicar la ráfaga final (§3, frames 328-339): `kri`, `jwf` (resultado), `juo` (recompensas), `kkp`, `kkm`, `krb`, `joh { f2 = RoleplayMapId }`, `lor`, `kri`, `jpf` de los grupos de mobs que quedan. Restaurar `GameState.IsInFight = false` **después** de emitir todo.

---

## 7. Lo que NO se debe hacer

1. No alargar opcodes a 4 letras (`igsp`, `jwop`…). El sufijo `p` de un grep de texto es el byte `0x70` del prefijo de longitud del siguiente frame.
2. No usar la captura `entrar en combate…pcapng` (v3.6.8.8).
3. No enviar `jpv` en contexto de combate (es exclusivo del mapa de roleplay y aborta la interfaz de combate).
4. No enviar `jyg` (viene de la captura prohibida).
5. No introducir `Task.Delay` entre paquetes de una misma ráfaga.
6. No parchear plantillas binarias por offsets.
7. No forzar `Fighter.Id = contextId` para los monstruos: son espacios de identificadores distintos.
8. No arrancar el turno 1 al cargar el mapa de combate: solo tras `jza` (o el timeout de 45 s).
9. No enviar la vida en los statId 27/28 ni el nivel en el 70.
10. No usar el `mapId` de roleplay dentro del combate.

---

## 8. Puntos que siguen abiertos (documentar, no inventar)

1. **Asociación oficial mapa ↔ arena.** Solo hay un par confirmado. La heurística de §2 es coherente pero puede no coincidir con la del servidor oficial. No es bloqueante.
2. **`jya.f4`**: en la captura ocupa 2 bytes que no encajan con `bool` (el `.proto` dice `bool`). Se recomienda **no enviarlo** (ausente = valor por defecto) y comprobar si el cliente lo echa en falta.
3. **Semántica exacta de los statId 27, 28, 34, 93, 95, 97, 101** — replicar los valores observados sin interpretarlos.
4. **`jtx` y `jvm`**: estructura confirmada solo parcialmente. Antes de implementar la Fase 6, volcar del pcap los `jtx`/`jvm` de los frames 254 y 269 (ataques reales) y replicarlos campo a campo.
5. **`jrb`**: se emite dos veces por secuencia con `f1` variable (7, 10, 12…). Parece un ack correlacionado; basta con ignorarlo.
6. **`f1` de `jut`** vale 300 y 290 en distintos turnos: es tiempo (décimas de segundo), no un identificador. Enviar siempre 300.

---

## 9. Apéndice — Cómo reproducir la evidencia

### A. Volcar la secuencia completa del combate

```bash
py C:\Jondo\scripts\fightdump.py
```

El script (guardado en el scratchpad de la sesión, reproducible en 40 líneas) hace:
1. `tshark -r <pcap> -Y "tcp.len>0" -T fields -e frame.number -e tcp.stream -e tcp.srcport -e tcp.dstport -e tcp.payload`
2. reensambla cada dirección de cada stream como un flujo continuo de bytes;
3. lee frames con prefijo **varint de longitud**;
4. de cada frame extrae el envoltorio `{ f3: { f1: Any{ f1: type_url, f2: payload } } }`;
5. para los mensajes de cliente, cuyo envoltorio difiere, busca la marca `type.ankama.com/` y lee los 19 bytes del `type_url` y el `0x12 <varint len>` siguiente;
6. imprime `frame | dirección | type_url | tamaño` y, opcionalmente, el árbol protobuf de los opcodes pedidos.

### B. Comprobar que el combate se libra en la arena

```python
import json
d = json.load(open(r'C:\Jondo\map_walkable_cells.json'))
arena, rp = set(d['153891076']), set(d['154010883'])
posiciones = [286, 299, 298, 285, 326, 411]     # kkz/jxx de la captura
print(sum(c in arena for c in posiciones), '/6 en arena')   # -> 6/6
print(sum(c in rp    for c in posiciones), '/6 en roleplay') # -> 0/6
```

### C. Comprobar el diagnóstico de las estadísticas

```bash
py -c "import sqlite3,json;c=sqlite3.connect(r'C:\Jondo\world.db');cur=c.cursor();cur.execute(\"SELECT Grades FROM Monsters WHERE Id=493\");print([ (g['grade'],g['level'],g['lifePoints'],g['actionPoints'],g['movementPoints']) for g in json.loads(cur.fetchone()[0])['Array'][:3] ])"
```
Salida esperada: `[(1,11,49,3,3), (2,12,56,3,3), (3,13,64,3,3)]` → el **64** que el cliente pinta como "esquiva" es la **vida** del Pío amarillo de grado 3.

### D. Detectar opcodes ilegales en el código

```bash
grep -rnoE 'type\.ankama\.com/[a-z]{4,6}' --include=*.cs "C:\Jondo\Jondo Unity Emulator"
```
Debe devolver 0 resultados.

---

## 10. Orden de trabajo recomendado

```
FASE 0  (observabilidad)      →  imprescindible para verificar el resto
FASE P0 (mapa coherente §2bis)→  sin esto, la Fase 1 calcula sobre el mapa equivocado
FASE 1  (arena)               →  síntomas 1 y 2
FASE 2  (handshake de turno)  →  síntoma 4   ← el más importante
FASE 3  (características)     →  síntomas 3 y 5
FASE 4  (equipos/colocación)
FASE 5  (disparo del combate)
FASE 6  (acciones)
FASE 7  (fin de combate)
```

Cada fase es verificable de forma independiente. **No pasar a la siguiente sin comprobar el criterio de verificación de la anterior en el log S→C de la Fase 0.**
