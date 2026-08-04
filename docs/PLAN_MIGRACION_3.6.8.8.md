# Plan de migración del emulador Jondo — del cliente 3.6.4.3 al 3.6.8.8

**Fecha:** 2026-08-03
**Origen:** cliente `C:\Jondo\DofusClient` (v3.6.4.3, `GameAssembly.dll` de 24/06/2026)
**Destino:** cliente `C:\Users\Santiago\AppData\Local\Ankama\Dofus-dofus3` (v3.6.8.8, 01/08/2026)
**Estado de partida:** el emulador funciona con el cliente viejo (login, mundo, mapas, inventario, características, NPC, zaaps, combate en curso).

---

## 0. Resumen ejecutivo y recomendación

Migrar **no es cambiar unos nombres de 3 letras**. El análisis de los dos ensamblados demuestra que Ankama hace tres cosas a la vez entre versiones:

1. **Renombra los mensajes** (de 1.605 parejas alineadas, solo 3 conservan el nombre).
2. **Permuta los números de campo** dentro de cada mensaje. Probado: `jya` = `{1:int32, 2:int64, 3:enum, 4:rep int32, 5:bool, 6:int64}` y su equivalente en 3.6.8.8 = `{1:enum, 2:int32, 3:int64, 4:bool, 5:int64, 6:rep int32}`.
3. **Renombra los enums** (`kzf` → `laq`, ambos con valores 0..10).

Y hay un cuarto problema, el más serio: **el mensaje envoltorio también cambió**. El `leo` del cliente viejo (`{1:lem, 2:lam, 3:led, 4:int64, 5:int64, 6:int32}`) **no tiene equivalente estructural en el cliente nuevo**, ni siquiera permutando la numeración. Eso significa que el formato de trama que usa `NetworkEnvelope.BuildGameNodePacket` hay que **volver a derivarlo desde cero de una captura**, y hasta que eso no esté resuelto no funciona absolutamente nada.

> ### Recomendación
> **Termina primero el combate sobre el cliente viejo y migra después.** Razones:
> - El combate está a punto: tienes la captura de referencia decodificada frame a frame y un plan cerrado ([PLAN_COMBATE_V2.md](PLAN_COMBATE_V2.md)).
> - Esa captura es de 3.6.4.3. Si migras ahora, **pierdes tu única fuente de verdad del combate** y tendrás que volver a capturar y volver a decodificar todo.
> - Migrar con el combate a medias significa depurar dos problemas a la vez sin saber cuál causa qué.
>
> Si aun así prefieres migrar ya, este plan es seguro: **no borra nada** y deja los dos clientes funcionando en paralelo, así que siempre puedes volver.

**Esfuerzo estimado:** 3-5 días de trabajo efectivo si las capturas salen limpias. El 70 % está en la Fase E (resolver el mapeo).

---

## 1. Inventario: todo lo que depende de la versión del cliente

