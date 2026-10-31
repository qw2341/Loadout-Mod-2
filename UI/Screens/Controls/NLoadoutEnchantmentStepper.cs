#nullable enable

namespace Loadout.UI.Screens.Controls;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Loadout.Keywords;
using Loadout.Services.CardModification;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Models;

public partial class NLoadoutEnchantmentStepper : NLoadoutDropdownStepper
{
    public void Init(CardAttachmentSpec? selected, Action<CardAttachmentSpec?> changed, bool allowNone = true)
    {
        List<LoadoutDropdownOption> options = [];
        if (allowNone)
            options.Add(new(string.Empty, LocMan.Loc("NONE", "None")));
        foreach (EnchantmentModel model in LoadoutEnchantmentKeywordModel.GetAvailableEnchantments()
                     .OrderBy(LoadoutEnchantmentKeywordModel.GetLabel, StringComparer.OrdinalIgnoreCase))
        {
            CardAttachmentSpec spec = new()
            {
                ModelId = model.Id.ToString(),
                Amount = model.Id.ToString() == selected?.ModelId ? selected.Amount : 1
            };
            options.Add(new(spec.ModelId, LoadoutEnchantmentKeywordModel.GetLabel(model),
                () => LoadoutEnchantmentKeywordModel.GetHoverTips(spec).ToArray(), GetIcon(model)));
        }
        string selectedId = selected?.ModelId ?? string.Empty;
        if (!string.IsNullOrEmpty(selectedId) && options.All(option => option.Id != selectedId))
            options.Add(new(selectedId, LoadoutEnchantmentKeywordModel.GetLabel(selected)));
        Init(options, selectedId, id => changed(string.IsNullOrEmpty(id)
            ? null : new CardAttachmentSpec { ModelId = id, Amount = selected?.ModelId == id ? selected.Amount : 1 }));
    }

    private static Texture2D? GetIcon(EnchantmentModel model)
    {
        try
        {
            return model.Icon;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Enchantment selector: could not load icon for '{model.Id}'. {exception.Message}");
            return null;
        }
    }
}
