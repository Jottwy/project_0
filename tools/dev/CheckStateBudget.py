# -*- coding: utf-8 -*-
"""Gate del presupuesto de arranque de sesión.

Comprueba lo MEDIBLE de `docs/STATE.md` (topes, esquema, formato de tanda) y el
presupuesto de los cuatro ficheros que se cargan al abrir sesión. No juzga prosa:
la compresión la escribe una persona, este script sólo impide que crezca otra vez.

    python tools/dev/CheckStateBudget.py
    python tools/dev/CheckStateBudget.py --staged   # mide lo que se va a COMMITEAR, no el disco

Salida 0 = verde, 2 = rojo. Lo invoca `/checkpoint` antes de commitear y el gate de
commit (`tools/dev/validate-scope.ps1`) en cada `git commit`.

Por qué existe: hasta el 2026-09-04, STATE.md pesaba 688 532 B / 2 588 líneas y la
regla dura #1 de CLAUDE.md ("léelo antes de tocar nada") era incumplible — Read
rechaza el fichero entero (tope 256 KB) y cualquier tramo de más de 25 000 tokens.
Se podó una vez el 2026-08-04 y volvió a crecer de 283 a 688 KB en un mes porque
nadie medía. Esta es la medida.

`--staged` lee del índice de git (`git show :ruta`) en vez del disco: un STATE.md
recortado en el árbol de trabajo pero sin estacionar pasaría el gate y entraría al
repositorio con el tamaño viejo igual.
"""
import io
import os
import re
import subprocess
import sys

STATE_PATH = 'docs/STATE.md'

MAX_LINES = 200
MAX_BYTES = 20480
MAX_COLUMNS = 160
MAX_LINES_PER_BATCH = 8

# Tope de líneas de cuerpo por sección. La única que falta a propósito es
# "## Últimas tandas": es la desbordable, y su exceso va verbatim a SESSION-LOG.md.
SECTION_CAPS = {
    '## Estado': 5,
    '## Próximo paso ÚNICO': 3,
    '## En curso': 10,
    '## Riesgos abiertos': 20,
    '## NO tocar': 15,
    '## Deuda declarada': 25,
}

# Presupuesto de arranque: suma de topes + margen.
# El tope de INDEX.md es 4 KB y no 3: con 40 documentos reales y una línea por
# entrada, el suelo medido es 3,9 KB. Bajarlo obligaría a dejar ficheros fuera del
# índice, que es peor que 900 bytes.
#
# DECISIONS-INDEX.md: 12 KB, no 8. El índice es GENERADO y crece una línea por ADR
# nuevo — no se puede "comprimir" sin dejar ADRs fuera, que es exactamente el fallo
# que el gate existe para impedir. Con 8 192 B medía 8 014 (106 ADR): 178 B de
# margen, o sea rojo en unos seis ADRs por crecimiento legítimo, y la salida obvia
# habría sido subir el tope con prisa o recortar el índice. A ~76 B por fila, 12 288
# da sitio para unos 55 ADR más.
STARTUP_BUDGET = {
    'CLAUDE.md': 3072,
    'docs/INDEX.md': 4096,
    'docs/STATE.md': 20480,
    'docs/DECISIONS-INDEX.md': 12288,
}
STARTUP_TOTAL = 40960

BATCH_HEADING = re.compile(r'^### \d{4}-\d{2}-\d{2} — .+')


def body_lines(lines, start, end):
    """Líneas de contenido de una sección: sin su encabezado ni las vacías."""
    return [line for line in lines[start + 1:end] if line.strip()]


def read_bytes(path, staged):
    """Contenido de `path`: del índice de git si `staged`, del disco si no.

    Devuelve `None` cuando el fichero no existe en la fuente pedida.
    """
    if not staged:
        if not os.path.exists(path):
            return None
        with open(path, 'rb') as handle:
            return handle.read()
    try:
        result = subprocess.run(['git', 'show', ':' + path], capture_output=True)
    except OSError as error:
        sys.stderr.write('no se pudo ejecutar git: %s\n' % error)
        return None
    if result.returncode != 0:
        return None
    return result.stdout


