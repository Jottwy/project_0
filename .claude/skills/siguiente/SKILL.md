---
name: siguiente
description: Arranque de sesión. Lee el estado del proyecto y propone la siguiente tarea concreta con su ficha de ruta. Usar al abrir cada sesión.
disable-model-invocation: true
---
1. Lee docs/STATE.md (y solo eso, salvo que el "Próximo paso" exija un ADR concreto).
2. Devuelve en máx. 12 líneas:
   - Estado en 2 líneas
   - SIGUIENTE TAREA: una sola, concreta, terminable en una sesión
   - Modelo recomendado y modo (plan/implementar) con justificación de 1 línea
   - Primer comando o acción exacta para empezar
3. Si el "Próximo paso" de STATE.md es ambiguo o demasiado grande, trocéalo y propón solo el primer pedazo.
