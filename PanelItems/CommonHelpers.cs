using System;
#nullable enable annotations

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Loadout.Helpers;
using Loadout.Services.Actions;
using Loadout.Services.LastActions;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using Loadout.UI.Screens.Controls;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.PanelItems;

public class CommonHelpers
{
    private const string BaseGameModId = "slaythespire2";
    private const string BaseGameModName = "Slay the Spire 2";
    private const int DynamicRelayoutAnimationItemLimit = 60;

    public const string GenericSelectScreenScenePath = "res://UI/Screens/GenericSelectScreen.tscn";
    public const string RelicSelectScreenScenePath = NRelicSelectScreen.ScenePath;

    public const string FavoriteModeAllKey = "all";
    public const string FavoriteModeFavoritesKey = "favorites";

    private static Dictionary<Assembly, string> _modIdsByAssembly;
    private static Dictionary<string, string> _modNamesById;

    public static NLoadoutPanelItem CreateAndAddLoadoutItem<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        Action<SelectScreenBuilder<TModel>> builder,
        Action<NGenericSelectScreen> beforeOpen,
        string textureFileName,
        string title,
        string description,
        CommonHelpers.SelectActivationHandler onActivated = null,
        string lastActionItemKey = null,
        Func<Task> replayQuickAction = null,
        string selectScreenScenePath = GenericSelectScreenScenePath)
    {
        var item = new NLoadoutPanelItem(textureFileName, title, description);
        var scene = GD.Load<PackedScene>(selectScreenScenePath);
        var screen = scene.Instantiate<NGenericSelectScreen>();
        bool activationInFlight = false;
        LastActionCaptureSession captureSession = null;

        void ConfigureScreen(NGenericSelectScreen target)
        {
            target.Configure(models, adapter, builder);
            beforeOpen?.Invoke(target);
            target.RequestDeferredVisibleRefresh();
        }

        ConfigureScreen(screen);
        screen.LocaleChanged += () =>
        {
            SelectScreenUiState state = screen.CaptureUiState();
            ConfigureScreen(screen);
            screen.RestoreUiState(state);
        };
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        screen.ScreenClosed += () =>
        {
            captureSession?.Commit();
        };
        if (onActivated is not null)
        {
            screen.ItemActivated += (selectItem, state) =>
            {
                if (activationInFlight)
                    return;

                LastActionCaptureSession activeCaptureSession = captureSession;
                activationInFlight = true;
                _ = HandleStaticItemActivatedAsync(
                    screen,
                    selectItem,
                    onActivated,
                    entries => activeCaptureSession?.Add(entries),
                    () => activationInFlight = false);
            };
        }

        item.BoundScreen = screen;
        item.QuickAction = replayQuickAction;
        if (lastActionItemKey is not null)
            item.AfterOpen = _ => captureSession = new LastActionCaptureSession(lastActionItemKey);
        item.BeforeOpen = target =>
        {
            if (!target.IsConfiguredForCurrentLocale)
            {
                ConfigureScreen(target);
                return;
            }

            beforeOpen?.Invoke(target);
        };

        NLoadoutPanel.ItemsContainer.AddChild(item);
        return item;
    }
    
	public static void CreateAndAddDynamicLoadoutItem<TModel>(
		Func<IReadOnlyList<TModel>> getModels,
		SelectItemAdapter<TModel> adapter,
		Action<SelectScreenBuilder<TModel>> builder,
		SelectActivationHandler onActivated,
		string textureFileName,
		string title,
		string description,
		Action<NGenericSelectScreen, Action>? afterConfigure = null,
		Action<NGenericSelectScreen, IGenericSelectItem>? afterActivationRefresh = null,
		string selectScreenScenePath = GenericSelectScreenScenePath,
		bool reconcileModelsOnEveryOpen = true,
		bool refreshModelsAfterActivation = true,
		bool syncChangesWhileHidden = false)
	{
		var item = new NLoadoutPanelItem(textureFileName, title, description);
		var scene = GD.Load<PackedScene>(selectScreenScenePath);
		var screen = scene.Instantiate<NGenericSelectScreen>();
		bool activationInFlight = false;
		bool hiddenSyncScheduled = false;
		List<LoadoutRunContentChangedEventArgs> pendingRunContentChanges = [];
		object? configuredRunState = null;

		void ConfigureCurrentModels(NGenericSelectScreen target, bool preserveViews = false)
		{
			object? currentRunState = CommonHelpers.GetCurrentDynamicRunStateIdentity();
			IReadOnlyList<TModel> models = getModels();
			// if (models.Count == 0)
			// 	LogEmptyDynamicScreen(title);

			if (preserveViews)
				target.ConfigurePreservingViews(models, adapter, builder, animateRelayout: true);
			else
				target.Configure(models, adapter, builder);

			afterConfigure?.Invoke(target, () => RefreshCurrentModels(target, resetScroll: true));

			if (!preserveViews)
				target.RequestDeferredVisibleRefresh();

			configuredRunState = currentRunState;
		}

		void RefreshCurrentModels(
			NGenericSelectScreen target,
			bool animateRelayout = false,
			bool resetScroll = false,
			bool updateExistingViews = true)
		{
			object? currentRunState = CommonHelpers.GetCurrentDynamicRunStateIdentity();
			IReadOnlyList<TModel> models = getModels();
			bool shouldAnimateRelayout = animateRelayout && models.Count <= DynamicRelayoutAnimationItemLimit;
			if (!resetScroll
			    && animateRelayout
			    && target.TryApplySingleItemRemoval(models, adapter, shouldAnimateRelayout))
			{
				configuredRunState = currentRunState;
				return;
			}

			target.RefreshItemsPreservingViews(models, adapter, shouldAnimateRelayout, resetScroll, updateExistingViews);
			configuredRunState = currentRunState;
		}

		void RefreshAfterActivation(NGenericSelectScreen target, IGenericSelectItem _)
		{
			if (refreshModelsAfterActivation)
				RefreshCurrentModels(target, animateRelayout: false);
		}

		bool TryHandleTargetedRunContentChange(NGenericSelectScreen target, LoadoutRunContentChangedEventArgs change)
		{
			Type modelType = typeof(TModel);
			if (change.Kind == LoadoutRunContentKind.Relics
			    && IsOwnedItemModel(modelType, typeof(RelicModel)))
			{
				if (change.Mode == LoadoutRunContentChangeMode.Update)
				{
					if (change.ChangedRelics.Count == 0)
						return true;

					foreach (LoadoutChangedRelic changed in change.ChangedRelics)
					{
						IGenericSelectItem? soleMatch = null;
						IGenericSelectItem? indexMatch = null;
						int matchCount = 0;
						foreach (IGenericSelectItem candidate in target.Items)
						{
							if (candidate.UntypedModel is not LoadoutOwnedItem<RelicModel> owned
							    || owned.OwnerNetId != changed.OwnerNetId
							    || !owned.Model.Id.Equals(changed.ModelId))
							{
								continue;
							}

							matchCount++;
							soleMatch = candidate;
							if (owned.Index == changed.Index)
								indexMatch = candidate;
						}

						IGenericSelectItem? matched = matchCount == 1 ? soleMatch : indexMatch;
						if (matched is null)
							return false;

						if (target is NRelicSelectScreen relicScreen)
							relicScreen.RefreshItemById(matched.Id);
						else
							target.RefreshItemView(matched);
					}

					return true;
				}

				if (change.Mode == LoadoutRunContentChangeMode.Add)
				{
					RefreshCurrentModels(target, animateRelayout: false, updateExistingViews: false);
					return true;
				}

				if (change.Mode == LoadoutRunContentChangeMode.Remove)
				{
					object? relicRunState = CommonHelpers.GetCurrentDynamicRunStateIdentity();
					IReadOnlyList<TModel> relicModels = getModels();
					string? removedRelicItemId = change.ChangedRelics
						.Select(changed => target.Items.FirstOrDefault(item => item.UntypedModel is LoadoutOwnedItem<RelicModel> owned && MatchesChangedRelic(owned, changed))?.Id)
						.FirstOrDefault(id => id is not null);
					if (!target.TryApplySingleItemRemoval(relicModels, adapter, relicModels.Count <= DynamicRelayoutAnimationItemLimit, updateExistingViews: false, expectedRemovedItemId: removedRelicItemId))
						return false;
					configuredRunState = relicRunState;
					return true;
				}
			}

			if (change.Kind != LoadoutRunContentKind.Cards
			    || !IsOwnedItemModel(modelType, typeof(CardModel)))
			{
				return false;
			}

			if (change.Mode == LoadoutRunContentChangeMode.Update)
			{
				if (change.ChangedCards.Count > 0)
				{
					Dictionary<(ulong OwnerNetId, int Index, string ModelId), LoadoutCardVisualRefreshKind> changedByIdentity = new();
					foreach (LoadoutChangedCard changed in change.ChangedCards)
					{
						var key = (changed.OwnerNetId, changed.Index, changed.ModelId.ToString());
						if (!changedByIdentity.TryGetValue(key, out LoadoutCardVisualRefreshKind existing)
						    || existing != LoadoutCardVisualRefreshKind.Reload)
						{
							changedByIdentity[key] = changed.RefreshKind;
						}
					}

					foreach (IGenericSelectItem item in target.VisibleItems)
					{
						if (item.UntypedModel is not LoadoutOwnedItem<CardModel> ownedCard || item.View is not { } view)
							continue;

						var key = (ownedCard.OwnerNetId, ownedCard.Index, ownedCard.Model.Id.ToString());
						if (!changedByIdentity.TryGetValue(key, out LoadoutCardVisualRefreshKind refreshKind))
							continue;

						if (refreshKind == LoadoutCardVisualRefreshKind.Reload)
							CardPrinter.ReloadCardVisuals(view, ownedCard.Model);
						else
							CardPrinter.RefreshCardVisuals(view, ownedCard.Model);
					}

					return true;
				}

				target.RefreshCurrentItemStates();
				return true;
			}

			if (change.Mode == LoadoutRunContentChangeMode.Add)
			{
				RefreshCurrentModels(target, animateRelayout: false, updateExistingViews: false);
				return true;
			}

			if (change.Mode != LoadoutRunContentChangeMode.Remove)
				return false;

			object? currentRunState = CommonHelpers.GetCurrentDynamicRunStateIdentity();
			IReadOnlyList<TModel> models = getModels();
			bool shouldAnimateRelayout = models.Count <= DynamicRelayoutAnimationItemLimit;
			string? removedItemId = null;
			foreach (LoadoutChangedCard changed in change.ChangedCards)
			{
				removedItemId = target.VisibleItems
					.FirstOrDefault(item => item.UntypedModel is LoadoutOwnedItem<CardModel> ownedCard
					                        && MatchesChangedCard(ownedCard, changed))
					?.Id;
				if (removedItemId is not null)
					break;
			}

			if (!target.TryApplySingleItemRemoval(
				    models,
				    adapter,
				    shouldAnimateRelayout,
				    updateExistingViews: false,
				    expectedRemovedItemId: removedItemId))
				return false;

			configuredRunState = currentRunState;
			return true;
		}

		void ApplyPendingRunContentChanges(NGenericSelectScreen target, bool animateRelayout)
		{
			if (pendingRunContentChanges.Count == 0)
				return;

			List<LoadoutRunContentChangedEventArgs> pending = pendingRunContentChanges.ToList();
			pendingRunContentChanges.Clear();

			// Structural changes are coalesced into one preserving snapshot. Applying
			// several add/remove events independently would enumerate the same relic
			// collection and relayout it multiple times before the player sees it.
			if (pending.Any(change => change.Mode != LoadoutRunContentChangeMode.Update))
			{
				RefreshCurrentModels(target, animateRelayout, updateExistingViews: false);
				return;
			}

			foreach (LoadoutRunContentChangedEventArgs change in pending)
			{
				if (TryHandleTargetedRunContentChange(target, change))
					continue;

				RefreshCurrentModels(target, animateRelayout, updateExistingViews: false);
				return;
			}
		}

		async Task SyncPendingChangesWhileHiddenAsync()
		{
			if (hiddenSyncScheduled)
				return;

			hiddenSyncScheduled = true;
			try
			{
				if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree())
					return;

				SceneTree tree = screen.GetTree();
				await screen.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

				if (!GodotObject.IsInstanceValid(screen)
				    || !screen.IsInsideTree()
				    || screen.IsVisibleInTree()
				    || pendingRunContentChanges.Count == 0)
				{
					return;
				}

				ApplyPendingRunContentChanges(screen, animateRelayout: false);
				if (screen is NRelicSelectScreen relicScreen)
					await relicScreen.PrewarmForFirstOpenAsync();
			}
			catch (Exception exception)
			{
				GD.PushWarning($"Loadout hidden select-screen synchronization failed for '{title}': {exception}");
			}
			finally
			{
				hiddenSyncScheduled = false;
				if (syncChangesWhileHidden
				    && GodotObject.IsInstanceValid(screen)
				    && screen.IsInsideTree()
				    && !screen.IsVisibleInTree()
				    && pendingRunContentChanges.Count > 0)
				{
					_ = SyncPendingChangesWhileHiddenAsync();
				}
			}
		}

		void RefreshDynamicScreenForOpen(NGenericSelectScreen target)
		{
			object? currentRunState = CommonHelpers.GetCurrentDynamicRunStateIdentity();
			if (!target.IsConfiguredForCurrentLocale || !ReferenceEquals(configuredRunState, currentRunState))
			{
				ConfigureCurrentModels(target);
				pendingRunContentChanges.Clear();
				return;
			}

			afterConfigure?.Invoke(target, () => RefreshCurrentModels(target, resetScroll: true));
			if (reconcileModelsOnEveryOpen)
			{
				RefreshCurrentModels(target, resetScroll: true);
				pendingRunContentChanges.Clear();
				return;
			}

			ApplyPendingRunContentChanges(target, animateRelayout: false);
		}

		ConfigureCurrentModels(screen);
		screen.LocaleChanged += () =>
		{
			SelectScreenUiState state = screen.CaptureUiState();
			ConfigureCurrentModels(screen, preserveViews: false);
			pendingRunContentChanges.Clear();
			screen.RestoreUiState(state);
		};
		screen.VisibilityChanged += () =>
		{
			if (!screen.Visible)
				return;

			ApplyPendingRunContentChanges(screen, animateRelayout: true);
		};
		LoadoutRunContentChangeService.Changed += change =>
		{
			if (!MatchesRunContentChange<TModel>(change))
				return;

			if (!GodotObject.IsInstanceValid(screen) || !screen.IsInsideTree() || !screen.IsVisibleInTree())
			{
				pendingRunContentChanges.Add(change);
				if (syncChangesWhileHidden)
					_ = SyncPendingChangesWhileHiddenAsync();
				return;
			}

			if (TryHandleTargetedRunContentChange(screen, change))
				return;

			RefreshCurrentModels(screen, animateRelayout: true);
		};
		screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
		screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
		screen.ItemActivated += (selectItem, state) =>
		{
			if (activationInFlight)
				return;

			activationInFlight = true;
			_ = HandleDynamicItemActivatedAsync(
				screen,
				selectItem,
				onActivated,
				afterActivationRefresh ?? RefreshAfterActivation,
				() => activationInFlight = false);
		};

		item.BoundScreen = screen;
		item.BeforeOpen = RefreshDynamicScreenForOpen;

		NLoadoutPanel.ItemsContainer.AddChild(item);
	}

	private static bool MatchesRunContentChange<TModel>(LoadoutRunContentChangedEventArgs change)
	{
		Type modelType = typeof(TModel);
		return change.Kind switch
		{
			LoadoutRunContentKind.Cards => modelType == typeof(CardModel)
			                              || IsOwnedItemModel(modelType, typeof(CardModel)),
			LoadoutRunContentKind.Relics => modelType == typeof(RelicModel)
			                               || IsOwnedItemModel(modelType, typeof(RelicModel)),
			_ => false
		};
	}

	private static bool IsOwnedItemModel(Type modelType, Type itemModelType)
	{
		return modelType.IsGenericType
		       && modelType.GetGenericTypeDefinition() == typeof(LoadoutOwnedItem<>)
		       && modelType.GetGenericArguments()[0] == itemModelType;
	}

	private static bool MatchesChangedCard(LoadoutOwnedItem<CardModel> item, LoadoutChangedCard changed)
	{
		return item.OwnerNetId == changed.OwnerNetId
		       && item.Index == changed.Index
		       && item.Model.Id.Equals(changed.ModelId);
	}

	private static bool MatchesChangedRelic(LoadoutOwnedItem<RelicModel> item, LoadoutChangedRelic changed)
	{
		return item.OwnerNetId == changed.OwnerNetId
		       && item.Index == changed.Index
		       && item.Model.Id.Equals(changed.ModelId);
	}

    public static void LogEmptyDynamicScreen(string title)
    {
        bool isInProgress = RunManager.Instance.IsInProgress;
        Player localPlayer = GetLocalRunPlayer();
        int deckCount = localPlayer?.Deck.Cards.Count ?? -1;
        int relicCount = localPlayer?.Relics.Count ?? -1;
        GD.PushWarning(
            $"LoadoutPanel: dynamic screen '{title}' has no items. " +
            $"isInProgress={isInProgress}, localPlayerResolved={localPlayer is not null}, deckCount={deckCount}, relicCount={relicCount}.");
    }

    public static IReadOnlyList<CardModel> GetLocalDeckCards()
    {
        Player localPlayer = GetLocalRunPlayer();
        return localPlayer is null ? Array.Empty<CardModel>() : localPlayer.Deck.Cards.ToList();
    }

    public static IReadOnlyList<RelicModel> GetLocalRelics()
    {
        Player localPlayer = GetLocalRunPlayer();
        return localPlayer is null ? Array.Empty<RelicModel>() : localPlayer.Relics.ToList();
    }

    public static Player GetLocalRunPlayer()
    {
        try
        {
            if (!RunManager.Instance.IsInProgress)
                return null;

            return LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not resolve local run player. {exception.Message}");
            return null;
        }
    }

    public static Action? BindGuiReleaseActivationWithCleanup(Control view, Action activate)
    {
        if (view is null || !GodotObject.IsInstanceValid(view))
            return null;

        Control control = TryFindDescendantOrSelf(view, out NLabPotionHolder potionHolder)
            ? potionHolder!
            : view;

        void OnGuiInput(InputEvent input)
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                activate();
        }

        control.GuiInput += OnGuiInput;
        return () =>
        {
            if (GodotObject.IsInstanceValid(control))
                control.GuiInput -= OnGuiInput;
        };
    }

    public static bool BindGuiReleaseActivation(Control view, Action activate)
    {
        return BindGuiReleaseActivationWithCleanup(view, activate) is not null;
    }

    public static bool TryFindDescendantOrSelf<TControl>(Node root, out TControl control)
        where TControl : class
    {
        control = null;

        if (root is null || !GodotObject.IsInstanceValid(root))
            return false;

        if (root is TControl direct)
        {
            control = direct;
            return true;
        }

        Godot.Collections.Array<Node> children;
        try
        {
            children = root.GetChildren();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        foreach (Node child in children)
        {
            if (TryFindDescendantOrSelf(child, out control))
                return true;
        }

        return false;
    }

    public static IReadOnlyList<TPool> BuildOrderedPools<TPool>(
        IEnumerable<TPool> usedPools,
        IEnumerable<TPool> characterPools,
        Func<TPool, bool> includeSharedPool)
        where TPool : AbstractModel
    {
        Dictionary<string, TPool> usedByKey = usedPools
            .GroupBy(PoolKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First<TPool>(), StringComparer.Ordinal);

        List<TPool> ordered = new();
        HashSet<string> added = new(StringComparer.Ordinal);

        foreach (TPool pool in characterPools)
        {
            string key = PoolKey(pool);
            if (usedByKey.ContainsKey(key) && added.Add(key))
                ordered.Add(pool);
        }

        foreach (TPool pool in usedByKey.Values.OrderBy(GetPoolLabel, StringComparer.Ordinal))
        {
            string key = PoolKey(pool);
            if (!added.Contains(key) && includeSharedPool(pool) && added.Add(key))
                ordered.Add(pool);
        }

        return ordered;
    }

    public static string GetPoolLabel(AbstractModel pool)
    {
        CharacterModel character = ModelDb.AllCharacters
            .Where(candidate => candidate.IsPlayable)
            .FirstOrDefault(candidate => SamePool(candidate.CardPool, pool)
                || SamePool(candidate.PotionPool, pool)
                || SamePool(candidate.RelicPool, pool));

        if (character is not null)
            return character.Title.GetFormattedText();

        if (pool is CardPoolModel cardPool && !string.IsNullOrWhiteSpace(cardPool.Title))
        {
            return cardPool switch
            {
                ColorlessCardPool => CommonLoc.Colorless,
                CurseCardPool => CommonLoc.Curse,
                EventCardPool => CommonLoc.Event,
                QuestCardPool => CommonLoc.Quest,
                StatusCardPool => CommonLoc.Status,
                TokenCardPool => CommonLoc.Token,
                _ => cardPool.Title
            };
        }

        string typeName = pool.GetType().Name;
        if (typeName.StartsWith("Shared", StringComparison.Ordinal))
            return LocMan.Loc("SHARED", "Shared");

        if (typeName.StartsWith("Colorless", StringComparison.Ordinal))
            return LocMan.Loc("COLORLESS", "Colorless");

        if (typeName.StartsWith("Event", StringComparison.Ordinal))
            return LocMan.Loc("EVENT", "Event");

        return PrettifyPoolTypeName(typeName);
    }

    public static string PoolFilterId(string prefix, AbstractModel pool)
    {
        return $"{prefix}_pool_{Regex.Replace(PoolKey(pool).ToLowerInvariant(), "[^a-z0-9_]+", "_")}";
    }

    private static string PoolKey(AbstractModel pool)
    {
        return $"{pool.GetType().FullName}:{pool.Id}";
    }

    public static bool SamePool(AbstractModel left, AbstractModel right)
    {
        return string.Equals(PoolKey(left), PoolKey(right), StringComparison.Ordinal);
    }

    public static bool IsSharedPool(AbstractModel pool)
    {
        string typeName = pool.GetType().Name;
        return typeName.StartsWith("Shared", StringComparison.Ordinal)
               || typeName.StartsWith("Event", StringComparison.Ordinal)
               || typeName.StartsWith("Colorless", StringComparison.Ordinal);
    }

    public static bool IsInternalPool(AbstractModel pool)
    {
        string typeName = pool.GetType().Name;
        return typeName.StartsWith("Deprecated", StringComparison.Ordinal)
               || typeName.StartsWith("Mock", StringComparison.Ordinal)
               || typeName.StartsWith("Fallback", StringComparison.Ordinal);
    }

    public static string PrettifyPoolTypeName(string typeName)
    {
        string name = typeName
            .Replace("CardPool", string.Empty, StringComparison.Ordinal)
            .Replace("PotionPool", string.Empty, StringComparison.Ordinal)
            .Replace("RelicPool", string.Empty, StringComparison.Ordinal);

        return Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
    }

    public static Texture2D TryGetValidTexture(Texture2D texture)
    {
        if (texture is null)
            return null;

        try
        {
            if (!GodotObject.IsInstanceValid(texture))
                return null;

            _ = texture.GetRid();
            return texture;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public static Texture2D LoadActionButtonIcon(string fileName)
    {
        string[] paths =
        [
            $"res://Loadout/images/relics/default/{fileName}",
            $"res://Loadout/images/relics/xggg/{fileName}",
            $"res://Loadout/images/relics/legacy/{fileName}",
            $"res://Loadout/images/relics/isaac/{fileName}"
        ];

        foreach (string path in paths)
        {
            if (ResourceLoader.Exists(path))
                return GD.Load<Texture2D>(path);
        }

        return null;
    }

    public static void AddEnumFilters<TModel, TEnum>(
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

            string label = GetEnumLabel(value);
            builder.Filter(EnumFilterId(typeof(TEnum).Name, value), label, model => EqualityComparer<TEnum>.Default.Equals(getValue(model), value), groupId);
        }
    }

    public static string GetModelModId(AbstractModel model)
    {
        EnsureModLookup();

        Assembly assembly = model.GetType().Assembly;
        if (_modIdsByAssembly.TryGetValue(assembly, out string modId))
            return modId;

        EnsureModLookup(forceRefresh: true);
        return _modIdsByAssembly.TryGetValue(assembly, out modId)
            ? modId
            : BaseGameModId;
    }

    public static string GetModName(string modId)
    {
        if (string.Equals(modId, BaseGameModId, StringComparison.Ordinal))
            return BaseGameModName;

        EnsureModLookup();
        if (_modNamesById.TryGetValue(modId, out string modName))
            return modName;

        EnsureModLookup(forceRefresh: true);
        return _modNamesById.TryGetValue(modId, out modName)
            ? modName
            : modId;
    }

    public static void AddModFilters<TModel>(SelectScreenBuilder<TModel> builder, IEnumerable<TModel> models)
        where TModel : AbstractModel
    {
        IReadOnlyList<string> modIds = models
            .Select(GetModelModId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(modId => string.Equals(modId, BaseGameModId, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(GetModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(modId => modId, StringComparer.Ordinal)
            .ToList();

        if (modIds.Count == 0)
            return;

        builder.FilterGroup("mods", LocMan.Loc("FILTER_GROUP_MODS", "Mods"));
        foreach (string modId in modIds)
        {
            string localModId = modId;
            builder.Filter(ModFilterId(localModId), GetModName(localModId), model => string.Equals(GetModelModId(model), localModId, StringComparison.Ordinal), "mods");
        }
    }

    private static string ModFilterId(string modId)
    {
        return $"mod_{Regex.Replace(modId.ToLowerInvariant(), "[^a-z0-9_]+", "_")}";
    }

    private static void EnsureModLookup(bool forceRefresh = false)
    {
        if (!forceRefresh && _modIdsByAssembly is not null && _modNamesById is not null)
            return;

        _modIdsByAssembly = new Dictionary<Assembly, string>();
        _modNamesById = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BaseGameModId] = BaseGameModName
        };

        try
        {
            foreach (Mod mod in ModManager.GetLoadedMods())
            {
                string modId = mod.manifest?.id;
                if (string.IsNullOrWhiteSpace(modId))
                    continue;

                _modNamesById[modId] = string.IsNullOrWhiteSpace(mod.manifest?.name)
                    ? modId
                    : mod.manifest.name;

                if (mod.assembly is not null)
                    _modIdsByAssembly[mod.assembly] = modId;
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not resolve loaded mod manifests for filters. {exception.Message}");
        }
    }

    private static string GetEnumLabel<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        try
        {
            return value switch
            {
                PotionRarity potionRarity => potionRarity.ToLocString().GetFormattedText(),
                RelicRarity relicRarity => LocMan.GameLoc("gameplay_ui", $"RELIC_RARITY.{relicRarity.ToString().ToUpperInvariant()}", PrettifyEnumValue(relicRarity)),
                _ => PrettifyEnumValue(value)
            };
        }
        catch
        {
            return PrettifyEnumValue(value);
        }
    }

    public static string EnumFilterId<TEnum>(string prefix, TEnum value)
        where TEnum : struct, Enum
    {
        string raw = $"{prefix}_{value}_{Convert.ToInt64(value)}";
        return Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]+", "_");
    }

    public static string PrettifyEnumValue<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2");
    }

    public static Font LoadGameFont()
    {
        const string localPath = "res://Loadout/themes/default/kreon_bold_glyph_space_one.tres";
        if (ResourceLoader.Exists(localPath))
            return GD.Load<Font>(localPath);

        return GD.Load<Font>("res://themes/kreon_bold_glyph_space_one.tres");
    }

    public static Font LoadGameFont(string path)
    {
        if (ResourceLoader.Exists(path))
            return GD.Load<Font>(path);

        return LoadGameFont();
    }

    public static string MakeSafeNodeName(string value)
    {
        string safeName = Regex.Replace(value, @"[^A-Za-z0-9_]", "_");
        return string.IsNullOrWhiteSpace(safeName) ? "GeneratedControl" : safeName;
    }

    public static bool ModelIdMatches(AbstractModel model, string id)
    {
        return string.Equals(model.Id.ToString(), id, StringComparison.Ordinal)
               || string.Equals(model.Id.Entry, id, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class LastActionCaptureSession
    {
        private readonly string _itemKey;
        private readonly List<LastActionEntry> _entries = new();
        private bool _committed;

        public LastActionCaptureSession(string itemKey)
        {
            _itemKey = itemKey;
        }

        public void Add(IReadOnlyList<LastActionEntry> entries)
        {
            _entries.AddRange(entries);
            if (_committed)
                Commit();
        }

        public void Commit()
        {
            _committed = true;
            if (_entries.Count > 0)
                LastActionService.SaveAction(_itemKey, _entries);
        }
    }

    private static async Task HandleStaticItemActivatedAsync(
        NGenericSelectScreen screen,
        IGenericSelectItem selectItem,
        CommonHelpers.SelectActivationHandler onActivated,
        Action<IReadOnlyList<LastActionEntry>> recordLastActions,
        Action clearActivation)
    {
        try
        {
            IReadOnlyList<LastActionEntry> entries = await onActivated(screen, selectItem);
            if (entries.Count > 0)
                recordLastActions(entries);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutPanel: item activation failed for '{selectItem.Id}' ({selectItem.Name}): {exception}");
        }
        finally
        {
            DeferActivationClear(clearActivation);
        }
    }

    public static string FormatPotionTitle(PotionModel potion)
    {
        return potion.Title.GetFormattedText();
    }

    public static string FormatRelicTitle(RelicModel relic)
    {
        return relic.Title.GetFormattedText();
    }

    public static string FormatEventTitle(EventModel eventModel)
    {
        return eventModel.Title.GetFormattedText();
    }

    public static string StripUiMarkup(string text)
    {
        string withoutBbcode = Regex.Replace(text, @"\[[^\]]+\]", " ");
        string withoutTags = Regex.Replace(withoutBbcode, @"<[^>]+>", " ");
        return Regex.Replace(withoutTags, @"\s+", " ").Trim();
    }

    public static string FormatPowerTitle(PowerModel power)
    {
        return power.Title.GetFormattedText();
    }

    public static Button CreateModelButton(Vector2 size)
    {
        Button button = new()
        {
            CustomMinimumSize = size,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            Text = string.Empty
        };
        button.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        return button;
    }

    public static MegaLabel CreateButtonLabel(
        string name,
        string text,
        Vector2 position,
        Vector2 size,
        int fontSize,
        HorizontalAlignment horizontalAlignment,
        Color color)
    {
        MegaLabel label = new()
        {
            Name = name,
            Text = text,
            AutoSizeEnabled = false,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = position,
            Size = size
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.5f));
        label.AddThemeConstantOverride("shadow_offset_x", 3);
        label.AddThemeConstantOverride("shadow_offset_y", 2);
        return label;
    }

    public static void AttachHoverTips(Control owner, IEnumerable<IHoverTip> hoverTips)
    {
        IReadOnlyList<IHoverTip> tips = hoverTips.Where(tip => tip is not null).ToList();
        if (tips.Count == 0)
            return;

        owner.MouseEntered += () => ShowHoverTips(owner, tips);
        owner.MouseExited += () => NHoverTipSet.Remove(owner);
        owner.TreeExiting += () => NHoverTipSet.Remove(owner);
    }

    private static void ShowHoverTips(Control owner, IReadOnlyList<IHoverTip> tips)
    {
        NHoverTipSet.Remove(owner);
        NHoverTipSet.CreateAndShow(owner, tips, HoverTip.GetHoverTipAlignment(owner))?.SetFollowOwner();
        NLoadoutPanelRoot.Instance?.AdoptGameHoverTips();
    }

    public static Button CreateTextModelGridItem(
        AbstractModel model,
        string title,
        string subtitle,
        string category,
        Texture2D icon = null,
        Vector2? itemSize = null)
    {
        Vector2 size = itemSize ?? new Vector2(220f, icon is null ? 120f : 148f);
        Button button = new()
        {
            CustomMinimumSize = size,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
        };
        button.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", StsColors.cream);
        button.Text = icon is null
            ? $"{title}\n{category}\n{subtitle}"
            : $"{title}\n{category}";

        if (icon is not null)
        {
            TextureRect iconRect = new()
            {
                Texture = icon,
                CustomMinimumSize = new Vector2(64f, 64f),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = new Vector2(Mathf.Max(0f, (size.X - 64f) * 0.5f), Mathf.Max(0f, size.Y - 70f)),
                Size = new Vector2(64f, 64f)
            };
            button.AddChild(iconRect);
        }

        return button;
    }

    public static object GetCurrentDynamicRunStateIdentity()
    {
        try
        {
            return RunManager.Instance.IsInProgress
                ? RunManager.Instance.DebugOnlyGetState()
                : null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not resolve current run state. {exception.Message}");
            return null;
        }
    }

    public static string RuntimeItemId(AbstractModel model)
    {
        return $"{model.Id}:{RuntimeHelpers.GetHashCode(model)}";
    }

    public static Panel CreateFavoriteGlow(Vector2 size, bool visible)
    {
        StyleBoxFlat style = new()
        {
            BgColor = new Color(1f, 0.78f, 0.08f, 0.09f),
            BorderColor = new Color(1f, 0.82f, 0.08f, 0.92f),
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3
        };

        Panel panel = new()
        {
            Name = "FavoriteGlow",
            Visible = visible,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(-4f, -4f),
            Size = size + new Vector2(8f, 8f),
            CustomMinimumSize = size + new Vector2(8f, 8f)
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    public static void PlayCardSmithFeedback(Control view)
    {
        if (!GodotObject.IsInstanceValid(view) || !CommonHelpers.TryFindDescendantOrSelf(view, out NGridCardHolder holder))
            return;

        if (holder!.CardNode is not NCard cardNode || !GodotObject.IsInstanceValid(cardNode))
            return;

        NCardSmithVfx smithVfx = NCardSmithVfx.Create(cardNode);
        if (smithVfx is null)
            return;

        Node host = NLoadoutPanelRoot.Instance?.HoverTipLayer;
        if (host is null)
        {
            smithVfx.QueueFree();
            return;
        }

        host.AddChildSafely(smithVfx);
    }

    public static void AddFavoritesModeDropdown(
        NGenericSelectScreen screen,
        string name,
        Func<bool> getFavoritesOnly,
        Action<bool> setFavoritesOnly)
    {
        NLoadoutDropdown favoritesDropdown = new()
        {
            Name = name,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(256f, 52f)
        };
        favoritesDropdown.SetItems(LocMan.Loc("FILTER_GROUP_FAVORITES", "Favorites"),
            [
                new LoadoutDropdownOption(FavoriteModeAllKey, LocMan.GameLoc("main_menu_ui", "CARD_LIBRARY_RARITY_ALL",LocMan.Loc("ALL","All"))),
                new LoadoutDropdownOption(FavoriteModeFavoritesKey, LocMan.Loc("FAVORITES_ONLY", "Favorites"))
            ],
            getFavoritesOnly() ? FavoriteModeFavoritesKey : FavoriteModeAllKey);
        favoritesDropdown.SelectedItemChanged += selectedId =>
        {
            setFavoritesOnly(selectedId == FavoriteModeFavoritesKey);
            screen.RefreshNow(resetScroll: true);
        };

        screen.AddCustomSidebarControl(favoritesDropdown);
    }

    public static string OwnedItemId<TModel>(LoadoutOwnedItem<TModel> item)
        where TModel : AbstractModel
    {
        return $"{item.OwnerNetId}:{item.Model.Id}:{RuntimeHelpers.GetHashCode(item.Model)}";
    }

    private static async Task HandleDynamicItemActivatedAsync(
        NGenericSelectScreen screen,
        IGenericSelectItem selectItem,
        CommonHelpers.SelectActivationHandler onActivated,
		Action<NGenericSelectScreen, IGenericSelectItem> refresh,
		Action clearActivation)
    {
        try
        {
            await onActivated(screen, selectItem);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutPanel: dynamic item activation failed for '{selectItem.Id}' ({selectItem.Name}): {exception}");
        }
        finally
        {
            try
            {
                refresh(screen, selectItem);
            }
            finally
            {
                DeferActivationClear(clearActivation);
            }
        }
    }

    private static void DeferActivationClear(Action clearActivation)
    {
        // NCardHolder emits its Pressed signal through CallDeferred. Keeping this
        // gate closed until the next idle turn prevents multiple stale pooled
        // callbacks from turning one physical click into several mutations.
        Callable.From(clearActivation).CallDeferred();
    }

    public delegate Task<IReadOnlyList<LastActionEntry>> SelectActivationHandler(NGenericSelectScreen screen, IGenericSelectItem selectItem);
}
