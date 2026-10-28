using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TagLib;

namespace BeatSage
{
    /// <summary>
    /// Lightweight local music browsing and metadata extraction used by Beat Sage.
    /// Directory browsing never opens an audio file; metadata is read only for paths
    /// explicitly supplied to <see cref="ScanAsync"/> or discovered below a selected
    /// directory.
    /// </summary>
    public static class LocalMusic
    {
        private const long MaxAudioFileBytes = 512L * 1024L * 1024L;
        private const int MaxCoverBytes = 8 * 1024 * 1024;

        private static readonly string[] DefaultFormats =
        {
            ".mp3", ".flac", ".wav", ".ogg", ".aac", ".aiff", ".aif"
        };

        public static Task<JArray> BrowseAsync(
            string path,
            JObject settings,
            CancellationToken token)
        {
            return Task.Run(() => BrowseCore(path, settings, token), token);
        }

        public static Task<JArray> ScanAsync(
            IEnumerable<string> paths,
            bool recursive,
            JObject settings,
            CancellationToken token,
            Action<JObject> prepareItem = null)
        {
            return Task.Run(() => ScanCore(paths, recursive, settings, token, prepareItem), token);
        }

        private static JArray BrowseCore(string path, JObject settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var rows = new JArray();
            var formats = ReadEnabledFormats(settings);

            if (string.IsNullOrWhiteSpace(path))
            {
                AddRootRows(rows, settings, token);
                return rows;
            }

            string fullPath;
            if (!TryNormalizePath(path, out fullPath, out var pathError))
            {
                rows.Add(CreateBrowseRow(path, GetSafeFileName(path), true, pathError));
                return rows;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(fullPath);
                if (!directory.Exists)
                {
                    rows.Add(CreateBrowseRow(fullPath, directory.Name, true, "Directory does not exist."));
                    return rows;
                }
            }
            catch (Exception exception)
            {
                rows.Add(CreateBrowseRow(fullPath, GetSafeFileName(fullPath), true, FormatError("Cannot access directory", exception)));
                return rows;
            }

            EnumerateBrowseChildren(directory, formats, rows, token);
            return rows;
        }

        private static void AddRootRows(JArray rows, JObject settings, CancellationToken token)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var musicPath = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(musicPath))
            {
                musicPath = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(musicPath))
            {
                AddBrowsePathRow(rows, seen, musicPath, "Music", true, token);
            }

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
                        {
                            continue;
                        }

