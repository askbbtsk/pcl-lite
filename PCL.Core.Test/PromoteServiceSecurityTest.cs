using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test;

[TestClass]
public class PromoteServiceSecurityTest
{
    [TestMethod]
    public void ElevatedOperationsMustNotExposeGenericProcessLaunchOrJsonDeserialization()
    {
        Assert.IsNull(typeof(PromoteService).GetMethod("StartProcess", BindingFlags.Public | BindingFlags.Static));
        Assert.IsNull(typeof(PromoteService).GetMethod("AddJsonOperationFunction", BindingFlags.Public | BindingFlags.Static));
    }
}
