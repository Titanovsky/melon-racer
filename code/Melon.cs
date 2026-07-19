using Sandbox;
using System;
using Ambi.Utils;

namespace Ambi.MelonRacer;

public sealed class Melon : Component, Component.ICollisionListener
{
	private const string MoneySaveFile = "melon_money";
	// Unlocks and the current selection stay on the owning player's machine.
	private const string ShopSaveFile = "melon_shop";
	private const int ShopSaveVersion = 1;
	private const string DefaultShopSkin = "Default (Gmod)";
	private const float ShopSkinChangeUpVelocity = 400f;
	private const float ShopSkinChangeJumpCooldown = 1f;

    public static Melon Local { get; private set; }

    [Property] public ModelRenderer Renderer { get; set; }
	[Property] public Rigidbody Rigidbody { get; set; }
	[Property] public SphereCollider Collider { get; set; }
    [Property] public PlayerWorldHud WorldHud { get; set; }

    [Property, Group( "Movement" )] public float Speed { get; set; } = 500f;
	[Property, Group( "Movement" )] public float Inertia { get; set; } = 3f;
	[Property, Group( "Movement" )] public float BrakeDeceleration { get; set; } = 450f;
	[Property, Group( "Movement" )] public float SpawnVelocity { get; set; } = 100f;

	[Property, Group( "Jump" )] public float JumpForce { get; set; } = 400f;
	[Property, Group( "Jump" )] public float JumpDelay { get; set; } = 0.5f;

	[Property, Group( "Boost" )] public float BoostSpeed { get; set; } = 500f;
	[Property, Group( "Boost" )] public float BoostCooldown { get; set; } = 1.5f;
	[Property, Group( "Boost" )] public float BoostFadeDuration { get; set; } = 0.35f;

	[Property, Group( "Smash" )] public float SmashSpeed { get; set; } = 700f;
	[Property, Group( "Smash" )] public float SmashCooldown { get; set; } = 1f;

	[Property, Group( "Death" )] public float RespawnDelay { get; set; } = 3f;
	[Property, Group( "Death" )] public GameObject DeathEffectPrefab { get; set; }
	[Property, Group( "Death" )] public GameObject DeathBurstEffectPrefab { get; set; }
	[Property, Group( "Death" )] public SoundEvent DeathSound { get; set; }
	[Property, Group( "Spawn" )] public float SpawnInvulnerabilityDuration { get; set; } = 3f;

	[Property, Group( "Camera" )] public bool EnableCameraSystem { get; set; } = true;
	[Property, Group( "Camera" )] public float CameraSensitivity { get; set; } = 0.15f;
	[Property, Group( "Camera" )] public float DefaultZoom { get; set; } = 100f;
	[Property, Group( "Camera" )] public float CameraPitchMin { get; set; } = -30f;
	[Property, Group( "Camera" )] public float CameraPitchMax { get; set; } = 80f;
	[Property, Group( "Camera" )] public float CameraPivotHeight { get; set; } = 12f;
	[Property, Group( "Camera" )] public float CameraCollisionPadding { get; set; } = 2f;
	[Property, Group( "Camera" )] public float CameraDriftCorrection { get; set; } = 3f;
	[Property, Group( "Camera" )] public float CameraVerticalDeadZone { get; set; } = 4f;
	[Property, Group( "Camera" )] public float CameraVerticalSmooth { get; set; } = 3f;
	[Property, Group( "Camera" )] public float CameraVerticalFollowLimit { get; set; } = 120f;

	[Property, Group( "Hud" )] public float HudDriftCorrection { get; set; } = 3f;
	[Property, Group( "Hud" )] public float HudVerticalSmooth { get; set; } = 8f;

