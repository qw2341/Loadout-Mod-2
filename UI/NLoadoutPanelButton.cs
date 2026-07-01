using Godot;

namespace Loadout.UI;
public partial class NLoadoutPanelButton : Button
{
	private NLoadoutPanel _nLoadoutPanel;
	private bool _signalsConnected;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_nLoadoutPanel = GetParent<NLoadoutPanel>();
		Pressed += OnPressed;
		_signalsConnected = true;
	}

	public override void _ExitTree()
	{
		if (!_signalsConnected)
			return;

		Pressed -= OnPressed;
		_signalsConnected = false;
	}

	private void OnPressed()
	{
		_nLoadoutPanel.ToggleShown();
		this.Text = _nLoadoutPanel.Shown ? "<" : ">"; // TODO: change to sprites instead of text
	}
}
