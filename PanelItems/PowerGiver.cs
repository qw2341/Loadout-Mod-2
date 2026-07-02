using System;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.LastActions;
using Loadout.Services.PowerGiver;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace Loadout.PanelItems;

public class PowerGiver
{
    public static void Initialize()
    {
        CreateAndAddPowerGiverItem(
            "PowerGiver.png",
            "Power Giver",
            "Potion that gives power.");
    }
    
    private static void CreateAndAddPowerGiverItem(
		string textureFileName,
		string title,
		string description)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool showPowerGiverFavoritesOnly = PowerGiverStateService.HasFavorites();
		CommonHelpers.LastActionCaptureSession? captureSession = null;

		SelectItemAdapter<PowerModel> adapter = new()
		{
			GetId = PowerId,
			GetName = CommonHelpers.FormatPowerTitle,
			GetSearchText = power => $"{power.Id} {CommonHelpers.FormatPowerTitle(power)} {power.Description}",
			CreateView = (power, _) => CreatePowerGridItem(
				power,
				PowerGiverStateService.GetCounter(PowerId(power)),
				PowerGiverStateService.IsFavorite(PowerId(power)) && !showPowerGiverFavoritesOnly),
			UpdateView = (power, view, _) => UpdatePowerGridItem(view, power, showPowerGiverFavoritesOnly),
			BindActivation = (power, view, _) => BindPowerGiverActivation(
				screen,
				power,
				view,
				entry => captureSession?.Add([entry]))
		};

		void ConfigurePowerGiverScreen(NGenericSelectScreen target, bool resetFavoriteMode = true)
		{
			PowerGiverStateService.EnsureLoaded();
			if (resetFavoriteMode)
				showPowerGiverFavoritesOnly = PowerGiverStateService.HasFavorites();

			target.Configure(ModelDb.AllPowers, adapter, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Eager);
				builder.Layout(5, new Vector2(220f, 104f), 24, 24, fixedSlots: false);
				builder.CustomVisibilityPredicate(power => !showPowerGiverFavoritesOnly || PowerGiverStateService.IsFavorite(PowerId(power)));
				builder.FilterGroup("type", LocMan.SScreenLoc("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("buff", LocMan.SScreenLoc("POWER_TYPE_BUFF", "Buff"), power => power.Type == PowerType.Buff, "type");
				builder.Filter("debuff", LocMan.SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"), power => power.Type == PowerType.Debuff, "type");
				builder.Filter("type_none", LocMan.SScreenLoc("NONE", "None"), power => power.Type == PowerType.None, "type");
				builder.FilterGroup("stack", LocMan.SScreenLoc("FILTER_GROUP_STACK", "Stack"));
				builder.Filter("stack_none", LocMan.SScreenLoc("NONE", "None"), power => power.StackType == PowerStackType.None, "stack");
				builder.Filter("counter", LocMan.SScreenLoc("POWER_STACK_COUNTER", "Counter"), power => power.StackType == PowerStackType.Counter, "stack");
				builder.Filter("single", LocMan.SScreenLoc("POWER_STACK_SINGLE", "Single"), power => power.StackType == PowerStackType.Single, "stack");
				builder.Sorter("name", LocMan.SScreenLoc("SORT_NAME", "Name"), (a, b) => string.Compare(CommonHelpers.FormatPowerTitle(a), CommonHelpers.FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", LocMan.SScreenLoc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("type", LocMan.GameLoc("gameplay_ui", "SORT_TYPE", LocMan.SScreenLoc("SORT_TYPE", "Type")), (a, b) => a.Type.CompareTo(b.Type));
			});
			AddPowerGiverSidebarDropdowns(
				target,
				() => showPowerGiverFavoritesOnly,
				value => showPowerGiverFavoritesOnly = value);
		}

		void RefreshPowerGiverScreenForOpen(NGenericSelectScreen target)
		{
			if (!target.IsConfiguredForCurrentLocale)
			{
				ConfigurePowerGiverScreen(target, resetFavoriteMode: false);
				return;
			}

			PowerGiverStateService.EnsureLoaded();
			target.SetCustomVisibilityPredicate(item =>
				item.UntypedModel is PowerModel power
				&& (!showPowerGiverFavoritesOnly || PowerGiverStateService.IsFavorite(PowerId(power))));
			target.GetNodeOrNull<NLoadoutDropdown>("Sidebar/MarginContainer/TopVBox/CustomControls/PowerGiverFavoritesDropdown")
				?.SetSelectedItem(showPowerGiverFavoritesOnly ? CommonHelpers.FavoriteModeFavoritesKey : CommonHelpers.FavoriteModeAllKey);
			target.RefreshNow(resetScroll: true);
			target.RefreshCurrentItemStates();
		}

		ConfigurePowerGiverScreen(screen);
		screen.LocaleChanged += () =>
		{
			SelectScreenUiState state = screen.CaptureUiState();
			ConfigurePowerGiverScreen(screen, resetFavoriteMode: false);
			screen.RestoreUiState(state);
		};
		screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
		screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
		screen.ScreenClosed += () =>
		{
			captureSession?.Commit();
			captureSession = null;
		};
		item.BoundScreen = screen;
		item.QuickAction = ReplayPowerGiverLastActionAsync;
		item.AfterOpen = _ => captureSession = new CommonHelpers.LastActionCaptureSession(LastActionService.PowerGiverKey);
		item.BeforeOpen = target =>
		{
			RefreshPowerGiverScreenForOpen(target);
		};
		NLoadoutPanel.ItemsContainer.AddChild(item);
	}

	private static Task ReplayPowerGiverLastActionAsync()
	{
		foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.PowerGiverKey))
		{
			if (entry.Kind != LastActionService.AdjustPowerKind || entry.Amount == 0)
				continue;

			PowerGiverTarget target = entry.Target ?? PowerGiverTarget.Player;
			if (!PowerGiverStateService.AdjustCounter(entry.ContentId, entry.Amount, target))
				GD.PushWarning($"LoadoutPanel: could not replay power action for '{entry.ContentId}'.");
		}

		return Task.CompletedTask;
	}

