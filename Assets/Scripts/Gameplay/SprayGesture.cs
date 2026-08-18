using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-068 S3 — el gesto en curso: los puntos que el jugador va marcando en la pared,
    /// guardados en ESPACIO DE MUNDO hasta el momento de mandarlos.
    ///
    /// POR QUÉ EN MUNDO Y NO EN LIENZO, que es el cambio que arregla lo que se veía mal. La
    /// primera versión fijaba el lienzo con el PRIMER impacto y proyectaba cada muestra contra
    /// él: como el lienzo mide poco más de un metro, en cuanto el gesto se salía todo se
    /// clampeaba al canto y el dibujo se apelmazaba en un borde, desplazado de donde apuntaba
    /// la mira. Guardando en mundo, el lienzo se AJUSTA al final a lo que de verdad se pintó
    /// (<see cref="TryFit"/>), así que el trazo cae exactamente donde el jugador lo hizo.
    ///
    /// El giro sí se fija con el primer impacto y no se toca: una pintada vive en UNA pared.
    /// </summary>
    public class SprayGesture
    {
        /// <summary>Margen alrededor del trazo para que la boquilla no se corte en el canto.</summary>
        private const float PaddingMeters = 0.06f;

        private readonly List<List<Vector3>> _strokes = new List<List<Vector3>>();
        private List<Vector3> _current;

        /// <summary>Giro de la pared, fijado con el primer impacto.</summary>
        public float Yaw { get; private set; }
        public bool HasYaw { get; private set; }

        public int StrokeCount => _strokes.Count;
        public int PointCount { get; private set; }
        public bool IsEmpty => PointCount == 0;
        public bool StrokeOpen => _current != null;

        public void SetWall(float yaw)
        {
            if (HasYaw) return;
            Yaw = yaw;
            HasYaw = true;
        }

        public void BeginStroke()
        {
            if (_current != null) return;
            _current = new List<Vector3>();
        }

        public void Add(Vector3 world)
        {
            if (_current == null) BeginStroke();
            _current.Add(world);
            PointCount++;
        }

        /// <summary>Último punto del trazo abierto, para medir cuánto se ha movido la mira.</summary>
        public bool TryGetLast(out Vector3 last)
        {
            if (_current != null && _current.Count > 0)
            {
                last = _current[_current.Count - 1];
                return true;
            }
            last = default;
            return false;
        }

        public void EndStroke()
        {
            if (_current == null) return;
            // Un trazo de un solo punto SÍ vale: es un toque, y un toque debe dejar marca.
            if (_current.Count > 0) _strokes.Add(_current);
            _current = null;
        }

        public void Clear()
        {
            _strokes.Clear();
            _current = null;
            PointCount = 0;
            HasYaw = false;
            Yaw = 0f;
        }

        /// <summary>
        /// Cuánto ocuparía el lienzo si se añadiera <paramref name="world"/>, en metros, sin
        /// modificar nada. Sirve para cerrar la pintada ANTES de que el gesto se salga del tope
        /// de 2 m, en vez de clampearlo y apelmazarlo contra el borde.
        /// </summary>
        public bool WouldExceedCanvas(Vector3 world)
        {
            if (IsEmpty) return false;
            Bounds2(world, out float su, out float sv);
            return su > SprayCanvas.MaxCanvasMeters || sv > SprayCanvas.MaxCanvasMeters;
        }

        /// <summary>
        /// El lienzo que encierra lo pintado: centro en mundo y lados en metros, ya acotados a
        /// lo que el host acepta. Devuelve false si no hay nada que encerrar.
        /// </summary>
        public bool TryFit(out Vector3 centre, out float sizeX, out float sizeY)
        {
            centre = default; sizeX = 0f; sizeY = 0f;
            if (IsEmpty) return false;

            Vector3 origin = FirstPoint();
            Vector3 right = SprayCanvas.RightOf(Yaw);
            MinMax(out float minU, out float maxU, out float minV, out float maxV, origin, right);

            sizeX = Mathf.Clamp((maxU - minU) + PaddingMeters * 2f,
                SprayCanvas.MinCanvasMeters, SprayCanvas.MaxCanvasMeters);
            sizeY = Mathf.Clamp((maxV - minV) + PaddingMeters * 2f,
                SprayCanvas.MinCanvasMeters, SprayCanvas.MaxCanvasMeters);

            centre = origin
                     + right * ((minU + maxU) * 0.5f)
                     + Vector3.up * ((minV + maxV) * 0.5f);
            return true;
        }

        /// <summary>
        /// Proyecta un trazo al blob de bytes del wire (X e Y intercalados) contra el lienzo
        /// dado. Incluye el trazo abierto cuando <paramref name="index"/> es el último.
        /// </summary>
        public byte[] ProjectStroke(int index, Vector3 centre, float sizeX, float sizeY)
        {
            var stroke = StrokeAt(index);
            if (stroke == null || stroke.Count == 0) return System.Array.Empty<byte>();

            var bytes = new byte[stroke.Count * 2];
            for (int i = 0; i < stroke.Count; i++)
            {
                SprayCanvas.WorldToCanvas(stroke[i], centre, Yaw, sizeX, sizeY, out byte u, out byte v);
                bytes[i * 2] = u;
                bytes[i * 2 + 1] = v;
            }
            return bytes;
        }

        /// <summary>Trazos cerrados MÁS el abierto, que es lo que hay que dibujar en la previa.</summary>
        public int TotalStrokesIncludingOpen => _strokes.Count + (_current != null && _current.Count > 0 ? 1 : 0);

        /// <summary>
        /// El gesto convertido a lo que viaja por el wire, con el lienzo AJUSTADO a lo pintado.
        ///
        /// Vive AQUÍ y no en <c>SprayPainter</c> desde ADR-078 porque ahora tiene dos usuarios:
        /// quien pinta (previa local + envío) y quien MIRA pintar a otro, que reconstruye el
        /// gesto ajeno y lo dibuja por este mismo camino. Dos copias de esta aritmética
        /// divergirían, y la divergencia se vería como un trazo que salta al soltar.
        /// </summary>
        public SprayMsg BuildMessage(byte color, byte width, byte layer)
        {
            if (!TryFit(out var centre, out float sizeX, out float sizeY)) return null;

            int total = TotalStrokesIncludingOpen;
            var strokes = new List<SprayStrokeMsg>(total);
            for (int i = 0; i < total; i++)
            {
                var points = ProjectStroke(i, centre, sizeX, sizeY);
                if (points.Length < 4) continue; // menos de dos puntos no es una línea
                strokes.Add(new SprayStrokeMsg { color = color, width = width, points = points });
            }
            if (strokes.Count == 0) return null;

            int cx = Mathf.FloorToInt(centre.x / SprayMsg.ChunkSize);
            int cz = Mathf.FloorToInt(centre.z / SprayMsg.ChunkSize);
            return new SprayMsg
            {
                id = 0,
                cx = cx,
                cz = cz,
                layer = layer,
                lx = centre.x - cx * SprayMsg.ChunkSize,
                ly = centre.y,
                lz = centre.z - cz * SprayMsg.ChunkSize,
                yaw = Yaw,
                sizeX = sizeX,
                sizeY = sizeY,
                strokes = strokes.ToArray(),
            };
        }

        /// <summary>
        /// ADR-078 — el ancla del gesto: el PRIMER punto, que es el origen del plano contra el
        /// que se miden los milímetros que viajan. Público porque el borrador lo manda y el
        /// receptor lo necesita para deshacer la cuenta.
        /// </summary>
        public bool TryGetAnchor(out Vector3 anchor)
        {
            anchor = FirstPoint();
            return !IsEmpty;
        }

        /// <summary>
        /// Vuelca a milímetros los puntos del trazo abierto desde <paramref name="from"/>, contra
        /// el plano del ancla. Pares (u, v) `i16` little-endian: 4 bytes por punto, el mismo
        /// formato binario que ya usan los puntos de un trazo.
        ///
        /// Devuelve null si no hay nada nuevo que mandar, para que el llamante no gaste un
        /// paquete en decir "sigo aquí".
        /// </summary>
        public byte[] OpenStrokeToMillimetres(int from, int maxPoints, Vector3 anchor)
        {
            if (_current == null || from < 0 || from >= _current.Count) return null;

            int count = Mathf.Min(_current.Count - from, maxPoints);
            if (count <= 0) return null;

            Vector3 right = SprayCanvas.RightOf(Yaw);
            var bytes = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                Vector3 d = _current[from + i] - anchor;
                // Clamp explícito: a ±32,7 m no se llega pintando (el lienzo son 1,2 m), pero un
                // desbordamiento silencioso dibujaría el punto en la esquina opuesta.
                short u = (short)Mathf.Clamp(Mathf.RoundToInt(Vector3.Dot(d, right) * 1000f),
                    short.MinValue, short.MaxValue);
                short v = (short)Mathf.Clamp(Mathf.RoundToInt(d.y * 1000f),
                    short.MinValue, short.MaxValue);
                bytes[i * 4] = (byte)(u & 0xFF);
                bytes[i * 4 + 1] = (byte)((u >> 8) & 0xFF);
                bytes[i * 4 + 2] = (byte)(v & 0xFF);
                bytes[i * 4 + 3] = (byte)((v >> 8) & 0xFF);
            }
            return bytes;
        }

        /// <summary>
        /// El camino inverso: mete en el gesto un punto que llegó en milímetros. Lo usa quien
        /// MIRA pintar — reconstruye el trazo ajeno en mundo para dibujarlo por el mismo camino
        /// que la previa propia.
        /// </summary>
        public void AddFromMillimetres(Vector3 anchor, short u, short v)
        {
            Vector3 right = SprayCanvas.RightOf(Yaw);
            Add(anchor + right * (u / 1000f) + Vector3.up * (v / 1000f));
        }

        private List<Vector3> StrokeAt(int index)
        {
            if (index < _strokes.Count) return _strokes[index];
            if (index == _strokes.Count && _current != null) return _current;
            return null;
        }

        private Vector3 FirstPoint()
        {
            if (_strokes.Count > 0 && _strokes[0].Count > 0) return _strokes[0][0];
            return _current != null && _current.Count > 0 ? _current[0] : Vector3.zero;
        }

        private void MinMax(out float minU, out float maxU, out float minV, out float maxV,
            Vector3 origin, Vector3 right)
        {
            minU = float.MaxValue; maxU = float.MinValue;
            minV = float.MaxValue; maxV = float.MinValue;

            for (int s = 0; s < TotalStrokesIncludingOpen; s++)
            {
                var stroke = StrokeAt(s);
                if (stroke == null) continue;
                for (int i = 0; i < stroke.Count; i++)
                {
                    Vector3 d = stroke[i] - origin;
                    float u = Vector3.Dot(d, right);
                    float v = d.y;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
            }
        }

        private void Bounds2(Vector3 extra, out float spanU, out float spanV)
        {
            Vector3 origin = FirstPoint();
            Vector3 right = SprayCanvas.RightOf(Yaw);
            MinMax(out float minU, out float maxU, out float minV, out float maxV, origin, right);

            Vector3 d = extra - origin;
            float eu = Vector3.Dot(d, right);
            float ev = d.y;
            minU = Mathf.Min(minU, eu); maxU = Mathf.Max(maxU, eu);
            minV = Mathf.Min(minV, ev); maxV = Mathf.Max(maxV, ev);

            spanU = (maxU - minU) + PaddingMeters * 2f;
            spanV = (maxV - minV) + PaddingMeters * 2f;
        }
    }
}
