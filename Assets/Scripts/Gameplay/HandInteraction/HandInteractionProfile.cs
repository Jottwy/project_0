using System;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.HandInteraction
{
    /// <summary>Cuántas manos sujetan el objeto. Lo demás (inspeccionar, consumir…) se añade cuando exista.</summary>
    public enum HandInteractionKind
    {
        OneHand = 0,
        TwoHand = 1,
    }

    /// <summary>
    /// Qué hace UNA mano con el objeto. La mano de la que CUELGA el modelo (la «portadora», hoy siempre
    /// <c>Hand.R</c>) no se mueve de sitio: con <see cref="Grip"/> sólo se rehacen sus dedos, porque moverla
    /// arrastraría el objeto. Recoger la izquierda fuera del encuadre sigue siendo cosa de
    /// <c>BackroomsToolPoseBaker</c> (ADR-077 enm. 4).
    /// </summary>
    public enum HandRole
    {
        /// <summary>Agarra: IK hasta su punto del objeto y dedos cerrados por contacto.</summary>
        Grip = 0,
        /// <summary>No se toca: se queda exactamente como la anima el clip base (un agarre ya horneado).</summary>
        Keep = 1,
        /// <summary>Brazo del clip base, dedos relajados: una mano que no sujeta nada no va en garra.</summary>
        Relaxed = 2,
        /// <summary>
        /// Copia la pose de esta mano de OTRO clip del mismo wieldable (<see cref="HandGripTarget.referenceClipPath"/>
        /// en <see cref="HandGripTarget.referenceTime"/>), en el espacio del objeto: mano, dedos, hombro y codo. Es
        /// para reutilizar un agarre ya resuelto y validado —la izquierda sobre el pomo de la manivela— en idle,
        /// equipar y enfundar, en vez de inventar otro.
        /// </summary>
        Reference = 3,
        /// <summary>
        /// Sólo la PORTADORA: se vuelve a resolver entera —brazo, muñeca y dedos, con la misma búsqueda de
        /// naturalidad que una secundaria— sujetando el objeto EXACTAMENTE donde ya lo llevaba cada clip. El
        /// encuadre (dónde está el objeto en pantalla) no cambia; cambia cómo lo coge la mano. Se aplica a
        /// todos los clips del controller (también a las capas de acción) y reescribe el offset del modelo
        /// bajo la mano.
        /// </summary>
        Regrip = 4,
    }

    public enum HandFingerStyle
    {
        /// <summary>Los cuatro dedos rodean y el pulgar cierra por el otro lado.</summary>
        Wrap = 0,
        /// <summary>Los cuatro rodean y el pulgar se tiende a lo largo del eje, hacia la punta.</summary>
        ThumbAlongAxis = 1,
        /// <summary>El índice se queda casi recto sobre el objeto (gatillo, pulsador, apoyo).</summary>
        IndexExtended = 2,
    }

    /// <summary>
    /// El objetivo de UNA mano, en el espacio del objeto: el eje es el +Y local de la malla de agarre
    /// (culata abajo, punta arriba) y el «reloj» gira alrededor de ese eje con 0° en su +Z y 90° en su +X.
    /// Nada aquí depende del rig: el mismo objetivo sirve para los brazos de primera persona y, más
    /// adelante, para el cuerpo remoto.
    /// </summary>
    [Serializable]
    public sealed class HandGripTarget
    {
        public HandRole role = HandRole.Grip;

        [Tooltip("Dónde cierra el puño a lo largo del eje: 0 = culata, 1 = punta.")]
        [Range(0f, 1f)] public float alongAxis = 0.5f;

        [Tooltip("En qué lado del eje queda la PALMA, en grados alrededor del eje (0 = +Z del objeto, 90 = +X).")]
        public float clockDegrees;

        [Tooltip("Inclinación de la línea de nudillos respecto del eje. Un agarre de fuerza real va oblicuo, 10–25°.")]
        public float tiltDegrees = 15f;

        [Tooltip("El índice hacia la punta del objeto (agarre de martillo normal). Falso = hacia la culata.")]
        public bool indexTowardTip = true;

        [Tooltip("Corrección fina del punto de agarre, en metros y en espacio del objeto.")]
        public Vector3 offsetMeters;

        [Tooltip("Separación extra de la palma respecto de la piel, en metros (+ = más fuera).")]
        public float palmOffsetMeters;

        [Tooltip("Peso del IK: 0 = la mano del clip base, 1 = la mano en su objetivo.")]
        [Range(0f, 1f)] public float weight = 1f;

        public HandFingerStyle fingers = HandFingerStyle.Wrap;

        [Header("Pieza que agarra ESTA mano (vacío = la malla de agarre del perfil)")]
        [Tooltip("Hijo del modelo que agarra esta mano, p. ej. 'Crank' para el pomo de la manivela.")]
        public string gripPartNodeName;

        [Tooltip("Eje del agarre en local de esa pieza (el pomo gira sobre +Z). Cero = +Y.")]
        public Vector3 gripPartAxis;

        [Tooltip("Qué tramo de la pieza se agarra, por su Y local normalizada: 0,75–1 = el cuarto de arriba (el pomo).")]
        [Range(0f, 1f)] public float gripPartMinY01;
        [Range(0f, 1f)] public float gripPartMaxY01 = 1f;

        [Tooltip("Sólo con role = Reference: el clip del que se copia la pose de esta mano.")]
        public string referenceClipPath;

        [Tooltip("Sólo con role = Reference: en qué segundo de ese clip.")]
        public float referenceTime;

        [Tooltip("Busca reloj, inclinación y separación de la palma alrededor de los valores dados, puntuando naturalidad.")]
        public bool autoSearch = true;

        [Tooltip("Semiancho del barrido a lo largo del eje, en fracción del largo. En un cuerpo corto dos puños no caben donde se pide: que lo decida la medida.")]
        [Range(0f, 0.5f)] public float searchAlongRange = 0.25f;

        [Tooltip("Semiancho del barrido del reloj, en grados.")]
        [Range(0f, 180f)] public float searchClockRange = 180f;

        [Tooltip("Semiancho del barrido de la inclinación, en grados.")]
        [Range(0f, 45f)] public float searchTiltRange = 20f;

        [Header("Resuelto por el último horneado o previsualización (sólo lectura)")]
        public bool resolved;
        public float resolvedAlongAxis;
        public bool resolvedIndexTowardTip = true;
        public float resolvedClockDegrees;
        public float resolvedTiltDegrees;
        public float resolvedPalmOffsetMeters;
        public float resolvedCost;
        public Vector3 resolvedShoulderShift;
        public int resolvedPole;
    }

    /// <summary>
    /// Perfil de interacción mano-objeto de un wieldable. Lo crea y lo hornea la herramienta de editor
    /// <c>Tools ▸ Interaction Authoring</c> (o su API JSON); el resultado persistente son los clips del
    /// wieldable y el nodo del modelo colgando de <c>Hand.R</c>. Este asset guarda las DECISIONES
    /// (dónde agarra cada mano) para poder rehornear, corregir y validar sin repetir nada a mano.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Hand Interaction Profile", fileName = "HandInteractionProfile")]
    public sealed class HandInteractionProfile : ScriptableObject
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;

        [Tooltip("El prefab del wieldable (con los brazos de primera persona dentro).")]
        public GameObject wieldablePrefab;

        [Tooltip("Nodo del modelo del objeto dentro del wieldable.")]
        public string modelNodeName;

        [Tooltip("Hijo con la malla que se agarra (su +Y es el eje). Vacío = la primera malla del nodo.")]
        public string gripMeshNodeName;

        public HandInteractionKind kind = HandInteractionKind.OneHand;

        public HandGripTarget rightHand = new() { role = HandRole.Keep };
        public HandGripTarget leftHand = new() { role = HandRole.Relaxed };

        [Header("Mezcla con la animación base")]
        [Tooltip("La mano secundaria agarra con peso completo mientras el objeto está a menos de esto de su sitio en el idle, en metros.")]
        public float secondaryFullWeightDistance = 0.04f;

        [Tooltip("A partir de esta distancia al sitio del idle la mano secundaria vuelve entera al clip base (equipar, enfundar).")]
        public float secondaryZeroWeightDistance = 0.22f;

        [Tooltip("Cuánto puede adelantarse un hombro para alcanzar su objetivo, en metros. Un rig de brazos 1P no tiene torso visible.")]
        public float maxShoulderShiftMeters = 0.12f;

        [Tooltip("Coste de naturalidad por encima del cual NO se hornea (salvo force): la mejor pose existe, pero no es natural.")]
        public float maxNaturalCost = 6f;

        [Header("Pieza que gira (la manivela)")]
        [Tooltip("Hijo del modelo que gira en runtime: ninguna mano que se resuelva de nuevo puede entrar en su órbita.")]
        public string sweptPartNodeName;

        [Tooltip("Eje de giro en local de esa pieza (la manivela gira sobre +Z).")]
        public Vector3 sweptPartAxis = Vector3.forward;

        [Tooltip("Holgura mínima entre la pieza girando y el hueso de cualquier falange, en metros (piel de un dedo).")]
        public float sweptClearanceMeters = 0.0085f;

        [Tooltip("Qué tramo de la pieza que gira cuenta, por su Y local normalizada: 0,75–1 = sólo el pomo. Con la vuelta " +
                 "completa del BRAZO de la manivela no queda ningún agarre natural del cuerpo (medido); el criterio del " +
                 "proyecto es el pomo (TheKnobOrbitClearsTheRightHand).")]
        [Range(0f, 1f)] public float sweptPartMinY01;
        [Range(0f, 1f)] public float sweptPartMaxY01 = 1f;

        [Header("Salida")]
        [Tooltip("Carpeta de los clips base copiados y de los que se crean nuevos.")]
        public string outputFolder;

        [Tooltip("Copias intactas de los clips que había antes del primer horneado: la fuente de todo rehorneado.")]
        public AnimationClip[] baseClips = Array.Empty<AnimationClip>();

        [Tooltip("Los clips que escribió el último horneado, en el mismo orden que baseClips.")]
        public AnimationClip[] bakedClips = Array.Empty<AnimationClip>();

        [Tooltip("Offset del nodo del modelo bajo la portadora ANTES del primer Regrip. Es entrada de la búsqueda (el " +
                 "objeto va donde el clip base + este offset lo ponen) y un Regrip lo reescribe en el prefab: sin esta copia " +
                 "el siguiente horneado buscaría con el objeto en otro sitio.")]
        public bool hasBaseNodeLocal;
        public Vector3 baseNodeLocalPosition;
        public Quaternion baseNodeLocalRotation = Quaternion.identity;

        [Header("Estado")]
        public string lastBakeUtc;

        [Tooltip("Rotación del objeto respecto de la vista del jugador en el idle (x derecha, y arriba, z delante). La usa «nudge».")]
        public Quaternion resolvedObjectRotationInView = Quaternion.identity;

        public bool hasResolvedView;

        [TextArea(2, 8)] public string notes;

        public HandGripTarget Hand(bool right) => right ? rightHand : leftHand;
    }
}
