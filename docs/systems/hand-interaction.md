# Interacción mano-objeto — `Tools ▸ Interaction Authoring` y su API

Herramienta de editor que prepara un wieldable para que las manos de primera persona lo sujeten bien,
sin animar a mano. Decisión en **ADR-150**. Código en `Assets/Editor/HandInteraction/`, datos en
`Assets/Scripts/Gameplay/HandInteraction/HandInteractionProfile.cs`, perfiles en `Assets/Data/HandInteraction/`.

## Qué hace (y qué no)

Por cada clip de la **capa base** del controller del wieldable (idle, equipar, enfundar, uso):

1. **Base.** El clip que el wieldable ya usaba, copiado INTACTO la primera vez a `<outputFolder>/Base/`.
   Todo rehorneado lee esa copia: nunca se apila un horneado encima de otro.
2. **Objeto.** Va donde ese clip lo lleva, porque cuelga de su mano **portadora** (hoy `Hand.R`).
3. **IK.** La mano **secundaria** va por IK analítico de dos huesos a su objetivo, definido en el espacio
   del objeto. Su peso baja a 0 cuando el objeto se aleja de su sitio del idle (equipar/enfundar), así
   que la mano llega y se va con el objeto.
4. **Dedos.** Se cierran una sola vez por contacto con la malla y se repiten en todos los fotogramas,
   sin tembleque.

El balanceo procedural del vendor (bob, sway) se aplica encima en runtime y mueve brazos y objeto como
una sola pieza. Lo que se escribe: los clips resultantes, los overrides del `WieldableAnimator` y dos
marcadores `HandTarget.R/L` con tag `EditorOnly` bajo la malla de agarre.

**Fuera de v1, declarado:**
- **Mover la portadora.** Arrastraría el objeto; con `Grip` sólo se rehacen sus dedos.
- **Recoger la izquierda fuera de cuadro.** Sigue siendo `BackroomsToolPoseBaker`.
- **Cajas o documentos a dos manos por las caras.** `inspect` lo marca como `BOX_LIKE`.
- **Capas de acción propias** (la cuerda de la linterna). Cada una sigue con su horneador.
- **Cuerpo remoto 3P.** El perfil ya está en espacio del objeto, listo para cuando se haga.

## Roles de una mano

| role | qué hace |
|---|---|
| `Grip` (0) | Agarra: IK a su objetivo en el objeto, con búsqueda por naturalidad. En la portadora sólo rehace los dedos, y sólo si mejoran. |
| `Keep` (1) | No se toca: la mano del clip base tal cual. |
| `Relaxed` (2) | Brazo del clip base y dedos en cascada relajada. |
| `Reference` (3) | Copia la pose de esta mano de OTRO clip del mismo wieldable (`referenceClipPath`, `referenceTime`) en el espacio del objeto: mano, dedos, hombro y codo. Sirve para reutilizar un agarre ya validado, como la izquierda sobre el pomo de la manivela. |
| `Regrip` (4) | Sólo la **portadora**: se resuelve de nuevo entera (brazo, muñeca y dedos) con la búsqueda de naturalidad, sujetando el objeto EXACTAMENTE donde ya lo llevaba cada clip. El encuadre no cambia; cambia cómo lo coge la mano. Se hornean **todos** los clips del controller (también las capas de acción, como la cuerda) y se reescribe el offset del modelo bajo la mano. |

**Pieza que gira** (`sweptPartNodeName`, `sweptPartAxis`, `sweptClearanceMeters`): una pieza que rota en
runtime, como la manivela sobre su +Z. Mientras se rehace la portadora, ninguna articulación puede entrar en su
órbita: se castiga con el término `órbita` (restricción dura) y `validate` da `SWEPT_PART_COLLISION`.

Cómo se mide la órbita: con un perfil cilíndrico de los vértices de la pieza (distancia al eje y altura sobre él). Es
exacto para una vuelta completa y barato.

`sweptPartMinY01`–`sweptPartMaxY01` limita qué tramo de la pieza cuenta, por su Y local normalizada. En la linterna es
**0,75–1, sólo el pomo**, con `sweptClearanceMeters` 9,5 mm (17 mm al centro del pomo, el criterio de
`TheKnobOrbitClearsTheRightHand`). El brazo va plano contra el cuerpo; lo que choca con los dedos es el pomo.
Resultado en la linterna: la derecha rehecha agarra por encima en 0,37 del eje, antebrazo −10°, dedos rodeando
112°, coste 4,47, pomo despejado (la muñeca de 129° del agarre anterior desaparece).

