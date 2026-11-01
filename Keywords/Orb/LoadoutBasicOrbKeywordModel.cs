#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Loadout.Services.CardModification;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;

public abstract class LoadoutBasicOrbKeywordModel : LoadoutCardKeywordModel
{
    public abstract LoadoutBasicMultiplayerTargetMode? TargetMode { get; }
    public override LoadoutKeywordEditorSection EditorSection => LoadoutKeywordEditorSection.BasicOrb;
    public override string? CardTextLocKey => "LOADOUT-BASIC_ORB.entries";
    public override bool ChangesTargeting => TargetMode == LoadoutBasicMultiplayerTargetMode.AnotherPlayer;
    public override bool RequiresAnotherPlayerTarget => ChangesTargeting;

    public override LoadoutCardKeywordEntry CreateDefaultEntry() => new()
    {
        KeywordKey = StorageKey,
        OrbId = ModelDb.GetId<LightningOrb>().ToString()
    };

    public static bool IsOrbKey(string? key) =>
        string.Equals(key, LoadoutKeywords.BasicOrbKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AnotherPlayerOrbKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AllOtherPlayersOrbKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, LoadoutKeywords.AllPlayersOrbKey, StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<OrbModel> GetAvailableOrbs() =>
        ModelDb.AllAbstractModelSubtypes
            .Where(type => !type.IsAbstract && typeof(OrbModel).IsAssignableFrom(type)
                && !type.Name.StartsWith("Mock", StringComparison.Ordinal)
                && !type.Name.StartsWith("Deprecated", StringComparison.Ordinal))
            .Select(ModelDb.DebugOrb).OfType<OrbModel>();

    public static bool TryResolveOrb(string? id, out OrbModel orb)
    {
        orb = null!;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string entry = id.Trim();
        int separator = entry.IndexOf('.');
        if (separator >= 0)
        {
            if (!entry[..separator].Equals("ORB", StringComparison.OrdinalIgnoreCase))
                return false;
            entry = entry[(separator + 1)..];
        }
        orb = ModelDb.GetByIdOrNull<OrbModel>(new ModelId("ORB", entry))!;
        return orb is not null;
    }

    public static string GetOrbTitle(string? id) =>
        TryResolveOrb(id, out OrbModel orb) ? orb.Title.GetFormattedText() : id ?? string.Empty;

    public static string FormatEntry(LoadoutCardKeywordEntry entry) =>
        FormatEntry(entry, entry.Amount.ToString(CultureInfo.InvariantCulture));

    public static string FormatEntry(LoadoutCardKeywordEntry entry, string amount)
    {
        if (!LoadoutKeywordRegistry.TryGet(entry.KeywordKey, out LoadoutKeywordModel model)
            || model is not LoadoutBasicOrbKeywordModel)
            return string.Empty;
        LocString text = new("card_keywords", model.TitleLocKey.Replace(".title", ".channelText"));
        text.AddObj("Amount", amount);
        text.AddObj("Orb", GetOrbTitle(entry.OrbId));
        return text.GetFormattedText();
    }

    protected override void AddCardTextVariables(CardModel card, LocString cardText) =>
        cardText.AddObj("Orbs", LoadoutCardKeywordState.FormatEntryDescriptions(card, StorageKey, FormatEntry));

    public override IEnumerable<IHoverTip> GetAdditionalCardHoverTips(CardModel card)
    {
        yield return HoverTipFactory.Static(StaticHoverTip.Channeling);
        HashSet<string> shown = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey))
        {
            if (TryResolveOrb(entry.OrbId, out OrbModel orb) && shown.Add(orb.Id.ToString()))
                yield return orb.DumbHoverTip;
        }
    }

    public override object CaptureBeforeOnPlay(CardModel card, CardPlay cardPlay) =>
        LoadoutCardKeywordState.GetEffectiveEntries(card, StorageKey).ToArray();

    public override async Task AfterOnPlay(CardModel card, PlayerChoiceContext choiceContext,
        CardPlay cardPlay, object? capturedState)
    {
        var combat = card.CombatState;
        if (combat is null || CombatManager.Instance.IsOverOrEnding
            || capturedState is not LoadoutCardKeywordEntry[] entries)
            return;
        IReadOnlyList<Creature> targets = LoadoutBasicMultiplayerKeywordModel.ResolvePlayerTargets(card, cardPlay, TargetMode);
        if (targets.Count == 0)
            return;

        bool animated = false;
        foreach (var entry in entries)
        {
            if (entry.Amount <= 0 || !TryResolveOrb(entry.OrbId, out OrbModel canonical))
                continue;
            if (!animated)
            {
                await CreatureCmd.TriggerAnim(card.Owner.Creature, "Cast", card.Owner.Character.CastAnimDelay);
                animated = true;
            }
            foreach (Creature target in targets)
            {
                for (int i = 0; i < entry.Amount; i++)
                {
                    if (CombatManager.Instance.IsOverOrEnding || !ReferenceEquals(card.CombatState, combat))
                        return;
                    if (!target.IsAlive || !ReferenceEquals(target.CombatState, combat)
                        || target.Player?.PlayerCombatState is null)
                        break;
                    await OrbCmd.Channel(choiceContext, canonical.ToMutable(), target.Player);
                }
            }
        }
    }
}

public sealed class BasicOrbKeyword : LoadoutBasicOrbKeywordModel
{
    public static BasicOrbKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.BasicOrb;
    public override string StorageKey => LoadoutKeywords.BasicOrbKey;
    public override string TitleLocKey => "LOADOUT-BASIC_ORB.title";
    public override LoadoutBasicMultiplayerTargetMode? TargetMode => null;
}

public sealed class AnotherPlayerOrbKeyword : LoadoutBasicOrbKeywordModel
{
    public static AnotherPlayerOrbKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AnotherPlayerOrb;
    public override string StorageKey => LoadoutKeywords.AnotherPlayerOrbKey;
    public override string TitleLocKey => "LOADOUT-BASIC_ORB_ANOTHER_PLAYER.title";
    public override LoadoutBasicMultiplayerTargetMode? TargetMode => LoadoutBasicMultiplayerTargetMode.AnotherPlayer;
}

public sealed class AllOtherPlayersOrbKeyword : LoadoutBasicOrbKeywordModel
{
    public static AllOtherPlayersOrbKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AllOtherPlayersOrb;
    public override string StorageKey => LoadoutKeywords.AllOtherPlayersOrbKey;
    public override string TitleLocKey => "LOADOUT-BASIC_ORB_ALL_OTHER_PLAYERS.title";
    public override LoadoutBasicMultiplayerTargetMode? TargetMode => LoadoutBasicMultiplayerTargetMode.AllOtherPlayers;
}

public sealed class AllPlayersOrbKeyword : LoadoutBasicOrbKeywordModel
{
    public static AllPlayersOrbKeyword Instance { get; } = new();
    public override CardKeyword Keyword => LoadoutKeywords.AllPlayersOrb;
    public override string StorageKey => LoadoutKeywords.AllPlayersOrbKey;
    public override string TitleLocKey => "LOADOUT-BASIC_ORB_ALL_PLAYERS.title";
    public override LoadoutBasicMultiplayerTargetMode? TargetMode => LoadoutBasicMultiplayerTargetMode.AllPlayers;
}
