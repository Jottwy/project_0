using System;

namespace PolymindGames
{
    /// <summary>
    /// Decoupling hook that lets a higher layer (e.g. the Backrooms networking
    /// layer) delay player spawn until it is ready — WITHOUT PolymindGames taking a
    /// dependency on that assembly (which would be a circular reference).
    ///
    /// Defaults to always-ready, so standalone scenes spawn immediately. A binder in
    /// the higher layer assigns <see cref="IsReady"/> in scenes that must wait for a
    /// connection, and must restore the default on teardown so other scenes are
    /// unaffected.
    /// </summary>
    public static class GameBootGate
    {
        /// <summary>True when GameMode may spawn the player. Default: always ready.</summary>
        public static Func<bool> IsReady = () => true;
    }
}
