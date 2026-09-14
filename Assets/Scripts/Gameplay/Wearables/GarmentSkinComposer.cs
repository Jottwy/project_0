using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Wearables
{
    /// <summary>
    /// ADR-149 R4b tanda 2: la máscara de piel del cuerpo (<c>_OpacityMask_Torso/Legs/Feet</c> del Skin.shadergraph del vendor)
    /// compuesta con dos capas de ropa y destapada en las zonas rotas. Compartido por el muñeco del inventario
    /// (<see cref="BackroomsGarmentDamageVisuals"/>) y el avatar remoto (ADR-149 enm. 7), que sólo difieren en de dónde sale
    /// la rotura: el inventario o la pose.
    /// </summary>
    public static class GarmentSkinComposer
    {
        private static readonly int MaskB = Shader.PropertyToID("_MaskB");
        private static readonly int ZoneMap = Shader.PropertyToID("_ZoneMap");
        private static readonly int RevealA = Shader.PropertyToID("_RevealA");
        private static readonly int RevealB = Shader.PropertyToID("_RevealB");
        private static readonly int RevealC = Shader.PropertyToID("_RevealC");
        private static readonly int RevealD = Shader.PropertyToID("_RevealD");
        private static readonly int TorsoMask = Shader.PropertyToID("_OpacityMask_Torso");
        private static readonly int LegsMask = Shader.PropertyToID("_OpacityMask_Legs");
        private static readonly int FeetMask = Shader.PropertyToID("_OpacityMask_Feet");

        /// <summary>
        /// Pone en el cuerpo la máscara de <paramref name="point"/>: la del vendor tal cual si no hay nada roto ni capa de
        /// encima, o una compuesta (se oculta lo que oculte cualquiera de las dos capas, se destapan las zonas marcadas).
        /// </summary>
        public static void Apply(SkinnedMeshRenderer body, BodyPoint point, Texture2D innerMask, Texture2D outerMask, bool[] reveal,
            Texture2D zoneMap, Shader composeShader, ref Material compose, RenderTexture[] cache, int maskSize)
        {
            if (body == null || zoneMap == null || composeShader == null) return;
            if (innerMask == null && outerMask == null) return; // el vendor no oculta piel aquí: nada que destapar

            int property = point switch
            {
                BodyPoint.Torso => TorsoMask,
                BodyPoint.Legs => LegsMask,
                _ => FeetMask,
            };
            bool any = false;
            foreach (bool r in reveal) any |= r;
            if (!any && outerMask == null)
            {
                body.material.SetTexture(property, innerMask);
                return;
            }

            ref var target = ref cache[(int)point];
            if (target == null)
                target = new RenderTexture(maskSize, maskSize, 0, RenderTextureFormat.ARGB32)
                    { name = $"BR_SkinMask_{point}", hideFlags = HideFlags.HideAndDontSave };
            compose ??= new Material(composeShader) { hideFlags = HideFlags.HideAndDontSave };
            compose.SetTexture(MaskB, outerMask != null ? outerMask : Texture2D.blackTexture);
            compose.SetTexture(ZoneMap, zoneMap);
            compose.SetVector(RevealA, Flags(reveal, 0));
            compose.SetVector(RevealB, Flags(reveal, 4));
            compose.SetVector(RevealC, Flags(reveal, 8));
            compose.SetVector(RevealD, Flags(reveal, 12));
            Graphics.Blit(innerMask != null ? innerMask : Texture2D.blackTexture, target, compose);
            body.material.SetTexture(property, target);
        }

        public static void Release(RenderTexture[] cache, ref Material compose)
        {
            for (int i = 0; i < cache.Length; i++)
            {
                if (cache[i] != null) cache[i].Release();
                cache[i] = null;
            }
            if (compose != null) Object.Destroy(compose);
            compose = null;
        }

        private static Vector4 Flags(bool[] reveal, int start)
            => new(At(reveal, start), At(reveal, start + 1), At(reveal, start + 2), At(reveal, start + 3));

        private static float At(bool[] reveal, int i) => i < reveal.Length && reveal[i] ? 1f : 0f;
    }
}
