# Sistema de generación procedural Backrooms — Documento de diseño técnico

> Plano de implementación. Diseño cerrado y validado visualmente.
> Reemplaza el sistema volumétrico (chunk displacement / BandHeightSpec) por un sistema basado en celdas de rejilla.
> Pensado para abrirse junto al proyecto en Claude Code e implementarse por fases.

---

## 0. Resumen ejecutivo

El sistema volumétrico actual genera **geometría continua** desde celdas y bandas de altura. Eso produce caos visual (mallas trianguladas deformes, rampas blandas, cubos sueltos) porque cada chunk inventa su propia forma sin vocabulario común.

El sistema nuevo genera **celdas tipadas en una rejilla regular de 2.5 m**. Rust posee el grid y la colisión; Unity instancia prefabs alineados a rejilla con greedy meshing. Imposible que salga geometría deforme: solo hay cajas alineadas a una cuadrícula.

**Qué se tira:** capa de generación volumétrica (bandas, BandHeightSpec, chunk displacement) y capa de render (ChunkRenderer con mallas trianguladas).

**Qué se conserva:** modelo de autoridad Rust↔Unity, IPC con colas concurrentes, ChunkVisualLifecycle (presupuesto por frame), sistema de carga/descarga de chunks por posición, tests de red, handshake multijugador. La auditoría confirmó que esta capa está bien diseñada.

**Naturaleza del trabajo:** trasplante de motor (generación + render nuevos) sobre un chasis que ya funciona (infraestructura). No es proyecto en blanco.

---

## 1. Rejilla base

- **Unidad de celda: 2.5 m × 2.5 m** en planta.
- Elegida porque es divisor de 5: el "look 5×5 Backrooms" se logra con piezas de 2×2 celdas, conservando la estética deseada.
- Permite anchuras de pasillo de 2.5 m (claustrofóbico), 5 m (normal), 7.5 m (ancho).
- Coordenadas siempre enteras → colisión del backend y render alineados sin desfase.

**Regla de oro:** todas las celdas miden lo mismo. La variedad de tamaño viene de **piezas multi-celda** (una sala 4×4 = 16 celdas marcadas como pertenecientes a la misma zona), nunca de celdas de tamaños distintos en la misma rejilla.

**Altura (eje Y):** independiente de la rejilla del suelo. Variable libremente por tipo de celda/zona. Unidad de altura = 2.5 m (mismo módulo). Techos típicos:
- Pasillo: 4–5 m (valor 2 en unidades de 2.5 m)
- Sala abierta: 8–12 m (valores 3–5, con redondeo a múltiplos)
- Anomalía / vacío vertical: variable o infinito

Si dos celdas transitables adyacentes tienen techos muy distintos, se coloca una pieza de transición de techo (escalón/marco). Es un prefab más, no un problema estructural.

---

## 2. Contrato de datos celda↔Unity

**Decisión: struct plano de 3 campos. Nada más por ahora.**

```rust
#[derive(Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub struct Cell {
    pub cell_type: u8,      // ver tabla de tipos
    pub ceiling_height: u8, // en unidades de 2.5 m: 2 = 5 m, 4 = 10 m
    pub zone_id: u16,       // pieza/sala a la que pertenece; 0 = ninguna
}
```

### Tabla de tipos de celda (cell_type)

| Valor | Tipo      | Transitable | Render                                  |
|-------|-----------|-------------|-----------------------------------------|
| 0     | `Wall`    | No          | Cubo de pared (greedy-meshed)           |
| 1     | `Corridor`| Sí          | Suelo + techo                           |
| 2     | `Open`    | Sí          | Suelo + techo (altura mayor)            |
| 3     | `Pillar`  | No          | Prefab columna sobre suelo              |
| 4     | `Stair`   | Sí          | Prefab escalera (conecta capa N → N+1)  |
| 5     | `Pit`     | Sí          | Prefab pozo/bajada (capa N → N-1)       |
| 6     | `Void`    | No          | Borde de vacío; sin suelo               |
| 7     | `Anomaly` | Sí          | Marcador; trigger o prefab especial     |

### Blindaje del contrato (crítico)

La auditoría señaló que las constantes duplicadas a mano entre Rust y C# eran la deuda más peligrosa (un bit cambiado en Rust rompe Unity sin error). **Mitigación obligatoria:** un test de contrato en Rust que serializa una celda de cada tipo y afirma el valor numérico exacto que Unity espera.

