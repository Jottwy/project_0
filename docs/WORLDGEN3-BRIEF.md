# WorldGen3 — Brief de diseño

Documento de CONTEXTO, no de decisiones. Escrito 2026-08-27 para poder abrir
conversaciones de diseño detallado sin arrastrar el historial. No es un ADR y no
tiene fuerza de ley: cuando una decisión de aquí se cierre, se cierra en el registro
ADR del proyecto.

## 1. Qué es el juego

MMO de supervivencia persistente en un mundo procedural infinito inspirado en
Backrooms. Cliente Unity 6 + URP, backend Rust autoritativo. El mundo se genera por
seed de forma determinista: el servidor decide la geometría, el cliente la dibuja.
La progresión es la profundidad alcanzada, no una campaña con final.

## 2. El problema que WorldGen3 resuelve

El mundo actual es una pila de rejillas 2D. Cada celda de 2,5 m guarda tipo
(pared/pasillo/zona abierta/columna...) y altura de techo. La altura del SUELO es
función del índice de capa: constante en todo un piso.

Consecuencias:

- **No hay verticalidad expresable.** Rampas, medias plantas, altillos, salas
  hundidas, balcones sobre una zona abierta: no caben en el modelo. No es que falte
  implementarlo — no hay dónde escribirlo.
- **No hay detalle.** Todo sale de estampar celdas cuadradas, así que todo se lee
  como cuadrícula.
- Existen campos declarados y muertos: `num_stairs`, `num_pits`, `inter_layer_up/down`
  están en `LayerRules` con el comentario "values are inert".
- La altura de techo por celda SÍ viaja por el wire y **el cliente la ignora**:
  `ChunkRenderer.ceilingHeight` es un float único de 3,3 m.

Referencia visual objetivo: pasillos de oficina con techos que suben y bajan,
descuelgues sobre las puertas, nichos, rodapiés, y escaleras/rampas reales que
conectan alturas dentro de un mismo tramo.

## 3. La idea de WorldGen3

El mundo se construye con **piezas de malla autoradas** en vez de celdas estampadas.

La clave que lo hace viable: **el servidor nunca lee mallas.** Cuando una pieza se
hornea en Unity, Unity deriva del MODELO una "chuleta" de colisión (cajas con giro
para suelo, techo, paredes y columnas). Esa chuleta viaja en el manifiesto. El
servidor la estampa en su layout de colisión. Rust coloca identificadores de pieza
y transformaciones; no sabe qué es un triángulo.

Determinismo, autoridad de servidor y coste de red quedan intactos: es lo mismo que
ya se hace hoy con las salas autoradas, pero con la geometría interior incluida.

## 4. Punto de partida real (esto ya funciona)

- **Salas autoradas colocadas por el servidor**: ADR-083/084/085. Colocación
  determinista por hash, footprint multi-chunk (hasta 2x2 chunks), altura sin cap
  real, vanos excavados hasta el laberinto, manifiesto firmado por digest,
  supresión de geometría del cliente bajo la sala.
- **`RoomColliderBuilder.cs`** ya deriva las cajas de colisión del modelo (no de la
  malla), es puro y comparte fuente con el constructor de malla.
- **`RoomAuthoringWindow`** es la herramienta de autorado de piezas.
- **`RoomManifestExporter`** ya exporta y firma el manifiesto.

**El hueco exacto:** el manifiesto lleva id, tamaño en tiles, altura y puertas — y
NO lleva las cajas de colisión. `carve_authored_into_layout` marca el interior entero
como caminable. El servidor cree que toda pieza es una caja hueca. Funciona hoy solo
porque hay ~2 piezas y aparecen una cada ~520 m.

## 5. Reglas duras

- **R1** Rust nunca lee mallas. Unity hornea, Rust coloca.
- **R2** Una sola fuente por pieza: malla y colisión del mismo modelo. Prohibido
  autorar colisión aparte.
- **R3** Determinismo por `hash(seed, chunk, indice)`. Cero RNG compartido, cero
  estado global de proceso.
- **R4** Duplicar, no refactorizar. WG3 en namespace propio; el sistema viejo no se
  toca hasta borrarlo.
