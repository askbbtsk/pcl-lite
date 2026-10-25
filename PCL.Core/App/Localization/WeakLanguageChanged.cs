using System;
using System.Collections.Generic;
using PCL.Core.Logging;

namespace PCL.Core.App.Localization;

public static class WeakLanguageChanged
{
    private static readonly object _Lock = new();
    private static readonly List<(WeakReference<object> Target, Action<object> Handler)> _Handlers = [];
    private static bool _hooked;

    /// <summary>
    ///     注册语言变更处理器。
    ///     <para>
    ///         <b>必须传入静态方法组</b>，例如 <c>WeakLanguageChanged.Add(this, OnLanguageChanged)</c>，
    ///         其中 <c>private static void OnLanguageChanged(MyPage page) =&gt; …</c>。
    ///     </para>
    ///     <para>
    ///         不要写成 <c>static page =&gt; ModBase.RunInUi(page.Foo())</c>：lambda 内部的
    ///         方法组转换会让编译器生成带 <see cref="Delegate.Target" /> 的委托，
    ///         即使加了 <c>static</c> 修饰符也一样（已实测），这里会直接抛
    ///         <see cref="ArgumentException" />。写成静态方法组则 <see cref="Delegate.Target" /> 一定为 <c>null</c>。
    ///     </para>
    /// </summary>
    public static void Add<T>(T target, Action<T> handler) where T : class
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        if (handler.Target is not null)
            throw new ArgumentException(
                "handler 必须是不捕获实例的静态方法组，请改用形如 private static void OnX(MyPage page) => … 的静态方法并通过 target 传入实例；" +
                "注意不要写成 static  lambda——lambda 内部的方法组转换同样会产生带 Target 的委托。",
                nameof(handler));

        lock (_Lock)
        {
            if (!_hooked)
            {
                LocalizationService.LanguageChanged += _OnLanguageChanged;
                _hooked = true;
            }

            _Handlers.Add((new WeakReference<object>(target), o => handler((T)o)));
        }
    }

    private static void _OnLanguageChanged()
    {
        (WeakReference<object> Target, Action<object> Handler)[] snapshot;
        lock (_Lock)
        {
            _Handlers.RemoveAll(h => !h.Target.TryGetTarget(out _));
            snapshot = _Handlers.ToArray();
        }

        foreach (var (target, handler) in snapshot)
        {
            if (!target.TryGetTarget(out var t))
                continue;
            try
            {
                handler(t);
            }
            catch (Exception ex)
            {
                LogWrapper.Warn(ex, "WeakLanguageChanged", "语言变更处理器执行出错");
            }
        }
    }
}
