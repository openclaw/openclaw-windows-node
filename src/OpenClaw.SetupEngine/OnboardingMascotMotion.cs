// Composition expressions adapted from the MIT OpenClaw Mac mascot.
// Create a definition when mood/clip changes, then bind its channels to native shapes.
using System.Globalization;

namespace OpenClaw.SetupEngine;

internal enum OnboardingMascotEffect { None, Sparkles, Hearts, Zzz, Sparks, Sweat }

/// <summary>Declarative motion, evaluated by Windows Composition, not a managed frame loop.</summary>
internal sealed class OnboardingMascotMotion
{
    // One fixed player timeline lets clips change without rebuilding the visual tree.
    internal const double TimelineSeconds = 10;
    internal Dictionary<string, string> Channels { get; } = new()
    {
        ["Float"] = "0",
        ["Tilt"] = "0",
        ["Stretch"] = "1",
        ["LeftClaw"] = "0",
        ["RightClaw"] = "0",
        ["Antenna"] = "0",
        ["Droop"] = "0",
        ["LeftEye"] = "1",
        ["RightEye"] = "1",
        ["HappyEyes"] = "0",
        ["EyeGlow"] = "1",
        ["GlowScale"] = "1",
        ["Smile"] = "0",
        ["Open"] = "0",
        ["Round"] = "0",
        ["Blush"] = "0",
        ["Hat"] = "1",
        ["Dizzy"] = "0",
        ["EffectPhase"] = "0",
        ["GazeX"] = "0",
        ["GazeY"] = "0",
    };
    internal OnboardingMascotEffect Effect { get; private set; }

    internal static double Duration(OnboardingMascotClip clip) => clip switch
    {
        OnboardingMascotClip.Idle or OnboardingMascotClip.Attentive => 4,
        OnboardingMascotClip.Curious => 3.4,
        OnboardingMascotClip.Thinking => 5,
        OnboardingMascotClip.Working => .95,
        OnboardingMascotClip.Happy => 3,
        OnboardingMascotClip.Sad => 5.5,
        OnboardingMascotClip.Celebrating => 2.4,
        OnboardingMascotClip.Sleepy => 6,
        OnboardingMascotClip.Wave or OnboardingMascotClip.Sneeze => 1.5,
        OnboardingMascotClip.Hop => .7,
        OnboardingMascotClip.Wink or OnboardingMascotClip.HatTip or OnboardingMascotClip.Greeting => .9,
        OnboardingMascotClip.Celebrate or OnboardingMascotClip.Dizzy => 2.4,
        OnboardingMascotClip.HeartBurst or OnboardingMascotClip.Yawn or OnboardingMascotClip.WipeBrow => 2,
        OnboardingMascotClip.Peek => 1.9,
        OnboardingMascotClip.AntennaZap or OnboardingMascotClip.DonHardHat => 1,
        OnboardingMascotClip.Sigh => 1.8,
        OnboardingMascotClip.Startle or OnboardingMascotClip.Shake => .8,
        OnboardingMascotClip.ClawSnap => .6,
        _ => throw new ArgumentOutOfRangeException(nameof(clip)),
    };

    internal static OnboardingMascotMotion Create(OnboardingMascotMood mood, OnboardingMascotClip clip, bool still)
    {
        if (!Enum.IsDefined(mood)) throw new ArgumentOutOfRangeException(nameof(mood));
        _ = Duration(clip);
        var motion = new OnboardingMascotMotion();
        motion.SetMood(mood, still);
        if (!still) motion.SetGesture(clip);
        return motion;
    }

