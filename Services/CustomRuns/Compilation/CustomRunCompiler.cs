#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Loadout.PanelItems;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Catalog;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
using Loadout.Services.Loadouts;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;

public sealed class CustomRunCompileResult
{
    public ResolvedCustomRunSnapshot? Snapshot { get; init; }
    public IReadOnlyList<CustomRunValidationIssue> Issues { get; init; } = [];
    public bool IsValid => Snapshot is not null && Issues.All(issue => issue.Severity != CustomRunValidationSeverity.Error);
}

public static class CustomRunCompiler
{
    public static CustomRunValidationResult ValidateForLobbyLoad(CustomRunDefinition source)
    {
        CustomRunDefinition definition = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(source));
        CustomRunValidationResult result = CustomRunValidator.Validate(definition);
        ValidateRuntimeSupport(definition, result.Issues);
        return result;
    }

    public static CustomRunCompileResult Compile(
        CustomRunDefinition source,
        StartRunLobby lobby,
        IReadOnlyDictionary<ulong, string?>? roleAssignments = null)
    {
        CustomRunDefinition definition = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(source));
        List<CustomRunValidationIssue> issues = [.. ValidateForLobbyLoad(definition).Issues];

        List<StartRunLobbyPlayerInfo> players = Sts2Compatibility
            .EnumerateStartRunLobbyPlayers(lobby)
            .OrderBy(player => player.SlotId)
            .ThenBy(player => player.PlayerId)
            .ToList();
        if (players.Count == 0)
            AddError(issues, "Lobby", definition.Id, "The lobby has no players.");

        string seed = CanonicalizeSeed(definition.Setup.RunSeed, lobby.Seed, issues, definition.Id);
        CustomRunRoleResolutionResult roleResolution = CustomRunRoleAssignmentResolver.Resolve(
            definition,
            players.Select(player => player.PlayerId),
            roleAssignments ?? new Dictionary<ulong, string?>(),
            seed);
        if (!roleResolution.IsValid)
            AddError(issues, "Roles", definition.Id, roleResolution.Error);

        Dictionary<string, ResolvedSetupTemplate> setupTemplates = new(StringComparer.Ordinal)
        {
            [string.Empty] = ResolveSetupTemplate(definition.Setup, "Run Setup", definition.Id, issues)
        };
        foreach (RoleDefinition role in definition.Roles)
            setupTemplates[role.Id] = ResolveSetupTemplate(role.Setup, "Roles", role.Id, issues);

        IReadOnlyList<string> missingMods = GetMissingRequiredMods(definition.RequiredModIds);
        if (missingMods.Count > 0)
        {
            AddError(
                issues,
                "Dependencies",
                definition.Id,
                $"Missing required mod: {missingMods[0]}.");
        }

        if (issues.Any(issue => issue.Severity == CustomRunValidationSeverity.Error))
            return new CustomRunCompileResult { Issues = issues };

        IReadOnlyList<CharacterModel> selectableCharacters = ModelDb.AllCharacters
            .Where(character => !IsRandomCharacter(character))
            .OrderBy(character => character.Id.ToString(), StringComparer.Ordinal)
            .ToList();
        if (selectableCharacters.Count == 0)
        {
            AddError(issues, "Run Setup", definition.Id, "No playable characters are available.");
            return new CustomRunCompileResult { Issues = issues };
        }

        List<ResolvedPlayerSetup> resolvedPlayers = [];
        foreach (StartRunLobbyPlayerInfo player in players)
        {
            roleResolution.Assignments.TryGetValue(player.PlayerId, out string? roleId);
            ResolvedSetupTemplate template = roleId is not null
                                             && setupTemplates.TryGetValue(roleId, out ResolvedSetupTemplate? roleTemplate)
                ? roleTemplate
                : setupTemplates[string.Empty];
            CharacterModel character = ResolvePlayerCharacter(
                player,
                seed,
                selectableCharacters,
                template.FixedCharacters,
                template.RandomCharacter);
            IStartingLoadoutDefinition loadout = ResolveStartingLoadout(template.Setup, character);
            ResolvedStartingLoadout resolvedLoadout = ResolveStartingLoadout(
                loadout,
                template.Setup.PotionSlots,
                roleId is null ? "Run Setup" : "Roles",
                roleId ?? definition.Id,
                issues);
            resolvedPlayers.Add(new ResolvedPlayerSetup
            {
                PlayerId = player.PlayerId,
                CharacterModelId = character.Id.ToString(),
                RoleId = roleId,
                DeckModelIds = resolvedLoadout.DeckModelIds,
                OverrideDeck = resolvedLoadout.OverrideDeck,
                DeckEntries = resolvedLoadout.DeckEntries.Select(entry => entry.Clone()).ToList(),
                RelicModelIds = resolvedLoadout.RelicModelIds,
                OverrideRelics = resolvedLoadout.OverrideRelics,
                RelicEntries = resolvedLoadout.RelicEntries.Select(entry => entry.Clone()).ToList(),
                PotionModelIds = resolvedLoadout.PotionModelIds,
                OverridePotions = resolvedLoadout.OverridePotions,
                StartingPowers = resolvedLoadout.StartingPowers.Select(power => new StartingPowerDefinition
                {
                    ModelId = power.ModelId,
                    Amount = power.Amount
                }).ToList(),
                StartingMorphModelId = resolvedLoadout.StartingMorphModelId,
                PotionSlots = template.Setup.PotionSlots,
                StartingGold = template.Setup.StartingGold,
                StartingCurrentHp = template.Setup.StartingCurrentHp,
                StartingMaxHp = template.Setup.StartingMaxHp,
                BaseEnergyPerTurn = template.Setup.BaseEnergyPerTurn,
                CardsDrawnPerTurn = template.Setup.CardsDrawnPerTurn
            });
        }

        if (issues.Any(issue => issue.Severity == CustomRunValidationSeverity.Error))
            return new CustomRunCompileResult { Issues = issues };

        IReadOnlyList<string> requiredModIds = BuildRequiredModIds(definition.RequiredModIds, resolvedPlayers);

        ResolvedCustomRunSnapshot unhashed = new()
        {
            SourceDefinitionId = definition.Id,
            RunSeed = seed,
            AscensionLevel = definition.Setup.StartingAscension,
            Players = resolvedPlayers,
            RequiredModIds = requiredModIds
        };
        ResolvedCustomRunSnapshot snapshot = new()
        {
            SchemaVersion = unhashed.SchemaVersion,
            SourceDefinitionId = unhashed.SourceDefinitionId,
            RunSeed = unhashed.RunSeed,
            AscensionLevel = unhashed.AscensionLevel,
            Players = unhashed.Players,
            Rules = unhashed.Rules,
            Variables = unhashed.Variables,
            RequiredModIds = unhashed.RequiredModIds,
            SnapshotHash = CustomRunHashService.Compute(unhashed)
        };
        return new CustomRunCompileResult { Snapshot = snapshot, Issues = issues };
    }

    internal static IReadOnlyList<string> GetMissingRequiredMods(IEnumerable<string> requiredModIds)
    {
        HashSet<string> loaded = CommonHelpers.GetLoadedModIdsByAssembly()
            .Values
            .Append("slaythespire2")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requiredModIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && !loaded.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static CharacterModel? ResolveCharacter(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        return ModelDb.AllCharacters.FirstOrDefault(character =>
            string.Equals(character.Id.ToString(), id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(character.Id.Entry, id, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRuntimeSupport(
        CustomRunDefinition definition,
        List<CustomRunValidationIssue> issues)
    {
        ValidateSetupRuntimeSupport(definition.Setup, "Run Setup", definition.Id, issues);
        foreach (RoleDefinition role in definition.Roles)
            ValidateSetupRuntimeSupport(role.Setup, "Roles", role.Id, issues);
        if (definition.PlayerChoices.Count > 0)
            AddError(issues, "Player Choices", definition.Id, "Player choices are not supported by Play yet.");
        if (definition.Rules.Count > 0)
            AddError(issues, "Rules", definition.Id, "Rules are not supported by Play yet.");
        if (definition.Variables.Count > 0)
            AddError(issues, "Variables", definition.Id, "Variables are not supported by Play yet.");
    }

    private static void ValidateSetupRuntimeSupport(
        RunSetupDefinition setup,
        string section,
        string objectId,
        List<CustomRunValidationIssue> issues)
    {
        if (setup.Character.Mode == SelectionMode.PlayerChoice)
            AddError(issues, section, objectId, "Character mode cannot use a separate player choice before Play.");
        if (setup.StartingLoadoutMode == StartingLoadoutMode.Unified)
        {
            ValidateStartingLoadoutRuntimeSupport(setup, section, objectId, issues);
        }
        else
        {
            foreach (CharacterStartingLoadoutDefinition loadout in setup.CharacterStartingLoadouts)
                ValidateStartingLoadoutRuntimeSupport(loadout, section, objectId, issues);
        }
    }

    private static void ValidateStartingLoadoutRuntimeSupport(
        IStartingLoadoutDefinition loadout,
        string section,
        string objectId,
        List<CustomRunValidationIssue> issues)
    {
        RejectUnsupportedSelectionMode(loadout.StartingDeck, "starting deck", section, objectId, issues);
        RejectUnsupportedSelectionMode(loadout.StartingRelics, "starting relics", section, objectId, issues);
        RejectUnsupportedSelectionMode(loadout.StartingPotions, "starting potions", section, objectId, issues);
    }

    private static void RejectUnsupportedSelectionMode(
        SelectionSpec selection,
        string label,
        string section,
        string objectId,
        List<CustomRunValidationIssue> issues)
    {
        if (selection.Mode is SelectionMode.Random or SelectionMode.PlayerChoice)
            AddError(issues, section, objectId, $"Random and player-choice {label} are not supported by Play yet.");
        if (HasCustomPool(selection.Pool))
            AddError(issues, section, objectId, $"Filtered {label} pools are not supported by Play yet.");
    }

    private static ResolvedSetupTemplate ResolveSetupTemplate(
        RunSetupDefinition setup,
        string section,
        string objectId,
        List<CustomRunValidationIssue> issues)
    {
        List<CharacterModel> fixedCharacters = [];
        if (setup.Character.Mode is SelectionMode.Fixed or SelectionMode.Random)
        {
            foreach (string id in setup.Character.FixedModelIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CharacterModel? character = ResolveCharacter(id);
                if (character is null)
                    AddError(issues, section, objectId, $"Unknown character '{id}'.");
                else if (IsRandomCharacter(character))
                    AddError(issues, section, objectId, "The random character cannot be part of a character filter.");
                else
                    fixedCharacters.Add(character);
            }
        }
        return new ResolvedSetupTemplate(
            setup,
            fixedCharacters,
            setup.Character.Mode == SelectionMode.Random);
    }

    private static IStartingLoadoutDefinition ResolveStartingLoadout(
        RunSetupDefinition setup,
        CharacterModel character)
    {
        if (setup.StartingLoadoutMode == StartingLoadoutMode.Unified)
            return setup;
        return setup.CharacterStartingLoadouts.FirstOrDefault(loadout =>
                   string.Equals(loadout.CharacterModelId, character.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                   || string.Equals(loadout.CharacterModelId, character.Id.Entry, StringComparison.OrdinalIgnoreCase))
               ?? new CharacterStartingLoadoutDefinition { CharacterModelId = character.Id.ToString() };
    }

    private static ResolvedStartingLoadout ResolveStartingLoadout(
        IStartingLoadoutDefinition loadout,
        int? potionSlots,
        string section,
        string objectId,
        List<CustomRunValidationIssue> issues)
    {
        IReadOnlyList<string> deckModelIds = ResolveFixedModelIds(loadout.StartingDeck);
        IReadOnlyList<string> relicModelIds = ResolveFixedModelIds(loadout.StartingRelics);
        IReadOnlyList<string> potionModelIds = ResolveFixedModelIds(loadout.StartingPotions);
        if (potionModelIds.Count > (potionSlots ?? 3))
        {
            AddError(
                issues,
                section,
                objectId,
                $"Starting potions ({potionModelIds.Count}) exceed the configured potion slots ({potionSlots ?? 3}).");
        }

        string? startingMorphModelId = loadout.StartingMorphModelId;
        if (startingMorphModelId is not null
            && CustomRunCatalogService.TryResolveMorph(startingMorphModelId, out AbstractModel morphModel))
        {
            startingMorphModelId = morphModel.Id.ToString();
        }

        return new ResolvedStartingLoadout(
            deckModelIds,
            loadout.StartingDeck.Mode == SelectionMode.Fixed,
            loadout.StartingCardEntries.Select(entry => entry.Clone()).ToList(),
            relicModelIds,
            loadout.StartingRelics.Mode == SelectionMode.Fixed,
            loadout.StartingRelicEntries.Select(entry => entry.Clone()).ToList(),
            potionModelIds,
            loadout.StartingPotions.Mode == SelectionMode.Fixed,
            loadout.StartingPowers.Select(power => new StartingPowerDefinition
            {
                ModelId = CustomRunCatalogService.CanonicalizeModelId(SelectionModelKind.Power, power.ModelId),
                Amount = power.Amount
            }).ToList(),
            startingMorphModelId);
    }

    private static IReadOnlyList<string> ResolveFixedModelIds(SelectionSpec selection)
    {
        if (selection.Mode != SelectionMode.Fixed)
            return [];

        return selection.FixedModelIds
            .Select(id => CustomRunCatalogService.CanonicalizeModelId(selection.Kind, id))
            .ToList();
    }

    private static IReadOnlyList<string> BuildRequiredModIds(
        IEnumerable<string> authoredModIds,
        IEnumerable<ResolvedPlayerSetup> players)
    {
        HashSet<string> required = authoredModIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedPlayerSetup player in players)
        {
            AddModelMod(required, SelectionModelKind.Character, player.CharacterModelId);
            foreach (string id in player.DeckModelIds)
                AddModelMod(required, SelectionModelKind.Card, id);
            foreach (string id in player.RelicModelIds)
                AddModelMod(required, SelectionModelKind.Relic, id);
            foreach (string id in player.PotionModelIds)
                AddModelMod(required, SelectionModelKind.Potion, id);
            foreach (StartingPowerDefinition power in player.StartingPowers)
                AddModelMod(required, SelectionModelKind.Power, power.ModelId);
            if (player.StartingMorphModelId is not null
                && CustomRunCatalogService.TryResolveMorph(player.StartingMorphModelId, out AbstractModel morph))
            {
                required.Add(CommonHelpers.GetModelModId(morph));
            }
        }

        required.RemoveWhere(id => string.Equals(id, "slaythespire2", StringComparison.OrdinalIgnoreCase));
        return required.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private static void AddModelMod(
        ISet<string> required,
        SelectionModelKind kind,
        string modelId)
    {
        if (CustomRunCatalogService.TryResolve(kind, modelId, out CustomRunCatalogEntry entry))
            required.Add(entry.ModId);
    }

    private static bool HasCustomPool(SelectionPoolDefinition pool)
    {
        return pool.IncludedModelIds.Count > 0
               || pool.ExcludedModelIds.Count > 0
               || pool.AllowedModIds.Count > 0
               || pool.ExcludedModIds.Count > 0
               || pool.Categories.Count > 0
               || pool.Types.Count > 0
               || pool.AllowDuplicates
               || pool.MaximumCopiesPerItem != 1;
    }

    private static string CanonicalizeSeed(
        string? authoredSeed,
        string? lobbySeed,
        List<CustomRunValidationIssue> issues,
        string definitionId)
    {
        try
        {
            string seed = !string.IsNullOrWhiteSpace(authoredSeed)
                ? authoredSeed
                : !string.IsNullOrWhiteSpace(lobbySeed)
                    ? lobbySeed
                    : SeedHelper.GetRandomSeed();
            return SeedHelper.CanonicalizeSeed(seed);
        }
        catch (Exception exception)
        {
            AddError(issues, "Run Setup", definitionId, $"Invalid seed: {exception.Message}");
            return string.Empty;
        }
    }

    private static CharacterModel ResolveDefaultCharacter(
        StartRunLobbyPlayerInfo player,
        string seed,
        IReadOnlyList<CharacterModel> selectableCharacters)
    {
        if (player.Character is not null && !IsRandomCharacter(player.Character))
            return player.Character;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{player.SlotId}:{player.PlayerId}"));
        int index = (int)(BitConverter.ToUInt32(digest, 0) % (uint)selectableCharacters.Count);
        return selectableCharacters[index];
    }

    private static CharacterModel ResolvePlayerCharacter(
        StartRunLobbyPlayerInfo player,
        string seed,
        IReadOnlyList<CharacterModel> selectableCharacters,
        IReadOnlyList<CharacterModel> fixedCharacters,
        bool randomCharacter)
    {
        if (randomCharacter)
        {
            IReadOnlyList<CharacterModel> randomPool = fixedCharacters.Count > 0
                ? fixedCharacters.OrderBy(character => character.Id.ToString(), StringComparer.Ordinal).ToList()
                : selectableCharacters;
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{seed}:random_character:{player.SlotId}:{player.PlayerId}"));
            int index = (int)(BitConverter.ToUInt32(digest, 0) % (uint)randomPool.Count);
            return randomPool[index];
        }
        if (fixedCharacters.Count == 0)
            return ResolveDefaultCharacter(player, seed, selectableCharacters);

        if (player.Character is not null
            && !IsRandomCharacter(player.Character)
            && fixedCharacters.Any(character => character.Id == player.Character.Id))
        {
            return player.Character;
        }

        return fixedCharacters[0];
    }

    private static bool IsRandomCharacter(CharacterModel character)
    {
        return character.GetType().Name.Contains("Random", StringComparison.OrdinalIgnoreCase)
               || character.Id.Entry.Contains("RANDOM", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddError(
        List<CustomRunValidationIssue> issues,
        string section,
        string objectId,
        string message)
    {
        issues.Add(new CustomRunValidationIssue(
            CustomRunValidationSeverity.Error,
            section,
            objectId,
            message));
    }

    private sealed record ResolvedSetupTemplate(
        RunSetupDefinition Setup,
        IReadOnlyList<CharacterModel> FixedCharacters,
        bool RandomCharacter);

    private sealed record ResolvedStartingLoadout(
        IReadOnlyList<string> DeckModelIds,
        bool OverrideDeck,
        IReadOnlyList<SavedCardLoadoutEntry> DeckEntries,
        IReadOnlyList<string> RelicModelIds,
        bool OverrideRelics,
        IReadOnlyList<SavedRelicLoadoutEntry> RelicEntries,
        IReadOnlyList<string> PotionModelIds,
        bool OverridePotions,
        IReadOnlyList<StartingPowerDefinition> StartingPowers,
        string? StartingMorphModelId);
}
