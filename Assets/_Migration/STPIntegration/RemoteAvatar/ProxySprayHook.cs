using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Gameplay.Audio;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-068 fase A: que se VEA y se OIGA que otro jugador está pintando, mientras lo pinta.
    ///
    /// Hasta aquí, pintar era invisible para los demás hasta que la pintada se cerraba y viajaba
    /// entera: hasta un segundo largo de alguien haciendo aspavientos contra una pared en silencio.
    /// Este hook pone el chorro y el siseo en cuanto empieza.
    ///
    /// LO QUE VIAJA ES UN BIT, no el trazo. `RemoteButtons.Spraying` va en los bits libres de
    /// `buttons` (ADR-044), así que esta feature cuesta CERO bytes de wire, cero campos y cero
    /// cambios en el backend — el dibujo sigue apareciendo al soltar, que es la fase B.
    ///
    /// LA DIRECCIÓN DEL CHORRO se compone del giro del proxy y del `pitch` que ya viaja (ADR-021),
    /// no del hueso de la mano: la mano la coloca <see cref="ProxyHeldItemHook"/> con la pose de
    /// agarre que le toque, y colgar la dirección de ahí ataría este hook a decisiones de arte.
    /// Sale del puño y va donde el peer mira, que es lo que el observador espera.
    ///
    /// El SONIDO es el mismo <see cref="SpraySfx"/> del bote propio, creado en modo espacial: un
    /// siseo sintetizado, sin assets, y ya enrutado al bus de SFX. Su objetivo se consume cada
    /// frame, así que si el peer desaparece a mitad de trazo el siseo se apaga solo por la rampa.
    ///
    /// Perezoso: un peer que no pinta nunca no crea ni partículas ni emisor. Y reutilizable — se
    /// borra el fichero y los demás dejan de ver el chorro, sin tocar nada más.
    /// </summary>
    public sealed class ProxySprayHook : MonoBehaviour
    {
        [Header("Anclaje")]
        [Tooltip("Hueso del que cuelga el chorro. Sobrevive a los cambios de item en la mano.")]
        [SerializeField] private string _handBoneName = "Hand.R";

        [Tooltip("Desplazamiento local desde el hueso, más o menos donde cae la boquilla.")]
        [SerializeField] private Vector3 _localOffset = new Vector3(0f, 0.12f, 0.04f);

        [Header("Chorro")]
        [SerializeField, Min(0f)] private float _speed = 7f;
        [SerializeField, Min(0f)] private float _lifetime = 0.22f;
        [SerializeField, Min(0f)] private float _size = 0.025f;
        [SerializeField, Min(0f)] private float _rate = 70f;

        private RemotePlayerManager _manager;
        private Transform _hand;
        private ParticleSystem _jet;
        private SpraySfx _sfx;
        private bool _emitting;

        private void Awake()
        {
            _hand = ProxyRigUtil.FindBone(transform, _handBoneName);
        }

        // Rearme para el reciclado del pool: un proxy reusado no hereda el chorro del anterior.
        private void OnEnable()
        {
            _emitting = false;
            if (_jet != null)
            {
                var emission = _jet.emission;
                emission.enabled = false;
            }
        }

        private void OnDisable() => SetEmitting(false);

        private void OnDestroy()
        {
            // Los dos cuelgan del mundo, no de este transform (ver CreateJet): si no se retiran
            // aquí, cada proxy reciclado deja un emisor mudo y un sistema de partículas parado.
            if (_sfx != null) Destroy(_sfx.gameObject);
            if (_jet != null) Destroy(_jet.gameObject);
        }

        private void LateUpdate()
        {
            if (_hand == null) return;
            if (!ProxyViewLookup.TryResolve(transform, ref _manager, out var view)) return;

            bool spraying = RemoteButtons.Has(view.buttons, RemoteButtons.Spraying) && !view.dead;
            SetEmitting(spraying);
            if (!spraying || _jet == null || _sfx == null) return;

            // Hacia donde mira el peer: su giro (ya está en el transform del proxy) más el pitch
            // que viaja aparte. En LateUpdate para leer el hueso DESPUÉS del Animator.
            var aim = Quaternion.AngleAxis(view.pitch, transform.right) * transform.forward;
            _jet.transform.SetPositionAndRotation(_hand.TransformPoint(_localOffset),
                Quaternion.LookRotation(aim, Vector3.up));

            // Se vuelve a pedir cada frame a propósito: SpraySfx consume su objetivo y se apaga
            // solo, así que no hace falta un mensaje de "para" que se pueda perder.
            _sfx.follow = _jet.transform;
            _sfx.SetSpraying(true);
        }

        private void SetEmitting(bool on)
        {
            if (on == _emitting) return;
            _emitting = on;

            if (on)
            {
                if (_jet == null) _jet = CreateJet();
                if (_sfx == null) _sfx = SpraySfx.Create("SpraySfx_Proxy", spatial: true);
            }
            if (_jet == null) return;

            var emission = _jet.emission;
            emission.enabled = on;
            if (on && !_jet.isPlaying) _jet.Play();
        }

        /// <summary>
        /// Un chorro estrecho de gotas pequeñas, en el color con el que pinta el bote. Se construye
        /// una sola vez y luego solo se enciende y se apaga la emisión: recrear el sistema por cada
        /// trazo tiraría el pool de partículas a cada rato.
        /// </summary>
        private ParticleSystem CreateJet()
        {
            var go = new GameObject("ProxySprayJet");
            go.transform.SetParent(null, false); // en mundo: se coloca a mano cada frame
            go.layer = gameObject.layer;

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = _lifetime;
            main.startSpeed = _speed;
            main.startSize = _size;
            // Color32 → Color explícito: `startColor` es un MinMaxGradient y no acepta el primero.
            main.startColor = (Color)SprayRenderer.ColorOf(SprayCan.DefaultColorIndex);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;
            main.gravityModifier = 0.15f;
            main.playOnAwake = false;

            var emission = ps.emission;
            emission.rateOverTime = _rate;
            emission.enabled = false;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 7f;
            shape.radius = 0.005f;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = BuildParticleMaterial();

            return ps;
        }

        /// <summary>
        /// Material URP para las partículas. Con el shader de Built-in saldría MAGENTA desde
        /// ADR-065, y sin material saldría el rosa de "shader perdido", que es peor: parece un bug
        /// de la feature y no del render.
        /// </summary>
        private static Material BuildParticleMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Sprites/Default");
            if (shader == null) return null;

            var mat = new Material(shader) { name = "ProxySprayJet" };
            mat.color = SprayRenderer.ColorOf(SprayCan.DefaultColorIndex);
            return mat;
        }
    }
}
