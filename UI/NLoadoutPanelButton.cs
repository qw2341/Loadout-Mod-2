using Godot;

namespace Loadout.UI;
public partial class NLoadoutPanelButton : Button
{
	private NLoadoutPanel _nLoadoutPanel;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_nLoadoutPanel = GetParent<NLoadoutPanel>();
		Pressed += OnPressed;
	}

	private void OnPressed()
	{
		_nLoadoutPanel.ToggleShown();
		this.Text = _nLoadoutPanel.Shown ? "<" : ">"; // TODO: change to sprites instead of text
	}
}
