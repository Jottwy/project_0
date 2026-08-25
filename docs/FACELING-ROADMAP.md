# Facelings — hoja de ruta de comportamiento

Estado y siguientes pasos de la IA de facelings (ADR-094 y sus enmiendas). Escrito al cierre de
la sesión del **2026-08-25**, en la que se hicieron las Enmiendas 9 a 13.

`docs/DECISIONS.md` es la ley y lleva el POR QUÉ de cada decisión ya tomada. Esto es lo otro: qué
falta, qué no está confirmado, y qué merece la pena antes que qué.

---

## 1. Lo que ya hacen

Para no reproponer lo que existe.

**Colmena.** Un miembro percibe, el pack entero reacciona el MISMO tick (`detect_for_pack` corre
antes que cualquier movimiento). No hay "avisar", que es justo lo inquietante.

**Roles** (`assign_roles`, se re-reparten a cada muerte):
- `Press` — de frente. Su golpe conectado tumba (ADR-076) y roba.
- `Flank` — a 125° de tu facing, fuera del cono.
- `Cut` — intercepta tu dirección DE VUELTA, no tu posición.
- `Ring` — orbita a 10 m y trabaja el lado al que NO miras. No entra nunca.

**Tres marchas.** 1.8 m/s si les miras, 4.2 si les das la espalda.

**La mirada congela**, por miembro y con línea de visión real (Enmienda 10). Mirar a uno te compra
ese uno y te cuesta los otros.

**El cerco cerrado apaga la mirada.** ≥3 miembros a <7 m y el congelar deja de funcionar.

**Acoso**: empujones sin daño de todo el que no sea `Press`; screamer con cinemática de cliente
(giro, cara a cara, empujón, 7 s de oídos taponados y sin correr).

**Robo**: el ladrón huye al nido a 5.5 m/s e IGNORA la mirada — es lo que lo identifica sin UI.

**Voz por bandas de distancia** con silencios, y temperamentos de pack (`Loud`/`Quiet`/`Silent`).

**Navegación**: A* con ventana ±24 celdas tras una puerta de atasco, más bisagras (ADR-082).

---

## 2. Lo que NO está confirmado

Esto es deuda, no ideas.

### 2.1 El atasco en paredes — CERRADO por la Enmienda 14 (2026-08-25, 2ª sesión)

Playtest 2026-08-25: *"se quedan como intentando atravesar una pared"*, y después de la Enmienda
12, *"sigue sin funcionar"*. La auditoría encontró **cinco causas, y el pathfinder no era ninguna**.

La que mandaba: **la detección era un test de ángulo sin oclusión**, así que el pack te fijaba a
través de una pared, convergía sobre un objetivo inalcanzable para el A*, y el mismo test ciego
refrescaba `lost_for` cada tick — la rendición de 20 s tampoco corría nunca. Más la estatua
permanente (el latch de congelación no se limpiaba al rendirse el pack), el trinquete de
`advance_step` que anulaba la Enmienda 12 entera, el `note_step` incondicional de `PackRoam`, y
—fuera del atasco— el panic de `regroup_lone_survivors` y el `Seize` muerto en cliente.

Detalle y por qué de cada uno: **ADR-094 Enmienda 14** en `docs/DECISIONS.md`. Commit `6d731660`.

Sigue abierto, y es lo próximo si el atasco reaparece: **el golpe no usa `contact_stance`** (pegado
a una pared eres intocable para ellos, ADR-082 pieza (b) sin portar), **`Flee` no navega ni tiene
watchdog**, y **`Enforce` puede moler contra la correa de su chunk**.

### 2.2 Tests perdidos de la Enmienda 12 — RECUPERADOS

`sed -i` corrompió `backend/src/game_loop/tests.rs` (431 KB de bytes nulos). Los tres tests se
reescribieron en la Enmienda 14: los dos unitarios (`a_deflected_step_is_not_progress`,
`a_route_is_not_abandoned_after_one_clean_step`) y, en lugar del end-to-end que no discriminaba,
cinco tests de conducta que SÍ fallan con su bug reinstalado — comprobado mutante a mutante en un
worktree limpio.

**La lección, que es lo reutilizable:** el end-to-end original no discriminaba porque buscaba un
fallo de NAVEGACIÓN cuando la causa era de PERCEPCIÓN. Y ningún test de percepción debe autorar
coordenadas a mano — `sightline_pair` busca el escenario en el mundo real con `with_rules`. Ver la
memoria `sed-i-destroys-source-files`.

