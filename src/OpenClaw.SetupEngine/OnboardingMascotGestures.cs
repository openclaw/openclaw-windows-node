// Port of OpenClawMascotAnimator.swift at 0fd603a6fece58d60c010e565df9e26c6601db8b.
// MIT attribution: Assets/Setup/Mascot-NOTICE.txt.
namespace OpenClaw.SetupEngine;

internal enum OnboardingMascotGesture
{
    Wave, Hop, Wink, Celebrate, HeartBurst, Peek, Sneeze, AntennaZap,
    Sigh, Yawn, Startle, Shake, ClawSnap, DonHardHat, WipeBrow, HatTip,
}

internal static class OnboardingMascotGestures
{
    public const double BlinkDuration = 0.16;

    public static double Duration(this OnboardingMascotGesture gesture) => gesture switch
    {
        OnboardingMascotGesture.Wave or OnboardingMascotGesture.Sneeze => 1.5,
        OnboardingMascotGesture.Hop => 0.7,
        OnboardingMascotGesture.Wink or OnboardingMascotGesture.HatTip => 0.9,
        OnboardingMascotGesture.Celebrate => 2.4,
        OnboardingMascotGesture.HeartBurst or OnboardingMascotGesture.Yawn or OnboardingMascotGesture.WipeBrow => 2,
        OnboardingMascotGesture.Peek => 1.9,
        OnboardingMascotGesture.AntennaZap or OnboardingMascotGesture.DonHardHat => 1,
        OnboardingMascotGesture.Sigh => 1.8,
        OnboardingMascotGesture.Startle or OnboardingMascotGesture.Shake => 0.8,
        OnboardingMascotGesture.ClawSnap => 0.6,
        _ => throw new ArgumentOutOfRangeException(nameof(gesture)),
    };