**Probar otro ángulo de reposo** (`sweptPartTrialRestDegrees`): gira la pieza que gira sobre `sweptPartAxis`
sólo en la copia de medida. Así se barren reposos con `set` + `preview` sin tocar el modelo. `bake` se niega
mientras no sea 0 (`REST_TRIAL_NOT_APPLIED`): el reposo real lo pone el horneador del objeto, y el perfil tiene
que volver a 0 antes de hornear.

**Seguir la pieza que gira** (`sweptPartActionClipPath`): el clip de acción cuya fase ES el ángulo de la pieza
(la cuerda). Una mano con `Grip` sobre la pieza que gira se hornea también en ese clip. En cada fotograma la
pieza se pone a `reposo · AngleAxis(fase · 360°)`, como la dibuja el componente en runtime. La muñeca va con
el centro de lo que agarra y conserva la orientación del agarre natural en reposo, así que el pomo rueda
dentro de los dedos. Hombro y codo son los de la búsqueda. El informe da el peor coste de brazo en la vuelta.
En reposo, el idle y la fase 0 son la misma pose, y la transición no salta.

**Piezas cortas.** Un dedo cuyo nudillo cae fuera del tramo de la superficie que agarra (el pomo de 2 cm, un
gatillo) se RECOGE. No cuenta para `yema-lejos`, `no-rodea` ni el hueco del puño; su penetración sí cuenta. Un
pomo se coge con pulgar, índice y corazón.

**Pieza por mano.** Por defecto una mano agarra la malla de agarre del perfil (`gripMeshNodeName`, eje +Y).
Con `gripPartNodeName` agarra otra pieza del modelo, con su propio eje (`gripPartAxis`, en local de esa pieza)
y el tramo de su Y local normalizada que cuenta (`gripPartMinY01`–`gripPartMaxY01`). Mientras agarra la pieza,
la malla principal y las demás piezas siguen siendo obstáculos: los dedos no las atraviesan. La portadora
agarra siempre la malla principal. Ejemplo, la izquierda sobre el pomo de la manivela (el pomo gira sobre +Z
y es el cuarto superior del brazo):

```powershell
.\tools\dev\HandInteraction.ps1 -Command set -Profile Assets/Data/HandInteraction/BR_Wieldable_CrankFlashlight_Hands.asset -Patch '{"maxShoulderShiftMeters":0.4,"leftHand":{"role":0,"gripPartNodeName":"Crank","gripPartAxis":{"x":0,"y":0,"z":1},"gripPartMinY01":0.75,"gripPartMaxY01":1,"searchAlongRange":0.5}}' -Bake -Capture
```

Con `Reference` no se busca nada, y el veredicto sólo frena fallos duros al reproducir la pose: alcance
recortado, manos a menos de 15 mm, desvío de más de 5 mm, muñeca o antebrazo en el tope. El hombro adelantado
de un rig 1P sin torso no cuenta. `validate` compara la mano horneada con su referencia y da
`REFERENCE_MISMATCH` si se aparta más de 5 mm o 3° en algún dedo.

Ejemplo, la izquierda de la linterna sobre el pomo de la manivela en reposo:

```powershell
.\tools\dev\HandInteraction.ps1 -Command set -Profile Assets/Data/HandInteraction/BR_Wieldable_CrankFlashlight_Hands.asset -Patch '{"leftHand":{"role":3,"referenceClipPath":"Assets/Art/Items/CrankFlashlight/Anim/BR_CrankFlashlight_Crank.anim","referenceTime":0}}' -Bake -Capture
```

## Naturalidad: qué se puntúa

La mano secundaria barre reloj (lado de la palma), inclinación, codo y separación de la palma. Gana el
candidato de menor coste. Cada término aparece en `costBreakdown` y en la validación:

