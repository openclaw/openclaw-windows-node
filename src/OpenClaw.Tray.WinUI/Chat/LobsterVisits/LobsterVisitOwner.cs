// The resident lobster's visit lifecycle for one composer: when it arrives and leaves, what it does while
// it is there, how it reacts to runs, disconnects, pokes and pets, and how it is dismissed. A faithful port of
// LobsterPetOwner and LobsterPetInteractions from OpenClaw's Control UI at
// 4cffd43ee043275f6a4c693aaacee8d8e5a61288 (MIT; see THIRD_PARTY_NOTICES.md), ui/src/components/
// lobster-pet-owner.ts, lobster-pet-interactions.ts and lobster-pet-timers.ts. The owner has no UI
// dependency: renderers measure the composer, feed scene/input/pointer changes in, and draw Snapshot()
// whenever Changed fires. All work runs on the scheduler's thread; the owner is not thread-safe.
//
// Not ported (later layers): ledge passers and bottles, molts, twins, the Elder, old friends, the
// Lobsterdex, moving day, sailor days, sounds.
//
// Usage:
//   var owner = new LobsterVisitOwner(scheduler, journal);
//   owner.Changed += () => Render(owner.Snapshot());
//   owner.VisitsDisabled += () => settings.ShowLobsterVisits = false;
//   owner.Connect(new LobsterVisitInputs(seed, mode));
//   owner.UpdateScene(LobsterSceneResolver.Resolve(...));
//   owner.Dispose(); // on unload; timers stop immediately
namespace OpenClawTray.Chat.LobsterVisits;

/// <summary>The owner's clock and single-threaded timer source; production uses the UI dispatcher.</summary>
internal interface ILobsterVisitScheduler
{
    /// <summary>Monotonic milliseconds for the poke window and gaze throttle.</summary>
    double ElapsedMs { get; }

    /// <summary>Local wall-clock time for seasonal looks and night-time naps.</summary>
    DateTime LocalNow { get; }

    /// <summary>Runs <paramref name="callback"/> once after <paramref name="delayMs"/>; disposing cancels it.</summary>
    IDisposable Schedule(double delayMs, Action callback);
}

/// <summary>Local familiarity counters. Implementations are best-effort: storage failures must not break visits.</summary>
internal interface ILobsterVisitJournal
{
    LobsterFamiliarity ReadFamiliarity();
    void RecordArrival();
    void RecordShoo();
}

/// <summary>Named timer slots; scheduling a slot replaces it, and firing releases it before the callback runs.</summary>
internal sealed class LobsterTimers<TName>(ILobsterVisitScheduler scheduler, Action? onFire = null) where TName : notnull
{
    private readonly Dictionary<TName, IDisposable> _pending = [];

    internal bool Has(TName name) => _pending.ContainsKey(name);

    internal void Schedule(TName name, double delayMs, Action callback)
    {
        Clear(name);
        IDisposable? handle = null;
        handle = scheduler.Schedule(delayMs, () =>
        {
            if (!_pending.TryGetValue(name, out var current) || !ReferenceEquals(current, handle))
                return;
            _pending.Remove(name);
            callback();
            onFire?.Invoke();
        });
        _pending[name] = handle;
    }

    internal void Clear(params TName[] names)
    {
        foreach (var name in names)
        {
            if (_pending.Remove(name, out var handle))
                handle.Dispose();
        }
    }
}

/// <summary>
/// Host inputs. <see cref="Seed"/> identifies one composer opening: a new seed is a new cast and schedule.
/// <see cref="ResidentEnabled"/> is false for themes without the mascot.
/// </summary>
internal sealed record LobsterVisitInputs(
    uint Seed,
    LobsterVisitMode Mode = LobsterVisitMode.Idle,
    bool VisitsEnabled = true,
    bool ResidentEnabled = true,
    bool FloorEnabled = false,
    LobsterRunOutcome RunOutcome = LobsterRunOutcome.Ok);

internal enum LobsterPresence { Out, In, Leaving }

