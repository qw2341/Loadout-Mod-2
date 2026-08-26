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
    private const float GroupHeaderHeight = 36f;
    private const float HeaderGridGap = 2f;
    private const float GroupSeparation = 10f;
    private const int Columns = 2;
    private const int VisibleRows = 7;

    private sealed record CatalogEntry(
        CardKeyword Keyword,
        string Label,
        string ModId,
        string ModName,
        LoadoutKeywordEditorSection EditorSection,
        int EditorOrder,
        LoadoutKeywordEditorControlKind ControlKind);

    private sealed record ContentBlock(
        string? Header,
        IReadOnlyList<CatalogEntry> Entries);

    private IReadOnlyList<CardModel> _contextCards = [];
    private Func<CardKeyword, bool> _isChecked = _ => false;
    private Action<CardKeyword, bool> _onChanged = (_, _) => { };
    private Action<string>? _onSelectedModChanged;
    private Func<CardKeyword, int> _getRepeatCount = _ => 0;
    private Action<CardKeyword> _onRepeatAdded = _ => { };
    private Action<CardKeyword> _onRepeatRemoved = _ => { };
    private string _selectedModId = AllModFilterId;

    public void Init(
        IReadOnlyList<CardModel> contextCards,
        Func<CardKeyword, bool> isChecked,
        Action<CardKeyword, bool> onChanged,
        string selectedModId = AllModFilterId,
        Action<string>? onSelectedModChanged = null,
        Func<CardKeyword, int>? getRepeatCount = null,
        Action<CardKeyword>? onRepeatAdded = null,
        Action<CardKeyword>? onRepeatRemoved = null)
    {
        _contextCards = contextCards;
        _isChecked = isChecked;
        _onChanged = onChanged;
        _selectedModId = selectedModId;
        _onSelectedModChanged = onSelectedModChanged;
        _getRepeatCount = getRepeatCount ?? (_ => 0);
        _onRepeatAdded = onRepeatAdded ?? (_ => { });
        _onRepeatRemoved = onRepeatRemoved ?? (_ => { });
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
        NScrollableContainer? preservedScroll =
            GetNodeOrNull<NScrollableContainer>(
                "KeywordContentHost/KeywordScroll");
        if (preservedScroll is not null
            && GodotObject.IsInstanceValid(preservedScroll))
        {
            preservedScroll.GetParent()?.RemoveChild(preservedScroll);
        }

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
        RebuildContent(contentHost, catalog, preservedScroll);

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
        IReadOnlyList<CatalogEntry> catalog,
        NScrollableContainer? preservedScroll = null)
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
            ? ContentWidth - NLoadoutNativeScrollbar.Width
            : ContentWidth;

        VBoxContainer content = CreateContent(blocks, contentWidth);
        content.CustomMinimumSize = new Vector2(contentWidth, contentHeight);
        if (!needsScrolling)
        {
            if (preservedScroll is not null
                && GodotObject.IsInstanceValid(preservedScroll))
            {
                preservedScroll.QueueFree();
            }
            contentHost.AddChild(content);
            return;
        }

        NScrollableContainer scroll;
        Control mask;
        float preservedContentY = 0f;
        if (preservedScroll is not null
            && GodotObject.IsInstanceValid(preservedScroll))
        {
            scroll = preservedScroll;
            mask = scroll.GetNode<Control>("Mask");
            preservedContentY = mask
                .GetNodeOrNull<Control>("Content")?
                .Position.Y ?? 0f;
            scroll.SetContent(null);
            ClearChildren(mask);
        }
        else
        {
            scroll = new NScrollableContainer
            {
                Name = "KeywordScroll",
                SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                MouseFilter = MouseFilterEnum.Stop
            };
            mask = new Control
            {
                Name = "Mask",
                ClipContents = true,
                MouseFilter = MouseFilterEnum.Ignore
            };
            mask.SetAnchorsPreset(LayoutPreset.FullRect);
            mask.OffsetRight = -NLoadoutNativeScrollbar.Width;
            scroll.AddChild(mask);

            NScrollbar scrollbar = NLoadoutNativeScrollbar.Create();
            scrollbar.Name = "Scrollbar";
            scrollbar.CustomMinimumSize = new Vector2(NLoadoutNativeScrollbar.Width, 0f);
            scrollbar.SetAnchorsPreset(LayoutPreset.RightWide);
            scrollbar.OffsetLeft = -NLoadoutNativeScrollbar.Width;
            scrollbar.OffsetTop = NLoadoutNativeScrollbar.EndCapSize;
            scrollbar.OffsetBottom = -NLoadoutNativeScrollbar.EndCapSize;
            scroll.AddChild(scrollbar);
            scroll.DisableScrollingIfContentFits();
        }

        scroll.CustomMinimumSize = new Vector2(ContentWidth, visibleHeight);

        content.Name = "Content";
        content.SetAnchorsPreset(LayoutPreset.TopWide);
        mask.AddChild(content);
        if (preservedContentY != 0f)
        {
            Vector2 position = content.Position;
            position.Y = preservedContentY;
            content.Position = position;
        }
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
                .ToList();
            if (string.Equals(
                    _selectedModId,
                    MainFile.ModId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return BuildLoadoutBlocks(filtered);
            }

            filtered = filtered
                .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => Convert.ToInt32(entry.Keyword))
                .ToList();
            return filtered.Count == 0
                ? []
                : [new ContentBlock(null, filtered)];
        }

        List<ContentBlock> blocks = [];
        IReadOnlyList<CatalogEntry> core = catalog
            .Where(entry =>
                IsCoreSource(entry.ModId)
                && entry.EditorSection == LoadoutKeywordEditorSection.Default)
            .OrderBy(entry => GetSourceRank(entry.ModId))
            .ThenBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => Convert.ToInt32(entry.Keyword))
            .ToList();
        if (core.Count > 0)
            blocks.Add(new ContentBlock(null, core));

        AddLoadoutSection(
            blocks,
            catalog,
            LoadoutKeywordEditorSection.Basic,
            "CARD_MOD_LOADOUT_BASIC_KEYWORDS",
            "Loadout Basic Keywords");
        AddLoadoutSection(
            blocks,
            catalog,
            LoadoutKeywordEditorSection.Power,
            "CARD_MOD_LOADOUT_POWER_KEYWORDS",
            "Loadout Power Keywords");
        AddLoadoutSection(
            blocks,
            catalog,
            LoadoutKeywordEditorSection.Restrictive,
            "CARD_MOD_LOADOUT_RESTRICTIVE_KEYWORDS",
            "Loadout Restrictive Keywords");
        AddLoadoutSection(
            blocks,
            catalog,
            LoadoutKeywordEditorSection.Fatal,
            "CARD_MOD_LOADOUT_FATAL_KEYWORDS",
            "Loadout Fatal Keywords");
        AddLoadoutSection(
            blocks,
            catalog,
            LoadoutKeywordEditorSection.Improvement,
            "CARD_MOD_LOADOUT_IMPROVEMENT_KEYWORDS",
            "Loadout Improvement Keywords");

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

    private static IReadOnlyList<ContentBlock> BuildLoadoutBlocks(
        IReadOnlyList<CatalogEntry> entries)
    {
        List<ContentBlock> blocks = [];
        IReadOnlyList<CatalogEntry> standard = entries
            .Where(entry =>
                entry.EditorSection == LoadoutKeywordEditorSection.Default)
            .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => Convert.ToInt32(entry.Keyword))
            .ToList();
        if (standard.Count > 0)
            blocks.Add(new ContentBlock(null, standard));

        AddLoadoutSection(
            blocks,
            entries,
            LoadoutKeywordEditorSection.Basic,
            "CARD_MOD_LOADOUT_BASIC_KEYWORDS",
            "Loadout Basic Keywords");
        AddLoadoutSection(
            blocks,
            entries,
            LoadoutKeywordEditorSection.Power,
            "CARD_MOD_LOADOUT_POWER_KEYWORDS",
            "Loadout Power Keywords");
        AddLoadoutSection(
            blocks,
            entries,
            LoadoutKeywordEditorSection.Restrictive,
            "CARD_MOD_LOADOUT_RESTRICTIVE_KEYWORDS",
            "Loadout Restrictive Keywords");
        AddLoadoutSection(
            blocks,
            entries,
            LoadoutKeywordEditorSection.Fatal,
            "CARD_MOD_LOADOUT_FATAL_KEYWORDS",
            "Loadout Fatal Keywords");
        AddLoadoutSection(
            blocks,
            entries,
            LoadoutKeywordEditorSection.Improvement,
            "CARD_MOD_LOADOUT_IMPROVEMENT_KEYWORDS",
            "Loadout Improvement Keywords");

        return blocks;
    }

    private static void AddLoadoutSection(
        ICollection<ContentBlock> blocks,
        IEnumerable<CatalogEntry> entries,
        LoadoutKeywordEditorSection section,
        string titleLocKey,
        string titleFallback)
    {
        IReadOnlyList<CatalogEntry> sectionEntries = entries
            .Where(entry => entry.EditorSection == section)
            .OrderBy(entry => entry.EditorOrder)
            .ToList();
        if (sectionEntries.Count == 0)
            return;

        blocks.Add(new ContentBlock(
            LocMan.Loc(titleLocKey, titleFallback),
            sectionEntries));
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
            if (entry.ControlKind
                == LoadoutKeywordEditorControlKind.RepeatablePower)
            {
                grid.AddChild(CreateRepeatableControl(entry, toggleWidth));
                continue;
            }

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

    private Control CreateRepeatableControl(
        CatalogEntry entry,
        float width)
    {
        HBoxContainer row = new()
        {
            CustomMinimumSize = new Vector2(width, ToggleHeight),
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            MouseFilter = MouseFilterEnum.Stop
        };
        row.AddThemeConstantOverride("separation", 4);
        MegaLabel label = CreateLabel(entry.Label, 19, StsColors.cream);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);

        Button add = CreateRepeatButton("+");
        add.Pressed += () => _onRepeatAdded(entry.Keyword);
        row.AddChild(add);

        int count = _getRepeatCount(entry.Keyword);
        Button remove = CreateRepeatButton("-");
        remove.Visible = count > 0;
        remove.Pressed += () => _onRepeatRemoved(entry.Keyword);
        row.AddChild(remove);
        CommonHelpers.AttachHoverTips(
            row,
            () => GetKeywordHoverTips(entry.Keyword),
            cacheResult: false);
        return row;
    }

    private static Button CreateRepeatButton(string text)
    {
        Button button = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(38f, ToggleHeight),
            MouseFilter = MouseFilterEnum.Stop
        };
        button.AddThemeFontOverride("font", CommonHelpers.LoadGameFont());
        button.AddThemeFontSizeOverride("font_size", 24);
        button.AddThemeColorOverride("font_color", StsColors.gold);
        return button;
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
                LoadoutKeywordEditorSection editorSection =
                    LoadoutKeywordEditorSection.Default;
                int editorOrder = int.MaxValue;
                LoadoutKeywordEditorControlKind controlKind =
                    LoadoutKeywordEditorControlKind.Toggle;
                for (int index = 0;
                     index < LoadoutKeywordRegistry.All.Count;
                     index++)
                {
                    LoadoutKeywordModel model =
                        LoadoutKeywordRegistry.All[index];
                    if (!model.Keyword.Equals(keyword))
                        continue;

                    editorSection = model.EditorSection;
                    editorOrder = index;
                    controlKind = model.EditorControlKind;
                    break;
                }
                return new CatalogEntry(
                    keyword,
                    GetKeywordLabel(keyword),
                    modId,
                    modName,
                    editorSection,
                    editorOrder,
                    controlKind);
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

}
