# AUDITORÍA — Sistema de animación (2026-09-03)

Solo lectura. Cero cambios de código. Todo lo que sigue está verificado en el árbol; lo que no
se encontró se declara ausente explícitamente.

---

## 1. ESTADO ACTUAL

Hay **DOS sistemas de animación separados**, sin nada en común:

### A. Primera persona (jugador local) — vendor PolymindGames FPSCore
- No hay cuerpo. `STP_Player.prefab` y `FPS_Player.prefab` tienen **0 `SkinnedMeshRenderer`** y
  **0 `Animator`** en su raíz. El jugador local no tiene esqueleto, ni piernas, ni sombra propia.
- Lo único animado es el **wieldable + los brazos** (`WieldableArmsHandler`, un `Animator` sobre
  el set de brazos, sujeto al mixer por `ParentConstraint`).
- Todo lo que "se siente" de movimiento en 1P (bob, sway, retroceso, salto, caída, lean) **no es
  animación**: es el `MotionMixer` procedural del vendor (`FPSCore/Code/Runtime/Motion/`),
  muelles (`Spring1D/2D/3D`) y datos (`BobMotionData`, `SwayMotionData`, `NoiseMotionData`…).

### B. Tercera persona (peers, robapieles, facelings) — sistema PROPIO
- `ProxyLocomotionController.controller` (`Assets/_Migration/STPIntegration/RemoteAvatar/`),
  **generado por script** (`Editor/ProxyAnimatorControllerBuilder.cs`) — no se edita a mano,
  un rebuild lo sobrescribe (ADR-012).
- **Una sola capa** (Base Layer), 3 estados: `Movement` (BlendTree), `Jump`, `Pickup`.
- Encima, **~20 MonoBehaviours "hook"** que escriben huesos a mano en `LateUpdate`.

Ambos conviven porque **nunca se cruzan**: el jugador local se ve a sí mismo por el sistema A y
ve a los demás por el sistema B.

---

## 2. COMPONENTES DE UNITY EN USO

| Componente | Estado | Dónde |
|---|---|---|
| `Animator` | **SÍ** | proxy remoto, corpse, facelings, real form del robapieles, brazos wieldable, UI |
| `AnimatorController` | **SÍ** | `ProxyLocomotionController` (propio, generado); `Template_*` del vendor |
| `AnimatorOverrideController` | **SÍ (vendor)**, sustituido en el proxy | `STP_MaleSurvivor.overrideController`, `STP_Carryable_*` |
| `Avatar` | **SÍ — HUMANOID** | `MaleSurvivor.fbx` `animationType: 3`; clips de `RemoteAvatar/Animations` idem |
| Blend Trees | **SÍ, uno** | `Movement`: 2D FreeformCartesian, X=`MovementSpeed`, Y=`Crouched` |
| Animation Layers | **NO en el proxy** (1 capa). Vendor: 2 en `STP_Template_Human`, ~4 en `Template_FirearmDefault` |
| Avatar Masks | **NO usadas por nosotros**. `STP_Character_UpperBody.mask` existe y solo la referencia el controller VENDOR. ADR-012 dice explícitamente que la máscara se **quitó**: amputaba el gesto de recoger del suelo |
| IK | **NO EXISTE**. Cero `OnAnimatorIK`, cero `SetIKPosition` en todo el proyecto |
| Animation Rigging | **NO INSTALADO**. No está en `Packages/manifest.json` |
| Root Motion | **ON por herencia, inerte de hecho**: `MTP_PlayerViewer` tiene `m_ApplyRootMotion: 1` y el variant `RemotePlayerAvatar` no lo pisa; los builders de facelings/robapieles sí lo apagan a mano. En el proxy da igual: `RemotePlayerManager` escribe `root.position` cada frame |
| State Machines | **SÍ, trivial**: AnyState→Jump / AnyState→Pickup, vuelta a `Movement` por exitTime |
| Animation Events | **NO**. `m_Events: []` en los clips inspeccionados; ninguna ruta de gameplay depende de un evento de clip |
| Playables API | **NO**. Considerada y rechazada en ADR-012 |
| Timeline | Paquete instalado (`com.unity.timeline`), **sin uso en animación de personaje** |
| `CharacterRagdoll` (vendor) | **SÍ** — cadáveres (`CorpseSpawner`, `CorpseRagdollFreezer`) |

