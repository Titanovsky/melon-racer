using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ambi.MelonRacer;

public sealed class GameManager : Component
{
    public static GameManager Instance { get; private set; }

    [Property] public MapInstance MapInstance { get; set; }

    [Sync( SyncFlags.FromHost )] public TimeUntil RaceTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsRaceActive { get; private set; }
    [Sync( SyncFlags.FromHost )] public int TotalLaps { get; private set; } = 1;

    public global::MapInfo CurrentMapInfo { get; private set; }
    public IReadOnlyList<int> SegmentIds => _segmentIds;

    private readonly List<int> _segmentIds = new();

    public float RaceElapsed => IsRaceActive ? MathF.Max( 0f, -(float)RaceTimer ) : 0f;
    public int FirstSegmentId => _segmentIds.Count > 0 ? _segmentIds[0] : 0;

    protected override void OnAwake()
    {
        if (Instance == null)
            Instance = this;
    }

    protected override void OnDestroy()
    {
        if (Instance != null)
            Instance = null;
    }

    protected override void OnStart()
    {
        if ( !Networking.IsHost )
            return;

        SetupRace();

        Log.Info(MapInstance.MapName);
    }

    public void RegisterMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() )
            return;

        if ( _segmentIds.Count == 0 )
            SetupRace();

        melon.ResetRaceProgress( FirstSegmentId, RaceElapsed );
    }

    public void PassSegment( Melon melon, int segmentId )
    {
        if ( !Networking.IsHost || !melon.IsValid() || melon.HasFinishedRace )
            return;

        if ( _segmentIds.Count == 0 )
            SetupRace();

        if ( segmentId != melon.ActiveSegmentId )
            return;

        if ( TryGetNextSegmentId( segmentId, out var nextSegmentId ) )
        {
            melon.SetActiveSegment( nextSegmentId );
            return;
        }

        var lapTime = RaceElapsed - melon.CurrentLapStartedAt;
        var completedLaps = melon.CompletedLaps + 1;

        melon.CompleteLap( completedLaps, lapTime, FirstSegmentId, RaceElapsed );

        if ( completedLaps >= TotalLaps )
        {
            melon.FinishRace();
            // todo: switch to the next map after every player completes all laps.
        }
    }

    private void SetupRace()
    {
        var startTimer = !IsRaceActive;

        CurrentMapInfo = Scene.GetAllComponents<global::MapInfo>().FirstOrDefault();
        TotalLaps = Math.Max( 1, CurrentMapInfo?.Laps ?? 1 );

        _segmentIds.Clear();
        _segmentIds.AddRange( Scene.GetAllComponents<global::TriggerSegment>()
            .Select( segment => segment.SegmentId )
            .Distinct()
            .OrderBy( segmentId => segmentId ) );

        if ( _segmentIds.Count == 0 )
            _segmentIds.Add( 0 );

        if ( startTimer )
        {
            RaceTimer = 0f;
            IsRaceActive = true;
        }
    }

    private bool TryGetNextSegmentId( int segmentId, out int nextSegmentId )
    {
        foreach ( var candidate in _segmentIds )
        {
            if ( candidate > segmentId )
            {
                nextSegmentId = candidate;
                return true;
            }
        }

        nextSegmentId = FirstSegmentId;
        return false;
    }
}
