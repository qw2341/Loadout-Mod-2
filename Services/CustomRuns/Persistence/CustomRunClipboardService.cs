#nullable enable

namespace Loadout.Services.CustomRuns.Persistence;

using System;
using Godot;
using Loadout.Services.CustomRuns.Models;

public static class CustomRunClipboardService
{
    public static bool Copy(CustomRunDefinition definition, out string error)
    {
        error = string.Empty;
        try
        {
            DisplayServer.ClipboardSet(CustomRunSerializationService.Encode(definition));
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not copy Custom Run. {exception.Message}";
            return false;
        }
    }

    public static bool TryImport(out CustomRunDefinition definition, out string error)
    {
        definition = new CustomRunDefinition();
        error = string.Empty;
        try
        {
            return CustomRunSerializationService.TryDecode(DisplayServer.ClipboardGet(), out definition, out error);
        }
        catch (Exception exception)
        {
            error = $"Could not read clipboard. {exception.Message}";
            return false;
        }
    }
}
