using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeatSage;
using BeatSage.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeatSage.Core.Tests;

internal static class CoreTests
{
    private static int assertions;

    public static int Main()
    {
        Run("duplicate input snapshots options", TestDuplicateInputSnapshots);
        Run("playlist setting upgrades and persists existing queues", TestPlaylistSettings);
        Run("ambiguous submission pauses without repeat", TestAmbiguousSubmission);
        Run("known remote ID resumes polling", TestKnownRemoteIdRecovery);
        Run("remote cancellation waits for terminal status", TestRemoteCancellation);
        Run("file errors do not block following songs", TestFileErrorsDoNotBlock);
        Run("corrupt state is preserved", TestCorruptStatePreserved);
        Run("deferred import resumes when the host is ready", TestDeferredImport);
        Run("v1 incompatible options are rejected", TestIncompatibleV1Options);
        Run("HTTP create does not retry and sends multipart fields", TestHttpCreate);
        Run("HTTP create survives handler content disposal", TestHttpCreateContentDisposal);
        Run("HTTP error remains submission-unknown", TestHttpErrorSubmissionUnknown);
        Run("HTTP timeout remains submission-unknown", TestHttpTimeoutSubmissionUnknown);
        Run("HTTP Retry-After returns an immediate recoverable fault", TestHttpRetryAfter);
        Run("localization bridge preserves raw arguments", TestLocalizationBridge);
        Run("localization bridge diagnoses reflection failures", TestLocalizationBridgeDiagnostics);
        Run("production localization sources are English", TestEnglishSources);
        Console.WriteLine("Beat Sage core tests passed (" + assertions + " assertions).");
        return 0;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + name + ": " + exception);
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static void TestPlaylistSettings()
    {
        var root = NewRoot();
        try
        {
            var registry = new ContractRegistry();
            var legacy = registry.DefaultState;
            legacy["installRoot"] = root;
            ((JObject)legacy["settings"]).Remove("autoPlaylist");
            legacy["notice"] = "Existing queue";
            var directory = Path.Combine(root, "UserData", "BeatSage");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json"), legacy.ToString());
            using (var app = NewApp(root, new FakeHost(), new FakeClient()))
            {
                Assert(app.AutoPlaylist, "existing config enables playlist backfill by default");
                Assert((string)app.Snapshot["notice"] == "Existing queue", "existing state is preserved");
                var settings = (JObject)app.Snapshot["settings"];
                settings["autoPlaylist"] = false;
                app.SaveSettings(settings);
                Assert(!app.AutoPlaylist, "playlist grouping can be disabled");
            }
            using (var app = NewApp(root, new FakeHost(), new FakeClient()))
                Assert(!app.AutoPlaylist, "disabled playlist grouping survives restart");
            var invalid = (JObject)registry.DefaultState["settings"];
            invalid["autoPlaylist"] = "true";
            bool rejected = false;
            try { registry.NormalizeSettings(invalid); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "playlist config is validated at the persistence boundary");
        }
        finally { DeleteRoot(root); }
    }

    private static void TestLocalizationBridge()
    {
        var enabled = true;
        var disabled = false;
        var custom = false;
        LocalizationBridge.SetTestTranslator((source, scope) =>
        {
            if (!enabled || disabled) return source;
            if (custom && source == "Song {0} by {1}") return "自定义 {0} / {1}";
            return source == "Song {0}" ? "歌曲 {0}" : source;
        });
        try
        {
            Assert(LocalizationBridge.Format("Song {0}", "Expert") == "歌曲 Expert", "translated templates keep raw arguments");
            disabled = true;
            Assert(LocalizationBridge.Format("Song {0}", "Expert") == "Song Expert", "disabled scope returns English source");
            disabled = false; custom = true;
            Assert(LocalizationBridge.Format("Song {0} by {1}", "Expert", "User/Path") == "自定义 Expert / User/Path", "custom override keeps raw title and path");
            Assert(LocalizationBridge.LegacyToSource("已加入 2 首，跳过 1 首重复歌曲") == "Added 2 songs; skipped 1 duplicates", "legacy notices migrate to stable English templates");
            enabled = false; custom = false;
            Assert(LocalizationBridge.T("Settings") == "Settings", "global disable returns English source");
        }
        finally { LocalizationBridge.SetTestTranslator(null); }
    }

