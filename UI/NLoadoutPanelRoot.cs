using Godot;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using System.Collections.Generic;
using Loadout.PanelItems;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

namespace Loadout.UI;

public partial class NLoadoutPanelRoot : Control
{
	private const string OverlayLayerName = "LoadoutOverlayLayer";
	private const string RootName = "LoadoutPanelRoot";
	private const int OverlayLayer = 1000;

	private static CanvasLayer _overlayLayer;
	private static NLoadoutPanelRoot _instance;

	private readonly Dictionary<StringName, Control> _screens = new();
	private readonly Dictionary<Control, ProcessModeEnum> _screenProcessModes = new();
	private readonly Dictionary<Control, MouseFilterEnum> _screenMouseFilters = new();
	private readonly Stack<Control> _screenHistory = new();
	private Control _screenContainer;
	private Control _dropdownLayer;
	private Control _hoverTipLayer;

	public static NLoadoutPanelRoot Instance => IsValid(_instance) ? _instance : null;

	public bool HasOpenScreen => TryPeekScreen(out _);
	public Control DropdownLayer => _dropdownLayer;
	public Control HoverTipLayer => _hoverTipLayer;

	[Export]
	public NodePath ScreenStackPath = "ScreenStack";

	[Export]
	public StringName InitialScreen = "";

	public override void _Ready()
	{
		_instance = this;
		Name = RootName;
		ZIndex = 999;
		MouseFilter = MouseFilterEnum.Ignore;

		LoadoutThemeManager.ThemeChanged += OnThemeChanged;
		LoadoutThemeManager.ApplyTheme(this);
		BindScreenStack();
		BindDropdownLayer();
		BindHoverTipLayer();
		RefreshScreens();

		if (!InitialScreen.IsEmpty)
			OpenScreen(InitialScreen);
		else
			CloseAllScreens();
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is not InputEventKey keyEvent)
			return;

		if (keyEvent.Keycode != Key.Escape || !keyEvent.Pressed || keyEvent.Echo)
			return;

		if (!HasOpenScreen)
			return;

