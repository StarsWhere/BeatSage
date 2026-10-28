using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using IPA;
using IPA.Utilities;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BeatSage;

[Plugin(RuntimeOptions.SingleStartInit)]
public sealed class Plugin
{
    public static IPA.Logging.Logger Log { get; private set; }
    public static BeatSageApp App { get; private set; }
    GameObject owner;
    [Init] public void Init(IPA.Logging.Logger logger) { Log = logger; }
    [OnStart] public void Start()
    {
        owner = new GameObject("BeatSage.Service"); UnityEngine.Object.DontDestroyOnLoad(owner);
        owner.AddComponent<RuntimeHost>();
    }
    internal static void Initialize(RuntimeHost host)
    {
        App = new BeatSageApp(UnityGame.InstallPath, host, diagnostic: e => Log.Error(e)); App.Start();
        Log.Info("Beat Sage ready; persistent serial generation queue");
    }
    [OnExit] public void Exit()
    {
        try { UI.Menu.Dispose(); } finally { App?.Dispose(); App = null; if (owner != null) UnityEngine.Object.Destroy(owner); }
    }
}

public sealed class RuntimeHost : MonoBehaviour, IGameHost
{
    volatile bool canImport;
    string[] environments = new[] { "DefaultEnvironment" };
    bool registered, environmentsLoaded;
    float nextTick;
    Coroutine selection;
    int selectionVersion;
    int playlistRevision = 1, syncedPlaylistRevision;
    int failedPlaylistRevision, playlistFailures;
    bool previousAutoPlaylist;
    float nextPlaylistAttempt;
    public bool CanImport => canImport;
    public string[] EnvironmentNames => environments;
    static T Active<T>() where T : Component => Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x => x != null && x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy);
    void Start()
    {
        try { SongCore.Loader.SongsLoadedEvent += LibraryLoaded; Plugin.Initialize(this); }
        catch (Exception e) { Plugin.Log.Error(e); enabled = false; }
    }
    void LibraryLoaded(object sender, object args) => Interlocked.Increment(ref playlistRevision);
    void Update()
    {
        if (Time.unscaledTime < nextTick) return; nextTick = Time.unscaledTime + .25f;
        try
        {
            canImport = SceneManager.GetSceneByName("MainMenu").isLoaded && !SceneManager.GetSceneByName("GameCore").isLoaded
                && SongCore.Loader.Instance != null && !SongCore.Loader.AreSongsLoading
                && !Resources.FindObjectsOfTypeAll<HMUI.FlowCoordinator>().Any(x => x != null && x.gameObject.activeInHierarchy && x.isInTransition);
            if (!environmentsLoaded && canImport) LoadEnvironments();
            if (!registered && Active<MainMenuViewController>()?.isActivated == true) { UI.Menu.Register(); registered = true; }
            Plugin.App?.Tick(SceneManager.GetSceneByName("GameCore").isLoaded);
            SyncPlaylist();
        }
        catch (Exception e) { Plugin.Log.Error(e); canImport = false; }
    }
    void SyncPlaylist()
    {
        bool auto = Plugin.App?.AutoPlaylist == true;
        if (auto && !previousAutoPlaylist) { Interlocked.Increment(ref playlistRevision); nextPlaylistAttempt = 0; }
        previousAutoPlaylist = auto;
        int revision = Volatile.Read(ref playlistRevision);
        if (failedPlaylistRevision != revision) { playlistFailures = 0; failedPlaylistRevision = revision; }
        if (!auto || !canImport || !SongCore.Loader.AreSongsLoaded || revision == syncedPlaylistRevision || Time.unscaledTime < nextPlaylistAttempt) return;
        if (playlistFailures >= 3) return;
        // Neither the dependency library alone nor a disabled consumer counts as playlist support.
        if (IPA.Loader.PluginManager.GetPluginFromId("PlaylistManager") == null && IPA.Loader.PluginManager.GetPluginFromId("MorePlaylists") == null)
        { syncedPlaylistRevision = revision; return; }
        if (IPA.Loader.PluginManager.GetPluginFromId("BeatSaberPlaylistsLib") == null)
        { syncedPlaylistRevision = revision; return; }
        try
        {
            int added = SyncGeneratedPlaylist();
            syncedPlaylistRevision = revision;
            if (added > 0) Plugin.Log.Info("Added " + added + " songs to the Beat Sage playlist");
        }
        catch (Exception e)
        {
            nextPlaylistAttempt = Time.unscaledTime + 60;
            playlistFailures++;
            Plugin.Log.Error(e);
            Plugin.App.ReportNotice("Could not update the Beat Sage playlist; retry after checking the playlist file");
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    int SyncGeneratedPlaylist()
    {
        var known = new HashSet<string>(Plugin.App.GeneratedLevelIds, StringComparer.OrdinalIgnoreCase);
        var songs = SongCore.Loader.CustomLevels.Values
            .Where(level => level != null && (known.Contains(level.levelID) || (level.allMappers?.Any(BeatSagePlaylist.IsBeatSageMapper) == true)))
            .Select(level => (BeatSaberPlaylistsLib.Types.ISong)new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistSong(
                hash: SongCore.Collections.GetCustomLevelHash(level.levelID), songName: level.songName,
                mapper: string.Join(", ", (level.allMappers ?? Array.Empty<string>()).Concat(level.allLighters ?? Array.Empty<string>()).Distinct())));
        return BeatSagePlaylist.Sync(BeatSaberPlaylistsLib.PlaylistManager.DefaultManager, songs);
    }
    void LoadEnvironments()
    {
        var catalog = new ContractRegistry().EnvironmentCatalog;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in Resources.FindObjectsOfTypeAll<EnvironmentInfoSO>())
        {
            names.Add(value.environmentName);
            names.Add(value.serializedName);
        }
        var values = catalog.Cast<JObject>().Where(e => names.Contains((string)e["gameName"]) || (string)e["value"] == "DefaultEnvironment").Select(e => (string)e["value"]).ToArray();
        environments = values.Length == 0 ? new[] { "DefaultEnvironment" } : values;
        environmentsLoaded = true;
        Plugin.Log.Info("Available Beat Sage environments: " + String.Join(",", environments));
    }
    public async Task RefreshAsync(CancellationToken token)
    {
        if (!canImport) throw new OperationCanceledException(token);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Loaded(object sender, object args) => completed.TrySetResult(true);
        SongCore.Loader.SongsLoadedEvent += Loaded;
        try
        {
            token.ThrowIfCancellationRequested(); SongCore.Loader.Instance.RefreshSongs(false);
            var timeout = Task.Delay(TimeSpan.FromSeconds(60), token);
            if (await Task.WhenAny(completed.Task, timeout) != completed.Task)
            { token.ThrowIfCancellationRequested(); throw new TimeoutException("The library refresh timed out; the map package was preserved"); }
            await completed.Task;
        }
        finally { SongCore.Loader.SongsLoadedEvent -= Loaded; }
    }
    public bool IsLoaded(string levelId, JArray expectedKeys)
    {
        var level = SongCore.Loader.GetLevelById(levelId);
        if (level == null) return false;
        var actual = new HashSet<string>(level.GetBeatmapKeys().Select(k => SongCore.Collections.GetCurrentCharacteristicSerializedName(k.characteristic) + "|" + k.difficulty));
        if (!actual.IsSupersetOf(expectedKeys.Select(k => (string)k))) return false;
        return level.GetBeatmapKeys().All(k => SongCore.Collections.GetCustomLevelSongDifficultyData(k)?.additionalDifficultyData._requirements.All(SongCore.Collections.capabilities.Contains) == true);
    }
    public void ShowSong(string levelId)
    {
        if (!canImport) throw new InvalidOperationException("Return to the menu before viewing the song");
        Plugin.App?.ReportNotice("");
        int version = ++selectionVersion;
        UI.Menu.Close(() => { if (version != selectionVersion) return; if (selection != null) StopCoroutine(selection); selection = StartCoroutine(Guard(Select(levelId))); });
    }
    IEnumerator Guard(IEnumerator routine)
    {
        while (true)
        {
            bool next; object current;
            try { next = routine.MoveNext(); current = next ? routine.Current : null; }
            catch (Exception e) { Plugin.Log.Error(e); Plugin.App?.ReportNotice("Could not open the song: " + e.Message); yield break; }
            if (!next) yield break; yield return current;
        }
    }
    IEnumerator Select(string levelId)
    {
        var level = SongCore.Loader.GetLevelById(levelId);
        if (level == null) throw new InvalidOperationException("The song is not loaded in the library");
        var key = level.GetBeatmapKeys().OrderBy(k => SongCore.Collections.GetCurrentCharacteristicSerializedName(k.characteristic) == "Standard" ? 0 : 1).ThenBy(k => k.difficulty).First();
        var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().FirstOrDefault();
        if (solo == null) throw new InvalidOperationException("The Solo level selection page is not ready");
        var state = new LevelSelectionFlowCoordinator.State(SelectLevelCategoryViewController.LevelCategory.All, SongCore.Loader.CustomLevelsPack, in key, level);
        solo.Setup(state);
        double deadline = Time.realtimeSinceStartupAsDouble + 30;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            var menu = Active<MainMenuViewController>();
            if (menu != null && menu.isActivated)
            {
                ((Button)AccessTools.Field(typeof(MainMenuViewController), "_soloButton").GetValue(menu)).onClick.Invoke();
                break;
            }
            if (Active<StandardLevelDetailView>()?.beatmapKey.levelId == levelId) yield break;
            yield return null;
        }
        while (Time.realtimeSinceStartupAsDouble < deadline && Active<StandardLevelDetailView>()?.beatmapKey.levelId != levelId) yield return null;
        if (Active<StandardLevelDetailView>()?.beatmapKey.levelId != levelId) throw new TimeoutException("Song selection timed out");
    }
    void OnDestroy() { SongCore.Loader.SongsLoadedEvent -= LibraryLoaded; canImport = false; }
}
