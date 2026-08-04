# Diagnóstico y plan de implementación — Combate en el emulador Jondo (Dofus 3)

**Fecha:** 2026-08-02
**Estado del síntoma:** al atacar a un grupo de mobs, el cliente funde a negro y vuelve al mapa vacío, sin entidades ni interfaz de combate.
**Alcance de este documento:** diagnóstico verificado con evidencia + plan de implementación por fases. Autocontenido: no requiere contexto previo de la conversación.

---

## 0. Resumen ejecutivo

Se han identificado **tres regresiones concretas** introducidas por iteraciones recientes (#189, #192, #194, #199) que "corrigieron" valores que en realidad ya eran correctos, basándose en lecturas erróneas de las capturas. Las tres, por separado, bastan para que el cliente aborte la escena de combate.

| # | Bug | Origen | Severidad |
|---|---|---|---|
| **B1** | Se envía `type.ankama.com/igsp` y `type.ankama.com/jwop`. **Esos opcodes no existen.** Los reales son `igs` y `jwo` (3 letras). | Iter. #189 y #192 | **Crítica** — el cliente descarta los paquetes en silencio |
| **B2** | El `Fighter ID` del monstruo en `jxx`/`jxe` se fuerza al *context ID* del grupo (`-20003`, `-1030918`…). El valor real es un **secuencial negativo por luchador: `-1`, `-2`, `-3`…** | Iter. #194 y #199 | **Crítica** — desalinea la lista de turnos y la identidad de los actores |
| **B3** | Faltan paquetes en la ráfaga inicial y falta una ráfaga entera; además `jyf` se emite en el momento equivocado. | Acumulado | **Alta** |

Adicionalmente hay un **problema metodológico grave** que ha causado gran parte de los ciclos fallidos (§5) y una **falta de visibilidad de diagnóstico** que hay que resolver primero (§6, Fase 0).

---

## 1. Regla de oro que se ha estado violando

> **Los opcodes de Dofus 3 tienen SIEMPRE 3 letras.** El `type_url` completo mide **exactamente 19 bytes**: `type.ankama.com/xxx`.

**Evidencia (irrefutable):** se extrajeron todas las ocurrencias de `type.ankama.com/` de las dos capturas oficiales leyendo el **byte de longitud protobuf que precede a la cadena** (no por regex sobre texto):

```
lanzar combate y combatir hasta ganar…pcapng  -> longitudes de type_url: {19: 228}   4-letras: NINGUNA
entrar en combate…pcapng                       -> longitudes de type_url: {19:  82}   4-letras: NINGUNA
```

**De dónde salió el error:** un `grep` de texto sobre el `.pcapng` produce falsos positivos de 4 letras (`igsp`, `jwop`, `lsyp`, `kkpp`, `jynp`, `lxsp`…). La `p` fantasma es el byte `0x70` (=112), que es el **prefijo varint de longitud del siguiente frame TCP**, y `0x70` coincide con el ASCII de `'p'`. No forma parte del nombre.

**Corolario:** cualquier "corrección" futura que alargue un opcode a 4 letras es un bug. El fichero `C:\Jondo\dofus3_sniffer_complete.proto` (extraído del binario del cliente) lo confirma: existen `message igs` y `message jwo`; **no** existen `igsp` ni `jwop`.

---

## 2. Bug B1 — Opcodes inexistentes `igsp` / `jwop`

### Ubicación exacta

```
Jondo.Unity.Launcher/Handlers/FightHandler.cs:188   "type.ankama.com/igsp"
Jondo.Unity.Launcher/Handlers/FightHandler.cs:207   "type.ankama.com/igsp"
Jondo.Unity.Launcher/Handlers/FightHandler.cs:249   "type.ankama.com/jwop"
Jondo.Unity.Launcher/Handlers/FightHandler.cs:305   "type.ankama.com/jwop"
```

Son las **únicas 4 líneas de todo el emulador** con un opcode de más de 3 letras (verificado con `grep -rnoE 'type\.ankama\.com/[a-z]{4,6}' --include=*.cs`). Todo el resto del proyecto usa opcodes correctos, que es por lo que el login, el mapa y el inventario sí funcionan.

### Historia de la regresión

- **Iteración #189** afirmó: *"Se detectó un error tipográfico crítico: la trama enviaba la URI corta `type.ankama.com/igs` (19 bytes), mientras que el nombre oficial es `type.ankama.com/igsp` (20 bytes)"*. **Falso.** El código original era correcto.
- **Iteración #192** hizo lo mismo con `jwo` → `jwop`. **Falso.**

### Efecto

Un `type_url` desconocido se descarta **en silencio** (sin excepción ni log). El cliente se queda esperando:
- `igs` = información complementaria de combate → sin ella no monta la escena.
- `jwo` = cabecera de inicio de turno → sin ella no activa el reloj ni el botón LISTO.

### Corrección

Sustituir en las 4 líneas: `igsp` → `igs`, `jwop` → `jwo`.

**Importante sobre el payload:** en la captura, `igs` y `jwo` son **mensajes vacíos**: llevan únicamente el `type_url`, **sin campo 2 (value)**. Hay que asegurarse de que `NetworkEnvelope.BuildGameNodePacket` con payload vacío **omita** el campo 2 (así lo hace hoy: `if (payload != null && payload.Length > 0)`). Correcto tal cual.

---

## 3. Bug B2 — Identidades: `context ID` ≠ `fighter ID`

El protocolo maneja **dos espacios de identificadores distintos** que las iteraciones #194/#199 fusionaron por error.

| Concepto | Valor en la captura | Dónde se usa |
|---|---|---|
| **Fight context ID** (identidad del grupo de mobs en el mapa de roleplay, negativo grande) | `-20003` | `kkq.f1`, `jya.f6`, `jpf.f1.f3`, `jyf` (equipo monstruos → `f1.f2`) |
| **Fighter ID del monstruo** (secuencial negativo por luchador) | **`-1`** (segundo monstruo sería `-2`, etc.) | `jxx` (`f2.f2.f3.f1.f4.f3`), `jxe`, `kkz` |
| **Fighter ID del jugador** | `670668947750` (su `CharacterId`) | `jxx`, `jxe`, `jya.f2`, `jyf` (equipo jugador), `jys.f2` |

### Evidencia decodificada de la captura

```
jya   -> f1=300(fightType)  f2=670668947750(jugador)  f3=4  f6=-20003(contexto)
kkq   -> f1=-20003
jyf#A -> f1{ f2=670668947750, f7=1, f8{ f2{ f2=670668947750, f4{ f2="Fortellon", f3=3 }}}}  f2=300
jyf#B -> f1{ f2=-20003, f4=1, f6=1, f7=1, f8=<vacío> }  f2=300
jxe   -> f3{ f2{ f1=670668947750 }}   f3{ f2{ f1=-1 }}      <-- MONSTRUO = -1
kkz   -> f1{ f2=286, f3=670668947750, f5=3 }  f1{ f2=411, f3=-1, f5=7 }
jxx(monstruo) -> f2{ f1{f1=0, f2=411(celda), f5=7(dir)},
                     f2{ f1{f1=3256, f3=3},
                         f3{ f1{ f2=1(team), f3=1, f4{ f1{f1=0,f2=411,f5=7}, f3=-1 }},
                             f2=0, f4{...256B de stats...} }}}
```

Nótese que la **plantilla binaria original extraída del pcap ya tenía `-1`** — la iteración #194 la describió como *"un ID de monstruo hardcoded a `-1`"* y lo consideró un defecto, cuando era el valor correcto. Luego #199 lo forzó a `DefenderLeaderId`. Ambas son regresiones.

### Corrección

- Asignar a cada monstruo un `Fighter.Id` secuencial: `-1`, `-2`, `-3`… **por combate** (reiniciando el contador en cada combate nuevo).
- Usar el `context ID` del grupo (`mobContextId`) **solo** en `kkq.f1`, `jya.f6`, `jpf.f1.f3` y en el `jyf` del equipo de monstruos.
- Revertir la lógica de `FightHandler.InitiateFightFromMobCollision` que hace `monFighterId = fight.DefenderLeaderId` para el primer monstruo.

---

## 4. Bug B3 — Estructura de ráfagas y paquetes faltantes

### 4.1. Cómo envía realmente el servidor (medido)

Línea temporal entrelazada extraída de `lanzar combate y combatir hasta ganar y cerrar pantalla fin combate.pcapng` (S→C = servidor, C→S = cliente):

```
 88   3.353250   C->S   jpp                        <- el cliente dispara el combate
 89   3.353355   C->S   hoy
 90   3.393473   S->C   joq          ┐
 91   3.393474   S->C   jpf          │
 92   3.393475   S->C   kkq          │
 93   3.393475   S->C   kkp          │  RÁFAGA 1
 94   3.393476   S->C   kkm          │  10 mensajes en 6 MICROsegundos
 95   3.393478   S->C   kri          │
 96   3.393478   S->C   joh + lor    │
 97   3.393479   S->C   krp          │
 98   3.393479   S->C   lsy          │
 99   3.393479   S->C   kkz          ┘
101   3.433616   S->C   [jyf jyf kkz kkz]          <- RÁFAGA 2 (los 4 en UN segmento TCP)
107   3.959382   C->S   igx + kkr                  <- el cliente pide el mapa de combate
108   3.999604   S->C   igs          ┐
109   3.999605   S->C   jya          │  RÁFAGA 3
110   3.999608   S->C   [jyj jxx jxx jyi jyf jyk jyk jyk jyk jxe]   (10 en UN segmento)
111   3.999609   S->C   jwo          │  14 mensajes en 5 MICROsegundos
112   3.999609   S->C   jox          ┘
131   5.324694   C->S   jyz                        <- mover ficha en preparación
132   5.365158   S->C   kkz                        <- respuesta 1:1 (se repite x7)
182  13.062943   C->S   jza                        <- botón LISTO
183  13.104870   S->C   jys jwu lsy kkz jyn jvn jwb jwu [jud jwm juc] [jud jtx jxf juc]
```

**Conclusiones sobre el *timing* (responde a la duda de si hay que espaciar los envíos):**

1. **Dentro de una ráfaga se envía todo de golpe**, back-to-back, con separación de **microsegundos**. No hay pausas deliberadas ni orden sensible al reloj. **No hay que introducir `Task.Delay` de ningún tipo.**
2. Los ~40 ms que se observan entre cliente y servidor son simplemente la **latencia de red de la captura** (RTT), no una espera del servidor.
3. **Lo que sí es estricto es el modelo petición/respuesta:** hay 3 ráfagas y cada una la dispara un mensaje concreto del cliente. No se puede volcar todo de una vez:
   - Ráfaga 1 ← disparada por el fin del movimiento / colisión.
   - Ráfaga 3 ← **hay que esperar al `kkr` del cliente**.
   - Inicio del turno 1 ← **hay que esperar al `jza`** (botón LISTO). Nunca antes.
4. Durante la preparación el cliente conduce: cada `jyz` se responde con un `kkz`, uno a uno.

### 4.2. Comparativa oficial vs emulador

| Ráfaga | Oficial | Emulador actual | Acción |
|---|---|---|---|
| **1** (colisión) | `joq jpf kkq kkp kkm kri joh lor krp lsy kkz` | `joq jpf kkq kri joh jyf` | añadir `kkp kkm lor krp lsy kkz`; **quitar `jyf`** |
| **2** (+40 ms) | `jyf jyf kkz kkz` | *(no existe)* | **añadir la ráfaga completa** |
| **3** (tras `kkr`) | `igs jya jyj jxx jxx jyi jyf jyk×4 jxe jwo jox` | `igsp jya jyj jxx… jyi jyf×2 jyk×4 jxe jwop jox` | arreglar `igs`/`jwo`; dejar **un solo `jyf`** |

**El orden de la ráfaga 3 ya es correcto.** El problema no es el orden sino los dos opcodes rotos y los duplicados/faltantes.

### 4.3. Estructuras de referencia (decodificadas de la captura)

Todas son el contenido del campo *value* del `Any`; el envoltorio lo pone `BuildGameNodePacket`.

```
joq  -> VACÍO (solo type_url)
kkp  -> VACÍO
lsy  -> VACÍO
igs  -> VACÍO
jwo  -> VACÍO
jyn  -> VACÍO

kkm  -> f1 = 1
jwb  -> f1 = 1
joh  -> f2 = <mapId>
lor  -> f1 = 120, f2 = <timestamp ms>
krp  -> f1 = 278, f2 = 77, f3 = 77
kkq  -> f1 = <contextId>            (p.ej. -20003)
jyj  -> f2=1, f4=4, f5=443, f6=1
jyk  -> f3 = <opción 0..3>, f5 = 300      (se envían 4, uno por opción)
jys  -> f1 = 1, f2 = <playerId>
jox  -> f1 = 450, f2 = { f1 = -3, f2 = -2 }, f3 = <mapId>     (fase de preparación)
kkz  -> f1 = { f2 = <celda>, f3 = <fighterId>, f5 = <dirección> }   (repetible)
jya  -> f1 = 300, f2 = <playerId>, f3 = 4, f4 = {…}, f6 = <contextId>
jxe  -> por luchador: f3 = { f2 = { f1 = <fighterId> } }
jyi  -> f1 = { f1 = <bytes celdas equipo0>, f2 = <bytes celdas equipo1> }
        (los dos campos son secuencias de varints de celda concatenados, NO packed con
         longitud por elemento: p.ej. 9b03 a803 b703 8d03 9a03 aa03 b603 c503)
jyf (equipo jugador)   -> f1 = { f2=<playerId>, f7=1,
                                 f8 = { f2 = { f2=<playerId>,
                                               f4 = { f2="<nombre>", f3=<breed> }}}},
                          f2 = 300
jyf (equipo monstruos) -> f1 = { f2=<contextId>, f4=1, f6=1, f7=1, f8=<vacío> }, f2 = 300
jpf -> f1 = { f1={f2=535,f5=5},
              f2={ f1={f1=3256,f3=3},
                   f2={ f1={ f2=-1, f3={f1={f3=3273,f4=3,f6=3}}, f4=1 }}},
              f3 = <contextId> }
jxx -> f2 = { f1 = { f1=0, f2=<celda>, f5=<dirección> },
              f2 = { …look/breed…,
                     f3 = { f1 = { f2=<teamId>, f3=<¿?>,
                                   f4 = { f1={f1=0,f2=<celda>,f5=<dir>}, f3=<fighterId> }},
                            f2 = <playerId ó 0>,
                            f4 = { …~256 B de estadísticas… } } } }
```

---

## 5. Problema metodológico (causa de los ciclos fallidos)

**Hay dos capturas de combate y solo una es válida:**

| Captura | Versión | Opcodes de combate | ¿Usar? |
|---|---|---|---|
| `lanzar combate y combatir hasta ganar y cerrar pantalla fin combate.pcapng` | compatible con el cliente instalado | `jxx jya jyf jyj jyk jys jyz jza jwo jxe igs kkq jpf joq jox kkz jwb` | ✅ **ÚNICA FUENTE DE VERDAD** |
| `entrar en combate-esperar segundos de preparacion-moverse en fase preparacion-empezar a pelear.pcapng` | v3.6.8.8 (ofuscación nueva) | `jxm jzy kaf jyg jym jyp jzg jzj jzs kae kag…` | ❌ **NO USAR** |

**Prueba de cuál corresponde al cliente instalado (v3.6.4.3):** el cliente en vivo envía `jqf`, `kkr`, `joi`, `kod` (visible en `gameserver_traffic.log`). Esos cuatro existen en la captura antigua; la nueva no los tiene (usa `jqe`/`jql`/`jqw`/`jpw`). Los conjuntos de opcodes de ambas capturas son **disjuntos**.

Las iteraciones **#187 y #192–#199 auditaron la captura nueva** para extraer plantillas y "confirmar la secuencia". Todo lo aprendido de ahí es sospechoso y debe re-verificarse contra la captura antigua.

**La captura antigua contiene la fase de preparación completa** (`jyi`, `jyf`, `jys`, `jyz`, `jza`, `kkz`), así que **no hace falta volver a capturar nada**.

---

## 6. Plan de implementación

### Fase 0 — Visibilidad (hacer esto ANTES de tocar nada más)

Sin esto es imposible verificar nada; buena parte de los ciclos anteriores se perdieron por falta de observabilidad.

1. **Registrar S→C.** Hoy `gameserver_traffic.log` **solo** registra C→S (marcadores `C->S` y `GAME_C->S`; cero entradas de servidor→cliente). Instrumentar el punto de escritura (`Jondo.Protocol.NetworkMessage.WriteFrameAsync`, en `Jondo.Unity.Launcher/Protocol/NetworkMessage.cs`) para volcar: timestamp, dirección, `type_url` y tamaño. Sin árbol protobuf completo por defecto (ver punto 3).
2. **FightHandler debe loguear a fichero.** Hoy usa `Console.WriteLine`, que **no** va a `emulator_debug.log`; por eso los logs de los últimos intentos no contienen ni una línea de combate. Cambiar a `Program.LogDebug` (o duplicar).
3. **Mantener el volcado de árbol acotado.** La iteración #197 ya detectó que volcar árboles completos bloqueaba el socket 50–100 ms/paquete y provocaba timeouts. Conservar el límite de líneas.
4. **Añadir un aserto de opcode:** en `BuildGameNodePacket`, si el nombre tras `type.ankama.com/` no mide exactamente 3 caracteres, escribir un `WARN` bien visible. Esto habría evitado B1 y evitará su reaparición.

**Criterio de verificación:** provocar un combate y obtener en el log la secuencia S→C completa con sus `type_url`.

### Fase 1 — Revertir las regresiones (mínimo cambio, máximo impacto)

1. `FightHandler.cs:188,207` → `igsp` → **`igs`**.
2. `FightHandler.cs:249,305` → `jwop` → **`jwo`**.
3. IDs de monstruo: asignar `-1, -2, -3…` por combate; dejar de forzar `monFighterId = fight.DefenderLeaderId`. Mantener el `context ID` únicamente en `kkq.f1`, `jya.f6`, `jpf.f1.f3` y en el `jyf` del equipo de monstruos.

**Criterio de verificación:** al atacar, el cliente ya no vuelve al mapa de roleplay; deben aparecer las casillas de colocación (rojas/azules) y el reloj de 45 s.

### Fase 2 — Completar las ráfagas

1. Ráfaga 1: añadir `kkp` (vacío), `kkm` (`f1=1`), `lor` (`f1=120, f2=<timestamp>`), `krp` (`f1=278,f2=77,f3=77`), `lsy` (vacío), `kkz` (posición del jugador). **Quitar el `jyf`** que hoy se envía aquí.
2. Ráfaga 2 (nueva, inmediatamente después de la 1): `jyf`(equipo jugador) + `jyf`(equipo monstruos) + `kkz`(jugador) + `kkz`(monstruos).
3. Ráfaga 3: dejar **un solo** `jyf`.
4. **No introducir esperas artificiales.** Escribir cada ráfaga en un bucle seguido. Sí respetar los disparadores: ráfaga 3 solo tras `kkr`; turno 1 solo tras `jza`.

**Criterio de verificación:** el cliente muestra los modelos 3D de jugador y monstruos sobre la cuadrícula y responde a los clics en casillas azules (`jyz` → `kkz`).

### Fase 3 — Construcción orgánica (eliminar plantillas binarias)

**Requisito explícito del propietario del proyecto: los paquetes deben construirse desde los datos reales (SQLite + estado), no con plantillas binarias capturadas ni parcheo por offsets.**

Estado actual: `SendFighterShow` usa `OfficialJxxPlayerTemplate` / `OfficialJxxMonsterTemplate` (arrays de bytes del pcap) y parchea offsets fijos (`0x28`, `0x33`, `0x4B`, `0x6A`, `0x6F`, `0x76`, `0x166`, `0x1AA`…). Esto ya ha causado `IndexOutOfRangeException` (#195) y desalineaciones de ID (#194, #199).

**Riesgo técnico del parcheo por offsets, a documentar:** los varints son de **longitud variable**. Escribir un valor cuya codificación ocupe distinto número de bytes que el original **corrompe todo el resto del mensaje**. Un ID pequeño y uno grande no ocupan lo mismo. Por eso el enfoque es intrínsecamente frágil y hay que abandonarlo, no seguir ajustando offsets.

**Plan:** reconstruir `jxx` con `ProtoMessage` siguiendo el árbol de §4.3, poblando desde `Fighter`/`world.db` (celda, dirección, `teamId`, `fighterId`, `LookBoneId`, estadísticas). Migrar también los payloads hex hardcodeados de `joq` y `jpf` (que hoy llevan datos incrustados de la sesión original: `contextId -20003`, mapa e IDs ajenos).

**Criterio de verificación:** el combate carga igual que en la Fase 2, pero con un mob distinto y un personaje distinto (apariencia y estadísticas correctas en ambos casos).

### Fase 4 — Ciclo de preparación y arranque de turno

1. `jyz` (cambio de celda) → responder con `kkz` (`f1{f2=<celdaNueva>, f3=<fighterId>, f5=<dir>}`), uno por petición.
2. `jza` (LISTO) → emitir la ráfaga de arranque: `jys`(`f1=1,f2=<playerId>`), `jwu`, `lsy`, `kkz`, `jyn`, `jvn`, `jwb`(`f1=1`), `jwu`, y a continuación la secuencia de turno.
3. **`jox` con `f2.f1 = -3` es la fase de preparación**, no un turno. El turno real lleva el `fighterId`. No enviar el turno 1 antes del `jza`.

---

## 7. Lo que NO se debe hacer (evitar repetir ciclos)

1. **No alargar opcodes a 4 letras.** Siempre 3 (§1).
2. **No usar la captura `entrar en combate…pcapng`** (ofuscación de v3.6.8.8, incompatible). Usar solo `lanzar combate y combatir hasta ganar…pcapng`.
3. **No enviar `jpv` en contexto de combate.** Ya verificado en #191: `jpv` es exclusivo del mapa de roleplay y hace que Unity aborte la interfaz de combate. En combate, la información complementaria va en **`igs`**.
4. **No introducir `Task.Delay` entre paquetes.** El servidor real los emite con microsegundos de separación (§4.1).
5. **No seguir parcheando offsets de las plantillas binarias.** Es la fuente de #194/#195/#199. Migrar a construcción orgánica (Fase 3).
6. **No forzar `Fighter ID = context ID`.** Son espacios distintos (§3).
7. **No arrancar el turno 1 al cargar el mapa de combate.** Solo tras `jza`.

---

## 8. Puntos abiertos (requieren verificación en vivo)

1. **¿Se dispara la colisión?** En los logs de los últimos intentos (2-ago 13:12/13:21/13:31) no hay **ninguna** línea de combate ni paquete de pelea: la traza termina con dos `joi` de movimiento. No se puede distinguir entre "el combate no se dispara" y "se dispara pero no se registra", precisamente por la falta de instrumentación (Fase 0). **Resolver esto primero.**
2. **Disparador exacto de la ráfaga 1.** En la captura, justo antes va un `jpp` + `hoy` del cliente. Conviene confirmar si el servidor reacciona al fin del movimiento o a ese `jpp`.
3. **Semántica de `f3` en `jxx`** (vale `1` en el monstruo) y del bloque `f4` de ~256 B de estadísticas: replicarlo campo a campo desde la captura al hacer la Fase 3.
4. **`igx`**: el cliente lo envía junto con `kkr` antes de la ráfaga 3; comprobar que el router lo trata (o lo ignora) sin romper el flujo.

---

## 9. Apéndice — Comandos de verificación

```bash
# Longitud real de los type_url (debe salir siempre 19)
py -c "import subprocess,re;from collections import Counter;TS=r'C:\Program Files\Wireshark\tshark.exe';\
out=subprocess.run([TS,'-r',r'C:\Jondo\lanzar combate y combatir hasta ganar y cerrar pantalla fin combate.pcapng','-Y','tcp.len>0','-T','fields','-e','tcp.payload'],capture_output=True,text=True);\
c=Counter();\
[c.update([pay[m.start()-1]]) for line in out.stdout.splitlines() if line.strip() for pay in [bytes.fromhex(line.replace(':',''))] for m in re.finditer(rb'type\.ankama\.com/',pay)];print(c)"
```

```bash
# Detectar opcodes ilegales en el código fuente (debe devolver 0 resultados)
grep -rnoE 'type\.ankama\.com/[a-z]{4,6}' --include=*.cs "C:\Jondo\Jondo Unity Emulator"
```
