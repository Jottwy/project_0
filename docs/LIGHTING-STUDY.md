# Estudio de iluminación — estado real y salto a Unity 6.7

Fecha: 2026-09-07. Rama `claude/showcase-lighting-broken-f13218`. Unity `6000.0.71f1`, URP `17.0.4`, Forward+.

Regla del documento: cada afirmación lleva etiqueta. **[V]** = verificado (fichero + línea, log o captura). **[H]** = hipótesis con cálculo, pendiente de un experimento concreto. **[F]** = fuente externa (enlace en §7). Nada se da por hecho.

---

## 0. Resumen en diez líneas

1. **[V]** Hasta hoy ninguna lámpara WG3 ni la linterna tocaba el mundo: escribían la máscara en `Light.renderingLayerMask`, URP lee `UniversalAdditionalLightData.renderingLayers` (`ForwardLights.cs:540`), y además el proyecto sólo tenía definida la capa `Default`, así que `ToValidRenderingLayers` dejaba las máscaras de planta en 0. Lo único que "iluminaba" eran los 117 light probes horneados al aire libre del demo del vendor. Corregido en el árbol de trabajo (sin commitear); captura de la build 4 en §6.
2. **[V]** La escena arrastra restos del demo exterior: `Directional Light` a 0,3 realtime, `LightingDataAsset` de 20,6 MB, `LightProbeGroup` de 118 probes, `ReflectionProbe` realtime, niebla lineal negra de −50 a +50 m.
3. **[H, cálculo]** Esa niebla lineal con inicio en −50 m mezcla **el 50 % a negro ya en la cámara** y el 60 % a 10 m. Es el candidato número uno a "materiales ultra oscuros". Un experimento de una línea lo confirma o lo descarta (§5, E1).
4. **[V]** El post-proceso activo es el perfil del demo: tonemapper **ACES** + `contrast 8` + SSAO a 0,9. ACES aplasta los bajos; en una escena que vive en los bajos, eso es oscuridad extra gratis.
5. **[V]** No hay iluminación indirecta de ningún tipo: ambiente negro (pedido por Joel), sin GI, sin probes propios. Los paneles emisivos del techo se VEN pero no ILUMINAN. Por eso "Level 0" (techo entero de fluorescentes, luz uniforme y plana) no puede salir sólo con point lights: falta el rebote.
6. **[V]** Presupuesto: Forward+ permite ilimitadas luces por objeto pero **256 luces visibles por cámara** en escritorio; WG3 crea hasta 4 point lights por tramo y hay 1 179 tramos en la ventana medida. Si el frustum ve más de 256, URP descarta el resto sin avisar. Medir antes de "más lámparas".
7. **[F]** Unity 6.0 LTS deja de tener soporte en **octubre de 2026**. El salto no es opcional; la pregunta es a cuál.
8. **[F]** Unity 6.7 (hoy alpha `6000.7.0a5`, LTS "a finales de 2026", antes de la beta de Unity 7 en diciembre) trae **Surface Cache GI**: GI difusa en tiempo real, sólo URP, sin bake, pensada para geometría procedural y luces dinámicas (linterna incluida). Es exactamente la pieza que falta aquí. Está en preview: difusa sólo, parpadeo temporal, coste por medir, soporte de emisivos y static batching sin confirmar.
9. **[V]** Riesgo de migración bajo en lo que a render se refiere: Compatibility Mode ya está apagado, **cero** `ScriptableRenderPass`/`RendererFeature` propios ni del vendor, shaders WG3 = `URP/Lit` de serie. Lo que sí hay que auditar: `[SerializeField]` fuera de campos (6.3 lo convierte en error), Editor Toolbox 0.12.11 (2024), STP sin fichero de versión, y el paquete local `com.tripo3d.unitybridge` que apunta a `C:/Users/JOELV/Downloads`.
10. **Recomendación**: tronco a **6.3 LTS** ahora (soportado hasta diciembre de 2027), y un worktree aparte con **6.7 alpha/beta** para medir Surface Cache GI sobre el mundo WG3 real antes de decidir. Mientras tanto, los experimentos E1–E5 de §5 no dependen de la versión y son los que cambian la cara del juego esta semana.

---

## 1. Qué hay ahora mismo (inventario verificado)

