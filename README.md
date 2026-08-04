# Jondo Unity Emulator — Servidor Dofus 3.6

Emulador de alto rendimiento para **Dofus 3 Unity (cliente 3.6.4.3)** desarrollado en C# (**NET 10**), desacoplado en arquitecturas modulares, con motor de mapa, base de datos SQLite y sistema completo de combate PvM.

---

## 🚀 Estado General del Emulador

### ✅ Funcionalidades Completadas y Operativas
- [x] **Autenticación y Conexión**: Servidor de Zaap, HAAPI y Game Server con bypass de suscripción VIP infinita.
- [x] **Selección de Servidor y Personaje**: Carga fluida de personajes, apariencia 3D y persisistencia de datos.
- [x] **Navegación y Mapas**: Movimiento roleplay, cambio de mapa, mapas adyacentes y persistencia de celda/mapa en base de datos.
- [x] **Sistema de Inventario**: Spawning de objetos, equipar/desequipar y bolsas de objetos.
- [x] **Estadísticas de Personaje**: Asignación de características funcional con capital dinámico y sincronización HUD.
- [x] **NPCs**: Carga de spawns, aparencias 3D y diálogos base.
- [x] **Sistema de Monstruos (100% Completo)**:
  - **Spawning dinámico**: Poblado de 2 a 4 grupos de monstruos por mapa.
  - **Niveles y Grados**: Niveles, grados y cálculo de experiencia oficial.
  - **Modelos 3D y Esqueletos (`lkr`)**: Modelos Protobuf, escalas y texturas para monstruos, monstruos de misión y archimonstruos.
  - **Grupos de 1 a 8 monstruos**.
  - **Validación espacial de radio 2 (`GetInnerWalkableCells`)**: Evita spawns en decoraciones u obstáculos.
- [x] **Sistema de Combate PvM Básicamente Completo (Ver sección detallada abajo)**.

### 🚧 En Desarrollo / Implementación Básica
- [ ] **Comandos GM**: `.level` y `.kamas` presentes y en proceso de pulido.
- [ ] **Sistema PvP y Combates Multijugador**.

### ❌ No Implementado
- [ ] Zaaps
- [ ] Koliseo
- [ ] Oficios (Jobs)
- [ ] Logros (Achievements)
- [ ] Títulos y Ornamentos
- [ ] Gremios (Guilds)

---

## 🏛 Arquitectura de la Solución (`Jondo.Unity.sln`)

- **`Jondo.Unity.Launcher`**: Punto de entrada (`Program.cs`), proxies de sockets TCP (`GameServerProxy`, `GameNodeProxy`), `DatabaseManager`, `StatsHandler`, `ChatHandler` y controladores de paquetes de red.
- **`Jondo.Unity.World`**: Estado de juego en memoria, gestor de mapas (`MapManager`), motor de combate (`FightInstance`, `FightHandler`), inventario y motor de Inteligencia Artificial de monstruos (`MonsterAI`).
- **`Jondo.Unity.Protocol`**: Clases C# tipo-seguras generadas con Protobuf 3 (`Jondo.Unity.Protocol.Messages`), serializadores de tramas y tipos réflex.
- **`Jondo.Unity.Core`**: Interfaces base, tipos primitivos, enumerados, constantes de protocolo (`ProtocolConstants`) y cálculos matemáticos.
- **`JondoFix`**: Parche de inyección en C# (.NET 6) para adaptar las llamadas de red del cliente nativo de Unity.

---

## ⚔️ Estado del Sistema de Combate PvM

El emulador cuenta con una primera pasada completa y funcional del sistema de combate Jugador contra Monstruos (PvM), construida y validada empíricamente con capturas de tráfico del juego oficial y metadatos del cliente.

### ✅ Funciona (100% Implementado)

#### Inicio y Escenario
- **Arena de combate propia**: Cada mapa de roleplay resuelve su mapa de arena táctico correspondiente en la subárea (+4, +6, +2... según la zona).
- **Transición de contexto**: Secuencia limpia `kkp` (destrucción) ➔ `kkm` (creación de contexto de combate) y restauración completa al finalizar (`lxs · kkp · kkm · krb · joh · lor`).
- **Fase de colocación**: Posiciones posibles rojas (monstruos) y azules (jugadores), con cambio dinámico de casilla antes de pulsar *Listo*.

