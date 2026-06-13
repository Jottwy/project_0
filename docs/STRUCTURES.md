# Superstructures — Authoring Guide

Las **superestructuras** son patrones de tiles fijos que el generador procedural
intenta estampar sobre cada chunk antes de colocar pits y pilares. Permiten
inyectar piezas reconocibles del Backrooms (atrios, cruces de pasillos, pozos
verticales) en un mundo por lo demás aleatorio, **sin tocar código**: todo vive
en un JSON editable por diseño.

- **Fuente:** `Assets/Resources/Structures/default_structures.json`
- **Consumidor:** `WorldGenerator.TryPlaceStructures` en
  `Assets/Scripts/Gameplay/GridWorld/ProceduralWorldGenerator.cs`
- **Validador:** menú `Backrooms/Validate Structures`
  (`Assets/Editor/StructureValidator.cs`)

> Tras editar el JSON, ejecuta **siempre** `Backrooms/Validate Structures`.
> El generador descarta en silencio celdas malformadas; el validador no.

---

## 1. Cómo añadir una estructura

1. Abre `default_structures.json`.
2. Añade un objeto al array `structures` con estos campos:

   | Campo            | Tipo            | Obligatorio | Descripción |
   |------------------|-----------------|-------------|-------------|
   | `id`             | string          | sí          | Identificador único (kebab/snake). |
   | `probability`    | número 0.0–1.0  | sí          | Probabilidad de intentar estampar la pieza por chunk/layer. |
   | `layers_tall`    | entero 1–4      | sí          | Cuántas layers verticales ocupa conceptualmente. |
   | `pattern`        | array de filas  | sí          | Rejilla rectangular de celdas (ver §2). |
   | `min_chunk_size` | entero          | no          | Reservado/heredado; el generador actual no lo usa. |

3. Guarda y ejecuta `Backrooms/Validate Structures`.
4. Si la consola dice `All structures valid.`, listo.

El `pattern` se lee `[fila][columna]`, con la **fila 0 = sur** (coherente con el
comentario en `StructureDefinition.pattern`). El generador elige un ancla
aleatoria dentro del chunk y escribe cada celda del patrón sobre los tiles.

---

## 2. Caracteres válidos

Cada celda del `pattern` es un string de **un solo carácter**. Solo estos cinco
son válidos (mapean 1:1 con el `switch` de `TryPlaceStructures` →
`GridCellType`):

| Char | GridCellType | Caminable | Significado |
|------|--------------|-----------|-------------|
| `W`  | Wall         | no        | Pared sólida. Bloquea el paso. |
| `C`  | Corridor     | sí        | Pasillo caminable (1 tile de ancho lógico). |
| `O`  | Open         | sí        | Zona abierta / sala. Candidata a pilares. |
| `P`  | Pillar       | no        | Columna sólida. Decorativa pero bloquea. |
| `T`  | Pit          | sí        | Pozo: conecta verticalmente con la layer de abajo (forced-walkable). |

> **No existe carácter para `Void`, `Stair` ni `Anomaly`.** El generador solo
> reconoce `W C O P T`; cualquier otro carácter es descartado silenciosamente y
> el validador lo marca como error. Ver §6 (Limitaciones).

---

## 3. Ejemplos comentados

### corridor_cross — cruz de pasillos (común)
```json
{
  "id": "corridor_cross",
  "probability": 0.15,
  "layers_tall": 1,
  "pattern": [
    ["W","C","W"],
    ["C","C","C"],
    ["W","C","W"]
  ]
}
```
Una intersección en `+`. Las esquinas son pared (`W`), el cuerpo es pasillo
(`C`). Barata y frecuente (0.15) porque conecta bien con el ruido de fondo.

### large_atrium — atrio con pilares (raro, 2 layers)
```json
{
  "id": "large_atrium",
  "probability": 0.08,
  "layers_tall": 2,
  "pattern": [
    ["O","O","O","O"],
    ["O","P","P","O"],
    ["O","P","P","O"],
    ["O","O","O","O"]
  ]
}
```
Sala abierta (`O`) de 4×4 con un cuadro central de 2×2 pilares (`P`).
`layers_tall: 2` marca que es una pieza de doble altura.

### pit_shaft — pozo vertical (raro, 4 layers)
```json
{
  "id": "pit_shaft",
  "probability": 0.06,
  "layers_tall": 4,
  "pattern": [
    ["W","T","W"],
    ["T","T","T"],
    ["W","T","W"]
  ]
}
```
Cruz de pits (`T`) enmarcada en pared. Cada `T` propaga *forced-walkable* a la
layer inferior (ver `GetPitCellIndices` / parámetro `forcedWalkable`), creando
un hueco que atraviesa varias layers. `layers_tall: 4` = pozo de altura máxima.

