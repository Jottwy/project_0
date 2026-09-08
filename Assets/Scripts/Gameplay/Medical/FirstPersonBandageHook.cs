using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Medical
{
    /// <summary>
    /// La venda en TUS brazos: pinta el estado médico sobre los brazos de primera persona.
    ///
    /// EL PROBLEMA QUE RESUELVE, Y QUE NO SE VE HASTA QUE SE MIRA EL PROYECTO: en este juego los
    /// brazos de primera persona NO son un objeto del jugador, son parte de CADA wieldable. Cada
    /// prefab de arma trae su propia copia del esqueleto de brazos (los mismos huesos
    /// <c>Forearm.L</c> / <c>Forearm.R</c>, doce copias). Así que una venda pegada a "los brazos"
    /// desaparecería al cambiar de arma. Por eso esto vigila el wieldable ACTIVO y recoloca la
    /// venda en los brazos que estén puestos en cada momento.
    ///
    /// Y ÉSA ES LA RAZÓN DE QUE SEA UN VIGILANTE Y NO UN EFECTO: la venda no la enciende el acto de
    /// vendarse, la enciende el ESTADO. Sacas el reloj, cambias de arma, mueres y reapareces — cada
    /// fotograma se vuelve a preguntar "¿está vendado este brazo?" a
    /// <see cref="PlayerMedicalState.Local"/>, que es la misma fuente que leen los peers. Si el
    /// jugador se vendó hace diez minutos, la venda sigue ahí porque la respuesta sigue siendo sí.
    ///
    /// Vive en su propio objeto <c>DontDestroyOnLoad</c>, como
    /// <see cref="Net.PlayerPoseTransmitter"/> y por el mismo motivo: el rig de STP se reconstruye
    /// en runtime y se lleva por delante lo que cuelgue del jugador.
    ///
    /// PEREZOSO: quien no se venda nunca no crea ni un GameObject. Y las vendas cuelgan del hueso,
    /// o sea del propio wieldable, así que se destruyen con él sin dejar rastro.
    /// </summary>
    public sealed class FirstPersonBandageHook : MonoBehaviour
    {
        private const string LeftForearmBone = "Forearm.L";
        private const string RightForearmBone = "Forearm.R";

        private static FirstPersonBandageHook _instance;

        private ICharacter _character;
        private IWieldablesControllerCC _controller;

        // El wieldable para el que están resueltos los huesos de abajo. Comparado por REFERENCIA:
        // es lo que detecta un cambio de arma sin preguntar nada al inventario.
        private Object _boundTo;
        private Transform _leftForearm;
        private Transform _rightForearm;
        private GameObject _leftBandage;
        private GameObject _rightBandage;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[FirstPersonBandageHook]");
            _instance = go.AddComponent<FirstPersonBandageHook>();
            DontDestroyOnLoad(go);
        }

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
            if (_instance == this)
                _instance = null;
        }

        // LateUpdate y no Update: los huesos los escribe el Animator, y colocar la venda antes de
        // que él pose el brazo la deja un fotograma por detrás en cada movimiento de la mano.
        private void LateUpdate()
        {
            var wieldable = ActiveWieldable();
            if (!ReferenceEquals(wieldable, _boundTo))
                Bind(wieldable);

            var medical = PlayerMedicalState.Local;
            Show(ref _leftBandage, _leftForearm, medical.IsBandaged(BodyPartSide.Left));
            Show(ref _rightBandage, _rightForearm, medical.IsBandaged(BodyPartSide.Right));
        }

        /// <summary>
        /// Reengancha los huesos al wieldable que esté en la mano. Las vendas viejas NO se destruyen
        /// a mano: colgaban del arma anterior y se han ido con ella; aquí sólo se sueltan las
        /// referencias, que es lo que evita hablarle a un objeto ya destruido.
        /// </summary>
        private void Bind(Object wieldable)
        {
            _boundTo = wieldable;
            _leftBandage = null;
            _rightBandage = null;
            _leftForearm = null;
            _rightForearm = null;

            if (wieldable is not Component component)
                return;

            _leftForearm = FindBone(component.transform, LeftForearmBone);
            _rightForearm = FindBone(component.transform, RightForearmBone);
        }

        /// <summary>
        /// Enciende o apaga la venda de una zona, creándola la primera vez que hace falta. El
        /// <c>ref</c> es lo que permite que la creación sea perezosa sin duplicar el bloque.
        /// </summary>
        private static void Show(ref GameObject bandage, Transform forearm, bool visible)
        {
            if (forearm == null)
                return;

            if (bandage == null)
            {
                if (!visible)
                    return; // nunca vendado: no se crea nada

                bandage = BandageVisual.Attach(forearm);
                if (bandage == null)
                    return;
            }

            if (bandage.activeSelf != visible)
                bandage.SetActive(visible);
        }

        private Object ActiveWieldable()
        {
            // El personaje cacheado se revalida, no se cachea y ya: el rig de STP se reconstruye en
            // runtime y deja aquí una referencia a un objeto destruido, que en C# no es null pero
            // en Unity sí. Mismo cuidado que el transmisor de poses tiene con el motor.
            if (_character is Object characterObject && characterObject == null)
            {
                _character = null;
                _controller = null;
            }

            if (_character == null)
            {
                _character = GameMode.HasInstance ? GameMode.Instance.LocalPlayer : null;
                _controller = null;
            }

            if (_character == null)
                return null;

            if (_controller == null)
                _controller = _character.GetCC<IWieldablesControllerCC>();
            if (_controller == null)
                return null;

            var wieldable = _controller.ActiveWieldable;
            // Un wieldable destruido sigue siendo no-null como interfaz: la comparación con el
            // Object de Unity es la única que dice la verdad.
            if (wieldable is Object unityObject)
                return unityObject == null ? null : unityObject;
            return null;
        }

        /// <summary>Busca un hueso por nombre bajo un transform. Los brazos son un puñado de huesos
        /// y esto sólo corre al cambiar de arma, así que un recorrido llano basta.</summary>
        private static Transform FindBone(Transform root, string boneName)
        {
            if (root == null)
                return null;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                    return t;
            }

            return null;
        }
    }
}
