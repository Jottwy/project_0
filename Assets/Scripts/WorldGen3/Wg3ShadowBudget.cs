using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// R4 (2026-09-12, con autorización explícita de Joel) — tope GLOBAL de luces con
    /// <c>LightShadows.Soft</c> en todo el mundo cargado, no por tramo.
    /// </summary>
    /// <remarks>
    /// # El problema que esto cierra
    ///
    /// <see cref="Wg3SceneAssembler"/> ya limita la sombra a UNA lámpara por tramo grande
    /// (<c>ShadowMinSide</c>, ver <c>AddSegmentLights</c>): eso evita que una nave entera sombree,
    /// pero no dice nada sobre CUÁNTOS tramos grandes puede haber cargados a la vez alrededor del
    /// jugador. La auditoría de rendimiento (`docs/perf/PERF_AUDIT_v1.md`) midió **6-8 lámparas con
    /// sombra suave simultáneas** en el radio de streaming, forzando el atlas de sombras
    /// (`PC_RPAsset`, 4096) a repartirse en mapas de 2048 —el aviso de Unity en el log— y su
    /// veredicto explícito es «IMPLEMENTAR el tope de luces con sombra (2 por escena, por
    /// distancia)».
    ///
    /// # Por qué un presupuesto dinámico y no bajar `ShadowMinSide`
    ///
    /// Subir el umbral local reduce cuántos TRAMOS califican, pero no acota el peor caso: con
    /// suficientes naves grandes cerca seguiría habiendo más de dos sombras a la vez, y de paso le
    /// quita sombra a salas medianas que hoy se benefician de ella sin que el jugador esté cerca de
    /// ninguna otra. El límite real es geométrico —cuántas hay CERCA del jugador ahora mismo—, así
    /// que se resuelve con un ranking por distancia, no con un número más grande.
    ///
    /// # Cómo funciona
    ///
    /// Cada luz que <c>AddSegmentLights</c> elegiría como sombreadora de su tramo se REGISTRA aquí
    /// en vez de encender <c>LightShadows.Soft</c> directamente. Cada <see cref="RerankInterval"/>
    /// segundos, este director poda las que ya murieron con su chunk, ordena las vivas por distancia
    /// al oyente y deja <c>Soft</c> sólo en las <see cref="MaxShadowCasters"/> más cercanas —el
    /// resto, <c>None</c>. <c>shadowStrength</c>/<c>shadowNearPlane</c> los fija
    /// <c>AddSegmentLights</c> UNA vez al crear la luz, con sombra o sin ella: Unity los ignora
    /// mientras no hay sombra, así que están listos en cuanto el presupuesto la reclame.
    /// </remarks>
    public sealed class Wg3ShadowBudget : MonoBehaviour
    {
        /// <summary>Cuántas <c>Light</c> pueden llevar sombra suave a la vez EN TODO EL MUNDO
        /// cargado. El veredicto de la auditoría de rendimiento, no un número inventado.</summary>
        public const int MaxShadowCasters = 2;

        private const float RerankInterval = 0.5f;

        private readonly List<Light> _candidates = new List<Light>();
        // Reusada cada pasada para no asignar: el tamaño se estabiliza enseguida con el número de
        // tramos grandes cargados, que es pequeño.
        private readonly List<(float distSqr, Light light)> _ranked = new List<(float, Light)>();

        private static Wg3ShadowBudget _instance;
        private static bool _quitting;

        private Transform _listener;
        private float _listenerRetry;
        private float _timer;

        /// <summary>
        /// Apunta <paramref name="light"/> como candidata a sombra. Se deja en <c>None</c> hasta el
        /// próximo reparto: sin esto, toda luz recién montada nacería con sombra por defecto y el
        /// tope se desbordaría en el instante entre dos repartos.
        /// </summary>
        public static void Register(Light light)
        {
            if (light == null) return;
            Wg3ShadowBudget director = EnsureInstance();
            if (director == null) return;
            director._candidates.Add(light);
            light.shadows = LightShadows.None;
        }

        // ── Ciclo de vida ─────────────────────────────────────────────────────
        //
        // Mismo patrón que FluorescentHumDirector: vive en la escena activa (NO
        // DontDestroyOnLoad), auto-arranque perezoso desde el único llamante (el worldgen), y
        // ResetStatics porque "Enter Play Mode" sin recarga de dominio deja los estáticos vivos
        // de la sesión anterior.

        private static Wg3ShadowBudget EnsureInstance()
        {
            if (_instance != null) return _instance;
            if (_quitting) return null;
            var go = new GameObject("Wg3ShadowBudget");
            _instance = go.AddComponent<Wg3ShadowBudget>();
            return _instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _quitting = false;
            _instance = null;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void OnApplicationQuit() => _quitting = true;

        // ── Bucle ─────────────────────────────────────────────────────────────

        private void Update()
        {
            _timer -= Time.unscaledDeltaTime;
            if (_timer > 0f) return;
            _timer = RerankInterval;

            if (!ResolveListener()) return;
            Rerank();
        }

        private bool ResolveListener()
        {
            if (_listener != null) return true;
            _listenerRetry -= Time.unscaledDeltaTime;
            if (_listenerRetry > 0f) return false;
            _listenerRetry = 0.5f;
            var al = FindAnyObjectByType<AudioListener>();
            if (al != null) _listener = al.transform;
            return _listener != null;
        }

        /// <summary>Poda las candidatas muertas, ordena las vivas por distancia y reparte el
        /// presupuesto. Separado de <see cref="Rank"/> (que es puro y por eso testeable) para que
        /// esta parte, la que toca <c>Light</c> de verdad, se quede lo más fina posible.</summary>
        private void Rerank()
        {
            Vector3 ear = _listener.position;

            for (int i = _candidates.Count - 1; i >= 0; i--)
                if (_candidates[i] == null) _candidates.RemoveAt(i);

            _ranked.Clear();
            for (int i = 0; i < _candidates.Count; i++)
            {
                Light l = _candidates[i];
                _ranked.Add(((l.transform.position - ear).sqrMagnitude, l));
            }

            Rank(_ranked);

            for (int i = 0; i < _ranked.Count; i++)
                _ranked[i].light.shadows = i < MaxShadowCasters ? LightShadows.Soft : LightShadows.None;
        }

        /// <summary>
        /// Ordena <paramref name="ranked"/> por <c>distSqr</c> ascendente, IN PLACE. Pública y sin
        /// estado a propósito (mismo motivo que <c>AudioOcclusionMath</c>): es el único trozo de
        /// este sistema que un test puede ejercer sin un <c>AudioListener</c> ni luces de verdad en
        /// escena — el corte de a quién le toca sombra (los primeros <see cref="MaxShadowCasters"/>)
        /// vive en <see cref="Rerank"/>, no aquí.
        /// </summary>
        public static void Rank(List<(float distSqr, Light light)> ranked) =>
            ranked.Sort((a, b) => a.distSqr.CompareTo(b.distSqr));
    }
}
