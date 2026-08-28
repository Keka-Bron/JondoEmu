# Logros

Ganarlos y cobrarlos. Todo medido: los datos salen del volcado del cliente y el protocolo de las
capturas.

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
| Logros que se ganan **acabando misiones** | **257** |
| Recompensas que dan un objeto | 2.137 |

Estaban los cuatro ficheros en `dofus3_data/` desde siempre y **nadie los había leído nunca**. Mismo
caso que las misiones antes de esta semana: el dato existía y no había extractor.

---

## 2. Cómo se sabe que el protocolo es éste

Tres opcodes, sacados de las capturas:

```
S->C  mfu  {1:{1: nivel del PERSONAJE, 2: personaje, 3: logro}}   conseguido
S->C  mfs  {2: 1, 4: logro}                                        conseguido / cobrado
C->S  mga  {1: logro}   o {1: -1}                                  «págame»
```

Los 8 ids que lleva `mfs` y los 20 de `mfu` son logros reales. Pero eso solo no bastaría, porque los
rangos de ids se solapan; **lo que lo zanja es que el significado cuadra**:

- En la captura del tutorial sale el **8518 «Primer tiempo»**, cuyo objetivo es literalmente
  `(Qf=2511)` — y 2511 es «Primeras armas», la misión que esa misma captura arranca.
- En la de la ruta larga salen «Landas de Cania» y «Bosque de Litneg» mientras se cruzan.
- En la del gremio, «Recibidor de gremio amakneano» al entrar.

**El primer campo de `mfu` es el nivel del personaje, no el del logro.** Vale 1 y luego 2 en el
tutorial, donde el jugador estaba subiendo, y 200 quince veces en la ruta larga, para logros cuyos
niveles propios son 30, 50, 110 y 140.

---

## 3. Conseguir y cobrar son dos cosas

La captura `Logros\aceptar recompensas de un logro` es un jugador pulsando el botón de cobrar: sube
un `mga {1: 8990}` y **sólo entonces** llega la recompensa.

Así que la tabla guarda las dos cosas por separado:

```sql
CREATE TABLE CharacterAchievements (
    CharacterId   INTEGER NOT NULL,
    AchievementId INTEGER NOT NULL,
    Claimed       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (CharacterId, AchievementId)
);
```

Un motor que pagase al conseguir sería otro juego, y uno que perdiera la distinción pagaría dos
veces en cada login.

---

## 4. La regla al revés que la de las misiones

En la condición de arranque de una misión, **lo que no se sabe juzgar se deja pasar**: rechazarlo
dejaría el 53% de las misiones fuera del alcance de cualquiera.

En un logro es **al revés: lo que no se sabe juzgar NO se concede.** Dejar pasar un término
ilegible allí cuesta una misión que el jugador iba a tener igual; dejarlo pasar aquí regala una
insignia y sus objetos por nada. «Mata 500 jalatós» no se cumple porque este motor no sepa contar
jalatós.

Un logro **sin ningún objetivo tampoco se concede**. Hay 322, y tratarlos como cumplidos los
repartiría todos en el primer login.

---

## 5. Lo que puede conceder hoy, y lo que no

**504 de los 2.780 (18%).** Son los que dependen sólo de misiones acabadas, de otros logros, del
nivel o del mapa.

Lo que los bloquea, por número de logros afectados:

| | | |
|---|---|---|
| `EH` | 647 | |
| `Ef` | 406 | |
| `BI` | 381 | |
| `SC` | 369 | |
| `Sc` | 255 | |
| `EM` | 141 | |

Son explorar, matar familias de monstruos, retos de mazmorra y niveles de oficio. Cada uno es un
enganche en su sistema, y dos de ellos ya tienen dónde engancharse: el fin de combate y los oficios.

Un ejemplo de lo que eso cuesta: el 8518 del tutorial **no se concede**, porque además de la misión
pide `(BI=1)`, la primera parte del tutorial, que no se sabe juzgar.

---

## 6. La cascada

`OA` es «logro obtenido», y es el operador más común del catálogo: 2.157 objetivos lo usan. El 8520
«Con bases sólidas» es exactamente `(OA=8518)` y `(OA=8519)` — una insignia por tener dos insignias.

Así que conseguir una puede conseguir otras en la misma jugada, y ésas otras. La cascada se recorre
en anchura con un tope de 16 vueltas — la cadena real más profunda es de dos, y el tope está por si
una regeneración futura trajera un criterio que se pidiera a sí mismo.

---

## 7. Lo que se paga y lo que no

**Los objetos sí**, con su cantidad exacta, a la bolsa y avisando al cliente.

**La experiencia y los kamas no**, y es el mismo agujero que en las misiones: son **ratios**,
multiplicadores sobre una base que este emulador no tiene. Se escriben en el registro y no se pagan,
porque inventar la fórmula pondría un número que parece bueno y no lo es.

**Títulos, ornamentos, emotes y puntos de gremio** tampoco: son recompensas reales que este servidor
no tiene dónde meter todavía. Se nombran en el registro en vez de desaparecer en silencio.
