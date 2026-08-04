# Documentación técnica Jondo — Parte 1: arranque, conexión y entrada al mundo

**Versión del cliente:** Dofus 3.6.4.3 (`C:\Jondo\DofusClient`)
**Alcance:** desde que se ejecuta `Dofus.exe` hasta que el personaje aparece en el mapa.
**Fecha:** 2026-08-04

Cubre: MelonLoader, JondoFix, certificados/TLS, Zaap (Thrift), HAAPI (HTTP), servidor de conexión, Game Node, el framing del protocolo, y cómo obtener los `.proto` originales del cliente.

---

## 1. Panorama general

El emulador es **un solo proceso** (`Jondo.Unity.Launcher`) que levanta cinco servidores en paralelo. El cliente oficial de Dofus se ejecuta sin modificar su binario: se le inyecta un mod (**JondoFix**) mediante **MelonLoader** que redirige todo su tráfico a `127.0.0.1`.

```
┌──────────────────────────────────────────────────────────────────┐
│  Dofus.exe  (cliente oficial, IL2CPP/Unity)                      │
│    └── version.dll  →  MelonLoader  →  Mods\JondoFix.dll         │
│           · redirige sockets y HTTP a 127.0.0.1                  │
│           · desactiva la validación TLS                          │
│           · parchea cuelgues de cartografía                      │
└───────────┬──────────────────────────────────────────────────────┘
            │
   ┌────────┼──────────┬──────────────┬─────────────┐
   ▼        ▼          ▼              ▼             ▼
 :15881   :8888      :5555          :5556        :6337
 Zaap     HAAPI    Servidor de     Game Node     Chat
 (Thrift) (HTTP)   conexión        (reserva)     (TLS)
                   + Game Node
                   (mismo puerto)
```

### Puertos (definidos en `Program.cs:12-15`)

| Puerto | Constante | Servidor | Protocolo |
|---|---|---|---|
| **15881** | `port` | `ZaapServer` | Thrift binario / Named Pipe / WebSocket |
| **8888** | `haapiPort` | `HaapiServer` | HTTP plano (JSON) |
| **5555** | `gamePort` | `GameServerProxy` | Protobuf con framing varint |
| **5556** | `gameNodePort` | `GameNodeProxy` | idem (listener propio, poco usado) |
| **6337** | fijo | `ChatServer` | TLS con certificado autofirmado |

> **Nota sobre 5555 y 5556:** en la práctica el cliente hace **todo** por el 5555. `GameServerProxy` detecta por contenido si la sesión es de conexión o de Game Node y delega (§8). El listener del 5556 existe pero el flujo normal no pasa por él.

### Arranque

`Program.Main` (`Program.cs:19`):
1. `DatabaseManager.Initialize()` — abre/crea `world.db` y `auth.db`, aplica migraciones y siembra el personaje.
2. `MobSpawnManager.InitializeAndSpawnAll()` — carga los 5.134 monstruos y los grupos por mapa.
3. `MapManager.Initialize()` — carga `map_walkable_cells.json` y las tablas de mapas.
4. Levanta los cinco servidores.
5. **Lanza el cliente automáticamente** si existe `C:\Jondo\DofusClient\Dofus.exe`.
6. Entra en un bucle de consola con `/status`, `/teleport`, `/exit`.

---

## 2. Lanzamiento del cliente

El cliente necesita creer que lo ha lanzado el launcher oficial de Ankama (Zaap). Eso se consigue con **argumentos de línea de comandos** y **variables de entorno**.

`Program.cs:76-92`:

```
Dofus.exe -force-d3d11
          --port 15881          <- puerto del servidor Zaap
          --gameName dofus
          --gameRelease dofus3
          --instanceId 1
          --hash <GUID aleatorio>
          --canLogin true
          --langCode es
          --autoConnectType 1
          --connectionPort 5555
```

Variables de entorno inyectadas en el proceso hijo:

| Variable | Valor |
|---|---|
| `ZAAP_PORT` | `15881` |
| `ZAAP_HASH` | el mismo GUID que `--hash` |
| `ZAAP_GAME` | `dofus` |
| `ZAAP_RELEASE` | `dofus3` |
| `ZAAP_INSTANCE_ID` | `1` |
| `ZAAP_CAN_AUTH` | `true` |

El cliente lee ambas fuentes; JondoFix las vuelca al log al arrancar (`Class1.cs:97-102`) para poder verificarlas.

