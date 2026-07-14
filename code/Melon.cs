using Sandbox;
using System;

namespace Ambi.MelonRacer;

public sealed class Melon : Component, Component.ICollisionListener
{
    public static Melon Local { get; private set; }

    [Property] public ModelRenderer Renderer { get; set; }
	[Property] public Rigidbody Rigidbody { get; set; }
	[Property] public SphereCollider Collider { get; set; }
    [Property] public PlayerWorldHud WorldHud { get; set; }

    [Property, Group( "Movement" )] public float Speed { get; set; } = 500f;
	[Property, Group( "Movement" )] public float Inertia { get; set; } = 3f;
	[Property, Group( "Movement" )] public float BrakeDeceleration { get; set; } = 1800f;
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
		var radius = Collider.IsValid() ? Collider.Radius : 16f;

		var trace = Scene.Trace
			.Sphere( radius * 0.9f, WorldPosition, WorldPosition + Vector3.Down * radius * 0.3f )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		return trace.Hit;
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

		_moveDirection = Vector3.Zero;

		var body = Rigidbody.PhysicsBody;
		var deceleration = MathF.Max( 0f, BrakeDeceleration );
		var horizontalVelocity = body.Velocity.WithZ( 0f );
		var speedToRemove = MathF.Min( horizontalVelocity.Length, deceleration * Time.Delta );

		if ( speedToRemove > 0f )
			body.Velocity -= horizontalVelocity.Normal * speedToRemove;

		var radius = Collider.IsValid() ? MathF.Max( 1f, Collider.Radius ) : 8f;
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

			if ( Networking.IsHost )
				GameManager.Instance?.RegisterMelon( this );
			else
				RequestInitialSpawn();
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
		if (Local == this)
			Local = null;
    }

    protected override void OnUpdate()
    {
		if ( Networking.IsHost && IsDead && _respawnTimer )
			GameManager.Instance?.RespawnSmashedMelon( this );

		UpdateDeathPresentation();
		UpdateWorldHud();

		if ( IsProxy )
			return;

		if ( IsDead )
			return;

		if ( GameManager.Instance?.IsMapVoteOpen == true )
			return;

		if ( EnableCameraSystem )
			UpdateCamera();
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

	public bool BeginDeath()
	{
		if ( !Networking.IsHost || IsDead || !_spawnInvulnerability )
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
