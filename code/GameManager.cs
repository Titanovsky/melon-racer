using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ambi.MelonRacer;

public sealed class GameManager : Component
{
    private const float MapVoteDuration = 10f;
	private const float SpawnOverlapRadius = 16f;
	private const float SpawnOverlapHeight = 10f;
	private const float MapPrefabSpawnDelay = 0.1f;
	private const float MapPrefabActivationDelay = 0.05f;
	private const float MapPrefabRetryDelay = 0.5f;
	private const int MapPrefabMaxAttempts = 10;

	private enum MapSetupStage
	{
		None,
		SpawnPrefab,
		ActivatePrefab
	}

    public static readonly string[] MapVoteChoices =
    {
        "titanovsky.melon_lestoria",
        "titanovsky.melon_snowball"
    };

    public static GameManager Instance { get; private set; }

    [Property] public MapInstance MapInstance { get; set; }
	[Property, Group( "Sounds" )] public SoundEvent ActiveSegmentSound { get; set; }
	[Property, Group( "Sounds" )] public SoundEvent FinalSegmentSound { get; set; }
	[Property, Group( "Sounds" )] public SoundEvent LevelStartSound { get; set; }

    [Sync( SyncFlags.FromHost )] public TimeUntil RaceTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public TimeUntil MapVoteTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsRaceActive { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsMapVoteOpen { get; private set; }
    [Sync( SyncFlags.FromHost )] public int LestoriaVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int SnowballVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int TotalLaps { get; private set; } = 1;
    [Sync( SyncFlags.FromHost )] public string ActiveMapName { get; private set; }
    [Sync( SyncFlags.FromHost )] public int ActiveMapRevision { get; private set; }

    public global::MapInfo CurrentMapInfo { get; private set; }
    public IReadOnlyList<int> SegmentIds => _segmentIds;

    private readonly List<int> _segmentIds = new();
    private readonly Dictionary<string, string> _mapVotes = new();
    private GameObject _spawnedMapObject;
    private bool _mapEventsHooked;
    private string _spawnedMapPrefabPath;
    private string _pendingMapName;
    private int _pendingMapRevision;
    private int _appliedMapRevision;
	private MapSetupStage _mapSetupStage;
	private TimeUntil _mapSetupTimer;
	private string _mapSetupMapName;
	private int _mapSetupAttempts;

    public float RaceElapsed => IsRaceActive ? MathF.Max( 0f, -(float)RaceTimer ) : 0f;
    public float MapVoteSecondsLeft => IsMapVoteOpen ? MathF.Max( 0f, MapVoteTimer.Relative ) : 0f;
    public int FirstSegmentId => _segmentIds.Count > 0 ? _segmentIds[0] : 0;

    protected override void OnAwake()
    {
		if ( Instance.IsValid() && Instance != this )
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Replacing stale GameManager.Instance '{Instance.GameObject.Name}' with '{GameObject.Name}'" );

		Instance = this;
		Log.Info( $"[MapLifecycle][{RuntimeSide}] GameManager OnAwake: instance assigned, object='{GameObject.Name}', networkMode={GameObject.NetworkMode}, networkRoot={GameObject.IsNetworkRoot}" );
    }

    protected override void OnDestroy()
    {
		Log.Info( $"[MapLifecycle][{RuntimeSide}] GameManager OnDestroy: unhooking map events and resetting runtime state" );
        UnhookMapEvents();
		ResetMapSetup();
		_mapVotes.Clear();
		_segmentIds.Clear();
		CurrentMapInfo = null;
		_spawnedMapObject = null;
		_spawnedMapPrefabPath = null;

		if ( Instance == this )
		{
			Instance = null;
			Log.Info( $"[MapLifecycle][{RuntimeSide}] GameManager.Instance reset" );
		}
    }

    protected override void OnStart()
    {
		LogMapState( "GameManager OnStart" );
        HookMapEvents();

        if ( Networking.IsHost )
            ActiveMapName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;
        else
            ApplyActiveMap();

        if ( MapInstance.IsValid() && MapInstance.IsLoaded )
            HandleMapLoaded();
    }

    protected override void OnUpdate()
    {
		ApplyPendingMapChange();
		UpdateMapSetup();

        if ( !Networking.IsHost )
        {
            ApplyActiveMap();
            return;
        }

		if ( IsMapVoteOpen && HaveAllPlayersVoted() )
		{
			FinishMapVote();
			return;
		}

        if ( !IsMapVoteOpen || !MapVoteTimer )
            return;

        FinishMapVote();
    }

    public void RegisterMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() )
            return;