                        var name = drive.Name;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(drive.VolumeLabel))
                            {
                                name = drive.VolumeLabel + " (" + drive.Name.TrimEnd('\\') + ")";
                            }
                        }
                        catch
                        {
                            // The root is still useful when a volume label cannot be read.
                        }

                        AddBrowsePathRow(rows, seen, drive.RootDirectory.FullName, name, true, token);
                    }
                    catch (Exception exception)
                    {
                        var drivePath = string.Empty;
                        try { drivePath = drive.Name; } catch { }
                        rows.Add(CreateBrowseRow(drivePath, drivePath, true, FormatError("Cannot access drive", exception)));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                rows.Add(CreateBrowseRow(string.Empty, "Drives", true, FormatError("Cannot enumerate drives", exception)));
            }

            foreach (var favorite in ReadFavorites(settings))
            {
                token.ThrowIfCancellationRequested();
                AddBrowsePathRow(rows, seen, favorite, GetSafeFileName(favorite), true, token);
            }
        }

        private static void AddBrowsePathRow(
            JArray rows,
            HashSet<string> seen,
            string path,
            string name,
            bool directory,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string fullPath;
            if (!TryNormalizePath(path, out fullPath, out var pathError))
            {
                rows.Add(CreateBrowseRow(path, name, directory, pathError));
                return;
            }

            var key = NormalizeKey(fullPath);
            if (!seen.Add(key))
            {
                return;
            }

            try
            {
                if (directory)
                {
                    var info = new DirectoryInfo(fullPath);
                    if (!info.Exists)
                    {
                        rows.Add(CreateBrowseRow(fullPath, name, true, "Directory does not exist."));
                        return;
                    }
                }

                rows.Add(CreateBrowseRow(fullPath, name, directory, string.Empty));
            }
            catch (Exception exception)
            {
                rows.Add(CreateBrowseRow(fullPath, name, directory, FormatError("Cannot access path", exception)));
            }
        }

        private static void EnumerateBrowseChildren(
            DirectoryInfo directory,
            HashSet<string> formats,
            JArray rows,
            CancellationToken token)
        {
            var entries = new List<JObject>();
            IEnumerator<FileSystemInfo> enumerator = null;
            try
            {
                enumerator = directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        rows.Add(CreateBrowseRow(directory.FullName, directory.Name, true, FormatError("Cannot enumerate directory", exception)));
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    var entry = enumerator.Current;
                    if (entry == null)
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception exception)
                    {
                        entries.Add(CreateBrowseRow(entry.FullName, entry.Name, false, FormatError("Cannot inspect entry", exception)));
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        ((attributes & FileAttributes.Hidden) != 0 && entry is DirectoryInfo))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        entries.Add(CreateBrowseRow(entry.FullName, entry.Name, true, string.Empty));
                        continue;
                    }

                    if (entry is FileInfo && IsEnabledFormat(entry.Extension, formats))
                    {
                        entries.Add(CreateBrowseRow(entry.FullName, entry.Name, false, string.Empty));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                rows.Add(CreateBrowseRow(directory.FullName, directory.Name, true, FormatError("Cannot enumerate directory", exception)));
            }
            finally
            {
                if (enumerator != null)
                {
                    enumerator.Dispose();
                }
            }

            foreach (var row in entries
                .OrderByDescending(row => (bool)row["directory"])
                .ThenBy(row => (string)row["name"], StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => (string)row["path"], StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                rows.Add(row);
            }
        }

        private static JArray ScanCore(
            IEnumerable<string> paths,
            bool recursive,
            JObject settings,
            CancellationToken token,
            Action<JObject> prepareItem)
        {
            var rows = new JArray();
            var formats = ReadEnabledFormats(settings);
            var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (paths == null)
            {
                AddScanRow(rows, CreateScanRow(string.Empty, "Paths collection is null."), prepareItem);
                return rows;
            }

            IEnumerator<string> pathEnumerator = null;
            try
            {
                pathEnumerator = paths.GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool moved;
                    try
                    {
                        moved = pathEnumerator.MoveNext();
                    }
                    catch (Exception exception)
                    {
                        AddScanRow(rows, CreateScanRow(string.Empty, FormatError("Cannot enumerate selected paths", exception)), prepareItem);
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    var selectedPath = pathEnumerator.Current;
                    ScanSelectedPath(selectedPath, recursive, formats, rows, visitedDirectories, visitedFiles, token, prepareItem);
                }
            }
            finally
            {
                if (pathEnumerator != null)
                {
                    pathEnumerator.Dispose();
                }
            }

            return rows;
        }

        private static void AddScanRow(JArray rows, JObject row, Action<JObject> prepareItem)
        {
            if (prepareItem != null)
            {
                try
                {
                    prepareItem(row);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    row["error"] = FormatError("Cannot prepare scan item", exception);
                }
            }

            rows.Add(row);
        }

        private static void ScanSelectedPath(
            string selectedPath,
            bool recursive,
            HashSet<string> formats,
            JArray rows,
            HashSet<string> visitedDirectories,
            HashSet<string> visitedFiles,
            CancellationToken token,
            Action<JObject> prepareItem)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                AddScanRow(rows, CreateScanRow(selectedPath ?? string.Empty, "Path is empty."), prepareItem);
                return;
            }

            string fullPath;
            if (!TryNormalizePath(selectedPath, out fullPath, out var pathError))
            {
                AddScanRow(rows, CreateScanRow(selectedPath, pathError), prepareItem);
                return;
            }

            FileAttributes attributes;
            try
            {
                attributes = System.IO.File.GetAttributes(fullPath);
            }
            catch (Exception exception)
            {
                AddScanRow(rows, CreateScanRow(fullPath, FormatError("Cannot access path", exception)), prepareItem);
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.Directory) != 0))
            {
                return;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                ScanDirectory(new DirectoryInfo(fullPath), recursive, formats, rows, visitedDirectories, visitedFiles, token, prepareItem);
                return;
            }

            if (!IsEnabledFormat(Path.GetExtension(fullPath), formats))
            {
                return;
            }

            if (visitedFiles.Add(NormalizeKey(fullPath)))
            {
                AddScanRow(rows, ReadMetadata(fullPath, token), prepareItem);
            }
        }

        private static void ScanDirectory(
            DirectoryInfo directory,
            bool recursive,
            HashSet<string> formats,
            JArray rows,
            HashSet<string> visitedDirectories,
            HashSet<string> visitedFiles,
            CancellationToken token,
            Action<JObject> prepareItem)
        {
            token.ThrowIfCancellationRequested();
            var directoryKey = NormalizeKey(directory.FullName);
            if (!visitedDirectories.Add(directoryKey))
            {
                return;
            }

            IEnumerator<FileSystemInfo> enumerator = null;
            try
            {
                enumerator = directory.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).GetEnumerator();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        AddScanRow(rows, CreateScanRow(directory.FullName, FormatError("Cannot enumerate directory", exception)), prepareItem);
                        return;
                    }

                    if (!moved)
                    {
                        return;
                    }

                    var entry = enumerator.Current;
                    if (entry == null)
                    {
                        continue;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception exception)
                    {
                        AddScanRow(rows, CreateScanRow(entry.FullName, FormatError("Cannot inspect entry", exception)), prepareItem);
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                        ((attributes & FileAttributes.Hidden) != 0 && (attributes & FileAttributes.Directory) != 0))
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (recursive)
                        {
                            ScanDirectory((DirectoryInfo)entry, true, formats, rows, visitedDirectories, visitedFiles, token, prepareItem);
                        }

                        continue;
                    }

                    if (IsEnabledFormat(Path.GetExtension(entry.FullName), formats) && visitedFiles.Add(NormalizeKey(entry.FullName)))
                    {
                        AddScanRow(rows, ReadMetadata(entry.FullName, token), prepareItem);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                AddScanRow(rows, CreateScanRow(directory.FullName, FormatError("Cannot enumerate directory", exception)), prepareItem);
            }
            finally
            {
                if (enumerator != null)
                {
                    enumerator.Dispose();
                }
            }
        }

        private static JObject ReadMetadata(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var row = CreateScanRow(path, string.Empty);
            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    row["error"] = "File does not exist.";
                    return row;
                }

                if (fileInfo.Length > MaxAudioFileBytes)
                {
                    row["error"] = "Audio file exceeds the 512 MiB limit.";
                    return row;
                }

                using (var abstraction = new ReadOnlyFileAbstraction(path))
                using (var audioFile = TagLib.File.Create(abstraction, ReadStyle.Average))
                {
                    token.ThrowIfCancellationRequested();
                    var tag = audioFile.Tag;
                    row["title"] = string.IsNullOrWhiteSpace(tag.Title)
                        ? Path.GetFileNameWithoutExtension(path)
                        : tag.Title;
                    row["artist"] = tag.Performers == null
                        ? string.Empty
                        : tag.Performers.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                    row["duration"] = audioFile.Properties == null
                        ? 0d
                        : Math.Max(0d, audioFile.Properties.Duration.TotalSeconds);

                    var pictures = tag.Pictures;
                    if (pictures != null && pictures.Length > 0)
                    {
                        var picture = pictures[0];
                        var coverData = picture == null ? null : picture.Data;
                        if (coverData != null && coverData.Count > MaxCoverBytes)
                        {
                            row["error"] = "Embedded cover exceeds the 8 MiB limit.";
                        }
                        else if (coverData != null && coverData.Count > 0)
                        {
                            var data = coverData.Data;
                            if (data.Length <= MaxCoverBytes)
                            {
                                row["coverBase64"] = Convert.ToBase64String(data);
                                row["coverMime"] = picture.MimeType ?? string.Empty;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                row["error"] = FormatError("Cannot read audio metadata", exception);
            }

            return row;
        }

        private static JObject CreateBrowseRow(string path, string name, bool directory, string error)
        {
            return new JObject
            {
                ["path"] = path ?? string.Empty,
                ["name"] = name ?? string.Empty,
                ["directory"] = directory,
                ["error"] = error ?? string.Empty
            };
        }

        private static JObject CreateScanRow(string path, string error)
        {
            return new JObject
            {
                ["path"] = path ?? string.Empty,
                ["title"] = string.Empty,
                ["artist"] = string.Empty,
                ["duration"] = 0d,
                ["coverPath"] = string.Empty,
                ["coverBase64"] = string.Empty,
                ["coverMime"] = string.Empty,
                ["error"] = error ?? string.Empty
            };
        }

        private static HashSet<string> ReadEnabledFormats(JObject settings)
        {
            var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var token = settings == null ? null : settings["enabledFormats"];
            if (token == null || token.Type == JTokenType.Null)
            {
                foreach (var format in DefaultFormats) formats.Add(format);
                return formats;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var value in token.Values<string>())
                {
                    AddFormat(formats, value);
                }
            }
            else if (token.Type == JTokenType.String)
            {
                foreach (var value in token.Value<string>().Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AddFormat(formats, value);
                }
            }

            return formats;
        }

        private static void AddFormat(HashSet<string> formats, string format)
        {
            if (string.IsNullOrWhiteSpace(format)) return;
            var normalized = format.Trim().ToLowerInvariant();
            if (!normalized.StartsWith(".", StringComparison.Ordinal)) normalized = "." + normalized;
            if (normalized == ".aif") normalized = ".aiff";
            if (DefaultFormats.Contains(normalized, StringComparer.OrdinalIgnoreCase)) formats.Add(normalized);
        }

        private static bool IsEnabledFormat(string extension, HashSet<string> formats)
        {
            var normalized = (extension ?? string.Empty).ToLowerInvariant();
            if (normalized == ".aif") normalized = ".aiff";
            return formats.Contains(normalized);
        }

        private static IEnumerable<string> ReadFavorites(JObject settings)
        {
            var token = settings == null ? null : settings["favorites"];
            if (token == null || token.Type == JTokenType.Null)
            {
                yield break;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token.Values<string>())
                {
                    if (!string.IsNullOrWhiteSpace(item)) yield return item;
                }

                yield break;
            }

            if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()))
            {
                yield return token.Value<string>();
            }
        }

        private static bool TryNormalizePath(string path, out string fullPath, out string error)
        {
            try
            {
                fullPath = Path.GetFullPath(path);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                fullPath = path ?? string.Empty;
                error = FormatError("Invalid path", exception);
                return false;
            }
        }

        private static string NormalizeKey(string path)
        {
            var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && string.Equals(normalized, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return normalized;
        }

        private static string GetSafeFileName(string path)
        {
            try
            {
                return Path.GetFileName(path) ?? path ?? string.Empty;
            }
            catch
            {
                return path ?? string.Empty;
            }
        }

        private static string FormatError(string prefix, Exception exception)
        {
            var message = exception == null ? string.Empty : exception.Message;
            if (string.IsNullOrWhiteSpace(message)) message = exception == null ? string.Empty : exception.GetType().Name;
            return prefix + ": " + message.Replace("\r", " ").Replace("\n", " ");
        }

        private sealed class ReadOnlyFileAbstraction : TagLib.File.IFileAbstraction, IDisposable
        {
            private readonly string _path;
            private FileStream _readStream;

            public ReadOnlyFileAbstraction(string path)
            {
                _path = path;
            }

            public string Name => _path;

            public Stream ReadStream
            {
                get
                {
                    if (_readStream == null)
                    {
                        _readStream = new FileStream(
                            _path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete,
                            64 * 1024,
                            FileOptions.SequentialScan);
                    }

                    return _readStream;
                }
            }

            public Stream WriteStream
            {
                get { throw new NotSupportedException("Beat Sage reads local music files without write access."); }
            }

            public void CloseStream(Stream stream)
            {
                if (stream == null) return;
                stream.Dispose();
                if (ReferenceEquals(stream, _readStream)) _readStream = null;
            }

            public void Dispose()
            {
                if (_readStream != null)
                {
                    _readStream.Dispose();
                    _readStream = null;
                }
            }
        }
    }
}
