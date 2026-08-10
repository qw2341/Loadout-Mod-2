#nullable enable

namespace Loadout.Services.CustomRuns.Compilation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Loadout.PanelItems;
using Loadout.Services.Compatibility;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Persistence;
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
    public static CustomRunCompileResult Compile(CustomRunDefinition source, StartRunLobby lobby)
    {
        CustomRunDefinition definition = CustomRunNormalizationService.Normalize(
            CustomRunNormalizationService.Clone(source));
        List<CustomRunValidationIssue> issues = [.. CustomRunValidator.Validate(definition).Issues];
        ValidateRuntimeSupport(definition, issues);

        List<StartRunLobbyPlayerInfo> players = Sts2Compatibility
            .EnumerateStartRunLobbyPlayers(lobby)
            .OrderBy(player => player.SlotId)
            .ThenBy(player => player.PlayerId)
            .ToList();
        if (players.Count == 0)
            AddError(issues, "Lobby", definition.Id, "The lobby has no players.");

        List<CharacterModel> fixedCharacters = [];
        if (definition.Setup.Character.Mode == SelectionMode.Fixed)
        {
            foreach (string id in definition.Setup.Character.FixedModelIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                CharacterModel? character = ResolveCharacter(id);
                if (character is null)
                    AddError(issues, "Run Setup", definition.Id, $"Unknown character '{id}'.");
                else
                    fixedCharacters.Add(character);
            }
        }

        string seed = CanonicalizeSeed(definition.Setup.RunSeed, lobby.Seed, issues, definition.Id);
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
            CharacterModel character = ResolvePlayerCharacter(
                player,
                seed,
                selectableCharacters,
                fixedCharacters);
            resolvedPlayers.Add(new ResolvedPlayerSetup
            {
                PlayerId = player.PlayerId,
                CharacterModelId = character.Id.ToString(),
                PotionSlots = definition.Setup.PotionSlots,
                StartingGold = definition.Setup.StartingGold,
                StartingCurrentHp = definition.Setup.StartingCurrentHp,
                StartingMaxHp = definition.Setup.StartingMaxHp,
                BaseEnergyPerTurn = definition.Setup.BaseEnergyPerTurn,
                CardsDrawnPerTurn = definition.Setup.CardsDrawnPerTurn
            });
        }

        ResolvedCustomRunSnapshot unhashed = new()
        {
            SourceDefinitionId = definition.Id,
            RunSeed = seed,
            Players = resolvedPlayers,
            RequiredModIds = definition.RequiredModIds
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList()
        };
        ResolvedCustomRunSnapshot snapshot = new()
        {
            SchemaVersion = unhashed.SchemaVersion,
            SourceDefinitionId = unhashed.SourceDefinitionId,
            RunSeed = unhashed.RunSeed,
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
        if (definition.Setup.Character.Mode is SelectionMode.Random or SelectionMode.PlayerChoice)
            AddError(issues, "Run Setup", definition.Id, "Character mode must be Default or Fixed before Play.");
        RejectUnsupportedSelection(definition.Setup.StartingDeck, "starting deck", definition, issues);
        RejectUnsupportedSelection(definition.Setup.StartingRelics, "starting relics", definition, issues);
        RejectUnsupportedSelection(definition.Setup.StartingPotions, "starting potions", definition, issues);

        if (definition.Roles.Count > 0)
            AddError(issues, "Roles", definition.Id, "Roles are not supported by Play yet; remove them or leave this run as a draft.");
        if (definition.PlayerChoices.Count > 0)
            AddError(issues, "Player Choices", definition.Id, "Player choices are not supported by Play yet.");
        if (definition.Rules.Count > 0)
            AddError(issues, "Rules", definition.Id, "Rules are not supported by Play yet.");
        if (definition.Variables.Count > 0)
            AddError(issues, "Variables", definition.Id, "Variables are not supported by Play yet.");
    }

    private static void RejectUnsupportedSelection(
        SelectionSpec selection,
        string label,
        CustomRunDefinition definition,
        List<CustomRunValidationIssue> issues)
    {
        if (selection.Mode != SelectionMode.Default || HasCustomPool(selection.Pool))
            AddError(issues, "Run Setup", definition.Id, $"Non-default {label} are not supported by Play yet.");
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
        IReadOnlyList<CharacterModel> fixedCharacters)
    {
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
}
