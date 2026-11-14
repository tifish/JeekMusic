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
                try { var value=SingingLanguage.Parse(reader.GetString(2),reader.GetString(3)); if(value.State!="auto") result[key]=value; }
                catch(System.Text.Json.JsonException) { }
            }
            return result;
        }
    }
    internal void SaveLanguage(Track track, SingingLanguage? value, string source)
    {
        if(source is not ("AUTO" or "MANUAL")) throw new ArgumentException("Invalid language source");
        lock(Gate) {
            value ??= new SingingLanguage("auto",[],"MANUAL");
            Exec("INSERT INTO track_language(track_key,signature,data,source,updated_at) VALUES($k,$sig,$d,$s,$at) ON CONFLICT(track_key,source) DO UPDATE SET signature=excluded.signature,data=excluded.data,updated_at=max(excluded.updated_at,track_language.updated_at+1)",null,
                ("$k",track.TrackKey),("$sig",SingingLanguage.Signature(track)),("$d",value.ToJson().ToJsonString()),("$s",source),("$at",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }
}
