using Sandbox;
using System;
using Ambi.MelonRacer;

public sealed class Coin : Component, Component.ITriggerListener
{
    [Property] public ModelRenderer Renderer { get; set; }
    [Property] public PointLight PointLight { get; set; }
    [Property] public GameObject ParticleTouch { get; set; }
    [Property] public SoundEvent SoundTouch { get; set; }

    [Property] public float RotationSpeed { get; set; } = 64f;
    [Property] public float MovementSpeed { get; set; } = 1f;
    [Property] public float MovementAmplitude { get; set; } = 1f;

    [Sync( SyncFlags.FromHost )] public bool IsOpen { get; private set; }

    private Vector3 _startPos = Vector3.Zero;

    public void Open()
    {
        if ( !Networking.IsHost )
            return;

        IsOpen = true;
		UpdatePresentation();
    }

    public void Close()
    {
        if ( !Networking.IsHost )
            return;

        IsOpen = false;
		UpdatePresentation();
    }

	public void SpawnTouchParticle()
	{
		if ( !Networking.IsHost || !ParticleTouch.IsValid() )
			return;

		var particle = ParticleTouch.Clone( new CloneConfig
		{
			Transform = new Transform( WorldPosition, WorldRotation ),
			StartEnabled = false
		} );

		if ( !particle.IsValid() )
			return;

		particle.NetworkMode = NetworkMode.Object;
		particle.Network.SetOrphanedMode( NetworkOrphaned.Host );
		particle.NetworkSpawn();
		particle.Enabled = true;
	}

	public void PlayTouchSound()
	{
		if ( SoundTouch is not null )
			Sound.Play( SoundTouch );
	}

    void ITriggerListener.OnTriggerEnter(GameObject other)
    {
        if (!IsOpen) return;

        var melon = other.Components.Get<Melon>( FindMode.EverythingInSelfAndAncestors );
        if ( !melon.IsValid() || melon.IsProxy || melon.IsDead )
            return;

        melon.TouchCoin( this );
    }

    protected override void OnStart()
    {
        _startPos = WorldPosition;
        UpdatePresentation();
    }

    protected override void OnFixedUpdate()
	{
        UpdatePresentation();

        if ( !IsOpen )
            return;

        WorldRotation *= Rotation.FromYaw(RotationSpeed * Time.Delta);
        WorldPosition = _startPos + Vector3.Up * MathF.Sin( Time.Now * MovementSpeed ) * MovementAmplitude;
    }

    private void UpdatePresentation()
    {
        if ( Renderer.IsValid() )
            Renderer.Enabled = IsOpen;

		if ( PointLight.IsValid() )
			PointLight.Enabled = IsOpen;
    }
}