---

## 3. CÓMO SE CONTROLAN LAS ANIMACIONES DESDE CÓDIGO

### 1P (vendor)
`IAnimatorController` (SetFloat/SetBool/SetInteger/SetTrigger/ResetTrigger sobre hashes) con tres
implementaciones: `CharacterAnimator`, `WieldableAnimator`, `WieldableArmsAnimator`, más
`NullAnimator`/`MultiAnimator`. Los nombres viven en `AnimationConstants` (hashes precomputados:
`Equip`, `Holster`, `Shoot`, `IsAiming`, `IsReloading`, `Attack`…). `AnimatorEffectTranslator`
mapea efectos serializados → parámetros.

### 3P (nuestro)
**Nadie llama al Animator desde la lógica de juego.** Solo dos escritores:
- `ProxyLocomotionFeeder` → `SetFloat("MovementSpeed", tier)` derivado de la velocidad planar
  reconstruida del propio Transform interpolado (con guard de teleport). Tiers 0/1/3, no m/s.
- `ProxyCrouchHook` → `SetFloat("Crouched", 0..1)`.
- `ProxyJumpFeeder` → `SetTrigger("Jump")` por flanco de velocidad vertical.
- `ProxyPickupHook` → `SetTrigger("Pickup")` por flanco de `view.animation == "pickup"`.

**Todo lo demás son poses procedurales escritas a mano sobre huesos**, en `LateUpdate`, después
del Animator, resueltas **por nombre** (`ProxyRigUtil.FindBone`) y aplicadas como **rotación
aditiva en espacio de mundo** (`ProxyRigUtil.RotateBoneAdditive`), con blend por peso:

| Hook | Qué hace | Fuente |
|---|---|---|
| `ProxyPitchHook` | cabeceo cabeza/cuello/espina | `view.pitch` (ADR-021) |
| `ProxyLeanHook` | alabeo de columna (Q/E) | bits 2-3 de `buttons` |
| `ProxyStanceHook` | apuntar / recargar (brazos) | bits 0-1 de `buttons` (ADR-044) |
| `ProxyMeleeHook` | arco de brazo del golpe | delta de `melee_seq` |
| `ProxyHitReactionHook` | sacudida de torso | delta de `hit_seq` (ADR-024) |
| `ProxyGrabHook` | dos brazos convergen sobre la víctima (FK de dos huesos) | estático de `PhantomAttackHandler` |
| `ProxyCarryHook` | pose de brazo izquierdo + N modelos apilados | `carry_def`/`carry_count` |
| `ProxyHeldItemHook` | modelo en `Hand.R` + curvado paramétrico de los 15 huesos de dedos | `held_item` + `GripPoseSet` |
| `ProxyGroundingHook` | raycast al suelo RENDERIZADO y desplaza `Pelvis` | geometría cliente |
| `ProxyClothingHook`, `ProxyLightHook`, `ProxyRevealHook`, `ProxySprayHook` | cosmética no-esqueleto | varios |

Criterio declarado y repetido en cada hook: **procedural en vez de clip**, porque el builder
rehace el controller desde cero en cada bake y una capa autorada a mano se perdería.

### Local, un caso aparte
`WristPoseOverride.cs` (nuestro): captura las `localRotation` de un subárbol de huesos del brazo
del wieldable y las **reescribe absolutas** cada frame (execution order 200, por encima del
mixer). `docs/STATE.md:921` lo marca **código muerto desde el overlay del reloj**.

---

## 4. MULTIPLAYER — cómo se sincroniza