#### Geometría del Tablero Isométrico
- **Rejilla isométrica de 4 vecinas (`MapGeometry`)**: Distancias calculadas por matriz BFS $O(1)$ precalculada (Deltas par: `-28, -15, -14, -1, +1, +13, +14, +28`; Deltas impar: `-28, -14, -13, -1, +1, +14, +15, +28`).
- **Línea de visión (LDV)**: Extraída a partir de los datos `los` del cliente para 17.222 mapas. Traza segmentos entre centros y valida casillas opacas.

#### Turnos y Movimiento
- **Protocolo de turno**: Handshake completo `juu` (espera) ➔ `jwe` (confirmación) ➔ `jut` (timer 30s) ➔ `jwl` (jugable).
- **Temporizador activo de 30s**: Paso de turno forzado desde el servidor al expirar.
- **Movimiento celda a celda**: Descompresión de `joi` (`v % 4096`), expansión del camino en `joo` y deducción acumulada de PM en `jvm`.

#### Hechizos y Monstruos
- **Hechizos dinámicos por nivel**: Lectura de `SpellLevels` en `world.db` para coste de PA, rangos mínimo/máximo, LDV y límites de lanzamiento por turno/objetivo.
- **Cálculo de daño elemental**: Daño calculado con la característica correspondiente, potencia de equipo y resistencias del objetivo.
- **Golpes críticos**: Probabilidad suma del hechizo y equipo; utiliza la franja crítica del hechizo.
- **IA de Monstruos activa**: Selección de objetivo (menor vida, vida %, aislamiento y distancia), ataque a rango, movimiento BFS con consumo mínimo de PM y modo huida si se encuentra a < 30% de vida.

#### Fin de Combate y Progreso
- **Pantalla de victoria/derrota**: Experiencia otorgada (`gradeXp`), subida de nivel (tabla real de 1.889 niveles), otorgamiento de puntos de característica y refresco de hechizos.
- **Botín de monstruos**: Tirada de ítems por probabilidades de grado agregados al inventario.
- **Reaparición**: Reposición del grupo de monstruos derrotado por uno generado al azar.

---

### 🟡 A Medias (Parcialmente Implementado)

- **Empuje**: Se calcula y aplica la casilla de destino en la recta del impacto, pero la animación utiliza el paquete de caminar. Falta el cálculo del daño de colisión por empuje.
- **Golpe con arma**: El daño y coste se aplican correctamente, pero se omite la animación del espadazo.
- **Lista de embrujos/estados**: Las retiradas de PA/PM se notan en los atributos, pero falta el widget visual de estados en el luchador.
- **Estadísticas finales**: Los contadores de daño infligido/recibido en la pantalla de victoria salen a 0.
- **Kamas en inventario**: Se persisten y muestran en la pantalla final, pero el marcador del inventario requiere volver a abrir la interfaz para refrescarse.

---

### 🔴 No Implementado

- Tirada de esquiva al retirar PA/PM.
- Formas de zonas de efecto (`zoneDescr` en área).
- Invocaciones, curas, escudos y estados avanzados.
- Placaje y huida al escapar de melé.
- Resistencias del equipo del jugador.
- Prospección y botín condicional por misiones/logros.
- Combates multijugador y PvP.

---

## 🚀 Guía de Instalación y Ejecución

### 1. Requisitos Previos
- **.NET 10 SDK** (o posterior).
- **Dofus 3.6.4.3 Client**.

### 2. Descomprimir la Base de Datos
La base de datos `world.db` se distribuye comprimida en `world.zip` para respetar los límites de tamaño de GitHub:

```powershell
cd "Jondo Unity Emulator"
powershell Expand-Archive -Path world.zip -DestinationPath . -Force
```

### 3. Compilación de la Solución
```powershell
dotnet build Jondo.Unity.sln -c Release
```

### 4. Compilar y Aplicar el Parche `JondoFix`
Compilar el proyecto `JondoFix/JondoFix.csproj` e inyectar la DLL resultante en el ejecutable/runtime del cliente Unity.

### 5. Iniciar el Emulador
```powershell
dotnet run --project Jondo.Unity.Launcher/Jondo.Unity.Launcher.csproj
```

---

## 📚 Documentación Adicional

- [Guía de Arranque y Conexión](docs/DOC_01_ARRANQUE_Y_CONEXION.md)
- [Mapas y Movimiento](docs/DOC_02_MAPAS_Y_MOVIMIENTO.md)
- [Especificación Técnica de Iteraciones](EspecificacionTecnica.md)
- [Planes de Combate V2 & V3](docs/PLAN_COMBATE_V3.md)
- [Herramientas de Extracción Python](tools/README.md)