```rust
#[test]
fn cell_contract_values_are_stable() {
    // Estos valores son un CONTRATO con Unity. Cambiarlos rompe el cliente.
    assert_eq!(CellType::Wall as u8, 0);
    assert_eq!(CellType::Corridor as u8, 1);
    assert_eq!(CellType::Open as u8, 2);
    assert_eq!(CellType::Pillar as u8, 3);
    assert_eq!(CellType::Stair as u8, 4);
    assert_eq!(CellType::Pit as u8, 5);
    assert_eq!(CellType::Void as u8, 6);
    assert_eq!(CellType::Anomaly as u8, 7);
    // ceiling_height en unidades de 2.5 m
    assert_eq!(height_units(5.0), 2);
    assert_eq!(height_units(10.0), 4);
}
```

En C#, el enum espejo debe documentarse como contrato y, si es viable, generarse o verificarse contra un golden file compartido.

---

## 3. Reglas por capa

**Decisión: tabla declarativa de reglas. El algoritmo de generación es uno solo.** Añadir una capa con personalidad nueva = añadir una fila a la tabla, no escribir código.

```rust
pub struct LayerRules {
    pub name: &'static str,
    pub wide_chance: f32,    // prob. de pasillo ancho (2 celdas)
    pub erode_chance: f32,   // prob. de abrir muros con 2+ vecinos suelo
    pub num_open_zones: u32, // cuántas zonas abiertas estampar
    pub open_zone_size: u32, // tamaño base de zona abierta (en celdas)
    pub pillar_chance: f32,  // densidad de pilares en salas grandes
    pub num_anomalies: u32,
    pub num_stairs: u32,
    pub num_pits: u32,
    pub num_voids: u32,
    pub ceiling_corridor: u8, // unidades de 2.5 m
    pub ceiling_open: u8,
}
```

### Perfiles de capa (validados visualmente)

| Capa | Carácter        | wide | erode | open | tamaño | pilares | anom | escaleras | vacíos | techo          |
|------|-----------------|------|-------|------|--------|---------|------|-----------|--------|----------------|
| 0    | El Vestíbulo    | 0.10 | 0.08  | 1    | 5      | 0.0     | 0    | 2         | 0      | 5 m (lineal)   |
| 1    | Las Salas       | 0.30 | 0.30  | 6    | 9      | 0.5     | 2    | 2         | 1      | 5–10 m         |
| 2    | El Caos         | 0.50 | 0.50  | 11   | 12     | 0.6     | 6    | 1         | 3      | 10–15 m        |
| 3    | El Vacío        | 0.20 | 0.20  | 5    | 10     | 0.3     | 4    | 0         | 14     | ∞              |

Esto habilita reglas condicionadas a altura ("a partir de capa 5 las anomalías son tipo X", "capas pares lineales / impares caóticas", "cada 10 capas una capa-vacío") como datos, no como lógica enterrada. El sistema se vuelve un lenguaje de diseño de niveles.

---

## 4. Algoritmo de generación (pseudocódigo portable a Rust)

Por capa, con seed determinista derivada de `(world_seed, layer_index)`:

