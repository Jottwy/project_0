using System.Collections.Generic;
using PolymindGames;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Pulido 3 (INVENTORY-ROADMAP): abrir y cerrar el inventario con curva en vez de golpe. El vendor enciende y apaga
    /// sus paneles poniendo el alpha de su <see cref="CanvasGroup"/> a 0 o 1 de una vez (y lo nuestro con
    /// <c>BackroomsInspectionOnly</c> igual); aquí cualquier salto 0↔1 de un CanvasGroup del inventario se convierte en
    /// un fundido corto, y las tres columnas se deslizan un poco hacia su sitio. Los clics se cortan al instante (eso lo
    /// sigue haciendo quien apaga), solo se suaviza lo que se ve.
    ///
    /// Lo que ya se anima solo (secciones, cinta del zoom, rótulo del cinturón) nunca salta de 0 a 1 en un frame, así que
    /// no se toca. Sin editar el vendor: solo lee y escribe alphas.
    /// </summary>
    public sealed class BackroomsInventoryTransition : CharacterUIBehaviour
    {
        [SerializeField] private RectTransform[] _columns = new RectTransform[0];
        [SerializeField] private Vector2[] _slides = new Vector2[0];
        [SerializeField, Range(1f, 40f)] private float _sharpness = 16f;

        private sealed class Fade
        {
            public CanvasGroup Group;
            public float Last;
            public float Written;
            public float Target;
            public bool Animating;
        }

        private readonly List<Fade> _fades = new();
        private readonly List<CanvasGroup> _scan = new();
        private Vector2[] _base = new Vector2[0];
        private IInventoryInspectionManagerCC _inspection;
        private float _progress;
        private bool _open;

        protected override void Awake()
        {
            base.Awake();
            _base = new Vector2[_columns.Length];
            for (int i = 0; i < _columns.Length; i++)
                if (_columns[i] != null) _base[i] = _columns[i].anchoredPosition;
            Rescan();
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            _inspection = character.GetCC<IInventoryInspectionManagerCC>();
            if (_inspection == null) return;
            _inspection.InspectionStarted += OnStarted;
            _inspection.InspectionEnded += OnEnded;
            _open = _inspection.IsInspecting;
            _progress = _open ? 1f : 0f;
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            if (_inspection == null) return;
            _inspection.InspectionStarted -= OnStarted;
            _inspection.InspectionEnded -= OnEnded;
            _inspection = null;
        }

        private void OnStarted()
        {
            _open = true;
            Rescan();
        }

        private void OnEnded() => _open = false;

        /// <summary>CanvasGroups creados en Play (rejillas de sección, huecos generados) entran al abrir.</summary>
        private void Rescan()
        {
            GetComponentsInChildren(true, _scan);
            foreach (var group in _scan)
            {
                bool known = false;
                foreach (var fade in _fades)
                    if (fade.Group == group) { known = true; break; }
                if (!known) _fades.Add(new Fade { Group = group, Last = group.alpha, Written = group.alpha, Target = group.alpha });
            }
        }

        private void LateUpdate()
        {
            float t = BackroomsInventorySections.Approach01(_sharpness, Time.unscaledDeltaTime);
            foreach (var fade in _fades) Step(fade, t);

            _progress = BackroomsInventorySections.Settle(_progress, _open ? 1f : 0f, t, 0.002f);
            for (int i = 0; i < _columns.Length; i++)
            {
                if (_columns[i] == null) continue;
                var slide = i < _slides.Length ? _slides[i] : Vector2.zero;
                _columns[i].anchoredPosition = _base[i] + slide * (1f - _progress);
            }
        }

        private static void Step(Fade fade, float t)
        {
            var group = fade.Group;
            if (group == null) return;
            float now = group.alpha;
            if (fade.Animating)
            {
                if (now != fade.Written)
                {
                    // Otro lo ha cambiado a mitad: si es otro salto, se persigue el nuevo destino; si no, manda él.
                    if (now > 0f && now < 1f)
                    {
                        fade.Animating = false;
                        fade.Last = now;
                        return;
                    }
                    fade.Target = now;
                    now = fade.Written;
                }
            }
            else
            {
                if (!IsFlip(fade.Last, now))
                {
                    fade.Last = now;
                    return;
                }
                fade.Animating = true;
                fade.Target = now;
                now = fade.Last;
            }

            float next = BackroomsInventorySections.Settle(now, fade.Target, t, 0.01f);
            group.alpha = next;
            fade.Written = next;
            fade.Last = next;
            if (next == fade.Target) fade.Animating = false;
        }

        /// <summary>Un salto de golpe de apagado a encendido o al revés: lo que se suaviza.</summary>
        public static bool IsFlip(float last, float now) => (last <= 0f && now >= 1f) || (last >= 1f && now <= 0f);
    }
}
