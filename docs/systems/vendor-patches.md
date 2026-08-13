# Parches en territorio vendor

> Todo lo que este proyecto ha escrito **dentro** de `Assets/PolymindGames/`. Un reimport del
> `.unitypackage` del vendor lo borra **en silencio**: los ficheros vuelven a su versión original,
> Unity no avisa, y el síntoma aparece días después como una regresión sin causa aparente.
>
> Esta lista es el antídoto. `tools/dev/CheckRegressionChecklist.ps1` la comprueba automáticamente
> (sección «Parches en territorio vendor»): **ejecútalo después de cualquier reimport del vendor**,
> y antes de dar por buena una sesión en la que tocaste sus assets.

## Por qué existen estos parches y no viven fuera

No es pereza: es una restricción de ensamblados. `PolymindGames` **no puede referenciar**
`Assembly-CSharp`, así que un hook que el vendor tenga que llamar no se puede poner fuera de su
propio ensamblado. La regla del proyecto es *hook externo o corregir después*, **nunca** editar o
poner guardas dentro de los métodos del vendor — pero los puntos de esta tabla son los que no
admitían esa vía.

## Inventario

| # | Familia | Marca de búsqueda | Ficheros | Qué se pierde si desaparece |
|---|---|---|---|---|
| 1 | Hooks ADR-009 (Rust es la única fuente de verdad) | `ADR-009` | 7 `.cs` | Vuelve el guardado de STP en paralelo al backend, y se van los hooks de reconciliación de posición/velocidad y el freno del drenaje de stamina por frame |
| 2 | Gate de arranque | `GameBootGate` | `GameBootGate.cs` (fichero **añadido**), `GameMode.cs` | Enmienda ADR-025: vuelve el timeout de 10 s y el jugador spawnea **sin backend**, sobre mundo vacío y sin sesión |
| 3 | Parche DoF sin rama URP | `PARCHE LOCAL (ADR-065` | `DepthOfFieldAnimation.cs` | El desenfoque enfoca el mundo en vez del libro: el libro vuelve a salir borroso |
| 4 | Traza MPTRACE de impactos (**temporal**, commit `458263a`) | `MPTRACE` | 4 `.cs` de disparo y melé | Se pierde la traza de atribución de daño PvP. Es andamiaje de diagnóstico: cuando el diagnóstico cierre, sale por decisión, no por reimport |
| 5 | Alta del bote de spray como wieldable | `BR_Wieldable_SprayCan` | `FPS_Player.prefab` | El bote se recoge y se ve en el inventario pero **no se equipa**, sin ningún error. Cura: menú `Backrooms/Spray/Registrar bote en el jugador` |
| 6 | Reverb por zona | `Rvb` (7 parámetros expuestos) | `FPS_AudioMixer.mixer` | El reverb se apaga **en silencio**: `ReverbMixerDriver` sondea los nombres una vez, no los encuentra, y se declara mudo (que es su degradación correcta, ver `docs/systems/reverb-mixer.md`) |

## Trampas conocidas

- **`GameMode.cs` no se identifica a sí mismo.** Su parche no lleva ni `ADR-` ni `Backrooms`: lo
  único que lo delata es que llama a `GameBootGate`. Por eso la comprobación #2 mira las dos cosas
  —que exista `GameBootGate.cs` y que `GameMode.cs` lo siga llamando—; con una sola, media
  restauración pasaría por buena.
- **Renombrar parámetros expuestos editando el YAML del `.mixer` no llega al runtime.** Hay que
  hacerlo desde el editor. Si la comprobación #6 falla, no se arregla a mano en el fichero.
- **El reimport del `.unitypackage` también se lleva las escenas demo enteras.** Commitea antes y
  desmarca todo lo que no sea render (pasó el 2026-08-11, commit `7793c4d` para restaurarlas).
- **Un reimport es reversible con git** siempre que lo parcheado esté commiteado. El riesgo real es
  reimportar con trabajo de vendor sin commitear.

## Cómo comprobarlo

```bash
powershell -ExecutionPolicy Bypass -File tools/dev/CheckRegressionChecklist.ps1
```

Las seis comprobaciones son de **presencia**, no de contenido: verifican que la marca sigue ahí y en
cuántos ficheros, que es exactamente lo que un reimport destruye. No validan que el parche siga
siendo correcto — eso lo hacen los tests y el juego.