```
fn generate_layer(rules, seed) -> Grid:
    rng = mulberry32(seed XOR layer_index*1337)  // determinista
    grid = fill(SOLID)

    # FASE 1 — Laberinto base (backtracking recursivo, paso de 2 celdas)
    grid[1,1] = CORRIDOR
    stack = [(1,1)]
    while stack not empty:
        (cx,cy) = stack.top()
        opts = vecinos a distancia 2 que sigan SOLID
        if opts empty: stack.pop(); continue
        (nx,ny,dx,dy) = opts[rng.pick()]
        grid[cx+dx/2, cy+dy/2] = CORRIDOR   # tumba el muro intermedio
        grid[nx,ny] = CORRIDOR
        if rng < rules.wide_chance: marcar (intermedio, destino) como wide
        if rng < 0.82: stack.push((nx,ny))   # sesgo a pasillos largos
        else: stack.remove_random()           # ramificación

    # FASE 2 — Pasillos anchos (engrosar tramos marcados)
    for cada celda wide:
        abrir vecinos [+1,0],[0,+1],[+1,+1] si son SOLID

    # FASE 3 — Erosión / aperturas (rompe la rigidez del laberinto)
    snapshot = grid.clone()
    for cada celda SOLID:
        f = nº de vecinos ortogonales que son suelo en snapshot
        if f >= 2 and rng < rules.erode_chance: grid[celda] = CORRIDOR

    # FASE 4 — Zonas abiertas (ESTAMPAR GANA — sobreescriben el laberinto)
    zones = []
    for i in 0..rules.num_open_zones:
        rect aleatorio de tamaño ~open_zone_size
        marcar todas sus celdas = OPEN
        zones.push(rect)
        if rect grande: sembrar PILLAR en rejilla interior (cada 3 celdas)

    # FASE 5 — Reconexión (garantiza que cada zona toca el laberinto)
    for cada zone:
        if zone aislada de todo CORRIDOR:
            abrir celda(s) de muro en el borde más cercano a un pasillo

    # FASE 6 — Vacíos y anomalías (sembrar DENTRO de zonas abiertas)
    for i in 0..rules.num_voids:  estampar VOID en zona aleatoria
    for i in 0..rules.num_anomalies: estampar ANOMALY en zona aleatoria (no sobre VOID)

    # FASE 7 — Conexiones verticales
    for i in 0..rules.num_stairs:
        elegir celda transitable; marcar STAIR
        FORZAR suelo transitable en (x,y) de la capa N+1   # ver §5
    for i in 0..rules.num_pits:
        elegir celda transitable; marcar PIT
        FORZAR suelo transitable en (x,y) de la capa N-1

    return grid
```

`mulberry32` (o cualquier PRNG sembrable equivalente en Rust, p. ej. `StdRng::seed_from_u64`) garantiza que la misma seed produce el mismo mundo en todos los peers — requisito para el multijugador determinista que ya tiene el proyecto.

---

## 5. Reglas de coherencia

**Decisión: "estampar gana, y reconectar después".**

1. **Prioridad de estampado:** cuando una pieza multi-celda (sala, anomalía) cae sobre el laberinto, sobreescribe sin preguntar. La sala gana siempre.
2. **Reconexión:** tras estampar, un paso recorre el borde de cada pieza y garantiza ≥1 conexión a pasillo adyacente. Si quedó aislada, abre una celda de muro hacia el pasillo más cercano.
3. **Escaleras/pozos:** la transición en `(x,y)` de la capa N **fuerza** suelo transitable en `(x,y)` de la capa destino. Se estampa el suelo al colocar la transición; no se espera a que coincida. Esto elimina las "escaleras que suben a una pared".

Cubre ~95% de casos sin lógica compleja. Casos raros restantes (sala que parte el mapa en dos mitades inconexas) se detectan con un flood-fill opcional de validación en fase 5.

---

## 6. Render en Unity

**Se tira `ChunkRenderer` volumétrico. Se reemplaza por instanciado de prefabs + greedy meshing.**

### Prefabs primitivos (crear desde cero — son cajas, ~2 h en ProBuilder)

| Prefab            | Descripción                                    |
|-------------------|------------------------------------------------|
| `FloorCeiling`    | Loseta de suelo + techo (textura papel/moqueta)|
| `Wall`            | Cubo de pared 2.5 m (papel pintado amarillo)   |
| `Pillar`          | Columna sobre suelo                            |
| `Stair`           | Escalera que sube 1 capa (altura = LayerHeight)|
| `VoidEdge`        | Borde de hueco al vacío                        |
| `CeilingStep`     | Transición entre dos alturas de techo          |

### Construcción por celda

```
para cada celda del chunk:
    siempre: colocar FloorCeiling (techo a ceiling_height)
    si cell_type == Wall:    acumular en buffer de greedy meshing
    si cell_type == Pillar:  instanciar Pillar
    si cell_type == Stair:   instanciar Stair
    si cell_type == Void:    sin suelo; colocar VoidEdge en bordes
    si cell_type == Anomaly: trigger / prefab especial según subtipo (futuro)
combinar todos los muros del chunk en UNA malla (greedy meshing)
```

### Greedy meshing (clave de rendimiento)

En vez de un GameObject por celda de pared, se combinan tramos contiguos en rectángulos únicos. Un pasillo de 8 muros en fila = 1 quad, no 8. Reduce geometría 80–90% en zonas regulares. Con esto, rejilla de 2.5 m en zonas de 100×100 m (1.600 celdas) va fluida. Sin esto, hasta 5 m puede lagear con instanciado ingenuo.

