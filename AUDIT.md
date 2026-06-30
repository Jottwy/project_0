# AUDIT.md — Auditoría de código STP (Survival Template Pro / PolymindGames)

> Objetivo: documentar las APIs públicas de STP relevantes para 8 sistemas, de cara a
> integrarlos **sin tocar `Assets/PolymindGames/`** (toda integración va en
> `Assets/_Migration/STPIntegration/` o sistemas propios).
> Solo auditoría. No se ha implementado nada.
>
> Convenciones de acceso STP (patrón transversal):
> - Todo cuelga de `ICharacter` (`PolymindGames/FPSCore/Code/Runtime/Core/ICharacter.cs`).
> - Componentes de personaje se obtienen con `character.GetCC<T>()` / `character.TryGetCC(out T)` donde `T : ICharacterComponent`.
> - Accesos directos: `character.HealthManager`, `character.Inventory`, `character.Audio`, `character.Animator`, `character.GetTransformOfBodyPoint(BodyPoint)`.
> - El personaje FPS es **primera persona**. No existe rig/cuerpo de tercera persona en STP.

---

## 1. EQUIPMENT / CLOTHING

- **Clases encontradas:**
  - `EquipAction` — `FPSCore/.../Inventory/Item/Actions/EquipAction.cs` (única clase con "Equip" en runtime).
  - `WieldableArmsHandler` / `IWieldableArmsHandlerCC` — `FPSCore/.../Wieldables/Components/WieldableArmsHandler.cs`.
  - **NO existe:** `Wearable`, `WearableItem`, `ClothingSlot`, `EquipmentSlot`, `CharacterEquipment`, `ItemEquipper`, `Loadout`. (Glob `*Wearable*`, `*Clothing*`, `*Loadout*` → sin resultados.)
  - **⚠️ CORRECCIÓN (2026-06-30, ADR-022):** lo anterior es ERRÓNEO. STP **SÍ** trae un sistema de ropa corporal completo: `CharacterClothing` (`STP/Code/Runtime/Utilities/CharacterClothing.cs`) que gestiona 4 slots `Head/Torso/Legs/Feet` vía contenedores de inventario tagueados (`ItemConstants.*EquipmentTag`), con preview 3P (`CharacterPreviewUI`). Equipar = `SetClothing(BodyPoint, itemId)` togglea `SkinnedMeshRenderer` pre-colocados + opacity mask. El glob `*Clothing*` debió encontrar `CharacterClothing.cs`. Ver ADR-022 y STATE.md §[E].

- **API pública relevante:**
  - `EquipAction : ItemAction` — **no es un sistema de equipo de ropa**. Es una `ItemAction` (ScriptableObject) que, al ejecutarse sobre un `ItemStack`, busca el contenedor por `Tag` del item y, si es `WieldableTag`, selecciona ese wieldable en mano vía `IWieldableInventoryCC.SelectAtIndex(...)`. Es "equipar arma/herramienta", no "vestir".
  - `WieldableArmsHandler` gestiona los brazos FPS por `ArmSet[]`:
    ```csharp
    private struct ArmSet { public string Name; public SkinnedMeshRenderer LeftArm; public SkinnedMeshRenderer RightArm; public void Enable(bool); }
    public Animator Animator { get; }
    public bool IsVisible { get; set; }   // activa/desactiva el ArmSet seleccionado
    public void EnableArms();              // engancha brazos al mixer vía ParentConstraint
    public void DisableArms();
    public void ToggleNextArmSet();        // cambia de set de brazos (p.ej. distinto guante/manga)
    ```
  - El cambio de "apariencia de brazos" se hace por **SkinnedMeshRenderer + GameObject.SetActive** sobre sets predefinidos, **no** por swap dinámico de meshes ni bone attachments arbitrarios.

- **Approach recomendado:** *(⚠️ obsoleto — ver corrección 2026-06-30 arriba; [E]/ADR-022 reutilizó el `CharacterClothing` nativo de STP en vez de construir desde cero)*
  - STP **no tiene** un sistema de ropa/equipo corporal. Hay que construirlo desde cero en `_Migration/STPIntegration/`.
  - Modelo sugerido: un `ClothingItem` propio (tag de item nuevo) + un `CharacterClothingController` propio que escuche cambios en un `IItemContainer` dedicado (creado por restricción de tag) y haga swap de `SkinnedMeshRenderer`/material por slot. Reutiliza la infraestructura de `IItemContainer` + `ContainerRestriction` de STP **sin modificarla** (solo se consume).
  - Para el "ArmSet" FPS sí se puede reaprovechar `ToggleNextArmSet()` si la ropa solo cambia brazos en primera persona.

