# Jondo Unity Emulator - Dofus 3.6.4.3

This repository contains the complete architecture of the **Jondo** local emulator designed for the Unity client of Dofus (version 3.6.4.3), as well as the **JondoFix** client mod for MelonLoader.

## 🚀 Emulation Status

### ✅ Completed / Working Features
- [x] **Client-Server-Authentication Emulation** (Zaap, HAAPI, Connection Server)
- [x] **Server Selection & Character Selection**
- [x] **World Loading (World / Game Node)**
- [x] **Character Spawn & Name Hover**
- [x] **Suscription bypass - Infinite VIP suscription**
- [x] **Movement, Map Change, Map Loading, Adjacent Maps**
- [x] **Last Cell and Map Persistence** in Database
- [x] **Inventory System:** Item spawning, equipping and unequipping items works well.
- [x] **Character Stats:** Characteristic assignment is fully functional.
- [x] **NPCs:** Spawns, looks, and dialogues are implemented *(Note: Needs a slight fix on closing dialogues properly. NPC buyers/sellers haven't been tested yet).*

### 🚧 Work In Progress (WIP) / Basic Implementation
- [ ] **Commands:** `.level` and `.kamas` are present but working roughly (working on it).

### ❌ Missing Features
- [ ] **Zaaps**
- [ ] **Monsters:** Spawns, Mobs management, and Monster AI.
- [ ] **Fights:** PVM and PVP combat system.
- [ ] **Spells**
- [ ] **Kolossium system**
- [ ] **Jobs (Oficios)**
- [ ] **Achievements (Logros)**
- [ ] **Ornaments and Titles**
- [ ] **Guilds (Gremios)**

---

## 📂 Repository Structure

* **`Jondo Emulator Launcher.exe`** (in root): Precompiled executable ready to run the local emulator server (includes all dependent DLLs and the `runtimes/` directory).
* **`JondoFix.dll`** (in root): Precompiled binary of the MelonLoader client mod ready for use in the game.
* **[Jondo.Unity.sln](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.sln)**: Visual Studio solution grouping all subprojects of the emulator:
  * **[Jondo.Unity.Launcher](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Launcher)**: Server entry point, proxies, network parser, and local database.
  * **[Jondo.Unity.Core](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Core)**: Basic networking layer and TCP servers.
  * **[Jondo.Unity.Auth](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Auth)**: Authentication service.
  * **[Jondo.Unity.Protocol](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.Protocol)**: Protocol buffers and message definitions (Protobuf).
  * **[Jondo.Unity.World](file:///C:/Jondo/Jondo%20Unity%20Emulator/Jondo.Unity.World)**: Game Node / World server logic.
* **[JondoFix](file:///C:/Jondo/Jondo%20Unity%20Emulator/JondoFix)**: MelonLoader client mod source code that redirects Dofus client traffic to the local server and bypasses official SSL certificate checks.
* **[Dofus3 Defuscated Data](file:///C:/Jondo/Jondo%20Unity%20Emulator/Dofus3%20Defuscated%20Data)**: Deobfuscated class dumps, IL2CPP headers, and analysis scripts for Ghidra and IDA.
* **[EspecificacionTecnica.md](file:///C:/Jondo/Jondo%20Unity%20Emulator/EspecificacionTecnica.md)**: Detailed specification of the protocol, network architecture, ports, and the double-role sniffer on port `5555`.

---

## 💾 Database & Persistence

The emulator uses two **SQLite** databases for local persistence, which are managed within the project launcher directory:
* **`auth.db`**: Stores accounts, credentials, and authentication sessions.
* **`world.db`**: Stores character data, inventories, positions, map persistence, and other world-related states.

---


## 🚀 Quick Start Guide (No Compilation Required)

Anyone can clone the repository and run the emulator immediately using the precompiled files in the root folder.

### Step 1: Run the Emulator (Server)
1. Run **`Jondo Emulator Launcher.exe`** in the root of this directory.
2. This will launch locally:
   - The Ankama Zaap API named pipe/TCP port `15881`.
   - The HTTP HAAPI server on port `8888`.
   - The Connection Server and Game Node on port `5555`.
   - The secure Chat Server on port `6337`.

---

### Step 2: Configure the Dofus Client (MelonLoader & JondoFix)

By default, the official Dofus client tries to connect to the official Ankama servers and verifies SSL/TLS security certificates. To redirect it locally and securely, we use **MelonLoader** and the **JondoFix** mod.

#### 1. Install MelonLoader
1. Download the **MelonLoader** installer (version `0.6.x` or compatible with .NET 6) from its official GitHub repository: [MelonLoader Releases](https://github.com/LavaGang/MelonLoader/releases).
2. Run the installer and select the executable file of the Dofus client (e.g., `Dofus.exe` in your Dofus client folder).
3. Ensure the installation is configured to use the appropriate runtime (usually auto-detected as **IL2CPP** or **.NET 6**).
4. Click **Install**. This will generate the `MelonLoader/`, `Mods/`, and `UserData/` folders in your game directory.

#### 2. Load the JondoFix Mod
1. Go to the root of this repository and copy the precompiled **`JondoFix.dll`** file.
2. Paste it inside the **`Mods/`** folder created in your Dofus client installation directory.
3. When you start the game via the official launcher or directly with the MelonLoader modified executable, the mod will load automatically.

#### What does JondoFix do exactly?
* **Network Redirection**: Intercepts sockets, Named Pipes, and DNS queries, redirecting Ankama web traffic to `localhost` (ports `8888`, `5555`, etc.).
* **SSL Bypass**: Prevents HTTPS requests to HAAPI and the Connection Server from failing due to self-signed local certificates.
* **Environment Configuration**: Injects the required environment variables (`ZAAP_PORT`, `ZAAP_HASH`, etc.) to fool the client into thinking the official launcher is running in the background.

---

## 🛠️ Development & Compilation (From Source)

If you wish to make modifications to the emulator or the client mod:

### Compiling the Emulator
You can open the **`Jondo.Unity.sln`** solution in Visual Studio 2022 or higher, or build it directly from your terminal with:
```bash
dotnet build -c Release
```

### Compiling JondoFix
The client mod can be compiled with:
```bash
dotnet build JondoFix/JondoFix.csproj -c Release
```
The resulting DLL file will be generated at `JondoFix/bin/Release/net6.0/JondoFix.dll`.

<img width="2560" height="1504" alt="image" src="https://github.com/user-attachments/assets/1af6569e-0fef-4c8d-8ede-512aec40aabb" />
<img width="2560" height="1500" alt="image" src="https://github.com/user-attachments/assets/bc56cc4c-8c56-4208-8863-76fbe7251f1d" />
<img width="2560" height="1510" alt="image" src="https://github.com/user-attachments/assets/f7bec8df-4aa2-4718-ad73-6229a3207d78" />
<img width="2560" height="1502" alt="image" src="https://github.com/user-attachments/assets/46ae56c8-d94f-47a0-830d-de68834d94e7" />
