using System;
using UnityEngine;

namespace PolymindGames.ProceduralMotion
{
    public static class Easer
    {
        private const float ConstantA = 1.70158f;
        private const float ConstantB = ConstantA * 1.525f;
        private const float ConstantC = ConstantA + 1f;
        private const float ConstantD = Mathf.PI / 2.0f;
        private const float ConstantE = 7.5625f;
        private const float ConstantF = 2.75f;


        /// <summary>
        /// Interpolate using the specified ease function.
        /// </summary>
        /// <param name="ease"></param>
        /// <param name="time"></param>
        /// <returns></returns>
        public static float Apply(EaseType ease, float time)
        {
            return ease switch
            {
                EaseType.Linear => Linear(time),
                EaseType.SineIn => SineIn(time),
                EaseType.SineOut => SineOut(time),
                EaseType.SineInOut => SineInOut(time),
                EaseType.QuadIn => QuadIn(time),
                EaseType.QuadOut => QuadOut(time),
                EaseType.QuadInOut => QuadInOut(time),
                EaseType.CubicIn => CubicIn(time),
                EaseType.CubicOut => CubicOut(time),
                EaseType.CubicInOut => CubicInOut(time),
                EaseType.QuartIn => QuartIn(time),
                EaseType.QuartOut => QuartOut(time),
                EaseType.QuartInOut => QuartInOut(time),
                EaseType.QuintIn => QuintIn(time),
                EaseType.QuintOut => QuintOut(time),
                EaseType.QuintInOut => QuintInOut(time),
                EaseType.ExpoIn => ExpoIn(time),
                EaseType.ExpoOut => ExpoOut(time),
                EaseType.ExpoInOut => ExpoInOut(time),
                EaseType.CircIn => CircIn(time),
                EaseType.CircOut => CircOut(time),
                EaseType.CircInOut => CircInOut(time),
                EaseType.BackIn => BackIn(time),
                EaseType.BackOut => BackOut(time),
                EaseType.BackInOut => BackInOut(time),
                EaseType.ElasticIn => ElasticIn(time),
                EaseType.ElasticOut => ElasticOut(time),
                EaseType.ElasticInOut => ElasticInOut(time),
                EaseType.BounceIn => BounceIn(time),
                EaseType.BounceOut => BounceOut(time),
                EaseType.BounceInOut => BounceInOut(time),
                _ => 0f
            };
        }

        private static float Linear(float time)
        {
            return time;
        }

        private static float SineIn(float time)
        {
            return 1f - Mathf.Cos(time * Mathf.PI / 2f);
        }

        private static float SineOut(float time)
        {
            return Mathf.Sin(time * Mathf.PI / 2f);
        }

        private static float SineInOut(float time)
        {
            return -(Mathf.Cos(Mathf.PI * time) - 1f) / 2f;
        }

        private static float QuadIn(float time)
        {
            return time * time;
        }

        private static float QuadOut(float time)
        {
            return 1 - (1 - time) * (1 - time);
        }

        private static float QuadInOut(float time)
        {
            return time < 0.5f
                ? 2 * time * time
                : 1 - Mathf.Pow(-2 * time + 2, 2) / 2;
        }

        private static float CubicIn(float time)
        {
            return time * time * time;
        }

        private static float CubicOut(float time)
        {
            return 1 - Mathf.Pow(1 - time, 3);
        }

        private static float CubicInOut(float time)
        {
            return time < 0.5f
                ? 4 * time * time * time
                : 1 - Mathf.Pow(-2 * time + 2, 3) / 2;
        }

        private static float QuartIn(float time)
        {
            return time * time * time * time;
        }

        private static float QuartOut(float time)
        {
            return 1 - Mathf.Pow(1 - time, 4);
        }

        private static float QuartInOut(float time)
        {
            return time < 0.5
                ? 8 * time * time * time * time
                : 1 - Mathf.Pow(-2 * time + 2, 4) / 2;
        }