		Log.Info( $"[MelonSpawn][HOST] RegisterMelon: object='{melon.GameObject.Name}', networkMode={melon.GameObject.NetworkMode}, networkRoot={melon.GameObject.IsNetworkRoot}, owner='{melon.GameObject.Network.Owner?.Name ?? "none"}', ownerActive={melon.GameObject.Network.Owner?.IsActive ?? false}, proxy={melon.IsProxy}, enabled={melon.GameObject.Enabled}, active={melon.GameObject.Active}" );

		if ( !CurrentMapInfo.IsValid() )
		{
			Log.Info( $"[MelonSpawn][HOST] RegisterMelon deferred for '{melon.GameObject.Name}': MapInfo is not ready for map '{GetMapLabel()}'" );
			return;
		}

        if ( _segmentIds.Count == 0 )
            SetupRace();

        melon.ResetRaceProgress( FirstSegmentId, RaceElapsed );

        RespawnMelon( melon );
    }

    public void SmashMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() )
            return;

        if ( !melon.BeginDeath() )
            return;

        Log.Info( $"Melon smashed: {melon.GameObject.Network.Owner?.Name ?? melon.GameObject.Name}" );
    }

    public void RespawnSmashedMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() || !melon.IsDead )
            return;

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

	public void PlayLocalSegmentSound( int segmentId, SoundEvent activeOverride = null, SoundEvent finalOverride = null )
	{
		if ( !IsRaceActive || IsMapVoteOpen )
			return;

		var sound = IsFinalSegment( segmentId )
			? finalOverride ?? FinalSegmentSound
			: activeOverride ?? ActiveSegmentSound;

		if ( sound is not null )
			Sound.Play( sound );
	}

	public bool IsFinalSegment( int segmentId )
	{
		IEnumerable<global::TriggerSegment> segments = CurrentMapInfo.IsValid()
			? CurrentMapInfo.GameObject.GetComponentsInChildren<global::TriggerSegment>( true, true )
			: Scene.GetAllComponents<global::TriggerSegment>();
		var segmentList = segments.ToList();
		var configuredFinalSegment = segmentList.FirstOrDefault( segment => segment.FinalSegment );

		if ( configuredFinalSegment.IsValid() )
			return segmentId == configuredFinalSegment.SegmentId;

		if ( _segmentIds.Count > 0 )
			return segmentId == _segmentIds[_segmentIds.Count - 1];

		var finalSegmentId = segmentList
			.Select( segment => segment.SegmentId )
			.DefaultIfEmpty( segmentId )
			.Max();

		return segmentId == finalSegmentId;
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

		if ( HaveAllPlayersVoted() )
		{
			var playerCount = Connection.All.Count();
			Log.Info( $"All players voted ({_mapVotes.Count}/{playerCount}), finishing map vote early" );
			FinishMapVote();
		}
    }

	private bool HaveAllPlayersVoted()
	{
		if ( !Networking.IsHost )
			return false;

		var playerCount = Connection.All.Count();
		return playerCount > 0 && _mapVotes.Count >= playerCount;
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
		Log.Info( $"[MapLifecycle][{RuntimeSide}] Race setup complete: map='{GetMapLabel()}', mapInfo='{CurrentMapInfo?.GameObject?.Name ?? "none"}', header='{CurrentMapInfo?.Header ?? "none"}', laps={TotalLaps}, segments=[{string.Join( ",", _segmentIds )}], configuredSpawns={CurrentMapInfo?.SpawnsMelons?.Count ?? 0}" );
    }

    private void StartRoundFromLoadedMap()
    {
        if ( !Networking.IsHost )
            return;

        CloseMapVote();
        SetupRace();
        RespawnAllMelons();
		PlayLevelStartSound( MapInstance.MapName );
    }

	[Rpc.Broadcast( NetFlags.Reliable | NetFlags.HostOnly )]
	private void PlayLevelStartSound( string mapName )
	{
		if ( LevelStartSound is null || !MapInstance.IsValid() || !MapInstance.IsLoaded )
			return;

		if ( MapInstance.MapName != mapName )
			return;

		Sound.Play( LevelStartSound );
	}

    private void HandleMapUnloaded()
    {
		LogMapState( "OnMapUnloaded callback" );
		ResetMapSetup();

		if ( Networking.IsHost )
		{
			if ( CurrentMapInfo.IsValid() )
			{
				Log.Info( $"[MapLifecycle][HOST] Destroying networked MapInfo object '{CurrentMapInfo.GameObject.Name}' from prefab '{_spawnedMapPrefabPath ?? "none"}'" );
				CurrentMapInfo.GameObject.Destroy();
			}
			else if ( _spawnedMapObject.IsValid() )
			{
				Log.Info( $"[MapLifecycle][HOST] Destroying networked map root '{_spawnedMapObject.Name}' without a valid MapInfo" );
				_spawnedMapObject.Destroy();
			}
		}
		else if ( _spawnedMapObject.IsValid() )
		{
			Log.Info( $"[MapLifecycle][CLIENT] Releasing local reference to networked map root '{_spawnedMapObject.Name}'; destruction is controlled by Host" );
		}

        CurrentMapInfo = null;
        _spawnedMapObject = null;
        _spawnedMapPrefabPath = null;
        _segmentIds.Clear();

        if ( !Networking.IsHost )
            return;

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
		if ( !Networking.IsHost || !IsMapVoteOpen )
			return;

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

        ActiveMapName = mapName;
		ActiveMapRevision++;
		BeginMapChange( mapName, ActiveMapRevision );
    }

    private void ApplyActiveMap()
    {
        if ( !MapInstance.IsValid() || string.IsNullOrWhiteSpace( ActiveMapName ) )
            return;

		if ( !string.IsNullOrWhiteSpace( _pendingMapName ) )
			return;

        if ( _appliedMapRevision == ActiveMapRevision && MapInstance.MapName == ActiveMapName )
            return;

		BeginMapChange( ActiveMapName, ActiveMapRevision );
    }

	private void BeginMapChange( string mapName, int revision )
	{
		if ( MapInstance.MapName == mapName && MapInstance.IsLoaded )
		{
			_pendingMapName = mapName;
			_pendingMapRevision = revision;
			MapInstance.MapName = string.Empty;
			return;
		}

		MapInstance.MapName = mapName;
		_appliedMapRevision = revision;
	}

	private void ApplyPendingMapChange()
	{
		if ( !MapInstance.IsValid() || string.IsNullOrWhiteSpace( _pendingMapName ) )
			return;

		if ( MapInstance.IsLoaded )
			return;

		MapInstance.MapName = _pendingMapName;
		_appliedMapRevision = _pendingMapRevision;
		_pendingMapName = null;
		_pendingMapRevision = 0;
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

    private bool SpawnMapInfoPrefab()
    {
		if ( !Networking.IsHost )
		{
			Log.Info( $"[MapLifecycle][CLIENT] Local prefab clone blocked: map prefabs are created and network-spawned by Host" );
			return false;
		}

		var fullMapName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;
        var prefabName = GetFormattedMapName();
		Log.Info( $"[MapLifecycle][{RuntimeSide}] Selecting map prefab: map='{fullMapName}' ({prefabName}), attempt={_mapSetupAttempts}/{MapPrefabMaxAttempts}" );

        if ( string.IsNullOrWhiteSpace( prefabName ) )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab selection failed: formatted map name is empty for '{fullMapName}'" );
			return false;
		}

        var prefabPath = $"prefabs/{prefabName}.prefab";
        if ( _spawnedMapObject.IsValid() && _spawnedMapPrefabPath == prefabPath )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Reusing existing prefab '{prefabPath}': root='{_spawnedMapObject.Name}', enabled={_spawnedMapObject.Enabled}, active={_spawnedMapObject.Active}, networkMode={_spawnedMapObject.NetworkMode}, networkRoot={_spawnedMapObject.IsNetworkRoot}" );
			LogMapPrefabState( "Existing prefab state" );
			return CurrentMapInfo.IsValid();
		}

        var prefab = GameObject.GetPrefab( prefabPath );
        if ( !prefab.IsValid() )
        {
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab resource NOT FOUND: map='{fullMapName}' ({prefabName}), requested='{prefabPath}'" );
			return false;
        }

		Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab resource found: requested='{prefabPath}', resourceRoot='{prefab.Name}', enabled={prefab.Enabled}, active={prefab.Active}, networkMode={prefab.NetworkMode}, networkRoot={prefab.IsNetworkRoot}, children={prefab.Children.Count}" );

        if ( _spawnedMapObject.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Destroying previous spawned prefab root '{_spawnedMapObject.Name}' ({_spawnedMapPrefabPath ?? "unknown"})" );
            _spawnedMapObject.Destroy();
		}

        _spawnedMapObject = prefab.Clone( new CloneConfig
		{
			StartEnabled = false
		} );
        if ( !_spawnedMapObject.IsValid() )
        {
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab clone FAILED: '{prefabPath}'" );
			return false;
        }

		Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab clone created DISABLED: root='{_spawnedMapObject.Name}', enabled={_spawnedMapObject.Enabled}, active={_spawnedMapObject.Active}, networkMode={_spawnedMapObject.NetworkMode}, networkRoot={_spawnedMapObject.IsNetworkRoot}, owner='{_spawnedMapObject.Network.Owner?.Name ?? "none"}'" );

        _spawnedMapPrefabPath = prefabPath;

        CurrentMapInfo = _spawnedMapObject.GetComponentInChildren<MapInfo>( true, true );
		if ( !CurrentMapInfo.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab clone rejected: MapInfo component not found in '{prefabPath}'" );
			_spawnedMapObject.Destroy();
			_spawnedMapObject = null;
			_spawnedMapPrefabPath = null;
			return false;
		}

		LogMapPrefabState( "Prefab cloned, waiting for activation" );
		return true;
    }

	private bool FindNetworkedMapInfoPrefab()
	{
		var prefabName = GetFormattedMapName();
		var prefabPath = $"prefabs/{prefabName}.prefab";
		var candidates = Scene.GetAllComponents<global::MapInfo>()
			.Where( mapInfo => mapInfo.IsValid() )
			.ToList();
		var mapInfo = candidates.FirstOrDefault( candidate =>
			candidate.GameObject.IsNetworkRoot
			&& string.Equals( candidate.Header, prefabName, StringComparison.OrdinalIgnoreCase ) );

		if ( !mapInfo.IsValid() )
		{
			var candidateNames = string.Join( ", ", candidates.Select( candidate => $"{candidate.GameObject.Name}[header={candidate.Header},networkRoot={candidate.GameObject.IsNetworkRoot},mode={candidate.GameObject.NetworkMode}]" ) );
			Log.Info( $"[MapLifecycle][CLIENT] Waiting for Host network prefab: map='{GetMapLabel()}', expected='{prefabPath}', candidates=[{candidateNames}]" );
			return false;
		}

		_spawnedMapObject = mapInfo.GameObject;
		_spawnedMapPrefabPath = prefabPath;
		CurrentMapInfo = mapInfo;
		LogMapPrefabState( "Network-spawned prefab received from Host" );
		return true;
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
		{
			Log.Info( $"[MelonSpawn][HOST] Respawn aborted for '{melon?.GameObject?.Name ?? "invalid"}': no valid SpawnMelon in map '{GetMapLabel()}'" );
            return;
		}

		var spawnPosition = GetAvailableSpawnPosition( spawn.WorldPosition, melon );
		Log.Info( $"[MelonSpawn][HOST] Respawn selected: melon='{melon.GameObject.Name}', owner='{melon.GameObject.Network.Owner?.Name ?? "none"}', spawnObject='{spawn.GameObject.Name}', sourcePosition={spawn.WorldPosition}, finalPosition={spawnPosition}, networkMode={melon.GameObject.NetworkMode}, networkRoot={melon.GameObject.IsNetworkRoot}" );

        var owner = melon.GameObject.Network.Owner;
        if ( owner != null && owner.IsActive )
        {
            using ( Rpc.FilterInclude( owner ) )
            {
				RespawnLocalMelon( spawnPosition, spawn.WorldRotation );
            }

            melon.CompleteDeath();
            return;
        }

        if ( !melon.IsProxy )
			melon.RespawnAt( spawnPosition, spawn.WorldRotation );

        melon.CompleteDeath();
    }

	private Vector3 GetAvailableSpawnPosition( Vector3 position, Melon spawningMelon )
	{
		var searchSphere = new Sphere( position, SpawnOverlapRadius );

		foreach ( var item in Scene.FindInPhysics( searchSphere ) )
		{
			var otherMelon = item.Components.Get<Melon>( FindMode.EverythingInSelfAndAncestors );
			if ( !otherMelon.IsValid() || otherMelon == spawningMelon )
				continue;

			return position + Vector3.Up * SpawnOverlapHeight;
		}

		return position;
	}

    [Rpc.Broadcast]
    private void RespawnLocalMelon( Vector3 position, Rotation rotation )
    {
        if ( !Melon.Local.IsValid() )
		{
			Log.Info( $"[MelonSpawn][{RuntimeSide}] RespawnLocalMelon RPC received at {position}, but Melon.Local is invalid" );
            return;
		}

		Log.Info( $"[MelonSpawn][{RuntimeSide}] RespawnLocalMelon RPC: object='{Melon.Local.GameObject.Name}', owner='{Melon.Local.GameObject.Network.Owner?.Name ?? "none"}', networkMode={Melon.Local.GameObject.NetworkMode}, networkRoot={Melon.Local.GameObject.IsNetworkRoot}, from={Melon.Local.WorldPosition}, to={position}" );

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

		return GetFormattedMapName( MapInstance.MapName );
	}

	private static string GetFormattedMapName( string mapName )
	{
        if ( string.IsNullOrWhiteSpace( mapName ) )
            return string.Empty;

        var parts = mapName.Split( '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        return parts.Length >= 2 ? parts[1] : mapName;
    }

    private void HookMapEvents()
    {
		if ( _mapEventsHooked )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] MapInstance events already subscribed" );
            return;
		}

		if ( !MapInstance.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Cannot subscribe MapInstance events: MapInstance is invalid" );
			return;
		}

        MapInstance.OnMapLoaded += HandleMapLoaded;
        MapInstance.OnMapUnloaded += HandleMapUnloaded;
        _mapEventsHooked = true;
		Log.Info( $"[MapLifecycle][{RuntimeSide}] Subscribed MapInstance.OnMapLoaded and OnMapUnloaded for '{GetMapLabel()}'" );
    }

    private void HandleMapLoaded()
    {
		LogMapState( "OnMapLoaded callback" );
		_mapSetupMapName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;
		_mapSetupStage = MapSetupStage.SpawnPrefab;
		_mapSetupTimer = MapPrefabSpawnDelay;
		_mapSetupAttempts = 0;
		Log.Info( $"[MapLifecycle][{RuntimeSide}] Deferred map setup scheduled in {MapPrefabSpawnDelay:0.00}s for '{GetMapLabel( _mapSetupMapName )}'" );
    }

    private void UnhookMapEvents()
    {
		if ( !_mapEventsHooked )
            return;

		if ( MapInstance.IsValid() )
		{
			MapInstance.OnMapLoaded -= HandleMapLoaded;
			MapInstance.OnMapUnloaded -= HandleMapUnloaded;
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Unsubscribed MapInstance.OnMapLoaded and OnMapUnloaded" );
		}
		else
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] MapInstance already invalid during unsubscribe; local subscription flag will still be reset" );
		}

        _mapEventsHooked = false;
    }

	private void UpdateMapSetup()
	{
		if ( _mapSetupStage == MapSetupStage.None || !_mapSetupTimer )
			return;

		if ( !MapInstance.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Deferred map setup cancelled: MapInstance is invalid" );
			ResetMapSetup();
			return;
		}

		if ( !MapInstance.IsLoaded )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Deferred map setup waiting: MapInstance is not loaded, map='{GetMapLabel()}'" );
			_mapSetupTimer = MapPrefabRetryDelay;
			return;
		}

		if ( MapInstance.MapName != _mapSetupMapName )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Deferred map setup cancelled: expected='{GetMapLabel( _mapSetupMapName )}', actual='{GetMapLabel()}'" );
			ResetMapSetup();
			return;
		}

		if ( _mapSetupStage == MapSetupStage.SpawnPrefab )
		{
			_mapSetupAttempts++;
			var setupSucceeded = Networking.IsHost
				? SpawnMapInfoPrefab()
				: FindNetworkedMapInfoPrefab();

			if ( !setupSucceeded )
			{
				if ( _mapSetupAttempts >= MapPrefabMaxAttempts )
				{
					Log.Info( $"[MapLifecycle][{RuntimeSide}] Map prefab setup FAILED after {_mapSetupAttempts} attempts for '{GetMapLabel()}'" );
					ResetMapSetup();
					return;
				}

				Log.Info( $"[MapLifecycle][{RuntimeSide}] Map prefab setup retry scheduled in {MapPrefabRetryDelay:0.00}s" );
				_mapSetupTimer = MapPrefabRetryDelay;
				return;
			}

			if ( !Networking.IsHost )
			{
				Log.Info( $"[MapLifecycle][CLIENT] Map prefab setup complete from Host network object: '{_spawnedMapPrefabPath}'" );
				ResetMapSetup();
				return;
			}

			_mapSetupStage = MapSetupStage.ActivatePrefab;
			_mapSetupTimer = MapPrefabActivationDelay;
			Log.Info( $"[MapLifecycle][HOST] Prefab activation and NetworkSpawn scheduled in {MapPrefabActivationDelay:0.00}s for '{_spawnedMapPrefabPath}'" );
			return;
		}

		if ( !Networking.IsHost )
		{
			Log.Info( $"[MapLifecycle][CLIENT] Invalid Host-only prefab activation stage cancelled" );
			ResetMapSetup();
			return;
		}

		if ( !_spawnedMapObject.IsValid() || !CurrentMapInfo.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] Prefab activation FAILED: rootValid={_spawnedMapObject.IsValid()}, mapInfoValid={CurrentMapInfo.IsValid()}" );
			ResetMapSetup();
			return;
		}

		_spawnedMapObject.Enabled = true;
		LogMapPrefabState( "Prefab activated locally before NetworkSpawn" );
		_spawnedMapObject.NetworkSpawn();
		LogMapPrefabState( "Prefab NetworkSpawn completed" );
		ResetMapSetup();

		StartRoundFromLoadedMap();
		LogMelonNetworkObjects( "Round started after prefab NetworkSpawn" );
	}

	private void ResetMapSetup()
	{
		_mapSetupStage = MapSetupStage.None;
		_mapSetupTimer = 0f;
		_mapSetupMapName = null;
		_mapSetupAttempts = 0;
	}

	private string RuntimeSide => Networking.IsHost ? "HOST" : "CLIENT";

	private string GetMapLabel()
	{
		return GetMapLabel( MapInstance.IsValid() ? MapInstance.MapName : string.Empty );
	}

	private static string GetMapLabel( string fullMapName )
	{
		var shortMapName = GetFormattedMapName( fullMapName );
		return $"{(string.IsNullOrWhiteSpace( fullMapName ) ? "<empty>" : fullMapName)} ({(string.IsNullOrWhiteSpace( shortMapName ) ? "<empty>" : shortMapName)})";
	}

	private void LogMapState( string reason )
	{
		if ( !MapInstance.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] {reason}: MapInstance INVALID" );
			return;
		}

		Log.Info( $"[MapLifecycle][{RuntimeSide}] {reason}: map='{GetMapLabel()}', isLoaded={MapInstance.IsLoaded}, componentEnabled={MapInstance.Enabled}, objectEnabled={MapInstance.GameObject.Enabled}, objectActive={MapInstance.GameObject.Active}, networkMode={MapInstance.GameObject.NetworkMode}, networkRoot={MapInstance.GameObject.IsNetworkRoot}, activeMap='{GetMapLabel( ActiveMapName )}', revision={ActiveMapRevision}" );
	}

	private void LogMapPrefabState( string reason )
	{
		if ( !_spawnedMapObject.IsValid() )
		{
			Log.Info( $"[MapLifecycle][{RuntimeSide}] {reason}: spawned prefab root INVALID, selected='{_spawnedMapPrefabPath ?? "none"}'" );
			return;
		}

		var objects = EnumerateHierarchy( _spawnedMapObject ).ToList();
		var segments = _spawnedMapObject.GetComponentsInChildren<global::TriggerSegment>( true, true ).ToList();
		var meshes = _spawnedMapObject.GetComponentsInChildren<MeshComponent>( true, true ).ToList();
		var spawns = _spawnedMapObject.GetComponentsInChildren<global::SpawnMelon>( true, true ).ToList();
		var neverObjects = objects.Count( gameObject => gameObject.NetworkMode == NetworkMode.Never );
		var networkObjects = objects.Count( gameObject => gameObject.NetworkMode == NetworkMode.Object );
		var snapshotObjects = objects.Count( gameObject => gameObject.NetworkMode == NetworkMode.Snapshot );
		var enabledSegments = segments.Count( segment => segment.Enabled );
		var enabledMeshes = meshes.Count( mesh => mesh.Enabled );
		var visibleMeshes = meshes.Count( mesh => mesh.Enabled && !mesh.HideInGame );
		var triggerMeshes = meshes.Count( mesh => mesh.Enabled && mesh.IsTrigger );

		Log.Info( $"[MapLifecycle][{RuntimeSide}] {reason}: map='{GetMapLabel()}', selected='{_spawnedMapPrefabPath ?? "none"}', root='{_spawnedMapObject.Name}', rootEnabled={_spawnedMapObject.Enabled}, rootActive={_spawnedMapObject.Active}, rootNetworkMode={_spawnedMapObject.NetworkMode}, rootNetworkRoot={_spawnedMapObject.IsNetworkRoot}, owner='{_spawnedMapObject.Network.Owner?.Name ?? "none"}', objects={objects.Count} [Never={neverObjects}, Object={networkObjects}, Snapshot={snapshotObjects}], segments={segments.Count}/{enabledSegments} enabled, meshes={meshes.Count}/{enabledMeshes} enabled/{visibleMeshes} visible/{triggerMeshes} triggers, spawns={spawns.Count}, mapInfoValid={CurrentMapInfo.IsValid()}" );
	}

	private void LogMelonNetworkObjects( string reason )
	{
		var melons = Scene.GetAllComponents<Melon>().Where( melon => melon.IsValid() ).ToList();
		Log.Info( $"[MelonSpawn][{RuntimeSide}] {reason}: found {melons.Count} Melon components" );

		foreach ( var melon in melons )
		{
			Log.Info( $"[MelonSpawn][{RuntimeSide}] Network object: name='{melon.GameObject.Name}', enabled={melon.GameObject.Enabled}, active={melon.GameObject.Active}, networkMode={melon.GameObject.NetworkMode}, networkRoot={melon.GameObject.IsNetworkRoot}, owner='{melon.GameObject.Network.Owner?.Name ?? "none"}', ownerActive={melon.GameObject.Network.Owner?.IsActive ?? false}, proxy={melon.IsProxy}, position={melon.WorldPosition}" );
		}
	}

	private static IEnumerable<GameObject> EnumerateHierarchy( GameObject root )
	{
		yield return root;

		foreach ( var child in root.Children )
		{
			foreach ( var descendant in EnumerateHierarchy( child ) )
				yield return descendant;
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
