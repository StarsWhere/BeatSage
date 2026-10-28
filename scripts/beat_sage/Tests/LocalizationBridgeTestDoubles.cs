namespace ModUiChinese
{
    public static class UiTranslator
    {
        public static object Engine;
    }

    public sealed class ThrowingEngine
    {
        public string Translate(string source, string scope) => throw new System.InvalidOperationException("simulated translation failure");
    }

    public sealed class WorkingEngine
    {
        public string Translate(string source, string scope) => "translated " + source;
    }
}

namespace BeatSage
{
    public static class Plugin
    {
        public static TestLogger Log { get; } = new TestLogger();
    }

    public sealed class TestLogger
    {
        public readonly System.Collections.Generic.List<string> Messages = new System.Collections.Generic.List<string>();
        public void Warn(string message) { Messages.Add(message); }
    }
}

namespace BeatSage.UI
{
    public static class BeatSageViewController
    {
        public static bool ThrowOnLocalizationReload;

        public static void OnLocalizationReloaded()
        {
            if (ThrowOnLocalizationReload) throw new System.InvalidOperationException("simulated refresh failure");
        }
    }
}
