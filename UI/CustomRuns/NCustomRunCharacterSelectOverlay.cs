#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

public partial class NCustomRunCharacterSelectOverlay : Control
{
    private Control? _sourceScreen;
    private StartRunLobby? _lobby;
    private TextureRect? _confirmImage;
    private readonly Dictionary<NCharacterSelectButton, bool> _originalVisibility = [];
    private float _rainbowHue;
    private bool _isLoaded;

    public void Init(Control sourceScreen, StartRunLobby lobby)
    {
        _sourceScreen = sourceScreen;
        _lobby = lobby;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 23;
        _confirmImage = _sourceScreen?.GetNodeOrNull<TextureRect>("ConfirmButton/Image");
        RememberCharacterVisibility();
        CustomRunLobbyService.LoadedDefinitionChanged += OnLoadedDefinitionChanged;
        CustomRunRoleAssignmentService.Changed += OnAssignmentsChanged;
        CustomRunRoleAssignmentService.AssignmentRejected += OnAssignmentRejected;
        RefreshLoadedRun();
    }

    public override void _ExitTree()
    {
        CustomRunLobbyService.LoadedDefinitionChanged -= OnLoadedDefinitionChanged;
        CustomRunRoleAssignmentService.Changed -= OnAssignmentsChanged;
        CustomRunRoleAssignmentService.AssignmentRejected -= OnAssignmentRejected;
        RestoreCharacterVisibility();
        if (_confirmImage is not null && GodotObject.IsInstanceValid(_confirmImage))
            _confirmImage.SelfModulate = Colors.White;
    }

    public override void _Process(double delta)
    {
        if (!_isLoaded
            || _confirmImage is null
            || !GodotObject.IsInstanceValid(_confirmImage))
        {
            return;
        }

        _rainbowHue = Mathf.PosMod(
            _rainbowHue + (float)delta * NLoadoutPanelButton.RainbowSpeed * Mathf.Tau,
            Mathf.Tau);
        _confirmImage.SelfModulate = NLoadoutPanelButton.GetSineRainbowColor(_rainbowHue);
    }

    public void RefreshLoadedRun()
    {
        if (_lobby is null)
            return;

        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(_lobby);
        bool loaded = definition is not null;
        _isLoaded = loaded;
        SetProcess(loaded);
        if (!loaded)
        {
            if (_confirmImage is not null && GodotObject.IsInstanceValid(_confirmImage))
                _confirmImage.SelfModulate = Colors.White;
            RestoreCharacterVisibility();
            NCustomRunEditorEntry.RefreshAttachedState(_sourceScreen, _lobby, null);
            return;
        }

        NCustomRunEditorEntry.RefreshAttachedState(_sourceScreen, _lobby, definition);
        ApplyCharacterRestrictions(definition!);
    }

    public void ShowError(string text)
    {
        NCustomRunEditorEntry.ShowAttachedStatus(_sourceScreen, text, error: true);
    }

    private void OnLoadedDefinitionChanged(StartRunLobby lobby)
    {
        if (ReferenceEquals(lobby, _lobby))
            RefreshLoadedRun();
    }

    private void OnAssignmentsChanged(StartRunLobby lobby)
    {
        if (ReferenceEquals(lobby, _lobby))
            RefreshLoadedRun();
    }

    private void OnAssignmentRejected(StartRunLobby lobby, string error)
    {
        if (ReferenceEquals(lobby, _lobby))
            ShowError(error);
    }

    private void RememberCharacterVisibility()
    {
        Control? container = GetCharacterButtonContainer();
        if (container is null)
            return;
        foreach (NCharacterSelectButton button in container.GetChildren().OfType<NCharacterSelectButton>())
            _originalVisibility.TryAdd(button, button.Visible);
    }

    private void ApplyCharacterRestrictions(CustomRunDefinition definition)
    {
        RestoreCharacterVisibility();
        RunSetupDefinition setup = definition.Setup;
        if (_lobby is not null
            && CustomRunRoleAssignmentService.GetRoleId(_lobby, _lobby.NetService.NetId) is { } roleId)
        {
            setup = definition.Roles.FirstOrDefault(role =>
                string.Equals(role.Id, roleId, StringComparison.Ordinal))?.Setup ?? definition.Setup;
        }
        if (setup.Character.Mode != SelectionMode.Fixed
            || setup.Character.FixedModelIds.Count == 0)
        {
            return;
        }

        HashSet<string> allowed = setup.Character.FixedModelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<NCharacterSelectButton> visibleButtons = [];
        foreach (NCharacterSelectButton button in _originalVisibility.Keys)
        {
            bool visible = !button.IsRandom
                           && (allowed.Contains(button.Character.Id.ToString())
                               || allowed.Contains(button.Character.Id.Entry));
            button.Visible = visible;
            if (visible)
                visibleButtons.Add(button);
        }

        RebuildFocusNeighbors(visibleButtons);
        NCharacterSelectButton? selected = visibleButtons.FirstOrDefault(button => button.IsSelected);
        if (selected is null)
            visibleButtons.FirstOrDefault(button => !button.IsLocked)?.Select();
    }

    private void RestoreCharacterVisibility()
    {
        List<NCharacterSelectButton> visibleButtons = [];
        foreach ((NCharacterSelectButton button, bool visible) in _originalVisibility)
        {
            if (!GodotObject.IsInstanceValid(button))
                continue;
            button.Visible = visible;
            if (visible)
                visibleButtons.Add(button);
        }
        RebuildFocusNeighbors(visibleButtons);
    }

    private Control? GetCharacterButtonContainer()
    {
        return _sourceScreen?.GetNodeOrNull<Control>("CharSelectButtons/ButtonContainer")
               ?? _sourceScreen?.GetNodeOrNull<Control>("LeftContainer/CharSelectButtons/ButtonContainer");
    }

    private static void RebuildFocusNeighbors(IReadOnlyList<NCharacterSelectButton> buttons)
    {
        if (buttons.Count == 0)
            return;
        for (int index = 0; index < buttons.Count; index++)
        {
            NCharacterSelectButton button = buttons[index];
            button.FocusNeighborTop = button.GetPath();
            button.FocusNeighborBottom = button.GetPath();
            button.FocusNeighborLeft = buttons[(index + buttons.Count - 1) % buttons.Count].GetPath();
            button.FocusNeighborRight = buttons[(index + 1) % buttons.Count].GetPath();
        }
    }
}
