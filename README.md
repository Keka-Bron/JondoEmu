High-performance server emulator for **Dofus 3 Unity (Client 3.6.10.10)** written in C# (**NET 10**), architected with decoupled modular projects, a SQLite database layer, and a functional PvM combat engine.

> ⚠️ **Compatibility Notice**: This emulator strictly requires **Dofus 3 Client Version 3.6.10.10 (mid August 2026)**. It is **NOT compatible** with newer or latest versions of the official Dofus client due to underlying protocol changes.
---

## 🚀 Emulation Status

### ✅ Completed / Working Features
- [x] **Custom Multilanguage Launcher**
- [x] **Client-Server-Authentication Emulation** (Zaap, HAAPI, Connection Server with infinite VIP subscription bypass)
- [x] **Server Selection & Character Selection**
- [x] **World Loading (World / Game Node)**
- [x] **Character Spawn & Name Hover**
- [x] **Movement, Map Change, Map Loading, Adjacent Maps**
- [x] **Last Cell and Map Persistence** in Database
- [x] **Inventory System:** Item spawning, equipping/unequipping, item bags, and persistent storage.
- [x] **Cosmetics/Appereances:** Every cosmetic works with persistent storage.
- [x] **Titles & Ornaments** Fully functional.
- [x] **Haven Bags:** Every haven bag works, including chests and lottery machines.
- [x] **Zaaps/Zaapis** Every zaap/zaapi works.
- [x] **Character Stats:** Characteristic assignment is fully functional (all stats map correctly, capital calculation is dynamic, and remaining points synchronize perfectly between all client panels, including the left sidebar HUD).
- [x] Spells and spells variants
- [x] **NPCs:** Spawns, 3D looks, and dialogue trees.
- [x] **Monsters & Mobs System (100% Complete):**
  - **Dynamic Map Spawning & Respawner**: Automatic population and maintenance of 2 to 4 mob groups per map.
  - **Level & Grade Management**: Correct levels, grades, and experience calculation.
  - **3D Looks & Skeleton System**: Native Protobuf bone models, custom scales, and textures for monsters, quest monsters, and archmonsters.
  - **Multi-Monster Groups**: Complete support for groups of 1 to 8 monsters per mob.
  - **Spatial Radius 2 Cell Validation (`GetInnerWalkableCells`)**: Strict grid validation preventing mob spawns on non-walkable decorations, walls, house windows, or Zaap pillars.
  - **Quest Monsters & Archmonsters**: Full database mapping for special quest mobs and archmonsters.

### 🚧 Work In Progress (WIP) / Basic Implementation
- [x] **PvM Combat System (Functional Core Engine):**
  - **Tactical Arenas**: Each roleplay map resolves to its corresponding tactical combat arena based on zone offsets.
  - **Context Transitions**: Clean switching between Roleplay and Tactical Combat states, restoring world state upon fight completion.
  - **Placement Phase**: Red (monsters) and blue (players) placement tiles with dynamic cell swapping before clicking *Ready*.
  - **Isometric Grid Geometry (`MapGeometry`)**: 4-neighbor isometric grid geometry calculated using a pre-computed $O(1)$ BFS distance matrix.
  - **Line of Sight (LoS)**: LoS obstacle validation extracted for 17,222 maps, tracing segments between cell centers.
  - **Turn Protocol & Timers**: Turn handshake, 30-second turn timers with automatic pass, and AP/MP replenishment.
  - **Movement**: Cell-by-cell path expansion and Movement Point (MP) deductions.
  - **Spells & Elemental Damage**: Level-based spell querying (AP cost, range, LoS, per-turn/per-target limits), elemental damage calculations (stats, equipment power, target resistances), and critical hit rolls.
  - **Active Monster AI**: Intelligent target selection (lowest HP, HP %, isolation, distance), ranged attacks, minimal-MP BFS pathing, and flee behavior when HP falls below 30%.
  - **Fight Resolution & Progression**: Victory/defeat screens, experience progression (1,889 levels), official loot drops, level ups, and monster group respawns.
- [ ] **Commands:** `.level` and `.kamas` are present but working roughly (working on it).
- [ ] **Partial Combat Mechanics:**
  - Pushback trajectory (calculates destination cell, omits collision damage & pushback animation).
  - Weapon strikes (applies damage & AP cost, omits slash animation).
  - Stat reductions (reduces stats, omits fighter debuff UI widget).
  - Inventory Kamas (persists & displays on victory screen, requires UI tab refresh).

### ❌ Missing Features
- [ ] Kolossium System & PvP Combat
- [ ] Advanced Combat Features (AP/MP dodge rolls, area-of-effect spell shapes, summons, shields, lock & dodge in melee)
- [ ] Professions (Jobs)
- [ ] Achievements
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
3. Build the solution using Visual Studio 2022 or newer / .NET 10 SDK or via command line:
   ```bash
   dotnet build Jondo.Unity.sln -c Release
   ```
4. Run **`Jondo.Unity.Launcher`**:
   ```bash
   dotnet run --project Jondo.Unity.Launcher/Jondo.Unity.Launcher.csproj
   ```

---

### Step 2: Configure the Dofus Client (Version 3.6.4.3 Only)

> ⚠️ **Note**: Ensure you are targeting a **Dofus 3.6.4.3** client build. The emulator is **not compatible** with the latest client updates.

By default, the official Dofus client connects to official Ankama servers and verifies SSL/TLS security certificates. To redirect traffic locally, use **MelonLoader** and the **JondoFix** mod.

#### 1. Install MelonLoader
1. Download the **MelonLoader** installer (version `0.6.x` or newer, or compatible with .NET 6) from its official repository: [MelonLoader Releases](https://github.com/LavaGang/MelonLoader/releases).
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
<img width="2550" height="1501" alt="image" src="https://github.com/user-attachments/assets/74b1d19c-3bfe-40f8-9c74-e82f4647173a" />
<img width="2559" height="1506" alt="image" src="https://github.com/user-attachments/assets/521bef24-6b19-4061-bc5b-37a178e91163" />
<img width="2559" height="1500" alt="image" src="https://github.com/user-attachments/assets/0f06761a-7dcf-481e-b045-02efce31c58e" />
<img width="2559" height="1488" alt="image" src="https://github.com/user-attachments/assets/aa2249c3-699d-4137-aeef-96fc2278fcf2" />
<img width="2559" height="1497" alt="image" src="https://github.com/user-attachments/assets/33829fde-d8f1-4b5e-a3f1-11e34fd8c4ca" />
<img width="2559" height="1493" alt="image" src="https://github.com/user-attachments/assets/86a0b6e6-ea31-45a3-b381-4ba4fcc6b043" />
<img width="2559" height="1503" alt="image" src="https://github.com/user-attachments/assets/7c2aec0c-85a5-497b-9e1f-db4b77697605" />
<img width="2559" height="1508" alt="image" src="https://github.com/user-attachments/assets/cb587972-a7c5-42cd-a1e2-c1567cecccc8" />
<img width="910" height="929" alt="image" src="https://github.com/user-attachments/assets/00b35bbe-7356-41d0-ba9a-d079fbc7165f" />
<img width="2559" height="1493" alt="image" src="https://github.com/user-attachments/assets/cb75bca8-358d-4153-a2e6-955c10be92f9" />
<img width="2559" height="1511" alt="image" src="https://github.com/user-attachments/assets/38c437da-d881-4d64-b2b4-0348c789a9a3" />






