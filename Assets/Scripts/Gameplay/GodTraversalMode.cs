namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Phase 3.2E — debug-only God Traversal Mode for POI/world validation.
    ///
    /// Set IsEnabled = true to bypass client-side collision and freeze the stat
    /// HUD display at 100. Stats remain backend-authoritative; only display and
    /// local collision proxy are affected.
    ///
    /// OFF by default. Remove this file (and callers in PlayerController,
    /// HUDUpdater, SanityEffects) after validation is complete.
    /// </summary>
    public static class GodTraversalMode
    {
        // SET TO true TO ENABLE — restores false for normal gameplay.
        public static bool IsEnabled = false;
    }
}
