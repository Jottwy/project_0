---
name: checkpoint
description: Cierra la sesión actualizando docs/STATE.md y dejando commit limpio. Usar SIEMPRE al final de cada sesión de trabajo.
disable-model-invocation: true
---
Cierre de sesión:
1. Lanza el subagente documentador para actualizar docs/STATE.md con: qué se hizo (hechos, rutas), próximo paso ÚNICO, pendientes a medias, riesgos nuevos, y sección "NO tocar" si se validó algo.
2. Si en la sesión se tomó una decisión de arquitectura aprobada por el humano: el documentador añade el ADR correspondiente.
3. Verifica build/tests del área tocada. Estado en una línea.
4. Propón mensaje de commit (convención de CONVENTIONS.md) y, si hay hito validado, el tag.
5. Devuelve el resumen final en máx. 10 líneas. Recuérdame hacer /clear después.
