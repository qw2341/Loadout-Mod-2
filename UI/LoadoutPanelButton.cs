using Godot;

namespace Loadout.UI;
public partial class LoadoutPanelButton : Button
{
	private LoadoutPanel _loadoutPanel;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_loadoutPanel = GetParent<LoadoutPanel>();
		Pressed += OnPressed;
	}

	private void OnPressed()
	{
		_loadoutPanel?.ToggleShown();
	}
}
