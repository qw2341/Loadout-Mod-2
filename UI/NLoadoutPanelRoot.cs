using Godot;
using System;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using Loadout.UI.CreatureManipulation;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Loadout.UI;

public partial class NLoadoutPanelRoot : Control
{
	private const string OverlayLayerName = "LoadoutOverlayLayer";
	private const string RootName = "LoadoutPanelRoot";
	private const int OverlayLayer = 1000;
	private const int DropdownLayerZIndex = 1000;
	private const int HoverTipLayerZIndex = 1010;
	private const int NativeFeedbackLayerZIndex = 1020;
	private const int ModalLayerZIndex = 1030;
	private const int DragLayerZIndex = 1040;

	private static CanvasLayer _overlayLayer;
	private static NLoadoutPanelRoot _instance;

	private readonly Dictionary<StringName, Control> _screens = new();
	private readonly Dictionary<Control, ProcessModeEnum> _screenProcessModes = new();
	private readonly Dictionary<Control, MouseFilterEnum> _screenMouseFilters = new();
	private readonly Stack<Control> _screenHistory = new();
	private Control _screenContainer;
	private Control _dropdownLayer;
	private Control _hoverTipLayer;
	private NLoadoutNativeFeedback _nativeFeedbackLayer;
	private Control _modalLayer;
	private Control _dragLayer;
	private Control _dragVisual;
	private NCreatureManipulationPanel _creatureManipulationPanel;
	private bool _lastHasOpenScreen;

	public static NLoadoutPanelRoot Instance => IsValid(_instance) ? _instance : null;
	public static event System.Action<bool> OpenScreenStateChanged;

