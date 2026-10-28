using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.ViewControllers;
using HMUI;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BeatSage.UI
{
    [ViewDefinition("BeatSage.UI.BeatSageView.bsml")]
    internal sealed partial class BeatSageViewController : BSMLAutomaticViewController, IDisposable
    {
        static BeatSageViewController activeInstance;
        private const int ScanPageSize = 1200;
        private const string ModelV1 = "v1";
        private const string ModelV2 = "v2";
        private const string ModelV2Flow = "v2-flow";
        private const string ModeStandard = "Standard";
        private const string ModeNoArrows = "NoArrows";
        private const string ModeOneSaber = "OneSaber";
        private const string ModeNinety = "90Degree";
        private const string EventBombs = "Bombs";
        private const string EventDotBlocks = "DotBlocks";
        private const string EventObstacles = "Obstacles";
        private static readonly Dictionary<string, string> UiMemberNames = BuildUiMemberNames();

        [UIComponent("browser-list")]
        private CustomListTableData browserList;

        [UIComponent("scan-list")]
        private CustomListTableData scanList;

        [UIComponent("queue-list")]
        private CustomListTableData queueList;

        [UIComponent("retry-modal")]
        private ModalView retryModal;

        [UIComponent("unsaved-modal")]
        private ModalView unsavedModal;

        [UIComponent("error-text")]
        private TMP_Text errorText;

        [UIComponent("local-panel")]
        private RectTransform localPanel;

        [UIComponent("queue-panel")]
        private RectTransform queuePanel;

        [UIComponent("settings-panel")]
        private RectTransform settingsPanel;

        [UIComponent("song-editor-modal")]
        private ModalView songEditorModal;

        [UIComponent("selected-cover-image")]
        private Image selectedCoverImage;

        private CancellationTokenSource uiCancellation = new CancellationTokenSource();
        private readonly HashSet<string> selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<JObject> browserRows = new List<JObject>();
        private readonly List<JObject> scanRows = new List<JObject>();
        private readonly List<JObject> jobs = new List<JObject>();
        private readonly List<JObject> presets = new List<JObject>();
        private readonly List<object> modelChoices = new List<object>();
        private readonly List<object> environmentChoices = new List<object>();

        private JObject settings = new JObject();
        private JObject options = new JObject();
        private JObject savedDraft;
        private Action pendingLeave;

        [UIValue("unsaved-title")]
        private string UnsavedTitle => LocalizationBridge.T("Save changes before leaving?");
        [UIValue("save-and-leave-label")]
        private string SaveAndLeaveLabel => LocalizationBridge.T("Save");
        [UIValue("discard-and-leave-label")]
        private string DiscardAndLeaveLabel => LocalizationBridge.T("Discard");
        [UIValue("continue-editing-label")]
        private string ContinueEditingLabel => LocalizationBridge.T("Continue editing");
        private JObject selectedSong;
        private string selectedSongPath;
        private string selectedJobId;
        private string retryJobId;
        private string currentPath = string.Empty;
        private string selectedTab = "local";
        private string browserFilter = string.Empty;
        private string formatText = string.Empty;
        private string presetName = string.Empty;
        private string selectedPreset = string.Empty;
        private string noticeMessage = string.Empty;
        private string errorMessage = string.Empty;
        private string statusMessage = string.Empty;
        private string retryTitle = string.Empty;
        private bool coverMode;
        private bool loadingBrowser;
        private bool loadingScan;
        private bool disposed;
        private bool parsed;
        private bool viewActive;
        private bool appSubscribed;
        private bool autoImport;
        private bool autoPlaylist = true;
        private bool retainZip;
        private bool resumeOnStart;
        private bool recursive;
        private int browserGeneration;
        private int scanGeneration;
        private int scanPage;
        private Texture2D selectedCoverTexture;
        private Sprite selectedCoverSprite;

        internal static void OnLocalizationReloaded()
        {
            activeInstance?.RefreshLocalization();
        }

        internal static bool ReportNavigationError(string message)
        {
            if (activeInstance == null || activeInstance.disposed || !activeInstance.viewActive) return false;
            activeInstance.SetError(message);
            return true;
        }

        internal void Prepare(bool openSettings)
        {
            selectedTab = openSettings ? "settings" : "local";
            if (openSettings) settingsSection = "generation";
            if (parsed) { NotifyTabs(); RefreshPresentation(); }
        }

        internal void RequestClose(Action close)
        {
            if (pendingLeave != null) { ContinueEditing(); return; }
            LeaveSettings(close);
        }

        private void LeaveSettings(Action continuation)
        {
            if (IsSettingsTab && HasUnsavedSettings && unsavedModal != null)
            {
                pendingLeave = continuation;
                unsavedModal.Show(true, true, null);
                return;
            }
            continuation();
        }

        [UIAction("save-and-leave")]
        private void SaveAndLeave()
        {
            if (pendingLeave == null || !TrySaveSettings()) return;
            CompleteLeave();
        }

        [UIAction("discard-and-leave")]
        private void DiscardAndLeave()
        {
            if (pendingLeave == null) return;
            RestoreSavedDraft();
            CompleteLeave();
        }

        [UIAction("continue-editing")]
        private void ContinueEditing()
        {
            pendingLeave = null;
            unsavedModal?.Hide(true);
        }

        private void CompleteLeave()
        {
            var continuation = pendingLeave;
            pendingLeave = null;
            unsavedModal?.Hide(true);
            continuation?.Invoke();
        }

        [UIValue("is-local-tab")]
        private bool IsLocalTab => selectedTab == "local";

        [UIValue("is-local-tab-inactive")]
        private bool IsLocalTabInactive => !IsLocalTab;

        [UIValue("is-queue-tab")]
        private bool IsQueueTab => selectedTab == "queue";

        [UIValue("is-queue-tab-inactive")]
        private bool IsQueueTabInactive => !IsQueueTab;

        [UIValue("is-settings-tab")]
        private bool IsSettingsTab => selectedTab == "settings";

        [UIValue("is-settings-tab-inactive")]
        private bool IsSettingsTabInactive => !IsSettingsTab;

        [UIValue("browser-title")]
        private string BrowserTitle
        {
            get
            {
                if (coverMode)
                {
                    return LocalizationBridge.T("Choose Cover");
                }

                if (string.IsNullOrWhiteSpace(currentPath))
                {
                    return LocalizationBridge.T("Local Music");
                }

                var name = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.IsNullOrWhiteSpace(name) ? LocalizationBridge.T("Local Music") : LocalizationBridge.Format("Music · {0}", name);
            }
        }

        [UIValue("current-path")]
        private string CurrentPath => string.IsNullOrWhiteSpace(currentPath) ? LocalizationBridge.T("Root") : currentPath;

        [UIValue("browser-filter")]
        private string BrowserFilter
        {
            get => browserFilter;
            set
            {
                browserFilter = value ?? string.Empty;
                RefreshBrowserList();
            }
        }

        [UIValue("has-error")]
        private bool HasError => !string.IsNullOrWhiteSpace(errorMessage);

        [UIValue("error")]
        private string Error => LocalizationBridge.Message(errorMessage);

        [UIValue("notice")]
        private string Notice => LocalizationBridge.Message(noticeMessage);

        [UIValue("status")]
        private string Status => LocalizationBridge.Message(statusMessage);

        [UIValue("selected-count")]
        private string SelectedCount => selectedPaths.Count == 0 ? LocalizationBridge.T("No music selected") : LocalizationBridge.Format("{0} songs selected", selectedPaths.Count);

        [UIValue("submit-label")]
        private string SubmitLabel => selectedPaths.Count == 0 ? LocalizationBridge.T("Submit Selected Music") : LocalizationBridge.Format("Submit {0} Songs", selectedPaths.Count);

        [UIValue("has-song")]
        private bool HasSong => selectedSong != null;

        [UIValue("cover-mode")]
        private bool CoverMode => coverMode;

        [UIValue("not-cover-mode")]
        private bool NotCoverMode => !coverMode;

        [UIValue("selected-song-title")]
        private string SelectedSongTitle => selectedSong == null
            ? LocalizationBridge.T("Select a Scan Result")
            : Text(selectedSong["title"], Text(selectedSong["name"], LocalizationBridge.T("Untitled Song")));

        [UIValue("selected-song-artist")]
        private string SelectedSongArtist => selectedSong == null
            ? LocalizationBridge.T("No Song Selected")
            : LocalizationBridge.Format("Artist: {0}", Text(selectedSong["artist"], LocalizationBridge.T("Unknown")));

        [UIValue("selected-song-duration")]
        private string SelectedSongDuration => selectedSong == null
            ? LocalizationBridge.T("Duration: —")
            : LocalizationBridge.Format("Duration: {0}", FormatDuration(selectedSong["duration"]));

        [UIValue("selected-song-cover")]
        private string SelectedSongCover
        {
            get
            {
                if (selectedSong == null)
                {
                    return LocalizationBridge.T("Cover: —");
                }

                var path = Text(selectedSong["coverPath"]);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (path.IndexOf(Path.Combine("UserData", "BeatSage", "covers"), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return LocalizationBridge.T("Cover: Set");
                    }

                    return LocalizationBridge.Format("Cover: {0}", Path.GetFileName(path));
                }

                return string.IsNullOrWhiteSpace(Text(selectedSong["coverBase64"])) ? LocalizationBridge.T("Cover: Not Set") : LocalizationBridge.T("Cover: Embedded");
            }
        }

        [UIValue("song-title")]
        private string SongTitle
        {
            get => selectedSong == null ? string.Empty : Text(selectedSong["title"], Text(selectedSong["name"]));
            set => SetSelectedSongField("title", value);
        }

        [UIValue("song-artist")]
        private string SongArtist
        {
            get => selectedSong == null ? string.Empty : Text(selectedSong["artist"]);
            set => SetSelectedSongField("artist", value);
        }

        [UIValue("song-cover-path")]
        private string SongCoverPath
        {
            get => selectedSong == null ? string.Empty : Text(selectedSong["coverPath"]);
            set => SetSelectedSongField("coverPath", value);
        }

        [UIValue("model-choices")]
        private IReadOnlyList<object> ModelChoices => modelChoices;

        [UIAction("model-formatter")]
        private string FormatModel(string value)
        {
            switch (value ?? string.Empty)
            {
                case ModelV1:
                    return LocalizationBridge.T("V1");
                case ModelV2Flow:
                    return LocalizationBridge.T("V2 Flow");
                case ModelV2:
                    return LocalizationBridge.T("V2");
                default:
                    return value ?? string.Empty;
            }
        }

        [UIValue("selected-model-label")]
        private string SelectedModelLabel => LocalizationBridge.Format("Model · {0}", FormatModel(SelectedModel));

        [UIValue("environment-choices")]
        private IReadOnlyList<object> EnvironmentChoices => environmentChoices;

        [UIAction("environment-formatter")]
        private string FormatEnvironment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LocalizationBridge.T("Default");
            }

            if (string.Equals(value, "DefaultEnvironment", StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationBridge.T("Default");
            }

            if (string.Equals(value, "BigMirrorEnvironment", StringComparison.OrdinalIgnoreCase))
            {
                return LocalizationBridge.T("Big Mirror");
            }

            var name = value.EndsWith("Environment", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - "Environment".Length)
                : value;
            if (name.Length == 0)
            {
                return value;
            }

            var builder = new System.Text.StringBuilder(name.Length + 8);
            for (var index = 0; index < name.Length; index++)
            {
                var current = name[index];
                if (index > 0 && char.IsUpper(current) &&
                    (char.IsLower(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                {
                    builder.Append(' ');
                }

                builder.Append(current);
            }

            return LocalizationBridge.T(builder.ToString());
        }

        [UIValue("selected-environment-label")]
        private string SelectedEnvironmentLabel => LocalizationBridge.Format("Environment · {0}", FormatEnvironment(SelectedEnvironment));

        [UIValue("selected-model")]
        private string SelectedModel
        {
            get => Text(options["model"], ModelV2);
            set
            {
                var selected = string.IsNullOrWhiteSpace(value) ? ModelV2 : value;
                options["model"] = selected;
                if (string.Equals(selected, ModelV1, StringComparison.OrdinalIgnoreCase))
                {
                    SetOptionFlag("modes", ModeNinety, false, false);
                    SetOptionFlag("events", EventObstacles, false, false);
                }

                NotifyOptionValues();
            }
        }

        [UIValue("selected-environment")]
        private string SelectedEnvironment
        {
            get => Text(options["environment"]);
            set
            {
                options["environment"] = value ?? string.Empty;
                NotifyPropertyChanged("selected-environment");
                NotifyPropertyChanged("selected-environment-label");
            }
        }

        [UIValue("difficulty-expert-plus")]
        private bool DifficultyExpertPlus
        {
            get => HasOption("difficulties", "ExpertPlus");
            set => SetOptionFlag("difficulties", "ExpertPlus", value);
        }

        [UIValue("difficulty-normal")]
        private bool DifficultyNormal
        {
            get => HasOption("difficulties", "Normal");
            set => SetOptionFlag("difficulties", "Normal", value);
        }

        [UIValue("difficulty-hard")]
        private bool DifficultyHard
        {
            get => HasOption("difficulties", "Hard");
            set => SetOptionFlag("difficulties", "Hard", value);
        }

        [UIValue("difficulty-expert")]
        private bool DifficultyExpert
        {
            get => HasOption("difficulties", "Expert");
            set => SetOptionFlag("difficulties", "Expert", value);
        }

        [UIValue("difficulty-normal-label")]
        private string DifficultyNormalLabel => ToggleLabel("Normal", DifficultyNormal);

        [UIValue("difficulty-hard-label")]
        private string DifficultyHardLabel => ToggleLabel("Hard", DifficultyHard);

        [UIValue("difficulty-expert-label")]
        private string DifficultyExpertLabel => ToggleLabel("Expert", DifficultyExpert);

        [UIValue("difficulty-expert-plus-label")]
        private string DifficultyExpertPlusLabel => ToggleLabel("Expert+", DifficultyExpertPlus);

        [UIAction("toggle-difficulty-normal")]
        private void ToggleDifficultyNormal() => DifficultyNormal = !DifficultyNormal;

        [UIAction("toggle-difficulty-hard")]
        private void ToggleDifficultyHard() => DifficultyHard = !DifficultyHard;

        [UIAction("toggle-difficulty-expert")]
        private void ToggleDifficultyExpert() => DifficultyExpert = !DifficultyExpert;

        [UIAction("toggle-difficulty-expert-plus")]
        private void ToggleDifficultyExpertPlus() => DifficultyExpertPlus = !DifficultyExpertPlus;

        [UIValue("mode-standard")]
        private bool ModeStandardEnabled
        {
            get => HasOption("modes", ModeStandard);
            set => SetOptionFlag("modes", ModeStandard, value);
        }

        [UIValue("mode-no-arrows")]
        private bool ModeNoArrowsEnabled
        {
            get => HasOption("modes", ModeNoArrows);
            set => SetOptionFlag("modes", ModeNoArrows, value);
        }

        [UIValue("mode-one-saber")]
        private bool ModeOneSaberEnabled
        {
            get => HasOption("modes", ModeOneSaber);
            set => SetOptionFlag("modes", ModeOneSaber, value);
        }

        [UIValue("mode-standard-label")]
        private string ModeStandardLabel => ToggleLabel("Standard", ModeStandardEnabled);

        [UIValue("mode-no-arrows-label")]
        private string ModeNoArrowsLabel => ToggleLabel("No Arrows", ModeNoArrowsEnabled);

        [UIValue("mode-one-saber-label")]
        private string ModeOneSaberLabel => ToggleLabel("One Saber", ModeOneSaberEnabled);

        [UIValue("mode-ninety-label")]
        private string ModeNinetyLabel => ToggleLabel("90 Degree", ModeNinetyEnabled);

        [UIAction("toggle-mode-standard")]
        private void ToggleModeStandard() => ModeStandardEnabled = !ModeStandardEnabled;

        [UIAction("toggle-mode-no-arrows")]
        private void ToggleModeNoArrows() => ModeNoArrowsEnabled = !ModeNoArrowsEnabled;

        [UIAction("toggle-mode-one-saber")]
        private void ToggleModeOneSaber() => ModeOneSaberEnabled = !ModeOneSaberEnabled;

        [UIAction("toggle-mode-ninety")]
        private void ToggleModeNinety() => ModeNinetyEnabled = !ModeNinetyEnabled;

        [UIValue("mode-ninety-active")]
        private bool ModeNinetyActive => !string.Equals(SelectedModel, ModelV1, StringComparison.OrdinalIgnoreCase);

        [UIValue("mode-ninety")]
        private bool ModeNinetyEnabled
        {
            get => HasOption("modes", ModeNinety);
            set => SetOptionFlag("modes", ModeNinety, value && ModeNinetyActive);
        }

        [UIValue("event-bombs")]
        private bool EventBombsEnabled
        {
            get => HasOption("events", EventBombs);
            set => SetOptionFlag("events", EventBombs, value);
        }

        [UIValue("event-dot-blocks")]
        private bool EventDotBlocksEnabled
        {
            get => HasOption("events", EventDotBlocks);
            set => SetOptionFlag("events", EventDotBlocks, value);
        }

        [UIValue("event-bombs-label")]
        private string EventBombsLabel => ToggleLabel("Bombs", EventBombsEnabled);

        [UIValue("event-dot-blocks-label")]
        private string EventDotBlocksLabel => ToggleLabel("Dot Blocks", EventDotBlocksEnabled);

        [UIValue("event-obstacles-label")]
        private string EventObstaclesLabel => ToggleLabel("Obstacles", EventObstaclesEnabled);

        [UIAction("toggle-event-bombs")]
        private void ToggleEventBombs() => EventBombsEnabled = !EventBombsEnabled;

        [UIAction("toggle-event-dot-blocks")]
        private void ToggleEventDotBlocks() => EventDotBlocksEnabled = !EventDotBlocksEnabled;

        [UIAction("toggle-event-obstacles")]
        private void ToggleEventObstacles() => EventObstaclesEnabled = !EventObstaclesEnabled;

        [UIValue("event-obstacles-active")]
        private bool EventObstaclesActive => !string.Equals(SelectedModel, ModelV1, StringComparison.OrdinalIgnoreCase);

        [UIValue("event-obstacles")]
        private bool EventObstaclesEnabled
        {
            get => HasOption("events", EventObstacles);
            set => SetOptionFlag("events", EventObstacles, value && EventObstaclesActive);
        }

        [UIValue("setting-auto-import")]
        private bool SettingAutoImport
        {
            get => autoImport;
            set => autoImport = value;
        }

        [UIValue("setting-auto-playlist")]
        private bool SettingAutoPlaylist
        {
            get => autoPlaylist;
            set => autoPlaylist = value;
        }

        [UIValue("setting-retain-zip")]
        private bool SettingRetainZip
        {
            get => retainZip;
            set => retainZip = value;
        }

        [UIValue("setting-resume")]
        private bool SettingResumeOnStart
        {
            get => resumeOnStart;
            set => resumeOnStart = value;
        }

        [UIValue("setting-recursive")]
        private bool SettingRecursive
        {
            get => recursive;
            set => recursive = value;
        }

        [UIValue("setting-auto-import-label")]
        private string SettingAutoImportLabel => ToggleLabel("Auto Import", SettingAutoImport);

        [UIValue("setting-auto-playlist-label")]
        private string SettingAutoPlaylistLabel => ToggleLabel("Beat Sage Playlist", SettingAutoPlaylist);

        [UIValue("setting-retain-zip-label")]
        private string SettingRetainZipLabel => ToggleLabel("Keep Archives", SettingRetainZip);

        [UIValue("setting-resume-label")]
        private string SettingResumeLabel => ToggleLabel("Resume Queue on Start", SettingResumeOnStart);

        [UIValue("setting-recursive-label")]
        private string SettingRecursiveLabel => ToggleLabel("Recursive Scan", SettingRecursive);

        [UIAction("toggle-setting-auto-import")]
        private void ToggleSettingAutoImport() { SettingAutoImport = !SettingAutoImport; NotifySettingsValues(); }

        [UIAction("toggle-setting-auto-playlist")]
        private void ToggleSettingAutoPlaylist() { SettingAutoPlaylist = !SettingAutoPlaylist; NotifySettingsValues(); }

        [UIAction("toggle-setting-retain-zip")]
        private void ToggleSettingRetainZip() { SettingRetainZip = !SettingRetainZip; NotifySettingsValues(); }

        [UIAction("toggle-setting-resume")]
        private void ToggleSettingResume() { SettingResumeOnStart = !SettingResumeOnStart; NotifySettingsValues(); }

        [UIAction("toggle-setting-recursive")]
        private void ToggleSettingRecursive() { SettingRecursive = !SettingRecursive; NotifySettingsValues(); }

        [UIValue("setting-formats")]
        private string SettingFormats
        {
            get => formatText;
            set => formatText = value ?? string.Empty;
        }

        [UIValue("setting-format-mp3")]
        private bool SettingFormatMp3
        {
            get => HasFormat(".mp3");
            set => SetFormat(".mp3", value);
        }

        [UIValue("setting-format-flac")]
        private bool SettingFormatFlac
        {
            get => HasFormat(".flac");
            set => SetFormat(".flac", value);
        }

        [UIValue("setting-format-wav")]
        private bool SettingFormatWav
        {
            get => HasFormat(".wav");
            set => SetFormat(".wav", value);
        }

        [UIValue("setting-format-ogg")]
        private bool SettingFormatOgg
        {
            get => HasFormat(".ogg");
            set => SetFormat(".ogg", value);
        }

        [UIValue("setting-format-aac")]
        private bool SettingFormatAac
        {
            get => HasFormat(".aac");
            set => SetFormat(".aac", value);
        }

        [UIValue("setting-format-aiff")]
        private bool SettingFormatAiff
        {
            get => HasFormat(".aiff") || HasFormat(".aif");
            set => SetAiffFormats(value);
        }

        [UIValue("setting-format-mp3-label")]
        private string SettingFormatMp3Label => ToggleLabel("MP3", SettingFormatMp3);

        [UIValue("setting-format-flac-label")]
        private string SettingFormatFlacLabel => ToggleLabel("FLAC", SettingFormatFlac);

        [UIValue("setting-format-wav-label")]
        private string SettingFormatWavLabel => ToggleLabel("WAV", SettingFormatWav);

        [UIValue("setting-format-ogg-label")]
        private string SettingFormatOggLabel => ToggleLabel("OGG", SettingFormatOgg);

        [UIValue("setting-format-aac-label")]
        private string SettingFormatAacLabel => ToggleLabel("AAC", SettingFormatAac);

        [UIValue("setting-format-aiff-label")]
        private string SettingFormatAiffLabel => ToggleLabel("AIFF", SettingFormatAiff);

        [UIAction("toggle-setting-format-mp3")]
        private void ToggleSettingFormatMp3() => SettingFormatMp3 = !SettingFormatMp3;

        [UIAction("toggle-setting-format-flac")]
        private void ToggleSettingFormatFlac() => SettingFormatFlac = !SettingFormatFlac;

        [UIAction("toggle-setting-format-wav")]
        private void ToggleSettingFormatWav() => SettingFormatWav = !SettingFormatWav;

        [UIAction("toggle-setting-format-ogg")]
        private void ToggleSettingFormatOgg() => SettingFormatOgg = !SettingFormatOgg;

        [UIAction("toggle-setting-format-aac")]
        private void ToggleSettingFormatAac() => SettingFormatAac = !SettingFormatAac;

        [UIAction("toggle-setting-format-aiff")]
        private void ToggleSettingFormatAiff() => SettingFormatAiff = !SettingFormatAiff;

        [UIValue("favorites-summary")]
        private string FavoritesSummary
        {
            get
            {
                var values = Strings(settings["favorites"]);
                return values.Length == 0 ? LocalizationBridge.T("No Favorites") : string.Join("\n", values.Take(8));
            }
        }

        [UIValue("preset-choices")]
        private IReadOnlyList<object> PresetChoices => presets.Select(p => (object)Text(p["name"])).Where(n => !string.IsNullOrWhiteSpace((string)n)).ToArray();

        [UIValue("preset-name")]
        private string PresetName
        {
            get => presetName;
            set => presetName = value ?? string.Empty;
        }

        [UIValue("selected-preset")]
        private string SelectedPreset
        {
            get => selectedPreset;
            set { selectedPreset = value ?? string.Empty; NotifyPropertyChanged("selected-preset-label"); }
        }

        [UIValue("selected-preset-label")]
        private string SelectedPresetLabel => string.IsNullOrEmpty(selectedPreset) ? LocalizationBridge.T("Select Preset") : selectedPreset;

        [UIValue("queue-paused")]
        private bool QueuePaused => SnapshotPaused();

        [UIValue("pause-label")]
        private string PauseLabel => QueuePaused ? LocalizationBridge.T("Resume New Submissions") : LocalizationBridge.T("Pause New Submissions");

        [UIValue("selected-job")]
        private string SelectedJob
        {
            get
            {
                var job = SelectedJobObject();
                if (job == null)
                {
                    return LocalizationBridge.T("No Task Selected");
                }

                var title = Text(job["title"], LocalizationBridge.T("Untitled Song"));
                var state = LocalizationBridge.T(StateLabel(Text(job["state"], "Unknown")));
                return LocalizationBridge.Format("{0} · {1}", title, state);
            }
        }

        [UIValue("scan-page")]
        private string ScanPage => scanRows.Count == 0 ? "0 / 0" : (scanPage + 1) + " / " + ScanPageCount;

        [UIValue("can-scan-prev")]
        private bool CanScanPrevious => scanPage > 0;

        [UIValue("can-scan-next")]
        private bool CanScanNext => scanPage + 1 < ScanPageCount;

        [UIValue("can-cancel-job")]
        private bool CanCancelJob => SelectedJobIs("Queued", "Preflight", "Uploading", "RemotePending", "LoadingAudio", "CreatingLevel", "FormattingLevel", "Downloading", "ReadyToImport", "Importing", "RetryNeeded");

        [UIValue("can-retry-job")]
        private bool CanRetryJob => SelectedJobIs("SubmissionUnknown", "RetryNeeded", "FileError", "Failed", "Cancelled", "WaitingForLibrary");

        [UIValue("can-regenerate-job")]
        private bool CanRegenerateJob => SelectedJobIs("ReadyToImport", "WaitingForLibrary", "Imported", "Cancelled", "FileError", "Failed", "RetryNeeded");

        [UIValue("can-show-song")]
        private bool CanShowSong => SelectedJobIs("Imported");

        [UIValue("can-import-ready")]
        private bool CanImportReady => jobs.Any(job => IsState(job, "ReadyToImport", "WaitingForLibrary"));

        [UIValue("retry-title")]
        private string RetryTitle => retryTitle;

        [UIAction("#post-parse")]
        private void Parsed()
        {
            try
            {
                if (disposed)
                {
                    return;
                }

                parsed = true;
                viewActive = true;
                activeInstance = this;
                RefreshCatalog();
                ApplySnapshot(Plugin.App.Snapshot);
                NormalizeUiTypography();
                RequestTypography();
                SubscribeToApp();
                RefreshBrowserAsync(InitialBrowserPath());
            }
            catch (Exception ex)
            {
                SetError("UI initialization failed; try again later");
                Plugin.Log.Error("Beat Sage UI initialization failed: " + ex);
            }
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (!addedToHierarchy || disposed)
            {
                return;
            }

            viewActive = true;
            if (parsed) activeInstance = this;
            if (!firstActivation && parsed)
            {
                ResumeView();
            }
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            if (removedFromHierarchy)
            {
                viewActive = false;
                UnsubscribeFromApp();
                CancelBrowserAndScan();
            }

            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            viewActive = false;
            if (ReferenceEquals(activeInstance, this)) activeInstance = null;
            UnsubscribeFromApp();
            Cancel(ref uiCancellation, true);
            CancelBrowserAndScan();
            ClearSelectedCover();
        }

        private CancellationTokenSource browserCancellation;
        private CancellationTokenSource scanCancellation;

        private void ResumeView()
        {
            try
            {
                if (Plugin.App == null)
                {
                    return;
                }

                RefreshCatalog();
                ApplySnapshot(Plugin.App.Snapshot);
                NormalizeUiTypography();
                RequestTypography();
                SubscribeToApp();
                RefreshBrowserAsync(InitialBrowserPath());
            }
            catch (Exception ex)
            {
                SetError("UI recovery failed; try again later");
                Plugin.Log.Warn("Beat Sage UI resume failed: " + ex);
            }
        }

        private void SubscribeToApp()
        {
            var app = Plugin.App;
            if (app == null || appSubscribed)
            {
                return;
            }

            app.Changed += OnAppChanged;
            appSubscribed = true;
        }

        internal void RefreshLocalization()
        {
            if (disposed || !parsed || !viewActive) return;
            RefreshBrowserList();
            RefreshScanList();
            RefreshQueueList();
            foreach (var alias in UiMemberNames.Keys.ToArray()) NotifyUi(alias);
            if (errorText != null) errorText.text = Feedback;
            RequestTypography();
        }

        private void UnsubscribeFromApp()
        {
            if (!appSubscribed)
            {
                return;
            }

            try
            {
                var app = Plugin.App;
                if (app != null)
                {
                    app.Changed -= OnAppChanged;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage UI event cleanup failed: " + ex);
            }
            finally
            {
                appSubscribed = false;
            }
        }

        private void CancelBrowserAndScan()
        {
            browserGeneration++;
            scanGeneration++;
            Cancel(ref browserCancellation, true);
            Cancel(ref scanCancellation, true);
            loadingBrowser = false;
            loadingScan = false;
        }

        private string InitialBrowserPath()
        {
            var lastDirectory = Text(settings["lastDirectory"]);
            if (!string.IsNullOrWhiteSpace(lastDirectory) && Directory.Exists(lastDirectory))
            {
                return lastDirectory;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) ?? string.Empty;
        }

        private void PersistLastDirectory(string path)
        {
            try
            {
                var value = (JObject)settings.DeepClone();
                value["lastDirectory"] = path;
                Plugin.App.SaveSettings(value);
                settings = value;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage last directory save failed: " + ex);
            }
        }

        [UIAction("tab-local")]
        private void ShowLocalTab()
        {
            if (selectedTab == "local") return;
            LeaveSettings(() => { selectedTab = "local"; NotifyTabs(); });
        }

        [UIAction("tab-queue")]
        private void ShowQueueTab()
        {
            if (selectedTab == "queue") return;
            LeaveSettings(() =>
            {
                selectedTab = "queue";
                if (SelectedJobObject() == null && jobs.Count > 0)
                {
                    selectedJobId = Text(jobs.Last()["id"]);
                    RefreshQueueList();
                }
                NotifyTabs();
            });
        }

        [UIAction("tab-settings")]
        private void ShowSettingsTab()
        {
            selectedTab = "settings";
            NotifyTabs();
        }

        [UIAction("browse-root")]
        private void BrowseRoot() => RefreshBrowserAsync(string.Empty);

        [UIAction("browse-parent")]
        private void BrowseParent()
        {
            if (coverMode && string.IsNullOrWhiteSpace(currentPath))
            {
                ExitCoverMode();
                return;
            }

            var parent = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : Directory.GetParent(currentPath)?.FullName;
            RefreshBrowserAsync(parent ?? string.Empty);
        }

        [UIAction("browse-last")]
        private void BrowseLastDirectory() => RefreshBrowserAsync(Text(settings["lastDirectory"]));

        [UIAction("scan-current")]
        private void ScanCurrentDirectory()
        {
            if (coverMode)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(currentPath))
            {
                SetError("Open a music folder first");
                return;
            }

            ScanAsync(new[] { currentPath }, recursive);
        }

        [UIAction("scan-selected")]
        private void ScanSelectedFiles()
        {
            if (coverMode)
            {
                return;
            }

            var paths = selectedPaths.ToArray();
            if (paths.Length == 0)
            {
                SetError("Select music files first");
                return;
            }

            ScanAsync(paths, false);
        }

        [UIAction("browser-select")]
        private void BrowserSelected(TableView table, int index)
        {
            var rows = FilteredBrowserRows().ToArray();
            if (index < 0 || index >= rows.Length)
            {
                return;
            }

            var row = rows[index];
            var path = Text(row["path"]);
            var rowError = Text(row["error"]);
            if (!string.IsNullOrWhiteSpace(rowError))
            {
                SetError(rowError);
                return;
            }

            if (Bool(row["directory"]))
            {
                RefreshBrowserAsync(path);
                return;
            }

            if (coverMode)
            {
                if (!IsImage(path))
                {
                    SetError("Choose a JPG or PNG image");
                    return;
                }

                SetSelectedSongField("coverPath", path);
                ExitCoverMode();
                return;
            }

            if (!IsAudio(path))
            {
                SetError("This file is not a supported audio format");
                return;
            }

            if (!selectedPaths.Add(path))
            {
                selectedPaths.Remove(path);
            }

            ClearError();
            SetStatus("{0} songs selected", selectedPaths.Count);
            NotifyPropertyChanged("selected-count");
            NotifyPropertyChanged("submit-label");
            
            RefreshBrowserList();
        }

        [UIAction("scan-select")]
        private void ScanSelected(TableView table, int index)
        {
            var actualIndex = scanPage * ScanPageSize + index;
            if (index < 0 || actualIndex < 0 || actualIndex >= scanRows.Count)
            {
                return;
            }

            var row = scanRows[actualIndex];
            var path = Text(row["path"]);
            if (!string.IsNullOrWhiteSpace(Text(row["error"])))
            {
                SetError(Text(row["error"]));
                return;
            }

            selectedSongPath = path;
            selectedSong = row;
            if (!selectedPaths.Add(path))
            {
                selectedPaths.Remove(path);
            }
            ClearError();
            SetStatus("{0} songs selected", selectedPaths.Count);
            NotifySongValues();
            NotifyPropertyChanged("selected-count");
            NotifyPropertyChanged("submit-label");
            
            RefreshScanList();
        }

        [UIAction("select-all-scan")]
        private void SelectAllScan()
        {
            foreach (var row in scanRows)
            {
                var path = Text(row["path"]);
                if (string.IsNullOrWhiteSpace(Text(row["error"])))
                {
                    selectedPaths.Add(path);
                }
            }

            SetStatus("{0} songs selected", selectedPaths.Count);
            NotifyPropertyChanged("selected-count");
            NotifyPropertyChanged("submit-label");
            
            RefreshScanList();
        }

        [UIAction("scan-page-prev")]
        private void ScanPagePrevious()
        {
            if (scanPage == 0)
            {
                return;
            }

            scanPage--;
            RefreshScanList();
        }

        [UIAction("scan-page-next")]
        private void ScanPageNext()
        {
            if (scanPage + 1 >= ScanPageCount)
            {
                return;
            }

            scanPage++;
            RefreshScanList();
        }

        [UIAction("clear-scan")]
        private void ClearScan()
        {
            selectedPaths.Clear();
            selectedSong = null;
            selectedSongPath = null;
            scanPage = 0;
            NotifySongValues();
            NotifyPropertyChanged("selected-count");
            NotifyPropertyChanged("submit-label");
            RefreshScanList();
            RefreshBrowserList();
        }

        [UIAction("open-song-editor")]
        private void OpenSongEditor()
        {
            if (selectedSong == null)
            {
                SetError("Select music in the scan preview");
                return;
            }

            ClearError();
            songEditorModal?.Show(true);
        }

        [UIAction("show-song-editor")]
        private void ShowSongEditor() => songEditorModal?.Show(true);

        [UIAction("close-song-editor")]
        private void CloseSongEditor() => songEditorModal?.Hide(false);

        [UIAction("choose-cover")]
        private void ChooseCover()
        {
            if (selectedSong == null)
            {
                SetError("Select a song first");
                return;
            }

            coverMode = true;
            songEditorModal?.Hide(false);
            ClearError();
            NotifyPropertyChanged("browser-title");
            NotifyPropertyChanged("cover-mode");
            NotifyPropertyChanged("not-cover-mode");
            RefreshBrowserAsync(string.IsNullOrWhiteSpace(currentPath) ? string.Empty : currentPath);
        }

        [UIAction("cancel-cover")]
        private void ExitCoverMode()
        {
            coverMode = false;
            NotifyPropertyChanged("browser-title");
            NotifyPropertyChanged("cover-mode");
            NotifyPropertyChanged("not-cover-mode");
            RefreshBrowserList();
            if (selectedSong != null)
            {
                songEditorModal?.Show(true);
            }
        }

        [UIAction("enqueue-selected")]
        private void EnqueueSelected()
        {
            var rows = scanRows.Where(row => selectedPaths.Contains(Text(row["path"])) && string.IsNullOrWhiteSpace(Text(row["error"]))).ToArray();
            if (rows.Length == 0)
            {
                SetError("Select music in the scan preview");
                return;
            }

            var songs = new JArray(rows.Select(row => row.DeepClone()).ToArray());
            var submitOptions = BuildOptions();
            ClearError();
            SetStatus("Submitting {0} songs", rows.Length);
            try
            {
                Observe(Plugin.App.EnqueueAsync(songs, submitOptions), "Submit task");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Beat Sage enqueue start failed: " + ex);
                SetError("Task submission failed: {0}", ExceptionMessage(ex));
            }
        }

        [UIAction("toggle-pause")]
        private void TogglePause()
        {
            try
            {
                if (SnapshotPaused())
                {
                    Plugin.App.Resume();
                }
                else
                {
                    Plugin.App.Pause();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage queue action failed: " + ex);
                SetError("Queue operation failed: {0}", ExceptionMessage(ex));
            }
        }

        [UIAction("queue-select")]
        private void QueueSelected(TableView table, int index)
        {
            if (index < 0 || index >= jobs.Count)
            {
                return;
            }

            selectedJobId = Text(jobs[index]["id"]);
            NotifyPropertyChanged("selected-job");
            NotifyPropertyChanged("can-cancel-job");
            NotifyPropertyChanged("can-retry-job");
            NotifyPropertyChanged("can-regenerate-job");
            NotifyPropertyChanged("can-show-song");
        }

        [UIAction("cancel-job")]
        private void CancelJob()
        {
            if (string.IsNullOrWhiteSpace(selectedJobId))
            {
                SetError("Select a task first");
                return;
            }

            if (!CanCancelJob)
            {
                SetError("The current state cannot be cancelled");
                return;
            }

            TryJobAction("Cancel task", () => Plugin.App.Cancel(selectedJobId));
        }

        [UIAction("retry-job")]
        private void RetryJob()
        {
            var job = SelectedJobObject();
            if (job == null)
            {
                SetError("Select a task first");
                return;
            }

            if (!CanRetryJob)
            {
                SetError("The current state cannot be retried");
                return;
            }

            if (IsUnknownUploadResult(job))
            {
                retryJobId = Text(job["id"]);
                retryTitle = "Confirm Resubmission";
                NotifyPropertyChanged("retry-title");
                retryModal?.Show(true);
                return;
            }

            TryJobAction("Retry task", () => Plugin.App.Retry(Text(job["id"]), false));
        }

        [UIAction("confirm-retry")]
        private void ConfirmRetry()
        {
            var id = retryJobId;
            retryJobId = null;
            retryModal?.Hide(false);
            if (!string.IsNullOrWhiteSpace(id))
            {
                TryJobAction("Confirm resubmission", () => Plugin.App.Retry(id, true));
            }
        }

        [UIAction("close-retry")]
        private void CloseRetry() => retryModal?.Hide(false);

        [UIAction("show-retry")]
        private void ShowRetry() => retryModal?.Show(true);

        [UIAction("regenerate-job")]
        private void RegenerateJob()
        {
            if (string.IsNullOrWhiteSpace(selectedJobId))
            {
                SetError("Select a task first");
                return;
            }

            if (!CanRegenerateJob)
            {
                SetError("The current state cannot be regenerated");
                return;
            }

            TryJobAction("Regenerate", () => Plugin.App.Regenerate(selectedJobId));
        }

        [UIAction("import-ready")]
        private void ImportReady() => TryJobAction("Import ready maps", () => Plugin.App.ImportReady());

        [UIAction("show-song")]
        private void ShowSong()
        {
            if (Menu.Closing)
            {
                SetError("Beat Sage is closing; try again");
                return;
            }
            if (string.IsNullOrWhiteSpace(selectedJobId))
            {
                SetError("Select a task first");
                return;
            }

            if (!CanShowSong)
            {
                SetError("The song is not loaded in the library");
                return;
            }

            TryJobAction("Open song", () => Plugin.App.ShowSong(selectedJobId));
        }

        [UIAction("save-settings")]
        private void SaveSettings() { TrySaveSettings(); }

        private bool TrySaveSettings()
        {
            try
            {
                var value = BuildSettingsValue();
                value["defaultOptions"] = BuildOptions();
                Plugin.App.SaveSettings(value);
                settings = value;
                savedDraft = CaptureDraft();
                ClearError();
                SetStatus("Settings saved");
                NotifyPropertyChanged("favorites-summary");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage settings save failed: " + ex);
                SetError("Settings could not be saved; try again later");
                return false;
            }
        }

        [UIAction("add-favorite")]
        private void AddFavorite() => UpdateFavorite(true);

        [UIAction("remove-favorite")]
        private void RemoveFavorite() => UpdateFavorite(false);

        private void UpdateFavorite(bool add)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                SetError("Open the folder to favorite first");
                return;
            }

            try
            {
                var value = (JObject)settings.DeepClone();
                var favorites = value["favorites"] as JArray ?? new JArray();
                if (value["favorites"] == null)
                {
                    value["favorites"] = favorites;
                }

                var existing = favorites.FirstOrDefault(x => string.Equals(Text(x), currentPath, StringComparison.OrdinalIgnoreCase));
                if (add && existing == null)
                {
                    favorites.Add(currentPath);
                }
                else if (!add && existing != null)
                {
                    existing.Remove();
                }

                Plugin.App.SaveSettings(value);
                settings = value;
                ClearError();
                SetStatus(add ? "Current folder added to favorites" : "Current folder removed from favorites");
                NotifyPropertyChanged("favorites-summary");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage favorites update failed: " + ex);
                SetError("Favorites could not be updated; try again later");
            }
        }

        private JObject BuildSettingsValue()
        {
            var value = (JObject)settings.DeepClone();
            value["autoImport"] = autoImport;
            value["autoPlaylist"] = autoPlaylist;
            value["retainZip"] = retainZip;
            value["resumeOnStart"] = resumeOnStart;
            value["recursive"] = recursive;
            value["lastDirectory"] = currentPath ?? string.Empty;
            value["enabledFormats"] = new JArray(ParseFormats(formatText));
            return value;
        }

        private bool HasUnsavedSettings => savedDraft != null && !JToken.DeepEquals(savedDraft, CaptureDraft());

        private JObject CaptureDraft()
        {
            var optionValue = (JObject)options.DeepClone();
            foreach (var key in new[] { "difficulties", "modes", "events" })
                optionValue[key] = new JArray(Strings(optionValue[key]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return new JObject
            {
                ["autoImport"] = autoImport, ["autoPlaylist"] = autoPlaylist,
                ["retainZip"] = retainZip, ["resumeOnStart"] = resumeOnStart,
                ["recursive"] = recursive,
                ["enabledFormats"] = new JArray(ParseFormats(formatText).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                ["defaultOptions"] = optionValue
            };
        }

        private void RestoreSavedDraft()
        {
            if (savedDraft == null) return;
            autoImport = Bool(savedDraft["autoImport"]);
            autoPlaylist = Bool(savedDraft["autoPlaylist"]);
            retainZip = Bool(savedDraft["retainZip"]);
            resumeOnStart = Bool(savedDraft["resumeOnStart"]);
            recursive = Bool(savedDraft["recursive"]);
            formatText = string.Join(", ", Strings(savedDraft["enabledFormats"]));
            options = (JObject)savedDraft["defaultOptions"].DeepClone();
            NotifySettingsValues();
            NotifyOptionValues();
        }

        [UIAction("save-preset")]
        private void SavePreset()
        {
            var name = (presetName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                SetError("Enter a preset name");
                return;
            }

            try
            {
                Plugin.App.SavePreset(name, BuildOptions());
                SelectedPreset = name;
                ClearError();
                SetStatus("Preset saved: {0}", name);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage preset save failed: " + ex);
                SetError("Preset could not be saved; try again later");
            }
        }

        [UIAction("load-preset")]
        private void LoadPreset()
        {
            var preset = presets.FirstOrDefault(p => string.Equals(Text(p["name"]), selectedPreset, StringComparison.OrdinalIgnoreCase));
            if (preset == null || !(preset["options"] is JObject presetOptions))
            {
                SetError("Select a preset");
                return;
            }

            options = (JObject)presetOptions.DeepClone();
            EnsureOptionsDefaults();
            NotifyOptionValues();
            ClearError();
            SetStatus("Preset loaded: {0}", selectedPreset);
        }

        [UIAction("delete-preset")]
        private void DeletePreset()
        {
            var name = (selectedPreset ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                SetError("Select a preset");
                return;
            }

            try
            {
                Plugin.App.DeletePreset(name);
                SelectedPreset = string.Empty;
                SetStatus("Preset deleted: {0}", name);
                NotifyPropertyChanged("selected-preset");
                NotifyPropertyChanged("preset-choices");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage preset delete failed: " + ex);
                SetError("Preset could not be deleted; try again later");
            }
        }

        private void OnAppChanged()
        {
            if (disposed || !viewActive || !appSubscribed)
            {
                return;
            }

            try
            {
                RefreshCatalog();
                ApplySnapshot(Plugin.App.Snapshot);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage snapshot refresh failed: " + ex);
                SetError("Queue state could not be read; try again later");
            }
        }

        private void ApplySnapshot(JObject snapshot)
        {
            if (snapshot == null)
            {
                throw new InvalidOperationException("The service snapshot is empty");
            }

            var preserveDraft = HasUnsavedSettings;
            var nextSettings = snapshot["settings"] as JObject;
            settings = nextSettings == null ? new JObject() : (JObject)nextSettings.DeepClone();
            if (!preserveDraft)
            {
                autoImport = Bool(settings["autoImport"]);
                var autoPlaylistValue = settings["autoPlaylist"];
                autoPlaylist = autoPlaylistValue == null || autoPlaylistValue.Type == JTokenType.Null || Bool(autoPlaylistValue);
                retainZip = Bool(settings["retainZip"]);
                resumeOnStart = Bool(settings["resumeOnStart"]);
                recursive = Bool(settings["recursive"]);
                formatText = string.Join(", ", Strings(settings["enabledFormats"]));
            }
            noticeMessage = Text(snapshot["notice"]);

            var defaultOptions = settings["defaultOptions"] as JObject;
            if (defaultOptions != null && !preserveDraft)
            {
                options = (JObject)defaultOptions.DeepClone();
            }

            EnsureOptionsDefaults();
            if (!preserveDraft) savedDraft = CaptureDraft();
            jobs.Clear();
            if (snapshot["jobs"] is JArray jobArray)
            {
                jobs.AddRange(jobArray.OfType<JObject>().Select(x => (JObject)x.DeepClone()));
            }

            presets.Clear();
            if (snapshot["presets"] is JArray presetArray)
            {
                presets.AddRange(presetArray.OfType<JObject>().Select(x => (JObject)x.DeepClone()));
            }

            RefreshQueueList();
            NotifySettingsValues();
            NotifyOptionValues();
            NotifyPropertyChanged("preset-choices");
            NotifyPropertyChanged("queue-paused");
            NotifyPropertyChanged("pause-label");
            NotifyPropertyChanged("notice");
        }

        private void RefreshCatalog()
        {
            modelChoices.Clear();
            environmentChoices.Clear();
            try
            {
                var catalog = Plugin.App.Catalog;
                var catalogModels = CatalogValues(catalog?["models"]);
                foreach (var value in (catalogModels.Length == 0 ? new[] { ModelV2, ModelV2Flow, ModelV1 } : catalogModels))
                {
                    modelChoices.Add(value);
                }

                var environments = Plugin.App.Environments ?? new string[0];
                foreach (var value in environments.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    environmentChoices.Add(value);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage catalog read failed: " + ex);
            }

            if (modelChoices.Count == 0)
            {
                modelChoices.Add(ModelV2);
            }

            if (environmentChoices.Count == 0)
            {
                environmentChoices.Add("DefaultEnvironment");
            }

            NotifyPropertyChanged("model-choices");
            NotifyPropertyChanged("environment-choices");
        }

        private void EnsureOptionsDefaults()
        {
            if (options == null)
            {
                options = new JObject();
            }

            if (options["model"] == null)
            {
                options["model"] = ModelV2;
            }

            if (options["environment"] == null || string.IsNullOrWhiteSpace(Text(options["environment"])))
            {
                options["environment"] = environmentChoices.FirstOrDefault() as string ?? "DefaultEnvironment";
            }

            if (!(options["difficulties"] is JArray) || !((JArray)options["difficulties"]).Any())
            {
                options["difficulties"] = new JArray("Normal", "Hard", "Expert", "ExpertPlus");
            }

            if (!(options["modes"] is JArray) || !((JArray)options["modes"]).Any())
            {
                options["modes"] = new JArray(ModeStandard);
            }

            if (!(options["events"] is JArray))
            {
                options["events"] = new JArray(EventBombs, EventDotBlocks, EventObstacles);
            }

            if (string.Equals(SelectedModel, ModelV1, StringComparison.OrdinalIgnoreCase))
            {
                SetOptionFlag("modes", ModeNinety, false, false);
                SetOptionFlag("events", EventObstacles, false, false);
            }
        }

        private JObject BuildOptions()
        {
            EnsureOptionsDefaults();
            var value = (JObject)options.DeepClone();
            value["difficulties"] = new JArray(VisibleFlags(new[]
            {
                new KeyValuePair<string, bool>("Normal", DifficultyNormal),
                new KeyValuePair<string, bool>("Hard", DifficultyHard),
                new KeyValuePair<string, bool>("Expert", DifficultyExpert),
                new KeyValuePair<string, bool>("ExpertPlus", DifficultyExpertPlus)
            }, "Normal").ToArray());
            value["modes"] = new JArray(VisibleFlags(new[]
            {
                new KeyValuePair<string, bool>(ModeStandard, ModeStandardEnabled),
                new KeyValuePair<string, bool>(ModeNoArrows, ModeNoArrowsEnabled),
                new KeyValuePair<string, bool>(ModeOneSaber, ModeOneSaberEnabled),
                new KeyValuePair<string, bool>(ModeNinety, ModeNinetyEnabled && ModeNinetyActive)
            }, ModeStandard).ToArray());
            value["events"] = new JArray(VisibleFlags(new[]
            {
                new KeyValuePair<string, bool>(EventBombs, EventBombsEnabled),
                new KeyValuePair<string, bool>(EventDotBlocks, EventDotBlocksEnabled),
                new KeyValuePair<string, bool>(EventObstacles, EventObstaclesEnabled && EventObstaclesActive)
            }, null).ToArray());
            value["model"] = SelectedModel;
            value["environment"] = SelectedEnvironment;
            options = (JObject)value.DeepClone();
            NotifyOptionValues();
            return value;
        }

        private IEnumerable<string> VisibleFlags(IEnumerable<KeyValuePair<string, bool>> values, string fallback)
        {
            var selected = values.Where(x => x.Value).Select(x => x.Key).ToArray();
            return selected.Length == 0
                ? (fallback == null ? Enumerable.Empty<string>() : new[] { fallback })
                : selected;
        }

        private bool HasOption(string property, string value)
        {
            return options[property] is JArray array && array.Any(x => string.Equals(Text(x), value, StringComparison.OrdinalIgnoreCase));
        }

        private void SetOptionFlag(string property, string value, bool enabled, bool enforceSelection = true)
        {
            var array = options[property] as JArray;
            if (array == null)
            {
                array = new JArray();
                options[property] = array;
            }

            var existing = array.FirstOrDefault(x => string.Equals(Text(x), value, StringComparison.OrdinalIgnoreCase));
            if (!enabled && enforceSelection && existing != null && array.Count <= 1 &&
                (string.Equals(property, "difficulties", StringComparison.OrdinalIgnoreCase) || string.Equals(property, "modes", StringComparison.OrdinalIgnoreCase)))
            {
                SetError(string.Equals(property, "difficulties", StringComparison.OrdinalIgnoreCase) ? "Keep at least one difficulty" : "Keep at least one mode");
                SetStatus("Select another item first");
                NotifyOptionValues();
                return;
            }

            if (enabled && existing == null)
            {
                array.Add(value);
            }
            else if (!enabled && existing != null)
            {
                existing.Remove();
            }

            NotifyOptionValues();
        }

        private void NotifyOptionValues()
        {
            foreach (var name in new[]
            {
                "selected-model", "selected-model-label", "selected-environment", "selected-environment-label", "difficulty-expert-plus", "difficulty-normal", "difficulty-hard", "difficulty-expert",
                "difficulty-normal-label", "difficulty-hard-label", "difficulty-expert-label", "difficulty-expert-plus-label",
                "mode-standard", "mode-no-arrows", "mode-one-saber", "mode-ninety", "mode-ninety-active",
                "mode-standard-label", "mode-no-arrows-label", "mode-one-saber-label", "mode-ninety-label",
                "event-bombs", "event-dot-blocks", "event-obstacles", "event-obstacles-active",
                "event-bombs-label", "event-dot-blocks-label", "event-obstacles-label"
            })
            {
                NotifyPropertyChanged(name);
            }
        }

        private void NotifySettingsValues()
        {
            foreach (var name in new[]
            {
                "setting-auto-import", "setting-auto-playlist", "setting-retain-zip", "setting-resume", "setting-recursive", "setting-formats", "favorites-summary",
                "setting-auto-import-label", "setting-auto-playlist-label", "setting-retain-zip-label", "setting-resume-label", "setting-recursive-label"
            })
            {
                NotifyPropertyChanged(name);
            }

            NotifySettingsFormatValues();
        }

        private void NotifySettingsFormatValues()
        {
            foreach (var name in new[]
            {
                "setting-format-mp3", "setting-format-flac", "setting-format-wav", "setting-format-ogg", "setting-format-aac", "setting-format-aiff",
                "setting-format-mp3-label", "setting-format-flac-label", "setting-format-wav-label", "setting-format-ogg-label", "setting-format-aac-label", "setting-format-aiff-label"
            })
            {
                NotifyPropertyChanged(name);
            }
        }

        private void NotifyTabs()
        {
            foreach (var name in new[]
            {
                "is-local-tab", "is-local-tab-inactive",
                "is-queue-tab", "is-queue-tab-inactive",
                "is-settings-tab", "is-settings-tab-inactive"
            })
            {
                NotifyPropertyChanged(name);
            }
        }

        private void NotifySongValues()
        {
            foreach (var name in new[]
            {
                "has-song", "cover-mode", "not-cover-mode", "song-title", "song-artist", "song-cover-path",
                "selected-song-title", "selected-song-artist", "selected-song-duration", "selected-song-cover"
            })
            {
                NotifyPropertyChanged(name);
            }

            RefreshSelectedCover();
        }

        private void NormalizeUiTypography()
        {
            if (transform == null)
            {
                return;
            }

            foreach (var text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (text == null)
                {
                    continue;
                }

                text.fontStyle = FontStyles.Normal;
                text.enableAutoSizing = false;
                if (text.GetComponentInParent<BeatSaberMarkupLanguage.Components.Settings.StringSetting>() != null)
                {
                    text.enableWordWrapping = false;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                }
                text.fontSize = Mathf.Max(text.fontSize, 3.2f);
            }
            foreach (var cell in GetComponentsInChildren<TableCell>(true))
            {
                var labels = cell.GetComponentsInChildren<TMP_Text>(true);
                for (var index = 0; index < labels.Length; index++)
                {
                    labels[index].fontStyle = FontStyles.Normal;
                    labels[index].enableAutoSizing = false;
                    labels[index].fontSize = index == 0 ? 3.2f : 2.8f;
                    labels[index].enableWordWrapping = false;
                    labels[index].overflowMode = TextOverflowModes.Ellipsis;
                }
            }
        }

        private void SetSelectedSongField(string name, string value)
        {
            if (selectedSong == null)
            {
                return;
            }

            selectedSong[name] = value ?? string.Empty;
            NotifySongValues();
            RefreshScanList();
        }

        private void RefreshSelectedCover()
        {
            if (selectedCoverImage == null)
            {
                return;
            }

            var coverKey = selectedSong == null ? string.Empty : Text(selectedSong["coverPath"]) + Text(selectedSong["coverBase64"]);
            if (coverKey == loadedCoverKey && selectedCoverSprite != null) return;
            ClearSelectedCover();
            loadedCoverKey = coverKey;
            if (selectedSong == null)
            {
                return;
            }

            byte[] bytes = null;
            var path = Text(selectedSong["coverPath"]);
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && IsImage(path) && File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 8L * 1024L * 1024L)
                    {
                        bytes = File.ReadAllBytes(path);
                    }
                }

                if (bytes == null)
                {
                    var encoded = Text(selectedSong["coverBase64"]);
                    if (!string.IsNullOrWhiteSpace(encoded))
                    {
                        bytes = Convert.FromBase64String(encoded);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage selected cover preview failed: " + ex.Message);
            }

            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024)
            {
                return;
            }

            try
            {
                selectedCoverTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                // Select Unity's byte[] overload: its Span overloads refer to
                // the game's mscorlib, while this plugin targets net472 refs.
                var load = typeof(ImageConversion).GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                if (load == null || !(bool)load.Invoke(null, new object[] { selectedCoverTexture, bytes, true }))
                {
                    ClearSelectedCover();
                    return;
                }

                selectedCoverSprite = Sprite.Create(
                    selectedCoverTexture,
                    new Rect(0, 0, selectedCoverTexture.width, selectedCoverTexture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
                selectedCoverImage.sprite = selectedCoverSprite;
                selectedCoverImage.preserveAspect = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage selected cover texture failed: " + ex.Message);
                ClearSelectedCover();
            }
        }

        private string loadedCoverKey;

        private void ClearSelectedCover()
        {
            if (selectedCoverImage != null)
            {
                selectedCoverImage.sprite = null;
            }

            if (selectedCoverSprite != null)
            {
                UnityEngine.Object.Destroy(selectedCoverSprite);
                selectedCoverSprite = null;
            }

            if (selectedCoverTexture != null)
            {
                UnityEngine.Object.Destroy(selectedCoverTexture);
                selectedCoverTexture = null;
            }
        }

        private void RefreshBrowserAsync(string path)
        {
            Observe(LoadBrowserAsync(path), "Browse files");
        }

        private async Task LoadBrowserAsync(string path)
        {
            Cancel(ref browserCancellation, true);
            browserCancellation = CancellationTokenSource.CreateLinkedTokenSource(uiCancellation.Token);
            browserCancellation.CancelAfter(TimeSpan.FromSeconds(30));
            var token = browserCancellation.Token;
            var generation = ++browserGeneration;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            loadingBrowser = true;
            SetStatus(coverMode ? "Opening image folder" : "Opening folder");

            try
            {
                var result = await Plugin.App.BrowseAsync(path ?? string.Empty, token);
                token.ThrowIfCancellationRequested();
                if (disposed || !viewActive || generation != browserGeneration)
                {
                    return;
                }

                browserRows.Clear();
                browserRows.AddRange(result?.OfType<JObject>().Select(x => (JObject)x.DeepClone()) ?? Enumerable.Empty<JObject>());
                if (coverMode)
                {
                    AddCoverRows(browserRows, path);
                }
                currentPath = path ?? string.Empty;
                if (!coverMode && !string.IsNullOrWhiteSpace(currentPath))
                {
                    PersistLastDirectory(currentPath);
                }
                ClearError();
                SetStatus("{0} items", browserRows.Count);
                RefreshBrowserList();
                NotifyPropertyChanged("current-path");
            }
            catch (OperationCanceledException)
            {
                if (!disposed && viewActive && generation == browserGeneration && DateTime.UtcNow >= deadline)
                {
                    Plugin.Log.Warn("Beat Sage browse timed out for path: " + path);
                    SetError("Browse timed out; narrow the folder and try again");
                    SetStatus(string.Empty);
                }
            }
            catch (Exception ex)
            {
                if (!disposed && viewActive && generation == browserGeneration)
                {
                    Plugin.Log.Warn("Beat Sage browse failed: " + ex);
                    SetError("Browse failed; try again");
                }
            }
            finally
            {
                if (generation == browserGeneration)
                {
                    loadingBrowser = false;
                    RefreshPresentation();
                }
            }
        }

        private void ScanAsync(IEnumerable<string> paths, bool recursiveScan)
        {
            Observe(LoadScanAsync(paths, recursiveScan), "Scan music");
        }

        private async Task LoadScanAsync(IEnumerable<string> paths, bool recursiveScan)
        {
            Cancel(ref scanCancellation, true);
            scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(uiCancellation.Token);
            scanCancellation.CancelAfter(TimeSpan.FromSeconds(60));
            var token = scanCancellation.Token;
            var generation = ++scanGeneration;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            loadingScan = true;
            SetStatus("Scanning music");

            try
            {
                var input = paths?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? new string[0];
                var result = await Plugin.App.ScanAsync(input, recursiveScan, token);
                token.ThrowIfCancellationRequested();
                if (disposed || !viewActive || generation != scanGeneration)
                {
                    return;
                }

                scanRows.Clear();
                scanRows.AddRange(result?.OfType<JObject>().Select(x => (JObject)x.DeepClone()) ?? Enumerable.Empty<JObject>());
                scanPage = 0;
                selectedPaths.Clear();
                foreach (var row in scanRows)
                {
                    if (string.IsNullOrWhiteSpace(Text(row["error"])))
                    {
                        var path = Text(row["path"]);
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            selectedPaths.Add(path);
                        }
                    }
                }
                selectedSong = scanRows.FirstOrDefault(row => string.Equals(Text(row["path"]), selectedSongPath, StringComparison.OrdinalIgnoreCase));
                if (selectedSong == null)
                {
                    selectedSong = scanRows.FirstOrDefault();
                    selectedSongPath = selectedSong == null ? null : Text(selectedSong["path"]);
                }

                ClearError();
                SetStatus("Scan complete: {0} songs", scanRows.Count);
                NotifySongValues();
                NotifyPropertyChanged("selected-count");
                NotifyPropertyChanged("submit-label");
                RefreshScanList();
            }
            catch (OperationCanceledException)
            {
                if (!disposed && viewActive && generation == scanGeneration && DateTime.UtcNow >= deadline)
                {
                    Plugin.Log.Warn("Beat Sage scan timed out.");
                    SetError("Scan timed out; narrow the selection and try again");
                    SetStatus(string.Empty);
                }
            }
            catch (Exception ex)
            {
                if (!disposed && viewActive && generation == scanGeneration)
                {
                    Plugin.Log.Warn("Beat Sage scan failed: " + ex);
                    SetError("Scan failed; try again");
                }
            }
            finally
            {
                if (generation == scanGeneration)
                {
                    loadingScan = false;
                    RefreshPresentation();
                }
            }
        }

        private IEnumerable<JObject> FilteredBrowserRows()
        {
            var filter = (browserFilter ?? string.Empty).Trim();
            if (filter.Length == 0)
            {
                return browserRows;
            }

            return browserRows.Where(row => Text(row["name"], Path.GetFileName(Text(row["path"]))).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void AddCoverRows(List<JObject> rows, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                var existing = new HashSet<string>(rows.Select(row => Text(row["path"])), StringComparer.OrdinalIgnoreCase);
                foreach (var imagePath in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(IsImage)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    if (existing.Add(imagePath))
                    {
                        rows.Add(new JObject
                        {
                            ["path"] = imagePath,
                            ["name"] = Path.GetFileName(imagePath),
                            ["directory"] = false,
                            ["error"] = string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage cover browse failed for '" + path + "': " + ex);
                rows.Add(new JObject
                {
                    ["path"] = path,
                    ["name"] = "Cover Read Failed",
                    ["directory"] = false,
                    ["error"] = "Could not read cover images in this folder"
                });
            }
        }

        private void RefreshBrowserList()
        {
            if (browserList == null)
            {
                return;
            }

            browserList.Data.Clear();
            foreach (var row in FilteredBrowserRows())
            {
                var path = Text(row["path"]);
                var name = Text(row["name"], Path.GetFileName(path));
                var directory = Bool(row["directory"]);
                var marker = !directory && selectedPaths.Contains(path) ? "✓ " : string.Empty;
                var suffix = directory ? LocalizationBridge.T("Folder") : LocalizationBridge.T(IsImage(path) ? "Image" : "Audio");
                browserList.Data.Add(new CustomListTableData.CustomCellInfo(marker + name, suffix));
            }

            browserList.TableView.ReloadData();
            RequestTypography();
            NotifyPropertyChanged("browser-title");
        }

        private void RefreshScanList()
        {
            if (scanList == null)
            {
                return;
            }

            scanList.Data.Clear();
            foreach (var row in scanRows.Skip(scanPage * ScanPageSize).Take(ScanPageSize))
            {
                var path = Text(row["path"]);
                var title = Text(row["title"], Text(row["name"], Path.GetFileNameWithoutExtension(path)));
                var artist = Text(row["artist"]);
                if (String.IsNullOrWhiteSpace(artist)) artist = LocalizationBridge.T("Unknown Artist");
                var duration = FormatDuration(row["duration"]);
                var rowError = Text(row["error"]);
                var text = selectedPaths.Contains(path) ? "✓ " : string.Empty;
                text += title;
                var subtext = string.IsNullOrWhiteSpace(rowError) ? artist + (duration == "—" ? string.Empty : " · " + duration) : LocalizationBridge.Message(rowError);
                scanList.Data.Add(new CustomListTableData.CustomCellInfo(text, subtext));
            }

            scanList.TableView.ReloadData();
            RequestTypography();
            NotifyPropertyChanged("scan-page");
            NotifyPropertyChanged("can-scan-prev");
            NotifyPropertyChanged("can-scan-next");
        }

        private void RefreshQueueList()
        {
            if (queueList == null)
            {
                return;
            }

            queueList.Data.Clear();
            foreach (var job in jobs)
            {
                var title = Text(job["title"], Text(job["path"], LocalizationBridge.T("Untitled Song")));
                var artist = Text(job["artist"]);
                var state = Text(job["state"], "Unknown");
                var progress = TransferProgressText(job, state);
                var stage = Text(job["remoteStage"]);
                if (string.Equals(stage, "DONE", StringComparison.OrdinalIgnoreCase))
                {
                    stage = string.Empty;
                }
                var subtext = LocalizationBridge.T(StateLabel(state)) + progress + (string.IsNullOrWhiteSpace(stage) ? string.Empty : " · " + stage) + (string.IsNullOrWhiteSpace(artist) ? string.Empty : " · " + artist);
                var rowError = Text(job["errorMessage"]);
                if (!string.IsNullOrWhiteSpace(rowError))
                {
                    subtext += " · " + LocalizationBridge.Message(rowError);
                }

                var marker = string.Equals(Text(job["id"]), selectedJobId, StringComparison.OrdinalIgnoreCase) ? "● " : "○ ";
                queueList.Data.Add(new CustomListTableData.CustomCellInfo(marker + title, subtext));
            }

            queueList.TableView.ReloadData();
            RequestTypography();
            NotifyPropertyChanged("selected-job");
            NotifyPropertyChanged("can-cancel-job");
            NotifyPropertyChanged("can-retry-job");
            NotifyPropertyChanged("can-regenerate-job");
            NotifyPropertyChanged("can-show-song");
            NotifyPropertyChanged("can-import-ready");
        }

        private string TransferProgressText(JObject job, string state)
        {
            var lower = (state ?? string.Empty).ToLowerInvariant();
            var transfer = lower.Contains("upload") || lower.Contains("download");
            if (!transfer || job["progress"] == null || job["progress"].Type == JTokenType.Null)
            {
                return string.Empty;
            }

            var progress = job["progress"].Value<double>();
            if (progress > 1.0)
            {
                progress /= 100.0;
            }

            return " " + Math.Max(0, Math.Min(100, progress * 100)).ToString("0") + "%";
        }

        private int ScanPageCount => scanRows.Count == 0 ? 0 : (scanRows.Count + ScanPageSize - 1) / ScanPageSize;

        private static string StateLabel(string state)
        {
            switch ((state ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant())
            {
                case "queued": return "Queued";
                case "preflight": return "Preflight";
                case "uploading": return "Uploading";
                case "submissionunknown": return "Submission Unknown";
                case "remotepending": return "Remote Queue";
                case "loadingaudio": return "Loading Audio";
                case "creatinglevel": return "Creating Level";
                case "formattinglevel": return "Formatting";
                case "downloading": return "Downloading";
                case "readytoimport": return "Ready to Import";
                case "importing": return "Importing";
                case "waitingforlibrary": return "Waiting for Library";
                case "imported": return "Imported";
                case "cancelled": return "Cancelled";
                case "retryneeded": return "Retry Needed";
                case "fileerror": return "File Error";
                case "failed": return "Failed";
                default: return "Unknown";
            }
        }

        private JObject SelectedJobObject() => jobs.FirstOrDefault(job => string.Equals(Text(job["id"]), selectedJobId, StringComparison.OrdinalIgnoreCase));

        private bool SelectedJobIs(params string[] states)
        {
            var job = SelectedJobObject();
            return job != null && IsState(job, states);
        }

        private static bool IsState(JObject job, params string[] states)
        {
            var state = Text(job?["state"]);
            return states.Any(value => string.Equals(value, state, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsUnknownUploadResult(JObject job)
        {
            var state = Text(job["state"]).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
            return state == "submissionunknown" || (state.Contains("upload") && state.Contains("unknown"));
        }

        private bool SnapshotPaused()
        {
            try
            {
                var value = Plugin.App.Snapshot?["paused"];
                return Bool(value);
            }
            catch
            {
                return false;
            }
        }

        private void TryJobAction(string label, Action action)
        {
            try
            {
                action();
                ClearError();
                SetStatus("{0} sent", label);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn("Beat Sage " + label + " failed: " + ex);
                SetError("{0} failed: {1}", label, ExceptionMessage(ex));
            }
        }

        private async Task ObserveTaskAsync(Task task, string label)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error("Beat Sage " + label + " failed: " + ex);
                if (!disposed && viewActive)
                {
                    SetError("{0} failed: {1}", label, ExceptionMessage(ex));
                }
            }
        }

        private static string ExceptionMessage(Exception exception)
        {
            var message = exception?.GetBaseException()?.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Unknown error";
            }

            message = message.Trim();
            return message.Length <= 240 ? message : message.Substring(0, 240) + "…";
        }

        private void Observe(Task task, string label)
        {
            if (task == null)
            {
                SetError("{0} did not start", label);
                return;
            }

            _ = ObserveTaskAsync(task, label);
        }

        private void ClearError()
        {
            if (errorMessage.Length == 0)
            {
                return;
            }

            errorMessage = string.Empty;
            NotifyPropertyChanged("has-error");
            NotifyPropertyChanged("error");
        }

        private void SetStatus(string template, params object[] args)
        {
            statusMessage = LocalizationBridge.SourceFormat(template, args);
            NotifyPropertyChanged("status");
        }

        private void SetError(string template, params object[] args)
        {
            errorMessage = LocalizationBridge.SourceFormat(template, args) ?? string.Empty;
            if (errorText != null)
            {
                errorText.text = LocalizationBridge.Message(errorMessage);
            }

            NotifyPropertyChanged("has-error");
            NotifyPropertyChanged("error");
        }

        private static void Cancel(ref CancellationTokenSource source, bool dispose)
        {
            if (source == null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch
            {
            }

            if (dispose)
            {
                source.Dispose();
                source = null;
            }
        }

        private static Dictionary<string, string> BuildUiMemberNames()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in typeof(BeatSageViewController).GetMembers(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var attribute = member.GetCustomAttributes(typeof(UIValue), true).OfType<UIValue>().FirstOrDefault();
                if (attribute != null && !string.IsNullOrWhiteSpace(attribute.Id))
                {
                    result[attribute.Id] = member.Name;
                }
            }

            return result;
        }

        private new void NotifyPropertyChanged(string alias)
        {
            NotifyUi(alias);
        }

        private void NotifyUi(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            base.NotifyPropertyChanged(UiMemberNames.TryGetValue(alias, out var memberName) ? memberName : alias);
            RefreshPresentation();
        }

        private static string[] ParseFormats(string value)
        {
            var allowed = new HashSet<string>(new[] { ".mp3", ".flac", ".wav", ".ogg", ".aac", ".aiff", ".aif" }, StringComparer.OrdinalIgnoreCase);
            return (value ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Select(x => x.StartsWith(".", StringComparison.Ordinal) ? x : "." + x)
                .Where(allowed.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray();
        }

        private bool HasFormat(string format)
        {
            return ParseFormats(formatText).Contains(format, StringComparer.OrdinalIgnoreCase);
        }

        private void SetFormat(string format, bool enabled, bool notify = true)
        {
            var values = ParseFormats(formatText).ToList();
            if (enabled && !values.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(format);
            }
            else if (!enabled && values.Count <= 1 && values.Contains(format, StringComparer.OrdinalIgnoreCase))
            {
                SetError("Keep at least one audio format");
                if (notify)
                {
                    NotifySettingsFormatValues();
                }

                return;
            }
            else if (!enabled)
            {
                values.RemoveAll(value => string.Equals(value, format, StringComparison.OrdinalIgnoreCase));
            }

            formatText = string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase));
            if (notify)
            {
                NotifySettingsFormatValues();
            }
        }

        private void SetAiffFormats(bool enabled)
        {
            var values = ParseFormats(formatText).ToList();
            if (enabled)
            {
                if (!values.Contains(".aiff", StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(".aiff");
                }

                if (!values.Contains(".aif", StringComparer.OrdinalIgnoreCase))
                {
                    values.Add(".aif");
                }
            }
            else
            {
                values.RemoveAll(value => string.Equals(value, ".aiff", StringComparison.OrdinalIgnoreCase) || string.Equals(value, ".aif", StringComparison.OrdinalIgnoreCase));
                if (values.Count == 0)
                {
                    SetError("Keep at least one audio format");
                    NotifySettingsFormatValues();
                    return;
                }
            }

            formatText = string.Join(", ", values.Distinct(StringComparer.OrdinalIgnoreCase));
            NotifySettingsFormatValues();
        }

        private static bool IsAudio(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return new[] { ".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac", ".aiff", ".aif" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsImage(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty);
            return string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToggleLabel(string label, bool enabled)
        {
            return LocalizationBridge.T(label);
        }

        private static string[] CatalogValues(JToken token)
        {
            if (!(token is JArray array))
            {
                return new string[0];
            }

            return array.Select(item => item is JObject ? Text(item["value"], Text(item["label"])) : Text(item))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string[] Strings(JToken token)
        {
            if (!(token is JArray array))
            {
                return new string[0];
            }

            return array.Select(item => Text(item)).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        }

        private static string Text(JToken token, string fallback = "")
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback ?? string.Empty;
            }

            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        private static string FormatDuration(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return "—";
            }

            try
            {
                var seconds = token.Value<double>();
                if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
                {
                    return "—";
                }

                var duration = TimeSpan.FromSeconds(seconds);
                return ((int)duration.TotalMinutes).ToString("0") + ":" + duration.Seconds.ToString("00");
            }
            catch
            {
                return "—";
            }
        }

        private static bool Bool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            return bool.TryParse(Text(token), out var value) && value;
        }
    }
}
