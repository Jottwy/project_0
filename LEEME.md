# Pack de configuración Claude — Backrooms Survival MMO

## Instalación (5 min)
1. Copia el contenido de esta carpeta a la RAÍZ de tu repo (CLAUDE.md, docs/, .claude/).
   Si ya tienes README/PROJECT.md/SETUP.md, no se tocan: conviven.
2. Ajusta docs/DECISIONS.md y docs/STATE.md a tu realidad actual (5 líneas).
3. Reinicia Claude Code dentro del repo (los agentes y skills se cargan al arrancar la sesión).
4. Verifica: escribe `/` y deberían aparecer: ruta, plan, auditar, debug, checkpoint, siguiente.
   Escribe `/agents` y deberían listarse: auditor-arquitectura, revisor-diffs, explorador, documentador.

## Rutina diaria
ABRIR:   /siguiente  →  (si la tarea es grande) /plan  →  aprobar  →  implementar
CERRAR:  /auditar  →  commit  →  /checkpoint  →  /clear

## Política de modelos (resumen)
- Por defecto en Claude Code: Sonnet 4.6.
- Opus 4.8: planes de sistemas núcleo, auditorías, debug atascado (>2 intentos con Sonnet).
- Fable 5: SOLO decisiones caras de revertir (ADR-003 red, ADR-004 chunks/protocolo, seams, diseño de workflows). 
  Ventana clave: incluido en límites del plan Max hasta el 22-jun-2026; después consume créditos extra y gasta límites ~2x más rápido que Opus. Front-cargar las auditorías de arquitectura AHORA.
- Haiku 4.5: documentación, checkpoints, exploración masiva (vía subagentes, ya configurado).

## Reglas de oro anti-tokens
1. Nunca pegues código en el prompt: da rutas. Claude Code lee lo que necesita.
2. Una tarea = una sesión = /clear al final. /compact solo si DEBES continuar un hilo largo.
3. Pide salida en diff, nunca archivos completos.
4. Lectura masiva → subagente explorador (contexto aislado, modelo barato).
5. CLAUDE.md y docs/ son punteros y contratos, no enciclopedias. Si un doc crece >200 líneas, recórtalo.

## Primera sesión recomendada con este sistema
1. `/ruta valida ADR-003 (topología de red) y ADR-004 (formato de chunk) antes de Sesión 1`
2. Esa sesión: modelo Fable 5 (ventana gratuita), modo plan, con auditor-arquitectura.
3. Resultado: ADR-003 y ADR-004 pasan a "validada" → recién entonces, Sesión 1 de implementación con Sonnet.
