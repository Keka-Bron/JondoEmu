# Qué subir a GitHub — emulador Jondo

Instrucciones para publicar la versión actual del emulador (con la primera pasada de
combate implementada).

## Situación de partida

El repositorio **ya existe** y no hay que crearlo:

- Raíz del repositorio: `C:\Jondo\Jondo Unity Emulator\` (ahí está el `.git`)
- Remoto: `https://github.com/santiagofu/JondoEmu.git`
- Rama: `main`
- Último commit publicado: `f35db21` (sistema de monstruos)
- Hay unos 270 ficheros modificados o sin seguimiento pendientes de commit

Ojo con esto: la carpeta `C:\Jondo\` **no** es el repositorio, es su carpeta padre. Ahí viven
las capturas de red, el cliente del juego y los scripts de extracción. Nada de `C:\Jondo\`
entra en el repositorio salvo lo que se indique explícitamente más abajo.

---

## Antes de nada: el problema del `world.db`

**`world.db` pesa 239 MB y GitHub rechaza cualquier fichero de más de 100 MB.** Si se
incluye tal cual, el `push` falla entero.

En el historial actual `world.db` está commiteado como fichero de **0 bytes** (un hueco),
así que el historial está limpio y no hace falta reescribirlo.

Opción recomendada: **no subir el `.db`, subir el comprimido.**

```bash
cd "C:\Jondo\Jondo Unity Emulator"
# Regenerar el zip (el world.zip que hay es viejo: contiene una versión de 125 MB)
powershell Compress-Archive -Path world.db -DestinationPath world.zip -Force
```

Y en `.gitignore` añadir `world.db`. Quien clone el repositorio descomprime el zip y listo.
Comprimido debería quedar en torno a 35-40 MB, muy por debajo del límite.

Las otras dos opciones, por si se prefieren:

- **Git LFS** para `world.db`. Funciona, pero la cuota gratuita de LFS es de 1 GB y el
  repositorio ya usa LFS para `Dofus3 Defuscated Data/` (unos 330 MB).
- **No subir la base de datos** y dejar solo los scripts que la reconstruyen desde
  `dofus3_data/`. Es lo más limpio, pero deja el repositorio inservible sin un paso manual
  largo.

---

## 1. Código fuente — imprescindible

Cuatro proyectos, con estas dependencias entre ellos:
`Launcher → Protocol, World` · `World → Core, Protocol` · `Protocol → Core`

```
Jondo Unity Emulator/
├── Jondo.Unity.sln
├── Jondo.Unity.slnx
├── Jondo.Unity.Launcher/          ← servidor: 30 .cs (proxy, handlers, gestores)
├── Jondo.Unity.World/             ← lógica de combate: 6 .cs
├── Jondo.Unity.Protocol/          ← envoltorio protobuf: 3 .cs
└── Jondo.Unity.Core/              ← tipos compartidos: 3 .cs
```

De cada carpeta hay que incluir los `.cs` y el `.csproj`, **nunca** sus `bin/` ni `obj/`.

### Parche del cliente — imprescindible

```
Jondo Unity Emulator/
└── JondoFix/                      ← Class1.cs, JondoFix.csproj, eud_class_definition.txt
```

Es la modificación que se inyecta en el cliente de Dofus. Sin ella el cliente no habla con
el emulador. No está en el `.sln`, se compila aparte.

### Proyectos auxiliares — opcionales

Están en la solución pero el emulador no los referencia. Se pueden incluir sin problema
(pesan muy poco) o dejar fuera:

```
Jondo.Unity.Auth/  ·  Jondo.Unity.Parser/  ·  Jondo.Unity.DatabaseSeeder/
FixDb/  ·  CheckDb/
```

---

## 2. Datos que el emulador lee al arrancar

Van en la raíz del repositorio, junto al ejecutable. Los resuelve `Paths.cs`.

| Fichero | Tamaño | Para qué sirve |
|---|---|---|
| `.jondo-root` | 0 B | Marcador que usa `Paths.cs` para localizar la raíz. **No olvidarlo** |
| `world.db` | 239 MB | Mapas, monstruos, objetos, hechizos, personajes. **Ver el aviso de arriba** |
| `auth.db` | 20 KB | Cuentas |
| `map_walkable_cells.json` | 14,4 MB | Casillas transitables en roleplay |
| `map_fight_cells.json` | 19,7 MB | Casillas de combate y casillas opacas (línea de visión) |
| `character_xp.json` | 40 KB | Experiencia acumulada por nivel |
| `map_dump_coordinates.csv` | 270 KB | Coordenadas de mapa |
| `map_dump_infos.csv` | 450 KB | Información de mapa |
| `map_dump_scrolls.csv` | 70 KB | Transiciones entre mapas |

Los dos JSON grandes están por debajo del límite de GitHub, así que van tal cual.

---

## 3. Herramientas que regeneran esos datos

Están en `C:\Jondo\` (fuera del repositorio). Conviene **copiarlas dentro**, por ejemplo a
`Jondo Unity Emulator/tools/`, porque son las que permiten reconstruir los JSON desde los
bundles del cliente:

```
extract_fight_cells.py        → genera map_fight_cells.json
extract_character_xp.py       → genera character_xp.json
extract_all_map_walkable.py   → genera map_walkable_cells.json
```

Necesitan Python con `UnityPy` y una copia del cliente instalada. Merece la pena añadir un
`tools/README.md` que lo diga.

Opcionalmente, las herramientas de análisis de capturas de `C:\Jondo\scripts\`
(`fightdump.py`, `johtrace.py`, `dump_jyi_cells.py`, `protomatch/`). Son útiles para seguir
descifrando el protocolo, pero no hacen falta para ejecutar nada.

---

## 4. Documentación

```
Jondo Unity Emulator/README.md
Jondo Unity Emulator/EspecificacionTecnica.md
```

Y de `C:\Jondo\`, si se quieren conservar (van a la raíz del repositorio o a `docs/`):

```
DOC_01_ARRANQUE_Y_CONEXION.md      DOC_02_MAPAS_Y_MOVIMIENTO.md
PLAN_COMBATE_V2.md                 PLAN_COMBATE_V3.md
PLAN_MIGRACION_3.6.8.8.md          DIAGNOSTICO_COMBATE.md
```

---

## 5. Qué NO subir

**Resultado de compilación** (se regenera; además ensucia el repositorio):

```
**/bin/  **/obj/  publish/  runtimes/
*.dll  *.pdb  *.exe
Jondo.Unity.*.deps.json  Jondo.Unity.*.runtimeconfig.json
```

Las DLL de la raíz (`Google.Protobuf.dll`, `Thrift.dll`, `Microsoft.Data.Sqlite.dll`,
`SQLitePCLRaw.*.dll`, `e_sqlite3.dll`, `System.Net.Http.WinHttpHandler.dll`) **todas vienen
de NuGet**; no hay ni una referencia local en los `.csproj`. Se restauran solas al compilar.

**Ficheros temporales y de trabajo:**

```
*.log                    ← emulator_debug.log, gameserver_traffic.log, dofus_jondo.log
world.db-shm  world.db-wal
.vs/
```

**Material pesado o problemático que está en `C:\Jondo\`:**

```
DofusClient/                  ← el cliente del juego: con copyright, no se publica
*.pcapng                      ← capturas de red, cientos de MB, con datos de tu cuenta
Dofus3 Defuscated Data/       ← volcado descompilado del cliente (~330 MB)
dofus3_data/                  ← 248 MB de JSON extraídos; solo hacen falta para
                                 reconstruir world.db desde cero
