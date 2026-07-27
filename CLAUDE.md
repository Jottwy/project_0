# Backrooms Survival MMO — CLAUDE.md

Mundo procedural multijugador persistente inspirado en Backrooms.
Cliente: Unity 6 + URP (C#). Backend: Rust. Mecánica núcleo: chunk displacement + estabilización por tiers.

## Fuente de verdad
- `docs/ARCHITECTURE.md` — arquitectura validada. No se contradice, se enmienda vía ADR.
- `docs/DECISIONS.md` — registro ADR. ES LEY. Inmutable: solo se añade, nunca se edita.
- `docs/STATE.md` — estado vivo: qué está hecho, qué sigue. Léelo SIEMPRE al iniciar sesión.
- `docs/CONVENTIONS.md` — convenciones C# / Rust / protocolo.
- `docs/INDEX.md` — índice de toda la documentación (ADRs, sistemas, guías). Empieza ahí para ubicar cualquier detalle que no esté en este archivo.

## Reglas duras (no negociables)
1. Lee `docs/STATE.md` antes de tocar nada.
2. Cualquier cambio que contradiga un ADR: PARA y pregunta. No "mejoras" arquitectura validada.
3. Scope cerrado: implementa solo lo pedido. Cero refactors oportunistas, cero abstracciones no pedidas, cero optimización prematura.
4. Plan antes de código si la tarea toca >1 archivo o cualquier sistema núcleo (worldgen, red, persistencia).
5. Diffs pequeños: una preocupación por commit. Si el plan supera ~300 líneas de diff, trocéalo.
6. No reimprimas archivos completos: muestra solo el diff.
7. Si una API pública (protocolo cliente↔servidor, formato de chunk, schema de guardado) cambia: requiere ADR nuevo antes de tocar código.
8. Build/tests en verde antes de cerrar tarea. Si no hay test del sistema tocado, créalo o decláralo.
9. Si dudas entre dos enfoques: expón ambos en 5 líneas y pregunta. No improvises en sistemas núcleo.
10. Respuestas en español, identificadores de código en inglés.
11. `docs/DECISIONS.md` solo se amplía con **Edit anclado** al final del archivo (anclar el `old_string` al último ADR/enmienda existente), NUNCA con `Write` ni reescritura completa — un `Write` sobre este archivo ya causó un incidente de truncado. Verifica `wc -l docs/DECISIONS.md` antes y después del append.

## Flujo estándar
/siguiente → /plan → (validación humana) → implementar → /auditar → /checkpoint → /clear