- **Gaps:**
  - No hay slots de ropa, ni eventos `OnEquip/OnUnequip` de prendas, ni gestión de mesh corporal. **Todo a construir.**
  - Sí existe contenedor + tags + acción de equipar (reutilizables como cimiento).

---

## 2. ITEM HELD / HANDS (Wieldables)

- **Clases encontradas:**
  - `IWieldablesControllerCC` / `WieldablesController` — controlador de equipar/holstering. `FPSCore/.../Wieldables/Components/`.
  - `WieldableInventory` / `IWieldableInventoryCC` — selecciona wieldable según item de inventario.
  - `WieldableItem` / `IWieldableItem` — puente item↔wieldable.
  - `IWieldable` / `Wieldable` / `NullWieldable` — `FPSCore/.../Wieldables/Implementations/Base/`.
  - `WieldableArmsHandler` — brazos (ver §1).
  - (No existe `HolsterHandler`/`ItemHolder`/`FPSWielding`; el rol lo cubren las clases anteriores.)

- **API pública relevante:**
  - `IWieldablesControllerCC` (qué hay en mano + eventos de cambio):
    ```csharp
    IWieldable ActiveWieldable { get; }            // wieldable activo (null si NullWieldable)
    WieldableControllerState State { get; }        // None / Equipping / Holstering
    Transform WieldablesRoot { get; }              // parent de todos los wieldables (bone/transform raíz)
    event WieldableEquipDelegate HolsteringStarted;
    event WieldableEquipDelegate HolsteringStopped;
    event WieldableEquipDelegate EquippingStarted; // ← evento de "cambio de item en mano"
    event WieldableEquipDelegate EquippingStopped;
    bool TryEquipWieldable(IWieldable, float holsterSpeed = 1f, UnityAction equipCallback = null);
    bool TryHolsterWieldable(IWieldable, float holsterSpeed = 1f);
    void HolsterAll();
    IWieldable RegisterWieldable(IWieldable, bool disable = true);
    bool UnregisterWieldable(IWieldable, bool destroy = false);
    // delegate: void WieldableEquipDelegate(IWieldable wieldable);
    ```
  - `WieldableInventory : IWieldableInventoryCC` (qué slot/item está seleccionado):
    ```csharp
    int SelectedIndex { get; }                     // índice de slot del holster
    int PreviousIndex { get; }
    event UnityAction<int> SelectedIndexChanged;   // ← evento de cambio de selección
    void SelectAtIndex(int index, bool allowRefresh = true);
    bool DropWieldable(bool forceDrop = false);
    IWieldable GetWieldableWithId(int itemId);
    ```
  - `IWieldable` (estado del item en mano):
    ```csharp
    ICharacter Character { get; }
    WieldableStateType State { get; }              // Hidden / Equipping / Equipped / Holstering
    bool IsGeometryVisible { get; set; }
    void SetCharacter(ICharacter);
    IEnumerator Equip();
    IEnumerator Holster(float holsterSpeed);
    ```
  - `WieldableItem` (vincula item↔wieldable y bone/slot):
    ```csharp
    DataIdReference<ItemDefinition> ReferencedItem { get; }
    SlotReference Slot { get; }
    event UnityAction<SlotReference> AttachedSlotChanged;
    void AttachToSlot(SlotReference slot);
    ```

- **Transform / bone usado:** los wieldables se instancian como hijos de `WieldablesRoot` (`_spawnRoot` del `WieldablesController`). Los brazos FPS se enganchan al `IMotionMixer.TargetTransform` mediante un `ParentConstraint` (no a un bone Humanoid). Es un rig de **primera persona**, no hay attach a bone de mano de un avatar.

- **Approach recomendado:**
  - Para saber "qué item está en mano" en tiempo real: suscribirse a `EquippingStarted/EquippingStopped` (controller) o a `SelectedIndexChanged` (`WieldableInventory`), y leer `ActiveWieldable` / el `ItemStack` del slot seleccionado. Esto es lo que debe **replicar el backend** para mostrar el item en la mano del proxy remoto.
  - Para el proxy de tercera persona (mano del avatar remoto) hay que mapear `ReferencedItem`→prefab visual y parentearlo a un bone de mano propio; STP no lo hace.