**El rendimiento depende más del troceado en chunks (que ya existe: `ChunkVisualLifecycle` con presupuesto por frame) que del tamaño de celda.**

---

## 7. Verticalidad multicapa

- Cada capa es un grid 2D independiente generado con el mismo algoritmo y reglas propias.
- Las capas se apilan en Y separadas por `LayerHeight` (el proyecto ya usa `LayerHeight=7`; ajustar a múltiplo de 2.5 m según altura de techo deseada).
- Conexión = celdas `Stair` / `Pit` que enlazan la misma coordenada `(x,y)` entre capas adyacentes (ver §5).
- `Void` = celda sin suelo: el jugador se asoma a la nada o cae. La "sensación de vacío hacia el vacío" de los Backrooms.
- Coherencia trivial: la coordenada `(x,y)` es la misma en todas las capas; solo cambia la altura.

---

## 8. Plan de implementación por fases

Cada fase es reversible y en ninguna te quedas sin un proyecto que corre.

### Fase 1 — Módulo `grid_gen` aislado (Rust)
- Crear módulo nuevo `grid_gen` sin tocar el código existente.
- Implementar `Cell`, `CellType`, `LayerRules`, tabla de perfiles, `generate_layer`.
- Test de contrato (§2) + tests de generación (laberinto conexo, escaleras con suelo destino, zonas reconectadas).
- **Costurado de bordes entre chunks (se choca aquí sí o sí):** Backrooms es infinito pero los chunks son finitos. El chunk en `(cx+1, cy)` debe conectar con el de `(cx, cy)` o habrá muros que cierran el paso en cada borde. La generación de cada chunk debe ser determinista por su coordenada `(world_seed, chunk_coord, layer)` y costurar bordes con los vecinos (garantizar ≥1 paso abierto entre chunks adyacentes, aplicando la regla de reconexión de §5 también en las costuras). Resolver esto en Fase 1, no después.
- **Sin integración todavía.** Solo el generador puro.

### Fase 2 — Validación visual del grid real
- Exportar el grid de `grid_gen` a un visualizador 2D (PNG o pequeño viewer) que lea el Rust REAL.
- Confirmar que el layout se ve como las maquetas validadas. Iterar parámetros aquí, barato.
- Decisión de continuar solo si el grid sale bien.

### Fase 3 — Renderer de prefabs (Unity)
- Crear los 6 prefabs primitivos.
- Implementar el constructor por celda + greedy meshing para muros.
- Probar con un grid de prueba hardcodeado (sin IPC todavía).

### Fase 4 — Conexión por IPC existente
- Serializar el grid de celdas por el IPC que YA existe.
- Implementar el enum espejo en C# + parsing del nuevo mensaje.
- Conectar el lifecycle de chunks existente al nuevo renderer.

### Fase 5 — Retirada del sistema viejo
- Solo cuando el nuevo funciona end-to-end: eliminar `ChunkRenderer` volumétrico, `BandHeightSpec`, lógica de bandas y chunk displacement.
- Limpiar constantes muertas y tests obsoletos.

### Fase 6 — Capas y reglas (escalabilidad)
- Mover la tabla de perfiles a config externa (TOML/JSON) editable sin recompilar.
- Añadir reglas condicionadas a altura.
- Subtipos de anomalía (sala infinita de pilares, hueco vertical, física rota) como prefabs curados que el generador coloca.

### Fase 7 — Chunk displacement (EXTENSIÓN FUTURA — no implementar en el motor base)

> Mecánica de juego, no parte del motor. Solo abordar cuando las Fases 1–5 estén estables y el sistema genere, renderice y colisione bien. Construirla antes vuelve al territorio de complejidad-sobre-cimientos-inestables que causó la frustración original.

**Concepto:** desplazar una región de chunks/celdas (con su contenido) a otra posición. Es la mecánica estrella del concepto Backrooms original ("chunk displacement").

**Por qué el sistema de celdas es el sustrato correcto (mejor que el volumétrico):** una región de celdas es solo datos — un bloque del array `Cell[]`. Desplazarla es copiar ese bloque a otras coordenadas y recalcular bordes con los nuevos vecinos. No hay mallas que regenerar ni geometría que pueda romperse. En el sistema volumétrico, mover un chunk obligaba a relanzar generación de bandas + mallas en la nueva posición — la fuente del caos visual. Con celdas: mover = copiar datos + reconectar bordes (misma regla de §5). **Esta es probablemente la razón más fuerte para abandonar el volumétrico: la mecánica estrella funciona mejor sobre celdas.**

