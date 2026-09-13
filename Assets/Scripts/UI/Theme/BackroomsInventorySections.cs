using System.Collections.Generic;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// D13 (INVENTORY-ROADMAP, 2026-09-13): las secciones de «Lo que llevas» se pliegan con clic en su cabecera y se
    /// reordenan arrastrándola. Orden y plegado se guardan EN EL PC (PlayerPrefs): es preferencia de interfaz, no
    /// estado de partida ni de servidor. Coloca cada sección cada frame porque su altura depende de los huecos que se
    /// ven, y eso cambia al ponerse o quitarse una mochila. Mientras arrastras algo que cabe en una sección plegada, se
    /// abre sola y vuelve a plegarse al soltar (no se guarda).
    /// </summary>
    public sealed class BackroomsInventorySections : MonoBehaviour
    {
        public const string OrderKey = "br.inventory.sections.order";
        public const string FoldedKey = "br.inventory.sections.folded";

        [SerializeField] private string[] _ids = new string[0];
        [SerializeField] private RectTransform[] _sections = new RectTransform[0];
        [SerializeField] private RectTransform[] _grids = new RectTransform[0];
        [SerializeField] private TextMeshProUGUI[] _foldMarks = new TextMeshProUGUI[0];
        [SerializeField] private float _topInset = 56f;
        [SerializeField] private float _gap = 12f;
        [SerializeField] private float _sidePad = 16f;
        [SerializeField] private float _titleHeight = 34f;

        private int[] _order;
        private bool[] _folded;
        private CanvasGroup[] _gridGroups;
        private GridLayoutGroup[] _layouts;
        private ItemContainerUI[] _containers;
        private readonly List<ItemSlotUIBase> _dragScan = new List<ItemSlotUIBase>();
        private int _dragging = -1;

        private void Awake()
        {
            int n = _ids.Length;
            _order = ParseOrder(PlayerPrefs.GetString(OrderKey, string.Empty), _ids);
            _folded = ParseFolded(PlayerPrefs.GetString(FoldedKey, string.Empty), _ids);
            _gridGroups = new CanvasGroup[n];
            _layouts = new GridLayoutGroup[n];
            _containers = new ItemContainerUI[n];
            for (int i = 0; i < n; i++)
            {
                if (_grids[i] == null) continue;
                _containers[i] = _grids[i].GetComponent<ItemContainerUI>();
                _gridGroups[i] = _grids[i].GetComponent<CanvasGroup>();
                if (_gridGroups[i] == null) _gridGroups[i] = _grids[i].gameObject.AddComponent<CanvasGroup>();
                _layouts[i] = _grids[i].GetComponent<GridLayoutGroup>();
            }
        }

        private void LateUpdate() => Layout();

        public void Toggle(string id)
        {
            int i = System.Array.IndexOf(_ids, id);
            if (i < 0) return;
            _folded[i] = !_folded[i];
            Save();
        }

        public void BeginDrag(string id) => _dragging = System.Array.IndexOf(_ids, id);

        public void Drag(PointerEventData eventData)
        {
            if (_dragging < 0) return;
            var panel = (RectTransform)transform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(panel, eventData.position, eventData.pressEventCamera, out var local))
                return;
            float fromTop = panel.rect.yMax - local.y;
            int current = System.Array.IndexOf(_order, _dragging);
            int target = TargetPosition(fromTop);
            if (target != current) Move(_order, current, target);
        }

        public void EndDrag()
        {
            if (_dragging >= 0) Save();
            _dragging = -1;
        }

        private void Layout()
        {
            bool dragging = TryGetDraggedStack(out var dragged);
            float y = _topInset;
            for (int p = 0; p < _order.Length; p++)
            {
                int i = _order[p];
                bool folded = _folded[i] && !(dragging && Fits(i, dragged));
                float h = HeightOf(i, folded);
                if (_sections[i] != null) PlaceTop(_sections[i], y, y + h);
                if (_grids[i] != null)
                {
                    PlaceTop(_grids[i], y + _titleHeight, y + h);
                    bool open = !folded;
                    _gridGroups[i].alpha = open ? 1f : 0f;
                    _gridGroups[i].blocksRaycasts = open;
                    _gridGroups[i].interactable = open;
                }
                if (_foldMarks[i] != null) _foldMarks[i].text = folded ? "+" : "-";
                y += h + _gap;
            }
        }

        /// <summary>Posición en el orden donde cae un puntero a <paramref name="fromTop"/> px del borde de arriba.</summary>
        private int TargetPosition(float fromTop)
        {
            float y = _topInset;
            for (int p = 0; p < _order.Length; p++)
            {
                float h = HeightOf(_order[p], _folded[_order[p]]);
                if (fromTop < y + h * 0.5f) return p;
                y += h + _gap;
            }
            return _order.Length - 1;
        }

        private float HeightOf(int i, bool folded)
        {
            if (folded || _grids[i] == null || _layouts[i] == null) return _titleHeight;
            int visible = 0;
            foreach (Transform child in _grids[i])
                if (child.gameObject.activeSelf) visible++;
            var layout = _layouts[i];
            float inner = ((RectTransform)transform).rect.width - 2f * _sidePad - layout.padding.left - layout.padding.right;
            int columns = Mathf.Max(1, Mathf.FloorToInt((inner + layout.spacing.x) / (layout.cellSize.x + layout.spacing.x)));
            return SectionHeight(visible, columns, _titleHeight, layout.padding.top, layout.padding.bottom, layout.cellSize.y, layout.spacing.y);
        }

        /// <summary>
        /// La pila que se está arrastrando. El vendor la guarda en privado, pero su copia visual es un hueco con un
        /// contenedor propio de un hueco, sin inventario, colgado del mismo padre que <see cref="ItemDragger"/>.
        /// </summary>
        private bool TryGetDraggedStack(out ItemStack stack)
        {
            stack = ItemStack.Null;
            if (!ItemDragger.HasInstance || !ItemDragger.Instance.IsDragging) return false;
            var parent = ItemDragger.Instance.transform.parent;
            if (parent == null) return false;
            parent.GetComponentsInChildren(false, _dragScan);
            foreach (var slot in _dragScan)
            {
                if (!slot.HasItem || slot.Slot.Container == null || slot.Slot.Container.Inventory != null) continue;
                stack = slot.Slot.GetStack();
                return true;
            }
            return false;
        }

        private bool Fits(int i, ItemStack stack)
        {
            var container = _containers[i] != null ? _containers[i].Container : null;
            return container != null && container.GetAllowedCount(stack).allowedCount > 0;
        }

        private void PlaceTop(RectTransform rt, float top, float bottom)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(_sidePad, -bottom);
            rt.offsetMax = new Vector2(-_sidePad, -top);
        }

        private void Save()
        {
            var order = new string[_order.Length];
            for (int p = 0; p < _order.Length; p++) order[p] = _ids[_order[p]];
            var folded = new List<string>();
            for (int i = 0; i < _ids.Length; i++) if (_folded[i]) folded.Add(_ids[i]);
            PlayerPrefs.SetString(OrderKey, string.Join(",", order));
            PlayerPrefs.SetString(FoldedKey, string.Join(",", folded));
            PlayerPrefs.Save();
        }

        /// <summary>Alto de una sección: cabecera, relleno y las filas de huecos visibles; sin huecos, solo la cabecera.</summary>
        public static float SectionHeight(int visibleSlots, int columns, float titleHeight, float padTop, float padBottom, float cell, float spacing)
        {
            if (visibleSlots <= 0) return titleHeight + padTop;
            int rows = Mathf.CeilToInt(visibleSlots / (float)Mathf.Max(1, columns));
            return titleHeight + padTop + rows * cell + (rows - 1) * spacing + padBottom;
        }

        /// <summary>Orden guardado → índices; ids desconocidos o repetidos se ignoran y los que falten van al final en su orden.</summary>
        public static int[] ParseOrder(string saved, string[] ids)
        {
            var result = new List<int>(ids.Length);
            if (!string.IsNullOrEmpty(saved))
                foreach (var token in saved.Split(','))
                {
                    int i = System.Array.IndexOf(ids, token.Trim());
                    if (i >= 0 && !result.Contains(i)) result.Add(i);
                }
            for (int i = 0; i < ids.Length; i++)
                if (!result.Contains(i)) result.Add(i);
            return result.ToArray();
        }

        public static bool[] ParseFolded(string saved, string[] ids)
        {
            var folded = new bool[ids.Length];
            if (string.IsNullOrEmpty(saved)) return folded;
            foreach (var token in saved.Split(','))
            {
                int i = System.Array.IndexOf(ids, token.Trim());
                if (i >= 0) folded[i] = true;
            }
            return folded;
        }

        /// <summary>Saca el elemento de <paramref name="from"/> y lo inserta en <paramref name="to"/>.</summary>
        public static void Move(int[] order, int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= order.Length || to >= order.Length) return;
            int moved = order[from];
            if (from < to) System.Array.Copy(order, from + 1, order, from, to - from);
            else System.Array.Copy(order, to, order, to + 1, from - to);
            order[to] = moved;
        }
    }
}
