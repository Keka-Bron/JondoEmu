# Jondo Unity Emulator — Dofus 3.6 Server

High-performance server emulator for **Dofus 3 Unity (Client 3.6.4.3)** written in C# (**NET 10**), architected with decoupled modular projects, a SQLite database layer, and a functional PvM combat engine.

---

## 🚀 General Emulation Status

### ✅ Completed & Fully Operational Features
- [x] **Authentication & Connection Protocol**: Zaap, HAAPI, and Connection Server proxies with infinite VIP subscription bypass.
- [x] **Server & Character Selection**: Smooth character loading, 3D appearances, and database persistence.
- [x] **Navigation & Map Engine**: Roleplay movement, map transitions, adjacent map loading, and position persistence in database.
- [x] **Inventory System**: Item spawning, equipping/unequipping, item bags, and persistent inventory storage.
- [x] **Character Stats & Attributes**: Full characteristic point allocation with dynamic capital points and real-time HUD synchronization.
- [x] **NPC System**: NPC spawns, 3D appearances, and dialogue trees.
- [x] **Monster & Mob System (100% Complete)**:
  - **Dynamic Map Spawning & Respawner**: Automatic population and maintenance of 2 to 4 mob groups per map.
  - **Level & Grade Management**: Accurate levels, grades, and official experience calculations.
  - **3D Skeleton & Mesh Models**: Native Protobuf bone models, custom scales, and textures for monsters, quest mobs, and archmonsters.
  - **Multi-Monster Groups**: Complete support for groups of 1 to 8 monsters per mob.
  - **Radius-2 Walkable Cell Validation**: Grid validation preventing mob spawns on non-walkable decorations or obstacles.
- [x] **PvM Combat System**: Functional turn-based combat system (see detailed section below).

### 🚧 In Progress / Basic Implementation
- [ ] **GM Commands**: `.level` and `.kamas` commands available and being refined.
- [ ] **PvP System & Multi-Player Combat**.

### ❌ Not Implemented Yet
- [ ] Zaaps
- [ ] Kolossium System
- [ ] Professions (Jobs)
- [ ] Achievements
- [ ] Titles & Ornaments
- [ ] Guilds

---

## 🏛 Solution Architecture (`Jondo.Unity.sln`)

- **`Jondo.Unity.Launcher`**: Entry point (`Program.cs`), TCP socket proxies (`GameServerProxy`, `GameNodeProxy`), `DatabaseManager`, `StatsHandler`, `ChatHandler`, and network message handlers.
- **`Jondo.Unity.World`**: In-memory world state, map management (`MapManager`), combat engine (`FightInstance`, `FightHandler`), inventory management, and Monster AI engine (`MonsterAI`).
- **`Jondo.Unity.Protocol`**: Type-safe C# classes generated via Protobuf 3 (`Jondo.Unity.Protocol.Messages`), frame serializers, and reflection types.
- **`Jondo.Unity.Core`**: Base interfaces, primitive types, enumerations, protocol constants (`ProtocolConstants`), and mathematical utilities.
- **`JondoFix`**: Injection patch written in C# (.NET 6) to adapt network calls within the native Unity client.

---

## ⚔️ PvM Combat System Status

The emulator features a complete and functional implementation of the Player vs. Monster (PvM) combat engine, verified against official network traffic captures and client metadata.

### ✅ Fully Functional (100% Implemented)

#### Combat Setup & Environment
- **Tactical Arenas**: Each roleplay map resolves to its corresponding tactical combat arena within the subarea based on zone offsets.
- **Context Transition**: Clean context switching between Roleplay and Tactical Combat states, restoring world state seamlessly upon fight conclusion.
- **Placement Phase**: Team placement tiles (red for monsters, blue for players) with interactive position changes before clicking *Ready*.

