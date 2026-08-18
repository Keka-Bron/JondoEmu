# Separar el servidor del lanzador

Que se pueda abrir el lanzador, usarlo y cerrarlo sin que el servidor se entere. Hoy son el mismo
proceso y cerrar la ventana mata la partida de todo el mundo.

Está escrito a partir de lo que hay HOY en el código. Cada afirmación lleva su fichero y su línea.
Lo que es opinión mía va marcado como tal.

---

## 0. La conclusión primero: es más pequeño de lo que parece

Se levantaron seis mapas del acoplamiento por separado y luego se cruzaron contra el código. De unas
cincuenta ataduras aparentes quedan **cuatro nudos de verdad**, y toda la superficie de cruce entre
la carpeta `UI/` y el resto del emulador son **ocho líneas**:

```
LauncherService.cs:124, 139, 146, 147, 188, 212
Network/ClientLaunchRegistry.cs:50, 52
Program.cs:110
```

Ocho. No hay más. Conviene saberlo antes de empezar, porque el instinto dice "esto es un refactor de
tres semanas" y no lo es.

---

## 1. Los cuatro nudos

### 1.1 La vida del proceso cuelga de un `Form`

`Main` se para en `await _shutdown.Task` (`Program.cs:123`). Ese `TaskCompletionSource` sólo lo
completa `RequestShutdown` (`Program.cs:151`), y a `RequestShutdown` lo llaman tres sitios, de los
cuales el que importa es el hilo de la ventana cuando `Application.Run` devuelve
(`UI/LauncherWindow.cs:1475`). Da igual cómo se cierre —la X, Alt+F4, `Application.Exit`—: todas las
salidas pasan por ahí, porque `OnFormClosed` (`:619`) no apaga nada, sólo para los temporizadores.

Y no hay ningún conmutador: `UI.LauncherWindow.OpenOnDedicatedThread()` se llama incondicionalmente
(`Program.cs:110`), y el `args` de `Main` (`Program.cs:19`) **no se lee ni una sola vez**. Ésa es la
respuesta corta a "¿qué impide arrancar sin ventana?": nadie ha escrito la bifurcación.

### 1.2 El apretón de manos del arranque vive en la RAM

Es el nudo funcional gordo. `LauncherService.LaunchClient` se inventa un hash (`:138`), lo apunta en
`ClientLaunchRegistry.Register` (`:140`) y se lo pasa al `Dofus.exe` por línea de órdenes y por
variables de entorno (`:155`, `:168`). Después el cliente se lo presenta al Zaap, que lo busca **en
esa misma memoria** (`Network/ZaapServer.cs:24`).

Con dos procesos, el lanzador escribe en su memoria y el Zaap busca en la del servidor: no lo
encuentra y el cliente no llega a conectar nunca. **El lanzador tiene que pedirle el hash al
servidor, no inventárselo.**

### 1.3 No queda ningún canal por el que hablarle al servidor

Y lo curioso es que existía. Lo dice el propio código, en `Network/HaapiServer.cs:82-84`:

> *The `/api/login`, `/api/register`, `/api/launch`, `/api/status` and `/api/logs` routes used to
> live here. Only the web launcher called them; the native window talks to LauncherService
> directly, so they were dead weight.*

Al pasar de la interfaz web a la ventana nativa se borró exactamente la superficie que ahora hace
falta. Hoy no hay ni ruta HTTP de mando, ni tubería con nombre de control, ni orden de parada: el
único `NamedPipeServerStream` del árbol es el del Zaap hablando Thrift con el cliente
(`ZaapServer.cs:192`).

### 1.4 La ventana lee memoria del servidor y la llama "consulta"

Tres sitios, los tres triviales de arreglar pero los tres rotos el primer día:

* **Estado.** `GetStatus` hace `ZaapServer.IsRunning && GameServerProxy.IsRunning`
  (`LauncherService.cs:272`), dos `bool` estáticos del proceso. En el lanzador valdrían `false`
  siempre y diría "fuera de línea" con el servidor perfectamente vivo.
* **Consola.** `ConsoleLogBuffer` secuestra `Console.Out` (`ConsoleLogBuffer.cs:23-28`) y guarda las
  líneas en una cola estática (`:18`) que la ventana lee por `LauncherService.GetLogs` (`:292`). El
  panel se quedaría en blanco.