**Tres piezas a añadir cuando llegue el momento:**

1. **Backend — operación de desplazamiento.**
   ```
   fn displace_region(origen: Rect, destino: Coord):
       copiar bloque de celdas origen → destino
       marcar bordes del destino para reconexión (regla §5)
       recalcular colisión de las celdas afectadas
       invalidar caché de vista de los chunks tocados
   ```

2. **Sincronización entre peers (LO DELICADO).** La auditoría ya detectó desync de layout en el teleport actual: `tick_teleportation` regeneraba contenido pero no asignaba `chunk.layout`, mientras `apply_remote_teleport` sí — host y joiner acababan colisionando contra layouts distintos (paredes fantasma divergentes). El desplazamiento de regiones es el mismo problema amplificado. Requisito: la operación debe ser **determinista o explícitamente sincronizada** — o ambos peers la derivan de la misma seed/evento, o se transmite el resultado completo de la región desplazada. No dejar que cada peer la calcule por separado sin reconciliación.

3. **Unity — pooling reposicionando.** Las piezas afectadas se reciclan desde el pool a las nuevas coordenadas, no se reconstruyen. Trivial gracias a la rejilla uniforme: cualquier muro reciclado encaja en cualquier celda de 2.5 m sin ajuste.

**Object pooling (prerequisito técnico, sí encaja en el motor base):** mantener un pool de prefabs reutilizables (muros, suelos, pilares) y reposicionarlos al cargar/descargar chunks en vez de crear/destruir GameObjects. Evolución natural del `ChunkVisualLifecycle` existente. Ideal con rejilla uniforme: una pieza reciclada encaja en cualquier posición. Esto puede introducirse ya en Fase 3–4; el *displacement* que lo usa es lo que espera a Fase 7.

---

## 8b. Ambientación derivada del grid — luz, sonido y reverb (EXTENSIÓN FUTURA — Fase 8)

> Capa de inmersión, no motor base. Se aborda cuando ya puedes caminar por el mundo. No diseñar antes: la ambientación se afina sintiéndola, no en abstracto.

**Principio unificador: el grid es la partitura.** Geometría, iluminación y audio son tres lecturas distintas del mismo mapa de celdas. No son sistemas paralelos que haya que sincronizar — son interpretaciones de una sola verdad. Por eso se sienten coherentes y por eso encajan sin fricción: no se añade una fuente de datos nueva, se lee la que ya existe (tipo de celda, tamaño de zona, altura de techo, posiciones de lámpara).

**Patrón técnico compartido por los tres: pooling por proximidad.** Igual que el greedy meshing no procesa lo que no se ve, la ambientación no procesa lo que no se percibe. Un pool pequeño de recursos caros (luces reales, AudioSources) se reasigna dinámicamente a las N fuentes más cercanas al jugador. Lo lejano se *fakea* barato.

### Iluminación

Objetivo estético: no oscuridad total, sino **luz fluorescente enferma y plana con zonas muertas**. El terror está en el contraste — charcos de luz amarillenta entre tramos en penumbra — no en la ausencia de luz.

Reto técnico: el baked lighting necesita geometría que exista antes de jugar; el mundo procedural se genera en runtime, así que no se puede hornear. Las luces en tiempo real funcionan con geometría dinámica pero son caras en masa.

Solución:
- **Pool de luces reales (8–16)** reasignadas a las lámparas más cercanas. Solo esas iluminan de verdad suelo y paredes.
- **Lámparas lejanas fakeadas:** quad emisivo brillante (material que se ve iluminado sin emitir luz real) + halo/cono pintado en geometría. El cerebro lee "eso brilla" sin coste de luz real.
- **Fog amarillento denso** + ambient light global bajo. Hace tres cosas a la vez: atmósfera asfixiante, oculta el límite donde los chunks aún no han cargado, y permite un mundo "oscuro" sin calcular luz en cada rincón. Nada es negro absoluto pero todo se siente tenue.
- **Lámparas que parpadean o están fundidas**, marcadas por seed (determinista). Crea los tramos oscuros entre charcos de luz sin diseñarlos a mano — emergen de la generación como todo lo demás.

