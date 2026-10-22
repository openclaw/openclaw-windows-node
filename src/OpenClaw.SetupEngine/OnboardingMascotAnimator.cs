// Port of OpenClawMascotAnimator.swift at 0fd603a6fece58d60c010e565df9e26c6601db8b.
// MIT attribution: Assets/Setup/Mascot-NOTICE.txt.
namespace OpenClaw.SetupEngine;

/// <summary>
/// UI-thread-owned behavior clock. Elapsed time, random seed and local hour are injectable.
/// Hidden or reduced-motion hosts freeze time; a stalled frame advances at most 100 ms.
/// </summary>
internal sealed class OnboardingMascotAnimator
{
    public const int FramesPerSecond = 12;
    public const double MaximumStepSeconds = 0.1;
    public OnboardingMascotMood Mood { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public bool AllowsAutoSleep { get; set; }
    internal OnboardingMascotGesture? ActiveGesture { get; private set; }
    internal bool IsDozing => AllowsAutoSleep && Mood == OnboardingMascotMood.Idle &&
        ElapsedSeconds - _lastInteractionAt > _sleepAfter;
    private ulong _randomState;
    private readonly bool _nightOwl;
    private bool _begun;
    private bool _motionEnabled = true;
    private double _gestureStart, _pendingAt, _nextBlink, _blinkStart = -1, _nextGlance, _gazeHold;
    private double _nextSnap, _nextQuirk, _nextMoodBeat, _dizzyStart, _dizzyUntil;
    private double _accessorySetAt, _lastInteractionAt, _sleepAfter = 60;
    private bool _doubleBlink, _dizzyRecovery;
    private OnboardingMascotGesture? _pending, _lastClick;
    private OnboardingMascotGaze? _pointer;
    private OnboardingMascotGaze _gazeTarget, _currentGaze;
    private OnboardingMascotAccessory _accessory;
    private double _lastHardHat;
    // Only the last six taps matter to the ladder; keep a bounded allocation-free ring.
    private readonly double[] _taps = new double[6];
    private int _tapCount, _tapIndex;

    public OnboardingMascotAnimator(ulong? seed = null, int? hourOfDay = null, bool allowsAutoSleep = false)
    {
        _randomState = seed ?? (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        if (_randomState == 0)
            _randomState = 0x9E3779B97F4A7C15;
        var hour = hourOfDay ?? DateTime.Now.Hour;
        _nightOwl = hour >= 23 || hour < 5;
        AllowsAutoSleep = allowsAutoSleep;
    }

    public void SetMood(OnboardingMascotMood mood)
    {
        if (!Enum.IsDefined(mood))
            throw new ArgumentOutOfRangeException(nameof(mood));
        if (Mood == mood)
            return;
        var wasWorking = Mood == OnboardingMascotMood.Working;
        Mood = mood;
        _lastInteractionAt = ElapsedSeconds;
        _nextMoodBeat = ElapsedSeconds + RandomRange(6, 12);
        if (wasWorking && _lastHardHat >= 0.9)
            StartGesture(OnboardingMascotGesture.HatTip);
        else if (Entrance(mood) is { } entrance)
            StartGesture(entrance);
    }

    public void SetAccessory(OnboardingMascotAccessory accessory)
    {
        if (!Enum.IsDefined(accessory))
            throw new ArgumentOutOfRangeException(nameof(accessory));
        if (_accessory == accessory)
            return;
        _accessory = accessory;
        _accessorySetAt = ElapsedSeconds;
    }

    public void SetPointer(OnboardingMascotGaze? pointer)
    {
        if (pointer is { } value)
        {
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y))
                throw new ArgumentOutOfRangeException(nameof(pointer));
            pointer = new(Math.Clamp(value.X, -1, 1), Math.Clamp(value.Y, -1, 1));
        }
        if (!_motionEnabled)
            return;
        _pointer = pointer;
        if (pointer is not null && !IsDozing)
            _lastInteractionAt = ElapsedSeconds;
    }