> ### ⚠️ Incoherencia detectada
> `DofusClient\launch_jondo.bat` lanza el cliente con **`--port 8080`**, mientras `Program.cs` usa **15881**. Si arrancas con el `.bat`, el cliente buscará Zaap en un puerto donde no hay nadie. **Usa el lanzamiento automático del emulador**, o corrige el `.bat` a 15881.

---

## 3. MelonLoader

MelonLoader es un cargador de mods para juegos Unity. En IL2CPP hace dos cosas:

1. **Se inyecta** en el proceso mediante un DLL proxy: el fichero `version.dll` en la carpeta del juego. Windows carga `version.dll` desde el directorio del ejecutable antes que la del sistema, y MelonLoader aprovecha eso para arrancar dentro del proceso. Renombrarlo a `version.dll.disabled` lo desactiva por completo.
2. **Genera ensamblados de interoperabilidad**: convierte el código nativo IL2CPP en DLL de .NET manejables, para que un mod en C# pueda llamar a las clases del juego.

### Estructura en disco

```
DofusClient\
   version.dll                                  <- el proxy (renombrar a .disabled para desactivar)
   MelonLoader\
      net6\           MelonLoader.dll, Il2CppInterop.*, 0Harmony.dll, AsmResolver.*
      Dependencies\
         Il2CppAssemblyGenerator\
            Cpp2IL\          <- el desensamblador
            Il2CppInterop\   <- el generador de interop
            UnityDependencies\
            Config.cfg       <- cachea GameAssemblyHash y UnityVersion
         SupportModules\, CompatibilityLayers\
      Il2CppAssemblies\      <- ~174 DLL generados (aquí vive el protocolo)
      Logs\, Latest.log
   Mods\
      JondoFix.dll                              <- nuestro mod
```

### Primera ejecución

Al arrancar por primera vez con un `GameAssembly.dll` nuevo, MelonLoader:
1. Ejecuta **Cpp2IL**, que lee `GameAssembly.dll` + `global-metadata.dat` y produce DLL "dummy" en `Cpp2IL\cpp2il_out\`.
2. Ejecuta **Il2CppInterop**, que convierte esos dummy en ensamblados usables y los deja en `Il2CppAssemblies\`.
3. Tarda entre 3 y 10 minutos. La consola parece congelada; es normal.

Si el hash de `GameAssembly.dll` no ha cambiado respecto a `Config.cfg`, salta el paso y arranca en segundos.

**Para forzar la regeneración** (por ejemplo al actualizar el cliente): borrar `Il2CppAssemblies\` y vaciar el valor de `GameAssemblyHash` en `Config.cfg`.

---

## 4. JondoFix — el mod

`Jondo Unity Emulator\JondoFix\Class1.cs`. Se compila contra los ensamblados de MelonLoader y se despliega como `DofusClient\Mods\JondoFix.dll`.

```xml
<Reference Include="C:\Jondo\DofusClient\MelonLoader\net6\*.dll" />
<Reference Include="C:\Jondo\DofusClient\MelonLoader\Il2CppAssemblies\*.dll" />
```

Declara `[assembly: MelonInfo(typeof(JondoFixMod), "JondoFix", "1.2.0", "Jondo")]` y `[assembly: MelonGame("Ankama", "Dofus")]`.

### 4.1. Autodetección del emulador

```csharp
private static bool IsEmulatorActive() {
    // intenta conectar a 127.0.0.1:8888 con 100 ms de timeout
}
```

Se ejecuta en `OnInitializeMelon`. Si el emulador **no** está escuchando en el 8888, `UseLocalRedirect = false` y **el mod se desactiva entero**: todos los parches comprueban esa bandera antes de actuar. Esto permite usar el mismo cliente contra el servidor oficial sin desinstalar nada.

> Consecuencia práctica: **arranca siempre el emulador antes que el cliente.** Si lo haces al revés, JondoFix no redirige y el cliente intentará ir a los servidores de Ankama.

### 4.2. Redirección de red

Todo lo que apunte a Ankama se reescribe a `127.0.0.1`. Los parches, con Harmony:

| Parche | Qué intercepta | Redirección |
|---|---|---|
| `UriPatch` | constructor de `System.Uri` | `https://haapi.ankama.com` y `.corp` → `http://127.0.0.1:8888` |
| `HttpClientSendAsyncPatch` | `HttpClient.SendAsync` | host que contenga `haapi.ankama` → `http://127.0.0.1:8888` + quita la cabecera `Host` |
| `SocketConnectIPPatch` | `Socket.Connect(IPAddress, int)` | puerto 5555 o 443 → `127.0.0.1:5555` |
| `SocketConnectEPPatch` | `Socket.Connect(EndPoint)` | endpoint con `ankama`, `34.247.205`, `54.75.207`, `:5555` o `:443` → `127.0.0.1:5555` |
| `SocketConnectAsyncEventArgsPatch` | `Socket.ConnectAsync(SocketAsyncEventArgs)` | idem |
| `TcpClientConnectStringPatch` | `TcpClient.Connect(string, int)` | idem |
| `TcpClientConnectEPPatch` | `TcpClient.Connect(IPEndPoint)` | idem |
| `TcpClientConnectAsyncStringPatch` | `TcpClient.ConnectAsync(string,int)` | idem |
| `TcpClientBeginConnectPatch` | `TcpClient.BeginConnect` | idem |
| `UnityWebRequestGetPatch` | `UnityWebRequest.Get(string)` | url con `dofus3.json` → `http://127.0.0.1:8888/config/dofus3.json` |