def check_state(failures, staged):
    raw = read_bytes(STATE_PATH, staged)
    if raw is None:
        failures.append('%s no esta %s' % (STATE_PATH, 'en el indice de git' if staged else 'en disco'))
        return
    text = raw.decode('utf-8')
    # El arbol de trabajo puede estar en CRLF (autocrlf). Sin quitar el \r, ningun encabezado
    # coincide con SECTION_CAPS y los topes por seccion no se aplican en silencio.
    lines = [line.rstrip('\r') for line in text.split('\n')]

    total_lines = len(lines)
    total_bytes = len(text.encode('utf-8'))
    print('lineas %d / %d' % (total_lines, MAX_LINES))
    print('bytes  %d / %d' % (total_bytes, MAX_BYTES))
    if total_lines > MAX_LINES:
        failures.append('STATE.md: %d lineas, tope %d' % (total_lines, MAX_LINES))
    if total_bytes > MAX_BYTES:
        failures.append('STATE.md: %d bytes, tope %d' % (total_bytes, MAX_BYTES))

    for i, line in enumerate(lines):
        if len(line) > MAX_COLUMNS:
            failures.append('L%d: %d caracteres, tope %d' % (i + 1, len(line), MAX_COLUMNS))

    starts = [i for i, line in enumerate(lines) if line.startswith('## ')]
    bounds = list(zip(starts, starts[1:] + [len(lines)]))
    print('--- secciones ---')
    for start, end in bounds:
        title = lines[start]
        count = len(body_lines(lines, start, end))
        cap = SECTION_CAPS.get(title)
        over = cap is not None and count > cap
        print('%-26s %3d lineas%s' % (title[:26], count, '  <-- EXCEDE' if over else ''))
        if over:
            failures.append('%s: %d lineas de cuerpo, tope %d' % (title, count, cap))

    batches = [i for i, line in enumerate(lines) if line.startswith('### ')]
    print('--- tandas ---')
    for n, start in enumerate(batches):
        end = batches[n + 1] if n + 1 < len(batches) else len(lines)
        count = len(body_lines(lines, start, end))
        well_formed = bool(BATCH_HEADING.match(lines[start]))
        print('%-64s %2d lineas  encabezado %s'
              % (lines[start][:64], count, 'OK' if well_formed else 'MAL'))
        if not well_formed:
            failures.append('encabezado de tanda mal formado (### AAAA-MM-DD — titulo): %s'
                            % lines[start][:60])
        if count > MAX_LINES_PER_BATCH:
            failures.append('tanda "%s": %d lineas, tope %d'
                            % (lines[start][4:44], count, MAX_LINES_PER_BATCH))


def check_startup(failures, staged):
    print('--- presupuesto de arranque ---')
    total = 0
    for path, cap in STARTUP_BUDGET.items():
        raw = read_bytes(path, staged)
        exists = raw is not None
        size = len(raw) if exists else 0
        total += size
        note = '' if exists else '  (no existe todavia)'
        over = '  <-- EXCEDE' if size > cap else ''
        print('%-24s %6d / %6d B%s%s' % (path, size, cap, over, note))
        if size > cap:
            failures.append('%s: %d bytes, tope %d' % (path, size, cap))
    print('%-24s %6d / %6d B' % ('SUMA', total, STARTUP_TOTAL))
    if total > STARTUP_TOTAL:
        failures.append('presupuesto de arranque: %d bytes, tope %d' % (total, STARTUP_TOTAL))


def main(argv=()):
    staged = '--staged' in argv
    if not os.path.exists(STATE_PATH):
        sys.stderr.write('no encuentro %s: ejecuta desde la raiz del repo\n' % STATE_PATH)
        return 2
    print('fuente: %s' % ('indice de git (--staged)' if staged else 'arbol de trabajo'))
    failures = []
    check_state(failures, staged)
    check_startup(failures, staged)
    print('--- RESULTADO ---')
    if failures:
        print('ROJO:')
        for failure in failures:
            print('  - ' + failure)
        return 2
    print('VERDE')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
