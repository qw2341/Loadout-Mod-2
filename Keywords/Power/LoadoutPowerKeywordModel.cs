#nullable enable

namespace Loadout.Keywords;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using Loadout.PanelItems;
using Loadout.Patches.Cards.CardModification;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

public abstract class LoadoutPowerKeywordModel : LoadoutKeywordModel
{
    public override LoadoutKeywordPresentation Presentation =>
        LoadoutKeywordPresentation.DescriptionOnly;

    public override LoadoutKeywordEditorSection EditorSection =>
        LoadoutKeywordEditorSection.Power;

    public override LoadoutKeywordEditorControlKind EditorControlKind =>
        LoadoutKeywordEditorControlKind.RepeatablePower;
}

public static class LoadoutPowerKeywordState
{
    private const int UnknownPowerWarningLimit = 32;
    private sealed class ExplicitState
    {
        public ExplicitState(
            IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            IReadOnlyList<LoadoutPowerKeywordEntry>? upgradeEntries)
        {
            BaseEntries = LoadoutPowerKeywordEntry.CloneList(baseEntries);
            UpgradeEntries = LoadoutPowerKeywordEntry.CloneList(upgradeEntries);
        }

        public List<LoadoutPowerKeywordEntry>? BaseEntries { get; }
        public List<LoadoutPowerKeywordEntry>? UpgradeEntries { get; }
    }

    private static ConditionalWeakTable<CardModel, ExplicitState> ExplicitStates = new();
    private static readonly Dictionary<string, PowerModel> PowersById =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedUnknownPowerIds =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _powerCacheBuilt;
    private static bool _unknownWarningLimitReported;

    public static void Reset()
    {
        ExplicitStates = new ConditionalWeakTable<CardModel, ExplicitState>();
        PowersById.Clear();
        WarnedUnknownPowerIds.Clear();
        _powerCacheBuilt = false;
        _unknownWarningLimitReported = false;
    }

    public static void SetExplicitState(
        CardModel card,
        CardModificationSpec state)
    {
        SetExplicitState(
            card,
            state.PowerKeywordEntries,
            state.UpgradeModification.PowerKeywordEntries);
    }

    public static void SetExplicitState(
        CardModel card,
        IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        IReadOnlyList<LoadoutPowerKeywordEntry>? upgradeEntries)
    {
        ExplicitStates.Remove(card);
        ExplicitStates.Add(card, new ExplicitState(baseEntries, upgradeEntries));
    }

    public static void CopyExplicitState(CardModel source, CardModel destination)
    {
        if (!ExplicitStates.TryGetValue(source, out ExplicitState? state))
            return;

        SetExplicitState(destination, state.BaseEntries, state.UpgradeEntries);
        Synchronize(destination);
    }

    public static void ClearExplicitState(CardModel card)
    {
        ExplicitStates.Remove(card);
    }

    public static void Synchronize(CardModel card)
    {
        foreach (LoadoutPowerKeywordModel model in
                 LoadoutKeywordRegistry.All.OfType<LoadoutPowerKeywordModel>())
        {
            bool enabled = HasEffectiveEntries(card, model.StorageKey);
            bool present = LoadoutKeywords.Has(card, model.Keyword);
            if (enabled && !present)
                card.AddKeyword(model.Keyword);
            else if (!enabled && present)
                card.RemoveKeyword(model.Keyword);
        }

        LoadoutKeywordRegistry.SynchronizeDynamicVars(card);
    }

    public static IEnumerable<LoadoutPowerKeywordEntry> GetEffectiveEntries(
        CardModel card,
        string keywordKey)
    {
        ResolveLists(card, out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? upgradeEntries);

        if (baseEntries is not null)
        {
            foreach (LoadoutPowerKeywordEntry entry in baseEntries)
            {
                if (MatchesKeyword(entry, keywordKey))
                    yield return entry;
            }
        }

        int upgradeLevel = Math.Max(0, card.CurrentUpgradeLevel);
        if (upgradeLevel == 0 || upgradeEntries is null)
            yield break;

        for (int level = 0; level < upgradeLevel; level++)
        {
            foreach (LoadoutPowerKeywordEntry entry in upgradeEntries)
            {
                if (MatchesKeyword(entry, keywordKey))
                    yield return entry;
            }
        }
    }

