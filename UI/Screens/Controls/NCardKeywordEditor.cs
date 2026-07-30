#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BaseLib.Patches.Content;
using Godot;
using Loadout.Keywords;
using Loadout.PanelItems;
using Loadout.UI.Managers;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

public partial class NCardKeywordEditor : VBoxContainer
{
    public const string AllModFilterId = "__all_keyword_mods__";

    private const string BaseGameModId = "slaythespire2";
    private const string BaseLibModId = "BaseLib";
    private const string OtherModId = "__other_keyword_mod__";
    private const float ContentWidth = 426f;
    private const float ToggleHeight = 44f;
    private const float RowSeparation = 2f;
    private const float ScrollbarWidth = 48f;
    private const float ScrollbarEndCapSize = 48f;
    private const float GroupHeaderHeight = 36f;
    private const float HeaderGridGap = 2f;
    private const float GroupSeparation = 10f;
    private const int Columns = 2;
    private const int VisibleRows = 7;

    private sealed record CatalogEntry(
        CardKeyword Keyword,
        string Label,
        string ModId,
        string ModName);

    private sealed record ContentBlock(
        string? Header,
        IReadOnlyList<CatalogEntry> Entries);

    private IReadOnlyList<CardModel> _contextCards = [];
    private Func<CardKeyword, bool> _isChecked = _ => false;
    private Action<CardKeyword, bool> _onChanged = (_, _) => { };
    private Action<string>? _onSelectedModChanged;
    private string _selectedModId = AllModFilterId;

