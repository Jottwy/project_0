---
name: ruta
description: Router de trabajo. Convierte un mensaje bruto del usuario en una ficha de tarea: superficie (web/Code), modelo, modo, herramientas, archivos a leer y prompt ejecutable. Invocar manualmente con /ruta antes de tareas ambiguas o grandes.
disable-model-invocation: true
---
Recibes una idea bruta: $ARGUMENTS

NO ejecutes la tarea. Clasifícala y devuelve EXACTAMENTE esta ficha:

FICHA DE TAREA
- Tipo: respuesta-directa | investigación | diseño/decisión | plan | implementación | refactor | debug | auditoría | documentación
- Superficie: Claude web (si es diseño/estrategia/investigación sin tocar repo) | Claude Code (si acaba en commit)
- Modelo: haiku (mecánico) | sonnet (implementación, debug normal, investigación) | opus (plan de sistema núcleo, auditoría, debug atascado) | fable-5 (solo decisiones caras de revertir: topología red, formato chunk, protocolo, seams)
- Modo: solo-responder | plan-mode | implementar | auditar
- Herramientas: búsqueda web sí/no | subagente (explorador/auditor-arquitectura/revisor-diffs/documentador) sí/no
- Archivos a leer primero: rutas exactas, mínimas (STATE.md casi siempre; ADRs solo si toca sistema núcleo)
- Riesgo ADR: ¿contradice o cambia algún ADR? sí/no — si sí, exige auditoría previa

PROMPT EJECUTABLE
Reescribe la idea como prompt listo para pegar: contexto mínimo (rutas, no contenido), objetivo único, restricciones, formato de salida, criterio de éxito verificable. Máx. 12 líneas.

Reglas de decisión:
- Si la tarea mezcla varias cosas: trocéala en fichas separadas y dilo.
- Si falta un dato que cambia el diseño: 1 pregunta, no más.
- Por defecto: sonnet + Claude Code. Escalar modelo solo con justificación de una línea.
