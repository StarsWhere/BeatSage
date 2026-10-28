using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LocalModContracts;

namespace BeatSage;

public sealed class ContractRegistry
{
    readonly JsonContractRegistry shared;
    public ContractRegistry()
    {
        shared = new JsonContractRegistry(Resource("BeatSage.Contracts.schema.json"), Resource("BeatSage.Contracts.endpoints.json"));
    }
    static string Resource(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Missing embedded contract: " + name))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
    }
    public void Validate(string name, JToken value) => shared.Validate(name, value);
    public JObject DefaultState => (JObject)shared.Source["x-defaultState"].DeepClone();
    public JObject NormalizeSettings(JObject settings)
    {
        Validate("Settings", settings);
        var normalized = (JObject)settings.DeepClone();
        // Additive optional settings keep existing queues valid. Defaults come from the schema.
        foreach (var property in ((JObject)shared.Source["definitions"]["Settings"]["properties"]).Properties())
            if (normalized[property.Name] == null && property.Value["default"] != null)
                normalized[property.Name] = property.Value["default"].DeepClone();
        return normalized;
    }
    public JArray EnvironmentCatalog => (JArray)shared.Source["x-environments"].DeepClone();
    public JObject Catalog
    {
        get
        {
            var result = new JObject();
            foreach (var pair in new[] { ("difficulties", "Difficulty"), ("modes", "Mode"), ("models", "Model"), ("events", "SongEvent") })
            {
                var def = shared.Source["definitions"][pair.Item2];
                result[pair.Item1] = new JArray(((JArray)def["enum"]).Select((v, i) => new JObject { ["value"] = v.DeepClone(), ["label"] = def["x-labels"][i].DeepClone() }));
            }
            return result;
        }
    }
    public string StateLabel(string state)
    {
        var def = shared.Source["definitions"]["JobState"];
        int index = ((JArray)def["enum"]).Select(v => (string)v).ToList().IndexOf(state);
        return index < 0 ? state : (string)def["x-labels"][index];
    }
    public JObject Endpoint(string name) => shared.Endpoint(name);
    public Uri Url(string name, string id = null)
    {
        string path = (string)Endpoint(name)["path"];
        if (path != null && path.Contains("{id}"))
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 160) throw new InvalidDataException("Invalid remote task ID");
            return shared.Url(name, new Dictionary<string, string> { ["id"] = id });
        }
        return shared.Url(name);
    }
    public string SerializeState(JObject state)
    {
        Validate("AppState", state);
        // The generated model is the persistence boundary; no hand-written DTO copy.
        var document = new JObject { ["state"] = state }.ToObject<Contracts.ContractDocument>();
        return JObject.FromObject(document)["state"].ToString(Formatting.Indented);
    }
}