    public void Init(
        IReadOnlyList<CardModel> contextCards,
        Func<CardKeyword, bool> isChecked,
        Action<CardKeyword, bool> onChanged,
        string selectedModId = AllModFilterId,
        Action<string>? onSelectedModChanged = null)
    {
        _contextCards = contextCards;
        _isChecked = isChecked;
        _onChanged = onChanged;
        _selectedModId = selectedModId;
        _onSelectedModChanged = onSelectedModChanged;
        if (IsNodeReady())
            Rebuild();
    }

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        MouseFilter = MouseFilterEnum.Ignore;
        AddThemeConstantOverride("separation", 4);
        Rebuild();
    }

    private void Rebuild()
    {
        ClearChildren(this);
        AddChild(CreateSectionLabel(LocMan.Loc("FILTER_GROUP_KEYWORD", "Keyword")));

        IReadOnlyList<CatalogEntry> catalog = BuildCatalog();
        IReadOnlyList<IGrouping<string, CatalogEntry>> sources =
            GetOrderedSources(catalog);
        HashSet<string> availableSourceIds = sources
            .Select(source => source.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (_selectedModId != AllModFilterId
            && !availableSourceIds.Contains(_selectedModId))
        {
            _selectedModId = AllModFilterId;
        }

        List<LoadoutDropdownOption> filterOptions =
        [
            new LoadoutDropdownOption(
                AllModFilterId,
                SelectScreenLoc.Text("ALL", "All"))
        ];
        filterOptions.AddRange(sources.Select(source =>
        {
            CatalogEntry first = source.First();
            return new LoadoutDropdownOption(source.Key, first.ModName);
        }));

        NSelectFilterDropdown modFilter = new()
        {
            Name = "KeywordModFilter",
            CustomMinimumSize = new Vector2(ContentWidth, 52f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            DropdownWidth = 420f
        };
        modFilter.SetItems(
            LocMan.Loc("FILTER_GROUP_MODS", "Mods"),
            filterOptions,
            _selectedModId);
        AddChild(modFilter);

        VBoxContainer contentHost = new()
        {
            Name = "KeywordContentHost",
            CustomMinimumSize = new Vector2(ContentWidth, 0f),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(contentHost);
        RebuildContent(contentHost, catalog);

        modFilter.SelectedItemChanged += selectedId =>
        {
            if (string.Equals(_selectedModId, selectedId, StringComparison.Ordinal))
                return;

            _selectedModId = availableSourceIds.Contains(selectedId)
                ? selectedId
                : AllModFilterId;
            _onSelectedModChanged?.Invoke(_selectedModId);
            RebuildContent(contentHost, catalog);
        };
    }

    private void RebuildContent(
        VBoxContainer contentHost,
        IReadOnlyList<CatalogEntry> catalog)
    {
        if (!GodotObject.IsInstanceValid(contentHost))
            return;

        ClearChildren(contentHost);
        IReadOnlyList<ContentBlock> blocks = BuildContentBlocks(catalog);
        float contentHeight = GetContentHeight(blocks);
        float maximumVisibleHeight = GetGridHeight(VisibleRows);
        bool needsScrolling = contentHeight > maximumVisibleHeight;
        float visibleHeight = Math.Min(contentHeight, maximumVisibleHeight);
        float contentWidth = needsScrolling
            ? ContentWidth - ScrollbarWidth
            : ContentWidth;

        VBoxContainer content = CreateContent(blocks, contentWidth);
        content.CustomMinimumSize = new Vector2(contentWidth, contentHeight);
        if (!needsScrolling)
        {
            contentHost.AddChild(content);
            return;
        }

        NScrollableContainer scroll = new()
        {
            Name = "KeywordScroll",
            CustomMinimumSize = new Vector2(ContentWidth, visibleHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Stop
        };
        Control mask = new()
        {
            Name = "Mask",
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        mask.SetAnchorsPreset(LayoutPreset.FullRect);
        mask.OffsetRight = -ScrollbarWidth;
        scroll.AddChild(mask);

        content.Name = "Content";
        content.SetAnchorsPreset(LayoutPreset.TopWide);
        mask.AddChild(content);

        NScrollbar scrollbar = CreateGameScrollbar();
        scrollbar.Name = "Scrollbar";
        scrollbar.CustomMinimumSize = new Vector2(ScrollbarWidth, 0f);
        scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
        scrollbar.OffsetLeft = -ScrollbarWidth;
        scrollbar.OffsetTop = ScrollbarEndCapSize;
        scrollbar.OffsetBottom = -ScrollbarEndCapSize;
        scroll.AddChild(scrollbar);
        scroll.DisableScrollingIfContentFits();
        contentHost.AddChild(scroll);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(scroll)
                && GodotObject.IsInstanceValid(content))
            {
                scroll.SetContent(content);
            }
        }).CallDeferred();
    }

    private IReadOnlyList<ContentBlock> BuildContentBlocks(
        IReadOnlyList<CatalogEntry> catalog)
    {
        if (_selectedModId != AllModFilterId)
        {
            IReadOnlyList<CatalogEntry> filtered = catalog
                .Where(entry => string.Equals(
                    entry.ModId,
                    _selectedModId,
                    StringComparison.Ordinal))
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => Convert.ToInt32(entry.Keyword))
                .ToList();
            return filtered.Count == 0
                ? []
                : [new ContentBlock(null, filtered)];
        }

        List<ContentBlock> blocks = [];
        IReadOnlyList<CatalogEntry> core = catalog
            .Where(entry => IsCoreSource(entry.ModId))
            .OrderBy(entry => GetSourceRank(entry.ModId))
            .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => Convert.ToInt32(entry.Keyword))
            .ToList();
        if (core.Count > 0)
            blocks.Add(new ContentBlock(null, core));

        foreach (IGrouping<string, CatalogEntry> source in GetOrderedSources(catalog)
                     .Where(source => !IsCoreSource(source.Key)))
        {
            IReadOnlyList<CatalogEntry> entries = source
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => Convert.ToInt32(entry.Keyword))
                .ToList();
            blocks.Add(new ContentBlock(source.First().ModName, entries));
        }
        return blocks;
    }

    private VBoxContainer CreateContent(
        IReadOnlyList<ContentBlock> blocks,
        float contentWidth)
    {
        VBoxContainer content = new()
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        content.AddThemeConstantOverride("separation", (int)GroupSeparation);
        foreach (ContentBlock block in blocks)
        {
            if (block.Header is null)
            {
                content.AddChild(CreateGrid(block.Entries, contentWidth));
                continue;
            }

            VBoxContainer group = new()
            {
                CustomMinimumSize = new Vector2(
                    contentWidth,
                    GetBlockHeight(block)),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Ignore
            };
            group.AddThemeConstantOverride("separation", (int)HeaderGridGap);
            group.AddChild(CreateGroupHeader(block.Header));
            group.AddChild(CreateGrid(block.Entries, contentWidth));
            content.AddChild(group);
        }
        return content;
    }

    private GridContainer CreateGrid(
        IReadOnlyList<CatalogEntry> entries,
        float gridWidth)
    {
        int rowCount = (entries.Count + Columns - 1) / Columns;
        float toggleWidth = (gridWidth - 8f) / Columns;
        GridContainer grid = new()
        {
            Columns = Columns,
            CustomMinimumSize = new Vector2(
                gridWidth,
                GetGridHeight(rowCount)),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", (int)RowSeparation);
        foreach (CatalogEntry entry in entries)
        {
            CardKeyword keyword = entry.Keyword;
            string key = LoadoutKeywords.GetStorageKey(keyword);
            NLoadoutToggle toggle = new()
            {
                CustomMinimumSize = new Vector2(toggleWidth, ToggleHeight),
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin
            };
            toggle.SetHoverTipsFactory(() => GetKeywordHoverTips(keyword));
            toggle.Init($"keyword_{key}", entry.Label, _isChecked(keyword));
            toggle.Toggled += changed =>
                _onChanged(keyword, changed.IsChecked);
            grid.AddChild(toggle);
        }
        return grid;
    }

    private IReadOnlyList<CatalogEntry> BuildCatalog()
    {
        HashSet<CardKeyword> nativeKeywords = Enum.GetValues<CardKeyword>()
            .Where(keyword => keyword != CardKeyword.None)
            .ToHashSet();
        HashSet<CardKeyword> loadoutKeywords = LoadoutKeywords.All
            .Where(keyword => keyword != CardKeyword.None)
            .ToHashSet();
        HashSet<CardKeyword> availableKeywords = new(nativeKeywords);
        availableKeywords.UnionWith(loadoutKeywords);
        IReadOnlyDictionary<Assembly, string> modIdsByAssembly =
            CommonHelpers.GetLoadedModIdsByAssembly();

        try
        {
            foreach (int rawKeyword in CustomKeywords.KeywordIDs.Keys.ToList())
                availableKeywords.Add((CardKeyword)rawKeyword);
        }
        catch (Exception exception)
        {
            GD.PushWarning(
                $"CardModification: failed to read BaseLib's registered keyword catalog. {exception.Message}");
        }

        Dictionary<CardKeyword, HashSet<string>> usageOwners = [];
        foreach (CardModel model in ModelDb.AllCards)
            AddCardKeywordUsage(model, modIdsByAssembly, availableKeywords, usageOwners);
        foreach (CardModel model in _contextCards)
            AddCardKeywordUsage(model, modIdsByAssembly, availableKeywords, usageOwners);

        return availableKeywords
            .Where(keyword => keyword != CardKeyword.None)
            .Select(keyword =>
            {
                string modId = ResolveModId(
                    keyword,
                    nativeKeywords,
                    loadoutKeywords,
                    usageOwners);
                string modName = string.Equals(
                    modId,
                    OtherModId,
                    StringComparison.Ordinal)
                    ? LocMan.Loc("OTHER", "Other")
                    : CommonHelpers.GetModName(modId);
                return new CatalogEntry(
                    keyword,
                    GetKeywordLabel(keyword),
                    modId,
                    modName);
            })
            .ToList();
    }

    private static void AddCardKeywordUsage(
        CardModel card,
        IReadOnlyDictionary<Assembly, string> modIdsByAssembly,
        ISet<CardKeyword> availableKeywords,
        IDictionary<CardKeyword, HashSet<string>> usageOwners)
    {
        Assembly assembly = card.GetType().Assembly;
        string? modId = assembly == typeof(CardModel).Assembly
            ? BaseGameModId
            : modIdsByAssembly.GetValueOrDefault(assembly);
        foreach (CardKeyword keyword in GetKeywordsSafely(card))
        {
            if (keyword == CardKeyword.None)
                continue;
            availableKeywords.Add(keyword);
            if (string.IsNullOrWhiteSpace(modId))
                continue;
            if (!usageOwners.TryGetValue(keyword, out HashSet<string>? owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                usageOwners[keyword] = owners;
            }
            owners.Add(modId);
        }
    }

    private static string ResolveModId(
        CardKeyword keyword,
        IReadOnlySet<CardKeyword> nativeKeywords,
        IReadOnlySet<CardKeyword> loadoutKeywords,
        IReadOnlyDictionary<CardKeyword, HashSet<string>> usageOwners)
    {
        if (nativeKeywords.Contains(keyword))
            return BaseGameModId;
        if (loadoutKeywords.Contains(keyword))
            return MainFile.ModId;
        return usageOwners.TryGetValue(keyword, out HashSet<string>? owners)
               && owners.Count == 1
            ? owners.First()
            : OtherModId;
    }

    private static IReadOnlyList<IGrouping<string, CatalogEntry>>
        GetOrderedSources(IReadOnlyList<CatalogEntry> catalog)
    {
        return catalog
            .GroupBy(entry => entry.ModId, StringComparer.Ordinal)
            .OrderBy(source => GetSourceRank(source.Key))
            .ThenBy(
                source => GetSourceRank(source.Key) == 3
                    ? source.First().ModName
                    : string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCoreSource(string modId)
    {
        return string.Equals(modId, BaseGameModId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(modId, BaseLibModId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(modId, MainFile.ModId, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSourceRank(string modId)
    {
        if (string.Equals(modId, BaseGameModId, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (string.Equals(modId, BaseLibModId, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (string.Equals(modId, MainFile.ModId, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (string.Equals(modId, OtherModId, StringComparison.Ordinal))
            return 4;
        return 3;
    }

    private static float GetContentHeight(IReadOnlyList<ContentBlock> blocks)
    {
        return blocks.Count == 0
            ? 0f
            : blocks.Sum(GetBlockHeight)
              + ((blocks.Count - 1) * GroupSeparation);
    }

    private static float GetBlockHeight(ContentBlock block)
    {
        int rows = (block.Entries.Count + Columns - 1) / Columns;
        float height = GetGridHeight(rows);
        if (block.Header is not null)
            height += GroupHeaderHeight + HeaderGridGap;
        return height;
    }

    private static float GetGridHeight(int rows)
    {
        return rows <= 0
            ? 0f
            : (rows * ToggleHeight) + ((rows - 1) * RowSeparation);
    }

    private static NScrollbar CreateGameScrollbar()
    {
        NScrollbar scrollbar = new()
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            MouseFilter = MouseFilterEnum.Stop
        };
        TextureRect trackBody = new()
        {
            Name = "TrackBody",
            Modulate = new Color(0.164706f, 0.290196f, 0.321569f, 1f),
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_track_center.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackBody.SetAnchorsPreset(LayoutPreset.FullRect);
        scrollbar.AddChild(trackBody);

        TextureRect trackTop = new()
        {
            Name = "TrackTop",
            Modulate = trackBody.Modulate,
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_track_edge2.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackTop.SetAnchorsPreset(LayoutPreset.TopWide);
        trackTop.OffsetTop = -ScrollbarEndCapSize;
        scrollbar.AddChild(trackTop);

        TextureRect trackBottom = new()
        {
            Name = "TrackBot",
            Modulate = trackBody.Modulate,
            Texture = trackTop.Texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            FlipV = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        trackBottom.SetAnchorsPreset(LayoutPreset.BottomWide);
        trackBottom.OffsetBottom = ScrollbarEndCapSize;
        scrollbar.AddChild(trackBottom);

        TextureRect handle = new()
        {
            Name = "Handle",
            UniqueNameInOwner = true,
            Texture = LoadTexture(
                "res://images/atlases/ui_atlas.sprites/scrollbar_train_large.tres"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            PivotOffset = new Vector2(36f, 36f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        handle.Position = new Vector2(-12f, -36f);
        handle.Size = new Vector2(72f, 72f);
        scrollbar.AddChild(handle);
        AssignOwnerRecursive(scrollbar, scrollbar);
        return scrollbar;
    }

    private static Texture2D? LoadTexture(string path)
    {
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private static MegaLabel CreateSectionLabel(string text)
    {
        MegaLabel label = CreateLabel(text, 25, StsColors.gold);
        label.CustomMinimumSize = new Vector2(0f, 42f);
        return label;
    }

    private static MegaLabel CreateGroupHeader(string text)
    {
        MegaLabel label = CreateLabel(text, 22, StsColors.gold);
        label.CustomMinimumSize = new Vector2(0f, GroupHeaderHeight);
        return label;
    }

    private static MegaLabel CreateLabel(string text, int size, Color color)
    {
        MegaLabel label = new()
        {
            Text = text,
            AutoSizeEnabled = false,
            MinFontSize = Math.Max(12, size - 8),
            MaxFontSize = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private static string GetKeywordLabel(CardKeyword keyword)
    {
        try
        {
            string label = CardPrinter.GetCardKeywordLabel(keyword);
            return string.IsNullOrWhiteSpace(label)
                ? keyword.ToString()
                : label;
        }
        catch
        {
            return keyword.ToString();
        }
    }

    private static IEnumerable<CardKeyword> GetKeywordsSafely(CardModel card)
    {
        try
        {
            return card.GetKeywordsWithSources(KeywordSources.Local);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<IHoverTip> GetKeywordHoverTips(
        CardKeyword keyword)
    {
        if (LoadoutKeywordRegistry.IsDescriptionKeyword(keyword))
            return [];
        try
        {
            return [HoverTipFactory.FromKeyword(keyword)];
        }
        catch
        {
            return [];
        }
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static void AssignOwnerRecursive(Node root, Node owner)
    {
        foreach (Node child in root.GetChildren())
        {
            child.Owner = owner;
            AssignOwnerRecursive(child, owner);
        }
    }
}
