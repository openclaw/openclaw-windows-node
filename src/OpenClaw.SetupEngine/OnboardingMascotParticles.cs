// Adapted from OpenClaw's MIT-licensed Mac mascot. See Assets/Setup/Mascot-NOTICE.txt.
namespace OpenClaw.SetupEngine;

internal readonly record struct OnboardingMascotParticle(double X, double Y, double Size, double Opacity, bool Accent);

internal static class OnboardingMascotParticles
{
    public const int Capacity = 6;

    public static OnboardingMascotParticle Sample(OnboardingMascotPose pose, int index)
    {
        if (index is < 0 or >= Capacity)
            throw new ArgumentOutOfRangeException(nameof(index));
        switch (pose.Effect)
        {
            case OnboardingMascotEffect.Sparkles:
                var phase = (pose.EffectPhase + index * 0.37) % 1;
                var alpha = OnboardingMascotAnimator.Bell(phase);
                var angle = Math.PI + Math.PI * (index + 0.5) / 6;
                var starSize = 2.5 + 2 * alpha;
                return new(60 + Math.Cos(angle) * (50 + index % 3 * 4),
                    55 + Math.Sin(angle) * (40 + index * 5 % 3 * 4),
                    starSize, alpha, index % 2 == 0);
            case OnboardingMascotEffect.Hearts when index < 3:
                phase = (pose.EffectPhase * 1.15 + index / 3d) % 1;
                alpha = phase < 0.15 ? phase / 0.15 : 1 - (phase - 0.15) / 0.85;
                return new(60 + (index - 1) * 26 + 4 * Math.Sin(phase * 2 * Math.PI + index),
                    30 - 28 * phase, 4.5 + index % 2 * 1.5, alpha, false);
            case OnboardingMascotEffect.Zzz when index < 3:
                phase = (pose.EffectPhase + index * 0.33) % 1;
                alpha = phase < 0.2 ? phase / 0.2 : 1 - (phase - 0.2) / 0.8;
                return new(86 + 14 * phase + 2 * Math.Sin(phase * 4 * Math.PI),
                    24 - 20 * phase, 6 + 4 * phase, alpha * 0.9, true);
            case OnboardingMascotEffect.Sparks when index < 5:
                var raw = pose.EffectPhase - index * 0.025;
                if (raw is < 0 or >= 0.45)
                    return default;
                var opacity = raw < 0.08 ? raw / 0.08 : 1 - (raw - 0.08) / 0.37;
                var theta = (-160 + index * 35) * Math.PI / 180;
                var radius = 5 + 12 * raw / 0.45;
                var size = 2.2 + index % 3 * 0.8;
                return new(Math.Clamp(106 + Math.Cos(theta) * radius, size, 120 - size),
                    Math.Clamp(66 + Math.Sin(theta) * radius, size, 120 - size), size, opacity, index % 2 == 0);
            default:
                return default;
        }
    }
}