		if(CloseTopScreen())
			GetViewport().SetInputAsHandled();
	}

	public override void _Process(double delta)
	{
		if (HasOpenScreen)
			AdoptGameHoverTips();
	}

	public override void _ExitTree()
	{
		LoadoutThemeManager.ThemeChanged -= OnThemeChanged;
		EventfulCompass.ReleaseAncientPreviewCache();
		_screens.Clear();
		_screenProcessModes.Clear();
		_screenMouseFilters.Clear();
		_screenHistory.Clear();

		if (_instance == this)
			_instance = null;
	}

	private void BindScreenStack()
	{
		_screenContainer = GetNodeOrNull<Control>(ScreenStackPath);
		if (!IsInstanceValid(_screenContainer))
		{
			GD.PushWarning($"LoadoutPanelRoot: could not find ScreenStack at path '{ScreenStackPath}'.");
			return;
		}

		_screenContainer.MouseFilter = MouseFilterEnum.Ignore;
	}

	private void BindHoverTipLayer()
	{
		_hoverTipLayer = GetNodeOrNull<Control>("HoverTipLayer");
		if (IsInstanceValid(_hoverTipLayer))
		{
			_hoverTipLayer.MouseFilter = MouseFilterEnum.Ignore;
			_hoverTipLayer.ZIndex = 60;
			_hoverTipLayer.MoveToFront();
			return;
		}

		_hoverTipLayer = new Control
		{
			Name = "HoverTipLayer",
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = 60
		};
		_hoverTipLayer.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_hoverTipLayer);
	}

	private void BindDropdownLayer()
	{
		_dropdownLayer = GetNodeOrNull<Control>("DropdownLayer");
		if (IsInstanceValid(_dropdownLayer))
		{
			_dropdownLayer.MouseFilter = MouseFilterEnum.Ignore;
			_dropdownLayer.ZIndex = 50;
			_dropdownLayer.MoveToFront();
			return;
		}

		_dropdownLayer = new Control
		{
			Name = "DropdownLayer",
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = 50
		};
		_dropdownLayer.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_dropdownLayer);
	}

	public void AdoptGameHoverTips()
	{
		if (!IsInstanceValid(_hoverTipLayer) || NGame.Instance?.HoverTipsContainer is not Node gameHoverTips)
			return;

		foreach (Node child in gameHoverTips.GetChildren())
		{
			if (child is not NHoverTipSet tipSet || tipSet.GetParent() == _hoverTipLayer)
				continue;

			Vector2 globalPosition = tipSet.GlobalPosition;
			child.GetParent()?.RemoveChild(child);
			_hoverTipLayer.AddChild(child);
			tipSet.GlobalPosition = globalPosition;
			tipSet.ZIndex = 0;
		}

		_hoverTipLayer.MoveToFront();
	}

	private void RefreshScreens()
	{
		_screens.Clear();
		_screenProcessModes.Clear();
		_screenMouseFilters.Clear();
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

		UpdateModalInputState();
	}

	public bool CloseTopScreen()
	{
		if (!_screenHistory.TryPop(out var screen))
			return false;

		SetScreenActive(screen, false);
		if (_screenHistory.TryPeek(out var previousScreen))
			SetScreenActive(previousScreen, true);

		UpdateModalInputState();
		return true;
	}

	public void CloseAllScreens()
	{
		foreach (var screen in _screens.Values)
			SetScreenActive(screen, false);

		_screenHistory.Clear();
		UpdateModalInputState();
	}

	public void RegisterScreen(Control screen)
	{
		if (screen == null || !IsInstanceValid(_screenContainer))
			return;

		if (screen.GetParent() != _screenContainer)
			_screenContainer.AddChild(screen);

		ApplyFullRectLayout(screen);
		TrackScreen(screen);
		SetScreenActive(screen, false);
		UpdateModalInputState();
	}

	private void ApplyFullRectLayout(Control screen)
	{
		screen.SetAnchorsPreset(LayoutPreset.FullRect);
		screen.AnchorLeft = 0f;
		screen.AnchorTop = 0f;
		screen.AnchorRight = 1f;
		screen.AnchorBottom = 1f;
		screen.OffsetLeft = 0f;
		screen.OffsetTop = 0f;
		screen.OffsetRight = 0f;
		screen.OffsetBottom = 0f;
		screen.Size = _screenContainer.Size;
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

		if (!_screenMouseFilters.ContainsKey(screen))
			_screenMouseFilters[screen] = screen.MouseFilter;
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

			if (!IsInstanceValid(screen))
			{
				_screenHistory.Pop();
				continue;
			}

			if (!screen.Visible)
			{
				_screenHistory.Pop();
				continue;
			}

			return true;
		}

		screen = null;
		return false;
	}

	private void SetScreenActive(Control screen, bool isActive)
	{
		if (!IsInstanceValid(screen))
			return;

		if (!isActive)
			ReleaseFocusOwnedBy(screen);

		screen.Visible = isActive;

		if (!_screenProcessModes.TryGetValue(screen, out var originalMode))
		{
			originalMode = screen.ProcessMode;
			_screenProcessModes[screen] = originalMode;
		}

		if (!_screenMouseFilters.TryGetValue(screen, out var originalMouseFilter))
		{
			originalMouseFilter = screen.MouseFilter;
			_screenMouseFilters[screen] = originalMouseFilter;
		}

		screen.ProcessMode = isActive ? originalMode : ProcessModeEnum.Disabled;
		screen.MouseFilter = isActive ? MouseFilterEnum.Ignore : originalMouseFilter;
	}

	private void ReleaseFocusOwnedBy(Control screen)
	{
		Viewport viewport = GetViewport();
		Control focusOwner = viewport?.GuiGetFocusOwner();
		if (!IsInstanceValid(focusOwner))
			return;

		if (focusOwner == screen || screen.IsAncestorOf(focusOwner))
			focusOwner.ReleaseFocus();
	}

	private void UpdateModalInputState()
	{
		MouseFilter = MouseFilterEnum.Ignore;

		if (IsInstanceValid(_screenContainer))
			_screenContainer.MouseFilter = MouseFilterEnum.Ignore;
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
		GetOrAttach(tree);
	}

	public static NLoadoutPanelRoot GetOrAttach(SceneTree tree)
	{
		if (tree == null)
			return Instance;

		if (IsValid(_instance))
			return _instance;

		CanvasLayer overlayLayer = GetOrCreateOverlayLayer(tree);
		var existingRoot = overlayLayer.GetNodeOrNull<NLoadoutPanelRoot>(RootName);
		if (IsValid(existingRoot))
		{
			_instance = existingRoot;
			return existingRoot;
		}

		var modRootScene = ResourceLoader.Load<PackedScene>("res://UI/LoadoutPanelRoot.tscn");
		if (modRootScene == null)
		{
			GD.PushError("LoadoutPanelRoot: failed to load res://UI/LoadoutPanelRoot.tscn.");
			return null;
		}

		var modRoot = modRootScene.Instantiate<NLoadoutPanelRoot>();
		modRoot.Name = RootName;
		modRoot.ZIndex = 999;
		modRoot.MouseFilter = MouseFilterEnum.Ignore;
		modRoot.SetAnchorsPreset(LayoutPreset.FullRect);

		_instance = modRoot;
		GD.Print("LoadoutPanelRoot has been initialized. Attaching to overlay layer.");
		overlayLayer.AddChild(modRoot);
		return modRoot;
	}

	private static CanvasLayer GetOrCreateOverlayLayer(SceneTree tree)
	{
		if (IsValid(_overlayLayer))
		{
			_overlayLayer.Layer = OverlayLayer;
			if (_overlayLayer.GetParent() == null)
				tree.Root.AddChild(_overlayLayer);

			return _overlayLayer;
		}

		var existingLayer = tree.Root.GetNodeOrNull<CanvasLayer>(OverlayLayerName);
		if (IsValid(existingLayer))
		{
			existingLayer.Layer = OverlayLayer;
			SetOverlayLayer(existingLayer);
			return existingLayer;
		}

		var existingNode = tree.Root.GetNodeOrNull<Node>(OverlayLayerName);
		if (IsInstanceValid(existingNode))
			GD.PushWarning($"LoadoutPanelRoot: /root/{OverlayLayerName} exists but is not a CanvasLayer. Creating another overlay layer.");

		var overlayLayer = new CanvasLayer
		{
			Name = OverlayLayerName,
			Layer = OverlayLayer
		};

		SetOverlayLayer(overlayLayer);
		tree.Root.AddChild(overlayLayer);
		return overlayLayer;
	}

	private static void SetOverlayLayer(CanvasLayer overlayLayer)
	{
		if (_overlayLayer == overlayLayer)
			return;

		if (IsValid(_overlayLayer))
			_overlayLayer.TreeExiting -= OnOverlayLayerTreeExiting;

		_overlayLayer = overlayLayer;
		if (IsValid(_overlayLayer))
			_overlayLayer.TreeExiting += OnOverlayLayerTreeExiting;
	}

	private static void OnOverlayLayerTreeExiting()
	{
		if (IsValid(_overlayLayer))
			_overlayLayer.TreeExiting -= OnOverlayLayerTreeExiting;

		_overlayLayer = null;
	}

	private static bool IsValid(GodotObject instance)
	{
		return instance != null && GodotObject.IsInstanceValid(instance);
	}

	private void OnThemeChanged(string _)
	{
		// LoadoutThemeManager.ApplyTheme(this);
	}
	
	public static void CloseTopLoadoutScreen()
	{
		Instance?.CloseTopScreen();
	}

	public static void CloseBlockingRunScreens()
	{
		NOverlayStack.Instance?.Clear();
		NCapstoneContainer.Instance?.Close();
	}
}
