#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
using Loadout.Services.Compatibility;
using Loadout.Services.LastActions;
using Loadout.Services.Morphing;
using Loadout.Services.Targets;
using Loadout.UI;
using Loadout.UI.Managers;
using Loadout.UI.Screens;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Rooms;

namespace Loadout.PanelItems;

public static class BottledMonster
{
    private const string MorphTargetKey = "bottled_monster_morph";
    private const string MorphTargetDropdownName = "BottledMonsterMorphTargetDropdown";
    private const string CategorySorterId = "category";
    private const string MorphRootGroupKey = "morph:forms";
    private const string MorphOriginalGroupKey = "morph:forms:original";
    private const string MorphCharactersGroupKey = "morph:forms:characters";
    private const string OtherRootGroupKey = "monster:other";
    private const int ActIndexHeaderFontSize = 36;
    private const int ActNameHeaderFontSize = 30;
    private const int MonsterTypeHeaderFontSize = 26;
    private static readonly Vector2 MonsterButtonSize = new(242f, 168f);
    private static readonly Vector2 PreviewSize = new(242f, 110f);
    private static IReadOnlySet<string> _encounterMonsterIds = new HashSet<string>(StringComparer.Ordinal);

    public static void Initialize()
    {
        IReadOnlyList<MonsterModel> allMonsters = BottledMonsterMorphService.GetMonsterModels();
        _encounterMonsterIds = ModelDb.Monsters
            .Select(monster => monster.Id.ToString())
            .ToHashSet(StringComparer.Ordinal);
        MonsterCatalogData catalog = BuildMonsterCatalogData(allMonsters);
        MonsterGroupPresentation grouping = BuildMonsterGroupPresentation(catalog, includeMorphGroups: false);

        MonsterCatalogData morphCatalog = BuildMonsterCatalogData(allMonsters);
        MonsterGroupPresentation morphGrouping = BuildMonsterGroupPresentation(morphCatalog, includeMorphGroups: true);
        IReadOnlyList<MorphOption> morphOptions = BuildMorphOptions(allMonsters);
        NGenericSelectScreen morphScreen = CreateMorphScreen(morphOptions, morphCatalog, morphGrouping);

        NLoadoutPanelItem panelItem = CommonHelpers.CreateAndAddLoadoutItem(
            allMonsters,
            new SelectItemAdapter<MonsterModel>
            {
                GetId = monster => monster.Id.ToString(),
                GetName = FormatMonsterTitle,
                GetSearchText = monster => BuildMonsterSearchText(monster, catalog),
                CreateView = (monster, _) => CreateMonsterGridItem(monster),
                BindActivationWithCleanup = (_, view, activate) => CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate)
            },
            builder =>
            {
                builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                builder.Materialization(SelectMaterializationMode.Lazy);
                builder.Layout(4, MonsterButtonSize, 24, 24, fixedSlots: false);
                AddActFilters(builder, catalog);
                AddMonsterCategoryFilters(builder, catalog);
                CommonHelpers.AddModFilters(builder, allMonsters);
                builder.Sorter(
                    CategorySorterId,
                    LocMan.Loc("FILTER_GROUP_MONSTER_CATEGORY", "Category"),
                    (left, right) => CompareMonstersByCategory(left, right, catalog, grouping),
                    (left, right) => CompareMonstersByCategory(right, left, catalog, grouping),
                    activeByDefault: true);
                builder.KeySorter("name", LocMan.Loc("SORT_NAME", "Name"), FormatMonsterTitle, comparer: StringComparer.Ordinal);
                builder.KeySorter("id", LocMan.Loc("SORT_ID", "ID"), model => model.Id.ToString(), comparer: StringComparer.Ordinal);
                builder.Sorter("mod", LocMan.Loc("FILTER_GROUP_MODS", "Mods"), CompareMonsterMod);
                AddMonsterGrouping(builder, catalog, grouping);
            },
            _ => { },
            "BottledMonster.png",
            LocMan.Loc("BOTTLEDMONSTER_TITLE", "Bottled Monster"),
            LocMan.Loc("BOTTLEDMONSTER_DESC", "Right-click to summon a monster. Alt + either click opens morph mode. Ctrl + right-click repeats the last summon."),
            HandleSummonMonsterActivatedAsync,
            LastActionService.BottleMonsterKey,
            ReplayBottleMonsterLastActionAsync,
            selectScreenScenePath: CommonHelpers.MonsterSelectScreenScenePath);

