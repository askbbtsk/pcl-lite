using System.IO.Packaging;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PCL.Core.Test;

[TestClass]
public sealed class TestAssemblyInitialization
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        // The launcher registers pack:// while starting WPF. A filtered test run
        // has no Application startup, so register it before any parallel class
        // creates a pack URI instead of relying on another test to initialize WPF.
        RuntimeHelpers.RunClassConstructor(typeof(PackUriHelper).TypeHandle);
    }
}
