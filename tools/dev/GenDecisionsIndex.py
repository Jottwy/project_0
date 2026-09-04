# -*- coding: utf-8 -*-
"""Genera `docs/DECISIONS-INDEX.md` a partir de los encabezados de `docs/DECISIONS.md`.

    python tools/dev/GenDecisionsIndex.py            # escribe el índice
    python tools/dev/GenDecisionsIndex.py --check    # sólo comprueba que está al día (exit 2 si no)
    python tools/dev/GenDecisionsIndex.py --check --staged   # igual, pero contra lo que se va a COMMITEAR

Por qué existe: `docs/DECISIONS.md` pesa 1,38 MB (~385 000 tokens) y es ilegible entero. Este índice
cabe en una lectura, y la forma de leer un ADR concreto pasa a ser `grep -n '^## ADR-NNN'` seguido de
`Read offset/limit` desde esa línea.

**Anclas por encabezado, nunca por número de línea**: `DECISIONS.md` es append-only y cualquier ancla
posicional caduca con el siguiente ADR. Por lo mismo, aquí NO hay una constante con el número de ADR
esperados: la integridad la comprueba `--check`, que regenera y compara.

**`--staged` lee los dos ficheros del índice de git (`git show :ruta`), no del disco.** Es lo que
necesita un gate de commit: un índice regenerado en el árbol de trabajo pero sin estacionar pasaría
un `--check` normal y entraría al repositorio desactualizado igual.

**Cobertura de encabezados, y por qué no basta con `--check`.** Un encabezado que no encaje con el
formato no rompe la comparación: simplemente no genera fila, y el índice sale «al día» sin él. Pasó
de verdad — `## ADR-121 y ADR-122 — Aprobación` (dos números en un encabezado) se perdía en silencio,
y con él los dos únicos ADR que pasaban de PROPUESTA a ACEPTADA. Por eso `parse` cuenta primero las
líneas que EMPIEZAN por `## ADR-` y falla si alguna no ha producido entrada.

Se escribe en Python y no en PowerShell a propósito: `Set-Content` en PS 5.1 destroza los acentos del
fichero sin dar error, y este índice es todo acentos.
"""
import io
import os
import re
import subprocess
import sys

SOURCE = 'docs/DECISIONS.md'
TARGET = 'docs/DECISIONS-INDEX.md'
MAX_TITLE = 48

# Toda linea que dice ser un encabezado de ADR. Lo que case aqui y no case con HEADING es un
# encabezado perdido, y el generador sale en rojo en vez de omitirlo.
HEADING_CLAIM = re.compile(r'^## ADR-\d+')
# Un encabezado puede cubrir VARIOS numeros: "## ADR-121 y ADR-122 — Aprobación ...".
HEADING = re.compile(r'^## (ADR-\d+(?:\s*(?:,|y|/|&)\s*ADR-\d+)*)\s*[—-]\s*(.*)$')
ADR_NUMBER = re.compile(r'ADR-\d+')
DATE = re.compile(r'\((\d{4}-\d{2}-\d{2})\)')
AMENDMENT = re.compile(r'^Enmienda\s+(\d+)\s*:?\s*', re.IGNORECASE)
# Estado: lo que va tras el ultimo " — " del encabezado, si esta en mayusculas.
STATE_WORDS = ('ACEPTADA', 'VALIDADA', 'PROPUESTA', 'RECHAZADA', 'SUPERADA', 'DEROGADA',
               'APROBADA', 'CONGELADA', 'PENDIENTE', 'IMPLEMENTADA')
# Relaciones declaradas en el propio encabezado.
RELATION = re.compile(r'(sustituye a|sustituida por|deroga|derogada por|reemplaza a|restringe|supera a)'
                      r'\s+(ADR-\d+)', re.IGNORECASE)
# Relacion declarada en el CUERPO, en su propia linea, que es el formato nuevo (ver CONVENTIONS.md):
#     Sustituye a: ADR-107
# Vive en el cuerpo y no en el encabezado porque el encabezado ya carga titulo, fecha y estado, y
# porque una supersesion se descubre casi siempre DESPUES de escribir el ADR: entonces se declara
# en una enmienda nueva, que es lo unico que la regla 11 (append-only) permite.
BODY_RELATION = re.compile(r'^(Sustituye a|Sustituida por|Deroga|Derogada por|Restringe|Supera a)\s*:\s*'
                           r'(ADR-\d+)\s*$', re.IGNORECASE)


class FormatError(Exception):
    """Un encabezado `## ADR-` que el parser no sabe leer. Nunca se ignora en silencio."""


