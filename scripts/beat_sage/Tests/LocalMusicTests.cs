using System.Buffers.Binary;
using System.Text;
using BeatSage;
using Newtonsoft.Json.Linq;

namespace BeatSage.Tests;

internal static class LocalMusicTests
{
    private static int _assertions;

    public static int Main()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int playlistChecks = PlaylistTests.Run(root);
            Console.WriteLine($"Beat Sage playlist tests passed ({playlistChecks} assertions).");
            TestBrowse(root);
            TestFlacMetadata(root);
            TestWavDuration(root);
            TestPrepareItemCallback(root);
            TestMalformedPath();
            TestCancellation();
            TestRecursiveDuplicatePaths(root);
            Console.WriteLine($"Beat Sage LocalMusic tests passed ({_assertions} assertions).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void TestBrowse(string root)
    {
        var nested = Directory.CreateDirectory(Path.Combine(root, "Nested"));
        var flac = Path.Combine(root, "browse.flac");
        File.WriteAllBytes(flac, CreateFlac("Browse", "Artist", 44100, 44100));
        File.WriteAllText(Path.Combine(root, "ignored.txt"), "not audio");

        var rows = LocalMusic.BrowseAsync(root, new JObject(), CancellationToken.None).GetAwaiter().GetResult();
        Assert(rows.Any(row => (bool?)row["directory"] == true && (string)row["name"] == "Nested"), "Browse returns directories.");
        Assert(rows.Any(row => (bool?)row["directory"] == false && (string)row["name"] == "browse.flac"), "Browse returns enabled audio files.");
        Assert(!rows.Any(row => (string)row["name"] == "ignored.txt"), "Browse filters unsupported formats.");
        Assert(rows.All(row => row["error"] != null), "Browse rows carry an error field.");

        var roots = LocalMusic.BrowseAsync(string.Empty, new JObject { ["favorites"] = new JArray(root) }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(roots.Any(row => string.Equals((string)row["path"], Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)), "Root browse includes favorites.");
    }

    private static void TestFlacMetadata(string root)
    {
        var path = Path.Combine(root, "wish.flac");
        File.WriteAllBytes(path, CreateFlac("Wish title", "Wish artist", 44100, 44100 * 3));
        var settings = new JObject
        {
            ["enabledFormats"] = new JArray(".flac")
        };
        var rows = LocalMusic.ScanAsync(new[] { path }, false, settings, CancellationToken.None).GetAwaiter().GetResult();
        Assert(rows.Count == 1, "FLAC scan returns one row.");
        var row = (JObject)rows[0]!;
        Assert((string)row["title"] == "Wish title", "FLAC title is parsed.");
        Assert((string)row["artist"] == "Wish artist", "FLAC artist is parsed.");
        Assert(string.IsNullOrEmpty((string)row["error"]), "Valid FLAC has no metadata error.");
    }

    private static void TestWavDuration(string root)
    {
        var path = Path.Combine(root, "duration.wav");
        File.WriteAllBytes(path, CreateWav(8000, 16000));
        var rows = LocalMusic.ScanAsync(new[] { path }, false, new JObject(), CancellationToken.None).GetAwaiter().GetResult();
        Assert(rows.Count == 1, "WAV scan returns one row.");
        var row = (JObject)rows[0]!;
        Assert(Math.Abs(((double?)row["duration"] ?? 0d) - 2d) < 0.01d, "WAV duration is parsed.");
        Assert(string.IsNullOrEmpty((string)row["error"]), "Valid WAV has no metadata error.");
    }

    private static void TestPrepareItemCallback(string root)
    {
        var path = Path.Combine(root, "prepared.flac");
        File.WriteAllBytes(path, CreateFlac("Prepared", "Artist", 44100, 44100));
        var missingDirectory = Path.Combine(root, "missing-directory");
        var callbackRows = new List<JObject>();
        var rows = LocalMusic.ScanAsync(
            new[] { path, missingDirectory },
            false,
            new JObject { ["enabledFormats"] = new JArray(".flac") },
            CancellationToken.None,
            item =>
            {
                callbackRows.Add(item);
                item["coverBase64"] = string.Empty;
            }).GetAwaiter().GetResult();

        Assert(callbackRows.Count == rows.Count, "prepareItem runs once for every scan row, including an error row.");
        Assert(rows.All(row => string.IsNullOrEmpty((string)row["coverBase64"])), "prepareItem can clear coverBase64 before the row is retained.");
        Assert(rows.Any(row => !string.IsNullOrWhiteSpace((string)row["error"])), "directory access errors remain explicit scan rows.");
        Assert(rows.All(row => row["path"] != null && row["title"] != null && row["artist"] != null && row["duration"] != null && row["coverMime"] != null), "error rows retain the complete scan contract.");
    }

    private static void TestMalformedPath()
    {
        var rows = LocalMusic.ScanAsync(new[] { "bad\0path" }, false, new JObject(), CancellationToken.None).GetAwaiter().GetResult();
        Assert(rows.Count == 1, "Malformed path returns one diagnostic row.");
        Assert(!string.IsNullOrWhiteSpace((string)rows[0]!["error"]), "Malformed path includes an explicit error.");
    }

    private static void TestCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            LocalMusic.BrowseAsync(string.Empty, new JObject(), cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "Browse honors an already cancelled token.");
    }

    private static void TestRecursiveDuplicatePaths(string root)
    {
        var directory = Path.Combine(root, "recursive");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "duplicate.flac");
        File.WriteAllBytes(path, CreateFlac("Duplicate", "Artist", 44100, 44100));
        var rows = LocalMusic.ScanAsync(new[] { directory, directory }, true, new JObject(), CancellationToken.None).GetAwaiter().GetResult();
        Assert(rows.Count(row => string.Equals((string)row["path"], Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) == 1,
            "Recursive scan de-duplicates repeated directories.");
    }

    private static byte[] CreateFlac(string title, string artist, int sampleRate, int sampleCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("fLaC"));

        var streamInfo = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(0, 2), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfo.AsSpan(2, 2), 4096);
        WriteUInt24BigEndian(streamInfo, 4, 1);
        WriteUInt24BigEndian(streamInfo, 7, 1);
        var packed = ((ulong)sampleRate << 44) | ((ulong)(2 - 1) << 41) | ((ulong)(16 - 1) << 36) | (uint)sampleCount;
        for (var index = 0; index < 8; index++) streamInfo[10 + index] = (byte)(packed >> (56 - index * 8));
        WriteMetadataBlock(writer, 0, streamInfo);