Se parchean **todas** las rutas porque el cliente usa varias capas de red distintas (la capa Spin, HttpClient manejado, sockets IL2CPP nativos y UnityWebRequest) y no siempre la misma.

### 4.3. TLS y certificados

**No hay ningún fichero de certificado en el proyecto.** No se instala ninguna CA en el sistema. En su lugar, JondoFix desactiva la validación desde dentro del cliente:

1. **Callbacks permisivos.** Construye dos delegados que devuelven siempre `true` y los convierte a tipos IL2CPP con `Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate`:
   - `BypassedCallback` → `Il2CppSystem.Net.Security.RemoteCertificateValidationCallback`
   - `BypassedMonoCallback` → `Il2CppMono.Security.Interface.MonoRemoteCertificateValidationCallback`
2. **Inyección en cada `SslStream`.** `BypassSslStreamInstance` fuerza `stream.validationCallback`, crea `MonoTlsSettings` si falta, y pone `UseServicePointManagerCallback = true` más el callback permisivo.
3. **Parches sobre `SslStream`**, aplicados dinámicamente porque hay varios constructores:
   - todos los constructores (postfix) → inyecta el bypass
   - `SetAndVerifyValidationCallback` (prefix) → sustituye el callback recibido
   - `AuthenticateAsClient`, `BeginAuthenticateAsClient`, `AuthenticateAsClientAsync` (prefix) → inyecta antes del handshake
4. **`ServicePointManager`**: se fuerza el getter de `ServerCertificateValidationCallback` para devolver siempre el permisivo, y además se asigna el equivalente manejado.
5. **`SpinProtocol.CheckAuthentication`** (prefix): devuelve `true` y `ConnectionErrors.NoneOrOtherOrUnknown`, saltándose la validación original por completo.

**Resultado:** HAAPI se sirve en **HTTP plano** (sin TLS) y el cliente lo acepta; el chat sí usa TLS pero con un certificado autofirmado generado al vuelo (§7), que el cliente acepta porque la validación está desactivada.

### 4.4. Parches anti-cuelgue de cartografía

El emulador no envía datos de misiones completos, y eso provoca `NullReferenceException` en el gestor de cartografía del cliente. Cuatro parches lo evitan:

| Método | Estrategia |
|---|---|
| `Il2Cpp.eud.bcnn(ku, bool)` | prefix: si la misión es `null` o su puntero nativo es cero, **salta el método original**. Finalizer: suprime cualquier excepción |
| `Il2Cpp.eud.bcku()` | prefix: diagnostica todos los campos, **limpia el diccionario de misiones activas** si detecta valores nulos, e inicializa las colecciones nulas. Finalizer: suprime excepciones |
| `Il2Cpp.eud.bckp(List<int>)` | prefix: elimina de la lista los ids de subárea que no existen en el DataCenter |
| `Il2Cpp.eud.bcoh(Dictionary<Vector2, epo>)` | prefix: **salta el método entero** siempre |

> `eud` es el `CartographyManager` ofuscado, y `ku`, `esm`, `gv`, `epo` son `Quest`, `CartographyArea`, `QuestObjective`, etc. **Estos nombres cambian con cada versión del cliente**; son la parte de JondoFix que no sobrevive a una actualización.

