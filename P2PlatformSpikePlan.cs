using System;
using System.Linq;

namespace OrbRadiance
{
    // Pure planning helpers shared by the P2 cycle and its regression harness.
    internal static class P2PlatformSpikePlan
    {
        internal static bool ShouldRun(bool enabled, bool easy, bool hard)
        {
            return enabled && !easy && !hard;
        }

        internal static bool ShouldHideVanillaPlatforms(bool easy, bool hard) => !easy && hard;
        internal static bool ShouldReplayAppear(bool normalPlatformsKept) => !normalPlatformsKept;
        internal static bool ShouldStopCycle(string state)
        {
            return state == "Ascend Tele" || state == "Scream" || state == "Battle Start" ||
                   state == "Refight" || state == "Init" || state == "Setup";
        }

        internal static bool AdvanceCycle(ref float elapsed, float deltaTime, float period)
        {
            if (period <= 0f) throw new ArgumentOutOfRangeException("period");
            elapsed += Math.Max(0f, deltaTime);
            if (elapsed < period) return false;
            elapsed = 0f;
            return true;
        }

        internal static bool ShouldEmitNails(bool hardMode) => hardMode;

        internal static bool ShouldSendRise(bool started, string state)
        {
            return started && state == "Downed";
        }

        internal static bool ShouldNormalizeDown(bool started, string state, bool alreadyNormalized, bool upAlreadySent)
        {
            return started && state != "Downed" && !alreadyNormalized && !upAlreadySent;
        }

        internal static SpikePlacement[] BuildPlacements(int[] chosenPlatforms, PlatformSpan[] spans,
                                                         float halfWidth, int perPlatform)
        {
            if (chosenPlatforms == null || spans == null || chosenPlatforms.Length != 4 || perPlatform < 1)
                throw new ArgumentException("A cycle requires four platforms and a positive spike count");
            if (chosenPlatforms.Distinct().Count() != chosenPlatforms.Length)
                throw new ArgumentException("Platform selection must be distinct");

            var placements = new SpikePlacement[chosenPlatforms.Length * perPlatform];
            int at = 0;
            foreach (int platformIndex in chosenPlatforms)
            {
                if (platformIndex < 0 || platformIndex >= spans.Length) throw new ArgumentOutOfRangeException("chosenPlatforms");
                float[] centers = EvenlySpacedCenters(spans[platformIndex].minX, spans[platformIndex].maxX, halfWidth, perPlatform);
                foreach (float x in centers) placements[at++] = new SpikePlacement(platformIndex, x, spans[platformIndex].topY);
            }
            return placements;
        }

        internal struct PlatformSpan
        {
            internal readonly float minX, maxX, topY;
            internal PlatformSpan(float min, float max, float top) { minX = min; maxX = max; topY = top; }
        }

        internal struct SpikePlacement
        {
            internal readonly int platformIndex;
            internal readonly float x, platformTopY;
            internal SpikePlacement(int platform, float centerX, float topY)
            { platformIndex = platform; x = centerX; platformTopY = topY; }
        }

        // randomIndex(maxExclusive) must return a value in [0,maxExclusive).
        internal static int[] ChooseFour(int available, Func<int, int> randomIndex)
        {
            if (available < 4) throw new ArgumentOutOfRangeException("available");
            if (randomIndex == null) throw new ArgumentNullException("randomIndex");

            int[] indices = new int[available];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            for (int i = 0; i < 4; i++)
            {
                int offset = randomIndex(available - i);
                if (offset < 0 || offset >= available - i)
                    throw new ArgumentOutOfRangeException("randomIndex");
                int selected = i + offset;
                int swap = indices[i];
                indices[i] = indices[selected];
                indices[selected] = swap;
            }

            return new[] { indices[0], indices[1], indices[2], indices[3] };
        }

        internal static float[] EvenlySpacedCenters(float minX, float maxX, float halfWidth, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException("count");
            float left = minX + halfWidth;
            float right = maxX - halfWidth;
            if (right < left) throw new ArgumentOutOfRangeException("halfWidth");

            float[] centers = new float[count];
            if (count == 1)
            {
                centers[0] = (left + right) * 0.5f;
                return centers;
            }

            for (int i = 0; i < count; i++)
                centers[i] = left + (right - left) * i / (count - 1);
            return centers;
        }
    }
}