| Término | Qué castiga |
|---|---|
| `alcance`, `hombro` | brazo que no llega; hombro adelantado (tope `maxShoulderShiftMeters`) |
| `supinación`, `pronación`, `antebrazo-tope` | antebrazo girado fuera de −70°…+30° (0 = mano de apretón, + = palma arriba), medido contra el cuerpo |
| `extensión`, `flexión`, `cubital`, `radial`, `muñeca-tope` | muñeca fuera de su rango cómodo, medida con VECTORES (antebrazo → metacarpo del corazón), no con los ejes del hueso |
| `codo-cerrado`, `codo-bloqueado`, `codo-alto`, `codo-sobre-hombro` | codo por debajo de 70° o por encima de 160°, o levantado |
| `yema-lejos-N`, `yema-dentro-N`, `falange-dentro` | dedo que no toca (> 12 mm) o que atraviesa (> 4 mm; el medio de la falange admite la flecha L²/8R) |
| `palma-dentro`, `palma-lejos` | nudillos o palma dentro de la malla, o el nudillo más cercano a más de 12 mm (sostener con las yemas) |
| `fuera-del-puño` | el eje del objeto a más de 12 mm del hueco que encierran los dedos |
| `no-rodea` | yemas que tocan sin dar la vuelta al eje (la deuda de ADR-077 enm. 5) |
| `tapa-extremo-N` | yema que cruza la cara de la punta o de la culata (la lente, la boquilla) |
| `manos-juntas` | articulaciones de las dos manos a menos de 22 mm |
| `fuera-de-vista` | la mano secundaria a más de 40° del centro de la vista |

**Veredicto:** si la mejor pose de una secundaria supera `maxNaturalCost`, `bake` no escribe
(`NO_NATURAL_GRIP`, con el desglose y alternativas) salvo `force`. La **portadora** con `Grip` sólo cambia
sus dedos si el coste BAJA; si no, se conservan los que había. `validate` sobre un perfil sin hornear
mide sólo la portadora tal como está: es la forma de medir un agarre ya existente.

Los dedos cierran **acoplados**: MCP, PIP y DIP a la vez, como los mueve el tendón. Paran en el primer
contacto y después sólo rematan las dos falanges de la punta. El pulgar prueba varias oposiciones y se
queda la que toca por el lado contrario a los dedos.

## Uso desde Claude

Siempre por `tools/dev/HandInteraction.ps1`, que elige la vía:
- **Editor abierto:** puente `Logs/HandInteraction/inbox → outbox`.
- **Editor cerrado:** Unity `-batchmode`. Arrancarlo cuesta minutos: agrupa varios comandos con
  `-RequestFile` y `{"requests":[…]}`.

La salida es JSON con `ok`, `errors[{code,message,hint}]`, `warnings`, `inspection`, `profile`,
`bake.report`, `bake.hands[]` (métricas), `validation.issues[{severity,code,hand,measured,threshold,hint}]`
y `captures[]` (PNG en `Logs/HandInteraction/captures/<perfil>/`).

### Flujo «prepara la linterna X para dos manos»

```powershell
.\tools\dev\HandInteraction.ps1 -Command find -Query "linterna"
.\tools\dev\HandInteraction.ps1 -Command inspect -Prefab Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab
.\tools\dev\HandInteraction.ps1 -Command prepare -Prefab Assets/Prefabs/Wieldables/BR_Wieldable_CrankFlashlight.prefab -Kind TwoHand
.\tools\dev\HandInteraction.ps1 -Command preview -Profile Assets/Data/HandInteraction/BR_Wieldable_CrankFlashlight_Hands.asset -Capture
.\tools\dev\HandInteraction.ps1 -Command bake -Profile Assets/Data/HandInteraction/BR_Wieldable_CrankFlashlight_Hands.asset -Capture
```

1. **Mirar** `inspection.manualSteps`. Si hay `GRIP_AXIS_NOT_Y` o `BOX_LIKE`, se para y se avisa: esos no
   se preparan solos.
2. **Tras `preview` o `bake`, abrir las capturas con Read**, sobre todo `*_eye.png` (la vista del
   jugador) y `*_side_left.png`.
3. **Leer `validation`:**
   - `error`: se arregla con su `hint` y se rehornea.
   - `warning`: se informa.
   - `manualChecks`: queda para el humano.

### Flujo «la mano izquierda está demasiado atrás»

`nudge` traduce direcciones de la **vista del jugador** al espacio del objeto, con la orientación
resuelta en el último `preview`/`bake`:

```powershell
.\tools\dev\HandInteraction.ps1 -Command nudge -Profile <perfil> -Hand L -Direction forward -Millimeters 20 -Bake -Capture
```

