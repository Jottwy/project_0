using System;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Body
{
    /// <summary>
    /// ADR-149 R4b: lo que necesita el shader <c>Backrooms/Garment Lit</c>. Cada vértice de una prenda lleva su zona del cuerpo
    /// (<see cref="ZoneFromBone"/>, horneada en el editor por el hueso que más lo mueve) y sus coordenadas en metros alrededor
    /// del eje de esa zona (<see cref="ProjectZones"/>); el material recibe el estado de cada zona (<see cref="ZoneCodes"/>)
    /// empaquetado en cuatro vectores (<see cref="Pack"/>). Puro, con test.
    /// </summary>
    public static class GarmentVisualState
    {
        public const int Slots = 16;

        /// <summary>El código de cada zona del cuerpo que cubre la prenda (<see cref="GarmentState.VisualCode"/>); el resto a 0.</summary>
        public static void ZoneCodes(GarmentState state, GarmentZonesData data, float[] codes)
        {
            Array.Clear(codes, 0, codes.Length);
            if (state == null || data == null) return;
            for (int i = 0; i < data.Zones.Count && i < GarmentZonesData.MaxZones; i++)
            {
                int zone = (int)data.Zones[i].Zone;
                if (zone < codes.Length) codes[zone] = state.VisualCode(i);
            }
        }

        /// <summary>16 códigos en 4 vectores: la zona 0 en <c>packed[0].x</c>.</summary>
        public static void Pack(float[] codes, Vector4[] packed)
        {
            for (int v = 0; v < 4; v++)
            {
                int b = v * 4;
                packed[v] = new Vector4(At(codes, b), At(codes, b + 1), At(codes, b + 2), At(codes, b + 3));
            }
        }

        private static float At(float[] codes, int index) => index < codes.Length ? codes[index] : 0f;

        /// <summary>
        /// La zona de un hueso: el suyo o el del primer padre que tenga zona (los dedos van a la mano, el cuello y las
        /// clavículas al pecho, los dedos del pie al pie). Sin ninguno, el pecho.
        /// </summary>
        public static BodyZone ZoneFromBone(Transform bone)
        {
            for (var t = bone; t != null; t = t.parent)
                if (BodyZoneResolver.TryFromBone(t.name, out var zone))
                    return zone;
            return BodyZone.Chest;
        }

        /// <summary>
        /// Tanda 2: en qué zonas se ve la piel. Manda la prenda MÁS INTERIOR que cubre cada zona: si está abierta (agujero o
        /// desgarro) se ve la piel; si está sana o arreglada, la tapa, aunque la de encima esté rota (por ese agujero se ve la
        /// de dentro). Donde solo cubre la de encima, manda la de encima.
        /// </summary>
        public static void SkinReveal(GarmentState inner, GarmentZonesData innerData, GarmentState outer, GarmentZonesData outerData,
            bool[] reveal)
        {
            Array.Clear(reveal, 0, reveal.Length);
            for (int zone = 0; zone < reveal.Length && zone < BodyZones.Count; zone++)
            {
                int i = inner != null && innerData != null ? innerData.IndexOf((BodyZone)zone) : -1;
                if (i >= 0)
                {
                    reveal[zone] = IsOpen(inner.DamageOf(i));
                    continue;
                }
                int o = outer != null && outerData != null ? outerData.IndexOf((BodyZone)zone) : -1;
                if (o >= 0) reveal[zone] = IsOpen(outer.DamageOf(o));
            }
        }

        public static bool IsOpen(GarmentDamage damage) => damage == GarmentDamage.Cut || damage == GarmentDamage.Torn;

        /// <summary>
        /// R4c: la zona de un hueso de los brazos de primera persona (su rig no es el del cuerpo: <c>Forearm.*</c> y los
        /// <c>ForearmTwist.N.*</c> son antebrazo). La mano y los dedos no llevan manga.
        /// </summary>
        public static bool TryFirstPersonZone(string boneName, out BodyZone zone)
        {
            zone = BodyZone.ForearmL;
            if (string.IsNullOrEmpty(boneName)) return false;
            bool left = boneName.EndsWith(".L");
            if (!left && !boneName.EndsWith(".R")) return false;
            if (boneName.StartsWith("UpperArm."))
            {
                zone = left ? BodyZone.UpperArmL : BodyZone.UpperArmR;
                return true;
            }
            if (boneName.StartsWith("Forearm.") || boneName.StartsWith("ForearmTwist."))
            {
                zone = left ? BodyZone.ForearmL : BodyZone.ForearmR;
                return true;
            }
            return false;
        }

        /// <summary>
        /// ADR-149 enm. 7: en qué hueco de <c>garments</c> viaja una prenda — 0-3 = <c>equipment</c> [Head, Torso, Legs, Feet],
        /// 4 = la de encima — o -1 si no se lleva puesta.
        /// </summary>
        public static int WireSlot(int itemId, int[] equipment, int outer)
        {
            if (itemId == 0) return -1;
            if (outer == itemId) return 4;
            if (equipment == null) return -1;
            for (int i = 0; i < equipment.Length && i < 4; i++)
                if (equipment[i] == itemId) return i;
            return -1;
        }

        /// <summary>ADR-149 enm. 7: el estado de una prenda a partir de lo que viaja (mismo empaquetado que sus propiedades).</summary>
        public static void StateFromWire(GarmentState state, uint damage, ushort cuts)
        {
            state.Unpack(damage);
            state.UnpackCuts(cuts);
        }

        /// <summary>El índice del hueso con más peso en un vértice.</summary>
        public static int DominantBone(BoneWeight weight)
        {
            int bone = weight.boneIndex0;
            float best = weight.weight0;
            if (weight.weight1 > best) { best = weight.weight1; bone = weight.boneIndex1; }
            if (weight.weight2 > best) { best = weight.weight2; bone = weight.boneIndex2; }
            if (weight.weight3 > best) bone = weight.boneIndex3;
            return bone;
        }

        /// <summary>
        /// Coordenadas en metros de cada vértice alrededor del eje de su zona, en la pose de reposo. El eje de un miembro es
        /// su dirección principal (apuntando hacia fuera del cuerpo); el del tronco, la cabeza, las manos y los pies, el de
        /// la zona si es alargada o si no «arriba». <c>u</c> es la vuelta (arco en metros, 0 al frente) y <c>v</c> la
        /// distancia a lo largo del eje desde el centro de la zona.
        /// </summary>
        public static void ProjectZones(Vector3[] positions, BodyZone[] zones, Vector3 up, Vector3 forward, Vector2[] coords,
            float[] front = null, Vector3[] zoneAxes = null, Vector3[] zoneReferences = null)
        {
            up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.up;
            forward = Vector3.ProjectOnPlane(forward, up);
            forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;

            var bodyCenter = Vector3.zero;
            foreach (var p in positions) bodyCenter += p;
            if (positions.Length > 0) bodyCenter /= positions.Length;

            for (int zone = 0; zone < BodyZones.Count; zone++)
            {
                var center = Vector3.zero;
                int n = 0;
                for (int i = 0; i < positions.Length; i++)
                    if ((int)zones[i] == zone) { center += positions[i]; n++; }
                if (n == 0) continue;
                center /= n;

                var axis = PrincipalAxis(positions, zones, zone, center, out float elongation);
                bool limb = BodyZones.IsLimb((BodyZone)zone) && !IsExtremity((BodyZone)zone);
                if (!limb && elongation < 2f) axis = up;
                if (limb && Vector3.Dot(axis, center - bodyCenter) < 0f) axis = -axis;
                if (!limb && Vector3.Dot(axis, up) < 0f && axis != up) axis = -axis;
                // El eje del esqueleto manda si lo hay: en una manga corta (más ancha que larga) la dirección de más
                // varianza sale atravesada y el daño caía encima del hombro en vez de delante.
                if (zoneAxes != null && zone < zoneAxes.Length && zoneAxes[zone].sqrMagnitude > 1e-8f) axis = zoneAxes[zone].normalized;

                var zoneForward = zoneReferences != null && zone < zoneReferences.Length && zoneReferences[zone].sqrMagnitude > 1e-8f
                    ? zoneReferences[zone] : forward;
                var reference = Vector3.ProjectOnPlane(zoneForward, axis);
                if (reference.sqrMagnitude < 0.05f) reference = Vector3.ProjectOnPlane(up, axis);
                reference.Normalize();
                var side = Vector3.Cross(axis, reference);

                float radius = 0f;
                for (int i = 0; i < positions.Length; i++)
                    if ((int)zones[i] == zone) radius += Vector3.ProjectOnPlane(positions[i] - center, axis).magnitude;
                radius /= n;

                for (int i = 0; i < positions.Length; i++)
                {
                    if ((int)zones[i] != zone) continue;
                    var d = positions[i] - center;
                    float along = Vector3.Dot(d, axis);
                    var radial = d - along * axis;
                    float angle = Mathf.Atan2(Vector3.Dot(radial, side), Vector3.Dot(radial, reference));
                    coords[i] = new Vector2(angle * radius, along);
                    if (front != null) front[i] = Mathf.Cos(angle);
                }
            }
        }

        private static bool IsExtremity(BodyZone zone)
            => zone == BodyZone.HandL || zone == BodyZone.HandR || zone == BodyZone.FootL || zone == BodyZone.FootR;

        /// <summary>Dirección de más varianza de los vértices de una zona (potencia sobre la covarianza) y cuánto domina.</summary>
        private static Vector3 PrincipalAxis(Vector3[] positions, BodyZone[] zones, int zone, Vector3 center, out float elongation)
        {
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if ((int)zones[i] != zone) continue;
                var d = positions[i] - center;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            var v = new Vector3(0.577f, 0.577f, 0.577f);
            float lambda = 0f;
            for (int it = 0; it < 32; it++)
            {
                var next = new Vector3(xx * v.x + xy * v.y + xz * v.z, xy * v.x + yy * v.y + yz * v.z, xz * v.x + yz * v.y + zz * v.z);
                lambda = next.magnitude;
                if (lambda < 1e-12f) { elongation = 1f; return Vector3.up; }
                v = next / lambda;
            }
            float trace = xx + yy + zz;
            float rest = Mathf.Max(trace - lambda, 1e-9f) * 0.5f;
            elongation = lambda / rest;
            return v;
        }
    }
}
