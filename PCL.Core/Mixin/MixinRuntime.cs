using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using PCL.Core.Logging;

namespace PCL.Mixin;

internal sealed partial class MixinRuntime : IMixinRuntime, IDisposable
{
    private static readonly Lazy<IntPtr> RuntimeJitHandle = new(LoadRuntimeJitLibrary);
    private readonly object _lock = new();
    private readonly Dictionary<MethodBase, TargetPlan> _plans = [];
    private readonly Dictionary<MethodInfo, ShadowMethodRegistration> _shadowMethods = [];
    private readonly HashSet<ApplicationKey> _appliedApplications = [];
    private readonly Harmony _harmony;
    private long _sequence;
    private bool _disposed;

    public MixinRuntime(string? ownerId = null)
    {
        _ = RuntimeJitHandle.Value;
        OwnerId = string.IsNullOrWhiteSpace(ownerId)
            ? $"pclnex.mixin.{Guid.NewGuid():N}"
            : ownerId;
        _harmony = new Harmony(OwnerId);
    }
}

internal static class MixinRuntimeDispatch
{
    private static readonly ConcurrentDictionary<MethodBase, TargetPlan> Plans = new();
    private static readonly ConcurrentDictionary<long, (MethodBase Target, HandlerPlan Handler)> ReturnHandlers = new();
    private static readonly ConcurrentDictionary<MethodBase, long[]> ReturnSequences = new();

    public static void Register(MethodBase target, TargetPlan plan)
    {
        UnregisterReturnHandlers(target);
        Plans[target] = plan;
        var sequences = plan.Transpilers
            .Where(handler => handler.Operation is InjectAttribute { At: MixinAt.Return or MixinAt.Tail })
            .Select(handler => handler.Sequence)
            .ToArray();
        foreach (var handler in plan.Transpilers.Where(handler => sequences.Contains(handler.Sequence)))
            ReturnHandlers[handler.Sequence] = (target, handler);
        ReturnSequences[target] = sequences;
    }

    public static void Unregister(MethodBase target)
    {
        Plans.TryRemove(target, out _);
        UnregisterReturnHandlers(target);
    }

