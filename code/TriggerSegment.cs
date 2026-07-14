using Sandbox;
using System;

public sealed class TriggerSegment : Component, Component.ITriggerListener
{
    [Property] public int SegmentId { get; set; } = 0;
	[Property] public bool FinalSegment { get; set; }
    [Property] public MeshComponent Mesh { get; set; }
    [Property] public SoundEvent TouchSound { get; set; }
	[Property] public SoundEvent FinalTouchSound { get; set; }
	[Property, Group( "Colors" )] public Color InactiveColor { get; set; } = new( 1f, 0.55f, 0.55f );
	[Property, Group( "Colors" )] public Color ActiveColor { get; set; } = new( 0.55f, 1f, 0.6f );
	[Property, Group( "Colors" )] public float ColorTransitionSpeed { get; set; } = 8f;

	protected override void OnUpdate()
	{
		if ( FinalSegment || !Mesh.IsValid() )
			return;

		var melon = Ambi.MelonRacer.Melon.Local;
		var isActive = melon.IsValid()
			&& !melon.HasFinishedRace
			&& melon.ActiveSegmentId == SegmentId;
		var targetColor = isActive ? ActiveColor : InactiveColor;
		var transition = 1f - MathF.Exp( -MathF.Max( 0f, ColorTransitionSpeed ) * Time.Delta );

		Mesh.Color = Mesh.Color.LerpTo( targetColor, transition );
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
