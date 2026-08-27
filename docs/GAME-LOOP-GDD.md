# GDD — El loop (versión simple)

> Documento de diseño, no técnico. Describe **qué hace el jugador y por qué**, con el
> scope mínimo para que el juego dé horas ya. No sustituye a ningún ADR ni contradice
> ninguno: los sistemas citados existen y están verificados en código (ver
> `docs/web/compendio.html` §01 para la evidencia archivo:línea).
> Fecha: 2026-08-25.

---

## 1. La frase

**Le arrancas territorio estable a un laberinto que se mueve.**

El mundo no es un escenario: se desplaza solo. Estabilizar un trozo y anclarlo para
siempre es la única forma de tener "un sitio". Eso es la meta, y no hay que inventarla —
la máquina de estados ya corre.

## 2. Los tres anillos

El loop no es uno; son tres encajados. Cada uno tiene su propio ritmo y su propia
recompensa.

### Anillo A — El minuto (moment-to-moment)
Andas por pasillo amarillo. Miras dentro de una sala. Abres un cofre. Oyes algo.
- **Tensión:** el robapieles. Te caza, y si te pilla de espaldas te agarra en vez de
  golpearte: forcejeo, y si no escapas te mata.
- **Recompensa:** un bote, una botella, chatarra.
- **Duración:** segundos a minutos.

### Anillo B — La expedición (la "jornada")
Sales de tu base, buscas, vuelves, gastas lo traído.
1. **Salir** con el depósito lleno (sed y hambre marcan cuánto aguantas fuera).
2. **Buscar** agua, metal/cable, y sitio: qué chunk quieres tuyo.
3. **Volver** — el camino de vuelta es el momento de riesgo real: cargado, cansado,
   con algo que perder.
4. **Gastar**: construir, estabilizar, anclar.
- **Duración objetivo: 45–90 min.** La sed es el reloj que la delimita (~4 h de
  depósito lleno hoy — es demasiado largo, ver §5).

### Anillo C — La cuenta (persistente, entre sesiones)
No hay final de partida. Lo que se acumula:
- **Tu base anclada** (no se desplaza jamás; es el único "para siempre" del juego).
- **Tu profundidad máxima alcanzada.** Es el marcador de la cuenta. Bajar es opcional
  siempre; nunca hay una salida obligatoria ni un run que "termina".

## 3. El motor de tensión: el desplazamiento

Un chunk tiene un temporizador y, cuando llega a cero, se mueve. Tres estados:

| Estado | Qué significa para el jugador | Coste |
|---|---|---|
| **Inestable** | Puede irse en cualquier momento. Todo lo que dejes ahí, con él. | — |
| **Estabilizado** | Aguanta. La cordura drena menos. | Barato (T1 instantáneo) → caro (T3, 30 min) |
| **Anclado** | Tuyo para siempre. Aquí vive la base. | 50 cable + 25 min |

**Regla de diseño dura:** *cuándo* se moverá un chunk es aleatorio y **opaco a
propósito**. El HUD no lo delata nunca. Esa incertidumbre **es** la mecánica; anticiparla
será función de un objeto futuro, no de la interfaz.

Esa curva barato → caro → permanente es la progresión tecnológica del juego. Ningún
competidor del género la tiene: sus mundos son diseño de nivel estático.

## 4. Qué está construido hoy

Ocho de once sistemas del loop están hechos y verificados:

- Hambre, sed, cordura · comer y beber
- Construcción de base (persiste)
- Capas verticales (bajar de nivel)
- El robapieles (12 estados de IA)
- Voz por proximidad (25 m)
- Persistencia entre sesiones
- Mundo infinito con desplazamiento de chunks

Parciales: minería/recolección, crafteo, HUD de capa. Inexistente: objetivo/extracción.

## 5. Scope mínimo para que dé horas — YA

Tres piezas. **No es un rediseño**, es cerrar eslabones sueltos.

### P1 — Que craftear funcione *(bloqueante)*
La tabla de recetas existe y nadie la consume. Sin este eslabón, **minar no sirve para
nada y los tres tiers de estabilización son texto muerto** — el loop entero está
desconectado por aquí. Es la única pieza que hay que construir de verdad.
*Efecto en el jugador:* la chatarra que recoge se convierte en algo. El anillo B se cierra.

### P2 — Que se vea de quién es el suelo *(alto impacto, coste bajo)*
Hoy el jugador no ve qué chunk es suyo, cuál está anclado, ni en qué capa está. Es
interfaz, no arquitectura; y hay superficie diegética donde ponerlo (el reloj de muñeca).
*Efecto:* el anillo C se vuelve legible. Sin esto la progresión existe pero es invisible.

### P3 — Que bajar sea peor *(tuneo de parámetros)*
Más entidades, menos luz, cordura más rápida a mayor profundidad. Sobre sistemas que ya
existen. Es el antídoto directo al "grind sin escalada" que hundió a los competidores
(*Inside the Backrooms*: 78 % → 45 % en 30 días).
*Efecto:* la profundidad máxima significa algo. Da razón para volver mañana.

### Calibrado que acompaña
Con la sed a su valor actual, la muerte por sed más rápida posible son **4 horas**. Una
expedición no puede tener presión de recursos si el depósito dura media sesión: hay que
recortar la jornada al rango de 45–90 min antes de dar por buena ninguna otra cifra.

## 6. La primera hora del jugador nuevo

El objetivo tácito, sin tutorial ni misión:

1. Despiertas en pasillo. Tienes sed. → *buscas.*
2. Encuentras agua. Aprendes que hay poca. → *racionas.*
3. Encuentras chatarra y un banco de trabajo. → *crafteas.* **(P1)**
4. Vuelves a un sitio y ya no está. → *entiendes el juego.*
5. Estabilizas un chunk. Aguanta. → *tienes un sitio.* **(P2)**
6. Anclas. Ese suelo es tuyo para siempre. → *tienes una razón para volver.*

Eso son las horas. Todo lo demás escala encima.

## 7. Por dónde escala (no ahora)

En orden de barato a caro, sin comprometerse a ninguno:
- **Racionamiento** — elegir *cuánto* bebes; la fracción restante viaja con el objeto.
- **Props desmontables** — sillas y archivadores como fuente de material, en vez de nodos
  de recurso artificiales (un árbol en una oficina infinita rompe el tono).
- **Director de necesidad** — el loot pondera por la sed/hambre de quien abre. Solo sube
  hacia una línea base, nunca baja: pasar sed a propósito no da excedente.
- **Almacenaje seguro por anclaje** — cofre en chunk anclado = protegido; el robo sigue
  existiendo en la frontera. Estabilizar pasa a ser deseo económico, no solo supervivencia.
- **Incursiones (Level 4)** — regiones acotadas con puertas inestables; contenido de
  destino, no el loop base.

## 8. Lo que conviene dejar quieto

PvP (ya en V0; ningún competidor del género lo usa como motor), superestructuras
(contenido de relleno) y más entidades (el robapieles aún no se ha validado a 2+
jugadores). Los tres son contenido post-lanzamiento, no loop.
