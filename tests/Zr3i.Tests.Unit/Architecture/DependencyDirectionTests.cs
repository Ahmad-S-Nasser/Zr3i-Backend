using System.Reflection;
using Xunit;

namespace Zr3i.Tests.Unit.Architecture;

public class DependencyDirectionTests
{
    [Fact]
    public void Kernel_does_not_depend_on_modules_or_hosts()
    {
        var refs = typeof(Zr3i.Kernel.KernelMarker).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Zr3i.", StringComparison.Ordinal))
            .ToList();

        // The Kernel may use the repository layer and the geometry library, nothing else.
        Assert.Empty(refs.Distinct().Except(["Zr3i.Data", "Zr3i.Geo"]));
        Assert.Contains("Zr3i.Data", refs);
    }

    [Fact]
    public void Geo_library_is_pure_and_depends_on_no_other_Zr3i_project()
    {
        var refs = typeof(Zr3i.Geo.GeoConstants).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Zr3i.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(refs);
    }

    [Fact]
    public void Data_layer_depends_on_no_other_Zr3i_project()
    {
        var refs = typeof(Zr3i.Data.TenantScope).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Zr3i.", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(refs);
    }

    [Theory]
    [InlineData(typeof(Zr3i.Modules.Verification.AssemblyMarker), typeof(Zr3i.Modules.Risk.AssemblyMarker))]
    [InlineData(typeof(Zr3i.Modules.Risk.AssemblyMarker), typeof(Zr3i.Modules.Verification.AssemblyMarker))]
    public void Modules_do_not_reference_each_other(Type module, Type other)
    {
        var refs = module.Assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain(other.Assembly.GetName().Name, refs);
    }
}