    private static void TestLocalizationBridgeDiagnostics()
    {
        var diagnostics = new List<string>();
        LocalizationBridge.SetTestDiagnostic(diagnostics.Add);
        LocalizationBridge.SetTestTranslator(null);
        BeatSage.Plugin.Log.Messages.Clear();
        ModUiChinese.UiTranslator.Engine = new ModUiChinese.ThrowingEngine();
        BeatSage.UI.BeatSageViewController.ThrowOnLocalizationReload = true;
        try
        {
            Assert(LocalizationBridge.T("Reflection failure") == "Reflection failure", "reflection failure keeps the English source");
            Assert(LocalizationBridge.T("Reflection failure") == "Reflection failure", "repeated reflection failure keeps the English source");
            Assert(diagnostics.Count == 1 && diagnostics[0].IndexOf("Translate", StringComparison.OrdinalIgnoreCase) >= 0, "translation reflection failure is diagnosed once");

            LocalizationBridge.OnLocalizationReloaded();
            LocalizationBridge.OnLocalizationReloaded();
            Assert(diagnostics.Count == 2 && diagnostics[1].IndexOf("OnLocalizationReloaded", StringComparison.Ordinal) >= 0, "refresh failure is diagnosed once");
            Assert(BeatSage.Plugin.Log.Messages.Count == 2, "diagnostics are written through Plugin.Log once per failure");

            ModUiChinese.UiTranslator.Engine = new ModUiChinese.WorkingEngine();
            Assert(LocalizationBridge.T("Settings") == "translated Settings", "working reflection engine still translates after a failure");
        }
        finally
        {
            BeatSage.UI.BeatSageViewController.ThrowOnLocalizationReload = false;
            ModUiChinese.UiTranslator.Engine = null;
            LocalizationBridge.SetTestTranslator(null);
            LocalizationBridge.SetTestDiagnostic(null);
        }
    }

    private static void TestEnglishSources()
    {
        var root = FindSourceRoot();
        foreach (var relative in new[]
        {
            "scripts/beat_sage/UI/BeatSageView.bsml",
            "scripts/beat_sage/UI/BeatSageText.cs",
            "scripts/beat_sage/UI/BeatSageViewController.cs",
            "scripts/beat_sage/UI/BeatSageFlowCoordinator.cs",
            "scripts/beat_sage/UI/Menu.cs",
            "scripts/beat_sage/BeatSageApp.cs",
            "scripts/beat_sage/BeatSageProtocol.cs",
            "scripts/beat_sage/Plugin.cs",
            "scripts/beat_sage/Contracts/contracts.schema.json"
        })
        {
            var text = File.ReadAllText(Path.Combine(root, relative), Encoding.UTF8);
            Assert(!ContainsHan(text), "source is English: " + relative);
        }

        var schema = JObject.Parse(File.ReadAllText(Path.Combine(root, "scripts/beat_sage/Contracts/contracts.schema.json"), Encoding.UTF8));
        var expected = new Dictionary<string, string[]>
        {
            ["Difficulty"] = new[] { "Normal", "Hard", "Expert", "ExpertPlus" },
            ["Mode"] = new[] { "Standard", "NoArrows", "OneSaber", "90Degree" },
            ["Model"] = new[] { "v2", "v2-flow", "v1" },
            ["SongEvent"] = new[] { "Bombs", "DotBlocks", "Obstacles" },
            ["JobState"] = new[] { "Queued", "Preflight", "Uploading", "SubmissionUnknown", "RemotePending", "LoadingAudio", "CreatingLevel", "FormattingLevel", "Downloading", "ReadyToImport", "Importing", "WaitingForLibrary", "Imported", "Cancelled", "RetryNeeded", "FileError", "Failed" }
        };
        foreach (var pair in expected)
        {
            var definition = (JObject)schema["definitions"][pair.Key];
            var values = ((JArray)definition["enum"]).Values<string>().ToArray();
            Assert(values.SequenceEqual(pair.Value), "contract enum IDs stay stable: " + pair.Key);
            Assert(((JArray)definition["x-labels"]).Values<string>().All(value => !ContainsHan(value)), "contract labels are English: " + pair.Key);
        }
    }