| # | Artefacto | Ruta | Impacto | Regenerable |
|---|---|---|---|---|
| 1 | Cliente de juego | `C:\Jondo\DofusClient\` | Se sustituye (conservando el viejo) | — |
| 2 | MelonLoader + interop | `DofusClient\MelonLoader\` | Regenerar para el cliente nuevo | Sí, automático |
| 3 | **Mod JondoFix** | `Jondo Unity Emulator\JondoFix\` | **Recompilar + re-resolver identificadores ofuscados** | Parcial (§8) |
| 4 | `Protocol.proto` (80 mensajes) | `Jondo.Unity.Protocol\Messages\` | Regenerar entero | Sí, con `dllq` |
| 5 | `GameProtocol.proto` (20 mensajes) | `Jondo.Unity.Launcher\Protocol\` | Revisar números de campo | Sí |
| 6 | **121 opcodes de 3 letras** | 12 archivos `.cs` del Launcher | **Remapear todos** | Fase E |
| 7 | **Números de campo** en cada builder | `FightHandler`, `StatsHandler`, `TransitionPacketsBuilder`… | **Remapear por mensaje** | Fase E |
| 8 | **`BasePayloads.WorldEnteringPackets`** | `BasePayloads.cs` | **Recapturar del servidor nuevo** | Solo con captura |
| 9 | Envoltorio de trama | `NetworkEnvelope.BuildGameNodePacket` | **Re-derivar de captura** | Solo con captura |
| 10 | Versión declarada al cliente | `HaapiServer.cs:149` (`"build_override": "3.6.4"`) | Cambiar a `3.6.8` | Trivial |
| 11 | Config del sniffer | `C:\Jondo\config.json` | Nuevo `.proto` + nombres de envoltorio (`leo`/`gui`) | Fase C |
| 12 | `dofus3_sniffer_complete.proto` | `C:\Jondo\` | Regenerar para 3.6.8.8 | Sí, con `dllq` |
| 13 | Volcados de mapas | `map_dump_*.csv`, `map_walkable_cells.json` | Regenerar con JondoFix nuevo | Sí |
| 14 | `world.db` / `auth.db` | `C:\Jondo\` | Revisar si cambian ids de juego | Probablemente no |
| 15 | `launch_jondo.bat` | `DofusClient\` | Copiar al cliente nuevo | Trivial |

**Lo que NO cambia** (comprobado): los ids del juego (mapas, ítems, monstruos, hechizos) son estables entre versiones — por eso `world.db` sirve para las dos. Y los 102 ficheros fuente del protocolo son **idénticos** en ambas versiones (`Account.cs`, `Fight.cs`, `Inventory.cs`…), así que la organización del protocolo no se reestructuró.

---

## 2. Principio rector: nada destructivo

**No borres el cliente viejo ni lo muevas.** Todo el plan se apoya en poder comparar las dos versiones y en poder volver atrás. La estructura final debe ser:

```
C:\Jondo\
   DofusClient\          <- 3.6.4.3, INTACTO (referencia + rollback)
   DofusClient368\       <- 3.6.8.8, copia de trabajo nueva
   Jondo Unity Emulator\ <- código, en una rama de git aparte
```

Y el cliente instalado en `%LOCALAPPDATA%\Ankama\Dofus-dofus3` se queda como está: es tu vía para actualizar y para recapturar del juego oficial.

---

## FASE A — Respaldo y control de versiones

1. **Inicializa git en el emulador si no lo está** (`C:\Jondo` no es un repositorio ahora mismo) y haz un commit del estado que funciona con 3.6.4.3. Etiquétalo `v3.6.4.3-estable`.
2. Copia de seguridad fuera del árbol de trabajo: `world.db`, `auth.db`, `dofus3_data\`, `map_walkable_cells.json`, `map_dump_*.csv`, y **todos los `.pcapng`** (son irreemplazables).
3. Crea la rama `migracion-3.6.8.8`. Todo el trabajo va ahí. `main` sigue funcionando con el cliente viejo.

**Puerta de verificación A:** puedes arrancar el emulador desde `main`, entrar al mundo con el cliente viejo y moverte por el mapa.

---

## FASE B — Instalar el cliente nuevo en paralelo

1. Copia `%LOCALAPPDATA%\Ankama\Dofus-dofus3` completo a `C:\Jondo\DofusClient368\`.
2. En la copia, activa MelonLoader: `version.dll.disabled` → `version.dll` (ya lo tienes hecho en el original; en la copia verifica que existe la carpeta `MelonLoader\` con `net6\MelonLoader.runtimeconfig.json`).
3. Copia `launch_jondo.bat` del cliente viejo al nuevo. Los argumentos (`--port 8080 --connectionPort 5555 …`) no dependen de la versión; se quedan igual.
4. **Deja el `%LOCALAPPDATA%` original con MelonLoader desactivado** (`version.dll.disabled`). Ese es el que usarás para capturar del juego oficial, y no quieres el mod cargado cuando te conectes al servidor real.

**Puerta de verificación B:** `C:\Jondo\DofusClient368\Dofus.exe` arranca y llega a la pantalla de login (contra el servidor oficial, sin emulador). Los interop assemblies están en `DofusClient368\MelonLoader\Il2CppAssemblies\`.

---

## FASE C — Regenerar los artefactos de esquema

Ya tienes la herramienta: `C:\Jondo\scripts\protomatch\dllq\`.

1. **Volcar el esquema de las dos versiones**, de los ensamblados de **Cpp2IL** (conservan los números de campo; los de Il2CppInterop NO):

   ```bash
   dotnet run --project C:/Jondo/scripts/protomatch/dllq -- "C:/Jondo/DofusClient/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/Ankama.Dofus.Protocol.Game.dll" old_game.json
   ```

   Repetir para:
   - `…/DofusClient368/…/cpp2il_out/Ankama.Dofus.Protocol.Game.dll` → `new_game.json`
   - `…/cpp2il_out/Ankama.Dofus.Protocol.Connection.dll` (ambas versiones) → `old_conn.json`, `new_conn.json`
     **Esto falta y es necesario**: el envoltorio de conexión (`leo`/`gui` en `config.json`) puede vivir ahí, no en Game.

2. **Generar el `.proto` completo de 3.6.8.8** a partir de `new_game.json`. Estructura de cada mensaje en el DLL: por cada campo protobuf hay un par de campos consecutivos — una constante `Int32` cuyo *valor* es el número de campo, y el campo de almacenamiento con el tipo real. Reglas:
   - `FieldCodec\`1<T>` + `RepeatedField\`1<T>` → `repeated T`
   - `Codec<K,V>` + `MapField\`2<K,V>` → `map<K,V>`
   - un `Int32` sin valor **antes** del primer const → campo `case` de un `oneof`
   - un tipo con campo `value__` → es un `enum`

   El `dofus3_sniffer_complete.proto` que tienes se generó así, pero **es incorrecto en varios sitios** (colapsaba los `repeated` y los `map` a `bytes`: `igs` son en realidad dos `map`, y `jya.f4` es `repeated int32`, no `bool`). Aprovecha para generar bien también el del cliente viejo.

