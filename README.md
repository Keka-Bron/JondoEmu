# Jondo Unity Emulator — Dofus 3.6 Server

High-performance server emulator for **Dofus 3 Unity (Client 3.6.4.3)** written in C# (**NET 10**), architected with decoupled modular projects, a SQLite database layer, and a functional PvM combat engine.

---

## 🚀 Emulation Status

### ✅ Completed / Working Features
- [x] **Client-Server-Authentication Emulation** (Zaap, HAAPI, Connection Server with infinite VIP subscription bypass)
- [x] **Server Selection & Character Selection**
- [x] **World Loading (World / Game Node)**
- [x] **Character Spawn & Name Hover**
- [x] **Movement, Map Change, Map Loading, Adjacent Maps**
- [x] **Last Cell and Map Persistence** in Database
- [x] **Inventory System:** Item spawning, equipping/unequipping, item bags, and persistent storage.
- [x] **Character Stats:** Characteristic assignment is fully functional (all stats map correctly, capital calculation is dynamic, and remaining points synchronize perfectly between all client panels, including the left sidebar HUD).
- [x] **NPCs:** Spawns, 3D looks, and dialogue trees.
- [x] **Monsters & Mobs System (100% Complete):**
  - **Dynamic Map Spawning & Respawner**: Automatic population and maintenance of 2 to 4 mob groups per map.
  - **Level & Grade Management**: Correct levels, grades, and experience calculation.
  - **3D Looks & Skeleton System**: Native Protobuf bone models, custom scales, and textures for monsters, quest monsters, and archmonsters.
  - **Multi-Monster Groups**: Complete support for groups of 1 to 8 monsters per mob.
  - **Spatial Radius 2 Cell Validation (`GetInnerWalkableCells`)**: Strict grid validation preventing mob spawns on non-walkable decorations, walls, house windows, or Zaap pillars.
  - **Quest Monsters & Archmonsters**: Full database mapping for special quest mobs and archmonsters.
- [x] **PvM Combat System (Functional Core Engine):**
  - **Tactical Arenas**: Each roleplay map resolves to its corresponding tactical combat arena based on zone offsets.
  - **Context Transitions**: Clean switching between Roleplay and Tactical Combat states, restoring world state upon fight completion.
  - **Placement Phase**: Red (monsters) and blue (players) placement tiles with dynamic cell swapping before clicking *Ready*.
  - **Isometric Grid Geometry (`MapGeometry`)**: 4-neighbor isometric grid geometry calculated using a pre-computed $O(1)$ BFS distance matrix (Even deltas: `-28, -15, -14, -1, +1, +13, +14, +28`; Odd deltas: `-28, -14, -13, -1, +1, +14, +15, +28`).
  - **Line of Sight (LoS)**: LoS obstacle validation extracted for 17,222 maps, tracing segments between cell centers.
  - **Turn Protocol & Timers**: Turn handshake, 30-second turn timers with automatic pass, and AP/MP replenishment.
  - **Movement**: Cell-by-cell path expansion and Movement Point (MP) deductions.
  - **Spells & Elemental Damage**: Level-based spell querying (AP cost, range, LoS, per-turn/per-target limits), elemental damage calculations (stats, equipment power, target resistances), and critical hit rolls.
  - **Active Monster AI**: Intelligent target selection (lowest HP, HP %, isolation, distance), ranged attacks, minimal-MP BFS pathing, and flee behavior when HP falls below 30%.
  - **Fight Resolution & Progression**: Victory/defeat screens, experience progression (1,889 levels), official loot drops, level ups, and monster group respawns.

### 🚧 Work In Progress (WIP) / Basic Implementation
- [ ] **Commands:** `.level` and `.kamas` are present but working roughly (working on it).
- [ ] **Partial Combat Mechanics:**
  - Pushback trajectory (calculates destination cell, omits collision damage & pushback animation).
  - Weapon strikes (applies damage & AP cost, omits slash animation).
  - Stat reductions (reduces stats, omits fighter debuff UI widget).
  - Inventory Kamas (persists & displays on victory screen, requires UI tab refresh).

### ❌ Missing Features
- [ ] Zaaps
- [ ] Kolossium System & PvP Combat
- [ ] Advanced Combat Features (AP/MP dodge rolls, area-of-effect spell shapes, summons, shields, lock & dodge in melee)
- [ ] Professions (Jobs)
- [ ] Achievements
- [ ] Titles & Ornaments
- [ ] Guilds

---

