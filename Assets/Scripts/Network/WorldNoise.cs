using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-090 — THE WORLD MAKES NOISE. The single gate through which gameplay actions that are not
    /// a gunshot or a voice report themselves to the AI: placing a piece, hammering material into one,
    /// demolishing one, dropping an item.
    ///
    /// Until this existed the only emitters of <c>report_noise</c> were <see cref="NoiseReporter"/>
    /// (weapons) and <c>VoiceCapture</c> (ADR-052). Building a base — the moment a player is most
    /// exposed and most invested — was perfectly silent to the creature. Now it is the noisiest
    /// thing short of a shot.
    ///
    /// LOUDNESS IS THE RADIUS (ADR-041): the backend treats the number as how far the sound carries.
    /// The table below sits deliberately between a footstep (~9 m, ADR-040's walk-hear radius) and
    /// the quietest weapon (25 m, the bow): building exposes you, shooting exposes you MORE, and the
    /// gradation is what makes the risk legible.
    ///
    /// Host and joiner alike go through <c>SendReportNoise</c>; a joiner's backend forwards it to the
    /// host on the <c>NoiseReport</c> lane (ADR-047), so the creature hears everybody's hammer.
    ///
    /// One call per gameplay action, placed NEXT TO the action's own <c>Send*</c>, so there is never
    /// a noise without an action nor an action without its noise. Nothing in gameplay may call
    /// <c>SendReportNoise</c> directly — this is the door.
    /// </summary>
    public static class WorldNoise
    {
        /// <summary>Placing a building piece (30 m).</summary>
        public const float PlaceLoudness = 30f;

        /// <summary>One batch of hammer blows adding material to a piece (25 m).</summary>
        public const float HammerLoudness = 25f;

        /// <summary>Tearing a piece down (35 m): the loudest thing a builder does.</summary>
        public const float DemolishLoudness = 35f;

        /// <summary>An item hitting the floor (12 m): more than a step, less than a tool.</summary>
        public const float DropLoudness = 12f;

        /// <summary>
        /// ADR-133: una vuelta de la manivela de la linterna (10 m). Entre el paso (~9 m) y el
        /// objeto que cae, y muy por debajo del arma más silenciosa (25 m): dar cuerda te delata en
        /// el pasillo de al lado, no en toda la planta.
        ///
        /// Y cierra el triángulo que hace cara la linterna: mientras das cuerda estás QUIETO (0,40
        /// de velocidad), a OSCURAS a ratos (los destellos) y haciendo RUIDO. Sin esta línea las
        /// otras dos son un incordio sin consecuencia.
        /// </summary>
        public const float CrankLoudness = 10f;

        /// <summary>
        /// Report a world noise at <paramref name="at"/>. Silently a no-op without a live IPC client
        /// (bare test scene, or before the backend is up) — a noise that cannot be delivered is not
        /// an error, it just was not heard.
        /// </summary>
        public static void Report(Vector3 at, float loudness)
        {
            if (loudness <= 0f)
                return;
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            ipc.SendReportNoise(at, loudness);
        }
    }
}
