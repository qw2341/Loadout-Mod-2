using Godot;
using Loadout.UI.Managers;
using System.Collections.Generic;
namespace Loadout.UI;
public partial class NLoadoutPanelRoot : Control
{
	private static Control _overlayInstance;
	private readonly Dictionary<StringName, Control> _screens = new();
	private readonly Dictionary<Control, ProcessModeEnum> _screenProcessModes = new();
	private readonly Stack<Control> _screenHistory = new();
	private Control _screenContainer;

	[Export]
	public NodePath ScreenStackPath = "ScreenStack";

	[Export]
	public StringName InitialScreen = "";
	
	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		LoadoutThemeManager.ThemeChanged += OnThemeChanged;
		LoadoutThemeManager.ApplyTheme(this);
		BindScreenStack();
		RefreshScreens();

		if (!InitialScreen.IsEmpty)
			OpenScreen(InitialScreen);
		else
			CloseAllScreens();
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey keyEvent)
			return;

		if (keyEvent.Keycode != Key.Escape || !keyEvent.Pressed || keyEvent.Echo)
			return;

		if (!TryPeekScreen(out _))
			return;

		CloseTopScreen();
		GetViewport().SetInputAsHandled();
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

	private void BindScreenStack()
	{
		_screenContainer = GetNodeOrNull<Control>(ScreenStackPath);
		if (!IsInstanceValid(_screenContainer))
			GD.PushWarning($"LoadoutPanelRoot: could not find ScreenStack at path '{ScreenStackPath}'.");
	}

	private void RefreshScreens()
	{
		_screens.Clear();
		_screenProcessModes.Clear();
		_screenHistory.Clear();
		if (!IsInstanceValid(_screenContainer))
			return;

		foreach (Node child in _screenContainer.GetChildren())
		{
			if (child is not Control screen)
				continue;

			TrackScreen(screen);
			SetScreenActive(screen, false);
		}
	}

	public bool OpenScreen(StringName screenName)
	{
		if (!TryGetScreen(screenName, out var nextScreen))
			return false;

		PushScreen(nextScreen);
		return true;
	}

	public void OpenScreen(Control screen)
	{
		if (screen == null)
			return;

		RegisterScreen(screen);
		PushScreen(screen);
	}

	public void CloseScreen(StringName screenName)
	{
		if (!TryGetScreen(screenName, out var screen))
			return;

		bool wasTop = TryPeekScreen(out var activeScreen) && activeScreen == screen;
		RemoveFromHistory(screen);
		SetScreenActive(screen, false);

		if (wasTop && TryPeekScreen(out var previousScreen))
			SetScreenActive(previousScreen, true);
	}

	public bool CloseTopScreen()
	{
		if (!_screenHistory.TryPop(out var screen))
			return false;

		SetScreenActive(screen, false);
		if (_screenHistory.TryPeek(out var previousScreen))
			SetScreenActive(previousScreen, true);

		return true;
	}

	public void CloseAllScreens()
	{
		foreach (var screen in _screens.Values)
			SetScreenActive(screen, false);

		_screenHistory.Clear();
	}

	public void RegisterScreen(Control screen)
	{
		if (screen == null || !IsInstanceValid(_screenContainer))
			return;

		if (screen.GetParent() != _screenContainer)
			_screenContainer.AddChild(screen);

		screen.SetAnchorsPreset(LayoutPreset.FullRect);
		TrackScreen(screen);
		SetScreenActive(screen, false);
	}

	public StringName GetActiveScreenName()
	{
		return TryPeekScreen(out var activeScreen) ? activeScreen.Name : "";
	}

	private bool TryGetScreen(StringName screenName, out Control screen)
	{
		if (_screens.TryGetValue(screenName, out screen))
			return true;

		if (!IsInstanceValid(_screenContainer))
			return false;

		screen = _screenContainer.GetNodeOrNull<Control>(new NodePath(screenName.ToString()));
		if (!IsInstanceValid(screen))
		{
			GD.PushWarning($"LoadoutPanelRoot: screen '{screenName}' was not found under ScreenStack.");
			return false;
		}

		TrackScreen(screen);
		return true;
	}

	private void TrackScreen(Control screen)
	{
		_screens[screen.Name] = screen;
		if (!_screenProcessModes.ContainsKey(screen))
			_screenProcessModes[screen] = screen.ProcessMode;
	}

	private void PushScreen(Control screen)
	{
		if (TryPeekScreen(out var activeScreen))
			SetScreenActive(activeScreen, false);

		RemoveFromHistory(screen);
		_screenHistory.Push(screen);
		SetScreenActive(screen, true);
	}

	private bool TryPeekScreen(out Control screen)
	{
		while (_screenHistory.Count > 0)
		{
			screen = _screenHistory.Peek();
			if (IsInstanceValid(screen))
				return true;

			_screenHistory.Pop();
		}

		screen = null;
		return false;
	}

	private void SetScreenActive(Control screen, bool isActive)
	{
		if (!IsInstanceValid(screen))
			return;

		screen.Visible = isActive;

		if (!_screenProcessModes.TryGetValue(screen, out var originalMode))
		{
			originalMode = screen.ProcessMode;
			_screenProcessModes[screen] = originalMode;
		}

		screen.ProcessMode = isActive ? originalMode : ProcessModeEnum.Disabled;
	}

	private bool RemoveFromHistory(Control screenToRemove)
	{
		if (_screenHistory.Count == 0)
			return false;

		var buffer = new Stack<Control>();
		bool removed = false;

		while (_screenHistory.Count > 0)
		{
			var current = _screenHistory.Pop();
			if (!removed && current == screenToRemove)
			{
				removed = true;
				continue;
			}

			buffer.Push(current);
		}

		while (buffer.Count > 0)
			_screenHistory.Push(buffer.Pop());

		return removed;
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
