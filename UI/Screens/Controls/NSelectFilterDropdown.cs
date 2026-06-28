#nullable enable

namespace Loadout.UI.Screens.Controls;

using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using System;
using System.Collections.Generic;
using System.Linq;

public readonly record struct SelectDropdownOption(string Id, string Label);

public partial class NSelectFilterDropdown : NDropdown
{
    private const string DropdownItemInnerPath = "ui/dropdown_item";

    private readonly List<SelectDropdownOption> _options = new();
    private readonly Dictionary<NDropdownItem, string> _optionIdsByItem = new();
    private string _groupLabel = string.Empty;
    private string _selectedOptionId = string.Empty;
    private bool _isReady;

    public event Action<string>? OptionSelected;

    public override void _Ready()
    {
        ConnectSignals();
        _isReady = true;
        ApplyOptions();
    }

    public void SetOptions(string groupLabel, IEnumerable<SelectDropdownOption> options, string selectedOptionId)
    {
        _groupLabel = groupLabel;
        _selectedOptionId = selectedOptionId;
        _options.Clear();
        _options.AddRange(options);

        if (_isReady)
            ApplyOptions();
    }

    public void SetSelectedOption(string selectedOptionId)
    {
        _selectedOptionId = selectedOptionId;

        if (_isReady)
            RefreshCurrentOptionLabel();
    }

    private void ApplyOptions()
    {
        ClearDropdownItems();
        _optionIdsByItem.Clear();

        string itemScenePath = SceneHelper.GetScenePath(DropdownItemInnerPath);
        PackedScene? itemScene = GD.Load<PackedScene>(itemScenePath);
        if (itemScene is null)
        {
            GD.PushError($"{nameof(NSelectFilterDropdown)}: missing dropdown item scene at '{itemScenePath}'.");
            return;
        }

        foreach (SelectDropdownOption option in _options)
        {
            NDropdownItem item = itemScene.Instantiate<NDropdownItem>(PackedScene.GenEditState.Disabled);
            _optionIdsByItem[item] = option.Id;
            item.Connect(NDropdownItem.SignalName.Selected, Callable.From<NDropdownItem>(OnDropdownItemSelected));
            _dropdownItems.AddChild(item);
            item.Text = option.Label;
        }

        _dropdownItems.GetParent<NDropdownContainer>().RefreshLayout();
        RefreshCurrentOptionLabel();
    }

    private void OnDropdownItemSelected(NDropdownItem dropdownItem)
    {
        if (!_optionIdsByItem.TryGetValue(dropdownItem, out string? selectedOptionId))
            return;

        _selectedOptionId = selectedOptionId;
        RefreshCurrentOptionLabel();
        OptionSelected?.Invoke(_selectedOptionId);
        CloseDropdown();
    }

    private void RefreshCurrentOptionLabel()
    {
        if (_options.Count == 0)
        {
            _currentOptionLabel.SetTextAutoSize(_groupLabel);
            return;
        }

        SelectDropdownOption? selectedOption = _options
            .Cast<SelectDropdownOption?>()
            .FirstOrDefault(option => option?.Id == _selectedOptionId);

        string label = selectedOption?.Label ?? _options.FirstOrDefault().Label;
        string text = string.IsNullOrWhiteSpace(_groupLabel) ? label : $"{_groupLabel}: {label}";
        _currentOptionLabel.SetTextAutoSize(text);
    }
}
