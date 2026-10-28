using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SongPackageIO;
using S = BeatSage.Contracts.JobState;

namespace BeatSage;

public interface IGameHost
{
    bool CanImport { get; }
    string[] EnvironmentNames { get; }
    Task RefreshAsync(CancellationToken token);
    bool IsLoaded(string levelId, JArray expectedKeys);
    void ShowSong(string levelId);
}

public sealed class BeatSageApp : IDisposable
{
    readonly object gate = new object();
    readonly object coverGate = new object();
    readonly string root, dataRoot, workRoot, statePath;
    readonly ContractRegistry contracts;
    readonly IGameHost host;
    readonly SynchronizationContext main;
    readonly Action<Exception> diagnostic;
    readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    readonly SemaphoreSlim wake = new SemaphoreSlim(0);
    readonly IBeatSageClient client;
    JObject state;
    Task processor = Task.CompletedTask, importer = Task.CompletedTask;
    CancellationTokenSource activeCancel, importCancel;
    string activeId, storeFault;
    bool started, importRequested, holdRecovery;
    DateTime lastProgressNotice;
    public event Action Changed;
    public JObject Catalog => contracts.Catalog;
    public bool AutoPlaylist { get { lock (gate) return (bool)state["settings"]["autoPlaylist"]; } }
    public string[] GeneratedLevelIds
    {
        get { lock (gate) return Jobs.Cast<JObject>().Select(job => (string)job["levelId"]).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }
    public string[] Environments => host.EnvironmentNames.Length == 0 ? new[] { "DefaultEnvironment" } : host.EnvironmentNames;
    public JObject Snapshot
    {
        get
        {
            lock (gate)
            {
                var copy = (JObject)state.DeepClone();
                foreach (JObject job in (JArray)copy["jobs"])
                    job["stateLabel"] = ((bool)job["cancelRequested"] && !Terminal(job) && !string.IsNullOrEmpty((string)job["remoteId"])) ? "Cancellation requested; waiting for remote task" : contracts.StateLabel((string)job["state"]);
                if (storeFault != null) copy["notice"] = storeFault;
                return copy;
            }
        }
    }
    public BeatSageApp(string gameRoot, IGameHost host, IBeatSageClient client = null, Action<Exception> diagnostic = null)
    {
        root = Path.GetFullPath(gameRoot); this.host = host; this.diagnostic = diagnostic ?? (_ => { });
        main = SynchronizationContext.Current ?? new SynchronizationContext();
        dataRoot = Path.Combine(root, "UserData", "BeatSage");
        workRoot = Path.Combine(root, ".tmp", "BeatSage");
        statePath = Path.Combine(dataRoot, "state.json");
        Directory.CreateDirectory(dataRoot); Directory.CreateDirectory(workRoot);
        contracts = new ContractRegistry();
        state = contracts.DefaultState;
        state["installRoot"] = root;
        state["settings"]["lastDirectory"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (File.Exists(statePath))
        {
            try
            {
                state = JObject.Parse(File.ReadAllText(statePath, Encoding.UTF8));
                contracts.Validate("AppState", state);
                state["settings"] = contracts.NormalizeSettings((JObject)state["settings"]);
                // All artifact paths are owned by this installation; a moved installation needs explicit recovery.
                if (!String.Equals((string)state["installRoot"], root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The queue belongs to a different game folder; restore it in the original folder");
                Recover();
            }
            catch (Exception e)
            {
                this.diagnostic(e); storeFault = "Could not read the generation queue; check " + statePath + "; the original file was preserved";
                state = contracts.DefaultState; state["installRoot"] = root; state["paused"] = true;
            }
        }
        holdRecovery = !(bool)state["settings"]["resumeOnStart"];
        if (holdRecovery) state["paused"] = true;
        this.client = client ?? new BeatSageClient(contracts, () => Settings());
    }
    JObject Settings() { lock (gate) return (JObject)state["settings"].DeepClone(); }
    JArray Jobs => (JArray)state["jobs"];
    static S Status(JObject job) => (S)Enum.Parse(typeof(S), (string)job["state"]);
    static bool Terminal(JObject job) => new[] { S.Imported, S.Cancelled, S.FileError, S.Failed }.Contains(Status(job));
    static bool Ready(JObject job) => Status(job) == S.ReadyToImport || Status(job) == S.WaitingForLibrary;
    static string Stamp() => DateTime.UtcNow.ToString("O");
    void Recover()
    {
        foreach (JObject job in Jobs)
        {
            switch (Status(job))
            {
                case S.Uploading:
                    job["state"] = S.SubmissionUnknown.ToString(); job["errorCategory"] = "submission-unknown";
                    job["errorMessage"] = "The previous upload did not return a task ID; confirm resubmission"; state["paused"] = true; break;
                case S.Preflight: job["state"] = S.Queued.ToString(); break;
                case S.Downloading: job["state"] = S.RemotePending.ToString(); break;
                case S.Importing: job["state"] = S.ReadyToImport.ToString(); break;
            }
        }
    }
    public void Start()
    {
        lock (gate)
        {
            if (started || storeFault != null) return;
            started = true; Persist(); processor = ProcessLoop();
        }
        Signal();
    }
    void Signal() { if (!shutdown.IsCancellationRequested) wake.Release(); }
    void RequireWritable() { if (storeFault != null) throw new IOException(storeFault); shutdown.Token.ThrowIfCancellationRequested(); }
    void Persist()
    {
        RequireWritable();
        string json = contracts.SerializeState(state), temporary = statePath + ".new", backup = statePath + ".bak";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (File.Exists(statePath)) File.Replace(temporary, statePath, backup); else File.Move(temporary, statePath);
    }
    void Notice(string text) { lock (gate) { state["notice"] = text; Persist(); } Notify(); }
    internal void ReportNotice(string text) => Notice(text);
    void Notify()
    {
        main.Post(_ => { if (shutdown.IsCancellationRequested) return; try { Changed?.Invoke(); } catch (Exception e) { diagnostic(e); } }, null);
    }
    public Task<JArray> BrowseAsync(string path, CancellationToken token) => LocalMusic.BrowseAsync(path, Settings(), token);
    public Task<JArray> ScanAsync(IEnumerable<string> paths, bool recursive, CancellationToken token)
    {
        return LocalMusic.ScanAsync(paths, recursive, Settings(), token, song =>
        {
            token.ThrowIfCancellationRequested();
            try { CacheCover(song); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is FormatException)
            { song["error"] = "Cover read failed: " + e.Message; song["coverBase64"] = ""; diagnostic(e); }
        });
    }
    void CacheCover(JObject song)
    {
        string encoded = (string)song["coverBase64"], path = (string)song["coverPath"];
        if (string.IsNullOrEmpty(encoded) && string.IsNullOrEmpty(path)) return;
        byte[] bytes;
        if (!string.IsNullOrEmpty(encoded)) bytes = Convert.FromBase64String(encoded);
        else
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var memory = new MemoryStream())
            {
                if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("The cover exceeds 8 MiB");
                input.CopyTo(memory); bytes = memory.ToArray();
            }
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("The cover exceeds 8 MiB");
        bool png = bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpeg) throw new InvalidDataException("The cover must be a PNG or JPEG image");
        string directory = Path.Combine(dataRoot, "covers"); Directory.CreateDirectory(directory);
        string extension = png ? ".png" : ".jpg";
        string cached = Path.Combine(directory, Hash(bytes) + extension);
        lock (coverGate)
        {
            if (!File.Exists(cached))
            {
                string temporaryRoot = Path.Combine(workRoot, "covers"); Directory.CreateDirectory(temporaryRoot);
                string temporary = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N") + ".part");
                try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, cached); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        song["coverPath"] = cached; song["coverBase64"] = "";
    }
    public void SaveSettings(JObject settings)
    {
        settings = contracts.NormalizeSettings(settings); ValidateOptions((JObject)settings["defaultOptions"]);
        lock (gate) { RequireWritable(); state["settings"] = settings.DeepClone(); Persist(); }
        Notify();
    }
    public void SavePreset(string name, JObject options)
    {
        ValidateOptions(options);
        var preset = new JObject { ["name"] = name?.Trim(), ["options"] = options.DeepClone() };
        contracts.Validate("Preset", preset);
        lock (gate)
        {
            RequireWritable(); var presets = (JArray)state["presets"];
            presets.FirstOrDefault(p => (string)p["name"] == (string)preset["name"])?.Remove(); presets.Add(preset); Persist();
        }
        Notify();
    }
    public void DeletePreset(string name)
    {
        lock (gate) { RequireWritable(); ((JArray)state["presets"]).FirstOrDefault(p => (string)p["name"] == name)?.Remove(); Persist(); }
        Notify();
    }
    void ValidateOptions(JObject options)
    {
        contracts.Validate("GenerationOptions", options);
        if (!Environments.Contains((string)options["environment"])) throw new InvalidDataException("The selected environment is not available in this game");
    }
    public async Task EnqueueAsync(JArray songs, JObject options, bool force = false)
    {
        songs = (JArray)songs.DeepClone(); options = (JObject)options.DeepClone();
        ValidateOptions(options);
        var accepted = new List<JObject>(); int duplicates = 0;
        await Task.Run(() =>
        {
            foreach (JObject original in songs)
            {
                shutdown.Token.ThrowIfCancellationRequested();
                var song = (JObject)original.DeepClone(); contracts.Validate("AudioItem", song);
                if (!string.IsNullOrEmpty((string)song["error"])) continue;
                string id = Guid.NewGuid().ToString("N");
                var job = new JObject();
                foreach (string field in new[] { "id", "path", "title", "artist", "coverPath", "fileSha256", "signature", "remoteId", "remoteStage", "createdUtc", "updatedUtc", "errorCategory", "errorMessage", "detail", "zipPath", "folder", "levelId" }) job[field] = "";
                job["id"] = id; foreach (string field in new[] { "path", "title", "artist", "coverPath", "duration" }) job[field] = song[field].DeepClone();
                job["options"] = options.DeepClone(); job["fileSize"] = 0L; job["revision"] = 0L; job["attempt"] = 0L;
                job["progress"] = 0d; job["cancelRequested"] = false; job["force"] = force; job["expectedKeys"] = new JArray();
                job["state"] = S.Queued.ToString(); job["createdUtc"] = job["updatedUtc"] = Stamp();
                try
                {
                    CacheCover(song); job["coverPath"] = song["coverPath"].DeepClone();
                    var info = new FileInfo((string)job["path"]);
                    if (!info.Exists || info.Length == 0 || info.Length > 512L * 1024 * 1024) throw new InvalidDataException("The audio is missing, empty, or larger than 512 MiB");
                    if ((double)job["duration"] <= 0 || (double)job["duration"] >= 600) throw new InvalidDataException("Anonymous generation requires valid audio shorter than 10 minutes");
                    job["fileSha256"] = HashFile(info.FullName); job["fileSize"] = info.Length;
                    job["signature"] = Signature((string)job["fileSha256"], options);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                { job["state"] = S.FileError.ToString(); job["errorCategory"] = "file"; job["errorMessage"] = e.Message; diagnostic(e); }
                accepted.Add(job);
            }
        }, shutdown.Token).ConfigureAwait(false);
        lock (gate)
        {
            RequireWritable();
            foreach (var job in accepted)
            {
                if (!force && !string.IsNullOrEmpty((string)job["signature"]) && Jobs.Cast<JObject>().Any(j => (string)j["signature"] == (string)job["signature"]
                    && (!Terminal(j) || Status(j) == S.Imported))) { duplicates++; continue; }
                Jobs.Add(job);
            }
            state["notice"] = duplicates > 0
                ? "Added " + (accepted.Count - duplicates) + " songs; skipped " + duplicates + " duplicates"
                : "Added " + (accepted.Count - duplicates) + " songs"; Persist();
        }
        Notify(); Signal();
    }
    static string Signature(string hash, JObject options)
    {
        var normalized = (JObject)options.DeepClone();
        foreach (string property in new[] { "difficulties", "modes", "events" }) normalized[property] = new JArray(((JArray)normalized[property]).Select(v => (string)v).OrderBy(v => v, StringComparer.Ordinal));
        return Hash(Encoding.UTF8.GetBytes(hash + "\n" + normalized.ToString(Formatting.None)));
    }
    static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static string HashFile(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    public void Pause() { lock (gate) { RequireWritable(); state["paused"] = true; Persist(); } Notify(); }
    void RequireRetryWindow()
    {
        if (DateTimeOffset.TryParse((string)state["retryAfterUtc"], out var until) && until > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Rate limited; retry after " + until.ToLocalTime().ToString("HH:mm:ss"));
    }
    public void Resume() { lock (gate) { RequireWritable(); RequireRetryWindow(); state["paused"] = false; holdRecovery = false; Persist(); } Notify(); Signal(); }
    JObject Find(string id) => Jobs.Cast<JObject>().Single(j => (string)j["id"] == id);
    JObject ReadJob(string id) { lock (gate) return (JObject)Find(id).DeepClone(); }
    bool IsCancelled(string id) { lock (gate) return (bool)Find(id)["cancelRequested"]; }
    public void Cancel(string id)
    {
        lock (gate)
        {
            RequireWritable(); var job = Find(id); if (Terminal(job)) return;
            if (Status(job) == S.SubmissionUnknown) throw new InvalidOperationException("Submission result is unknown; keep the queue paused or confirm resubmission");
            if (Status(job) == S.WaitingForLibrary) throw new InvalidOperationException("The map package is saved and waiting for the library to refresh");
            job["cancelRequested"] = true;
            if (string.IsNullOrEmpty((string)job["remoteId"]) && activeId != id || Ready(job)) job["state"] = S.Cancelled.ToString();
            if (activeId == id && (string.IsNullOrEmpty((string)job["remoteId"]) || Status(job) == S.Downloading)) activeCancel?.Cancel();
            Touch(job); Persist();
        }
        Notify(); Signal();
    }
    public void Retry(string id, bool resubmit = false)
    {
        lock (gate)
        {
            RequireWritable(); RequireRetryWindow(); var job = Find(id);
            if (activeId == id) throw new InvalidOperationException("The task is still processing; try again later");
            if (Status(job) == S.SubmissionUnknown && !resubmit) throw new InvalidOperationException("Confirm resubmission first");
            if (!new[] { S.RetryNeeded, S.SubmissionUnknown, S.FileError, S.Failed, S.Cancelled, S.WaitingForLibrary }.Contains(Status(job))) return;
            if (Terminal(job) || string.IsNullOrEmpty((string)job["remoteId"]) || resubmit) job["cancelRequested"] = false;
            if (resubmit) job["remoteId"] = "";
            if (Status(job) == S.WaitingForLibrary) { importRequested = true; state["importBlocked"] = false; }
            else job["state"] = string.IsNullOrEmpty((string)job["remoteId"]) ? S.Queued.ToString() : S.RemotePending.ToString();
            job["errorMessage"] = job["errorCategory"] = ""; state["paused"] = false; holdRecovery = false; Touch(job); Persist();
        }
        Notify(); Signal();
    }
    public void Regenerate(string id)
    {
        JObject job; lock (gate) job = (JObject)Find(id).DeepClone();
        var song = new JObject { ["path"] = job["path"], ["title"] = job["title"], ["artist"] = job["artist"], ["duration"] = job["duration"], ["coverPath"] = job["coverPath"], ["coverBase64"] = "", ["coverMime"] = "", ["error"] = "" };
        Observe(EnqueueAsync(new JArray(song), (JObject)job["options"], true));
    }
    void Touch(JObject job) { job["revision"] = (long)job["revision"] + 1; job["updatedUtc"] = Stamp(); }
    bool Update(string id, long attempt, Action<JObject> update, bool persist = true)
    {
        lock (gate)
        {
            var job = Find(id); if ((long)job["attempt"] != attempt) return false;
            update(job); if (persist) { Touch(job); Persist(); }
        }
        if (persist || DateTime.UtcNow - lastProgressNotice > TimeSpan.FromMilliseconds(200)) { lastProgressNotice = DateTime.UtcNow; Notify(); }
        return true;
    }
    void Stage(string id, long attempt, S value, string detail = "") => Update(id, attempt, j => { j["state"] = value.ToString(); j["detail"] = detail; j["progress"] = 0d; j["errorMessage"] = j["errorCategory"] = ""; });
    void Fail(string id, long attempt, S value, string category, string message, bool pause)
    {
        Update(id, attempt, j => { j["state"] = value.ToString(); j["errorCategory"] = category; j["errorMessage"] = message; if (pause) state["paused"] = true; });
    }
    JObject Next()
    {
        lock (gate)
        {
            if (holdRecovery) return null;
            var blocking = Jobs.Cast<JObject>().FirstOrDefault(j => !Terminal(j) && !Ready(j) && Status(j) != S.Importing && (!string.IsNullOrEmpty((string)j["remoteId"]) || Status(j) == S.SubmissionUnknown));
            if (blocking != null) return new[] { S.RetryNeeded, S.SubmissionUnknown }.Contains(Status(blocking)) ? null : (JObject)blocking.DeepClone();
            if ((bool)state["paused"]) return null;
            return (JObject)Jobs.Cast<JObject>().FirstOrDefault(j => Status(j) == S.Queued)?.DeepClone();
        }
    }
    async Task ProcessLoop()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await wake.WaitAsync(shutdown.Token).ConfigureAwait(false);
                JObject job;
                while ((job = Next()) != null && !shutdown.IsCancellationRequested) await ProcessJob((string)job["id"]).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception e) { diagnostic(e); lock (gate) { state["paused"] = true; state["notice"] = "Queue paused: " + e.Message; if (!shutdown.IsCancellationRequested) Persist(); } Notify(); }
    }
    async Task ProcessJob(string id)
    {
        JObject job; long attempt;
        lock (gate)
        {
            job = Find(id); attempt = (long)job["attempt"] + 1; job["attempt"] = attempt;
            activeId = id; activeCancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token); Persist();
            job = (JObject)job.DeepClone();
        }
        var token = activeCancel.Token;
        try
        {
            if (string.IsNullOrEmpty((string)job["remoteId"]))
            {
                Stage(id, attempt, S.Preflight);
                var info = new FileInfo((string)job["path"]);
                if (!info.Exists || info.Length == 0 || info.Length > 512L * 1024 * 1024) throw new InvalidDataException("The audio cannot be read or exceeds 512 MiB");
                var current = await LocalMusic.ScanAsync(new[] { info.FullName }, false, Settings(), token).ConfigureAwait(false);
                var audio = current.Cast<JObject>().FirstOrDefault();
                if (audio == null || !string.IsNullOrEmpty((string)audio["error"]) || (double)audio["duration"] <= 0 || (double)audio["duration"] >= 600) throw new InvalidDataException(audio == null ? "Could not read the audio" : (string)audio["error"] == "" ? "Anonymous generation requires audio shorter than 10 minutes" : (string)audio["error"]);
                string hash = await Task.Run(() => HashFile(info.FullName), token).ConfigureAwait(false);
                if (hash != (string)job["fileSha256"])
                {
                    lock (gate)
                    {
                        string signature = Signature(hash, (JObject)job["options"]);
                        if (!(bool)job["force"] && Jobs.Cast<JObject>().Any(j => (string)j["id"] != id && (string)j["signature"] == signature && (!Terminal(j) || Status(j) == S.Imported)))
                        { Stage(id, attempt, S.Cancelled, "Skipped because the changed audio duplicates an existing task"); return; }
                        Update(id, attempt, j => { j["fileSha256"] = hash; j["fileSize"] = info.Length; j["duration"] = audio["duration"]; j["signature"] = signature; });
                        job["fileSha256"] = hash;
                    }
                }
                token.ThrowIfCancellationRequested();
                Stage(id, attempt, S.Uploading);
                var options = (JObject)job["options"];
                var request = new JObject { ["audioFile"] = job["path"], ["audioFileSha256"] = job["fileSha256"], ["audio_metadata_title"] = job["title"], ["audio_metadata_artist"] = job["artist"], ["difficulties"] = options["difficulties"].DeepClone(), ["modes"] = options["modes"].DeepClone(), ["events"] = options["events"].DeepClone(), ["environment"] = options["environment"], ["system_tag"] = options["model"], ["coverFile"] = job["coverPath"] };
                string remoteId = await client.CreateAsync(request, token, p => Update(id, attempt, j => j["progress"] = Math.Min(1d, Math.Max(0d, p)), false)).ConfigureAwait(false);
                Update(id, attempt, j => { j["remoteId"] = remoteId; j["state"] = S.RemotePending.ToString(); });
                job["remoteId"] = remoteId;
            }
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes((int)Settings()["generationMinutes"]));
                while (true)
                {
                    string remote = await client.GetStatusAsync((string)job["remoteId"], deadline.Token).ConfigureAwait(false);
                    Update(id, attempt, j => j["remoteStage"] = remote);
                    if (remote == "DONE") break;
                    if (new[] { "FAILED", "ERROR", "CANCELLED", "EXPIRED" }.Contains(remote))
                    { Stage(id, attempt, IsCancelled(id) ? S.Cancelled : S.Failed, "Remote task ended: " + remote); return; }
                    S next;
                    switch (remote) { case "PENDING": next = S.RemotePending; break; case "LOADING_AUDIO": next = S.LoadingAudio; break; case "CREATING_LEVEL": next = S.CreatingLevel; break; case "FORMATTING_LEVEL": next = S.FormattingLevel; break; default: throw new ServiceFault("protocol", "Unknown remote state: " + remote); }
                    Stage(id, attempt, next);
                    await Task.Delay(TimeSpan.FromSeconds((int)Settings()["pollSeconds"]), deadline.Token).ConfigureAwait(false);
                }
            }
            if (IsCancelled(id)) { Stage(id, attempt, S.Cancelled); return; }
            Stage(id, attempt, S.Downloading);
            string downloadRoot = Path.Combine(workRoot, "downloads"); Directory.CreateDirectory(downloadRoot);
            string part = Path.Combine(downloadRoot, id + ".part");
            await client.DownloadAsync((string)job["remoteId"], part, token, p => Update(id, attempt, j => j["progress"] = Math.Min(1d, Math.Max(0d, p)), false)).ConfigureAwait(false);
            var manifest = InspectPackage(part, (JObject)job["options"]);
            string destinationRoot = (bool)Settings()["retainZip"] ? Path.Combine(root, "releases", "BeatSage", "generated") : downloadRoot;
            Directory.CreateDirectory(destinationRoot); string zip = Path.Combine(destinationRoot, id + ".zip");
            if (File.Exists(zip))
            {
                if (HashFile(zip) != HashFile(part)) throw new IOException("A different map package already exists for this task");
                File.Delete(part);
            }
            else File.Move(part, zip);
            if ((bool)Settings()["retainZip"]) File.WriteAllText(zip + ".sha256", HashFile(zip) + "  " + Path.GetFileName(zip) + "\n", Encoding.ASCII);
            Update(id, attempt, j => { j["zipPath"] = zip; j["expectedKeys"] = manifest.keys; j["levelId"] = manifest.levelId; j["state"] = S.ReadyToImport.ToString(); j["progress"] = 1d; });
        }
        catch (ServiceFault e)
        {
            diagnostic(e);
            if (e.RetryAfterUtc.HasValue) { lock (gate) state["retryAfterUtc"] = e.RetryAfterUtc.Value.ToString("O"); }
            Fail(id, attempt, e.AmbiguousSubmission ? S.SubmissionUnknown : e.Category == "remote-not-found" ? S.Failed : S.RetryNeeded, e.Category, e.Message, e.Category != "remote-not-found");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (IsCancelled(id) && (string.IsNullOrEmpty((string)job["remoteId"]) || (string)ReadJob(id)["remoteStage"] == "DONE")) Stage(id, attempt, S.Cancelled);
            else Fail(id, attempt, S.RetryNeeded, "timeout", "The wait timed out; you can continue querying this task", true);
        }
        catch (Exception e)
        {
            diagnostic(e);
            if (Status(ReadJob(id)) == S.Uploading)
            { Fail(id, attempt, S.SubmissionUnknown, "submission-unknown", "Submission result is unknown: " + e.Message, true); return; }
            bool local = Status(ReadJob(id)) == S.Preflight || string.IsNullOrEmpty((string)job["remoteId"]);
            Fail(id, attempt, local ? S.FileError : S.RetryNeeded, local ? "file" : "download", e.Message, !local);
        }
        finally { lock (gate) { activeId = null; activeCancel.Dispose(); activeCancel = null; } }
    }
    static (JArray keys, string levelId) InspectPackage(string path, JObject options)
    {
        using (var zip = ZipFile.OpenRead(path))
        {
            if (zip.Entries.Count > PackageImporter.MaxEntryCount || zip.Entries.Sum(e => e.Length) > PackageImporter.MaxTotalBytes
                || zip.Entries.GroupBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidDataException("The map package has too many entries, duplicate entries, or exceeds the unpacked size limit");
            var infoEntry = zip.Entries.Single(e => e.FullName.Equals("Info.dat", StringComparison.OrdinalIgnoreCase));
            if (infoEntry.Length > 1024 * 1024) throw new InvalidDataException("The map package information file is too large");
            byte[] infoBytes;
            using (var input = infoEntry.Open()) using (var memory = new MemoryStream())
            { var block = new byte[8192]; int count; while ((count = input.Read(block, 0, block.Length)) != 0) { if (memory.Length + count > 1024 * 1024) throw new InvalidDataException("The map package information file is too large"); memory.Write(block, 0, count); } infoBytes = memory.ToArray(); }
            var info = JObject.Parse(Encoding.UTF8.GetString(infoBytes));
            var sets = info["_difficultyBeatmapSets"] as JArray ?? throw new InvalidDataException("The Beat Sage map package format is not recognized");
            var keys = new JArray(); var files = new List<ZipArchiveEntry>();
            if (sets.Count > 4) throw new InvalidDataException("The map package has too many modes");
            foreach (JObject set in sets)
            {
                var difficulties = set["_difficultyBeatmaps"] as JArray ?? throw new InvalidDataException("The map package is missing its difficulty list");
                if (difficulties.Count > 4) throw new InvalidDataException("The map package has too many difficulties");
                foreach (JObject difficulty in difficulties)
                {
                    keys.Add((string)set["_beatmapCharacteristicName"] + "|" + (string)difficulty["_difficulty"]);
                    var entry = zip.Entries.Single(e => e.FullName == (string)difficulty["_beatmapFilename"]);
                    if (entry.Length > 8L * 1024 * 1024) throw new InvalidDataException("A beatmap file is too large");
                    files.Add(entry);
                }
            }
            if (files.Sum(e => e.Length) > 64L * 1024 * 1024) throw new InvalidDataException("The beatmap data exceeds the size limit");
            var expected = new HashSet<string>(((JArray)options["modes"]).SelectMany(m => ((JArray)options["difficulties"]).Select(d => (string)m + "|" + (string)d)));
            if (keys.Distinct().Count() != keys.Count || !expected.SetEquals(keys.Select(k => (string)k))) throw new InvalidDataException("The generated map package does not match the selected modes or difficulties");
            using (var sha = SHA1.Create())
            {
                sha.TransformBlock(infoBytes, 0, infoBytes.Length, null, 0);
                var buffer = new byte[65536];
                long total = 0;
                foreach (var file in files)
                    using (var input = file.Open())
                    { int read; long size = 0; while ((read = input.Read(buffer, 0, buffer.Length)) != 0) { size += read; total += read; if (size > 8L * 1024 * 1024 || total > 64L * 1024 * 1024) throw new InvalidDataException("The beatmap data exceeds the size limit"); sha.TransformBlock(buffer, 0, read, null, 0); } }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return (keys, "custom_level_" + BitConverter.ToString(sha.Hash).Replace("-", ""));
            }
        }
    }
    public void Tick(bool gameplay = false)
    {
        if (shutdown.IsCancellationRequested || storeFault != null) return;
        bool menu = host.CanImport;
        // SongCore becomes busy during our own refresh. Only gameplay cancels the
        // current import; a busy loader merely prevents starting another batch.
        if (!menu) { if (gameplay) importCancel?.Cancel(); return; }
        lock (gate)
        {
            if (!importer.IsCompleted || !Jobs.Cast<JObject>().Any(Ready)) return;
            if ((bool)state["importBlocked"] && !importRequested) return;
            if (!(bool)state["settings"]["autoImport"] && !importRequested) return;
            importRequested = false; importCancel?.Dispose(); importCancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            importer = ImportBatch(importCancel.Token);
        }
    }
    public void ImportReady() { lock (gate) { RequireWritable(); importRequested = true; state["importBlocked"] = false; Persist(); } }
    async Task ImportBatch(CancellationToken token)
    {
        List<JObject> batch; lock (gate) batch = Jobs.Cast<JObject>().Where(Ready).Select(j => (JObject)j.DeepClone()).ToList();
        try
        {
            foreach (var job in batch)
            {
                if (Terminal(ReadJob((string)job["id"]))) continue;
                string id = (string)job["id"]; long attempt = (long)job["attempt"];
                if (Status(job) == S.WaitingForLibrary && Directory.Exists((string)job["folder"])) continue;
                Stage(id, attempt, S.Importing);
                string name = "BeatSage_" + SafeName((string)job["title"]) + "_" + id;
                using (var prepared = await Task.Run(() => PackageImporter.Prepare((string)job["zipPath"], Path.Combine(root, "Beat Saber_Data", "CustomLevels"), name, Path.Combine(workRoot, "imports", id), token), token).ConfigureAwait(false))
                {
                    await OnMain(() =>
                    {
                        lock (gate)
                        {
                            if (!host.CanImport || IsCancelled(id)) throw new OperationCanceledException(token);
                            string folder = prepared.Publish(token);
                            Update(id, attempt, j => { j["folder"] = folder; j["state"] = S.WaitingForLibrary.ToString(); });
                            return folder;
                        }
                    }, token).ConfigureAwait(false);
                }
            }
            await OnMainAsync(() => host.RefreshAsync(token), token).ConfigureAwait(false);
            foreach (var job in batch)
            {
                if (Terminal(ReadJob((string)job["id"]))) continue;
                bool loaded = await OnMain(() => host.IsLoaded((string)job["levelId"], (JArray)job["expectedKeys"]), token).ConfigureAwait(false);
                if (!loaded) throw new InvalidDataException("The map package is saved, but SongCore did not load every difficulty; retry the import");
                Update((string)job["id"], (long)job["attempt"], j => { j["state"] = S.Imported.ToString(); j["progress"] = 1d; });
                if (!(bool)Settings()["retainZip"] && IsWithin((string)job["zipPath"], workRoot))
                { File.Delete((string)job["zipPath"]); Update((string)job["id"], (long)job["attempt"], j => j["zipPath"] = ""); }
            }
            Notice("Imported " + batch.Count + " songs");
        }
        catch (OperationCanceledException)
        {
            foreach (var job in batch.Select(j => ReadJob((string)j["id"])).Where(j => Status(j) == S.Importing))
                if (!shutdown.IsCancellationRequested) Stage((string)job["id"], (long)job["attempt"], (bool)job["cancelRequested"] ? S.Cancelled : S.ReadyToImport);
        }
        catch (Exception e)
        {
            diagnostic(e);
            lock (gate)
            {
                foreach (var job in batch.Select(j => Find((string)j["id"])).Where(j => !Terminal(j)))
                { job["state"] = Directory.Exists((string)job["folder"]) ? S.WaitingForLibrary.ToString() : S.ReadyToImport.ToString(); job["errorCategory"] = "import"; job["errorMessage"] = e.Message; Touch(job); }
                // An import error requires explicit retry instead of a frame-by-frame retry loop.
                state["importBlocked"] = true; state["notice"] = "Import incomplete: " + e.Message; if (!shutdown.IsCancellationRequested) Persist();
            }
            Notify();
        }
    }
    static bool IsWithin(string path, string parent) => !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static string SafeName(string value) => new string((value ?? "song").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).Take(48).ToArray()).Trim().TrimEnd('.');
    Task<T> OnMain<T>(Func<T> action, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = token.Register(() => tcs.TrySetCanceled());
        main.Post(_ => { try { if (!token.IsCancellationRequested) tcs.TrySetResult(action()); } catch (Exception e) { tcs.TrySetException(e); } finally { registration.Dispose(); } }, null);
        return tcs.Task;
    }
    async Task OnMainAsync(Func<Task> action, CancellationToken token) { var task = await OnMain(action, token).ConfigureAwait(false); await task.ConfigureAwait(false); }
    public void ShowSong(string id)
    {
        JObject job = ReadJob(id);
        if (Status(job) != S.Imported) throw new InvalidOperationException("The song is not loaded in the library");
        host.ShowSong((string)job["levelId"]);
    }
    void Observe(Task task) => task.ContinueWith(t => { if (t.Exception != null) { diagnostic(t.Exception); if (!shutdown.IsCancellationRequested) Notice(t.Exception.GetBaseException().Message); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public void Dispose()
    {
        lock (gate)
        {
            if (shutdown.IsCancellationRequested) return;
            if (storeFault == null) Persist(); shutdown.Cancel(); importCancel?.Cancel(); activeCancel?.Cancel();
        }
        client.Dispose();
        try { if (!Task.WhenAll(processor, importer).Wait(TimeSpan.FromSeconds(3))) diagnostic(new TimeoutException("The generation service shutdown timed out; recovery information was saved")); }
        catch (AggregateException e) { if (e.Flatten().InnerExceptions.Any(x => !(x is OperationCanceledException))) diagnostic(e); }
        Changed = null;
    }
}
