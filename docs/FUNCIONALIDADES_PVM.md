# Sistema de combate PvM

Estado del combate jugador contra monstruos. Todo lo que hay aquí se ha construido
contrastándolo con capturas de red del juego oficial y con los datos del propio cliente;
donde no ha habido datos que lo respalden, está señalado como tal en vez de rellenarse a ojo.

Cliente: **Dofus 3.6.4.3** · Servidor: **.NET 10**

---

## Cómo transcurre un combate

1. **Inicio.** Al pulsar sobre un grupo de monstruos en roleplay, el servidor crea la
   instancia de combate, resuelve el mapa de arena y cambia el contexto del cliente.
2. **Colocación.** 45 segundos. Casillas azules para el jugador y rojas para los monstruos;
   se puede cambiar de casilla dentro de las azules antes de pulsar *Listo*.
3. **Turnos.** Orden por iniciativa, alternando equipos. 30 segundos por turno, con paso
   automático si se agota.
4. **Acciones.** Movimiento casilla a casilla, lanzamiento de hechizos y golpe con arma.
5. **Fin.** Pantalla de victoria o derrota con experiencia, kamas y botín; vuelta a roleplay
   y reposición del grupo de monstruos derrotado.

---

## Funciona

### Inicio y escenario

- **Arena de combate propia.** Cada mapa de roleplay tiene su mapa de arena asociado. Se
  resuelve emparejando por desplazamiento del identificador dentro de la misma subárea
  (+4, +6, +2… según la zona), con reparto determinista de reserva.
- **Cambio de contexto.** `kkp` destruye el contexto y `kkm` crea el nuevo (vacío = roleplay,
  1 = combate). Al terminar se restaura la secuencia completa `lxs · kkp · kkm · krb · joh · lor`.
- **Fase de colocación** con posiciones posibles por equipo y cambio de casilla.

### Geometría

- **Rejilla de cuatro vecinas.** Un paso solo puede ir a una casilla que comparta arista:
  desplazamientos `-15/-14/+13/+14` en filas pares y `-14/-13/+14/+15` en impares. Los
  desplazamientos `+1` y `+28` **no** son un paso, son dos.
- **Distancia de combate** = |dx| + |dy| sobre las coordenadas convertidas. Es la que usa el
  juego para el alcance de los hechizos: una diagonal cuenta como 2.
- **Línea de visión** a partir del campo `los` de los datos de mapa del cliente, extraído
  para 17.222 mapas. Recorre el segmento entre los centros y corta si atraviesa una casilla
  opaca; en las esquinas basta con que una de las candidatas deje pasar.
- Autocomprobación al arrancar que falla si alguna casilla acaba con más de cuatro vecinas.

### Turnos

- Protocolo de saludo completo: `juu` (espera) → `jwe` (confirmación del cliente) → `jut`
  (turno iniciado) → `jwl` (jugable).
- **Temporizador de 30 segundos** con paso de turno forzado desde el servidor.
- **PA y PM se reponen** al empezar cada turno. Los campos de variación son opcionales y el
  cliente distingue "presente con valor cero" de "ausente": al restablecer se manda
  únicamente el máximo.

### Movimiento

- Camino casilla a casilla, expandiendo los vértices que manda el cliente por el camino más
  corto real, respetando obstáculos y luchadores.
- Consumo de PM correcto: un vértice a dos pasos gasta dos PM.
- Transitabilidad **de combate** (`mov` y `nonWalkableDuringFight`), no la de roleplay.

### Hechizos

- **Lista por nivel** desde la base de datos: libro de hechizos (`hmd`), barra de accesos
  directos (`itp`) y lista de combate (`jvn`), las tres con la misma fuente.
- **Coste de PA, alcance mínimo y máximo y línea de visión** validados en el servidor.
- **Límites de lanzamiento**: por turno y por objetivo, según declara cada hechizo.
- **Daño por elemento** con la característica correspondiente, la potencia del equipo y las
  resistencias del objetivo.