/// <summary>Everything a renderer needs for one frame of the visit.</summary>
internal sealed record LobsterVisitSnapshot(
    LobsterLook Look,
    string Title,
    LobsterVisitMode Mode,
    LobsterPresence Presence,
    bool Entering,
    LobsterEntrance Entrance,
    LobsterAct? Act,
    double SpotPct,
    int Facing,
    LobsterAnchor Anchor,
    LobsterTravel? Travel,
    bool Grumpy,
    bool Vigil,
    bool MenuOpen,
    LobsterScene Scene)
{
    /// <summary>The resident is drawn while present or leaving, and only when the composer has a top lane.</summary>
    internal bool ShowsResident => Presence != LobsterPresence.Out && Scene.Top is not null;
}

internal sealed class LobsterVisitOwner : IDisposable
{
    private enum ResidentTimer { Idle, ActEnd, Enter, Visit, Leave, Vigil }

    private enum InteractionTimer { Grumpy, Hold }

    private readonly ILobsterVisitScheduler _scheduler;
    private readonly ILobsterVisitJournal _journal;
    private readonly LobsterTimers<ResidentTimer> _timers;
    private readonly LobsterTimers<InteractionTimer> _interactionTimers;
    private LobsterVisitInputs _inputs = new(0);
    private LobsterScene _scene = LobsterScene.Empty;
    private LobsterScene _travelScene = LobsterScene.Empty;
    private LobsterLook? _look;
    private string _title = "";
    private Func<double> _rng = LobsterRandom.Mulberry32(0);
    private Func<double> _motionRng = LobsterRandom.Mulberry32(0);
    private Func<double> _visitRng = LobsterRandom.Mulberry32(0);
    private Func<double> _entranceRng = LobsterRandom.Mulberry32(0);
    private LobsterFamiliarity _familiarity = LobsterFamiliarity.New;
    private LobsterAct? _act;
    private double _spotPct = 80;
    private int _facing = 1;
    private bool _entering;
    private LobsterEntrance _entrance;
    private LobsterPresence _presence;
    private LobsterAnchor _anchor;
    private LobsterTravel? _travel;
    private bool _scheduledVisiting, _dismissed, _menuOpen, _grumpy, _vigil, _outcomeOwnsPresence;
    private bool _greetedThisLoad, _restartPending, _active, _reducedMotion, _hostVisible = true;
    private readonly List<double> _pokeTimes = [];
    private double? _lastGazeAt;

    internal LobsterVisitOwner(ILobsterVisitScheduler scheduler, ILobsterVisitJournal journal)
    {
        _scheduler = scheduler;
        _journal = journal;
        _timers = new(scheduler, Refresh);
        _interactionTimers = new(scheduler);
    }

    /// <summary>Raised after any visible change; read <see cref="Snapshot"/>.</summary>
    internal event Action? Changed;

    /// <summary>Raised once when the user chooses "Dismiss and don't show again"; the host persists it.</summary>
    internal event Action? VisitsDisabled;

    /// <summary>Starts a fresh opening for <paramref name="inputs"/>. The cast and schedule derive from its seed.</summary>
    internal void Connect(LobsterVisitInputs inputs)
    {
        _look = null;
        _active = true;
        _inputs = inputs;
        Update(inputs);
    }

    internal void Update(LobsterVisitInputs inputs)
    {
        var previous = _inputs;
        _inputs = inputs;
        Reconcile(previous);
        if (_active)
            Publish();
    }

    /// <summary>New collision lanes from the renderer's measurement. Equal scenes are ignored.</summary>
    internal void UpdateScene(LobsterScene scene)
    {
        if (scene == _scene)
            return;
        _scene = scene;
        Refresh();
    }

    /// <summary>
    /// Reduced motion (Windows animation effects off) keeps the visitor calm and stationary: no acts, pets or
    /// gaze. Enabling it mid-visit stops queued and running acts at once; turning it back off resumes the act loop.
    /// </summary>
    internal void SetReducedMotion(bool reducedMotion)
    {
        if (reducedMotion == _reducedMotion)
            return;
        _reducedMotion = reducedMotion;
        if (reducedMotion)
        {
            HandleHoldCancel();
            StopActing();
            _entering = false;
        }
        else
        {
            ScheduleNextAct();
        }
        Refresh();
    }

