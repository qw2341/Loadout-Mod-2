#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Runs;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	private const int MaxLoadoutItemInitAttempts = 120;

	[Export]
	public bool Shown = true;

	[Export]
	public float SlideSpeed = 12f;
	
	private PanelContainer _panelContainer = null!;
	private MarginContainer _marginContainer = null!;
	private Control _itemsContainer = null!;
	private int _loadoutItemInitAttempts;
	private bool _loadoutItemsAdded;
	private bool _loadoutItemRetryScheduled;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_panelContainer = GetNode<PanelContainer>("PanelContainer");
		_marginContainer = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		_itemsContainer = GetNode<Control>("PanelContainer/MarginContainer/VBoxContainer");
		
		
		
		TryAddLoadoutItems();

		// Recompute whenever the VBox minimum size changes
		// _marginContainer.MinimumSizeChanged += UpdatePanelHeight;
		// _itemsContainer.MinimumSizeChanged += UpdatePanelHeight;
	}

	public void ToggleShown()
	{
		Shown = !Shown;
	}

	public override void _Process(double delta)
	{
		UpdatePosition(delta);
	}
	
	private void UpdatePosition(double delta)
	{
		Vector2 target = Position;
		target.Y = (GetParent<Control>().Size.Y - Size.Y) / 2f;
		target.X = Shown ? 0 : -Size.X;
		
		float weight = Mathf.Clamp((float)(SlideSpeed * delta), 0f, 1f);
		Position = Position.Lerp(target, weight);
	}

	private void AddLoadoutItems()
	{
		CardPoolModel? currentCardPool = GetCurrentCharacterCardPool();
		RelicGroupingData relicGroupingData = BuildRelicGroupingData();

		CreateAndAddLoadoutItem(
			ModelDb.AllCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = card => card.Id.ToString(),
				GetName = card => FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = (card, _) => CreateCardGridItem(card),
				ViewReady = (_, view) => RefreshCardVisuals(view),
				BindActivation = (_, view, activate) => BindCardActivation(view, activate)
			}, builder =>
			{
				builder.Layout(5, NCard.defaultSize * NCardHolder.smallScale, 40, 40);
				builder.FilterGroup("class","Class");
				builder.Filter("ironclad","Ironclad", card => card.Pool is IroncladCardPool, "class", currentCardPool is IroncladCardPool);
				builder.Filter("silent", "Silent", card => card.Pool is SilentCardPool, "class", currentCardPool is SilentCardPool);
				builder.Filter("regent", "Regent", card => card.Pool is RegentCardPool, "class", currentCardPool is RegentCardPool);
				builder.Filter("necrobinder", "Necrobinder", card => card.Pool is NecrobinderCardPool, "class", currentCardPool is NecrobinderCardPool);
				builder.Filter("defect", "Defect", card => card.Pool is DefectCardPool, "class", currentCardPool is DefectCardPool);
				builder.Filter("colorless", "Colorless", card => card.Pool is ColorlessCardPool, "class", currentCardPool is ColorlessCardPool);
				builder.FilterGroup("type", "Type");
				builder.Filter("attack", "Attack", card => card.Type == CardType.Attack, "type");
				builder.Filter("skill", "Skill", card => card.Type == CardType.Skill, "type");
				builder.Filter("power", "Power", card => card.Type == CardType.Power, "type");
				builder.FilterGroup("rarity", "Rarity");
				builder.Filter("basic", "Basic", card => card.Rarity == CardRarity.Basic, "rarity");
				builder.Filter("common", "Common", card => card.Rarity == CardRarity.Common, "rarity");
				builder.Filter("uncommon", "Uncommon", card => card.Rarity == CardRarity.Uncommon, "rarity");
				builder.Filter("rare", "Rare", card => card.Rarity == CardRarity.Rare, "rarity");
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatCardTitle(a), FormatCardTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("cost", "Cost", (a, b) => a.EnergyCost.Canonical.CompareTo(b.EnergyCost.Canonical));
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllPotions,
			new SelectItemAdapter<PotionModel>
			{
				GetId = potion => potion.Id.ToString(),
				GetName = potion => FormatPotionTitle(potion),
				GetSearchText = potion => $"{potion.Id} {FormatPotionTitle(potion)} {potion.DynamicDescription}",
				CreateView = (potion, _) => CreatePotionGridItem(potion),
				BindActivation = (_, view, activate) => BindGuiReleaseActivation(view, activate)
			}, builder =>
			{
				builder.Layout(10, new Vector2(60f, 60f), 32, 32);
				builder.FilterGroup("class", "Class");
				builder.Filter("ironclad", "Ironclad", potion => potion.Pool is IroncladPotionPool, "class");
				builder.Filter("silent", "Silent", potion => potion.Pool is SilentPotionPool, "class");
				builder.Filter("regent", "Regent", potion => potion.Pool is RegentPotionPool, "class");
				builder.Filter("necrobinder", "Necrobinder", potion => potion.Pool is NecrobinderPotionPool, "class");
				builder.Filter("defect", "Defect", potion => potion.Pool is DefectPotionPool, "class");
				builder.Filter("shared", "Shared", potion => potion.Pool is SharedPotionPool, "class");
				builder.FilterGroup("rarity", "Rarity");
				AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatPotionTitle(a), FormatPotionTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", "Rarity", ComparePotionRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					GetPotionGroupKey,
					GetPotionGroupHeader,
					PotionGroupOrder);
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateRelicGridItem(relic),
				BindActivation = (_, view, activate) => BindRelicActivation(view, activate)
			}, builder =>
			{
				builder.Layout(10, new Vector2(68f, 68f), 32, 32);
				builder.FilterGroup("class", "Class");
				builder.Filter("ironclad", "Ironclad", relic => relic.Pool is IroncladRelicPool, "class");
				builder.Filter("silent", "Silent", relic => relic.Pool is SilentRelicPool, "class");
				builder.Filter("regent", "Regent", relic => relic.Pool is RegentRelicPool, "class");
				builder.Filter("necrobinder", "Necrobinder", relic => relic.Pool is NecrobinderRelicPool, "class");
				builder.Filter("defect", "Defect", relic => relic.Pool is DefectRelicPool, "class");
				builder.Filter("shared", "Shared", relic => relic.Pool is SharedRelicPool, "class");
				builder.FilterGroup("rarity", "Rarity");
				AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatRelicTitle(a), FormatRelicTitle(b), StringComparison.Ordinal));
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", "Rarity", CompareRelicRarity, activeByDefault: true);
				builder.GroupBySorter(
					"rarity",
					relic => GetRelicGroupKey(relic, relicGroupingData),
					key => GetRelicGroupHeader(key, relicGroupingData),
					relicGroupingData.GroupOrder);
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllEvents,
			new SelectItemAdapter<EventModel>
			{
				GetId = eventModel => eventModel.Id.ToString(),
				GetName = eventModel => FormatEventTitle(eventModel),
				GetSearchText = eventModel => $"{eventModel.Id} {FormatEventTitle(eventModel)} {eventModel.InitialDescription}",
				CreateView = (eventModel, _) => CreateTextModelGridItem(eventModel, FormatEventTitle(eventModel), eventModel.Id.Entry, "Event")
			}, builder =>
			{
				builder.Layout(4, new Vector2(220f, 120f), 24, 24);
				builder.FilterGroup("layout", "Layout");
				builder.Filter("default", "Default", eventModel => eventModel.LayoutType == EventLayoutType.Default, "layout");
				builder.Filter("combat", "Combat", eventModel => eventModel.LayoutType == EventLayoutType.Combat, "layout");
				builder.Filter("ancient", "Ancient", eventModel => eventModel.LayoutType == EventLayoutType.Ancient, "layout");
				builder.FilterGroup("sharing", "Scope");
				builder.Filter("shared", "Shared", eventModel => eventModel.IsShared, "sharing");
				builder.Filter("solo", "Solo", eventModel => !eventModel.IsShared, "sharing");
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatEventTitle(a), FormatEventTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllPowers,
			new SelectItemAdapter<PowerModel>
			{
				GetId = power => power.Id.ToString(),
				GetName = power => FormatPowerTitle(power),
				GetSearchText = power => $"{power.Id} {FormatPowerTitle(power)} {power.Description}",
				CreateView = (power, _) => CreatePowerGridItem(power)
			}, builder =>
			{
				builder.Layout(5, new Vector2(220f, 104f), 24, 24);
				builder.FilterGroup("type", "Type");
				builder.Filter("buff", "Buff", power => power.Type == PowerType.Buff, "type");
				builder.Filter("debuff", "Debuff", power => power.Type == PowerType.Debuff, "type");
				builder.Filter("type_none", "None", power => power.Type == PowerType.None, "type");
				builder.FilterGroup("stack", "Stack");
				builder.Filter("stack_none", "None", power => power.StackType == PowerStackType.None, "stack");
				builder.Filter("counter", "Counter", power => power.StackType == PowerStackType.Counter, "stack");
				builder.Filter("single", "Single", power => power.StackType == PowerStackType.Single, "stack");
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatPowerTitle(a), FormatPowerTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("type", "Type", (a, b) => a.Type.CompareTo(b.Type));
			});
	}

	private void TryAddLoadoutItems()
	{
		if (_loadoutItemsAdded)
			return;

		try
		{
			AddLoadoutItems();
			_loadoutItemsAdded = true;
			UpdatePanelHeight();
		}
		catch (KeyNotFoundException exception)
		{
			ScheduleLoadoutItemRetry(exception);
		}
	}

	private async void ScheduleLoadoutItemRetry(KeyNotFoundException exception)
	{
		if (_loadoutItemRetryScheduled)
			return;

		if (_loadoutItemInitAttempts >= MaxLoadoutItemInitAttempts)
		{
			GD.PushError($"LoadoutPanel: failed to initialize loadout items after {_loadoutItemInitAttempts} frames. Last missing key: {exception.Message}");
			return;
		}

		_loadoutItemInitAttempts++;
		_loadoutItemRetryScheduled = true;

		if (_loadoutItemInitAttempts == 1)
			GD.PushWarning($"LoadoutPanel: ModelDb is not ready yet; retrying loadout item initialization. Missing key: {exception.Message}");

		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

		_loadoutItemRetryScheduled = false;
		if (IsInsideTree())
			TryAddLoadoutItems();
	}

	private void CreateAndAddLoadoutItem<TModel>(IEnumerable<TModel> models, SelectItemAdapter<TModel> adapter,  Action<SelectScreenBuilder<TModel>> builder)
	{
		var item = new NLoadoutPanelItem();
		var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
		var screen = scene.Instantiate<NGenericSelectScreen>();
		screen.Configure(models, adapter, builder);
		
		item.BoundScreen = screen;
		_itemsContainer.AddChild(item);
	}

	private static Control CreateCardGridItem(CardModel model)
	{
		var card = NCard.Create(model);
		if (card is null)
		{
			return new Control
			{
				CustomMinimumSize = NCard.defaultSize
			};
		}

		var holder = NGridCardHolder.Create(card);
		if (holder is null)
		{
			card.CustomMinimumSize = card.GetCurrentSize();
			return card;
		}

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.Scale = holder.SmallScale;
		holder.CustomMinimumSize = NCard.defaultSize * holder.SmallScale;
		return holder;
	}

	private static Control CreatePotionGridItem(PotionModel model)
	{
		NLabPotionHolder? holder = NLabPotionHolder.Create(model.ToMutable(), ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatPotionTitle(model), model.Id.Entry, "Potion");

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(60f, 60f);
		return holder;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicCollectionEntry? holder = NRelicCollectionEntry.Create(model, ModelVisibility.Visible);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatRelicTitle(model), model.Id.Entry, "Relic");

		holder.MouseFilter = MouseFilterEnum.Pass;
		holder.CustomMinimumSize = new Vector2(68f, 68f);
		return holder;
	}

	private static Control CreatePowerGridItem(PowerModel model)
	{
		Texture2D? icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		return CreateTextModelGridItem(model, FormatPowerTitle(model), model.Id.Entry, model.Type.ToString(), icon, new Vector2(220f, 104f));
	}

	private static Button CreateTextModelGridItem(
		AbstractModel model,
		string title,
		string subtitle,
		string category,
		Texture2D? icon = null,
		Vector2? itemSize = null)
	{
		Vector2 size = itemSize ?? new Vector2(220f, icon is null ? 120f : 148f);
		Button button = new()
		{
			CustomMinimumSize = size,
			MouseFilter = MouseFilterEnum.Stop,
			FocusMode = FocusModeEnum.All,
			SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
			SizeFlagsVertical = SizeFlags.ShrinkBegin
		};
		button.AddThemeFontOverride("font", LoadGameFont());
		button.AddThemeFontSizeOverride("font_size", 18);
		button.AddThemeColorOverride("font_color", StsColors.cream);
		button.Text = icon is null
			? $"{title}\n{category}\n{subtitle}"
			: $"{title}\n{category}";
		button.TooltipText = $"{title}\n{model.Id}";

		if (icon is not null)
		{
			TextureRect iconRect = new()
			{
				Texture = icon,
				CustomMinimumSize = new Vector2(64f, 64f),
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
				MouseFilter = MouseFilterEnum.Ignore,
				Position = new Vector2(Mathf.Max(0f, (size.X - 64f) * 0.5f), Mathf.Max(0f, size.Y - 70f)),
				Size = new Vector2(64f, 64f)
			};
			button.AddChild(iconRect);
		}

		return button;
	}

	private static void RefreshCardVisuals(Control view)
	{
		if (!TryFindDescendantOrSelf(view, out NGridCardHolder? holder) || holder!.CardNode is null)
			return;

		holder.CardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
	}

	private static bool BindCardActivation(Control view, Action activate)
	{
		if (!TryFindDescendantOrSelf(view, out NGridCardHolder? holder))
			return false;

		holder!.Connect(NCardHolder.SignalName.Pressed, Callable.From<NCardHolder>(_ => activate()));
		return true;
	}

	private static bool BindRelicActivation(Control view, Action activate)
	{
		if (!TryFindDescendantOrSelf(view, out NRelicCollectionEntry? clickable))
			return false;

		clickable!.Connect(NClickableControl.SignalName.Released, Callable.From<NRelicCollectionEntry>(_ => activate()));
		return true;
	}

	private static bool BindGuiReleaseActivation(Control view, Action activate)
	{
		Control control = TryFindDescendantOrSelf(view, out NLabPotionHolder? potionHolder)
			? potionHolder!
			: view;

		control.GuiInput += input =>
		{
			if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
				activate();
		};

		return true;
	}

	private static bool TryFindDescendantOrSelf<TControl>(Node root, out TControl? control)
		where TControl : class
	{
		if (root is TControl direct)
		{
			control = direct;
			return true;
		}

		foreach (Node child in root.GetChildren())
		{
			if (TryFindDescendantOrSelf(child, out control))
				return true;
		}

		control = null;
		return false;
	}

	private static CardPoolModel? GetCurrentCharacterCardPool()
	{
		try
		{
			return LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())?.Character.CardPool;
		}
		catch (Exception exception)
		{
			GD.PushWarning($"LoadoutPanel: could not detect current character card pool; showing all cards by default. {exception.Message}");
			return null;
		}
	}

	private static int ComparePotionRarity(PotionModel left, PotionModel right)
	{
		int rarity = GetPotionRaritySortValue(left.Rarity).CompareTo(GetPotionRaritySortValue(right.Rarity));
		return rarity != 0 ? rarity : string.Compare(FormatPotionTitle(left), FormatPotionTitle(right), StringComparison.Ordinal);
	}

	private static int GetPotionRaritySortValue(PotionRarity rarity)
	{
		return rarity switch
		{
			PotionRarity.Common => 0,
			PotionRarity.Uncommon => 1,
			PotionRarity.Rare => 2,
			PotionRarity.Event => 3,
			PotionRarity.Token => 3,
			_ => 99
		};
	}

	private static int CompareRelicRarity(RelicModel left, RelicModel right)
	{
		int rarity = GetRelicRaritySortValue(left.Rarity).CompareTo(GetRelicRaritySortValue(right.Rarity));
		return rarity != 0 ? rarity : string.Compare(FormatRelicTitle(left), FormatRelicTitle(right), StringComparison.Ordinal);
	}

	private static int GetRelicRaritySortValue(RelicRarity rarity)
	{
		return rarity switch
		{
			RelicRarity.Starter => 0,
			RelicRarity.Common => 1,
			RelicRarity.Uncommon => 2,
			RelicRarity.Rare => 3,
			RelicRarity.Shop => 4,
			RelicRarity.Ancient => 5,
			RelicRarity.Event => 6,
			_ => 99
		};
	}

	private static readonly string[] PotionGroupOrder =
	{
		"potion:common",
		"potion:uncommon",
		"potion:rare",
		"potion:special"
	};

	private static string GetPotionGroupKey(PotionModel potion)
	{
		return potion.Rarity switch
		{
			PotionRarity.Common => "potion:common",
			PotionRarity.Uncommon => "potion:uncommon",
			PotionRarity.Rare => "potion:rare",
			PotionRarity.Event or PotionRarity.Token => "potion:special",
			_ => "potion:unknown"
		};
	}

	private static SelectGroupHeader GetPotionGroupHeader(string key)
	{
		return key switch
		{
			"potion:common" => new SelectGroupHeader(new LocString("potion_lab", "COMMON").GetFormattedText()),
			"potion:uncommon" => new SelectGroupHeader(new LocString("potion_lab", "UNCOMMON").GetFormattedText()),
			"potion:rare" => new SelectGroupHeader(new LocString("potion_lab", "RARE").GetFormattedText()),
			"potion:special" => new SelectGroupHeader(new LocString("potion_lab", "SPECIAL").GetFormattedText()),
			_ => new SelectGroupHeader("Other")
		};
	}

	private static RelicGroupingData BuildRelicGroupingData()
	{
		Dictionary<string, string> keyByRelicId = new(StringComparer.Ordinal);
		Dictionary<string, SelectGroupHeader> headers = new(StringComparer.Ordinal)
		{
			["relic:starter"] = new(new LocString("relic_collection", "STARTER").GetFormattedText()),
			["relic:common"] = new(new LocString("relic_collection", "COMMON").GetFormattedText()),
			["relic:uncommon"] = new(new LocString("relic_collection", "UNCOMMON").GetFormattedText()),
			["relic:rare"] = new(new LocString("relic_collection", "RARE").GetFormattedText()),
			["relic:shop"] = new(new LocString("relic_collection", "SHOP").GetFormattedText()),
			["relic:ancient"] = new(new LocString("relic_collection", "ANCIENT").GetFormattedText(), showWhenEmpty: true),
			["relic:event"] = new(new LocString("relic_collection", "EVENT").GetFormattedText())
		};
		List<string> groupOrder = new()
		{
			"relic:starter",
			"relic:common",
			"relic:uncommon",
			"relic:rare",
			"relic:shop",
			"relic:ancient"
		};

		foreach (AncientEventModel ancient in ModelDb.AllAncients.Distinct())
		{
			string groupKey = $"relic:ancient:{ancient.Id.Entry}";
			LocString headerText = new("relic_collection", "ANCIENT_SUBCATEGORY");
			headerText.Add("Ancient", ancient.Title);
			headers[groupKey] = new SelectGroupHeader(headerText.GetFormattedText(), ancient.RunHistoryIcon);
			groupOrder.Add(groupKey);

			foreach (RelicModel relic in ancient.AllPossibleOptions.Select(option => option.Relic?.CanonicalInstance).OfType<RelicModel>())
				keyByRelicId[relic.Id.ToString()] = groupKey;
		}

		groupOrder.Add("relic:event");
		return new RelicGroupingData(keyByRelicId, headers, groupOrder);
	}

	private static string GetRelicGroupKey(RelicModel relic, RelicGroupingData groupingData)
	{
		if (relic.Rarity == RelicRarity.Ancient && groupingData.AncientGroupKeyByRelicId.TryGetValue(relic.Id.ToString(), out string? ancientKey))
			return ancientKey;

		return relic.Rarity switch
		{
			RelicRarity.Starter => "relic:starter",
			RelicRarity.Common => "relic:common",
			RelicRarity.Uncommon => "relic:uncommon",
			RelicRarity.Rare => "relic:rare",
			RelicRarity.Shop => "relic:shop",
			RelicRarity.Ancient => "relic:ancient",
			RelicRarity.Event => "relic:event",
			_ => "relic:unknown"
		};
	}

	private static SelectGroupHeader GetRelicGroupHeader(string key, RelicGroupingData groupingData)
	{
		return groupingData.HeadersByKey.TryGetValue(key, out SelectGroupHeader? header)
			? header
			: new SelectGroupHeader("Other");
	}

	private static void AddEnumFilters<TModel, TEnum>(
		SelectScreenBuilder<TModel> builder,
		string groupId,
		Func<TModel, TEnum> getValue,
		TEnum excludedValue)
		where TEnum : struct, Enum
	{
		foreach (TEnum value in Enum.GetValues<TEnum>())
		{
			if (EqualityComparer<TEnum>.Default.Equals(value, excludedValue))
				continue;

			string label = value.ToString();
			builder.Filter(label.ToLowerInvariant(), label, model => EqualityComparer<TEnum>.Default.Equals(getValue(model), value), groupId);
		}
	}

	private static Font LoadGameFont()
	{
		return GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
	}

	private static string FormatCardTitle(CardModel card)
	{
		return card.Title;
	}

	private static string FormatPotionTitle(PotionModel potion)
	{
		return potion.Title.GetFormattedText();
	}

	private static string FormatRelicTitle(RelicModel relic)
	{
		return relic.Title.GetFormattedText();
	}

	private static string FormatEventTitle(EventModel eventModel)
	{
		return eventModel.Title.GetFormattedText();
	}

	private static string FormatPowerTitle(PowerModel power)
	{
		return power.Title.GetFormattedText();
	}

	private sealed class RelicGroupingData
	{
		public RelicGroupingData(
			IReadOnlyDictionary<string, string> ancientGroupKeyByRelicId,
			IReadOnlyDictionary<string, SelectGroupHeader> headersByKey,
			IReadOnlyList<string> groupOrder)
		{
			AncientGroupKeyByRelicId = ancientGroupKeyByRelicId;
			HeadersByKey = headersByKey;
			GroupOrder = groupOrder;
		}

		public IReadOnlyDictionary<string, string> AncientGroupKeyByRelicId { get; }
		public IReadOnlyDictionary<string, SelectGroupHeader> HeadersByKey { get; }
		public IReadOnlyList<string> GroupOrder { get; }
	}
	
	private void UpdatePanelHeight()
	{
		// Includes children/layout of the VBox.
		Vector2 contentMin = _panelContainer.GetCombinedMinimumSize();

		// Keep current width, only change height.
		Vector2 size = Size;
		size.Y = contentMin.Y;
		Size = size;
		//recenter it
		Vector2 pos = Position;
		pos.Y = (GetParent<Control>().Size.Y - Size.Y) / 2f;
		Position = pos;
	}
}