**Regla de oro del proyecto: no se replica animación. Se replica ESTADO, y el cliente anima.**
(ADR-013.)

Lo que viaja en `PacketPayload::PlayerUpdate` (`backend/src/network/protocol.rs:737+`), a 10 Hz,
unreliable, todo marcado *cosmetic*:

```
position[3], rotation(yaw), animation:String, crouch, pitch:i8, equipment[4],
held_item, hit_seq, dead, revealed, light_on, fire_seq, buttons:u16, melee_seq,
vocal_seq, vocal_kind, carry_def, carry_count, species
```

- `animation:String` es un **enum abierto de presentación** con UN valor a la vez
  (`idle` / `walk_slow` / `pickup`). El backend lo deriva (`sync.rs:852`), NO el cliente. En la
  práctica solo `pickup` importa: la locomoción NO se lee de aquí.
- `buttons:u16` es el bitfield de estados **sostenidos** (`RemoteButtons`: Aiming, Reloading,
  LeanLeft, LeanRight, Spraying). Append-only, **11 bits libres** — la vía barata para todo
  estado nuevo (cero wire, cero ADR).
- `*_seq:u8` son contadores monótonos para eventos **transitorios** (leídos como delta, porque a
  10 Hz dos eventos caben en una muestra).
- El emisor es `PlayerPoseTransmitter` (~30 Hz, IPC → backend), que **muestrea el estado del
  vendor**, nunca el input crudo.
- Reparto: `broadcast_player_update` (directo) + `broadcast_peer_poses` (relay del host con
  `sender_id` ajeno, ADR-015) + AOI de 100 m con LOD de cadencia (ADR-074).
- El robapieles y los facelings **son peers sintéticos** en `net.peers` (ADR-016/094): usan
  exactamente el mismo camino y el mismo controller.

---

## 5. QUÉ VE UNO DE OTRO JUGADOR (tercera persona)

`RemotePlayerManager` (737 líneas) mantiene un pool de `RemotePlayerAvatar.prefab` (variant de
`MTP_PlayerViewer`, cargado por `Resources.Load`), interpola posición (lerp exponencial,
`positionSmoothing = 22`) y yaw (`SmoothDampAngle`, `rotationSmoothTime = 0.1`).

Se ve, hoy, todo esto: caminar/correr (derivado de velocidad), agacharse (blend 2D), saltar,
recoger, cabecear con la cámara, inclinarse, apuntar, recargar, golpear cuerpo a cuerpo, encajar
un golpe, la ropa, el objeto en la mano con agarre por categoría, las tablas que carga, la
linterna encendida, el chorro de spray, la voz, los pasos, la muerte (ragdoll) y la forma real
del robapieles.

Fidelidad real: **es un mosaico de aproximaciones**, no un cuerpo animado. El torso lo escriben
seis hooks distintos sumando rotaciones sobre los mismos huesos.

---

## 6. MANOS, PIES, OBJETOS Y INTERACCIONES

- **Manos (3P)**: `Hand.R` = objeto en mano (`ProxyHeldItemHook` + `GripPoseSet`, curvado
  paramétrico de 15 huesos). `Hand.L` = carga (`ProxyCarryHook` + `CarryPoseSet`).
  **Limitación conocida y documentada**: el agarre es solo mano derecha; un arma a dos manos deja
  la izquierda suelta en el aire ([memoria] Slice 2 grip).
- **Pies (3P)**: **no hay IK de pies**. Solo `ProxyGroundingHook`, que desplaza el `Pelvis` entero
  en Y por un raycast — corrige el flotar/hundirse, no coloca los pies. En rampas y escaleras los
  pies siguen sin apoyarse.
- **Manos (1P)**: brazos del wieldable, clip por acción del `Template_*` correspondiente. Ninguna
  IK, ninguna adaptación a la geometría.
- **Interacciones**: `pickup` es la ÚNICA con animación de cuerpo entero en 3P. Construir,
  desmontar, abrir contenedores, plantar, comer: **cero animación en 3P**; en 1P es la animación
  genérica `Use` del wieldable.