    /// <summary>
    /// Hidden hosts (minimized, occluded tab) stop acting, cancel a pending pet and admit no new acts, so a
    /// run finishing while hidden ends any outcome presence quietly.
    /// </summary>
    internal void SetHostVisible(bool visible)
    {
        if (visible == _hostVisible)
            return;
        _hostVisible = visible;
        if (!visible)
        {
            HandleHoldCancel();
            StopActing();
        }
        else
        {
            ScheduleNextAct();
        }
        Refresh();
    }

    // Upstream's visibilitychange handler: an interrupted outcome chain no longer holds the visitor.
    private void StopActing()
    {
        _outcomeOwnsPresence = false;
        ClearActTimers();
        _act = null;
    }

    public void Dispose()
    {
        _active = false;
        ClearActTimers();
        _restartPending = false;
        _scheduledVisiting = false;
        _presence = LobsterPresence.Out;
        _act = null;
        _timers.Clear(ResidentTimer.Visit, ResidentTimer.Leave, ResidentTimer.Vigil);
        HandleHoldCancel();
        _interactionTimers.Clear(InteractionTimer.Grumpy);
    }

    internal LobsterVisitSnapshot? Snapshot() => _look is null
        ? null
        : new(_look, _title, _inputs.Mode, _presence, _entering, _entrance, _act, _spotPct, _facing, _anchor,
            _travel, _grumpy, _vigil, _menuOpen, _scene);

    // Pointer input. Press-and-hold pets; a quick press-release pokes. Three fast pokes turn the visitor
    // grumpy for a minute; ten end its visit. Offline visitors never huff off.

    internal void HandleHoldStart()
    {
        if (_reducedMotion)
            return;
        HandleHoldCancel();
        _interactionTimers.Schedule(InteractionTimer.Hold, LobsterVisitPlans.PetHoldMs, () =>
        {
            SetGrumpy(false);
            PerformAct(LobsterAct.Pet);
            Refresh();
        });
    }

    internal void HandleHoldEnd()
    {
        if (!_interactionTimers.Has(InteractionTimer.Hold))
            return;
        HandleHoldCancel();
        var now = _scheduler.ElapsedMs;
        _pokeTimes.RemoveAll(at => now - at >= LobsterVisitPlans.PokeWindowMs);
        _pokeTimes.Add(now);
        if (_pokeTimes.Count >= LobsterVisitPlans.HuffPokes && _inputs.Mode != LobsterVisitMode.Offline)
        {
            _pokeTimes.Clear();
            SetGrumpy(false);
            // Ends this visit only; the resident returns after an ordinary gap.
            _timers.Clear(ResidentTimer.Visit, ResidentTimer.Leave);
            _scheduledVisiting = false;
            ArmArrival(LobsterRandom.Between(_visitRng, LobsterVisitPlans.GapMs.Min, LobsterVisitPlans.GapMs.Max));
            Refresh();
            return;
        }
        if (_pokeTimes.Count >= LobsterVisitPlans.GrumpyPokes)
        {
            SetGrumpy(true);
            _interactionTimers.Schedule(InteractionTimer.Grumpy, LobsterVisitPlans.GrumpyMs, () => SetGrumpy(false));
        }
        PerformAct(LobsterAct.Startle);
        Refresh();
    }

    internal void HandleHoldCancel() => _interactionTimers.Clear(InteractionTimer.Hold);

    /// <summary>Between acts the visitor turns to watch the pointer (throttled).</summary>
    internal void HandlePointerMoved(double pointerX, double spriteCenterX)
    {
        if (_presence != LobsterPresence.In || _act is not null || _vigil || _reducedMotion)
            return;
        var now = _scheduler.ElapsedMs;
        if (now - _lastGazeAt < LobsterVisitPlans.GazeThrottleMs)
            return;
        _lastGazeAt = now;
        _facing = pointerX < spriteCenterX ? -1 : 1;
        Refresh();
    }

    /// <summary>Right-click. Returns false when there is no visitor to dismiss.</summary>
    internal bool OpenMenu()
    {
        if (!_inputs.ResidentEnabled || !_inputs.VisitsEnabled)
            return false;
        HandleHoldCancel();
        _menuOpen = true;
        Refresh();
        return true;
    }

    internal void CloseMenu()
    {
        _menuOpen = false;
        Refresh();
    }

