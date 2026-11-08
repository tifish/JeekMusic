using JeekMusic.Analysis;

namespace JeekMusic.Library;

public sealed partial class LibraryDb
{
    internal Track? TrackById(long id)
    {
        lock(Gate) {
            using var cmd=Command($"SELECT {TrackColumns} FROM track WHERE id=$id");
            cmd.Parameters.AddWithValue("$id",id);
            return ReadTracks(cmd).SingleOrDefault();
        }
    }
    public IReadOnlyDictionary<string,SingingLanguage> Languages(IReadOnlyList<Track> tracks)
    {
        lock(Gate) {
            var signatures=tracks.ToDictionary(t=>t.TrackKey,SingingLanguage.Signature);
            var result=new Dictionary<string,SingingLanguage>();
            using var cmd=Command("SELECT track_key,signature,data,source FROM track_language" + (tracks.Count==1?" WHERE track_key=$k":"") + " ORDER BY CASE source WHEN 'MANUAL' THEN 1 ELSE 0 END");
            if(tracks.Count==1) cmd.Parameters.AddWithValue("$k",tracks[0].TrackKey);
            using var reader=cmd.ExecuteReader();
            while(reader.Read()) {
                var key=reader.GetString(0);
                if(signatures.GetValueOrDefault(key)!=reader.GetString(1)) continue;
                try { result[key]=SingingLanguage.Parse(reader.GetString(2),reader.GetString(3)); }
                catch(System.Text.Json.JsonException) { }
            }
            return result;
        }
    }
    internal void SaveLanguage(Track track, SingingLanguage? value, string source)
    {
        if(source is not ("AUTO" or "MANUAL")) throw new ArgumentException("Invalid language source");
        lock(Gate) {
            if(value is null) Exec("DELETE FROM track_language WHERE track_key=$k AND source=$s",null,("$k",track.TrackKey),("$s",source));
            else Exec("INSERT INTO track_language(track_key,signature,data,source) VALUES($k,$sig,$d,$s) ON CONFLICT(track_key,source) DO UPDATE SET signature=excluded.signature,data=excluded.data",null,
                ("$k",track.TrackKey),("$sig",SingingLanguage.Signature(track)),("$d",value.ToJson().ToJsonString()),("$s",source));
        }
    }
}
