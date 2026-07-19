using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ambi.MelonRacer;

public sealed class GameManager : Component
{
	private const string ShopSkinDirectory = "resources/skins/";
	private const string DefaultShopSkinPath = ShopSkinDirectory + "default.mshop";
    private const float MapVoteDuration = 10f;
	private const float SpawnOverlapRadius = 16f;
	private const float SpawnOverlapHeight = 10f;
	private const float MapPrefabSpawnDelay = 0.1f;
	private const float MapPrefabRetryDelay = 0.5f;
	private const int MapPrefabMaxAttempts = 10;

    public static readonly string[] MapVoteChoices =
    {
        "titanovsky.melon_lestoria",
		"titanovsky.melon_snowball",
		"facepunch.flatgrass"
    };

    public static GameManager Instance { get; private set; }

    [Property] public MapInstance MapInstance { get; set; }
	[Property, Group( "Maps" )] public float MapInactivityDuration { get; set; } = 300f;
	[Property, Group( "Sounds" )] public SoundEvent ActiveSegmentSound { get; set; }
	[Property, Group( "Sounds" )] public SoundEvent FinalSegmentSound { get; set; }
	[Property, Group( "Sounds" )] public SoundEvent LevelStartSound { get; set; }
	[Property, Group( "Money" )] public int DefaultMoney { get; set; } = 0;
	[Property, Group( "Coins" )] public int MaxActiveCoins { get; set; } = 5;
	[Property, Group( "Coins" )] public float CoinRefreshDelay { get; set; } = 90f;
	[Property, Group( "Shop" )] public int ShopColorPrice { get; set; } = 20;
	[Property, Group( "Shop" )] public List<Color> ShopColors { get; set; } = new()
	{
		new Color( 1f, 1f, 1f ),
		new Color( 0.03f, 0.03f, 0.03f ),
		new Color( 0.93f, 0.22f, 0.22f ),
		new Color( 1f, 0.49f, 0.12f ),
		new Color( 1f, 0.82f, 0.18f ),
		new Color( 0.55f, 0.86f, 0.22f ),
		new Color( 0.13f, 0.72f, 0.32f ),
		new Color( 0.12f, 0.76f, 0.68f ),
		new Color( 0.11f, 0.68f, 0.94f ),
		new Color( 0.24f, 0.43f, 0.94f ),
		new Color( 0.45f, 0.28f, 0.94f ),
		new Color( 0.67f, 0.26f, 0.91f ),
		new Color( 0.91f, 0.24f, 0.73f ),
		new Color( 0.94f, 0.41f, 0.62f ),
		new Color( 0.54f, 0.33f, 0.19f ),
		new Color( 0.55f, 0.60f, 0.68f )
	};

