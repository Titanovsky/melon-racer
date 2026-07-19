using Sandbox;
using System;

public sealed class TriggerSegment : Component, Component.ITriggerListener
{
    [Property] public int SegmentId { get; set; } = 0;
	[Property] public bool FinalSegment { get; set; }
	// TODO: Temporary workaround until Facepunch fixes the MeshComponent tint bug.
	[Property] public ModelRenderer Renderer1 { get; set; }
	[Property] public ModelRenderer Renderer2 { get; set; }
	[Property] public ModelRenderer Renderer3 { get; set; }
	[Property] public SpriteRenderer Sprite { get; set; }
	[Property] public PointLight PointLight { get; set; }
	[Property, Group( "Sprite" )] public float SpriteMovementSpeed { get; set; } = 1f;
	[Property, Group( "Sprite" )] public float SpriteMovementAmplitude { get; set; } = 1f;
    [Property] public SoundEvent TouchSound { get; set; }
	[Property] public SoundEvent FinalTouchSound { get; set; }
	[Property, Group( "Colors" )] public Color InactiveColor { get; set; } = new( 1f, 0.55f, 0.55f );
	[Property, Group( "Colors" )] public Color ActiveColor { get; set; } = new( 0.55f, 1f, 0.6f );
	[Property, Group( "Colors" )] public float ColorTransitionSpeed { get; set; } = 8f;

	private Vector3 _spriteStartPosition;

	protected override void OnStart()
	{
		if ( Sprite.IsValid() )
			_spriteStartPosition = Sprite.GameObject.WorldPosition;
	}

	protected override void OnUpdate()
	{
		var melon = Ambi.MelonRacer.Melon.Local;
		var isActive = melon.IsValid()
			&& !melon.HasFinishedRace
			&& melon.ActiveSegmentId == SegmentId;

		if ( Sprite.IsValid() )
		{
			Sprite.Enabled = isActive;
			Sprite.GameObject.WorldPosition = _spriteStartPosition
				+ Vector3.Up * MathF.Sin( Time.Now * SpriteMovementSpeed ) * SpriteMovementAmplitude;
		}

		if ( PointLight.IsValid() )
			PointLight.Enabled = isActive;

		if ( FinalSegment )
			return;

		var targetColor = isActive ? ActiveColor : InactiveColor;
		var transition = 1f - MathF.Exp( -MathF.Max( 0f, ColorTransitionSpeed ) * Time.Delta );

		UpdateRendererTint( Renderer1, targetColor, transition );
		UpdateRendererTint( Renderer2, targetColor, transition );
		UpdateRendererTint( Renderer3, targetColor, transition );
	}

	private static void UpdateRendererTint( ModelRenderer renderer, Color targetColor, float transition )
	{
		if ( renderer.IsValid() )
			renderer.Tint = renderer.Tint.LerpTo( targetColor, transition );
	}

    void ITriggerListener.OnTriggerEnter(GameObject other)
    {
        var melon = other.Components.Get<Ambi.MelonRacer.Melon>();
        if ( !melon.IsValid() )
            return;

		var gameManager = Ambi.MelonRacer.GameManager.Instance;

		if ( melon == Ambi.MelonRacer.Melon.Local && melon.ActiveSegmentId == SegmentId )
			gameManager?.PlayLocalSegmentSound( SegmentId, TouchSound, FinalTouchSound );

		if ( melon == Ambi.MelonRacer.Melon.Local )
		{
			melon.TouchSegment( SegmentId );
			return;
		}

		if ( Networking.IsHost )
			gameManager?.PassSegment( melon, SegmentId );
    }
}
