#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""ADR-129 enm. 1 — hornea el ATLAS DE CARTELES de WorldGen3.

Sale un PNG de 2048 x 2048 con 8 x 8 celdas de 256: las variantes 0-31 con el texto bueno y las
32-63 con el mismo cartel ESTROPEADO (letras cambiadas, fechas imposibles), que es lo que se sirve
al bajar (ADR-130 D4). La variante `v` ocupa la fila `v // 8` y la columna `v % 8`, contando desde
arriba a la izquierda: `Wg3SignCatalog.UvOf` refleja exactamente esa cuenta.

**Cada celda es CUADRADA y el cartel no lo es.** El dibujo se hace a la proporcion real
(`sign_size_cm` en Rust, `SizeCm` en C#) y se estira a la celda; el quad del cliente, que si tiene
la proporcion real, deshace el estirado exactamente. Asi el atlas no necesita tabla de UV por
variante ni que nadie la copie a mano: la unica cuenta compartida es fila/columna.

    python tools/dev/BakeSignAtlas.py

Escribe Assets/Art/Signage/Resources/Wg3SignAtlas.png. Necesita Pillow y las fuentes de Windows.
"""

import os
import random
import sys

from PIL import Image, ImageDraw, ImageFont

CELL = 256
COLS = 8
ROWS = 8
ATLAS = CELL * COLS
# Margen transparente de cada celda, en pixeles del atlas. Espejo de `Wg3SignCatalog.Gutter`.
GUTTER = 4
# Se dibuja a este ancho y se baja a la celda: el remuestreo hace de antialiasing.
WORK_W = 1024

FONT_DIR = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")


def font(name, size):
    path = os.path.join(FONT_DIR, name)
    if not os.path.exists(path):
        raise SystemExit("falta la fuente " + path)
    return ImageFont.truetype(path, size)


# ─────────────────── el contenido ───────────────────
#
# Cada familia trae sus variantes buenas y, en el mismo orden, las rotas. La rotura no es ruido:
# son las mismas palabras con las letras cambiadas de sitio y las fechas imposibles, que es lo que
# se lee como «esto lo escribio alguien que ya no sabe escribir» y no como «falta la textura».

DOOR_PLATES = [
    ("301", "J. MARSH"),
    ("OFICINA", "12"),
    ("CONTABILIDAD", ""),
    ("SALA DE", "JUNTAS"),
    ("ARCHIVO", "B-2"),
    ("DIRECCION", ""),
    ("RECURSOS", "HUMANOS"),
    ("ALMACEN", "2"),
]
DOOR_PLATES_BAD = [
    ("3O1", "J. MRASH"),
    ("OFCINIA", "21"),
    ("CONTIBALADID", ""),
    ("SLAA ED", "JUNATS"),
    ("ARHCIVO", "B-\u0398"),
    ("DRIECCOIN", ""),
    ("RECRUSOS", "HUMNAOS"),
    ("ALMCAEN", "-4"),
]

TAGS = [
    "M. ROJAS",
    "PUESTO 14",
    "T. ABARCA",
    "VENTAS 03",
    "L. FENTON",
    "PUESTO 7",
    "R. QUINTANA",
    "SOPORTE",
]
TAGS_BAD = [
    "M. RJOSA",
    "PUESOT 41",
    "T. ABRACA",
    "VENATS 3O",
    "L. FNETON",
    "PUESTO -2",
    "R. QUINTNAA",
    "SPOORTE",
]

# (texto, flecha) — la flecha es -1 izquierda, 1 derecha, 0 ninguna, 2 arriba.
EXITS = [("SALIDA", 1), ("SALIDA", -1), ("SALIDA", 0), ("SALIDA", 2)]
EXITS_BAD = [("SALDIA", 1), ("ADILAS", -1), ("SAL1DA", 0), ("NO SALIDA", 2)]

# Los papeles de un tablon: cada uno con su titular.
CORKS = [
    ["TURNOS", "AVISO", "REUNION 9:00", "NO USAR"],
    ["EVACUACION", "TURNO B", "FIRMAR", "CAFE 0,60"],
    ["NORMAS", "AVERIA", "VACACIONES", "SE BUSCA"],
    ["INVENTARIO", "AVISO", "NO PASAR", "TURNOS"],
]
CORKS_BAD = [
    ["TRUNOS", "AVSIO", "REUNION 9:61", "NO USRA"],
    ["EVACUCAION", "TRUNO \u0398", "FIRMRA", "CAFE -0,60"],
    ["NORMSA", "AVREIA", "VACACINOES", "SE BSUCA"],
    ["INVENTRAIO", "AVISO", "NO PSAAR", "TRUNOS"],
]

CALENDARS = [("MARZO", 14), ("JUNIO", 7), ("OCTUBRE", 23), ("ENERO", 2)]
CALENDARS_BAD = [("MRAZO", 32), ("MES 14", 44), ("OCTBURE", 0), ("ENREO", 61)]

PLATE_BG = (222, 220, 212, 255)
PLATE_INK = (38, 38, 40, 255)
TAG_BG = (238, 236, 228, 255)
EXIT_BG = (24, 92, 52, 255)
EXIT_INK = (236, 240, 236, 255)
CORK_BG = (176, 132, 78, 255)
CORK_FRAME = (92, 66, 38, 255)
PAPER = (240, 238, 228, 255)
CAL_INK = (46, 46, 50, 255)
CAL_RED = (176, 48, 44, 255)


def centred(d, box, text, f, fill):
    """Texto centrado en `box` = (x0, y0, x1, y1)."""
    x0, y0, x1, y1 = box
    l, t, r, b = d.textbbox((0, 0), text, font=f)
    d.text((x0 + (x1 - x0 - (r - l)) / 2 - l, y0 + (y1 - y0 - (b - t)) / 2 - t), text, font=f,
           fill=fill)


def fit(d, box, text, name, fill, cap):
    """El texto mas grande que cabe en `box`, hasta `cap`."""
    x0, y0, x1, y1 = box
    for size in range(cap, 7, -2):
        f = font(name, size)
        l, t, r, b = d.textbbox((0, 0), text, font=f)
        if r - l <= x1 - x0 and b - t <= y1 - y0:
            centred(d, box, text, f, fill)
            return
    centred(d, box, text, font(name, 8), fill)


def new(aspect):
    """Lienzo a la proporcion real del cartel: `aspect` = ancho / alto."""
    img = Image.new("RGBA", (WORK_W, max(8, int(round(WORK_W / aspect)))), (0, 0, 0, 0))
    return img, ImageDraw.Draw(img)


def draw_plate(top, bottom):
    img, d = new(30 / 12)
    w, h = img.size
    d.rounded_rectangle((6, 6, w - 7, h - 7), radius=12, fill=PLATE_BG,
                        outline=(150, 148, 142, 255), width=4)
    if bottom:
        fit(d, (26, 20, w - 27, h // 2 - 4), top, "arialbd.ttf", PLATE_INK, 150)
        fit(d, (26, h // 2 + 2, w - 27, h - 21), bottom, "arial.ttf", PLATE_INK, 130)
    else:
        fit(d, (26, 22, w - 27, h - 23), top, "arialbd.ttf", PLATE_INK, 170)
    return img


def draw_tag(text):
    img, d = new(24 / 9)
    w, h = img.size
    d.rectangle((4, 4, w - 5, h - 5), fill=TAG_BG, outline=(120, 118, 112, 255), width=4)
    d.rectangle((4, 4, w - 5, 22), fill=(96, 104, 122, 255))
    fit(d, (24, 34, w - 25, h - 22), text, "arialbd.ttf", (32, 32, 36, 255), 150)
    return img


def draw_exit(text, arrow):
    img, d = new(40 / 15)
    w, h = img.size
    d.rectangle((0, 0, w - 1, h - 1), fill=EXIT_BG)
    d.rectangle((5, 5, w - 6, h - 6), outline=(200, 220, 205, 255), width=4)
    box = (30, 24, w - 31, h - 25)
    if arrow:
        a = int(h * 0.62)
        cy = h // 2
        if arrow == 2:
            box = (30, 24, w - 34 - a, h - 25)
            cx = w - 30 - a // 2
            pts = [(cx, cy - a // 2), (cx + a // 2, cy + a // 4), (cx - a // 2, cy + a // 4)]
        elif arrow > 0:
            box = (30, 24, w - 34 - a, h - 25)
            cx = w - 30 - a // 2
            pts = [(cx + a // 2, cy), (cx - a // 2, cy - a // 2), (cx - a // 2, cy + a // 2)]
        else:
            box = (34 + a, 24, w - 31, h - 25)
            cx = 30 + a // 2
            pts = [(cx - a // 2, cy), (cx + a // 2, cy - a // 2), (cx + a // 2, cy + a // 2)]
        d.polygon(pts, fill=EXIT_INK)
    fit(d, box, text, "arialbd.ttf", EXIT_INK, 190)
    return img


def draw_cork(titles, rnd):
    img, d = new(120 / 90)
    w, h = img.size
    d.rectangle((0, 0, w - 1, h - 1), fill=CORK_BG)
    d.rectangle((0, 0, w - 1, h - 1), outline=CORK_FRAME, width=18)
    # Grano del corcho: motas, no ruido de television.
    for _ in range(2600):
        x, y = rnd.randrange(20, w - 20), rnd.randrange(20, h - 20)
        s = rnd.choice((2, 2, 3))
        v = rnd.randrange(-26, 26)
        d.ellipse((x, y, x + s, y + s), fill=(176 + v, 132 + v, 78 + v, 255))
    slots = [(0.06, 0.07, 0.44, 0.46), (0.52, 0.05, 0.94, 0.38),
             (0.05, 0.56, 0.46, 0.93), (0.53, 0.45, 0.95, 0.92)]
    for (title, (fx0, fy0, fx1, fy1)) in zip(titles, slots):
        x0, y0 = int(w * fx0), int(h * fy0)
        x1, y1 = int(w * fx1), int(h * fy1)
        d.rectangle((x0 + 5, y0 + 6, x1 + 5, y1 + 6), fill=(120, 92, 54, 110))
        d.rectangle((x0, y0, x1, y1), fill=PAPER, outline=(196, 194, 186, 255), width=2)
        fit(d, (x0 + 12, y0 + 10, x1 - 12, y0 + (y1 - y0) // 3), title, "arialbd.ttf",
            (40, 40, 44, 255), 70)
        # Las lineas del cuerpo: a esta distancia un texto de verdad seria ruido.
        ly = y0 + (y1 - y0) // 3 + 16
        while ly < y1 - 16:
            d.line((x0 + 14, ly, x1 - 14 - rnd.randrange(0, (x1 - x0) // 3), ly),
                   fill=(126, 126, 130, 255), width=3)
            ly += 18
        cx = (x0 + x1) // 2
        d.ellipse((cx - 9, y0 + 6, cx + 9, y0 + 24), fill=(190, 60, 56, 255))
    return img


def draw_calendar(month, day):
    img, d = new(30 / 42)
    w, h = img.size
    d.rectangle((0, 0, w - 1, h - 1), fill=PAPER, outline=(170, 168, 160, 255), width=4)
    head = int(h * 0.17)
    d.rectangle((4, 4, w - 5, head), fill=(58, 62, 78, 255))
    fit(d, (16, 10, w - 17, head - 6), month, "arialbd.ttf", (238, 238, 234, 255), 120)
    # Las dos anillas.
    for fx in (0.3, 0.7):
        cx = int(w * fx)
        d.ellipse((cx - 12, 14, cx + 12, 38), outline=(150, 150, 150, 255), width=5)
    top = head + 14
    cw = (w - 40) / 7.0
    ch = (h - top - 20) / 6.0
    f = font("arial.ttf", max(10, int(ch * 0.52)))
    n = 1
    for row in range(6):
        for col in range(7):
            if n > 31:
                continue
            x = 20 + col * cw
            y = top + row * ch
            if n == day:
                d.ellipse((x + 3, y + 3, x + cw - 3, y + ch - 3), outline=CAL_RED, width=5)
            centred(d, (x, y, x + cw, y + ch), str(n), f, CAL_INK)
            n += 1
    # El dia imposible se marca igual, aunque no exista en la reticula: se tacha el mes.
    if day == 0 or day > 31:
        d.line((14, top + ch * 2, w - 14, top + ch * 4), fill=CAL_RED, width=7)
        fit(d, (20, int(h * 0.42), w - 21, int(h * 0.58)), str(day), "arialbd.ttf", CAL_RED, 150)
    return img


def cells():
    """Las 64 celdas en orden de variante. `None` = celda libre (28-31 y sus gemelas)."""
    rnd = random.Random(1291)
    out = []
    for bad in (False, True):
        plates = DOOR_PLATES_BAD if bad else DOOR_PLATES
        tags = TAGS_BAD if bad else TAGS
        exits = EXITS_BAD if bad else EXITS
        corks = CORKS_BAD if bad else CORKS
        cals = CALENDARS_BAD if bad else CALENDARS
        out += [draw_plate(a, b) for (a, b) in plates]
        out += [draw_tag(t) for t in tags]
        out += [draw_exit(t, a) for (t, a) in exits]
        out += [draw_cork(c, random.Random(rnd.randrange(1 << 30))) for c in corks]
        out += [draw_calendar(m, dd) for (m, dd) in cals]
        out += [None] * 4
    return out


def main():
    here = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    dst = os.path.join(here, "Assets", "Art", "Signage", "Resources", "Wg3SignAtlas.png")
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    atlas = Image.new("RGBA", (ATLAS, ATLAS), (0, 0, 0, 0))
    for v, img in enumerate(cells()):
        if img is None:
            continue
        # El GUTTER es lo que separa una celda de la siguiente. Sin el, el filtrado bilineal y los
        # mipmaps traen pixeles del cartel de al lado al borde de este: una placa con una franja
        # verde de la senal de salida vecina. `Wg3SignCatalog.Gutter` refleja este numero.
        cell = img.resize((CELL - 2 * GUTTER, CELL - 2 * GUTTER), Image.LANCZOS)
        atlas.paste(cell, ((v % COLS) * CELL + GUTTER, (v // COLS) * CELL + GUTTER))
    atlas.save(dst)
    print("[carteles] %s (%dx%d, %dx%d celdas de %d)" % (dst, ATLAS, ATLAS, COLS, ROWS, CELL))


if __name__ == "__main__":
    sys.exit(main())