    private static string FindSourceRoot()
    {
        var configured = Environment.GetEnvironmentVariable("BEATSAGE_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "scripts", "beat_sage", "BeatSage.csproj"))) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "beat_sage", "BeatSage.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Beat Sage source root was not found");
    }

    private static bool ContainsHan(string text)
    {
        return text.Any(character => character >= '\u3400' && character <= '\u9fff');
    }

    private static void TestDuplicateInputSnapshots()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "snapshot.wav");
            var host = new FakeHost { CanImportValue = false };
            var client = new FakeClient();
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                var options = Options();
                var song = Audio(audio, "Original title", "Original artist");
                var savedOptions = (JObject)options.DeepClone();
                var savedSong = (JObject)song.DeepClone();
                app.EnqueueAsync(new JArray(song), options).GetAwaiter().GetResult();
                song["title"] = "Mutated title";
                options["environment"] = "Mutated environment";
                WaitFor(app, job => (string)job["state"] == "ReadyToImport");
                var job = FirstJob(app);
                Assert((string)job["title"] == "Original title", "queued title is a deep snapshot");
                Assert((string)job["options"]["environment"] == "DefaultEnvironment", "queued options are a deep snapshot");
                app.EnqueueAsync(new JArray(savedSong), savedOptions).GetAwaiter().GetResult();
                Assert(((JArray)app.Snapshot["jobs"]).Count == 1, "same file and options are de-duplicated");
                var forcedOptions = (JObject)savedOptions.DeepClone();
                forcedOptions["difficulties"] = new JArray("Hard");
                app.EnqueueAsync(new JArray(savedSong), forcedOptions, true).GetAwaiter().GetResult();
                var jobs = (JArray)app.Snapshot["jobs"];
                Assert(jobs.Count == 2, "force regeneration creates an independent job");
                var forcedJob = jobs.Cast<JObject>().Single(item => (string)item["id"] != (string)job["id"]);
                Assert((string)forcedJob["id"] != (string)job["id"], "forced job receives a distinct ID");
                Assert((bool)forcedJob["force"], "force intent is persisted on the regenerated job");
                Assert((string)((JArray)forcedJob["options"]["difficulties"])[0] == "Hard", "forced job snapshots its options independently");
                forcedOptions["difficulties"] = new JArray("Expert");
                savedSong["title"] = "Mutated after force enqueue";
                Assert((string)((JArray)forcedJob["options"]["difficulties"])[0] == "Hard" && (string)forcedJob["title"] == "Original title", "forced job does not retain mutable enqueue inputs");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestAmbiguousSubmission()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "ambiguous.wav");
            var host = new FakeHost { CanImportValue = false };
            var client = new FakeClient { CreateFailure = new ServiceFault("submission-unknown", "unknown", false, true) };
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                EnqueueOne(app, audio, "Ambiguous");
                var job = WaitFor(app, item => (string)item["state"] == "SubmissionUnknown");
                Assert((bool)app.Snapshot["paused"], "ambiguous submission pauses the queue");
                Thread.Sleep(250);
                Assert(client.CreateCalls == 1, "ambiguous submission is not repeated automatically");
                Assert((string)job["errorCategory"] == "submission-unknown", "ambiguous category is persisted");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestKnownRemoteIdRecovery()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "recovery.wav");
            var host = new FakeHost { CanImportValue = false };
            var firstClient = new FakeClient { CreateFailure = new ServiceFault("submission-unknown", "unknown", false, true) };
            using (var first = NewApp(root, host, firstClient))
            {
                first.Start();
                EnqueueOne(first, audio, "Recovery");
                WaitFor(first, item => (string)item["state"] == "SubmissionUnknown");
            }

            var statePath = Path.Combine(root, "UserData", "BeatSage", "state.json");
            var state = JObject.Parse(File.ReadAllText(statePath, Encoding.UTF8));
            var job = (JObject)((JArray)state["jobs"])[0];
            job["remoteId"] = "known-id";
            job["state"] = "RemotePending";
            job["cancelRequested"] = false;
            state["paused"] = false;
            File.WriteAllText(statePath, state.ToString(Formatting.Indented), new UTF8Encoding(false));

            var secondClient = new FakeClient { RemoteId = "known-id" };
            using (var second = NewApp(root, host, secondClient))
            {
                second.Start();
                WaitFor(second, item => (string)item["state"] == "ReadyToImport");
                Assert(secondClient.CreateCalls == 0, "known remote ID does not upload again");
                Assert(secondClient.GetCalls > 0, "known remote ID resumes status polling");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestRemoteCancellation()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "cancel.wav");
            var host = new FakeHost { CanImportValue = false };
            var client = new FakeClient();
            client.Status = call => call == 1 ? "PENDING" : "DONE";
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                var id = EnqueueOne(app, audio, "Cancel");
                WaitFor(app, item => (string)item["state"] == "RemotePending");
                app.Cancel(id);
                WaitFor(app, item => (string)item["state"] == "Cancelled", 8000);
                Assert(client.GetCalls >= 2, "cancellation keeps polling until the remote terminal state");
                Assert(client.DownloadCalls == 0, "cancelled remote job is not downloaded");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestFileErrorsDoNotBlock()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "following.wav");
            var host = new FakeHost { CanImportValue = false };
            var client = new FakeClient();
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                var invalid = Audio(Path.Combine(root, "missing.wav"), "Missing", "");
                var valid = Audio(audio, "Following", "");
                app.EnqueueAsync(new JArray(invalid, valid), Options()).GetAwaiter().GetResult();
                WaitFor(app, item => (string)item["title"] == "Following" && (string)item["state"] == "ReadyToImport");
                var jobs = (JArray)app.Snapshot["jobs"];
                Assert(jobs.Any(item => (string)item["title"] == "Missing" && (string)item["state"] == "FileError"), "invalid file is recorded as a file error");
                Assert(client.CreateCalls == 1, "a file error does not stop the following valid song");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestCorruptStatePreserved()
    {
        var root = NewRoot();
        try
        {
            var stateDirectory = Directory.CreateDirectory(Path.Combine(root, "UserData", "BeatSage"));
            var statePath = Path.Combine(stateDirectory.FullName, "state.json");
            const string corrupt = "{ this is not state";
            File.WriteAllText(statePath, corrupt, Encoding.UTF8);
            var host = new FakeHost();
            using (var app = NewApp(root, host, new FakeClient()))
            {
                Assert((bool)app.Snapshot["paused"], "corrupt state pauses recovery");
            }
            Assert(File.ReadAllText(statePath, Encoding.UTF8) == corrupt, "corrupt state is preserved for diagnosis");
        }
        finally { DeleteRoot(root); }
    }

    private static void TestDeferredImport()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "deferred.wav");
            var host = new FakeHost { CanImportValue = false };
            var client = new FakeClient();
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                var id = EnqueueOne(app, audio, "Deferred");
                WaitFor(app, item => (string)item["state"] == "ReadyToImport");
                app.Tick();
                Assert((string)FirstJob(app)["state"] == "ReadyToImport", "import waits while the host cannot import");
                host.CanImportValue = true;
                app.Tick();
                var imported = WaitFor(app, item => (string)item["state"] == "Imported", 8000);
                Assert(Directory.Exists((string)imported["folder"]), "deferred import publishes the staged package");
                Assert(client.DownloadCalls == 1, "a real ZIP artifact was supplied to the importer");
                Assert((string)FirstJob(app)["id"] == id, "imported job identity is preserved");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestIncompatibleV1Options()
    {
        var root = NewRoot();
        try
        {
            var host = new FakeHost { CanImportValue = false };
            using (var app = NewApp(root, host, new FakeClient()))
            {
                app.Start();
                var invalid = Options();
                invalid["model"] = "v1";
                invalid["events"] = new JArray("Obstacles");
                var rejected = false;
                try { app.EnqueueAsync(new JArray(), invalid).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "v1 options reject obstacles at the contract boundary");
                Assert(((JArray)app.Snapshot["jobs"]).Count == 0, "rejected options do not create a job");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestHttpCreate()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "http-create.wav");
            var handler = new RecordingHandler((_, request) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{}")
            });
            var registry = new ContractRegistry();
            var request = CreateRequest(audio, "HTTP title", "HTTP artist");
            ServiceFault fault = null;
            using (var client = new BeatSageClient(registry, () => (JObject)registry.DefaultState["settings"].DeepClone(), handler))
            {
                try { client.CreateAsync(request, CancellationToken.None, null).GetAwaiter().GetResult(); }
                catch (ServiceFault exception) { fault = exception; }
            }

            Assert(fault != null && fault.AmbiguousSubmission, "HTTP 500 upload is submission-unknown");
            Assert(handler.RequestCount == 1, "upload failure is not automatically retried");
            Assert(handler.LastMethod == HttpMethod.Post, "create uses POST");
            Assert(handler.LastBody.IndexOf("name=audio_file", StringComparison.Ordinal) >= 0, "multipart audio field is present");
            Assert(handler.LastBody.IndexOf("name=audio_metadata_title", StringComparison.Ordinal) >= 0, "multipart title field is present");
            Assert(handler.LastBody.IndexOf("name=audio_metadata_artist", StringComparison.Ordinal) >= 0, "multipart artist field is present");
            Assert(handler.LastBody.IndexOf("audioFileSha256", StringComparison.Ordinal) < 0, "local hash field is not uploaded");
        }
        finally { DeleteRoot(root); }
    }

    private static void TestHttpRetryAfter()
    {
        var handler = new RecordingHandler((_, request) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        });
        var registry = new ContractRegistry();
        ServiceFault fault = null;
        using (var client = new BeatSageClient(registry, () => (JObject)registry.DefaultState["settings"].DeepClone(), handler))
        {
            try { client.GetStatusAsync("retry-id", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (ServiceFault exception) { fault = exception; }
        }

        Assert(fault != null && fault.Retryable, "Retry-After remains recoverable");
        Assert(handler.RequestCount == 1, "Retry-After response pauses without an immediate second request");
        var property = fault.GetType().GetProperty("RetryAfterUtc");
        Assert(property != null, "ServiceFault exposes RetryAfterUtc");
        var retryAfter = property.GetValue(fault, null);
        Assert(retryAfter != null, "Retry-After is preserved on ServiceFault");
    }

    private static void TestHttpCreateContentDisposal()
    {
        var root = NewRoot();
        try
        {
            var audio = CreateWav(root, "http-dispose.wav");
            var handler = new MultipartDisposingHandler();
            var registry = new ContractRegistry();
            var request = CreateRequest(audio, "Dispose title", "Dispose artist");
            string remoteId;
            using (var client = new BeatSageClient(registry, () => (JObject)registry.DefaultState["settings"].DeepClone(), handler))
                remoteId = client.CreateAsync(request, CancellationToken.None, null).GetAwaiter().GetResult();

            Assert(remoteId == "created-after-dispose", "multipart disposal does not hide the create response ID");
            Assert(handler.RequestCount == 1, "multipart disposal handler received one request");
            Assert(handler.ContentConsumed && handler.LastBody.IndexOf("audio_file", StringComparison.Ordinal) >= 0, "handler consumed the complete multipart body");
        }
        finally { DeleteRoot(root); }
    }

    private static void TestHttpTimeoutSubmissionUnknown()
    {
        var root = NewRoot();
        try
        {
            var firstAudio = CreateWav(root, "http-timeout-first.wav");
            var secondAudio = CreateWav(root, "http-timeout-second.wav");
            var secondBytes = File.ReadAllBytes(secondAudio);
            secondBytes[secondBytes.Length - 1] = 1;
            File.WriteAllBytes(secondAudio, secondBytes);
            var host = new FakeHost { CanImportValue = false };
            var handler = new TimeoutHandler();
            var registry = new ContractRegistry();
            using (var client = new BeatSageClient(registry, () => (JObject)registry.DefaultState["settings"].DeepClone(), handler))
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                app.EnqueueAsync(new JArray(Audio(firstAudio, "Timeout first", "Artist"), Audio(secondAudio, "Timeout second", "Artist")), Options()).GetAwaiter().GetResult();
                var first = WaitFor(app, item => (string)item["title"] == "Timeout first" && (string)item["state"] == "SubmissionUnknown");
                var jobs = (JArray)app.Snapshot["jobs"];
                var second = jobs.Cast<JObject>().Single(item => (string)item["title"] == "Timeout second");
                Assert((string)first["errorCategory"] == "submission-unknown", "HTTP timeout keeps the stable submission-unknown category");
                Assert(!jobs.Any(item => (string)item["state"] == "FileError"), "HTTP timeout is not converted into a file error");
                Assert((string)second["state"] == "Queued", "unknown upload result pauses before following songs");
                Assert(handler.RequestCount == 1, "unknown upload result is not automatically retried");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static void TestHttpErrorSubmissionUnknown()
    {
        var root = NewRoot();
        try
        {
            var firstAudio = CreateWav(root, "http-error-first.wav");
            var secondAudio = CreateWav(root, "http-error-second.wav");
            var secondBytes = File.ReadAllBytes(secondAudio);
            secondBytes[secondBytes.Length - 1] = 2;
            File.WriteAllBytes(secondAudio, secondBytes);
            var host = new FakeHost { CanImportValue = false };
            var handler = new ErrorHandler();
            var registry = new ContractRegistry();
            using (var client = new BeatSageClient(registry, () => (JObject)registry.DefaultState["settings"].DeepClone(), handler))
            using (var app = NewApp(root, host, client))
            {
                app.Start();
                app.EnqueueAsync(new JArray(Audio(firstAudio, "Error first", "Artist"), Audio(secondAudio, "Error second", "Artist")), Options()).GetAwaiter().GetResult();
                var first = WaitFor(app, item => (string)item["title"] == "Error first" && (string)item["state"] == "SubmissionUnknown");
                var jobs = (JArray)app.Snapshot["jobs"];
                var second = jobs.Cast<JObject>().Single(item => (string)item["title"] == "Error second");
                Assert((string)first["errorCategory"] == "submission-unknown", "HTTP error keeps the stable submission-unknown category");
                Assert(!jobs.Any(item => (string)item["state"] == "FileError"), "HTTP error is not converted into a file error");
                Assert((string)second["state"] == "Queued", "unknown HTTP result pauses before following songs");
                Assert(handler.RequestCount == 1, "unknown HTTP result is not automatically retried");
            }
        }
        finally { DeleteRoot(root); }
    }

    private static BeatSageApp NewApp(string root, FakeHost host, IBeatSageClient client)
    {
        return new BeatSageApp(root, host, client, exception => Console.Error.WriteLine("diagnostic: " + exception.Message));
    }

    private static string EnqueueOne(BeatSageApp app, string path, string title)
    {
        app.EnqueueAsync(new JArray(Audio(path, title, "Artist")), Options()).GetAwaiter().GetResult();
        return (string)FirstJob(app)["id"];
    }

    private static JObject FirstJob(BeatSageApp app) => (JObject)((JArray)app.Snapshot["jobs"])[0];

    private static JObject WaitFor(BeatSageApp app, Func<JObject, bool> predicate, int timeoutMilliseconds = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (JObject job in (JArray)app.Snapshot["jobs"])
                if (predicate(job)) return job;
            Thread.Sleep(50);
        }
        throw new TimeoutException("Timed out waiting for Beat Sage job: " + app.Snapshot.ToString(Formatting.None));
    }

    private static JObject Options()
    {
        return new JObject
        {
            ["difficulties"] = new JArray("Normal"),
            ["modes"] = new JArray("Standard"),
            ["model"] = "v2",
            ["events"] = new JArray("DotBlocks"),
            ["environment"] = "DefaultEnvironment"
        };
    }

    private static JObject Audio(string path, string title, string artist)
    {
        return new JObject
        {
            ["path"] = path,
            ["title"] = title,
            ["artist"] = artist,
            ["duration"] = 2d,
            ["coverPath"] = "",
            ["coverBase64"] = "",
            ["coverMime"] = "",
            ["error"] = ""
        };
    }

    private static JObject CreateRequest(string path, string title, string artist)
    {
        return new JObject
        {
            ["audioFile"] = path,
            ["audioFileSha256"] = HashFile(path),
            ["audio_metadata_title"] = title,
            ["audio_metadata_artist"] = artist,
            ["difficulties"] = new JArray("Normal"),
            ["modes"] = new JArray("Standard"),
            ["events"] = new JArray("DotBlocks"),
            ["environment"] = "DefaultEnvironment",
            ["system_tag"] = "v2",
            ["coverFile"] = ""
        };
    }

    private static string HashFile(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string CreateWav(string root, string name)
    {
        var path = Path.Combine(root, name);
        const int sampleRate = 8000;
        const int sampleCount = 16000;
        var dataLength = sampleCount * 2;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataLength); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataLength);
            writer.Write(new byte[dataLength]); File.WriteAllBytes(path, stream.ToArray());
        }
        return path;
    }

    private static byte[] CreateSongZip()
    {
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                WriteEntry(archive, "Info.dat", "{\"_version\":\"2.0.0\",\"_songFilename\":\"song.ogg\",\"_coverImageFilename\":\"cover.png\",\"_difficultyBeatmapSets\":[{\"_beatmapCharacteristicName\":\"Standard\",\"_difficultyBeatmaps\":[{\"_difficulty\":\"Normal\",\"_beatmapFilename\":\"Normal.dat\"}]}]}");
                WriteEntry(archive, "song.ogg", "audio"); WriteEntry(archive, "cover.png", "cover"); WriteEntry(archive, "Normal.dat", "{}");
            }
            return stream.ToArray();
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(text);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "core-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }

    private static void DeleteRoot(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private sealed class FakeHost : IGameHost
    {
        public bool CanImportValue;
        public int RefreshCalls;
        public string[] EnvironmentNames => new[] { "DefaultEnvironment" };
        public bool CanImport => CanImportValue;
        public Task RefreshAsync(CancellationToken token) { RefreshCalls++; return Task.CompletedTask; }
        public bool IsLoaded(string levelId, JArray expectedKeys) => true;
        public void ShowSong(string levelId) { }
    }

    private sealed class FakeClient : IBeatSageClient
    {
        readonly byte[] zip = CreateSongZip();
        public int CreateCalls, GetCalls, DownloadCalls;
        public string RemoteId = "remote-id";
        public ServiceFault CreateFailure;
        public Func<int, string> Status;
        public Task<string> CreateAsync(JObject request, CancellationToken token, Action<float> progress)
        {
            CreateCalls++; if (CreateFailure != null) throw CreateFailure; return Task.FromResult(RemoteId);
        }
        public Task<string> GetStatusAsync(string id, CancellationToken token)
        {
            GetCalls++; return Task.FromResult(Status == null ? "DONE" : Status(GetCalls));
        }
        public Task DownloadAsync(string id, string destination, CancellationToken token, Action<float> progress)
        {
            DownloadCalls++; Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllBytes(destination, zip); progress?.Invoke(1f); return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        readonly Func<int, HttpRequestMessage, HttpResponseMessage> responseFactory;
        public int RequestCount;
        public HttpMethod LastMethod;
        public string LastBody = "";

        public RecordingHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            this.responseFactory = responseFactory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            if (request.Content != null) LastBody = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
            return responseFactory(RequestCount, request);
        }
    }

    private sealed class MultipartDisposingHandler : HttpMessageHandler
    {
        public int RequestCount;
        public bool ContentConsumed;
        public string LastBody = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Content != null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                LastBody = Encoding.UTF8.GetString(bytes);
                ContentConsumed = bytes.Length > 0;
                request.Content.Dispose();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"created-after-dispose\"}")
            };
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new TaskCanceledException("simulated transport timeout");
        }
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