---

## 7. ANIMACIONES EXISTENTES Y DÓNDE ESTÁN

**54 `.anim` en todo el proyecto**, y la mayoría son de UI (paneles, botones, HUD de munición).

### Locomoción 3P — `Assets/_Migration/STPIntegration/RemoteAvatar/Animations/` (6 FBX, Mixamo, Humanoid)
`Standard Walk`, `Standard Run`, `Jumping`, `Picking Up`, `CrouchIdle`, `CrouchedWalking`.
Idle = `PolymindGames/STP/Art/Models/Characters/MaleSurvivor/MaleSurvivor_Idle.anim` (vendor).

### Vendor STP — `PolymindGames/STP/Art/Animations/Characters/Templates/` (5)
`STP_Template_Idle/Walk/Run/GetHitNormal/GetHitHard`. Las dos de golpe **no las usa el proxy**
(el flinch es procedural).

### Vendor FPSCore wieldables — `FPSCore/Art/Animations/Wieldables/Templates/` (~22)
`Equip`, `Holster`, `Idle`, `Hold`, `Run`, `Jump`, `Fall`, `FallImpact`, `Aim`, `AimCancel`,
`AimShoot`, `Shoot`, `DryFire`, `Reload`, `BeginReload`, `EmptyReload`, `EndReload`,
`Attack1/2/3`, `Throw`, `Use`, `StartCharge`, `StopCharge`. Son **plantillas**: cada arma trae su
propio override.

### Resumen crudo
Para el cuerpo entero hay **9 clips de locomoción/acción**. Nada más.

---

## 8. ASSETS / PAQUETES DE ANIMACIÓN DISPONIBLES

- **Instalado**: `com.unity.modules.animation` (Mecanim base), `com.unity.timeline`,
  `com.unity.ai.navigation`, `com.unity.visualscripting`.
- **NO instalado**: `com.unity.animation.rigging`, ningún paquete de Motion Matching, ningún
  Kinematica (retirado por Unity hace años), ningún asset de tienda de animación.
- **Contenido de animación de terceros**: PolymindGames FPSCore + STP (los `Template_*`), y 6 FBX
  de Mixamo importados a mano.
- Vendedores no relacionados presentes que NO aportan animación de personaje: AK Studio Art
  (una `Elevator Animator Controller.controller`), GroceryStoreProps, Meshy/Tripo3D (generación
  de malla).

---

## 9. SISTEMAS EXTERNOS EN USO

1. **PolymindGames FPSCore** — animación de wieldables + `MotionMixer` procedural. Es el 100 % de
   la sensación de primera persona.
2. **PolymindGames STP** (Survival Template Pro) — el esqueleto `MaleSurvivor`, el prefab
   `MTP_PlayerViewer` del que hereda el proxy, `CharacterRagdoll`, la base de datos de superficies
   (que los pasos y el golpe reutilizan para sonido).
3. **Mixamo** — los 6 clips de locomoción.
4. Regla dura del proyecto: **nunca editar el vendor** (`docs/systems/vendor-patches.md`,
   [memoria] `stp-no-direct-edits`); se trabaja con clases puente y hooks externos.

---

## 10. QUÉ ES NUESTRO Y QUÉ SE REUTILIZA

### Nuestro (~10 300 líneas bajo `_Migration/STPIntegration/`)
- El controller generado y su builder.
- Los ~20 hooks de proxy.
- Los 8 builders de Editor (proxy, corpse, faceling adulto/niño y sus formas reales, robapieles).
- `RemotePlayerManager`, `PlayerPoseTransmitter`, `RemoteButtons`, `GripPoseSet`, `CarryPoseSet`.

### Reutilizable tal cual en cualquier arquitectura futura
- **El wire**: 19 campos cosméticos ya plumbed end-to-end + 11 bits libres en `buttons`. Esto es
  la parte cara y **no hay que rehacerla**.
