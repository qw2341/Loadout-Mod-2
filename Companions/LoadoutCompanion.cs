#nullable enable

namespace Loadout.Companions;

using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

public abstract class LoadoutCompanion : AbstractModel
{
    private List<Player>? _subscribedPlayers;
    private bool _selectedHooksRegistered;
    private bool _handlesRunStarted;
    private bool _handlesRelicObtained;
    private bool _handlesRelicRemoved;

    public abstract string CompanionId { get; }
    public abstract string DisplayName { get; }
    public abstract string TooltipDescription { get; }
    public abstract string SpritePath { get; }

    public virtual string ConfigLocalizationKey => $"Companion{GetType().Name.Replace(nameof(LoadoutCompanion), string.Empty)}";
    public virtual string NameLocalizationKey => $"{ConfigLocalizationKey}Name";
    public virtual string TooltipLocalizationKey => $"{ConfigLocalizationKey}Description";
    public virtual Rect2? SpriteRegion => null;
    public virtual bool IsCustom => false;
    public virtual bool UsesLocalizedConfigText => true;
    public virtual bool IsGameplayAffecting => false;
    public virtual Color? SelectionColor => IsGameplayAffecting ? new Color("EFC851") : null;
    public virtual bool UsesRunStateHooks => false;
    public override bool ShouldReceiveCombatHooks => false;

    public virtual void RegisterHooks()
    {
    }

    public virtual void UnregisterHooks()
    {
    }

    public virtual void OnRunStarted(RunState runState)
    {
    }

    public virtual void OnRelicObtained(Player player, RelicModel relic)
    {
    }

    public virtual void OnRelicRemoved(Player player, RelicModel relic)
    {
    }

    public void Peek(double seconds = 1.5)
    {
        LoadoutCompanionRegistry.RequestPresentation(this, null, seconds);
    }

    public void Say(string text, double seconds = 2.0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        LoadoutCompanionRegistry.RequestPresentation(this, text, seconds);
    }

    internal void RegisterSelectedHooks()
    {
        if (_selectedHooksRegistered)
            return;

        _selectedHooksRegistered = true;
        _handlesRunStarted = OverridesHook(nameof(OnRunStarted), typeof(RunState));
        _handlesRelicObtained = OverridesHook(nameof(OnRelicObtained), typeof(Player), typeof(RelicModel));
        _handlesRelicRemoved = OverridesHook(nameof(OnRelicRemoved), typeof(Player), typeof(RelicModel));
        if (_handlesRunStarted || _handlesRelicObtained || _handlesRelicRemoved)
            RunManager.Instance.RunStarted += HandleRunStarted;
        if ((_handlesRelicObtained || _handlesRelicRemoved)
            && RunManager.Instance.DebugOnlyGetState() is { } runState)
            AttachToRunPlayers(runState);

        try
        {
            RegisterHooks();
        }
        catch
        {
            UnregisterSelectedHooks();
            throw;
        }
    }

    internal void UnregisterSelectedHooks()
    {
        if (!_selectedHooksRegistered)
            return;

        _selectedHooksRegistered = false;
        RunManager.Instance.RunStarted -= HandleRunStarted;
        DetachFromRunPlayers();
        UnregisterHooks();
    }

    protected override void DeepCloneFields()
    {
        base.DeepCloneFields();
        _subscribedPlayers = null;
        _selectedHooksRegistered = false;
        _handlesRunStarted = false;
        _handlesRelicObtained = false;
        _handlesRelicRemoved = false;
    }

    private void HandleRunStarted(RunState runState)
    {
        if (_handlesRelicObtained || _handlesRelicRemoved)
            AttachToRunPlayers(runState);
        if (_handlesRunStarted)
            TryInvoke(() => OnRunStarted(runState), nameof(OnRunStarted));
    }

    private void AttachToRunPlayers(RunState runState)
    {
        DetachFromRunPlayers();
        _subscribedPlayers = new List<Player>(runState.Players.Count);
        foreach (Player player in runState.Players)
        {
            if (_handlesRelicObtained)
                player.RelicObtained += HandleRelicObtained;
            if (_handlesRelicRemoved)
                player.RelicRemoved += HandleRelicRemoved;
            _subscribedPlayers.Add(player);
        }
    }

    private void DetachFromRunPlayers()
    {
        if (_subscribedPlayers is null)
            return;

        foreach (Player player in _subscribedPlayers)
        {
            if (_handlesRelicObtained)
                player.RelicObtained -= HandleRelicObtained;
            if (_handlesRelicRemoved)
                player.RelicRemoved -= HandleRelicRemoved;
        }
        _subscribedPlayers = null;
    }

    private void HandleRelicObtained(RelicModel relic)
    {
        TryInvoke(() => OnRelicObtained(relic.Owner, relic), nameof(OnRelicObtained));
    }

    private void HandleRelicRemoved(RelicModel relic)
    {
        TryInvoke(() => OnRelicRemoved(relic.Owner, relic), nameof(OnRelicRemoved));
    }

    private bool OverridesHook(string methodName, params Type[] parameterTypes)
    {
        MethodInfo? method = GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public,
            null,
            parameterTypes,
            null);
        return method?.DeclaringType != typeof(LoadoutCompanion);
    }

    private void TryInvoke(Action action, string hookName)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: companion '{CompanionId}' {hookName} failed. {exception}");
        }
    }
}

public readonly record struct LoadoutCompanionPresentationRequest(
    LoadoutCompanion Companion,
    string? Text,
    double Seconds);
