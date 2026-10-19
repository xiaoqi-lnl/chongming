namespace OrbRadiance
{
    internal enum BattleMode
    {
        Normal,
        Easy,
        Hard
    }

    internal static class BattleModeState
    {
        private static BattleMode? current;

        internal static BattleMode? Current => current;
        internal static bool IsEasy => current == BattleMode.Easy;

        internal static BattleMode Resolve(bool legacyHardMode)
        {
            return current ?? (legacyHardMode ? BattleMode.Hard : BattleMode.Normal);
        }

        internal static void Set(BattleMode mode)
        {
            current = mode;
        }

        internal static void Reset()
        {
            current = null;
        }
    }
}
