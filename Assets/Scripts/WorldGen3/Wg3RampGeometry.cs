using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-122 — la geometría de una RAMPA en el cliente: sus cajas de colisión y su cuña de dibujo.
    /// </summary>
    /// <remarks>
    /// # Las cajas son las del servidor, al centímetro
    ///
    /// <see cref="StepBoxes"/> es el espejo de <c>ramp::ramp_step_boxes</c> (Rust) y lo ata el oráculo
    /// <c>wg3_ramp_oracle.json</c>: una caja por celda de 50 cm desde el extremo bajo, con toda la
    /// anchura y la cota del borde ALTO de la celda redondeada hacia arriba. Es lo que frena a los dos
    /// lados (ADR-122 D2). Con la pendiente tope de 1:5 cada caja sube ≤ 10 cm, por debajo del
    /// <c>stepOffset</c> del jugador.
    ///
    /// # La cuña sólo se ve
    ///
    /// <see cref="WedgeVolume"/> es un volumen de SUELO con forma <see cref="Wg3Shape.Wedge"/>: va a la
    /// submalla de suelo (la moqueta continúa desde la tira de la puerta) y <c>AddColliders</c> la
    /// ignora por no ser caja. Se levanta <see cref="WedgeLiftM"/> para que ninguna arista de las tiras
    /// de debajo asome. Como las cajas van al borde alto de su celda, un pie puede quedar hasta 10 cm
    /// por encima del dibujo: es el precio aceptado de colisionar con cajas y no con un plano.
    /// </remarks>
    public static class Wg3RampGeometry
    {
        public const int CellCm = 50;
        public const int MaxRisePerCellCm = 10;
        public const int MaxRiseCm = 100;
        public const float WedgeLiftM = 0.01f;

        /// <summary>Una caja de colisión, en centímetros enteros como la de Rust.</summary>
        public struct StepBox
        {
            public int xCm, zCm, sizeXCm, sizeZCm, bottomYCm, topYCm;
        }

        public static int AlongCm(in Wg3RampMsg r) => r.dir % 2 == 0 ? r.sizeZCm : r.sizeXCm;
        public static int AcrossCm(in Wg3RampMsg r) => r.dir % 2 == 0 ? r.sizeXCm : r.sizeZCm;
        public static int RiseCm(in Wg3RampMsg r) => r.topYCm - r.bottomYCm;

        /// <summary>Lo mínimo para montarla. El servidor ya filtra por pendiente y tope; aquí sólo se
        /// evita construir algo degenerado si llega basura.</summary>
        public static bool IsValid(in Wg3RampMsg r) =>
            r.sizeXCm > 0 && r.sizeZCm > 0 && r.dir < 4 && RiseCm(r) > 0;

        /// <summary>Espejo exacto de <c>ramp::ramp_step_boxes</c>.</summary>
        public static List<StepBox> StepBoxes(in Wg3RampMsg r)
        {
            var boxes = new List<StepBox>();
            int along = AlongCm(r);
            int rise = RiseCm(r);
            if (along <= 0 || rise <= 0 || r.sizeXCm <= 0 || r.sizeZCm <= 0) return boxes;

            int cells = (along + CellCm - 1) / CellCm;
            for (int k = 0; k < cells; k++)
            {
                int a0 = k * CellCm;
                int a1 = Mathf.Min((k + 1) * CellCm, along);
                int top = r.bottomYCm + (rise * a1 + along - 1) / along;
                var b = new StepBox { bottomYCm = r.bottomYCm, topYCm = top };
                switch (r.dir % 4)
                {
                    case 0: b.xCm = r.xCm; b.zCm = r.zCm + a0; b.sizeXCm = r.sizeXCm; b.sizeZCm = a1 - a0; break;
                    case 1: b.xCm = r.xCm + a0; b.zCm = r.zCm; b.sizeXCm = a1 - a0; b.sizeZCm = r.sizeZCm; break;
                    case 2: b.xCm = r.xCm; b.zCm = r.zCm + r.sizeZCm - a1; b.sizeXCm = r.sizeXCm; b.sizeZCm = a1 - a0; break;
                    default: b.xCm = r.xCm + r.sizeXCm - a1; b.zCm = r.zCm; b.sizeXCm = a1 - a0; b.sizeZCm = r.sizeZCm; break;
                }
                boxes.Add(b);
            }
            return boxes;
        }

        /// <summary>Las cajas como volúmenes de peldaño, centro en MUNDO (como toda lista de
        /// volúmenes: <c>AddColliders</c> resta el origen una vez).</summary>
        public static List<Wg3Volume> StepVolumes(in Wg3RampMsg r)
        {
            List<StepBox> boxes = StepBoxes(r);
            var volumes = new List<Wg3Volume>(boxes.Count);
            foreach (StepBox b in boxes)
            {
                float sx = b.sizeXCm / 100f, sz = b.sizeZCm / 100f, sy = (b.topYCm - b.bottomYCm) / 100f;
                volumes.Add(new Wg3Volume
                {
                    center = new Vector3(b.xCm / 100f + sx * 0.5f, b.bottomYCm / 100f + sy * 0.5f, b.zCm / 100f + sz * 0.5f),
                    size = new Vector3(sx, sy, sz),
                    yawDegrees = 0f,
                    kind = Wg3VolumeKind.Step,
                    shape = Wg3Shape.Box,
                });
            }
            return volumes;
        }

        /// <summary>
        /// La cuña de dibujo, centro en MUNDO. En su espacio local sube hacia +Z (<c>dir</c> 0) y el
        /// giro la orienta: N 0°, E 90°, S 180°, O 270°, horario visto desde arriba como todo
        /// <c>yawDegrees</c>. <c>size</c> va en local: x el ancho, y el desnivel, z el largo.
        /// </summary>
        public static Wg3Volume WedgeVolume(in Wg3RampMsg r)
        {
            float sx = r.sizeXCm / 100f, sz = r.sizeZCm / 100f, rise = RiseCm(r) / 100f;
            return new Wg3Volume
            {
                center = new Vector3(r.xCm / 100f + sx * 0.5f, r.bottomYCm / 100f + rise * 0.5f + WedgeLiftM,
                    r.zCm / 100f + sz * 0.5f),
                size = new Vector3(AcrossCm(r) / 100f, rise, AlongCm(r) / 100f),
                yawDegrees = (r.dir % 4) * 90f,
                kind = Wg3VolumeKind.Floor,
                shape = Wg3Shape.Wedge,
            };
        }
    }
}
