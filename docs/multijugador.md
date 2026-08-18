# De un jugador a muchos

> **Documento histórico.** El plan de sesiones de las primeras secciones ya está implementado y la
> descripción autoritativa del código actual vive en `sessions.md`; el launcher de ocho cuentas se
> documenta en `launcher.md`. Este fichero se conserva por el razonamiento de las fases que quedan,
> sobre todo los combates realmente multijugador, pero sus afirmaciones sobre la ausencia de
> `GameSession` o de broadcasts ya no describen el estado actual del proyecto.

Plan para que el emulador aguante varias sesiones a la vez: varios clientes conectados, cada uno
con su cuenta y su personaje, viéndose entre ellos en el mapa y compartiendo los monstruos; y un
launcher desde el que abrir más de una cuenta.

Se escribió a partir del estado del código anterior al refactor de sesiones. Las líneas y los
diagnósticos de abajo son una fotografía histórica, no referencias vigentes.

---

## 0. Sobre `sessions.md`

`sessions.md` ya describe la implementación vigente: `GameSession`, `SessionState`,
`SessionContext`, el registro activo, los broadcasts por mapa, la serialización por socket y la
entrada real del puerto 5555. `launcher.md` completa la parte de procesos y cuentas. Lo que sigue
explica por qué se eligió ese modelo y conserva el plan de los aspectos multijugador que todavía no
están terminados.

---

## 1. Lo que lo impedía al escribir el plan, por orden de dependencia

El orden importaba: cada bloqueo tapaba al siguiente. Varias entradas de esta lista ya están
resueltas; consultar `sessions.md` para el estado actual.

1. **`GameState` es estático.** `GameState.cs:6`, con identidad, posición, kamas, experiencia,
   características, inventario y equipo. En cuanto un segundo cliente elige personaje,
   `DatabaseManager.LoadCharacter` (`DatabaseManager.cs:1358`) pisa la identidad del primero.
2. **No hay registro de sesiones ni de sockets.** Ningún `NetworkStream` se guarda en ningún sitio:
   es una variable local (`Network/GameNodeProxy.cs:71`) que se pasa a los manejadores. **Hoy es
   literalmente imposible mandarle un paquete a otro jugador.**
3. **No hay difusión por mapa.** El `jss` sólo lleva el actor propio
   (`Network/GameNodeProxy.cs:253`) y el `jpv` empieza con `int totalActors = 1;`
   (`Handlers/MapLoadHandler.cs:104`).
4. **El combate es de uno.** `GetCurrentFight()` devuelve `_activeFights.Values.FirstOrDefault()`
   (`Handlers/FightHandler.cs:884`), y `InitiateFightFromMobCollision` hace `_activeFights.Clear()`
   antes de empezar (`Handlers/FightHandler.cs:40`). Además la ronda, el contador de acciones y los
   límites de lanzamiento por turno son estáticos globales (`:1059`, `:1062`, `:2133`, `:2134`).
5. **`SaveCurrentCharacter()` no recibe a quién guardar.** `DatabaseManager.cs:1412` compone el
   `UPDATE` leyendo `GameState`. Con dos jugadores esto no pierde datos: los **mezcla entre
   cuentas**, que es peor.

Y dos que no están en esa cadena pero muerden igual:

6. ~~**Los identificadores de actor se recalculan en cada carga de mapa y por cliente.** Los NPCs
   empiezan en `-20000` y bajan (`Handlers/MapLoadHandler.cs:112`) y los monstruos siguen la cuenta
   desde donde la dejaron los NPCs (`:122`). Dos jugadores en el mismo mapa recibirían números
   distintos para el mismo bicho, así que ningún aviso de "el mob X se ha movido" sería
   coherente.~~ Hecho, fase 4. Y era peor de lo que dice aquí: el mismo cliente ya recibía dos
   números distintos para el mismo grupo, uno en el `jss` y otro en el `jpv`.
7. **SQLite está en WAL (`DatabaseManager.cs:27` y `:98`) pero sin `busy_timeout`.** Con dos
   escritores, el segundo se lleva un `SQLITE_BUSY` inmediato en vez de esperar. Y el uid de objeto
   se saca con `SELECT MAX(Uid)` (`Handlers/NpcHandler.cs:326`), que es una carrera de manual.

---