- **El pipeline Humanoid**: los clips retargetean a cualquier avatar humanoide válido — el
  robapieles y los facelings ya lo explotan.
- El `MotionMixer` del vendor para primera persona.
- El sistema de builders reproducibles (todo se re-hornea por menú).

### A jubilar cuando llegue el sustituto
- Las poses procedurales de torso/brazos (`Stance`, `Lean`, `Melee`, `HitReaction`) — son
  placeholders declarados como tales en su propio doc-comment.
- `WristPoseOverride.cs` (ya muerto).

---

## 11. HALLAZGOS QUE CAMBIAN EL PLAN

1. **El rig ES HUMANOID, no Generic.** Los doc-comments de casi todos los hooks afirman
   "GENERIC rig → no `Animator.GetBoneTransform`, no `OnAnimatorIK`" y **es falso**:
   `MaleSurvivor.fbx.meta` → `animationType: 3` y los seis FBX de Mixamo igual.
   `PhantomRealFormBuilder` ya lo dice ("the doc comment … is stale; the .meta files are the
   authority"). **Consecuencia: IK nativa y Animation Rigging están disponibles HOY**, sin
   cambiar rig ni reimportar nada. Esto abarata radicalmente la fase de IK.
2. **Seis hooks escriben los mismos huesos de columna/brazos sin árbitro.** Se suman
   (rotaciones aditivas, peso 0 = no escribe), lo cual funciona por convención, no por diseño.
   Es exactamente lo que las capas + máscaras existen para resolver.
3. **`m_ApplyRootMotion: 1` heredado en el proxy.** Inofensivo hoy porque la red pisa la posición
   cada frame, pero es una mina para cualquier futuro que quiera root motion o motion warping.
4. **Una sola capa en el controller.** Añadir una capa upper-body es barato (una función más en
   el builder) y desbloquea acciones simultáneas — lo que ADR-011 dejó explícitamente fuera.
5. **Cobertura de tests: casi nula.** Solo `RemotePlayerManagerTests` y `IPCMessagesParityTests`
   tocan este territorio. Ningún test cubre un hook de pose.

---

# PROPUESTA

## 12. COMPATIBILIDAD CON EL SISTEMA OBJETIVO

| Tecnología | Veredicto | Dónde encaja |
|---|---|---|
| **Blend Trees** | **PARCIAL** | Ya hay uno 2D (velocidad × crouch). Falta el eje de DIRECCIÓN: hoy el proxy no tiene strafe ni marcha atrás — camina de frente siempre. Ampliar a 2D direccional (X=lateral, Y=frontal) + `Crouched` como capa/parámetro es el cambio de mayor retorno por línea de todo el informe |
| **Animation Layers** | **NO EXISTE** (1 capa) | Necesario para separar locomoción (piernas) de acción (torso/brazos). Es la vía correcta de jubilar `ProxyStanceHook`/`ProxyMeleeHook` y de romper el límite de "una acción a la vez" de ADR-011 |
| **Avatar Masks** | **NO EXISTE** para nosotros (existe una del vendor, sin usar) | Va de la mano de la capa anterior. Ojo al precedente: ADR-012 quitó la máscara porque amputaba `pickup`, que necesita las piernas — la lección es que la máscara va en la CAPA DE ACCIÓN, con `pickup` quedándose full-body |
| **IK / Animation Rigging** | **NO EXISTE — pero el rig ya lo admite** | Tres usos claros, por orden de valor: (a) **pies** sobre rampas y escaleras (hoy solo hay corrección de pelvis); (b) **mirada/cabeza** — sustituye a `ProxyPitchHook` por algo que además pueda mirar objetivos; (c) **manos sobre el arma** — resuelve la mano izquierda suelta de las armas a dos manos. Requiere instalar `com.unity.animation.rigging` |
| **Motion Warping** | **NO EXISTE, y hoy NO ES NECESARIO** | Warping sirve para alinear una animación con un objetivo del mundo (agarrar un borde, saltar a un sitio exacto, un golpe que impacta donde toca). Nada del loop actual lo pide. **Además hay un bloqueo arquitectónico**: la posición del proxy la manda la red y `ProxyLocomotionFeeder` DERIVA la velocidad del transform — un warp que mueva la raíz se leería como una carrera falsa. Si algún día entra, entra en el jugador local primero |
| **Motion Matching** | **NO EXISTE, y NO SE RECOMIENDA** | Tres razones concretas, no de gusto: (1) necesita una **base de datos de decenas de minutos** de captura; tenemos **9 clips**, y el coste de arte es de otro orden de magnitud; (2) come CPU por personaje y aquí hay N peers + robapieles + facelings, todos con el mismo controller; (3) su entrada natural es la **trayectoria futura del input**, y de un peer remoto **no tenemos input, ni futuro** — solo posiciones pasadas a 10 Hz con un lerp encima. Un blend tree direccional bien alimentado da el 80 % del resultado por el 2 % del coste. **Reevaluar solo si algún día se compra una librería de captura completa** |
| **Locomoción procedural/contextual** | **PARCIAL** | Ya existe en su forma más cruda (`ProxyGroundingHook`, y los hooks de pose son procedurales por definición). Falta lo contextual: no hay noción de superficie, pendiente, ni espacio estrecho |

## 13. ARQUITECTURA OBJETIVO

Manteniendo intactos el wire, el transmisor y `RemotePlayerManager`:

```
[LOCAL]                                    [REMOTO — un proxy por peer]
input vendor                               PlayerUpdate 10 Hz (19 campos, sin tocar)
   ↓                                          ↓
FPSCore motor + MotionMixer                RemotePlayerManager (interpola pos + yaw)
   ↓                                          ↓
PlayerPoseTransmitter (30 Hz)  ──wire──►   ProxyState  ← NUEVO: un solo lector del view,
   ↓                                       │            resuelve conflictos, publica
brazos wieldable (Animator)                │            speed/dir/crouch/stance/acción
                                           ↓
                                   Base Layer  ── BlendTree 2D direccional (X lateral,
                                           │      Y frontal) × Crouched
                                           ↓
                                   Action Layer ── AvatarMask torso+brazos: aim, reload,
                                           │        melee, carry. Pickup sigue full-body
                                           ↓
                                   Additive Layer ── flinch, lean, pitch (lo que hoy
                                           │           hacen los hooks, ahora como clips
                                           │           o como aditivo procedural con árbitro)
                                           ↓
                                   Rig Layer (Animation Rigging)
                                           │  · pies al suelo (two-bone + raycast)
                                           │  · cabeza/mirada (multi-aim, sustituye PitchHook)
                                           │  · mano izquierda al arma (two-bone)
                                           ↓
                                   Poses residuales (grab del robapieles, dedos)
                                           ↓
                                       Final Character
```

**Lo que NO cambia**: el protocolo, `PlayerPoseTransmitter`, `RemotePlayerManager`, el pool, la
lógica de facelings/robapieles, los builders de Editor, `CorpseSpawner`.

**Lo que cambia**: `ProxyAnimatorControllerBuilder` gana capas; los hooks dejan de escribir
huesos directamente y pasan a escribir parámetros o pesos de rig.

## 14. PLAN DE MIGRACIÓN

### FASE 1 — Sincerar el terreno (sin tocar comportamiento)
- **Modifica**: doc-comments de los hooks que afirman "GENERIC rig"; `m_ApplyRootMotion` del
  variant del proxy (a 0, explícito).
- **Afecta**: ~12 ficheros bajo `RemoteAvatar/`, `RemoteAvatarPrefabBuilder`.
- **Reutiliza**: todo.
- **Riesgo**: mínimo. **Dificultad**: baja.
- **Resultado**: la siguiente fase no se planifica sobre una premisa falsa. Requisito real,
  porque el "es Generic" ha frenado ya varias decisiones de diseño.

### FASE 2 — Blend tree direccional
- **Modifica**: `ProxyAnimatorControllerBuilder.AddMovementState`, `ProxyLocomotionFeeder`
  (pasa de escalar a vector local: velocidad proyectada sobre `transform.forward`/`right`).
- **Necesita**: 6-8 clips nuevos (walk/run × adelante/atrás/izq/der; strafe agachado si se quiere).
- **Riesgo**: bajo — el guard de teleport y el suavizado ya están resueltos y probados.
- **Dificultad**: media (el coste es de ARTE, no de código).
- **Resultado**: un peer que se aleja andando de espaldas deja de caminar de frente. Es el salto
  de calidad visible más grande de todo el plan.

### FASE 3 — Capa de acción + máscara
- **Modifica**: el builder (capa 1 + `AvatarMask` propia, autorada, no la del vendor);
  `ProxyStanceHook` y `ProxyMeleeHook` pasan de escribir huesos a escribir parámetros.
- **Necesita**: clips de aim/reload/melee para el cuerpo (hoy no hay ninguno).
- **Riesgo**: MEDIO — es donde ADR-012 ya se quemó con la máscara. `Pickup` se queda full-body.
- **Dificultad**: media.
- **Resultado**: apuntar mientras se camina, recargar mientras se corre. Rompe el límite de
  "una acción a la vez" sin tocar el wire.

### FASE 4 — Animation Rigging: pies
- **Modifica**: `Packages/manifest.json` (+`com.unity.animation.rigging`);
  `RemoteAvatarPrefabBuilder` (cablear `RigBuilder` + dos `TwoBoneIKConstraint`);
  `ProxyGroundingHook` pasa de mover el pelvis a alimentar los targets de IK.
- **Riesgo**: MEDIO — un paquete nuevo, y hay que medir el coste con N proxies. Mitigación
  obvia: peso de rig a 0 más allá de X metros.
- **Dificultad**: media-alta.
- **Resultado**: los pies se apoyan en rampas y escaleras. Con las plantas altas de WG3 esto pasa
  de cosmético a necesario.

### FASE 5 — Animation Rigging: cabeza y mano izquierda
- **Modifica**: `ProxyPitchHook` → `MultiAimConstraint`; `ProxyHeldItemHook` gana un
  `TwoBoneIKConstraint` para `Hand.L` sobre el `GripPoseSet`.
- **Riesgo**: bajo (encima de la fase 4 ya montada).
- **Dificultad**: media.
- **Resultado**: cierra la limitación conocida de las armas a dos manos; la mirada pasa a poder
  seguir objetivos (útil de verdad para el robapieles).

### FASE 6 — Árbitro de poses
- **Modifica**: introducir `ProxyState` (un lector del view, N consumidores) y retirar los hooks
  ya jubilados por las fases 3-5.
- **Riesgo**: bajo si va al final; ALTO si se hace antes (tocaría lo que aún está en uso).
- **Dificultad**: media.
- **Resultado**: un solo sitio donde se decide qué escribe cada hueso. Menos código que hoy.

### NO PLANIFICADO A PROPÓSITO
**Motion Matching** y **Motion Warping**. Motivos en §12. Reabrir solo con (a) una librería de
captura comprada, o (b) una mecánica concreta que exija alinear una animación con un punto del
mundo (trepar, vault, ejecución cuerpo a cuerpo).

---

## 15. LO QUE NO EXISTE — declaración explícita

Ninguna de estas cosas está en el proyecto, ni parcialmente:
IK de cualquier tipo · Animation Rigging · Motion Matching · Motion Warping · capas de animación
propias · máscaras de avatar propias · Animation Events de gameplay · root motion efectivo ·
cuerpo visible del jugador local · animación de construir/desmontar/consumir en 3P · strafe o
marcha atrás · IK de pies · giro en el sitio · transiciones de start/stop de locomoción.
