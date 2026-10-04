# Logros

Ganarlos, verlos en la ventana y cobrarlos. Los datos salen del volcado del cliente y el protocolo
de las capturas; lo que no está medido se dice que no lo está.

---

## 1. Qué es un logro

```
logro       nombre, categoría, nivel, puntos, objetivos, recompensas
objetivo    un criterio. TODOS tienen que cumplirse.
recompensa  objetos con su cantidad, experiencia y kamas como RATIOS, ornamentos,
            títulos, emotes, hechizos, puntos de gremio — y su propio criterio,
            porque un logro puede pagar distinto a distinta gente.
```

Los criterios están escritos en **el mismo lenguaje** que la condición de arranque de una misión,
paréntesis y `!` incluidos, así que los lee el mismo evaluador.

| | |
|---|---|
| Logros | **2.780** |
| Objetivos | **8.946** |
| Recompensas | **6.394** |
| Categorías | 134 |
| Objetivos que el cliente nombra y no describe | **322 logros**, 272 atados |
| Logros con todos sus objetivos en términos que este motor juzga | **2.172** |

---

## 2. El protocolo

Todo sale de las capturas, y donde las capturas callan, del propio cliente: la clase `epj` de Core
(cuyo único nombre legible es `DelayedClearAchievements`) recibe exactamente estos mensajes y se los
pasa a la ventana de logros con nombres que el ofuscador no tocó — `OnAchievementList`,
`OnAchievementFinished`, `OnAchievementRewardSuccess`, `OnAchievementDetailedList`,
`OnAchievementAlmostFinishedDetailedList`, `OnAchievementDetails`. La clase `epf` construye las
cinco peticiones.

```
S->C  mft  {1 (rep): {2: personaje, 3: logro}}              la lista, al entrar al mundo
S->C  mfu  {1: {1: nivel del PERSONAJE, 2: personaje, 3: logro}}   conseguido
C->S  mga  {1: logro}  o {1: -1}                            «págame»
S->C  mfs  {2: 1, 4: logro}                                 pagado

C->S  mfe  (vacío)          -> S->C  mgb  los más cerca de conseguirse
C->S  mfp  (vacío)          -> S->C  mfx  (raíz 3, vacío)
C->S  mff  {1: categoría}   -> S->C  mfo  la categoría entera, con el progreso de cada objetivo
C->S  mfm  {1: logro}       -> S->C  mfg  un logro             INFERIDO: sin captura
```

**`mfs` no va con `mfu`.** En las nueve tramas capturadas llega siempre detrás de un `mga` y de la
ficha que el cobro cambió. Antes se mandaba al conseguir; ya no.

**La ventana**, en `Chats\usando todos los chats`, tramas 103 a 108: suben `mfe`, `mfp` y
`mff {40}` y bajan `mgb`, `mfx` y `mfo` en ese orden. Se contesta cada petición con la respuesta que
ocupa su mismo puesto. El `mfo` de la categoría 40 lista sus **23 logros, todos**, conseguidos o no,
los pendientes primero. Cada objetivo lleva `f1` el objetivo, `f2` sobre cuánto, y `f4` cuánto lleva
**incluso a cero** (`20 00`) mientras no está hecho; uno hecho no lleva `f4`. «Ez>1,99» viaja como 91
de 100: el máximo es el umbral más uno. El `mgb` son seis logros empezados de seis categorías; cómo
elige el servidor real esos seis no está en la captura, y aquí son los seis más avanzados.

**La lista al entrar** no lleva nivel en ninguna de las 5.198 entradas de los siete `mft`
capturados, porque esas cuentas lo tenían todo cobrado. Un logro conseguido y sin cobrar se manda como lo manda `mfu`, con el
nivel en `f1`: INFERIDO, es lo único que distingue a los dos en ese mensaje.

**El cobro**, medido en el tutorial y en los cinco `mga` capturados, por logro:

```
ivf {kamas}, lqn 45 «has ganado N kamas»     si paga kamas
kua {nivel}                                   si sube de nivel
kub (la ficha), kuf {1: experiencia ganada}
iua ...                                       los objetos
khi {1: actitud}                              las actitudes
mfs {2: 1, 4: logro}
```

`kuf` lleva la experiencia ganada: en doce tramas su `f1` es exactamente lo que subió la experiencia
del `kub` anterior (545 en la ruta de Pandala, 109 en la de las ratas). Tras un combate viaja vacío.

---

## 3. Qué los hace avanzar

El catálogo archiva cada logro bajo todo lo que mencionan sus objetivos (`(operador, clave)`), y
cada cosa que pasa pregunta sólo por lo suyo:

| Pasa | Mira | Operadores |
|---|---|---|
| Acabar una misión | los que esperan esa misión | `Qf` `Qc` `QF` `QQ` |
| Llegar a un mapa | la zona, si es nueva; el nivel, si cambió; la bolsa; el mapa | `Xs` `PL` `PO` `Pm` |
| Ganar un combate | cada monstruo vencido; con un reto cumplido, otra vez; los retos | `EM` `Ef` `EH` |
| Fabricar | lo fabricado y los oficios | `Xc` `Xj` |
| Subir un oficio | los oficios | `Xj` |
| Entrar al mundo | todo, una vez | |