    private void SetMood(OnboardingMascotMood mood, bool still)
    {
        var c = Channels;
        switch (mood)
        {
            case OnboardingMascotMood.Idle: Float(4, 4.8, 3, 2); break;
            case OnboardingMascotMood.Curious:
                Float(3.4, 4.2, 4, 1.7); c["Tilt"] = $"1.6 * {Wave(5.2)}"; break;
            case OnboardingMascotMood.Thinking:
                Float(5, 3.2, 5, 1.3); c["Tilt"] = $"2 * {Wave(6)}";
                c["EyeGlow"] = still ? "1" : $"0.9 + 0.1 * {Wave(.8)}";
                c["GazeX"] = still ? "0.3" : $"0.4 * {Wave(3.8)}"; c["GazeY"] = still ? "-0.5" : "-0.55"; break;
            case OnboardingMascotMood.Working:
                c["RightClaw"] = still ? "-28" : $"m.Work < 0.05 ? -6 : m.Work < 0.6 ? -6 - 28 * {Ease("(m.Work-0.05)/0.55")} : m.Work < 0.72 ? -34 + 46 * Pow((m.Work-0.6)/0.12,2) : 12 - 18 * {Ease("(m.Work-0.72)/0.28")}";
                c["LeftClaw"] = still ? "0" : "4 + 2 * Sin(2 * Pi * m.Work)";
                c["Stretch"] = still ? "1" : $"1 - 0.03 * {Bell("(m.Work-0.72)/0.14")}";
                c["Tilt"] = still ? "2" : $"2.2 + 0.6 * {Wave(5)}";
                c["Float"] = $"-2 * (1-Cos(2*Pi*m.T/3.8)) + 0.8 * {Bell("(m.Work-0.72)/0.14")}";
                c["Antenna"] = "6 * (1-Clamp((m.Work-0.72)/0.28,0,1)) * Sin(Clamp((m.Work-0.72)/0.28,0,1)*3*Pi)";
                c["LeftEye"] = c["RightEye"] = still ? "1" : "0.85";
                c["Smile"] = still ? "0.15" : "0.18";
                c["GazeX"] = still ? "0.4" : $"0.55+0.04*{Wave(4.6)}";
                c["GazeY"] = still ? "0.35" : "0.45+0.02*Cos(2*Pi*m.T/3.9)";
                Effect = OnboardingMascotEffect.Sparks; c["EffectPhase"] = "Mod(m.Work+0.28,1)"; break;
            case OnboardingMascotMood.Happy:
                Float(3, 6, 4.5, 1.6); c["Smile"] = still ? "0.6" : $"0.55 + 0.1 * {Wave(3)}"; c["HappyEyes"] = still ? "0.4" : "0.35"; break;
            case OnboardingMascotMood.Sad:
                Float(5.5, 2.4, 1.5, 3); c["Droop"] = "0.75"; c["Smile"] = "-0.55";
                c["EyeGlow"] = "0.6"; c["GazeY"] = "0.5"; break;
            case OnboardingMascotMood.Celebrating:
                c["Float"] = $"-9 * Abs({Wave(1.6)})"; c["Stretch"] = $"1 + 0.03 * Abs({Wave(1.6)})";
                c["Antenna"] = $"-6 * {Wave(.8)}";
                c["LeftClaw"] = still ? "30" : $"20 + 8 * {Wave(.9)}";
                c["RightClaw"] = still ? "-30" : $"-20 + 8 * {Wave(.9)}";
                c["Smile"] = "0.9"; c["Open"] = still ? "0.4" : "0.35"; c["HappyEyes"] = still ? "0.8" : "0.7";
                c["GlowScale"] = still ? "1" : "1.1";
                Effect = OnboardingMascotEffect.Sparkles; c["EffectPhase"] = "Mod(m.T/2.2,1)"; break;
            case OnboardingMascotMood.Sleepy:
                Float(6, 2, 0, 1); c["Droop"] = "0.35"; c["Tilt"] = $"2.5 * {Wave(6)}";
                c["LeftEye"] = c["RightEye"] = still ? "0.25" : $"0.22 + 0.08 * {Wave(3)}";
                c["EyeGlow"] = "0.5"; c["Round"] = still ? "0" : "0.15"; c["GazeY"] = still ? "0" : "0.4";
                Effect = OnboardingMascotEffect.Zzz; c["EffectPhase"] = "Mod(m.T/3,1)"; break;
            case OnboardingMascotMood.Attentive:
                Float(4, 3, 2.5, 2); c["Smile"] = still ? "0" : "0.25"; c["GazeY"] = still ? "0" : "0.35"; break;
        }
        if (still)
        {
            c["Float"] = c["Antenna"] = "0"; c["Stretch"] = "1";
            if (mood != OnboardingMascotMood.Working) c["Tilt"] = "0";
            c["EffectPhase"] = "0";
            Effect = OnboardingMascotEffect.None;
        }
        else
        {
            c["LeftEye"] = $"Min({c["LeftEye"]}, 1-m.Blink)";
            c["RightEye"] = $"Min({c["RightEye"]}, 1-m.Blink)";
        }
    }