#### Isometric Grid Geometry
- **4-Neighbor Isometric Grid (`MapGeometry`)**: Exact distances calculated using a pre-computed $O(1)$ BFS distance matrix (Even deltas: `-28, -15, -14, -1, +1, +13, +14, +28`; Odd deltas: `-28, -14, -13, -1, +1, +14, +15, +28`).
- **Line of Sight (LoS)**: LoS obstacle data extracted for 17,222 maps, tracing segments between cell centers and checking opaque obstacles.

#### Turns & Movement
- **Turn Handshake Protocol**: Full sequence covering turn start notifications, client confirmation, 30-second turn timer initialization, and action readiness.
- **30-Second Turn Timer**: Server-enforced automatic turn pass when the timer expires.
- **Cell-by-Cell Movement**: Path expansion across walkable grid tiles and accurate Movement Point (MP) deductions.

#### Spells & Monster AI
- **Dynamic Level-Based Spells**: Queries `SpellLevels` in the database for AP costs, min/max ranges, Line of Sight requirements, and per-turn/per-target cast limits.
- **Elemental Damage Calculation**: Damage calculated using elemental stats, equipment power, and target resistances.
- **Critical Hits**: Combined spell and equipment critical hit chances, triggering spell-specific critical damage rolls.
- **Active Monster AI**: Intelligent target selection (lowest HP, HP %, isolation, distance), ranged attacks, minimal-MP movement via BFS, and flee behavior when HP falls below 30%.

#### Fight Resolution & Progression
- **Victory & Defeat Screens**: Experience awards, level ups (using official 1,889-level table), stat point allocation, and spellbook updates.
- **Monster Loot**: Item drops calculated using official grade probabilities added to inventory.
- **Respawner**: Defeated monster groups are automatically replaced by newly generated random groups on the map.

---

### 🟡 Partial Implementation

- **Pushback**: Destination cells are correctly calculated and applied along the collision trajectory, though the movement animation reuses standard walking visuals. Collision damage on obstacles is pending.
- **Weapon Strikes**: Damage and AP costs are correctly processed, but weapon slash animations are omitted.
- **Buff & Debuff List**: AP/MP stat reductions take effect, but the visual debuff widget on the fighter UI is not yet rendered.
- **End-Screen Statistics**: Cumulative damage dealt/received meters currently display zero.
- **Inventory Kamas Counter**: Kamas are persisted and displayed on the victory screen, but the inventory UI counter requires an interface refresh.

---

### 🔴 Pending Implementation

- Dodge rolls for AP/MP loss reduction.
- Area-of-effect spell shapes.
- Summons, healing spells, shields, and advanced status effects.
- Lock and Dodge mechanics when escaping melee range.
- Player equipment elemental resistances.
- Prospecting multipliers and conditional quest drops.
- Multi-player and PvP combat.

---

## 🚀 Getting Started

### 1. Prerequisites
- **.NET 10 SDK** (or later).
- **Dofus 3.6.4.3 Client**.

### 2. Extract the Database
The database `world.db` is distributed as `world.zip` to remain within GitHub file size limits:

```powershell
cd "Jondo Unity Emulator"
powershell Expand-Archive -Path world.zip -DestinationPath . -Force
```

### 3. Build the Solution
```powershell
dotnet build Jondo.Unity.sln -c Release
```

### 4. Build & Apply `JondoFix` Patch
Build `JondoFix/JondoFix.csproj` and inject the output DLL into the Unity client runtime.

### 5. Launch the Emulator Server
```powershell
dotnet run --project Jondo.Unity.Launcher/Jondo.Unity.Launcher.csproj
```

---

## 📚 Additional Documentation

- [Startup & Connection Guide](docs/DOC_01_ARRANQUE_Y_CONEXION.md)
- [Maps & Movement Specification](docs/DOC_02_MAPAS_Y_MOVIMIENTO.md)
- [Technical Specification & Iteration Log](EspecificacionTecnica.md)
- [Combat Refactoring Plans](docs/PLAN_COMBATE_V3.md)
- [Python Data Extraction Tools](tools/README.md)