    /// <summary>
    /// Shoos the visitor for this opening; dismissal outlasts later scheduled visits and offline summons.
    /// <paramref name="permanently"/> also turns visits off through <see cref="VisitsDisabled"/>.
    /// </summary>
    internal void Dismiss(bool permanently)
    {
        _menuOpen = false;
        _dismissed = true;
        _journal.RecordShoo();
        if (permanently)
        {
            _inputs = _inputs with { VisitsEnabled = false };
            VisitsDisabled?.Invoke();
        }
        Refresh();
    }

    private void SetGrumpy(bool grumpy)
    {
        _grumpy = grumpy;
        Refresh();
    }

    // Upstream suspend(): its grumpy reset refreshes immediately, even mid-reconcile.
    private void SuspendInteractions()
    {
        HandleHoldCancel();
        _interactionTimers.Clear(InteractionTimer.Grumpy);
        SetGrumpy(false);
    }

    private void Publish() => Changed?.Invoke();

    private void Refresh()
    {
        if (!_active)
            return;
        Reconcile(_inputs);
        Publish();
    }

    private bool WantsVisible() =>
        _inputs.VisitsEnabled && _inputs.ResidentEnabled && !_dismissed
        && (_inputs.Mode == LobsterVisitMode.Offline || _vigil || _outcomeOwnsPresence || _scheduledVisiting);

    private void Reconcile(LobsterVisitInputs previous)
    {
        if (!_active)
            return;
        var seedChanged = _look is null || previous.Seed != _inputs.Seed;
        if (seedChanged)
        {
            var seed = _inputs.Seed;
            var look = LobsterLooks.Create(seed, _scheduler.LocalNow);
            _look = look;
            _rng = LobsterRandom.Mulberry32(seed ^ 0x9e3779b9);
            _motionRng = LobsterRandom.Mulberry32(seed ^ 0xf1002);
            _visitRng = LobsterRandom.Mulberry32(seed ^ 0x5eaf00d);
            _entranceRng = LobsterRandom.Mulberry32(seed ^ 0xe27a);
            _spotPct = look.SpotPct;
            _facing = look.Facing;
            // A press on the previous opening must not pet the newly rolled visitor.
            HandleHoldCancel();
            ClearActTimers();
            _act = null;
            _dismissed = false;
            _menuOpen = false;
            _presence = LobsterPresence.Out;
            _familiarity = _journal.ReadFamiliarity();
            _title = LobsterLooks.Title(look, seed, _familiarity.Visits);
            _greetedThisLoad = false;
            ScheduleVisits();
            // The first update takes this branch, so arm the vigil tracker here as well.
            _vigil = false;
            _outcomeOwnsPresence = false;
            TrackVigil();
        }
        else if (previous.Mode != _inputs.Mode)
        {
            var finished = previous.Mode == LobsterVisitMode.Busy && _inputs.Mode == LobsterVisitMode.Idle;
            var ownsPresence = finished && _vigil;
            TrackVigil();
            if (_presence == LobsterPresence.In && !_reducedMotion)
                PerformAct(finished ? LobsterVisitPlans.FinishAct(_inputs.RunOutcome) : LobsterAct.Startle, ownsPresence);
        }
        if (previous.VisitsEnabled != _inputs.VisitsEnabled || previous.ResidentEnabled != _inputs.ResidentEnabled)
        {
            if (_inputs.VisitsEnabled && !previous.VisitsEnabled)
                _dismissed = false;
            if (!_inputs.VisitsEnabled || !_inputs.ResidentEnabled)
            {
                ClearActTimers();
                _timers.Clear(ResidentTimer.Visit, ResidentTimer.Leave);
                SuspendInteractions();
                _timers.Clear(ResidentTimer.Vigil);
                _vigil = false;
                _outcomeOwnsPresence = false;
                _menuOpen = false;
                _scheduledVisiting = false;
                _presence = LobsterPresence.Out;
                _act = null;
                _entering = false;
                _restartPending = false;
            }
            else if (!previous.VisitsEnabled || !previous.ResidentEnabled)
            {
                ScheduleVisits();
                TrackVigil();
            }
        }
        // Losing an empty floor is immediate: never animate back through newly entered text or controls.
        if (((!_inputs.FloorEnabled || _scene.Floor is null) && _anchor == LobsterAnchor.Floor)
            || (_travel is not null && _travelScene != _scene))
        {
            ClearActTimers();
            _act = null;
            _anchor = LobsterAnchor.Top;
            _restartPending = _presence == LobsterPresence.In;
        }
        ReconcilePresence();
        if (!_active || !_restartPending)
            return;
        _restartPending = false;
        _timers.Schedule(ResidentTimer.Enter, LobsterVisitPlans.EntranceMs(_entrance), () =>
        {
            _entering = false;
            if (!_greetedThisLoad && _familiarity.Tier == LobsterFamiliarityTier.Friend
                && _presence == LobsterPresence.In && !_reducedMotion)
            {
                _greetedThisLoad = true;
                PerformAct(LobsterAct.Wave);
            }
        });
        ScheduleNextAct();
    }