3. Guarda los dos como `dofus3_proto_3.6.4.3.proto` y `dofus3_proto_3.6.8.8.proto`.

**Puerta de verificación C:** el `.proto` regenerado del cliente **viejo** reproduce los mensajes que ya conoces (`jox` con campos 1,3,2,4; `lnk` con `{lhi, lni, int64}`). Si eso cuadra, el generador es fiable y el del cliente nuevo también lo será.

---

## FASE D — Capturas del juego real

Esta es la fase que **solo puedes hacer tú**, y de su calidad depende todo lo demás.

### Preparación (importante)

- Captura desde `%LOCALAPPDATA%\Ankama\Dofus-dofus3` **con MelonLoader desactivado** (`version.dll.disabled`) y con el emulador **parado** (JondoFix se autoactiva si detecta algo escuchando en el 8888).
- Wireshark sobre tu **interfaz de red real** (no loopback), filtro de captura `tcp port 5555 or tcp port 443`.
- Un fichero `.pcapng` **por escenario**, nunca varios escenarios en el mismo fichero.
- **Método por escenario:** iniciar captura → esperar 5 s quieto → hacer **una sola** acción → esperar 5 s quieto → parar. Los silencios son lo que permite alinear.
- Apunta en un `.txt` junto a cada captura: personaje, mapa (coordenadas), hora de cada acción. Los ids concretos (mapId, itemId) son anclas de oro para el emparejamiento.

### Capturas por orden de prioridad

