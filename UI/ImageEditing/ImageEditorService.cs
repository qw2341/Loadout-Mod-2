#nullable enable

namespace Loadout.UI.ImageEditing;

using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Loadout.UI.Managers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

public static class ImageEditorService
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool _busy;

    public static bool IsBusy => _busy;

    public static async Task<ImageEditResult> PickAndEditAsync(ImageEditRequest request)
    {
        if (_busy)
            return ImageEditResult.Failed("Another image editor is already open.");

        _busy = true;
        try
        {
            string? sourcePath = await PickImageFileAsync(request.Title, request.InitialOpenDirectory);
            if (string.IsNullOrWhiteSpace(sourcePath))
                return ImageEditResult.Cancelled();

            ImageMediaDocument source = ImageMediaLoader.LoadDocumentFromFile(ProjectSettings.GlobalizePath(sourcePath));

            string? initialName = request.InitialDisplayName;
            if (request.AllowDisplayNameEditing && string.IsNullOrWhiteSpace(initialName))
                initialName = Path.GetFileNameWithoutExtension(sourcePath);

            return await EditCoreAsync(source, request with { InitialDisplayName = initialName });
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: image selection/editing failed. {exception}");
            return ImageEditResult.Failed(exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    public static async Task<ImageEditResult> EditAsync(Image source, ImageEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        return await EditAsync(ImageMediaDocument.FromImage(source), request);
    }

    public static async Task<ImageEditResult> EditAsync(ImageMediaDocument source, ImageEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_busy)
            return ImageEditResult.Failed("Another image editor is already open.");

        _busy = true;
        try
        {
            return await EditCoreAsync(source, request);
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: image editing failed. {exception}");
            return ImageEditResult.Failed(exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<ImageEditResult> EditCoreAsync(ImageMediaDocument source, ImageEditRequest request)
    {
        string? validationError = ValidateRequest(request);
        if (validationError is not null)
            return ImageEditResult.Failed(validationError);

        NModalContainer? modalContainer = NModalContainer.Instance;
        if (modalContainer is null || !GodotObject.IsInstanceValid(modalContainer))
            return ImageEditResult.Failed("The game's modal UI is not available.");

        if (modalContainer.OpenModal is not null)
            return ImageEditResult.Failed("Another modal window is already open.");

        NImageEditorModal modal = new() { Name = "LoadoutImageEditor" };
        NImageEditorModal.EditorSessionResult session;
        try
        {
            modal.Initialize(source, request);
            modalContainer.Add(modal);
            session = await modal.Completion;
        }
        catch
        {
            if (ReferenceEquals(modalContainer.OpenModal, modal))
                modalContainer.Clear();
            else if (GodotObject.IsInstanceValid(modal))
                modal.QueueFree();
            throw;
        }

        if (!string.IsNullOrWhiteSpace(session.ErrorMessage))
            return ImageEditResult.Failed(session.ErrorMessage);
        if (!session.Accepted || session.Document is null)
            return ImageEditResult.Cancelled();

        try
        {
            string savedPath = SaveDocumentAtomically(session.Document, request.DestinationDirectory, request.OutputFileName);
            return new ImageEditResult(
                ImageEditStatus.Saved,
                savedPath,
                session.Document.FirstImage,
                session.DisplayName,
                OutputDocument: session.Document);
        }
        catch (Exception exception)
        {
            GD.PushError($"Loadout: failed to save edited image. {exception}");
            return ImageEditResult.Failed(exception.Message);
        }
    }

    private static Task<string?> PickImageFileAsync(string title, string? initialOpenDirectory)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
            return Task.FromResult<string?>(null);

        TaskCompletionSource<string?> completion = new();
        FileDialog dialog = new()
        {
            Name = "LoadoutImageFileDialog",
            Title = title,
            ModeOverridesTitle = false,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Exclusive = true,
            Filters = [$"{ImageMediaLoader.FileDialogPatterns};{LocMan.GameLoc("settings_ui", "LOADOUT-IMAGE_EDITOR_IMAGE_FILES.title", "Image Files")};{ImageMediaLoader.FileDialogMimeTypes}"]
        };
        string? resolvedInitialDirectory = ResolveInitialOpenDirectory(initialOpenDirectory);
        if (resolvedInitialDirectory is not null)
            dialog.CurrentDir = resolvedInitialDirectory;

        void Complete(string? path)
        {
            if (!completion.TrySetResult(path))
                return;
            if (GodotObject.IsInstanceValid(dialog))
                dialog.QueueFree();
        }

        dialog.FileSelected += path => Complete(path);
        dialog.Canceled += () => Complete(null);
        dialog.CloseRequested += () => Complete(null);
        try
        {
            tree.Root.AddChild(dialog);
            dialog.PopupCentered(new Vector2I(960, 720));
        }
        catch
        {
            if (GodotObject.IsInstanceValid(dialog))
                dialog.QueueFree();
            throw;
        }
        return completion.Task;
    }

    private static string? ResolveInitialOpenDirectory(string? requestedDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedDirectory))
            return null;

        try
        {
            string trimmed = requestedDirectory.Trim();
            string resolved = trimmed.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
                ? ProjectSettings.GlobalizePath(trimmed)
                : trimmed;
            if (!Path.IsPathFullyQualified(resolved))
                return null;

            resolved = Path.GetFullPath(resolved);
            return Directory.Exists(resolved) ? resolved : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ValidateRequest(ImageEditRequest request)
    {
        if (request.Frame.OutputSize.X <= 0 || request.Frame.OutputSize.Y <= 0)
            return "The image output size must be positive.";
        if (request.Frame.OutputSize.X > 8192 || request.Frame.OutputSize.Y > 8192)
            return "The image output size cannot exceed 8192 pixels per side.";
        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
            return "An output directory is required.";
        if (string.IsNullOrWhiteSpace(request.OutputFileName))
            return "An output filename is required.";
        return null;
    }

    private static string SaveDocumentAtomically(
        ImageMediaDocument document,
        string requestedDirectory,
        string requestedFileName)
    {
        string directory = ResolveWritableDirectory(requestedDirectory);
        Directory.CreateDirectory(directory);

        string requestedName = requestedFileName.Trim();
        string fileName = Path.GetFileName(requestedName);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("The output filename is invalid.");
        if (!string.Equals(fileName, requestedName, StringComparison.Ordinal))
            throw new InvalidOperationException("The output filename cannot contain a directory path.");
        fileName = Path.ChangeExtension(
            fileName,
            document.IsAnimated ? ImageAnimationPackage.Extension : ".png");

        string directoryRoot = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string outputPath = Path.GetFullPath(Path.Combine(directoryRoot, fileName));
        if (!outputPath.StartsWith(directoryRoot, PathComparison))
            throw new InvalidOperationException("The output path escapes the requested directory.");

        string temporaryPath = Path.Combine(
            directoryRoot,
            $".{Guid.NewGuid():N}.tmp{(document.IsAnimated ? ImageAnimationPackage.Extension : ".png")}");
        try
        {
            if (document.IsAnimated)
            {
                ImageAnimationPackage.Save(temporaryPath, document);
            }
            else
            {
                Error error = document.FirstImage.SavePng(temporaryPath);
                if (error != Error.Ok)
                    throw new IOException($"Godot could not save the PNG ({error}).");
            }
            File.Move(temporaryPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ResolveWritableDirectory(string requestedDirectory)
    {
        string trimmed = requestedDirectory.Trim();
        if (trimmed.StartsWith("user://", StringComparison.OrdinalIgnoreCase))
        {
            string userDirectory = Path.GetFullPath(ProjectSettings.GlobalizePath("user://"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string userRoot = userDirectory + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(ProjectSettings.GlobalizePath(trimmed));
            if (!string.Equals(resolved, userDirectory, PathComparison)
                && !resolved.StartsWith(userRoot, PathComparison))
                throw new InvalidOperationException("The output directory escapes user://.");
            return resolved;
        }
        if (trimmed.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException("Only user:// or absolute filesystem output directories are supported.");
        if (!Path.IsPathFullyQualified(trimmed))
            throw new InvalidOperationException("The output directory must be user:// or an absolute filesystem path.");
        return Path.GetFullPath(trimmed);
    }
}
