#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-077 enm. 4 — los números de diseño de cada herramienta de una mano, y sus menús. El
    /// algoritmo vive en <see cref="BackroomsToolPoseBaker"/>; aquí sólo está lo que distingue a un
    /// destornillador de un bote de spray.
    /// </summary>
    internal static class BackroomsToolPoseSpecs
    {
        public const string ScrewdriverAnimFolder = BackroomsScrewdriverModelApplier.BakedFolder + "/Anim";
        public const string SprayAnimFolder = "Assets/Art/Items/SprayCan/Anim";

        public const string ScrewdriverIdleClip = ScrewdriverAnimFolder + "/BR_Screwdriver_Idle.anim";
        public const string SprayIdleClip = SprayAnimFolder + "/BR_SprayCan_Idle.anim";

        /// <summary>
        /// El destornillador. Donante: el HACHA, que es de dos manos — de ahí que la izquierda haya
        /// que recogerla, porque un destornillador no lo agarra nadie con las dos.
        /// </summary>
        internal static BackroomsToolPoseBaker.Spec Screwdriver() => new()
        {
            Tag = "[ScrewdriverPose]",
            PrefabPath = BackroomsScrewdriverModelApplier.WieldablePrefabPath,
            NodeName = BackroomsScrewdriverModelApplier.NodeName,
            AnimFolder = ScrewdriverAnimFolder,
            ClipPrefix = "BR_Screwdriver",
            DonorSkinNode = BackroomsScrewdriverModelApplier.DonorMeshNode,
            TipChildHint = null,
            LengthMeters = 0.24f,
            // El puño cierra sobre el MANGO, que es el quinto de abajo: 0,22 del largo desde la
            // culata. Con 0,5 (el centro) los dedos cerraban sobre el vástago, que es de 5 mm.
            FistFromTail = 0.22f,
            TuckLeftHand = true,
        };

        /// <summary>
        /// El bote de spray. Donante: la ANTORCHA, que ya es de una mano, así que la izquierda se
        /// queda como el vendor la dejó.
        /// </summary>
        internal static BackroomsToolPoseBaker.Spec SprayCan() => new()
        {
            Tag = "[SprayPose]",
            PrefabPath = BackroomsSprayModelSwapper.PrefabPath,
            NodeName = BackroomsSprayModelSwapper.NodeName,
            AnimFolder = SprayAnimFolder,
            ClipPrefix = "BR_SprayCan",
            DonorSkinNode = "WoodenTorch",
            TipChildHint = "FPS_VFX_SmallFire",
            LengthMeters = 0.19f,
            // Por encima del centro, que es donde se agarra una lata para que el ÍNDICE llegue al
            // pulsador; pero no tan arriba como 0,75, que descolgaba la lata cuatro centímetros y
            // la sacaba del encuadre del jugador (medido, captura de la 8.ª pasada).
            FistFromTail = 0.60f,
            IndexOnNozzle = true,
            TuckLeftHand = false,
        };

        [MenuItem("Backrooms/Screwdriver/Hornear pose y dedos", false, 93)]
        public static void BakeScrewdriver() => BackroomsToolPoseBaker.Bake(Screwdriver());

        [MenuItem("Backrooms/Spray/Hornear pose y dedos", false, 100)]
        public static void BakeSpray() => BackroomsToolPoseBaker.Bake(SprayCan());
    }
}
#endif
