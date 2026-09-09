global using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

[TestClass]
public class AssemblyInitializer
{
    [AssemblyInitialize]
    public static void Init(TestContext context)
    {
        NebulaModel.Logger.Log.Init(new DummyLogger());
    }

    private class DummyLogger : NebulaModel.Logger.ILogger
    {
        public void LogDebug(object data) { }
        public void LogInfo(object data) { }
        public void LogWarning(object data) { }
        public void LogError(object data) { }
    }
}
