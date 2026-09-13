using BackroomsSurvival.Gameplay.Body;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Una zona de la vista Heridas (ADR-149 R0): pinta el estado de su zona en <see cref="BackroomsBodyPrototype.Local"/>
    /// y, con un clic, la trata con la venda o la férula del inventario. Sin el prototipo en la escena solo avisa.
    /// </summary>
    public sealed class BackroomsWoundZoneUI : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField]
        private BodyZone _zone;

        [SerializeField]
        private Image _fill;

        [SerializeField]
        private TextMeshProUGUI _label;

        [SerializeField]
        private TextMeshProUGUI _notice;

        [SerializeField]
        private Color _healthy = Color.gray;

        [SerializeField]
        private Color _scratch = new(0.69f, 0.53f, 0.12f);

        [SerializeField]
        private Color _cut = new(0.56f, 0.14f, 0.11f);

        [SerializeField]
        private Color _fracture = new(0.37f, 0.23f, 0.55f);

        [SerializeField]
        private Color _treated = new(0.29f, 0.35f, 0.23f);

        private int _shown = -1;

        public BodyZone Zone => _zone;

        private void OnEnable() => _shown = -1;

        private void Update()
        {
            int raw = BackroomsBodyPrototype.Local.Raw(_zone);
            if (raw == _shown) return;
            _shown = raw;
            Paint();
        }

        private void Paint()
        {
            var body = BackroomsBodyPrototype.Local;
            var injury = body.InjuryOf(_zone);
            bool treated = body.IsBandaged(_zone) || body.IsSplinted(_zone);
            if (_fill != null)
                _fill.color = injury switch
                {
                    BodyInjury.None => _healthy,
                    _ when treated => _treated,
                    BodyInjury.Scratch => _scratch,
                    BodyInjury.Cut => _cut,
                    _ => _fracture,
                };
            if (_label != null)
            {
                string state = Describe(injury, body.IsBandaged(_zone), body.IsSplinted(_zone));
                _label.text = state.Length == 0 ? BodyZones.Label(_zone) : $"{BodyZones.Label(_zone)}\n{state}";
            }
        }

        public static string Describe(BodyInjury injury, bool bandaged, bool splinted) => injury switch
        {
            BodyInjury.Scratch => bandaged ? "rasguño · vendado" : "rasguño",
            BodyInjury.Cut => bandaged ? "corte · vendado" : "corte · sangra",
            BodyInjury.Fracture => splinted ? "fractura · férula" : "fractura",
            _ => string.Empty,
        };

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            string message = BackroomsBodyPrototype.Instance != null
                ? BackroomsBodyPrototype.Instance.TryTreat(_zone)
                : "Heridas: solo en la escena de pruebas";
            if (_notice != null) _notice.text = message;
            _shown = -1;
        }
    }
}