	[Sync( SyncFlags.FromHost )] public int ActiveSegmentId { get; private set; }
	[Sync( SyncFlags.FromHost )] public int CompletedLaps { get; private set; }
	[Sync( SyncFlags.FromHost )] public float CurrentLapStartedAt { get; private set; }
	[Sync( SyncFlags.FromHost )] public float LastLapTime { get; private set; }
	[Sync( SyncFlags.FromHost )] public float BestLapTime { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool HasFinishedRace { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool IsDead { get; private set; }

	[Sync] public int Coins { get; private set; }
	[Sync( SyncFlags.FromHost )] public string ShopSkinHeader { get; private set; } = DefaultShopSkin;
	[Sync( SyncFlags.FromHost )] public int ShopColorIndex { get; private set; }

	private TimeUntil _jumpReady;
	private TimeUntil _boostReady;
	private TimeUntil _smashReady;
	private TimeUntil _respawnTimer;
	private TimeUntil _spawnInvulnerability;
	private Vector3 _worldHudOffset;
	private Vector3 _hudPosition;
	private Vector3 _moveDirection;
	private Vector3 _boostDirection;
	private float _boostSpeedRemaining;
	private float _boostFadeRate;
	private float _zoomDistance;
	private Vector3 _focusPosition;
	private bool _ignoreJumpVertical;
	private bool _presentedDeathState;
	private float _cameraYaw;
	private float _cameraPitch = 25f;
	private ShopSaveData _shopData = new();
	private int _submittedShopMapRevision = int.MinValue;
	private int _pendingShopMapRevision = int.MinValue;
	private int _shopAppearanceRequestId;
	private TimeUntil _shopAppearanceRetry;
	private TimeUntil _shopSkinChangeJumpReady;
	private bool _pendingShopSkinChangeJump;
	private string _presentedShopSkin;
	private int _presentedShopColor = -1;

	private Vector3 GetWishDirection()
	{
		var direction = Vector3.Zero;

		if ( Input.Down( "Forward" ) ) direction += Vector3.Forward;
		if ( Input.Down( "Backward" ) ) direction += Vector3.Backward;
		if ( Input.Down( "Left" ) ) direction += Vector3.Left;
		if ( Input.Down( "Right" ) ) direction += Vector3.Right;

		return (Rotation.FromYaw( _cameraYaw ) * direction.Normal).WithZ( 0 ).Normal;
	}

	private bool IsGrounded()
	{
		var radius = GetWorldSphereRadius();

		var trace = Scene.Trace
			.Sphere( radius * 0.9f, WorldPosition, WorldPosition + Vector3.Down * radius * 0.3f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		return trace.Hit;
	}

	private float GetWorldSphereRadius()
	{
		if ( !Collider.IsValid() )
			return 16f;

		var scale = MathF.Max( MathF.Abs( WorldScale.x ), MathF.Max( MathF.Abs( WorldScale.y ), MathF.Abs( WorldScale.z ) ) );
		return MathF.Max( 0.01f, Collider.Radius * scale );
	}

	private void UpdateCamera()
	{
		var camera = Scene.Camera;
		if ( !camera.IsValid() )
			return;

		var look = Input.AnalogLook;
		if ( !look.IsNearlyZero() )
		{
			var sensitivity = Preferences.Sensitivity * CameraSensitivity;

			_cameraYaw += look.yaw * sensitivity;
			_cameraPitch = ( _cameraPitch + look.pitch * sensitivity ).Clamp( CameraPitchMin, CameraPitchMax );
		}

		UpdateFocusPosition();

		var eyeRotation = Rotation.From( _cameraPitch, _cameraYaw, 0f );
		var cameraPivot = _focusPosition + Vector3.Up * MathF.Max( 0f, CameraPivotHeight );
		var targetPosition = cameraPivot - eyeRotation.Forward * _zoomDistance;
		var collisionPadding = MathF.Max( 0f, CameraCollisionPadding );

		var trace = Scene.Trace
			.Ray( cameraPivot, targetPosition )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		camera.WorldPosition = trace.EndPosition
			+ (trace.Hit ? trace.Normal * collisionPadding : Vector3.Zero);
		camera.WorldRotation = eyeRotation;
	}

	private void UpdateFocusPosition()
	{
		var horizontalError = (WorldPosition - _focusPosition).WithZ( 0 ).Length;
		if ( horizontalError > 100f )
		{
			_focusPosition = WorldPosition;
			return;
		}

		if ( Rigidbody.IsValid() )
			_focusPosition += Rigidbody.Velocity.WithZ( 0 ) * Time.Delta;

		var horizontalTarget = WorldPosition.WithZ( _focusPosition.z );
		var driftLerp = 1f - MathF.Exp( -MathF.Max( 0f, CameraDriftCorrection ) * Time.Delta );
		_focusPosition = _focusPosition.LerpTo( horizontalTarget, driftLerp );

		var isGrounded = IsGrounded();
		var verticalVelocity = Rigidbody.IsValid() ? Rigidbody.Velocity.z : 0f;
		if ( _ignoreJumpVertical && isGrounded && verticalVelocity <= 0f )
			_ignoreJumpVertical = false;

		var verticalError = WorldPosition.z - _focusPosition.z;

		var followLimit = MathF.Max( CameraVerticalDeadZone, CameraVerticalFollowLimit );
		if ( MathF.Abs( verticalError ) > followLimit )
		{
			_focusPosition = _focusPosition.WithZ( WorldPosition.z - MathF.Sign( verticalError ) * followLimit );
			verticalError = WorldPosition.z - _focusPosition.z;
		}

		if ( _ignoreJumpVertical )
			return;

		var deadZone = MathF.Max( 0f, CameraVerticalDeadZone );
		if ( MathF.Abs( verticalError ) <= deadZone )
			return;

		var verticalTarget = WorldPosition.z - MathF.Sign( verticalError ) * deadZone;
		var verticalLerp = 1f - MathF.Exp( -MathF.Max( 0f, CameraVerticalSmooth ) * Time.Delta );
		_focusPosition = _focusPosition.WithZ( _focusPosition.z + (verticalTarget - _focusPosition.z) * verticalLerp );
	}

	private void UpdateBoostFade()
	{
		if ( _boostSpeedRemaining <= 0f || !Rigidbody.PhysicsBody.IsValid() )
			return;

		var body = Rigidbody.PhysicsBody;
		var speedAlongBoost = MathF.Max( 0f, Vector3.Dot( body.Velocity.WithZ( 0f ), _boostDirection ) );
		var speedToRemove = MathF.Min( _boostSpeedRemaining, _boostFadeRate * Time.Delta );
		speedToRemove = MathF.Min( speedToRemove, speedAlongBoost );

		if ( speedToRemove <= 0f )
		{
			_boostSpeedRemaining = 0f;
			return;
		}

		body.Velocity -= _boostDirection * speedToRemove;
		_boostSpeedRemaining -= speedToRemove;
	}

	private void TryBoost( Vector3 wishDirection )
	{
		if ( !Input.Pressed( "Run" ) || !_boostReady || _boostSpeedRemaining > 0f )
			return;

		if ( !Rigidbody.PhysicsBody.IsValid() )
			return;

		var direction = wishDirection;
		if ( direction.IsNearZeroLength )
			direction = Rigidbody.PhysicsBody.Velocity.WithZ( 0f ).Normal;

		if ( direction.IsNearZeroLength )
			direction = (Rotation.FromYaw( _cameraYaw ) * Vector3.Forward).WithZ( 0f ).Normal;

		var boostSpeed = MathF.Max( 0f, BoostSpeed );
		if ( boostSpeed <= 0f || direction.IsNearZeroLength )
			return;

		_boostDirection = direction;
		_boostSpeedRemaining = boostSpeed;
		_boostFadeRate = boostSpeed / MathF.Max( 0.05f, BoostFadeDuration );
		_boostReady = MathF.Max( 0f, BoostCooldown );

		Rigidbody.ApplyImpulse( direction * boostSpeed * Rigidbody.PhysicsBody.Mass );
	}

	private void ApplyBrake()
	{
		if ( !Rigidbody.PhysicsBody.IsValid() )
			return;

		var inputFade = 1f - MathF.Exp( -MathF.Max( 0f, Inertia ) * Time.Delta );
		_moveDirection = Vector3.Lerp( _moveDirection, Vector3.Zero, inputFade );

		var body = Rigidbody.PhysicsBody;
		var deceleration = MathF.Max( 0f, BrakeDeceleration );
		var horizontalVelocity = body.Velocity.WithZ( 0f );
		var speedToRemove = MathF.Min( horizontalVelocity.Length, deceleration * Time.Delta );

		if ( speedToRemove > 0f )
			body.Velocity -= horizontalVelocity.Normal * speedToRemove;

		var radius = MathF.Max( 1f, GetWorldSphereRadius() );
		var angularSpeed = body.AngularVelocity.Length;
		var angularToRemove = MathF.Min( angularSpeed, deceleration / radius * Time.Delta );

		if ( angularToRemove > 0f )
			body.AngularVelocity -= body.AngularVelocity.Normal * angularToRemove;
	}

	private void StopMovement()
	{
		_moveDirection = Vector3.Zero;
		_boostDirection = Vector3.Zero;
		_boostSpeedRemaining = 0f;
		_boostFadeRate = 0f;

		if ( !Rigidbody.IsValid() )
			return;

		Rigidbody.ClearForces();

		if ( !Rigidbody.PhysicsBody.IsValid() )
			return;

		Rigidbody.PhysicsBody.Velocity = Vector3.Zero;
		Rigidbody.PhysicsBody.AngularVelocity = Vector3.Zero;
		Rigidbody.PhysicsBody.ClearForces();
		Rigidbody.PhysicsBody.ClearTorque();
	}

	private void UpdateDeathPresentation()
	{
		if ( _presentedDeathState == IsDead )
			return;

		_presentedDeathState = IsDead;

		if ( Renderer.IsValid() )
			Renderer.Enabled = !IsDead;

		if ( Collider.IsValid() )
			Collider.Enabled = !IsDead;

		if ( WorldHud.IsValid() )
			WorldHud.GameObject.Enabled = !IsDead;

		if ( IsDead && !IsProxy )
			StopMovement();
	}

	protected override void OnStart()
	{
		if ( !IsProxy )
		{
			Local = this;

			LoadMoney();
			LoadShop();

			if ( Networking.IsHost )
				GameManager.Instance?.RegisterMelon( this );
			else
				RequestInitialSpawn();

			QueueSavedShopAppearance();
		}

		if ( WorldHud.IsValid() )
		{
			WorldHud.Name = GameObject.Network.Owner?.Name ?? Connection.Local?.Name ?? "";
			_worldHudOffset = WorldHud.GameObject.WorldPosition - WorldPosition;
			_hudPosition = WorldHud.GameObject.WorldPosition;
		}

        _zoomDistance = DefaultZoom;
		_focusPosition = WorldPosition;

    }

	[Rpc.Host( NetFlags.OwnerOnly | NetFlags.Reliable )]
	private void RequestInitialSpawn()
	{
		GameManager.Instance?.RegisterMelon( this );
	}

	public void TouchSegment( int segmentId )
	{
		if ( IsProxy )
			return;

		if ( Networking.IsHost )
		{
			GameManager.Instance?.PassSegment( this, segmentId );
			return;
		}

		RequestSegmentPass( segmentId );
	}

	[Rpc.Host( NetFlags.Reliable | NetFlags.SendImmediate )]
	private void RequestSegmentPass( int segmentId )
	{
		GameManager.Instance?.PassSegment( this, segmentId );
	}

	public void TouchCoin( Coin coin )
	{
		if ( IsProxy || !coin.IsValid() )
			return;

		if ( Networking.IsHost )
		{
			GameManager.Instance?.PickupCoin( this, coin );
			return;
		}

		RequestCoinPickup( coin.GameObject );
	}

	[Rpc.Host( NetFlags.Reliable )]
	private void RequestCoinPickup( GameObject coinObject )
	{
		if ( !coinObject.IsValid() )
			return;

		GameManager.Instance?.PickupCoin( this, coinObject.Components.Get<Coin>() );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	public void GrantCoin( GameObject coinObject )
	{
		if ( coinObject.IsValid() )
			coinObject.Components.Get<Coin>()?.PlayTouchSound();

		GrantCoinLocal();
	}

	public void GrantCoinLocal()
	{
		if ( IsProxy )
			return;

		Coins += 1;
		SaveMoney();
	}

	private void LoadMoney()
	{
		var version = GameManager.Instance?.MoneyVersion ?? 0;
		var defaultMoney = GameManager.Instance?.DefaultMoney ?? 0;

		if ( !DataStore.Exists( MoneySaveFile ) )
		{
			Coins = defaultMoney;
			SaveMoney();
			return;
		}

		var data = DataStore.Load<MoneySaveData>( MoneySaveFile );
		if ( data.Version != version )
		{
			Log.Info( $"[Melon] Money wipe: save version {data.Version} -> {version}" );
			Coins = defaultMoney;
			SaveMoney();
			return;
		}

		Coins = data.Money;
	}

	private void SaveMoney()
	{
		DataStore.Save( new MoneySaveData
		{
			Version = GameManager.Instance?.MoneyVersion ?? 0,
			Money = Coins
		}, MoneySaveFile );
	}

	private void LoadShop()
	{
		_shopData = DataStore.Exists( ShopSaveFile )
			? DataStore.Load<ShopSaveData>( ShopSaveFile )
			: new ShopSaveData();

		if ( _shopData.Version != ShopSaveVersion )
			_shopData = new ShopSaveData { Version = ShopSaveVersion };

		_shopData.OwnedSkins ??= new List<string>();
		_shopData.OwnedColors ??= new List<int>();
		var defaultSkinHeader = GameManager.Instance?.DefaultShopSkin?.Header ?? DefaultShopSkin;

		if ( !_shopData.OwnedSkins.Any( skin => string.Equals( skin, defaultSkinHeader, StringComparison.OrdinalIgnoreCase ) ) )
			_shopData.OwnedSkins.Add( defaultSkinHeader );

		if ( !_shopData.OwnedColors.Contains( 0 ) )
			_shopData.OwnedColors.Add( 0 );

		if ( string.IsNullOrWhiteSpace( _shopData.SelectedSkin )
			|| !_shopData.OwnedSkins.Any( skin => string.Equals( skin, _shopData.SelectedSkin, StringComparison.OrdinalIgnoreCase ) ) )
			_shopData.SelectedSkin = defaultSkinHeader;

		var manager = GameManager.Instance;
		if ( manager is not null && !manager.IsShopColorIndexValid( _shopData.SelectedColor ) )
			_shopData.SelectedColor = 0;

		SaveShop();
	}

	private void SaveShop()
	{
		_shopData.Version = ShopSaveVersion;
		DataStore.Save( _shopData, ShopSaveFile );
	}

	public bool OwnsShopSkin( string skinHeader )
	{
		return _shopData.OwnedSkins?.Any( owned =>
			string.Equals( owned, skinHeader, StringComparison.OrdinalIgnoreCase ) ) == true;
	}

	public bool OwnsShopColor( int colorIndex )
	{
		return _shopData.OwnedColors?.Contains( colorIndex ) == true;
	}

	public bool IsShopSkinSelected( string skinHeader )
	{
		return string.Equals( _shopData.SelectedSkin, skinHeader, StringComparison.OrdinalIgnoreCase );
	}

	public bool IsShopColorSelected( int colorIndex )
	{
		return _shopData.SelectedColor == colorIndex;
	}

	public void BuyOrSelectShopSkin( MelonShopConfig skin )
	{
		if ( IsProxy || skin is null )
			return;

		if ( OwnsShopSkin( skin.Header ) )
		{
			_shopData.SelectedSkin = skin.Header;
			SaveShop();
			QueueSavedShopAppearance( true );
			return;
		}

		RequestShopSkinPurchase( this, skin.Header );
	}

	public void BuyOrSelectShopColor( int colorIndex )
	{
		if ( IsProxy || GameManager.Instance?.IsShopColorIndexValid( colorIndex ) != true )
			return;

		if ( OwnsShopColor( colorIndex ) )
		{
			_shopData.SelectedColor = colorIndex;
			SaveShop();
			QueueSavedShopAppearance();
			return;
		}

		RequestShopColorPurchase( this, colorIndex );
	}

	private void QueueSavedShopAppearance( bool jumpOnSkinChange = false )
	{
		_shopAppearanceRequestId++;
		_submittedShopMapRevision = int.MinValue;
		_pendingShopMapRevision = int.MinValue;
		_pendingShopSkinChangeJump = jumpOnSkinChange;
		SubmitSavedShopAppearance();
	}

	private void SubmitSavedShopAppearance()
	{
		if ( IsProxy )
			return;

		var manager = GameManager.Instance;
		if ( manager is null )
			return;

		var revision = manager.ActiveMapRevision;
		RequestShopAppearance( this, _shopData.SelectedSkin, _shopData.SelectedColor, revision, _shopAppearanceRequestId, _pendingShopSkinChangeJump );
		_pendingShopMapRevision = revision;
		_shopAppearanceRetry = 0.5f;
	}

	private void UpdateShopAppearanceSubmission()
	{
		if ( IsProxy )
			return;

		var manager = GameManager.Instance;
		if ( manager is null )
			return;

		var revision = manager.ActiveMapRevision;
		if ( revision == _submittedShopMapRevision )
			return;

		if ( revision != _pendingShopMapRevision )
		{
			QueueSavedShopAppearance();
			return;
		}

		if ( _shopAppearanceRetry )
			SubmitSavedShopAppearance();
	}

	[Rpc.Host( NetFlags.Reliable )]
	private void RequestShopAppearance( Melon melon, string skinHeader, int colorIndex, int mapRevision, int requestId, bool jumpOnSkinChange )
	{
		if ( !ValidateShopCaller( melon, "appearance" ) )
			return;

		var manager = GameManager.Instance;
		if ( manager is null || !manager.IsShopColorIndexValid( colorIndex ) )
			return;

		manager.ApplyShopAppearance( melon, skinHeader, colorIndex, jumpOnSkinChange );
		melon.ConfirmShopAppearance( mapRevision, requestId );
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ConfirmShopAppearance( int mapRevision, int requestId )
	{
		if ( IsProxy || requestId != _shopAppearanceRequestId )
			return;

		_submittedShopMapRevision = mapRevision;
		_pendingShopMapRevision = int.MinValue;
	}

	[Rpc.Host( NetFlags.Reliable )]
	private void RequestShopSkinPurchase( Melon melon, string skinHeader )
	{
		if ( !ValidateShopCaller( melon, "skin purchase" ) )
			return;

		GameManager.Instance?.PurchaseShopSkin( melon, skinHeader );
	}

	[Rpc.Host( NetFlags.Reliable )]
	private void RequestShopColorPurchase( Melon melon, int colorIndex )
	{
		if ( !ValidateShopCaller( melon, "color purchase" ) )
			return;

		GameManager.Instance?.PurchaseShopColor( melon, colorIndex );
	}

	private static bool ValidateShopCaller( Melon melon, string action )
	{
		if ( !Networking.IsHost || !melon.IsValid() )
			return false;

		var owner = melon.GameObject.Network.Owner;
		if ( Rpc.Caller == owner )
			return true;

		Log.Warning( $"Rejected shop {action} from {Rpc.Caller?.DisplayName ?? "unknown"} for {owner?.DisplayName ?? melon.GameObject.Name}" );
		return false;
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	public void CompleteShopPurchase( bool isColor, string skinHeader, int colorIndex, int price )
	{
		if ( IsProxy )
			return;

		Coins = Math.Max( 0, Coins - Math.Max( 0, price ) );
		SaveMoney();

		if ( isColor )
		{
			if ( !_shopData.OwnedColors.Contains( colorIndex ) )
				_shopData.OwnedColors.Add( colorIndex );

			_shopData.SelectedColor = colorIndex;
		}
		else
		{
			if ( !_shopData.OwnedSkins.Any( owned => string.Equals( owned, skinHeader, StringComparison.OrdinalIgnoreCase ) ) )
				_shopData.OwnedSkins.Add( skinHeader );

			_shopData.SelectedSkin = skinHeader;
		}

		SaveShop();
	}

	public void SetShopAppearance( string skinHeader, int colorIndex )
	{
		if ( !Networking.IsHost )
			return;

		ShopSkinHeader = skinHeader;
		ShopColorIndex = colorIndex;
		_presentedShopSkin = null;
		ApplyShopAppearancePresentation();
	}

	public void TryShopSkinChangeJump()
	{
		if ( !Networking.IsHost || !_shopSkinChangeJumpReady )
			return;

		_shopSkinChangeJumpReady = ShopSkinChangeJumpCooldown;
		ApplyShopSkinChangeJump();
	}

	[Rpc.Owner( NetFlags.HostOnly | NetFlags.Reliable )]
	private void ApplyShopSkinChangeJump()
	{
		if ( IsProxy || IsDead || !Rigidbody.IsValid() || !Rigidbody.PhysicsBody.IsValid() )
			return;

		var velocity = Rigidbody.PhysicsBody.Velocity;
		Rigidbody.PhysicsBody.Velocity = velocity.WithZ(
			MathF.Max( ShopSkinChangeUpVelocity, velocity.z + ShopSkinChangeUpVelocity )
		);
	}

	private void ApplyShopAppearancePresentation()
	{
		var manager = GameManager.Instance;
		if ( manager is null || !Renderer.IsValid() )
			return;

		// ModelRenderer can recreate its SceneObject after a model change. Keep the
		// selected tint applied even when the rest of the appearance is already cached.
		var tint = manager.GetShopColor( ShopColorIndex );
		ApplyShopTint( tint );

		if ( string.Equals( _presentedShopSkin, ShopSkinHeader, StringComparison.OrdinalIgnoreCase )
			&& _presentedShopColor == ShopColorIndex )
			return;

		var skin = manager.GetShopSkin( ShopSkinHeader );
		if ( skin is null || !Collider.IsValid() )
			return;

		var model = Model.Load( skin.Model.Name );
		if ( model.IsValid() )
			Renderer.Model = model;

		ApplyShopTint( tint );
		Renderer.GameObject.LocalScale = Vector3.One * MathF.Max( 0.01f, skin.Scale );
		Collider.Radius = MathF.Max( 0.01f, skin.SphereRadius );
		Collider.Center = skin.SphereCenter;

		_presentedShopSkin = skin.Header;
		_presentedShopColor = ShopColorIndex;
	}

	private void ApplyShopTint( Color tint )
	{
		Renderer.Tint = tint;

		if ( Renderer.SceneObject.IsValid() )
			Renderer.SceneObject.ColorTint = tint;
	}

    protected override void OnFixedUpdate()
    {
		if ( IsProxy )
			return;

		if ( IsDead )
		{
			StopMovement();
			return;
		}

		if ( GameManager.Instance?.IsMapVoteOpen == true )
			return;

        if (!Rigidbody.IsValid())
            return;

		UpdateBoostFade();

		if ( Input.Down( "Duck" ) )
		{
			ApplyBrake();
			return;
		}

        var wishDirection = GetWishDirection();

        _moveDirection = Vector3.Lerp(_moveDirection, wishDirection, Time.Delta * Inertia); // this is inertia

        if (!_moveDirection.IsNearZeroLength)
            Rigidbody.ApplyForce(_moveDirection * Speed * Rigidbody.PhysicsBody.Mass);

		TryBoost( wishDirection );

        if (Input.Pressed("Jump") && _jumpReady && IsGrounded())
        {
            Rigidbody.ApplyImpulse(Vector3.Up * JumpForce * Rigidbody.PhysicsBody.Mass);
            _ignoreJumpVertical = true;
            _jumpReady = JumpDelay;
        }
    }

    protected override void OnDestroy()
    {
		if ( Local == this )
			Local = null;
    }

    protected override void OnUpdate()
    {
		if ( Networking.IsHost && IsDead && _respawnTimer )
			GameManager.Instance?.RespawnSmashedMelon( this );

		ApplyShopAppearancePresentation();
		UpdateDeathPresentation();
		UpdateWorldHud();

		if ( IsProxy )
			return;

		UpdateShopAppearanceSubmission();

		if ( Input.Pressed( "Kill" ) )
			RequestKillSelf();

		if ( IsDead )
			return;

		if ( GameManager.Instance?.IsMapVoteOpen == true )
			return;

		if ( EnableCameraSystem )
			UpdateCamera();
    }

	[ConCmd( "kill" )]
	public static void KillCommand()
	{
		Local?.RequestKillSelf();
	}

	public void RequestKillSelf()
	{
		if ( IsProxy )
			return;

		RequestKill( this );
	}

	[Rpc.Host( NetFlags.OwnerOnly | NetFlags.Reliable )]
	private void RequestKill( Melon melon )
	{
		if ( !Networking.IsHost || !melon.IsValid() )
			return;

		var owner = melon.GameObject.Network.Owner;
		if ( Rpc.Caller != owner )
		{
			Log.Warning( $"Rejected kill request from {Rpc.Caller?.DisplayName ?? "unknown"} for {owner?.DisplayName ?? melon.GameObject.Name}" );
			return;
		}

		GameManager.Instance?.KillMelon( melon );
	}

	private void UpdateWorldHud()
	{
		if ( !WorldHud.IsValid() )
			return;

		var target = WorldPosition + _worldHudOffset;

		if ( _hudPosition.Distance( target ) > 100f )
		{
			_hudPosition = target;
		}
		else
		{
			if ( Rigidbody.IsValid() )
				_hudPosition += Rigidbody.Velocity.WithZ( 0 ) * Time.Delta;

			var driftLerp = 1f - MathF.Exp( -MathF.Max( 0f, HudDriftCorrection ) * Time.Delta );
			_hudPosition = _hudPosition.LerpTo( target.WithZ( _hudPosition.z ), driftLerp );

			var verticalLerp = 1f - MathF.Exp( -MathF.Max( 0f, HudVerticalSmooth ) * Time.Delta );
			_hudPosition = _hudPosition.WithZ( _hudPosition.z + (target.z - _hudPosition.z) * verticalLerp );
		}

		WorldHud.GameObject.WorldPosition = _hudPosition;
		WorldHud.GameObject.WorldRotation = Rotation.Identity;
	}

	void Component.ICollisionListener.OnCollisionStart( Collision collision )
	{
		if ( IsProxy || IsDead || !_smashReady )
			return;

		var impactSpeed = collision.Contact.Speed.Length;
		if ( impactSpeed < SmashSpeed )
			return;

		_smashReady = SmashCooldown;
		Log.Info( $"Melon smashed at speed {impactSpeed:0}" );

		var otherMelon = collision.Other.GameObject.Components.Get<Melon>( FindMode.EverythingInSelfAndAncestors );
		RequestSmash( otherMelon?.GameObject );
	}

	[Rpc.Host]
	private void RequestSmash( GameObject collidedMelon )
	{
		var gameManager = GameManager.Instance;
		gameManager?.SmashMelon( this );

		if ( !collidedMelon.IsValid() )
			return;

		var otherMelon = collidedMelon.Components.Get<Melon>();
		if ( !otherMelon.IsValid() || otherMelon == this )
			return;

		gameManager?.SmashMelon( otherMelon );
	}

	public bool BeginDeath( bool ignoreSpawnInvulnerability = false )
	{
		if ( !Networking.IsHost || IsDead || (!ignoreSpawnInvulnerability && !_spawnInvulnerability) )
			return false;

		IsDead = true;
		_respawnTimer = MathF.Max( 0f, RespawnDelay );

		SpawnDeathEffects( WorldPosition );
		PlayDeathSound( WorldPosition );
		UpdateDeathPresentation();

		return true;
	}

	public void CompleteDeath()
	{
		if ( !Networking.IsHost )
			return;

		IsDead = false;
		_respawnTimer = 0f;
		StartSpawnInvulnerability();
		UpdateDeathPresentation();
	}

	private void SpawnDeathEffects( Vector3 position )
	{
		if ( !Networking.IsHost )
			return;

		SpawnDeathEffect( DeathEffectPrefab, position, Rotation.Identity );
		SpawnDeathEffect( DeathBurstEffectPrefab, position, Rotation.FromYaw( 180f ) );
	}

	private static void SpawnDeathEffect( GameObject prefab, Vector3 position, Rotation rotation )
	{
		if ( !prefab.IsValid() )
			return;

		var effect = prefab.Clone( new CloneConfig
		{
			Transform = new Transform( position, rotation ),
			StartEnabled = false
		} );

		if ( !effect.IsValid() )
			return;

		effect.NetworkMode = NetworkMode.Object;
		effect.Network.SetOrphanedMode( NetworkOrphaned.Host );
		effect.NetworkSpawn();
		effect.Enabled = true;
	}

	[Rpc.Broadcast( NetFlags.Reliable )]
	private void PlayDeathSound( Vector3 position )
	{
		if ( DeathSound is not null )
			Sound.Play( DeathSound, position );
	}

	public void RespawnAt( Vector3 position, Rotation rotation )
	{
		GameObject.WorldPosition = position;
		GameObject.WorldRotation = rotation;

		var spawnForward = rotation.Forward.WithZ( 0f );
		if ( !spawnForward.IsNearZeroLength )
			_cameraYaw = MathF.Atan2( spawnForward.y, spawnForward.x ) * 180f / MathF.PI;

		StartSpawnInvulnerability();

		_moveDirection = Vector3.Zero;
		_boostDirection = Vector3.Zero;
		_boostSpeedRemaining = 0f;
		_boostFadeRate = 0f;
		_boostReady = 0f;
		_zoomDistance = DefaultZoom;
		_focusPosition = position;
		_hudPosition = position + _worldHudOffset;
		_ignoreJumpVertical = false;

		if ( !Rigidbody.IsValid() )
			return;

		Rigidbody.ClearForces();

		if ( Rigidbody.PhysicsBody.IsValid() )
		{
			Rigidbody.PhysicsBody.Velocity = rotation.Forward * MathF.Max( 0f, SpawnVelocity );
			Rigidbody.PhysicsBody.AngularVelocity = Vector3.Zero;
			Rigidbody.PhysicsBody.ClearForces();
			Rigidbody.PhysicsBody.ClearTorque();
		}
	}

	private void StartSpawnInvulnerability()
	{
		_spawnInvulnerability = MathF.Max( 0f, SpawnInvulnerabilityDuration );
	}

	public void ResetRaceProgress( int activeSegmentId, float raceElapsed )
	{
		if ( !Networking.IsHost )
			return;

		ActiveSegmentId = activeSegmentId;
		CompletedLaps = 0;
		CurrentLapStartedAt = raceElapsed;
		LastLapTime = 0f;
		BestLapTime = 0f;
		HasFinishedRace = false;
	}

	public void SetActiveSegment( int segmentId )
	{
		if ( !Networking.IsHost )
			return;

		ActiveSegmentId = segmentId;
	}

	public void CompleteLap( int completedLaps, float lapTime, int nextSegmentId, float raceElapsed )
	{
		if ( !Networking.IsHost )
			return;

		CompletedLaps = completedLaps;
		LastLapTime = MathF.Max( 0f, lapTime );

		if ( BestLapTime <= 0f || LastLapTime < BestLapTime )
			BestLapTime = LastLapTime;

		CurrentLapStartedAt = raceElapsed;
		ActiveSegmentId = nextSegmentId;
	}

	public void FinishRace()
	{
		if ( !Networking.IsHost )
			return;

		HasFinishedRace = true;
	}
}