## 2. El modelo al que hay que llegar

```
una conexión TCP de juego
   └── una Sesion
         ├── una cuenta y un servidor        (del ticket, ya existe)
         ├── cero o un personaje
         ├── un EstadoDePersonaje            (lo que hoy es GameState)
         └── un socket con su candado de escritura
```

Y por encima, tres registros de MUNDO, que no son de nadie en particular:

```
Mundo
 ├── Sesiones          quién está conectado, y en qué mapa
 ├── Mapas             qué actores hay en cada mapa: jugadores, NPCs, grupos de monstruos
 └── Combates          los combates en curso, cada uno con sus participantes y sus sesiones
```

La regla que decide dónde va cada dato, y que conviene tener a mano al migrar cada fichero:

* Si lo cambia el jugador y sólo le afecta a él, es de la **sesión** (posición, inventario, kamas,
  diálogos abiertos, borradores).
* Si dos jugadores tienen que verlo igual, es del **mundo** (los grupos de monstruos de un mapa,
  quién hay en un mapa, un combate).
* Si no cambia nunca, es una **tabla** y puede seguir siendo estática (plantillas, traducciones,
  la tabla de experiencia, el catálogo de efectos).

---

## 3. Las fases

Cada fase deja el emulador funcionando con un jugador. Eso es innegociable: nada de un refactor de
tres semanas con el servidor roto por el medio.

### Fase 1 — La sesión, sin cambiar comportamiento

Meter el estado en un objeto, sin tocar todavía nada de multijugador.

* Nace `Sesion` con `Id`, `Socket`, `CuentaId`, `ServidorId`, `Personaje` (el `EstadoDePersonaje`)
  y `EnElMundo`.
* `GameState` **no se borra de golpe**: se convierte en una fachada que reenvía a la sesión actual.
  Con eso los 425 usos siguen compilando y se van migrando por ficheros, empezando por los que más
  tienen: `FightHandler` (96), `DatabaseManager` (47), `StatsHandler` (44), `GameNodeProxy` (37).
* La sesión llega a los manejadores **por parámetro**, no por `AsyncLocal`.

  Aquí discrepo del documento del compañero. `AsyncLocal` compila sin tocar firmas, sí, pero
  revienta justo donde más falta hace: la IA de los monstruos, los temporizadores de turno
  (`Handlers/FightHandler.cs:1820`) y el repoblado de grupos corren FUERA del hilo del socket y se
  quedarían sin contexto. Y esos son precisamente los tres trabajos de fondo que un servidor
  multijugador necesita. Cambiar firmas es más pesado y es lo correcto.
* `SaveCurrentCharacter()` pasa a `GuardarPersonaje(EstadoDePersonaje)`. Es un cambio pequeño y
  quita el peor punto de corrupción.

Prueba de que la fase está bien: un jugador sigue jugando exactamente igual, y `GameState` ya no
tiene ningún campo mutable propio.

### Fase 2 — El registro de sesiones y las escrituras seguras

* `SessionRegistry` (`Network/SessionRegistry.cs`, hoy sólo tickets) crece con un segundo
  diccionario de sesiones vivas, y con `EnElMapa(mapId)` devolviendo una **copia**.
* En `GameNodeProxy`, alta al conectar y baja en un `finally`, con guardado del personaje al cerrar
  —que hoy no se hace en ninguna parte.
* **Un candado de escritura por socket.** Hoy `Protocol/NetworkMessage.cs:108-109` escribe la
  longitud y el cuerpo en dos llamadas: con dos hilos escribiendo al mismo cliente sale
  `longitud A, longitud B, cuerpo A, cuerpo B` y el cliente se desincroniza para siempre. Se
  arregla componiendo un solo buffer y serializando con un semáforo por socket. Sin esto, cualquier
  difusión corrompe la conexión.

### Fase 3 — Verse en el mapa

* Un `RegistroDeMapas` que sepa quién está en cada mapa, alimentado al entrar y al salir.
* `jsn` a los demás cuando alguien llega; `jsd` a los demás cuando se va —hoy el `jsd` existe pero
  se manda a uno mismo (`Handlers/WorldMoveHandler.cs:204`, `Handlers/ZaapTravelHandler.cs:170`).
* El `jpv`/`jss` de entrada deja de llevar un actor y lleva los que haya
  (`Handlers/MapLoadHandler.cs:104`).