    public void HandleTap()
    {
        if (!_motionEnabled || !AllowsAutoSleep)
            return;
        var wasDozing = IsDozing;
        _lastInteractionAt = ElapsedSeconds;
        _sleepAfter = RandomSleepDelay();
        if (wasDozing)
        {
            StartGesture(OnboardingMascotGesture.Startle);
            return;
        }
        _taps[_tapIndex] = ElapsedSeconds;
        _tapIndex = (_tapIndex + 1) % _taps.Length;
        _tapCount = Math.Min(_tapCount + 1, _taps.Length);
        var burst = 0;
        for (var i = 0; i < _tapCount; i++)
            if (ElapsedSeconds - _taps[i] <= 3)
                burst++;
        if (ElapsedSeconds < _dizzyUntil || burst >= 6)
        {
            if (ElapsedSeconds >= _dizzyUntil)
                _dizzyStart = ElapsedSeconds;
            _dizzyUntil = Math.Max(_dizzyUntil, ElapsedSeconds + 2.4);
            _dizzyRecovery = true;
            ActiveGesture = null;
        }
        else if (burst >= 3)
            StartGesture(OnboardingMascotGesture.HeartBurst);
        else
        {
            Span<OnboardingMascotGesture> reactions = stackalloc[]
            {
                OnboardingMascotGesture.Hop, OnboardingMascotGesture.Wave, OnboardingMascotGesture.Wink,
            };
            var count = 0;
            foreach (var reaction in reactions)
                if (reaction != _lastClick)
                    reactions[count++] = reaction;
            _lastClick = reactions[(int)(NextRandom() % (ulong)count)];
            StartGesture(_lastClick.Value);
        }
    }