| # | Nombre de fichero sugerido | Qué hacer | Qué desbloquea |
|---|---|---|---|
| **1** | `368_login_completo.pcapng` | Desde el launcher: jugar → elegir servidor → elegir personaje → esperar a estar en el mundo → **quedarse quieto 30 s** | **La más importante.** Cadena de autenticación + la ráfaga de entrada al mundo, que es lo que sustituye a `BasePayloads.WorldEnteringPackets`. Y el envoltorio de trama (Fase E paso 1) |
| **2** | `368_cambio_mapa_4_dir.pcapng` | Andar a un borde y cambiar de mapa: arriba, abajo, izquierda, derecha (pausa de 5 s entre cada uno) | `joi joo joh jos kkr jqf jpv` |
| **3** | `368_combate_completo.pcapng` | Atacar un grupo de mobs, mover ficha en preparación, LISTO, un movimiento, un hechizo, pasar turno, ganar, cerrar pantalla de fin | Todo el combate para la nueva versión. **Hazla en Incarnam (-2,-3) si puedes**, para poder comparar celda a celda con la captura vieja |
| **4** | `368_inventario.pcapng` | Abrir inventario, equipar un ítem (doble clic), desequiparlo, cerrar | `irm isi iry isf luq luy` |
| **5** | `368_caracteristicas.pcapng` | Abrir panel de características, subir 1 punto a Fuerza, aplicar, cerrar | `krc krb krd kri` |
| **6** | `368_npc_mision.pcapng` | Hablar con un NPC, aceptar una misión, cerrar diálogo | `ilr ilu ilq kjn kjl` |
| **7** | `368_zaap.pcapng` | Usar un zaap, elegir destino, teletransportarse | Cadena de teletransporte |
| **8** | `368_chat.pcapng` | Escribir un mensaje en el canal general | `kqn kqp` |
| **9** | `368_reposo.pcapng` | Entrar al mundo y **no hacer nada 90 s** | Aísla los keepalives (`kod`, `kns`) y los mensajes periódicos |

> Las capturas 1, 2, 4, 5, 6, 7 tienen **equivalente en tus capturas viejas** (`jugar en launcher-eleccion server…`, `cambiar mapa hacia…`, `equipar item arrastrando…`, `hablar con NPC…`, `usar zaap…`). Eso es lo que permite alinearlas y deducir el mapeo. **Reproduce el mismo escenario lo más fielmente que puedas.**

**Puerta de verificación D:** cada `.pcapng` tiene un solo escenario, silencios visibles entre acciones, y su `.txt` de notas.

---

## FASE E — Resolver el mapeo (el corazón de la migración)

### E.1 — Re-derivar el envoltorio de trama (bloqueante)

De `368_login_completo.pcapng`, reensamblar el stream del puerto 5555 y descubrir a mano el formato de trama del cliente nuevo: ¿sigue habiendo prefijo varint de longitud? ¿sigue el `type_url` con el prefijo `type.ankama.com/`? ¿sigue el anidamiento `{f3:{f1:{Any}}}`?

Usa `C:\Jondo\scripts\fightdump.py` como base: ya hace el reensamblado y el parseo genérico. Si la marca `type.ankama.com/` sigue apareciendo, el 90 % del trabajo está hecho; si no, hay que reconstruir el envoltorio desde el `.proto` nuevo.

**Sin esto resuelto no sigas.** Todo lo demás depende del envoltorio.

### E.2 — Emparejamiento automático

Ejecuta `C:\Jondo\scripts\protomatch\align2.py` con los dos volcados. Lo que da hoy:

- Alineamiento monótono válido: **rachas de 61, 41, 36, 26, 26** tipos consecutivos con estructura idéntica, 89 % de coincidencia exacta entre los alineados. El orden de declaración **se conserva**, eso está probado.
- **27 pares estables** que salen iguales con métodos independientes:
  ```
  hhh→him  hmv→hnx  hnq→hpd  ibt→idy  igs→iiy  ilr→inv  ilu→iob
  isi→iue  itn→iwg  izh→jai  izu→jax  jos→jph  jpv→jpv  jrf→jsf
  jto→juq  jub→jvg  juc→jvr  juu→jym  kjn→klu  kku→knh  knx→kqo
  kof→kqq  kpa→krj  kpc→krl  lol→lqg  lou→lqo  lxh→lyy
  ```
- **Aviso honesto:** los pares que NO están en esa lista **no son fiables todavía**. Distintas funciones de puntuación razonables dan respuestas distintas (una dice `kqo→ksp`, otra `kqo→ktd`). La estructura sola no distingue entre mensajes con la misma forma, y hay cientos.

### E.3 — Anclar con las capturas

Aquí es donde las capturas de la Fase D cierran el problema:

