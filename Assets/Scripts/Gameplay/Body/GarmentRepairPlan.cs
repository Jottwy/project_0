using System.Collections.Generic;
using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>Una zona de una prenda puesta, tal como la lista la sastrería.</summary>
    public readonly struct WornGarmentZone
    {
        public readonly Item Item;
        public readonly GarmentZonesData Data;
        public readonly int Index;

        public WornGarmentZone(Item item, GarmentZonesData data, int index)
        {
            Item = item;
            Data = data;
            Index = index;
        }

        public BodyZone Zone => Data.Zones[Index].Zone;
    }

    /// <summary>
    /// ADR-149 enm. 1, sastrería: qué arreglo cabe en una zona con lo que se lleva encima y, si no cabe, qué falta. Coser pide
    /// aguja e hilo (y tela si es un desgarro); la cinta, cinta. Puro, con test.
    /// </summary>
    public static class GarmentRepairPlan
    {
        public static bool CanSew(GarmentState state, int zoneIndex, int needles, int threads, int cloths, out string missing)
        {
            missing = string.Empty;
            if (!state.CanRepair(zoneIndex, GarmentRepair.Sew)) return false;
            var parts = new List<string>(3);
            if (needles <= 0) parts.Add("aguja");
            if (threads <= 0) parts.Add("hilo");
            if (state.NeedsCloth(zoneIndex) && cloths <= 0) parts.Add("tela");
            missing = string.Join(" + ", parts);
            return parts.Count == 0;
        }

        public static bool CanTape(GarmentState state, int zoneIndex, int tapes, out string missing)
        {
            missing = string.Empty;
            if (!state.CanRepair(zoneIndex, GarmentRepair.Tape)) return false;
            if (tapes <= 0) missing = "cinta";
            return missing.Length == 0;
        }
    }
}