        var comments = new[] { "TITLE=" + title, "ARTIST=" + artist };
        using var commentStream = new MemoryStream();
        using (var commentWriter = new BinaryWriter(commentStream, Encoding.UTF8, true))
        {
            WriteUInt32LittleEndian(commentWriter, (uint)Encoding.UTF8.GetByteCount("BeatSageTests"));
            commentWriter.Write(Encoding.UTF8.GetBytes("BeatSageTests"));
            WriteUInt32LittleEndian(commentWriter, (uint)comments.Length);
            foreach (var comment in comments)
            {
                var bytes = Encoding.UTF8.GetBytes(comment);
                WriteUInt32LittleEndian(commentWriter, (uint)bytes.Length);
                commentWriter.Write(bytes);
            }
        }

        WriteMetadataBlock(writer, 0x84, commentStream.ToArray());
        return stream.ToArray();
    }

    private static void WriteMetadataBlock(BinaryWriter writer, byte type, byte[] data)
    {
        writer.Write(type);
        writer.Write((byte)((data.Length >> 16) & 0xff));
        writer.Write((byte)((data.Length >> 8) & 0xff));
        writer.Write((byte)(data.Length & 0xff));
        writer.Write(data);
    }

    private static void WriteUInt24BigEndian(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 16);
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)value;
    }

    private static void WriteUInt32LittleEndian(BinaryWriter writer, uint value)
    {
        writer.Write(value);
    }

    private static byte[] CreateWav(int sampleRate, int sampleCount)
    {
        var dataLength = sampleCount * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        return stream.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
