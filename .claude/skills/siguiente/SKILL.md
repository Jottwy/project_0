---
name: siguiente
description: Arranque de sesión. Lee el estado del proyecto y propone la siguiente tarea concreta con su ficha de tarea (tipo, modelo, modo, ficheros, riesgo ADR) y un prompt ejecutable. Usar al abrir cada sesión.
disable-model-invocation: true
---
Absorbe la FICHA DE TAREA que hasta el 2026-09-05 vivía en la skill `/ruta`. Eran dos skills para
un solo momento: `/siguiente` decía QUÉ hacer y `/ruta` CÓMO encuadrarlo, y en la práctica la
segunda se invocaba sobre lo que acababa de decir la primera. Se fusionan.

1. Lee `docs/STATE.md` entero (y solo eso, salvo que el «Próximo paso ÚNICO» exija un ADR concreto:
   entonces `docs/DECISIONS-INDEX.md` → `grep -n "^## ADR-NNN" docs/DECISIONS.md` → `Read`
   con offset/limit, nunca el fichero entero).
2. Estado en 2 líneas y **SIGUIENTE TAREA**: una sola, concreta, terminable en una sesión. Si el
   «Próximo paso» es ambiguo o demasiado grande, trocéalo y propón sólo el primer pedazo.
3. Devuelve su FICHA DE TAREA:
   - **Tipo:** respuesta-directa | investigación | diseño/decisión | plan | implementación |
     refactor | debug | auditoría | documentación
   - **Superficie:** Claude web (diseño/estrategia/investigación sin tocar el repo) | Claude Code
     (si acaba en commit)
   - **Modelo:** haiku (mecánico) | sonnet (implementación, debug normal, investigación) | opus
     (plan de sistema núcleo, auditoría, debug atascado) | fable (sólo decisiones caras de
     revertir: topología de red, formato de chunk, protocolo, seams)
   - **Modo:** solo-responder | plan-mode | implementar | auditar
   - **Herramientas:** búsqueda web sí/no · subagente (explorador / auditor-arquitectura /
     revisor-diffs / documentador) sí/no
   - **Ficheros a leer primero:** rutas exactas y mínimas
   - **Riesgo ADR:** ¿contradice o cambia algún ADR? Si sí, exige auditoría previa (regla dura 2) y,
     si toca una API pública, ADR nuevo ANTES de código (regla 7)
   - **Gate que tendrá que pasar:** qué correrá `tools/dev/validate-scope.ps1` con esas rutas
     (Rust → fmt + clippy + test · `Assets/**.cs` → CompileCheckClient · siempre → presupuesto de
     arranque e índice de ADR)
4. **PROMPT EJECUTABLE**, máx. 12 líneas: contexto mínimo (rutas, no contenido), objetivo único,
   restricciones, formato de salida y criterio de éxito verificable.

Reglas de decisión:
- Si la tarea mezcla varias cosas: trocéala en fichas separadas y dilo.
- Si falta un dato que cambia el diseño: 1 pregunta, no más.
- Por defecto: sonnet + Claude Code. Escalar modelo sólo con justificación de una línea.