| direction | efecto |
|---|---|
| `forward` `back` `up` `down` `left` `right` | mueve el punto de agarre en la vista. La parte a lo largo del eje va a `alongAxis`, la perpendicular a `offsetMeters` |
| `tip` `tail` | a lo largo del eje del objeto |
| `in` `out` | palma más cerca o más lejos de la piel (`palmOffsetMeters`) |
| `clockwise` `counterclockwise` (`-Degrees`) | gira el lado desde el que llega la mano y **fija** el reloj (`searchClockRange = 0`) |
| `tiltUp` `tiltDown` (`-Degrees`) | inclinación de los nudillos, y la fija |

Otras órdenes en lenguaje natural:
- **«El agarre está bien, no lo vuelvas a elegir»:** `set` con
  `-Patch '{"leftHand":{"autoSearch":false,"clockDegrees":<resolvedClockDegrees>,"tiltDegrees":<resolvedTiltDegrees>}}'`.
- **«Los dedos atraviesan la linterna»:** mirar `validation` (`FINGER_PENETRATION` dice la falange) y
  aplicar su `hint`, normalmente `-Direction out -Millimeters 3`.

### Comandos

| command | entrada | escribe |
|---|---|---|
| `find` | `query` (español o inglés) | nada |
| `list` | — | nada |
| `inspect` | `prefab` | nada |
| `prepare` | `prefab`, `kind` (`OneHand`/`TwoHand`), `profile`, `overwrite` | el perfil |
| `get` | `profile` o `prefab` | nada |
| `set` | `profile`, `patch` (JSON parcial del perfil), `bake`, `capture` | perfil (y horneado) |
| `nudge` | `profile`, `hand`, `direction`, `millimeters`/`degrees`, `bake`, `capture` | perfil (y horneado) |
| `preview` | `profile`, `capture` | sólo los campos `resolved*` del perfil |
| `bake` | `profile`, `capture` | clips, overrides, marcadores, perfil; valida |
| `validate` | `profile` | nada |
| `capture` | `profile` | PNG en `Logs/HandInteraction/captures/` |
| `pose` | `profile` | nada; vuelca hombro, codo, muñeca, nudillos y ejes de las dos manos en el idle efectivo |

Llamada directa desde dentro del editor (MCP, un test, otro script):
`HandInteractionApi.Execute("{\"command\":\"validate\",\"profile\":\"…\"}")`.

La CLI a mano:

```
Unity.exe -batchmode -projectPath . -executeMethod BackroomsSurvival.EditorTools.HandInteraction.HandInteractionApi.RunCli -hiRequest req.json -hiResponse res.json
```

Va **sin** `-quit` (sale sola) y **sin** `-nographics` (si no, las capturas salen negras).

## Trampas conocidas

- **Una métrica absurda se MIDE con `pose` antes de tocar la fórmula.** Así se descubrió que el agarre
  horneado de la linterna tiene la muñeca derecha a 129° entre antebrazo y metacarpo (el destornillador
  26°): no era la herramienta, era la pose. No se ha corregido; es deuda declarada.
- **Unity headless borra `Temp/` al cerrar.** Por eso peticiones y capturas viven en `Logs/HandInteraction/`.
- **La copia de medida se DESEMPAQUETA.** Dentro de una instancia de prefab Unity no deja cambiar el padre de un
  hijo (sólo lo dice en el log), y `Regrip` necesita soltar el modelo de la mano. Sin desempaquetar, todos los
  candidatos daban los mismos números byte a byte. Si vuelve a pasar, sale `NODE_DETACH_FAILED`.
- **Una instancia nueva por captura.** Muestrear varios clips seguidos sobre la misma instancia y renderizar entre
  medias dejaba los brazos con un skinning viejo: las capturas enseñaban la mano lejos del objeto mientras el test
  de dedos, sobre el mismo clip, pasaba. Si una captura y un test no cuadran, **manda el test**.
- **La órbita de lo que gira es una restricción dura con dedos reales** (+10 en cuanto un hueso entra). Como simple
  preferencia, una mano 3 mm dentro costaba 1,0, y el pomo pasaba a 13 mm del índice en runtime. En la etapa gruesa
  (puño genérico) es blanda, y nunca es obstáculo del ajuste de dedos: eso envenenó todos los candidatos (coste 71).