* **"En juego".** La ventana consulta `ClientLaunchRegistry.IsActive` y `ActiveCount` en siete
  sitios (`LauncherWindow.cs:936, 1071, 1087, 1101, 1121, 1242, 1267`). Pintaría todas las cuentas
  como libres.

---

## 2. La decisión de fondo: un solo ejecutable, dos modos

Aquí discrepo de lo que se dio por supuesto al levantar el mapa. La conclusión que salía era "dos
procesos = dos ejecutables = dos `Main`, y un ensamblado sólo admite uno, así que hay que sacar los
82 ficheros de lógica a una biblioteca antes de nada". **Eso es cierto para dos ejecutables y falso
para dos procesos.** El mismo `.exe` arrancado dos veces con argumentos distintos ya son dos
procesos.

Propongo eso: **un `Jondo Emulator Launcher.exe`, dos modos.**

```
Jondo Emulator Launcher.exe              → modo lanzador: ventana, sin servicios
Jondo Emulator Launcher.exe --servidor   → modo servidor: servicios, sin ventana
```

Por qué me parece mejor que partir en dos ejecutables:

* **La raíz se queda como está.** Es una restricción escrita del proyecto, en el propio
  `Jondo.Unity.Launcher.csproj:16-20`: *"La raíz del emulador no debe tener ficheros sueltos: quien
  se lo baje tiene que ver el .exe y carpetas, sin dudar de qué abrir."* Un segundo ejecutable va
  justo en contra.
* **El problema de compilación se evapora.** Sin separar ensamblados no hay que tocar las ocho
  líneas de cruce para *empezar*. Y la atadura más dura de todas —que `LauncherPreferences`
  (`UI/LauncherPreferences.cs:18`) y `LauncherTexts` (`:20`) son `internal`, así que partir el
  ensamblado rompe la compilación por visibilidad antes de que WinForms entre en la conversación—
  deja de bloquear.
* **Se llega al objetivo del usuario en la primera fase**, no en la última.

Lo que se paga: el proceso servidor sigue siendo un `WinExe` que carga WinForms aunque no dibuje
nada, y no puede correr como servicio de Windows ni sin sesión de escritorio. Para dos procesos en
la misma máquina —que es lo que hay, y lo argumento abajo— no molesta. El día que se quiera un
servidor de verdad headless, la fase 4 lo deja listo.

---

## 3. Quién se queda con qué

Contestado siguiendo lo que el cliente habla de verdad:

| Servicio | Puerto | Lo habla | Va al |
|---|---|---|---|
| HaapiServer | 8888 | el cliente Dofus | **servidor** |
| ZaapServer | 15881 (TCP + tubería con nombre "15881") | el cliente Dofus | **servidor** |
| GameServerProxy | 5555 | el cliente Dofus | **servidor** |
| GameNodeProxy | 5556 | el cliente Dofus | **servidor** |
| ChatServer | 6337 | el cliente Dofus | **servidor** |

**Los cinco se van al servidor.** El lanzador no habla ninguno: lo único que hace con los puertos es
pasárselos al `Dofus.exe` como argumentos (`LauncherService.cs:155, 157, 167`).

Al lanzador le quedan tres cosas, y sólo tres: **la ventana**, **las preferencias**
(`%APPDATA%\Jondo\lanzador.cfg`) y **arrancar el proceso del cliente** (`LauncherService.cs:177`,
con su `ShowWindow` de user32 en la `:251`).

Y una consecuencia que conviene dejar escrita: **esto son dos procesos en la misma máquina, no un
servidor en red.** El lanzador arranca el `Dofus.exe` y le manipula la ventana; eso sólo funciona en
local. Además Zaap y GameServerProxy sólo escuchan en `127.0.0.1` (`ZaapServer.cs:161`,
`GameServerProxy.cs:37`) y el HAAPI en `localhost` (`HaapiServer.cs:21-22`). Un servidor remoto
serían cinco `bind` y seis literales más, y es otro proyecto.

---

## 4. Las fases

Cada fase deja el emulador funcionando. Nada de dejarlo roto por el medio.

### Fase 0 — Los dos modos, y ya se puede cerrar el lanzador

Es la fase que resuelve la petición. Lo demás es acabado.

1. `Main` lee `args`. Con `--servidor`: base, managers, los cinco servicios, y a vivir; sin
   argumentos: sólo la ventana.
2. En modo servidor, la vida del proceso deja de colgar de `_shutdown` y pasa a colgar de Ctrl+C y
   de una orden de parada. En modo lanzador, quitar el `RequestShutdown` de
   `UI/LauncherWindow.cs:1475` y el suicidio de `Program.cs:117`.
