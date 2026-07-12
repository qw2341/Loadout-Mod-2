#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using Loadout.Services.Actions;
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
    private static readonly Vector2 MonsterButtonSize = new(242f, 168f);
    private static readonly Vector2 PreviewSize = new(242f, 110f);

    public static void Initialize()
    {
        IReadOnlyList<MonsterModel> allMonsters = ModelDb.Monsters
            .GroupBy(monster => monster.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(FormatMonsterTitle, StringComparer.Ordinal)
            .ToList();
        Dictionary<string, IReadOnlyList<string>> actNamesByMonsterId = BuildActNamesByMonsterId();
        Dictionary<string, HashSet<RoomType>> roomTypesByMonsterId = BuildRoomTypesByMonsterId();

        IReadOnlyList<MorphOption> morphOptions = BuildMorphOptions();
        NGenericSelectScreen morphScreen = CreateMorphScreen(morphOptions);

        NLoadoutPanelItem panelItem = CommonHelpers.CreateAndAddLoadoutItem(
            allMonsters,
            new SelectItemAdapter<MonsterModel>
            {
                GetId = monster => monster.Id.ToString(),
                GetName = FormatMonsterTitle,
                GetSearchText = monster => $"{monster.Id} {FormatMonsterTitle(monster)} {CommonHelpers.GetModName(CommonHelpers.GetModelModId(monster))} {GetActSearchText(monster, actNamesByMonsterId)} {GetRoomTypeSearchText(monster, roomTypesByMonsterId)}",
                CreateView = (monster, _) => CreateMonsterGridItem(monster),
                BindActivationWithCleanup = (_, view, activate) => CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate)
            },
            builder =>
            {
                builder.Options(new SelectScreenOptions { SelectionMode = SelectSelectionMode.None });
                builder.Materialization(SelectMaterializationMode.Lazy);
                builder.Layout(4, MonsterButtonSize, 24, 24, fixedSlots: false);
                AddActFilters(builder);
                AddMonsterCategoryFilters(builder, roomTypesByMonsterId);
                CommonHelpers.AddModFilters(builder, allMonsters);
                builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), (a, b) => string.Compare(FormatMonsterTitle(a), FormatMonsterTitle(b), StringComparison.Ordinal), activeByDefault: true);
                builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (a, b) => string.Compare(a.Id.Entry, b.Id.Entry, StringComparison.Ordinal));
                builder.Sorter("mod", LocMan.Loc("FILTER_GROUP_MODS", "Mods"), CompareMonsterMod);
            },
            _ => { },
            "BottledMonster.png",
            LocMan.Loc("BOTTLEDMONSTER_TITLE", "Bottled Monster"),
            LocMan.Loc("BOTTLEDMONSTER_DESC", "Right-click to summon a monster. Alt + either click opens morph mode. Ctrl + right-click repeats the last summon."),
            HandleSummonMonsterActivatedAsync,
            LastActionService.BottleMonsterKey,
            ReplayBottleMonsterLastActionAsync);

        panelItem.AlternateBoundScreen = morphScreen;
        panelItem.AlternateBeforeOpen = screen => LoadoutTargetService.UpsertTargetDropdown(
            screen,
            MorphTargetDropdownName,
            MorphTargetKey,
            LoadoutTargetMode.PlayersOnly);
    }

    private static IReadOnlyList<MorphOption> BuildMorphOptions()
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
        options.AddRange(ModelDb.Monsters
            .GroupBy(monster => monster.Id.ToString(), StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(monster => new MorphOption($"monster:{monster.Id}", monster, MorphOptionKind.Monster)));
        return options;
    }

    private static NGenericSelectScreen CreateMorphScreen(IReadOnlyList<MorphOption> options)
    {
        PackedScene scene = GD.Load<PackedScene>("res://UI/Screens/GenericSelectScreen.tscn");
        NGenericSelectScreen screen = scene.Instantiate<NGenericSelectScreen>();
        SelectItemAdapter<MorphOption> adapter = new()
        {
            GetId = option => option.Id,
            GetName = FormatMorphOptionTitle,
            GetSearchText = option => option.Model is null
                ? $"{FormatMorphOptionTitle(option)} reset original"
                : $"{option.Model.Id} {FormatMorphOptionTitle(option)} {CommonHelpers.GetModName(CommonHelpers.GetModelModId(option.Model))}",
            CreateView = (option, _) => CreateMorphGridItem(option),
            BindActivationWithCleanup = (_, view, activate) => CommonHelpers.BindGuiReleaseActivationWithCleanup(view, activate)
        };

        void Configure(NGenericSelectScreen target)
        {
            target.Configure(options, adapter, builder => BuildMorphScreen(builder, options));
            target.RequestDeferredVisibleRefresh();
        }

        Configure(screen);
        screen.LocaleChanged += () =>
        {
            SelectScreenUiState state = screen.CaptureUiState();
            Configure(screen);
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

    private static void BuildMorphScreen(SelectScreenBuilder<MorphOption> builder, IReadOnlyList<MorphOption> options)
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

        builder.Sorter("name", LocMan.Loc("SORT_NAME", "Name"), CompareMorphOptionName, activeByDefault: true);
        builder.Sorter("id", LocMan.Loc("SORT_ID", "ID"), (left, right) => CompareMorphOptions(left, right, option => option.Model?.Id.ToString() ?? string.Empty));
        builder.Sorter("mod", LocMan.Loc("FILTER_GROUP_MODS", "Mods"), (left, right) => CompareMorphOptions(left, right, option => option.Model is null ? string.Empty : CommonHelpers.GetModName(CommonHelpers.GetModelModId(option.Model))));
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
            try
            {
                TextureRect portrait = new()
                {
                    Texture = character.CharacterSelectIcon,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Position = new Vector2(12f, 4f),
                    Size = new Vector2(218f, 106f)
                };
                button.AddChild(portrait);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"BottledMonsterMorph: could not load character preview '{character.Id}'. {exception.Message}");
            }
        }

        float titleY = option.Kind == MorphOptionKind.Original ? 48f : 112f;
        MegaLabel title = CommonHelpers.CreateButtonLabel(
            "MorphTitle",
            FormatMorphOptionTitle(option),
            new Vector2(12f, titleY),
            new Vector2(218f, 48f),
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

        Control preview = new()
        {
            Name = "MonsterPreview",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = PreviewSize,
            CustomMinimumSize = PreviewSize
        };
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

    private static void TryAddMonsterPreview(Control preview, MonsterModel canonical)
    {
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
            GD.PushWarning($"LoadoutPanel: could not create local monster preview for '{canonical.Id}'. {exception.Message}");
            preview.AddChild(CommonHelpers.CreateButtonLabel(
                "MonsterPreviewFallback",
                canonical.Id.Entry,
                new Vector2(12f, 28f),
                new Vector2(218f, 48f),
                16,
                HorizontalAlignment.Center,
                StsColors.gray));
        }
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

    private static void AddActFilters(SelectScreenBuilder<MonsterModel> builder)
    {
        IReadOnlyList<(string Id, string Label, HashSet<string> MonsterIds)> actFilters = ModelDb.Acts
            .Select(act => (
                Id: FilterId("act", act.Id.Entry),
                Label: FormatActTitle(act),
                MonsterIds: act.AllMonsters.Select(monster => monster.Id.ToString()).ToHashSet(StringComparer.Ordinal)))
            .Where(filter => filter.MonsterIds.Count > 0)
            .ToList();

        if (actFilters.Count == 0)
            return;

        builder.FilterGroup("act", LocMan.Loc("FILTER_GROUP_ACT", "Act"));
        foreach ((string id, string label, HashSet<string> monsterIds) in actFilters)
        {
            builder.Filter(id, label, monster => monsterIds.Contains(monster.Id.ToString()), "act");
        }
    }

    private static void AddMonsterCategoryFilters(
        SelectScreenBuilder<MonsterModel> builder,
        IReadOnlyDictionary<string, HashSet<RoomType>> roomTypesByMonsterId)
    {
        builder.FilterGroup("monster_category", LocMan.Loc("FILTER_GROUP_MONSTER_CATEGORY", "Category"));
        builder.Filter("monster_category_boss", LocMan.Loc("MONSTER_CATEGORY_BOSS", "Boss"), monster => HasRoomType(monster, roomTypesByMonsterId, RoomType.Boss), "monster_category");
        builder.Filter("monster_category_elite", LocMan.Loc("MONSTER_CATEGORY_ELITE", "Elite"), monster => HasRoomType(monster, roomTypesByMonsterId, RoomType.Elite), "monster_category");
        builder.Filter("monster_category_monster", LocMan.Loc("MONSTER_CATEGORY_MONSTER", "Monster"), monster => HasRoomType(monster, roomTypesByMonsterId, RoomType.Monster), "monster_category");
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildActNamesByMonsterId()
    {
        Dictionary<string, List<string>> actNamesByMonsterId = new(StringComparer.Ordinal);
        foreach (ActModel act in ModelDb.Acts)
        {
            string actName = FormatActTitle(act);
            foreach (MonsterModel monster in act.AllMonsters)
            {
                string monsterId = monster.Id.ToString();
                if (!actNamesByMonsterId.TryGetValue(monsterId, out List<string>? actNames))
                {
                    actNames = [];
                    actNamesByMonsterId[monsterId] = actNames;
                }

                if (!actNames.Contains(actName, StringComparer.Ordinal))
                    actNames.Add(actName);
            }
        }

        return actNamesByMonsterId.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private static string GetActSearchText(MonsterModel monster, IReadOnlyDictionary<string, IReadOnlyList<string>> actNamesByMonsterId)
    {
        return actNamesByMonsterId.TryGetValue(monster.Id.ToString(), out IReadOnlyList<string>? actNames)
            ? string.Join(" ", actNames)
            : string.Empty;
    }

    private static Dictionary<string, HashSet<RoomType>> BuildRoomTypesByMonsterId()
    {
        Dictionary<string, HashSet<RoomType>> roomTypesByMonsterId = new(StringComparer.Ordinal);
        foreach (ActModel act in ModelDb.Acts)
        {
            foreach (EncounterModel encounter in act.AllEncounters)
            {
                if (encounter.RoomType is not (RoomType.Boss or RoomType.Elite or RoomType.Monster))
                    continue;

                foreach (MonsterModel monster in encounter.AllPossibleMonsters)
                {
                    string monsterId = monster.Id.ToString();
                    if (!roomTypesByMonsterId.TryGetValue(monsterId, out HashSet<RoomType>? roomTypes))
                    {
                        roomTypes = [];
                        roomTypesByMonsterId[monsterId] = roomTypes;
                    }

                    roomTypes.Add(encounter.RoomType);
                }
            }
        }

        return roomTypesByMonsterId;
    }

    private static bool HasRoomType(
        MonsterModel monster,
        IReadOnlyDictionary<string, HashSet<RoomType>> roomTypesByMonsterId,
        RoomType roomType)
    {
        return roomTypesByMonsterId.TryGetValue(monster.Id.ToString(), out HashSet<RoomType>? roomTypes)
               && roomTypes.Contains(roomType);
    }

    private static string GetRoomTypeSearchText(
        MonsterModel monster,
        IReadOnlyDictionary<string, HashSet<RoomType>> roomTypesByMonsterId)
    {
        return roomTypesByMonsterId.TryGetValue(monster.Id.ToString(), out HashSet<RoomType>? roomTypes)
            ? string.Join(" ", roomTypes.Select(FormatMonsterCategory))
            : string.Empty;
    }

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
            return (act.Index + 1) +": " + act.Title.GetFormattedText();
        }
        catch
        {
            return act.Id.Entry;
        }
    }

    private static MonsterModel? ResolveMonster(string monsterId)
    {
        return ModelDb.Monsters.FirstOrDefault(monster => CommonHelpers.ModelIdMatches(monster, monsterId));
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
}