* El `jsj` del movimiento se difunde. El comentario que hay en `Handlers/WorldMoveHandler.cs:56`
  ya dice que el `jsj` es cómo se entera otro cliente "and there is nobody else here": pues ya lo
  hay.
* El chat `kqp` deja de ser un eco (`Handlers/ChatHandler.cs:33`) y va al mapa o al canal.

### Fase 4 — Identificadores de actor estables — HECHA

Antes de tocar los monstruos hay que arreglar esto o nada cuadrará entre clientes.

* Un asignador único de ids de actor por mundo, **asignado al aparecer** y no al cargar el mapa.
* Los rangos, separados y sin solaparse: jugadores por su `CharacterId`, NPCs en un tramo, mobs en
  otro, invocaciones en otro.
* Los mobs guardan su id en el `MobGroup`, no lo calculan en `MapLoadHandler`.

Lo que había, y que resultó estar roto **ya con un jugador**: el cliente pide los dos mensajes de
actores al cargar un mapa —el `jss` con el `jrh` y el `jpv` con el `kkr`— y los dos repartían
números distintos para el mismo bicho. En el mapa de los NPCs de Amakna, medido: el `jss` daba a
los dos grupos de monstruos el −1011567 y el −1011566, que son sus `MobId`, y el `jpv` les daba el
−20052 y el −20053, porque los numeraba por su posición detrás de los 52 NPCs. El cliente devuelve
el que le llegó el último, así que atacar caía en un `mobs.FirstOrDefault()` de
`Handlers/FightHandler.cs` y el jugador peleaba contra otro grupo —y al ganar desaparecía del mapa
ese otro—.

Cómo queda:

* `Managers/Actores.cs` es el único que reparte, con las bandas y con `EsJugador`/`EsNpc`/
  `EsMonstruo`. Los monstruos se piden con `Interlocked`: el generador de un mapa vacío corre en el
  hilo del jugador que llega, y dos llegando a la vez hacían el mismo `_id--`.
* Al arrancar, `ReservarMonstruosHasta` baja el cursor por debajo del `MobId` más bajo que venga
  escrito en la base, así que los grupos generados al vuelo no pisan a los sembrados. Antes eran
  dos bandas fijas, −1000000 y −2000000, y con más de un millón de grupos sembrados se cruzaban.
* El `jpv` sale de `MapLoadHandler.ConstruirJpv`, separado del envío para poder compararlo con el
  `jss` en el banco de pruebas. Ya no calcula ningún id: el del grupo es su `MobId` y el del NPC es
  el que le puso `Managers.Npcs` al arrancar.
* Los NPCs se leían dos veces, y de las dos lecturas salían los ids: `Managers/Npcs.cs` con
  `ORDER BY MapId, Id` y `DatabaseManager.GetNpcSpawnsForMap` **sin `ORDER BY` ninguno**. Que
  coincidieran dependía del plan que eligiera SQLite. Ahora hay una sola lista, y de paso el
  `jpv` se ahorra un recorrido entero de `NpcSpawns` en cada carga de mapa.
* Fuera `PatchJpvEnteringPacket` de `Network/GameNodeProxy.cs`, que no llamaba nadie y llevaba tres
  ids de personaje de las capturas escritos a mano.

Y dos cosas de otras fases que se adelantaron porque estorbaban aquí: el `_mapMobs` de
`MobSpawnManager` va con candado —es un `Dictionary` pelado que se toca desde el hilo de cada
jugador— y el `_activeFights.Clear()` de `Handlers/FightHandler.cs:40` pasa a quitar sólo el
combate del propio jugador. Lo demás de las fases 5 y 6 sigue sin tocar.

### Fase 5 — Monstruos compartidos

* `MobSpawnManager` pasa de `Dictionary` planos (`Managers/MobSpawnManager.cs:40-41`, mutados sin
  candado desde tres sitios) a colecciones concurrentes, o mejor, a un actor único por mapa que
  serialice los cambios.
* Un grupo en combate se marca como ocupado: hoy `GetMobAtCell` (`:361`) le daría el mismo grupo a
  dos jugadores que pinchen a la vez.
