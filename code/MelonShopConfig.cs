using Sandbox;
using System.Collections.Generic;

namespace Ambi.MelonRacer;

[AssetType( Name = "Melon Shop Config", Extension = "mshop", Category = "Melon Racer" )]
public sealed partial class MelonShopConfig : GameResource
{
	private static readonly List<MelonShopConfig> _all = new();

	public static IReadOnlyList<MelonShopConfig> All => _all;

	public string Header { get; set; } = "Melon";
	public int Price { get; set; }

	public Model Model { get; set; }

	public float Scale { get; set; } = 1f;
	public float SphereRadius { get; set; } = 8f;
	public Vector3 SphereCenter { get; set; } = Vector3.Zero;

	protected override void PostLoad()
	{
		base.PostLoad();

		if ( !_all.Contains( this ) )
			_all.Add( this );
	}
}
