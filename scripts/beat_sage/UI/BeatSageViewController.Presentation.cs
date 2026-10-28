using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BeatSage.UI
{
    internal sealed partial class BeatSageViewController
    {
        [UIComponent("generation-panel")] private RectTransform generationPanel;
        [UIComponent("application-panel")] private RectTransform applicationPanel;
        [UIComponent("presets-panel")] private RectTransform presetsPanel;
        [UIComponent("choice-list")] private CustomListTableData choiceList;
        [UIComponent("choice-modal")] private ModalView choiceModal;
        [UIComponent("submit-button")] private Button submitButton;
        [UIComponent("browser-title-text")] private TMP_Text browserTitleText;
        [UIComponent("current-path-text")] private TMP_Text currentPathText;
        [UIComponent("selected-song-title-text")] private TMP_Text selectedSongTitleText;
        [UIComponent("selected-song-artist-text")] private TMP_Text selectedSongArtistText;
        [UIComponent("job-title-text")] private TMP_Text jobTitleText;
        [UIComponent("job-artist-text")] private TMP_Text jobArtistText;
        [UIComponent("job-error-text")] private TMP_Text jobErrorText;
        [UIComponent("favorites-summary-text")] private TMP_Text favoritesSummaryText;
        [UIComponent("selected-preset-button")] private Button selectedPresetButton;
        [UIValue("setting-recursive-off")] private bool SettingRecursiveOff => !SettingRecursive;
        [UIValue("event-dot-blocks-off")] private bool EventDotBlocksEnabledOff => !EventDotBlocksEnabled;
        [UIValue("mode-ninety-off")] private bool ModeNinetyEnabledOff => !ModeNinetyEnabled;
        [UIValue("setting-format-aac-off")] private bool SettingFormatAacOff => !SettingFormatAac;
        [UIValue("setting-retain-zip-off")] private bool SettingRetainZipOff => !SettingRetainZip;
        [UIValue("setting-format-aiff-off")] private bool SettingFormatAiffOff => !SettingFormatAiff;
        [UIValue("mode-one-saber-off")] private bool ModeOneSaberEnabledOff => !ModeOneSaberEnabled;
        [UIValue("setting-format-ogg-off")] private bool SettingFormatOggOff => !SettingFormatOgg;
        [UIValue("setting-format-mp3-off")] private bool SettingFormatMp3Off => !SettingFormatMp3;
        [UIValue("difficulty-hard-off")] private bool DifficultyHardOff => !DifficultyHard;
        [UIValue("setting-auto-import-off")] private bool SettingAutoImportOff => !SettingAutoImport;
        [UIValue("setting-auto-playlist-off")] private bool SettingAutoPlaylistOff => !SettingAutoPlaylist;
        [UIValue("mode-standard-off")] private bool ModeStandardEnabledOff => !ModeStandardEnabled;
        [UIValue("mode-no-arrows-off")] private bool ModeNoArrowsEnabledOff => !ModeNoArrowsEnabled;
        [UIValue("difficulty-expert-plus-off")] private bool DifficultyExpertPlusOff => !DifficultyExpertPlus;
        [UIValue("setting-resume-off")] private bool SettingResumeOnStartOff => !SettingResumeOnStart;
        [UIValue("setting-format-flac-off")] private bool SettingFormatFlacOff => !SettingFormatFlac;
        [UIValue("difficulty-expert-off")] private bool DifficultyExpertOff => !DifficultyExpert;
        [UIValue("setting-format-wav-off")] private bool SettingFormatWavOff => !SettingFormatWav;
        [UIValue("difficulty-normal-off")] private bool DifficultyNormalOff => !DifficultyNormal;
        [UIValue("event-obstacles-off")] private bool EventObstaclesEnabledOff => !EventObstaclesEnabled;
        [UIValue("event-bombs-off")] private bool EventBombsEnabledOff => !EventBombsEnabled;
        [UIValue("caption-add-current-folder")] private string CaptionAddCurrentFolder => LocalizationBridge.T("Add Current Folder");
        [UIValue("caption-application")] private string CaptionApplication => LocalizationBridge.T("Application");
        [UIValue("caption-artist")] private string CaptionArtist => LocalizationBridge.T("Artist");
        [UIValue("caption-audio-formats")] private string CaptionAudioFormats => LocalizationBridge.T("Audio Formats");
        [UIValue("caption-back")] private string CaptionBack => LocalizationBridge.T("Back");
        [UIValue("caption-cancel")] private string CaptionCancel => LocalizationBridge.T("Cancel");
        [UIValue("caption-choose-cover")] private string CaptionChooseCover => LocalizationBridge.T("Choose Cover");
        [UIValue("caption-clear")] private string CaptionClear => LocalizationBridge.T("Clear");
        [UIValue("caption-confirm-retry")] private string CaptionConfirmRetry => LocalizationBridge.T("Confirm Retry");
        [UIValue("caption-cover-path")] private string CaptionCoverPath => LocalizationBridge.T("Cover Path");
        [UIValue("caption-delete")] private string CaptionDelete => LocalizationBridge.T("Delete");
        [UIValue("caption-difficulties")] private string CaptionDifficulties => LocalizationBridge.T("Difficulties");
        [UIValue("caption-events")] private string CaptionEvents => LocalizationBridge.T("Events");
        [UIValue("caption-search")] private string CaptionSearch => LocalizationBridge.T("Search");
        [UIValue("caption-generation")] private string CaptionGeneration => LocalizationBridge.T("Generation");
        [UIValue("caption-generation-queue")] private string CaptionGenerationQueue => LocalizationBridge.T("Generation Queue");
        [UIValue("caption-import-ready")] private string CaptionImportReady => LocalizationBridge.T("Import Ready");
        [UIValue("caption-last-folder")] private string CaptionLastFolder => LocalizationBridge.T("Last Folder");
        [UIValue("caption-load")] private string CaptionLoad => LocalizationBridge.T("Load");
        [UIValue("caption-local-music")] private string CaptionLocalMusic => LocalizationBridge.T("Local Music");
        [UIValue("caption-modes")] private string CaptionModes => LocalizationBridge.T("Modes");
        [UIValue("caption-name")] private string CaptionName => LocalizationBridge.T("Name");
        [UIValue("caption-next-page")] private string CaptionNextPage => LocalizationBridge.T("Next Page");
        [UIValue("caption-open-song")] private string CaptionOpenSong => LocalizationBridge.T("Open Song");
        [UIValue("caption-parent")] private string CaptionParent => LocalizationBridge.T("Parent");
        [UIValue("caption-presets")] private string CaptionPresets => LocalizationBridge.T("Presets");
        [UIValue("caption-previous-page")] private string CaptionPreviousPage => LocalizationBridge.T("Previous Page");
        [UIValue("caption-regenerate")] private string CaptionRegenerate => LocalizationBridge.T("Regenerate");
        [UIValue("caption-remove-current-favorite")] private string CaptionRemoveCurrentFavorite => LocalizationBridge.T("Remove Current Favorite");
        [UIValue("caption-retry")] private string CaptionRetry => LocalizationBridge.T("Retry");
        [UIValue("caption-root")] private string CaptionRoot => LocalizationBridge.T("Root");
        [UIValue("caption-save-current")] private string CaptionSaveCurrent => LocalizationBridge.T("Save Current");
        [UIValue("caption-save-settings")] private string CaptionSaveSettings => LocalizationBridge.T("Save Settings");
        [UIValue("caption-scan-current")] private string CaptionScanCurrent => LocalizationBridge.T("Scan Current");
        [UIValue("caption-scan-preview")] private string CaptionScanPreview => LocalizationBridge.T("Scan Preview");
        [UIValue("caption-scan-selected")] private string CaptionScanSelected => LocalizationBridge.T("Scan Selected");
        [UIValue("caption-select-all")] private string CaptionSelectAll => LocalizationBridge.T("Select All");
        [UIValue("caption-settings")] private string CaptionSettings => LocalizationBridge.T("Settings");
        [UIValue("caption-song-metadata")] private string CaptionSongMetadata => LocalizationBridge.T("Song Metadata");
        [UIValue("caption-the-upload-result-is-unknown-resubmit")] private string CaptionTheuploadresultisunknownResubmit => LocalizationBridge.T("The upload result is unknown. Resubmit?");
        [UIValue("caption-title")] private string CaptionTitle => LocalizationBridge.T("Title");
        private string settingsSection = "generation", choiceKind = "environment";
        private readonly List<string> choiceValues = new List<string>();
        private bool refreshingPresentation;
        private Coroutine typographyRoutine;

        [UIValue("is-generation-settings")] private bool IsGenerationSettings => settingsSection == "generation";
        [UIValue("is-application-settings")] private bool IsApplicationSettings => settingsSection == "application";
        [UIValue("is-preset-settings")] private bool IsPresetSettings => settingsSection == "presets";
        [UIValue("not-generation-settings")] private bool NotGenerationSettings => !IsGenerationSettings;
        [UIValue("not-application-settings")] private bool NotApplicationSettings => !IsApplicationSettings;
        [UIValue("not-preset-settings")] private bool NotPresetSettings => !IsPresetSettings;
        [UIValue("can-submit")] private bool CanSubmit => !loadingScan && scanRows.Any(row => selectedPaths.Contains(Text(row["path"])) && string.IsNullOrEmpty(Text(row["error"])));
        [UIValue("has-selected-job")] private bool HasSelectedJob => SelectedJobObject() != null;
        [UIValue("has-feedback")] private bool HasFeedback => !string.IsNullOrEmpty(errorMessage) || !string.IsNullOrEmpty(noticeMessage) || loadingBrowser || loadingScan || statusMessage == "Settings saved" || statusMessage.StartsWith("Preset", StringComparison.Ordinal);
        [UIValue("feedback")] private string Feedback => HasError ? Error : !string.IsNullOrEmpty(noticeMessage) ? Notice : Status;
        [UIValue("option-summary")] private string OptionSummary => FormatModel(SelectedModel) + "  ·  " + string.Join(" / ", Strings(options["modes"]).Select(ModeCaption)) + "  ·  " + string.Join(" / ", Strings(options["difficulties"]).Select(DifficultyCaption));
        [UIValue("queue-summary")] private string QueueSummary => jobs.Count(job => IsState(job, "Imported")) + " / " + jobs.Count + "  " + LocalizationBridge.T("Imported");
        [UIValue("job-title")] private string JobTitle => Text(SelectedJobObject()?["title"], LocalizationBridge.T("No Task Selected"));
        [UIValue("job-artist")] private string JobArtist => Text(SelectedJobObject()?["artist"]);
        [UIValue("job-stage")] private string JobStage => SelectedJobObject() is JObject job ? LocalizationBridge.T(StateLabel(Text(job["state"]))) + TransferProgressText(job, Text(job["state"])) : string.Empty;
        [UIValue("job-options")] private string JobOptions => SelectedJobObject()?["options"] is JObject value ? string.Join(" / ", Strings(value["difficulties"]).Select(DifficultyCaption)) + "\n" + string.Join(" / ", Strings(value["modes"]).Select(ModeCaption)) + "  ·  " + FormatModel(Text(value["model"])) : string.Empty;
        [UIValue("job-error")] private string JobError => LocalizationBridge.Message(Text(SelectedJobObject()?["errorMessage"]));
        [UIValue("has-job-error")] private bool HasJobError => !string.IsNullOrEmpty(JobError);
        [UIValue("choice-title")] private string ChoiceTitle => choiceKind == "preset" ? LocalizationBridge.T("Select Preset") : choiceKind == "model" ? SelectedModelLabel : SelectedEnvironmentLabel;
        [UIValue("has-presets")] private bool HasPresets => presets.Count > 0;
        [UIValue("has-selected-preset")] private bool HasSelectedPreset => presets.Any(preset => Text(preset["name"]) == selectedPreset);
        [UIValue("edit-label")] private string EditLabel => LocalizationBridge.T("Edit Metadata");

        [UIAction("settings-generation")] private void ShowGenerationSettings() { settingsSection = "generation"; RefreshPresentation(); }
        [UIAction("settings-application")] private void ShowApplicationSettings() { settingsSection = "application"; RefreshPresentation(); }
        [UIAction("settings-presets")] private void ShowPresetSettings() { settingsSection = "presets"; RefreshPresentation(); }
        [UIAction("open-generation")] private void OpenGenerationSettings() { ShowGenerationSettings(); ShowSettingsTab(); }
        [UIAction("open-model-choices")] private void OpenModelChoices() => OpenChoices("model");
        [UIAction("open-environment-choices")] private void OpenEnvironmentChoices() => OpenChoices("environment");
        [UIAction("open-preset-choices")] private void OpenPresetChoices() => OpenChoices("preset");
        [UIAction("close-choices")] private void CloseChoices() => choiceModal?.Hide(false, null);

        private void OpenChoices(string kind)
        {
            choiceKind = kind; choiceValues.Clear();
            choiceValues.AddRange(kind == "model" ? modelChoices.Select(v => v.ToString()) : kind == "preset" ? PresetChoices.Select(v => v.ToString()) : environmentChoices.Select(v => v.ToString()));
            choiceList.Data.Clear();
            string selected = kind == "model" ? SelectedModel : kind == "preset" ? SelectedPreset : SelectedEnvironment;
            foreach (var value in choiceValues)
            {
                string label = kind == "model" ? FormatModel(value) : kind == "environment" ? FormatEnvironment(value) : value;
                choiceList.Data.Add(new CustomListTableData.CustomCellInfo((value == selected ? "✓  " : "") + label));
            }
            RefreshPresentation(); choiceModal.Show(false, true, null); choiceList.TableView.ReloadData();
            RequestTypography();
        }

        [UIAction("choice-select")]
        private void ChoiceSelected(TableView table, int index)
        {
            if (index < 0 || index >= choiceValues.Count) return;
            var value = choiceValues[index];
            if (choiceKind == "model") SelectedModel = value;
            else if (choiceKind == "preset") SelectedPreset = value;
            else SelectedEnvironment = value;
            RefreshPresentation(); CloseChoices();
        }

        private static string DifficultyCaption(string value) => LocalizationBridge.T(value == "ExpertPlus" ? "Expert+" : value);
        private static string ModeCaption(string value) => LocalizationBridge.T(value == "NoArrows" ? "No Arrows" : value == "OneSaber" ? "One Saber" : value == "90Degree" ? "90 Degree" : value);

        private void RequestTypography()
        {
            if (!parsed || disposed || !isActiveAndEnabled) return;
            if (typographyRoutine != null) StopCoroutine(typographyRoutine);
            typographyRoutine = StartCoroutine(ApplyTypographyAfterLayout());
        }

        private IEnumerator ApplyTypographyAfterLayout()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            NormalizeUiTypography();
            typographyRoutine = null;
        }

        private void RefreshPresentation()
        {
            if (refreshingPresentation || !parsed || disposed) return;
            refreshingPresentation = true;
            try
            {
                foreach (var alias in new[] { "is-generation-settings", "is-application-settings", "is-preset-settings", "not-generation-settings", "not-application-settings", "not-preset-settings", "can-submit", "has-selected-job", "has-feedback", "feedback", "option-summary", "queue-summary", "job-title", "job-artist", "job-stage", "job-options", "job-error", "has-job-error", "choice-title", "has-presets", "has-selected-preset", "edit-label" })
                    base.NotifyPropertyChanged(UiMemberNames[alias]);
                foreach (var alias in new[] { "setting-recursive-off", "event-dot-blocks-off", "mode-ninety-off", "setting-format-aac-off", "setting-retain-zip-off", "setting-format-aiff-off", "mode-one-saber-off", "setting-format-ogg-off", "setting-format-mp3-off", "difficulty-hard-off", "setting-auto-import-off", "setting-auto-playlist-off", "mode-standard-off", "mode-no-arrows-off", "difficulty-expert-plus-off", "setting-resume-off", "setting-format-flac-off", "difficulty-expert-off", "setting-format-wav-off", "difficulty-normal-off", "event-obstacles-off", "event-bombs-off", "caption-add-current-folder", "caption-application", "caption-artist", "caption-audio-formats", "caption-back", "caption-cancel", "caption-choose-cover", "caption-clear", "caption-confirm-retry", "caption-cover-path", "caption-delete", "caption-difficulties", "caption-events", "caption-search", "caption-generation", "caption-generation-queue", "caption-import-ready", "caption-last-folder", "caption-load", "caption-local-music", "caption-modes", "caption-name", "caption-next-page", "caption-open-song", "caption-parent", "caption-presets", "caption-previous-page", "caption-regenerate", "caption-remove-current-favorite", "caption-retry", "caption-root", "caption-save-current", "caption-save-settings", "caption-scan-current", "caption-scan-preview", "caption-scan-selected", "caption-select-all", "caption-settings", "caption-song-metadata", "caption-the-upload-result-is-unknown-resubmit", "caption-title" }) base.NotifyPropertyChanged(UiMemberNames[alias]);
                RefreshHints();
            }
            finally { refreshingPresentation = false; }
        }

        private void RefreshHints()
        {
            var songPath = selectedSong == null ? string.Empty : Text(selectedSong["path"]);
            var job = SelectedJobObject();
            var jobPath = job == null ? string.Empty : Text(job["path"]);
            SetHint(errorText, Feedback);
            SetHint(browserTitleText, CurrentPath);
            SetHint(currentPathText, CurrentPath);
            SetHint(selectedSongTitleText, WithPath(SelectedSongTitle, songPath));
            SetHint(selectedSongArtistText, SelectedSongArtist);
            SetHint(jobTitleText, WithPath(JobTitle, jobPath));
            SetHint(jobArtistText, JobArtist);
            SetHint(jobErrorText, JobError);
            var favorites = Strings(settings["favorites"]);
            SetHint(favoritesSummaryText, favorites.Length == 0 ? FavoritesSummary : string.Join("\n", favorites));
            SetHint(selectedPresetButton, SelectedPresetLabel);
        }

        private static string WithPath(string value, string path) => string.IsNullOrWhiteSpace(path) ? value : value + "\n" + path;

        private static void SetHint(Component target, string value)
        {
            if (target == null) return;
            var hint = target.GetComponent<HoverHint>() ?? target.GetComponentInParent<HoverHint>();
            if (hint != null) hint.text = value ?? string.Empty;
            var graphic = target as Graphic;
            if (graphic != null) graphic.raycastTarget = true;
        }
    }
}
