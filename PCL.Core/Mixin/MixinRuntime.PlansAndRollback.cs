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

internal sealed partial class MixinRuntime
{

    private MixinApplyResult BuildResult(
        Assembly assembly,
        IReadOnlyList<string> warnings,
        int? mixinCount = null,
        string? applicationId = null)
    {
        var handlers = _plans.Values.SelectMany(plan => plan.AllHandlers())
            .Where(handler => handler.SourceAssembly == assembly &&
                              (applicationId is null || handler.ApplicationId == applicationId))
            .ToArray();
        return new MixinApplyResult(
            assembly,
            mixinCount ?? handlers.Select(handler => handler.MixinType).Distinct().Count(),
            handlers.Select(handler => MixinTargetResolver.ResolveMethod(handler.TargetType, handler.Operation, handler.Handler))
                .Distinct().Count(),
            warnings);
    }


    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type is not null).Cast<Type>().ToArray(); }
    }


    private sealed record ShadowValidation(
        IReadOnlyList<ShadowFieldBinding> Fields,
        IReadOnlyList<(MethodInfo Shadow, MethodInfo Target)> Methods);


    private static ShadowValidation ValidateShadows(Type mixinType, Type targetType, List<string> warnings)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var fields = new List<ShadowFieldBinding>();
        var methods = new List<(MethodInfo Shadow, MethodInfo Target)>();
        foreach (var member in mixinType.GetMembers(flags))
        {
            var shadow = member.GetCustomAttribute<ShadowAttribute>(false);
            if (member is MethodInfo intrinsic && intrinsic.IsDefined(typeof(IntrinsicAttribute), false) &&
                shadow is null && !intrinsic.IsDefined(typeof(OverwriteAttribute), false))
                throw new MixinApplyException(
                    $"Intrinsic 方法必须同时声明 [Shadow] 或 [Overwrite]：{mixinType.FullName}.{intrinsic.Name}");
            if (shadow is null)
            {
                if (member.IsDefined(typeof(UniqueAttribute), false))
                {
                    var collides = member switch
                    {
                        FieldInfo => MixinTargetResolver.FindField(targetType, member.Name) is not null,
                        PropertyInfo => MixinTargetResolver.FindProperty(targetType, member.Name) is not null,
                        MethodInfo method => MixinTargetResolver.EnumerateMethods(targetType).Any(candidate =>
                            candidate.Name == method.Name && candidate.GetParameters().Length == method.GetParameters().Length),
                        _ => false
                    };
                    if (collides)
                        warnings.Add($"Unique 成员与目标同名但保持 Mixin 私有隔离：{mixinType.FullName}.{member.Name}");
                }
                continue;
            }
            var name = string.IsNullOrWhiteSpace(shadow.Name) ? member.Name : shadow.Name;
            var targetMember = ResolveShadowTarget(member, targetType, name);
            if (targetMember is not null)
            {
                ValidateShadowSignature(member, targetMember, targetType, name);
                if (member.IsDefined(typeof(FinalAttribute), false))
                {
                    var isFinal = targetMember switch
                    {
                        FieldInfo field => field.IsInitOnly || field.IsLiteral,
                        PropertyInfo property => property.SetMethod is null,
                        _ => false
                    };
                    if (!isFinal)
                        throw new MixinApplyException($"Final Shadow 的目标可写：{targetType.FullName}::{name}");
                }
                var mutable = shadow.Mutable || member.IsDefined(typeof(MutableAttribute), false);
                switch (member, targetMember)
                {
                    case (FieldInfo shadowField, FieldInfo targetField):
                        fields.Add(new ShadowFieldBinding(
                            shadowField,
                            targetField,
                            mutable,
                            targetField.IsInitOnly || targetField.IsLiteral));
                        break;
                    case (MethodInfo shadowMethod, MethodInfo targetMethod):
                        methods.Add((shadowMethod, targetMethod));
                        break;
                    case (PropertyInfo shadowProperty, PropertyInfo targetProperty):
                        RegisterPropertyAccessors(shadowProperty, targetProperty, mutable, targetType, name, methods);
                        break;
                }
                continue;
            }

            var message = $"Shadow 成员不存在：{targetType.FullName}::{name}";
            if (shadow.Optional) warnings.Add(message);
            else throw new MixinApplyException(message);
        }
        return new ShadowValidation(fields, methods);
    }


    private static MemberInfo? ResolveShadowTarget(MemberInfo member, Type targetType, string name)
        => member switch
        {
            FieldInfo => MixinTargetResolver.FindField(targetType, name),
            PropertyInfo => MixinTargetResolver.FindProperty(targetType, name),
            MethodInfo method => MixinTargetResolver.EnumerateMethods(targetType).SingleOrDefault(candidate =>
                candidate.Name == name &&
                candidate.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(method.GetParameters().Select(parameter => parameter.ParameterType))),
            _ => null
        };


    private static void ValidateShadowSignature(MemberInfo shadow, MemberInfo target, Type targetType, string name)
    {
        static bool IsStatic(MemberInfo member) => member switch
        {
            FieldInfo field => field.IsStatic,
            PropertyInfo property => (property.GetMethod ?? property.SetMethod)?.IsStatic == true,
            MethodInfo method => method.IsStatic,
            _ => false
        };

        if (IsStatic(shadow) != IsStatic(target))
            throw new MixinApplyException($"Shadow 静态/实例不匹配：{targetType.FullName}::{name}");
        var compatible = (shadow, target) switch
        {
            (FieldInfo left, FieldInfo right) => left.FieldType == right.FieldType,
            (PropertyInfo left, PropertyInfo right) => left.PropertyType == right.PropertyType &&
                                                       left.GetIndexParameters().Select(p => p.ParameterType)
                                                           .SequenceEqual(right.GetIndexParameters().Select(p => p.ParameterType)),
            (MethodInfo left, MethodInfo right) => left.ReturnType == right.ReturnType &&
                                                   left.GetParameters().Select(p => p.ParameterType)
                                                       .SequenceEqual(right.GetParameters().Select(p => p.ParameterType)),
            _ => false
        };
        if (!compatible)
            throw new MixinApplyException($"Shadow 类型或签名不匹配：{targetType.FullName}::{name}");
    }


    private static void RegisterPropertyAccessors(
        PropertyInfo shadow,
        PropertyInfo target,
        bool mutable,
        Type targetType,
        string name,
        List<(MethodInfo Shadow, MethodInfo Target)> methods)
    {
        if (shadow.GetMethod is not null)
        {
            if (target.GetMethod is null)
                throw new MixinApplyException($"Shadow 属性目标不可读：{targetType.FullName}::{name}");
            methods.Add((shadow.GetMethod, target.GetMethod));
        }
        if (shadow.SetMethod is null) return;
        if (target.SetMethod is null)
        {
            if (!mutable)
                throw new MixinApplyException($"写入 Final Shadow 属性必须声明 [Mutable]：{targetType.FullName}::{name}");
            throw new MixinApplyException($"Mutable Shadow 属性目标没有 setter：{targetType.FullName}::{name}");
        }
        methods.Add((shadow.SetMethod, target.SetMethod));
    }

}