* Al morir un grupo, `jsd` del actor a los del mapa; al repoblar, `jsn`.
* Los grupos generados al azar (`GenerateDynamicMobsForMap`, `:265`) no se escriben nunca a la base.
  Decidir si eso está bien —yo diría que sí, que se regeneren al arrancar— pero dejarlo dicho.

### Fase 6 — Combates de varios

* Quitar el `_activeFights.Clear()` de `Handlers/FightHandler.cs:40` y `:265`.
* `GetCurrentFight()` muere; se busca el combate por sesión o por combatiente.
* La ronda, el contador de acciones y los límites por turno (`:1059`, `:1062`, `:2133`, `:2134`) se
  mudan dentro de `FightInstance`, que ya es un objeto por combate y sólo le falta esto y la lista
  de sesiones.
* Un envío a todo el combate, en vez de escribir en el socket del que actuó.
* Y lo que hoy no existe en absoluto: espectadores, unirse a un combate empezado, y colocación con
  varios por bando.

### Fase 7 — La base de datos en serio

* `busy_timeout` en la cadena de conexión. Hoy no hay ninguno en todo el árbol.
* Un único escritor lógico por personaje, o transacciones que agrupen las operaciones de varios
  pasos —mover un objeto a un cofre, comprar— que hoy van sueltas.
* Fuera el `SELECT MAX(Uid)` de `Handlers/NpcHandler.cs:326`: un contador con `Interlocked` o una
  columna autoincremental.
* Guardado al desconectar y guardado periódico.

### Fase 8 — El launcher multicuenta

Lo que hoy lo ata a una cuenta:

* `HaapiServer.ActiveAccount` (`Network/HaapiServer.cs:207`) es **un solo campo estático** que
  sobrescribe cada `SignIn` (`LauncherService.cs:70`), y de él salen todas las respuestas de HAAPI
  y de Zaap. El segundo login le cambia la cuenta al primero por debajo.
* `Accounts.GameToken` es una columna por cuenta: un login nuevo invalida el token del anterior.
* `--instanceId 1` y `ZAAP_INSTANCE_ID=1` son literales (`LauncherService.cs:149`, `:165`), igual
  que `--port 15881`; el named pipe de Zaap se llama por el número de puerto
  (`Network/ZaapServer.cs:178`), así que dos clientes se pelean por el mismo canal.
* La ventana guarda un `_token` y una `_account` (`UI/LauncherWindow.cs:37-38`).
* `logs/gameserver_traffic.log` se escribe con `File.AppendAllText` **sin candado**
  (`Network/GameServerProxy.cs:281`): con dos sesiones se entrelaza y se pierde.

El plan:

1. La ventana pasa de una sesión a una **lista de cuentas conectadas**, cada una con su token, su
   proceso de cliente y su estado.
2. HAAPI y Zaap dejan de tener cuenta activa: resuelven **por el token o por el instanceId** que
   les llega. Es el cambio de fondo de esta fase.
3. `instanceId` y el puerto de Zaap, uno por sesión. Conviene mirar `zaap-start.bat`, que es lo que
   usa Ankama para multicuenta y ya pasa un `-logFile` por cuenta; hoy no se usa.
4. El log de tráfico, uno por sesión, o uno solo con candado y con la sesión en cada línea.
5. Comprobar si `Dofus.exe`/MelonLoader llevan guard de instancia propio y si la carpeta
   `Cliente 3.6.10.10` aguanta dos procesos (comparten `MelonLoader/Latest.log` y
   `UserData/MelonPreferences.cfg`). Esto hay que **probarlo**, no suponerlo.

---

## 4. Por dónde empezaría

Fases 1 y 2 juntas, y no tocar nada de multijugador hasta tenerlas. Son el 80% del trabajo aburrido
y el 100% del riesgo: mientras `GameState` sea estático y las escrituras al socket no estén
serializadas, cualquier cosa que se construya encima hereda los dos fallos.

La fase 4 (ids de actor) es pequeña y va antes que la 3 y la 5 aunque no lo parezca: sin ids
estables, los avisos entre clientes son incoherentes y se depura a ciegas.

Y una prueba que merece la pena montar pronto: **dos clientes falsos** —ya hay un
`tools/cliente_falso.py`— conectados a la vez, andando por el mismo mapa, comprobando que cada uno
recibe los actores del otro. Sin eso, cada fase se prueba a mano y se tarda más en cada vuelta.
