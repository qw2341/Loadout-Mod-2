using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace  Loadout.UI;

public partial class NLoadoutPanel : Panel
{
	[Export]
	public bool Shown = true;

	[Export]
	public float SlideSpeed = 12f;
	
	private PanelContainer _panelContainer;
	private MarginContainer _marginContainer;
	private Control _itemsContainer;

	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_panelContainer = GetNode<PanelContainer>("PanelContainer");
		_marginContainer = GetNode<MarginContainer>("PanelContainer/MarginContainer");
		_itemsContainer = GetNode<Control>("PanelContainer/MarginContainer/VBoxContainer");
		
		
		
		AddLoadoutItems();
		
		// Initial resize
		UpdatePanelHeight();

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
				GetName = card => card.Title.ToString(),
				GetSearchText = card => $"{card.Id} {card.Title.ToString()} {card.TitleLocString} {card.Description}",
				CreateView = (card, state) => NGridCardHolder.Create(NCard.Create(card)),
				UpdateView = (card, holder, state) =>
				{
					// if (holder is NGridCardHolder)
					// {
					// 	((NGridCardHolder)holder).
					// }
				},
				MatchesSearch = null,
				BindActivation = null
			}, builder =>
			{
				builder.FilterGroup("class","Class");
				builder.Filter("ironclad","Ironclad", card => card.Pool is IroncladCardPool, "ironclad");
				builder.Sorter("name", "Name", (a, b) => a.EntrySortingId - b.EntrySortingId, (a, b) => b.EntrySortingId - a.EntrySortingId, true, false);
			});
		
		var loadoutBag2 = new NLoadoutPanelItem();
		_itemsContainer.AddChild(loadoutBag2);
		var loadoutBag3 = new NLoadoutPanelItem();
		_itemsContainer.AddChild(loadoutBag3);
		var loadoutBag4 = new NLoadoutPanelItem();
		_itemsContainer.AddChild(loadoutBag4);
	}

	private void CreateAndAddLoadoutItem<TModel>(IEnumerable<TModel> models, SelectItemAdapter<TModel> adapter,  Action<SelectScreenBuilder<TModel>> builder) where TModel : AbstractModel
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