        panelItem.AlternateBoundScreen = morphScreen;
        panelItem.AlternateBeforeOpen = screen => LoadoutTargetService.UpsertTargetDropdown(
            screen,
            MorphTargetDropdownName,
            MorphTargetKey,
            LoadoutTargetMode.PlayersOnly);
    }

    private static IReadOnlyList<MorphOption> BuildMorphOptions(IReadOnlyList<MonsterModel> allMonsters)
    {
        List<MorphOption> options =
        [
            new MorphOption("original_form", null, MorphOptionKind.Original)
        ];

        options.AddRange(ModelDb.AllCharacters
            .Where(character => character.IsPlayable)
            .GroupBy(character => character.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(character => new MorphOption($"character:{character.Id}", character, MorphOptionKind.Character)));
        options.AddRange(allMonsters
            .Select(monster => new MorphOption($"monster:{monster.Id}", monster, MorphOptionKind.Monster)));
        return options;
    }

    private static NGenericSelectScreen CreateMorphScreen(
        IReadOnlyList<MorphOption> options,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        PackedScene scene = GD.Load<PackedScene>(CommonHelpers.MorphSelectScreenScenePath);
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();
        SelectItemAdapter<MorphOption> adapter = new()
        {
            GetId = option => option.Id,
            GetName = FormatMorphOptionTitle,
            GetSearchText = option => BuildMorphSearchText(option, catalog),
            CreateView = (option, _) => CreateMorphGridItem(option),
            BindActivationWithCleanup = (_, view, activate) => CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate)
        };

        void Configure(NGenericSelectScreen target, bool preserveViews = false)
        {
            if (preserveViews)
                target.ConfigurePreservingViews(options, adapter, builder => BuildMorphScreen(builder, options, catalog, grouping));
            else
                target.Configure(options, adapter, builder => BuildMorphScreen(builder, options, catalog, grouping));

            target.RequestDeferredVisibleRefresh();
        }

        Configure(screen);
        screen.LocaleChanged += () =>
        {
            SelectScreenUiState state = screen.CaptureUiState();
            Configure(screen, preserveViews: true);
            screen.RestoreUiState(state);
        };
        screen.Cancelled += NLoadoutPanelRoot.CloseTopLoadoutScreen;
        screen.Confirmed += _ => NLoadoutPanelRoot.CloseTopLoadoutScreen();
        screen.ItemActivated += (item, _) =>
        {
            if (item.UntypedModel is MorphOption option)
                RequestMorph(option);
        };
        return screen;
    }

    private static void BuildMorphScreen(
        SelectScreenBuilder<MorphOption> builder,
        IReadOnlyList<MorphOption> options,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
        builder.Materialization(SelectMaterializationMode.Lazy);
        builder.Layout(4, MonsterButtonSize, 24, 24, fixedSlots: false);
        builder.FilterGroup("morph_type", LocMan.Loc("BOTTLEDMONSTER_MORPH_TYPE", "Morph Type"));
        builder.Filter(
            "morph_characters",
            LocMan.Loc("BOTTLEDMONSTER_MORPH_CHARACTERS", "Characters"),
            option => option.Kind == MorphOptionKind.Character,
            "morph_type");
        builder.Filter(
            "morph_monsters",
            LocMan.Loc("BOTTLEDMONSTER_MORPH_MONSTERS", "Monsters"),
            option => option.Kind == MorphOptionKind.Monster,
            "morph_type");
        AddMorphActFilters(builder, catalog);
        AddMorphCategoryFilters(builder, catalog);

        IReadOnlyList<string> modIds = options
            .Where(option => option.Model is not null)
            .Select(option => CommonHelpers.GetModelModId(option.Model!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(CommonHelpers.GetModName, StringComparer.Ordinal)
            .ToList();
        if (modIds.Count > 1)
        {
            builder.FilterGroup("morph_mods", LocMan.Loc("FILTER_GROUP_MODS", "Mods"));
            foreach (string modId in modIds)
            {
                string capturedModId = modId;
                builder.Filter(
                    $"morph_mod_{FilterId("mod", capturedModId)}",
                    CommonHelpers.GetModName(capturedModId),
                    option => option.Model is not null
                              && string.Equals(CommonHelpers.GetModelModId(option.Model), capturedModId, StringComparison.Ordinal),
                    "morph_mods");
            }
        }

        builder.Sorter(
            CategorySorterId,
            LocMan.Loc("FILTER_GROUP_MONSTER_CATEGORY", "Category"),
            (left, right) => CompareMorphOptionsByCategory(left, right, catalog, grouping),
            (left, right) => CompareMorphOptionsByCategory(right, left, catalog, grouping),
            activeByDefault: true);
        builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), CompareMorphOptionName);
        builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (left, right) => CompareMorphOptions(left, right, option => option.Model?.Id.ToString() ?? string.Empty));
        builder.Sorter("mod", LocMan.Loc("FILTER_GROUP_MODS", "Mods"), (left, right) => CompareMorphOptions(left, right, option => option.Model is null ? string.Empty : CommonHelpers.GetModName(CommonHelpers.GetModelModId(option.Model))));
        AddMorphGrouping(builder, catalog, grouping);
    }

    private static int CompareMorphOptionName(MorphOption left, MorphOption right)
    {
        return CompareMorphOptions(left, right, FormatMorphOptionTitle);
    }

    private static int CompareMorphOptions(MorphOption left, MorphOption right, Func<MorphOption, string> selector)
    {
        if (left.Kind == MorphOptionKind.Original || right.Kind == MorphOptionKind.Original)
            return left.Kind == right.Kind ? 0 : left.Kind == MorphOptionKind.Original ? -1 : 1;

        int compared = string.Compare(selector(left), selector(right), StringComparison.OrdinalIgnoreCase);
        return compared != 0
            ? compared
            : string.Compare(FormatMorphOptionTitle(left), FormatMorphOptionTitle(right), StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareMorphOptionsByCategory(
        MorphOption left,
        MorphOption right,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        int byGroup = grouping.GetLeafOrder(GetMorphGroupKey(left, catalog))
            .CompareTo(grouping.GetLeafOrder(GetMorphGroupKey(right, catalog)));
        return byGroup != 0
            ? byGroup
            : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
    }

    private static void AddMorphGrouping(
        SelectScreenBuilder<MorphOption> builder,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        builder.GroupBySorter(
            CategorySorterId,
            option => GetMorphGroupKey(option, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.DescendingGroupOrder);
        builder.GroupBySorter(
            "name",
            option => GetMorphGroupKey(option, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
        builder.GroupBySorter(
            "id",
            option => GetMorphGroupKey(option, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
        builder.GroupBySorter(
            "mod",
            option => GetMorphGroupKey(option, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
    }

    private static string GetMorphGroupKey(MorphOption option, MonsterCatalogData catalog)
    {
        return option.Kind switch
        {
            MorphOptionKind.Original => MorphOriginalGroupKey,
            MorphOptionKind.Character => MorphCharactersGroupKey,
            _ when option.Model is MonsterModel monster => GetMonsterGroupKey(monster, catalog),
            _ => OtherRootGroupKey
        };
    }

    private static string BuildMorphSearchText(MorphOption option, MonsterCatalogData catalog)
    {
        return option.Model switch
        {
            MonsterModel monster => BuildMonsterSearchText(monster, catalog),
            CharacterModel character => string.Join(
                " ",
                character.Id.ToString(),
                FormatCharacterTitle(character),
                CommonHelpers.GetModName(CommonHelpers.GetModelModId(character)),
                LocMan.Loc("BOTTLEDMONSTER_MORPH_CHARACTERS", "Characters")),
            _ => string.Join(
                " ",
                FormatMorphOptionTitle(option),
                LocMan.Loc("BOTTLEDMONSTER_MORPH_ORIGINAL", "Original Form"),
                "reset original")
        };
    }

    private static void RequestMorph(MorphOption option)
    {
        LoadoutTargetSelection target = LoadoutTargetService.GetSelected(MorphTargetKey, LoadoutTargetMode.PlayersOnly);
        ModelId modelId = option.Model?.Id ?? ModelId.none;
        LoadoutImmediateMutationService.RequestMorphPlayer(modelId, target);
    }

    private static Control CreateMorphGridItem(MorphOption option)
    {
        if (option.Model is MonsterModel monster)
            return CreateMonsterGridItem(monster);

        Button button = CommonHelpers.CreateModelButton(MonsterButtonSize);
        button.ClipContents = true;
        ColorRect shade = new()
        {
            Color = new Color(0.02f, 0.018f, 0.015f, 0.52f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = MonsterButtonSize
        };
        button.AddChild(shade);

        if (option.Model is CharacterModel character)
        {
            Control preview = CreatePreviewContainer();
            button.AddChild(preview);
            TryAddCharacterPreview(preview, character);
        }

        float titleY = option.Kind == MorphOptionKind.Original ? 48f : 104f;
        MegaLabel title = CommonHelpers.CreateButtonLabel(
            "MorphTitle",
            FormatMorphOptionTitle(option),
            new Vector2(12f, titleY),
            new Vector2(218f, option.Kind == MorphOptionKind.Original ? 48f : 42f),
            option.Kind == MorphOptionKind.Original ? 24 : 20,
            HorizontalAlignment.Center,
            StsColors.cream);
        ConfigureWrappingTitle(title);
        button.AddChild(title);

        if (option.Model is not null)
        {
            MegaLabel modLabel = CommonHelpers.CreateButtonLabel(
                "MorphMod",
                CommonHelpers.GetModName(CommonHelpers.GetModelModId(option.Model)),
                new Vector2(12f, 140f),
                new Vector2(218f, 20f),
                13,
                HorizontalAlignment.Center,
                StsColors.gray);
            button.AddChild(modLabel);
        }

        return button;
    }

    private static void TryAddCharacterPreview(Control preview, CharacterModel character)
    {
        try
        {
            NCreatureVisuals visuals = character.CreateVisuals();
            preview.AddChild(visuals);
            Callable.From(() => ConfigureDirectVisualPreview(preview, visuals, character)).CallDeferred();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: could not load character visual '{character.Id}'. {exception.Message}");
            TryAddCharacterIconFallback(preview, character);
        }
    }

    private static void TryAddCharacterIconFallback(Control preview, CharacterModel character)
    {
        try
        {
            preview.AddChild(new TextureRect
            {
                Texture = character.CharacterSelectIcon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Position = new Vector2(12f, 4f),
                Size = new Vector2(218f, 102f)
            });
        }
        catch (Exception exception)
        {
            GD.PushWarning($"BottledMonsterMorph: could not load character icon '{character.Id}'. {exception.Message}");
        }
    }

    private static string FormatMorphOptionTitle(MorphOption option)
    {
        return option.Model switch
        {
            MonsterModel monster => FormatMonsterTitle(monster),
            CharacterModel character => FormatCharacterTitle(character),
            _ => LocMan.Loc("BOTTLEDMONSTER_MORPH_ORIGINAL", "Original Form")
        };
    }

    private static string FormatCharacterTitle(CharacterModel character)
    {
        try
        {
            return character.Title.GetFormattedText();
        }
        catch
        {
            return character.Id.Entry;
        }
    }

    private static Task<IReadOnlyList<LastActionEntry>> HandleSummonMonsterActivatedAsync(NGenericSelectScreen _, IGenericSelectItem selectItem)
    {
        if (selectItem.UntypedModel is not MonsterModel monster)
            return Task.FromResult<IReadOnlyList<LastActionEntry>>([]);

        bool summoned = SummonMonster(monster, selectItem.Id);
        IReadOnlyList<LastActionEntry> entries = summoned
            ?
            [
                new LastActionEntry
                {
                    Kind = LastActionService.SummonMonsterKind,
                    ContentId = monster.Id.ToString(),
                    Amount = 1
                }
            ]
            : [];
        return Task.FromResult(entries);
    }

    private static Task ReplayBottleMonsterLastActionAsync()
    {
        IReadOnlyList<LastActionEntry> entries = LastActionService.GetAction(LastActionService.BottleMonsterKey)
            .Where(action => action.Kind == LastActionService.SummonMonsterKind && action.Amount > 0)
            .ToList();
        if (entries.Count == 0)
        {
            DuplicateCurrentMonsters();
            return Task.CompletedTask;
        }

        foreach (LastActionEntry entry in entries)
        {
            MonsterModel? monster = ResolveMonster(entry.ContentId);
            if (monster is null)
            {
                GD.PushWarning($"LoadoutPanel: cannot replay monster summon for unknown monster '{entry.ContentId}'.");
                continue;
            }

            for (int i = 0; i < entry.Amount; i++)
                SummonMonster(monster, entry.ContentId);
        }

        return Task.CompletedTask;
    }

    private static bool SummonMonster(MonsterModel monster, string logId)
    {
        if (!CombatManager.Instance.IsInProgress)
        {
            GD.PushWarning($"LoadoutPanel: cannot summon monster '{logId}' outside combat.");
            return false;
        }

        try
        {
            return LoadoutSummonMonsterService.RequestSummonMonster(monster.Id);
        }
        catch (Exception exception)
        {
            GD.PushError($"LoadoutPanel: failed to summon monster '{monster.Id}': {exception}");
            return false;
        }
    }

    private static void DuplicateCurrentMonsters()
    {
        if (!CombatManager.Instance.IsInProgress)
            return;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState is null)
            return;

        foreach (ModelId monsterId in combatState.Enemies
                     .Where(creature => creature.Monster is not null)
                     .Select(creature => creature.Monster!.Id)
                     .ToList())
        {
            LoadoutSummonMonsterService.RequestSummonMonster(monsterId);
        }
    }

    private static Control CreateMonsterGridItem(MonsterModel model)
    {
        Button button = CommonHelpers.CreateModelButton(MonsterButtonSize);
        button.ClipContents = true;

        ColorRect shade = new()
        {
            Color = new Color(0.02f, 0.018f, 0.015f, 0.52f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = MonsterButtonSize
        };
        button.AddChild(shade);

        Control preview = CreatePreviewContainer();
        button.AddChild(preview);
        TryAddMonsterPreview(preview, model);

        MegaLabel titleLabel = CommonHelpers.CreateButtonLabel(
            "MonsterTitle",
            FormatMonsterTitle(model),
            new Vector2(12f, 104f),
            new Vector2(218f, 42f),
            20,
            HorizontalAlignment.Center,
            StsColors.cream);
        ConfigureWrappingTitle(titleLabel);
        button.AddChild(titleLabel);

        MegaLabel modLabel = CommonHelpers.CreateButtonLabel(
            "MonsterMod",
            CommonHelpers.GetModName(CommonHelpers.GetModelModId(model)),
            new Vector2(12f, 140f),
            new Vector2(218f, 20f),
            13,
            HorizontalAlignment.Center,
            StsColors.gray);
        button.AddChild(modLabel);

        return button;
    }

    private static Control CreatePreviewContainer()
    {
        return new Control
        {
            Name = "CreaturePreview",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = PreviewSize,
            CustomMinimumSize = PreviewSize
        };
    }

    private static void TryAddMonsterPreview(Control preview, MonsterModel canonical)
    {
        if (!_encounterMonsterIds.Contains(canonical.Id.ToString()))
        {
            if (!TryAddDirectMonsterPreview(preview, canonical))
                AddMonsterPreviewTextFallback(preview, canonical);

            return;
        }

        try
        {
            MonsterModel monster = canonical.ToMutable();
            monster.SetUpForCombat();
            Creature creature = new(monster, CombatSide.Enemy, null)
            {
                CombatState = new NullCombatState()
            };
            NCreature? creatureNode = NCreature.Create(creature);
            if (creatureNode is null)
                throw new InvalidOperationException("NCreature.Create returned null.");

            preview.AddChild(creatureNode);
            Callable.From(() => FitPreviewCreature(preview, creatureNode)).CallDeferred();
        }
        catch (Exception exception)
        {
            ClearPreviewChildren(preview);
            if (TryAddDirectMonsterPreview(preview, canonical))
                return;

            GD.PushWarning($"LoadoutPanel: could not create local monster preview for '{canonical.Id}'. {exception.Message}");
            AddMonsterPreviewTextFallback(preview, canonical);
        }
    }

    private static bool TryAddDirectMonsterPreview(Control preview, MonsterModel canonical)
    {
        try
        {
            NCreatureVisuals visuals = canonical.CreateVisuals();
            preview.AddChild(visuals);
            Callable.From(() => ConfigureDirectVisualPreview(preview, visuals, canonical)).CallDeferred();
            return true;
        }
        catch (Exception exception)
        {
            ClearPreviewChildren(preview);
            GD.PushWarning($"LoadoutPanel: could not create direct monster visual for '{canonical.Id}'. {exception.Message}");
            return false;
        }
    }

    private static void ConfigureDirectVisualPreview(
        Control preview,
        NCreatureVisuals visuals,
        AbstractModel model)
    {
        if (!GodotObject.IsInstanceValid(preview)
            || !GodotObject.IsInstanceValid(visuals)
            || !visuals.IsNodeReady())
        {
            return;
        }

        try
        {
            MonsterModel? monster = model as MonsterModel;
            visuals.UpdatePhobiaMode(monster);
            if (monster is not null)
                visuals.SetUpSkin(monster);

            PlayPreviewIdle(visuals);
            FitDirectVisualPreview(visuals);
        }
        catch (Exception exception)
        {
            ClearPreviewChildren(preview);
            GD.PushWarning($"LoadoutPanel: could not configure creature visual preview '{model.Id}'. {exception.Message}");
            if (model is CharacterModel character)
                TryAddCharacterIconFallback(preview, character);
            else if (model is MonsterModel failedMonster)
                AddMonsterPreviewTextFallback(preview, failedMonster);
        }
    }

    private static void FitDirectVisualPreview(NCreatureVisuals visuals)
    {
        Rect2 bounds = new(visuals.Bounds.Position, visuals.Bounds.Size);
        if (bounds.Size.X <= 0f || bounds.Size.Y <= 0f)
        {
            visuals.Scale = Vector2.One * 0.28f;
            visuals.Position = new Vector2(PreviewSize.X * 0.5f, PreviewSize.Y - 10f);
            return;
        }

        float scale = MathF.Min(
            (PreviewSize.X - 28f) / bounds.Size.X,
            (PreviewSize.Y - 12f) / bounds.Size.Y);
        scale = Mathf.Clamp(scale, 0.1f, 0.42f);
        visuals.Scale = Vector2.One * scale;
        visuals.Position = new Vector2(
            PreviewSize.X * 0.5f - bounds.GetCenter().X * scale,
            PreviewSize.Y - 8f - bounds.End.Y * scale);
    }

    private static void PlayPreviewIdle(NCreatureVisuals visuals)
    {
        if (visuals.SpineBody is not { } spine)
            return;

        foreach (string animation in new[] { "idle_loop", "relaxed_loop", "idle" })
        {
            if (!spine.HasAnimation(animation))
                continue;

            visuals.SpineAnimation.AddAnimation(animation);
            return;
        }
    }

    private static void ClearPreviewChildren(Control preview)
    {
        foreach (Node child in preview.GetChildren())
        {
            preview.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static void AddMonsterPreviewTextFallback(Control preview, MonsterModel canonical)
    {
        preview.AddChild(CommonHelpers.CreateButtonLabel(
            "MonsterPreviewFallback",
            canonical.Id.Entry,
            new Vector2(12f, 28f),
            new Vector2(218f, 48f),
            16,
            HorizontalAlignment.Center,
            StsColors.gray));
    }

    private static void FitPreviewCreature(Control preview, NCreature creatureNode)
    {
        if (!GodotObject.IsInstanceValid(preview) || !GodotObject.IsInstanceValid(creatureNode) || !creatureNode.IsNodeReady())
            return;

        try
        {
            creatureNode.SetupForBestiary();
            creatureNode.ToggleIsInteractable(false);
            creatureNode.SetAnimationTrigger("Idle");

            Vector2 boundsSize = creatureNode.Visuals.Bounds.Size;
            float scale = boundsSize.X <= 0f || boundsSize.Y <= 0f
                ? 0.28f
                : MathF.Min((PreviewSize.X - 28f) / boundsSize.X, (PreviewSize.Y - 12f) / boundsSize.Y);
            scale = Mathf.Clamp(scale, 0.12f, 0.42f);

            creatureNode.Scale = Vector2.One * scale;
            creatureNode.Position = new Vector2(PreviewSize.X * 0.5f, PreviewSize.Y - 10f);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"LoadoutPanel: could not fit local monster preview '{creatureNode.Entity.ModelId}'. {exception.Message}");
        }
    }

    private static void ConfigureWrappingTitle(MegaLabel label)
    {
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        label.AutoSizeEnabled = true;
        label.MinFontSize = 13;
        label.MaxFontSize = 20;
        label.AddThemeFontSizeOverride("font_size", label.MaxFontSize);
    }

    private static MonsterCatalogData BuildMonsterCatalogData(IReadOnlyList<MonsterModel> allMonsters)
    {
        IReadOnlyList<ActModel> acts = ModelDb.Acts
            .Where(act => act.Index >= 0)
            .GroupBy(act => act.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(act => act.Index)
            .ThenBy(act => act.IsDefault ? 0 : 1)
            .ThenBy(act => act.Id.ToString(), StringComparer.Ordinal)
            .ToList();
        Dictionary<string, int> actOrderById = acts
            .Select((act, index) => (act.Id, index))
            .ToDictionary(pair => pair.Id.ToString(), pair => pair.index, StringComparer.Ordinal);
        Dictionary<string, List<MonsterMembership>> memberships = new(StringComparer.Ordinal);
        HashSet<string> actEncounterIds = new(StringComparer.Ordinal);

        foreach (ActModel act in acts)
        {
            foreach (EncounterModel encounter in act.AllEncounters)
            {
                actEncounterIds.Add(encounter.Id.ToString());
                AddEncounterMemberships(memberships, encounter, act);
            }
        }

        foreach (EncounterModel encounter in Sts2Compatibility.EnumerateEncounters()
                     .GroupBy(model => model.Id.ToString(), StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (!actEncounterIds.Contains(encounter.Id.ToString()))
                AddEncounterMemberships(memberships, encounter, null);
        }

        Dictionary<string, MonsterPlacement> placements = new(StringComparer.Ordinal);
        foreach (MonsterModel monster in allMonsters)
        {
            string monsterId = monster.Id.ToString();
            IReadOnlyList<MonsterMembership> allMemberships = memberships.GetValueOrDefault(monsterId) ?? [];
            IReadOnlyList<MonsterMembership> eligibleActMemberships = allMemberships
                .Where(membership => membership.Act is not null && IsMonsterRoomType(membership.RoomType))
                .OrderBy(membership => actOrderById.GetValueOrDefault(membership.Act!.Id.ToString(), int.MaxValue))
                .ThenBy(membership => GetMonsterCategoryOrder(membership.RoomType))
                .ToList();
            IReadOnlyList<ActModel> applicableActs = allMemberships
                .Where(membership => membership.Act is not null)
                .Select(membership => membership.Act!)
                .GroupBy(act => act.Id.ToString(), StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(act => actOrderById.GetValueOrDefault(act.Id.ToString(), int.MaxValue))
                .ToList();
            IReadOnlySet<RoomType> roomTypes = allMemberships
                .Select(membership => membership.RoomType)
                .ToHashSet();

            string groupKey;
            if (eligibleActMemberships.Count > 0)
            {
                MonsterMembership primary = eligibleActMemberships[0];
                groupKey = GetActCategoryGroupKey(primary.Act!, primary.RoomType);
            }
            else
            {
                MonsterMembership? noActMembership = allMemberships
                    .Where(membership => membership.Act is null && IsMonsterRoomType(membership.RoomType))
                    .OrderBy(membership => GetMonsterCategoryOrder(membership.RoomType))
                    .FirstOrDefault();
                groupKey = noActMembership is null
                    ? GetOtherCategoryGroupKey(null)
                    : GetOtherCategoryGroupKey(noActMembership.RoomType);
            }

            placements[monsterId] = new MonsterPlacement(
                groupKey,
                applicableActs,
                applicableActs.Select(act => act.Id.ToString()).ToHashSet(StringComparer.Ordinal),
                roomTypes,
                allMemberships,
                allMemberships.Any(membership => membership.Act is null),
                eligibleActMemberships.Count == 0);
        }

        return new MonsterCatalogData(acts, placements);
    }

    private static void AddEncounterMemberships(
        IDictionary<string, List<MonsterMembership>> memberships,
        EncounterModel encounter,
        ActModel? act)
    {
        foreach (MonsterModel monster in encounter.AllPossibleMonsters)
        {
            string monsterId = monster.Id.ToString();
            if (!memberships.TryGetValue(monsterId, out List<MonsterMembership>? monsterMemberships))
            {
                monsterMemberships = [];
                memberships[monsterId] = monsterMemberships;
            }

            string? actId = act?.Id.ToString();
            if (monsterMemberships.Any(existing =>
                    existing.RoomType == encounter.RoomType
                    && string.Equals(existing.Act?.Id.ToString(), actId, StringComparison.Ordinal)))
            {
                continue;
            }

            monsterMemberships.Add(new MonsterMembership(act, encounter.RoomType));
        }
    }

    private static MonsterGroupPresentation BuildMonsterGroupPresentation(
        MonsterCatalogData catalog,
        bool includeMorphGroups)
    {
        List<string> groupOrder = [];
        List<IReadOnlyList<string>> groupBlocks = [];
        Dictionary<string, SelectGroupHeader> headers = new(StringComparer.Ordinal);
        Dictionary<string, int> leafOrder = new(StringComparer.Ordinal);

        if (includeMorphGroups)
        {
            List<string> morphBlock =
            [
                MorphRootGroupKey,
                MorphOriginalGroupKey,
                MorphCharactersGroupKey
            ];
            headers[MorphRootGroupKey] = new SelectGroupHeader(
                FormatActIndexHeader(LocMan.Loc("BOTTLEDMONSTER_MORPH_TYPE", "Morph Type")),
                childGroupPrefix: MorphRootGroupKey + ":");
            headers[MorphOriginalGroupKey] = new SelectGroupHeader(
                FormatActNameHeader(LocMan.Loc("BOTTLEDMONSTER_MORPH_ORIGINAL", "Original Form")));
            headers[MorphCharactersGroupKey] = new SelectGroupHeader(
                FormatActNameHeader(LocMan.Loc("BOTTLEDMONSTER_MORPH_CHARACTERS", "Characters")));
            leafOrder[MorphOriginalGroupKey] = leafOrder.Count;
            leafOrder[MorphCharactersGroupKey] = leafOrder.Count;
            groupBlocks.Add(morphBlock);
            groupOrder.AddRange(morphBlock);
        }

        foreach (IGrouping<int, ActModel> actsAtIndex in catalog.Acts.GroupBy(act => act.Index))
        {
            string rootKey = GetActRootGroupKey(actsAtIndex.Key);
            List<string> block = [rootKey];
            headers[rootKey] = new SelectGroupHeader(
                FormatActIndexHeader(FormatActNumber(actsAtIndex.Key)),
                childGroupPrefix: rootKey + ":");

            foreach (ActModel act in actsAtIndex)
            {
                string actKey = GetActGroupKey(act);
                block.Add(actKey);
                headers[actKey] = new SelectGroupHeader(
                    FormatActNameHeader(FormatActTitle(act)),
                    childGroupPrefix: actKey + ":");

                foreach (RoomType roomType in MonsterRoomTypes)
                {
                    string categoryKey = GetActCategoryGroupKey(act, roomType);
                    block.Add(categoryKey);
                    headers[categoryKey] = new SelectGroupHeader(
                        FormatMonsterTypeHeader(FormatMonsterCategory(roomType)));
                    leafOrder[categoryKey] = leafOrder.Count;
                }
            }

            groupBlocks.Add(block);
            groupOrder.AddRange(block);
        }

        List<string> otherBlock = [OtherRootGroupKey];
        headers[OtherRootGroupKey] = new SelectGroupHeader(
            FormatActIndexHeader(LocMan.Loc("OTHER", "Other")),
            childGroupPrefix: OtherRootGroupKey + ":");
        foreach (RoomType roomType in MonsterRoomTypes)
        {
            string categoryKey = GetOtherCategoryGroupKey(roomType);
            otherBlock.Add(categoryKey);
            headers[categoryKey] = new SelectGroupHeader(
                FormatMonsterTypeHeader(FormatMonsterCategory(roomType)));
            leafOrder[categoryKey] = leafOrder.Count;
        }

        string uncategorizedKey = GetOtherCategoryGroupKey(null);
        otherBlock.Add(uncategorizedKey);
        headers[uncategorizedKey] = new SelectGroupHeader(
            FormatMonsterTypeHeader(LocMan.Loc("OTHER", "Other")));
        leafOrder[uncategorizedKey] = leafOrder.Count;
        groupBlocks.Add(otherBlock);
        groupOrder.AddRange(otherBlock);

        IReadOnlyList<string> descendingGroupOrder = groupBlocks
            .AsEnumerable()
            .Reverse()
            .SelectMany(block => block)
            .ToList();
        return new MonsterGroupPresentation(groupOrder, descendingGroupOrder, headers, leafOrder);
    }

    private static void AddActFilters(
        SelectScreenBuilder<MonsterModel> builder,
        MonsterCatalogData catalog)
    {
        builder.FilterGroup("act", LocMan.Loc("FILTER_GROUP_ACT", "Act"));
        for (int actOrder = 0; actOrder < catalog.Acts.Count; actOrder++)
        {
            ActModel act = catalog.Acts[actOrder];
            string actId = act.Id.ToString();
            if (!catalog.Placements.Values.Any(placement => placement.ApplicableActIds.Contains(actId)))
                continue;

            builder.Filter(
                $"act_{actOrder}",
                $"{FormatActNumber(act.Index)}: {FormatActTitle(act)}",
                monster => catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                           && placement.ApplicableActIds.Contains(actId),
                "act");
        }

        builder.Filter(
            "act_other",
            LocMan.Loc("OTHER", "Other"),
            monster => catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                       && placement.IsOther,
            "act");
    }

    private static void AddMorphActFilters(
        SelectScreenBuilder<MorphOption> builder,
        MonsterCatalogData catalog)
    {
        builder.FilterGroup("act", LocMan.Loc("FILTER_GROUP_ACT", "Act"));
        for (int actOrder = 0; actOrder < catalog.Acts.Count; actOrder++)
        {
            ActModel act = catalog.Acts[actOrder];
            string actId = act.Id.ToString();
            if (!catalog.Placements.Values.Any(placement => placement.ApplicableActIds.Contains(actId)))
                continue;

            builder.Filter(
                $"act_{actOrder}",
                $"{FormatActNumber(act.Index)}: {FormatActTitle(act)}",
                option => option.Model is MonsterModel monster
                          && catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                          && placement.ApplicableActIds.Contains(actId),
                "act");
        }

        builder.Filter(
            "act_other",
            LocMan.Loc("OTHER", "Other"),
            option => option.Model is MonsterModel monster
                      && catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                      && placement.IsOther,
            "act");
    }

    private static void AddMonsterCategoryFilters(
        SelectScreenBuilder<MonsterModel> builder,
        MonsterCatalogData catalog)
    {
        builder.FilterGroup("monster_category", LocMan.Loc("FILTER_GROUP_MONSTER_CATEGORY", "Category"));
        foreach (RoomType roomType in MonsterRoomTypes)
        {
            RoomType capturedRoomType = roomType;
            builder.Filter(
                $"monster_category_{roomType.ToString().ToLowerInvariant()}",
                FormatMonsterCategory(roomType),
                monster => catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                           && placement.RoomTypes.Contains(capturedRoomType),
                "monster_category");
        }
    }

    private static void AddMorphCategoryFilters(
        SelectScreenBuilder<MorphOption> builder,
        MonsterCatalogData catalog)
    {
        builder.FilterGroup("monster_category", LocMan.Loc("FILTER_GROUP_MONSTER_CATEGORY", "Category"));
        foreach (RoomType roomType in MonsterRoomTypes)
        {
            RoomType capturedRoomType = roomType;
            builder.Filter(
                $"monster_category_{roomType.ToString().ToLowerInvariant()}",
                FormatMonsterCategory(roomType),
                option => option.Model is MonsterModel monster
                          && catalog.TryGetPlacement(monster, out MonsterPlacement placement)
                          && placement.RoomTypes.Contains(capturedRoomType),
                "monster_category");
        }
    }

    private static void AddMonsterGrouping(
        SelectScreenBuilder<MonsterModel> builder,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        builder.GroupBySorter(
            CategorySorterId,
            monster => GetMonsterGroupKey(monster, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.DescendingGroupOrder);
        builder.GroupBySorter(
            "name",
            monster => GetMonsterGroupKey(monster, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
        builder.GroupBySorter(
            "id",
            monster => GetMonsterGroupKey(monster, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
        builder.GroupBySorter(
            "mod",
            monster => GetMonsterGroupKey(monster, catalog),
            grouping.GetHeader,
            grouping.GroupOrder,
            grouping.GroupOrder);
    }

    private static int CompareMonstersByCategory(
        MonsterModel left,
        MonsterModel right,
        MonsterCatalogData catalog,
        MonsterGroupPresentation grouping)
    {
        int byGroup = grouping.GetLeafOrder(GetMonsterGroupKey(left, catalog))
            .CompareTo(grouping.GetLeafOrder(GetMonsterGroupKey(right, catalog)));
        return byGroup != 0
            ? byGroup
            : string.Compare(left.Id.ToString(), right.Id.ToString(), StringComparison.Ordinal);
    }

    private static string GetMonsterGroupKey(MonsterModel monster, MonsterCatalogData catalog)
    {
        return catalog.TryGetPlacement(monster, out MonsterPlacement placement)
            ? placement.GroupKey
            : GetOtherCategoryGroupKey(null);
    }

    private static string BuildMonsterSearchText(MonsterModel monster, MonsterCatalogData catalog)
    {
        List<string> searchParts =
        [
            monster.Id.ToString(),
            FormatMonsterTitle(monster),
            CommonHelpers.GetModName(CommonHelpers.GetModelModId(monster))
        ];

        if (catalog.TryGetPlacement(monster, out MonsterPlacement placement))
        {
            foreach (ActModel act in placement.ApplicableActs)
            {
                searchParts.Add(FormatActNumber(act.Index));
                searchParts.Add(FormatActTitle(act));
            }

            foreach (RoomType roomType in placement.RoomTypes.OrderBy(GetMonsterCategoryOrder))
                searchParts.Add(FormatMonsterCategory(roomType));

            if (placement.IsOther)
                searchParts.Add(LocMan.Loc("OTHER", "Other"));
        }

        return string.Join(" ", searchParts.Distinct(StringComparer.Ordinal));
    }

    private static readonly RoomType[] MonsterRoomTypes =
    [
        RoomType.Monster,
        RoomType.Elite,
        RoomType.Boss
    ];

    private static bool IsMonsterRoomType(RoomType roomType)
    {
        return roomType is RoomType.Monster or RoomType.Elite or RoomType.Boss;
    }

    private static int GetMonsterCategoryOrder(RoomType roomType)
    {
        return roomType switch
        {
            RoomType.Monster => 0,
            RoomType.Elite => 1,
            RoomType.Boss => 2,
            _ => 3
        };
    }

    private static string GetActRootGroupKey(int actIndex) => $"monster:act:{actIndex}";
    private static string GetActGroupKey(ActModel act) => $"{GetActRootGroupKey(act.Index)}:act:{act.Id}";
    private static string GetActCategoryGroupKey(ActModel act, RoomType roomType) => $"{GetActGroupKey(act)}:category:{roomType.ToString().ToLowerInvariant()}";
    private static string GetOtherCategoryGroupKey(RoomType? roomType) => $"{OtherRootGroupKey}:category:{roomType?.ToString().ToLowerInvariant() ?? "other"}";

    private static string FormatMonsterCategory(RoomType roomType)
    {
        return roomType switch
        {
            RoomType.Boss => LocMan.Loc("MONSTER_CATEGORY_BOSS", "Boss"),
            RoomType.Elite => LocMan.Loc("MONSTER_CATEGORY_ELITE", "Elite"),
            RoomType.Monster => LocMan.Loc("MONSTER_CATEGORY_MONSTER", "Monster"),
            _ => roomType.ToString()
        };
    }

    private static int CompareMonsterMod(MonsterModel left, MonsterModel right)
    {
        int byMod = string.Compare(
            CommonHelpers.GetModName(CommonHelpers.GetModelModId(left)),
            CommonHelpers.GetModName(CommonHelpers.GetModelModId(right)),
            StringComparison.OrdinalIgnoreCase);
        return byMod != 0
            ? byMod
            : string.Compare(FormatMonsterTitle(left), FormatMonsterTitle(right), StringComparison.Ordinal);
    }

    private static string FormatMonsterTitle(MonsterModel monster)
    {
        try
        {
            return monster.Title.GetFormattedText();
        }
        catch
        {
            return monster.Id.Entry;
        }
    }

    private static string FormatActTitle(ActModel act)
    {
        try
        {
            return act.Title.GetFormattedText();
        }
        catch
        {
            return act.Id.Entry;
        }
    }

    private static string FormatActNumber(int actIndex)
    {
        int actNumber = actIndex + 1;
        return LocMan.Loc("ACT_NUMBER", $"Act {actNumber}", actNumber);
    }

    private static string FormatActIndexHeader(string text)
    {
        return $"[gold][font_size={ActIndexHeaderFontSize}][b]{text}[/b][/font_size][/gold]";
    }

    private static string FormatActNameHeader(string text)
    {
        return $"[font_size={ActNameHeaderFontSize}][b]{text}[/b][/font_size]";
    }

    private static string FormatMonsterTypeHeader(string text)
    {
        return $"[font_size={MonsterTypeHeaderFontSize}]  {text}[/font_size]";
    }

    private static MonsterModel? ResolveMonster(string monsterId)
    {
        return BottledMonsterMorphService.GetMonsterModels()
            .FirstOrDefault(monster => CommonHelpers.ModelIdMatches(monster, monsterId));
    }

    private static string FilterId(string prefix, string raw)
    {
        return $"{prefix}_{Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]+", "_")}";
    }

    private enum MorphOptionKind
    {
        Original,
        Character,
        Monster
    }

    private sealed record MorphOption(string Id, AbstractModel? Model, MorphOptionKind Kind);

    private sealed record MonsterMembership(ActModel? Act, RoomType RoomType);

    private sealed record MonsterPlacement(
        string GroupKey,
        IReadOnlyList<ActModel> ApplicableActs,
        IReadOnlySet<string> ApplicableActIds,
        IReadOnlySet<RoomType> RoomTypes,
        IReadOnlyList<MonsterMembership> Memberships,
        bool HasNoActMembership,
        bool IsOther);

    private sealed class MonsterCatalogData
    {
        public MonsterCatalogData(
            IReadOnlyList<ActModel> acts,
            IReadOnlyDictionary<string, MonsterPlacement> placements)
        {
            Acts = acts;
            Placements = placements;
        }

        public IReadOnlyList<ActModel> Acts { get; }
        public IReadOnlyDictionary<string, MonsterPlacement> Placements { get; }

        public bool TryGetPlacement(MonsterModel monster, out MonsterPlacement placement)
        {
            return Placements.TryGetValue(monster.Id.ToString(), out placement!);
        }
    }

    private sealed class MonsterGroupPresentation
    {
        private readonly IReadOnlyDictionary<string, SelectGroupHeader> _headers;
        private readonly IReadOnlyDictionary<string, int> _leafOrder;

        public MonsterGroupPresentation(
            IReadOnlyList<string> groupOrder,
            IReadOnlyList<string> descendingGroupOrder,
            IReadOnlyDictionary<string, SelectGroupHeader> headers,
            IReadOnlyDictionary<string, int> leafOrder)
        {
            GroupOrder = groupOrder;
            DescendingGroupOrder = descendingGroupOrder;
            _headers = headers;
            _leafOrder = leafOrder;
        }

        public IReadOnlyList<string> GroupOrder { get; }
        public IReadOnlyList<string> DescendingGroupOrder { get; }

        public SelectGroupHeader GetHeader(string key)
        {
            return _headers.TryGetValue(key, out SelectGroupHeader? header)
                ? header
                : new SelectGroupHeader(key);
        }

        public int GetLeafOrder(string key)
        {
            return _leafOrder.GetValueOrDefault(key, int.MaxValue);
        }
    }
}
