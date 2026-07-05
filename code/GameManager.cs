using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ambi.MelonRacer;

public sealed class GameManager : Component
{
    private const float MapVoteDuration = 10f;

    public static readonly string[] MapVoteChoices =
    {
        "titanovsky.melon_lestoria",
        "titanovsky.melon_snowball"
    };

    public static GameManager Instance { get; private set; }

    [Property] public MapInstance MapInstance { get; set; }

    [Sync( SyncFlags.FromHost )] public TimeUntil RaceTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public TimeUntil MapVoteTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsRaceActive { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsMapVoteOpen { get; private set; }
    [Sync( SyncFlags.FromHost )] public int LestoriaVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int SnowballVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int TotalLaps { get; private set; } = 1;

    public global::MapInfo CurrentMapInfo { get; private set; }
    public IReadOnlyList<int> SegmentIds => _segmentIds;

    private readonly List<int> _segmentIds = new();
    private readonly Dictionary<string, string> _mapVotes = new();
    private GameObject _spawnedMapObject;
    private bool _mapEventsHooked;
    private string _spawnedMapPrefabPath;

    public float RaceElapsed => IsRaceActive ? MathF.Max( 0f, -(float)RaceTimer ) : 0f;
    public float MapVoteSecondsLeft => IsMapVoteOpen ? MathF.Max( 0f, MapVoteTimer.Relative ) : 0f;
    public int FirstSegmentId => _segmentIds.Count > 0 ? _segmentIds[0] : 0;

    protected override void OnAwake()
    {
        if (Instance == null)
            Instance = this;
    }

    protected override void OnDestroy()
    {
        UnhookMapEvents();

        if (Instance != null)
            Instance = null;
    }

    protected override void OnStart()
    {
        HookMapEvents();

        if ( !Networking.IsHost )
            return;

        if ( MapInstance.IsValid() && MapInstance.IsLoaded )
            StartRoundFromLoadedMap();
    }

    protected override void OnUpdate()
    {
        if ( !Networking.IsHost || !IsMapVoteOpen || !MapVoteTimer )
            return;

        FinishMapVote();
    }

    public void RegisterMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() )
            return;

        if ( _segmentIds.Count == 0 )
            SetupRace();

        melon.ResetRaceProgress( FirstSegmentId, RaceElapsed );

