using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using FixPortal.Ci.Backend.Api.Dashboard.Services;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace FixPortal.Ci.Backend.Api.Tests;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(IDashboardSnapshotStore).Assembly)
        .Build();

    [Fact]
    public void Interfaces_must_have_I_prefix()
    {
        FixPortalArchRules.InterfacesMustHaveIPrefix()
            .Check(Architecture);
    }

    [Fact]
    public void Exception_types_must_inherit_from_Exception()
    {
        FixPortalArchRules.ExceptionsMustInheritFromException()
            .Check(Architecture);
    }

    [Fact]
    public void Async_methods_must_end_in_Async()
    {
        FixPortalArchRules.AsyncMethodsMustEndInAsync()
            .Check(Architecture);
    }

    [Fact]
    public void Model_types_must_be_sealed()
    {
        FixPortalArchRules.ModelTypesMustBeSealed("FixPortal.Ci.Backend.Api.Dashboard.Model")
            .Check(Architecture);
    }


}