### 1.1 Pipeline y renderer
- `ProjectSettings/GraphicsSettings.asset:39` → `Assets/Settings/PC_RPAsset.asset`. Los cinco niveles de `QualitySettings` tienen `customRenderPipeline: 0`, así que todos usan el mismo asset. Nivel activo `High` (`m_CurrentQuality: 3`).
- `PC_RPAsset`: `m_SupportsHDR: 1`, `m_MSAA: 2`, `m_LightProbeSystem: 0` (Light Probe Groups, APV apagado), `m_AdditionalLightsPerObjectLimit: 4` (irrelevante en Forward+), `m_AdditionalLightShadowsSupported: 1`, atlas adicional `4096`, `m_ShadowDistance: 50`, `m_SupportsLightLayers: 1`, `m_ColorGradingMode: 0` (**LDR**, LUT 32), `m_VolumeProfile` = `SampleSceneProfile` (ACES + viñeta 0,2).
- `PC_Renderer.asset`: `m_RenderingMode: 2` (**Forward+**), `m_UseNativeRenderPass: 1`, `m_DepthPrimingMode: 0`. Única feature: **SSAO** (`Intensity 0.9`, `Radius 0.9`, `DirectLightingStrength 0.25`, `Samples 1` = Low). Sin decals, sin Render Objects.
- `UniversalRenderPipelineGlobalSettings.asset`: `RenderGraphSettings.m_EnableRenderCompatibilityMode: 0` (Render Graph activo). `m_RenderingLayerNames` son los ocho nombres por defecto `Light Layer default..7` (campo legacy; en Unity 6 los nombres reales viven en `TagManager.m_RenderingLayers`).
- `ProjectSettings.asset`: `m_ActiveColorSpace: 1` (**lineal**), `useHDRDisplay: 0`. Sin entrada de API gráfica para Standalone (automático = DX11/DX12 según editor).

### 1.2 La escena `STP_Showcase.unity` (3 905 líneas, sin modificar respecto a HEAD)
- `RenderSettings` (L13-38): `m_Fog: 1`, **`m_FogMode: 1` (lineal), `m_LinearFogStart: -50`, `m_LinearFogEnd: 50`, `m_FogColor` negro**; `m_AmbientMode: 3` (Flat) con `m_AmbientSkyColor` negro; `m_SkyboxMaterial: 0`; `m_Sun: 0`; `m_ReflectionIntensity: 0`.
- `LightmapSettings`: `m_EnableBakedLightmaps: 0`, `m_EnableRealtimeLightmaps: 1`, **`m_LightingDataAsset` → `STP_Showcase/LightingData.asset` (20,6 MB)** con dos `ReflectionProbe-*.exr`. Backend `Progressive CPU` (deprecado en 6.6).
- Luces serializadas: exactamente dos. **`Directional Light`** (`&551954988`): tipo 1, intensidad **0,3**, sin sombras, realtime, `m_RenderingLayers: 1`. **`Area Light`** (`&554756487`): tipo 3, `m_Lightmapping: 2` (sólo cuenta en bake; no hace nada en runtime).
- `LightProbeGroup` con **118** posiciones en la zona del demo (X −52…+6, Z −48…−8).
- `Probe Adjustment Volume` (APV) huérfano: APV está apagado en el pipeline.
- `STP_WorldManager` instanciado: `SunLight` y `MoonLight` **desactivadas por GameObject** (overrides), `DayNightCycleController` deshabilitado. Quedan vivos su `ReflectionProbe` (realtime, 128, caja 200×100×200, `m_RefreshMode: 2` = por script, o sea nunca se refresca) y su **`Volume` global en capa 11** con `STP_DemoProfile_URP`.
- `GameManager` con `_enableWorldGen3: 1`; `GridTestWorld` activo con los cuatro materiales WG3.

