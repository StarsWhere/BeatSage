using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BeatSaberPlaylistsLib;
using BeatSaberPlaylistsLib.Types;

namespace BeatSage;

// Only entered after a playlist consumer is enabled. The library remains optional at runtime.
public static class BeatSagePlaylist
{
    public const string Filename = "BeatSage";

    public static bool IsBeatSageMapper(string mapper) =>
        string.Equals(mapper?.Trim(), "Beat Sage", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mapper?.Trim(), "BeatSage", StringComparison.OrdinalIgnoreCase);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Sync(PlaylistManager manager, IEnumerable<ISong> songs)
    {
        // SongCore has the authoritative content hashes. Folder names and titles are not identities.
        var candidates = songs.Where(song => song != null && song.Hash != null && song.Hash.Length == 40
            && song.Hash.All(Uri.IsHexDigit)).GroupBy(song => song.Hash, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (candidates.Length == 0) return 0;
        var playlist = manager.GetPlaylist(Filename + ".bplist", false);
        if (playlist == null && File.Exists(Path.Combine(manager.PlaylistPath, Filename + ".bplist")))
            throw new InvalidDataException("The Beat Sage playlist could not be read; the original file was preserved");
        playlist = playlist ?? manager.CreatePlaylist(Filename, "Beat Sage", "Beat Sage", "");
        var hashes = new HashSet<string>(playlist.Select(song => song.Hash).Where(hash => hash != null), StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var song in candidates)
            if (hashes.Add(song.Hash))
            {
                if (playlist.Add(song) == null) throw new InvalidDataException("The playlist library rejected a generated song");
                added++;
            }
        if (added == 0 && !manager.IsPlaylistChanged(playlist)) return 0;
        // The shared library owns bplist serialization and notifies PlaylistManager/MorePlaylists.
        playlist.RaisePlaylistChanged();
        manager.StorePlaylist(playlist);
        manager.RequestRefresh("BeatSage");
        return added;
    }
}
