#nullable enable

namespace Loadout.Services.Configuration;

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

public static class OptionalBaseLibIntegrationService
{
    private const string BaseLibAssemblyName = "BaseLib";
    private const string IntegrationAssemblyName = "Loadout.BaseLibIntegration";
    private const string IntegrationTypeName = "Loadout.Config.LoadoutBaseLibBootstrap";

    private static bool _initialized;
    private static bool _attemptedForLoadedBaseLib;

    public static bool IsActive => _initialized;

    public static void TryInitialize()
    {
        if (_initialized || _attemptedForLoadedBaseLib)
            return;

        if (!IsAssemblyLoaded(BaseLibAssemblyName))
            return;

        _attemptedForLoadedBaseLib = true;

        try
        {
            Assembly loadoutAssembly = typeof(OptionalBaseLibIntegrationService).Assembly;
            string? directory = Path.GetDirectoryName(loadoutAssembly.Location);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Loadout assembly directory could not be resolved.");

            string integrationPath = Path.Combine(directory, IntegrationAssemblyName + ".dll");
            if (!File.Exists(integrationPath))
                throw new FileNotFoundException("The optional BaseLib integration assembly was not found.", integrationPath);

            Assembly? integrationAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => assembly.GetName().Name == IntegrationAssemblyName);

            integrationAssembly ??= AssemblyLoadContext
                .GetLoadContext(loadoutAssembly)?
                .LoadFromAssemblyPath(integrationPath);

            Type bootstrap = integrationAssembly?.GetType(IntegrationTypeName, throwOnError: true)
                             ?? throw new TypeLoadException($"Could not find {IntegrationTypeName}.");
            MethodInfo initialize = bootstrap.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)
                                    ?? throw new MissingMethodException(IntegrationTypeName, "Initialize");

            initialize.Invoke(null, null);
            _initialized = true;
            MainFile.Logger.Info("[Loadout] BaseLib config integration initialized.");
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            MainFile.Logger.Warn($"[Loadout] BaseLib is loaded, but optional config integration could not start: {cause.Message}");
        }
    }

    private static bool IsAssemblyLoaded(string assemblyName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name == assemblyName);
    }
}
