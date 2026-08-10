#nullable enable

namespace Loadout.UI.CustomRuns;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Services.CustomRuns.Models;
using Loadout.Services.CustomRuns.Networking;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

public partial class NCustomRunCharacterSelectOverlay : Control
{
    private Control? _sourceScreen;
    private StartRunLobby? _lobby;
    private MegaLabel? _statusLabel;
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
        BuildStatusLabel();
        _confirmImage = _sourceScreen?.GetNodeOrNull<TextureRect>("ConfirmButton/Image");
        RememberCharacterVisibility();
        CustomRunLobbyService.LoadedDefinitionChanged += OnLoadedDefinitionChanged;
        RefreshLoadedRun();
    }

    public override void _ExitTree()
    {
        CustomRunLobbyService.LoadedDefinitionChanged -= OnLoadedDefinitionChanged;
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
        if (_lobby is null || _statusLabel is null)
            return;

        CustomRunDefinition? definition = CustomRunLobbyService.GetLoadedDefinition(_lobby);
        bool loaded = definition is not null;
        _isLoaded = loaded;
        SetProcess(loaded);
        if (!loaded)
        {
            _statusLabel.Text = string.Empty;
            _statusLabel.TooltipText = string.Empty;
            _statusLabel.Visible = false;
            if (_confirmImage is not null && GodotObject.IsInstanceValid(_confirmImage))
                _confirmImage.SelfModulate = Colors.White;
            RestoreCharacterVisibility();
            return;
        }

        string name = string.IsNullOrWhiteSpace(definition!.Name) ? "Unnamed Custom Run" : definition.Name;
        _statusLabel.Text = $"CUSTOM RUN LOADED  •  {name}";
        _statusLabel.TooltipText = name;
        _statusLabel.Visible = true;
        _statusLabel.AddThemeColorOverride("font_color", StsColors.gold);
        ApplyCharacterRestrictions(definition);
    }

    public void ShowError(string text)
    {
        if (_statusLabel is null)
            return;
        _statusLabel.Visible = true;
        _statusLabel.Text = text;
        _statusLabel.TooltipText = text;
        _statusLabel.AddThemeColorOverride("font_color", new Color(1f, 0.58f, 0.48f));
    }

    private void BuildStatusLabel()
    {
        _statusLabel = new MegaLabel
        {
            Name = "LoadedRunStatus",
            AutoSizeEnabled = false,
            MinFontSize = 18,
            MaxFontSize = 27,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _statusLabel.AnchorLeft = 0.5f;
        _statusLabel.AnchorTop = 1f;
        _statusLabel.AnchorRight = 0.5f;
        _statusLabel.AnchorBottom = 1f;
        _statusLabel.OffsetLeft = -480f;
        _statusLabel.OffsetTop = -72f;
        _statusLabel.OffsetRight = 480f;
        _statusLabel.OffsetBottom = -18f;
        _statusLabel.AddThemeFontOverride(
            "font",
            GD.Load<Font>("res://themes/kreon_bold_glyph_space_two.tres"));
        _statusLabel.AddThemeFontSizeOverride("font_size", 27);
        _statusLabel.AddThemeConstantOverride("outline_size", 8);
        _statusLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        AddChild(_statusLabel);
    }

    private void OnLoadedDefinitionChanged(StartRunLobby lobby)
    {
        if (ReferenceEquals(lobby, _lobby))
            RefreshLoadedRun();
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
        if (definition.Setup.Character.Mode != SelectionMode.Fixed
            || definition.Setup.Character.FixedModelIds.Count == 0)
        {
            return;
        }

        HashSet<string> allowed = definition.Setup.Character.FixedModelIds
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