### 2.3 Densidades y radios siguen siendo PLACEHOLDER v1

Marcados como tal en el código, nunca medidos con una sonda contra población real.

---

## 3. Ideas de comportamiento, priorizadas

Ordenadas por cuánto cambian la SENSACIÓN por unidad de trabajo, no por dificultad. Ninguna toca
el protocolo salvo donde se indica.

### P1 — Curiosidad sin agresión

Hoy solo hay dos modos: te ignoran o te cazan. Falta el de en medio, que es el que da miedo de
verdad: un niño que te SIGUE sin atacar, a distancia, y **se para cuando tú te paras**.

La amenaza latente pesa más que la ejecutada. Y da un uso al `Ring`, que ya sabe orbitar sin
entrar. Probablemente sea un `ChildState` nuevo entre `PackRoam` y `PackStalk`, con su propia
salida (te acercas tú, o pasan N segundos, o el pack alcanza cierto tamaño).

### P2 — Que hagan algo cuando no estás

Jugar, pelearse entre ellos, arrastrar objetos — y **parar en seco** al verte. Ese corte es lo
que vende que están vivos y no esperando al jugador.

Barato: no necesita percepción nueva, solo animaciones/poses y un estado ocioso con contenido. Se
apoya en la relé de poses que ya existe.

### P3 — Reaccionar a lo que HACES, no a dónde estás

Disparos, linterna, objetos que sueltas. El robapieles ya tiene el canal de ruido
(`NoiseReporter`), así que la mitad de la fontanería existe. Un pack que investiga un ruido y se
dispersa con la luz se puede LEER, y eso convierte el encuentro en algo con tácticas.

### P4 — Memoria del pack

Hoy te olvidan a los 20 s (`FACELING_CHILD_GIVE_UP_S`). Que recuerden POR DÓNDE te fuiste y
vigilen esa salida es mucho más listo que perseguirte, y mucho más barato que perseguirte.

`PackMind` ya guarda `last_known_pos` y `last_known_vel`: la pieza existe, falta el estado que la
use como puesto de vigilancia en vez de como destino.

### P5 — Jerarquía interna

Uno decide, los demás siguen. Si matas al que manda, el pack se desmorona (o cambia de rol). Da
una razón para leer al GRUPO en vez de disparar al más cercano, que es lo que hace un jugador
ahora mismo.

### P6 — Instinto de conservación del pack

Si matas a dos, que huyan — y que vuelvan luego, con más. `ChildState::Flee` ya existe para el
superviviente solitario; falta a nivel de pack, con un umbral de bajas.

---

## 4. Lo pendiente de presentación

No es comportamiento, pero es lo que hace que el comportamiento se lea.

- **Animación propia del niño.** ADR-094 punto 6 pedía "paso ligero/saltitos"; sigue usando el
  blend tree del jugador retargeteado, así que anda como un adulto en miniatura.
- **Los adultos se han quedado muy atrás.** Los niños tienen ocho roles, voz por bandas, cerco,
  robo y captura. El adulto pasea, te mira y pega. Sin voz (bancos vacíos), sin variedad.
- **Normal maps.** Los dos modelos de Meshy vinieron sin ellos.

---

## 5. Trampas conocidas

Antes de tocar nada de esto, ver también las memorias del proyecto.

- **`GridGenChunkCache::new` genera OTRO mundo que `with_rules`.** Una sonda de test con `new`
  miente sobre paredes y líneas de visión. Los drivers usan
  `with_rules(seed, zone_density::rules_for)`.
- **`FACELING_CHILD_PATROL_RADIUS_M` son 100 m.** Un test que deja al pack vagabundear no está
  midiendo lo que cree: cruza cualquier umbral de distancia a media prueba. `record_pack_voice`
  tiene un flag `pin` para eso.
- **`(0,0,0)` es celda de pared.** Los escenarios de test con coordenadas a mano caen en sólido;
  hay que partir de la posición ya ajustada y BUSCAR el escenario.
- **Nunca `sed -i` sobre las fuentes**: deja el fichero en bytes nulos sin dar error.
- **`player_is_looking_at` NO lleva oclusión, y es a propósito** — ADR-016 D1=(a) lo fija así
  para la estatua del robapieles. La oclusión del faceling vive en `update_freeze_for_pack`.
