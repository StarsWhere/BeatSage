using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SongPackageIO {
	/// <summary>Thrown when a song archive or its Info.dat is not a supported, safe package.</summary>
	public sealed class InvalidSongPackageException : Exception {
		public InvalidSongPackageException(string message) : base(message) { }
		public InvalidSongPackageException(string message, Exception innerException) : base(message, innerException) { }
	}

	/// <summary>Thrown when the archive is valid but the package cannot be read or published.</summary>
	public sealed class SongPackageIOException : IOException {
		public SongPackageIOException(string message) : base(message) { }
		public SongPackageIOException(string message, Exception innerException) : base(message, innerException) { }
	}

	/// <summary>
	/// A fully validated song staged outside CustomLevels. Publication is deliberately
	/// separate so a caller can perform the final directory move on its owning thread.
	/// </summary>
	public sealed class PreparedPackage : IDisposable {
		private readonly object gate = new object();
		private string stagePath;
		private readonly string customLevelsRoot;
		private readonly string folderName;
		private string publishedPath;

		internal PreparedPackage(string stagePath, string customLevelsRoot, string folderName) {
			this.stagePath = stagePath;
			this.customLevelsRoot = customLevelsRoot;
			this.folderName = folderName;
		}

		/// <summary>Publishes the prepared package and returns its final song directory.</summary>
		public string Publish(CancellationToken token) {
			lock(gate) {
				if(publishedPath != null)
					return publishedPath;
				if(stagePath == null)
					throw new ObjectDisposedException(nameof(PreparedPackage));
				publishedPath = PackageImporter.PublishPrepared(stagePath, customLevelsRoot, folderName, token);
				stagePath = null;
				return publishedPath;
			}
		}

		public void Dispose() {
			lock(gate) {
				if(stagePath != null) {
					PackageImporter.CleanupStage(stagePath);
					stagePath = null;
				}
			}
		}
	}

	/// <summary>
	/// Validates and publishes a Beat Saber song archive without exposing a partially
	/// extracted directory to SongCore.
	/// </summary>
	public static class PackageImporter {
		public const int MaxEntryCount = 512;
		public const long MaxEntryBytes = 256L * 1024L * 1024L;
		public const long MaxTotalBytes = 512L * 1024L * 1024L;

		private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
		private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
		private static readonly char[] DirectorySeparators = new[] { '/', '\\' };

		private sealed class ArchiveEntryInfo {
			public ZipArchiveEntry Entry;
			public string Path;
			public bool IsDirectory;
			public long Length;
		}

		private sealed class ImportRoots {
			public string CustomLevelsRoot;
			public string StagingRoot;
			public string FolderName;
		}

		/// <summary>Imports a ZIP file and returns the published song directory.</summary>
		public static string Install(string zipPath, string customLevelsRoot, string folderName, string stagingRoot,
			CancellationToken token, Action<float> progress = null) {
			if(string.IsNullOrWhiteSpace(zipPath))
				throw new ArgumentNullException(nameof(zipPath));

			using(var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			using(var prepared = Prepare(stream, customLevelsRoot, folderName, stagingRoot, token, progress))
				return prepared.Publish(token);
		}

		/// <summary>Imports a ZIP stream and returns the published song directory.</summary>
		public static string Install(Stream zipStream, string customLevelsRoot, string folderName, string stagingRoot,
			CancellationToken token, Action<float> progress = null) {
			using(var prepared = Prepare(zipStream, customLevelsRoot, folderName, stagingRoot, token, progress))
				return prepared.Publish(token);
		}

		/// <summary>Validates a ZIP file and stages it without publishing to CustomLevels.</summary>
		public static PreparedPackage Prepare(string zipPath, string customLevelsRoot, string folderName, string stagingRoot,
			CancellationToken token, Action<float> progress = null) {
			if(string.IsNullOrWhiteSpace(zipPath))
				throw new ArgumentNullException(nameof(zipPath));
			using(var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
				return Prepare(stream, customLevelsRoot, folderName, stagingRoot, token, progress);
		}

		/// <summary>Validates a ZIP stream and stages it without publishing to CustomLevels.</summary>
		public static PreparedPackage Prepare(Stream zipStream, string customLevelsRoot, string folderName, string stagingRoot,
			CancellationToken token, Action<float> progress = null) {
			if(zipStream == null)
				throw new ArgumentNullException(nameof(zipStream));
			if(!zipStream.CanRead)
				throw new SongPackageIOException("The song archive stream is not readable.");

			var roots = ValidateRoots(customLevelsRoot, folderName, stagingRoot);
			string stagePath = null;
			Report(progress, 0f);

			try {
				using(var archive = OpenArchive(zipStream)) {
					var entries = ValidateEntries(archive, token);
					stagePath = CreateStageDirectory(roots.StagingRoot);
					ExtractEntries(entries, stagePath, token, progress);
					ValidateInfoAndReferences(stagePath, entries, token);
				}

				return new PreparedPackage(stagePath, roots.CustomLevelsRoot, roots.FolderName);
			} catch(InvalidSongPackageException) {
				CleanupStage(stagePath);
				throw;
			} catch(InvalidDataException exception) {
				CleanupStage(stagePath);
				throw new InvalidSongPackageException("The song archive is invalid.", exception);
			} catch(OperationCanceledException) {
				CleanupStage(stagePath);
				throw;
			} catch(UnauthorizedAccessException exception) {
				CleanupStage(stagePath);
				throw new SongPackageIOException("The song archive could not be published.", exception);
			} catch(IOException) {
				CleanupStage(stagePath);
				throw;
			} catch(Exception) {
				CleanupStage(stagePath);
				throw;
			}
		}

		private static ZipArchive OpenArchive(Stream stream) {
			try {
				return new ZipArchive(stream, ZipArchiveMode.Read, true, StrictUtf8);
			} catch(InvalidDataException exception) {
				throw new InvalidSongPackageException("The song archive is not a valid ZIP file.", exception);
			} catch(ArgumentException exception) {
				throw new SongPackageIOException("The song archive stream cannot be read.", exception);
			} catch(NotSupportedException exception) {
				throw new SongPackageIOException("The song archive stream cannot be read.", exception);
			}
		}

		private static ImportRoots ValidateRoots(string customLevelsRoot, string folderName, string stagingRoot) {
			if(string.IsNullOrWhiteSpace(customLevelsRoot))
				throw new ArgumentNullException(nameof(customLevelsRoot));
			if(string.IsNullOrWhiteSpace(stagingRoot))
				throw new ArgumentNullException(nameof(stagingRoot));
			if(string.IsNullOrWhiteSpace(folderName))
				throw new ArgumentNullException(nameof(folderName));

			var roots = new ImportRoots {
				CustomLevelsRoot = GetFullPath(customLevelsRoot, nameof(customLevelsRoot)),
				StagingRoot = GetFullPath(stagingRoot, nameof(stagingRoot)),
				FolderName = ValidateFolderName(folderName)
			};

			if(IsSameOrWithin(roots.StagingRoot, roots.CustomLevelsRoot) ||
				IsSameOrWithin(roots.CustomLevelsRoot, roots.StagingRoot)) {
				throw new ArgumentException("The staging directory must be outside the song library.", nameof(stagingRoot));
			}

			var customVolume = Path.GetPathRoot(roots.CustomLevelsRoot);
			var stagingVolume = Path.GetPathRoot(roots.StagingRoot);
			if(string.IsNullOrEmpty(customVolume) || string.IsNullOrEmpty(stagingVolume) ||
				!string.Equals(customVolume, stagingVolume, PathComparison)) {
				throw new ArgumentException("The staging directory must be on the same volume as the song library.", nameof(stagingRoot));
			}

			if(File.Exists(roots.CustomLevelsRoot))
				throw new SongPackageIOException("The song library path is a file.");
			if(File.Exists(roots.StagingRoot))
				throw new SongPackageIOException("The staging path is a file.");
			if(HasReparsePointInExistingPath(roots.CustomLevelsRoot) || HasReparsePointInExistingPath(roots.StagingRoot))
				throw new SongPackageIOException("Reparse-point roots are not supported.");

			return roots;
		}

		private static string GetFullPath(string path, string parameterName) {
			try {
				return TrimDirectorySeparator(Path.GetFullPath(path));
			} catch(Exception exception) when(exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException) {
				throw new ArgumentException("The path is invalid.", parameterName, exception);
			}
		}

		private static string ValidateFolderName(string folderName) {
			if(folderName.IndexOfAny(DirectorySeparators) >= 0 || folderName.IndexOf(':') >= 0 ||
				folderName == "." || folderName == ".." || folderName.Length > 255)
				throw new ArgumentException("The song folder name must be one safe Windows directory name.", nameof(folderName));
			ValidatePathSegment(folderName, "folder name");
			return folderName;
		}

		private static List<ArchiveEntryInfo> ValidateEntries(ZipArchive archive, CancellationToken token) {
			List<ZipArchiveEntry> rawEntries;
			try {
				rawEntries = archive.Entries.ToList();
			} catch(InvalidDataException exception) {
				throw new InvalidSongPackageException("The song archive entries cannot be read.", exception);
			}

			if(rawEntries.Count == 0)
				throw Invalid("The song archive is empty.");
			if(rawEntries.Count > MaxEntryCount)
				throw Invalid("The song archive contains too many entries.");

			var entries = new List<ArchiveEntryInfo>(rawEntries.Count);
			var names = new HashSet<string>(PathComparer);
			ArchiveEntryInfo infoEntry = null;
			long totalBytes = 0;

			foreach(var entry in rawEntries) {
				token.ThrowIfCancellationRequested();
				if(entry == null)
					throw Invalid("The song archive contains an empty entry.");
				if(IsSymlink(entry))
					throw Invalid("Symbolic links and reparse points are not allowed in song archives.");

				var isDirectory = entry.FullName != null && entry.FullName.EndsWith("/", StringComparison.Ordinal);
				var normalizedPath = NormalizeArchivePath(entry.FullName, isDirectory);
				if(!names.Add(normalizedPath))
					throw Invalid("The song archive contains duplicate paths.");

				long length;
				try { length = entry.Length; }
				catch(InvalidDataException exception) { throw Invalid("The song archive has an invalid entry size.", exception); }
				if(length < 0 || (!isDirectory && length > MaxEntryBytes))
					throw Invalid("A song archive entry exceeds the per-file size limit.");
				if(!isDirectory) {
					if(totalBytes > MaxTotalBytes - length)
						throw Invalid("The song archive exceeds the total uncompressed size limit.");
					totalBytes += length;
				}

				var record = new ArchiveEntryInfo {
					Entry = entry,
					Path = normalizedPath,
					IsDirectory = isDirectory,
					Length = length
				};
				entries.Add(record);
				if(!isDirectory && string.Equals(normalizedPath, "Info.dat", StringComparison.OrdinalIgnoreCase))
					infoEntry = record;
			}

			if(infoEntry == null)
				throw Invalid("The song archive does not contain a root Info.dat.");
			return entries;
		}

		private static string NormalizeArchivePath(string value, bool isDirectory) {
			if(string.IsNullOrEmpty(value) || value.IndexOf('\\') >= 0 || value.IndexOf('\0') >= 0)
				throw Invalid("The song archive contains an invalid path.");
			if(value.StartsWith("/", StringComparison.Ordinal) || value.IndexOf(':') >= 0)
				throw Invalid("Absolute paths and alternate data streams are not allowed in song archives.");

			var path = isDirectory ? value.Substring(0, value.Length - 1) : value;
			if(path.Length == 0)
				throw Invalid("The song archive contains an empty directory path.");
			var segments = path.Split(new[] { '/' });
			if(segments.Length == 0)
				throw Invalid("The song archive contains an invalid path.");
			foreach(var segment in segments)
				ValidatePathSegment(segment, "archive path");
			return string.Join("/", segments);
		}

		private static string NormalizeReference(string value) {
			if(string.IsNullOrWhiteSpace(value) || value.IndexOf('\\') >= 0 || value.IndexOf('\0') >= 0)
				throw Invalid("Info.dat contains an invalid file reference.");
			if(value.StartsWith("/", StringComparison.Ordinal) || value.IndexOf(':') >= 0)
				throw Invalid("Info.dat contains an absolute or alternate-data-stream reference.");
			var segments = value.Split(new[] { '/' });
			if(segments.Length == 0)
				throw Invalid("Info.dat contains an invalid file reference.");
			foreach(var segment in segments)
				ValidatePathSegment(segment, "Info.dat reference");
			return string.Join("/", segments);
		}

		private static void ValidatePathSegment(string segment, string context) {
			if(string.IsNullOrEmpty(segment) || segment == "." || segment == ".." || segment.Length > 255 ||
				segment.TrimEnd(' ', '.') != segment || segment.IndexOf(':') >= 0)
				throw Invalid("The " + context + " contains traversal, a drive, or an invalid segment.");

			foreach(var character in segment) {
				if(character < 0x20 || character == '<' || character == '>' || character == '"' ||
					character == '|' || character == '?' || character == '*')
					throw Invalid("The " + context + " contains an invalid Windows filename character.");
			}

			var deviceName = segment;
			var dot = deviceName.IndexOf('.');
			if(dot >= 0)
				deviceName = deviceName.Substring(0, dot);
			deviceName = deviceName.TrimEnd(' ', '.');
			if(IsReservedDeviceName(deviceName))
				throw Invalid("The " + context + " contains a reserved Windows device name.");
		}

		private static bool IsReservedDeviceName(string value) {
			if(value.Equals("CON", StringComparison.OrdinalIgnoreCase) || value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
				value.Equals("AUX", StringComparison.OrdinalIgnoreCase) || value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
				return true;
			if(value.Length == 4 && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
				return value[3] >= '1' && value[3] <= '9';
			return false;
		}

		private static bool IsSymlink(ZipArchiveEntry entry) {
			var attributes = unchecked((uint)entry.ExternalAttributes);
			var unixMode = (attributes >> 16) & 0xF000u;
			return unixMode == 0xA000u || (attributes & 0x400u) != 0;
		}

		private static string CreateStageDirectory(string stagingRoot) {
			try {
				Directory.CreateDirectory(stagingRoot);
				for(var attempt = 0; attempt < 8; attempt++) {
					var stage = Path.Combine(stagingRoot, ".song-package-" + Guid.NewGuid().ToString("N"));
					try {
						Directory.CreateDirectory(stage);
						return stage;
					} catch(IOException) when(Directory.Exists(stage)) { }
				}
				throw new IOException("Could not create a unique song staging directory.");
			} catch(UnauthorizedAccessException exception) {
				throw new SongPackageIOException("Could not create a song staging directory.", exception);
			}
		}

		private static void ExtractEntries(IReadOnlyList<ArchiveEntryInfo> entries, string stagePath,
			CancellationToken token, Action<float> progress) {
			long totalBytes = entries.Where(x => !x.IsDirectory).Sum(x => x.Length);
			long completedBytes = 0;
			var denominator = Math.Max(1L, totalBytes);
			var buffer = new byte[64 * 1024];

			foreach(var item in entries) {
				token.ThrowIfCancellationRequested();
				var destination = CombineRelative(stagePath, item.Path);
				if(!IsSameOrWithin(destination, stagePath))
					throw Invalid("The song archive path escapes its staging directory.");

				if(item.IsDirectory) {
					Directory.CreateDirectory(destination);
					continue;
				}

				var parent = Path.GetDirectoryName(destination);
				if(string.IsNullOrEmpty(parent))
					throw Invalid("The song archive contains an invalid destination path.");
				Directory.CreateDirectory(parent);
				long copied = 0;
				try {
					using(var input = item.Entry.Open())
					using(var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
						int read;
						while((read = input.Read(buffer, 0, buffer.Length)) > 0) {
							token.ThrowIfCancellationRequested();
							copied += read;
							if(copied > item.Length || copied > MaxEntryBytes)
								throw Invalid("A song archive entry exceeds the per-file size limit.");
							output.Write(buffer, 0, read);
							completedBytes += read;
							Report(progress, (float)Math.Min(1d, (double)completedBytes / denominator));
						}
						output.Flush(true);
					}
				} catch(InvalidDataException exception) {
					throw new InvalidSongPackageException("The song archive entry could not be read.", exception);
				}
				if(copied != item.Length)
					throw Invalid("The song archive entry ended before its declared size.");
				Report(progress, (float)Math.Min(1d, (double)completedBytes / denominator));
			}

			Report(progress, 1f);
		}

		private static void ValidateInfoAndReferences(string stagePath, IReadOnlyList<ArchiveEntryInfo> entries, CancellationToken token) {
			token.ThrowIfCancellationRequested();
			var files = new Dictionary<string, ArchiveEntryInfo>(PathComparer);
			foreach(var entry in entries)
				if(!entry.IsDirectory)
					files.Add(entry.Path, entry);

			var infoPath = CombineRelative(stagePath, "Info.dat");
			var infoEntry = files.Values.FirstOrDefault(x => string.Equals(x.Path, "Info.dat", StringComparison.OrdinalIgnoreCase));
			if(infoEntry == null || !File.Exists(infoPath))
				throw Invalid("The song archive does not contain Info.dat.");

			byte[] data;
			try { data = File.ReadAllBytes(infoPath); }
			catch(UnauthorizedAccessException exception) { throw new SongPackageIOException("Info.dat could not be read.", exception); }
			JObject info;
			try {
				var json = StrictUtf8.GetString(data);
				if(json.Length > 0 && json[0] == '\uFEFF')
					json = json.Substring(1);
				info = JObject.Parse(json);
			} catch(Exception exception) when(exception is DecoderFallbackException || exception is JsonException || exception is ArgumentException) {
				throw Invalid("Info.dat is not valid UTF-8 JSON.", exception);
			}

			var references = ReadReferences(info);
			foreach(var reference in references) {
				token.ThrowIfCancellationRequested();
				if(!files.ContainsKey(reference))
					throw Invalid("Info.dat references a missing file: " + reference);
			}
		}

		private static List<string> ReadReferences(JObject info) {
			var version = RequiredString(info, "_version", "version");
			var dot = version.IndexOf('.');
			var majorText = dot >= 0 ? version.Substring(0, dot) : version;
			int major;
			if(!int.TryParse(majorText, out major) || (major != 2 && major != 3 && major != 4))
				throw Invalid("Info.dat uses an unsupported schema version: " + version);

			var references = new List<string>();
			var seen = new HashSet<string>(PathComparer);
			Action<string> add = value => {
				var normalized = NormalizeReference(value);
				if(seen.Add(normalized))
					references.Add(normalized);
			};

			if(major == 4) {
				var audio = info["audio"] as JObject;
				if(audio == null)
					throw Invalid("Info.dat v4 has no audio object.");
				add(RequiredString(audio, "songFilename"));
				AddOptionalString(info, "songPreviewFilename", add);
				AddOptionalString(audio, "audioDataFilename", add);
				add(RequiredString(info, "coverImageFilename"));

				var difficulties = info["difficultyBeatmaps"] as JArray;
				if(difficulties == null || difficulties.Count == 0)
					throw Invalid("Info.dat v4 contains no difficulty beatmaps.");
				foreach(var item in difficulties) {
					var difficulty = item as JObject;
					if(difficulty == null)
						throw Invalid("Info.dat v4 contains an invalid difficulty entry.");
					add(RequiredString(difficulty, "beatmapDataFilename"));
					AddOptionalString(difficulty, "lightshowDataFilename", add);
				}
			} else {
				add(RequiredString(info, "_songFilename"));
				add(RequiredString(info, "_coverImageFilename"));
				var sets = info["_difficultyBeatmapSets"] as JArray;
				if(sets == null || sets.Count == 0)
					throw Invalid("Info.dat contains no difficulty beatmap sets.");
				var difficultyCount = 0;
				foreach(var setToken in sets) {
					var set = setToken as JObject;
					if(set == null)
						throw Invalid("Info.dat contains an invalid difficulty set.");
					var difficulties = set["_difficultyBeatmaps"] as JArray;
					if(difficulties == null)
						throw Invalid("Info.dat contains an invalid difficulty list.");
					foreach(var item in difficulties) {
						var difficulty = item as JObject;
						if(difficulty == null)
							throw Invalid("Info.dat contains an invalid difficulty entry.");
						add(RequiredString(difficulty, "_beatmapFilename"));
						difficultyCount++;
					}
				}
				if(difficultyCount == 0)
					throw Invalid("Info.dat contains no difficulty beatmaps.");
			}

			return references;
		}

		private static void AddOptionalString(JObject objectValue, string propertyName, Action<string> add) {
			if(!objectValue.TryGetValue(propertyName, out var value) || value == null || value.Type == JTokenType.Null)
				return;
			if(value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value))
				throw Invalid("Info.dat contains an invalid " + propertyName + " reference.");
			add((string)value);
		}

		private static string RequiredString(JObject objectValue, params string[] propertyNames) {
			foreach(var propertyName in propertyNames) {
				if(!objectValue.TryGetValue(propertyName, out var value) || value == null || value.Type == JTokenType.Null)
					continue;
				if(value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value))
					throw Invalid("Info.dat contains an invalid " + propertyName + " value.");
				return (string)value;
			}
			throw Invalid("Info.dat is missing " + propertyNames[0] + ".");
		}

		internal static string PublishPrepared(string stagePath, string customLevelsRoot, string folderName, CancellationToken token) {
			if(stagePath == null)
				throw new SongPackageIOException("The song staging directory was not created.");
			token.ThrowIfCancellationRequested();
			try {
				Directory.CreateDirectory(customLevelsRoot);
				if(HasReparsePointInExistingPath(customLevelsRoot))
					throw new SongPackageIOException("The song library is a reparse point.");

				var requested = CombineRelative(customLevelsRoot, folderName);
				if(Directory.Exists(requested) && !IsReparsePoint(requested) && AreFilesIdentical(stagePath, requested, token)) {
					CleanupStage(stagePath);
					return requested;
				}

				for(var suffix = 0; suffix < 100000; suffix++) {
					token.ThrowIfCancellationRequested();
					var candidate = suffix == 0 ? requested : requested + " (" + suffix + ")";
					if(CandidateExists(candidate))
						continue;
					try {
						Directory.Move(stagePath, candidate);
						return candidate;
					} catch(IOException) {
						if(CandidateExists(candidate))
							continue;
						throw;
					}
				}
				throw new SongPackageIOException("Could not allocate a song directory name.");
			} catch(UnauthorizedAccessException exception) {
				throw new SongPackageIOException("The song archive could not be published.", exception);
			}
		}

		private static bool CandidateExists(string path) {
			if(Directory.Exists(path) || File.Exists(path))
				return true;
			try {
				return File.GetAttributes(path) != 0;
			} catch(FileNotFoundException) { return false; }
			catch(DirectoryNotFoundException) { return false; }
			catch(UnauthorizedAccessException) { return true; }
		}

		private static bool AreFilesIdentical(string left, string right, CancellationToken token) {
			var leftFiles = new Dictionary<string, FileInfo>(PathComparer);
			var rightFiles = new Dictionary<string, FileInfo>(PathComparer);
			if(!TryCollectFiles(left, left, leftFiles, token) || !TryCollectFiles(right, right, rightFiles, token))
				return false;
			if(leftFiles.Count != rightFiles.Count)
				return false;
			foreach(var pair in leftFiles) {
				FileInfo other;
				if(!rightFiles.TryGetValue(pair.Key, out other) || pair.Value.Length != other.Length)
					return false;
				if(!HashesEqual(pair.Value.FullName, other.FullName, token))
					return false;
			}
			return true;
		}

		private static bool TryCollectFiles(string root, string current, IDictionary<string, FileInfo> files, CancellationToken token) {
			DirectoryInfo directory;
			try {
				directory = new DirectoryInfo(current);
				if(!directory.Exists || IsReparsePoint(directory.FullName))
					return false;
				foreach(var item in directory.GetFileSystemInfos()) {
					token.ThrowIfCancellationRequested();
					if(IsReparsePoint(item.FullName))
						return false;
					var relative = item.FullName.Substring(root.Length).TrimStart(DirectorySeparators);
					if(item is DirectoryInfo) {
						if(!TryCollectFiles(root, item.FullName, files, token))
							return false;
					} else if(item is FileInfo) {
						if(!files.ContainsKey(relative))
							files.Add(relative, (FileInfo)item);
						else
							return false;
					}
				}
				return true;
			} catch(IOException) { return false; }
			catch(UnauthorizedAccessException) { return false; }
		}

		private static bool HashesEqual(string left, string right, CancellationToken token) {
			return ByteArraysEqual(ComputeHash(left, token), ComputeHash(right, token));
		}

		private static byte[] ComputeHash(string path, CancellationToken token) {
			using(var hash = SHA256.Create())
			using(var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
				var buffer = new byte[64 * 1024];
				int read;
				while((read = stream.Read(buffer, 0, buffer.Length)) > 0) {
					token.ThrowIfCancellationRequested();
					hash.TransformBlock(buffer, 0, read, buffer, 0);
				}
				hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
				return hash.Hash;
			}
		}

		private static bool ByteArraysEqual(byte[] left, byte[] right) {
			if(left == null || right == null || left.Length != right.Length)
				return false;
			var same = true;
			for(var i = 0; i < left.Length; i++)
				same &= left[i] == right[i];
			return same;
		}

		private static string CombineRelative(string root, string relativePath) {
			var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			return Path.GetFullPath(path);
		}

		private static bool IsSameOrWithin(string path, string parent) {
			path = TrimDirectorySeparator(path);
			parent = TrimDirectorySeparator(parent);
			return string.Equals(path, parent, PathComparison) ||
				path.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison) ||
				path.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);
		}

		private static string TrimDirectorySeparator(string path) {
			var root = Path.GetPathRoot(path);
			if(!string.IsNullOrEmpty(root) && string.Equals(path, root, PathComparison))
				return root;
			return path.TrimEnd(DirectorySeparators);
		}

		private static bool IsReparsePoint(string path) {
			try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
			catch(FileNotFoundException) { return false; }
			catch(DirectoryNotFoundException) { return false; }
		}

		private static bool HasReparsePointInExistingPath(string path) {
			try {
				var current = new DirectoryInfo(TrimDirectorySeparator(path));
				while(current != null) {
					if(current.Exists && IsReparsePoint(current.FullName))
						return true;
					current = current.Parent;
				}
				return false;
			} catch(IOException) { return true; }
			catch(UnauthorizedAccessException) { return true; }
		}

		private static bool IsReparsePoint(FileSystemInfo item) {
			try { return (item.Attributes & FileAttributes.ReparsePoint) != 0; }
			catch(IOException) { return true; }
			catch(UnauthorizedAccessException) { return true; }
		}

		internal static void CleanupStage(string stagePath) {
			if(string.IsNullOrEmpty(stagePath) || !Directory.Exists(stagePath))
				return;
			try { Directory.Delete(stagePath, true); }
			catch { }
		}

		private static InvalidSongPackageException Invalid(string message, Exception inner = null) {
			return inner == null ? new InvalidSongPackageException(message) : new InvalidSongPackageException(message, inner);
		}

		private static void Report(Action<float> progress, float value) {
			if(progress != null)
				progress(Math.Max(0f, Math.Min(1f, value)));
		}

		private static StringComparison PathComparison {
			get { return StringComparison.OrdinalIgnoreCase; }
		}
	}
}