### Audio — cuatro tipos de fuente, todos derivados del grid

1. **Fuentes puntuales pooleadas** — lámparas (hum fluorescente), anomalías (sonido característico), props futuros. Para el sistema de audio, una lámpara y una anomalía son lo mismo: una posición de celda con un clip. Mismo gestor de proximidad que las luces.
2. **Ambiente por tipo de zona** — colchón de fondo que cambia según dónde está el jugador (pasillo estrecho ≠ sala enorme ≠ vacío). Crossfade entre capas de ambiente según el tipo de celda que lo rodea. Barato (1–2 fuentes en loop), vende mucho la sensación de espacio.
3. **Reverb por geometría** — pasos secos y cercanos en un pasillo de 2.5 m; eco con cola larga en una sala de 12 m de alto. `Audio Reverb Zones` de Unity con preset asignado automáticamente por zona, usando el tamaño y la altura de techo que YA están en el grid. El jugador cruza de pasillo a sala y el eco cambia solo. Inmersión casi gratis porque la info ya existe.
4. **Sonido posicional difuso / lejano** — ruidos "de algo" que vienen de lejos sin fuente visible. El terror Backrooms puro. Más diseño que sistema: triggers en anomalías o eventos que reproducen sonidos a media distancia con espacialización.

### Sistema unificado de lámpara: luz + sonido

Una lámpara emite **luz y hum desde la misma celda**, gestionados por el **mismo pool de proximidad**. Por eso luz y sonido se diseñan juntos, no por separado: son el mismo objeto leído dos veces. La posición, el estado (funciona/parpadea/fundida) y la asignación de recursos del pool se comparten.



Pegar como comentario en los archivos clave. La auditoría mostró que los agentes respetan contexto in-file mejor que instrucciones abstractas.

1. **Rejilla uniforme de 2.5 m.** Nunca celdas de tamaños distintos en el mismo grid.
2. **El contrato de `cell_type` es sagrado.** Cambiar un valor numérico rompe Unity. Si cambia, actualizar el test de contrato Y el enum C# en el mismo commit.
3. **Rust es autoridad de colisión.** Unity solo renderiza. No mover lógica de colisión al cliente.
4. **No tocar Unity physics** sin justificación explícita (restricción heredada de la auditoría).
5. **No crear structs/clases con nombres ya existentes** en el codebase.
6. **Generación determinista:** misma seed → mismo mundo en todos los peers. No introducir aleatoriedad no sembrada.
7. **Migración incremental:** no borrar el sistema viejo hasta que el nuevo funcione end-to-end.
8. **Chunk displacement es Fase 7.** No implementarlo antes de que el motor base (Fases 1–5) sea estable. Object pooling sí puede entrar antes (Fase 3–4); la mecánica de desplazamiento que lo usa, no.

---

## 10. Estado del diseño

**Cerrado y validado (motor base):** rejilla 2.5 m, generación por capas (laberinto orgánico + erosión + anchuras + zonas + anomalías), reglas por capa declarativas, verticalidad, contrato de datos de 3 campos, reglas de coherencia, plan de trasplante por fases.

**Documentado como extensión futura (no implementar en el motor base):** chunk displacement (§Fase 7), ambientación derivada del grid — luz, sonido y reverb (§8b, Fase 8).

**Pendiente de cerrar contra el código real** (no se puede en abstracto): cuánto del grid actual del backend sirve como base de `grid_gen`, valor exacto de `LayerHeight` ajustado a múltiplo de 2.5 m, y subtipos concretos de anomalía. Se deciden en Fase 1–2 viendo el código.

**Capa de juego, deliberadamente sin diseñar todavía** (se diseña cuando se puede caminar por el mundo, no antes): spawn y orientación del jugador, y el loop de juego (qué hace el jugador ahí — explorar, huir, buscar salida). Las anomalías del grid son ganchos para esto. No cerrar decisiones de motor que cierren prematuramente estas opciones.

---

> **Recordatorio de método.** El diseño del motor está cerrado y es accionable. El siguiente aprendizaje real no está en otra capa de plano — está en escribir `grid_gen` y ver el primer laberinto salir del Rust real. El riesgo conocido es generar más diseño antes de implementar lo anterior. Siguiente paso: Fase 1, no más planificación.