1. Alinea cada captura nueva contra su equivalente vieja: misma secuencia de acciones → misma secuencia de mensajes, con tamaños y estructura parecidos. Un `joq` vacío no se distingue de otros 132 por estructura, pero **el noveno mensaje de una ráfaga, de 0 bytes, entre uno de 11 y uno de 2, solo puede ser uno**.
2. Ancla por **valores**: los ids del juego no rotan. Un mensaje que lleve un varint que existe en `MapPositions` justo después de cambiar de mapa es el mensaje de mapa. (Es la técnica que ya usa `scripts/johtrace.py`.)
3. Cada ancla confirmada se propaga: si el mensaje X está identificado, los mensajes que lo referencian quedan restringidos.

### E.4 — Producir el mapeo de campos, no solo de nombres

Para cada pareja confirmada, deriva la **permutación de números de campo** comparando los tipos: si el viejo tiene `{1:int32, 2:int64, 3:enum}` y el nuevo `{1:enum, 2:int32, 3:int64}`, el mapa de campos es `1→2, 2→3, 3→1`. Cuando haya varios campos del mismo tipo la permutación es ambigua: **anótalo y resuélvelo con la captura**, no lo adivines.

**Entregable de la fase:** `opcodes_3.6.8.8.json` con, por cada mensaje:
```json
{ "old": "jya", "new": "kaf", "confianza": "alta",
  "evidencia": ["estructura", "captura:368_combate_completo#109"],
  "campos": {"1":2, "2":3, "3":1, "4":6, "5":4, "6":5} }
```

**Puerta de verificación E:** el mapeo cubre los 121 opcodes que usa el emulador, cada uno con su evidencia. Los que queden sin resolver van marcados como `"confianza": "ninguna"` y se atacan uno a uno.

---

## FASE F — Capa de traducción en el emulador

Aquí está la decisión de diseño más importante del plan.

**Opción 1 — Capa de traducción (recomendada).** Un único punto que traduce nombre + renumera campos, dejando intacto el resto del código:

- **Salida:** en `NetworkEnvelope.BuildGameNodePacket`, traducir el `type_url` y **reescribir el payload** aplicando la permutación de campos del mensaje (recorrer los tags varint, sustituir el número de campo, reserializar). Recursivo para los submensajes.
- **Entrada:** en el router (`GameNodeProxy.cs:396-440`), extraer el opcode real del frame, traducirlo a nombre viejo y aplicar la permutación inversa antes de entregarlo a los handlers.
- Ventaja: **todo el emulador sigue hablando en el protocolo viejo**, y `FightHandler`, `StatsHandler`, etc. no se tocan.
- Coste: la reescritura de campos es delicada (los submensajes anidados tienen sus propias permutaciones), pero es ~300 líneas bien acotadas y testeables.

**Opción 2 — Reescribir los builders.** Cambiar los números de campo a mano en los ~12 archivos. Más simple conceptualmente, pero toca todo el código, no es reversible y no ayuda con la siguiente rotación.

Además, en cualquiera de las dos opciones:

1. **Arreglar el router de entrada.** Hoy hace `payloadStr.Contains("type.ankama.com/jyz")` sobre los bytes crudos: además de frágil (un payload puede contener esa marca), impide traducir limpio. Cambiar a extraer los 3 caracteres tras la marca y hacer `switch`.
2. `HaapiServer.cs:149` → `"build_override": "3.6.8"`.
3. Regenerar `Jondo.Unity.Protocol\Messages\Protocol.proto` (80 mensajes) y `Jondo.Unity.Launcher\Protocol\GameProtocol.proto` (20 mensajes) desde el esquema nuevo, y recompilar.
4. **`BasePayloads.WorldEnteringPackets`:** el volcado literal del servidor viejo **no sirve**. Dos caminos:
   - *Rápido:* sustituirlo por el volcado equivalente de `368_login_completo.pcapng`.
   - *Correcto:* aprovechar para construirlo orgánicamente desde SQLite, que es lo que ya pedía el plan de combate (§2.bis de [PLAN_COMBATE_V2.md](PLAN_COMBATE_V2.md)) — hoy lleva incrustados el mapa y las misiones de la sesión original.

---

## FASE G — Migrar el mod JondoFix

JondoFix es el segundo foco de riesgo, y por un motivo distinto: usa identificadores ofuscados **del ensamblado del juego**, no del protocolo.