### 4.5. Volcado de metadatos de mapas

En `OnUpdate`, una sola vez, cuando `DataCenterModule` tiene los datos cargados en memoria, escribe tres CSV en `C:\Jondo\`:

| Fichero | Origen | Columnas |
|---|---|---|
| `map_dump_coordinates.csv` | `mapsCoordinatesDataRoot` | `compressedCoords, x, y, mapIds` |
| `map_dump_scrolls.csv` | `mapScrollActionsDataRoot` | `mapId, rightMapId, bottomMapId, leftMapId, topMapId` |
| `map_dump_infos.csv` | `mapsInformationDataRoot` | `mapId, posX, posY, subAreaId, outdoor, name` |

Si los tres ficheros ya existen, no vuelve a volcar. Para regenerarlos hay que borrarlos.

Estos nombres (`DataCenterModule`, `MapsCoordinateData`…) **no están ofuscados** y sobreviven entre versiones.

### 4.6. Utilidad de desarrollo: ids en los nombres de objeto

`TryGetLocalizationPatch` añade el `gid` entre corchetes al nombre de cada objeto: `"Espada de Boisaille [12345]"`. Los ids salen de `C:\Jondo\dofus3_data\items.json`, cargado al arrancar. Sirve para identificar objetos sin salir del juego.

---

## 5. Zaap — puerto 15881 (Thrift)

Zaap es el launcher de Ankama. El cliente habla con él por **Apache Thrift** para obtener el token de sesión y los ajustes. `ZaapServer` lo emula.

### 5.1. Interfaz implementada (`ZaapHandler`)

| Método | Devuelve |
|---|---|
| `connect(gameName, releaseName, instanceId, hash)` | el mismo `hash` recibido (lo valida como sesión) |
| `auth_getGameToken(gameSession, gameId)` | un GUID nuevo |
| `settings_get(gameSession, key)` | `autoConnectType`→`"0"`, `language`→`"es"`, `connectionPort`→`"5555"`; cualquier otra clave lanza `TApplicationException` |
| `userInfo_get(gameSession)` | un JSON de cuenta con `id 188940901`, nick `CADERNIS#2026`, suscripción hasta 2035 |
| `updater_isUpdateAvailable(gameSession)` | cadena vacía (no hay actualización) |

### 5.2. Tres transportes en el mismo puerto

El cliente puede conectarse de tres formas distintas y el servidor las **autodetecta leyendo los primeros 4 bytes**:

1. **Thrift binario directo (TCP).** Si los primeros bytes no son ASCII de HTTP, se trata como Thrift. Como esos 4 bytes ya se han consumido del socket, se usa la clase auxiliar **`PrefixedStream`**, que los reinyecta al principio del flujo para que el parser Thrift los vea. Es un detalle sutil pero imprescindible.
2. **Named Pipe.** En paralelo se abre un `NamedPipeServerStream` cuyo **nombre es el número de puerto** (`"15881"`). El cliente oficial usa esta vía en Windows (`TNamedPipeClientTransport`).
3. **WebSocket.** Si los primeros bytes son `GET ` o `POST`:
   - Petición a `/v2/feedbacks` sin cabecera de upgrade → responde `200 OK` con `{}` y cierra (es telemetría de MelonLoader).
   - Con `Upgrade: websocket` → hace el handshake calculando `Sec-WebSocket-Accept` con SHA-1 sobre la clave + el GUID mágico `258EAFA5-E914-47DA-95CA-C5AB0DC85B11`, y luego procesa frames: opcode 1 (texto) responde `{}`, **opcode 2 (binario) se procesa como Thrift** y devuelve la respuesta en otro frame binario, opcode 8 cierra, opcode 9 responde pong.

---

## 6. HAAPI — puerto 8888 (HTTP)

HAAPI es la API REST de cuentas de Ankama. `HaapiServer` la emula con un `HttpListener` en **HTTP plano**, escuchando en `localhost` y `127.0.0.1`. Añade cabeceras CORS permisivas.

### Endpoints