- **Todo lo que un horneado escribe y la búsqueda lee necesita copia base.** Los clips van a `Base/`; el offset del
  modelo bajo la portadora, que `Regrip` reescribe, va a `baseNodeLocalPosition/Rotation` (`hasBaseNodeLocal`), y
  la búsqueda parte siempre de él. Medido antes de existir: tras hornear un `Regrip`, la misma pose pasó de coste
  4,46 a 186 porque el objeto se colocaba con el brazo base y el offset nuevo, y cinco tiradas de ajuste persiguieron
  un objeto que estaba en otro sitio. **Detector:** congelar la última pose buena (`autoSearch:false` con sus
  valores resueltos) y medirla; si su coste cambió sin tocar las métricas, cambió una entrada. Al quitar el
  `Regrip`, el siguiente `bake` devuelve el offset base al prefab, rehornea también las capas de acción y apaga
  `hasBaseNodeLocal`. Pendiente: la pieza que gira supone escala uniforme (`lossyScale.x`).
- **Un antebrazo que tiembla es un salto de signo, no ruido.** La torsión mano/antebrazo se mide en (−180°, 180°]; al
  cruzar ±180° cambia de signo y los `ForearmTwist` dan media vuelta en un fotograma. Medido: 160° en la cuerda, 164° al
  equipar la linterna y 178° al enfundar el destornillador, que venía de su horneador propio. Al hornear, la torsión
  es continua dentro del clip, y `LosClipsHorneadosNoTiemblan` mide la aceleración angular por hueso (lo legítimo no
  pasa de 35°; tope 90°). **Detector:** la segunda diferencia angular leída del `.anim`.
- **Elegir por fotograma en pasos discretos también tiembla:** dos opciones de coste parecido alternan. Se elige, se
  suaviza sobre el clip, se reelige cerca de lo suavizado y se suaviza otra vez, más ligero.
- **`Regrip` prueba la pose que ya había.** La búsqueda por reloj e inclinación puede no pasar cerca: en el
  destornillador todos sus candidatos tocaban el tope de muñeca y la pose real tenía la muñeca a −24°/8°. La pose actual
  se empuja fuera del mango en radial y CONTRA LA PALMA (sólo en radial, la palma seguía dentro). Si el brazo no llega
  en un clip, el hombro se adelanta; si aun así el objeto se separa más de 5 mm, `REGRIP_DRIFT`.
- **No se hornea un clip que no anima el brazo de la portadora** (`CLIP_WITHOUT_ARM`): los `Template_Attack` del vendor
  son plantillas casi vacías y, horneados, salían con 64–81° de brazo inventado.
- **El hueco del puño no cae en el eje de un tubo gordo.** La tolerancia de `fuera-del-puño` crece con el radio
  (`targetToleranceMm`): 30 mm en un mango de 12 mm y 43 mm en el cuerpo de 26 mm de la linterna.
- **Cada clip horneado lleva una marca** en el `userData` de su `.meta` (`HandInteraction:baked:<guid del perfil>`).
  Si un perfil sin base intenta partir de un clip marcado —perfil borrado, revert parcial, segundo perfil sobre
  el mismo wieldable— sale `BASE_IS_BAKED` en vez de apilar IK sobre IK. Arreglo: restaurar el clip original
  (git o su horneador) o usar el perfil que lo horneó.

- **La portadora no se mueve.** Si el objeto cuelga de `Hand.R`, el sitio en pantalla es el del clip
  base. Cambiar el encuadre es rehornear ese clip con su horneador (`BackroomsToolPoseBaker`,
  `BackroomsCrankFlashlightPoseBaker`) y **después** este.
- **Rehornear el horneador propio del objeto** (p. ej. `Backrooms/Linterna/Hornear animaciones`) escribe
  sobre los clips que este perfil usa como salida. Hay que borrar `baseClips`/`bakedClips` del perfil
  (`set -Patch '{"baseClips":[],"bakedClips":[],"hasBaseNodeLocal":false}'`) y volver a hornear, o la base se
  queda vieja.
- **Capturas sin warp** (`_FOVEnabled = 0`): juzgan la pose, no el encuadre deformado de juego.
- **La puerta es genérica**: `HandInteractionProfileTests` mide todo perfil horneado. Un objeto nuevo no
  necesita test propio.