### G.1 — Repuntar el proyecto

`JondoFix.csproj` referencia rutas fijas:
```xml
<Reference Include="C:\Jondo\DofusClient\MelonLoader\net6\*.dll" />
<Reference Include="C:\Jondo\DofusClient\MelonLoader\Il2CppAssemblies\*.dll" />
```
Cambiar `DofusClient` por `DofusClient368` (mejor: extraer la ruta a una propiedad MSBuild para poder alternar).

### G.2 — Lo que sobrevive sin tocar

Estos identificadores **no están ofuscados** y seguirán funcionando:
- `Il2CppCore.DataCenter.DataCenterModule` y `mapsCoordinatesDataRoot` / `mapScrollActionsDataRoot` / `mapsInformationDataRoot` / `subAreasDataRoot` → el volcado de CSV de mapas sigue igual.
- `Il2CppAnkama.SpinConnection.SpinProtocol.CheckAuthentication`
- `Il2CppZaap_CSharp_Client.ZaapClient.Connect`
- `Il2CppThrift.Transport.TNamedPipeClientTransport`
- `Il2CppCore.Localization.Utils.LocalizationAccessor.TryGetLocalization`
- Todos los parches de `Il2CppSystem.Net.*` (SslStream, Socket, TcpClient) y `UnityEngine.*`
- Toda la lógica de redirección a `127.0.0.1:5555` / `:8888`

Es decir: **la parte crítica de JondoFix (redirección + bypass SSL) migra sin cambios.**

### G.3 — Lo que hay que re-resolver

Los parches anti-crash de cartografía usan nombres ofuscados que **cambiarán**:

| Elemento actual | Qué es | Cómo re-resolverlo |
|---|---|---|
| `Il2Cpp.eud` | CartographyManager | Buscar en el nuevo `dump.cs`/interop la clase con los mismos métodos y campos |
| `eud.bcnn(ku, bool)` | procesa una misión | por firma: 1 parámetro de tipo "Quest" + bool |
| `eud.bcku()` | refresco de cartografía | sin parámetros, en la misma clase |
| `eud.bckp(List<int>)` | filtra subáreas | 1 parámetro `List<int>` |
| `eud.bcoh(Dictionary<Vector2, epo>)` | vuelca marcadores | por firma del diccionario |
| `Il2Cpp.ku`, `esm`, `gv`, `epo`, `esh`, `euh` | Quest, CartographyArea, QuestObjective… | por su uso en las firmas anteriores |
| campos `dqyj dqyh dqyi dqwn dqwp dqwi drac..drao dckz dclc` | estado interno | por tipo dentro de la clase ya identificada |

**Método recomendado:** en vez de re-adivinar por nombre, **localiza la clase por su forma** (una clase con un `Dictionary<long, X>`, un `Dictionary<int, Dictionary<string, Y>>`, un `List<Z>` y 4-5 campos privados de objeto). Es el mismo problema de emparejamiento de la Fase E, pero sobre `Il2Cpp.dll` en vez del protocolo, y con muchísimas menos candidatas.

**Alternativa pragmática:** esos parches existen para evitar cuelgues por metadatos de misiones nulos que provoca **el propio emulador** al no enviar misiones bien. Puedes **arrancar sin ellos** (comentarlos), ver si el cliente nuevo se cuelga, y solo re-resolverlos si hace falta. Empieza así: ahorra días.

### G.4 — Recompilar y desplegar

