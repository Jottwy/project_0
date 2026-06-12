---
name: auditar
description: Lanza la auditoría del trabajo actual contra arquitectura, ADRs y convenciones usando los subagentes de revisión. Usar al terminar una implementación y antes de commit en sistemas núcleo.
disable-model-invocation: true
---
Objeto a auditar: $ARGUMENTS (si está vacío: el diff actual de git)

1. Lanza el subagente revisor-diffs sobre el diff/archivos.
2. Si el cambio toca un sistema núcleo (worldgen, red, persistencia, regiones) o cualquier contrato, lanza TAMBIÉN el subagente auditor-arquitectura.
3. Sintetiza en máx. 15 líneas:
   - VEREDICTO GLOBAL: commit sí/no
   - Bloqueantes (con archivo:línea)
   - Acción mínima para desbloquear
No arregles nada todavía: primero el veredicto, luego el humano decide.
