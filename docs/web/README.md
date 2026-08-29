# docs/web — documentos publicados como Artifact

Cada fila es un documento publicado en claude.ai. La **fuente** es el HTML que se
republica: publicar el mismo fichero conserva la URL; publicar otro fichero crea
un artifact nuevo.

| Documento | URL | Fuente | Estado |
|---|---|---|---|
| Compendio | `claude.ai/code/artifact/8845e548-f4b3-4f9d-9aa1-b78961b55c33` | `docs/web/compendio.html` (aquí) | **vigente · corte 2026-08-29 noche** — reescrito entero para la era WorldGen3 (ADR-095 a 109), con la mudanza de autoridad cerrada. Ya no comprime sólo a los otros cuatro: su fuente principal es el repo (`STATE.md`, `WG3-ROADMAP.md`, `DECISIONS.md`, `AUDIT-2026-08-28.md`) |
| Análisis de loop | `claude.ai/code/artifact/27c4bfe4-3a84-4fe5-8e50-8371e99a6d68` | **`docs/web/dossier-loop.html` (aquí, recuperado del artifact vivo el 2026-08-29)** | **vigente · rev. 36** — pestaña «Mundo — WG3» con la mudanza CERRADA y la retirada por etapas (ADR-109), tramo 0.0.0.14 en Versiones, cinco entradas de devlog y calendario resuelto en favor del repo. La pestaña Semana queda archivada |
| Debt Roadmap | `claude.ai/code/artifact/5ff6680e-6a11-4b64-abc8-49be3fd0e92e` | scratchpad de sesión (`debt-roadmap.html`) | vigente — tracker de los 68 hallazgos |
| Economía de supervivencia | `claude.ai/code/artifact/60ed8d22-6328-4a23-8187-fc0580e544f0` | scratchpad de sesión (`economia-supervivencia.html`) | vigente |
| Dónde está cada ajuste | `claude.ai/code/artifact/ea8cdb8b-e526-4867-b5f0-9f9c8047de4f` | scratchpad de sesión (`settings-manual.html`) | vigente |
| Análisis de loop — variante Level 0 | `claude.ai/code/artifact/6f15dffc-98f3-4f4c-bd3e-827500556e1c` | scratchpad de sesión (`loop-analysis.html`) | **superado** por la rev. 34 |

## Regla de sincronización del Compendio

El Compendio comprime a los otros cuatro. **Cuando se toque el Análisis de loop,
en la misma sesión se recalculan las secciones 01 (estado del loop) y 04 (roadmap
y negocio) de `compendio.html` y se republica en su URL.** Lo mismo con Economía
(sección 02), Debt Roadmap (sección 03) y el manual de ajustes (sección 05).

Una página publicada no puede leer otra: el CSP bloquea peticiones fuera del host
y ninguna de las capacidades disponibles (`artifact`, `downloads`, `mcp`, `self`)
da lectura de otro artifact. La sincronización es un paso de sesión, no un enlace
vivo dentro del HTML.

Para comprobar si el Compendio se ha quedado atrás, sin reescribir nada:

1. Traer el HTML vivo del dossier (WebFetch sobre su URL guarda una copia completa
   en `~/.claude/projects/.../tool-results/`).
2. Extraer texto plano de esa copia y del último HTML comprimido, y `diff`.
   Cero líneas de diferencia = el Compendio está al día.

Última comprobación: **2026-08-20**, cero diferencias sobre 1.945 párrafos.

## Corte del 2026-08-29 — la regla de arriba cambia

El Compendio se reescribió entero (secciones 00 a 09) contra el estado del repo, no
contra los cuatro artifacts. Motivo: entre el 21 y el 29 de agosto el mundo pasó a
WorldGen3 (ADR-095 a 108, wire 38 → 50) y ninguno de los cuatro documentos lo cubre.

Lo que eso implica para la sincronización:

- El Compendio ya **no depende** del Análisis de loop para las secciones de estado; sus
  fuentes son `docs/STATE.md`, `docs/WG3-ROADMAP.md`, `docs/DECISIONS.md`,
  `docs/DEBT-ROADMAP.md`, `docs/AUDIT-2026-08-28.md`, `SCALING-` y `FARMING-ROADMAP.md`.
- Sigue heredando de los artifacts lo que no está en el repo: cifras de crowdfunding y
  contraste de género (Análisis de loop) y el manual de ajustes campo a campo.
- Nada del contenido publicado anteriormente se ha perdido: lo que dejó de ser estado
  —salas autoradas de ADR-083, pendientes de economía, rendimiento— se conserva
  recolocado en su sección nueva.

## Aviso sobre las fuentes en scratchpad

Las fuentes marcadas «scratchpad de sesión» viven en
`%LOCALAPPDATA%\Temp\claude\J--Unity-BackroomsSurvivalMMO\<sesión>\scratchpad\` y
esa carpeta se limpia. Si desaparecen, el HTML vivo se recupera con WebFetch sobre
la URL del artifact — que además es la única vía, porque `curl` sobre esas URLs
devuelve el shell de la SPA o un 403.

## Compartir

Un artifact es privado hasta que se comparte desde el menú de la propia página.
Ojo con la versión fijada: el Análisis de loop está compartido por enlace y quien
lo abra ve una revisión anterior a la viva.