3. El modo lanzador, al abrirse, **sondea el 8888**. Si no contesta, se arranca a sí mismo con
   `--servidor` en un proceso suelto y espera a que responda.
4. **Un botón explícito de "Detener el servidor"** en la ventana. Hoy el único modo de pararlo es la
   X, y si la X deja de apagarlo hay que dar otra puerta.
5. **Guardia de instancia única** por modo (un mutex con nombre). Hoy no hay ninguno: dos servidores
   se pelean por el 8888 y por la tubería "15881", y el segundo muere en `Program.cs:73-87`
   escribiendo el error en una consola que en `WinExe` no existe. Doble clic que no hace nada.
6. De paso, arreglar una mentira: `ZaapServer.Start` pone `_isRunning = true` (`:158`) **antes** de
   `_tcpListener.Start()` (`:163`), y `GameServerProxy` hace lo mismo (`:34` antes de `:38`). Si el
   `bind` falla, `IsRunning` dice que sí. Hoy se tapa porque el proceso se muere; con dos procesos y
   reintentos, no.

### Fase 1 — El canal de mando

Reconstruir lo que se borró en `HaapiServer.cs:82-84`. **Opinión: HTTP sobre el 8888**, porque
`HaapiServer` ya es un `HttpListener`, porque es el puerto que el mod sondea de todos modos, y
porque se prueba con un navegador. Atado a `127.0.0.1`.

Los verbos, por orden de necesidad:

| Verbo | Para qué | Sin él |
|---|---|---|
| `estado` | el semáforo de la ventana | el lanzador dice siempre "fuera de línea" |
| `lanzamiento` | pedir hash e instanceId | **no arranca ni un cliente** |
| `activos` | quién está en juego, cuántos | el punto de "En juego" y el tope de 8 |
| `entrar` / `crear-cuenta` | login y registro | dos procesos escribiendo `auth.db` |
| `registro?desde=N` | el panel de consola | panel en blanco |
| `apagar` | el botón de detener | sólo se puede matar a mano |

El `estado` puede ser incluso más barato: sondear el 5555 desde fuera, que es exactamente lo que ya
hace el mod (`JondoFix/Class1.cs:471-477`).

Para el registro, **no vale hacer `tail` de `logs/emulator_console.log`**: el volcado a fichero
(`ConsoleLogBuffer.cs:43`) no reconstruye las entradas y los identificadores de secuencia sólo
existen en RAM (`:55`), y la ventana los usa para pedir sólo lo nuevo. Tiene que ir por el canal.

### Fase 2 — El registro de lanzamientos se muda al servidor

`ClientLaunchRegistry` pasa a ser del servidor entero. El lanzador pide `{hash, instanceId}` por el
canal, arranca el `Dofus.exe` con lo que le den, y avisa de la baja cuando el proceso muere
(`LauncherService.cs:192`).

Y darle **caducidad del lado del servidor**, que hoy no tiene ninguna:

* Engancharlo al `finally` que **ya existe** en `GameServerProxy.cs:161-162`, donde se quita la
  sesión al morir el socket. El servidor ya sabe de qué cuenta era (`GameSession.cs:28`); lo único
  que falta es el cable hasta `ClientLaunchRegistry.Remove`.
* Darle por fin uso a `CreatedAtUtc` (`ClientLaunchRegistry.cs:61`) como caducidad, para el cliente
  que arranca y nunca llega al 5555. Hoy ese campo se escribe y no lo lee nadie —igual que
  `LauncherToken`—.

Sin esto, cerrar el lanzador deja cuentas colgadas y `Register` las rechaza para siempre
(`ClientLaunchRegistry.cs:49-50`).

### Fase 3 — Un solo dueño de la base de datos

El lanzador toca `DatabaseManager` en **cuatro sitios**: `LauncherService.cs:68, 71, 96, 271`. Si
esos cuatro se piden al servidor, el lanzador se queda sin base y sin SQLite, y de paso el contador
de intentos por IP (`DatabaseManager.cs:836`) vuelve a ser uno solo en vez de dos.

Y con dos escritores hay que poner `busy_timeout` en la cadena de conexión —hoy no hay ninguno en
todo el árbol, sólo `journal_mode=WAL` (`DatabaseManager.cs:27` y `:98`)—, así el segundo espera en
vez de llevarse un `SQLITE_BUSY` inmediato. Esto ya estaba pendiente como fase 7 del plan de
multijugador; aquí se cobra.