### 1.3 Post-proceso efectivo
Cámara `FPS_PlayerCamera.prefab`: `m_HDR: 1`, `m_RenderPostProcessing: 1`, SMAA High, `m_VolumeLayerMask: 2048` (sólo capa 11). Pila resultante:
1. `DefaultVolumeProfile` (global, neutro).
2. `SampleSceneProfile` (asset del pipeline): **Tonemapping ACES**, Vignette 0,2.
3. `STP_DemoProfile_URP` (Volume del WorldManager, prioridad 1, peso 1): **Tonemapping ACES**, ColorAdjustments `postExposure 1.1`, **`contrast 8`**, `hueShift -2`, `saturation 0`; Bloom `threshold 0.9`, `intensity 1`; WhiteBalance `temperature 5`; Vignette; ChromaticAberration; FilmGrain a 0; DoF apagado.

### 1.4 Código WG3 de luz (árbol de trabajo)
- `Wg3SceneAssembler.cs:443` `MaxPerAxis = 2` → hasta **4 point lights por tramo**; `:557` `range 11`; `:567` `intensity 3.1 × UpperFloorBoost`; `:575-587` sólo la primera lámpara de un tramo ≥ 12 m proyecta sombra (Soft, 0,72); el resto `LightShadows.None`.
- `Wg3SceneAssembler.cs:1391-1455` plafón de pieza: `intensity 3.6 × boost`, `range ≤ 9`, sin sombras.
- `Wg3SceneAssembler.cs:647-676` luz de emergencia verde: 0,55 / 5 m.
- `Wg3SceneAssembler.cs:972-1011` paneles emisivos: renderer con `lampMaterial` (URP/Lit + `_EMISSION`, `RealtimeEmissive`). **Sin GI activa, `RealtimeEmissive` no hace nada**: el panel brilla en pantalla y no ilumina.
- `Wg3SceneAssembler.cs:210-230` `Apply(Light)` escribe `renderingLayers`; `Apply(Renderer)` escribe `renderingLayerMask` y **apaga `lightProbeUsage`** (para que WG3 no muestree los probes del demo).
- `Wg3ChunkStreamer.cs:96,103-104`: ambiente Flat **negro** (pedido de Joel, 07-09).
- WG3 **no toca `RenderSettings.fog`**: la niebla de la escena queda tal cual (§1.2). El generador WG2 sí la ajustaba (`ProceduralWorldGenerator.cs:719-724`), pero sale antes cuando `Wg3Enabled`.
- `Wg3LightCadence.cs`: `offChance 0.12`, `flickerChance 0.08`, `DecayOffShare 0.60`, `DecayFlickerShare 0.30`, `DecayDimShare 0.35`, `DecayPanelMissingShare 0.30`.
- `ProjectSettings/TagManager.asset:46-54`: `Default` + `Wg3 Storey 1..7` (nuevo, sin commitear). `Wg3StoreyLayers.StoreyOf` = `RawStoreyOf + 3`, así que la **calle es el bit 3 y el sótano B3 es el bit 0 = `Default`**: toda luz del vendor o de prefab con máscara 1 ilumina B3 y nada más del mundo.

### 1.5 Materiales WG3
Los cuatro son `Universal Render Pipeline/Lit`, `_Metallic 0`, sin emisión: `Wg3_Floor` `_BaseColor (0.30, 0.36, 0.28)` (HEAD: 0.21/0.25/0.19), smoothness 0,06; `Wg3_Structure` (0.86, 0.86, 0.82); `Wg3_Ceiling` (0.80, 0.80, 0.77); `Wg3_Trim` (0.95, 0.95, 0.93). Albedos de pared y techo ya son altos; el suelo es el único "oscuro" de fábrica. La oscuridad no viene del albedo.

### 1.6 Linterna (ADR-133)
`BR_Wieldable_CrankFlashlight.prefab`: Spot **4,0 / 18 m / 55°** (inner 22°), sin sombras, color (0.93, 0.95, 1). El prefab no lleva `UniversalAdditionalLightData`; se crea en runtime y `CrankFlashlightWieldable.cs:520-521` le asigna la capa de la planta del haz cada frame. Carga inicial 10–50 % de 60 s (6–30 s de luz antes de dar a la manivela).

---

## 2. Cadena de causas de "se ve todo negro" (ordenada por peso)

