using SongPackageIO;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace SongPackageIOTests {
	internal static class Program {
		private static readonly Encoding Utf8 = new UTF8Encoding(false);
		private static string root;
		private static int checks;

		private static void Main() {
			root = Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "song-package-io-tests", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			try {
				TestValidLegacy();
				TestValidModern();
				TestPrepareAndPublishBoundary();
				TestTraversalAndDuplicatePaths();
				TestMissingReference();
				TestEntryCountLimit();
				TestSizeCaps();
				TestCancellationCleanup();
				TestIdempotentRepeatAndCollision();
				Console.WriteLine("SongPackageIO tests passed ({0} checks).", checks);
			} catch(Exception exception) {
				Console.Error.WriteLine("SongPackageIO tests failed: " + exception.GetType().FullName);
				Console.Error.WriteLine(exception.Message);
				Console.Error.WriteLine(exception.StackTrace);
				Environment.ExitCode = 1;
			} finally {
				try { if(Directory.Exists(root)) Directory.Delete(root, true); } catch { }
			}
		}

		private static void TestValidLegacy() {
			var files = new Dictionary<string, byte[]> {
				["Info.dat"] = Bytes("{\"_version\":\"2.0.0\",\"_songFilename\":\"song.ogg\",\"_coverImageFilename\":\"cover.jpg\",\"_difficultyBeatmapSets\":[{\"_beatmapCharacteristicName\":\"Standard\",\"_difficultyBeatmaps\":[{\"_difficulty\":\"Expert\",\"_beatmapFilename\":\"Expert.dat\"}]}]}"),
				["song.ogg"] = new byte[] { 1, 2, 3, 4 },
				["cover.jpg"] = new byte[] { 5, 6 },
				["Expert.dat"] = Bytes("{}"),
				["preserved.bin"] = new byte[] { 0, 255, 9 }
			};
			var destination = Install(files, "legacy");
			Check(File.Exists(Path.Combine(destination, "Info.dat")), "legacy Info.dat published");
			Check(ReadBytes(Path.Combine(destination, "preserved.bin"))[1] == 255, "legacy bytes preserved");
		}

		private static void TestValidModern() {
			var files = new Dictionary<string, byte[]> {
				["Info.dat"] = Bytes("{\"version\":\"4.0.0\",\"song\":{\"title\":\"Modern\"},\"audio\":{\"songFilename\":\"Audio/song.ogg\",\"audioDataFilename\":\"Audio/BPMInfo.dat\"},\"songPreviewFilename\":\"Audio/preview.ogg\",\"coverImageFilename\":\"Images/cover.png\",\"difficultyBeatmaps\":[{\"characteristic\":\"Standard\",\"difficulty\":\"Expert\",\"beatmapDataFilename\":\"Maps/Expert.dat\",\"lightshowDataFilename\":\"Maps/Lightshow.dat\"}]}"),
				["Audio/song.ogg"] = new byte[] { 1 },
				["Audio/preview.ogg"] = new byte[] { 2 },
				["Audio/BPMInfo.dat"] = new byte[] { 3 },
				["Images/cover.png"] = new byte[] { 4 },
				["Maps/Expert.dat"] = Bytes("{}"),
				["Maps/Lightshow.dat"] = Bytes("{}")
			};
			var destination = Install(files, "modern");
			Check(File.Exists(Path.Combine(destination, "Maps", "Lightshow.dat")), "v4 lightshow published");
		}

		private static void TestPrepareAndPublishBoundary() {
			var customRoot = Path.Combine(root, "prepared-custom");
			var stagingRoot = Path.Combine(root, "prepared-stage");
			PreparedPackage prepared;
			using(var stream = CreateZip(LegacyFiles()))
				prepared = PackageImporter.Prepare(stream, customRoot, "prepared", stagingRoot, CancellationToken.None);
			try {
				Check(!Directory.Exists(Path.Combine(customRoot, "prepared")), "prepare does not publish early");
				var published = prepared.Publish(CancellationToken.None);
				Check(Directory.Exists(published), "prepared package publishes on demand");
			} finally {
				prepared.Dispose();
			}
		}

		private static void TestTraversalAndDuplicatePaths() {
			var valid = LegacyFiles();
			var traversal = new Dictionary<string, byte[]>(valid) { ["../outside.txt"] = new byte[] { 1 } };
			ExpectInvalid(traversal, "traversal rejected");
			var duplicate = new Dictionary<string, byte[]>(valid) { ["info.DAT"] = Bytes("duplicate") };
			ExpectInvalid(duplicate, "case-insensitive duplicate rejected");
			var ads = new Dictionary<string, byte[]>(valid) { ["audio:stream"] = new byte[] { 1 } };
			ExpectInvalid(ads, "alternate data stream rejected");
			var reserved = new Dictionary<string, byte[]>(valid) { ["CON.txt"] = new byte[] { 1 } };
			ExpectInvalid(reserved, "reserved device name rejected");
		}

		private static void TestMissingReference() {
			var files = LegacyFiles();
			files.Remove("song.ogg");
			ExpectInvalid(files, "missing audio reference rejected");
		}

		private static void TestEntryCountLimit() {
			var files = LegacyFiles();
			for(var i = 0; i < PackageImporter.MaxEntryCount; i++)
				files["extra" + i + ".bin"] = new byte[] { 0 };
			ExpectInvalid(files, "entry count limit rejected");
		}

		private static void TestSizeCaps() {
			var individual = LegacyFiles();
			individual["large.bin"] = new byte[] { 0 };
			ExpectInvalid(CreateZipWithDeclaredSize(individual, "large.bin", PackageImporter.MaxEntryBytes + 1), "individual size cap rejected");

			var total = LegacyFiles();
			total["large-one.bin"] = new byte[] { 0 };
			total["large-two.bin"] = new byte[] { 0 };
			var oversizedTotal = CreateZipWithDeclaredSize(total, "large-one.bin", PackageImporter.MaxEntryBytes);
			var patched = CreateZipWithDeclaredSize(total, "large-two.bin", PackageImporter.MaxEntryBytes);
			using(oversizedTotal)
			using(patched) {
				// Patch both central-directory declarations into one archive so the
				// importer rejects the total before opening either large stream.
				var data = patched.ToArray();
				var first = oversizedTotal.ToArray();
				Buffer.BlockCopy(first, 0, data, 0, Math.Min(first.Length, data.Length));
				WriteDeclaredSize(data, "large-one.bin", PackageImporter.MaxEntryBytes);
				WriteDeclaredSize(data, "large-two.bin", PackageImporter.MaxEntryBytes);
				ExpectInvalid(new MemoryStream(data), "total size cap rejected");
			}
		}

		private static void TestCancellationCleanup() {
			var customRoot = Path.Combine(root, "cancel-custom");
			var stagingRoot = Path.Combine(root, "cancel-stage");
			using(var cancellation = new CancellationTokenSource()) {
				var cancelled = false;
				try {
					using(var stream = CreateZip(LegacyFiles()))
						PackageImporter.Install(stream, customRoot, "cancel", stagingRoot, cancellation.Token, progress => {
							if(progress > 0f && !cancelled) {
								cancelled = true;
								cancellation.Cancel();
							}
						});
					throw new Exception("cancellation did not stop import");
				} catch(OperationCanceledException) { }
				Check(!Directory.Exists(Path.Combine(customRoot, "cancel")), "cancel leaves no song directory");
				Check(!Directory.Exists(stagingRoot) || !Directory.GetDirectories(stagingRoot, ".song-package-*", SearchOption.TopDirectoryOnly).Any(), "cancel cleans task staging");
			}
		}

		private static void TestIdempotentRepeatAndCollision() {
			var files = LegacyFiles();
			var first = Install(files, "repeat");
			var second = Install(files, "repeat");
			Check(string.Equals(first, second, StringComparison.OrdinalIgnoreCase), "identical repeat is idempotent");
			files["song.ogg"] = new byte[] { 99, 98, 97 };
			var collision = Install(files, "repeat");
			Check(string.Equals(collision, first + " (1)", StringComparison.OrdinalIgnoreCase), "different repeat gets suffix");
			Check(File.Exists(Path.Combine(first, "preserved.bin")), "collision does not replace existing song");
		}

		private static Dictionary<string, byte[]> LegacyFiles() {
			return new Dictionary<string, byte[]> {
				["Info.dat"] = Bytes("{\"_version\":\"3.0.0\",\"_songFilename\":\"song.ogg\",\"_coverImageFilename\":\"cover.jpg\",\"_difficultyBeatmapSets\":[{\"_difficultyBeatmaps\":[{\"_beatmapFilename\":\"Expert.dat\"}]}]}"),
				["song.ogg"] = new byte[] { 7, 8 },
				["cover.jpg"] = new byte[] { 9 },
				["Expert.dat"] = Bytes("{}"),
				["preserved.bin"] = new byte[] { 10, 11 }
			};
		}

		private static string Install(Dictionary<string, byte[]> files, string name) {
			var customRoot = Path.Combine(root, name + "-custom");
			var stagingRoot = Path.Combine(root, name + "-stage");
			using(var stream = CreateZip(files))
				return PackageImporter.Install(stream, customRoot, name, stagingRoot, CancellationToken.None);
		}

		private static void ExpectInvalid(Dictionary<string, byte[]> files, string message) {
			using(var stream = CreateZip(files))
				ExpectInvalid(stream, message);
		}

		private static void ExpectInvalid(MemoryStream stream, string message) {
			try {
				using(stream)
					PackageImporter.Install(stream, Path.Combine(root, Guid.NewGuid().ToString("N")), "invalid", Path.Combine(root, "staging" + Guid.NewGuid().ToString("N")), CancellationToken.None);
				throw new Exception("archive was accepted");
			} catch(InvalidSongPackageException) {
				Check(true, message);
			}
		}

		private static MemoryStream CreateZipWithDeclaredSize(Dictionary<string, byte[]> files, string entryName, long size) {
			using(var source = CreateZip(files)) {
				var bytes = source.ToArray();
				WriteDeclaredSize(bytes, entryName, size);
				return new MemoryStream(bytes);
			}
		}

		private static void WriteDeclaredSize(byte[] bytes, string entryName, long size) {
			var target = (uint)size;
			for(var offset = 0; offset <= bytes.Length - 46; offset++) {
				if(ReadUInt32(bytes, offset) != 0x02014b50u)
					continue;
				var nameLength = ReadUInt16(bytes, offset + 28);
				var extraLength = ReadUInt16(bytes, offset + 30);
				var commentLength = ReadUInt16(bytes, offset + 32);
				if(offset + 46 + nameLength + extraLength + commentLength > bytes.Length)
					continue;
				var name = Utf8.GetString(bytes, offset + 46, nameLength);
				if(!string.Equals(name, entryName, StringComparison.Ordinal))
					continue;
				WriteUInt32(bytes, offset + 24, target);
				return;
			}
			throw new Exception("Could not find ZIP central entry " + entryName);
		}

		private static uint ReadUInt32(byte[] bytes, int offset) {
			return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
		}

		private static ushort ReadUInt16(byte[] bytes, int offset) {
			return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
		}

		private static void WriteUInt32(byte[] bytes, int offset, uint value) {
			bytes[offset] = (byte)value;
			bytes[offset + 1] = (byte)(value >> 8);
			bytes[offset + 2] = (byte)(value >> 16);
			bytes[offset + 3] = (byte)(value >> 24);
		}

		private static MemoryStream CreateZip(Dictionary<string, byte[]> files) {
			var stream = new MemoryStream();
			using(var archive = new ZipArchive(stream, ZipArchiveMode.Create, true, Utf8)) {
				foreach(var pair in files) {
					var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
					using(var output = entry.Open())
						output.Write(pair.Value, 0, pair.Value.Length);
				}
			}
			stream.Position = 0;
			return stream;
		}

		private static byte[] Bytes(string value) { return Utf8.GetBytes(value); }
		private static byte[] ReadBytes(string path) { return File.ReadAllBytes(path); }

		private static void Check(bool condition, string message) {
			checks++;
			if(!condition)
				throw new Exception("FAILED: " + message);
		}
	}
}