- **Gaps:**
  - No hay representación en mano para tercera persona ni attach a bone de avatar Humanoid (todo es FPS). El sistema de proxies (`_Migration/STPIntegration/RemoteAvatar/`) ya empezó a cubrir esto (pickup/anim).

---

## 3. MANNEQUIN / LOOTABLE CORPSE

- **Clases encontradas:**
  - **NO existe** `Mannequin`, `LootContainer`, `Lootable`, `DeadBody`, `Corpse`. (Glob → sin resultados.)
  - Equivalente funcional = sistema **Workstation + contenedor**:
    - `IWorkstation` / `Workstation` (abstract) — `STP/.../Workstations/`.
    - `StorageStation : Workstation, ISaveableComponent` — cofre/almacén con N contenedores.
    - `ItemContainerGenerator` — genera un `IItemContainer` (poblable por loot).
    - `LootTable` / `SimpleLootTable` / `CompositeLootTable` — `FPSCore/.../Inventory/Container/LootTables/`.

- **API pública relevante:**
  - `IWorkstation`:
    ```csharp
    string Name { get; }
    IReadOnlyList<IItemContainer> GetContainers();
    void BeginInspection();
    void EndInspection();
    ```
  - `Workstation` (abstract): se dispara solo. Implementa `[RequireComponent(IHoverableInteractable)]` y en `Start()` hace:
    ```csharp
    _interactable.Interacted += StartInspection;   // al interactuar →
    // → character.TryGetCC(out IInventoryInspectionManagerCC insp); insp.StartInspection(this);
    ```
  - `StorageStation`:
    ```csharp
    public override IReadOnlyList<IItemContainer> GetContainers();  // de Inventory o de ItemContainerGenerator
    // Save: persiste el contenedor (ItemContainer) vía ISaveableComponent — ojo ADR-009 (Rust autoritativo).
    ```
  - Inicialización con items: `ItemContainerGenerator.GenerateContainer(...)` + `LootTable` para poblar. Transferencia entre contenedores: ver §4 (`SlotReference.Transfer*`).

- **Approach recomendado:**
  - Modelar el cadáver loteable como un **componente propio que implemente `IWorkstation`** (o herede de `Workstation`) en `_Migration/STPIntegration/`, exponiendo en `GetContainers()` el/los contenedores con el inventario del muerto. Así reutilizas toda la UI de inspección y la transferencia de items **sin tocar PolymindGames**.
  - Poblarlo al morir: copiar los `IItemContainer` del jugador muerto (o snapshot del backend) a un contenedor nuevo creado por `ItemContainerGenerator`. La apertura de UI sale gratis vía `IInventoryInspectionManagerCC.StartInspection`.
  - Requiere `IHoverableInteractable` en el GameObject del cadáver (STP tiene `Interactable`/`Interactable` base).

- **Gaps:**
  - No hay concepto de "cadáver" ni de transferir el loadout del jugador al morir. **A construir** (componente `IWorkstation` propio + lógica de poblado al evento `Death`).
  - El guardado nativo de `StorageStation` choca con ADR-009 (Rust = fuente de verdad); el cadáver debe ser autoritativo en backend, no en el save de STP.

---

## 4. INVENTARIO UI DUAL (inspección de contenedor externo)

- **Clases encontradas:**
  - `IInventoryInspectionManagerCC` / `InventoryInspectionManager` — `STP/.../Inventory/Inspection/`. **Es el punto de entrada para abrir/cerrar loot.**
  - `ItemContainerUI` — `FPSCore/.../UI/Inventory/`. Une un `IItemContainer` a slots UI.
  - `WorkstationInspectorBaseUI<T>` + `StorageStationUI` — `STP/.../UI/Inventory/Workstations/`. UI concreta del panel externo.
  - `ItemTransfering` (extensiones) — `FPSCore/.../Inventory/Utility/ItemTransfering.cs`.

