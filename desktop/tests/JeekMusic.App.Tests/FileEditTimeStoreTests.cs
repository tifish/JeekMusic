using JeekMusic.Sync;
using Microsoft.Data.Sqlite;

namespace JeekMusic.App.Tests;

public sealed class FileEditTimeStoreTests
{
    [Fact] public void OnlyLastTimePersistsAndStaleFileMetadataDoesNotBorrowIt()
    {
        var directory=Path.Combine(Path.GetTempPath(),"jeek-last-edit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try {
            var database=Path.Combine(directory,"clock.db");var file=Path.Combine(directory,"A.flac");
            using(var clock=new FileEditTimeStore(database)) {
                Assert.Equal(0,clock.Read(file,10,100));clock.Set(file,10,100,1000);clock.Set(file,20,100,2000);
                Assert.Equal(0,clock.Read(file,10,100));Assert.Equal(0,clock.Read(file,20,101));Assert.Equal(2000,clock.Read(file,20,100));
            }
            using(var clock=new FileEditTimeStore(database))Assert.Equal(2000,clock.Read(file,20,100));
            using(var db=new SqliteConnection($"Data Source={database};Pooling=False")) {
                db.Open();using var count=db.CreateCommand();count.CommandText="SELECT COUNT(*) FROM latest_edit";Assert.Equal(1L,count.ExecuteScalar());
            }
            using(var clock=new FileEditTimeStore(database)) { clock.Set(file,20,100,0);Assert.Equal(0,clock.Read(file,20,100)); }
        } finally { Directory.Delete(directory,true); }
    }
}
