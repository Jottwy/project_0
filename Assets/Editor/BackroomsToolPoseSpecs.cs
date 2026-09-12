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
            // De la FOTO DE REFERENCIA de Joel: el destornillador apunta ADELANTE, con el mango
            // cruzado en la palma y la culata en el talón de la mano, no hacia arriba como lo pone
            // la animación del hacha. Y el pulgar va TENDIDO sobre el mango, que es el agarre con
            // el que se empuja y se atornilla.
            KeepVendorFraming = false,
            DesignAxisOnly = true,
            PitchDownDegrees = 6f,
            YawDegrees = -8f,
            ThumbAlongTool = true,
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
            // DE LAS SIETE FOTOS DE REFERENCIA de Joel (gente pintando), lo que coincide en todas:
            // la lata va casi VERTICAL con la boquilla arriba y una pizca de caída hacia delante;
            // el puño agarra ALTO, justo debajo de la tapa; el índice va extendido ENCIMA del
            // pulsador; y los otros tres rodean el cuerpo con el pulgar cerrando por el lado
            // opuesto. La muñeca sale casi recta, con el antebrazo entrando en diagonal por abajo.
            // PROBADO Y DESCARTADO 0,55 (el cuerpo de la lata, que es lo que se puede rodear): la
            // mano cierra tan abajo que el borde de la tapa ATRAVIESA el corazón y el anular —
            // 28,9 mm de sobra en el peor dedo frente a 1,3 con 0,68, y la captura lo enseña. Con
            // este bote de 19 cm el único sitio donde el puño no choca con la tapa es justo debajo
            // de ella. Lo que sigue sin estar bien es OTRA cosa: ver la deuda del checkpoint.
            FistFromTail = 0.68f,
            IndexOnNozzle = true,
            TuckLeftHand = false,
            KeepVendorFraming = false,
            DesignAxisOnly = true,
            // Cabeceo negativo = la boquilla mira ARRIBA. −72° deja la lata casi vertical con 18°
            // de caída hacia delante, que es la inclinación de las fotos.
            PitchDownDegrees = -72f,
            YawDegrees = -10f,
            // Muñeca casi neutra: quien pinta no fuerza la muñeca, la lleva alineada con el antebrazo.
            WristExtensionDegrees = 6f,
            WristUlnarDegrees = 4f,
        };

        [MenuItem("Backrooms/Screwdriver/Hornear pose y dedos", false, 93)]
        public static void BakeScrewdriver() => BackroomsToolPoseBaker.Bake(Screwdriver());

        [MenuItem("Backrooms/Spray/Hornear pose y dedos", false, 100)]
        public static void BakeSpray() => BackroomsToolPoseBaker.Bake(SprayCan());
    }
}
#endif
