using Godot;

namespace  Loadout.UI;

public partial class LoadoutPanel : Panel
{
	[Export]
	public bool Shown = true;

	[Export]
	public float SlideSpeed = 12f;

	private Vector2 _shownPosition;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_shownPosition = Position;
		Position = GetTargetPosition();
	}

	public void ToggleShown()
	{
		Shown = !Shown;
	}

	public override void _Process(double delta)
	{
		Vector2 target = GetTargetPosition();
		float weight = Mathf.Clamp((float)(SlideSpeed * delta), 0f, 1f);
		Position = Position.Lerp(target, weight);
	}

	private Vector2 GetTargetPosition()
	{
		float hiddenOffsetX = -Size.X;
		return Shown
			? _shownPosition
			: _shownPosition + new Vector2(hiddenOffsetX, 0f);
	}
}
