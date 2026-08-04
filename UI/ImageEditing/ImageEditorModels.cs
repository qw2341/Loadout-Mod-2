#nullable enable

namespace Loadout.UI.ImageEditing;

using Godot;

public enum ImageEditStatus
{
    Saved,
    Cancelled,
    Failed
}

public sealed record ImageEditFrameDefinition(
    string Id,
    Vector2I OutputSize,
    Image? AlphaMask = null,
    Texture2D? PreviewOverlay = null,
    bool BakeMaskIntoOutput = false)
{
    public static ImageEditFrameDefinition Rectangle(string id, Vector2I outputSize)
    {
        return new ImageEditFrameDefinition(id, outputSize);
    }
}

public sealed record ImageEditRequest(
    ImageEditFrameDefinition Frame,
    string DestinationDirectory,
    string OutputFileName,
    string Title,
    string? InitialDisplayName = null,
    bool AllowDisplayNameEditing = false,
    string? InitialOpenDirectory = null);

public sealed record ImageEditResult(
    ImageEditStatus Status,
    string? SavedPath = null,
    Image? OutputImage = null,
    string? DisplayName = null,
    string? ErrorMessage = null,
    ImageMediaDocument? OutputDocument = null)
{
    public bool Saved => Status == ImageEditStatus.Saved;

    public static ImageEditResult Cancelled() => new(ImageEditStatus.Cancelled);

    public static ImageEditResult Failed(string errorMessage) =>
        new(ImageEditStatus.Failed, ErrorMessage: errorMessage);
}