    private void ReconcilePresence()
    {
        var visible = WantsVisible();
        if (visible && _presence != LobsterPresence.In)
        {
            _timers.Clear(ResidentTimer.Leave);
            if (_presence == LobsterPresence.Out)
            {
                _anchor = LobsterAnchor.Top;
                _spotPct = LobsterRandom.Round(LobsterRandom.Between(_visitRng, 12, 88));
                _facing = _visitRng() < .5 ? 1 : -1;
                // Entrance rolls burn once per arrival, aligned across scheduled visits and offline summons.
                _entrance = LobsterVisitPlans.PickEntrance(_entranceRng());
                _journal.RecordArrival();
            }
            _presence = LobsterPresence.In;
            _entering = !_reducedMotion;
            _restartPending = true;
            return;
        }
        if (!visible && _presence == LobsterPresence.In)
        {
            _menuOpen = false;
            _outcomeOwnsPresence = false;
            ClearActTimers();
            _act = null;
            _entering = false;
            _presence = LobsterPresence.Leaving;
            _timers.Schedule(ResidentTimer.Leave, LobsterVisitPlans.LeaveMs, () => _presence = LobsterPresence.Out);
        }
    }

    private void TrackVigil()
    {
        _timers.Clear(ResidentTimer.Vigil);
        if (_inputs.Mode == LobsterVisitMode.Busy && _inputs.VisitsEnabled && _inputs.ResidentEnabled && !_dismissed)
        {
            _timers.Schedule(ResidentTimer.Vigil, LobsterVisitPlans.VigilAfterMs, () =>
            {
                _vigil = true;
                ClearActTimers();
                _act = null;
            });
        }
        else
        {
            _vigil = false;
        }
    }

    private void ClearActTimers()
    {
        _travel = null;
        _timers.Clear(ResidentTimer.Idle, ResidentTimer.ActEnd, ResidentTimer.Enter);
    }

    private void ScheduleVisits()
    {
        _timers.Clear(ResidentTimer.Visit, ResidentTimer.Leave);
        _scheduledVisiting = false;
        if (!_inputs.VisitsEnabled || !_inputs.ResidentEnabled)
            return;
        // A shy share of openings never visits on its own; offline still summons.
        if (_visitRng() < LobsterVisitPlans.ShyChance)
            return;
        var tuning = LobsterFamiliarityTuning.For(_familiarity.Tier);
        ArmArrival(LobsterRandom.Between(_visitRng, LobsterVisitPlans.FirstDelayMs.Min, LobsterVisitPlans.FirstDelayMs.Max)
            * tuning.FirstDelayMultiplier);
    }

    private void ArmArrival(double delayMs)
    {
        if (!_active || !_inputs.VisitsEnabled || !_inputs.ResidentEnabled)
            return;
        _timers.Schedule(ResidentTimer.Visit, delayMs, () =>
        {
            _scheduledVisiting = true;
            var tuning = LobsterFamiliarityTuning.For(_familiarity.Tier);
            var stayMs = LobsterRandom.Between(_visitRng, LobsterVisitPlans.StayMs.Min, LobsterVisitPlans.StayMs.Max)
                * tuning.StayMultiplier;
            _timers.Schedule(ResidentTimer.Visit, stayMs, () =>
            {
                _scheduledVisiting = false;
                var wary = _familiarity.Wary ? LobsterFamiliarityTuning.WaryGapMultiplier : 1;
                ArmArrival(LobsterRandom.Between(_visitRng, LobsterVisitPlans.GapMs.Min, LobsterVisitPlans.GapMs.Max)
                    * tuning.GapMultiplier * wary);
            });
        });
    }

