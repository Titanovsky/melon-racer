using Sandbox;
using System;

namespace Ambi.MelonRacer;

public sealed class Melon : Component
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

	[Property, Group( "Camera" )] public float CameraDistance { get; set; } = 250f;
	[Property, Group( "Camera" )] public float CameraSmooth { get; set; } = 8f;
	[Property, Group( "Camera" )] public float CameraVerticalDeadZone { get; set; } = 4f;
	[Property, Group( "Camera" )] public float CameraVerticalSmooth { get; set; } = 3f;
	[Property, Group( "Camera" )] public float CameraRotateSpeed { get; set; } = 4f;
	[Property, Group( "Camera" )] public float CameraSensitivity { get; set; } = 0.15f;
	[Property, Group( "Camera" )] public float AutoFollowDelay { get; set; } = 1.5f;

	[Sync( SyncFlags.FromHost )] public int ActiveSegmentId { get; private set; }
	[Sync( SyncFlags.FromHost )] public int CompletedLaps { get; private set; }
	[Sync( SyncFlags.FromHost )] public float CurrentLapStartedAt { get; private set; }
	[Sync( SyncFlags.FromHost )] public float LastLapTime { get; private set; }
	[Sync( SyncFlags.FromHost )] public float BestLapTime { get; private set; }
	[Sync( SyncFlags.FromHost )] public bool HasFinishedRace { get; private set; }

	private TimeUntil _jumpReady;
	private TimeUntil _autoFollowReady;
	private Vector3 _moveDirection;
	private Vector3 _followPosition;
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

		if ( Rigidbody.IsValid() )
			_followPosition += Rigidbody.Velocity.WithZ( 0 ) * Time.Delta;

		var isGrounded = IsGrounded();
		var verticalVelocity = Rigidbody.IsValid() ? Rigidbody.Velocity.z : 0f;
		if ( _ignoreJumpVertical && isGrounded && verticalVelocity <= 0f )
			_ignoreJumpVertical = false;

		var canFollowVertical = isGrounded && !_ignoreJumpVertical;
		var horizontalError = WorldPosition.WithZ( _followPosition.z ) - _followPosition;
		var verticalError = WorldPosition.z - _followPosition.z;
		if ( horizontalError.Length > 50f )
		{
			_followPosition = canFollowVertical ? WorldPosition : WorldPosition.WithZ( _followPosition.z );
		}
		else if ( canFollowVertical && MathF.Abs( verticalError ) > 50f )
		{
			_followPosition = WorldPosition;
		}
		else
		{
			var horizontalTarget = WorldPosition.WithZ( _followPosition.z );
			_followPosition = _followPosition.LerpTo( horizontalTarget, 1f - MathF.Exp( -CameraSmooth * 0.5f * Time.Delta ) );

			var verticalDeadZone = MathF.Max( 0f, CameraVerticalDeadZone );
			if ( canFollowVertical && MathF.Abs( verticalError ) > verticalDeadZone )
			{
				var verticalTarget = WorldPosition.z - MathF.Sign( verticalError ) * verticalDeadZone;
				var verticalLerp = 1f - MathF.Exp( -MathF.Max( 0f, CameraVerticalSmooth ) * Time.Delta );
				_followPosition = _followPosition.WithZ( _followPosition.z + (verticalTarget - _followPosition.z) * verticalLerp );
			}
		}

		var look = Input.AnalogLook;
		if ( !look.IsNearlyZero() )
		{
			var sensitivity = Preferences.Sensitivity * CameraSensitivity;

			_cameraYaw += look.yaw * sensitivity;
			_cameraPitch = ( _cameraPitch + look.pitch * sensitivity ).Clamp( 5f, 70f );
			_autoFollowReady = AutoFollowDelay;
		}

		var velocity = Rigidbody.IsValid() ? Rigidbody.Velocity.WithZ( 0 ) : Vector3.Zero;
		if ( _autoFollowReady && velocity.Length > 10f )
		{
			var targetYaw = velocity.EulerAngles.yaw;
			_cameraYaw = MathX.LerpDegrees( _cameraYaw, targetYaw, Time.Delta * CameraRotateSpeed );
		}

		var orbitRotation = Rotation.From( _cameraPitch, _cameraYaw, 0f );
		var targetPosition = _followPosition - orbitRotation.Forward * CameraDistance;

		camera.WorldPosition = camera.WorldPosition.LerpTo( targetPosition, 1f - MathF.Exp( -CameraSmooth * Time.Delta ) );
		camera.WorldRotation = Rotation.Slerp( camera.WorldRotation,
			Rotation.LookAt( _followPosition + Vector3.Up * 20f - camera.WorldPosition ),
			1f - MathF.Exp( -CameraSmooth * Time.Delta ) );
	}

	[Rpc.Broadcast]
	private void SetupName()
	{
		WorldHud.Name = Connection.Local.Name;
	}

	protected override void OnStart()
	{
		if (!IsProxy)
		{
			Local = this;
			SetupName();
		}

        _followPosition = WorldPosition;

		if ( Networking.IsHost )
			GameManager.Instance?.RegisterMelon( this );
    }

    protected override void OnFixedUpdate()
    {
		if ( IsProxy )
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
		if (Local != null)
			Local = null;
    }

    protected override void OnUpdate()
    {
		if ( IsProxy )
			return;

        UpdateCamera();
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