    public static void Apply(this OnboardingMascotGesture gesture, ref OnboardingMascotPose pose, double p)
    {
        switch (gesture)
        {
            case OnboardingMascotGesture.Wave:
                var raised = Plateau(p, 0.18, 0.82);
                pose.RightClawDegrees += raised * (-28 + 9 * Math.Sin(p * 6 * Math.PI));
                pose.BodyTilt -= 2 * raised;
                pose.MouthCurve = Math.Max(pose.MouthCurve, 0.5 * raised);
                break;
            case OnboardingMascotGesture.Hop:
                var air = Bell((p - 0.2) / 0.6);
                pose.FloatOffset -= 9 * air;
                pose.BodyStretch += 0.045 * air - 0.1 * Bell(p / 0.2) - 0.06 * Bell((p - 0.82) / 0.18);
                pose.MouthCurve = Math.Max(pose.MouthCurve, 0.4 * air);
                break;
            case OnboardingMascotGesture.Wink:
                var closure = Plateau(p, 0.3, 0.72);
                pose.RightEyeOpenness = Math.Min(pose.RightEyeOpenness, 1 - closure);
                pose.MouthCurve = Math.Max(pose.MouthCurve, 0.5 * closure);
                pose.BodyTilt += 1.5 * closure;
                break;
            case OnboardingMascotGesture.Celebrate:
                var env = Plateau(p, 0.12, 0.88);
                var hops = Math.Abs(Math.Sin(p * 4 * Math.PI));
                pose.FloatOffset -= 11 * hops * env;
                pose.BodyStretch += 0.035 * hops * env;
                pose.LeftClawDegrees += 38 * env;
                pose.RightClawDegrees -= 38 * env;
                pose.HappyEyes = Math.Max(pose.HappyEyes, env);
                pose.MouthCurve = Math.Max(pose.MouthCurve, env);
                pose.MouthOpen = Math.Max(pose.MouthOpen, 0.6 * Bell(p));
                pose.AntennaDroop = 0;
                pose.GlowScale = Math.Max(pose.GlowScale, 1 + 0.2 * env);
                pose.Effect = OnboardingMascotEffect.Sparkles;
                pose.EffectPhase = p;
                break;
            case OnboardingMascotGesture.HeartBurst:
                env = Plateau(p, 0.15, 0.85);
                pose.Blush = Math.Max(pose.Blush, 0.9 * env);
                pose.HappyEyes = Math.Max(pose.HappyEyes, 0.8 * env);
                pose.MouthCurve = Math.Max(pose.MouthCurve, 0.8 * env);
                pose.BodyTilt += 2 * env * Math.Sin(p * 4 * Math.PI);
                pose.Effect = OnboardingMascotEffect.Hearts;
                pose.EffectPhase = p;
                break;
            case OnboardingMascotGesture.Peek:
                var left = Plateau(p / 0.5, 0.3, 0.7);
                var right = Plateau((p - 0.5) / 0.5, 0.3, 0.7);
                pose.BodyTilt += -6 * left + 6 * right;
                pose.Gaze = new(-1.1 * left + 1.1 * right, -0.1);
                pose.GlowScale = Math.Max(pose.GlowScale, 1.1);
                break;
            case OnboardingMascotGesture.Sneeze:
                if (p < 0.42)
                {
                    var inhale = Ease(p / 0.42);
                    pose.BodyStretch += 0.04 * inhale;
                    pose.Gaze = new(0, -0.8 * inhale);
                    pose.MouthRound = Math.Max(pose.MouthRound, 0.55 * inhale);
                    pose.AntennaDegrees += 8 * inhale;
                }
                else if (p < 0.58)
                {
                    var burst = Bell((p - 0.42) / 0.16);
                    pose.BodyStretch -= 0.13 * burst;
                    pose.LeftEyeOpenness = pose.RightEyeOpenness = 0;
                    pose.AntennaDroop = Math.Max(pose.AntennaDroop, 0.9 * burst);
                    pose.BodyTilt += 3 * burst;
                }
                else
                {
                    var recover = 1 - Ease((p - 0.58) / 0.42);
                    pose.AntennaDroop = Math.Max(pose.AntennaDroop, 0.5 * recover);
                    pose.EyeGlowOpacity *= 1 - 0.4 * recover;
                    pose.MouthRound = Math.Max(pose.MouthRound, 0.2 * recover);
                }
                break;
            case OnboardingMascotGesture.AntennaZap:
                pose.AntennaDegrees += 5 * (1 - p) * Math.Sin(p * 12 * Math.PI);
                pose.GlowScale = Math.Max(pose.GlowScale, 1 + 0.55 * Bell(p));
                pose.EyeGlowOpacity = 1;
                break;
            case OnboardingMascotGesture.Sigh:
                var rise = Ease(p / 0.3);
                var fall = Ease((p - 0.3) / 0.45);
                pose.BodyStretch += 0.025 * rise - 0.08 * fall * (1 - Math.Clamp((p - 0.85) / 0.15, 0, 1));
                pose.Gaze = new(pose.Gaze.X, 0.5 * fall);
                pose.AntennaDroop = Math.Min(1, pose.AntennaDroop + 0.15 * fall);
                break;
            case OnboardingMascotGesture.Yawn:
                var openness = Plateau(p, 0.3, 0.75);
                pose.MouthRound = Math.Max(pose.MouthRound, 0.9 * openness);
                pose.LeftEyeOpenness = Math.Min(pose.LeftEyeOpenness, 1 - 0.9 * openness);
                pose.RightEyeOpenness = Math.Min(pose.RightEyeOpenness, 1 - 0.9 * openness);
                pose.BodyStretch += 0.03 * openness;
                pose.BodyTilt -= 2 * openness;
                break;
            case OnboardingMascotGesture.Startle:
                var jolt = Bell(p / 0.4);
                pose.FloatOffset -= 5 * jolt;
                pose.BodyStretch += 0.05 * jolt;
                pose.GlowScale = Math.Max(pose.GlowScale, 1 + 0.4 * jolt);
                pose.LeftEyeOpenness = pose.RightEyeOpenness = 1;
                pose.AntennaDroop = pose.AntennaDegrees = 0;
                pose.Gaze = default;
                break;
            case OnboardingMascotGesture.Shake:
                pose.BodyTilt += 5 * (1 - p) * Math.Sin(p * 6 * Math.PI);
                pose.Gaze = default;
                break;
            case OnboardingMascotGesture.ClawSnap:
                pose.LeftClawDegrees -= 8 * Bell(p / 0.7);
                pose.RightClawDegrees -= 8 * Bell((p - 0.25) / 0.7);
                break;
            case OnboardingMascotGesture.DonHardHat:
                pose.HardHat = Math.Min(pose.HardHat, Ease(p / 0.55));
                if (p < 0.55)
                    pose.Gaze = new(0, -0.9 * (1 - p));
                pose.BodyStretch -= 0.04 * Bell((p - 0.5) / 0.2);
                var ready = Bell((p - 0.7) / 0.3);
                pose.LeftClawDegrees -= 8 * ready;
                pose.RightClawDegrees += 8 * ready;
                break;
            case OnboardingMascotGesture.WipeBrow:
                env = Plateau(p, 0.2, 0.8);
                pose.LeftClawDegrees *= 1 - env;
                pose.RightClawDegrees *= 1 - env;
                pose.LeftClawDegrees += 38 * env * (0.9 + 0.1 * Math.Sin(p * 5 * Math.PI));
                pose.BodyTilt *= 1 - env;
                pose.BodyStretch += 0.02 * env;
                pose.HappyEyes = Math.Max(pose.HappyEyes, 0.7 * env);
                pose.MouthCurve = Math.Max(pose.MouthCurve, 0.5 * env);
                pose.Gaze = new(pose.Gaze.X * (1 - env), pose.Gaze.Y * (1 - env));
                pose.Effect = OnboardingMascotEffect.Sweat;
                pose.EffectPhase = p;
                break;
            case OnboardingMascotGesture.HatTip:
                var reach = Plateau(p, 0.22, 0.82);
                pose.RightClawDegrees = pose.RightClawDegrees * (1 - reach) + (-33 + 3 * Math.Sin(p * 4 * Math.PI)) * reach;
                pose.BodyTilt += 3 * Bell(p);
                pose.BodyStretch -= 0.02 * Bell(p);
                pose.HardHat = Math.Max(pose.HardHat, 1 - Ease((p - 0.55) / 0.45));
                break;
        }
    }

    internal static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    internal static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    internal static double Bell(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Ease(t < 0.5 ? t * 2 : (1 - t) * 2);
    }

    internal static double Plateau(double t, double attack, double release)
    {
        t = Math.Clamp(t, 0, 1);
        return t < attack ? Ease(t / attack) : t > release ? Ease((1 - t) / (1 - release)) : 1;
    }
}
