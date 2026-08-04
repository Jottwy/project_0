---
name: auditar
description: Lanza la auditoría del trabajo actual contra arquitectura, ADRs y convenciones usando los subagentes de revisión. Usar al terminar una implementación y antes de commit en sistemas núcleo.
disable-model-invocation: true
---
Objeto a auditar: $ARGUMENTS (si está vacío: el diff actual de git)

1. Determina el alcance con `.claude/.session-touched-<session_id>` de esta sesión. Si no existe o el session id no está disponible, usa `git diff --name-only` y declara que el alcance puede ser impreciso.
2. Ejecuta la validación aplicable: Rust = `cargo +stable-x86_64-pc-windows-gnu fmt --manifest-path backend/Cargo.toml --all -- --check`, clippy `--all-targets -- -D warnings` y test; C# = validación disponible del proyecto y `dotnet format` solo con proyecto generado compatible; solo docs/config = ninguna suite de código.
3. Lanza el subagente revisor-diffs sobre las rutas del alcance.
4. Si el cambio toca un sistema núcleo (worldgen, red, persistencia, regiones) o cualquier contrato, lanza TAMBIÉN el subagente auditor-arquitectura.
5. Sintetiza en máx. 15 líneas:
   - VEREDICTO GLOBAL: commit sí/no
   - Bloqueantes (con archivo:línea)
   - Acción mínima para desbloquear
No arregles nada todavía: primero el veredicto, luego el humano decide.