- **R5** Conectores tipados (lado, ancho, altura de suelo, altura de techo, tipo).
  Sin comodines.
- **R6** Pieza sin chuleta firmada = pieza que no existe. Nada se coloca a medias
  en silencio.
- **R7** Conectividad por construcción, nunca por reparación posterior.
- **R8** Altura de suelo por celda; la colisión resuelve Y por celda, no por capa.
  Pendiente máxima declarada por pieza.
- **R9** Escena aislada `WorldGen3Test.unity` (sin backend, sin red) antes de tocar
  el servidor.
- **R10** Presupuesto medido por fase: ns/chunk de colocación, bytes/chuleta,
  segundos de horneado.
- **R11** Wire propio; no se bumpea el actual. Coexistencia tras bandera.

## 6. Fases

| Fase | Contenido | Cierra cuando |
|---|---|---|
| F0 | Escena aislada, una pieza, sus cajas, caminarla. Sin Rust. | Chocas contra una columna interior en Unity |
| F1 | La chuleta entra al manifiesto (exportador + digest). Sin consumidor. | JSON + test de formato |
| F2 | Rust lee la chuleta y la estampa. Una pieza, un chunk. | Caminas la columna EN JUEGO |
| F3 | Conectores; dos piezas que encajan. | Test de encaje |
| F4 | N piezas por chunk + conectividad garantizada. | |
| F5 | Altura de suelo y rampas. | |
| F6 | Nav del robapieles sobre WG3. | |
| F7 | Retirada del sistema viejo. | |

F0 y F1 no necesitan ADR. **F2 en adelante sí** (toca formato de chunk y colisión).

## 7. Decisiones ABIERTAS (esto es lo que hay que discutir)

1. **Granularidad de la chuleta.** ¿La colisión del servidor son las cajas tal cual
   (lista de AABB con giro por pieza), o se rasterizan a una rejilla fina de
   ocupación? Cajas = exactas pero cambian `resolve_move`; rejilla = reaprovecha
   todo pero redondea el detalle. **Es LA decisión de la que cuelga el resto.**
2. **Tamaño de pieza y unidad de encaje.** ¿Piezas del tamaño de un chunk, de un
   tile, o libres con conectores en posiciones arbitrarias?
3. **Vocabulario de conectores.** Cuántos tipos, y si la altura de suelo forma parte
   del tipo o se resuelve con piezas de transición.
4. **¿Sobrevive el laberinto?** Propuesta: sí, degradado a relleno entre piezas
   mientras el catálogo crece; se jubila cuando haya catálogo suficiente. Si se mata
   antes, el mundo se acaba donde se acaben las piezas.
5. **Presupuesto de autorado.** Cuántas piezas hacen falta para que no se note la
   repetición, y si se compran modulares o se autoran con la herramienta existente.
6. **Capas.** ¿WG3 mantiene el concepto de capa a pitch fijo de 4 m, o la
   verticalidad pasa a ser continua y la capa desaparece?
7. **Nav del robapieles.** La IA lee hoy la rejilla fina de 2,5 m. Sobre qué lee en
   WG3, y si la chuleta le vale o necesita su propia malla de navegación.
8. **Loot y props dentro de la pieza.** Hoy no hay autoridad de servidor sobre
   props autorados; con el mundo entero autorado deja de ser un detalle.

## 8. Contexto que NO hay que redescubrir

- El mundo existe en DOS representaciones: `LayerGrid` a 2,5 m (render + IA) y
  `ChunkLayoutV1` a 5 m (colisión del jugador). Tallar solo una da paredes
  invisibles o paredes atravesables. WG3 debería reducirlo a una.
- El cap anunciado de las salas nunca coincidió con el colocable por un problema de
  paridad del origen: los caps reales son 6x6 en chunk y 16x16 multi-chunk.
- Una sala grande con UN solo vano nace incomunicada ~6 de 55 veces; con cuatro,
  0 de 58.
- El manifiesto activo es un `OnceLock` del proceso: dos sondas de medición en el
  mismo proceso se contaminan entre sí.
- Bumpear el schema de wire sin bumpear su espejo en C# deja el juego inarrancable.