| # | Causa | Etiqueta | Evidencia | Estado |
|---|---|---|---|---|
| 1 | Luces WG3 y linterna en la capa equivocada (`Light.renderingLayerMask`) | [V] | `ForwardLights.cs:540`; captura build 3 sin luz, build 4 con luz | Corregido, sin commitear |
| 2 | Capas de render no definidas → máscaras clampeadas a 0 | [V] | `RenderingLayerUtils.ToValidRenderingLayers`; `TagManager` tenía sólo `Default` | Corregido, sin commitear |
| 3 | Niebla lineal negra −50…50 m | [H] | §1.2; factor lineal `(end−d)/(end−start)` = 0,5 en d=0 | **E1** |
| 4 | ACES + `contrast 8` + LDR grading | [V] config, [H] impacto | §1.3 | **E2** |
| 5 | Sin indirecta (ambiente negro por diseño, sin GI, sin probes propios) | [V] | §1.4 | Diseño; sólo lo resuelve GI o un sucedáneo (§4) |
| 6 | Probes del demo exterior como única "luz" de los objetos que no pasan por `Apply` (avatares, ítems, robapieles) | [V] | `m_LightingDataAsset` + 118 probes; `lightProbeUsage=Off` sólo en WG3 | **E3** |
| 7 | `Directional Light` 0,3 en un interior | [V] | §1.2 | **E3** |
| 8 | SSAO 0,9 con 1 muestra | [V] | §1.1 | **E4** |
| 9 | Tope de 256 luces visibles por cámara | [F] + [H] | §3.2 | **E5** |

Lo que **no** es causa (descartado con datos): albedo de pared/techo (§1.5), espacio de color (lineal), HDR (activo), el skybox (Joel confirmó interior sin cielo: correcto que no haya).

---

## 3. Límites duros del pipeline actual que condicionan el diseño