	public bool HasOpenScreen => TryPeekScreen(out _);
	public bool HasActiveNativeCardFeedback =>
		IsValid(_nativeFeedbackLayer) && _nativeFeedbackLayer.HasActiveCardFeedback;
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
		BindNativeFeedbackLayer();
		BindModalLayer();
		BindDragLayer();
		RefreshScreens();
		GetCreatureManipulationPanel();
		SetProcess(false);

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
		AdoptGameHoverTips();
	}

	public override void _ExitTree()
	{
		ClearNativeFeedback();
		ClearDragVisual();
		LoadoutThemeManager.ThemeChanged -= OnThemeChanged;
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
			_hoverTipLayer.ZIndex = HoverTipLayerZIndex;
			_hoverTipLayer.MoveToFront();
			return;
		}

		_hoverTipLayer = new Control
		{
			Name = "HoverTipLayer",
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = HoverTipLayerZIndex
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
			_dropdownLayer.ZIndex = DropdownLayerZIndex;
			_dropdownLayer.MoveToFront();
			return;
		}

		_dropdownLayer = new Control
		{
			Name = "DropdownLayer",
			MouseFilter = MouseFilterEnum.Ignore,
			ZIndex = DropdownLayerZIndex
		};
		_dropdownLayer.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_dropdownLayer);
	}

	private void BindNativeFeedbackLayer()
	{
		_nativeFeedbackLayer = GetNodeOrNull<NLoadoutNativeFeedback>("NativeFeedbackLayer");
		if (!IsValid(_nativeFeedbackLayer))
		{
			_nativeFeedbackLayer = new NLoadoutNativeFeedback
			{
				Name = "NativeFeedbackLayer",
				MouseFilter = MouseFilterEnum.Ignore
			};
			_nativeFeedbackLayer.SetAnchorsPreset(LayoutPreset.FullRect);
			AddChild(_nativeFeedbackLayer);
		}

		_nativeFeedbackLayer.ZIndex = NativeFeedbackLayerZIndex;
		_nativeFeedbackLayer.MoveToFront();
	}

	private void BindModalLayer()
	{
		_modalLayer = GetNodeOrNull<Control>("ModalLayer");
		if (!IsInstanceValid(_modalLayer))
		{
			_modalLayer = new Control
			{
				Name = "ModalLayer",
				MouseFilter = MouseFilterEnum.Ignore
			};
			_modalLayer.SetAnchorsPreset(LayoutPreset.FullRect);
			AddChild(_modalLayer);
		}

		_modalLayer.ZIndex = ModalLayerZIndex;
		_modalLayer.MoveToFront();
	}

	private void BindDragLayer()
	{
		_dragLayer = GetNodeOrNull<Control>("DragLayer");
		if (!IsInstanceValid(_dragLayer))
		{
			_dragLayer = new Control
			{
				Name = "DragLayer",
				MouseFilter = MouseFilterEnum.Ignore
			};
			_dragLayer.SetAnchorsPreset(LayoutPreset.FullRect);
			AddChild(_dragLayer);
		}

		_dragLayer.ZIndex = DragLayerZIndex;
		_dragLayer.MoveToFront();
	}

	public void HostDragVisual(Control visual)
	{
		if (!IsInstanceValid(visual) || !IsInstanceValid(_dragLayer))
			return;
		ClearDragVisual();
		_dragVisual = visual;
		_dragLayer.AddChild(visual);
		_dragLayer.MoveToFront();
	}

	public void ClearDragVisual()
	{
		if (!IsInstanceValid(_dragVisual))
		{
			_dragVisual = null;
			return;
		}
		_dragVisual.GetParent()?.RemoveChild(_dragVisual);
		_dragVisual.QueueFree();
		_dragVisual = null;
	}

	public IDisposable HostNativeModal(NModalContainer modal)
	{
		if (!IsInstanceValid(modal)
		    || !IsInstanceValid(_modalLayer)
		    || modal.GetParent() is not Node originalParent)
		{
			return EmptyDisposable.Instance;
		}

		NativeModalLease lease = new(modal, originalParent, modal.GetIndex());
		originalParent.RemoveChild(modal);
		_modalLayer.AddChild(modal);
		ApplyFullRectLayout(modal);
		modal.ZIndex = 0;
		modal.ZAsRelative = true;
		_modalLayer.MoveToFront();
		return lease;
	}

	public void AdoptGameHoverTips()
	{
		if (!IsInstanceValid(_hoverTipLayer) || NGame.Instance?.HoverTipsContainer is not Node gameHoverTips)
			return;

		foreach (Node child in gameHoverTips.GetChildren())
		{
			if (child is NHoverTipSet tipSet)
				AdoptGameHoverTip(tipSet);
		}

		_hoverTipLayer.MoveToFront();
	}

	public void AdoptGameHoverTip(NHoverTipSet tipSet)
	{
		if (!IsInstanceValid(_hoverTipLayer)
		    || !IsInstanceValid(tipSet)
		    || tipSet.GetParent() == _hoverTipLayer)
		{
			return;
		}

		Vector2 globalPosition = tipSet.GlobalPosition;
		tipSet.GetParent()?.RemoveChild(tipSet);
		_hoverTipLayer.AddChild(tipSet);
		tipSet.GlobalPosition = globalPosition;
		tipSet.ZIndex = 0;
		_hoverTipLayer.MoveToFront();
	}

	public NCreatureManipulationPanel GetCreatureManipulationPanel()
	{
		if (IsValid(_creatureManipulationPanel))
			return _creatureManipulationPanel;

		_creatureManipulationPanel = GetNodeOrNull<NCreatureManipulationPanel>("CreatureManipulationPanel");
		if (IsValid(_creatureManipulationPanel))
			return _creatureManipulationPanel;

		_creatureManipulationPanel = new NCreatureManipulationPanel
		{
			Name = "CreatureManipulationPanel",
			Visible = false
		};
		AddChild(_creatureManipulationPanel);
		_creatureManipulationPanel.MoveToFront();
		return _creatureManipulationPanel;
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

		if (wasTop && TryPeekHistoryScreen(out var previousScreen))
			SetScreenActive(previousScreen, true);

		UpdateModalInputState();
	}

	public void RemoveScreen(Control screen)
	{
		if (!IsInstanceValid(screen))
			return;

		bool wasTop = TryPeekScreen(out var activeScreen) && activeScreen == screen;
		RemoveFromHistory(screen);
		SetScreenActive(screen, false);

		if (_screens.TryGetValue(screen.Name, out var trackedScreen) && trackedScreen == screen)
			_screens.Remove(screen.Name);
		_screenProcessModes.Remove(screen);
		_screenMouseFilters.Remove(screen);

		if (screen.GetParent() == _screenContainer)
			_screenContainer.RemoveChild(screen);

		if (wasTop && TryPeekHistoryScreen(out var previousScreen))
			SetScreenActive(previousScreen, true);

		UpdateModalInputState();
	}

	public bool CloseTopScreen()
	{
		if (_screenHistory.TryPeek(out var currentScreen)
		    && currentScreen is NGenericSelectScreen selectScreen
		    && !selectScreen.CanCloseFromBackNavigation)
			return false;
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

	public bool TryPreviewCardPileAdd(
		IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardPileAddResult> results,
		float lingerTime,
		MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle style)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;

		try
		{
			_nativeFeedbackLayer.PreviewCardPileAdd(results, lingerTime, style);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: native card-add feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewCardRemoval(IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;

		try
		{
			_nativeFeedbackLayer.PreviewCardRemoval(cards);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: native card-removal feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewCustomRunCardAdd(
		IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards,
		Control destination)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;
		try
		{
			_nativeFeedbackLayer.PreviewCustomRunCardAdd(cards, destination);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: Custom Run card-add feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewCustomRunCardRemoval(IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel> cards)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;
		try
		{
			_nativeFeedbackLayer.PreviewCustomRunCardRemoval(cards);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: Custom Run card-removal feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewCustomRunRelicAdd(
		MegaCrit.Sts2.Core.Models.RelicModel relic,
		int amount,
		Control source,
		Control destination)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;
		try
		{
			_nativeFeedbackLayer.PreviewCustomRunRelicAdd(relic, amount, source, destination);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: Custom Run relic-add feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewCustomRunPotionAdd(
		MegaCrit.Sts2.Core.Models.PotionModel potion,
		int amount,
		Control source,
		Control destination)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;
		try
		{
			_nativeFeedbackLayer.PreviewCustomRunPotionAdd(potion, amount, source, destination);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: Custom Run potion-add feedback failed. {exception.Message}");
			return false;
		}
	}

	public bool TryPreviewRelicObtained(MegaCrit.Sts2.Core.Models.RelicModel relic)
	{
		if (!IsValid(_nativeFeedbackLayer))
			return false;

		try
		{
			_nativeFeedbackLayer.PreviewRelicObtained(relic);
			return true;
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: native relic feedback failed for '{relic.Id}'. {exception.Message}");
			return false;
		}
	}

	public long QueueRelicObtainSource(
		MegaCrit.Sts2.Core.Models.ModelId relicId,
		Control sourceIcon,
		int amount)
	{
		if (!IsValid(_nativeFeedbackLayer)
		    || sourceIcon == null
		    || !IsInstanceValid(sourceIcon))
		{
			return 0;
		}

		try
		{
			return _nativeFeedbackLayer.QueueRelicObtainSource(relicId, sourceIcon, amount);
		}
		catch (System.Exception exception)
		{
			GD.PushWarning($"LoadoutPanelRoot: could not capture relic feedback source for '{relicId}'. {exception.Message}");
			return 0;
		}
	}

	public void CancelRelicObtainSource(long token)
	{
		if (IsValid(_nativeFeedbackLayer))
			_nativeFeedbackLayer.CancelRelicObtainSource(token);
	}

	public void ClearNativeFeedback()
	{
		if (IsValid(_nativeFeedbackLayer))
			_nativeFeedbackLayer.Clear();
	}

	public void RegisterScreen(Control screen)
	{
		if (screen == null || !IsInstanceValid(_screenContainer))
			return;

		bool newlyAttached = screen.GetParent() != _screenContainer;
		TrackScreen(screen);

		if (newlyAttached)
		{
			// Keep the screen completely dormant before AddChild triggers _Ready().
			screen.Visible = false;
			screen.ProcessMode = ProcessModeEnum.Disabled;
			screen.MouseFilter = MouseFilterEnum.Ignore;
			ApplyFullRectLayout(screen);
			_screenContainer.AddChild(screen);
		}

		ApplyFullRectLayout(screen);
		if (newlyAttached)
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
		UpdateModalInputState();
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

	private bool TryPeekHistoryScreen(out Control screen)
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

		if (isActive)
		{
			screen.ProcessMode = originalMode;
			screen.MouseFilter = originalMouseFilter;
			screen.Visible = true;
			if (screen is NGenericSelectScreen selectScreen)
				selectScreen.SetScreenLifecycleActive(true);
			return;
		}

		if (screen is NGenericSelectScreen dormantSelectScreen)
			dormantSelectScreen.SetScreenLifecycleActive(false);
		ReleaseFocusOwnedBy(screen);
		screen.Visible = false;
		screen.MouseFilter = MouseFilterEnum.Ignore;
		screen.ProcessMode = ProcessModeEnum.Disabled;
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

		bool hasOpenScreen = HasOpenScreen;
		SetProcess(hasOpenScreen);
		if (_lastHasOpenScreen == hasOpenScreen)
			return;

		_lastHasOpenScreen = hasOpenScreen;
		OpenScreenStateChanged?.Invoke(hasOpenScreen);
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
				tree.Root.AddChildSafely(_overlayLayer);

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
		tree.Root.AddChildSafely(overlayLayer);
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

	private sealed class NativeModalLease : IDisposable
	{
		private readonly NModalContainer _modal;
		private readonly Node _parent;
		private readonly int _index;
		private readonly int _zIndex;
		private readonly bool _zAsRelative;
		private readonly float _anchorLeft;
		private readonly float _anchorTop;
		private readonly float _anchorRight;
		private readonly float _anchorBottom;
		private readonly float _offsetLeft;
		private readonly float _offsetTop;
		private readonly float _offsetRight;
		private readonly float _offsetBottom;
		private bool _disposed;

		public NativeModalLease(NModalContainer modal, Node parent, int index)
		{
			_modal = modal;
			_parent = parent;
			_index = index;
			_zIndex = modal.ZIndex;
			_zAsRelative = modal.ZAsRelative;
			_anchorLeft = modal.AnchorLeft;
			_anchorTop = modal.AnchorTop;
			_anchorRight = modal.AnchorRight;
			_anchorBottom = modal.AnchorBottom;
			_offsetLeft = modal.OffsetLeft;
			_offsetTop = modal.OffsetTop;
			_offsetRight = modal.OffsetRight;
			_offsetBottom = modal.OffsetBottom;
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			if (!GodotObject.IsInstanceValid(_modal) || !GodotObject.IsInstanceValid(_parent))
				return;

			_modal.GetParent()?.RemoveChild(_modal);
			_parent.AddChild(_modal);
			_parent.MoveChild(_modal, Math.Clamp(_index, 0, _parent.GetChildCount() - 1));
			_modal.AnchorLeft = _anchorLeft;
			_modal.AnchorTop = _anchorTop;
			_modal.AnchorRight = _anchorRight;
			_modal.AnchorBottom = _anchorBottom;
			_modal.OffsetLeft = _offsetLeft;
			_modal.OffsetTop = _offsetTop;
			_modal.OffsetRight = _offsetRight;
			_modal.OffsetBottom = _offsetBottom;
			_modal.ZIndex = _zIndex;
			_modal.ZAsRelative = _zAsRelative;
		}
	}

	private sealed class EmptyDisposable : IDisposable
	{
		public static readonly EmptyDisposable Instance = new();
		public void Dispose()
		{
		}
	}
	
	public static void CloseTopLoadoutScreen()
	{
		Instance?.CloseTopScreen();
	}

	public static void CloseBlockingRunScreens()
	{
		NOverlayStack.Instance?.Clear();
		NCapstoneContainer.Instance?.Close();
		NMapScreen.Instance?.Close(animateOut: false);
	}
}
