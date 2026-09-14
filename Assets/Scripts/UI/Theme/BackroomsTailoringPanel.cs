using System;
using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Body;
using PolymindGames.InventorySystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Pestaña SASTRERÍA de la columna derecha (ADR-149; Joel, 2026-09-14: «un panel dedicado a sastrería»). Lista la ropa por
    /// zonas que se lleva puesta: una fila por prenda y, debajo, las zonas que necesitan arreglo con COSER y CINTA y lo que
    /// falta para cada uno. Abajo, los materiales. Lee y repara a través de <see cref="BackroomsGarmentPrototype"/>; sin él
    /// (fuera de la escena de pruebas) solo lo dice. Las filas salen de una plantilla que monta el builder.
    /// </summary>
    public sealed class BackroomsTailoringPanel : MonoBehaviour
    {
        [SerializeField] private RectTransform _content;
        [SerializeField] private RectTransform _rowTemplate;
        [SerializeField] private TextMeshProUGUI _empty;
        [SerializeField] private TextMeshProUGUI _materials;
        [SerializeField] private TextMeshProUGUI _notice;
        [SerializeField] private Color _garmentInk = Color.white;
        [SerializeField] private Color _zoneInk = new(0.66f, 0.64f, 0.55f);

        private sealed class Row
        {
            public GameObject Go;
            public TextMeshProUGUI Name;
            public TextMeshProUGUI State;
            public Button Sew;
            public Button Tape;
            public Item Item;
            public int Index;
        }

        private readonly List<WornGarmentZone> _zones = new();
        private readonly List<Row> _rows = new();
        private int _shownKey = int.MinValue;
        private float _nextCheck;

        private void OnEnable()
        {
            _shownKey = int.MinValue;
            _nextCheck = 0f;
            if (_notice != null) _notice.text = string.Empty;
            if (_rowTemplate != null) _rowTemplate.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.2f;
            var garments = BackroomsGarmentPrototype.Instance;
            _zones.Clear();
            var materials = (0, 0, 0, 0);
            if (garments != null)
            {
                garments.CollectWornZones(_zones);
                materials = garments.Materials();
            }
            var hash = new HashCode();
            hash.Add(garments != null);
            hash.Add(GarmentState.Version);
            hash.Add(materials);
            foreach (var zone in _zones) hash.Add(zone.Item.GetHashCode() ^ zone.Index);
            int key = hash.ToHashCode();
            if (key == _shownKey) return;
            _shownKey = key;
            Rebuild(garments, materials);
        }

        private void Rebuild(BackroomsGarmentPrototype garments, (int needles, int threads, int cloths, int tapes) materials)
        {
            int used = 0;
            if (garments == null)
            {
                SetEmpty("Sastrería: solo en la escena de pruebas");
                if (_materials != null) _materials.text = string.Empty;
                HideRowsFrom(0);
                return;
            }

            if (_materials != null)
                _materials.text = $"Aguja {materials.needles} · Hilo {materials.threads} · Tela {materials.cloths} · Cinta {materials.tapes}";
            SetEmpty(_zones.Count == 0 ? "No llevas ropa que se pueda coser" : string.Empty);

            Item garment = null;
            for (int i = 0; i < _zones.Count; i++)
            {
                var zone = _zones[i];
                var state = GarmentState.Of(zone.Item);
                if (zone.Item != garment)
                {
                    garment = zone.Item;
                    int damaged = 0;
                    for (int j = i; j < _zones.Count && _zones[j].Item == garment; j++)
                        if (state.NeedsRepair(_zones[j].Index)) damaged++;
                    var header = Take(used++);
                    Fill(header, garment.Name, damaged == 0 ? "sin daños" : damaged == 1 ? "1 zona por arreglar" : $"{damaged} zonas por arreglar",
                        _garmentInk, null, -1);
                }
                if (!state.NeedsRepair(zone.Index)) continue;

                bool canSew = GarmentRepairPlan.CanSew(state, zone.Index, materials.needles, materials.threads, materials.cloths, out string sewMissing);
                bool canTape = GarmentRepairPlan.CanTape(state, zone.Index, materials.tapes, out _);
                int percent = BackroomsGarmentPrototype.ToPercent(state.Protection(zone.Index, zone.Data.Zones[zone.Index]));
                var row = Take(used++);
                Fill(row, "   " + BodyZones.Label(zone.Zone),
                    StateLine(state.DamageOf(zone.Index), state.IsPocketBroken(zone.Index), percent, sewMissing), _zoneInk, zone.Item, zone.Index);
                row.Sew.gameObject.SetActive(state.CanRepair(zone.Index, GarmentRepair.Sew));
                row.Sew.interactable = canSew;
                row.Tape.gameObject.SetActive(state.CanRepair(zone.Index, GarmentRepair.Tape));
                row.Tape.interactable = canTape;
            }
            HideRowsFrom(used);
        }

        /// <summary>La segunda línea de una zona: daño, protección que le queda y lo que falta para coserla.</summary>
        public static string StateLine(GarmentDamage damage, bool pocketBroken, int protectionPercent, string sewMissing)
        {
            string text = GarmentState.Describe(damage, pocketBroken);
            text = text.Length == 0 ? $"protege {protectionPercent} %" : $"{text} · protege {protectionPercent} %";
            return string.IsNullOrEmpty(sewMissing) ? text : $"{text} · coser: falta {sewMissing}";
        }

        private void Fill(Row row, string name, string state, Color ink, Item item, int index)
        {
            row.Name.text = name;
            row.Name.color = ink;
            row.State.text = state;
            row.Item = item;
            row.Index = index;
            row.Sew.gameObject.SetActive(false);
            row.Tape.gameObject.SetActive(false);
        }

        private Row Take(int index)
        {
            while (_rows.Count <= index)
            {
                var go = Instantiate(_rowTemplate.gameObject, _content, false);
                go.name = "Row";
                var row = new Row
                {
                    Go = go,
                    Name = go.transform.Find("Name").GetComponent<TextMeshProUGUI>(),
                    State = go.transform.Find("State").GetComponent<TextMeshProUGUI>(),
                    Sew = go.transform.Find("SewBtn").GetComponent<Button>(),
                    Tape = go.transform.Find("TapeBtn").GetComponent<Button>(),
                };
                row.Sew.onClick.AddListener(() => OnRepair(row, GarmentRepair.Sew));
                row.Tape.onClick.AddListener(() => OnRepair(row, GarmentRepair.Tape));
                _rows.Add(row);
            }
            var taken = _rows[index];
            if (!taken.Go.activeSelf) taken.Go.SetActive(true);
            return taken;
        }

        private void HideRowsFrom(int index)
        {
            for (int i = index; i < _rows.Count; i++)
                if (_rows[i].Go.activeSelf) _rows[i].Go.SetActive(false);
        }

        private void SetEmpty(string text)
        {
            if (_empty == null) return;
            _empty.text = text;
            _empty.gameObject.SetActive(text.Length > 0);
        }

        private void OnRepair(Row row, GarmentRepair repair)
        {
            var garments = BackroomsGarmentPrototype.Instance;
            if (garments == null || row.Item == null) return;
            string message = garments.Repair(row.Item, row.Index, repair);
            if (_notice != null) _notice.text = message;
            _shownKey = int.MinValue;
            _nextCheck = 0f;
        }
    }
}
