# Desofuscar el protocolo entre versiones

> Estado a 19/08/2026. Este documento es el punto de partida para montar la tubería completa
> en una conversación nueva: qué hay hecho, qué está medido, qué no funciona y por qué, cómo lo
> resuelve el único que lo ha resuelto, y qué arquitectura propongo.

---

## 1. El problema

Ankama **rota los nombres de tres letras del protocolo en cada parche**. El `jsd` que hoy saca a
un actor del mapa mañana se llamará `xqr`, y el `xqr` de hoy será otra cosa.

Consecuencias, por orden de gravedad:

1. **Las capturas caducan.** Una captura de Wireshark de hoy sirve para siempre como referencia de
   *forma*, pero deja de poder cruzarse con el cliente nuevo: los opcodes ya no coinciden.
2. **Lo que no se capture ahora, no se captura luego.** Cuando el cliente se actualice, la versión
   3.6.10.10 deja de poder conectarse a los servidores de Ankama. Todo lo que no esté grabado se
   pierde.
3. **El emulador se queda mudo.** Tiene `"jsd"`, `"ktm"`, `"jru"` escritos a pelo en cientos de
   sitios. Sin un mapeo, portarlo al parche siguiente es rehacerlo.

La pregunta que hay que responder: **¿se puede automatizar el mapeo de una versión a la
siguiente?**

---

## 2. Lo que funciona, y está medido

### 2.1 Extraer el protocolo del cliente — RESUELTO

`Jondo.Unity.ProtocolBuilder` saca el protocolo entero del cliente, sin arrancarlo y sin
conectarse a nada:

```
protocolbuilder proto <ruta a Ankama.Dofus.Protocol.Game.dll> salida.proto
```

| versión | mensajes | campos | enumerados |
|---|---|---|---|
| 3.6.10.10 Game | 2.169 | 6.186 | 550 |
| 3.6.10.10 Connection | 37 | 92 | 19 |
| 3.6.4.3 Game | 2.169 | 6.165 | 527 |

Con **números de campo y tipos**. Está en `datos/protocolo_3.6.10.10.proto` (207 KB),
`datos/protocolo_conexion_3.6.10.10.proto` y `datos/protocolo_3.6.4.3.proto`.

**De dónde sale.** El cliente es Unity con IL2CPP. El descriptor serializado de protobuf **no está
en ninguna parte** (ver §3), pero no hace falta: el generador de C# de protobuf deja en cada clase
una constante con el número de cada campo, justo antes del campo que la usa, y **Cpp2IL vuelca las
clases enteras**. Los nombres van rotados; los números y los tipos están intactos.

```
class jsd : IMessage<jsd>, IBufferMessage
    const int epvu = 1, epvw = 2, epvy = 3      ← los números de campo
    static MessageParser<jsd> epvs               ← el analizador
    UnknownFieldSet epvt
    lbo epvv, Int64 epvx, lbo epvz               ← un campo por número
```

**Y el volcado ya estaba hecho.** MelonLoader trae Cpp2IL dentro y deja su salida en:

```
<cliente>/MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/
```

Los dos clientes lo tienen: el de 3.6.10.10 en `C:\Jondo 3.6.10.10\Cliente 3.6.10.10` y el de
3.6.4.3 en `C:\Jondo\DofusClient`.

### 2.2 Los `oneof` — RESUELTO

242 mensajes salían descuadrados en el primer intento. Eran `oneof`: protobuf guarda todos sus
casos en **un solo campo `Object`** más un enumerado con el que esté puesto, así que los números no
cuadran con los campos de respaldo. **Con las propiedades sí cuadran**, y además llevan el tipo
bueno de cada caso. Cero descuadrados, y 128 campos que antes se perdían.

### 2.3 Hechos medidos sobre la rotación

Comparando 3.6.4.3 con 3.6.10.10:

- **0 de 2.169 mensajes conservan el nombre.** Rotan absolutamente todos.
- **Los números de campo NO se barajan.** `hez -> les` empareja limpio: campos 1..4 con los mismos
  tipos. El 93% de los mensajes numera 1..N seguido en las dos versiones.
- **Las formas se conservan casi**: los histogramas de campos por mensaje son casi idénticos
  (194 vs 176 vacíos, 462 vs 470 de un campo, 556 vs 574 de dos...).

Traducción: el protocolo es prácticamente el mismo; lo único que cambia son los nombres.

---

## 3. Lo que no funciona, y por qué

### 3.1 El descriptor serializado no está en el cliente

Tres callejones sin salida, documentados para que nadie los repita:

