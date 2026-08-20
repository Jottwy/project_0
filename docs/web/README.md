# docs/web — documentos publicados como Artifact

Cada fila es un documento publicado en claude.ai. La **fuente** es el HTML que se
republica: publicar el mismo fichero conserva la URL; publicar otro fichero crea
un artifact nuevo.

| Documento | URL | Fuente | Estado |
|---|---|---|---|
| Compendio | `claude.ai/code/artifact/8845e548-f4b3-4f9d-9aa1-b78961b55c33` | `docs/web/compendio.html` (aquí) | vigente — comprime los otros cuatro |
| Análisis de loop | `claude.ai/code/artifact/27c4bfe4-3a84-4fe5-8e50-8371e99a6d68` | scratchpad de sesión (`dossier-loop.html`) | vigente · rev. 34 |
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
