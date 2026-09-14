using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// El libro de supervivencia abierto en las manos del vecino (bit <c>BookOpen</c>, 2026-09-14). Aritmética pura
    /// para los tests EditMode, mismo motivo que <see cref="ProxyGripSolver"/>: el hook vive en Assembly-CSharp.
    ///
    /// Marco del libro (lo fija ProxyOpenBookBuilder al hornear la malla): origen en el centro del libro abierto,
    /// +Z hacia el LECTOR (el lado de las páginas), +Y hacia el borde de arriba de la página y +X de una tapa a la otra.
    ///
    /// Cómo se lleva: delante del pecho y por debajo de los hombros, con las páginas mirando a los ojos, y cada mano
    /// por DETRÁS de su tapa cerca del canto de fuera — palma contra la tapa, dedos hacia el lomo y el pulgar asomando
    /// por el canto hacia la página.
    /// </summary>
    public static class ProxyBookHold
    {
        /// <summary>Cabeceo con el que el vecino lee: la cabeza lo sigue al 80 % (<see cref="ProxyHeadPitchSolver"/>) y
        /// los ojos ponen el resto hasta el libro.</summary>
        public const float ReadingPitchDegrees = 45f;

        /// <summary>Centro del libro por debajo de la línea de hombros (m).</summary>
        public const float BelowShoulders = 0.24f;

        /// <summary>Centro del libro por delante de la línea de hombros (m).</summary>
        public const float InFront = 0.30f;

        /// <summary>Ojos por encima de la línea de hombros (m): hacia ellos miran las páginas.</summary>
        public const float EyesAboveShoulders = 0.17f;

        /// <summary>Ojos por delante de la línea de hombros (m).</summary>
        public const float EyesInFront = 0.08f;

        /// <summary>El codo, por lo menos esto por debajo de la muñeca (m): con el libro delante del pecho el antebrazo
        /// SUBE hacia la tapa.</summary>
        public const float ElbowBelowWrist = 0.08f;

        /// <summary>El codo, como mucho esto hacia fuera del hombro (m).</summary>
        public const float ElbowOutward = 0.10f;

        /// <summary>
        /// La tapa de atrás, medida: abierto, el libro hace V y la tapa no es plana. Recta z = <see cref="BackZAtSpine"/> +
        /// <see cref="BackSlope"/>·|x| en el marco del libro (z negativa = detrás de las páginas).
        /// </summary>
        public struct Cover
        {
            public float HalfWidth;
            public float HalfHeight;
            public float BackZAtSpine;
            public float BackSlope;
            /// <summary>La cara de las páginas, igual: z = FrontZAtSpine + FrontSlope·|x|.</summary>
            public float FrontZAtSpine;
            public float FrontSlope;
        }

        public struct EdgeGrip
        {
            /// <summary>Dónde van los nudillos: detrás de la tapa, dentro del canto.</summary>
            public Vector3 Knuckles;
            /// <summary>Hacia dónde mira la palma: contra la tapa, que es hacia el lector.</summary>
            public Vector3 Palm;
            /// <summary>Hacia dónde apuntan los dedos: hacia el lomo, girados sobre la palma.</summary>
            public Vector3 Fingers;
        }

        /// <summary>Pose en mundo del libro abierto a partir de los dos hombros y la orientación del cuerpo.</summary>
        public static void BookPose(Vector3 shoulderLeft, Vector3 shoulderRight, Vector3 rootForward, Vector3 rootUp,
            out Vector3 centre, out Quaternion rotation)
        {
            Vector3 mid = (shoulderLeft + shoulderRight) * 0.5f;
            centre = mid - rootUp * BelowShoulders + rootForward * InFront;
            Vector3 eyes = mid + rootUp * EyesAboveShoulders + rootForward * EyesInFront;
            Vector3 toReader = (eyes - centre).normalized;
            // El borde de arriba de la página, lo más hacia arriba que deja el plano del libro: se aleja del lector.
            Vector3 pageUp = Vector3.ProjectOnPlane(rootUp, toReader);
            if (pageUp.sqrMagnitude < 1e-8f)
                pageUp = Vector3.ProjectOnPlane(rootForward, toReader);
            rotation = Quaternion.LookRotation(toReader, pageUp.normalized);
        }

        /// <summary>Hacia qué canto va cada mano: el eje de tapa a tapa, del lado del cuerpo de esa mano.</summary>
        public static Vector3 SideAxis(Quaternion bookRotation, Vector3 rootRight, bool right)
        {
            Vector3 across = bookRotation * Vector3.right;
            if (Vector3.Dot(across, rootRight) < 0f)
                across = -across;
            return right ? across : -across;
        }

        /// <summary>
        /// Mide la tapa de atrás sobre los vértices del libro horneado (en su marco): por cada franja de |x| entre el
        /// 25 % y el 100 % del medio ancho, la z más atrasada, y una recta por mínimos cuadrados. El lomo, más grueso,
        /// queda fuera. Sin franjas suficientes, tapa plana en la z más atrasada.
        /// </summary>
        public static Cover MeasureCover(Vector3[] vertices)
        {
            const int Bins = 8;
            var cover = new Cover();
            if (vertices == null || vertices.Length == 0)
                return cover;

            float half = 0f, halfHeight = 0f, deepest = float.MaxValue, highest = float.MinValue;
            foreach (var v in vertices)
            {
                half = Mathf.Max(half, Mathf.Abs(v.x));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(v.y));
                deepest = Mathf.Min(deepest, v.z);
                highest = Mathf.Max(highest, v.z);
            }
            cover.HalfWidth = half;
            cover.HalfHeight = halfHeight;
            cover.BackZAtSpine = deepest;
            cover.FrontZAtSpine = highest;
            if (half < 1e-4f)
                return cover;

            var back = new float[Bins];
            var front = new float[Bins];
            for (int i = 0; i < Bins; i++)
            {
                back[i] = float.MaxValue;
                front[i] = float.MinValue;
            }
            float from = half * 0.25f, span = half - from;
            foreach (var v in vertices)
            {
                float ax = Mathf.Abs(v.x);
                if (ax < from)
                    continue;
                int bin = Mathf.Min(Bins - 1, (int)((ax - from) / span * Bins));
                back[bin] = Mathf.Min(back[bin], v.z);
                front[bin] = Mathf.Max(front[bin], v.z);
            }

            if (FitLine(back, from, span, out float backZ, out float backSlope))
            {
                cover.BackZAtSpine = backZ;
                cover.BackSlope = backSlope;
            }
            if (FitLine(front, from, span, out float frontZ, out float frontSlope))
            {
                cover.FrontZAtSpine = frontZ;
                cover.FrontSlope = frontSlope;
            }
            return cover;
        }

        // Recta z = a + b·x por mínimos cuadrados sobre las franjas con dato.
        private static bool FitLine(float[] bins, float from, float span, out float a, out float b)
        {
            a = b = 0f;
            float n = 0f, sx = 0f, sz = 0f, sxx = 0f, sxz = 0f;
            for (int i = 0; i < bins.Length; i++)
            {
                if (bins[i] == float.MaxValue || bins[i] == float.MinValue)
                    continue;
                float x = from + span * (i + 0.5f) / bins.Length;
                n++;
                sx += x;
                sz += bins[i];
                sxx += x * x;
                sxz += x * bins[i];
            }
            float det = n * sxx - sx * sx;
            if (n < 2f || Mathf.Abs(det) < 1e-10f)
                return false;
            b = (n * sxz - sx * sz) / det;
            a = (sz - b * sx) / n;
            return true;
        }

        /// <summary>
        /// Agarre de un canto: nudillos <paramref name="inset"/> por dentro del canto de fuera y <paramref name="along"/>
        /// a lo alto de la página, a una piel por detrás de la tapa; palma contra la tapa y dedos hacia el lomo POR LA
        /// TAPA, girados <paramref name="spreadDegrees"/> sobre la palma. Con la tapa inclinada (el libro en V) la palma
        /// y los dedos se inclinan con ella: dedos rectos hacia el lomo atravesaban la tapa y asomaban por las páginas
        /// (captura 14-09).
        /// </summary>
        public static EdgeGrip Grip(Vector3 centre, Quaternion bookRotation, Vector3 side, Cover cover,
            float inset, float along, float spreadDegrees)
        {
            Vector3 toReader = bookRotation * Vector3.forward;
            Vector3 pageUp = bookRotation * Vector3.up;
            float x = cover.HalfWidth - inset;
            float z = cover.BackZAtSpine + cover.BackSlope * x;
            Vector3 palm = (toReader - side * cover.BackSlope).normalized;
            Vector3 inward = -(side + toReader * cover.BackSlope).normalized;
            return new EdgeGrip
            {
                Knuckles = centre + side * x + pageUp * along + toReader * z - palm * KnuckleBehindMetres,
                Palm = palm,
                Fingers = Quaternion.AngleAxis(spreadDegrees, palm) * inward,
            };
        }

        /// <summary>
        /// Cuánto se mete un punto de la mano en el libro (m): positivo si está dentro del contorno de las páginas y por
        /// delante de la tapa de atrás; negativo si queda detrás de la tapa o fuera del contorno (menos lo que le falta
        /// para entrar).
        ///
        /// POR QUÉ: con los dedos ya detrás de la tapa, agachado la captura (14-09) enseñaba el dorso de la mano derecha
        /// sobre la página. Agarrando lejos del canto, la muñeca y la base del pulgar —que sobresale hacia la palma, o sea
        /// hacia la tapa— quedaban dentro del contorno y atravesaban el libro. Con la muñeca fuera del canto el pulgar lo
        /// rodea, que es como se sujeta un libro.
        /// </summary>
        public static float PierceDepth(Vector3 point, Vector3 centre, Quaternion bookRotation, Cover cover)
        {
            Vector3 local = Quaternion.Inverse(bookRotation) * (point - centre);
            float ax = Mathf.Abs(local.x);
            float outside = Mathf.Max(ax - cover.HalfWidth, Mathf.Abs(local.y) - cover.HalfHeight);
            if (outside > 0f)
                return -outside;
            return local.z - (cover.BackZAtSpine + cover.BackSlope * ax);
        }

        /// <summary>
        /// Lateralidad de una mano medida sobre su marco: +1 si la línea de nudillos es Cross(dedos, palma), −1 en la
        /// mano espejo. Así no se escribe qué mano es cuál, se mide (lección de los agarres de primera persona).
        /// </summary>
        public static float Chirality(ProxyGripSolver.HandFrame frame) =>
            Vector3.Dot(Vector3.Cross(frame.FingerAxis, frame.PalmNormal), frame.KnuckleAxis) >= 0f ? 1f : -1f;

        /// <summary>La línea de nudillos (meñique→índice) que corresponde a unos dedos y una palma en esa mano.</summary>
        public static Vector3 KnuckleAxis(Vector3 fingers, Vector3 palm, float chirality) =>
            (Vector3.Cross(fingers, palm) * chirality).normalized;

        /// <summary>
        /// Coste del codo con el libro, SUMADO al de <see cref="ProxyArmNaturalness"/>: codo por encima de la muñeca y codo
        /// abierto. La primera captura (14-09) sacó «alas de pollo»: muñecas por fuera de los cantos, antebrazos
        /// horizontales y codos a la altura de las manos, con la muñeca cómoda — la medida de naturalidad no lo veía.
        ///
        /// En tercera persona el «codo-alto» se quitó porque una mano BAJA lleva el codo por encima de la muñeca por pura
        /// anatomía. Aquí las manos están levantadas delante del pecho, que es el caso de primera persona: vuelve a valer.
        /// </summary>
        public static float ElbowCost(Vector3 shoulder, Vector3 elbow, Vector3 wrist, Vector3 rootRight, Vector3 rootUp, bool right)
        {
            float overWrist = Vector3.Dot(elbow - wrist, rootUp);
            float outward = Vector3.Dot(elbow - shoulder, right ? rootRight : -rootRight);
            return Sq(Mathf.Max(0f, overWrist + ElbowBelowWrist) / 0.05f)
                 + Sq(Mathf.Max(0f, outward - ElbowOutward) / 0.05f);
        }

        /// <summary>Las yemas se quedan por lo menos esto antes del lomo (m): en la parte de FUERA de su tapa. Era 0,03 y los
        /// dedos cubrían media tapa hasta el lomo — de frente, las manos se veían superpuestas al libro (Joel, 14-09).</summary>
        public const float SpineMargin = 0.07f;

        /// <summary>Nudillos por detrás de la tapa (m): la carne del nudillo y el arco de la palma, no la piel de un agarre de
        /// linterna. Con 16 mm la malla medida aún metía en la tapa las falanges proximales (sobre todo el índice junto al
        /// canto) y algo de palma (arnés por hueso, 14-09).</summary>
        public const float KnuckleBehindMetres = 0.021f;

        /// <summary>Carne de la palma por delante del plano de los metacarpianos (m): lo que asoma si la palma roza la tapa.</summary>
        public const float PalmFleshMetres = 0.015f;

        /// <summary>
        /// Coste de que una yema pase del lomo hacia la otra mitad del libro. Vista de frente a las tapas (captura 14-09):
        /// con los dedos hacia el lomo y 8 cm de dedo en 15 cm de medio libro, los de las dos manos se cruzaban detrás.
        /// </summary>
        public static float SpineCost(Vector3 fingertip, Vector3 centre, Vector3 side) =>
            Sq(Mathf.Max(0f, SpineMargin - Vector3.Dot(fingertip - centre, side)) / 0.02f);

        /// <summary>Holgura de cada articulación y de la yema por detrás de la tapa (m): el GROSOR del dedo. Con 4 mm los huesos
        /// quedaban detrás pero la malla medida del dedo metía 55–105 vértices por mano en la tapa (arnés, 14-09).</summary>
        public const float FingerClearance = 0.011f;

        /// <summary>Tope de lo que se abre un dedo: pasado esto la cadena ya estaría hiperextendida.</summary>
        public const float MaxOpenDegrees = 60f;

        /// <summary>
        /// Abre una cadena de falanges sobre <paramref name="axisWorld"/> hasta que todas sus articulaciones y la yema quedan
        /// por detrás del plano de la tapa. Deja la cadena abierta y devuelve el ángulo aplicado con su signo.
        ///
        /// POR QUÉ: el clip de reposo trae los dedos a medio cerrar HACIA LA PALMA, que aquí es hacia la tapa. Con la mano
        /// bien puesta detrás del libro, las yemas atravesaban la tapa y salían por las páginas (captura 14-09). El signo de
        /// abrir se mide (el que aleja la yema del plano), no se supone.
        /// </summary>
        public static float OpenBehindPlane(Transform[] chain, Vector3 axisWorld, Vector3 planePoint, Vector3 planeNormal,
            float clearance)
        {
            if (chain == null || chain.Length < 2 || chain[chain.Length - 1] == null || chain[chain.Length - 2] == null)
                return 0f;

            // Se prueban LOS DOS sentidos hasta el final y se queda el que deja la cadena detrás con menos giro (o, si ninguno
            // lo logra, el que menos asoma). Con un tanteo de 10° el signo salía mal cuando la yema se movía casi paralela a
            // la tapa, y el dedo se CERRABA 60° contra ella: agachado asomaba 11 mm por la página (captura 14-09).
            float bestApplied = 0f;
            float bestFront = MaxInFront(chain, planePoint, planeNormal);
            foreach (float sign in Signs)
            {
                float applied = 0f;
                while (AnyInFront(chain, planePoint, planeNormal, clearance) && Mathf.Abs(applied) < MaxOpenDegrees - 1e-3f)
                {
                    ProxyGripSolver.Curl(chain, axisWorld, sign * ProxyGripSolver.CurlStepDegrees);
                    applied += sign * ProxyGripSolver.CurlStepDegrees;
                }
                float front = MaxInFront(chain, planePoint, planeNormal);
                bool clear = front <= -clearance, bestClear = bestFront <= -clearance;
                if ((clear && !bestClear) || (clear == bestClear && (clear ? Mathf.Abs(applied) < Mathf.Abs(bestApplied) : front < bestFront)))
                {
                    bestApplied = applied;
                    bestFront = front;
                }
                ProxyGripSolver.Curl(chain, axisWorld, -applied);
            }
            ProxyGripSolver.Curl(chain, axisWorld, bestApplied);
            return bestApplied;
        }

        private static readonly float[] Signs = { 1f, -1f };

        /// <summary>La yema del pulgar se apoya en la página a esta distancia del canto de fuera (m).</summary>
        public const float ThumbInsetMetres = 0.03f;

        /// <summary>Hueso de la yema del pulgar por delante de la cara de las páginas (m): la carne de la yema y nada más, para
        /// que se vea apoyada sin hundirse. Con 11 mm la malla de la falange distal se hundía en la página (30–43 vértices).</summary>
        public const float ThumbAbovePageMetres = 0.015f;

        /// <summary>Tope y paso del barrido del pulgar sobre cada eje.</summary>
        // 70° dejaba un pulgar RÍGIDO (base sola, sin flexión propia) libre de girar hasta apuntar hacia arriba, como un
        // cuerno perpendicular a la página, cuando eso era lo que más acercaba la yema al objetivo (Joel, 14-09: «el
        // pulgar sigue muy raro»). 40° más el coste de <see cref="PageUpCost"/> lo evita sin volver a la flexión por
        // falange (eso fue lo que lo retorcía en tornillo, el problema anterior).
        public const float MaxThumbDegrees = 40f;
        public const float ThumbStepDegrees = 5f;

        /// <summary>
        /// Coste (metros equivalentes) de que el pulgar apunte FUERA del plano del libro. Un pulgar que sujeta un canto
        /// se tumba casi en el plano de la tapa; uno perpendicular a ella (apuntando hacia el lector o alejándose) se ve
        /// como un cuerno. <paramref name="tipDirection"/> es la dirección actual de la base a la yema, en mundo.
        /// </summary>
        public static float PageUpCost(Vector3 tipDirection, Quaternion bookRotation)
        {
            float outOfPlane = Vector3.Dot(tipDirection.normalized, bookRotation * Vector3.forward);
            return 0.05f * outOfPlane * outOfPlane;
        }

        /// <summary>
        /// Lleva la yema de una cadena lo más cerca posible de <paramref name="target"/> girándola sobre DOS ejes (barrido en
        /// rejilla, cada falange el mismo ángulo). Deja la cadena puesta y devuelve los dos ángulos; a igual distancia, el de
        /// menos giro.
        ///
        /// POR QUÉ DOS: el pulgar sólo cerrado sobre el eje de los dedos (hacia el lomo) se mueve en el plano de la palma y la
        /// altura de la página, nunca hacia DENTRO del canto: la yema quedaba delante del plano de las páginas pero fuera del
        /// libro, y de frente a las páginas se veía un bulto de carne en el canto en vez de un pulgar encima (14-09).
        /// </summary>
        public static void ReachWithTip(Transform[] chain, Vector3 axisA, Vector3 axisB, Vector3 target, float maxDegrees,
            float stepDegrees, out float degreesA, out float degreesB, System.Func<float> extraCost = null)
        {
            degreesA = degreesB = 0f;
            if (chain == null || chain.Length < 2 || chain[chain.Length - 1] == null || chain[chain.Length - 2] == null
                || stepDegrees <= 0f)
                return;

            var rest = new Quaternion[chain.Length];
            for (int i = 0; i < chain.Length; i++)
                rest[i] = chain[i] != null ? chain[i].localRotation : Quaternion.identity;

            float best = float.MaxValue, bestA = 0f, bestB = 0f;
            // Rejilla gruesa y luego fina alrededor de la mejor: 5° de paso son 7 mm en la yema de un pulgar.
            Sweep(-maxDegrees, maxDegrees, -maxDegrees, maxDegrees, stepDegrees);
            float fine = stepDegrees / 5f;
            Sweep(bestA - stepDegrees, bestA + stepDegrees, bestB - stepDegrees, bestB + stepDegrees, fine);

            Apply(bestA, bestB);
            degreesA = bestA;
            degreesB = bestB;

            // Una función local no puede tocar los parámetros out: trabaja sobre bestA/bestB.
            void Sweep(float fromA, float toA, float fromB, float toB, float step)
            {
                for (float a = fromA; a <= toA + 1e-3f; a += step)
                for (float b = fromB; b <= toB + 1e-3f; b += step)
                {
                    if (Mathf.Abs(a) > maxDegrees + 1e-3f || Mathf.Abs(b) > maxDegrees + 1e-3f)
                        continue;
                    Apply(a, b);
                    // Regularización: a igualdad de acierto, el giro MENOR. Sin esto, un giro grande que acerca la yema
                    // unos milímetros gana aunque deje el dedo apuntando a cualquier lado — el pulgar salía como un
                    // cuerno perpendicular a la página (Joel, 14-09). Peso pensado para que haga falta ~3 cm de mejora
                    // por cada 45° de más (45²·RegWeight ≈ 0,03).
                    const float regWeight = 1.5e-5f;
                    float score = Vector3.Distance(ProxyGripSolver.Tip(chain), target) + (extraCost != null ? extraCost() : 0f)
                        + regWeight * (a * a + b * b);
                    bool better = score < best - 1e-5f;
                    bool tieWithLessTurn = Mathf.Abs(score - best) <= 1e-5f
                        && Mathf.Abs(a) + Mathf.Abs(b) < Mathf.Abs(bestA) + Mathf.Abs(bestB);
                    if (!better && !tieWithLessTurn)
                        continue;
                    best = score;
                    bestA = a;
                    bestB = b;
                }
            }

            // LOS DOS ejes SOLO en la BASE: es la articulación que orienta el pulgar entero (como el pulgar de una mano
            // real, que apunta desde la base y el resto lo sigue casi recto). El resto de la cadena queda SOLIDARIO,
            // sin flexión propia. Aplicar los ejes progresivamente a las tres falanges (como antes, con Curl) las
            // retorcía en tornillo: con un barrido de hasta 70° por eje, la flexión sola se acumulaba hasta 3 veces de
            // nudillo a yema — más de 200° en total (Joel, 14-09: «los pulgares se ven como rotados mal»; primer
            // intento, aplicar sólo axisB así, no bastó: seguía acumulando axisA).
            void Apply(float a, float b)
            {
                Restore(chain, rest);
                ApplyRigidReach(chain, axisA, axisB, a, b);
            }
        }

        /// <summary>
        /// Pone la cadena en los ángulos ya resueltos por <see cref="ReachWithTip"/>: los DOS ejes SOLO en la base, sin
        /// tocar el resto. Público porque el hook lo reaplica cada fotograma sobre la pose que deja el Animator — con el
        /// <c>ProxyGripSolver.Curl</c> normal (progresivo por toda la cadena) el pulgar se retorcía en tornillo cada
        /// fotograma, aunque <see cref="ReachWithTip"/> ya lo hubiera resuelto bien la primera vez (Joel, 14-09).
        /// </summary>
        public static void ApplyRigidReach(Transform[] chain, Vector3 axisA, Vector3 axisB, float degreesA, float degreesB)
        {
            RotateBase(chain, axisA, degreesA);
            RotateBase(chain, axisB, degreesB);
        }

        /// <summary>Gira SOLO la primera falange de la cadena (rotación de mundo): la base orienta el dedo entero y el
        /// resto la sigue por jerarquía, sin retorcerse cada una por su cuenta.</summary>
        private static void RotateBase(Transform[] chain, Vector3 axisWorld, float degrees)
        {
            if (chain == null || chain.Length == 0 || chain[0] == null || Mathf.Approximately(degrees, 0f))
                return;
            chain[0].rotation = Quaternion.AngleAxis(degrees, axisWorld) * chain[0].rotation;
        }

        /// <summary>Carne del pulgar alrededor de sus articulaciones (m). 9 mm se quedaba corto para la yema (arnés, 14-09).</summary>
        public const float ThumbFleshMetres = 0.013f;

        /// <summary>
        /// Cuánto se mete un punto en el GROSOR del libro (m): positivo dentro del contorno y entre la tapa de atrás y la cara
        /// de las páginas; negativo fuera. A diferencia de <see cref="PierceDepth"/>, un punto por delante de las páginas (un
        /// pulgar apoyado) no cuenta.
        /// </summary>
        public static float InsideDepth(Vector3 point, Vector3 centre, Quaternion bookRotation, Cover cover)
        {
            Vector3 local = Quaternion.Inverse(bookRotation) * (point - centre);
            float ax = Mathf.Abs(local.x);
            float outside = Mathf.Max(ax - cover.HalfWidth, Mathf.Abs(local.y) - cover.HalfHeight);
            float fromBack = local.z - (cover.BackZAtSpine + cover.BackSlope * ax);
            float toFront = cover.FrontZAtSpine + cover.FrontSlope * ax - local.z;
            return Mathf.Min(-outside, Mathf.Min(fromBack, toFront));
        }

        /// <summary>
        /// Coste (en metros equivalentes, para sumar a la distancia de la yema) de que el pulgar atraviese el grosor del libro
        /// al rodear el canto: articulaciones y puntos medios de cada falange. De frente a las páginas, agachado, se veían
        /// parches de carne recortados en los cantos (14-09): el pulgar tiene que dar la vuelta por FUERA.
        /// </summary>
        public static float ThumbInsideCost(Transform[] chain, Vector3 centre, Quaternion bookRotation, Cover cover)
        {
            float cost = 0f;
            for (int i = 1; i < chain.Length; i++)
            {
                if (chain[i] == null || chain[i - 1] == null)
                    continue;
                cost += Mathf.Max(0f, InsideDepth(chain[i].position, centre, bookRotation, cover) + ThumbFleshMetres);
                cost += Mathf.Max(0f, InsideDepth((chain[i].position + chain[i - 1].position) * 0.5f, centre, bookRotation, cover)
                    + ThumbFleshMetres);
            }
            Vector3 tip = ProxyGripSolver.Tip(chain);
            Vector3 last = chain[chain.Length - 1].position;
            cost += Mathf.Max(0f, InsideDepth((tip + last) * 0.5f, centre, bookRotation, cover) + ThumbFleshMetres);
            // Peso 10: con 3, un centímetro de yema más cerca del sitio compensaba 3 mm de pulgar atravesando el canto, y la
            // malla medida metía 18–42 vértices del pulgar en el libro (arnés, 14-09).
            return 10f * cost;
        }

        private static void Restore(Transform[] chain, Quaternion[] rest)
        {
            for (int i = 0; i < chain.Length; i++)
                if (chain[i] != null)
                    chain[i].localRotation = rest[i];
        }

        private static bool AnyInFront(Transform[] chain, Vector3 planePoint, Vector3 planeNormal, float clearance) =>
            MaxInFront(chain, planePoint, planeNormal) > -clearance;

        /// <summary>Lo más adelantado de una cadena respecto de un plano (m, positivo = por delante): articulaciones y yema.</summary>
        public static float MaxInFront(Transform[] chain, Vector3 planePoint, Vector3 planeNormal)
        {
            float front = float.MinValue;
            if (chain == null || chain.Length < 2 || chain[chain.Length - 1] == null || chain[chain.Length - 2] == null)
                return front;
            for (int i = 1; i < chain.Length; i++)
                if (chain[i] != null)
                    front = Mathf.Max(front, Vector3.Dot(chain[i].position - planePoint, planeNormal));
            return Mathf.Max(front, Vector3.Dot(ProxyGripSolver.Tip(chain) - planePoint, planeNormal));
        }

        private static float Sq(float x) => x * x;
    }
}