- **No hay base64** en `global-metadata.dat` (38 MB). Las 12.471 cadenas candidatas: ninguna
  parsea como `FileDescriptorProto`.
- **Tampoco en crudo.** Los `.proto` que aparecen son rutas de paquetes de Unity —
  `...\PackageCache\com.ankama.dofus.protocol.game@cc9f1e...` — porque *"protocol"* contiene
  *".proto"*. Coincidencia tonta.
- **Ni en `GameAssembly.dll`** (110 MB): cero nombres de fichero `.proto`.

### 3.2 Il2CppDumper no sirve

Los metadatos del cliente son **versión 39**, más nueva de lo que admite Il2CppDumper 6.7.46
(`ERROR: Metadata file supplied is not a supported version[39]`). **Cpp2IL sí la lee**, y es lo que
usa MelonLoader.

### 3.3 El emparejamiento puramente estructural — TECHO BAJO

`protocolbuilder emparejar <dll vieja> <dll nueva>` implementa:

- **Huellas por rondas** (refinamiento tipo Weisfeiler-Lehman): la huella de un mensaje son sus
  campos, y en cada ronda se le añaden las huellas de aquellos a los que apunta.
- **Semillas**: parejas con huella única a los dos lados.
- **Riego por parecido**: doble listón — parecerse bastante (≥0,55) *y* más que ningún otro
  (margen ≥0,08).
- **Arrastre por los padres**: si a y b son el mismo mensaje y los dos tienen un campo 3 que apunta
  a otro mensaje, esos dos hijos son el mismo.

Resultado real sobre 3.6.4.3 → 3.6.10.10:

| versión del algoritmo | emparejados | ambiguos | sin pareja |
|---|---|---|---|
| sólo huellas exactas | 108 (5,0 %) | 1.348 | 713 |
| + riego por parecido | 214 (9,9 %) | 1.332 | 623 |
| + arrastre por padres | **245 (11,3 %)** | 1.314 | 610 |

**Por qué se atasca ahí.** La mitad del protocolo son mensajes de cero a tres campos:

```
message xxx {
  int64 f2 = 2;
}
```

Eso es idéntico a otros cuatrocientos. La forma **no contiene información suficiente**. El
arrastre por los padres es la idea correcta —a una hoja la identifica quién le apunta— pero no
arranca porque las semillas iniciales son pocas: hace falta una base de anclas fiables que la
estructura sola no da.

Esto **no es un fallo de implementación**: es el techo de la señal. Quien más lejos ha llegado con
este problema llegó a la misma conclusión y se fue a buscar la señal a otro sitio.

---

## 4. Cómo lo resuelve Snowbot

Código descompilado del bot, en `scratchpad/snowbot/`. Cuatro piezas:

```
FieldsOrderFromIL   usa Cpp2IL.Core para analizar el código máquina y sacar qué campos
FieldReaderV2       toca cada método y EN QUÉ ORDEN, con su pseudocódigo

MapperFields        arma un prompt por mensaje: campos ofuscados + tipo + el pseudocódigo
                    que los usa + una versión vieja sin ofuscar, y se lo manda a un LLM
                    (DeepSeek / o4-mini) pidiendo los nombres desofuscados.
                    Con reintentos por rate limit y timeout de una hora: son miles de llamadas.

DofusUnityMapping   una vez hay nombres, convierte mensajes de una versión a otra POR NOMBRE
                    DE CAMPO, ignorando los tags (ProtoDynamicMapper)

MapperTester        valida el resultado
```

Su propio prompt (`PromptLLM.BuildDeobfuscationPrompt`) lo dice:

> *"Je souhaite désobfusquer les champs obfusqués en me basant exclusivement sur l'analyse du
> pseudoCode Obfusqué et une ancienne version non obfusquée."*

**La conclusión que importa: la señal está en el código que usa el mensaje, no en el mensaje.**

Otros proyectos públicos del mismo terreno:

