using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Una boca que quedó sin pareja y hubo que sellar. REGLA L21: no se maximiza la
    /// conectividad, pero un socket sin usar NO se deja abierto — se tapona. Sin esto, "no usar
    /// todos los sockets" y "conectividad por construcción" (§13) se contradicen y el mundo
    /// acaba con agujeros al vacío.</summary>
    public struct Wg3Cap
    {
        public Vector2 point;
        public int side;
        public float width;
        public Wg3SocketType type;
        /// <summary>true si se selló por falta de candidata; false si fue decisión de composición.</summary>
        public bool forced;
    }

    /// <summary>
    /// Resultado de una composición. Datos planos a propósito: esto es exactamente lo que en F2
    /// tendrá que saber Rust (qué pieza, dónde, con qué giro), así que no puede depender de nada
    /// de escena.
    /// </summary>
    public sealed class Wg3World
    {
        public const byte SocketOpen = 0;
        public const byte SocketConnected = 1;
        public const byte SocketCapped = 2;

        public int worldSeed;
        public readonly List<Wg3Placement> placements = new List<Wg3Placement>();
        public readonly List<Wg3Cap> caps = new List<Wg3Cap>();

        /// <summary>Candidatas descartadas porque la huella pisaba algo ya colocado. No es un
        /// error: es la medida de cuánto aprieta el mundo. Un cero sostenido significa que el
        /// catálogo es demasiado pequeño para llenar el espacio.</summary>
        public int rejectedByOverlap;

        /// <summary>Candidatas descartadas por el validador (tipo, anchura, cota). Un número alto
        /// aquí delata un catálogo con bocas que no casan entre sí — falta una transición.</summary>
        public int rejectedByValidator;

        /// <summary>Bocas selladas por no haber ninguna candidata viable.</summary>
        public int forcedCaps;

        public Bounds FootprintBounds()
        {
            if (placements.Count == 0) return new Bounds();
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < placements.Count; i++)
            {
                Wg3Placement p = placements[i];
                if (p.originX < minX) minX = p.originX;
                if (p.originZ < minZ) minZ = p.originZ;
                if (p.MaxX > maxX) maxX = p.MaxX;
                if (p.MaxZ > maxZ) maxZ = p.MaxZ;
            }
            var b = new Bounds();
            b.SetMinMax(new Vector3(minX, 0f, minZ), new Vector3(maxX, 0f, maxZ));
            return b;
        }

        /// <summary>Huella de piezas por clase de escala. Es la métrica que dice si el campo de
        /// escala está haciendo algo o si el mundo salió homogéneo (L20).</summary>
        public int[] ScaleHistogram()
        {
            var h = new int[4];
            for (int i = 0; i < placements.Count; i++) h[(int)placements[i].piece.scale]++;
            return h;
        }

        public int DeadEndCount()
        {
            int n = 0;
            for (int i = 0; i < placements.Count; i++) if (placements[i].piece.isDeadEnd) n++;
            return n;
        }

        /// <summary>Firma estable del mundo. Para el test de determinismo: dos composiciones con
        /// la misma semilla tienen que dar la misma cadena, byte a byte.</summary>
        public string Signature()
        {
            var sb = new System.Text.StringBuilder(placements.Count * 24);
            for (int i = 0; i < placements.Count; i++)
            {
                Wg3Placement p = placements[i];
                sb.Append(p.piece.id).Append('|').Append(p.rotation).Append('|')
                  .Append(p.originX.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                  .Append(p.originZ.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
            }
            return sb.ToString();
        }
    }
}