    public static bool HasEffectiveEntries(CardModel card, string keywordKey)
    {
        ResolveLists(card, out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
            out IReadOnlyList<LoadoutPowerKeywordEntry>? upgradeEntries);
        return baseEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true
               || card.CurrentUpgradeLevel > 0
               && upgradeEntries?.Any(entry => MatchesKeyword(entry, keywordKey)) == true;
    }

    public static bool HasConfiguredEntries(
        IReadOnlyList<LoadoutPowerKeywordEntry>? entries) =>
        entries?.Any(entry => !entry.IsEmpty) == true;

    public static bool TryResolvePower(string? powerId, out PowerModel power)
    {
        EnsurePowerCache();
        if (!string.IsNullOrWhiteSpace(powerId)
            && PowersById.TryGetValue(powerId.Trim(), out PowerModel? resolved))
        {
            power = resolved;
            return true;
        }

        power = null!;
        return false;
    }

    public static string GetDefaultStrengthPowerId()
    {
        EnsurePowerCache();
        PowerModel? strength = ModelDb.AllPowers.FirstOrDefault(power => power is StrengthPower);
        return strength?.Id.ToString() ?? "Strength";
    }

    public static string FormatEntries(CardModel card, string keywordKey)
    {
        string separator = LocMan.Loc(
            "CARD_MOD_POWER_KEYWORD_SEPARATOR",
            ", ");
        return string.Join(
            separator,
            GetEffectiveEntries(card, keywordKey).Select(entry =>
            {
                string title = TryResolvePower(entry.PowerId, out PowerModel power)
                    ? CommonHelpers.FormatPowerTitle(power)
                    : GetPowerIdFallback(entry.PowerId);
                return LocMan.Loc(
                    "CARD_MOD_POWER_KEYWORD_ENTRY",
                    "{0} {1}",
                    entry.Amount,
                    title);
            }));
    }

    public static void WarnUnknownPower(string powerId)
    {
        if (WarnedUnknownPowerIds.Contains(powerId))
            return;
        if (WarnedUnknownPowerIds.Count >= UnknownPowerWarningLimit)
        {
            if (!_unknownWarningLimitReported)
            {
                _unknownWarningLimitReported = true;
                GD.PushWarning(
                    "Loadout power keyword: additional unknown power warnings were suppressed.");
            }
            return;
        }

        if (WarnedUnknownPowerIds.Add(powerId))
            GD.PushWarning($"Loadout power keyword: unknown power '{powerId}' was skipped.");
    }

    private static void ResolveLists(
        CardModel card,
        out IReadOnlyList<LoadoutPowerKeywordEntry>? baseEntries,
        out IReadOnlyList<LoadoutPowerKeywordEntry>? upgradeEntries)
    {
        if (ExplicitStates.TryGetValue(card, out ExplicitState? explicitState))
        {
            baseEntries = explicitState.BaseEntries;
            upgradeEntries = explicitState.UpgradeEntries;
            return;
        }

        baseEntries = null;
        upgradeEntries = null;
        if (PermanentCardModificationStore.TryGetDelta(
                card.Id,
                out CardModificationDelta? permanent))
        {
            baseEntries = permanent.PowerKeywordEntries;
            upgradeEntries = permanent.UpgradeModification.PowerKeywordEntries;
        }

        if (!CardModificationFields.TryGet(card, out CardModificationCardData temporary))
            return;

        if (temporary.Delta.PowerKeywordEntries is not null)
            baseEntries = temporary.Delta.PowerKeywordEntries;
        if (temporary.Delta.UpgradeModification.PowerKeywordEntries is not null)
            upgradeEntries = temporary.Delta.UpgradeModification.PowerKeywordEntries;
    }

    private static bool MatchesKeyword(
        LoadoutPowerKeywordEntry entry,
        string keywordKey) =>
        string.Equals(
            entry.KeywordKey,
            keywordKey,
            StringComparison.OrdinalIgnoreCase);

    private static string GetPowerIdFallback(string powerId)
    {
        string trimmed = powerId.Trim();
        int separator = Math.Max(trimmed.LastIndexOf(':'), trimmed.LastIndexOf('/'));
        return separator >= 0 && separator + 1 < trimmed.Length
            ? trimmed[(separator + 1)..]
            : trimmed;
    }

    private static void EnsurePowerCache()
    {
        if (_powerCacheBuilt)
            return;

        PowersById.Clear();
        foreach (PowerModel power in ModelDb.AllPowers)
        {
            PowersById[power.Id.ToString()] = power;
            PowersById.TryAdd(power.Id.Entry, power);
        }
        _powerCacheBuilt = true;
    }
}