`Xs`, `Xj` y `Xc` son claves nuestras, no del cliente: los logros que las usan son los que el cliente
no describe. Lo del combate se deja apartado y se mira al volver al mapa, para no meter un `mfu` en
medio del final del combate, donde ninguna captura lo tiene.

Los contadores que nada más guarda se guardan en `CharacterAchievementCounters` (monstruos vencidos,
vencidos con reto, zonas pisadas, objetos fabricados, y el día de la última ofrenda del Almanax).

### Los 322 que el cliente no describe

Sus objetivos no están en la tabla del cliente: el servidor los juzga por su cuenta. Lo que piden
está en el nombre y la descripción del propio logro, y `tools/extract_achievement_links.py` lo lee:

| | | |
|---|---|---|
| explorar una zona | 224 | el logro se llama como la subzona. **Medido**: los 17 que ganan las capturas saltan al entrar en la subzona de ese nombre |
| vencer a un monstruo | 18 | los dopeuls |
| acabar una misión | 13 | «Terminar la misión: …», por el nombre |
| alcanzar un nivel | 11 | «Alcanzar el nivel N» |
| nivel N en M oficios | 5 | |
| fabricar | 1 | «Fabricar 1 objeto»; el tutorial lo gana justo tras el anillo |

Quedan 50 sin atar, y no se ganan: los niveles de Temporis, criar monturas, comer dulces, romper un
miaumiau, abrir un regalo, «Criptas» (ninguna zona se llama así), el dopeul feca (dos monstruos con
ese nombre).

### La regla al revés que la de las misiones

En la condición de arranque de una misión, **lo que no se sabe juzgar se deja pasar**. En un logro
es **al revés: lo que no se sabe juzgar NO se concede.** Pero se juzga con tres respuestas, así que
un término desconocido que un `|` ya ha hecho irrelevante no bloquea nada: `SC=0|(SC=5&ST!7)` es
verdad en un servidor clásico sea lo que sea `ST`.

`SC` es el tipo de servidor de la tabla del cliente (`server_game_types`): 0 «Clásico», 5
«Temporis». Este servidor es clásico, así que los logros de Temporis no se conceden.

### La cascada

`OA` es «logro obtenido» y el operador más común: 2.157 objetivos. El 8520 «Con bases sólidas» es
`(OA=8518)` y `(OA=8519)`. Conseguir uno puede conseguir otros en la misma jugada; también los de
puntos (`Oa>999`). Se recorre en anchura con tope.

---

## 4. Lo que se paga

**La experiencia y los kamas, con la fórmula del propio cliente.** El cliente enseña la recompensa
antes de darla, así que la fórmula está en su código: clase `lg` de Core, `nza` (experiencia) y
`nzd` (kamas), las mismas para misiones y logros. Desensamblada, con las constantes leídas de
GameAssembly.dll:

```
fijo(L)   = L · trunc((100 + 2L)²) / 20 · duración · ratio
L ≤ A     experiencia = fijo(L)
L > A     experiencia = 0,3 · fijo(A) + 0,7 · fijo(min(L, trunc(1,5 · A)))
luego     · (1 + bonus/100), · límite
kamas     = trunc(nivel² + 20·nivel − 20) · ratio
```

L es el nivel del personaje (tope 200), A el del logro o el óptimo del paso, la duración 1 en un
logro. Contra las nueve cobranzas de las capturas sale **exacto**, con un 5 % de bonus que tenían
esos personajes (y 110 % el de las ratas) — el bonus es del personaje y aquí no se modela. Dos
diferencias de redondeo del servidor frente al cliente: suma las dos mitades antes de truncar, y
multiplica el bonus con más precisión que un float. Con el redondeo del cliente saldrían 26 y 544
donde las capturas dicen 27 y 545.

**Las recompensas con condición.** 1.060 dicen `Ob!<el propio logro>`: no puede ser «no lo tiene»,
porque quien cobra lo tiene, así que se lee como «no se le ha pagado todavía» (INFERIDO; en Ankama es
la cuenta, aquí el personaje). Se juzga antes de marcarlo cobrado. `PO!10207` es «no tiene ese
objeto». Lo que no se sabe juzgar no se paga.

**Los objetos**, a la bolsa. **Las actitudes**, aprendidas con `khi`. **Títulos y ornamentos** se
anotan y no se manda nada: todo personaje tiene ya los 539 y los 167 ofrecidos. **Los puntos de
gremio**, no.

---

## 5. Lo que se guarda

```sql
CharacterAchievements        (CharacterId, AchievementId, Claimed)
CharacterAchievementCounters (CharacterId, Kind, Key, Count)
```

Las crea el servidor al arrancar y también quien las lee o escribe, porque una base recién sacada de
`datos/world.zip` no las trae.
