# Interacción mano-objeto — `Tools ▸ Interaction Authoring` y su API

Herramienta de editor que prepara un wieldable para que las manos de primera persona lo sujeten bien,
sin animar a mano. Decisión en **ADR-149**. Código en `Assets/Editor/HandInteraction/`, datos en
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
- **Cada clip horneado lleva una marca** en el `userData` de su `.meta` (`HandInteraction:baked:<guid del perfil>`).
  Si un perfil sin base intenta partir de un clip marcado —perfil borrado, revert parcial, segundo perfil sobre
  el mismo wieldable— sale `BASE_IS_BAKED` en vez de apilar IK sobre IK. Arreglo: restaurar el clip original
  (git o su horneador) o usar el perfil que lo horneó.

- **La portadora no se mueve.** Si el objeto cuelga de `Hand.R`, el sitio en pantalla es el del clip
  base. Cambiar el encuadre es rehornear ese clip con su horneador (`BackroomsToolPoseBaker`,
  `BackroomsCrankFlashlightPoseBaker`) y **después** este.
- **Rehornear el horneador propio del objeto** (p. ej. `Backrooms/Linterna/Hornear animaciones`) escribe
  sobre los clips que este perfil usa como salida. Hay que borrar `baseClips`/`bakedClips` del perfil
  (`set -Patch '{"baseClips":[],"bakedClips":[]}'`) y volver a hornear, o la base se queda vieja.
- **Capturas sin warp** (`_FOVEnabled = 0`): juzgan la pose, no el encuadre deformado de juego.
- **La puerta es genérica**: `HandInteractionProfileTests` mide todo perfil horneado. Un objeto nuevo no
  necesita test propio.