    private void SetGesture(OnboardingMascotClip clip)
    {
        var c = Channels;
        switch (clip)
        {
            case OnboardingMascotClip.Wave:
                Add("RightClaw", "m.Envelope * (-28 + 9 * Sin(m.P*6*Pi))"); Add("Tilt", "-2*m.Envelope"); Max("Smile", "0.5*m.Envelope"); break;
            case OnboardingMascotClip.Hop:
                Add("Float", $"-9*{Bell("(m.P-0.2)/0.6")}");
                Add("Stretch", $"0.045*{Bell("(m.P-0.2)/0.6")}-0.1*{Bell("m.P/0.2")}-0.06*{Bell("(m.P-0.82)/0.18")}");
                Max("Smile", $"0.4*{Bell("(m.P-0.2)/0.6")}"); break;
            case OnboardingMascotClip.Wink:
                c["RightEye"] = $"Min({c["RightEye"]},1-m.Envelope)"; Max("Smile", "0.5*m.Envelope"); Add("Tilt", "1.5*m.Envelope"); break;
            case OnboardingMascotClip.Celebrate:
                Add("Float", "-11*Abs(Sin(m.P*4*Pi))*m.Envelope");
                Add("Stretch", "0.035*Abs(Sin(m.P*4*Pi))*m.Envelope");
                Add("LeftClaw", "38*m.Envelope"); Add("RightClaw", "-38*m.Envelope");
                Max("HappyEyes", "m.Envelope"); Max("Smile", "m.Envelope"); Max("Open", $"0.6*{Bell("m.P")}");
                c["Droop"] = "0"; Max("GlowScale", "1+0.2*m.Envelope");
                Effect = OnboardingMascotEffect.Sparkles; c["EffectPhase"] = "m.P"; break;
            case OnboardingMascotClip.HeartBurst:
                Max("Blush", "0.9*m.Envelope"); Max("HappyEyes", "0.8*m.Envelope"); Max("Smile", "0.8*m.Envelope");
                Add("Tilt", "2*m.Envelope*Sin(m.P*4*Pi)"); Effect = OnboardingMascotEffect.Hearts; c["EffectPhase"] = "m.P"; break;
            case OnboardingMascotClip.Peek:
                c["GazeX"] = "-1.1*m.PeekLeft+1.1*m.PeekRight";
                c["GazeY"] = "-0.1"; Add("Tilt", "-6*m.PeekLeft+6*m.PeekRight"); Max("GlowScale", "1.1"); break;
            case OnboardingMascotClip.Sneeze:
                Add("Stretch", $"m.P<0.42 ? 0.04*{Ease("m.P/0.42")} : m.P<0.58 ? -0.13*{Bell("(m.P-0.42)/0.16")} : 0");
                Max("Round", $"m.P<0.42 ? 0.55*{Ease("m.P/0.42")} : m.P<0.58 ? 0 : 0.2*(1-{Ease("(m.P-0.58)/0.42")})");
                c["LeftEye"] = c["RightEye"] = $"((m.P>=0.42) && (m.P<0.58)) ? 0 : {c["LeftEye"]}";
                Max("Droop", $"m.P<0.42 ? 0 : m.P<0.58 ? 0.9*{Bell("(m.P-0.42)/0.16")} : 0.5*(1-{Ease("(m.P-0.58)/0.42")})");
                Add("Tilt", $"((m.P>=0.42) && (m.P<0.58)) ? 3*{Bell("(m.P-0.42)/0.16")} : 0");
                Add("Antenna", $"m.P<0.42 ? 8*{Ease("m.P/0.42")} : 0");
                c["EyeGlow"] = $"({c["EyeGlow"]})*(m.P>=0.58 ? 1-0.4*(1-{Ease("(m.P-0.58)/0.42")}) : 1)";
                c["GazeX"] = $"m.P<0.42 ? 0 : ({c["GazeX"]})";
                c["GazeY"] = $"m.P<0.42 ? -0.8*{Ease("m.P/0.42")} : ({c["GazeY"]})"; break;
            case OnboardingMascotClip.AntennaZap:
                Add("Antenna", "5*(1-m.P)*Sin(m.P*12*Pi)"); Max("GlowScale", $"1+0.55*{Bell("m.P")}");
                c["EyeGlow"] = "1"; break;
            case OnboardingMascotClip.Sigh:
                Add("Stretch", $"0.025*{Ease("m.P/0.3")}-0.08*{Ease("(m.P-0.3)/0.45")}*(1-Clamp((m.P-0.85)/0.15,0,1))");
                Add("Droop", $"0.15*{Ease("(m.P-0.3)/0.45")}");
                c["GazeY"] = $"0.5*{Ease("(m.P-0.3)/0.45")}"; break;
            case OnboardingMascotClip.Yawn:
                Max("Round", "0.9*m.Envelope"); c["LeftEye"] = c["RightEye"] = $"Min({c["LeftEye"]},1-0.9*m.Envelope)";
                Add("Stretch", "0.03*m.Envelope"); Add("Tilt", "-2*m.Envelope"); break;
            case OnboardingMascotClip.Startle:
                Add("Float", $"-5*{Bell("m.P/0.4")}"); Add("Stretch", $"0.05*{Bell("m.P/0.4")}");
                Max("GlowScale", $"1+0.4*{Bell("m.P/0.4")}");
                c["LeftEye"] = c["RightEye"] = "1"; c["Droop"] = c["Antenna"] = c["GazeX"] = c["GazeY"] = "0"; break;
            case OnboardingMascotClip.Shake:
                Add("Tilt", "5*(1-m.P)*Sin(m.P*6*Pi)"); c["GazeX"] = c["GazeY"] = "0"; break;
            case OnboardingMascotClip.ClawSnap:
                Add("LeftClaw", $"-8*{Bell("m.P/0.7")}"); Add("RightClaw", $"-8*{Bell("(m.P-0.25)/0.7")}"); break;
            case OnboardingMascotClip.DonHardHat:
                c["Hat"] = Ease("m.P/0.55"); c["GazeX"] = "0"; c["GazeY"] = "m.P<0.55 ? -0.9*(1-m.P) : 0.35";
                Add("Stretch", $"-0.04*{Bell("(m.P-0.5)/0.2")}");
                Add("LeftClaw", $"-8*{Bell("(m.P-0.7)/0.3")}"); Add("RightClaw", $"8*{Bell("(m.P-0.7)/0.3")}"); break;
            case OnboardingMascotClip.WipeBrow:
                c["LeftClaw"] = $"({c["LeftClaw"]})*(1-m.Envelope)+38*m.Envelope*(0.9+0.1*Sin(m.P*5*Pi))";
                c["RightClaw"] = $"({c["RightClaw"]})*(1-m.Envelope)";
                c["Tilt"] = $"({c["Tilt"]})*(1-m.Envelope)"; Add("Stretch", "0.02*m.Envelope");
                c["GazeX"] = $"({c["GazeX"]})*(1-m.Envelope)"; c["GazeY"] = $"({c["GazeY"]})*(1-m.Envelope)";
                Max("HappyEyes", "0.7*m.Envelope"); Max("Smile", "0.5*m.Envelope");
                Effect = OnboardingMascotEffect.Sweat; c["EffectPhase"] = "m.P"; break;
            case OnboardingMascotClip.HatTip:
                c["RightClaw"] = $"({c["RightClaw"]})*(1-m.Envelope)+(-33+3*Sin(m.P*4*Pi))*m.Envelope";
                c["Hat"] = $"1-{Ease("(m.P-0.55)/0.45")}"; Add("Tilt", $"3*{Bell("m.P")}");
                Add("Stretch", $"-0.02*{Bell("m.P")}"); break;
            case OnboardingMascotClip.Dizzy:
                c["Dizzy"] = "Clamp(Min(m.P*2.4/0.25,(1-m.P)*2.4/0.4),0,1)";
                Add("Tilt", $"4.5*{Wave(.85)}"); Add("Antenna", $"6*{Wave(.45)}");
                Max("Round", "0.3"); c["HappyEyes"] = c["Smile"] = c["GazeX"] = c["GazeY"] = "0"; break;
        }
    }