    [Sync( SyncFlags.FromHost )] public TimeUntil RaceTimer { get; private set; }
	[Sync( SyncFlags.FromHost )] public float FrozenRaceElapsed { get; private set; }
    [Sync( SyncFlags.FromHost )] public TimeUntil MapVoteTimer { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsRaceActive { get; private set; }
    [Sync( SyncFlags.FromHost )] public bool IsMapVoteOpen { get; private set; }
    [Sync( SyncFlags.FromHost )] public int LestoriaVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int SnowballVotes { get; private set; }
	[Sync( SyncFlags.FromHost )] public int FlatgrassVotes { get; private set; }
    [Sync( SyncFlags.FromHost )] public int TotalLaps { get; private set; } = 1;
	[Sync( SyncFlags.FromHost )] public int MaxCompletedLaps { get; private set; }
    [Sync( SyncFlags.FromHost )] public string ActiveMapName { get; private set; }
    [Sync( SyncFlags.FromHost )] public int ActiveMapRevision { get; private set; }
	[Sync( SyncFlags.FromHost )] public TimeUntil MapInactivityTimer { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool IsCurrentMapSupported { get; private set; } = true;
	[Property, Group( "Money" ), Sync( SyncFlags.FromHost )] public int MoneyVersion { get; set; } = 0;

    public global::MapInfo CurrentMapInfo { get; private set; }
    public IReadOnlyList<int> SegmentIds => _segmentIds;
	public IReadOnlyList<MelonShopConfig> ShopSkins => MelonShopConfig.All
		.Where( config => config?.ResourcePath?.StartsWith( ShopSkinDirectory, StringComparison.OrdinalIgnoreCase ) == true )
		.OrderBy( config => config.Price )
		.ThenBy( config => config.Header )
		.ToList();
	public MelonShopConfig DefaultShopSkin => ResourceLibrary.Get<MelonShopConfig>( DefaultShopSkinPath );

    private readonly List<int> _segmentIds = new();
    private readonly List<Coin> _coins = new();
    private readonly Dictionary<string, string> _mapVotes = new();
	private TimeUntil _coinRefreshTimer;
    private GameObject _spawnedMapObject;
    private bool _mapEventsHooked;
    private string _spawnedMapPrefabPath;
    private string _pendingMapName;
    private int _pendingMapRevision;
    private int _appliedMapRevision;
	private bool _mapSetupPending;
	private TimeUntil _mapSetupTimer;
	private string _mapSetupMapName;
	private int _mapSetupAttempts;
	private bool _mapPrefabUnavailable;
	private Melon _leaderboardLocalMelon;
	private string _leaderboardMapName;
	private bool _localRaceResultSubmitted;

	public float RaceElapsed => IsRaceActive
		? MathF.Max( 0f, -(float)RaceTimer )
		: FrozenRaceElapsed;
    public float MapVoteSecondsLeft => IsMapVoteOpen ? MathF.Max( 0f, MapVoteTimer.Relative ) : 0f;
    public int FirstSegmentId => _segmentIds.Count > 0 ? _segmentIds[0] : 0;

	public MelonShopConfig GetShopSkin( string header )
	{
		if ( string.IsNullOrWhiteSpace( header ) )
			return DefaultShopSkin ?? ShopSkins.FirstOrDefault();

		var skin = ShopSkins.FirstOrDefault( config =>
			string.Equals( config.Header, header, StringComparison.OrdinalIgnoreCase ) );

		return skin ?? (string.Equals( header, "Default (gmod)", StringComparison.OrdinalIgnoreCase )
			? DefaultShopSkin
			: null);
	}

	public Color GetShopColor( int colorIndex )
	{
		if ( ShopColors is null || ShopColors.Count == 0 )
			return Color.White;

		return ShopColors[Math.Clamp( colorIndex, 0, ShopColors.Count - 1 )];
	}

	public bool IsShopColorIndexValid( int colorIndex )
	{
		return ShopColors is not null && colorIndex >= 0 && colorIndex < ShopColors.Count;
	}

	public void ApplyShopAppearance( Melon melon, string skinHeader, int colorIndex, bool jumpOnSkinChange = false )
	{
		if ( !Networking.IsHost || !melon.IsValid() || !IsShopColorIndexValid( colorIndex ) )
			return;

		var skin = GetShopSkin( skinHeader );
		var resolvedSkinHeader = skin?.Header;
		if ( string.IsNullOrWhiteSpace( resolvedSkinHeader ) )
			resolvedSkinHeader = string.IsNullOrWhiteSpace( skinHeader ) ? melon.ShopSkinHeader : skinHeader;

		// Tint restoration must not wait for custom Resource Assets to finish loading.
		// The renderer can use the synced color immediately and resolve the skin later.
		var skinChanged = !string.Equals( melon.ShopSkinHeader, resolvedSkinHeader, StringComparison.OrdinalIgnoreCase );
		melon.SetShopAppearance( resolvedSkinHeader, colorIndex );

		if ( jumpOnSkinChange && skinChanged )
			melon.TryShopSkinChangeJump();
	}

	public void PurchaseShopSkin( Melon melon, string skinHeader )
	{
		if ( !Networking.IsHost || !melon.IsValid() )
			return;

		var skin = GetShopSkin( skinHeader );
		if ( skin is null )
			return;

		var price = Math.Max( 0, skin.Price );
		if ( melon.Coins < price )
			return;

		melon.CompleteShopPurchase( false, skin.Header, 0, price );
		ApplyShopAppearance( melon, skin.Header, melon.ShopColorIndex, true );
	}

	public void PurchaseShopColor( Melon melon, int colorIndex )
	{
		if ( !Networking.IsHost || !melon.IsValid() || !IsShopColorIndexValid( colorIndex ) )
			return;

		var price = Math.Max( 0, ShopColorPrice );
		if ( melon.Coins < price )
			return;

		melon.CompleteShopPurchase( true, string.Empty, colorIndex, price );
		melon.SetShopAppearance( melon.ShopSkinHeader, colorIndex );
	}

    protected override void OnAwake()
    {
		Instance = this;
    }

    protected override void OnDestroy()
    {
        UnhookMapEvents();
		ResetMapSetup();
		_mapVotes.Clear();
		_segmentIds.Clear();
		_coins.Clear();
		CurrentMapInfo = null;
		_spawnedMapObject = null;
		_spawnedMapPrefabPath = null;

		if ( Instance == this )
			Instance = null;
    }

    protected override void OnStart()
    {
        HookMapEvents();

        if ( Networking.IsHost )
		{
            ActiveMapName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;
			ResetMapInactivityTimer();
		}
        else
            ApplyActiveMap();

        if ( MapInstance.IsValid() && MapInstance.IsLoaded )
            HandleMapLoaded();
    }

    protected override void OnUpdate()
    {
		TrySubmitLocalRaceResult();
		ApplyPendingMapChange();
		UpdateMapSetup();

		if ( !Networking.IsHost )
        {
            ApplyActiveMap();
            return;
        }

		if ( IsMapVoteOpen )
		{
			if ( HaveAllPlayersVoted() || MapVoteTimer )
				FinishMapVote();

			return;
		}

		if ( MapInactivityDuration > 0f && MapInactivityTimer )
			ChangeToRandomMap();

		if ( _coins.Count > 0 && _coinRefreshTimer )
			RefreshCoins();
    }

	public void TrySubmitLocalRaceResult()
	{
		var localMelon = Melon.Local;
		var mapFullName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;

		if ( Connection.Local is null || !localMelon.IsValid() || string.IsNullOrWhiteSpace( mapFullName ) )
		{
			_leaderboardLocalMelon = null;
			_leaderboardMapName = null;
			_localRaceResultSubmitted = false;
			return;
		}

		if ( localMelon != _leaderboardLocalMelon
			|| !string.Equals( mapFullName, _leaderboardMapName, StringComparison.OrdinalIgnoreCase ) )
		{
			_leaderboardLocalMelon = localMelon;
			_leaderboardMapName = mapFullName;
			_localRaceResultSubmitted = false;
		}

		if ( !localMelon.HasFinishedRace )
		{
			_localRaceResultSubmitted = false;
			return;
		}

		if ( _localRaceResultSubmitted || RaceElapsed <= 0f )
			return;

		_localRaceResultSubmitted = true;
		RaceLeaderboardService.SubmitLocalResult( mapFullName, RaceElapsed );
	}

    public void PickupCoin( Melon melon, Coin coin )
    {
        if ( !Networking.IsHost || !melon.IsValid() || !coin.IsValid() || !coin.IsOpen )
            return;

        coin.Close();
		coin.SpawnTouchParticle();

        var owner = melon.GameObject.Network.Owner;
        if ( owner != null && owner.IsActive )
        {
            using ( Rpc.FilterInclude( owner ) )
            {
				melon.GrantCoin( coin.GameObject );
            }

            return;
        }

		if ( !melon.IsProxy )
		{
			coin.PlayTouchSound();
            melon.GrantCoinLocal();
		}
    }

    private void SetupCoins()
    {
        if ( !Networking.IsHost )
            return;

        _coins.Clear();

        IEnumerable<Coin> coins = CurrentMapInfo.IsValid()
            ? CurrentMapInfo.GameObject.GetComponentsInChildren<Coin>( true, true )
            : Scene.GetAllComponents<Coin>();

        _coins.AddRange( coins.Where( coin => coin.IsValid() ) );

        RefreshCoins();
    }

    private void RefreshCoins()
    {
        if ( !Networking.IsHost )
            return;

        foreach ( var coin in _coins )
        {
            if ( coin.IsValid() )
                coin.Close();
        }

        var openCoins = _coins
            .Where( coin => coin.IsValid() )
            .OrderBy( _ => Random.Shared.Next() )
            .Take( Math.Max( 0, MaxActiveCoins ) );

        foreach ( var coin in openCoins )
            coin.Open();

        _coinRefreshTimer = MathF.Max( 1f, CoinRefreshDelay );
    }

    public void RegisterMelon( Melon melon )
    {
        if ( !Networking.IsHost || !melon.IsValid() )
            return;

		if ( !CurrentMapInfo.IsValid() && IsCurrentMapSupported )
			return;

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

	public void KillMelon( Melon melon )
	{
		if ( !Networking.IsHost || !melon.IsValid() )
			return;

		if ( !melon.BeginDeath( true ) )
			return;

		Log.Info( $"Melon killed: {melon.GameObject.Network.Owner?.Name ?? melon.GameObject.Name}" );
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

		ResetMapInactivityTimer();

        if ( TryGetNextSegmentId( segmentId, out var nextSegmentId ) )
        {
            melon.SetActiveSegment( nextSegmentId );
            return;
        }

        var lapTime = RaceElapsed - melon.CurrentLapStartedAt;
        var completedLaps = melon.CompletedLaps + 1;
		MaxCompletedLaps = Math.Max( MaxCompletedLaps, completedLaps );

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
		Log.Info( $"Vote accepted from {GetVoterLogName( voterId, voterName )}: {mapName}. {GetMapVoteSummary()}" );

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
		MaxCompletedLaps = 0;

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

		FrozenRaceElapsed = 0f;
        RaceTimer = 0f;
        IsRaceActive = true;
    }

    private void StartRoundFromLoadedMap()
    {
        if ( !Networking.IsHost )
            return;

        CloseMapVote();
        SetupRace();
        SetupCoins();
        RespawnAllMelons();
		ResetMapInactivityTimer();
		PlayLevelStartSound( MapInstance.MapName );
    }

	private void StartUnsupportedMapRound()
	{
		if ( !Networking.IsHost )
			return;

		CloseMapVote();
		IsCurrentMapSupported = false;
		CurrentMapInfo = null;
		_segmentIds.Clear();
		_segmentIds.Add( 0 );
		TotalLaps = 1;
		MaxCompletedLaps = 0;
		FrozenRaceElapsed = 0f;
		RaceTimer = 0f;
		IsRaceActive = true;
		RespawnAllMelons();
		ResetMapInactivityTimer();
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
		ResetMapSetup();

		if ( Networking.IsHost )
		{
			if ( CurrentMapInfo.IsValid() )
				CurrentMapInfo.GameObject.Destroy();
			else if ( _spawnedMapObject.IsValid() )
				_spawnedMapObject.Destroy();
		}

        CurrentMapInfo = null;
        _spawnedMapObject = null;
        _spawnedMapPrefabPath = null;
        _segmentIds.Clear();
        _coins.Clear();

		if ( !Networking.IsHost )
			return;

		if ( IsRaceActive )
			FrozenRaceElapsed = RaceElapsed;

        IsRaceActive = false;
        CloseMapVote();
        TotalLaps = 1;
		MaxCompletedLaps = 0;
    }

    private void OpenMapVote()
    {
        if ( !Networking.IsHost )
            return;

        if ( IsMapVoteOpen )
            return;

		FrozenRaceElapsed = RaceElapsed;
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

		if ( _mapVotes.Count == 0 )
		{
			CloseMapVote();
			StartRoundFromLoadedMap();
			return;
		}

		var selectedMap = MapVoteChoices
			.Select( (mapName, index) => new { MapName = mapName, Votes = GetMapVoteCount( mapName ), Index = index } )
			.OrderByDescending( choice => choice.Votes )
			.ThenBy( choice => choice.Index )
			.First().MapName;

		Log.Info( $"Map vote finished. Selected {selectedMap}. {GetMapVoteSummary()}" );
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

	private void ChangeToRandomMap()
	{
		if ( !Networking.IsHost || !MapInstance.IsValid() )
			return;

		var currentMap = MapInstance.MapName;
		var choices = MapVoteChoices
			.Where( mapName => !string.Equals( mapName, currentMap, StringComparison.OrdinalIgnoreCase ) )
			.ToList();

		if ( choices.Count == 0 )
			choices.AddRange( MapVoteChoices );

		if ( choices.Count == 0 )
			return;

		ResetMapInactivityTimer();
		ChangeMap( Random.Shared.FromList( choices ) );
	}

	private void ResetMapInactivityTimer()
	{
		if ( !Networking.IsHost )
			return;

		MapInactivityTimer = MathF.Max( 0f, MapInactivityDuration );
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
		FlatgrassVotes = 0;
    }

    private void RecountMapVotes()
    {
        LestoriaVotes = _mapVotes.Values.Count( mapName => mapName == MapVoteChoices[0] );
        SnowballVotes = _mapVotes.Values.Count( mapName => mapName == MapVoteChoices[1] );
		FlatgrassVotes = _mapVotes.Values.Count( mapName => mapName == MapVoteChoices[2] );
    }

	public int GetMapVoteCount( string mapName )
	{
		if ( mapName == MapVoteChoices[0] )
			return LestoriaVotes;

		if ( mapName == MapVoteChoices[1] )
			return SnowballVotes;

		if ( mapName == MapVoteChoices[2] )
			return FlatgrassVotes;

		return 0;
	}

	private string GetMapVoteSummary()
	{
		return string.Join( ", ", MapVoteChoices.Select( mapName => $"{mapName}={GetMapVoteCount( mapName )}" ) );
	}

    private bool SpawnMapInfoPrefab()
    {
        var prefabName = GetFormattedMapName();
        if ( string.IsNullOrWhiteSpace( prefabName ) )
			return false;

        var prefabPath = $"prefabs/{prefabName}.prefab";
        if ( _spawnedMapObject.IsValid() && _spawnedMapPrefabPath == prefabPath )
			return CurrentMapInfo.IsValid();

		var existingMapInfo = Scene.GetAllComponents<global::MapInfo>().FirstOrDefault( candidate =>
			candidate.IsValid()
			&& string.Equals( candidate.Header, prefabName, StringComparison.OrdinalIgnoreCase ) );

		if ( existingMapInfo.IsValid() )
		{
			_spawnedMapObject = existingMapInfo.GameObject;
			_spawnedMapPrefabPath = prefabPath;
			CurrentMapInfo = existingMapInfo;
			return true;
		}

		// The host owns the map-info prefab. Clients must wait for that same
		// network object so Coin.IsOpen is synchronized on the actual coins.
		if ( !Networking.IsHost )
			return false;

        var prefab = GameObject.GetPrefab( prefabPath );
        if ( !prefab.IsValid() )
        {
			_mapPrefabUnavailable = true;
			Log.Warning( $"MapInfo prefab not found: {prefabPath}" );
			return false;
        }

        if ( _spawnedMapObject.IsValid() )
            _spawnedMapObject.Destroy();

		_spawnedMapObject = GameObject.Clone( prefabPath );
        if ( !_spawnedMapObject.IsValid() )
        {
			Log.Warning( $"Failed to create MapInfo prefab: {prefabPath}" );
			return false;
        }

		_spawnedMapObject.NetworkSpawn();

        _spawnedMapPrefabPath = prefabPath;

        CurrentMapInfo = _spawnedMapObject.GetComponentInChildren<MapInfo>( true, true );
		if ( !CurrentMapInfo.IsValid() )
		{
			_spawnedMapObject.Destroy();
			_spawnedMapObject = null;
			_spawnedMapPrefabPath = null;
			return false;
		}

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
            return;

		var spawnPosition = GetAvailableSpawnPosition( spawn.WorldPosition, melon );

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
            return;

        Melon.Local.RespawnAt( position, rotation );
    }

    private GameObject GetRandomMelonSpawn()
    {
		if ( CurrentMapInfo.IsValid() )
		{
			var mapSpawns = CurrentMapInfo.SpawnsMelons
				.Where( spawn => spawn.IsValid() )
				.Select( spawn => spawn.GameObject )
				.ToList();

			if ( mapSpawns.Count > 0 )
				return Random.Shared.FromList( mapSpawns );
		}

		var sceneSpawns = Scene.GetAllComponents<Sandbox.SpawnPoint>()
			.Where( spawn => spawn.IsValid() && spawn.GameObject.Enabled )
			.Select( spawn => spawn.GameObject )
			.ToList();

		if ( sceneSpawns.Count > 0 )
			return Random.Shared.FromList( sceneSpawns );

		var networkHelpers = Scene.GetAllComponents<NetworkHelper>()
			.Where( helper => helper.IsValid() )
			.ToList();
		var configuredSpawns = networkHelpers
			.SelectMany( helper => helper.SpawnPoints ?? new List<GameObject>() )
			.Where( spawn => spawn.IsValid() && spawn.Enabled )
			.ToList();

		if ( configuredSpawns.Count > 0 )
			return Random.Shared.FromList( configuredSpawns );

		return networkHelpers.FirstOrDefault()?.GameObject;
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
            return;

		if ( !MapInstance.IsValid() )
			return;

        MapInstance.OnMapLoaded += HandleMapLoaded;
        MapInstance.OnMapUnloaded += HandleMapUnloaded;
        _mapEventsHooked = true;
    }

    private void HandleMapLoaded()
    {
		_mapSetupMapName = MapInstance.IsValid() ? MapInstance.MapName : string.Empty;
		_mapSetupPending = true;
		_mapSetupTimer = MapPrefabSpawnDelay;
		_mapSetupAttempts = 0;
		_mapPrefabUnavailable = false;

		if ( Networking.IsHost )
			IsCurrentMapSupported = true;
    }

    private void UnhookMapEvents()
    {
		if ( !_mapEventsHooked )
            return;

		if ( MapInstance.IsValid() )
		{
			MapInstance.OnMapLoaded -= HandleMapLoaded;
			MapInstance.OnMapUnloaded -= HandleMapUnloaded;
		}

        _mapEventsHooked = false;
    }

	private void UpdateMapSetup()
	{
		if ( !_mapSetupPending || !_mapSetupTimer )
			return;

		if ( !MapInstance.IsValid() )
		{
			ResetMapSetup();
			return;
		}

		if ( !MapInstance.IsLoaded )
		{
			_mapSetupTimer = MapPrefabRetryDelay;
			return;
		}

		if ( MapInstance.MapName != _mapSetupMapName )
		{
			ResetMapSetup();
			return;
		}

		_mapSetupAttempts++;
		var setupSucceeded = SpawnMapInfoPrefab();

		if ( !setupSucceeded )
		{
			if ( _mapPrefabUnavailable || (Networking.IsHost && _mapSetupAttempts >= MapPrefabMaxAttempts) )
			{
				ResetMapSetup();

				if ( Networking.IsHost )
					StartUnsupportedMapRound();

				return;
			}

			_mapSetupTimer = MapPrefabRetryDelay;
			return;
		}

		ResetMapSetup();

		if ( Networking.IsHost )
		{
			IsCurrentMapSupported = true;
			StartRoundFromLoadedMap();
		}
	}

	private void ResetMapSetup()
	{
		_mapSetupPending = false;
		_mapSetupTimer = 0f;
		_mapSetupMapName = null;
		_mapSetupAttempts = 0;
		_mapPrefabUnavailable = false;
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

public class MoneySaveData
{
    public int Version { get; set; }
    public int Money { get; set; }
}