| Método | Ruta | Respuesta |
|---|---|---|
| `GET` | `/config/dofus3.json` | la configuración del juego (abajo) |
| `POST` | `/json/Ankama/v5/Api/Connect` | token fijo `eb95866f-…` con caducidad 2035 |
| `POST` | `/json/Ankama/v5/Account/ApiKey` | idem |
| `POST` | `/json/Ankama/v5/Account/CreateApiKey` | idem |
| `GET` | `/json/Ankama/v5/Account/GetAccount…` | ficha de cuenta (id `188940901`, `jondo@emulator.com`, lang `es`) |
| `GET` | `/json/Ankama/v5/Game/ServerList` | un servidor: id 401, "Tal Kasha", estado 3, 1 personaje |
| `POST` | `/json/Ankama/v5/Api/GameToken` | GUID nuevo **que se guarda en la BD** (`SetGameToken`) + `{host:127.0.0.1, port:5555}` |
| `POST` | `/json/Ankama/v5/Game/SelectServer` | GUID + `{host:127.0.0.1, port:5555}` |
| *(cualquier otra)* | — | `{}` con 200, para que ninguna promesa del cliente quede rechazada |

### `dofus3.json` — el fichero que redirige todo

Es la pieza central: el cliente lee de aquí a dónde conectarse.

```json
{
  "gameAppId": 1,
  "connectionHosts": ["JMBouftou:127.0.0.1:5555"],
  "chatServerHost": "127.0.0.1",
  "chatServerPort": 6337,
  "haapiAnkamaUrl": "http://127.0.0.1:8888/json/Ankama/v5/",
  "haapiDofusUrl":  "http://127.0.0.1:8888/json/Dofus/v3/",
  "local":  { "build_override": "3.6.4", "client_override": "es" },
  "login":  { "ports": [5555], "hosts": ["127.0.0.1"] }
}
```

> `build_override` debe coincidir con la versión del cliente. Con el cliente 3.6.8.8 hay que cambiarlo a `"3.6.8"`.

---

## 7. Chat — puerto 6337 (TLS)

`ChatServer` **genera un certificado X.509 autofirmado en memoria al arrancar** (`GenerateSelfSignedCertificate`) y vuelca al log su sujeto, emisor, número de serie, huella y validez. No se persiste en disco ni se instala en el almacén de Windows: el cliente lo acepta porque JondoFix ha desactivado la validación (§4.3).

---

## 8. Servidor de conexión y Game Node — puerto 5555

Aquí está el detalle más peculiar de la arquitectura: **dos protocolos distintos comparten puerto** y se distinguen por el contenido del primer frame.

`GameServerProxy.HandleGameClient` (`GameServerProxy.cs:57`):

```csharp
byte[] firstPayload = await NetworkMessage.ReadFrameAsync(clientStream);
if (Encoding.UTF8.GetString(firstPayload).Contains("type.ankama.com/"))
     → GameNodeProxy.HandleGameNodeSessionAsync(...)   // protocolo de juego
else → HandleConnectionServerSessionAsync(...)         // protocolo de conexión
```

### 8.1. El framing (común a los dos)

`Jondo.Protocol.NetworkMessage`:

```
[ longitud como varint ][ payload protobuf de esa longitud ]
```

Y el payload de juego va envuelto en tres niveles:

```
raíz { campo 3 : envoltorio { campo 1 : Any { campo 1 : "type.ankama.com/xxx",
                                              campo 2 : <payload del mensaje> } } }
```

Lo construye `NetworkEnvelope.BuildGameNodePacket(typeUrl, payload)`. Si el payload está vacío, **omite el campo 2** (así lo hace el servidor real con los mensajes vacíos). Además avisa por consola si el opcode no mide exactamente 3 caracteres, porque en Dofus 3 **siempre** son 3 letras.

### 8.2. Protocolo de conexión

Usa mensajes protobuf con nombres semánticos, definidos en `Jondo.Unity.Launcher\Protocol\GameProtocol.proto` (20 mensajes: `AuthenticationTicketMessage`, `ServerList`, `SelectedServerData`…).

1. **Cliente → `AuthenticationTicket`** con su token.
   **Servidor →** `GetModifiedAuthAcceptedMessage()`: parte de una trama hex capturada del servidor oficial, la parsea y **sustituye los datos por los de la base de datos** — nombre de cuenta `CADERNIS`, tag `2026`, suscripción hasta 2035, y el primer personaje con su nombre, nivel, raza y sexo reales de `Characters`.
