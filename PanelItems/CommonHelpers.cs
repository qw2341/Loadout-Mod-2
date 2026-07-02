using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.LastActions;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.PotionLab;
using MegaCrit.Sts2.Core.Runs;

namespace Loadout.PanelItems;

public class CommonHelpers
{
    public static void CreateAndAddLoadoutItem<TModel>(
        IEnumerable<TModel> models,
        SelectItemAdapter<TModel> adapter,
        Action<SelectScreenBuilder<TModel>> builder,
        Action<NGenericSelectScreen> beforeOpen,
        string textureFileName,
        string title,
        string description,
        NLoadoutPanel.SelectActivationHandler onActivated = null,
        string lastActionItemKey = null,
        Func<Task> replayQuickAction = null)
    {
        var item = new NLoadoutPanelItem(textureFileName, title, description);
        var scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
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
            captureSession = null;
        };
        if (onActivated is not null)
        {
            screen.ItemActivated += (selectItem, state) =>
            {
                if (activationInFlight)
                    return;

                activationInFlight = true;
                _ = HandleStaticItemActivatedAsync(
                    screen,
                    selectItem,
                    onActivated,
                    entries => captureSession?.Add(entries),
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

    public static bool BindGuiReleaseActivation(Control view, Action activate)
    {
        Control control = TryFindDescendantOrSelf(view, out NLabPotionHolder potionHolder)
            ? potionHolder!
            : view;

        control.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
                activate();
        };

        return true;
    }

    public static bool TryFindDescendantOrSelf<TControl>(Node root, out TControl control)
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
            return cardPool.Title;

        string typeName = pool.GetType().Name;
        if (typeName.StartsWith("Shared", StringComparison.Ordinal))
            return LocMan.SScreenLoc("POOL_SHARED", "Shared");

        if (typeName.StartsWith("Colorless", StringComparison.Ordinal))
            return LocMan.SScreenLoc("POOL_COLORLESS", "Colorless");

        if (typeName.StartsWith("Event", StringComparison.Ordinal))
            return LocMan.SScreenLoc("POOL_EVENT", "Event");

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

        public LastActionCaptureSession(string itemKey)
        {
            _itemKey = itemKey;
        }

        public void Add(IReadOnlyList<LastActionEntry> entries)
        {
            _entries.AddRange(entries);
        }

        public void Commit()
        {
            if (_entries.Count > 0)
                LastActionService.SaveAction(_itemKey, _entries);
        }
    }

    private static async Task HandleStaticItemActivatedAsync(
        NGenericSelectScreen screen,
        IGenericSelectItem selectItem,
        NLoadoutPanel.SelectActivationHandler onActivated,
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
            clearActivation();
        }
    }
}