### Fase 4 — La higiene, para que algún día pueda ser un servidor de verdad

Las ocho líneas de cruce, y sólo entonces la separación en ensamblados:

* Códigos de error en vez de frases en `ClientLaunchRegistry.cs:50, 52` y `LauncherService.cs:124,
  188`; que traduzca el lanzador. **Esas dos de `ClientLaunchRegistry` las metí yo hoy** al mover
  los textos franceses al catálogo: arreglé un problema y creé otro más pequeño, un trozo de
  servidor que lee las preferencias de idioma del usuario.
* El idioma y la ruta del cliente, decididos por quien llama (`LauncherService.cs:139, 212`).
* `Screen.PrimaryScreen` y `System.Drawing.Rectangle` fuera de `LauncherService` (`:146-147`). Son
  las **únicas dos líneas de WinForms** en código que no está en `UI/`; el resto de la carpeta `UI/`
  son cadenas y ficheros, sin una sola referencia a `System.Windows.Forms`.
* Y entonces sí: biblioteca con la lógica, y dos puntos de entrada si se quiere.

---

## 5. Cuidado con estas cinco

Cosas que hoy no se ven porque todo arranca en el mismo orden, y que muerden en cuanto haya dos
procesos.

1. **El cliente se va a los servidores de Ankama si el emulador no está levantado.** Es la peor de
   todas. El mod decide **una sola vez**, al inicializarse, si redirige: `UseLocalRedirect =
   IsEmulatorActive()` (`JondoFix/Class1.cs:121`), e `IsEmulatorActive` es un
   `TcpClient.BeginConnect` a `127.0.0.1:8888` con **100 ms** de espera (`:471-477`). Si el sondeo
   falla, el cliente no da ningún error: se conecta hacia fuera. Hoy no pasa nunca porque
   `Program.cs:75` levanta el HAAPI antes de que exista la ventana desde la que se lanza. **El
   lanzador tiene que comprobar que el 8888 contesta antes de llamar a `Process.Start`**, y no
   fiarse de haberlo arrancado él.
2. **Instalación limpia con el lanzador abierto antes que el servidor.** El esquema lo crea sólo
   `DatabaseManager.Initialize` (`DatabaseManager.cs:31-41`), y su único llamador es
   `Program.cs:35`. Crear cuenta antes revienta con `no such table: Accounts`, y ese texto se
   enseña tal cual (`LauncherService.cs:105` → `LauncherWindow.cs:1215`).
3. **El formato del registro es una API de facto.** La ventana colorea las líneas buscando
   `"[DatabaseManager]"` y `"[World]"` dentro del texto (`LauncherWindow.cs:1375`). Lo que se monte
   para llevar la consola al otro proceso tiene que preservar el texto tal cual.
4. **Los guardias de arranque tocan el registro real.** `RegressionGuardTests.Run()`
   (`Program.cs:67`) llama a `AssertTwoClientsAreIsolated` y `AssertEightClientLimit`, que registran
   diez lanzamientos de mentira y los borran, dejando `_nextInstanceId` en 10. Con el registro ya
   del lado servidor, eso sólo debe correr en el proceso que sea su dueño.
5. **El servidor muriendo con clientes dentro.** Nadie avisa. El lanzador se enteraría a los dos
   segundos por `CheckStatus` (`LauncherWindow.cs:1013`). Al rearrancar, los diccionarios salen
   vacíos y el token que repartió `TokenResponse` (`HaapiServer.cs:208-211`) desaparece del todo,
   porque ese camino no escribe en la base. Y en cierre limpio sí se graba el personaje
   (`GameServerProxy.cs:153`), pero en muerte del proceso no.

---

## 6. Por dónde empezaría

La fase 0 entera y parar. Son un puñado de líneas —leer `args`, no llamar a `RequestShutdown` al
cerrar la ventana, un mutex y un sondeo— y ya cumple lo que se pedía: abrir el lanzador, usarlo,
cerrarlo, y que la partida siga. Todo lo demás de la lista son cosas que **se ven mal** con el
lanzador cerrado —el semáforo, el panel de consola, el punto de "En juego"— pero que no impiden
jugar.

La fase 1 va inmediatamente después porque sin canal el lanzador no puede volver a arrancar un
cliente contra un servidor que ya estaba vivo, y ése es el caso normal a partir de la fase 0.