2. **Cliente → `SelectedServer`** con el id del servidor.
   **Servidor →** `SelectedServerData` con `ServerHostInfo { Ticket = GUID, Address = "127.0.0.1", Ports = B3 2B B3 2B }`. Esos bytes son el varint de 5555 dos veces.
   Acto seguido **cierra la sesión** (`return`), a propósito: si se deja abierta, el cliente se queda esperando y da timeout.
3. El cliente reconecta al 5555, esta vez hablando el protocolo de Game Node.

### 8.3. Game Node — el bucle principal

`GameNodeProxy.HandleGameNodeSessionAsync` enruta por el `type_url` contenido en el frame:

| Opcodes del cliente | Handler |
|---|---|
| `hmt`, `ise`, `jtk`, `knx` | `CharacterSelectionHandler.HandleAuthRequest` (solo la primera vez) |
| `jto`, `kpc`, `ksx`, `kpa` | `CharacterSelectionHandler.HandleCharacterListRequest` |
| **`ksl`** | selección de personaje + **ráfaga de entrada al mundo** |
| `kkr`, `jqf`, `igx` | `MapLoadHandler` (o `FightHandler` si hay combate) |
| `joi` | `MapChangeHandler.HandleMovementRequest` |
| `jos` | `MapChangeHandler.HandleMapChangeRequest` |
| `jpp` | confirmación de movimiento (dispara combate por colisión) |
| `isi` | `InventoryHandler` |
| `krc` | `StatsHandler` |
| `kqn` | `ChatHandler` |
| `jyz`, `jza`, `jwb`, `hoy`, `jxx`, `jyk` | `FightHandler` |

### 8.4. La entrada al mundo (`ksl`)

Es el punto más delicado. Al recibir `ksl`:

1. `HandleCharacterSelectionRequest(payload)` carga el personaje en `GameState` desde la BD.
2. Se **reproduce una ráfaga capturada** del servidor oficial: `BasePayloads.WorldEnteringPackets`, un `byte[]` literal. Se recorre frame a frame (varint de longitud + payload) y se envían **los primeros 17**.
3. Durante el volcado, dos frames se sustituyen o parchean al vuelo:
   - **`irm`** (inventario) → se descarta el capturado y se genera entero desde la BD con `BuildDynamicIrmPayload()`.
   - **`joh`** (mapa actual) → se parchea el `mapId` con `GameState.MapId`.
4. Después se envían los "transition packets" desde `TransitionPayloads.cs` / `TransitionPacketsBuilder.cs` (unos 50 opcodes: `lok`, `jdj`, `kkp`, `kkm`, `krb`, `ilc`, `joh`, `lor`, `hmd`, `itp`, `lpe`, `hnk`, `kqm`, `icg`, `ith`, `klt`, `klp`…).

> ### ⚠️ Deuda técnica importante
> Todo lo que no sean `kri`, `irm` e `isf` sigue siendo **un volcado literal de la sesión original**, con el mapa, las misiones y las fechas de aquella partida incrustados. Por eso el cliente rotula "Incarnam (Camino de las Almas)" aunque el personaje esté guardado en Astrub (`MapId = 191104002`). Detallado en la §2.bis de [PLAN_MIGRACION_3.6.8.8.md](PLAN_MIGRACION_3.6.8.8.md).

---

## 9. Secuencia completa, paso a paso

```
 1. Arranca el emulador  →  BD, mapas, mobs, 5 servidores en escucha
 2. Program.cs lanza Dofus.exe con --port 15881 y las variables ZAAP_*
 3. version.dll carga MelonLoader  →  MelonLoader carga Mods\JondoFix.dll
 4. JondoFix sondea 127.0.0.1:8888  →  responde  →  UseLocalRedirect = true
 5. JondoFix aplica los parches de red, TLS y cartografía
 6. El cliente pide la config    →  UnityWebRequest interceptado
                                 →  GET http://127.0.0.1:8888/config/dofus3.json
                                 →  aprende: conexión 127.0.0.1:5555, chat :6337
 7. El cliente habla con Zaap    →  :15881 por Thrift (TCP, pipe o WebSocket)
                                 →  connect() y userInfo_get() dan sesión y cuenta
 8. El cliente llama a HAAPI     →  Api/Connect, GetAccount, ServerList, GameToken
                                 →  un servidor: "Tal Kasha"
 9. El cliente conecta al :5555  →  primer frame SIN "type.ankama.com/"
                                 →  protocolo de conexión
10. AuthenticationTicket         →  AuthAccepted (con los datos reales de la BD)
11. SelectedServer               →  ServerHostInfo 127.0.0.1:5555  →  se cierra la sesión
12. El cliente reconecta al :5555 →  primer frame CON "type.ankama.com/"
                                 →  Game Node
13. hmt/ise/jtk/knx              →  HandleAuthRequest
14. ksx/kpa                      →  lista de personajes desde la BD
15. ksl (selección)              →  GameState desde la BD
                                 →  ráfaga de entrada (17 frames, irm y joh parcheados)
                                 →  transition packets (~50 opcodes)
16. kkr del cliente              →  MapLoadHandler responde con actores e interactivos
17. El personaje aparece en el mapa
```

