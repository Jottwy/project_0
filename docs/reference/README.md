# Referencias visuales por nivel

Con doce zonas más por autorar, la comparación contra la referencia se va a repetir
muchas veces. No puede depender de una imagen pegada en un chat: lo que no está
versionado aquí no existe para la sesión siguiente.

## Level 0

### `level0-canon.png` — FALTA

**La fotografía canónica de Level 0 no está en el repo.** Todas las decisiones de
color tomadas hasta ahora (albedo de las tres superficies, `lampColor` de
ZONE_NORMAL, `ambientLight`) se ajustaron contra la descripción del canon y contra
las relaciones numéricas entre superficies, NO contra un recorte real.

Para cerrarlo: dejar el archivo en `docs/reference/level0-canon.png` y commitearlo.
A partir de ahí el criterio A4 —"comparar las tres texturas aisladas contra un
recorte de la referencia"— se puede ejecutar sin depender de nadie.

### `level0-target-palette.png` — objetivo derivado, NO es la referencia

Tarjeta generada a partir de los valores YA congelados en el repo. Sirve para
detectar derivas (¿alguien movió el albedo?), no para validar el canon: está
derivada de nuestras propias decisiones, así que compararse contra ella es
circular. La validación contra el canon necesita `level0-canon.png`.

| superficie | albedo (media del PNG) | luma | sat | tono |
|---|---|---|---|---|
| pared  `WallpaperYellow.png` | 206.7 / 198.5 / 148.1 | 196.6 | 0.283 | 51.7° |
| suelo  `CarpetBeige.png`     | 209.0 / 195.6 / 141.0 | 194.5 | 0.325 | 48.2° |
| techo  `CeilingTiles.png`    | 211.2 / 204.2 / 167.3 | 203.1 | 0.208 | 50.4° |

Luz de ZONE_NORMAL: `lampColor` 1.00 / 0.96 / 0.86 · `lampEmission` 1.25 ·
`ambientLight` 0.465 / 0.45 / 0.375.

Las guardas que impiden que estos valores se muevan sin darse cuenta viven en
`Assets/Tests/EditMode/WallpaperSurfaceTests.cs` y `ZoneAmbienceSetTests.cs`.

## Protocolo de comparación

1. **Albedo aislado**: abrir los tres PNG de `Assets/Resources/Textures/` y
   compararlos contra un recorte plano de la referencia. Coinciden en tono y en
   luminancia RELATIVA entre sí; en luminancia absoluta el albedo va algo más
   apagado, porque la foto está tomada bajo luz cálida y el albedo no.
2. **Escena**: captura desde la posición de referencia. Lo que se compara es la
   pared en penumbra, no la pared bajo la lámpara: es la mayor parte del encuadre
   y es donde se ve si el ambient tiñe o hunde.
3. **Orden**: albedo primero y se congela; luz después. Invertirlo produce el lazo
   de sobrecorrección que ya costó tres commits (albedo desaturado ⇒ lámpara
   saturada ⇒ techo verde oliva).