- **Golpes críticos.** Probabilidad = la del hechizo más el crítico del equipo (la Flecha
  Helada trae 10 y el Dofus Turquesa otros 10, de ahí el 20 % que muestra la descripción).
  En crítico se usa el rango de daño crítico del propio hechizo, no un multiplicador.
- **Efectos sobre características**: retirada de PA, de PM y de alcance, con su número
  flotante y su línea en el registro de combate.
- **Bonificación de daño acumulable** (efecto 293): la Flecha Helada se deja +4 de daño base
  durante 3 turnos y relanzarla renueva el plazo en vez de acumular.
- **Golpe con arma**: coste de PA, alcance y crítico salen de la ficha del arma; el daño, de
  los efectos tirados de ese ejemplar concreto.

### Inteligencia de los monstruos

- **Hechizos reales** de cada monstruo, con su grado, leídos de `MonsterTemplates`
  (`spells` y `spellGrades`).
- **Selección de objetivo**: primero el de menos vida, luego el de menor porcentaje, luego el
  más aislado, luego el más cercano.
- **Ataque si está a tiro**; si no, se mueve lo mínimo necesario para ponerse a rango y con
  línea de visión.
- **Repite el hechizo** mientras le queden PA y no supere su límite por turno.
- **Huida por debajo del 30 % de vida**: ataca primero y luego se aleja lo máximo posible.
- Reparte los mismos efectos que el jugador: daño, empuje, retirada de puntos.

### Estadísticas del personaje

- Vida máxima con las bonificaciones del equipo.
- Iniciativa = características elementales + equipo.
- Potencia (característica 25) y crítico (18) del equipo, aplicados al cálculo de daño.

### Fin de combate

- **Pantalla de victoria y de derrota** con la estructura real: una entrada por luchador,
  botín, kamas y bloque de progreso de experiencia.
- **Experiencia** = la que da cada monstruo derrotado (`gradeXp` de su ficha), la misma que
  muestra el cliente al pasar el ratón por el grupo. Se persiste y sube de nivel usando la
  tabla real del cliente (1.889 niveles; nivel 50 = 5.350.000 y nivel 51 = 5.860.000).
- **Subida de nivel**: sube el nivel, otorga 5 puntos de característica por nivel y rehace el
  libro y la barra de hechizos por si se desbloquea alguno. La pantalla la saca el cliente
  solo al ver el nivel nuevo.
- **Botín** tirado de la tabla real de cada monstruo con sus probabilidades por grado. Se
  añade al inventario y se reenvía la bolsa.
- **Muerte de un luchador** notificada al cliente, para que caiga y cuente como derrotado.
- **Vuelta a roleplay** con la ficha de características refrescada y **reposición** del grupo
  derrotado por otro generado al azar, sin tocar los que quedaban en el mapa.

---

## A medias

Cosas implementadas por dentro pero que no se ven bien, casi siempre por no tener una captura
del juego oficial que enseñe cómo lo codifica el cliente.

### Empuje

El destino **se calcula y se aplica**: sigue la recta que va del lanzador al objetivo y se
detiene en el primer obstáculo, así que el monstruo acaba en la casilla correcta.

Lo que falta: **la animación**. Se reutiliza el mensaje de movimiento normal, con lo que el
objetivo "camina" hacia atrás en vez de salir despedido. De los identificadores de acción que
he podido identificar en las capturas —300 lanzar, 129 puntos de movimiento, 102 puntos de
acción, 99 daño, 103 muerte— ninguno corresponde a un desplazamiento forzado.

Tampoco se calcula el **daño por empuje** (el que se recibe al chocar contra un obstáculo o
contra otro luchador).

*Qué haría falta:* una captura del juego oficial usando un hechizo que empuje.

### Golpe con arma

El daño se aplica y se ve, pero **no se manda la acción de lanzamiento**, porque no tengo
ninguna captura de un ataque con arma. Falta la animación del espadazo.