def parse(text):
    entries = []
    claimed = 0      # lineas que EMPIEZAN por "## ADR-"
    read = 0         # encabezados que el parser ha sabido leer; si no coinciden, error
    pending = None   # (indices en entries) del ultimo encabezado, para colgarle una relacion de cuerpo
    for line in text.split('\n'):
        if HEADING_CLAIM.match(line):
            claimed += 1
        else:
            found = BODY_RELATION.match(line.strip())
            if found and pending is not None:
                for index in pending:
                    adr, date, state, title, relation = entries[index]
                    if not relation or relation.startswith('enm.'):
                        relation = '%s %s' % (found.group(1).lower(), found.group(2))
                        entries[index] = (adr, date, state, title, relation)
            continue

        match = HEADING.match(line)
        if not match:
            raise FormatError(line.strip())
        numbers, rest = ADR_NUMBER.findall(match.group(1)), match.group(2).strip()

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
            relation = 'enm. %s de %s' % (amendment.group(1), numbers[0])
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

        # Un encabezado que cubre varios numeros aporta una entrada a CADA uno: es lo que hace que
        # "## ADR-121 y ADR-122 — Aprobación ... ACEPTADAS" mueva el estado de los dos.
        pending = []
        for number in numbers:
            pending.append(len(entries))
            entries.append((number, date, state, title, relation))
        read += 1
    if claimed != read:
        # No puede pasar: HEADING_CLAIM y HEADING se han desincronizado. Fallar es la unica salida
        # segura, porque la alternativa es un indice al que le faltan ADR y que dice estar al dia.
        raise FormatError('%d lineas "## ADR-" y %d encabezados leidos' % (claimed, read))
    return entries, read


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


def render(entries, headings):
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
               % (len(rows), headings))
    out.append('')
    return '\n'.join(out)


def staged_blob(path):
    """Contenido de `path` tal y como quedará en el commit (el índice de git), no el del disco.

    Devuelve `None` si la ruta no está en el índice — que para el gate significa «no la mires».
    """
    try:
        result = subprocess.run(['git', 'show', ':' + path], capture_output=True)
    except OSError as error:
        raise FormatError('no se pudo ejecutar git: %s' % error)
    if result.returncode != 0:
        return None
    return result.stdout.decode('utf-8')


def main(argv):
    staged = '--staged' in argv
    if staged and '--check' not in argv:
        sys.stderr.write('--staged solo tiene sentido con --check: el modo escritura escribe en el disco\n')
        return 2

    if staged:
        text = staged_blob(SOURCE)
        if text is None:
            # `git show :ruta` lee el INDICE, que contiene todos los ficheros trackeados, no solo
            # los modificados: llegar aqui significa que DECISIONS.md no esta en el indice (borrado
            # o fuera del repo), y entonces no hay nada contra lo que comparar.
            print('%s no esta en el indice de git: nada que comprobar' % SOURCE)
            return 0
    else:
        if not os.path.exists(SOURCE):
            sys.stderr.write('no encuentro %s: ejecuta desde la raiz del repo\n' % SOURCE)
            return 2
        text = io.open(SOURCE, encoding='utf-8', newline='').read()

    try:
        entries, headings = parse(text)
    except FormatError as error:
        sys.stderr.write(
            'ENCABEZADO DE ADR QUE EL PARSER NO SABE LEER en %s: %s\n'
            'No se omite en silencio: un ADR que no entra en el indice es un ADR invisible.\n'
            'Arregla el encabezado o el parser (tools/dev/GenDecisionsIndex.py).\n' % (SOURCE, error))
        return 2
    if not entries:
        sys.stderr.write('cero encabezados "## ADR-" en %s: el formato ha cambiado\n' % SOURCE)
        return 2
    rendered = render(entries, headings)

    if '--check' in argv:
        if staged:
            current = staged_blob(TARGET)
            where = 'estacionado'
            if current is None:
                current = ''
        else:
            current = io.open(TARGET, encoding='utf-8', newline='').read() if os.path.exists(TARGET) else ''
            where = 'en disco'
        if current != rendered:
            sys.stderr.write('%s (%s) esta desactualizado: regeneralo con '
                             'python tools/dev/GenDecisionsIndex.py y estacionalo\n' % (TARGET, where))
            return 2
        print('%s al dia (%s): %d entradas, %d B'
              % (TARGET, where, len(entries), len(rendered.encode('utf-8'))))
        return 0

    io.open(TARGET, 'w', encoding='utf-8', newline='').write(rendered)
    print('%s escrito: %d entradas, %d B' % (TARGET, len(entries), len(rendered.encode('utf-8'))))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
