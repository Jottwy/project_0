namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// ADR-105 enm. 19 — QUÉ ES esta superficie, más allá de a qué espacio pertenece.
    ///
    /// <c>style</c> dice el PAPEL del espacio (espina, pasillo, nave…) y de ahí sale el tinte. Esto
    /// es el otro eje: dentro de un mismo papel, una mampara de cubículo no es una pared y una placa
    /// de falso techo no es un forjado. Selecciona QUÉ material va en la ranura;
    /// <see cref="Wg3StyleMaterials"/> le aplica después el tinte del papel.
    /// </summary>
    public enum Wg3Look : byte
    {
        /// <summary>Los cuatro materiales de siempre.</summary>
        Base = 0,
        /// <summary>Un despacho: moqueta de oficina en el suelo.</summary>
        Office = 1,
        /// <summary>Un despacho CON falso techo: moqueta de oficina y placa de 60 con perfil en T.</summary>
        OfficeDropped = 2,
        /// <summary>Una mampara de cubículo: tela gris en la ranura de estructura.</summary>
        Partition = 3,
    }

    /// <summary>
    /// De lo que llega por el cable a un <see cref="Wg3Look"/>. **Se INFIERE, y por eso el cable no
    /// se toca.**
    ///
    /// # Por qué inferir y no mandar un bit
    ///
    /// Los tres casos ya son distinguibles con los campos que hay: una mampara tiene una forma que
    /// ningún otro macizo tiene (12 × 140), y un despacho es el único papel que sale con estilo 0
    /// —<c>fill::style_of</c> reparte 1..6 y 7/8, y el <c>_ =&gt; 0</c> sólo lo pisan
    /// <c>SpaceRole::Office</c> y <c>Void</c>, que no se construye (<c>is_built</c>)—. Un bit nuevo
    /// habría costado un bump de wire y un ADR para decir lo que la geometría ya dice.
    ///
    /// # EL ACOPLE, que es real y hay que declararlo
    ///
    /// <see cref="CubicleThicknessCm"/> y <see cref="CubicleHeightCm"/> son copias de
    /// <c>fill::CUBICLE_T_CM</c> y <c>CUBICLE_H_CM</c>. Es la misma convención con la que el backend
    /// se clasifica a sí mismo los macizos —por FORMA: 8 barrote, 12 mampara, 15 dintel, 20 pretil,
    /// 30 división, 35 parteluz, 40 viga, 45 oclusor— así que un emisor nuevo que reutilice el 12
    /// con 140 de alto rompe las dos puntas a la vez, no sólo ésta. Los tests de EditMode fijan los
    /// números para que el cambio se vea aquí y no en una captura.
    /// </summary>
    public static class Wg3Looks
    {
        /// <summary>Espejo de <c>fill::CUBICLE_T_CM</c>.</summary>
        public const int CubicleThicknessCm = 12;
        /// <summary>Espejo de <c>fill::CUBICLE_H_CM</c>.</summary>
        public const int CubicleHeightCm = 140;

        /// <summary>El estilo de un despacho: <c>fill::style_of</c> devuelve 0 para
        /// <c>SpaceRole::Office</c>.</summary>
        public const int OfficeStyle = 0;

        /// <summary>Altura libre por debajo de la cual un despacho lleva falso techo. Es el techo del
        /// rango de <c>fill::ceiling_cap_cm</c> (270..300, en pasos de 10) para el carácter oficina;
        /// un despacho sin falso techo mide 300..380 (ADR-104 enm. 4), así que el corte está en el
        /// borde: 300 justo lo lleva y 310 no. Un empate cae del lado del falso techo a propósito —
        /// una placa en un techo de 3,00 se lee como oficina, y un forjado a 3,00 también.</summary>
        public const int DroppedCeilingMaxCm = 300;

        /// <summary>El aspecto de un TRAMO: despacho o no, con placa o sin ella.</summary>
        public static Wg3Look ForSegment(int style, int heightCm)
        {
            if (style != OfficeStyle) return Wg3Look.Base;
            return heightCm > 0 && heightCm <= DroppedCeilingMaxCm ? Wg3Look.OfficeDropped : Wg3Look.Office;
        }

        /// <summary>El aspecto de un MACIZO. Sólo la mampara de cubículo tiene material propio: un
        /// pilar o un pretil de un despacho son de obra, como en cualquier otro espacio.</summary>
        public static Wg3Look ForSolid(int sizeXCm, int sizeZCm, int bottomYCm, int topYCm)
        {
            int thin = sizeXCm < sizeZCm ? sizeXCm : sizeZCm;
            if (thin == CubicleThicknessCm && topYCm - bottomYCm == CubicleHeightCm)
                return Wg3Look.Partition;
            return Wg3Look.Base;
        }

        /// <summary>Espejo de <c>fill::DAIS_HEIGHTS_CM</c> (ADR-151 D1): 40 a 120 de 20 en 20.</summary>
        public const int DaisMinHeightCm = 40;
        public const int DaisMaxHeightCm = 120;
        /// <summary>Espejo de <c>fill::DAIS_MIN_DEPTH_CM</c>.</summary>
        public const int DaisMinDepthCm = 200;
        /// <summary>Espejo de <c>fill::DAIS_STEP_RISE_CM</c>, <c>DAIS_STEP_RUN_CM</c> y
        /// <c>DAIS_ACCESS_WIDTH_CM</c>.</summary>
        public const int DaisStepRiseCm = 20;
        public const int DaisStepRunCm = 60;
        public const int DaisAccessWidthCm = 200;

        /// <summary>
        /// ADR-151 — ¿esta caja se PISA por arriba? Un estrado o uno de sus peldaños, reconocidos por
        /// la forma igual que <c>fill::is_dais</c> y <c>fill::is_dais_step</c> (el llamador ya ha
        /// descartado lo oculto, la decoración y lo que no es caja). Lleva el suelo de la sala en la
        /// cara de arriba: con la pared entera se leía como un bloque forrado de papel.
        ///
        /// La tarima de 20 (ADR-105 enm. 13) NO entra: su huella es la de la sala y ningún peldaño
        /// mide 60 × 200 con 20 de alto y huella de sala a la vez; es valor validado y no se toca.
        /// </summary>
        public static bool IsWalkableTop(int sizeXCm, int sizeZCm, int bottomYCm, int topYCm)
        {
            int h = topYCm - bottomYCm;
            int thin = sizeXCm < sizeZCm ? sizeXCm : sizeZCm;
            int wide = sizeXCm < sizeZCm ? sizeZCm : sizeXCm;
            bool dais = h >= DaisMinHeightCm && h <= DaisMaxHeightCm && h % DaisStepRiseCm == 0
                && thin >= DaisMinDepthCm;
            bool step = thin == DaisStepRunCm && wide == DaisAccessWidthCm
                && h % DaisStepRiseCm == 0 && h >= DaisStepRiseCm && h < DaisMaxHeightCm;
            return dais || step;
        }
    }
}