---

## 10. Cómo obtener los `.proto` originales del cliente

Todo el protocolo está dentro del binario. Esta es la receta verificada para extraerlo de **cualquier** versión.

### Paso 1 — Generar los ensamblados con MelonLoader

1. En la carpeta del cliente, renombrar `version.dll.disabled` → `version.dll`.
2. Si no existe la carpeta `MelonLoader\`, **copiarla de un cliente donde ya funcione**. El proxy solo no basta: sin `MelonLoader\net6\MelonLoader.runtimeconfig.json` aborta con *"Runtime config not found"*.
3. Vaciar `GameAssemblyHash` en `MelonLoader\Dependencies\Il2CppAssemblyGenerator\Config.cfg` para forzar la regeneración.
4. Arrancar el juego una vez y esperar a `Assembly Generation Successful!`. No hace falta conectarse a ningún servidor.

### Paso 2 — Usar la salida correcta

Hay **dos** salidas y solo una sirve:

| Ruta | Contiene los números de campo |
|---|---|
| `MelonLoader\Dependencies\Il2CppAssemblyGenerator\Cpp2IL\cpp2il_out\Ankama.Dofus.Protocol.Game.dll` | **SÍ** |
| `MelonLoader\Il2CppAssemblies\Il2CppAnkama.Dofus.Protocol.Game.dll` | **NO** (Il2CppInterop convierte las constantes en propiedades y pierde el valor) |

Usa siempre la de **`cpp2il_out`**. Hay también un `Ankama.Dofus.Protocol.Connection.dll` con los mensajes de conexión.

### Paso 3 — Leer la estructura

Herramienta ya montada: `C:\Jondo\scripts\protomatch\dllq\` (lector de metadatos .NET con `System.Reflection.Metadata`).

```bash
dotnet run --project C:/Jondo/scripts/protomatch/dllq -- "<ruta>/cpp2il_out/Ankama.Dofus.Protocol.Game.dll" salida.json
```

Los tipos del protocolo están en el namespace vacío (o `Il2Cpp` en los de interop), con nombres ofuscados de 3 letras para los mensajes y 4 para los campos.

### Paso 4 — Interpretar los campos

Cada campo protobuf aparece como **un par de campos consecutivos**: una constante `Int32` cuyo *valor* es el número de campo, y a continuación el campo de almacenamiento con el tipo real.

```
jxx:  erzr : MessageParser`1<jxx>    <- ignorar
      erzs : UnknownFieldSet         <- ignorar
      erzt : Int32 = 1               <- número de campo 1
      erzu : Boolean                 <- tipo del campo 1
      erzv : Int32 = 2               <- número de campo 2
      erzw : lnk                     <- tipo del campo 2

  →  message jxx { bool erzt = 1; lnk erzv = 2; }
```

Reglas para el resto de formas:

| Patrón en el DLL | Significado |
|---|---|
| `FieldCodec\`1<T>` + `RepeatedField\`1<T>` | `repeated T` |
| `Codec<K,V>` + `MapField\`2<K,V>` | `map<K,V>` |
| un `Int32` sin valor **antes** del primer const | campo `case` de un `oneof` |
| tipo con un campo `value__` | es un `enum`; sus constantes son los valores |

**Validación:** el mensaje `jox` debe salir con los campos `1, 3, 2, 4` (numeración no secuencial). Si tu extractor lo reproduce, es fiable.

> El `dofus3_sniffer_complete.proto` que hay en el repositorio se generó con una versión anterior de esta receta y **es incorrecto**: colapsa los `repeated` y los `map` a `bytes`. Por eso `igs` parecía vacío cuando en realidad son dos `map`, y `jya.f4` parecía `bool` cuando es `repeated int32`. Regenéralo.