---

## 4. Probabilidades recomendadas

`probability` es la chance de **intentar** estampar la pieza una vez por
chunk+layer (`rng.NextDouble() < probability`). Como cada estructura tira su
propio dado, las probabilidades **no** suman 1; varias pueden caer en el mismo
chunk y solaparse.

| Rango        | Uso típico | Sensación |
|--------------|-----------|-----------|
| 0.01 – 0.04  | Piezas raras, peligrosas o muy memorables (pozos, bordes de void). | "Una vez cada muchos chunks." |
| 0.05 – 0.09  | Piezas notables pero no constantes (atrios, salas especiales). | Punto de interés ocasional. |
| 0.10 – 0.15  | Conectores y relleno reconocible (cruces, pasillos, oficinas). | Aparecen con regularidad. |
| > 0.20       | **Desaconsejado:** saturan el mundo y rompen la aleatoriedad. | Repetitivo. |

Guía práctica: cuanto más grande o disruptiva sea la pieza, más baja la
probabilidad. Reserva valores altos para piezas pequeñas que se mezclan bien.

---

## 5. Interacción con LayerConfig

El generador procesa cada chunk en pasos (ver `GenerateChunk`). Las
superestructuras se estampan en el **paso 6**, *después* del zoning BSP, el
tallado de pasillos y las zonas abiertas, pero *antes* de pits de borde y
pilares:

```
BSP zoning → carve → connect → [SUPERSTRUCTURES] → pits → pilares → borde
```

Consecuencias:

- **Sobrescriben** lo que el ruido base (gobernado por `LayerConfig`) puso en
  esos tiles. Una estructura es determinista; el fondo no.
- Los pasos posteriores **pueden modificar** tiles de la estructura:
  - `PlacePits` puede convertir en `Pit` un borde de zona aunque caiga sobre la
    pieza (salvo que sea `Wall`).
  - `PlacePillars` solo afecta tiles `Open` dentro de zonas abiertas grandes, así
    que puede añadir pilares extra dentro de un `O` de tu estructura.
  - `EnforceBorderCorridor` garantiza conectividad en los 4 bordes del chunk y
    puede abrir un tile que tu patrón puso como pared si tapaba el único acceso.
- `LayerConfig` **no** filtra qué estructuras aparecen: `probability` es global a
  todas las layers. La única dependencia con la layer es indirecta vía la RNG
  determinista por `(seed, chunkX, chunkZ, layer)`.
- `layers_tall` es **metadato declarativo**: el generador actual no lee el campo
  para encadenar layers (el encadenamiento vertical real lo hace `T`/Pit vía
  `forcedWalkable`). Documenta la intención de la pieza; mantenlo coherente.

---

## 6. Limitaciones conocidas

- **Tamaño máximo nominal: 10×10 tiles** (el chunk es 10×10). El validador
  rechaza patrones mayores.
- **Tamaño práctico máximo: 8×8 tiles.** `TryPlaceStructures` ancla la pieza con
  un borde de 1 tile y exige `Tiles - cols - 1 >= 1`, es decir
  `cols, rows ≤ 8`. Un patrón de 9 o 10 de lado pasa el validador pero **nunca
  se coloca**. Mantén las piezas en ≤ 8×8.
- **Sin recorte ni colisión entre piezas:** dos estructuras pueden solaparse en
  el mismo chunk; la segunda sobrescribe a la primera. No hay garantía de
  exclusión mutua.
- **`layers_tall` no encadena layers por sí solo.** El único mecanismo vertical
  real es `T`/Pit. Una pieza con `layers_tall: 3` y sin `T` ocupa una sola layer
  de hecho.
- **Sin `Void`, `Stair` ni `Anomaly`.** El generador no tiene caso en el switch
  para esos tipos, así que no son expresables desde el JSON. Las piezas que
  conceptualmente los necesitan se aproximan con `W` (sólido/borde) — ver
  `void_edge_zone`, que usa pared como borde impenetrable y `C` seguro dentro.
- **Pilares en pasillos 1-wide bloquean el paso.** `Pillar` es sólido; una pieza
  como `long_corridor` (pasillo de 1 tile con `P` intercalados) es un colonnade
  decorativo/parcialmente bloqueado, no un corredor transitable de extremo a
  extremo. Es intencional; tenlo en cuenta al diseñar rutas.
