using BeatSage;

namespace BeatSage.Tests;

internal static class PlaylistTests
{
    private static int assertions;
    public static int Run(string parent)
    {
        var root = Path.Combine(parent, "playlists");
        Directory.CreateDirectory(root);
        // The real library checks BSIPA for the optional cover generator. This process has no game plugins.
        var plugins = Type.GetType("IPA.Loader.PluginManager, IPA.Loader", true).GetField("_bsPlugins", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        plugins.SetValue(null, Activator.CreateInstance(plugins.FieldType));
        try
        {
            var manager = new BeatSaberPlaylistsLib.PlaylistManager(root, new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistHandler());
            int refreshes = 0;
            manager.PlaylistsRefreshRequested += (_, __) => refreshes++;
            var first = new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistSong(hash: new string('A', 40), songName: "Previously generated", mapper: "Beat Sage");
            var second = new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistSong(hash: new string('B', 40), songName: "Newly generated", mapper: "Beat Sage");
            Assert(BeatSagePlaylist.Sync(manager, Array.Empty<BeatSaberPlaylistsLib.Types.ISong>()) == 0 && !File.Exists(Path.Combine(root, "BeatSage.bplist")), "no empty playlist is created");
            Assert(BeatSagePlaylist.Sync(manager, new[] { first, first }) == 1, "historical songs are added once by hash");
            var playlist = manager.GetPlaylist("BeatSage.bplist", false);
            playlist.Title = "My AI Songs";
            playlist.Description = "Keep my notes";
            playlist.Add(new string('C', 40), "Manual entry", null, "Human");
            manager.StorePlaylist(playlist);
            string file = Path.Combine(root, "BeatSage.bplist");
            string unchanged = File.ReadAllText(file);
            Assert(BeatSagePlaylist.Sync(manager, new[] { first }) == 0 && File.ReadAllText(file) == unchanged && refreshes == 1, "repeated scans neither rewrite nor refresh the playlist");
            Assert(BeatSagePlaylist.Sync(manager, new[] { first, second }) == 1 && refreshes == 2, "a later import appends and refreshes once");
            var reloaded = new BeatSaberPlaylistsLib.PlaylistManager(root, new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistHandler()).GetPlaylist("BeatSage.bplist", false);
            Assert(reloaded.Count == 3 && reloaded.Title == "My AI Songs" && reloaded.Description == "Keep my notes", "playlist metadata and manually added songs survive save and reload");
            Assert(BeatSagePlaylist.IsBeatSageMapper("Beat Sage") && BeatSagePlaylist.IsBeatSageMapper("beatsage") && !BeatSagePlaylist.IsBeatSageMapper("Human / Beat Sage Remix"), "source identification matches actual Beat Sage mapper identities");
            const string corrupt = "{invalid playlist";
            File.WriteAllText(file, corrupt);
            var damaged = new BeatSaberPlaylistsLib.PlaylistManager(root, new BeatSaberPlaylistsLib.Legacy.LegacyPlaylistHandler());
            bool rejected = false;
            try { BeatSagePlaylist.Sync(damaged, new[] { first }); } catch { rejected = true; }
            Assert(rejected && File.ReadAllText(file) == corrupt, "an unreadable existing playlist is never replaced");
        }
        finally { Directory.Delete(root, true); }
        return assertions;
    }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
