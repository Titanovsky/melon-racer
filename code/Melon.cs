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

	[Property, Group( "Jump" )] public float JumpForce { get; set; } = 400f;
	[Property, Group( "Jump" )] public float JumpDelay { get; set; } = 0.5f;

	[Property, Group( "Smash" )] public float SmashSpeed { get; set; } = 700f;
	[Property, Group( "Smash" )] public float SmashCooldown { get; set; } = 1f;

	[Property, Group( "Camera" )] public float CameraSensitivity { get; set; } = 0.15f;
	[Property, Group( "Camera" )] public float ZoomMin { get; set; } = 0f;
	[Property, Group( "Camera" )] public float ZoomMax { get; set; } = 250f;
	[Property, Group( "Camera" )] public float ZoomSpeed { get; set; } = 200f;
	[Property, Group( "Camera" )] public float DefaultZoom { get; set; } = 100f;
	[Property, Group( "Camera" )] public float CameraPitchMin { get; set; } = -30f;
	[Property, Group( "Camera" )] public float CameraPitchMax { get; set; } = 80f;
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

	private TimeUntil _jumpReady;
	private TimeUntil _smashReady;
	private Vector3 _worldHudOffset;
	private Vector3 _hudPosition;
	private Vector3 _moveDirection;
	private float _zoomDistance;
	private Vector3 _focusPosition;
	private bool _ignoreJumpVertical;
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

		if ( Input.Down( "Run" ) )
			_zoomDistance = MathF.Min( ZoomMax, _zoomDistance + ZoomSpeed * Time.Delta );
		else if ( Input.Down( "Duck" ) )
			_zoomDistance = MathF.Max( ZoomMin, _zoomDistance - ZoomSpeed * Time.Delta );

		UpdateFocusPosition();

		var eyeRotation = Rotation.From( _cameraPitch, _cameraYaw, 0f );
		var targetPosition = _focusPosition - eyeRotation.Forward * _zoomDistance;

		var trace = Scene.Trace
			.Ray( _focusPosition, targetPosition )
			.IgnoreGameObjectHierarchy( GameObject )
			.Run();

		camera.WorldPosition = trace.Hit
			? trace.HitPosition + trace.Normal * 2f
			: targetPosition;
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

	protected override void OnStart()
	{
		if (!IsProxy)
			Local = this;

		if ( WorldHud.IsValid() )
		{
			WorldHud.Name = GameObject.Network.Owner?.Name ?? Connection.Local?.Name ?? "";
			_worldHudOffset = WorldHud.GameObject.WorldPosition - WorldPosition;
			_hudPosition = WorldHud.GameObject.WorldPosition;
		}

        _zoomDistance = DefaultZoom;
		_focusPosition = WorldPosition;

		if ( Networking.IsHost )
			GameManager.Instance?.RegisterMelon( this );
    }

    protected override void OnFixedUpdate()
    {
		if ( IsProxy )
			return;

		if ( GameManager.Instance?.IsMapVoteOpen == true )
			return;

        if (!Rigidbody.IsValid())
            return;

        var wishDirection = GetWishDirection();

        _moveDirection = Vector3.Lerp(_moveDirection, wishDirection, Time.Delta * Inertia); // this is inertia

        if (!_moveDirection.IsNearZeroLength)
            Rigidbody.ApplyForce(_moveDirection * Speed * Rigidbody.PhysicsBody.Mass);

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
		UpdateWorldHud();

		if ( IsProxy )
			return;

		if ( GameManager.Instance?.IsMapVoteOpen == true )
			return;

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
		if ( IsProxy || !_smashReady )
			return;

		var impactSpeed = collision.Contact.Speed.Length;
		if ( impactSpeed < SmashSpeed )
			return;

		_smashReady = SmashCooldown;
		Log.Info( $"Melon smashed at speed {impactSpeed:0}" );
		RequestSmash();
	}

	[Rpc.Host]
	private void RequestSmash()
	{
		GameManager.Instance?.SmashMelon( this );
	}

	public void RespawnAt( Vector3 position, Rotation rotation )
	{
		GameObject.WorldPosition = position;
		GameObject.WorldRotation = rotation;

		_moveDirection = Vector3.Zero;
		_zoomDistance = DefaultZoom;
		_focusPosition = position;
		_hudPosition = position + _worldHudOffset;
		_ignoreJumpVertical = false;

		if ( !Rigidbody.IsValid() )
			return;

		Rigidbody.ClearForces();

		if ( Rigidbody.PhysicsBody.IsValid() )
		{
			Rigidbody.PhysicsBody.Velocity = Vector3.Zero;
			Rigidbody.PhysicsBody.AngularVelocity = Vector3.Zero;
			Rigidbody.PhysicsBody.ClearForces();
			Rigidbody.PhysicsBody.ClearTorque();
		}
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