	private static Control CreatePowerGridItem(PowerModel model, int selectedAmount = 0, bool isFavorite = false)
	{
		Texture2D icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		Button button = CommonHelpers.CreateModelButton(new Vector2(220f, 104f));
		button.ClipContents = false;
		Panel favoriteGlow = CommonHelpers.CreateFavoriteGlow(button.CustomMinimumSize, isFavorite);
		button.AddChild(favoriteGlow);

		if (icon is not null)
		{
			TextureRect iconRect = new()
			{
				Texture = icon,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				Position = new Vector2(18f, 22f),
				Size = new Vector2(62f, 62f)
			};
			button.AddChild(iconRect);
		}

		MegaLabel nameLabel = CommonHelpers.CreateButtonLabel(
			"PowerName", CommonHelpers.FormatPowerTitle(model),
			new Vector2(82f, 8f),
			new Vector2(126f, 78f),
			18,
			HorizontalAlignment.Center,
			StsColors.cream);
		ConfigureWrappingPowerName(nameLabel);
		button.AddChild(nameLabel);

		MegaLabel amountLabel = CreatePowerAmountLabel(model, selectedAmount);
		button.AddChild(amountLabel);

		CommonHelpers.AttachHoverTips(button, model.HoverTips);
		return button;
	}

	private static MegaLabel CreatePowerAmountLabel(PowerModel model, int selectedAmount)
	{
		MegaLabel amountLabel = CommonHelpers.CreateButtonLabel(
			"PowerAmount",
			selectedAmount != 0 ? selectedAmount.ToString() : string.Empty,
			new Vector2(160f, 72f),
			new Vector2(50f, 26f),
			22,
			HorizontalAlignment.Right,
			model.AmountLabelColor);
		amountLabel.Visible = selectedAmount != 0;
		return amountLabel;
	}

