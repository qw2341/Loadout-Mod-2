using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Loadout.Patches
{
    /// <summary>
    /// Prevents the STS2 startup crash where Workshop mod loading causes the game's
    /// Sentry GDExtension bridge to be disabled/shutdown while startup is still using it.
    ///
    /// Important: this intentionally does NOT patch Workshop loading. Workshop loading is
    /// only the trigger. The unsafe boundary is MegaCrit.Sts2.Core.Debug.SentryService.
    /// </summary>
    internal static class SentryStartupCrashInterceptor
    {
        private const string SentryServiceTypeName = "MegaCrit.Sts2.Core.Debug.SentryService";

        private static readonly string[] MethodsToSkip =
        {
            
            "DisableGdExtensionIfModded",
            "AfterGameInit",
            "Shutdown",
            "SetGdExtensionUser",
            "SetGdExtensionBreadcrumb",
            "SetPlatformBranch",
            "Initialize",
            "InitializeForTesting"
        };

        private static bool _installed;
        private static Type? _sentryServiceType;
        private static PropertyInfo? _isEnabledProperty;

        public static void Install(Harmony harmony)
        {
            if (_installed)
                return;

            _sentryServiceType = AccessTools.TypeByName(SentryServiceTypeName);
            if (_sentryServiceType == null)
            {
                SafeLog("Sentry service type not found; interceptor not installed.");
                return;
            }

            _isEnabledProperty = AccessTools.Property(_sentryServiceType, "IsEnabled");

            var prefix = new HarmonyMethod(typeof(SentryStartupCrashInterceptor), nameof(SkipUnsafeSentryMethod));
            var finalizer = new HarmonyMethod(typeof(SentryStartupCrashInterceptor), nameof(SwallowSentryException));

            var patched = new List<string>();
            foreach (var methodName in MethodsToSkip)
            {
                var method = AccessTools.Method(_sentryServiceType, methodName);
                if (method == null)
                {
                    SafeLog($"SentryService.{methodName} not found; skipped patch.");
                    continue;
                }

                harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                patched.Add(methodName);
            }

            SoftDisableManagedSentry("install");
            _installed = true;

            SafeLog("Sentry startup crash interceptor installed: " + string.Join(", ", patched));
        }

        private static bool SkipUnsafeSentryMethod(MethodBase __originalMethod)
        {
            SoftDisableManagedSentry(__originalMethod.Name);
            return false;
        }

        private static Exception? SwallowSentryException(Exception? __exception, MethodBase __originalMethod)
        {
            if (__exception != null)
            {
                SoftDisableManagedSentry(__originalMethod.Name + " exception");
                SafeLog($"Suppressed SentryService.{__originalMethod.Name} exception: {__exception.GetType().Name}: {__exception.Message}");
            }
            return null;
        }

        private static void SoftDisableManagedSentry(string reason)
        {
            try
            {
                _isEnabledProperty?.SetValue(null, false);
            }
            catch (Exception ex)
            {
                SafeLog($"Could not soft-disable Sentry during {reason}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                var logType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Logging.Log");
                var info = AccessTools.Method(logType, "Info", new[] { typeof(string), typeof(int) });
                if (info != null)
                {
                    info.Invoke(null, new object[] { "[Loadout] " + message, 2 });
                    return;
                }
            }
            catch
            {
                //
            }

            Console.WriteLine("[Loadout] " + message);
        }
    }
}
