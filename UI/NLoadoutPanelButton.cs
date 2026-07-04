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
		_nLoadoutPanel.VisibilityStateChanged += RefreshState;
		Pressed += OnPressed;
		_signalsConnected = true;
		RefreshState();
	}

	public override void _ExitTree()
	{
		if (!_signalsConnected)
			return;

		Pressed -= OnPressed;
		_nLoadoutPanel.VisibilityStateChanged -= RefreshState;
		_signalsConnected = false;
	}

	private void OnPressed()
	{
		if (_nLoadoutPanel.Hidden)
		{
			RefreshState();
			return;
		}

		_nLoadoutPanel.ToggleShown();
		RefreshState();
	}

	private void RefreshState()
	{
		Disabled = _nLoadoutPanel.Hidden;
		Text = _nLoadoutPanel.Shown ? "<" : ">"; // TODO: change to sprites instead of text
	}
}