- [RuinedYourLife/dofus-deobfs](https://github.com/RuinedYourLife/dofus-deobfs) — mapea protos
  ofuscados contra claros, en Go. Entrada: `.proto` sacados con Il2CppDumper + protodec.
- [LuaxY/dofus-unity-protocol-builder](https://github.com/LuaxY/dofus-unity-protocol-builder) — el
  catálogo con nombres reales (`Com.Ankama.Dofus.Server.Game.Protocol...`). **Desactualizado**.
- [Xpl0itR/protodec](https://github.com/Xpl0itR/protodec) — deriva `.proto` de ensamblados IL2CPP.
  Es lo que hace nuestro `ProtocolBuilder`, y por eso no hace falta.

---

## 5. Nuestra ventaja, que ellos no tienen

Tres cosas:

1. **No necesitamos los 2.169 mensajes.** Snowbot es un bot genérico y los necesita todos. El
   emulador implementa del orden de **cien**. Mapear cien es un problema acotado y verificable; dos
   mil, no.
2. **Sabemos qué hace cada uno de esos cien.** `jsd` saca un actor, `kti` reparte chat, `jru` carga
   mapa. Medido contra el juego real, no deducido. Son anclas semánticas de verificación.
3. **La biblioteca de capturas etiquetadas** (`C:\Jondo 3.6.10.10\Wireshark captures from real
   game\`), con escenas con nombre: "salir del mapa", "usar zaap", "otro personaje saliendo del
   mapa". Fija opcodes a momentos concretos.

Ninguno de los proyectos públicos tiene 2 ni 3.

---

## 6. Arquitectura propuesta

### 6.1 Las cinco etapas

```
┌─ 1. EXTRAER ─────────────────────────────────────────────────────────┐
│  Cpp2IL (ya lo trae MelonLoader) -> cpp2il_out                       │
│  ProtocolBuilder proto            -> protocolo_<version>.proto       │
│  HECHO. Segundos.                                                    │
└──────────────────────────────────────────────────────────────────────┘
┌─ 2. ANCLAR (estructura) ─────────────────────────────────────────────┐
│  ProtocolBuilder emparejar        -> ~11 % con alta confianza         │
│  Son los mensajes GRANDES, que son los que más cuesta a mano.        │
│  HECHO. Mejorable con anclas de enumerados.                          │
└──────────────────────────────────────────────────────────────────────┘
┌─ 3. LEER EL CÓDIGO (la señal buena) ─────────────────────────────────┐
│  Para cada mensaje sin resolver, sacar de Cpp2IL:                    │
│    · los métodos que lo construyen o lo leen                         │
│    · su pseudocódigo                                                 │
│    · las cadenas de texto cercanas (nombres de UI, claves de i18n)   │
│  POR HACER. Es el FieldsOrderFromIL de Snowbot.                      │
└──────────────────────────────────────────────────────────────────────┘
┌─ 4. DECIDIR (el LLM) ────────────────────────────────────────────────┐
│  Un expediente por mensaje: forma + candidatos de la etapa 2 +        │
│  pseudocódigo + qué sabemos de la versión vieja.                     │
│  Salida OBLIGATORIA: nombre propuesto + confianza + en qué se basa.  │
│  POR HACER.                                                          │
└──────────────────────────────────────────────────────────────────────┘
┌─ 5. VERIFICAR (lo que nadie más puede) ──────────────────────────────┐
│  · Contra las capturas: si la propuesta dice "esto sale al cruzar un  │
│    borde", tiene que aparecer en la captura de cruzar un borde.      │
│  · Contra el emulador: los cien que implementamos tienen semántica    │
│    conocida y comportamiento comprobable con el cliente falso.       │
│  · Contra el banco de dos clientes (tools/two_on_a_map.py).          │
│  POR HACER, pero las piezas existen.                                 │
└──────────────────────────────────────────────────────────────────────┘
```

### 6.2 Principios que no se negocian

- **Nada se acepta sin evidencia.** Una propuesta del LLM sin confianza y sin en-qué-se-basa no
  entra en la tabla. Es la diferencia entre un mapeo y una alucinación cara.
- **Tres niveles de confianza**: *segura* (estructura única, o verificada contra captura),
  *probable* (LLM con apoyo del código), *a mano* (hoja del grafo sin nada que la distinga). Y se
  publica cuántas hay de cada.
- **El humano decide las dudosas.** Por eso hace falta interfaz (§7).
- **El mapeo es un fichero versionado**: `mapeo_<vieja>_a_<nueva>.txt`, en el repo, revisable en un
  diff.

### 6.3 Consecuencia para el emulador — empezar YA

Tenemos `"jsd"` escrito a pelo por todo el código. **El día del parche, tener el mapeo no sirve de
nada si hay que editar trescientos literales a mano.**

Hace falta una capa intermedia:

```csharp
ConnectionProtocol.Push(Op.ActorLeft, ...)     // en vez de Push("jsd", ...)
```

con una tabla por versión que resuelva `Op.ActorLeft → "jsd"`. Es refactor mecánico, se puede hacer
poco a poco, y convierte "portar el emulador al parche siguiente" en cambiar un fichero. **Esto se
puede —y se debe— hacer antes que nada de lo demás.**

---

## 7. La interfaz

Un WinForms con el aspecto del resto (reutiliza `LauncherTheme`, `LauncherPanel`, `LauncherButton`
de `Jondo.Unity.Contract`). Cuatro zonas:

```
┌───────────────────────────────────────────────────────────────────────────┐
│  JONDO — DESOFUSCADOR                                    [3.6.4.3 ▾]      │
│                                                          [3.6.10.10 ▾]    │
├──────────────────┬────────────────────────────────────────────────────────┤
│ MENSAJES         │  DETALLE                                               │
│                  │                                                        │
│ ▸ seguras   245  │   vieja: hez              nueva: les      92 %         │
│ ▸ probables 610  │   ┌──────────────────┬──────────────────┐              │
│ ▸ a mano  1.314  │   │ hex  f1          │ leq  f1          │              │
│ ▸ nuevas    ...  │   │ int32 f2         │ int32 f2         │              │
│                  │   │ string f3        │ string f3        │              │
│ [buscar...]      │   │ int32 f4         │ int32 f4         │              │
│                  │   └──────────────────┴──────────────────┘              │
│ hez → les    92% │                                                        │
│ hee → jlm    61% │   EN QUÉ SE BASA                                       │
│ hhs → mah    54% │   · forma idéntica, 4 campos, tipos iguales            │
│ ...              │   · el padre hdw ya está emparejado con jkq (campo 7)  │
│                  │   · aparece en «usar zaap» tras el iwo   [ver captura] │
│                  │                                                        │
│                  │   [ACEPTAR]  [RECHAZAR]  [BUSCAR OTRO]                 │
├──────────────────┴────────────────────────────────────────────────────────┤
│  1.235 de 2.169 resueltos   ·   de los 104 que usa el emulador: 98        │
│  [EXTRAER]  [EMPAREJAR]  [PREGUNTAR AL LLM]  [VERIFICAR]  [EXPORTAR]      │
└───────────────────────────────────────────────────────────────────────────┘
```

Lo que la hace útil y no un adorno:

- **La barra de abajo cuenta lo que importa**: no "1.235 de 2.169", sino **"de los 104 que usa el
  emulador, 98"**. Ése es el número que decide si el emulador arranca con el parche nuevo.
- **"En qué se basa"** con la evidencia de cada propuesta, y el botón para abrir la captura donde
  aparece.
- **Aceptar / rechazar** una por una, porque las dudosas las decide una persona.
- Los botones de abajo son las cinco etapas, en orden, y se pueden repetir por separado.

---

## 8. Cómo está el repo

```
Jondo.Unity.ProtocolBuilder/          el proyecto, en la solución
  Program.cs                          comandos: proto, emparejar, probar, mirar, volcar
  AssemblyReader.cs                   lee ensamblados sin ejecutarlos (MetadataLoadContext)
  ProtoWriter.cs                      reconstruye el .proto de las clases
  Matcher.cs                          huellas, riego y arrastre
  Shuffle.cs                          baraja nombres para medir el techo
  DescriptorExtractor.cs              el camino muerto del descriptor serializado (§3.1)

datos/protocolo_3.6.10.10.proto       2.169 mensajes con números y tipos
datos/protocolo_conexion_3.6.10.10.proto
datos/protocolo_3.6.4.3.proto         la versión vieja
datos/mapeo_3.6.4.3_a_3.6.10.10.txt   245 parejas + ambiguos + sin pareja

scratchpad/snowbot/                   el código descompilado de Snowbot (fuera del repo)
```

Comandos:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- proto <dll> <salida.proto>
dotnet run --project Jondo.Unity.ProtocolBuilder -- emparejar <dll vieja> <dll nueva> <salida.txt>
dotnet run --project Jondo.Unity.ProtocolBuilder -- probar <dll>
dotnet run --project Jondo.Unity.ProtocolBuilder -- mirar <dll> [tipo]
```

---

## 9. Por dónde empezar en la conversación nueva

Por orden de valor:

1. **La capa `Op.` en el emulador** (§6.3). Es lo único que hay que tener hecho ANTES del parche, y
   no depende de nada de lo demás.
2. **La etapa 3 sobre un puñado de mensajes** — cinco o seis que ya conozcamos (`jsd`, `kti`,
   `jru`) — para medir si el pseudocódigo da lo que promete antes de invertir en la tubería
   entera. Es una prueba barata con respuesta conocida.
3. **Anclas por enumerados** en el emparejador: los enumerados no cambian de forma entre versiones
   y ahora mismo no se usan como semilla. Es la mejora estructural más barata que queda.
4. La interfaz, cuando las etapas 3 y 4 den resultados que revisar.

Y lo que no cambia nunca: **seguir capturando**. Un mapeo traduce nombres; no inventa datos que no
se grabaron.