        private static float QuintIn(float time)
        {
            return time * time * time * time * time;
        }

        private static float QuintOut(float time)
        {
            return 1 - Mathf.Pow(1 - time, 5);
        }

        private static float QuintInOut(float time)
        {
            return time < 0.5f
                ? 16 * time * time * time * time * time
                : 1 - Mathf.Pow(-2 * time + 2, 5) / 2;
        }

        private static float ExpoIn(float time)
        {
            return time == 0
                ? 0
                : Mathf.Pow(2, 10 * time - 10);
        }

        private static float ExpoOut(float time)
        {
            return Math.Abs(time - 1) < 0.001f
                ? 1
                : 1 - Mathf.Pow(2, -10 * time);
        }

        private static float ExpoInOut(float time)
        {
            return time == 0
                ? 0
                : Math.Abs(time - 1) < 0.001f
                    ? 1
                    : time < 0.5
                        ? Mathf.Pow(2, 20 * time - 10) / 2
                        : (2 - Mathf.Pow(2, -20 * time + 10)) / 2;
        }

        private static float CircIn(float time)
        {
            return 1 - Mathf.Sqrt(1 - Mathf.Pow(time, 2));
        }

        private static float CircOut(float time)
        {
            return Mathf.Sqrt(1 - Mathf.Pow(time - 1, 2));
        }

        private static float CircInOut(float time)
        {
            return time < 0.5
                ? (1 - Mathf.Sqrt(1 - Mathf.Pow(2 * time, 2))) / 2
                : (Mathf.Sqrt(1 - Mathf.Pow(-2 * time + 2, 2)) + 1) / 2;
        }

        private static float BackIn(float time)
        {
            return ConstantC * time * time * time - ConstantA * time * time;
        }

        private static float BackOut(float time)
        {
            return 1f + ConstantC * Mathf.Pow(time - 1, 3) + ConstantA * Mathf.Pow(time - 1, 2);
        }

        private static float BackInOut(float time)
        {
            return time < 0.5 ?
                Mathf.Pow(2 * time, 2) * ((ConstantB + 1) * 2 * time - ConstantB) / 2 :
                (Mathf.Pow(2 * time - 2, 2) * ((ConstantB + 1) * (time * 2 - 2) + ConstantB) + 2) / 2;
        }

        private static float ElasticIn(float p)
        {
            return Mathf.Sin(13 * ConstantD * p) * Mathf.Pow(2, 10 * (p - 1));
        }

        private static float ElasticOut(float p)
        {
            return Mathf.Sin(-13 * ConstantD * (p + 1)) * Mathf.Pow(2, -10 * p) + 1;
        }

        private static float ElasticInOut(float p)
        {
            if (p < 0.5f)
                return 0.5f * Mathf.Sin(13 * ConstantD * (2 * p)) * Mathf.Pow(2, 10 * (2 * p - 1));

            return 0.5f * (Mathf.Sin(-13 * ConstantD * (2 * p - 1 + 1)) * Mathf.Pow(2, -10 * (2 * p - 1)) + 2);
        }

        private static float BounceIn(float time)
        {
            return 1 - BounceOut(1 - time);
        }

        private static float BounceOut(float time)
        {
            if (time < 1 / ConstantF)
                return ConstantE * time * time;

            if (time < 2 / ConstantF)
                return ConstantE * (time -= 1.5f / ConstantF) * time + 0.75f;

            if (time < 2.5f / ConstantF)
                return ConstantE * (time -= 2.25f / ConstantF) * time + 0.9375f;

            return ConstantE * (time -= 2.625f / ConstantF) * time + 0.984375f;
        }

        private static float BounceInOut(float time)
        {
            return time < 0.5f
                ? (1 - BounceOut(1 - 2 * time)) / 2
                : (1 + BounceOut(2 * time - 1)) / 2;
        }
    }
}