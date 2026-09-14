using System.Runtime.CompilerServices;
using PolymindGames;
using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>ADR-149 D9: estado de una zona de prenda. Ordinal estable.</summary>
    public enum GarmentDamage : byte
    {
        Intact = 0,
        Cut = 1,
        Torn = 2,
        Patched = 3,
        Sewn = 4,
    }

    public enum GarmentRepair : byte
    {
        Tape = 1,
        Sew = 2,
    }

    /// <summary>
    /// ADR-149 D7-D9 + enm. 1: estado de UNA prenda por zona (daño de la tela y bolsillo roto). R2b: si la definición declara
    /// la propiedad <see cref="PropertyName"/>, el estado se guarda empaquetado en ella (4 bits por zona, 32 en un double) y
    /// viaja con el objeto: inventario, suelo, cadáver y guardado, sin tocar el esquema (ADR-072, <c>ItemProps</c>). Sin la
    /// propiedad vive solo en memoria. Objeto plano para EditMode.
    /// </summary>
    public sealed class GarmentState
    {
        public const float CutFactor = 0.4f;
        public const float TornFactor = 0f;
        public const float PatchedFactor = 0.6f;
        public const float SewnFactor = 0.9f;

        /// <summary>Un zarpazo desde este daño desgarra; menos, solo corta.</summary>
        public const float TearDamage = 20f;

        /// <summary>Nombre de la <c>ItemPropertyDefinition</c> (asset <c>BR_Garment Zones</c>).</summary>
        public const string PropertyName = "Garment Zones";

        private static readonly ConditionalWeakTable<Item, GarmentState> Table = new();
        private static int s_propertyId = int.MinValue;
        private ItemProperty _property;

        /// <summary>Sube con cualquier cambio de cualquier prenda: la UI lo sondea en vez de suscribirse.</summary>
        public static int Version { get; private set; }

        private readonly GarmentDamage[] _zones = new GarmentDamage[GarmentZonesData.MaxZones];
        private readonly bool[] _pocketBroken = new bool[GarmentZonesData.MaxZones];

        public static GarmentState Of(Item item) => Table.GetValue(item, Load);

        private static GarmentState Load(Item item)
        {
            var state = new GarmentState();
            if (s_propertyId == int.MinValue)
                s_propertyId = ItemPropertyDefinition.GetWithName(PropertyName)?.Id ?? 0;
            if (s_propertyId != 0 && item.TryGetProperty(s_propertyId, out var property))
            {
                state._property = property;
                state.Unpack((uint)property.Double);
            }
            return state;
        }

        /// <summary>Los 8 estados de zona en 32 bits: 3 de daño y 1 de bolsillo roto por zona, la zona 0 en los bits bajos.</summary>
        public uint Pack()
        {
            uint packed = 0;
            for (int i = 0; i < GarmentZonesData.MaxZones; i++)
                packed |= (uint)(((byte)_zones[i] & 0x7) | (_pocketBroken[i] ? 0x8 : 0)) << (4 * i);
            return packed;
        }

        public void Unpack(uint packed)
        {
            for (int i = 0; i < GarmentZonesData.MaxZones; i++)
            {
                uint nibble = (packed >> (4 * i)) & 0xF;
                uint damage = nibble & 0x7;
                _zones[i] = damage <= (uint)GarmentDamage.Sewn ? (GarmentDamage)damage : GarmentDamage.Intact;
                _pocketBroken[i] = (nibble & 0x8) != 0;
            }
        }

        private void Changed()
        {
            Version++;
            if (_property != null) _property.Double = Pack();
        }

        public GarmentDamage DamageOf(int zoneIndex) => _zones[zoneIndex];

        public bool IsPocketBroken(int zoneIndex) => _pocketBroken[zoneIndex];

        public static float Factor(GarmentDamage damage) => damage switch
        {
            GarmentDamage.Cut => CutFactor,
            GarmentDamage.Torn => TornFactor,
            GarmentDamage.Patched => PatchedFactor,
            GarmentDamage.Sewn => SewnFactor,
            _ => 1f,
        };

        public float Protection(int zoneIndex, in GarmentZone zone) => zone.Protection * Factor(_zones[zoneIndex]);

        /// <summary>Enm. 1: bala, puñalada o zarpazo rompen tela y bolsillo; golpe y caída, no.</summary>
        public static bool Tears(DamageType type)
            => type == DamageType.Slash || type == DamageType.Pierce || type == DamageType.Ballistic;

        public static GarmentDamage DamageFor(DamageType type, float damage)
        {
            if (!Tears(type)) return GarmentDamage.Intact;
            return type == DamageType.Slash && damage >= TearDamage ? GarmentDamage.Torn : GarmentDamage.Cut;
        }

        /// <summary>
        /// Un golpe en la zona. Devuelve si cambió algo; <paramref name="pocketBroke"/> dice si el bolsillo se ha roto AHORA.
        /// Un daño menos grave que el que ya tiene la zona no lo sustituye (un corte no «arregla» un desgarro).
        /// </summary>
        public bool ApplyHit(int zoneIndex, in GarmentZone zone, DamageType type, float damage, out bool pocketBroke)
        {
            pocketBroke = false;
            var hit = DamageFor(type, damage);
            if (hit == GarmentDamage.Intact) return false;
            bool changed = false;
            if (Rank(hit) >= Rank(_zones[zoneIndex]) && _zones[zoneIndex] != hit)
            {
                _zones[zoneIndex] = hit;
                changed = true;
            }
            if (zone.PocketSlots > 0 && !_pocketBroken[zoneIndex])
            {
                _pocketBroken[zoneIndex] = true;
                pocketBroke = true;
                changed = true;
            }
            if (changed) Changed();
            return changed;
        }

        public bool NeedsRepair(int zoneIndex)
            => _zones[zoneIndex] == GarmentDamage.Cut || _zones[zoneIndex] == GarmentDamage.Torn
               || _zones[zoneIndex] == GarmentDamage.Patched || _pocketBroken[zoneIndex];

        public bool CanRepair(int zoneIndex, GarmentRepair repair) => repair switch
        {
            GarmentRepair.Tape => _zones[zoneIndex] == GarmentDamage.Cut || _zones[zoneIndex] == GarmentDamage.Torn,
            GarmentRepair.Sew => NeedsRepair(zoneIndex),
            _ => false,
        };

        /// <summary>Coser un desgarro gasta además una tela.</summary>
        public bool NeedsCloth(int zoneIndex) => _zones[zoneIndex] == GarmentDamage.Torn;

        /// <summary>Cinta: 60 % y el bolsillo sigue roto. Coser: 90 % y devuelve el bolsillo.</summary>
        public bool Repair(int zoneIndex, GarmentRepair repair)
        {
            if (!CanRepair(zoneIndex, repair)) return false;
            if (repair == GarmentRepair.Tape)
            {
                _zones[zoneIndex] = GarmentDamage.Patched;
            }
            else
            {
                _zones[zoneIndex] = GarmentDamage.Sewn;
                _pocketBroken[zoneIndex] = false;
            }
            Changed();
            return true;
        }

        public int BrokenPocketSlots(GarmentZonesData data)
        {
            int broken = 0;
            for (int i = 0; i < data.Zones.Count && i < GarmentZonesData.MaxZones; i++)
                if (_pocketBroken[i]) broken += data.Zones[i].PocketSlots;
            return broken;
        }

        public bool IsPocketSlotBroken(GarmentZonesData data, int slotIndex)
        {
            int zone = data.ZoneOfPocketSlot(slotIndex);
            return zone >= 0 && zone < GarmentZonesData.MaxZones && _pocketBroken[zone];
        }

        public static string Describe(GarmentDamage damage, bool pocketBroken)
        {
            string cloth = damage switch
            {
                GarmentDamage.Cut => "agujero",
                GarmentDamage.Torn => "desgarro",
                GarmentDamage.Patched => "con cinta",
                GarmentDamage.Sewn => "cosido",
                _ => string.Empty,
            };
            if (!pocketBroken) return cloth;
            return cloth.Length == 0 ? "bolsillo roto" : cloth + " · bolsillo roto";
        }

        private static int Rank(GarmentDamage damage) => damage switch
        {
            GarmentDamage.Torn => 4,
            GarmentDamage.Cut => 3,
            GarmentDamage.Patched => 2,
            GarmentDamage.Sewn => 1,
            _ => 0,
        };
    }
}
