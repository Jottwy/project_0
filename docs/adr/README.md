# docs/adr/

Los ADR-001..032 (+ enmiendas) ya existentes viven en `../DECISIONS.md` — no se
movieron aquí: 876 líneas de historial append-only, mover el contenido tenía
riesgo de truncado/pérdida para cero beneficio real (el archivo ya es la ley,
enlazado desde CLAUDE.md).

**A partir de hoy**, los ADRs NUEVOS se registran en dos sitios:
1. Entrada completa, append-only, en `../DECISIONS.md` (como siempre — Edit
   anclado al final, nunca `Write`).
2. Un archivo `ADR-0NN-slug.md` aquí, con el mismo contenido o un resumen que
   enlace a la sección correspondiente de `DECISIONS.md` — para que se puedan
   referenciar/enlazar individualmente desde `docs/systems/*.md` y
   `docs/INDEX.md` sin tener que apuntar a una línea dentro de un archivo de
   876+ líneas.

No dupliques prosa: si el ADR completo ya está en `DECISIONS.md`, el archivo
de aquí puede ser solo un stub con el título, la fecha, el veredicto y un
enlace `../DECISIONS.md#adr-0NN-...`.
