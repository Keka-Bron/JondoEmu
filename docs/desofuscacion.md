# Desofuscar el protocolo entre versiones

> Estado a 19/08/2026, segunda pasada. Este documento es el punto de partida para montar la
> tubería completa: qué hay hecho, qué está medido, qué no funciona y por qué, cómo lo resuelve el
> único que lo ha resuelto, y qué arquitectura propongo.
>
> **Lo que ha cambiado en esta pasada.** Las etapas 3 y 4 ya no son un plan: están escritas, corren
> y están medidas. Y midiéndolas se han caído dos premisas de la versión anterior, las dos en §4:
> Snowbot no lee el pseudocódigo de un cliente ofuscado contra otro ofuscado —tiene una compilación
> SIN ofuscar, y eso lo cambia todo—, y los ensamblados que deja Cpp2IL en `cpp2il_out` no traen
> código, sólo firmas. A cambio ha aparecido una vena que no estaba a la vista: al ofuscador se le
> escapan nombres dentro de las clases que sí renombra, y son nombres que describen lo que hacen.

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

Desde el barrido del 24 de agosto de 2026 conserva también la presencia `optional`. El C# que
genera protobuf añade después de cada scalar opcional una propiedad Boolean de sólo lectura
(`HasFoo`) que no es un campo del cable; con los nombres ofuscados se estaba emparejando por error
con la constante del campo siguiente. `ProtoWriter` descarta ahora esos indicadores y el enumerado
auxiliar de cada `oneof`, pero conserva las propiedades `RepeatedField` y `MapField`, que sí son
campos aunque no tengan setter. La regla cuadra exactamente en los 2.169 mensajes Game: 6.186
constantes y 6.186 propiedades de cable, incluidos 381 indicadores de presencia descartados. Así,
por ejemplo, `jvp` queda como `optional int32 f1`, `int64 f2` y tres `int32` en `f3..f5`, sin el
Boolean ficticio ni el corrimiento que antes contaminaba el expediente.

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

> **Corregido (§2.6).** De aquí se concluía que «el protocolo es prácticamente el mismo; lo único
> que cambia son los nombres». Es falso, y el histograma es justo el argumento que engaña: que la
> silueta del montón sea la misma no dice nada de si cada mensaje sigue igual. Medido mensaje a
> mensaje, un parche con rotación pierde **690 de las 760 formas únicas**. Cambian los nombres y
> cambia el protocolo, y lo segundo es lo que hace daño.

### 2.4 Al ofuscador se le escapan nombres — RESUELTO, y es la vena buena

Ankama no ofusca el cliente entero. Lo que Unity necesita por nombre —los `MonoBehaviour`, los
campos serializados, los espacios de nombres, las implementaciones de interfaz— se queda tal cual.
Medido sobre `Core.dll` de 3.6.10.10:

| | legibles | de | |
|---|---:|---:|---|
| tipos | 3.042 | 9.442 | 32 % |
| métodos | 15.759 | 110.811 | 14 % |
| campos | 19.930 | 52.408 | 38 % |

Y hay una segunda capa, mejor todavía: **una clase con el nombre rotado suele conservar por dentro
nombres que la delatan**. Son las máquinas de estado de `async`, las lambdas y los accesores, que
el compilador bautiza `<NombreOriginal>d__31` y el ofuscador no toca porque el runtime los busca
por nombre.

```
ehl                          ← el nombre de la clase no dice nada
ehl+<WaitProcessMapComplementaryInfo>d__31::MoveNext      ← pero esto sí
ehl+<WaitForDroppingObjects>d__20::MoveNext
```

**377 clases ofuscadas de `Core.dll` conservan al menos un nombre así.** `jss` lo toca `ehl`, o sea
que `jss` es la información complementaria del mapa. Que es exactamente lo que es.

### 2.5 El techo del emparejador, medido de dos maneras

`protocolbuilder probar` rota los nombres del protocolo de ahora y le pide al emparejador que
reconstruya la correspondencia. Con la respuesta conocida entera:

```
2.169 mensajes, con los nombres barajados
emparejados bien : 1.481  (68,3 %)
emparejados MAL  : 0
ambiguos         : 688

de los 254 que usa el emulador y están en el protocolo: 154 (60,6 %)
```

Dos lecturas que hay que tener presentes:

- **El 11,3 % de §3.3 no es lo que va a dar el parche siguiente.** Es lo que da el salto de 3.6.4.3
  a 3.6.10.10, que son muchos parches y un protocolo que cambió de verdad. Un parche normal se
  parece más al 68 %, porque cambia poco más que los nombres.
- **Cero emparejados mal.** El emparejador no inventa: cuando no lo tiene claro lo deja ambiguo. Eso
  significa que lo que saca se puede usar sin revisar, y que el trabajo está en los 688 ambiguos.

Y el número que de verdad decide, que es el que pide §7: aun en el caso bueno, **100 de los 254
mensajes que el emulador usa se quedan sin pareja**. Ésos son el trabajo de las etapas 3 y 4.

> **Corregido (§2.6).** Aquí decía que «un parche normal se parece más al 68 %». No hay tal parche
> normal: los hay de dos clases y no se parecen en nada. Sin rotación sale el 71 %, que es este
> techo; con rotación sale el 11 %, y no hay término medio.

### 2.6 Ocho parches reales, medidos uno a uno

Los clientes viejos siguen en la CDN de Ankama (§2.7), así que la cadena 3.6.4.3 → 3.6.10.10 se
puede recorrer entera. `protocolbuilder cadena` la recorre y mide cada salto por separado.

Esto vale más que todo lo medido hasta ahora, y por un motivo concreto: **el emparejador no mira
los nombres en ningún momento** —sólo números de campo, clases de campo y vecindad; el nombre es la
clave del diccionario y nada más—. Así que un mensaje que se llama igual en dos versiones es una
respuesta conocida que el emparejador **no ha podido copiar**. Verdad de campo gratis, sobre parches
de verdad y no sobre nombres barajados a mano.

```
salto                nombres  formas  semillas  rota │ empareja   duda   solo │ acierta  FALLA
3.6.4.3 →3.6.5.4      2,169   2,169       760    no  │    1,540    629      0 │   1,540      0
3.6.5.4 →3.6.6.5      1,384   1,470        74    sí  │      223  1,305    641 │      —      —
3.6.6.5 →3.6.6.6      2,167   2,167       728    no  │    1,511    656      0 │   1,511      0
3.6.6.6 →3.6.7.7      1,384   1,483        66    sí  │      232  1,329    606 │      —      —
3.6.7.7 →3.6.8.8      1,373   1,465        72    sí  │      249  1,307    622 │      —      —
3.6.8.8 →3.6.9.9      1,382   1,477        77    sí  │      261  1,296    624 │      —      —
3.6.9.9 →3.6.10.10    2,169   2,169       753    no  │    1,574    595      0 │   1,574      0
```

**1. Ankama NO rota en cada parche.** Tres de los siete saltos conservan los 2.169 nombres uno a
uno. Hay cinco generaciones de ofuscación en ocho versiones: `{4.3, 5.4}`, `{6.5, 6.6}`, `{7.7}`,
`{8.8}`, `{9.9, 10.10}`. Cuando no rota, **el mapeo es la identidad y no hay trabajo que hacer**.
Comprobarlo cuesta un segundo y es lo primero que hay que mirar el día del parche.

**2. Cero emparejamientos equivocados, ahora sobre parches de verdad.** En los tres saltos sin
rotación: de 6.505 mensajes, **4.625 bien (71,1 %) y 0 mal**. El 71,1 % real confirma el 68,3 %
sintético de §2.5, así que el techo estaba bien medido; y el «no inventa» deja de ser una propiedad
observada en un experimento de laboratorio.

**3. El daño de la rotación satura en la primera.** Un salto con rotación empareja 223–261. El
salto directo, que cruza **cuatro** rotaciones, empareja 245. Cruzar cuatro cuesta lo mismo que
cruzar una. Con lo cual el 11,3 % de §3.3 nunca fue «son muchos parches»: **es lo que cuesta una
sola rotación**, y ya está.

