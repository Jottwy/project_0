---
name: plan
description: Genera un plan de implementación cerrado y auditable para una tarea, sin escribir código. Usar antes de implementar nada que toque >1 archivo o un sistema núcleo.
disable-model-invocation: true
---
Tarea a planificar: $ARGUMENTS

1. Lee docs/STATE.md. Si la tarea toca worldgen/red/persistencia/regiones, lee también los ADRs relevantes de docs/DECISIONS.md (solo los relevantes).
2. NO escribas código. Devuelve:

PLAN
- Objetivo (1 línea) y criterio de éxito verificable (comando o test concreto)
- Archivos a crear/modificar (rutas exactas, nada más)
- Pasos numerados, cada uno ≤ ~80 líneas de diff estimadas
- Contratos que cambian (protocolo/formatos): si hay alguno → "REQUIERE ADR" y para ahí
- Qué NO se hace (anti scope-creep, mínimo 2 puntos)
- Riesgos (máx. 3) y plan de validación (tests/build/medición)

3. Termina con: "¿Apruebas el plan? (sí / cambios)". No implementes hasta aprobación explícita.