    private void ScheduleNextAct()
    {
        // Guard here, not only at arrival: resumes must also stay inert for reduced motion and departed pets.
        if (!_active || !_inputs.VisitsEnabled || !_inputs.ResidentEnabled || _look is null
            || _presence != LobsterPresence.In || _vigil
            || _timers.Has(ResidentTimer.Idle) || _timers.Has(ResidentTimer.ActEnd) || _reducedMotion)
        {
            return;
        }
        var profile = LobsterVisitPlans.ResolveActProfile(_inputs.Mode, _look.Personality, _scheduler.LocalNow);
        var delay = LobsterRandom.Between(_rng, profile.DelayMs.Min, profile.DelayMs.Max);
        _timers.Schedule(ResidentTimer.Idle, delay, () =>
        {
            if (!_hostVisible || _presence != LobsterPresence.In || _look is null)
                return;
            var next = LobsterVisitPlans.ResolveActProfile(_inputs.Mode, _look.Personality, _scheduler.LocalNow);
            PerformAct(LobsterRandom.PickWeighted(_rng, next.Acts));
        });
    }

    /// <summary>
    /// An outcome chain (finish act, droop then sweep) carries its own presence across linked acts so a
    /// vigil-only visitor stays to react before leaving.
    /// </summary>
    private void PerformAct(LobsterAct act, bool ownsPresence = false)
    {
        if (!_inputs.VisitsEnabled || !_inputs.ResidentEnabled || _presence != LobsterPresence.In || !_hostVisible)
            return;
        ClearActTimers();
        _outcomeOwnsPresence = ownsPresence;
        _entering = false;
        if (act == LobsterAct.Hop)
            StartFloorHop();
        else if (act == LobsterAct.Scuttle)
            ApplyMove(LobsterSceneResolver.PlanWalk(_scene, _anchor, _spotPct, _rng()));
        var duration = _travel?.DurationMs ?? LobsterVisitPlans.ActDurationMs(act);
        _act = _travel is { Hop: false } ? LobsterAct.Scuttle : act;
        _timers.Schedule(ResidentTimer.ActEnd, duration, () =>
        {
            _act = null;
            _travel = null;
            if (act == LobsterAct.Droop)
            {
                // After bad news, some lobsters tidy the ledge.
                PerformAct(LobsterAct.Sweep, ownsPresence);
                return;
            }
            _outcomeOwnsPresence = false;
            if (WantsVisible())
                ScheduleNextAct();
        });
    }

    private void ApplyMove(LobsterMove? move)
    {
        if (move is null)
            return;
        _anchor = move.Anchor;
        _spotPct = move.SpotPct;
        _facing = move.Facing;
        _travel = move.Travel;
        _travelScene = _scene;
    }

    private void StartFloorHop()
    {
        // Top-lane visitors usually hop in place; some take the passage down to the floor.
        if (!_inputs.FloorEnabled || (_anchor == LobsterAnchor.Top && _motionRng() >= .45))
            return;
        ApplyMove(LobsterSceneResolver.PlanHop(_scene, _anchor, _spotPct));
    }
}

/// <summary>
/// Upstream <c>offlineStable</c>: the Gateway counts as offline only after it has stayed disconnected for
/// <see cref="DelayMs"/>; reconnecting clears it at once. Brief reconnect blips never summon a worried visitor.
/// </summary>
internal sealed class LobsterOfflineTracker(ILobsterVisitScheduler scheduler) : IDisposable
{
    /// <summary>Upstream OFFLINE_INDICATOR_DELAY_MS in ui/src/app/gateway-store.availability.ts.</summary>
    internal const double DelayMs = 2_000;

    private IDisposable? _pending;

    internal bool OfflineStable { get; private set; }

    internal event Action? Changed;

    internal void Update(bool connected)
    {
        if (connected)
        {
            _pending?.Dispose();
            _pending = null;
            if (OfflineStable)
            {
                OfflineStable = false;
                Changed?.Invoke();
            }
            return;
        }
        if (OfflineStable || _pending is not null)
            return;
        _pending = scheduler.Schedule(DelayMs, () =>
        {
            _pending = null;
            OfflineStable = true;
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