**4. Por qué, en un número.** Las semillas —formas que señalan a un solo mensaje en las dos
versiones— caen de ~750 a ~70 al rotar. Diez veces menos. El emparejador siembra con eso y luego
riega; con setenta semillas no hay riego que valga. Y no es que la rotación toque las formas: es que
un parche que rota es también un parche que cambia el protocolo, y las formas que pierde son
justamente las distintivas. Selección pura — una forma es única porque el mensaje tiene muchos
campos, y un mensaje de quince campos es el que más papeletas tiene de que le toquen uno.

**5. Encadenar es mucho PEOR que saltar de golpe.** Ésta era la hipótesis y queda refutada:

```
3.6.4.3 → 3.6.10.10        de un tirón   por la cadena
  parejas                          245             12
  de los 254 del emulador           20              1
```

La cadena es una intersección: sólo pasa lo que sobrevive a **todos** los saltos, y basta con que un
salto con rotación deje el 11 % para que cuatro seguidos no dejen nada. Se queda como instrumento de
medida, no como estrategia.

**Consecuencia.** Para un parche con rotación, la estructura da el 11 % y ahí se acaba: no es cosa
de afinar el emparejador ni de conseguir más versiones. Lo que queda es el índice del código y el
modelo, o sea las etapas 3 y 4. Dejan de ser una mejora y pasan a ser el camino.

### 2.7 Los clientes viejos siguen descargables

`protocolbuilder bajar 3.6.4.3 3.6.10.10 clientes` se trae los ocho clientes de la cadena.

- Ankama sirve todavía los manifiestos antiguos:
  `cytrus.cdn.ankama.com/dofus/releases/dofus3/windows/6.0_<versión>.manifest`. De las once versiones
  3.6.x sólo dan 403 la 3.6.8.9 y la 3.6.9.11, que deben de estar en otra rama.