        RespawnMelon( melon );
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
            OpenMapVote();
        }
    }

    public void VoteForMap( string mapName )
    {
        if ( !IsMapVoteOpen || !MapVoteChoices.Contains( mapName ) || !MapInstance.IsValid() )
            return;

        var voterId = GetLocalVoterId();
        var voterName = GetLocalVoterName();

        if ( Networking.IsHost )
        {
            RegisterMapVote( mapName, voterId, voterName );
            return;
        }

        SubmitMapVote( mapName, voterId, voterName );
    }

    [Rpc.Host]
    private void SubmitMapVote( string mapName, string voterId, string voterName )
    {
        var caller = Rpc.Caller;
        if ( caller != null )
        {
            voterId = caller.Id.ToString();
            voterName = caller.Name;
        }

        Log.Info( $"Vote RPC received from {GetVoterLogName( voterId, voterName )}: {mapName}" );
        RegisterMapVote( mapName, voterId, voterName );
    }

    private void RegisterMapVote( string mapName, string voterId, string voterName )
    {
        if ( !Networking.IsHost )
            return;

        if ( !IsMapVoteOpen )
        {
            Log.Info( $"Vote ignored from {GetVoterLogName( voterId, voterName )}: vote is closed" );
            return;
        }

        if ( !MapVoteChoices.Contains( mapName ) )
        {
            Log.Info( $"Vote ignored from {GetVoterLogName( voterId, voterName )}: unknown map {mapName}" );
            return;
        }

        if ( string.IsNullOrWhiteSpace( voterId ) )
        {
            Log.Info( $"Vote ignored: empty voter id for {mapName}" );
            return;
        }

        _mapVotes[voterId] = mapName;
        RecountMapVotes();
        Log.Info( $"Vote accepted from {GetVoterLogName( voterId, voterName )}: {mapName}. {MapVoteChoices[0]}={LestoriaVotes}, {MapVoteChoices[1]}={SnowballVotes}" );
    }

    private static string GetLocalVoterId()
    {
        return Connection.Local != null ? Connection.Local.Id.ToString() : "local";
    }

    private static string GetLocalVoterName()
    {
        return Connection.Local != null ? Connection.Local.Name : "Local Host";
    }

    private static string GetVoterLogName( string voterId, string voterName )
    {
        return string.IsNullOrWhiteSpace( voterName ) ? voterId : $"{voterName} ({voterId})";
    }

    private void SetupRace()
    {
        CurrentMapInfo ??= Scene.GetAllComponents<global::MapInfo>().FirstOrDefault();
        TotalLaps = Math.Max( 1, CurrentMapInfo?.Laps ?? 1 );

        IEnumerable<global::TriggerSegment> segments = CurrentMapInfo.IsValid()
            ? CurrentMapInfo.GameObject.GetComponentsInChildren<global::TriggerSegment>( true, true )
            : Scene.GetAllComponents<global::TriggerSegment>();

        _segmentIds.Clear();
        _segmentIds.AddRange( segments
            .Select( segment => segment.SegmentId )
            .Distinct()
            .OrderBy( segmentId => segmentId ) );

        if ( _segmentIds.Count == 0 )
            _segmentIds.Add( 0 );

        RaceTimer = 0f;
        IsRaceActive = true;
    }

    private void StartRoundFromLoadedMap()
    {
        if ( !Networking.IsHost )
            return;

        CloseMapVote();
        SpawnMapInfoPrefab();
        SetupRace();
        RespawnAllMelons();
    }

    private void HandleMapUnloaded()
    {
        if ( !Networking.IsHost )
            return;

        if ( CurrentMapInfo.IsValid() )
            CurrentMapInfo.GameObject.Destroy();
        else if ( _spawnedMapObject.IsValid() )
            _spawnedMapObject.Destroy();

        CurrentMapInfo = null;
        _spawnedMapObject = null;
        _spawnedMapPrefabPath = null;
        _segmentIds.Clear();
        IsRaceActive = false;
        CloseMapVote();
        TotalLaps = 1;
    }

    private void OpenMapVote()
    {
        if ( !Networking.IsHost )
            return;

        if ( IsMapVoteOpen )
            return;

        IsRaceActive = false;
        IsMapVoteOpen = true;
        MapVoteTimer = MapVoteDuration;
        _mapVotes.Clear();
        RecountMapVotes();
        Log.Info( "Map vote opened" );
    }

    private void FinishMapVote()
    {
        var selectedMap = LestoriaVotes >= SnowballVotes
            ? MapVoteChoices[0]
            : MapVoteChoices[1];

        Log.Info( $"Map vote finished. Selected {selectedMap}. {MapVoteChoices[0]}={LestoriaVotes}, {MapVoteChoices[1]}={SnowballVotes}" );
        CloseMapVote();
        ChangeMap( selectedMap );
    }

    private void ChangeMap( string mapName )
    {
        if ( !MapInstance.IsValid() )
            return;

        if ( MapInstance.MapName == mapName )
        {
            StartRoundFromLoadedMap();
            return;
        }

        MapInstance.MapName = mapName;
    }

    private void CloseMapVote()
    {
        IsMapVoteOpen = false;
        MapVoteTimer = 0f;
        _mapVotes.Clear();
        LestoriaVotes = 0;
        SnowballVotes = 0;
    }

    private void RecountMapVotes()
    {
        LestoriaVotes = _mapVotes.Values.Count( mapName => mapName == MapVoteChoices[0] );
        SnowballVotes = _mapVotes.Values.Count( mapName => mapName == MapVoteChoices[1] );
    }

    private void SpawnMapInfoPrefab()
    {
        var prefabName = GetFormattedMapName();
        if ( string.IsNullOrWhiteSpace( prefabName ) )
            return;

        var prefabPath = $"prefabs/{prefabName}.prefab";
        if ( _spawnedMapObject.IsValid() && _spawnedMapPrefabPath == prefabPath )
            return;

        var prefab = GameObject.GetPrefab( prefabPath );
        if ( !prefab.IsValid() )
        {
            Log.Warning( $"MapInfo prefab not found: {prefabPath}" );
            return;
        }

        if ( _spawnedMapObject.IsValid() )
            _spawnedMapObject.Destroy();

        _spawnedMapObject = GameObject.Clone( prefabPath );
        _spawnedMapObject.NetworkSpawn();
        _spawnedMapPrefabPath = prefabPath;

        CurrentMapInfo = _spawnedMapObject.GetComponentInChildren<global::MapInfo>( true, true );
        CurrentMapInfo ??= Scene.GetAllComponents<global::MapInfo>().FirstOrDefault();
    }

    private void RespawnAllMelons()
    {
        foreach ( var melon in Scene.GetAllComponents<Melon>() )
        {
            if ( !melon.IsValid() )
                continue;

            melon.ResetRaceProgress( FirstSegmentId, RaceElapsed );
            RespawnMelon( melon );
        }
    }

    private void RespawnMelon( Melon melon )
    {
        var spawn = GetRandomMelonSpawn();
        if ( !spawn.IsValid() )
            return;

        var owner = melon.GameObject.Network.Owner;
        if ( owner != null && owner.IsActive )
        {
            using ( Rpc.FilterInclude( owner ) )
            {
                RespawnLocalMelon( spawn.WorldPosition, spawn.WorldRotation );
            }

            return;
        }

        if ( !melon.IsProxy )
            melon.RespawnAt( spawn.WorldPosition, spawn.WorldRotation );
    }

    [Rpc.Broadcast]
    private void RespawnLocalMelon( Vector3 position, Rotation rotation )
    {
        if ( !Melon.Local.IsValid() )
            return;

        Melon.Local.RespawnAt( position, rotation );
    }

    private global::SpawnMelon GetRandomMelonSpawn()
    {
        if ( !CurrentMapInfo.IsValid() )
            return null;

        var spawns = CurrentMapInfo.SpawnsMelons
            .Where( spawn => spawn.IsValid() )
            .ToList();

        return spawns.Count > 0 ? Random.Shared.FromList( spawns ) : null;
    }

    private string GetFormattedMapName()
    {
        if ( !MapInstance.IsValid() )
            return string.Empty;

        var mapName = MapInstance.MapName;
        if ( string.IsNullOrWhiteSpace( mapName ) )
            return string.Empty;

        var parts = mapName.Split( '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        return parts.Length >= 2 ? parts[1] : mapName;
    }

    private void HookMapEvents()
    {
        if ( _mapEventsHooked || !MapInstance.IsValid() )
            return;

        MapInstance.OnMapLoaded += StartRoundFromLoadedMap;
        MapInstance.OnMapUnloaded += HandleMapUnloaded;
        _mapEventsHooked = true;
    }

    private void UnhookMapEvents()
    {
        if ( !_mapEventsHooked || !MapInstance.IsValid() )
            return;

        MapInstance.OnMapLoaded -= StartRoundFromLoadedMap;
        MapInstance.OnMapUnloaded -= HandleMapUnloaded;
        _mapEventsHooked = false;
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
