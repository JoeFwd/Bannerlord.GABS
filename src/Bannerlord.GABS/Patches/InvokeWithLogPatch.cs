using HarmonyLib;

using System;
using System.Reflection;

using TaleWorlds.Library;

namespace Bannerlord.GABS.Patches;

/// <summary>
/// Replaces Bannerlord's InvokeWithLog with a direct delegate call for parameterless void
/// ViewModel commands (e.g. OnNextStage). This avoids MethodBase.Invoke entirely, so
/// exceptions propagate naturally from the actual throw site — identical to a vanilla mouse
/// click — giving the debugger a live frame with all locals inspectable.
/// Methods with parameters fall through to the original (reflection) path.
/// </summary>
[HarmonyPatch(typeof(Common), nameof(Common.InvokeWithLog), typeof(MethodInfo), typeof(object), typeof(object[]))]
public static class InvokeWithLogPatch
{
    static bool Prefix(MethodInfo methodInfo, object obj, object[] args, ref object __result)
    {
        if (methodInfo.ReturnType != typeof(void))
            return true;
        if (methodInfo.GetParameters().Length != 0)
            return true;
        if (args != null && args.Length != 0)
            return true;

        // Call directly via a typed delegate — no reflection wrapper, no TargetInvocationException.
        // Exceptions propagate naturally from the throw site, debugger lands there with locals intact.
        var del = (Action)Delegate.CreateDelegate(typeof(Action), obj, methodInfo);
        del();
        __result = null;
        return false;
    }
}
