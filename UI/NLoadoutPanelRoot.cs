using Godot;
using Loadout.UI.Managers;
namespace Loadout.UI;
public partial class NLoadoutPanelRoot : Control
{
	private static Control _overlayInstance;
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		LoadoutThemeManager.ThemeChanged += OnThemeChanged;
		LoadoutThemeManager.ApplyTheme(this);
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

	public override void _ExitTree()
	{
		LoadoutThemeManager.ThemeChanged -= OnThemeChanged;
	}
	
	private void OnSceneChanged()
	{
		EnsureOverlay();
	}

	private void EnsureOverlay()
	{
		if (IsInstanceValid(_overlayInstance))
		{
			if (_overlayInstance.GetParent() == null)
				GetTree().Root.CallDeferred(Node.MethodName.AddChild, _overlayInstance);
		}
	}
	
	public static void AttachToTree(SceneTree tree)
	{
		if (tree == null) return;

		if (tree.Root.GetNodeOrNull("/Game/LoadoutPanelRoot") != null)
			return;

		var modRootScene = ResourceLoader.Load<PackedScene>("res://UI/LoadoutPanelRoot.tscn");
		var modRoot = modRootScene.Instantiate<NLoadoutPanelRoot>();
		modRoot.Name = "LoadoutPanelRoot";
		modRoot.ZIndex = 999;
		_overlayInstance = modRoot;
		modRoot.SetAnchorsPreset(LayoutPreset.FullRect);
		GD.Print("LoadoutPanelRoot has been initialized. Attaching to root.");
		tree.Root.CallDeferred(Node.MethodName.AddChild, modRoot);
	}

	private void OnThemeChanged(string _)
	{
		// LoadoutThemeManager.ApplyTheme(this);
	}
}