	private static void UpdatePowerGridItem(Control view, PowerModel model, bool favoritesOnly)
	{
		string powerId = PowerId(model);
		int selectedAmount = PowerGiverStateService.GetCounter(powerId);
		if (view.GetNodeOrNull<MegaLabel>("PowerAmount") is { } amountLabel)
		{
			amountLabel.Text = selectedAmount != 0 ? selectedAmount.ToString() : string.Empty;
			amountLabel.Visible = selectedAmount != 0;
		}

		if (view.GetNodeOrNull<CanvasItem>("FavoriteGlow") is { } favoriteGlow)
			favoriteGlow.Visible = !favoritesOnly && PowerGiverStateService.IsFavorite(powerId);
	}

	public static void ConfigureWrappingPowerName(MegaLabel label)
	{
		label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
		label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
		label.AutoSizeEnabled = true;
		label.MinFontSize = 13;
		label.MaxFontSize = 18;
		label.AddThemeFontSizeOverride("font_size", label.MaxFontSize);
	}

	private static bool BindPowerGiverActivation(
		NGenericSelectScreen screen,
		PowerModel power,
		Control view,
		Action<LastActionEntry> recordLastAction = null)
	{
		string powerId = PowerId(power);
		view.GuiInput += input =>
		{
			if (input is not InputEventMouseButton mouseButton || mouseButton.Pressed)
				return;

			if (mouseButton.ButtonIndex != MouseButton.Left && mouseButton.ButtonIndex != MouseButton.Right)
				return;

			if (mouseButton.AltPressed || Input.IsKeyPressed(Key.Alt))
			{
				PowerGiverStateService.ToggleFavorite(powerId);
				screen.RefreshNow();
				view.AcceptEvent();
				return;
			}

			int multiplier = screen.GetCurrentActivationMultiplier();
			int delta = mouseButton.ButtonIndex == MouseButton.Right ? -multiplier : multiplier;
			PowerGiverTarget target = PowerGiverStateService.SelectedTarget;
			if (PowerGiverStateService.AdjustCounter(powerId, delta, target))
			{
				recordLastAction?.Invoke(new LastActionEntry
				{
					Kind = LastActionService.AdjustPowerKind,
					ContentId = powerId,
					Amount = delta,
					Target = target
				});
			}

			screen.RefreshCurrentItemStates();
			view.AcceptEvent();
		};

		return true;
	}

	private static void AddPowerGiverSidebarDropdowns(
		NGenericSelectScreen screen,
		Func<bool> getFavoritesOnly,
		Action<bool> setFavoritesOnly)
	{
		CommonHelpers.AddFavoritesModeDropdown(screen, "PowerGiverFavoritesDropdown", getFavoritesOnly, setFavoritesOnly);
		AddPowerGiverTargetDropdown(screen);
	}

	public static void AddPowerGiverTargetDropdown(NGenericSelectScreen screen)
	{
		NLoadoutDropdown dropdown = new()
		{
			Name = "PowerGiverTargetDropdown",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(256f, 52f)
		};
		dropdown.SetItems(LocMan.SScreenLoc("POWER_GIVER_TARGET", "Target"),
			[
				new LoadoutDropdownOption(PowerGiverTarget.Player.ToString(), LocMan.SScreenLoc("POWER_GIVER_TARGET_PLAYER", "Player")),
				new LoadoutDropdownOption(PowerGiverTarget.Monsters.ToString(), LocMan.SScreenLoc("POWER_GIVER_TARGET_MONSTERS", "Monsters"))
			],
			PowerGiverStateService.SelectedTarget.ToString());
		dropdown.SelectedItemChanged += selectedId =>
		{
			if (Enum.TryParse(selectedId, ignoreCase: true, out PowerGiverTarget target))
			{
				PowerGiverStateService.SetSelectedTarget(target);
				screen.RefreshCurrentItemStates();
			}
		};

		screen.AddCustomSidebarControl(dropdown);
	}

	private static string PowerId(PowerModel power)
	{
		return power.Id.ToString();
	}

	public static string FormatPowerCategory(PowerType type)
	{
		return type switch
		{
			PowerType.Buff => LocMan.SScreenLoc("POWER_TYPE_BUFF", "Buff"),
			PowerType.Debuff => LocMan.SScreenLoc("POWER_TYPE_DEBUFF", "Debuff"),
			_ => LocMan.SScreenLoc("NONE", "None")
		};
	}
}