### 3.1 Rendering Layers como aislamiento entre plantas
Funciona (ADR-104 enm. 2) y ahora de verdad (§2 #1-2). Coste: **Deferred+ no soporta Rendering Layers** [F], así que el esquema de plantas nos ata a Forward+. No es un problema hoy; sí cierra la puerta a Deferred+ como vía a "miles de luces".

### 3.2 Luces visibles por cámara
Forward+: sin límite por objeto, **256 luces adicionales visibles por cámara en escritorio** (constante `MAX_VISIBLE_LIGHT_COUNT` del paquete `com.unity.render-pipelines.universal-config`, presente en el lock a 17.0.3) [F]. Con hasta 4 luces por tramo y 1 179 tramos en ventana [V, STATE.md:14], la pregunta es cuántas caen dentro del frustum con `m_ShadowDistance 50` y el radio de streaming. Si son más de 256, las sobrantes desaparecen sin log. Se puede subir embebiendo el paquete config, a coste de memoria de cluster. **Medir antes de tocar `MaxPerAxis`** (E5).

### 3.3 Sombras de luces adicionales
Atlas 4096 (enm. ADR-065). Una point light con sombra consume seis caras. Hoy sólo la primera lámpara de tramos ≥ 12 m proyecta; la linterna no. Si la linterna gana sombras (lo natural para una linterna), es la que debe llevarse el presupuesto; `TorchShadowCaster.cs` ya promueve una única luz.

### 3.4 Sin GI de ningún tipo
Lightmaps y APV exigen bake de escena: inútiles para un mundo servido por chunks. Realtime GI (Enlighten) no existe en URP. Conclusión: con URP 17.0.4 la única indirecta posible es **sintética** (ambiente por zona, luces de relleno, probes calculados a mano).

---

## 4. Qué cambia con Unity 6.x y qué aporta 6.7 (fuentes en §7)

| Versión | Estado (07-09-2026) | Qué trae para este proyecto | Qué rompe |
|---|---|---|---|
| **6.0 LTS** (la nuestra) | Soporte hasta **oct-2026** | — | — |
| 6.1 | Superada | **Deferred+** (sin Rendering Layers → no nos sirve); Render Graph consolidado | `AfterRendering` cambia de sitio (no usamos) |
| 6.2 | Superada | Visibility Mesh Occlusion, Mesh LOD automático | `SetupRenderPasses` deprecado (no usamos) |
| **6.3 LTS** | Soporte hasta **dic-2027** | GPU lightmapper por defecto, shader_feature/dynamic_branch, `UnifiedRayTracing` con fallback compute, Bloom Kawase/Dual, 8 UV, custom lighting en Shader Graph URP | **Compatibility Mode eliminado** (ya lo tenemos en 0); `[SerializeField]` sólo en campos (error de compilación); `Scene.handle` cambia de tipo; `AdditionalBakedProbes` API eliminada |
| 6.4 | Superada (mar-2026) | Stats de render renovadas, GRD en profiler | `URP_COMPATIBILITY_MODE` deja de existir del todo; PVRTC fuera |
| 6.5 | Superada | `additionalLightsShadowResolutionTier` en runtime (útil para `TorchShadowCaster`); ventana Lighting Search; Built-in RP deprecado (sigue soportado hasta 6.7 LTS) | Dynamic batching deprecado (ya en 0) |
| 6.6 | Supported (sep-2026) | **Unity Compute Light Baker** (lightmaps/probes/APV; no aplica a procedural), DXC, PSO caching, "fast enter play mode" por defecto | Dynamic batching eliminado; Progressive CPU baker deprecado (la escena lo tiene seleccionado) |
| **6.7 LTS** | Alpha `6000.7.0a5`; LTS "a finales de 2026" | **Surface Cache GI** (abajo); **API para crear/cargar/descargar `LightProbes` procedurales**; deferred on-tile; stencil en Shader Graph; CoreCLR experimental en player de escritorio (opt-in) | OptiX denoiser eliminado (no usamos); Unity 7 = 6.8 sin roturas anunciadas |

### 4.1 Surface Cache GI (SCGI) — lo que sabemos y lo que no
**Sabemos [F]**: URP exclusivo; GI **difusa** en tiempo real sin precálculo; discretiza posiciones y direcciones en "parches" y lanza rayos con HW RT si hay y con un backend de compute si no; pensado explícitamente para "runtime construction/destruction, procedurally generated content, time of day", y responde a luces dinámicas (linternas). Setup: define `SURFACE_CACHE`, feature en el Renderer Data, activar en el RP asset, override de Volume, **static batching desactivado**. Diseñado priorizando alcance y robustez sobre fidelidad.

**No sabemos / reportado en preview [F]**: soporte de **emisivos** (justo nuestros paneles) y de alpha clip está en la lista de preguntas abiertas del hilo oficial; usuarios reportan parpadeo/ghosting temporal, artefactos en objetos finos, GI gris en algunos materiales opacos, y agotamiento de memoria en escenas de 15 M de vértices; no se combina con GI horneada (no nos afecta); sólo difusa (el especular sigue en probes/SSR). Coste real en GPUs sin RT: **por medir en nuestro mundo**.

**Por qué importa aquí**: el aspecto "Level 0" es techo de fluorescentes + rebote uniforme; con SCGI el rebote sale del propio panel emisivo (si se confirma el soporte) o de las point lights, y donde no hay lámpara queda negro **sin tocar el ambiente**. Es la única vía en la que "oscuro al 100 % sin luz" y "Level 0" no se contradicen.

### 4.2 API de LightProbes procedurales (6.7)
La nota de release dice literalmente "Added an API for creating, loading and unloading procedurally generated LightProbes objects". Hoy ya se puede mover y retetraedralizar probes existentes (`GetPositionsSelf`/`SetPositionsSelf`/`Tetrahedralize`), pero hace falta un `LightProbes` de partida. Con la API nueva, WG3 podría **calcular sus propios SH por chunk** desde las lámparas (una integral trivial sobre point lights) y dar indirecta barata a avatares, ítems y robapieles sin SCGI. Plan B de coste cero en GPU.

### 4.3 Lo que 6.7 NO resuelve
- Rendering Layers y su ausencia en Deferred+: igual.
- Tope de 256 luces: igual (sigue siendo el paquete config).
- La niebla, el ACES y el SSAO del demo: son nuestros, no de Unity.

---

## 5. Experimentos — TODOS APLICADOS el 07-09 (Joel: «aplícalo todo, apruebo»)

Se aplicaron de golpe por decisión suya, no de uno en uno como proponía la versión anterior de esta
sección. Consecuencia asumida y escrita aquí para que nadie la descubra dentro de tres semanas: si
el resultado no gusta, **no se sabrá cuál de los siete lo causó** sin volver a separarlos. El orden
de la lista se conserva porque es el orden de reversión recomendado (deshacer E6 primero, E1 el
último).

| Exp. | Qué se hizo | Dónde |
|---|---|---|
| E1 | Niebla lineal −50/+50 → **exponencial cuadrada, densidad 0,02**, color del ambiente. Escrita en runtime, no en la escena del vendor | `Wg3ChunkStreamer.cs` `OnEnable` |
| E2 | **Volume propio de prioridad 100** en capa `PostProcessing` con Tonemapping **Neutral** y `contrast 0`, `postExposure 1.1`; y `m_ColorGradingMode` LDR → **HDR** | `Wg3ChunkStreamer.ApplyInteriorGrade`, `PC_RPAsset.asset:79` |
| E3 | Restos del demo apagados en runtime: sondas de luz a null, lightmaps vacíos, direccionales deshabilitadas, sondas de reflexión deshabilitadas, `reflectionIntensity 0` | `Wg3ChunkStreamer.StripVendorLighting` |
| E4 | SSAO `Intensity 0.9 → 0.4`, `Samples Low → Medium` | `PC_Renderer.asset:74,77` |
| E5 | Censo de luces cada 5 s: vivas y a menos de 50 m, contra el tope de 256 | `Wg3ChunkStreamer.Update` |
| E6 | **Luz de relleno** por tramo (hijo `light_fill`): punto, 18 m, 0,22× la intensidad del plafón, color lavado a medio camino del blanco, sin sombras ni parpadeo. Una por tramo, en la primera encendida | `Wg3SceneAssembler.cs` `AddSegmentLights` |
| E7 | **Ningún cambio necesario**: `TorchShadowCaster` ya es agnóstico del ítem y promociona la luz de mayor alcance del wieldable equipado — el haz de la linterna tiene 18 m frente a los 7,5 de la llama heredada. Se añadió `shadows=` al diagnóstico para confirmarlo en el log | `CrankFlashlightWieldable.cs` (sólo el log) |

Dos decisiones de método que conviene no revertir por descuido:
- **Nada se escribió en `STP_Showcase.unity` ni en `STP_DemoProfile_URP.asset`.** Son del vendor y un
  reimport del `.unitypackage` los devuelve a su sitio; ya ocurrió (ADR-065 registra que el import se
  llevó la escena entera por delante, −3 438 líneas). Todo va por código o por assets nuestros.
- **El relleno de E6 es un GameObject hijo, no un segundo `Light` sobre el plafón.** El relay de
  ADR-042 y el zumbido de ADR-107 resuelven la lámpara con `GetComponent<Light>`, que devuelve uno
  solo: dos luces en el mismo objeto habrían convertido «cuál de las dos» en el orden de componentes.

### Enunciado original de cada experimento

Cada uno es un cambio de una línea o de un asset, build de 2 min y captura con el mismo encuadre (posición (2.5, 1.5, 78) mirando −Z/+X, la de la build 4).

- **E1 · Niebla**. `RenderSettings.fog = false` en `Wg3ChunkStreamer.OnEnable` (junto al ambiente). Predicción [H]: el suelo bajo la linterna sube de ~0,10 a ~0,20 de luminancia y las paredes a 10 m salen del negro. Si se confirma, la niebla pasa a exponencial-cuadrada de densidad ~0,02 y color del ambiente, y se decide por planta (los sótanos pueden querer más).
- **E2 · Tonemapper**. En `STP_DemoProfile_URP`: ACES → **Neutral**, `contrast 8 → 0`. Comparar histograma de la captura. Después, `m_ColorGradingMode` LDR → HDR (bandas en los oscuros).
- **E3 · Restos del demo**. Desactivar el `Directional Light`, vaciar `m_LightingDataAsset`, desactivar el `LightProbeGroup` y el `ReflectionProbe` del WorldManager. Efecto esperado: avatares e ítems dejan de brillar con luz de exterior en un mundo negro; B3 deja de recibir el direccional.
- **E4 · SSAO**. `Intensity 0.9 → 0.4`, `Samples Low → Medium`. Sólo estética; barato.
- **E5 · Presupuesto de luces**. Contar `Light` activas dentro del frustum en la posición de la captura (script de diagnóstico, un log). Si > 256, decidir: menos luces por tramo con más alcance, o embeber `universal-config` y subir el tope. **Sólo después** de esto tiene sentido "más lámparas".
- **E6 · Sucedáneo de rebote (mientras no haya SCGI)**. Dos opciones, medir ambas: (a) por cada lámpara encendida, una segunda point light de alcance 2× e intensidad 0,25× sin sombra (dobla el gasto de luces; choca con E5); (b) ambiente **por planta y por tramo** calculado en el assembler desde la densidad de lámparas encendidas, aplicado vía `MaterialPropertyBlock` a un `_AmbientBoost` en un shader propio derivado de URP/Lit. (b) es más trabajo y más fiel al "oscuro donde no hay luz".
- **E7 · Sombras de la linterna**. `LightShadows.Soft` sólo en el haz, vía `TorchShadowCaster`. Es lo que más vende una linterna y cuesta un slot del atlas.

---

## 5 bis. Resultado medido tras aplicar E1–E7 (build 6, 07-09 23:53)

Todo el código nuevo corre, confirmado por el propio log:
```
[wg3-diag] restos del demo apagados: direccionales=1 reflexiones=1
[wg3-diag] ambient=Flat/RGBA(0,0,0,1) probes=0 fog=ExponentialSquared/0,02
[crank-diag] lit=True charge=0,94 intensity=4,00 layers=8 shadows=Soft
```
- `probes=0` (antes 117): las sondas del demo ya no iluminan a nadie.
- `shadows=Soft` en el haz: **E7 confirmado sin escribir una línea de comportamiento**. El gancho
  de la enmienda a ADR-065 hacía ya lo correcto; lo que faltaba era mirarlo.
- La captura del pasillo pasa de negro con un charco a Level 0 legible: paredes beige, rejilla de
  fluorescentes en el techo, profundidad hasta el fondo del pasillo.

### El censo, y por qué su primera versión medía mal
La primera versión contaba luces vivas dentro de una **esfera de 50 m** y dio `vivas=309
a_menos_de_50m=309` contra un tope de 256 — un número alarmante y **engañoso**. El tope de URP se
aplica a las luces que el culling entrega **para la cámara**, no a las que rodean al jugador: esa
esfera cuenta también todo lo que queda detrás de la nuca y al otro lado de las paredes. La captura
no muestra ningún parpadeo de luces apareciendo y desapareciendo, que es como se manifestaría el
recorte, así que el número esférico y los síntomas no concuerdan.

Corregido a **`GeometryUtility.TestPlanesAABB` contra el frustum real** (build 7). Ocho medidas
seguidas caminando por el mundo:

| vivas (tras el culling de 30 m) | en frustum |
|---|---|
| 278 | 192 |
| 278 | 223 |
| **294** | **249** |
| 294 | 238 |
| 292 | 223 |
| 282 | 215 |
| 261 | 214 |

**Conclusión: no hay recorte, pero no cabe ni una lámpara más.** El pico medido es 249 contra un
tope de 256, y la medida es además conservadora por arriba (la prueba usa una caja de lado
2 × alcance por luz, más generosa que la esfera real), así que el número verdadero está algo por
debajo. Coherente con lo observado: en ninguna captura hay luces apareciendo o desapareciendo.

Esto **responde la pregunta original de Joel** —«subir la cantidad de lámparas»—: con el pipeline
tal cual, más lámparas no caben. `MaxPerAxis` sigue en 2 por eso, no por conservadurismo.
Las tres salidas, por coste creciente y **ninguna aplicada** (queda fuera de lo aprobado como E1–E7,
que era medir):
1. Bajar `lightCullDistance` de 30 m. Gratis, reversible, y se paga en luces del fondo del pasillo
   que se apagan a la vista: justo lo que hace bonita la captura de la build 7.
2. Repartir distinto: menos plafones con más alcance por tramo. No cambia el total de luz, sí el censo.
3. Embeber `com.unity.render-pipelines.universal-config` y subir `MAX_VISIBLE_LIGHT_COUNT`. Es la
   única que permite MÁS lámparas de verdad. Toca un paquete del pipeline, o sea que afecta a todos
   los worktrees que comparten el editor, y el coste sube en el clustering de cada frame.

**Coste medido de E1–E7**: de 60 FPS a 56-58 en la misma máquina y encuadre. La luz de relleno de
E6 añade una puntual de 18 m por tramo y la oclusión ambiental pasó de 1 muestra a 2. Aceptable,
anotado por si alguien mide una regresión y no sabe de dónde viene.

## 6. Evidencia de esta sesión
- Build 3 (capas de render sin definir): mundo negro salvo la geometría del demo. `[crank-diag] lit=True charge=1.00 intensity=4.00 layers=8` y aun así nada iluminado.
- Build 4 (`TagManager` con `Wg3 Storey 1..7`): `[wg3-diag] ambient=Flat/RGBA(0,0,0,1) probes=117`; captura `capture_build4.png`: charco de luz de la linterna en el suelo, vano del fondo iluminado por una lámpara WG3, paredes a media distancia negras (compatible con E1/E5).
- Build 5 abortada: `error CS0136` — la variable `eye` del censo de E5 chocaba con otra del mismo
  `Update`. Renombrada a `censusEye`. Vale como recordatorio de que el compile-check por `.csproj`
  da falso verde y el build es la única verificación real.
- Diff sin commitear (6 ficheros, +149/−40 antes de aplicar E1–E7): `Wg3SceneAssembler.cs`, `Wg3ChunkStreamer.cs`, `Wg3LightCadence.cs`, `CrankFlashlightWieldable.cs` (incluye logs `[crank-diag]`/`[wg3-diag]` temporales), `Wg3_Floor.mat`, `TagManager.asset`. Los `Assets/Editor/_Claude*` son runners locales y no se commitean.

---

## 7. Fuentes externas
- Unity 6 releases y soporte (6.0 LTS hasta oct-2026, 6.3 LTS hasta dic-2027): https://unity.com/releases/unity-6/support
- Estado de versiones a 04-09-2026 (6.6 publicada 01-09-2026, 6.7 LTS "later this year", Unity 7 beta dic-2026): https://makaka.org/unity-tutorials/best-version
- Roadmap 6.4–6.8 (SCGI en 6.7, CoreCLR por fases): https://www.strayspark.studio/blog/unity-6-4-to-6-8-roadmap-indie-developers-2026
- What's new 6.3: https://docs.unity3d.com/6000.3/Documentation/Manual/WhatsNewUnity63.html · 6.4: https://docs.unity3d.com/6000.4/Documentation/Manual/WhatsNewUnity64.html · 6.5: https://docs.unity3d.com/6000.5/Documentation/Manual/WhatsNewUnity65.html · 6.6: https://docs.unity3d.com/6000.6/Documentation/Manual/WhatsNewUnity66.html
- Upgrade guide 6.2: https://docs.unity3d.com/6000.4/Documentation/Manual/UpgradeGuideUnity62.html · 6.3: https://docs.unity3d.com/6000.3/Documentation/Manual/UpgradeGuideUnity63.html
- Notas 6000.7.0a5 (LightProbes procedurales, deferred on-tile): https://unity.com/releases/editor/alpha/6000.7.0a5
- Surface Cache GI, hilo oficial de preview: https://discussions.unity.com/t/surface-cache-gi-preview/1720494 · anuncio: https://x.com/unity/status/2079377478997459307 · resumen técnico: https://gamefromscratch.com/unity-getting-real-time-global-illumination/
- Rendering paths (Deferred+ sin Rendering Layers): https://docs.unity3d.com/6000.1/Documentation/Manual/urp/rendering-paths-comparison.html
- Límites de luces en URP: https://docs.unity3d.com/6000.1/Documentation/Manual/urp/lighting/light-limits-in-urp.html · Forward+ y `MAX_VISIBLE_LIGHT_COUNT`: https://docs.unity3d.com/6000.0/Documentation/Manual/urp/rendering/forward-rendering-paths.html
- `Light.renderingLayerMask` vs `UniversalAdditionalLightData.renderingLayers`: https://issuetracker.unity3d.com/issues/gameobject-does-not-receive-light-but-casts-shadows-when-setting-light-dot-renderinglayermask-in-play-mode
- Changelog URP 17.x: https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.4/changelog/CHANGELOG.html
- APV en URP (por qué no aplica a un mundo servido): https://docs.unity3d.com/6000.4/Documentation/Manual/urp/probevolumes.html