    public static void ReturnVoid(long sequence, object? instance, object?[] arguments)
    {
        var (target, handler) = GetReturnHandler(sequence);
        var inject = (InjectAttribute)handler.Operation;
        var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, null);
        MixinInvocation.Invoke(handler, target, instance, arguments, null, callback);
    }

    public static object? ReturnResult(long sequence, object? instance, object?[] arguments, object? currentResult)
    {
        var (target, handler) = GetReturnHandler(sequence);
        var inject = (InjectAttribute)handler.Operation;
        var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, currentResult);
        var invoked = MixinInvocation.Invoke(handler, target, instance, arguments, currentResult, callback);
        return MixinInvocation.GetCallbackResult(callback, invoked);
    }

    private static (MethodBase Target, HandlerPlan Handler) GetReturnHandler(long sequence)
        => ReturnHandlers.TryGetValue(sequence, out var handler)
            ? handler
            : throw new MixinApplyException($"RETURN/TAIL 处理器未注册：{sequence}");

    private static void UnregisterReturnHandlers(MethodBase target)
    {
        if (!ReturnSequences.TryRemove(target, out var sequences)) return;
        foreach (var sequence in sequences) ReturnHandlers.TryRemove(sequence, out _);
    }

    public static bool PrefixVoid(MethodBase target, object? instance, object?[] arguments)
    {
        if (!Plans.TryGetValue(target, out var plan)) return true;
        foreach (var handler in plan.Head.ToArray())
        {
            var inject = (InjectAttribute)handler.Operation;
            var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, null);
            MixinInvocation.Invoke(handler, target, instance, arguments, null, callback);
            if (callback.IsCancelled)
            {
                RunReturnHandlersAfterCancellation(plan, target, instance, arguments, null);
                return false;
            }
        }
        if (plan.Overwrite is null) return true;
        MixinInvocation.Invoke(plan.Overwrite, target, instance, arguments, null, null);
        return false;
    }

    public static object? PrefixResult(
        MethodBase target,
        object? instance,
        object?[] arguments,
        object? currentResult,
        out bool runOriginal)
    {
        runOriginal = true;
        if (!Plans.TryGetValue(target, out var plan)) return currentResult;
        foreach (var handler in plan.Head.ToArray())
        {
            var inject = (InjectAttribute)handler.Operation;
            var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, currentResult);
            MixinInvocation.Invoke(handler, target, instance, arguments, currentResult, callback);
            if (!callback.IsCancelled) continue;
            runOriginal = false;
            var cancelledResult = MixinInvocation.GetCallbackResult(callback, currentResult);
            return RunReturnHandlersAfterCancellation(plan, target, instance, arguments, cancelledResult);
        }
        if (plan.Overwrite is null) return currentResult;
        runOriginal = false;
        return MixinInvocation.Invoke(plan.Overwrite, target, instance, arguments, currentResult, null);
    }

    public static void PostfixVoid(MethodBase target, object? instance, object?[] arguments)
    {
        if (!Plans.TryGetValue(target, out var plan)) return;
        foreach (var handler in plan.Return.ToArray())
        {
            var inject = (InjectAttribute)handler.Operation;
            var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, null);
            MixinInvocation.Invoke(handler, target, instance, arguments, null, callback);
        }
    }

    public static object? PostfixResult(MethodBase target, object? instance, object?[] arguments, object? currentResult)
    {
        if (!Plans.TryGetValue(target, out var plan)) return currentResult;
        foreach (var handler in plan.Return.ToArray())
        {
            var inject = (InjectAttribute)handler.Operation;
            var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, currentResult);
            var invoked = MixinInvocation.Invoke(handler, target, instance, arguments, currentResult, callback);
            currentResult = MixinInvocation.GetCallbackResult(callback, invoked);
        }
        return currentResult;
    }

    private static object? RunReturnHandlersAfterCancellation(
        TargetPlan plan,
        MethodBase target,
        object? instance,
        object?[] arguments,
        object? currentResult)
    {
        // RETURN handlers normally live in the transpiled original body. A HEAD cancellation
        // skips that body entirely, so treat the cancellation as one synthetic RETURN site.
        // Local/ordinal/slice-sensitive handlers still require a real IL return site and are skipped.
        foreach (var handler in plan.Transpilers.Where(CanRunAtSyntheticReturn).ToArray())
        {
            var inject = (InjectAttribute)handler.Operation;
            var callback = MixinInvocation.CreateCallback(target, inject.Cancellable, currentResult);
            var invoked = MixinInvocation.Invoke(handler, target, instance, arguments, currentResult, callback);
            currentResult = MixinInvocation.GetCallbackResult(callback, invoked);
        }
        return currentResult;
    }

    private static bool CanRunAtSyntheticReturn(HandlerPlan handler)
    {
        if (handler.Operation is not InjectAttribute { At: MixinAt.Return } inject) return false;
        if (inject.Ordinal >= 0 || !string.IsNullOrWhiteSpace(inject.Slice) ||
            !string.IsNullOrWhiteSpace(inject.SliceFrom) || !string.IsNullOrWhiteSpace(inject.SliceTo))
            return false;
        if (inject.Shift is not AtShift.Before && !(inject.Shift == AtShift.By && inject.By == 0))
            return false;
        return handler.Handler.GetParameters().All(parameter =>
            !parameter.IsDefined(typeof(LocalAttribute), false));
    }

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        System.Reflection.Emit.ILGenerator generator,
        MethodBase __originalMethod)
    {
        if (!Plans.TryGetValue(__originalMethod, out var plan)) return instructions;
        return MixinTranspiler.Apply(__originalMethod, instructions, generator, plan.Transpilers);
    }
}