## 📂 Repository Structure

* **[Jondo.Unity.sln](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.sln)**: Visual Studio solution grouping all emulator subprojects:
  * **[Jondo.Unity.Launcher](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Launcher)**: Server entry point, proxies, network parser, handlers, and local database management.
  * **[Jondo.Unity.Core](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Core)**: Core networking infrastructure and TCP servers.
  * **[Jondo.Unity.Auth](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Auth)**: Authentication and HAAPI service handlers.
  * **[Jondo.Unity.Protocol](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Protocol)**: Protocol buffers, constants (`ProtocolConstants`), and message definitions (Protobuf).
  * **[Jondo.Unity.World](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.World)**: Game Node / World server logic, combat engine (`FightInstance`), isometric grid geometry (`MapGeometry`), and Monster AI.
* **[JondoFix](file:///C:/Jondo/Jondo%20Unity%20Emulator/JondoFix)**: MelonLoader client mod source code that redirects Dofus client traffic to the local server and bypasses official SSL certificate checks.
* **[EspecificacionTecnica.md](file:///C:/Jondo/Jondo%20Unity%20Emulator/EspecificacionTecnica.md)**: Detailed specification of the protocol, network architecture, ports, and iteration logs.

---

## 💾 Database & Persistence

The emulator uses two **SQLite** databases for local persistence:
* **`auth.db`**: Stores accounts, credentials, and authentication sessions.
* **`world.db`**: Distributed compressed as `world.zip` (unzip after cloning). Stores character data, inventories, positions, map persistence, spells, monsters, and world states.

---

## 🚀 Quick Start Guide

### Step 1: Extract Database & Build Server
1. Clone the repository and navigate into `Jondo Unity Emulator`.
2. Extract the `world.zip` archive into the root folder to generate `world.db`:
   ```powershell
   powershell Expand-Archive -Path world.zip -DestinationPath . -Force
   ```
3. Build the solution using Visual Studio 2022 / .NET 10 SDK or via command line:
   ```bash
   dotnet build Jondo.Unity.sln -c Release
   ```
4. Run **`Jondo.Unity.Launcher`**:
   ```bash
   dotnet run --project Jondo.Unity.Launcher/Jondo.Unity.Launcher.csproj
   ```

---

### Step 2: Configure the Dofus Client (MelonLoader & JondoFix)

By default, the official Dofus client connects to official Ankama servers and verifies SSL/TLS security certificates. To redirect traffic locally, use **MelonLoader** and the **JondoFix** mod.

#### 1. Install MelonLoader
1. Download the **MelonLoader** installer (version `0.6.x` or compatible with .NET 6) from its official repository: [MelonLoader Releases](https://github.com/LavaGang/MelonLoader/releases).
2. Run the installer and select the executable file of the Dofus client (`Dofus.exe`).
3. Set runtime auto-detection (**IL2CPP** / **.NET 6**) and click **Install**.

#### 2. Load the JondoFix Mod
1. Build **`JondoFix/JondoFix.csproj`**:
   ```bash
   dotnet build JondoFix/JondoFix.csproj -c Release
   ```
2. Copy `JondoFix/bin/Release/net6.0/JondoFix.dll` into the **`Mods/`** folder inside your Dofus client installation directory.

#### What does JondoFix do?
* **Network Redirection**: Intercepts sockets, Named Pipes, and DNS queries, redirecting traffic to `localhost` (ports `8888`, `5555`, `15881`, `6337`).
* **SSL Bypass**: Prevents HTTPS requests from failing due to self-signed local certificates.
* **Environment Configuration**: Injects required environment variables (`ZAAP_PORT`, `ZAAP_HASH`, etc.).

---

<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/1af6569e-0fef-4c8d-8ede-512aec40aabb" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/f7bec8df-4aa2-4718-ad73-6229a3207d78" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/46ae56c8-d94f-47a0-830d-de68834d94e7" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/0c64cfd4-4f00-4147-85d4-56d341e2f4fc" />
<img width="2558" height="1500" alt="image" src="https://github.com/user-attachments/assets/3fc9ca8c-11b2-4c97-b459-bf4b63849b4a" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/fec53113-e5b5-4bca-84de-487322db3201" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/c1bd7344-c7d2-44cb-86c1-844b85e41659" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/2a13f071-9d3f-4010-a4a5-cdef517b36bb" />
<img width="2560" height="1600" alt="image" src="https://github.com/user-attachments/assets/f66ea136-3185-4222-a413-431a37964346" />
