using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.MenuButtons;
using HMUI;
using IPA;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongCore;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BeatSageRuntimeSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        private GameObject owner;

        [OnStart]
        public void Start()
        {
            if (!Environment.GetCommandLineArgs().Contains("--beat-sage-runtime-smoke"))
            {
                return;
            }

            owner = new GameObject("BeatSageRuntimeSmoke");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            owner.AddComponent<Runner>();
        }

        [OnExit]
        public void Exit()
        {
            if (owner != null)
            {
                UnityEngine.Object.Destroy(owner);
            }
        }
    }

    public sealed class Runner : MonoBehaviour
    {
        private const double MenuWaitSeconds = 90;
        private const double GenerationWaitSeconds = 1200;
        private readonly List<object> checks = new List<object>();
        private readonly List<string> errors = new List<string>();
        private readonly List<string> diagnostics = new List<string>();
        private readonly List<object> stateTransitions = new List<object>();
        private readonly Stopwatch timer = Stopwatch.StartNew();
        private string directory;
        private string phase = "starting";
        private string wishPath;
        private bool generate;
        private bool finished;
        private double lastProgressWrite;

        private void Start()
        {
            directory = Environment.GetEnvironmentVariable("BEATSAGE_SMOKE_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(Environment.CurrentDirectory, ".tmp", "beatsage-mod", "runtime");
            }

            Directory.CreateDirectory(directory);
            generate = Environment.GetEnvironmentVariable("BEATSAGE_SMOKE_GENERATE") == "1";
            Application.logMessageReceived += OnLog;
            StartCoroutine(Guard(Run()));
        }

        private void Update()
        {
            if (!finished && timer.Elapsed.TotalSeconds > (generate ? GenerationWaitSeconds + 180 : 240))
            {
                Finish("Finite runtime deadline exceeded in phase " + phase);
            }
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }

            var text = (message ?? string.Empty) + "\n" + (stack ?? string.Empty);
            if (text.IndexOf("BeatSage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Beat Sage", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!diagnostics.Contains(text))
                {
                    diagnostics.Add(text);
                }
            }
        }

        private IEnumerator Guard(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            while (!finished && stack.Count > 0)
            {
                bool next;
                object current = null;
                try
                {
                    var currentRoutine = stack.Peek();
                    next = currentRoutine.MoveNext();
                    if (next)
                    {
                        current = currentRoutine.Current;
                    }
                }
                catch (Exception ex)
                {
                    Finish(phase + ": " + ex);
                    yield break;
                }

                if (!next)
                {
                    stack.Pop();
                    continue;
                }

                if (current is IEnumerator child)
                {
                    stack.Push(child);
                    continue;
                }

                yield return current;
            }

            if (!finished)
            {
                Finish(null);
            }
        }

        private void Check(string name, bool passed, string detail)
        {
            checks.Add(new { name, passed, detail });
            if (!passed)
            {
                errors.Add(name + ": " + detail);
            }
        }

        private IEnumerator Run()
        {
            phase = "service";
            yield return WaitUntil(() => BeatSage.Plugin.App != null, MenuWaitSeconds, "Beat Sage service");
            Check("service-started", BeatSage.Plugin.App != null, "Plugin.App");
            if (BeatSage.Plugin.App == null)
            {
                Finish(null);
                yield break;
            }

            phase = "menu-bsml";
            yield return CheckMenuAndBsml();

            phase = "environment";
            yield return WaitUntil(HasEnvironmentCatalog, 30, "environment catalog");
            CheckEnvironmentCatalog();

            phase = "local-browse-scan";
            JObject song = null;
            yield return CheckWishBrowseAndScan(value => song = value);
            if (song == null)
            {
                Finish(null);
                yield break;
            }

            var levelId = Environment.GetEnvironmentVariable("BEATSAGE_SMOKE_LEVEL_ID");
            if (!string.IsNullOrWhiteSpace(levelId))
            {
                yield return WaitUntil(() => Loader.AreSongsLoaded && !Loader.AreSongsLoading, 90, "SongCore library");
                CheckExistingLevel(levelId);
            }

            phase = "mono-http-multipart";
            CheckMonoMultipart(song);

            if (generate)
            {
                phase = "generation";
                yield return GenerateAndTrack(song);
            }
            else
            {
                Check("generation-disabled", true, "只执行菜单、浏览和扫描；未提交远端任务");
            }

            if (!string.IsNullOrWhiteSpace(levelId))
            {
                phase = "show-song-ui";
                yield return CheckImportedUiShowSong(levelId);
            }

            Finish(null);
        }

        private IEnumerator WaitUntil(Func<bool> predicate, double seconds, string description)
        {
            var deadline = timer.Elapsed.TotalSeconds + seconds;
            while (!predicate() && timer.Elapsed.TotalSeconds < deadline)
            {
                yield return null;
            }

            Check("wait-" + description, predicate(), "elapsed=" + timer.Elapsed.TotalSeconds.ToString("0.0"));
        }

        private IEnumerator CheckMenuAndBsml()
        {
            MenuButton button = null;
            yield return WaitUntil(() =>
            {
                var menu = Resources.FindObjectsOfTypeAll<MainMenuViewController>().FirstOrDefault(x => x != null && x.isActivated && x.gameObject.activeInHierarchy);
                if (menu == null)
                {
                    return false;
                }

                try
                {
                    button = FindBeatSageButton();
                    return button != null;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, MenuWaitSeconds, "center registry entry");

            Check("menu-button-registered", button != null, button == null ? "Beat Sage center entry not found" : button.Text);
            if (button == null)
            {
                yield break;
            }

            try
            {
                button.ButtonClicked();
            }
            catch (Exception ex)
            {
                Check("menu-button-click", false, ex.ToString());
                yield break;
            }

            var viewType = typeof(BeatSage.Plugin).Assembly.GetType("BeatSage.UI.BeatSageViewController");
            Component view = null;
            yield return WaitUntil(() =>
            {
                if (viewType == null)
                {
                    return false;
                }

                view = Resources.FindObjectsOfTypeAll(viewType).OfType<Component>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy);
                return view != null;
            }, 30, "BSML view");

            Check("menu-bsml-view-created", view != null, viewType == null ? "BeatSageViewController type missing" : "active=" + (view != null));
            if (view != null)
            {
                var required = new[] { "browserList", "scanList", "queueList", "retryModal", "unsavedModal", "songEditorModal", "selectedCoverImage", "localPanel", "queuePanel", "settingsPanel", "generationPanel", "applicationPanel", "presetsPanel", "choiceList", "choiceModal", "submitButton" };
                var missing = required.Where(name => ReadField(view, name) == null).ToArray();
                Check("menu-bsml-native-controls", missing.Length == 0, missing.Length == 0 ? "browser/scan/queue/settings/editor controls parsed" : string.Join(",", missing));
                yield return WaitUntil(() => IsMenuFlowStable(view), 30, "BSML flow transition");
                yield return WaitForUiSettled();
                for (var frame = 0; frame < 3; frame++)
                {
                    yield return new WaitForEndOfFrame();
                }
                CheckUiGeometry(view);
                yield return CheckLocalization(view);
                yield return CheckSettingsDraft(view);
                var closingField = typeof(BeatSage.UI.Menu).GetField("closing", BindingFlags.Static | BindingFlags.NonPublic);
                var duplicateContinued = false;
                try
                {
                    closingField.SetValue(null, true);
                    BeatSage.UI.Menu.Close(() => duplicateContinued = true);
                    Check("menu-close-duplicate", !duplicateContinued, "a duplicate close does not start navigation");
                }
                finally { closingField.SetValue(null, false); }
            }

            BeatSage.UI.Menu.Close(null);
            yield return WaitUntil(() => IsMenuFlowDeactivated() && IsViewDeactivated(view), 15, "flow close");
            Check("menu-flow-close", IsMenuFlowDeactivated() && IsViewDeactivated(view), "Beat Sage flow dismissed through its parent");
            yield return WaitUntil(CenterReady, 10, "center resumes after Beat Sage");
            LeaveCenter();
            yield return WaitUntil(() => Resources.FindObjectsOfTypeAll<MainMenuViewController>().Any(x => x != null && x.isActivated && x.gameObject.activeInHierarchy), 15, "center close");
        }

        private IEnumerator CheckSettingsDraft(Component view)
        {
            var original = (JObject)((JObject)BeatSage.Plugin.App.Snapshot["settings"]).DeepClone();
            var initial = (bool)ReadField(view, "autoImport");
            try
            {
                InvokeViewMethod(view, "ShowSettingsTab");
                InvokeViewMethod(view, "ToggleSettingAutoImport");
                InvokeViewMethod(view, "ApplySnapshot", BeatSage.Plugin.App.Snapshot);
                Check("settings-draft-snapshot", (bool)ReadField(view, "autoImport") != initial,
                    "service refresh preserves the edited application setting");
                InvokeViewMethod(view, "ShowLocalTab");
                yield return EndOfFrameStabilization();
                Check("settings-draft-prompt", ModalActive(view, "unsavedModal") && PanelActive(view, "settingsPanel"),
                    "leaving settings requests a choice");
                InvokeViewMethod(view, "ContinueEditing");
                yield return WaitUntil(() => !ModalActive(view, "unsavedModal"), 3, "continue editing closes prompt");
                Check("settings-draft-continue", !ModalActive(view, "unsavedModal") && PanelActive(view, "settingsPanel") && (bool)ReadField(view, "autoImport") != initial,
                    "continue editing retains the draft");
                InvokeViewMethod(view, "ShowLocalTab");
                InvokeViewMethod(view, "DiscardAndLeave");
                yield return EndOfFrameStabilization();
                Check("settings-draft-discard", (bool)ReadField(view, "autoImport") == initial && PanelActive(view, "localPanel") && (bool)BeatSage.Plugin.App.Snapshot["settings"]["autoImport"] == initial,
                    "discard restores the saved value without persisting the draft");

                InvokeViewMethod(view, "ShowSettingsTab");
                InvokeViewMethod(view, "ToggleSettingAutoImport");
                var closed = false;
                InvokeViewMethod(view, "RequestClose", (Action)(() => closed = true));
                yield return EndOfFrameStabilization();
                Check("settings-draft-back-prompt", ModalActive(view, "unsavedModal") && !closed, "back waits for a decision");
                InvokeViewMethod(view, "SaveAndLeave");
                Check("settings-draft-save", closed && (bool)BeatSage.Plugin.App.Snapshot["settings"]["autoImport"] != initial,
                    "save persists the edited setting before continuing");
            }
            finally
            {
                InvokeViewMethod(view, "ContinueEditing");
                InvokeViewMethod(view, "RestoreSavedDraft");
                BeatSage.Plugin.App.SaveSettings(original);
                InvokeViewMethod(view, "ApplySnapshot", BeatSage.Plugin.App.Snapshot);
                InvokeViewMethod(view, "ShowLocalTab");
            }
            yield return EndOfFrameStabilization();
        }

        private static bool IsViewDeactivated(Component view)
        {
            if (view == null)
            {
                return true;
            }

            var controller = view as HMUI.ViewController;
            return controller != null ? !controller.isActivated : !view.gameObject.activeInHierarchy;
        }

        private IEnumerator CheckLocalization(Component view)
        {
            yield return WaitUntil(() => !(bool)ReadField(view, "loadingBrowser") && !(bool)ReadField(view, "loadingScan"),
                30, "browser idle before localization feedback");
            var state = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("ModUiChinese.LocalizationState", false)).FirstOrDefault(type => type != null);
            if (state == null) yield break;
            yield return WaitUntil(() => !Equals(ReadField(view, "loadingBrowser"), true) && !Equals(ReadField(view, "loadingScan"), true), 30, "language view idle");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var translator = state.Assembly.GetType("ModUiChinese.UiTranslator");
            var options = state.GetField("ActiveOptions", flags).GetValue(null);
            var enabled = options.GetType().GetField("Enabled");
            var mods = (IDictionary)options.GetType().GetField("Mods").GetValue(options);
            var customField = state.GetField("Custom", flags);
            var previousCustom = customField.GetValue(null);
            bool previousEnabled = (bool)enabled.GetValue(options);
            bool hadScope = mods.Contains("BeatSage");
            object previousScope = hadScope ? mods["BeatSage"] : null;
            var reload = translator.GetMethod("Reload", flags);
            var statusField = view.GetType().GetField("statusMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            var songField = view.GetType().GetField("selectedSong", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldStatus = statusField.GetValue(view);
            var errorField = view.GetType().GetField("errorMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldError = errorField.GetValue(view);
            var oldSong = songField.GetValue(view);
            var setStatus = view.GetType().GetMethod("SetError", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<string, bool> visible = text => view.GetComponentsInChildren<TMP_Text>(true).Any(label => label.text == text);
            Action refresh = () => reload.Invoke(null, null);
            try
            {
                enabled.SetValue(options, true);
                mods["BeatSage"] = true;
                songField.SetValue(view, new JObject { ["title"] = "Default", ["artist"] = "Settings" });
                setStatus.Invoke(view, new object[] { "Select a task first", new object[0] });
                refresh();
                yield return new WaitForSecondsRealtime(0.6f);
                WriteJson("language-enabled-ui.json", translator.GetMethod("Capture", flags).Invoke(null, null));
                Check("language-default-chinese", visible("本地音乐") && visible("请先选择任务"), "local=" + visible("本地音乐") + "; status=" + string.Join("|", view.GetComponentsInChildren<TMP_Text>(true).Select(label => label.text).Where(text => text.Contains("任务") || text.Contains("task") || text.Contains("项"))));
                Check("language-user-song-preserved", visible("Default"), "Song title Default remains user data");
                var feedbackText = ReadField(view, "errorText") as TMP_Text;
                var feedbackHint = feedbackText == null ? null : feedbackText.GetComponent<HMUI.HoverHint>() ?? feedbackText.GetComponentInParent<HMUI.HoverHint>();
                Check("ui-feedback-full-text-hint", feedbackText != null && feedbackText.raycastTarget && feedbackHint != null && feedbackHint.text == feedbackText.text,
                    "Feedback has a raycastable tooltip with its full current value");
                Check("ui-feedback-wraps", feedbackText != null && feedbackText.textWrappingMode == TextWrappingModes.Normal,
                    "Feedback wraps instead of truncating the error");

                enabled.SetValue(options, false);
                refresh();
                yield return new WaitForSecondsRealtime(0.6f);
                Check("language-global-off-english", visible("Local Music") && visible("Select a task first") && !visible("本地音乐"), "Static and bound text restored to source English");
                enabled.SetValue(options, true);
                mods["BeatSage"] = false;
                refresh();
                yield return new WaitForSecondsRealtime(0.6f);
                Check("language-scope-off-english", visible("Local Music") && visible("Select a task first") && !visible("本地音乐"), "BeatSage disable blocks Common fallback");

                mods["BeatSage"] = true;
                customField.SetValue(null, new Dictionary<string, Dictionary<string, string>> {
                    ["BeatSage"] = new Dictionary<string, string> { ["Local Music"] = "自定义音乐" }
                });
                refresh();
                yield return new WaitForSecondsRealtime(0.6f);
                Check("language-custom-caption-priority", visible("自定义音乐") && visible("Default"), "Custom caption wins and user title stays intact");
            }
            finally
            {
                enabled.SetValue(options, previousEnabled);
                if (hadScope) mods["BeatSage"] = previousScope; else mods.Remove("BeatSage");
                customField.SetValue(null, previousCustom);
                statusField.SetValue(view, oldStatus);
                errorField.SetValue(view, oldError);
                songField.SetValue(view, oldSong);
                refresh();
            }
            yield return null;
        }

        private static bool IsViewStable(Component view)
        {
            var controller = view as HMUI.ViewController;
            return controller != null && controller.isActivated && !controller.isInTransition;
        }

        private static bool IsMenuFlowStable(Component view)
        {
            var field = typeof(BeatSage.UI.Menu).GetField("flowCoordinator", BindingFlags.Static | BindingFlags.NonPublic);
            var flow = field?.GetValue(null) as HMUI.FlowCoordinator;
            return flow != null && flow.isActivated && !flow.isInTransition && IsViewStable(view);
        }

        private static bool IsMenuFlowDeactivated()
        {
            var field = typeof(BeatSage.UI.Menu).GetField("flowCoordinator", BindingFlags.Static | BindingFlags.NonPublic);
            var flow = field?.GetValue(null) as HMUI.FlowCoordinator;
            return flow == null || !flow.isActivated;
        }

        private IEnumerator CheckImportedUiShowSong(string levelId)
        {
            MenuButton button = null;
            yield return WaitUntil(() =>
            {
                var menu = Resources.FindObjectsOfTypeAll<MainMenuViewController>()
                    .FirstOrDefault(value => value != null && value.isActivated && value.gameObject.activeInHierarchy);
                if (menu == null)
                {
                    return false;
                }

                try
                {
                    button = FindBeatSageButton();
                    return button != null;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }, MenuWaitSeconds, "menu button for show song");
            Check("show-song-menu-button", button != null, button == null ? "Beat Sage center entry not found" : button.Text);
            if (button == null)
            {
                yield break;
            }

            try
            {
                button.ButtonClicked();
            }
            catch (Exception ex)
            {
                Check("show-song-menu-open", false, ex.ToString());
                yield break;
            }

            var viewType = typeof(BeatSage.Plugin).Assembly.GetType("BeatSage.UI.BeatSageViewController");
            Component view = null;
            yield return WaitUntil(() =>
            {
                if (viewType == null)
                {
                    return false;
                }

                view = Resources.FindObjectsOfTypeAll(viewType).OfType<Component>()
                    .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy);
                return view != null;
            }, 30, "BSML view for show song");
            Check("show-song-view-created", view != null, viewType == null ? "BeatSageViewController type missing" : "active=" + (view != null));
            if (view == null)
            {
                yield break;
            }

            yield return WaitUntil(() => IsMenuFlowStable(view), 30, "show-song flow transition");
            yield return WaitForUiSettled();
            var localActionError = InvokeViewMethod(view, "ShowLocalTab");
            Check("show-song-local-tab", localActionError == null, localActionError == null ? "local tab" : localActionError.ToString());
            Check("show-song-local-panel", PanelActive(view, "localPanel") && !PanelActive(view, "settingsPanel") && !PanelActive(view, "queuePanel"), "local=" + PanelActive(view, "localPanel") + "; settings=" + PanelActive(view, "settingsPanel") + "; queue=" + PanelActive(view, "queuePanel"));
            var browseError = InvokeViewMethod(view, "RefreshBrowserAsync", Path.GetDirectoryName(wishPath));
            Check("show-song-local-browse", browseError == null, browseError == null ? Path.GetDirectoryName(wishPath) : browseError.ToString());
            yield return WaitUntil(() => UiRowsContain(view, "browserRows", wishPath), 60, "UI browser rows");
            Check("show-song-browser-row", UiRowsContain(view, "browserRows", wishPath), "wish row visible in local browser");

            var clearBeforeScanError = InvokeViewMethod(view, "ClearScan");
            Check("show-song-clear-before-scan", clearBeforeScanError == null, clearBeforeScanError == null ? "ClearScan" : clearBeforeScanError.ToString());
            yield return EndOfFrameStabilization();
            CheckSubmitButtonState(view, false, "show-song-submit-disabled-before-scan");

            var scanError = InvokeViewMethod(view, "ScanAsync", new[] { wishPath }, false);
            Check("show-song-local-scan", scanError == null, scanError == null ? wishPath : scanError.ToString());
            yield return WaitUntil(() => UiRowsContainWithoutError(view, "scanRows", wishPath), 90, "UI scan rows");
            Check("show-song-scan-row", UiRowsContainWithoutError(view, "scanRows", wishPath), "wish row visible in scan preview");
            yield return EndOfFrameStabilization();
            CheckSubmitButtonState(view, true, "show-song-submit-enabled-after-scan");
            CheckVisibleList(view, "browserList", "wish", "show-song-browser-visible-cell");
            CheckVisibleList(view, "scanList", "wish", "show-song-scan-visible-cell");
            CheckVisibleText(view, "wish", "show-song-visible-title");
            CheckVisibleText(view, Path.GetDirectoryName(wishPath), "show-song-visible-path");
            CheckVisibleSelectionCount(view, "show-song-visible-selection-count");

            var clearSelectionError = InvokeViewMethod(view, "ClearScan");
            Check("show-song-clear-scan-selection", clearSelectionError == null, clearSelectionError == null ? "ClearScan" : clearSelectionError.ToString());
            yield return EndOfFrameStabilization();
            CheckSubmitButtonState(view, false, "show-song-submit-disabled-after-clear");
            var scanListForSelection = (ReadField(view, "scanList") as CustomListTableData)?.TableView;
            var selectOneError = InvokeViewMethod(view, "ScanSelected", scanListForSelection, 0);
            Check("show-song-restore-scan-selection", selectOneError == null, selectOneError == null ? "ScanSelected index=0" : selectOneError.ToString());
            yield return EndOfFrameStabilization();
            var selectAllError = InvokeViewMethod(view, "SelectAllScan");
            Check("show-song-select-all-scan", selectAllError == null, selectAllError == null ? "SelectAllScan" : selectAllError.ToString());
            yield return EndOfFrameStabilization();
            CheckSubmitButtonState(view, true, "show-song-submit-enabled-after-select-all");
            CheckActivePanelGeometry(view, "localPanel", "show-song-local-geometry");
            CaptureViewScreenshot("menu-local.png", "show-song-local-screenshot");
            var editorError = InvokeViewMethod(view, "OpenSongEditor");
            Check("show-song-editor-open", editorError == null, editorError == null ? "editor" : editorError.ToString());
            yield return EndOfFrameStabilization();
            yield return new WaitForSecondsRealtime(0.35f);
            yield return EndOfFrameStabilization();
            Check("show-song-metadata-edit", TextControlActiveAny(view, new[] { "标题", "Title" }) && TextControlActiveAny(view, new[] { "艺人", "Artist" }), "title/artist edit controls active");
            CaptureViewScreenshot("menu-song-editor.png", "show-song-editor-screenshot");
            InvokeViewMethod(view, "CloseSongEditor");

            var settingsError = InvokeViewMethod(view, "ShowSettingsTab");
            Check("show-song-settings-tab", settingsError == null, settingsError == null ? "settings tab" : settingsError.ToString());
            yield return EndOfFrameStabilization();
            Check("show-song-settings-panel", PanelActive(view, "settingsPanel") && !PanelActive(view, "localPanel") && !PanelActive(view, "queuePanel"), "local=" + PanelActive(view, "localPanel") + "; settings=" + PanelActive(view, "settingsPanel") + "; queue=" + PanelActive(view, "queuePanel"));
            CheckActivePanelGeometry(view, "settingsPanel", "show-song-settings-geometry");
            yield return CheckSettingsSubPage(view, "ShowGenerationSettings", "generation", "generationPanel", new[] { "Generation", "生成" });
            var modelV1Error = SetViewProperty(view, "SelectedModel", "v1");
            Check("show-song-model-v1", modelV1Error == null, modelV1Error == null ? "v1" : modelV1Error.ToString());
            yield return EndOfFrameStabilization();
            CheckModelOptionVisibility(view, false, "show-song-v1-hides-unsupported");
            var modelV2Error = SetViewProperty(view, "SelectedModel", "v2");
            Check("show-song-model-v2", modelV2Error == null, modelV2Error == null ? "v2" : modelV2Error.ToString());
            yield return EndOfFrameStabilization();
            CheckModelOptionVisibility(view, true, "show-song-v2-restores-supported");
            CaptureViewScreenshot("menu-settings-generation.png", "show-song-settings-generation-screenshot");
            yield return CheckChoicePopupSurface(view, "OpenModelChoices", "model", true);
            yield return CheckEnvironmentChoicePopup(view);
            yield return CheckSettingsSubPage(view, "ShowApplicationSettings", "application", "applicationPanel", new[] { "Application", "应用" });
            yield return CheckSettingsSubPage(view, "ShowPresetSettings", "presets", "presetsPanel", new[] { "Presets", "Preset", "预设" });
            yield return CheckChoicePopupSurface(view, "OpenPresetChoices", "preset", false);

            var queueError = InvokeViewMethod(view, "ShowQueueTab");
            Check("show-song-queue-tab", queueError == null, queueError == null ? "queue tab" : queueError.ToString());
            if (ModalActive(view, "unsavedModal")) InvokeViewMethod(view, "DiscardAndLeave");
            yield return EndOfFrameStabilization();
            Check("show-song-queue-panel", PanelActive(view, "queuePanel") && !PanelActive(view, "localPanel") && !PanelActive(view, "settingsPanel"), "local=" + PanelActive(view, "localPanel") + "; settings=" + PanelActive(view, "settingsPanel") + "; queue=" + PanelActive(view, "queuePanel"));
            var jobs = (ReadField(view, "jobs") as IEnumerable)?.OfType<JObject>().ToArray() ?? new JObject[0];
            var jobIndex = Array.FindIndex(jobs, job => string.Equals((string)job["levelId"], levelId, StringComparison.OrdinalIgnoreCase) && string.Equals((string)job["state"], "Imported", StringComparison.OrdinalIgnoreCase));
            Check("show-song-imported-job", jobIndex >= 0, jobIndex < 0 ? "Imported job for levelId not found: " + levelId : (string)jobs[jobIndex]["id"]);
            if (jobIndex < 0)
            {
                BeatSage.UI.Menu.Close(null);
                yield break;
            }

            var queueSelectedForScreenshot = viewType.GetMethod("QueueSelected", BindingFlags.Instance | BindingFlags.NonPublic);
            if (queueSelectedForScreenshot != null)
            {
                queueSelectedForScreenshot.Invoke(view, new object[] { null, jobIndex });
                yield return EndOfFrameStabilization();
            }
            CheckVisibleList(view, "queueList", "wish", "show-song-queue-visible-cell");
            CheckActivePanelGeometry(view, "queuePanel", "show-song-queue-geometry");
            CaptureViewScreenshot("menu-queue.png", "show-song-queue-screenshot");

            try
            {
                var showSong = viewType.GetMethod("ShowSong", BindingFlags.Instance | BindingFlags.NonPublic);
                if (showSong == null)
                {
                    throw new MissingMethodException("ShowSong action missing");
                }

                showSong.Invoke(view, null);
                Check("show-song-ui-action", showSong != null, showSong == null ? "ShowSong action missing" : (string)jobs[jobIndex]["id"]);
            }
            catch (Exception ex)
            {
                Check("show-song-ui-action", false, ex.ToString());
                BeatSage.UI.Menu.Close(null);
                yield break;
            }

            yield return WaitUntil(() => FindNativeDetail(levelId) != null, 90, "native song detail");
            var native = FindNativeDetail(levelId);
            Check("show-song-native-detail", native != null, native == null ? "StandardLevelDetailViewController not opened for " + levelId : levelId);
            if (native == null)
            {
                yield break;
            }

            yield return WaitUntil(() => NativeActionsReady(native), 30, "native song actions");
            Check("show-song-native-actions", NativeActionsReady(native), "play=" + NativeActionState(native, false) + "; practice=" + NativeActionState(native, true));
        }

        private IEnumerator CheckSettingsSubPage(Component view, string methodName, string key, string panelField, string[] headingCandidates)
        {
            var methodError = InvokeViewMethod(view, methodName);
            Check("settings-" + key + "-method", methodError == null, methodError == null ? methodName : methodError.ToString());
            yield return EndOfFrameStabilization();

            var panel = GetTransform(ReadField(view, panelField)) as RectTransform;
            var activeScrolls = panel == null
                ? new HMUI.ScrollView[0]
                : panel.GetComponentsInChildren<HMUI.ScrollView>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy)
                    .ToArray();
            var activeControls = panel == null
                ? new Selectable[0]
                : panel.GetComponentsInChildren<Selectable>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy && value.isActiveAndEnabled)
                    .ToArray();
            var heading = ActiveTexts(view).FirstOrDefault(text => (headingCandidates ?? new string[0])
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Any(candidate => text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0));
            var detail = "panel=" + PanelActive(view, panelField) + "; activeControls=" + activeControls.Length + "; activeScrolls=" + activeScrolls.Length + "; heading=" + (heading ?? "");
            Check("settings-" + key + "-single-screen", PanelActive(view, panelField) && activeControls.Length > 0 && activeScrolls.Length == 0, detail);
            Check("settings-" + key + "-visible-heading", !string.IsNullOrWhiteSpace(heading), detail);
            CheckActivePanelGeometry(view, panelField, "settings-" + key + "-bounds");
            CaptureViewScreenshot("menu-settings-" + key + ".png", "settings-" + key + "-screenshot");
        }

        private IEnumerator CheckChoicePopupSurface(Component view, string methodName, string kind, bool requireCell)
        {
            var openError = InvokeViewMethod(view, methodName);
            Check("settings-" + kind + "-choices-open", openError == null, openError == null ? methodName : openError.ToString());
            yield return EndOfFrameStabilization();

            var list = ReadField(view, "choiceList") as CustomListTableData;
            var table = list?.TableView;
            var dataCount = list?.Data?.Count ?? 0;
            var activeCells = table == null
                ? 0
                : table.GetComponentsInChildren<TableCell>(true).Count(value => value != null && value.gameObject.activeInHierarchy);
            var modalActive = ModalActive(view, "choiceModal");
            var surfacePass = modalActive && (requireCell ? dataCount > 0 && activeCells > 0 : dataCount == 0 || activeCells > 0);
            Check("settings-" + kind + "-choices-visible", surfacePass, "modal=" + modalActive + "; data=" + dataCount + "; activeCells=" + activeCells);
            if (requireCell || dataCount > 0)
            {
                CheckVisibleList(view, "choiceList", string.Empty, "settings-" + kind + "-choices-visible-cell");
            }

            var closeError = InvokeViewMethod(view, "CloseChoices");
            Check("settings-" + kind + "-choices-close", closeError == null, closeError == null ? "CloseChoices" : closeError.ToString());
            yield return WaitUntil(() => !ModalActive(view, "choiceModal"), 2, "choice modal close");
            Check("settings-" + kind + "-choices-dismissed", !ModalActive(view, "choiceModal"), "modal=" + ModalActive(view, "choiceModal"));
        }

        private IEnumerator CheckEnvironmentChoicePopup(Component view)
        {
            var before = ReadViewProperty(view, "SelectedEnvironment") as string;
            var beforeLabels = EnvironmentLabels(view);
            var openError = InvokeViewMethod(view, "OpenEnvironmentChoices");
            Check("settings-environment-choices-open", openError == null, openError == null ? "OpenEnvironmentChoices" : openError.ToString());
            yield return EndOfFrameStabilization();

            var list = ReadField(view, "choiceList") as CustomListTableData;
            var table = list?.TableView;
            var dataCount = list?.Data?.Count ?? 0;
            var activeCells = table == null
                ? 0
                : table.GetComponentsInChildren<TableCell>(true).Count(value => value != null && value.gameObject.activeInHierarchy);
            var modalActive = ModalActive(view, "choiceModal");
            Check("settings-environment-choices-visible", modalActive && dataCount > 0 && activeCells > 0, "modal=" + modalActive + "; data=" + dataCount + "; activeCells=" + activeCells);
            CheckActivePanelGeometry(view, "choiceModal", "settings-environment-modal-bounds");
            CheckVisibleList(view, "choiceList", string.Empty, "settings-environment-choices-visible-cell");
            yield return new WaitForSecondsRealtime(0.35f);
            yield return EndOfFrameStabilization();
            CaptureViewScreenshot("menu-settings-environment-choices.png", "settings-environment-choices-screenshot");

            if (table != null && dataCount > 0)
            {
                var canonicalChoiceValues = ReadField(view, "choiceValues") as IEnumerable;
                var canonicalValues = canonicalChoiceValues == null
                    ? new string[0]
                    : canonicalChoiceValues.Cast<object>().Select(value => value?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                var oldIndex = FindCanonicalChoiceIndex(canonicalChoiceValues, before);
                if (oldIndex < 0)
                {
                    oldIndex = FindChoiceIndex(list, before);
                }

                var targetValue = canonicalValues
                    .FirstOrDefault(value => !string.Equals(value, before, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(targetValue))
                {
                    targetValue = (BeatSage.Plugin.App.Environments ?? new string[0])
                    .FirstOrDefault(value => !string.Equals(value, before, StringComparison.OrdinalIgnoreCase));
                }

                var targetIndex = FindCanonicalChoiceIndex(canonicalChoiceValues, targetValue);
                if (targetIndex < 0)
                {
                    targetIndex = FindChoiceIndex(list, targetValue);
                }
                if (targetIndex < 0)
                {
                    for (var index = 0; index < dataCount; index++)
                    {
                        if (index != oldIndex)
                        {
                            targetIndex = index;
                            break;
                        }
                    }
                }

                if (targetIndex >= 0 && targetIndex != oldIndex)
                {
                    var selectedError = InvokeViewMethod(view, "ChoiceSelected", table, targetIndex);
                    Check("settings-environment-choice-select", selectedError == null, selectedError == null ? "index=" + targetIndex : selectedError.ToString());
                    yield return WaitUntil(() => !ModalActive(view, "choiceModal"), 2, "environment choice close");
                    yield return EndOfFrameStabilization();
                    var after = ReadViewProperty(view, "SelectedEnvironment") as string;
                    var afterLabels = EnvironmentLabels(view);
                    var propertyChanged = !string.IsNullOrWhiteSpace(after) && !string.Equals(after, before, StringComparison.OrdinalIgnoreCase);
                    var labelChanged = !beforeLabels.SequenceEqual(afterLabels, StringComparer.Ordinal);
                    Check("settings-environment-label-update", propertyChanged && labelChanged, "before=" + before + "; after=" + after + "; labelsBefore=" + string.Join(" | ", beforeLabels) + "; labelsAfter=" + string.Join(" | ", afterLabels));

                    var restoreOpenError = InvokeViewMethod(view, "OpenEnvironmentChoices");
                    Check("settings-environment-restore-open", restoreOpenError == null, restoreOpenError == null ? "OpenEnvironmentChoices" : restoreOpenError.ToString());
                    yield return EndOfFrameStabilization();
                    var restoreList = ReadField(view, "choiceList") as CustomListTableData;
                    var restoreTable = restoreList?.TableView;
                    var restoreValues = ReadField(view, "choiceValues") as IEnumerable;
                    var restoreIndex = FindCanonicalChoiceIndex(restoreValues, before);
                    if (restoreIndex < 0)
                    {
                        restoreIndex = FindChoiceIndex(restoreList, before);
                    }
                    var restoreError = restoreTable == null || restoreIndex < 0
                        ? new InvalidOperationException("Original environment choice is not available")
                        : InvokeViewMethod(view, "ChoiceSelected", restoreTable, restoreIndex);
                    Check("settings-environment-choice-restore", restoreError == null, restoreError == null ? before : restoreError.ToString());
                    yield return WaitUntil(() => !ModalActive(view, "choiceModal"), 2, "environment restore close");
                    yield return EndOfFrameStabilization();
                    var restored = ReadViewProperty(view, "SelectedEnvironment") as string;
                    Check("settings-environment-restored", string.Equals(restored, before, StringComparison.OrdinalIgnoreCase), "before=" + before + "; restored=" + restored);
                }
                else
                {
                    Check("settings-environment-choice-select", true, "only one environment choice is available");
                }
            }

            var closeError = InvokeViewMethod(view, "CloseChoices");
            Check("settings-environment-choices-close", closeError == null, closeError == null ? "CloseChoices" : closeError.ToString());
            yield return WaitUntil(() => !ModalActive(view, "choiceModal"), 2, "environment choice close after close");
            Check("settings-environment-choices-dismissed", !ModalActive(view, "choiceModal"), "modal=" + ModalActive(view, "choiceModal"));
        }

        private static Exception InvokeViewMethod(Component view, string name, params object[] arguments)
        {
            try
            {
                var method = view.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null)
                {
                    return new MissingMethodException(name);
                }

                method.Invoke(view, arguments);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static Exception SetViewProperty(Component view, string name, object value)
        {
            try
            {
                var property = view.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (property == null)
                {
                    return new MissingMemberException(name);
                }

                property.SetValue(view, value, null);
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static object ReadViewProperty(Component view, string name)
        {
            if (view == null)
            {
                return null;
            }

            var property = view.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (property != null && property.GetMethod != null)
            {
                return property.GetValue(view, null);
            }

            var field = view.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(view);
        }

        private static bool ModalActive(Component view, string fieldName)
        {
            var transform = GetTransform(ReadField(view, fieldName));
            return transform != null && transform.gameObject.activeInHierarchy;
        }

        private void CheckSubmitButtonState(Component view, bool expectedInteractable, string checkName)
        {
            var button = ReadField(view, "submitButton") as Selectable;
            var source = "submitButton";
            if (button == null)
            {
                var buttonTransform = FindDescendant(view?.transform, "submit-button");
                button = buttonTransform?.GetComponent<Selectable>() ?? buttonTransform?.GetComponentInChildren<Selectable>(true);
                source = "submit-button descendant";
            }

            var active = button != null && button.gameObject.activeInHierarchy && button.isActiveAndEnabled;
            var interactable = button != null && button.interactable;
            Check(checkName, active && interactable == expectedInteractable, "source=" + source + "; found=" + (button != null) + "; active=" + active + "; interactable=" + interactable + "; expected=" + expectedInteractable);
        }

        private static string[] EnvironmentLabels(Component view)
        {
            return ActiveTexts(view)
                .Where(IsEnvironmentLabel)
                .ToArray();
        }

        private static bool IsEnvironmentLabel(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                (text.IndexOf("environment", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("环境", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static int FindChoiceIndex(CustomListTableData list, string value)
        {
            if (list == null || list.Data == null || string.IsNullOrWhiteSpace(value))
            {
                return -1;
            }

            var normalized = NormalizeChoiceText(value);
            for (var index = 0; index < list.Data.Count; index++)
            {
                var text = ChoiceText(list.Data[index]);
                var candidate = NormalizeChoiceText(text);
                if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(normalized) && candidate.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(candidate) && normalized.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int FindCanonicalChoiceIndex(IEnumerable values, string value)
        {
            if (values == null || string.IsNullOrWhiteSpace(value))
            {
                return -1;
            }

            var index = 0;
            foreach (var item in values)
            {
                if (string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private static string ChoiceText(object value)
        {
            var text = ReadStringMember(value, "text") ?? ReadStringMember(value, "Text");
            var subtext = ReadStringMember(value, "subtext") ?? ReadStringMember(value, "Subtext");
            return string.IsNullOrWhiteSpace(subtext) ? text : (text ?? string.Empty) + " " + subtext;
        }

        private static string NormalizeChoiceText(string value)
        {
            return (value ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("·", string.Empty)
                .ToLowerInvariant()
                .Replace("environment", string.Empty);
        }

        private static bool PanelActive(Component view, string fieldName)
        {
            var transform = GetTransform(ReadField(view, fieldName));
            return transform != null && transform.gameObject.activeInHierarchy;
        }

        private void CheckVisibleList(Component view, string fieldName, string expected, string checkName)
        {
            var list = ReadField(view, fieldName) as CustomListTableData;
            var table = list?.TableView;
            var activeCells = table == null ? 0 : table.GetComponentsInChildren<TableCell>(true).Count(value => value != null && value.gameObject.activeInHierarchy);
            var texts = table == null
                ? new string[0]
                : table.GetComponentsInChildren<TMP_Text>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(value.text))
                    .Select(value => value.text.Trim())
                    .ToArray();
            var matching = texts.FirstOrDefault(value => value.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
            var dataCount = list?.Data?.Count ?? 0;
            Check(checkName, dataCount > 0 && activeCells > 0 && !string.IsNullOrWhiteSpace(matching), "data=" + dataCount + "; activeCells=" + activeCells + "; text=" + string.Join(" | ", texts.Take(8)));
        }

        private void CheckVisibleText(Component view, string expected, string checkName)
        {
            var texts = view == null
                ? new string[0]
                : view.GetComponentsInChildren<TMP_Text>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(value.text))
                    .Select(value => value.text.Trim())
                    .ToArray();
            var matching = texts.FirstOrDefault(value => value.IndexOf(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
            Check(checkName, !string.IsNullOrWhiteSpace(matching), "expected=" + expected + "; text=" + string.Join(" | ", texts.Take(12)));
        }

        private void CheckVisibleSelectionCount(Component view, string checkName)
        {
            var texts = ActiveTexts(view);
            var matching = texts.FirstOrDefault(IsSelectionCountText);
            Check(checkName, !string.IsNullOrWhiteSpace(matching), "text=" + string.Join(" | ", texts.Take(16)));
        }

        private void CheckModelOptionVisibility(Component view, bool expectedVisible, string checkName)
        {
            var texts = ActiveTexts(view);
            var ninety = texts.Any(IsNinetyOptionText);
            var obstacles = texts.Any(IsObstacleOptionText);
            var passed = ninety == expectedVisible && obstacles == expectedVisible;
            Check(checkName, passed, "expectedVisible=" + expectedVisible + "; ninety=" + ninety + "; obstacles=" + obstacles + "; text=" + string.Join(" | ", texts.Take(20)));
        }

        private static string[] ActiveTexts(Component view)
        {
            return view == null
                ? new string[0]
                : view.GetComponentsInChildren<TMP_Text>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(value.text))
                    .Select(value => value.text.Trim())
                    .ToArray();
        }

        private static bool IsSelectionCountText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ((text.IndexOf("选择", StringComparison.Ordinal) >= 0 || text.IndexOf("选", StringComparison.Ordinal) >= 0) &&
                 (text.IndexOf("音乐", StringComparison.Ordinal) >= 0 || text.IndexOf("歌曲", StringComparison.Ordinal) >= 0));
        }

        private static bool IsNinetyOptionText(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                (text.IndexOf("90", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 text.IndexOf("ninety", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 text.IndexOf("旋转", StringComparison.Ordinal) >= 0);
        }

        private static bool IsObstacleOptionText(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                (text.IndexOf("obstacle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 text.IndexOf("障碍", StringComparison.Ordinal) >= 0);
        }

        private static bool TextControlActive(Component view, string text)
        {
            return view != null && view.GetComponentsInChildren<TMP_Text>(true).Any(value =>
                value != null && value.gameObject.activeInHierarchy && string.Equals((value.text ?? string.Empty).Trim(), text, StringComparison.Ordinal));
        }

        private static bool TextControlActiveAny(Component view, IEnumerable<string> texts)
        {
            var expected = new HashSet<string>(texts ?? new string[0], StringComparer.Ordinal);
            return view != null && view.GetComponentsInChildren<TMP_Text>(true).Any(value =>
                value != null && value.gameObject.activeInHierarchy && expected.Contains((value.text ?? string.Empty).Trim()));
        }

        private static bool UiRowsContain(Component view, string fieldName, string path)
        {
            var rows = ReadField(view, fieldName) as IEnumerable;
            return rows != null && rows.OfType<JObject>().Any(row => string.Equals((string)row["path"], path, StringComparison.OrdinalIgnoreCase));
        }

        private static bool UiRowsContainWithoutError(Component view, string fieldName, string path)
        {
            var rows = ReadField(view, fieldName) as IEnumerable;
            return rows != null && rows.OfType<JObject>().Any(row =>
                string.Equals((string)row["path"], path, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace((string)row["error"]));
        }

        private IEnumerator EndOfFrameStabilization()
        {
            for (var frame = 0; frame < 3; frame++)
            {
                yield return new WaitForEndOfFrame();
            }
        }

        private IEnumerator WaitForUiSettled()
        {
            yield return new WaitForSecondsRealtime(0.75f);
            yield return EndOfFrameStabilization();
        }

        private void CaptureViewScreenshot(string name, string checkName)
        {
            var path = Path.Combine(directory, name);
            try
            {
                CaptureMenuScreenshot(path);
                Check(checkName, File.Exists(path) && new FileInfo(path).Length > 0, path);
            }
            catch (Exception ex)
            {
                Check(checkName, false, ex.ToString());
            }
        }

        private static StandardLevelDetailViewController FindNativeDetail(string levelId)
        {
            return Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>()
                .FirstOrDefault(value => value != null && value.isActivated && value.gameObject.activeInHierarchy && MatchesLevel(value, levelId));
        }

        private static bool MatchesLevel(StandardLevelDetailViewController controller, string levelId)
        {
            try
            {
                return string.Equals(controller.beatmapKey.levelId, levelId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static StandardLevelDetailView GetNativeDetailView(StandardLevelDetailViewController controller)
        {
            var field = controller.GetType().GetField("_standardLevelDetailView", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(controller) as StandardLevelDetailView;
        }

        private static bool NativeActionState(StandardLevelDetailViewController controller, bool practice)
        {
            try
            {
                var view = GetNativeDetailView(controller);
                return view != null && (practice ? view.practiceButton : view.actionButton).interactable;
            }
            catch
            {
                return false;
            }
        }

        private static bool NativeActionsReady(StandardLevelDetailViewController controller)
        {
            return NativeActionState(controller, false) && NativeActionState(controller, true);
        }

        private static MenuButton FindBeatSageButton()
        {
            var registry = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("LocalModUI.UiRegistry", false)).FirstOrDefault(x => x != null);
            if (registry == null || !(bool)registry.GetProperty("IsReady").GetValue(null, null)) return null;
            var entries = registry?.GetProperty("Entries")?.GetValue(null, null) as IEnumerable;
            if (entries == null || !entries.Cast<object>().Any(x => (string)x.GetType().GetProperty("Id").GetValue(x, null) == "beatsage")) return null;
            return new MenuButton("Beat Sage", () => registry.GetMethod("Open").Invoke(null, new object[] { "beatsage" }));
        }

        private static void LeaveCenter()
        {
            var registry = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("LocalModUI.UiRegistry", false)).First(x => x != null);
            registry.GetMethod("LeaveCenter").Invoke(null, new object[] { (Action)(() => { }) });
        }

        private static bool CenterReady()
        {
            return Resources.FindObjectsOfTypeAll<FlowCoordinator>().Any(x => x != null && x.GetType().FullName == "LocalModUI.CenterFlow" && x.isActivated && !x.isInTransition);
        }

        private static bool HasEnvironmentCatalog()
        {
            try
            {
                var values = BeatSage.Plugin.App?.Environments;
                return values != null && values.Length > 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            if (root.name == name)
            {
                return root;
            }

            for (var index = 0; index < root.childCount; index++)
            {
                var found = FindDescendant(root.GetChild(index), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private void CheckUiGeometry(Component view)
        {
            var rootRect = view?.transform as RectTransform;
            var fieldNames = new[] { "browserList", "scanList", "queueList", "retryModal" };
            var names = new[] { "local-panel", "queue-panel" };
            var measurements = new Dictionary<string, object>(StringComparer.Ordinal);
            var transforms = new List<Transform>();
            foreach (var name in names)
            {
                var transform = FindDescendant(view?.transform, name);
                measurements[name] = RectInfo(transform);
            }
            foreach (var fieldName in fieldNames)
            {
                var transform = GetTransform(ReadField(view, fieldName));
                transforms.Add(transform);
                measurements[fieldName] = RectInfo(transform);
            }

            Canvas.ForceUpdateCanvases();
            var rootSizeValid = rootRect != null && rootRect.rect.width > 0f && rootRect.rect.height > 0f;
            var screenRect = FindScreenRect(rootRect);
            var rootInsideScreen = screenRect == null || RectInsideRect(screenRect, rootRect);
            var activeTransforms = transforms.Where(transform => transform != null && transform.gameObject.activeInHierarchy).ToArray();
            var activeListRects = activeTransforms.OfType<RectTransform>().ToArray();
            var controlsSized = activeTransforms.Length > 0 && activeTransforms.All(transform =>
            {
                var rect = transform as RectTransform;
                return rect != null && rect.rect.width > 0f && rect.rect.height > 0f;
            });
            var controlsFitWidth = rootSizeValid && activeTransforms.All(transform =>
            {
                var rect = transform as RectTransform;
                return rect != null && rect.rect.width <= rootRect.rect.width * 1.2f + 2f;
            });
            var controlsInsideRoot = rootSizeValid && activeListRects.All(rect => RectInsideRoot(rootRect, rect));
            var controlsInsideScreen = screenRect == null || activeListRects.All(rect => RectInsideRect(screenRect, rect));
            var controlsHaveMask = activeListRects.All(rect => HasScrollMask(rect.transform, rootRect?.transform));
            var geometryDetail = rootRect == null
                ? "view root is not a RectTransform"
                : "root=" + rootRect.rect.width.ToString("0.0") + "x" + rootRect.rect.height.ToString("0.0") + "; screen=" + (screenRect == null ? "unknown" : screenRect.rect.width.ToString("0.0") + "x" + screenRect.rect.height.ToString("0.0")) + "; rootInsideScreen=" + rootInsideScreen + "; controlsSized=" + controlsSized + "; controlsFitWidth=" + controlsFitWidth + "; insideRoot=" + controlsInsideRoot + "; insideScreen=" + controlsInsideScreen + "; masks=" + controlsHaveMask;
            Check("menu-ui-geometry", rootSizeValid && controlsSized && controlsFitWidth && controlsInsideRoot && controlsHaveMask && rootInsideScreen && controlsInsideScreen, geometryDetail);

            var screenshotPath = Path.Combine(directory, "menu-bsml.png");
            try
            {
                CaptureMenuScreenshot(screenshotPath);
                Check("menu-bsml-screenshot", File.Exists(screenshotPath) && new FileInfo(screenshotPath).Length > 0, screenshotPath);
            }
            catch (Exception ex)
            {
                Check("menu-bsml-screenshot", false, ex.ToString());
            }

            WriteJson("ui-geometry.json", new
            {
                root = RectInfo(view?.transform),
                screen = RectInfo(screenRect),
                controls = measurements,
                activeBounds = activeListRects.Select(rect => new { name = rect.name, insideRoot = RectInsideRoot(rootRect, rect), insideScreen = screenRect == null || RectInsideRect(screenRect, rect), mask = HasScrollMask(rect.transform, rootRect?.transform) }).ToArray(),
                screenshot = screenshotPath
            });
        }

        private void CheckActivePanelGeometry(Component view, string panelField, string checkName)
        {
            var rootRect = view?.transform as RectTransform;
            var panel = GetTransform(ReadField(view, panelField)) as RectTransform;
            var screenRect = FindScreenRect(rootRect);
            var targets = new List<RectTransform>();
            if (panel != null)
            {
                targets.Add(panel);
                targets.AddRange(panel.GetComponentsInChildren<Selectable>(true)
                    .Where(value => value != null && value.gameObject.activeInHierarchy)
                    .Select(value => value.transform as RectTransform)
                    .Where(value => value != null));
            }

            var scroll = panelField == "settingsPanel" && panel != null ? panel.GetComponentsInChildren<HMUI.ScrollView>(true).FirstOrDefault() : null;
            var viewport = scroll == null ? null : typeof(HMUI.ScrollView).GetField("_viewport", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(scroll) as RectTransform;
            var scrollContent = scroll == null ? null : typeof(HMUI.ScrollView).GetField("_contentRectTransform", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(scroll) as RectTransform;

            foreach (var listField in panelField == "localPanel"
                ? new[] { "browserList", "scanList" }
                : panelField == "queuePanel" ? new[] { "queueList" } : new string[0])
            {
                var listRect = GetTransform(ReadField(view, listField)) as RectTransform;
                if (listRect != null && listRect.gameObject.activeInHierarchy)
                {
                    targets.Add(listRect);
                }
            }

            var active = panel != null && panel.gameObject.activeInHierarchy;
            var visibleTargets = targets.Where(value => value == panel ||
                (RectIntersectsRect(panel, value) && (viewport == null || RectIntersectsRect(viewport, value)))).ToArray();
            var sized = visibleTargets.Length > 1 && visibleTargets.All(value => value.rect.width > 0f && value.rect.height > 0f);
            var insidePanel = panel != null && visibleTargets.All(value => value == panel || (viewport == null || scrollContent == null || !value.IsChildOf(scrollContent) ? RectInsideRect(panel, value) : ScrollControlInside(panel, viewport, value)));
            var insideScreen = screenRect == null || visibleTargets.All(value => value == panel || (viewport == null || scrollContent == null || !value.IsChildOf(scrollContent) ? RectInsideRect(screenRect, value) : ScrollControlInside(screenRect, viewport, value)));
            var interactable = panel != null && panel.GetComponentsInChildren<Selectable>(true).Any(value => value != null && value.isActiveAndEnabled && value.interactable);
            var detail = panelField + " active=" + active + "; visible=" + visibleTargets.Length + "; sized=" + sized + "; insidePanel=" + insidePanel + "; insideScreen=" + insideScreen + "; interactable=" + interactable;
            Check(checkName, active && sized && insidePanel && insideScreen && interactable, detail);
            WriteJson(checkName + ".json", new {detail, viewport = viewport?.name, bounds = visibleTargets.Select(value => { var r = LocalBounds(panel, value); return new {value.name, r.xMin, r.xMax, r.yMin, r.yMax}; })});
        }

        private static Rect LocalBounds(RectTransform outer, RectTransform inner)
        {
            var corners = new Vector3[4]; inner.GetWorldCorners(corners);
            var local = corners.Select(outer.InverseTransformPoint).ToArray();
            return Rect.MinMaxRect(local.Min(v => v.x), local.Min(v => v.y), local.Max(v => v.x), local.Max(v => v.y));
        }

        // Vertical clipping is expected while scrolling. Horizontal clipping is
        // always a layout error; retain the complete original width check.
        private static bool ScrollControlInside(RectTransform outer, RectTransform viewport, RectTransform control)
        {
            var bounds = LocalBounds(outer, control); var clip = LocalBounds(outer, viewport);
            return bounds.xMin >= outer.rect.xMin - 1f && bounds.xMax <= outer.rect.xMax + 1f
                && bounds.xMin >= clip.xMin - 1f && bounds.xMax <= clip.xMax + 1f
                && Math.Max(bounds.yMin, clip.yMin) >= outer.rect.yMin - 1f
                && Math.Min(bounds.yMax, clip.yMax) <= outer.rect.yMax + 1f;
        }

        private static bool RectInsideRoot(RectTransform root, RectTransform child)
        {
            if (root == null || child == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            child.GetWorldCorners(corners);
            for (var index = 0; index < corners.Length; index++)
            {
                var point = root.InverseTransformPoint(corners[index]);
                if (point.x < root.rect.xMin - 1f || point.x > root.rect.xMax + 1f || point.y < root.rect.yMin - 1f || point.y > root.rect.yMax + 1f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RectInsideRect(RectTransform outer, RectTransform inner)
        {
            if (outer == null || inner == null)
            {
                return false;
            }

            var corners = new Vector3[4];
            inner.GetWorldCorners(corners);
            for (var index = 0; index < corners.Length; index++)
            {
                var point = outer.InverseTransformPoint(corners[index]);
                if (point.x < outer.rect.xMin - 1f || point.x > outer.rect.xMax + 1f || point.y < outer.rect.yMin - 1f || point.y > outer.rect.yMax + 1f)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RectIntersectsRect(RectTransform first, RectTransform second)
        {
            if (first == null || second == null)
            {
                return false;
            }

            var firstCorners = new Vector3[4];
            var secondCorners = new Vector3[4];
            first.GetWorldCorners(firstCorners);
            second.GetWorldCorners(secondCorners);
            var firstMin = firstCorners.Select(first.InverseTransformPoint).Aggregate((left, right) => new Vector3(Mathf.Min(left.x, right.x), Mathf.Min(left.y, right.y), 0f));
            var firstMax = firstCorners.Select(first.InverseTransformPoint).Aggregate((left, right) => new Vector3(Mathf.Max(left.x, right.x), Mathf.Max(left.y, right.y), 0f));
            var secondInFirst = secondCorners.Select(first.InverseTransformPoint).ToArray();
            var secondMin = secondInFirst.Aggregate((left, right) => new Vector3(Mathf.Min(left.x, right.x), Mathf.Min(left.y, right.y), 0f));
            var secondMax = secondInFirst.Aggregate((left, right) => new Vector3(Mathf.Max(left.x, right.x), Mathf.Max(left.y, right.y), 0f));
            return firstMin.x <= secondMax.x && firstMax.x >= secondMin.x && firstMin.y <= secondMax.y && firstMax.y >= secondMin.y;
        }

        private static RectTransform FindScreenRect(RectTransform root)
        {
            for (var current = root?.parent; current != null; current = current.parent)
            {
                var canvas = current.GetComponent<Canvas>();
                if (canvas != null && canvas.isRootCanvas && current is RectTransform canvasRect && canvasRect.rect.width > 0f && canvasRect.rect.height > 0f)
                {
                    return canvasRect;
                }
            }

            return root?.parent as RectTransform;
        }

        private static bool HasScrollMask(Transform child, Transform root)
        {
            for (var current = child; current != null; current = current.parent)
            {
                if (current.GetComponent<RectMask2D>() != null || current.GetComponent<Mask>() != null)
                {
                    return true;
                }

                if (current == root)
                {
                    break;
                }
            }

            return child != null && (child.GetComponentsInChildren<RectMask2D>(true).Length > 0 || child.GetComponentsInChildren<Mask>(true).Length > 0);
        }

        private static object ReadField(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance);
        }

        private static Transform GetTransform(object value)
        {
            if (value is Component component)
            {
                return component.transform;
            }

            if (value is GameObject gameObject)
            {
                return gameObject.transform;
            }

            if (value == null)
            {
                return null;
            }

            var property = value.GetType().GetProperty("transform", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property?.GetValue(value, null) as Transform;
        }

        private static object RectInfo(Transform transform)
        {
            var rect = transform as RectTransform;
            if (rect == null)
            {
                return new Dictionary<string, object>
                {
                    ["found"] = false,
                    ["name"] = transform?.name
                };
            }

            return new Dictionary<string, object>
            {
                ["found"] = true,
                ["name"] = transform.name,
                ["active"] = transform.gameObject.activeInHierarchy,
                ["width"] = rect.rect.width,
                ["height"] = rect.rect.height,
                ["x"] = rect.rect.x,
                ["y"] = rect.rect.y
            };
        }

        private static void CaptureMenuScreenshot(string path)
        {
            var camera = Resources.FindObjectsOfTypeAll<Camera>()
                .FirstOrDefault(value => value != null && value.name == "MenuMainCamera" && value.gameObject.activeInHierarchy)
                ?? Camera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("Menu camera absent");
            }

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousFov = camera.fieldOfView;
            var previousRotation = camera.transform.rotation;
            var avatarRenderers = Resources.FindObjectsOfTypeAll<Renderer>()
                .Where(value => value != null && value.enabled && value.GetComponentsInParent<MonoBehaviour>(true)
                    .Any(component => component != null && component.GetType().Assembly.GetName().Name == "CustomAvatar"))
                .ToArray();
            const int width = 1920;
            const int height = 1080;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                foreach (var renderer in avatarRenderers)
                {
                    renderer.enabled = false;
                }

                target.Create();
                camera.targetTexture = target;
                // The runtime evidence camera is temporary. Aim at the active view's
                // world-space center so the complete menu and native titlebar stay in
                // frame; restore the camera rotation and FOV after capture.
                var viewType = typeof(BeatSage.Plugin).Assembly.GetType("BeatSage.UI.BeatSageViewController");
                var view = viewType == null
                    ? null
                    : Resources.FindObjectsOfTypeAll(viewType).OfType<Component>()
                        .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy);
                var viewRoot = view?.transform as RectTransform;
                if (viewRoot != null)
                {
                    var center = viewRoot.TransformPoint(new Vector3(viewRoot.rect.center.x, viewRoot.rect.center.y, 0f));
                    camera.transform.LookAt(center);
                }

                camera.fieldOfView = 52;
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                var pixels = image.GetPixels32();
                for (var index = 0; index < pixels.Length; index++)
                {
                    pixels[index].a = 255;
                }

                image.SetPixels32(pixels);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                foreach (var renderer in avatarRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                    }
                }

                camera.targetTexture = previousTarget;
                camera.fieldOfView = previousFov;
                camera.transform.rotation = previousRotation;
                RenderTexture.active = previousActive;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(image);
            }
        }

        private void CheckEnvironmentCatalog()
        {
            var values = BeatSage.Plugin.App.Environments ?? new string[0];
            var resource = ReadResource("BeatSage.Contracts.schema.json");
            var schema = string.IsNullOrWhiteSpace(resource) ? null : JObject.Parse(resource);
            var known = schema?["x-environments"] is JArray environments
                ? environments.OfType<JObject>().Select(x => (string)x["value"]).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                : new string[0];

            Check("environment-list-nonempty", values.Length > 0, string.Join(",", values));
            Check("environment-default-available", values.Contains("DefaultEnvironment", StringComparer.Ordinal), string.Join(",", values));
            Check("environment-values-match-contract", known.Length == 0 || values.All(x => known.Contains(x, StringComparer.Ordinal)), string.Join(",", values));

            var environmentType = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("EnvironmentInfoSO", false)).FirstOrDefault(x => x != null);
            var liveNames = new List<string>();
            if (environmentType != null)
            {
                foreach (var instance in Resources.FindObjectsOfTypeAll(environmentType))
                {
                    foreach (var member in new[] { "environmentName", "serializedName" })
                    {
                        var name = ReadStringMember(instance, member);
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            liveNames.Add(name);
                        }
                    }
                }
            }

            var expected = (schema?["x-environments"] as JArray)?.OfType<JObject>()
                .Where(x => liveNames.Contains((string)x["gameName"]) || (string)x["value"] == "DefaultEnvironment")
                .Select(x => (string)x["value"]).ToArray() ?? new string[0];
            Check("environmentinfo-assembly-shape", environmentType?.Assembly.GetName().Name == "DataModels", environmentType == null ? "type not loaded" : environmentType.Assembly.FullName);
            Check("environment-live-assets-present", liveNames.Count > 0, "live names=" + liveNames.Distinct(StringComparer.Ordinal).Count());
            Check("environment-live-catalog-complete", expected.SequenceEqual(values, StringComparer.Ordinal), "expected=" + string.Join(",", expected) + "; actual=" + string.Join(",", values));
            WriteJson("environment.json", new { values, known, expected, liveNames = liveNames.Distinct(StringComparer.Ordinal).ToArray(), assembly = environmentType?.Assembly.FullName });
        }

        private IEnumerator CheckWishBrowseAndScan(Action<JObject> completed)
        {
            wishPath = LocateWishAudio();
            Check("wish-audio-found", !string.IsNullOrWhiteSpace(wishPath), wishPath ?? "No .ogg wish audio was found below CustomLevels");
            if (string.IsNullOrWhiteSpace(wishPath))
            {
                completed(null);
                yield break;
            }

            var directoryPath = Path.GetDirectoryName(wishPath);
            var browseToken = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<JArray> browseTask = null;
            try
            {
                browseTask = BeatSage.Plugin.App.BrowseAsync(directoryPath, browseToken.Token);
            }
            catch (Exception ex)
            {
                Check("wish-browse-start", false, ex.ToString());
            }

            while (browseTask != null && !browseTask.IsCompleted)
            {
                yield return null;
            }

            JArray browseRows = null;
            if (browseTask != null)
            {
                try
                {
                    browseRows = browseTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Check("wish-browse", false, ex.GetBaseException().Message);
                }
            }

            var browseHit = browseRows?.OfType<JObject>().FirstOrDefault(row => string.Equals((string)row["path"], wishPath, StringComparison.OrdinalIgnoreCase) && !(bool)row["directory"]);
            Check("wish-browse", browseHit != null, browseRows == null ? "no browse result" : "rows=" + browseRows.Count + "; path=" + wishPath);
            WriteJson("browse.json", browseRows ?? new JArray());
            browseToken.Dispose();

            var scanToken = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task<JArray> scanTask = null;
            try
            {
                scanTask = BeatSage.Plugin.App.ScanAsync(new[] { wishPath }, false, scanToken.Token);
            }
            catch (Exception ex)
            {
                Check("wish-scan-start", false, ex.ToString());
            }

            while (scanTask != null && !scanTask.IsCompleted)
            {
                yield return null;
            }

            JArray scanRows = null;
            if (scanTask != null)
            {
                try
                {
                    scanRows = scanTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Check("wish-scan", false, ex.GetBaseException().Message);
                }
            }

            var song = scanRows?.OfType<JObject>().FirstOrDefault(row => string.Equals((string)row["path"], wishPath, StringComparison.OrdinalIgnoreCase));
            var scanDetail = song == null ? "no row" : "duration=" + (string)song["duration"] + "; error=" + (string)song["error"];
            Check("wish-scan", song != null && string.IsNullOrWhiteSpace((string)song["error"]) && (double)song["duration"] > 0, scanDetail);
            WriteJson("scan.json", scanRows ?? new JArray());
            scanToken.Dispose();
            completed(song);
        }

        private void CheckMonoMultipart(JObject song)
        {
            MultipartDisposingHandler handler = null;
            try
            {
                var snapshot = BeatSage.Plugin.App.Snapshot;
                var settings = snapshot?["settings"] as JObject;
                if (settings == null)
                {
                    throw new InvalidDataException("settings snapshot missing");
                }

                var transferSeconds = (int?)settings["transferSeconds"] ?? 120;
                if (transferSeconds <= 0)
                {
                    transferSeconds = 120;
                }

                var clientSettings = (JObject)settings.DeepClone();
                clientSettings["transferSeconds"] = transferSeconds;
                clientSettings["requestSeconds"] = (int?)clientSettings["requestSeconds"] ?? 30;
                clientSettings["safeRetries"] = (int?)clientSettings["safeRetries"] ?? 0;
                var request = new JObject
                {
                    ["audioFile"] = song["path"],
                    ["audio_metadata_title"] = song["title"] ?? song["name"] ?? "wish",
                    ["audio_metadata_artist"] = song["artist"] ?? string.Empty,
                    ["difficulties"] = ((JObject)snapshot["settings"])["defaultOptions"]["difficulties"]?.DeepClone() ?? new JArray("Normal"),
                    ["modes"] = ((JObject)snapshot["settings"])["defaultOptions"]["modes"]?.DeepClone() ?? new JArray("Standard"),
                    ["events"] = ((JObject)snapshot["settings"])["defaultOptions"]["events"]?.DeepClone() ?? new JArray(),
                    ["environment"] = ((JObject)snapshot["settings"])["defaultOptions"]["environment"] ?? "DefaultEnvironment",
                    ["system_tag"] = ((JObject)snapshot["settings"])["defaultOptions"]["model"] ?? "v2",
                    ["coverFile"] = string.Empty,
                    ["audioFileSha256"] = HashFile((string)song["path"])
                };

                handler = new MultipartDisposingHandler();
                string remoteId;
                using (var client = new BeatSage.BeatSageClient(new BeatSage.ContractRegistry(), () => clientSettings, handler))
                {
                    remoteId = client.CreateAsync(request, CancellationToken.None, null).GetAwaiter().GetResult();
                }

                var passed = remoteId == "local-runtime-check" && handler.ContentConsumed && handler.RequestCount == 1;
                Check("mono-http-multipart-dispose", passed, "id=" + remoteId + "; requests=" + handler.RequestCount + "; consumed=" + handler.ContentConsumed + "; bytes=" + handler.BytesConsumed);
                WriteJson("http-multipart.json", new { remoteId, handler.RequestCount, handler.ContentConsumed, handler.BytesConsumed, handler.LastMethod });
            }
            catch (Exception ex)
            {
                Check("mono-http-multipart-dispose", false, ex.ToString());
            }
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private IEnumerator GenerateAndTrack(JObject song)
        {
            var snapshot = BeatSage.Plugin.App.Snapshot;
            var settings = snapshot?["settings"] as JObject;
            var defaultOptions = settings?["defaultOptions"] as JObject;
            Check("generation-default-options", defaultOptions != null, defaultOptions == null ? "settings.defaultOptions missing" : defaultOptions.ToString(Formatting.None));
            if (defaultOptions == null)
            {
                yield break;
            }

            var path = (string)song["path"];
            var existing = (snapshot?["jobs"] as JArray)?.OfType<JObject>().Where(job => string.Equals((string)job["path"], path, StringComparison.OrdinalIgnoreCase)).ToArray() ?? new JObject[0];
            JObject job = existing.LastOrDefault();
            string jobId = job == null ? null : (string)job["id"];
            var guardPassed = existing.All(value => !string.IsNullOrWhiteSpace((string)value["id"]));
            Check("generation-state-guard", guardPassed, existing.Length == 0 ? "no existing state; enqueue is permitted" : "existing jobs=" + existing.Length + "; id=" + jobId + "; state=" + (string)job["state"]);
            if (!guardPassed)
            {
                yield break;
            }

            var requestedResubmit = Environment.GetEnvironmentVariable("BEATSAGE_SMOKE_RESUBMIT_ID");
            if (!string.IsNullOrWhiteSpace(requestedResubmit) && (job == null || !string.Equals(requestedResubmit, jobId, StringComparison.OrdinalIgnoreCase)))
            {
                Check("generation-resubmit-id", false, "指定的 resubmit ID 与音频已有任务不匹配；不会调用 Retry");
                yield break;
            }

            if (job != null && !string.IsNullOrWhiteSpace(requestedResubmit))
            {
                try
                {
                    BeatSage.Plugin.App.Retry(jobId, true);
                    Check("generation-confirmed-resubmit", true, "已按显式 BEATSAGE_SMOKE_RESUBMIT_ID 确认重新提交：" + jobId);
                    snapshot = BeatSage.Plugin.App.Snapshot;
                    job = (snapshot?["jobs"] as JArray)?.OfType<JObject>().FirstOrDefault(value => string.Equals((string)value["id"], jobId, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    Check("generation-confirmed-resubmit", false, ex.ToString());
                    yield break;
                }
            }
            else if (job != null && string.Equals((string)job["state"], "SubmissionUnknown", StringComparison.OrdinalIgnoreCase))
            {
                Check("generation-confirmation-required", false, "已有 SubmissionUnknown 任务 " + jobId + "，需要用户在游戏内确认重新提交；smoke 不会自动 POST");
                yield break;
            }

            if (job == null)
            {
                Task enqueue = null;
                try
                {
                    var songs = new JArray(new object[] { song.DeepClone() });
                    enqueue = BeatSage.Plugin.App.EnqueueAsync(songs, (JObject)defaultOptions.DeepClone());
                }
                catch (Exception ex)
                {
                    Check("generation-enqueue", false, ex.ToString());
                    yield break;
                }

                yield return WaitTask(enqueue, 90, "enqueue");
                snapshot = BeatSage.Plugin.App.Snapshot;
                job = (snapshot?["jobs"] as JArray)?.OfType<JObject>().LastOrDefault(value => string.Equals((string)value["path"], path, StringComparison.OrdinalIgnoreCase));
                jobId = job == null ? null : (string)job["id"];
                Check("generation-enqueue", job != null && !string.IsNullOrWhiteSpace(jobId), job == null ? "queue job was not persisted" : jobId);
            }

            if (job == null || string.IsNullOrWhiteSpace(jobId))
            {
                yield break;
            }

            phase = "generation-track";
            var deadline = timer.Elapsed.TotalSeconds + GenerationWaitSeconds;
            string previousState = null;
            while (timer.Elapsed.TotalSeconds < deadline)
            {
                snapshot = BeatSage.Plugin.App.Snapshot;
                job = (snapshot?["jobs"] as JArray)?.OfType<JObject>().FirstOrDefault(value => string.Equals((string)value["id"], jobId, StringComparison.OrdinalIgnoreCase));
                if (job == null)
                {
                    Check("generation-job-present", false, "job disappeared: " + jobId);
                    yield break;
                }

                var state = (string)job["state"] ?? string.Empty;
                if (!string.Equals(previousState, state, StringComparison.Ordinal))
                {
                    previousState = state;
                    stateTransitions.Add(new
                    {
                        utc = DateTime.UtcNow.ToString("O"),
                        state,
                        stateLabel = (string)job["stateLabel"],
                        progress = job["progress"],
                        remoteId = (string)job["remoteId"],
                        errorMessage = (string)job["errorMessage"]
                    });
                    WriteProgress(job);
                }

                if (string.Equals(state, "Imported", StringComparison.Ordinal))
                {
                    Check("generation-imported", true, "job=" + jobId + "; levelId=" + (string)job["levelId"]);
                    WriteProgress(job);
                    try
                    {
                        BeatSage.Plugin.App.ShowSong(jobId);
                        Check("show-song-request", true, jobId);
                    }
                    catch (Exception ex)
                    {
                        Check("show-song-request", false, ex.ToString());
                    }
                    yield break;
                }

                if (new[] { "Failed", "FileError", "Cancelled" }.Contains(state, StringComparer.Ordinal))
                {
                    Check("generation-imported", false, state + ": " + (string)job["errorMessage"]);
                    WriteProgress(job);
                    yield break;
                }

                if (timer.Elapsed.TotalSeconds - lastProgressWrite >= 5)
                {
                    WriteProgress(job);
                }

                yield return new WaitForSecondsRealtime(1f);
            }

            Check("generation-imported", false, "20-minute generation deadline exceeded; last state=" + previousState);
        }

        private IEnumerator WaitTask(Task task, double seconds, string name)
        {
            var deadline = timer.Elapsed.TotalSeconds + seconds;
            while (!task.IsCompleted && timer.Elapsed.TotalSeconds < deadline)
            {
                yield return null;
            }

            if (!task.IsCompleted)
            {
                Check(name, false, "task deadline exceeded");
                yield break;
            }

            if (task.IsFaulted)
            {
                Check(name, false, task.Exception?.GetBaseException().Message ?? "task failed");
                yield break;
            }

            if (task.IsCanceled)
            {
                Check(name, false, "task canceled");
                yield break;
            }

            Check(name, true, "completed");
        }

        private void CheckExistingLevel(string levelId)
        {
            try
            {
                var level = Loader.GetLevelById(levelId);
                Check("manual-existing-level", level != null, level == null ? levelId : level.levelID);
                if (level != null)
                {
                    var keys = level.GetBeatmapKeys().ToArray();
                    Check("manual-existing-level-difficulties", keys.Length > 0, keys.Length + " beatmap keys");
                }
            }
            catch (Exception ex)
            {
                Check("manual-existing-level", false, ex.ToString());
            }
        }

        private static string LocateWishAudio()
        {
            var overridePath = Environment.GetEnvironmentVariable("BEATSAGE_SMOKE_WISH_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            var customLevels = Path.Combine(Application.dataPath, "CustomLevels");
            if (!Directory.Exists(customLevels))
            {
                return null;
            }

            var files = Directory.EnumerateFiles(customLevels, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".ogg", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.IndexOf("wish", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(path => path.IndexOf("BeatSage_Aiobahn_somunia_wish", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.Length == 0 ? null : Path.GetFullPath(files[0]);
        }

        private void WriteProgress(JObject job)
        {
            lastProgressWrite = timer.Elapsed.TotalSeconds;
            WriteJson("progress.json", new
            {
                phase,
                elapsed = timer.Elapsed.TotalSeconds,
                job,
                transitions = stateTransitions.ToArray()
            });
        }

        private void WriteJson(string name, object value)
        {
            File.WriteAllText(Path.Combine(directory, name), JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        private static string ReadResource(string name)
        {
            using (var stream = typeof(BeatSage.Plugin).Assembly.GetManifestResourceStream(name))
            using (var reader = stream == null ? null : new StreamReader(stream))
            {
                return reader?.ReadToEnd();
            }
        }

        private sealed class MultipartDisposingHandler : System.Net.Http.HttpMessageHandler
        {
            public int RequestCount { get; private set; }
            public bool ContentConsumed { get; private set; }
            public long BytesConsumed { get; private set; }
            public string LastMethod { get; private set; }

            protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                LastMethod = request.Method.Method;
                if (request.Content != null)
                {
                    BytesConsumed = request.Content.Headers.ContentLength ?? 0;
                    using (var sink = Stream.Null)
                    {
                        await request.Content.CopyToAsync(sink).ConfigureAwait(false);
                    }

                    ContentConsumed = true;
                    request.Content.Dispose();
                }

                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("{\"id\":\"local-runtime-check\"}")
                };
            }
        }

        private static string ReadStringMember(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.PropertyType == typeof(string))
            {
                return property.GetValue(instance, null) as string;
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field != null && field.FieldType == typeof(string) ? field.GetValue(instance) as string : null;
        }

        private void Finish(string failure)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            if (!string.IsNullOrWhiteSpace(failure))
            {
                errors.Add(failure);
            }

            WriteJson("report.json", new
            {
                passed = errors.Count == 0,
                phase,
                generate,
                wishPath,
                elapsed = timer.Elapsed.TotalSeconds,
                xrActive = UnityEngine.XR.XRSettings.isDeviceActive,
                scene = SceneManager.GetActiveScene().name,
                screenshots = Directory.GetFiles(directory, "menu-*.png").Select(Path.GetFileName).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                checks,
                errors,
                diagnostics,
                transitions = stateTransitions.ToArray()
            });
            Application.logMessageReceived -= OnLog;
            Application.Quit(errors.Count == 0 ? 0 : 1);
        }
    }
}
