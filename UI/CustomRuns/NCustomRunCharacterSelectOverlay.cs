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
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

public partial class NCustomRunCharacterSelectOverlay : Control
{
    private Control? _sourceScreen;
    private StartRunLobby? _lobby;
    private TextureRect? _confirmImage;
    private readonly Dictionary<NCharacterSelectButton, bool> _originalVisibility = [];
    private float _rainbowHue;
    private bool _isLoaded;
    private bool _roleGateDisabledConfirm;

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
        CustomRunRoleAssignmentService.AssignmentAccepted += OnAssignmentAccepted;
        RefreshLoadedRun();
    }

    public override void _ExitTree()
    {
        CustomRunLobbyService.LoadedDefinitionChanged -= OnLoadedDefinitionChanged;
        CustomRunRoleAssignmentService.Changed -= OnAssignmentsChanged;
        CustomRunRoleAssignmentService.AssignmentRejected -= OnAssignmentRejected;
        CustomRunRoleAssignmentService.AssignmentAccepted -= OnAssignmentAccepted;
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
            ReleaseConfirmRoleGate();
            return;
        }

        NCustomRunEditorEntry.RefreshAttachedState(_sourceScreen, _lobby, definition);
        ApplyCharacterRestrictions(definition!);
        RefreshRoleGate();
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
        {
            NCustomRunEditorEntry.CompleteLocalRoleAction(_sourceScreen, lobby, accepted: false);
            RefreshRoleGate();
            ShowError(error);
        }
    }

    private void OnAssignmentAccepted(StartRunLobby lobby)
    {
        if (!ReferenceEquals(lobby, _lobby) || _sourceScreen is null)
            return;
        NCustomRunEditorEntry.CompleteLocalRoleAction(_sourceScreen, lobby, accepted: true);
        RefreshRoleGate();
    }

    public void RefreshRoleGate()
    {
        if (_sourceScreen is null || _lobby is null)
            return;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(_lobby);
        bool blocked = definition is not null
                       && NCustomRunEditorEntry.IsRoleConfirmationBlocked(_lobby, definition);
        NConfirmButton? confirm = _sourceScreen.GetNodeOrNull<NConfirmButton>("ConfirmButton");
        if (confirm is null)
            return;
        if (blocked)
        {
            confirm.Disable();
            _roleGateDisabledConfirm = true;
        }
        else
        {
            ReleaseConfirmRoleGate();
        }
    }

    public void RefreshRoleSelection()
    {
        if (_lobby is null)
            return;
        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(_lobby);
        if (definition is not null)
            ApplyCharacterRestrictions(definition);
        RefreshRoleGate();
    }

    private void ReleaseConfirmRoleGate()
    {
        if (!_roleGateDisabledConfirm)
            return;
        NConfirmButton? confirm = _sourceScreen?.GetNodeOrNull<NConfirmButton>("ConfirmButton");
        if (confirm is not null && GodotObject.IsInstanceValid(confirm))
            confirm.Enable();
        _roleGateDisabledConfirm = false;
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
            && NCustomRunEditorEntry.GetEffectiveLocalRoleId(_lobby) is { } roleId)
        {
            setup = definition.Roles.FirstOrDefault(role =>
                string.Equals(role.Id, roleId, StringComparison.Ordinal))?.Setup ?? definition.Setup;
        }
        bool fixedSelection = setup.Character.Mode == SelectionMode.Fixed;
        bool randomSelection = setup.Character.Mode == SelectionMode.Random;
        if (!fixedSelection && !randomSelection)
        {
            return;
        }

        HashSet<string> allowed = setup.Character.FixedModelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<NCharacterSelectButton> visibleButtons = [];
        foreach (NCharacterSelectButton button in _originalVisibility.Keys)
        {
            bool characterAllowed = allowed.Count == 0
                                    || allowed.Contains(button.Character.Id.ToString())
                                    || allowed.Contains(button.Character.Id.Entry);
            bool visible = button.IsRandom ? randomSelection : characterAllowed;
            button.Visible = visible;
            if (visible)
                visibleButtons.Add(button);
        }

        RebuildFocusNeighbors(visibleButtons);
        NCharacterSelectButton? selected = randomSelection
            ? visibleButtons.FirstOrDefault(button => button.IsRandom && !button.IsLocked)
            : visibleButtons.FirstOrDefault(button => button.IsSelected && !button.IsRandom);
        if (selected is null)
            visibleButtons.FirstOrDefault(button => !button.IsLocked)?.Select();
        else if (!selected.IsSelected)
            selected.Select();
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
