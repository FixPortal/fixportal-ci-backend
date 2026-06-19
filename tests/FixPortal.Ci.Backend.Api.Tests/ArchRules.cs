using System;
using System.Runtime.CompilerServices;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Slices;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace FixPortal.Ci.Backend.Api.Tests;

/// <summary>
/// Reusable architecture rules backed by ArchUnitNET. Each factory returns an
/// <see cref="IArchRule"/> ready for <c>.Check(architecture)</c>.
/// </summary>
/// <remarks>
/// ArchUnitNET API quirks baked in so they don't need rediscovering:
/// <list type="bullet">
///   <item>Method names include parameter types — use <c>HaveNameContaining("Async(")</c>, not <c>HaveNameEndingWith("Async")</c>.</item>
///   <item>Async detection uses <see cref="AsyncStateMachineAttribute"/>, not a missing <c>AreAsync()</c> predicate.</item>
///   <item><see cref="SliceRuleDefinition"/> (no trailing 's') needs <c>using ArchUnitNET.Fluent.Slices</c>.</item>
///   <item><c>ResideInAssembly</c> matches the CLR full name — use namespace OR-chains or <see cref="LayerMustNotDependOn"/> instead.</item>
/// </list>
/// </remarks>
public static class FixPortalArchRules
{
    /// <summary>All interfaces must be named with an "I" prefix.</summary>
    public static IArchRule InterfacesMustHaveIPrefix() =>
        Interfaces()
            .Should().HaveNameStartingWith("I");

    /// <summary>Classes whose name ends in "Exception" must inherit from <see cref="Exception"/>.</summary>
    public static IArchRule ExceptionsMustInheritFromException() =>
        Classes()
            .That().HaveNameEndingWith("Exception")
            .Should().BeAssignableTo(typeof(Exception));

    /// <summary>
    /// All compiler-generated async state machines must live in methods whose name
    /// contains "Async(" — i.e. the public method name ends in "Async".
    /// </summary>
    public static IArchRule AsyncMethodsMustEndInAsync() =>
        MethodMembers()
            .That().HaveAnyAttributes(typeof(AsyncStateMachineAttribute))
            .Should().HaveNameContaining("Async(");

    /// <summary>All classes residing in <paramref name="modelNamespace"/> must be sealed.</summary>
    public static IArchRule ModelTypesMustBeSealed(string modelNamespace) =>
        Classes()
            .That().ResideInNamespace(modelNamespace)
            .Should().BeSealed();

    /// <summary>Namespace slices matching <paramref name="rootPattern"/> must be free of cycles.</summary>
    public static IArchRule NamespaceSlicesMustBeFreeOfCycles(string rootPattern) =>
        SliceRuleDefinition.Slices()
            .Matching(rootPattern)
            .Should().BeFreeOfCycles();

    /// <summary>Types in <paramref name="layer"/> must not depend on any type in <paramref name="forbidden"/>.</summary>
    public static IArchRule LayerMustNotDependOn(
        IObjectProvider<IType> layer,
        IObjectProvider<IType> forbidden) =>
        Types().That().Are(layer)
            .Should().NotDependOnAny(forbidden);
}
