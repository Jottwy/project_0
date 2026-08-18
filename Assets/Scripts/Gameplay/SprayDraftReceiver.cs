using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-078 (fase B de ADR-068) — dibuja el trazo que OTRO jugador está pintando ahora mismo.
    ///
    /// Hasta aquí una pintada ajena aparecía de golpe y terminada, más de un segundo después de
    /// que el otro empezara. Esto la enseña creciendo.
    ///
    /// CÓMO, en una línea: los borradores llegan con puntos en milímetros sobre un plano, se
    /// vuelven a montar en un <see cref="SprayGesture"/> igual que si los estuviera pintando uno
    /// mismo, y se dibujan por el MISMO camino que la previa local. Reusar el gesto no es
    /// elegancia: es lo que garantiza que lo que ve el que mira y lo que ve el que pinta salgan
    /// de la misma aritmética, y que no puedan divergir.
    ///
    /// TRES REGLAS DE VIDA, y las tres existen para que no queden trazos colgados:
    /// 1. Al llegar la pintada AUTORITATIVA con ese `place_id`, el borrador se retira en el acto.
    ///    Si no, quedarían las dos y se vería doble trazo.
    /// 2. Sin noticias durante <see cref="ExpirySeconds"/>, fuera: el pintor puede haberse
    ///    desconectado a mitad de trazo, y nadie va a mandar el final.
    /// 3. Tope de borradores vivos (ADR-078 decisión 6). Al llenarse cae el más viejo, que es el
    ///    que más cerca está de caducar igualmente.
    ///
    /// Nada de esto entra en el almacén ni se guarda: es presentación efímera, exactamente como
    /// la previa local.
    /// </summary>
    public sealed class SprayDraftReceiver : MonoBehaviour
    {
        /// <summary>Sin un trozo nuevo en este tiempo, el trazo se da por muerto.</summary>
        public const float ExpirySeconds = 3f;

        /// <summary>Trazos ajenos dibujándose a la vez. ADR-078 decisión 6.</summary>
        public const int MaxLiveDrafts = 8;

        private sealed class Draft
        {
            public readonly SprayGesture Gesture = new SprayGesture();
            public Vector3 Anchor;
            public byte Color;
            public byte Width;
            public byte Layer;
            public int NextIndex;
            public float LastSeen;
        }

        private static SprayDraftReceiver _instance;

        /// <summary>
        /// Se levanta solo, igual que <see cref="SprayPainter"/> y <see cref="SprayRenderer"/>:
        /// nadie tiene que acordarse de ponerlo en una escena, y así no puede faltar en una y
        /// estar en otra.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("SprayDraftReceiver");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SprayDraftReceiver>();
        }

        private readonly Dictionary<long, Draft> _drafts = new Dictionary<long, Draft>();
        private IPCClient _client;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void OnDestroy()
        {
            Unhook();
            if (_instance == this) _instance = null;
        }

        private void OnDisable() => Unhook();

        private void Update()
        {
            TryHook();
            ExpireStale();
        }

        /// <summary>
        /// Engancha al IPC cuando aparece, y se REENGANCHA si el cliente cambia — la sesión se
        /// puede reconstruir por debajo (ADR-056), y un suscriptor pegado al cliente viejo deja
        /// de recibir sin que nada lo diga.
        /// </summary>
        private void TryHook()
        {
            var client = IPCClient.Instance;
            if (client == null || client == _client) return;
            Unhook();
            _client = client;
            _client.AddSprayDraftListener(OnDraft);
            _client.AddSprayListener(OnSprayPlaced);
        }

        private void Unhook()
        {
            if (_client == null) return;
            _client.RemoveSprayDraftListener(OnDraft);
            _client.RemoveSprayListener(OnSprayPlaced);
            _client = null;
        }

        /// <summary>
        /// Llegó la pintada de verdad: fuera el borrador. Se hace por `place_id` y no por pintor
        /// porque es el id lo que empareja las dos cosas — un mismo jugador puede tener un trazo
        /// cerrándose y otro empezando.
        /// </summary>
        private void OnSprayPlaced(SprayMsg spray)
        {
            if (spray == null) return;
            Retire(spray.id);
        }

        private void OnDraft(SprayDraftMsg msg)
        {
            if (msg == null || msg.PointCount == 0) return;

            var renderer = SprayRenderer.Instance;
            if (renderer == null) return;

            if (!_drafts.TryGetValue(msg.placeId, out var draft))
            {
                EvictIfFull();
                draft = new Draft();
                _drafts[msg.placeId] = draft;
            }

            // Un índice 0 abre trazo NUEVO; cualquier otro continúa el abierto. Es la única
            // señal que hay, y basta: quien pinta reinicia la cuenta al levantar el dedo.
            if (msg.firstIndex == 0)
            {
                draft.Gesture.EndStroke();
                draft.Gesture.SetWall(msg.yaw);
                draft.Anchor = msg.Anchor;
                draft.Color = msg.color;
                draft.Width = (byte)Mathf.Clamp(Mathf.RoundToInt(msg.width * 255f), 1, 255);
                draft.Layer = msg.layer;
                draft.Gesture.BeginStroke();
                draft.NextIndex = 0;
            }
            else if (!draft.Gesture.StrokeOpen)
            {
                // Llegamos a mitad de trazo (paquete perdido, o entramos en alcance ahora). Se
                // abre igual: media raya es mejor que nada, y la pintada final la corrige. Color/
                // Width/Layer también se fijan aquí — SprayPainter los manda en TODO paquete, no
                // solo en el de firstIndex==0, así que sin esto el trazo nacía negro y fino en vez
                // del color/grosor real del bote emisor.
                draft.Gesture.SetWall(msg.yaw);
                draft.Anchor = msg.Anchor;
                draft.Color = msg.color;
                draft.Width = (byte)Mathf.Clamp(Mathf.RoundToInt(msg.width * 255f), 1, 255);
                draft.Layer = msg.layer;
                draft.Gesture.BeginStroke();
            }

            for (int i = 0; i < msg.PointCount; i++)
            {
                msg.PointAt(i, out short u, out short v);
                draft.Gesture.AddFromMillimetres(draft.Anchor, u, v);
            }

            draft.NextIndex = msg.firstIndex + msg.PointCount;
            draft.LastSeen = Time.time;

            var built = draft.Gesture.BuildMessage(draft.Color, draft.Width, draft.Layer);
            if (built != null) renderer.ShowPreview(msg.placeId, built);
        }

        private void ExpireStale()
        {
            if (_drafts.Count == 0) return;

            List<long> dead = null;
            foreach (var kv in _drafts)
            {
                if (Time.time - kv.Value.LastSeen < ExpirySeconds) continue;
                (dead ??= new List<long>()).Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) Retire(dead[i]);
        }

        private void EvictIfFull()
        {
            if (_drafts.Count < MaxLiveDrafts) return;

            long oldestKey = 0;
            float oldest = float.MaxValue;
            foreach (var kv in _drafts)
            {
                if (kv.Value.LastSeen >= oldest) continue;
                oldest = kv.Value.LastSeen;
                oldestKey = kv.Key;
            }
            Retire(oldestKey);
        }

        private void Retire(long placeId)
        {
            if (!_drafts.Remove(placeId)) return;
            var renderer = SprayRenderer.Instance;
            if (renderer != null) renderer.ClearPreview(placeId);
        }
    }
}
