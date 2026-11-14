using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Run independent classes concurrently while keeping methods within each class serial.
// Keep process-global state and WPF resource tests explicitly DoNotParallelize.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.ClassLevel)]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("cecfef77-5371-4c0b-b40f-a97cf6e0712b")]
