# Reverb por zona — contrato con el AudioMixer

> **Por qué existe este documento:** el reverb depende de un efecto autorado dentro de
> `Assets/PolymindGames/FPSCore/Data/Audio/FPS_AudioMixer.mixer`, que es **contenido del
> vendor**. Un reimport del `.unitypackage` de PolymindGames lo sobrescribe y el reverb
> desaparece **en silencio** — `AudioMixer.SetFloat` sobre un parámetro que ya no existe
> devuelve `false` sin lanzar nada. Ya pasó con tres escenas del proyecto (ADR-065).
> Esto es lo que hay que rehacer si vuelve a ocurrir.

## Renombrar los parámetros SOLO funciona desde el editor

Editar `name:` dentro de `m_ExposedParameters` en el YAML **no basta**: el runtime siguió
usando los nombres viejos aunque el asset en disco mostraba los nuevos, y el resultado fue
`SetFloat` escribiendo contra nombres inexistentes. Lo mismo vale para **añadir** entradas
a esa lista a mano.

Si el asset y el runtime se desincronizan: click derecho sobre `FPS_AudioMixer` ▸
**Reimport**, y si aún así no casa, renombra desde el desplegable *Exposed Parameters* de
la ventana del Audio Mixer.

Como red, `ReverbMixerDriver.ParamCandidates` prueba también los nombres que Unity asigna
por defecto (`MyExposedParam`, `MyExposedParam 1`, …, en el orden en que se expusieron:
Dry, Room, Room HF, Decay, Reverb). Es un respaldo, no un contrato — los dos parámetros
añadidos después (`RvbReflect`, `RvbReflectDelay`) no tienen variante numerada.

## `SetFloat` no falla en silencio

Un `AudioMixer.SetFloat` con un nombre que el mixer no conoce **no** devuelve `false` sin
más: Unity escribe `Exposed name does not exist: X` **con stack trace** en cada llamada.
El driver escribe siete valores por frame mientras interpola, así que un solo nombre mal
puesto generó **229.725 errores y 386 MB de log** en una sesión — la misma mecánica que
tumbó el editor con el espejo del backend.

Por eso `ResolveParamNames()` sondea con `GetFloat` (ese sí es mudo) una única vez y, si
falta cualquiera de los siete, marca el sistema como **mudo y sin escrituras**. Nunca
añadas una escritura al mixer fuera de `Write()`.

## Síntoma de que se ha perdido

Al entrar en Play sale una vez:

```
[Reverb] el mixer no expone N de 7 parámetros: reverb MUDO y sin escrituras
```

`ReverbMixerDriver.ResolveParamNames()` lo emite una sola vez por sesión. Si no aparece
ese aviso pero tampoco se oye reverb, el problema **no** es el mixer: mira la autoría por
zona (`LayerVisualConfig.zoneAmbienceSets`, campo `overrideReverb`), y sobre todo el acuse
`[Reverb] pedido … | EN EL MIXER …`, que sale al cruzar de zona y compara lo que el código
pidió contra lo que el efecto tiene de verdad. Si el pedido ya es `room=-10000`, la zona
no está autorada y el mixer no tiene nada que ver.

## Rehacerlo (5 minutos en el editor)

1. Abre `FPS_AudioMixer`.
2. En la tira del grupo **Master**: `Add Effect ▸ SFX Reverb`.
   **Master y no Ambience**, y esto es load-bearing — ver abajo.
3. Selecciona el efecto para que el Inspector muestre sus campos. Sobre cada uno de
   estos siete: botón derecho ▸ *Expose … to script*.
4. En el desplegable **Exposed Parameters** (esquina superior derecha de la ventana del
   mixer) renómbralos **exactamente** así:

| Parámetro del efecto | Nombre expuesto | Unidad |
|---|---|---|
| Dry Level    | `RvbDry`          | mB |
| Room         | `RvbRoom`         | mB |
| Room HF      | `RvbRoomHF`       | mB |
| Decay Time   | `RvbDecay`        | segundos |
| Reverb       | `RvbLevel`        | mB |
| Reflections  | `RvbReflect`      | mB |
| Reflect Delay| `RvbReflectDelay` | segundos |

Los nombres son constantes en `ReverbMixerDriver` (`ParamDry`, `ParamRoom`, …). Si no
casan, esa dimensión del reverb queda muerta **sin avisar**.

5. `File ▸ Save Project` y commitea el `.mixer`.

## Por qué Master y no Ambience

La jerarquía real del mixer es:

```
Master
 ├── Effects     ← pasos y SFX del jugador   (AudioChannel.Sfx)
 └── Ambience    ← zumbido de las lámparas   (AudioChannel.Ambience)
UI                ← cuelga de la raíz, fuera de Master
```

`Effects` y `Ambience` son **hermanos**. Un reverb en cualquiera de los dos mojaría la
mitad de la escena y dejaría la otra seca — una sala donde los pasos reverberan pero el
zumbido no (o al revés) canta de inmediato. `Master` es el único punto que los une, y
además no lleva la UI, así que los menús quedan secos sin hacer nada. Por eso basta un
insert y no hacen falta send/receive.

## Trampas

- **La unidad es mB, no dB.** `100 mB = 1 dB`. Un reverb discreto está en `-2000`
  (= −20 dB), no en `-20`, que sería casi el máximo. Sólo `RvbDecay` y `RvbReflectDelay`
  van en segundos.
- **Al exponer, Unity NO usa el nombre del parámetro.** Los bautiza `MyExposedParam`,
  `MyExposedParam 1`, … El renombrado del paso 4 no es cosmético.
- **Para saber qué `MyExposedParam N` es cuál**, no te fíes del orden de la lista (mezcla
  alfabéticamente los nuevos con los volúmenes que ya había). Cruza el `m_GUID` de cada
  entrada de `m_ExposedParameters` con el `m_ParameterName` del bloque
  `AudioMixerEffectController` del SFX Reverb.
- **El mixer es un asset**, así que lo que se escriba en Play persiste tras salir. Por eso
  `ReverbMixerDriver.OnDestroy` devuelve el bus a silencio; sin eso, salir del Play dentro
  de una nave deja el menú principal reverberando.

## Dónde se autora cada sala

`LayerVisualConfig`: campos `reverb*` de capa (fallback) y bloque `overrideReverb` dentro
de cada `zoneAmbienceSets`. El override es **todo-o-nada** sobre los siete mandos: una sala
es una descripción coherente de un espacio, y mezclar la cola de una zona con las
reflexiones de otra da un sitio que no existe.

`reverbWallMetres` se autora en **metros**, no en segundos —
`ReverbMixerDriver.ReflectDelayForMetres` hace la conversión (ida y vuelta a 343 m/s).

### El vacío es deliberado en dos zonas

`reverbReflect: -10000` apaga las reflexiones tempranas. Sin ellas no hay sensación de
paredes y sólo queda cola difusa: **irreal, sin superficies**. En un pasillo eso delata el
efecto, pero en `BLACKOUT` y en `PIT` es exactamente el objetivo — validado en juego. No lo
"arregles" al retocar valores.
