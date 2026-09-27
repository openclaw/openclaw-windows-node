// Adapted from OpenClaw's MIT-licensed Mac mascot. See Assets/Setup/Mascot-NOTICE.txt.
namespace OpenClaw.SetupEngine;

public enum OnboardingMascotMood
{
    Idle,
    Curious,
    Thinking,
    Working,
    Happy,
    Sad,
    Celebrating,
    Sleepy,
    Attentive,
}

public enum OnboardingMascotAccessory { None, Nightcap, GradCap }

internal enum OnboardingMascotEffect { None, Sparkles, Hearts, Zzz, Sparks, Sweat }

internal readonly record struct OnboardingMascotGaze(double X, double Y);

/// <summary>Rendering channels in the upstream mascot's native 120 by 120 coordinate space.</summary>
internal record struct OnboardingMascotPose
{
    public OnboardingMascotPose() { }
    public double FloatOffset { get; set; }
    public double AntennaDegrees { get; set; }
    public double AntennaDroop { get; set; }
    public double LeftClawDegrees { get; set; }
    public double RightClawDegrees { get; set; }
    public double EyeGlowOpacity { get; set; } = 1;
    public double GlowScale { get; set; } = 1;
    public double LeftEyeOpenness { get; set; } = 1;
    public double RightEyeOpenness { get; set; } = 1;
    public double HappyEyes { get; set; }
    public OnboardingMascotGaze Gaze { get; set; }
    public double MouthCurve { get; set; }
    public double MouthOpen { get; set; }
    public double MouthRound { get; set; }
    public double Blush { get; set; }
    public double HardHat { get; set; }
    public OnboardingMascotAccessory Accessory { get; set; }
    public double AccessoryAmount { get; set; }
    public double BodyTilt { get; set; }
    public double BodyStretch { get; set; } = 1;
    public double Dizzy { get; set; }
    public double DizzyPhase { get; set; }
    public OnboardingMascotEffect Effect { get; set; }
    public double EffectPhase { get; set; }

    public static OnboardingMascotPose Static(OnboardingMascotMood mood) => mood switch
    {
        OnboardingMascotMood.Idle or OnboardingMascotMood.Curious or OnboardingMascotMood.Attentive => new(),
        OnboardingMascotMood.Thinking => new() { Gaze = new(0.3, -0.5) },
        OnboardingMascotMood.Working => new()
        {
            HardHat = 1, RightClawDegrees = -28, Gaze = new(0.4, 0.35), MouthCurve = 0.15, BodyTilt = 2,
        },
        OnboardingMascotMood.Happy => new() { MouthCurve = 0.6, HappyEyes = 0.4 },
        OnboardingMascotMood.Sad => new()
        {
            AntennaDroop = 0.75, MouthCurve = -0.55, EyeGlowOpacity = 0.6, Gaze = new(0, 0.5),
        },
        OnboardingMascotMood.Celebrating => new()
        {
            MouthCurve = 0.9, MouthOpen = 0.4, HappyEyes = 0.8, LeftClawDegrees = 30, RightClawDegrees = -30,
        },
        OnboardingMascotMood.Sleepy => new()
        {
            LeftEyeOpenness = 0.25, RightEyeOpenness = 0.25, EyeGlowOpacity = 0.5,
            AntennaDroop = 0.35, Accessory = OnboardingMascotAccessory.Nightcap, AccessoryAmount = 1,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mood)),
    };

    public OnboardingMascotPose Clamp() => this with
    {
        FloatOffset = Math.Clamp(FloatOffset, -12, 2),
        AntennaDegrees = Math.Clamp(AntennaDegrees, -14, 14),
        AntennaDroop = Math.Clamp(AntennaDroop, 0, 1),
        LeftClawDegrees = Math.Clamp(LeftClawDegrees, -45, 45),
        RightClawDegrees = Math.Clamp(RightClawDegrees, -45, 45),
        EyeGlowOpacity = Math.Clamp(EyeGlowOpacity, 0, 1),
        GlowScale = Math.Clamp(GlowScale, 0.5, 1.6),
        LeftEyeOpenness = Math.Clamp(LeftEyeOpenness, 0, 1),
        RightEyeOpenness = Math.Clamp(RightEyeOpenness, 0, 1),
        HappyEyes = Math.Clamp(HappyEyes, 0, 1),
        Gaze = new(Math.Clamp(Gaze.X, -1.2, 1.2), Math.Clamp(Gaze.Y, -1.2, 1.2)),
        MouthCurve = Math.Clamp(MouthCurve, -1, 1),
        MouthOpen = Math.Clamp(MouthOpen, 0, 1),
        MouthRound = Math.Clamp(MouthRound, 0, 1),
        Blush = Math.Clamp(Blush, 0, 1),
        HardHat = Math.Clamp(HardHat, 0, 1),
        AccessoryAmount = Math.Clamp(AccessoryAmount, 0, 1),
        BodyTilt = Math.Clamp(BodyTilt, -8, 8),
        BodyStretch = Math.Clamp(BodyStretch, 0.86, 1.05),
        Dizzy = Math.Clamp(Dizzy, 0, 1),
    };
}