- **API pública relevante:**
  - `IInventoryInspectionManagerCC` (abrir/cerrar sesión de loot):
    ```csharp
    bool IsInspecting { get; }
    IWorkstation Workstation { get; }
    IReadOnlyList<IItemContainer> InspectedContainers { get; }
    event UnityAction InspectionStarted;
    event UnityAction InspectionPostStarted;
    event UnityAction InspectionEnded;
    void StartInspection(IWorkstation workstation);   // ← abre el panel externo (workstation=null → solo backpack)
    void StopInspection();                            // ← cierra
    void InspectContainer(IItemContainer container);  // añade un contenedor suelto a la inspección
    void RemoveContainerFromInspection(IItemContainer container);
    ```
    `StartInspection` ya gestiona cursor (`UnlockCursor`), input context (`PushContext`), callback de ESC y `Workstation.BeginInspection()`. Se cierra solo al morir (`Death → StopInspection`).
  - `ItemContainerUI`:
    ```csharp
    IItemContainer Container { get; }
    event UnityAction<IItemContainer> AttachedContainerChanged;
    void AttachToContainer(IItemContainer container);  // genera/asocia slots UI
    void DetachFromContainer();
    void Sort();
    ```
  - `WorkstationInspectorBaseUI<T>` (a extender para UI propia): `OnInspectionStarted(T)`, `OnInspectionEnded(T)`, `OnCharacterAttached(ICharacter)`.
  - Transferencia de items (extensiones sobre `SlotReference`, `ItemTransfering.cs`):
    ```csharp
    int  TransferItemToInventory(this SlotReference slot, IInventory inventory);
    bool TransferOrSwapWithSlot(this SlotReference slot, in SlotReference targetSlot);
    bool TransferOrSwapWithContainer(this SlotReference slot, IItemContainer targetContainer);
    bool TransferOrSwapToTaggedContainer(this SlotReference slot, IReadOnlyList<IItemContainer> targetContainers);
    bool TransferOrSwapToUntaggedContainer(this SlotReference slot, IReadOnlyList<IItemContainer> targetContainers);
    ```
    (StorageStationUI usa `slot.TransferItemToInventory(_characterInventory)` para "Take All".)

- **Approach recomendado:**
  - Para el panel dual loot↔jugador: llamar a `IInventoryInspectionManagerCC.StartInspection(workstation)` con el cadáver/contenedor (§3). La UI lee `InspectedContainers` y se pinta con `ItemContainerUI.AttachToContainer`.
  - Para sincronizar transferencias en MMO: interceptar las llamadas `Transfer*` (o los eventos `IItemContainer.SlotChanged`) y enviarlas al backend host-authoritative; el backend confirma y replica. No modificar `ItemTransfering`; envolver desde `_Migration`.

- **Gaps:**
  - Toda la UI dual existe y es reutilizable; **no hay** capa de red sobre las transferencias (a construir en integración).

---

## 5. DAÑO / MUERTE

- **Clases encontradas:**
  - `IHealthManager` / `HealthManager` — `FPSCore/.../Damage/`.
  - `IDamageHandler` (+ `DamageResult`), `DamageArgs`, `DamageType`, `IDamageSource`.
  - `CharacterDamageHandler` / `PlayerDamageHandler` (efectos audio/anim de daño), `CharacterFallDamageHandler`.
  - `NullHealthManager` (objeto nulo).

- **API pública relevante:**
  - `IHealthManager`:
    ```csharp
    float Health { get; }
    float MaxHealth { get; set; }
    event DamageReceivedDelegate DamageReceived;   // void(float damage, in DamageArgs args)
    event HealthRestoredDelegate HealthRestored;   // void(float value)
    event DeathDelegate Death;                     // void(in DamageArgs args)  ← evento de muerte
    event UnityAction Respawn;
    float RestoreHealth(float value);
    float ReceiveDamage(float damage);                       // firma simple
    float ReceiveDamage(float damage, in DamageArgs args);   // firma con contexto
    ```
  - `IDamageHandler` (receptor por collider/hitbox):
    ```csharp
    ICharacter Character { get; }
    DamageResult HandleDamage(float damage, in DamageArgs args = default);  // → Normal/Critical/Fatal/Ignored
    ```
  - `DamageArgs` (readonly struct): `IDamageSource Source; Vector3 HitPoint; Vector3 HitForce; DamageType DamageType;`
  - Extensiones (`HealthExtensions`): `IsAlive()`, `IsDead()`, `IsFullHealth()`, `ResetHealth()`, `GetDamageResultBasedOnStatus(bool isCritical)` (umbral `Threshold = 0.001f`).
  - **`HealthManager.SetHealthSilent(float)`** ← **ya añadido por ADR-009**: fija HP sin disparar `DamageReceived/HealthRestored` (reconciliación desde Rust). El `StatInterpolator` lo usa.
  - **Save/HP serializable:** `HealthManager` implementa `ISaveableComponent` pero **el save STP está deshabilitado** (ADR-009: `LoadMembers`/`SaveMembers` vacíos; Rust es la fuente de verdad). HP no se persiste por STP.

