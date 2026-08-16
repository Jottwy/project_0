using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// PLACEHOLDER (2026-08-15) — fija el brazo del wieldable en una pose AUTORADA A MANO, encima
    /// de la animación que esté sonando.
    ///
    /// SEGUNDO INTENTO, y el primero se descartó por inusable. La versión anterior pedía offsets
    /// en grados por hueso y por eje, más un eje de cierre de dedos que dependía de cómo se
    /// exportó el esqueleto y no se podía saber de antemano. Eso son ~20 números a encontrar a
    /// ciegas, reintroduciendo Play en cada prueba y perdiéndolos al salir: no es ajustar, es
    /// adivinar. Este componente no tiene ni un solo campo de grados.
    ///
    /// CÓMO SE USA: se abre el prefab, se giran los huesos del brazo en la vista de escena con el
    /// gizmo de rotación de siempre —viéndolo— y se pulsa "Capture Pose" en el menú del engranaje
    /// del componente. Eso guarda la <c>localRotation</c> de cada hueso del subárbol de
    /// <see cref="poseRootBone"/>. En juego se vuelven a escribir esas rotaciones.
    ///
    /// SE ESCRIBEN ABSOLUTAS, no sumadas: lo que autores es exactamente lo que sale, sin depender
    /// de la pose de la que parta el clip. El brazo NO queda muerto por esto — el balanceo y el
    /// bob del wieldable los aplica el MotionMixer sobre el rig entero, por encima, así que la
    /// pose se mueve con el cuerpo aunque los huesos estén fijos entre sí.
    ///
    /// <see cref="poseWeight"/> mezcla contra la animación: 0 la deja intacta, 1 impone la pose.
    /// Sirve para comparar sin desactivar nada y para suavizar si la pose sale demasiado rígida.
    /// </summary>
    // Por encima de AfterDefault2 (=100), que es donde corre el MotionMixer del vendor en
    // LateUpdate. El mixer mueve el rig entero y no huesos sueltos, así que en teoría no pisa esta
    // pose — pero el coste de asegurarlo es un atributo y el de equivocarse es una tarde.
    [DefaultExecutionOrder(200)]
    public sealed class WristPoseOverride : MonoBehaviour
    {
        [Serializable]
        public struct BoneRotation
        {
            public string boneName;
            public Quaternion localRotation;
        }

        [Tooltip("Hueso raíz de la cadena que se captura. Se guarda él y todos sus descendientes, " +
                 "así que UpperArm.L cubre antebrazo, twists, mano y los quince huesos de dedos.")]
        public string poseRootBone = "UpperArm.L";

        [Range(0f, 1f)]
        [Tooltip("Peso MÁXIMO de la pose. 0 = animación tal cual. 1 = la pose manda del todo.")]
        public float poseWeight = 1f;

        [Tooltip("Segundos que tarda la pose en imponerse al sacar el reloj. A 0 entra de golpe " +
                 "en el primer frame y se carga la animación de sacar.")]
        public float poseBlendInSeconds = 0.35f;

        [Tooltip("La referencia enseña un antebrazo solo. Apaga el otro brazo para comparar.")]
        public bool hideRightArm = false;

        [Header("Ajuste fino por hueso — EN VIVO, se suma a la pose capturada")]
        [Tooltip("Grados extra sobre la pose capturada, solo este hueso. El resto no se mueve.")]
        public Vector3 upperArmExtraEuler;

        [Tooltip("Grados extra solo para el antebrazo — la palanca que faltaba: RotationOffset " +
                 "del WieldableMotion gira el rig entero y esto gira SOLO esta articulación.")]
        public Vector3 forearmExtraEuler;

        [Tooltip("Grados extra solo para la mano (la cara del reloj viaja con ella).")]
        public Vector3 handExtraEuler;

        // VISIBLE a propósito, aunque no se edite a mano: su TAMAÑO es la única prueba de que la
        // captura llegó al disco. Escondido, un "Capture Pose" que no persiste es indistinguible
        // de uno que sí — y eso ya costó una ronda.
        [SerializeField]
        [Tooltip("Rellenado por Capture Pose. No se edita a mano; el número de elementos es la " +
                 "confirmación de que la pose está guardada en el prefab.")]
        private BoneRotation[] _capturedPose = Array.Empty<BoneRotation>();

        private const string RightArmRendererName = "RightArm";

        private Transform[] _boneCache;
        private Renderer _rightArmRenderer;
        private bool _resolved;
        private float _blendTimer;
        private float _currentWeight;

        // Índices en _capturedPose/_boneCache de los tres huesos con ajuste fino; -1 = no está.
        private int _upperArmIndex = -1;
        private int _forearmIndex = -1;
        private int _handIndex = -1;

        /// <summary>
        /// La pose guardada. Existe para que el creador pueda ARRASTRARLA de un prefab al
        /// siguiente en un Rebuild: posar el brazo a mano cuesta una tarde y regenerar el prefab
        /// lo borraría sin avisar.
        /// </summary>
        public BoneRotation[] CapturedPose
        {
            get => _capturedPose;
            set
            {
                _capturedPose = value ?? Array.Empty<BoneRotation>();
                _resolved = false;
            }
        }

        [ContextMenu("Capture Pose")]
        public void CapturePose()
        {
            var root = FindBone(poseRootBone);
            if (root == null)
            {
                Debug.LogWarning($"[WristPose] No existe el hueso \"{poseRootBone}\"; no hay nada " +
                                 "que capturar.", this);
                return;
            }

            var captured = new List<BoneRotation>(32);
            foreach (var bone in root.GetComponentsInChildren<Transform>(true))
                captured.Add(new BoneRotation { boneName = bone.name, localRotation = bone.localRotation });

            _capturedPose = captured.ToArray();
            _resolved = false;

            // Los extras se ponen a cero al capturar: en Play los huesos ya llevan pose×extra
            // escrito por el último LateUpdate, así que la captura los HORNEA dentro. Dejarlos
            // además como offset los aplicaría dos veces.
            upperArmExtraEuler = Vector3.zero;
            forearmExtraEuler = Vector3.zero;
            handExtraEuler = Vector3.zero;

            MarkDirty();

            Debug.Log($"[WristPose] Capturados {_capturedPose.Length} huesos desde {poseRootBone} " +
                      "(ajustes finos horneados y puestos a cero).", this);
        }

        [ContextMenu("Clear Pose")]
        public void ClearPose()
        {
            _capturedPose = Array.Empty<BoneRotation>();
            _resolved = false;
            MarkDirty();

            Debug.Log("[WristPose] Pose borrada; el brazo vuelve a la animación.", this);
        }

        /// <summary>
        /// Marca el cambio para que sobreviva a Ctrl+S. <c>SetDirty</c> por sí solo NO ensucia el
        /// Prefab Stage: al capturar con el prefab abierto, Unity no ve nada que guardar y el
        /// array se pierde al cerrar — sin ningún aviso, y con el log de captura ya impreso, que
        /// es lo que lo hace difícil de atribuir. Hay que ensuciar además la escena del stage.
        /// </summary>
        private void MarkDirty()
        {
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);

            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(stage.scene);
                return;
            }

            if (!Application.isPlaying && gameObject.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
#endif
        }

        // El reloj se guarda desactivando el GameObject y se saca activándolo, así que OnEnable es
        // exactamente el momento de sacar: reiniciar aquí el cronómetro hace que la mezcla arranque
        // de cero en cada saque sin tener que escuchar los eventos del controller.
        private void OnEnable()
        {
            _blendTimer = 0f;
            StartCoroutine(ReapplyMotionOffsets());
        }

        /// <summary>
        /// Reinicia el <c>WieldableMotion</c> entero unos frames después de sacar el reloj,
        /// apagándolo y encendiéndolo — la misma ruta que Unity.
        ///
        /// HISTORIA, porque este método ya falló una vez: el brazo salía mal colocado al equipar y
        /// se arreglaba tocando CUALQUIER campo del Inspector (o con Ctrl+Z). Eso es
        /// <c>OnValidate</c>, que llama a <c>OnEnable</c>: <c>ResetMixer</c> + <c>PushProfile</c>.
        /// La primera versión de este arreglo solo repetía el <c>ResetMixer</c> (reasignando los
        /// offsets a sí mismos) y NO bastó — verificado en juego el 2026-08-15, DLL recompilada y
        /// todo. La diferencia restante es <c>PushProfile</c>: recolocar el perfil de movimiento
        /// en la cima de la pila del handler. De ahí el toggle: <c>enabled = false/true</c>
        /// dispara <c>OnDisable</c> (PopProfile) y <c>OnEnable</c> (ResetMixer + PushProfile), el
        /// ciclo completo, por el camino del vendor y sin replicar ningún parámetro.
        /// </summary>
        private IEnumerator ReapplyMotionOffsets()
        {
            // Dos frames: el equipado del controller es una corrutina y el primer frame aún está
            // a mitad de su secuencia holster-anterior/equipar-este.
            yield return null;
            yield return null;

            var motion = GetComponentInChildren<PolymindGames.WieldableSystem.WieldableMotion>(true);
            if (motion == null || !motion.gameObject.activeInHierarchy)
                yield break;

            motion.enabled = false;
            motion.enabled = true;

            // Con log: la versión anterior corrió sin dejar rastro y hubo que deducir a ciegas si
            // se había ejecutado siquiera.
            Debug.Log("[WristPose] WieldableMotion reiniciado (pop+push del perfil y ResetMixer).", this);
        }

        private void LateUpdate()
        {
            if (!_resolved)
                Resolve();

            ApplyRightArmVisibility();

            // La pose sube de 0 al peso pedido durante el saque. Antes entraba a tope en el primer
            // frame: la animación de equipar empezaba a mover el brazo y esto se lo arrancaba de
            // golpe, que es la sensación de "se superpone". Ahora la animación se ve, y el brazo se
            // ASIENTA en la postura. El bob y el sway no dependen de esto — los aplica el
            // MotionMixer sobre el rig entero, una capa por encima — así que no se pierde nada.
            if (poseBlendInSeconds > 0f)
            {
                _blendTimer += Time.deltaTime;
                _currentWeight = poseWeight * Mathf.SmoothStep(
                    0f, 1f, Mathf.Clamp01(_blendTimer / poseBlendInSeconds));
            }
            else
            {
                _currentWeight = poseWeight;
            }

            if (_currentWeight <= 0f || _boneCache == null)
                return;

            // LateUpdate para pisar lo que el Animator acaba de escribir. En Update la animación
            // ganaría y no se vería nada.
            for (int i = 0; i < _boneCache.Length; i++)
            {
                var bone = _boneCache[i];
                if (bone == null)
                    continue;

                var target = _capturedPose[i].localRotation;

                // Ajuste fino por hueso, DESPUÉS de la pose: multiplicar en local equivale a girar
                // la articulación desde donde la dejó la captura. Es la palanca que el
                // RotationOffset del WieldableMotion no puede dar — aquel gira el rig entero.
                if (i == _upperArmIndex)
                    target *= Quaternion.Euler(upperArmExtraEuler);
                else if (i == _forearmIndex)
                    target *= Quaternion.Euler(forearmExtraEuler);
                else if (i == _handIndex)
                    target *= Quaternion.Euler(handExtraEuler);

                bone.localRotation = _currentWeight >= 1f
                    ? target
                    : Quaternion.Slerp(bone.localRotation, target, _currentWeight);
            }
        }

        private void Resolve()
        {
            _resolved = true;

            var byName = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                byName[t.name] = t;

            _boneCache = new Transform[_capturedPose.Length];
            int matched = 0;
            _upperArmIndex = _forearmIndex = _handIndex = -1;
            for (int i = 0; i < _capturedPose.Length; i++)
            {
                if (byName.TryGetValue(_capturedPose[i].boneName, out _boneCache[i]))
                    matched++;

                switch (_capturedPose[i].boneName)
                {
                    case "UpperArm.L": _upperArmIndex = i; break;
                    case "Forearm.L": _forearmIndex = i; break;
                    case "Hand.L": _handIndex = i; break;
                }
            }

            // Diagnóstico de una línea, y separa los tres fallos posibles sin más viajes: 0
            // guardados = la captura no llegó al disco; guardados pero 0 encontrados = los nombres
            // no cuadran con el rig; los dos iguales y aun así no se ve = algo pisa la pose después.
            if (Application.isPlaying)
                Debug.Log($"[WristPose] Pose guardada: {_capturedPose.Length} huesos. " +
                          $"Encontrados en el rig: {matched}. Peso: {poseWeight}.", this);

            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.name != RightArmRendererName)
                    continue;

                _rightArmRenderer = r;
                break;
            }
        }

        private Transform FindBone(string boneName)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name == boneName)
                    return t;

            return null;
        }

        private void ApplyRightArmVisibility()
        {
            if (_rightArmRenderer != null && _rightArmRenderer.enabled == hideRightArm)
                _rightArmRenderer.enabled = !hideRightArm;
        }
    }
}
