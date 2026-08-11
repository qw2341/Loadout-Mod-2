using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.LastActions;
using Loadout.Services.Actions;
using Loadout.Services.PowerGiver;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;

namespace Loadout.PanelItems;

public class PowerGiver
{
	private static readonly Vector2 PowerButtonSize = new(220f, 104f);
	private static readonly Vector2 PowerIconSize = new(62f, 62f);

    public static void Initialize()
    {
        CreateAndAddPowerGiverItem(
            "PowerGiver.png",
            LocMan.Loc("POWERGIVER_TITLE", "Potion of Powers"),
            LocMan.Loc("POWERGIVER_DESC", "Right-click this relic to select the power for you/monsters at the start/middle of combat. Left-click to increase, right-click to decrease. Ctrl x5, Shift x10. Alt + left-click to favorite. Ctrl + right click to repeat the last action."));
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
		CommonHelpers.LastActionCaptureSession captureSession = null;
		Dictionary<string, string> powerTitles = new(StringComparer.Ordinal);
		Dictionary<string, PowerType> powerTypes = new(StringComparer.Ordinal);
		Dictionary<string, PowerStackType> powerStackTypes = new(StringComparer.Ordinal);

		string GetPowerTitle(PowerModel power)
		{
			string powerId = PowerId(power);
			if (!powerTitles.TryGetValue(powerId, out string title))
			{
				title = CommonHelpers.FormatPowerTitle(power);
				powerTitles[powerId] = title;
			}

			return title;
		}

		PowerType GetPowerType(PowerModel power)
		{
			string powerId = PowerId(power);
			if (!powerTypes.TryGetValue(powerId, out PowerType type))
			{
				type = GetSafePowerType(power);
				powerTypes[powerId] = type;
			}

			return type;
		}

		PowerStackType GetPowerStackType(PowerModel power)
		{
			string powerId = PowerId(power);
			if (!powerStackTypes.TryGetValue(powerId, out PowerStackType stackType))
			{
				stackType = GetSafePowerStackType(power);
				powerStackTypes[powerId] = stackType;
			}

			return stackType;
		}

		SelectItemAdapter<PowerModel> adapter = new()
		{
			GetId = PowerId,
			GetName = GetPowerTitle,
			GetSearchTextFromName = CreateSafePowerSearchText,
			CapturePreloadResourcePaths = power => [power.IconPath],
			CreateView = (power, state) => CreatePowerGridItem(
				power,
				screen.IsReusedSelectionActive
					? state.SelectionAmount
					: PowerGiverStateService.GetCounter(PowerId(power)),
				PowerGiverStateService.IsFavorite(PowerId(power)) && !showPowerGiverFavoritesOnly,
				GetPowerTitle(power)),
			UpdateView = (power, view, state) => UpdatePowerGridItem(
				view,
				power,
				showPowerGiverFavoritesOnly,
				screen.IsReusedSelectionActive ? state.SelectionAmount : int.MinValue),
			BindActivationWithCleanup = (power, view, _) => BindPowerGiverActivationWithCleanup(
				screen,
				power,
				view,
				() => showPowerGiverFavoritesOnly,
				entry => captureSession?.Add([entry]))
		};

		void ConfigurePowerGiverScreen(NGenericSelectScreen target, bool resetFavoriteMode = true)
		{
			PowerGiverStateService.EnsureLoaded();
			if (resetFavoriteMode)
				showPowerGiverFavoritesOnly = PowerGiverStateService.HasFavorites();

			powerTitles.Clear();
			powerTypes.Clear();
			powerStackTypes.Clear();
			IReadOnlyList<PowerModel> allPowers = ModelDb.AllPowers.ToList();
			target.Configure(allPowers, adapter, builder =>
			{
				builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
				builder.Materialization(SelectMaterializationMode.Lazy);
				builder.Layout(5, PowerButtonSize, 24, 24, fixedSlots: false);
				builder.ActionButton("clear_current_buffs", LocMan.Loc("POWER_GIVER_CLEAR_CURRENT_BUFFS", "Clear Current Buffs"), _ => HandleClearCurrentPowers(PowerType.Buff));
				builder.ActionButton("clear_current_debuffs", LocMan.Loc("POWER_GIVER_CLEAR_CURRENT_DEBUFFS", "Clear Current Debuffs"), _ => HandleClearCurrentPowers(PowerType.Debuff));
				builder.CustomVisibilityPredicate(power => !showPowerGiverFavoritesOnly || PowerGiverStateService.IsFavorite(PowerId(power)));
				builder.FilterGroup("type", LocMan.Loc("FILTER_GROUP_TYPE", "Type"));
				builder.Filter("buff", LocMan.Loc("POWER_TYPE_BUFF", "Buff"), power => GetPowerType(power) == PowerType.Buff, "type");
				builder.Filter("debuff", LocMan.Loc("POWER_TYPE_DEBUFF", "Debuff"), power => GetPowerType(power) == PowerType.Debuff, "type");
				builder.Filter("type_none", LocMan.Loc("NONE", "None"), power => GetPowerType(power) == PowerType.None, "type");
				builder.FilterGroup("stack", LocMan.Loc("FILTER_GROUP_STACK", "Stack"));
				builder.Filter("stack_none", LocMan.Loc("NONE", "None"), power => GetPowerStackType(power) == PowerStackType.None, "stack");
				builder.Filter("counter", LocMan.Loc("POWER_STACK_COUNTER", "Counter"), power => GetPowerStackType(power) == PowerStackType.Counter, "stack");
				builder.Filter("single", LocMan.Loc("POWER_STACK_SINGLE", "Single"), power => GetPowerStackType(power) == PowerStackType.Single, "stack");
				CommonHelpers.AddModFilters(builder, allPowers);
				builder.KeySorter("name", LocMan.Loc("SORT_NAME", "Name"), GetPowerTitle, activeByDefault: true, comparer: StringComparer.Ordinal);
				builder.KeySorter("id", LocMan.Loc("SORT_ID", "ID"), model => model.Id.Entry, comparer: StringComparer.Ordinal);
				builder.Sorter("type", LocMan.GameLoc("gameplay_ui", "SORT_TYPE", LocMan.Loc("SORT_TYPE", "Type")), (a, b) => GetPowerType(a).CompareTo(GetPowerType(b)));
			});
			target.SetHiddenPrewarmAllItems(true);
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
			AddPowerGiverTargetDropdown(target);
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
		LoadoutTargetSelection fallbackTarget = LoadoutTargetService.GetSelected(PowerGiverStateService.TargetKey, LoadoutTargetMode.PowerGiver);
		foreach (LastActionEntry entry in LastActionService.GetAction(LastActionService.PowerGiverKey))
		{
			if (entry.Kind != LastActionService.AdjustPowerKind || entry.Amount == 0)
				continue;

			PowerModel power = ResolveCanonicalPower(entry.ContentId);
			if (power is null)
			{
				GD.PushWarning($"LoadoutPanel: could not replay power action for unknown power '{entry.ContentId}'.");
				continue;
			}

			if (!LoadoutImmediateMutationService.RequestAdjustPower(
				    power.Id,
				    entry.Amount,
				    entry.GetTargetSelection(fallbackTarget)))
			{
				GD.PushWarning($"LoadoutPanel: could not replay power action for '{entry.ContentId}'.");
			}
		}

		return Task.CompletedTask;
	}

	private static void HandleClearCurrentPowers(PowerType type)
	{
		LoadoutTargetSelection target = LoadoutTargetService.GetSelected(PowerGiverStateService.TargetKey, LoadoutTargetMode.PowerGiver);
		LoadoutImmediateMutationService.RequestClearCurrentPowers(type, target);
	}

	internal static Control CreatePowerGridItem(
		PowerModel model,
		int selectedAmount = 0,
		bool isFavorite = false,
		string displayName = null)
	{
		Texture2D icon = GetLivePowerIcon(model);

		Button button = CommonHelpers.CreateModelButton(PowerButtonSize);
		button.ClipContents = false;
		Panel favoriteGlow = CommonHelpers.CreateFavoriteGlow(button.CustomMinimumSize, isFavorite);
		button.AddChild(favoriteGlow);

		if (icon is not null)
		{
			TextureRect iconRect = new()
			{
				Texture = icon,
				CustomMinimumSize = PowerIconSize,
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				Position = new Vector2(18f, 22f),
				Size = PowerIconSize
			};
			button.AddChild(iconRect);
		}

		MegaLabel nameLabel = CommonHelpers.CreateButtonLabel(
			"PowerName", displayName ?? CommonHelpers.FormatPowerTitle(model),
			new Vector2(82f, 8f),
			new Vector2(126f, 78f),
			18,
			HorizontalAlignment.Center,
			StsColors.cream);
		ConfigureWrappingPowerName(nameLabel);
		button.AddChild(nameLabel);

		MegaLabel amountLabel = CreatePowerAmountLabel(model, selectedAmount);
		button.AddChild(amountLabel);

		CommonHelpers.AttachHoverTips(
			button,
			() => CreateSafePowerHoverTips(model, GetLivePowerIcon(model)).ToList(),
			cacheResult: false);
		return button;
	}

	private static Texture2D GetLivePowerIcon(PowerModel model)
	{
		try
		{
			Texture2D icon = model.Icon;
			return GodotObject.IsInstanceValid(icon) ? icon : null;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
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
			GetSafePowerAmountLabelColor(model));
		amountLabel.Visible = selectedAmount != 0;
		return amountLabel;
	}

	private static Color GetSafePowerAmountLabelColor(PowerModel model)
	{
		try
		{
			return model.AmountLabelColor;
		}
		catch
		{
			return GetSafePowerType(model) == PowerType.Debuff ? StsColors.red : StsColors.cream;
		}
	}

	internal static PowerType GetSafePowerType(PowerModel model)
	{
		try
		{
			return model.Type;
		}
		catch
		{
			return PowerType.None;
		}
	}

	internal static PowerStackType GetSafePowerStackType(PowerModel model)
	{
		try
		{
			return model.StackType;
		}
		catch
		{
			return PowerStackType.None;
		}
	}

	internal static string CreateSafePowerSearchText(PowerModel model, string displayName)
	{
		string description = GetSafeRawPowerDescription(model);
		return string.IsNullOrWhiteSpace(description)
			? $"{model.Id} {displayName}"
			: $"{model.Id} {displayName} {description}";
	}

	private static IEnumerable<IHoverTip> CreateSafePowerHoverTips(PowerModel model, Texture2D icon)
	{
		try
		{
			if (!model.Title.Exists())
				return [];

			string description = GetSafeRawPowerDescription(model);
			HoverTip hoverTip = new(
				model.Title,
				string.IsNullOrWhiteSpace(description) ? PowerId(model) : description,
				icon)
			{
				Id = PowerId(model),
				IsDebuff = GetSafePowerType(model) == PowerType.Debuff
			};
			return [hoverTip];
		}
		catch
		{
			return [];
		}
	}

	private static string GetSafeRawPowerDescription(PowerModel model)
	{
		try
		{
			return model.Description.Exists()
				? model.Description.GetRawText()
				: string.Empty;
		}
		catch
		{
			return string.Empty;
		}
	}

	private static void UpdatePowerGridItem(
		Control view,
		PowerModel model,
		bool favoritesOnly,
		int selectionAmount = int.MinValue)
	{
		string powerId = PowerId(model);
		int selectedAmount = selectionAmount == int.MinValue
			? PowerGiverStateService.GetCounter(powerId)
			: selectionAmount;
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

	private static Action? BindPowerGiverActivationWithCleanup(
		NGenericSelectScreen screen,
		PowerModel power,
		Control view,
		Func<bool> getFavoritesOnly,
		Action<LastActionEntry> recordLastAction = null)
	{
		if (view is null || !GodotObject.IsInstanceValid(view))
			return null;

		string powerId = PowerId(power);
		void OnGuiInput(InputEvent input)
		{
			if (input is not InputEventMouseButton mouseButton || mouseButton.Pressed)
				return;

			if (mouseButton.ButtonIndex != MouseButton.Left && mouseButton.ButtonIndex != MouseButton.Right)
				return;

			if (mouseButton.AltPressed || Input.IsKeyPressed(Key.Alt))
			{
				PowerGiverStateService.ToggleFavorite(powerId);
				if (getFavoritesOnly())
					screen.RefreshLayout(resetScroll: false, updateExistingViews: false);
				else
					screen.RefreshItemView(powerId);
				view.AcceptEvent();
				return;
			}

			int multiplier = screen.GetCurrentActivationMultiplier();
			int delta = mouseButton.ButtonIndex == MouseButton.Right ? -multiplier : multiplier;
			if (screen.IsReusedSelectionActive)
			{
				int amount = screen.SelectedAmounts.GetValueOrDefault(powerId) + delta;
				screen.SelectItem(powerId, amount);
				screen.RefreshItemView(powerId);
				view.AcceptEvent();
				return;
			}
			LoadoutTargetSelection target = LoadoutTargetService.GetSelected(PowerGiverStateService.TargetKey, LoadoutTargetMode.PowerGiver);
			if (LoadoutImmediateMutationService.RequestAdjustPower(power.Id, delta, target))
			{
				LastActionEntry entry = new()
				{
					Kind = LastActionService.AdjustPowerKind,
					ContentId = powerId,
					Amount = delta
				};
				entry.SetTargetSelection(target);
				recordLastAction?.Invoke(entry);
			}

			screen.RefreshItemView(powerId);
			view.AcceptEvent();
		}

		view.GuiInput += OnGuiInput;
		return () =>
		{
			if (GodotObject.IsInstanceValid(view))
				view.GuiInput -= OnGuiInput;
		};
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
		LoadoutTargetService.UpsertTargetDropdown(
			screen,
			"PowerGiverTargetDropdown",
			PowerGiverStateService.TargetKey,
			LoadoutTargetMode.PowerGiver,
			screen.RefreshCurrentItemStates);
	}

	private static string PowerId(PowerModel power)
	{
		return power.Id.ToString();
	}

	private static PowerModel ResolveCanonicalPower(string powerId)
	{
		return ModelDb.AllPowers.FirstOrDefault(power =>
			string.Equals(power.Id.ToString(), powerId, StringComparison.Ordinal)
			|| string.Equals(power.Id.Entry, powerId, StringComparison.OrdinalIgnoreCase));
	}

	public static string FormatPowerCategory(PowerType type)
	{
		return type switch
		{
			PowerType.Buff => LocMan.Loc("POWER_TYPE_BUFF", "Buff"),
			PowerType.Debuff => LocMan.Loc("POWER_TYPE_DEBUFF", "Debuff"),
			_ => LocMan.Loc("NONE", "None")
		};
	}
}