_binarios_antiguos_raiz/  __pycache__/  publish/
*_old.cs  scratch_*  test_*  Check*.cs  Test*.cs
```

Sobre `Dofus3 Defuscated Data/`: ahora mismo está en LFS. Mi recomendación es **sacarlo del
repositorio** — son 330 MB de código descompilado del cliente, con las implicaciones legales
que eso tiene, y no hace falta para nada en tiempo de ejecución.

Sobre `dofus3_data/`: solo lo leen `EnsureMobsSeeded` y `EnsureSpellsSeeded`, que se limitan
a rellenar tablas vacías. Con un `world.db` ya poblado no se toca. Si se quiere que el
repositorio sea autosuficiente para reconstruir la base de datos, habría que subirlo (248 MB,
con `spell_levels.json` a 85 MB), pero es mejor documentar de dónde sale.

---

## 6. `.gitignore` propuesto

El actual solo tiene 62 bytes y se queda muy corto. Sustituirlo por:

```gitignore
# Compilación
[Bb]in/
[Oo]bj/
publish/
runtimes/
*.dll
*.pdb
*.exe
*.deps.json
*.runtimeconfig.json

# Base de datos: se distribuye comprimida (world.zip). Descomprimir tras clonar.
world.db
world.db-shm
world.db-wal

# Registros
*.log

# Capturas de red y volcados de trabajo
*.pcapng
scratch_*
*_old.cs

# Datos extraídos del cliente (se regeneran con tools/)
dofus3_data/
Dofus3 Defuscated Data/
DofusClient/

# Entornos de desarrollo
.vs/
.idea/
.vscode/
__pycache__/
*.user
*.suo
```

Y en `.gitattributes` se pueden quitar las cuatro líneas de LFS si se saca
`Dofus3 Defuscated Data/` del repositorio.

---

## 7. Orden sugerido

1. Regenerar `world.zip` desde el `world.db` actual.
2. Escribir el `.gitignore` nuevo.
3. `git rm -r --cached` de lo que ahora esté seguido y deba dejar de estarlo
   (`bin/`, `obj/`, `*.log`, `world.db`, `Dofus3 Defuscated Data/`).
4. Copiar los tres scripts de extracción a `tools/`.
5. Añadir al `README.md` cómo arrancar: descomprimir `world.zip`, compilar la solución,
   compilar `JondoFix` e inyectarlo en el cliente.
6. Commit y push.

Antes del push conviene comprobar que no queda ningún fichero grande:

```bash
git ls-files -z | xargs -0 -I{} sh -c 'test -f "{}" && s=$(stat -c%s "{}") && [ "$s" -gt 50000000 ] && echo "$((s/1048576)) MB {}"'
```

Si esa orden no devuelve nada, el push debería pasar sin problemas.
