using System;
using System.Collections.Generic;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>Una zona de una prenda (ADR-149 D7): qué parte del cuerpo cubre, cuánto protege y cuántos huecos de bolsillo tiene.</summary>
    [Serializable]
    public struct GarmentZone
    {
        public BodyZone Zone;

        [Range(0f, 1f)]
        public float Protection;

        [Range(0, 4)]
        public int PocketSlots;

        public GarmentZone(BodyZone zone, float protection, int pocketSlots = 0)
        {
            Zone = zone;
            Protection = protection;
            PocketSlots = pocketSlots;
        }
    }

    /// <summary>
    /// Las zonas de una prenda (ADR-149 D7 + enm. 1). Dato de DEFINICIÓN. Hasta 8 zonas (su estado cabrá en 32 bits en R2).
    /// Los bolsillos de cada zona son un tramo fijo de índices del contenedor de bolsillos, en el orden de las zonas.
    /// </summary>
    [Serializable]
    public sealed class GarmentZonesData : ItemData
    {
        public const int MaxZones = 8;

        [SerializeField]
        private GarmentZone[] _zones = new GarmentZone[0];

        public GarmentZonesData() { }

        public GarmentZonesData(params GarmentZone[] zones) => _zones = zones ?? new GarmentZone[0];

        public IReadOnlyList<GarmentZone> Zones => _zones;

        public int PocketSlots
        {
            get
            {
                int slots = 0;
                foreach (var zone in _zones) slots += zone.PocketSlots;
                return slots;
            }
        }

        public int IndexOf(BodyZone zone)
        {
            for (int i = 0; i < _zones.Length && i < MaxZones; i++)
                if (_zones[i].Zone == zone) return i;
            return -1;
        }

        /// <summary>Primer índice del contenedor de bolsillos que pertenece a la zona <paramref name="zoneIndex"/>.</summary>
        public int PocketStart(int zoneIndex)
        {
            int start = 0;
            for (int i = 0; i < zoneIndex && i < _zones.Length; i++) start += _zones[i].PocketSlots;
            return start;
        }

        /// <summary>Zona dueña del hueco <paramref name="slotIndex"/> del contenedor de bolsillos, o -1.</summary>
        public int ZoneOfPocketSlot(int slotIndex)
        {
            int start = 0;
            for (int i = 0; i < _zones.Length; i++)
            {
                if (slotIndex >= start && slotIndex < start + _zones[i].PocketSlots) return i;
                start += _zones[i].PocketSlots;
            }
            return -1;
        }
    }
}
