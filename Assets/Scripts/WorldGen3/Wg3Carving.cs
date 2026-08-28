using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-101 — LA RESTA: quitar de la geometría los vanos que el servidor excavó.
    ///
    /// <para>
    /// El plan decide dónde va cada puerta; una pieza del catálogo trae sus bocas donde las puso
    /// quien la dibujó, y los dos sitios no coinciden casi nunca. El servidor lo resuelve
    /// <b>excavando</b> —le quita materia al ráster ya estampado— y manda las cajas por el cable. Este
    /// fichero hace la MISMA operación sobre los volúmenes antes de que se conviertan en malla y en
    /// colliders.
    /// </para>
    ///
    /// <para>
    /// <b>No es CSG.</b> Todo lo que WG3 dibuja son cajas alineadas a los ejes, y la diferencia de dos
    /// AABB son AABB: hasta seis, y a menudo dos. Un volumen que el vano no toca sale intacto y sin
    /// copiarse.
    /// </para>
    ///
    /// <para>
    /// <b>Se aplica a las piezas Y a los tramos, y no es opcional.</b> El servidor excava el ráster ya
    /// estampado, o sea todo lo que haya dentro de esa caja. Restringirlo aquí a las piezas sería una
    /// divergencia deliberada entre lo que se ve y lo que frena — exactamente el fallo que la regla R6
    /// existe para impedir, y el que no se ve en una captura.
    /// </para>
    /// </summary>
    public static class Wg3Carving
    {
        /// <summary>
        /// Tolerancia bajo la cual un giro se considera múltiplo de 90° y el volumen puede tratarse
        /// como alineado a los ejes del mundo. Mismo criterio que <c>Wg3SceneAssembler</c>.
        /// </summary>
        private const float YawEpsilon = 0.01f;

        /// <summary>Trozos por debajo de este lado se tiran: son astillas de redondeo, no geometría,
        /// y cada una cuesta un BoxCollider y doce triángulos.</summary>
        private const float MinSliver = 0.005f;

        /// <summary>
        /// Los volúmenes con los vanos ya restados.
        ///
        /// Devuelve la MISMA lista si no hay nada que restar. Es el caso mayoritario —la inmensa
        /// mayoría de chunks no lleva un solo vano— y no merece una copia.
        /// </summary>
        public static List<Wg3Volume> Apply(List<Wg3Volume> volumes, List<Wg3CarveMsg> carves)
        {
            if (volumes == null || carves == null || carves.Count == 0) return volumes;

            var boxes = new List<Bounds>(carves.Count);
            for (int i = 0; i < carves.Count; i++)
            {
                Wg3CarveMsg c = carves[i];
                if (c.sizeXCm <= 0 || c.sizeZCm <= 0 || c.topYCm <= c.bottomYCm) continue;
                var min = new Vector3(c.xCm * 0.01f, c.bottomYCm * 0.01f, c.zCm * 0.01f);
                var max = new Vector3(
                    (c.xCm + c.sizeXCm) * 0.01f,
                    c.topYCm * 0.01f,
                    (c.zCm + c.sizeZCm) * 0.01f);
                var b = new Bounds();
                b.SetMinMax(min, max);
                boxes.Add(b);
            }
            if (boxes.Count == 0) return volumes;

            var current = new List<Wg3Volume>(volumes);
            var next = new List<Wg3Volume>(volumes.Count);

            for (int k = 0; k < boxes.Count; k++)
            {
                next.Clear();
                for (int v = 0; v < current.Count; v++)
                    Subtract(current[v], boxes[k], next);

                // Se intercambian las listas en vez de reasignar: con varios vanos por chunk esto
                // corre por cada pared de cada pieza, y una asignación por vuelta se nota.
                (current, next) = (next, current);
            }
            return current;
        }

        /// <summary>
        /// Resta una caja de un volumen y escribe en <paramref name="output"/> lo que queda.
        ///
        /// <para>
        /// Un volumen con giro propio que NO sea múltiplo de 90° se deja entero. Hoy no existe ninguno
        /// donde caiga un vano —los vanos se abren en paredes del plan, que son ortogonales— y tallar
        /// una caja girada exige el CSG que ADR-101 evita. Queda dicho para que se note el día que
        /// exista, en vez de salir como una pared que no se puede atravesar.
        /// </para>
        /// </summary>
        private static void Subtract(Wg3Volume vol, Bounds carve, List<Wg3Volume> output)
        {
            float yaw = Mathf.Repeat(vol.yawDegrees, 90f);
            bool axisAligned = yaw < YawEpsilon || yaw > 90f - YawEpsilon;
            if (!axisAligned)
            {
                output.Add(vol);
                return;
            }

            // Un giro de 90° o 270° intercambia los lados vistos desde el mundo. Es la misma
            // corrección que hace `Wg3SceneAssembler.AddColliders`, y omitirla dejaría paredes de
            // grosor equivocado: el fallo que no revienta nada y deja gente encajada.
            bool swapped = Mathf.Repeat(vol.yawDegrees, 180f) > 45f;
            Vector3 size = swapped ? new Vector3(vol.size.z, vol.size.y, vol.size.x) : vol.size;

            Vector3 min = vol.center - size * 0.5f;
            Vector3 max = vol.center + size * 0.5f;
            Vector3 cmin = carve.min;
            Vector3 cmax = carve.max;

            bool touches = min.x < cmax.x && max.x > cmin.x
                        && min.y < cmax.y && max.y > cmin.y
                        && min.z < cmax.z && max.z > cmin.z;
            if (!touches)
            {
                output.Add(vol);
                return;
            }

            // Las seis lonchas, en orden: primero lo que queda por debajo y por encima del vano,
            // luego lo de los lados dentro de esa banda, y por último lo de delante y detrás dentro
            // de esa columna. Cada corte reduce la caja que se sigue partiendo, así que ninguna
            // loncha se solapa con otra — que es lo que haría un muro de doble grosor.
            if (min.y < cmin.y) Emit(vol, min, new Vector3(max.x, cmin.y, max.z), output);
            if (max.y > cmax.y) Emit(vol, new Vector3(min.x, cmax.y, min.z), max, output);

            float y0 = Mathf.Max(min.y, cmin.y);
            float y1 = Mathf.Min(max.y, cmax.y);

            if (min.x < cmin.x) Emit(vol, new Vector3(min.x, y0, min.z), new Vector3(cmin.x, y1, max.z), output);
            if (max.x > cmax.x) Emit(vol, new Vector3(cmax.x, y0, min.z), new Vector3(max.x, y1, max.z), output);

            float x0 = Mathf.Max(min.x, cmin.x);
            float x1 = Mathf.Min(max.x, cmax.x);

            if (min.z < cmin.z) Emit(vol, new Vector3(x0, y0, min.z), new Vector3(x1, y1, cmin.z), output);
            if (max.z > cmax.z) Emit(vol, new Vector3(x0, y0, cmax.z), new Vector3(x1, y1, max.z), output);
        }

        /// <summary>
        /// Una loncha superviviente, ya en ejes de mundo.
        ///
        /// Sale con <c>yawDegrees = 0</c> a propósito: el resultado de partir una caja alineada es una
        /// caja alineada, y arrastrar el giro original obligaría a volver a intercambiar los lados en
        /// cada consumidor. El <c>kind</c> SÍ se conserva — es lo que decide el material y si la
        /// loncha colisiona.
        /// </summary>
        private static void Emit(Wg3Volume source, Vector3 min, Vector3 max, List<Wg3Volume> output)
        {
            Vector3 size = max - min;
            if (size.x < MinSliver || size.y < MinSliver || size.z < MinSliver) return;

            output.Add(new Wg3Volume
            {
                center = min + size * 0.5f,
                size = size,
                yawDegrees = 0f,
                kind = source.kind
            });
        }
    }
}