Compilar `JondoFix.csproj` y dejar el DLL en `C:\Jondo\DofusClient368\Mods\JondoFix.dll` (MelonLoader carga desde `Mods\`).

**Puerta de verificación G:** el log de MelonLoader del cliente nuevo muestra `JONDO REDIRECTOR & FIX` y `[+] DNS and Socket redirection is ACTIVE` con el emulador arrancado.

---

## FASE H — Regenerar los datos derivados

1. Con JondoFix nuevo cargado, borra `map_dump_coordinates.csv`, `map_dump_scrolls.csv`, `map_dump_infos.csv` y arranca el cliente: los regenera solo (`JondoFixMod.OnUpdate`).
2. Regenera `map_walkable_cells.json` con tus scripts de extracción, contra los datos del cliente nuevo.
3. **`world.db`:** verifica antes de tocar nada si los ids cambiaron. Prueba rápida: comprueba que el mapa de Incarnam (-2,-3) sigue siendo `154010883` y que el monstruo `493` sigue siendo "Pío amarillo". Si es así, **no toques la base de datos**.
4. `dofus3_data\items.json` y compañía: regenerar solo si detectas ids nuevos.

---

## FASE I — Puesta en marcha, por puertas

No intentes que funcione todo de golpe. Este es el orden y cada punto es una puerta:

1. **Trama.** El emulador acepta la conexión y el cliente no se desconecta al primer paquete. (Valida el envoltorio, Fase E.1.)
2. **Autenticación.** Llegas a la pantalla de selección de servidor. (Valida `GameProtocol.proto` y HAAPI.)
3. **Selección de personaje.** Ves tu personaje en la lista. (Valida la cadena de `Jondo.Unity.Protocol`.)
4. **Entrada al mundo.** Apareces en un mapa con tu personaje. (Valida la ráfaga de entrada, el punto más duro.)
5. **Movimiento.** Te mueves y cambias de mapa. (Valida `joi/joo/joh/kkr`.)
6. **Interfaces.** Inventario, características, chat.
7. **Combate.** Aplicar [PLAN_COMBATE_V2.md](PLAN_COMBATE_V2.md) sobre el protocolo nuevo.

Con la instrumentación S→C de la Fase 0 del plan de combate ya montada, cada puerta se diagnostica en minutos en vez de a ciegas.

---

## 2bis. Riesgos y puntos de no retorno

| Riesgo | Probabilidad | Mitigación |
|---|---|---|
| El envoltorio de trama cambió de forma incompatible | **Alta** (`leo` no tiene equivalente estructural) | Fase E.1 antes que nada; si no se resuelve, aborta la migración sin haber tocado nada |
| El mapeo de opcodes queda incompleto | Alta | Capturas de la Fase D; empezar por los ~40 opcodes de la ruta crítica (login → mundo → movimiento), no por los 121 |
| Los parches de cartografía de JondoFix no se pueden re-resolver | Media | Arrancar sin ellos (§G.3) |
| El cliente nuevo añade validaciones nuevas (anti-tamper) | Media | Se detecta en la puerta 1 o 2. No hay mitigación previa |
| Perder la capacidad de trabajar el combate | **Alta si borras el cliente viejo** | **No borrar nada** (§2) |

**No hay ningún punto de no retorno** si sigues §2. El único paso irreversible sería borrar `C:\Jondo\DofusClient` o sobrescribir los `.pcapng` viejos. No lo hagas.

---

## 3bis. Lo que NO se debe hacer

1. **No borres el cliente viejo** ni sus capturas. Son tu referencia y tu rollback.
2. **No migres el combate a medias.** Termina primero sobre 3.6.4.3.
3. **No asumas que basta con renombrar los opcodes.** Los números de campo también cambian (probado).
4. **No confíes en los pares del emparejador que no estén en la lista de 27.** Distintos métodos se contradicen; cada par necesita evidencia de captura.
5. **No uses los ensamblados de Il2CppInterop para sacar el esquema** — pierden los números de campo. Usa los de `cpp2il_out`.
6. **No te conectes al servidor oficial con JondoFix cargado.**
7. **No regeneres `world.db`** sin comprobar antes que los ids cambiaron.
8. **No reutilices `dofus3_sniffer_complete.proto`**: es incorrecto (colapsa `repeated` y `map` a `bytes`). Regenéralo.

---

## 4bis. Resumen de decisiones que tienes que tomar

1. **¿Migrar ahora o terminar el combate primero?** Mi recomendación es terminar el combate.
2. **¿Capa de traducción u reescribir los builders?** (Fase F). Recomiendo la capa: reversible y sirve para la próxima rotación.
3. **¿Re-resolver los parches de cartografía de JondoFix o arrancar sin ellos?** Recomiendo arrancar sin ellos y añadirlos solo si el cliente se cuelga.