- **Approach recomendado:**
  - El daño/muerte real es **autoritativo en backend**. En cliente: aplicar daño visual/efectos vía `ReceiveDamage`, y reconciliar HP con `SetHealthSilent` desde el estado del servidor. Suscribirse a `Death` para drop de wieldables, cierre de UI y spawn del cadáver (§3).
  - `WieldableInventory` y `InventoryInspectionManager` ya escuchan `Death` (drop on death / cerrar inspección) — patrón a seguir.

- **Gaps:**
  - Ninguno crítico: el sistema existe y ya está parcheado para MMO (ADR-009). Falta solo la cola de eventos de muerte→cadáver (cae bajo §3).

---

## 6. CROUCH

- **Clases encontradas:**
  - `CharacterCrouchState : CharacterGroundedState` — `FPSCore/.../Movement/States/CharacterCrouchState.cs`. **STP ya tiene crouch.**
  - `IMotorCC` / `CharacterControllerMotor` — `FPSCore/.../Movement/`.
  - (No existe `CrouchHandler`/`StanceHandler`/`PlayerStance` como clase aparte; es un estado de la máquina de movimiento.)

- **API pública relevante:**
  - `CharacterCrouchState`:
    ```csharp
    public override MovementStateType StateType => MovementStateType.Crouch;
    // _crouchHeight (altura agachado), _crouchCooldown, audio crouch/standup.
    public override bool IsValid();   // Motor.IsGrounded && Motor.CanSetHeight(_crouchHeight)
    public override void OnEnter(...) // Motor.Height = _crouchHeight; audio
    public override void OnExit();
    ```
    Se activa por la **máquina de estados de movimiento** (`Controller.TrySetState(MovementStateType.Crouch)`) según input (`Input.IsCrouching`).
  - `IMotorCC` (resize de collider + offset, **automático**):
    ```csharp
    float Height { get; set; }          // setear Height redimensiona el CharacterController
    float DefaultHeight { get; }
    float Radius { get; }
    bool CanSetHeight(float height);    // comprueba que hay hueco para des/agacharse
    event UnityAction<float> HeightChanged;  // ← para sincronizar cámara/proxy
    ```

- **Approach recomendado:**
  - STP ya hace el crouch **y el resize del collider automáticamente** (`Motor.Height`). El camera height offset lo gestiona la capa de movimiento procedural (no hay que tocarlo).
  - Para MMO: replicar el estado `MovementStateType.Crouch` (o el valor `Height`/`HeightChanged`) hacia el backend y aplicar la misma altura en el proxy remoto. No reimplementar la lógica; consumir `HeightChanged`.

- **Gaps:**
  - Nada que construir en cliente local. Falta solo: (a) reflejar crouch en el avatar de tercera persona/proxy, (b) replicación del estado por red.

---

## 7. FOOT IK / GROUND DETECTION

- **Clases encontradas:**
  - **NO existe** Foot IK: ni `FootIK`, `FootPlacement`, `ProceduralLegs`, `CharacterGroundInfo`. (Glob `*Foot*` → solo `FootstepsController`, que es **solo audio de pisadas**.)
  - Ground detection **sí** existe, dentro de `IMotorCC` / `CharacterControllerMotor`.

- **API pública relevante (ground detection):**
  ```csharp
  // IMotorCC
  bool IsGrounded { get; }
  float LastGroundedChangeTime { get; }
  Vector3 GroundNormal { get; }
  float GroundSurfaceAngle { get; }
  CollisionFlags CollisionFlags { get; }
  LayerMask CollisionMask { get; }
  event UnityAction<bool> GroundedChanged;
  event UnityAction<float> FallImpact;
  // Extensiones: motor.Raycast(ray, dist[, out hit]); motor.SphereCast(ray, dist, radius);
  ```
  `FootstepsController` (`FPSCore/.../Surfaces/`) = solo reproduce pisadas según superficie; no posiciona pies.

- **Approach recomendado:**
  - Foot IK debe construirse **desde cero**, sobre el avatar Humanoid de tercera persona (proxies remotos y/o cuerpo propio visible). Opciones: `Animator.OnAnimatorIK` + `SetIKPosition/SetIKRotation` nativo de Unity, o el paquete **Animation Rigging** (Two Bone IK + raycast por pie). Usar `IMotorCC.GroundNormal`/`CollisionMask` para los raycasts de apoyo.
  - Vive enteramente en `_Migration/STPIntegration/RemoteAvatar/` (o sistema propio del cuerpo), sin tocar PolymindGames.

