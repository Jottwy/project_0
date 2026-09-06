#!/usr/bin/env python3
"""Compone el icono de inventario de un item a partir de dos fotogramas del editor.

    python tools/dev/MakeItemIcon.py Temp/icon/crankflashlight_icon_black.png \
        Temp/icon/crankflashlight_icon_white.png Assets/Art/Items/BR_CrankFlashlight_Icon.png

Los fotogramas los genera el creador del pickup ("Backrooms > Linterna > Crear la linterna del
suelo"): el MISMO render sobre fondo negro y sobre fondo blanco. De la diferencia entre los dos
sale el alfa por pixel -- donde los dos coinciden hay objeto opaco, donde difieren tanto como el
fondo hay transparencia, y en medio (bordes antialiasados) un alfa parcial que ningun recorte por
umbral daria.

Es el mismo tratamiento que llevaron los iconos del bote, del agua de almendras y del
destornillador, que vivia en un scratchpad de sesion y por eso hubo que reescribirlo: recorte al
contenido, giro de 25 grados (los iconos del vendor estan inclinados, y uno recto al lado canta),
y ajuste al 94 % de un lienzo CUADRADO. El cuadrado no es decoracion: el hueco del inventario lo
es, y sin rellenar el sprite Unity lo estira.

Salida RGBA de 512 px. El color es el del fotograma sobre negro DESCOMPUESTO por el alfa (sin eso
los bordes salen oscurecidos por el fondo que se mezclo en ellos).
"""
import sys
from pathlib import Path

from PIL import Image

CANVAS = 512
FILL = 0.94
TILT_DEGREES = 25.0
# Por debajo de esto el pixel se considera fondo puro y no entra en el recorte.
ALPHA_FLOOR = 8


def compose(black_path: Path, white_path: Path, out_path: Path) -> None:
    black = Image.open(black_path).convert("RGB")
    white = Image.open(white_path).convert("RGB")
    if black.size != white.size:
        raise SystemExit(f"los fotogramas no miden lo mismo: {black.size} vs {white.size}")

    w, h = black.size
    bp = black.load()
    wp = white.load()
    out = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    op = out.load()

    for y in range(h):
        for x in range(w):
            rb, gb, bb = bp[x, y]
            rw, gw, bw = wp[x, y]
            # alfa = 1 - (blanco - negro): un pixel de fondo puro difiere 255, uno opaco 0.
            diff = ((rw - rb) + (gw - gb) + (bw - bb)) / 3.0
            alpha = max(0.0, min(1.0, 1.0 - diff / 255.0))
            if alpha * 255 < ALPHA_FLOOR:
                continue
            # El fotograma sobre negro es color * alfa: se descompone para no oscurecer bordes.
            r = min(255, int(round(rb / alpha)))
            g = min(255, int(round(gb / alpha)))
            b = min(255, int(round(bb / alpha)))
            op[x, y] = (r, g, b, int(round(alpha * 255)))

    bbox = out.getbbox()
    if bbox is None:
        raise SystemExit("los dos fotogramas son identicos al fondo: no hay objeto que recortar")
    cropped = out.crop(bbox)

    tilted = cropped.rotate(TILT_DEGREES, resample=Image.BICUBIC, expand=True)
    tilted = tilted.crop(tilted.getbbox())

    side = int(CANVAS * FILL)
    scale = min(side / tilted.width, side / tilted.height)
    fitted = tilted.resize(
        (max(1, int(round(tilted.width * scale))), max(1, int(round(tilted.height * scale)))),
        Image.LANCZOS,
    )

    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    canvas.paste(fitted, ((CANVAS - fitted.width) // 2, (CANVAS - fitted.height) // 2), fitted)

    out_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(out_path, "PNG")
    print(f"{out_path}: {CANVAS}x{CANVAS}, objeto {fitted.width}x{fitted.height} px")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    compose(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
