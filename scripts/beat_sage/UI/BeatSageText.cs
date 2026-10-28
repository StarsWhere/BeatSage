using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace BeatSage.UI
{
    /// <summary>
    /// Resolves Beat Sage source captions through ModUiChinese when it is
    /// available. Arguments are formatted after translating the template so
    /// song names, paths, preset names, and service details remain untouched.
    /// </summary>
    public static class LocalizationBridge
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> reportedDiagnostics = new HashSet<string>(StringComparer.Ordinal);
        static readonly object diagnosticsLock = new object();
        static FieldInfo engineField;
        static MethodInfo translate;
        static object cachedEngine;
        internal static Func<string, string, string> TestTranslator;
        internal static Action<string> TestDiagnostic;

        static object Engine
        {
            get
            {
                if (engineField == null)
                {
                    try
                    {
                        var type = FindType("ModUiChinese.UiTranslator");
                        if (type != null)
                        {
                            engineField = type.GetField("Engine", BindingFlags.Public | BindingFlags.Static);
                            if (engineField == null) ReportDiagnostic("resolve ModUiChinese.UiTranslator.Engine");
                        }
                    }
                    catch (Exception exception)
                    {
                        ReportDiagnostic("resolve ModUiChinese.UiTranslator.Engine", exception);
                    }
                }

                object current = null;
                try { current = engineField?.GetValue(null); }
                catch (Exception exception) { ReportDiagnostic("read ModUiChinese.UiTranslator.Engine", exception); }
                if (!ReferenceEquals(current, cachedEngine))
                {
                    cache.Clear();
                    translate = null;
                    cachedEngine = current;
                }
                return current;
            }
        }

        public static string T(string source)
        {
            if (source == null) return null;
            if (TestTranslator != null)
            {
                try { return TestTranslator(source, "BeatSage") ?? source; }
                catch (Exception exception)
                {
                    ReportDiagnostic("test translation callback", exception);
                    return source;
                }
            }
            var engine = Engine;
            if (engine == null) return source;
            string result;
            if (cache.TryGetValue(source, out result)) return result;
            try
            {
                translate ??= engine.GetType().GetMethod("Translate", new[] { typeof(string), typeof(string) });
                if (translate == null)
                {
                    ReportDiagnostic("resolve ModUiChinese.UiTranslator.Translate");
                    result = source;
                }
                else
                {
                    result = translate.Invoke(engine, new object[] { source, "BeatSage" }) as string;
                    if (result == null)
                    {
                        ReportDiagnostic("invoke ModUiChinese.UiTranslator.Translate returned no string");
                        result = source;
                    }
                }
            }
            catch (Exception exception)
            {
                ReportDiagnostic("invoke ModUiChinese.UiTranslator.Translate", exception);
                result = source;
            }
            cache[source] = result;
            return result;
        }

        public static string Format(string source, params object[] args)
        {
            if (source == null) return null;
            var translated = T(source);
            try { return String.Format(CultureInfo.InvariantCulture, translated, args ?? new object[0]); }
            catch (Exception exception)
            {
                ReportDiagnostic("format translated caption", exception);
                try { return String.Format(CultureInfo.InvariantCulture, source, args ?? new object[0]); }
                catch (Exception sourceException)
                {
                    ReportDiagnostic("format English caption", sourceException);
                    return source;
                }
            }
        }

        internal static string SourceFormat(string source, params object[] args)
        {
            if (source == null) return null;
            try { return String.Format(CultureInfo.InvariantCulture, source, args ?? new object[0]); }
            catch (Exception exception)
            {
                ReportDiagnostic("format English source caption", exception);
                return source;
            }
        }

        internal static string Message(string source)
        {
            return T(LegacyToSource(source));
        }

        internal static string LegacyToSource(string value)
        {
            if (String.IsNullOrEmpty(value) || !ContainsHan(value)) return value;

            var added = Regex.Match(value, "^\\u5df2\\u52a0\\u5165 (\\d+) \\u9996(?:\\uff0c\\u8df3\\u8fc7 (\\d+) \\u9996\\u91cd\\u590d\\u6b4c\\u66f2)?$");
            if (added.Success)
                return added.Groups[2].Success ? "Added " + added.Groups[1].Value + " songs; skipped " + added.Groups[2].Value + " duplicates" : "Added " + added.Groups[1].Value + " songs";
            var imported = Regex.Match(value, "^\\u5df2\\u5bfc\\u5165 (\\d+) \\u9996\\u6b4c\\u66f2$");
            if (imported.Success) return "Imported " + imported.Groups[1].Value + " songs";
            var selected = Regex.Match(value, "^\\u5df2\\u9009\\u62e9 (\\d+) \\u9996\\u97f3\\u4e50$");
            if (selected.Success) return "" + selected.Groups[1].Value + " songs selected";
            var queueRead = Regex.Match(value, "^\\u65e0\\u6cd5\\u8bfb\\u53d6\\u5236\\u8c31\\u961f\\u5217\\uff0c\\u8bf7\\u68c0\\u67e5 (.+)\\uff1b\\u539f\\u6587\\u4ef6\\u5df2\\u4fdd\\u7559$");
            if (queueRead.Success) return "Could not read the generation queue; check " + queueRead.Groups[1].Value + "; the original file was preserved";
            var rateLimited = Regex.Match(value, "^\\u670d\\u52a1\\u9650\\u6d41\\uff0c\\u8bf7\\u5728 (.+) \\u540e\\u91cd\\u8bd5$");
            if (rateLimited.Success) return "Rate limited; retry after " + rateLimited.Groups[1].Value;
            var beatSageRateLimited = Regex.Match(value, "^Beat Sage \\u8981\\u6c42\\u5728 (.+) \\u540e\\u91cd\\u8bd5(\\uff1b\\u63d0\\u4ea4\\u7ed3\\u679c\\u672a\\u77e5)?$");
            if (beatSageRateLimited.Success) return "Beat Sage is rate limited; retry after " + beatSageRateLimited.Groups[1].Value + (beatSageRateLimited.Groups[2].Success ? "; submission result is unknown" : "");
            var rejected = Regex.Match(value, "^Beat Sage \\u672a\\u63a5\\u53d7\\u8bf7\\u6c42\\uff08HTTP (\\d+)\\uff09$");
            if (rejected.Success) return "Beat Sage rejected the request (HTTP " + rejected.Groups[1].Value + ")";

            foreach (var rule in legacyExact)
                if (String.Equals(value, rule.Key, StringComparison.Ordinal)) return rule.Value;
            foreach (var rule in legacyPrefixes)
                if (value.StartsWith(rule.Key, StringComparison.Ordinal)) return rule.Value + value.Substring(rule.Key.Length);
            return value;
        }

        public static void OnLocalizationReloaded()
        {
            cache.Clear();
            translate = null;
            cachedEngine = null;
            var type = FindType("BeatSage.UI.BeatSageViewController");
            if (type == null)
            {
                ReportDiagnostic("refresh BeatSage.UI.BeatSageViewController: type not found");
                return;
            }
            MethodInfo method;
            try { method = type.GetMethod("OnLocalizationReloaded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); }
            catch (Exception exception)
            {
                ReportDiagnostic("resolve BeatSage.UI.BeatSageViewController.OnLocalizationReloaded", exception);
                return;
            }
            if (method == null)
            {
                ReportDiagnostic("resolve BeatSage.UI.BeatSageViewController.OnLocalizationReloaded");
                return;
            }
            try { method.Invoke(null, null); }
            catch (Exception exception) { ReportDiagnostic("invoke BeatSage.UI.BeatSageViewController.OnLocalizationReloaded", exception); }
        }

        internal static void SetTestTranslator(Func<string, string, string> translator)
        {
            TestTranslator = translator;
            cache.Clear();
            translate = null;
            cachedEngine = null;
        }

        internal static void SetTestDiagnostic(Action<string> diagnostic)
        {
            TestDiagnostic = diagnostic;
            lock (diagnosticsLock) reportedDiagnostics.Clear();
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch (Exception exception)
                {
                    string assemblyName;
                    try { assemblyName = assembly.FullName ?? "<unknown assembly>"; }
                    catch (Exception nameException)
                    {
                        ReportDiagnostic("read assembly name while finding " + fullName, nameException);
                        assemblyName = "<unknown assembly>";
                    }
                    ReportDiagnostic("find type " + fullName + " in " + assemblyName, exception);
                }
            }
            return null;
        }

        static void ReportDiagnostic(string location, Exception exception = null)
        {
            lock (diagnosticsLock)
                if (!reportedDiagnostics.Add(location)) return;

            var message = "Beat Sage localization bridge [" + location + "] failed; English fallback is active.";
            if (exception != null) message += " " + exception.GetType().Name + ": " + exception.Message;
            try { TestDiagnostic?.Invoke(message); }
            catch (Exception) { /* A test diagnostic callback must never break the English fallback. */ }

            try { BeatSage.Plugin.Log?.Warn(message); }
            catch (Exception loggerException)
            {
                try { TestDiagnostic?.Invoke("Beat Sage localization bridge could not write Plugin.Log diagnostics: " + loggerException.GetType().Name); }
                catch (Exception) { /* Logger failures must never break the English fallback. */ }
            }
        }

        static bool ContainsHan(string value)
        {
            foreach (var character in value)
                if (character >= '\u3400' && character <= '\u9fff') return true;
            return false;
        }

        // Compatibility conversion for messages written by the pre-localized
        // Beat Sage build. This is deliberately limited to built-in captions;
        // unknown persisted text and all user/service data remain unchanged.
        static readonly Dictionary<string, string> legacyExact = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "\u8bf7\u5148\u9009\u62e9\u4efb\u52a1", "Select a task first" },
            { "\u8bf7\u5148\u9009\u62e9\u97f3\u4e50\u6587\u4ef6", "Select music files first" },
            { "\u8bf7\u5728\u626b\u63cf\u9884\u89c8\u4e2d\u9009\u62e9\u97f3\u4e50", "Select music in the scan preview" },
            { "\u8bf7\u5148\u5728\u626b\u63cf\u9884\u89c8\u4e2d\u9009\u62e9\u97f3\u4e50", "Select music in the scan preview" },
            { "\u8bf7\u5148\u9009\u62e9\u4e00\u9996\u97f3\u4e50", "Select a song first" },
            { "\u8bf7\u5148\u6253\u5f00\u4e00\u4e2a\u97f3\u4e50\u76ee\u5f55", "Open a music folder first" },
            { "\u8bf7\u9009\u62e9\u9884\u8bbe", "Select a preset" },
            { "\u672a\u77e5", "Unknown" },
            { "\u672a\u77e5\u9519\u8bef", "Unknown error" },
            { "\u4efb\u52a1\u4ecd\u5728\u5904\u7406\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5", "The task is still processing; try again later" },
            { "\u9700\u8981\u5148\u786e\u8ba4\u91cd\u65b0\u63d0\u4ea4", "Confirm resubmission first" },
            { "\u63d0\u4ea4\u7ed3\u679c\u672a\u77e5\uff0c\u65e0\u6cd5\u786e\u8ba4\u8fdc\u7aef\u5df2\u7ed3\u675f\uff1b\u8bf7\u4fdd\u7559\u6682\u505c\u6216\u786e\u8ba4\u91cd\u65b0\u63d0\u4ea4", "Submission result is unknown; keep the queue paused or confirm resubmission" },
            { "\u66f2\u5305\u5df2\u4fdd\u5b58\uff0c\u7b49\u5f85\u66f2\u5e93\u5237\u65b0", "The package is saved and waiting for the library to refresh" },
            { "\u6b4c\u66f2\u5c1a\u672a\u8f7d\u5165\u66f2\u5e93", "The song is not loaded in the library" },
            { "\u5f53\u524d\u6e38\u620f\u6ca1\u6709\u6240\u9009\u573a\u666f\u73af\u5883", "The selected environment is not available in this game" },
            { "\u97f3\u9891\u4e0d\u5b58\u5728\u3001\u4e3a\u7a7a\u6216\u8d85\u8fc7 512 MiB", "The audio is missing, empty, or larger than 512 MiB" },
            { "\u533f\u540d\u5236\u8c31\u9700\u8981\u6709\u6548\u4e14\u77ed\u4e8e 10 \u5206\u949f\u7684\u97f3\u9891", "Anonymous generation requires valid audio shorter than 10 minutes" },
            { "\u533f\u540d\u5236\u8c31\u9700\u8981\u77ed\u4e8e 10 \u5206\u949f\u7684\u97f3\u9891", "Anonymous generation requires audio shorter than 10 minutes" },
            { "\u540c\u4e00\u4efb\u52a1\u7684\u66f2\u5305\u5df2\u5b58\u5728\u4e14\u5185\u5bb9\u4e0d\u540c", "A different map package already exists for this task" },
            { "\u7b49\u5f85\u5df2\u8d85\u65f6\uff1b\u53ef\u4ee5\u7ee7\u7eed\u67e5\u8be2\u6b64\u4efb\u52a1", "The wait timed out; you can continue querying this task" },
            { "\u66f2\u5305\u6761\u76ee\u8fc7\u591a\u3001\u91cd\u590d\u6216\u89e3\u538b\u5927\u5c0f\u8d85\u9650", "The map package has too many entries, duplicate entries, or exceeds the unpacked size limit" },
            { "\u66f2\u5305\u4fe1\u606f\u6587\u4ef6\u8fc7\u5927", "The map package information file is too large" },
            { "\u65e0\u6cd5\u8bc6\u522b Beat Sage \u66f2\u5305\u683c\u5f0f", "The Beat Sage map package format is not recognized" },
            { "\u66f2\u5305\u6a21\u5f0f\u6570\u91cf\u8d85\u9650", "The map package has too many modes" },
            { "\u66f2\u5305\u7f3a\u5c11\u96be\u5ea6\u5217\u8868", "The map package is missing its difficulty list" },
            { "\u66f2\u5305\u96be\u5ea6\u6570\u91cf\u8d85\u9650", "The map package has too many difficulties" },
            { "\u8c31\u9762\u6587\u4ef6\u8fc7\u5927", "A beatmap file is too large" },
            { "\u8c31\u9762\u6570\u636e\u5927\u5c0f\u8d85\u9650", "The beatmap data exceeds the size limit" },
            { "\u751f\u6210\u66f2\u5305\u4e0e\u6240\u9009\u6a21\u5f0f\u6216\u96be\u5ea6\u4e0d\u4e00\u81f4", "The generated map package does not match the selected modes or difficulties" },
            { "\u66f2\u5305\u5df2\u4fdd\u5b58\uff0c\u4f46 SongCore \u672a\u52a0\u8f7d\u5168\u90e8\u96be\u5ea6\uff1b\u53ef\u91cd\u8bd5\u5bfc\u5165", "The map package is saved, but SongCore did not load every difficulty; retry the import" }
        };

        static readonly KeyValuePair<string, string>[] legacyPrefixes =
        {
            new KeyValuePair<string, string>("\u961f\u5217\u5df2\u6682\u505c\uff1a", "Queue paused: "),
            new KeyValuePair<string, string>("\u5bfc\u5165\u672a\u5b8c\u6210\uff1a", "Import incomplete: "),
            new KeyValuePair<string, string>("\u63d0\u4ea4\u7ed3\u679c\u672a\u77e5\uff1a", "Submission result is unknown: "),
            new KeyValuePair<string, string>("\u63d0\u4ea4\u4efb\u52a1\u5931\u8d25\uff1a", "Task submission failed: "),
            new KeyValuePair<string, string>("\u961f\u5217\u64cd\u4f5c\u5931\u8d25\uff1a", "Queue operation failed: "),
            new KeyValuePair<string, string>("\u9884\u8bbe\u5df2\u4fdd\u5b58\uff1a", "Preset saved: "),
            new KeyValuePair<string, string>("\u5df2\u8f7d\u5165\u9884\u8bbe\uff1a", "Preset loaded: "),
            new KeyValuePair<string, string>("\u9884\u8bbe\u5df2\u5220\u9664\uff1a", "Preset deleted: "),
            new KeyValuePair<string, string>("\u5c01\u9762\u8bfb\u53d6\u5931\u8d25\uff1a", "Cover read failed: "),
            new KeyValuePair<string, string>("\u65e0\u6cd5\u6253\u5f00\u66f2\u76ee\uff1a", "Could not open the song: "),
            new KeyValuePair<string, string>("\u65e0\u6cd5\u8bfb\u53d6\u5236\u8c31\u961f\u5217\uff0c\u8bf7\u68c0\u67e5 ", "Could not read the generation queue; check "),
            new KeyValuePair<string, string>("\u8fdc\u7aef\u4efb\u52a1\u7ed3\u675f\uff1a", "Remote task ended: "),
            new KeyValuePair<string, string>("\u65e0\u6cd5\u8bc6\u522b\u7684\u8fdc\u7aef\u72b6\u6001\uff1a", "Unknown remote state: "),
            new KeyValuePair<string, string>("\u5df2\u52a0\u5165 ", "Added "),
            new KeyValuePair<string, string>("\u5df2\u5bfc\u5165 ", "Imported "),
            new KeyValuePair<string, string>("\u670d\u52a1\u9650\u6d41\uff0c\u8bf7\u5728 ", "Rate limited; retry after "),
            new KeyValuePair<string, string>("\u5df2\u9009\u62e9 ", "Selected ")
        };
    }
}
