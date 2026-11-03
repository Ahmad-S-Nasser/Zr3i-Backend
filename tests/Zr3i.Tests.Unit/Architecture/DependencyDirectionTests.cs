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
