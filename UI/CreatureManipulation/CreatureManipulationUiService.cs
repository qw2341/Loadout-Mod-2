#nullable enable

namespace Loadout.UI.CreatureManipulation;

using System;
using System.Collections.Generic;
using Godot;
using Loadout.Services.CreatureManipulation;
using Loadout.Services.Loadouts;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;

public static class CreatureManipulationUiService
{
    private static readonly Dictionary<ulong, Control.GuiInputEventHandler> InputHandlers = [];

    public static void OnCreatureReady(NCreature node)
    {
        CreatureManipulationStateService.OnCreatureReady(node);
        if (node.Hitbox is null)
            return;

        ulong id = node.GetInstanceId();
        OnCreatureExit(node);

        void OnGuiInput(InputEvent input)
        {
            if (input is not InputEventMouseButton
                {
                    ButtonIndex: MouseButton.Right,
                    Pressed: true
                } mouse
                || !CanOpen(node))
            {
                return;
            }

            NLoadoutPanelRoot? root = NLoadoutPanelRoot.Instance;
            if (root is null)
                return;

            root.GetCreatureManipulationPanel().OpenFor(node, mouse.GlobalPosition);
            node.Hitbox.AcceptEvent();
        }

        InputHandlers[id] = OnGuiInput;
        node.Hitbox.GuiInput += OnGuiInput;
    }

    public static void OnCreatureExit(NCreature node)
    {
        ulong id = node.GetInstanceId();
        if (!InputHandlers.Remove(id, out Control.GuiInputEventHandler? handler))
            return;

        if (GodotObject.IsInstanceValid(node.Hitbox))
            node.Hitbox.GuiInput -= handler;
    }

    public static void Clear()
    {
        InputHandlers.Clear();
        NLoadoutPanelRoot.Instance?.GetCreatureManipulationPanel().Close();
    }

    private static bool CanOpen(NCreature node)
    {
        return GodotObject.IsInstanceValid(node)
               && node.Entity is { CombatId: not null }
               && CreatureManipulationStateService.CombatEpoch > 0
               && CombatManager.Instance.IsInProgress
               && LoadoutPanelAccessService.CanLocalPlayerUsePanel()
               && NLoadoutPanelRoot.Instance?.HasOpenScreen != true
               && NPlayerHand.Instance?.InCardPlay != true;
    }
}