    internal static (double Attack, double Release) Envelope(OnboardingMascotClip clip) => clip switch
    {
        OnboardingMascotClip.Wink => (.3, .72),
        OnboardingMascotClip.Celebrate => (.12, .88),
        OnboardingMascotClip.HeartBurst => (.15, .85),
        OnboardingMascotClip.Yawn => (.3, .75),
        OnboardingMascotClip.WipeBrow => (.2, .8),
        OnboardingMascotClip.HatTip => (.22, .82),
        _ => (.18, .82),
    };
    private void Float(double period, double depth, double antenna, double antennaPeriod)
    {
        Channels["Float"] = $"-{Number(depth)}*(1-Cos(2*Pi*m.T/{Number(period)}))";
        Channels["Antenna"] = $"-{Number(antenna)}*{Wave(antennaPeriod)}";
    }
    private void Add(string channel, string value) => Channels[channel] = $"({Channels[channel]})+({value})";
    private void Max(string channel, string value) => Channels[channel] = $"Max(({Channels[channel]}),({value}))";
    // Composition parses an exponent's E as a parameter name, not a numeric literal.
    internal static string Number(double value) => value.ToString("0.################", CultureInfo.InvariantCulture);
    internal static string Wave(double period) => $"Sin(2*Pi*m.T/{Number(period)})";
    internal static string Ease(string value) => $"(Pow(Clamp(({value}),0,1),2)*(3-2*Clamp(({value}),0,1)))";
    internal static string Bell(string value) => Ease($"1-Abs(2*Clamp(({value}),0,1)-1)");
    internal static string Plateau(string value, double attack, double release) =>
        $"Min({Ease($"({value})/{Number(attack)}")},{Ease($"(1-({value}))/{Number(1 - release)}")})";
}
