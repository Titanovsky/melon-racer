using Sandbox;

public sealed class TriggerSegment : Component, Component.ITriggerListener
{
    [Property] public int SegmentId { get; set; } = 0;
    [Property] public MeshComponent Mesh { get; set; }
    [Property] public SoundEvent TouchSound { get; set; }

    void ITriggerListener.OnTriggerEnter(GameObject other)
    {
        if ( !Networking.IsHost )
            return;

        var melon = other.Components.Get<Ambi.MelonRacer.Melon>();
        if ( !melon.IsValid() )
            return;

        Ambi.MelonRacer.GameManager.Instance?.PassSegment( melon, SegmentId );
    }
}
