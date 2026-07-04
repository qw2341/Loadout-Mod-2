#nullable enable

namespace Loadout.Services.Loadouts;

using System;
using Godot;

public static class LoadoutClipboardService
{
    public static bool Copy(SavedLoadout loadout)
    {
        try
        {
            DisplayServer.ClipboardSet(LoadoutSerializationService.Encode(loadout));
            return true;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Loadout: failed to copy loadout to clipboard. {exception.Message}");
            return false;
        }
    }

    public static bool TryImportFromClipboard(out SavedLoadout loadout, out string error)
    {
        loadout = new SavedLoadout();
        error = string.Empty;

        try
        {
            return LoadoutSerializationService.TryDecode(DisplayServer.ClipboardGet(), out loadout, out error);
        }
        catch (Exception exception)
        {
            error = $"Could not read clipboard. {exception.Message}";
            return false;
        }
    }
}