---

## 11. Montaje desde cero — lista de comprobación

1. **Cliente**: copiar `DofusClient\` completo (incluye `GameAssembly.dll`, `Dofus_Data\`, `version.dll`).
2. **MelonLoader**: `version.dll` presente y carpeta `MelonLoader\` completa. Arrancar una vez para generar `Il2CppAssemblies\`.
3. **JondoFix**: compilar `JondoFix.csproj` (referencia rutas absolutas a `MelonLoader\net6` e `Il2CppAssemblies`) y copiar el DLL a `DofusClient\Mods\`.
4. **Bases de datos**: `C:\Jondo\world.db` y `auth.db` (rutas fijas en `DatabaseManager.cs`).
5. **Datos auxiliares**: `map_walkable_cells.json`, `dofus3_data\items.json`.
6. **Compilar y arrancar** `Jondo.Unity.Launcher`. Verificar en consola los cinco `[+]` de servidores en escucha.
7. **El cliente lo lanza el emulador solo.** No uses `launch_jondo.bat` sin corregirle el puerto.

### Verificación por puertas

| Puerta | Señal de éxito |
|---|---|
| MelonLoader carga | consola negra con `MelonLoader v0.7.3` |
| JondoFix activo | `JONDO REDIRECTOR & FIX` y `[+] DNS and Socket redirection is ACTIVE` |
| HAAPI responde | en el emulador, `[HAAPI] GET /config/dofus3.json` |
| Zaap responde | `[Thrift] connect(...)` y `[Thrift] userInfo_get(...)` |
| Conexión | `[Game Server] Received Auth` y `Sent Auth Accepted and ServersList!` |
| Selección de servidor | `[Game Server] Sent SelectedServerData` |
| Game Node | `[+] Detected Game Node protocol on port 5555!` |
| Entrada al mundo | `[Game Node] Streaming database-synchronized world entering packets...` |

---

## 12. Diagnóstico

| Fichero | Contenido |
|---|---|
| `C:\Jondo\emulator_debug.log` | todo lo que pase por `Program.LogDebug` |
| `C:\Jondo\gameserver_traffic.log` | tráfico con hex y texto (`LogTraffic`) |
| `DofusClient\MelonLoader\Latest.log` | log del cliente y de JondoFix |

La consola del emulador imprime cada paquete **enriquecido**: dirección, tamaño, contexto ("Lista de Servidores", "Carga del Mundo", "En el Juego"), categoría (Personaje, Mapa, Inventario…), opcode, descripción legible, hex y árbol protobuf. La tabla de descripciones está en `NetworkMessage.GetPacketMetadata` (`NetworkMessage.cs:178`) y cubre unos 90 opcodes. Los latidos (`kod`, `kns`, `kpc`, `jgv`) se imprimen sin árbol para no ensuciar.

> **Aviso:** el volcado del árbol protobuf de cada paquete es costoso. Si aparecen timeouts, es lo primero que hay que limitar.

---

## 13. Deudas técnicas conocidas de esta parte

1. **`launch_jondo.bat` usa el puerto 8080** y `Program.cs` el 15881.
2. **`BasePayloads.WorldEnteringPackets` y `TransitionPayloads.cs`** son volcados literales de la sesión original: arrastran mapa, misiones y fechas ajenas.
3. **`GetModifiedAuthAcceptedMessage` parte de una trama hex** y la parchea; debería construirse desde cero.
4. **Identidad de cuenta fija** (`188940901`, `CADERNIS`, `jondo@emulator.com`) repartida entre `HaapiServer`, `ZaapHandler` y `GameServerProxy`. Debería salir de `auth.db`.
5. **`build_override` fijo a `"3.6.4"`** en `HaapiServer.cs:149`.
6. **El router de Game Node usa `payloadStr.Contains(...)`** sobre los bytes crudos: es frágil (un payload puede contener la marca de otro opcode) y complica cualquier traducción de opcodes.
7. **`GameState` tiene valores por defecto** que enmascaran fallos de carga desde la BD. Detallado en la §D8 de [PLAN_COMBATE_V3.md](PLAN_COMBATE_V3.md).

---

*Siguiente parte prevista: mapas, movimiento y cambio de mapa (`joi`, `joo`, `jos`, `kkr`, `jpv`).*