- **Gaps:**
  - **Todo el Foot IK.** STP solo aporta los datos de suelo (`IsGrounded`, `GroundNormal`, raycasts del motor) que el IK puede consumir.

---

## 8. CHARACTER ROTATION / BODY LEAN

- **Clases encontradas:**
  - `IBodyLeanHandlerCC` / `BodyLeanHandler` — `FPSCore/.../Camera/`. **Lean de cámara en primera persona (procedural), NO rotación de cuerpo.**
  - `FPSBodyLeanInput`, `BodyPoint` (enum de puntos del cuerpo para audio/efectos).
  - **NO existe** `CharacterBody`, `TurnInPlace`, `IKLookAt`, `SpineRotation`. (Grep → sin resultados.)

- **API pública relevante:**
  - `IBodyLeanHandlerCC`:
    ```csharp
    BodyLeanState LeanState { get; }              // Center / Left(-1) / Right(1)
    void SetLeanState(BodyLeanState leanState);
    ```
  - `BodyLeanHandler` aplica el lean a `LeanMotion` (cámara) y a `_wieldableLeanMotion` (arma), con comprobación de obstrucción por `SphereCast`. Es **inclinación lateral peek**, no orientación del torso respecto a la cámara.
  - Rotación: la orientación del personaje la lleva el `CharacterControllerMotor` (`IMotorCC.TurnSpeed`) + input de mirada; no hay separación spine/cámara porque es FPS (cuerpo = cámara).

- **Approach recomendado:**
  - Para el cuerpo de **tercera persona / proxy remoto** (lo que ve el resto de jugadores) hay que construir rotación de cuerpo vs. cámara (spine bend, turn-in-place, look-at) desde cero, sobre el avatar Humanoid. El sistema de proxies ya iniciado en `_Migration/STPIntegration/RemoteAvatar/` (`ProxyLocomotionFeeder`, `ProxyJumpFeeder`, animator builder) es el lugar natural.
  - `BodyLeanState` puede replicarse al proxy como un parámetro más de pose si se quiere reflejar el peek.

- **Gaps:**
  - No hay rotación de cuerpo de tercera persona, ni spine/look-at IK, ni turn-in-place. **Todo a construir** sobre el rig de avatar. STP solo aporta el `BodyLeanState` (peek FPS).

---

## ORDEN DE IMPLEMENTACIÓN RECOMENDADO

Criterio: primero lo que es **API nativa de STP** (bajo riesgo, alto reuso) y base de los demás;
después lo **greenfield** (rig de avatar / IK), de mayor a menor dependencia.

1. **Daño / Muerte (§5)** — Nativo y ya parcheado para MMO (ADR-009: `SetHealthSilent`, save off). Su evento `Death` es la base de drop, cierre de UI y spawn de cadáver. Cimiento de todo.
2. **Crouch (§6)** — Nativo, aislado, resize de collider automático (`Motor.Height`/`HeightChanged`). Valida el patrón de replicación de estado de movimiento. Bajo coste.
3. **Item Held / Hands (§2)** — Nativo (`IWieldablesControllerCC` + `WieldableInventory`). Necesario para mostrar el item en mano del proxy y prerequisito conceptual del equipo.
4. **Inventario UI Dual (§4)** — Nativo (`IInventoryInspectionManagerCC` + `ItemContainerUI` + `Transfer*`). Prerequisito directo del cadáver loteable.
5. **Mannequin / Lootable Corpse (§3)** — Se construye **sobre §4** (UI de inspección) + contenedor + evento `Death` de §5. Componente propio `IWorkstation`.
6. **Equipment / Clothing (§1)** — Greenfield. Reutiliza `IItemContainer`/tags de §2–§4 como cimiento, pero el swap de mesh/slots es nuevo.
7. **Character Rotation / Body Lean 3ª persona (§8)** — Greenfield sobre el rig de avatar/proxy (`_Migration/.../RemoteAvatar/`). Define el esqueleto que necesita el Foot IK.
8. **Foot IK / Ground Detection (§7)** — Greenfield, último (pulido). Depende del rig de §7; consume datos de suelo de `IMotorCC`.

> Nota transversal: §3, §6, §7, §8 requieren capa de **replicación host-authoritative** (backend Rust).
> Las APIs de STP se **consumen y envuelven** desde `Assets/_Migration/STPIntegration/`; nunca se modifica `Assets/PolymindGames/`.
