---
name: checkpoint
description: Cierra la sesión actualizando docs/STATE.md y dejando commit limpio. Usar SIEMPRE al final de cada sesión de trabajo.
disable-model-invocation: true
---
Cierre de sesión:
0. Nunca uses staging masivo ni `git commit -a/--all`; lista y añade solo rutas revisadas de esta sesión.
1. Determina el alcance con `.claude/.session-touched-<session_id>` de esta sesión. Si no existe o el session id no está disponible, usa `git diff --name-only` y declara que el alcance puede ser impreciso.
2. Ejecuta el gate aplicable: Rust = `cargo +stable-x86_64-pc-windows-gnu fmt --manifest-path backend/Cargo.toml --all -- --check`, clippy `--all-targets -- -D warnings` y test; C# = validación disponible del proyecto y `dotnet format` solo con proyecto generado compatible; solo docs/config = ninguna suite de código. Si cualquier validación aplicable falla, RECHAZA el commit y termina con la acción mínima para corregirlo.
3. Lanza el subagente documentador para actualizar docs/STATE.md con: qué se hizo (hechos, rutas), próximo paso ÚNICO, pendientes a medias, riesgos nuevos, y sección "NO tocar" si se validó algo.
4. Si en la sesión se tomó una decisión de arquitectura aprobada por el humano: el documentador añade el ADR correspondiente.
5. Propón mensaje de commit (convención de CONVENTIONS.md) y, si hay hito validado, el tag.
6. Devuelve el resumen final en máx. 10 líneas. Recuérdame hacer /clear después.