    public OnboardingMascotPose Advance(TimeSpan elapsed, bool animationsEnabled)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        _motionEnabled = animationsEnabled;
        if (!animationsEnabled)
        {
            ActiveGesture = _pending = null;
            _pointer = null;
            _dizzyUntil = 0;
            _dizzyRecovery = false;
            _tapCount = _tapIndex = 0;
            _lastHardHat = 0;
            var still = OnboardingMascotPose.Static(Mood);
            if (_accessory != OnboardingMascotAccessory.None && still.HardHat == 0)
            {
                still.Accessory = _accessory;
                still.AccessoryAmount = 1;
            }
            return still;
        }
        if (!_begun)
            Begin();
        var dt = Math.Min(elapsed.TotalSeconds, MaximumStepSeconds);
        ElapsedSeconds += dt;
        AdvanceSchedules();
        var effectiveMood = IsDozing ? OnboardingMascotMood.Sleepy : Mood;
        var pose = BasePose(effectiveMood, ElapsedSeconds);
        ApplyGaze(ref pose, effectiveMood, dt);
        if (pose.HappyEyes < 0.6 && ElapsedSeconds - _blinkStart is >= 0 and <= OnboardingMascotGestures.BlinkDuration)
        {
            var closure = Bell((ElapsedSeconds - _blinkStart) / OnboardingMascotGestures.BlinkDuration);
            pose.LeftEyeOpenness = Math.Min(pose.LeftEyeOpenness, 1 - closure);
            pose.RightEyeOpenness = Math.Min(pose.RightEyeOpenness, 1 - closure);
            pose.EyeGlowOpacity *= Math.Max(0.3, 1 - closure);
        }
        ApplyDizzy(ref pose);
        if (ActiveGesture is { } gesture)
        {
            var progress = (ElapsedSeconds - _gestureStart) / gesture.Duration();
            if (progress >= 1)
                ActiveGesture = null;
            else
                gesture.Apply(ref pose, progress);
        }
        if (_accessory != OnboardingMascotAccessory.None)
        {
            pose.Accessory = _accessory;
            pose.AccessoryAmount = OnboardingMascotGestures.EaseOut((ElapsedSeconds - _accessorySetAt) / 0.5);
        }
        if (pose.HardHat > 0.01)
            pose.AccessoryAmount = 0;
        pose = pose.Clamp();
        _lastHardHat = pose.HardHat;
        return pose;
    }

    private void Begin()
    {
        _begun = true;
        _lastInteractionAt = ElapsedSeconds;
        _sleepAfter = RandomSleepDelay();
        _nextBlink = ElapsedSeconds + RandomRange(0.8, 2.4);
        _nextGlance = ElapsedSeconds + RandomRange(1.5, 4);
        _nextSnap = ElapsedSeconds + RandomRange(2, 5);
        _nextQuirk = ElapsedSeconds + RandomRange(9, 18);
        _nextMoodBeat = ElapsedSeconds + RandomRange(6, 12);
        if (Mood is OnboardingMascotMood.Idle or OnboardingMascotMood.Curious or OnboardingMascotMood.Happy)
        {
            _pending = OnboardingMascotGesture.Wave;
            _pendingAt = ElapsedSeconds + 0.9;
        }
    }

    private void AdvanceSchedules()
    {
        var t = ElapsedSeconds;
        if (t >= _nextBlink)
        {
            _blinkStart = t;
            if (_doubleBlink)
            {
                _doubleBlink = false;
                _nextBlink = t + BlinkInterval();
            }
            else if (RandomRange(0, 1) < 0.14)
            {
                _doubleBlink = true;
                _nextBlink = t + 0.34;
            }
            else
                _nextBlink = t + BlinkInterval();
        }
        if (t >= _nextGlance)
        {
            var magnitude = RandomRange(0.5, 1);
            var angle = RandomRange(0, 2 * Math.PI);
            _gazeTarget = new(Math.Cos(angle) * magnitude, Math.Sin(angle) * magnitude * 0.6);
            _gazeHold = t + RandomRange(0.7, 1.9);
            _nextGlance = _gazeHold + (Mood switch
            {
                OnboardingMascotMood.Curious => RandomRange(1.6, 4),
                OnboardingMascotMood.Thinking => RandomRange(1.2, 3),
                _ => RandomRange(3, 8),
            });
        }
        else if (t >= _gazeHold)
            _gazeTarget = default;
        var dozing = IsDozing;
        if (t >= _nextSnap)
        {
            if (ActiveGesture is null && !dozing && Mood is not (OnboardingMascotMood.Sad or OnboardingMascotMood.Working) &&
                t >= _dizzyUntil)
                StartGesture(OnboardingMascotGesture.ClawSnap);
            _nextSnap = t + RandomRange(4, 9);
        }
        if (t >= _nextQuirk)
        {
            if (ActiveGesture is null && !dozing && t >= _dizzyUntil &&
                Mood is OnboardingMascotMood.Idle or OnboardingMascotMood.Curious or OnboardingMascotMood.Happy or OnboardingMascotMood.Attentive)
                StartGesture(RandomRange(0, 11) switch
                {
                    < 3 => OnboardingMascotGesture.Wink,
                    < 6 => OnboardingMascotGesture.Peek,
                    < 8 => OnboardingMascotGesture.AntennaZap,
                    < 10 => OnboardingMascotGesture.Hop,
                    _ => OnboardingMascotGesture.Sneeze,
                });
            _nextQuirk = t + (Mood == OnboardingMascotMood.Curious ? RandomRange(7, 14) : RandomRange(9, 18));
        }
        if (t >= _nextMoodBeat)
        {
            if (ActiveGesture is null && t >= _dizzyUntil)
            {
                if (Mood == OnboardingMascotMood.Sad)
                    StartGesture(OnboardingMascotGesture.Sigh);
                else if (dozing || Mood == OnboardingMascotMood.Sleepy)
                    StartGesture(OnboardingMascotGesture.Yawn);
                else if (Mood == OnboardingMascotMood.Working)
                    StartGesture(OnboardingMascotGesture.WipeBrow);
            }
            _nextMoodBeat = t + RandomRange(6, 12);
        }
        if (_pending is { } pending && t >= _pendingAt && ActiveGesture is null && !dozing)
        {
            _pending = null;
            StartGesture(pending);
        }
        if (_dizzyRecovery && t >= _dizzyUntil)
        {
            _dizzyRecovery = false;
            StartGesture(OnboardingMascotGesture.Shake);
        }
    }

    private void ApplyGaze(ref OnboardingMascotPose pose, OnboardingMascotMood mood, double dt)
    {
        var target = _pointer ?? _gazeTarget;
        var t = ElapsedSeconds;
        if (mood == OnboardingMascotMood.Sleepy)
            target = new(0, 0.4);
        else if (_pointer is null)
            target = mood switch
            {
                OnboardingMascotMood.Thinking => new(0.4 * Wave(t, 3.8), -0.55),
                OnboardingMascotMood.Working => new(0.55 + 0.04 * Wave(t, 4.6), 0.45 + 0.02 * Math.Cos(2 * Math.PI * Phase(t, 3.9))),
                OnboardingMascotMood.Attentive => new(target.X * 0.5, 0.35),
                OnboardingMascotMood.Sad => new(target.X * 0.3, 0.5),
                _ => target,
            };
        var blend = 1 - Math.Exp(-dt * 9);
        _currentGaze = new(_currentGaze.X + (target.X - _currentGaze.X) * blend,
            _currentGaze.Y + (target.Y - _currentGaze.Y) * blend);
        pose.Gaze = _currentGaze;
    }

    private void ApplyDizzy(ref OnboardingMascotPose pose)
    {
        var t = ElapsedSeconds;
        if (t >= _dizzyUntil)
            return;
        var ramp = Math.Clamp(Math.Min((t - _dizzyStart) / 0.25, (_dizzyUntil - t) / 0.4), 0, 1);
        pose.Dizzy = ramp;
        pose.DizzyPhase = Phase(t, 0.55);
        pose.BodyTilt += 4.5 * ramp * Wave(t, 0.85);
        pose.AntennaDegrees += 6 * ramp * Wave(t, 0.45);
        pose.MouthRound = Math.Max(pose.MouthRound, 0.3 * ramp);
        pose.HappyEyes = 0;
        pose.MouthCurve = Math.Min(pose.MouthCurve, 0);
        pose.Gaze = default;
    }

    internal static OnboardingMascotPose BasePose(OnboardingMascotMood mood, double t) => mood switch
    {
        OnboardingMascotMood.Idle => new() { FloatOffset = Float(t, 4, 4.8), AntennaDegrees = -3 * Wave(t, 2) },
        OnboardingMascotMood.Curious => new()
        {
            FloatOffset = Float(t, 3.4, 4.2), AntennaDegrees = -4 * Wave(t, 1.7), BodyTilt = 1.6 * Wave(t, 5.2),
        },
        OnboardingMascotMood.Thinking => new()
        {
            FloatOffset = Float(t, 5, 3.2), AntennaDegrees = -5 * Wave(t, 1.3), BodyTilt = 2 * Wave(t, 6),
            EyeGlowOpacity = 0.9 + 0.1 * Wave(t, 0.8),
        },
        OnboardingMascotMood.Working => Working(t),
        OnboardingMascotMood.Happy => new()
        {
            FloatOffset = Float(t, 3, 6), AntennaDegrees = -4.5 * Wave(t, 1.6),
            MouthCurve = 0.55 + 0.1 * Wave(t, 3), HappyEyes = 0.35,
        },
        OnboardingMascotMood.Celebrating => new()
        {
            FloatOffset = -9 * Math.Abs(Wave(t, 1.6)), BodyStretch = 1 + 0.03 * Math.Abs(Wave(t, 1.6)),
            AntennaDegrees = -6 * Wave(t, 0.8), LeftClawDegrees = 20 + 8 * Wave(t, 0.9),
            RightClawDegrees = -20 + 8 * Wave(t, 0.9), MouthCurve = 0.9, MouthOpen = 0.35,
            HappyEyes = 0.7, GlowScale = 1.1, Effect = OnboardingMascotEffect.Sparkles, EffectPhase = Phase(t, 2.2),
        },
        OnboardingMascotMood.Sad => new()
        {
            FloatOffset = Float(t, 5.5, 2.4), AntennaDegrees = -1.5 * Wave(t, 3),
            AntennaDroop = 0.75, MouthCurve = -0.55, EyeGlowOpacity = 0.6,
        },
        OnboardingMascotMood.Sleepy => new()
        {
            FloatOffset = Float(t, 6, 2), AntennaDroop = 0.35,
            LeftEyeOpenness = 0.22 + 0.08 * Wave(t, 3), RightEyeOpenness = 0.22 + 0.08 * Wave(t, 3),
            EyeGlowOpacity = 0.5, MouthRound = 0.15, BodyTilt = 2.5 * Wave(t, 6),
            Accessory = OnboardingMascotAccessory.Nightcap, AccessoryAmount = 1,
            Effect = OnboardingMascotEffect.Zzz, EffectPhase = Phase(t, 3),
        },
        OnboardingMascotMood.Attentive => new()
        {
            FloatOffset = Float(t, 4, 3), AntennaDegrees = -2.5 * Wave(t, 2), MouthCurve = 0.25,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(mood)),
    };

    private static OnboardingMascotPose Working(double t)
    {
        var phase = Phase(t, 0.95);
        var claw = phase switch
        {
            < 0.05 => -6,
            < 0.60 => -6 - 28 * Ease((phase - 0.05) / 0.55),
            < 0.72 => -34 + 46 * Math.Pow((phase - 0.60) / 0.12, 2),
            _ => 12 - 18 * Ease((phase - 0.72) / 0.28),
        };
        var impact = Bell((phase - 0.72) / 0.14);
        var recoil = Math.Clamp((phase - 0.72) / 0.28, 0, 1);
        return new()
        {
            RightClawDegrees = claw, LeftClawDegrees = 4 + 2 * Math.Sin(2 * Math.PI * phase),
            FloatOffset = Float(t, 3.8, 2) + 0.8 * impact, BodyStretch = 1 - 0.03 * impact,
            BodyTilt = 2.2 + 0.6 * Wave(t, 5), AntennaDegrees = 6 * (1 - recoil) * Math.Sin(recoil * 3 * Math.PI),
            LeftEyeOpenness = 0.85, RightEyeOpenness = 0.85, MouthCurve = 0.18, HardHat = 1,
            Effect = OnboardingMascotEffect.Sparks, EffectPhase = Phase(phase - 0.72, 1),
        };
    }

    private static OnboardingMascotGesture? Entrance(OnboardingMascotMood mood) => mood switch
    {
        OnboardingMascotMood.Happy => OnboardingMascotGesture.Hop,
        OnboardingMascotMood.Celebrating => OnboardingMascotGesture.Celebrate,
        OnboardingMascotMood.Sad => OnboardingMascotGesture.Sigh,
        OnboardingMascotMood.Sleepy => OnboardingMascotGesture.Yawn,
        OnboardingMascotMood.Working => OnboardingMascotGesture.DonHardHat,
        _ => null,
    };

    private void StartGesture(OnboardingMascotGesture gesture)
    {
        ActiveGesture = gesture;
        _gestureStart = ElapsedSeconds;
    }

    private double BlinkInterval() => Mood == OnboardingMascotMood.Attentive ? RandomRange(1.8, 4) : RandomRange(2.2, 5.5);
    private double RandomSleepDelay() => RandomRange(45, 80) * (_nightOwl ? 0.55 : 1);
    private double RandomRange(double min, double max) => min + (NextRandom() >> 11) * (1d / (1UL << 53)) * (max - min);

    private ulong NextRandom()
    {
        _randomState ^= _randomState >> 12;
        _randomState ^= _randomState << 25;
        _randomState ^= _randomState >> 27;
        return unchecked(_randomState * 2685821657736338717);
    }

    internal static double Phase(double seconds, double period)
    {
        var phase = seconds % period / period;
        return phase < 0 ? phase + 1 : phase;
    }
    private static double Wave(double seconds, double period) => Math.Sin(2 * Math.PI * Phase(seconds, period));
    private static double Float(double seconds, double period, double depth) => -depth * (1 - Math.Cos(2 * Math.PI * Phase(seconds, period)));
    internal static double Ease(double t) => OnboardingMascotGestures.Ease(t);
    internal static double Bell(double t) => OnboardingMascotGestures.Bell(t);
}