- La **lista** de versiones no está en el `cytrus.json` vivo, que sólo trae las de hoy (3,5 KB).
  Está en [dofera/cytrus](https://github.com/dofera/cytrus), que lo fusiona cada minuto en vez de
  sobrescribirlo y conserva las ~200 versiones publicadas desde la 3.0.1.1.
- El manifiesto es un FlatBuffer de cinco tablas; el esquema está en
  [dofusdude/ankabuffer](https://github.com/dofusdude/ankabuffer) y la disposición del CDN en
  [ledouxm/cytrus-v6](https://github.com/ledouxm/cytrus-v6). `Cytrus.cs` lo lee a mano —cinco tablas
  no justifican arrastrar el generador de Google— y pide los paquetes **por rango**, así que de los
  ~12 GB de un cliente se bajan los **183 MB** que hacen falta: `GameAssembly.dll`,
  `global-metadata.dat` y `UnityPlayer.dll`.
- Verificado: los tres salen **idénticos byte a byte** al cliente instalado, y el volcado propio da
  las mismas 245 parejas que el de MelonLoader. Los clientes de la CDN son intercambiables con el
  instalado.

Un cliente bajado no trae `cpp2il_out` —lo deja MelonLoader al arrancar—, así que `Dumper.cs` lo
reconstruye con la misma biblioteca y el mismo formato. Se escribe el volcado entero (69 MB por
versión) y no sólo el ensamblado del protocolo: el lector necesita ver los hermanos para resolver
`IMessage`, y con la carpeta a medias ninguna clase parece un mensaje.

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

### 3.1b Los ensamblados de `cpp2il_out` están huecos

Esto invalidaba media arquitectura de la versión anterior de este documento, así que va medido.
Los `.dll` que deja Cpp2IL **no llevan el código de los métodos**: llevan las firmas y poco más.

| | métodos | con cuerpo de más de 16 bytes | IL total |
|---|---:|---:|---:|
| `cpp2il_out/Core.dll` | 110.811 | **0** | 214 KB |
| `cpp2il_out/Ankama.Dofus.Protocol.Game.dll` | 55.930 | **0** | 112 KB |

La mediana del cuerpo son dos bytes, que es un `ret`. El código del juego está compilado a máquina
dentro de `GameAssembly.dll` (110 MB) y sólo se ve levantándolo.

Probado también el formato de salida `dll_il_recovery` de Cpp2IL: no recupera nada en esta versión,
sale igual de hueco. Lo que **sí** funciona es usar `Cpp2IL.Core` como biblioteca: `Analyze()` sobre
un método devuelve su ISIL con las llamadas y los usos de metadatos ya resueltos, y cuesta 0,09 ms
por método —los 366.413 del cliente en veinte segundos—. Es lo que hace `CodeIndex`.

### 3.1c IL2CPP pliega direcciones, y eso fabrica evidencia

Vale la pena dejarlo escrito porque no se ve venir y ensucia sin dar la cara. Cuando se resuelve una
dirección nativa a un método, `MethodsByAddress` no devuelve un método: devuelve una **lista**.
IL2CPP comparte el código de los cuerpos idénticos y el de las instanciaciones genéricas. Medido en
3.6.10.10: de 261.768 direcciones, **24.227 las comparten dos o más métodos**, y una de ellas la
comparten 2.319.

Quedarse con el primero de la lista es echarlo a suertes, y cuando el sorteo cae en un mensaje se le
atribuye el avistamiento a quien no era. Resultado antes de arreglarlo: seis mensajes con el
expediente entero fabricado —`jzd` sólo lo tocaban `TMP_FontAsset` y `FontAsset`, `heo` sólo
`System.IO.FileStream`, `hgc` sólo clases de `Mono.CSharp`— y cada uno metiendo además aristas
falsas en el grafo, que luego se propagan a uno y dos saltos.

Arreglado con una regla de una línea —si la dirección no señala a uno solo, no señala— los números
del índice bajan y son de verdad: los mensajes con contexto legible pasan de 548 a **524**, y los
que llegan a un método con nombre legible de 121 a **102**. Diecinueve de aquellas anclas no
existían.

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

> **Precisión de §2.6.** Este 11,3 % se leía como «son muchos parches encadenados». No lo es: un
> solo parche con rotación da 223–261 parejas, casi lo mismo que este salto que cruza cuatro. El
> 11,3 % es el precio de **una** rotación. Y por eso partir el salto en saltos de un parche no
> arregla nada —medido: 12 parejas contra 245—.

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

### 4.1 Y la letra pequeña: ellos tienen el cliente sin ofuscar

La versión anterior de este documento sacaba de ahí que «la señal está en el código que usa el
mensaje». Es verdad para ellos y **no se traslada a nuestra situación**, y conviene saber por qué
antes de invertir una semana en copiarles.

En `FieldsOrderFromIL/Program.cs`, tal cual:

```csharp
bool obfu = true;
...
else
{
    pathGameAssembly = @"C:\Users\quent\Downloads\gameassemblyNonObfu\GameAssembly.dll";
    pathMetadata     = @"C:\Users\quent\Downloads\gameassemblyNonObfu\global-metadata.dat";
}
```

`gameassemblyNonObfu`. La *«ancienne version non obfusquée»* del prompt no es una versión vieja: es
**una compilación del cliente con los nombres puestos**. Lo que hacen es alinear el pseudocódigo de
la función ofuscada contra el de la misma función con nombres, y el modelo hace de emparejador.

Nosotros tenemos dos clientes y los dos están ofuscados. Sin ese lado limpio, pedirle al modelo que
lea pseudocódigo es pedirle que bautice a partir de nada. Por eso nuestra etapa 3 no persigue el
pseudocódigo campo a campo: persigue los nombres que se le escaparon al ofuscador (§2.4), que es lo
que sí tenemos.

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
   emulador nombra **303 opcodes** y de ellos **254 son mensajes del protocolo** —el resto son
   literales que no llegan al cable—. Mapear doscientos cincuenta es un problema acotado y
   verificable; dos mil, no. Están contados uno a uno, con fichero y línea, en
   `datos/opcodes_emulador_3.6.10.10.tsv`.
2. **Sabemos qué hace una buena parte de ésos.** `jsd` saca un actor, `kti` reparte chat, `jru`
   carga mapa: **99 con nombre y significado** en `datos/anclas_3.6.10.10.tsv`, y 292 con dirección
   y forma medida. Contra el juego real, no deducido. Son anclas de verificación y, de paso, los
   ejemplos con los que se calibra el modelo de la etapa 4.
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
┌─ 3. LEER EL CÓDIGO ──────────────────────────────────────────────────┐
│  protocolbuilder indexar <carpeta del cliente>                       │
│  Levanta los 366.413 métodos con Cpp2IL.Core y anota, por mensaje:   │
│    · quién lo toca, por firma, por llamada, por tipo o por dirección │
│    · los nombres que se le escaparon al ofuscador en esas clases     │
│    · las cadenas de texto de alrededor y los mensajes vecinos        │
│  HECHO. 20 segundos. 24,2 % de los mensajes con contexto legible,    │
│  y 94 de los 254 que usa el emulador.                                │
└──────────────────────────────────────────────────────────────────────┘
┌─ 4. DECIDIR (el LLM) ────────────────────────────────────────────────┐
│  protocolbuilder expediente / preguntar / evaluar                    │
│  Un expediente por mensaje: forma + de quién es campo + el código +  │
│  lo medido en las capturas + los mensajes que se manejan al lado.    │
│  Salida OBLIGATORIA: nombre + confianza + en qué se basa.            │
│  HECHO y medido a ciegas: ver §6.3.                                  │
└──────────────────────────────────────────────────────────────────────┘
┌─ 5. VERIFICAR (lo que nadie más puede) ──────────────────────────────┐
│  protocolbuilder evaluar <anclas> <propuestas>                       │
│  · Contra lo medido: puntúa cualquier tabla de propuestas contra las │
│    99 anclas con nombre, y desglosa el acierto POR CONFIANZA, que es │
│    lo que dice si se puede uno fiar. HECHO.                          │
│  · Contra las capturas: si la propuesta dice "esto sale al cruzar un  │
│    borde", tiene que aparecer en la captura de cruzar un borde.      │
│  · Contra el emulador: los 254 que implementamos tienen semántica     │
│    conocida y comportamiento comprobable con el cliente falso.       │
│  · Contra el banco de dos clientes (tools/two_on_a_map.py).          │
│  Los tres últimos, POR HACER; las piezas existen.                    │
└──────────────────────────────────────────────────────────────────────┘
```

### 6.2 Principios que no se negocian

- **Nada se acepta sin evidencia.** Una propuesta del LLM sin confianza y sin en-qué-se-basa no
  entra en la tabla. Es la diferencia entre un mapeo y una alucinación cara.
- **Cuatro niveles de confianza**, que es lo que declara el modelo y lo que desglosa `evaluar`:
  *segura* (la evidencia lo dice casi con todas las letras), *probable* (varias señales al mismo
  sitio y ninguna en contra), *posible* (encaja, pero encajarían otras dos o tres) y *ninguna*. Se
  publica cuántas hay de cada, y el acierto de cada nivel: sin eso, la confianza es decoración.
  Medido en §6.3: de *probable* para arriba se puede uno fiar, *posible* es echarlo a suertes.
- **El humano decide las dudosas.** Por eso hace falta interfaz (§7).
- **El mapeo es un fichero versionado**: `mapeo_<vieja>_a_<nueva>.txt`, en el repo, revisable en un
  diff.

### 6.3 Cuánto acierta la etapa 4, medido a ciegas

La prueba barata con respuesta conocida que pedía §9, hecha. Se cogen los 99 mensajes de los que se
sabe el nombre, se le tapa a cada uno **su propia ancla** —en el expediente y en la lista de
ejemplos, sólo la suya— y se contesta sin mirar `docs/opcodes.md` ni la tabla de anclas. Comprobado
en las transcripciones: ninguno de los diez contestadores abrió esos ficheros.

Se corrió **tres veces**. Los dos primeros barridos con el índice que aún atribuía mensajes a clases
que no los tocan (§3.1c); el tercero con el índice corregido, que es el que está en el repo:

```
                barrido 1   barrido 2   barrido 3
índice            con fallo   con fallo   corregido
sin nombre            69          69          68
dan nombre            30          30          31
aciertan              11          11          14        (45,2 %)  ·  sobre los 99: 14,1 %

  segura            4 de  5     4 de  5     4 de  4     (100 %)
  probable          4 de  7     3 de  8     3 de  6
  posible           3 de 18     4 de 17     7 de 21
```

El listón se apretó tres veces, y las tres porque estaba midiendo de más:

1. Perdonar una palabra a partir de tres daba 56,7 %, contando
   `AppearanceSlotSetRequestMessage` y `AppearanceSlotSetResultMessage` como el mismo mensaje.
2. Exigir que el nombre corto esté entero dentro del largo daba 43,3 %, y aún colaba `TitleSelect`
   como acierto de `TitleSelectRequestMessage`.
3. Perdonar sólo lo que no fuera «papel» —request, result, success…— daba 40,0 %, y colaba
   `AuthenticationTicketMessage` por `AuthenticationTicketAcceptedMessage`. Son `kqz` y `kra`, dos
   opcodes distintos de la propia tabla de anclas.

El que queda: **las mismas palabras, ni una más**, perdonando el orden, los plurales y el
«Message» del final. Perdonar lo que sobra obliga a mantener a mano una lista de qué palabras son
relleno, y esa lista hay que alargarla cada vez que aparece un «Accepted», un «End» o un «Storage».
Con igualdad no hay lista que mantener. Rechaza sinónimos legítimos —Teleport frente a Zaap— y por
tanto mide por lo bajo, que es como hay que medir.

Lo importante no es el 45,2 %: es que **la confianza está calibrada y es estable**. De los tres
barridos:

- **Arreglar el índice sí movió la aguja**: de 11 de 30 a 14 de 31. Con treinta muestras eso es
  indicio, no prueba, pero el mecanismo se entiende —desaparecieron diecinueve anclas fabricadas—.
- **Anotar en el expediente a cuántos mensajes toca cada clase NO movió nada**: fue lo único que
  cambió entre el barrido 1 y el 2, y salieron idénticos. Conviene apuntarlo en vez de apuntarse el
  arreglo.
- **«Segura» no falla en ninguno de los tres.**
- **63 de los 69 silencios coinciden** entre los dos primeros. No se calla al azar: se calla ante
  los mismos mensajes.

Eso convierte la salida en algo utilizable con una regla sencilla: lo seguro entra, lo probable
entra con revisión, lo posible es una pista para que la mire una persona, y las dos terceras partes
que se callan no ensucian nada.

Y lo que se calla, se calla por buenas razones. `hid` es un `int32` en el campo 1 y nada más;
ninguna cantidad de modelo va a sacar de ahí que es el título que lleva puesto el personaje. Eso
sólo lo dice una captura.

Los que falla son instructivos. En los dos primeros barridos, `itg` salió `PresetsMessage` en vez de
`ShortcutBarContentMessage` y `lyt` lo mismo en vez de `OutfitsListMessage`: las dos veces el
culpable es el mismo contexto —la clase `eqq`, que conserva `PresetListEventWhenCharacterInfo`—
pegado a dos mensajes distintos. En el segundo barrido el expediente ya avisaba de que `eqq` toca a
76 mensajes, y `itg` siguió saliendo `PresetsMessage` con confianza «probable». Avisar no basta:
hay que **descontar** la pista, no anotarla.

Y hay un fallo que se repite en los tres y no se arregla con más código: `jrw` sale
`GameMapMovementMessage` cuando es `GameMapMovementRequestMessage`, e `iwo` sale
`InteractiveUseWithParamRequestMessage` cuando es `InteractiveUseRequestMessage`. En los dos casos
el mensaje es el correcto y la variante no: la ida en vez de la vuelta, la versión con parámetro en
vez de la simple. La forma no dice quién habla. **La dirección la dicen las capturas y sólo las
capturas**, y está en la tabla de anclas para los 292 que se han visto pasar; el expediente ya la
enseña cuando la tiene, y por eso los mensajes anclados no fallan así.

**Una advertencia sobre estos 99.** Son los mensajes mejor documentados que hay, y por eso son los
que se pueden medir; también son los que más contexto tienen. El 45,2 % es lo que da la tubería
sobre su mejor material, no lo que va a dar sobre los otros 2.070.

### 6.4 Consecuencia para el emulador — empezar YA

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
  Program.cs                          los comandos
  AssemblyReader.cs                   lee ensamblados sin ejecutarlos (MetadataLoadContext)
  ProtoWriter.cs                      reconstruye el .proto de las clases
  Matcher.cs                          huellas, riego y arrastre
  Shuffle.cs                          baraja nombres para medir el techo
  DescriptorExtractor.cs              el camino muerto del descriptor serializado (§3.1)
  ClientReader.cs        NUEVO        abre el cliente de verdad con Cpp2IL.Core
  CodeIndex.cs           NUEVO        etapa 3: qué código toca cada mensaje
  Dossier.cs             NUEVO        etapa 4: el expediente de un mensaje
  Llm.cs                 NUEVO        el modelo, con caché, reintentos y límite
  Cytrus.cs              NUEVO        baja un cliente viejo de la CDN, sólo lo que hace falta
  Dumper.cs              NUEVO        reconstruye el cpp2il_out que un cliente bajado no trae
  Relay.cs               NUEVO        recorre la cadena parche a parche y mide cada salto (§2.6)

datos/protocolo_3.6.10.10.proto       2.169 mensajes con números y tipos
datos/protocolo_conexion_3.6.10.10.proto
datos/protocolo_3.6.4.3.proto         la versión vieja
datos/mapeo_3.6.4.3_a_3.6.10.10.txt   245 parejas + ambiguos + sin pareja
datos/indice_3.6.10.10.json      NUEVO   la etapa 3 ya corrida (1,4 MB)
datos/anclas_3.6.10.10.tsv       NUEVO   lo que se sabe de cada opcode, y de dónde
datos/opcodes_emulador_3.6.10.10.tsv  NUEVO   los que el emulador usa, con fichero y línea
datos/propuestas_ciegas_3.6.10.10.tsv NUEVO   la medición de §6.3

scratchpad/snowbot/                   el código descompilado de Snowbot (fuera del repo)
```

Comandos:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- proto <dll> <salida.proto>
dotnet run --project Jondo.Unity.ProtocolBuilder -- emparejar <dll vieja> <dll nueva> <salida.txt>
dotnet run --project Jondo.Unity.ProtocolBuilder -- probar <dll> [opcodes del emulador.tsv]
dotnet run --project Jondo.Unity.ProtocolBuilder -- mirar <dll> [tipo]
```

Los clientes de otras versiones, y la medida de §2.6:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- bajar --lista
dotnet run --project Jondo.Unity.ProtocolBuilder -- bajar 3.6.4.3 3.6.10.10 clientes
dotnet run --project Jondo.Unity.ProtocolBuilder -- cadena clientes datos/opcodes_emulador_3.6.10.10.tsv
```

`bajar` son 183 MB por versión y unos segundos; `cadena` tarda unos minutos la primera vez porque
reconstruye el `cpp2il_out` de cada cliente, y segundos las siguientes porque ya está escrito.

La tubería nueva, de principio a fin:

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- indexar "C:\Jondo 3.6.10.10\Cliente 3.6.10.10" datos/indice_3.6.10.10.json
```

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- expediente <dll> datos/indice_3.6.10.10.json datos/anclas_3.6.10.10.tsv jss
```

```bash
dotnet run --project Jondo.Unity.ProtocolBuilder -- preguntar <dll> datos/indice_3.6.10.10.json datos/anclas_3.6.10.10.tsv datos/propuestas.tsv --evaluar
```

`preguntar` necesita `JONDO_LLM_KEY` o `ANTHROPIC_API_KEY` en el entorno; el modelo y la dirección
salen de `JONDO_LLM_MODEL` y `JONDO_LLM_URL`, así que apuntarlo a otro proveedor no toca código. Sin
clave, `expediente --todos` vuelca los 2.169 expedientes para contestarlos por otro camino, y
`evaluar` puntúa la tabla venga de donde venga.

---

## 9. Por dónde seguir

Los puntos 2 y 3 de la lista anterior ya están: la etapa 3 corre en veinte segundos y la etapa 4
está medida a ciegas (§6.3). Lo que queda, por orden de valor:

0. **Mirar primero si ha rotado**, que es gratis y sale bien casi la mitad de las veces (§2.6). Se
   comparan los dos juegos de nombres: si están los 2.169 del viejo en el nuevo, el mapeo es la
   identidad y no hay nada que resolver, ni con el emparejador ni con el modelo. Pasó en 3 de los 7
   parches medidos. Hoy la aplicación no lo comprueba: arranca el emparejamiento entero igual, y en
   esos casos se pasa un minuto reconstruyendo algo que ya sabía. Es media hora de trabajo y quita
   el problema entero de en medio casi la mitad de los parches.
1. **La capa `Op.` en el emulador** (§6.4). Sigue siendo lo primero y lo único que hay que tener
   hecho ANTES del parche. No depende de nada de lo demás, y sin ella el mapeo no sirve: hay 496
   literales de tres letras repartidos por 303 opcodes distintos en `Jondo.Unity.Launcher`, y
   editarlos a mano el día del parche no es un plan.
2. **Meter las anclas en el emparejador como semillas, pero por lo que de verdad dan.** Está
   probado en un experimento aparte: se le inyectan K parejas ciertas a `Matcher.Match` y se mide.
   **No hay cascada**: cada ancla aporta entre 0,04 y 0,27 parejas nuevas según la deriva, o sea que
   ochocientas anclas no arreglan el problema de cobertura. Lo que sí hacen, y mucho, es acertar:
   con una deriva del 40 %, los emparejamientos EQUIVOCADOS bajan de 94 a 35. La razón es que el
   arrastre por los padres ya está saturado —las semillas que faltan no son pocas, son las que no
   existen— pero una pareja falsa arrastra a otra detrás, y las anclas cortan esas cadenas. Vale la
   pena por precisión, no por cobertura, y el punto de inyección es `Matcher.cs`, justo después de
   `var signB = Signatures(b, rounds);`.
3. **Descontar el contexto compartido, no anotarlo.** Anotarlo ya está hecho —el expediente dice
   «eqq (toca a 76 mensajes)»— y está medido que **no sirve**: `itg` siguió saliendo
   `PresetsMessage` con confianza «probable». Lo que falta es que el propio `CodeIndex` deje de
   ofrecer como contexto las clases que tocan a medio protocolo, o que las ponga las últimas y
   marcadas. Un umbral simple —fuera las que pasen de diez— es lo primero que hay que probar, y se
   mide repitiendo el barrido de §6.3.
4. **Los campos `map<>` no cuentan nunca.** `ProtoWriter.Describe` los escribe como
   `map<string, hqu>` con el nombre rotado dentro, y ni `Matcher.Kind` ni `Matcher.Similar` saben
   leer eso: son 41 campos en 25 mensajes de 3.6.10.10 que hoy no suman parecido aunque coincidan.
   Es el arreglo más barato que queda en el emparejador.
5. **Anclas por enumerados**, con expectativas ajustadas: 496 de los 550 enumerados son
   simplemente `0..N-1`, así que su huella por valores no dice más que cuántos valores tienen.
   Emparejándolos sólo por eso salen **13 anclas deterministas**, no doscientas.
6. **Que `proto` y `emparejar` protesten con el ensamblado equivocado.** Apuntándolos a
   `MelonLoader/Il2CppAssemblies/Il2Cpp*.dll` en vez de a `cpp2il_out/` devuelven cero mensajes y
   siguen adelante imprimiendo «0 mensajes» y «NaN %» como si nada. Los buenos son los de
   `cpp2il_out`.
5. La interfaz (§7), cuando haya propuestas que revisar de verdad.

Y lo que no cambia nunca: **seguir capturando**. De los 99 mensajes de §6.3, los 69 que el modelo
se calla se callan porque su forma no dice nada —`hid` es un `int32` y ya está—. Eso no lo arregla
ningún modelo ni ningún análisis del binario: lo arregla haber grabado el momento en que pasó.
