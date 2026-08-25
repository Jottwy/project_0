using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-093 — el hueco de una puerta del Level 4 enseña lo que hay al otro lado de su gemela.
    ///
    /// CÓMO. Una cámara colocada en la gemela con la MISMA transformación relativa que la del
    /// jugador respecto a esta puerta, renderizando a una render texture que el quad del hueco
    /// muestrea en coordenadas de pantalla (`Backrooms/Portal`). Como la imagen ya viene en el
    /// encuadre correcto, el quad sólo la recorta: te asomas y la vista se mueve contigo, que es
    /// lo que separa un portal de una televisión colgada.
    ///
    /// La matriz es `gemela.localToWorld * giro180 * esta.worldToLocal`. El giro de media vuelta
    /// NO es decoración: atravesar un portal te saca por su cara OPUESTA, así que sin él la cámara
    /// se planta DELANTE de la gemela mirándola de frente y el hueco enseña el marco de la otra
    /// puerta en vez de lo que hay al cruzarla. Escrita como matriz general sigue siendo correcta
    /// el día que una de las dos rote.
    ///
    /// POR QUÉ HACE FALTA FIJAR CHUNKS. Con `viewRadius` = 1 el cliente tiene 9 chunks alrededor
    /// del jugador y nada más; la gemela está a 10 km, así que sin
    /// <see cref="ChunkStreamer.SetPinned"/> la cámara renderizaría el vacío. Se fija
    /// al ACERCARSE (no al abrir) para que la geometría ya esté cuando la puerta se abra, y se
    /// suelta al alejarse.
    ///
    /// LÍMITE CONOCIDO Y ACEPTADO (decisión de Joel): el otro lado sale SIN CRIATURAS. El backend
    /// sólo transmite entidades cerca del jugador, así que el vestíbulo se ve vacío aunque tenga
    /// facelings dentro. Arreglarlo es transmisión de entidades a distancia — trabajo de red, no
    /// de render.
    /// </summary>
    [RequireComponent(typeof(Level4DoorTrigger))]
    public sealed class Level4Portal : MonoBehaviour
    {
        /// A qué distancia se empieza a fijar el otro lado. Más que el alcance de uso, para que
        /// los chunks lleguen ANTES de que el jugador pueda abrir la puerta.
        private const float PinRadiusM = 18f;
        /// Y a cuál se suelta. HISTÉRESIS, no el mismo número: cruzar cambia de golpe la distancia
        /// a las dos puertas, y con un solo umbral el conjunto fijado parpadeaba en cada salto —
        /// en el log del playtest se ve `chunks 9 → 0 → 9 → 18 → 9` por cruce, y ese 0 SUELTA el
        /// islote que se acababa de construir. Reconstruirlo es exactamente el "le cuesta cargar
        /// el chunk" que se sentía al llegar.
        private const float UnpinRadiusM = 34f;
        /// Radio en chunks del islote fijado al otro lado. 1 = 3×3, lo mismo que ve el jugador.
        private const int PinChunkRadius = 1;
        private const float ChunkSide = GridConstants.ChunkCells * GridConstants.CellSize;

        private Level4DoorTrigger _door;
        private Level4Portal _twin;

        private Camera _cam;
        private RenderTexture _rt;
        private Renderer _surface;
        private Material _mat;
        private bool _pinning;

        /// Todos los portales vivos. La cámara de CUALQUIERA de ellos tiene que renderizar sin
        /// ver ninguna superficie de portal, o un portal se ve a sí mismo a través del otro y la
        /// imagen se realimenta.
        private static readonly List<Level4Portal> _all = new List<Level4Portal>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _all.Clear();
            _repinDirty = false;
        }

        public void Bind(Level4Portal twin, float width, float height)
        {
            _twin = twin;
            if (_surface == null)
                BuildSurface(width, height);
        }

        private void Awake()
        {
            _door = GetComponent<Level4DoorTrigger>();
            _all.Add(this);
            RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            RenderPipelineManager.endCameraRendering += OnEndCamera;
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
            RenderPipelineManager.endCameraRendering -= OnEndCamera;
            _all.Remove(this);
            if (_rt != null)
                _rt.Release();
            if (_pinning)
                ChunkStreamer.UnpinAll();
        }

        private void BuildSurface(float width, float height)
        {
            // Resources.Load y no Shader.Find: `Find` sólo ve shaders que el build haya incluido
            // (por "Always Included" o por estar referenciados desde un material serializado), y
            // este no lo referencia ningún asset — se instancia entero por código. Bajo
            // Resources/ la carga es determinista en editor y en build por igual, que es el mismo
            // criterio con el que ChunkLootManager carga su ZoneLootTable.
            var shader = Resources.Load<Shader>("Shaders/BR_Portal");
            if (shader == null)
            {
                // Sin shader no hay portal, y un quad magenta en mitad del vano es peor que
                // ninguno. Se dice y se sigue: la puerta funciona igual sin la vista.
                Debug.LogWarning("[Level4Portal] shader 'Backrooms/Portal' no encontrado — " +
                                 "el hueco se queda sin vista, la puerta sigue funcionando");
                return;
            }

            _mat = new Material(shader);

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PortalSurface";
            quad.transform.SetParent(transform, false);
            // Centrado en el vano, muy pegado al plano de la puerta pero no EN él: exactamente en
            // el plano, el z-fighting con la hoja cerrada es visible desde ciertos ángulos.
            quad.transform.localPosition = new Vector3(0f, height * 0.5f, 0.01f);
            quad.transform.localScale = new Vector3(width, height, 1f);
            var col = quad.GetComponent<Collider>();
            if (col != null)
                Destroy(col);
            _surface = quad.GetComponent<Renderer>();
            _surface.sharedMaterial = _mat;
            _surface.enabled = false; // sólo se enciende con la puerta abierta

            var camGo = new GameObject($"PortalCam_{name}");
            camGo.transform.SetParent(transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.enabled = false; // se enciende sólo cuando hay algo que enseñar
            var data = camGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
        }

        private void LateUpdate()
        {
            var playerCam = Camera.main;
            if (playerCam == null || _twin == null)
                return;

            float dist = Vector3.Distance(playerCam.transform.position, transform.position);
            UpdatePinning(dist);
            ApplyPendingRepin();

            // SÓLO DESDE LA CARA FRONTAL. El quad lleva `Cull Off` para que el hueco no
            // desaparezca visto de canto, pero eso hacía que desde DETRÁS del marco enseñara la
            // misma vista que desde delante — y desde atrás lo que corresponde ver es el otro
            // lado, no el mismo. Un portal de una cara es además lo que hace Portal, y aquí
            // encaja: la cara frontal de cada puerta es por donde se llega a ella.
            bool inFront = transform.InverseTransformPoint(playerCam.transform.position).z >= 0f;
            bool show = _door != null && _door.IsOpen && inFront && dist <= PinRadiusM;
            if (_surface != null)
                _surface.enabled = show;
            if (_cam == null)
                return;
            _cam.enabled = show;
            if (!show)
                return;

            EnsureRenderTexture();
            // La RT conserva el último frame renderizado. Al volver a encender la cámara tras un
            // rato apagada, ese frame viejo es de otro sitio del mundo y asoma durante un frame
            // como un destello. Descartarlo cuesta nada y lo quita.
            _rt.DiscardContents();

            // LA CÁMARA VIRTUAL, con el giro de media vuelta que es TODO el asunto.
            //
            // Sin ese `Rotate(0,180,0)` la cámara aterriza DELANTE de la gemela mirándola de
            // frente, así que el portal enseñaba el marco de la otra puerta en vez de lo que hay
            // al cruzarla. Atravesar un portal te saca por su cara OPUESTA: la media vuelta es
            // exactamente eso, y es lo que pone la cámara detrás de la gemela mirando hacia
            // fuera. Es la misma composición que usa Portal.
            Matrix4x4 flip = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f));
            Matrix4x4 m = _twin.transform.localToWorldMatrix * flip * transform.worldToLocalMatrix;
            _cam.transform.SetPositionAndRotation(
                m.MultiplyPoint(playerCam.transform.position),
                m.rotation * playerCam.transform.rotation);

            // Proyección HEREDADA del jugador y no un `fieldOfView` propio: así el encuadre
            // coincide exactamente con el suyo (FOV y aspecto), que es lo que el muestreo en
            // coordenadas de pantalla da por hecho. Con proyección propia la vista sale
            // desalineada del hueco por mucho que la pose sea correcta.
            _cam.projectionMatrix = playerCam.projectionMatrix;

            // RECORTE OBLICUO: el plano cercano se dobla para que coincida con el plano de la
            // gemela. Sin esto la cámara está literalmente dentro de la pared que hay detrás de
            // la otra puerta y renderiza su interior — se ve el reverso de la geometría flotando
            // delante de lo que se quería enseñar. Es el otro requisito clásico de un portal, y
            // el que hace que la vista empiece EXACTAMENTE en el vano.
            Vector4 clip = CameraSpacePlane(_cam, _twin.transform.position, _twin.transform.forward);
            _cam.projectionMatrix = _cam.CalculateObliqueMatrix(clip);

            // El plano lejano tiene que cubrir el islote fijado.
            _cam.farClipPlane = Mathf.Max(playerCam.farClipPlane, ChunkSide * (PinChunkRadius + 2));
        }

        /// El plano de la gemela expresado en espacio de cámara, como lo quiere
        /// <see cref="Camera.CalculateObliqueMatrix"/>. Patrón estándar de espejos/portales.
        private static Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal)
        {
            Matrix4x4 w2c = cam.worldToCameraMatrix;
            Vector3 cpos = w2c.MultiplyPoint(pos);
            Vector3 cnormal = w2c.MultiplyVector(normal).normalized;
            return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
        }

        /// La render texture tiene que llevar el ASPECTO DE LA PANTALLA, no ser cuadrada: el
        /// shader la muestrea en coordenadas de pantalla, así que una textura 1:1 en un monitor
        /// 16:9 sale estirada. A media resolución, que es una ventana de 1,6 × 2,4 m.
        private void EnsureRenderTexture()
        {
            int w = Mathf.Max(256, Screen.width / 2);
            int h = Mathf.Max(144, Screen.height / 2);
            if (_rt != null && _rt.width == w && _rt.height == h)
                return;
            if (_rt != null)
                _rt.Release();
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.Default)
            {
                name = $"Level4PortalRT_{name}",
            };
            _rt.Create();
            if (_cam != null)
                _cam.targetTexture = _rt;
            if (_mat != null)
                _mat.SetTexture("_MainTex", _rt);
        }

        /// Fija (o suelta) el islote de chunks alrededor de la gemela. El conjunto se construye
        /// entre TODOS los portales que lo pidan, porque `SetPinned` reemplaza — si cada uno
        /// llamara con lo suyo, el segundo borraría lo del primero.
        ///
        /// Con histéresis y COALESCIDO a una sola aplicación por frame: los dos portales cambian
        /// de estado en el mismo salto pero en llamadas distintas, así que aplicar al vuelo
        /// publicaba el estado intermedio (un portal ya soltó, el otro aún no fijó) y el streamer
        /// destruía chunks para reconstruirlos acto seguido.
        private void UpdatePinning(float dist)
        {
            bool wanted = _pinning ? dist <= UnpinRadiusM : dist <= PinRadiusM;
            if (wanted == _pinning)
                return;
            _pinning = wanted;
            _repinDirty = true;
        }

        private static bool _repinDirty;

        /// Aplica el conjunto fijado UNA vez por frame, después de que todos los portales hayan
        /// decidido. Lo hace el primero de la lista para que haya exactamente un responsable.
        private void ApplyPendingRepin()
        {
            if (!_repinDirty || _all.Count == 0 || _all[0] != this)
                return;
            _repinDirty = false;
            RepinAll();
        }

        private static void RepinAll()
        {
            var keys = new List<(int, int, int)>();
            foreach (var p in _all)
            {
                if (!p._pinning || p._twin == null)
                    continue;
                Vector3 at = p._twin.transform.position;
                int cx = Mathf.FloorToInt(at.x / ChunkSide);
                int cz = Mathf.FloorToInt(at.z / ChunkSide);
                int layer = Mathf.Clamp(
                    Mathf.FloorToInt(at.y / GridConstants.LayerHeight), 0, 3);
                for (int dz = -PinChunkRadius; dz <= PinChunkRadius; dz++)
                    for (int dx = -PinChunkRadius; dx <= PinChunkRadius; dx++)
                        keys.Add((cx + dx, cz + dz, layer));
            }
            ChunkStreamer.SetPinned(keys);
            Debug.Log($"[Level4Portal] MPTRACE step=L4 event=portal_pin chunks={keys.Count}");
        }

        // Lo que una cámara de portal NO puede ver, y son dos cosas distintas:
        //
        //  1. LA PUERTA GEMELA ENTERA — marco, hoja y umbral. La cámara está justo detrás de ella
        //     mirando hacia fuera, así que su marco le tapa el centro del encuadre: era
        //     literalmente "el portal enseña la otra puerta" en vez de lo que hay al cruzarla.
        //     En Portal el portal de destino tampoco se dibuja desde su propia cámara.
        //  2. CUALQUIER superficie de portal, incluida la suya — si no, un portal se ve a sí mismo
        //     a través del otro y la imagen se realimenta hasta el infinito.
        //
        // Se apaga por renderers y no por capas porque una layer es un recurso escaso y global del
        // proyecto, y esto necesita exactamente dos objetos apagados durante exactamente un
        // render. Se guarda lo que había para restaurarlo: dar por hecho "estaba encendido" deja
        // la puerta invisible en la vista normal el frame que alguien la apague por otro motivo.
        private static readonly List<Renderer> _hidden = new List<Renderer>();

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_cam == null || cam != _cam)
                return;

            _hidden.Clear();
            // La gemela al completo: es la que está delante de esta cámara.
            if (_twin != null)
                foreach (var r in _twin.GetComponentsInChildren<Renderer>(includeInactive: false))
                    if (r.enabled)
                    {
                        r.enabled = false;
                        _hidden.Add(r);
                    }
            // Y las superficies de portal que queden encendidas, la propia incluida.
            foreach (var p in _all)
                if (p._surface != null && p._surface.enabled)
                {
                    p._surface.enabled = false;
                    _hidden.Add(p._surface);
                }
        }

        private void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
        {
            if (_cam == null || cam != _cam)
                return;
            foreach (var r in _hidden)
                if (r != null)
                    r.enabled = true;
            _hidden.Clear();
        }
    }
}
