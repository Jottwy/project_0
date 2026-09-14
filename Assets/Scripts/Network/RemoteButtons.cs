namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-044: bit layout of the relayed <c>buttons</c> field — the peer's SUSTAINED cosmetic
    /// states. One definition shared by the writer (<see cref="PlayerPoseTransmitter"/>) and the
    /// readers (the proxy hooks), because a bitfield with the meaning of each bit written down in
    /// two places is a bug waiting for the first person who adds a third.
    ///
    /// The field itself is not new: it has existed in the IPC frame since ADR-009, written as a
    /// literal 0 and read by nobody. ADR-044 gives it meaning instead of adding a bool per state,
    /// so this costs no extra field on the wire and leaves 14 bits for whatever comes next.
    ///
    /// Bits are APPEND-ONLY. A value already assigned here must never be reused for something else:
    /// a peer running an older build keeps sending the old meaning, and a recycled bit would make
    /// its avatar do the wrong thing rather than simply do nothing.
    ///
    /// TRANSIENT actions do NOT belong here. A level bit cannot tell two chained events from one
    /// held one — that is what the counters (<c>hit_seq</c>, <c>fire_seq</c>, <c>melee_seq</c>) are for.
    /// </summary>
    public static class RemoteButtons
    {
        /// <summary>Aiming down sights.</summary>
        public const int Aiming = 1 << 0;

        /// <summary>Reloading — tactically the most valuable bit here: it says "vulnerable now".</summary>
        public const int Reloading = 1 << 1;

        /// <summary>Body-leaning left (Q). The exact case ADR-044 reserved these bits for: a sustained
        /// state that costs no wire because the field was already paid for.</summary>
        public const int LeanLeft = 1 << 2;

        /// <summary>Body-leaning right (E). Mutually exclusive with <see cref="LeanLeft"/> at the
        /// source (<c>BodyLeanState</c> is a single enum), so the two bits are never both set.</summary>
        public const int LeanRight = 1 << 3;

        /// <summary>
        /// Está saliendo pintura del bote AHORA MISMO (ADR-068). Otro estado SOSTENIDO en los bits
        /// libres que ADR-044 dejó: cero campos nuevos, cero bump de esquema y cero cambios en el
        /// backend, que relaya `buttons` sin mirarlo. Un peer con la versión vieja decodifica el bit
        /// y no lo interpreta — deja de ver el chorro, que es la degradación correcta.
        ///
        /// NIVEL y no contador: pintar dura, no es un evento. Un datagrama perdido lo corrige el
        /// siguiente, así que no hay nada que secuenciar (mismo criterio que `lightOn`).
        /// </summary>
        public const int Spraying = 1 << 4;

        /// <summary>
        /// ADR-131 D4 — este peer está SENTADO. Hoy sólo lo escribe el servidor, al dar de alta un
        /// vigilante (`species == 3`) en la silla de un puesto de oficina; nada impide que un día lo
        /// escriba un jugador que se siente.
        ///
        /// El bit y no la especie, porque son dos preguntas distintas: la especie dice QUÉ es (qué
        /// modelo, qué banco de audio) y esto dice EN QUÉ POSTURA está. Y bit y no contador porque
        /// es un estado SOSTENIDO — estar sentado dura, no ocurre (ADR-049 rechazó por escrito meter
        /// un conteo en este campo).
        ///
        /// Cero coste de wire: el campo ya viaja. Un cliente viejo decodifica el bit, no lo
        /// interpreta y dibuja al vigilante de pie, que es la degradación correcta.
        /// </summary>
        public const int Seated = 1 << 5;

        /// <summary>
        /// ADR-133 — este peer está DANDO CUERDA a su linterna de manivela. Séptimo bit y primero
        /// libre tras el sentado de ADR-131.
        ///
        /// NIVEL y no contador, por el mismo criterio de ADR-044 que ya decidió `Spraying`: dar
        /// cuerda DURA. Un datagrama perdido lo corrige el siguiente, y aquí ni siquiera importa
        /// perder uno — lo que se pinta es un giro continuo, no un golpe que haya que contar. Las
        /// vueltas sueltas sí serían contador, pero nadie necesita saber cuántas ha dado el vecino:
        /// lo que se ve es que está ocupado.
        ///
        /// Y ES LO QUE MÁS DICE DE UN VECINO en este juego, más que apuntar o recargar: quien da
        /// cuerda está clavado al 40 % de velocidad, parpadeando y haciendo ruido. Es el momento en
        /// el que no puede correr.
        ///
        /// Cero coste de wire: el campo ya viaja y el backend lo relaya sin mirarlo. Un cliente
        /// viejo decodifica el bit, no lo interpreta, y dibuja al vecino quieto sin más — la
        /// degradación correcta.
        /// </summary>
        public const int Cranking = 1 << 6;

        /// <summary>
        /// Este peer lleva una VENDA puesta en el brazo izquierdo. Octavo bit.
        ///
        /// Es el caso más puro de los que ADR-044 dejó sitio: un estado sostenido, cosmético y
        /// booleano. Lo que hace falta transmitir no es la herida ni el tratamiento, sino el hecho
        /// de que ahora mismo hay una venda ahí — el observador ve una tela blanca en el antebrazo
        /// del vecino y con eso sabe que le han dado y que ha tenido con qué curarse.
        ///
        /// NIVEL Y NO CONTADOR, y aquí ni siquiera se plantea la duda de `hit_seq`: vendarse no es
        /// un evento que haya que contar dos veces seguidas. Un datagrama perdido lo corrige el
        /// siguiente y el peor caso es una décima de segundo sin venda pintada.
        ///
        /// El estado vive en <c>PlayerMedicalState.Local</c> y es VOLÁTIL por decisión de alcance
        /// (Alpha 1): no se guarda. Persistirlo sería campo en el snapshot del jugador, o sea
        /// cambio del schema de guardado, o sea ADR — nada de lo cual hace falta para que se vea.
        ///
        /// Cero coste de wire: el campo ya viaja y el backend relaya `buttons` sin mirarlo. Un
        /// cliente viejo decodifica el bit, no lo interpreta y dibuja el brazo limpio.
        /// </summary>
        public const int BandagedArmLeft = 1 << 7;

        /// <summary>El mismo estado, brazo derecho. Noveno bit. Los dos son independientes: se
        /// puede llevar venda en los dos brazos a la vez, a diferencia de los bits de inclinación,
        /// que se excluyen en el origen.</summary>
        public const int BandagedArmRight = 1 << 8;

        /// <summary>
        /// Este peer tiene el LIBRO de supervivencia abierto (la libreta de mapas va dentro, B o N). Décimo bit.
        ///
        /// Hacía falta porque el libro NO pasa por la funda: es un wieldable suelto del vendor sin item detrás, así
        /// que `heldItem` seguía diciendo lo que hubiera en la funda y el vecino veía un rifle en la mano de alguien
        /// que estaba leyendo (arnés de poses, 2026-09-14). Con el bit, el avatar remoto esconde ese objeto y enseña
        /// el libro abierto.
        ///
        /// NIVEL y no contador: leer dura. Cero coste de wire; un cliente viejo lo ignora y dibuja lo de siempre.
        /// </summary>
        public const int BookOpen = 1 << 9;

        public static bool Has(int buttons, int bit) => (buttons & bit) != 0;
    }
}
