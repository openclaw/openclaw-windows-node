// Selects native animation clips; the player owns frames and interpolation.
// Hosts call SetActive, SetMood, Tap/WakeUp and Complete at playback boundaries.
namespace OpenClaw.SetupEngine;

public enum OnboardingMascotMood
{
    Idle, Curious, Thinking, Working, Happy, Sad, Celebrating, Sleepy, Attentive,
}

public enum OnboardingMascotAccessory { None, Nightcap, GradCap }

internal enum OnboardingMascotClip
{
    Idle, Curious, Thinking, Working, Happy, Sad, Celebrating, Sleepy, Attentive,
    Wave, Hop, Wink, Celebrate, HeartBurst, Peek, Sneeze, AntennaZap, Sigh, Yawn,
    Startle, Shake, ClawSnap, DonHardHat, WipeBrow, HatTip, Dizzy, Greeting,
}

/// <summary>Interaction policy without a frame clock, geometry or animation curves.</summary>
internal sealed class OnboardingMascotPlayback(TimeProvider? timeProvider = null, Random? random = null, int? hour = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Random _random = random ?? Random.Shared;
    private readonly bool _night = (hour ?? DateTime.Now.Hour) is >= 23 or < 5;
    private readonly Queue<double> _taps = new(6);
    private TimeSpan _elapsed;
    private long _started;
    private double _lastInteraction, _sleepAfter = 60, _nextQuirk = 9, _workingSince;
    private OnboardingMascotClip? _lastTap;
    private OnboardingMascotAccessory _accessory;
    private bool _interactive = true;
    private bool _begun;

    public bool Active { get; private set; }
    public bool Interactive
    {
        get => _interactive;
        set
        {
            if (_interactive == value) return;
            _interactive = value;
            Clip = RestingClip;
            _lastInteraction = Elapsed;
            _taps.Clear();
        }
    }
    public OnboardingMascotMood Mood { get; private set; }
    public OnboardingMascotClip Clip { get; private set; } = OnboardingMascotClip.Greeting;
    public double Elapsed => (_elapsed + (Active ? _time.GetElapsedTime(_started) : TimeSpan.Zero)).TotalSeconds;
    public bool IsDozing => Active && Interactive && Mood == OnboardingMascotMood.Idle &&
        Elapsed - _lastInteraction > _sleepAfter;
    public bool HardHat => Mood == OnboardingMascotMood.Working || Clip == OnboardingMascotClip.HatTip;
    public OnboardingMascotAccessory Accessory
    {
        get => HardHat ? OnboardingMascotAccessory.None :
            _accessory != OnboardingMascotAccessory.None ? _accessory :
            IsDozing || Mood == OnboardingMascotMood.Sleepy ? OnboardingMascotAccessory.Nightcap : OnboardingMascotAccessory.None;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _accessory = value;
        }
    }

    public OnboardingMascotClip RestingClip => (OnboardingMascotClip)Mood;

    /// <summary>Pauses behavioral time too; hidden time never counts toward sleep or a tap burst.</summary>
    public void SetActive(bool active)
    {
        if (Active == active) return;
        if (active)
        {
            _started = _time.GetTimestamp();
            if (!_begun)
            {
                _begun = true;
                _sleepAfter = SleepDelay();
                Clip = Mood is OnboardingMascotMood.Idle or OnboardingMascotMood.Curious or OnboardingMascotMood.Happy
                    ? OnboardingMascotClip.Greeting : Entrance();
            }
        }
        else
        {
            _elapsed += _time.GetElapsedTime(_started);
            Clip = RestingClip;
            _taps.Clear();
        }
        Active = active;
        if (active && IsDozing) Clip = OnboardingMascotClip.Sleepy;
    }

    public void SetMood(OnboardingMascotMood mood)
    {
        if (!Enum.IsDefined(mood)) throw new ArgumentOutOfRangeException(nameof(mood));
        if (Mood == mood) return;
        var seatedHat = Active && Mood == OnboardingMascotMood.Working && Elapsed - _workingSince >= .55;
        Mood = mood;
        _lastInteraction = Elapsed;
        _nextQuirk = Elapsed + _random.Next(6, 13);
        if (mood == OnboardingMascotMood.Working) _workingSince = Elapsed;
        Clip = !Active ? RestingClip : seatedHat ? OnboardingMascotClip.HatTip : Entrance();
    }

    private OnboardingMascotClip Entrance() => Mood switch
    {
        OnboardingMascotMood.Working => OnboardingMascotClip.DonHardHat,
        OnboardingMascotMood.Happy => OnboardingMascotClip.Hop,
        OnboardingMascotMood.Celebrating => OnboardingMascotClip.Celebrate,
        OnboardingMascotMood.Sad => OnboardingMascotClip.Sigh,
        OnboardingMascotMood.Sleepy => OnboardingMascotClip.Yawn,
        _ => RestingClip,
    };

    /// <summary>Pointer activity postpones sleep but never wakes a sleeping character.</summary>
    public bool PointerActivity()
    {
        if (!Active || IsDozing) return false;
        _lastInteraction = Elapsed;
        return Clip is not (OnboardingMascotClip.Dizzy or OnboardingMascotClip.Shake);
    }

    /// <summary>Wake during a drag without adding a tap to the reaction ladder.</summary>
    public bool WakeUp()
    {
        if (!Active || !Interactive) return false;
        var sleeping = IsDozing;
        _lastInteraction = Elapsed;
        if (!sleeping) return false;
        _sleepAfter = SleepDelay();
        Clip = OnboardingMascotClip.Startle;
        return true;
    }

    public bool Tap()
    {
        if (!Active || !Interactive) return false;
        if (WakeUp()) return true;
        _sleepAfter = SleepDelay();
        while (_taps.TryPeek(out var tap) && Elapsed - tap > 3) _taps.Dequeue();
        if (_taps.Count == 6) _taps.Dequeue();
        _taps.Enqueue(Elapsed);
        if (Clip == OnboardingMascotClip.Dizzy || _taps.Count >= 6)
            Clip = OnboardingMascotClip.Dizzy;
        else if (_taps.Count >= 3)
            Clip = OnboardingMascotClip.HeartBurst;
        else
        {
            var choices = new[] { OnboardingMascotClip.Hop, OnboardingMascotClip.Wave, OnboardingMascotClip.Wink };
            var next = _random.Next(choices.Length);
            if (choices[next] == _lastTap) next = (next + 1) % choices.Length;
            Clip = choices[next];
            _lastTap = Clip;
        }
        return true;
    }

    /// <summary>Advance only when a native clip finishes, never on a UI-thread frame timer.</summary>
    public void Complete()
    {
        if (!Active) return;
        if (Clip == OnboardingMascotClip.Greeting)
        {
            Clip = OnboardingMascotClip.Wave;
            return;
        }
        if (Clip == OnboardingMascotClip.Dizzy)
        {
            Clip = OnboardingMascotClip.Shake;
            return;
        }
        Clip = IsDozing ? OnboardingMascotClip.Sleepy : RestingClip;
        if (Elapsed < _nextQuirk) return;
        _nextQuirk = Elapsed + _random.Next(6, 13);
        Clip = Clip switch
        {
            OnboardingMascotClip.Sleepy => OnboardingMascotClip.Yawn,
            OnboardingMascotClip.Sad => OnboardingMascotClip.Sigh,
            OnboardingMascotClip.Working => OnboardingMascotClip.WipeBrow,
            OnboardingMascotClip.Idle or OnboardingMascotClip.Curious or OnboardingMascotClip.Happy or OnboardingMascotClip.Attentive =>
                new[] { OnboardingMascotClip.Wink, OnboardingMascotClip.Peek, OnboardingMascotClip.AntennaZap,
                    OnboardingMascotClip.Hop, OnboardingMascotClip.Sneeze, OnboardingMascotClip.ClawSnap }[_random.Next(6)],
            _ => Clip,
        };
    }

    private double SleepDelay() => (45 + _random.NextDouble() * 35) * (_night ? .55 : 1);
}
