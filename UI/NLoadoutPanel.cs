#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;

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
		CreateAndAddLoadoutItem(
			ModelDb.AllCards,
			new SelectItemAdapter<CardModel>
			{
				GetId = card => card.Id.ToString(),
				GetName = card => FormatCardTitle(card),
				GetSearchText = card => $"{card.Id} {FormatCardTitle(card)} {card.TitleLocString} {card.Description}",
				CreateView = (card, _) => CreateCardGridItem(card)
			}, builder =>
			{
				builder.FilterGroup("class","Class");
				builder.Filter("ironclad","Ironclad", card => card.Pool is IroncladCardPool, "class");
				builder.Filter("silent", "Silent", card => card.Pool is SilentCardPool, "class");
				builder.Filter("regent", "Regent", card => card.Pool is RegentCardPool, "class");
				builder.Filter("necrobinder", "Necrobinder", card => card.Pool is NecrobinderCardPool, "class");
				builder.Filter("defect", "Defect", card => card.Pool is DefectCardPool, "class");
				builder.Filter("colorless", "Colorless", card => card.Pool is ColorlessCardPool, "class");
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
				CreateView = (potion, _) => CreatePotionGridItem(potion)
			}, builder =>
			{
				builder.FilterGroup("class", "Class");
				builder.Filter("ironclad", "Ironclad", potion => potion.Pool is IroncladPotionPool, "class");
				builder.Filter("silent", "Silent", potion => potion.Pool is SilentPotionPool, "class");
				builder.Filter("regent", "Regent", potion => potion.Pool is RegentPotionPool, "class");
				builder.Filter("necrobinder", "Necrobinder", potion => potion.Pool is NecrobinderPotionPool, "class");
				builder.Filter("defect", "Defect", potion => potion.Pool is DefectPotionPool, "class");
				builder.Filter("shared", "Shared", potion => potion.Pool is SharedPotionPool, "class");
				builder.FilterGroup("rarity", "Rarity");
				AddEnumFilters(builder, "rarity", (PotionModel potion) => potion.Rarity, PotionRarity.None);
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatPotionTitle(a), FormatPotionTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", "Rarity", (a, b) => a.Rarity.CompareTo(b.Rarity));
			});

		CreateAndAddLoadoutItem(
			ModelDb.AllRelics,
			new SelectItemAdapter<RelicModel>
			{
				GetId = relic => relic.Id.ToString(),
				GetName = relic => FormatRelicTitle(relic),
				GetSearchText = relic => $"{relic.Id} {FormatRelicTitle(relic)} {relic.DynamicDescription}",
				CreateView = (relic, _) => CreateRelicGridItem(relic)
			}, builder =>
			{
				builder.FilterGroup("class", "Class");
				builder.Filter("ironclad", "Ironclad", relic => relic.Pool is IroncladRelicPool, "class");
				builder.Filter("silent", "Silent", relic => relic.Pool is SilentRelicPool, "class");
				builder.Filter("regent", "Regent", relic => relic.Pool is RegentRelicPool, "class");
				builder.Filter("necrobinder", "Necrobinder", relic => relic.Pool is NecrobinderRelicPool, "class");
				builder.Filter("defect", "Defect", relic => relic.Pool is DefectRelicPool, "class");
				builder.Filter("shared", "Shared", relic => relic.Pool is SharedRelicPool, "class");
				builder.FilterGroup("rarity", "Rarity");
				AddEnumFilters(builder, "rarity", (RelicModel relic) => relic.Rarity, RelicRarity.None);
				builder.Sorter("name", "Name", (a, b) => string.Compare(FormatRelicTitle(a), FormatRelicTitle(b), StringComparison.Ordinal), activeByDefault: true);
				builder.Sorter("id", "ID", (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
				builder.Sorter("rarity", "Rarity", (a, b) => a.Rarity.CompareTo(b.Rarity));
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
		NPotion? potion = NPotion.Create(model);
		if (potion is null)
			return CreateTextModelGridItem(model, FormatPotionTitle(model), model.Id.Entry, "Potion");

		potion.MouseFilter = MouseFilterEnum.Pass;
		potion.CustomMinimumSize = new Vector2(96f, 128f);
		return potion;
	}

	private static Control CreateRelicGridItem(RelicModel model)
	{
		NRelicBasicHolder? holder = NRelicBasicHolder.Create(model);
		if (holder is null)
			return CreateTextModelGridItem(model, FormatRelicTitle(model), model.Id.Entry, "Relic");

		holder.CustomMinimumSize = new Vector2(112f, 112f);
		return holder;
	}

	private static Control CreatePowerGridItem(PowerModel model)
	{
		Texture2D? icon = null;
		if (ResourceLoader.Exists(model.IconPath))
			icon = model.Icon;

		return CreateTextModelGridItem(model, FormatPowerTitle(model), model.Id.Entry, model.Type.ToString(), icon);
	}

	private static Button CreateTextModelGridItem(AbstractModel model, string title, string subtitle, string category, Texture2D? icon = null)
	{
		Button button = new()
		{
			CustomMinimumSize = new Vector2(220f, icon is null ? 120f : 148f),
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
				Position = new Vector2(78f, 72f),
				Size = new Vector2(64f, 64f)
			};
			button.AddChild(iconRect);
		}

		return button;
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
