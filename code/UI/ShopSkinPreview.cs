using Ambi.MelonRacer;
using Sandbox;
using Sandbox.UI;
using System;

namespace Sandbox;

[Library( "ShopSkinPreview" )]
public sealed class ShopSkinPreview : ScenePanel
{
	private readonly SceneWorld _previewWorld;
	private SceneModel _sceneModel;
	private MelonShopConfig _skin;

	public MelonShopConfig Skin
	{
		get => _skin;
		set
		{
			if ( ReferenceEquals( _skin, value ) )
				return;

			_skin = value;
			RebuildModel();
		}
	}

	public ShopSkinPreview()
	{
		_previewWorld = new SceneWorld
		{
			AmbientLightColor = Color.White * 0.45f
		};

#pragma warning disable CS0618
		Camera.World = _previewWorld;
		Camera.AmbientLightColor = Color.White * 0.35f;
		Camera.BackgroundColor = Color.Transparent;
		Camera.FieldOfView = 35f;
		Camera.ZNear = 0.1f;
		Camera.ZFar = 2048f;
		Camera.AntiAliasing = true;
		Camera.EnablePostProcessing = true;
#pragma warning restore CS0618

		new ScenePointLight( _previewWorld, new Vector3( 80f, -80f, 100f ), 350f, Color.White * 3f )
		{
			ShadowsEnabled = false
		};

		new ScenePointLight( _previewWorld, new Vector3( -60f, 80f, 50f ), 300f, new Color( 0.45f, 0.65f, 1f ) * 2f )
		{
			ShadowsEnabled = false
		};

		RenderOnce = true;
	}

	private void RebuildModel()
	{
		if ( _sceneModel.IsValid() )
		{
			_sceneModel.Delete();
			_sceneModel = null;
		}

		var model = Skin?.Model;
		if ( !model.IsValid() )
			return;

		var scale = MathF.Max( 0.01f, Skin.Scale );
		var bounds = model.Bounds;
		var center = bounds.Center * scale;
		var radius = MathF.Max( 4f, bounds.Size.Length * scale * 0.5f );

		_sceneModel = new SceneModel(
			_previewWorld,
			model,
			new Transform( -center, Rotation.FromYaw( 25f ), scale )
		);

#pragma warning disable CS0618
		var distance = radius * 2.35f;
		Camera.Position = new Vector3( distance, -distance, radius * 0.65f );
		Camera.Rotation = Rotation.LookAt( -Camera.Position, Vector3.Up );
#pragma warning restore CS0618

		RenderNextFrame();
	}

	public override void OnDeleted()
	{
		if ( _sceneModel.IsValid() )
			_sceneModel.Delete();

		_previewWorld?.Delete();
		base.OnDeleted();
	}
}
