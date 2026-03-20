using Godot;
using System;
namespace Loadout.UI;
public partial class LoadoutPanelRoot : Control
{
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}
	
	public static void AttachToTree(SceneTree tree)
	{
		if (tree == null) return;

		if (tree.Root.GetNodeOrNull("/Game/LoadoutPanelRoot") != null)
			return;

		var modRoot = new LoadoutPanelRoot();
		modRoot.Name = "LoadoutPanelRoot";
		modRoot.ZIndex = 999;
		
		tree.Root.CallDeferred(Node.MethodName.AddChild, modRoot);
	}
}