public sealed class ServiceFault : Exception
{
    public string Category { get; }
    public bool Retryable { get; }
    public bool AmbiguousSubmission { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    public ServiceFault(string category, string message, bool retryable = false, bool ambiguous = false, Exception inner = null, DateTimeOffset? retryAfterUtc = null)
        : base(message, inner) { Category = category; Retryable = retryable; AmbiguousSubmission = ambiguous; RetryAfterUtc = retryAfterUtc; }
}

public interface IBeatSageClient : IDisposable
{
    Task<string> CreateAsync(JObject request, CancellationToken token, Action<float> progress);
    Task<string> GetStatusAsync(string id, CancellationToken token);
    Task DownloadAsync(string id, string destination, CancellationToken token, Action<float> progress);
}

public sealed class BeatSageClient : IBeatSageClient
{
    readonly ContractRegistry registry;
    readonly HttpClient http;
    readonly Func<JObject> settings;
    const long MaximumZipBytes = 256L * 1024 * 1024;
    public BeatSageClient(ContractRegistry registry, Func<JObject> settings, HttpMessageHandler handler = null)
    {
        this.registry = registry; this.settings = settings;
        http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSageMod/0.1.0");
    }
    public async Task<string> CreateAsync(JObject request, CancellationToken token, Action<float> progress)
    {
        registry.Validate("CreateRequest", request);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var body = new OwnedMultipartContent())
        {
            deadline.CancelAfter(TimeSpan.FromSeconds((int)settings()["transferSeconds"]));
            foreach (var field in ((JObject)registry.Endpoint("create")["fields"]).Properties())
            {
                if (field.Value.Type == JTokenType.Null) continue;
                var value = request[field.Name];
                if (field.Name == "audioFile" || field.Name == "coverFile")
                {
                    string path = (string)value;
                    if (string.IsNullOrEmpty(path)) continue;
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    try
                    {
                    if (field.Name == "audioFile")
                    {
                        using (var sha = SHA256.Create())
                        {
                            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                            if (actual != (string)request["audioFileSha256"]) throw new InvalidDataException("The audio changed before upload; scan it again");
                            stream.Position = 0;
                        }
                    }
                    var content = new ProgressContent(stream, deadline.Token, field.Name == "audioFile" ? progress : null);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(field.Name == "audioFile" ? "application/octet-stream" : CoverMime(path));
                    body.Add(content, (string)field.Value, Path.GetFileName(path));
                    stream = null; // HttpContent now owns the stream.
                    }
                    finally { stream?.Dispose(); }
                }
                else body.Add(new StringContent(value is JArray values ? string.Join(",", values.Select(v => (string)v)) : (string)value ?? "", Encoding.UTF8), (string)field.Value);
            }
            token.ThrowIfCancellationRequested();
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Post, registry.Url("create")) { Content = body })
                using (var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        bool uncertain = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout;
                        if ((int)response.StatusCode == 429 || response.Headers.RetryAfter != null)
                            throw RateLimited(response, uncertain);
                        throw new ServiceFault(uncertain ? "submission-unknown" : "service-rejected", "Beat Sage rejected the request (HTTP " + (int)response.StatusCode + ")", false, uncertain);
                    }
                    var data = await ReadJson(response, deadline.Token).ConfigureAwait(false);
                    registry.Validate("CreateResponse", data);
                    return (string)data["id"];
                }
            }
            catch (ServiceFault) { throw; }
            catch (Exception e)
            { throw new ServiceFault("submission-unknown", "Upload result is unknown; confirm resubmission before retrying", false, true, e); }
        }
    }
    static string CoverMime(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    static ServiceFault RateLimited(HttpResponseMessage response, bool ambiguous = false)
    {
        var until = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5));
        if (until < DateTimeOffset.UtcNow) until = DateTimeOffset.UtcNow;
        var message = ambiguous
            ? "Beat Sage is rate limited; retry after " + until.ToLocalTime().ToString("HH:mm:ss") + "; submission result is unknown"
            : "Beat Sage is rate limited; retry after " + until.ToLocalTime().ToString("HH:mm:ss");
        return new ServiceFault(ambiguous ? "submission-unknown" : "rate-limit", message, true, ambiguous, retryAfterUtc: until);
    }
    public async Task<string> GetStatusAsync(string id, CancellationToken token)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds((int)settings()["requestSeconds"]));
            using (var response = await SafeGet("heartbeat", id, deadline.Token).ConfigureAwait(false))
            {
            JObject data;
            try { data = await ReadJson(response, deadline.Token).ConfigureAwait(false); }
            catch (JsonException e) { throw new ServiceFault("protocol", "The service response is not valid task data", false, false, e); }
            try { registry.Validate("HeartbeatResponse", data); }
            catch (InvalidDataException e) { throw new ServiceFault("protocol", "The service returned an unknown task state; update the interface adapter", false, false, e); }
            return (string)data["status"];
            }
        }
    }
    public async Task DownloadAsync(string id, string destination, CancellationToken token, Action<float> progress)
    {
        int retries = (int)settings()["safeRetries"];
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds((int)settings()["transferSeconds"]));
                    using (var response = await SafeGet("download", id, deadline.Token, 0).ConfigureAwait(false))
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        long expected = response.Content.Headers.ContentLength ?? 0, written = 0;
                        if (expected > MaximumZipBytes) throw new ServiceFault("invalid-archive", "The map package exceeds the allowed size");
                        var buffer = new byte[65536]; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) != 0)
                        {
                            written += read;
                            if (written > MaximumZipBytes) throw new ServiceFault("invalid-archive", "The map package exceeds the allowed size");
                            await output.WriteAsync(buffer, 0, read, deadline.Token).ConfigureAwait(false);
                            if (expected > 0) progress?.Invoke((float)written / expected);
                        }
                        if (written == 0 || (expected > 0 && written != expected)) throw new IOException("The map package download is incomplete");
                        return;
                    }
                }
            }
            catch (Exception e) when (!token.IsCancellationRequested && attempt < retries && (e is IOException || e is HttpRequestException || e is OperationCanceledException || (e is ServiceFault fault && fault.Retryable && fault.Category != "rate-limit")))
            { await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 << attempt)), token).ConfigureAwait(false); }
        }
    }
    async Task<HttpResponseMessage> SafeGet(string endpoint, string id, CancellationToken token, int? retryBudget = null)
    {
        int retries = retryBudget ?? (int)settings()["safeRetries"];
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(15, 2 << attempt));
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds((int)settings()[endpoint == "download" ? "transferSeconds" : "requestSeconds"]));
                    var response = await http.GetAsync(registry.Url(endpoint, id), HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return response;
                    int status = (int)response.StatusCode;
                    if (status == 404 || status == 410) { response.Dispose(); throw new ServiceFault("remote-not-found", "The remote task no longer exists; regenerate it"); }
                    bool retryable = status == 429 || status == 408 || status >= 500;
                    if (status == 429 || (retryable && response.Headers.RetryAfter != null))
                    { var limited = RateLimited(response); response.Dispose(); throw limited; }
                    response.Dispose();
                    if (!retryable || attempt >= retries) throw new ServiceFault("service", "Beat Sage is temporarily unavailable (HTTP " + status + ")", retryable);
                }
            }
            catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !token.IsCancellationRequested))
            { if (attempt >= retries) throw new ServiceFault("network", "The network request failed; you can retry", true, false, e); }
            token.ThrowIfCancellationRequested();
            await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, token).ConfigureAwait(false);
        }
    }
    static async Task<JObject> ReadJson(HttpResponseMessage response, CancellationToken token)
    {
        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        using (var output = new MemoryStream())
        {
            var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > 65536) throw new InvalidDataException("The response is too large");
                output.Write(buffer, 0, read);
            }
            return JObject.Parse(Encoding.UTF8.GetString(output.ToArray()));
        }
    }
    public void Dispose() => http.Dispose();

    // Unity's Mono HttpClient may dispose request content after transmission.
    // MultipartContent in that runtime is not safe to dispose twice, so keep one owner.
    sealed class OwnedMultipartContent : MultipartFormDataContent
    {
        int disposed;
        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) base.Dispose(disposing);
        }
    }

    sealed class ProgressContent : HttpContent
    {
        readonly Stream input; readonly CancellationToken token; readonly Action<float> progress;
        public ProgressContent(Stream input, CancellationToken token, Action<float> progress) { this.input = input; this.token = token; this.progress = progress; }
        protected override bool TryComputeLength(out long length) { length = input.Length; return true; }
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
        {
            var buffer = new byte[65536]; int read; long sent = 0;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
            { await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false); sent += read; progress?.Invoke((float)sent / input.Length); }
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
