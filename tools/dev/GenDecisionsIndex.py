# -*- coding: utf-8 -*-
"""Genera `docs/DECISIONS-INDEX.md` a partir de los encabezados de `docs/DECISIONS.md`.

    python tools/dev/GenDecisionsIndex.py            # escribe el índice
    python tools/dev/GenDecisionsIndex.py --check    # sólo comprueba que está al día (exit 2 si no)

Por qué existe: `docs/DECISIONS.md` pesa 1,38 MB (~385 000 tokens) y es ilegible entero. Este índice
cabe en una lectura, y la forma de leer un ADR concreto pasa a ser `grep -n '^## ADR-NNN'` seguido de
`Read offset/limit` desde esa línea.

**Anclas por encabezado, nunca por número de línea**: `DECISIONS.md` es append-only y cualquier ancla
posicional caduca con el siguiente ADR. Por lo mismo, aquí NO hay una constante con el número de ADR
esperados: la integridad la comprueba `--check`, que regenera y compara.

Se escribe en Python y no en PowerShell a propósito: `Set-Content` en PS 5.1 destroza los acentos del
fichero sin dar error, y este índice es todo acentos.
"""
import io
import os
import re
import sys

SOURCE = 'docs/DECISIONS.md'
TARGET = 'docs/DECISIONS-INDEX.md'
MAX_TITLE = 48

HEADING = re.compile(r'^## (ADR-\d+)\s*[—-]\s*(.*)$')
DATE = re.compile(r'\((\d{4}-\d{2}-\d{2})\)')
AMENDMENT = re.compile(r'^Enmienda\s+(\d+)\s*:?\s*', re.IGNORECASE)
# Estado: lo que va tras el ultimo " — " del encabezado, si esta en mayusculas.
STATE_WORDS = ('ACEPTADA', 'VALIDADA', 'PROPUESTA', 'RECHAZADA', 'SUPERADA', 'DEROGADA',
               'APROBADA', 'CONGELADA', 'PENDIENTE', 'IMPLEMENTADA')
# Relaciones declaradas en el propio encabezado.
RELATION = re.compile(r'(sustituye a|sustituida por|deroga|derogada por|restringe|supera a)\s+(ADR-\d+)',
                      re.IGNORECASE)


def parse(text):
    entries = []
    for line in text.split('\n'):
        match = HEADING.match(line)
        if not match:
            continue
        adr, rest = match.group(1), match.group(2).strip()

        date = ''
        found = DATE.search(rest)
        if found:
            date = found.group(1)

        state = ''
        for word in STATE_WORDS:
            if word in rest.upper():
                state = word
                break

        relation = ''
        amendment = AMENDMENT.match(rest)
        if amendment:
            relation = 'enm. %s de %s' % (amendment.group(1), adr)
            rest = AMENDMENT.sub('', rest)
        found = RELATION.search(rest)
        if found:
            relation = '%s %s' % (found.group(1).lower(), found.group(2))

        # El titulo es lo que queda tras quitar fecha y estado.
        title = DATE.sub('', rest)
        if state:
            title = re.split(r'\s+[—-]\s+', title)[0]
        title = re.sub(r'\s+', ' ', title).strip(' —-·')
        if len(title) > MAX_TITLE:
            title = title[:MAX_TITLE - 1].rstrip() + '…'

        entries.append((adr, date, state, title, relation))
    return entries


def collapse(entries):
    """Una fila por NÚMERO de ADR, no por encabezado.

    Las enmiendas no llevan fila propia: se cuentan. `grep '^## ADR-105'` devuelve el ADR y sus
    enmiendas con sus títulos, así que la fila sólo tiene que llevar hasta el número. Con una fila
    por encabezado el índice se iba a 15 KB y dejaba de caber en el presupuesto de arranque.
    """
    rows = {}
    order = []
    for adr, date, state, title, relation in entries:
        if adr not in rows:
            rows[adr] = {'title': title, 'date': date, 'state': state, 'extra': 0, 'rel': relation}
            order.append(adr)
            continue
        row = rows[adr]
        row['extra'] += 1
        if date > row['date']:
            row['date'] = date
        if state:
            row['state'] = state
        if relation and not relation.startswith('enm.'):
            row['rel'] = relation
    return [(adr, rows[adr]) for adr in order]


def render(entries):
    rows = collapse(entries)
    out = [
        '# docs/DECISIONS-INDEX.md — Índice de ADR',
        '',
        '> **Generado por `tools/dev/GenDecisionsIndex.py`. No editar a mano.**',
        '> Una fila por NÚMERO de ADR. Para leer uno: `grep -n "^## ADR-105" docs/DECISIONS.md`,',
        '> que devuelve el ADR **y todas sus enmiendas** con sus títulos, y luego `Read offset/limit`',
        '> desde la línea que interese. «enm.» es cuántos encabezados más lleva ese número; fecha y',
        '> estado son los del último.',
        '',
        '`ADR-NNN [+N enm.] fecha ESTADO — título [· relación]`',
        '',
    ]
    for adr, row in rows:
        parts = [adr]
        if row['extra']:
            parts.append('+%d enm.' % row['extra'])
        if row['date']:
            parts.append(row['date'])
        if row['state']:
            parts.append(row['state'])
        line = ' '.join(parts) + ' — ' + row['title']
        if row['rel']:
            line += ' · ' + row['rel']
        out.append(line)
    out.append('')
    out.append('Total: %d números de ADR, %d encabezados `## ADR-` en `docs/DECISIONS.md`.'
               % (len(rows), len(entries)))
    out.append('')
    return '\n'.join(out)


def main(argv):
    if not os.path.exists(SOURCE):
        sys.stderr.write('no encuentro %s: ejecuta desde la raiz del repo\n' % SOURCE)
        return 2
    text = io.open(SOURCE, encoding='utf-8', newline='').read()
    entries = parse(text)
    if not entries:
        sys.stderr.write('cero encabezados "## ADR-" en %s: el formato ha cambiado\n' % SOURCE)
        return 2
    rendered = render(entries)

    if '--check' in argv:
        current = io.open(TARGET, encoding='utf-8', newline='').read() if os.path.exists(TARGET) else ''
        if current != rendered:
            sys.stderr.write('%s esta desactualizado: regeneralo con '
                             'python tools/dev/GenDecisionsIndex.py\n' % TARGET)
            return 2
        print('%s al dia: %d entradas, %d B' % (TARGET, len(entries), len(rendered.encode('utf-8'))))
        return 0

    io.open(TARGET, 'w', encoding='utf-8', newline='').write(rendered)
    print('%s escrito: %d entradas, %d B' % (TARGET, len(entries), len(rendered.encode('utf-8'))))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