### Lista de embrujos y estados

Las retiradas de alcance y de puntos se aplican y se notan en el juego, pero **no aparecen en
el panel de efectos** del luchador. El mensaje que alimenta esa lista no está en ninguna de
las capturas disponibles.

### Estadísticas de la pantalla final

Daños infligidos, recibidos, bloqueados, escudos, curas y enemigos derrotados **salen todos a
cero**. No viajan en el mensaje de fin de combate ni en el que le sigue; deben ir en otro sitio
que aún no he localizado.

### Contador de kamas del inventario

Las kamas se ganan, se guardan y se muestran bien en la pantalla de fin de combate, pero **el
contador del inventario no se actualiza**: el mensaje que usábamos para eso no aparece en
ninguna captura, así que está inventado.

*Qué haría falta:* una captura donde cambien las kamas fuera de combate (vender algo, cobrar
una misión).

### Hechizos deshabilitados en combate

Con el personaje a nivel 50, el libro de hechizos muestra los 14 desbloqueados y la barra los
dibuja todos, pero **en combate solo se pueden lanzar los cuatro de nivel mínimo 1**. Al pulsar
uno de los otros el cliente **no envía nada al servidor**, así que lo bloquea él por su cuenta.

Descartado hasta ahora: no es el nivel (el libro los da por desbloqueados hasta Ojo de Topo,
que es de nivel 50), no es el aprendizaje, no son los PA (los bloqueados cuestan 1-3, menos
que los que sí funcionan) y no son los tiempos de recarga (todos a cero). Sin resolver.

---

## No implementado

- **Tirada de esquiva** al retirar PA o PM: el efecto se aplica entero, sin comparar la
  retirada del lanzador con la esquiva del objetivo.
- **Zonas de efecto.** Todos los hechizos se resuelven sobre un único objetivo; no se leen las
  formas de área (`zoneDescr`).
- **Invocaciones, curas, escudos y estados.**
- **Placaje y huida** (perder PM al escapar de un cuerpo a cuerpo).
- **Resistencias del jugador.** Están a cero: no se leen del equipo, así que se recibe más
  daño del que tocaría.
- **PA y PM base fijos** en 6 y 3; no se suman los del equipo.
- **Modificador de experiencia por diferencia de nivel.** Se entrega la experiencia base del
  monstruo, sin el ajuste que aplica el juego cuando el personaje va muy por encima.
- **Prospección**: el botín usa la probabilidad base, equivalente a 100 de prospección.
- **Botín condicional**: las entradas con criterios (de misión o de logro) se descartan,
  porque el lenguaje de criterios no está implementado.
- **Combates de varios jugadores y PvP.**
- **Barra de experiencia de la pantalla final** en la parte de multiplicadores: los campos que
  no supe identificar se omiten en vez de rellenarse.

---

## Sobre el método

Dos reglas que se han seguido en todo el sistema:

**Los datos salen del juego, no del código.** Hechizos, monstruos, botín, experiencia,
transitabilidad y línea de visión se leen de la base de datos o se extraen de los *bundles* del
cliente. Se han ido eliminando las listas escritas a mano que quedaban de las capturas de
referencia: el libro de hechizos, la barra, los hechizos de los monstruos y las tablas de
experiencia estaban fijados a los valores de un personaje concreto.

**Un mensaje mal formado es peor que ninguno.** Cuando la estructura de un mensaje no se ha
podido deducir de una captura, no se manda. Es preferible que falte una animación a que el
cliente reciba algo que no sabe interpretar.

### Herramientas de extracción

```
extract_fight_cells.py      → casillas de combate y opacas de 17.222 mapas
extract_character_xp.py     → tabla de experiencia por nivel
extract_all_map_walkable.py → casillas transitables en roleplay
```

Necesitan Python con `UnityPy` y una copia del cliente instalada.
