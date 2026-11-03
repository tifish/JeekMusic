namespace JeekMusic.Core.Sync;

/// <summary>One admission budget for all operation hashes, including plan-bound dependencies.</summary>
public sealed class LanHashBudget
{
    readonly object gate=new();
    readonly Dictionary<string,long> owners=new();
    DateTime window;
    long total;
    public bool Reserve(string owner,long bytes)
    {
        lock(gate) {
            if((DateTime.UtcNow-window).TotalHours>=1) { window=DateTime.UtcNow;owners.Clear();total=0; }
            var own=owners.GetValueOrDefault(owner);
            if(bytes<0 || bytes>8L*1024*1024*1024 || own+bytes>2L*1024*1024*1024*1024 || total+bytes>4L*1024*1024*1024*1024)return false;
            owners[owner]=own+bytes;total+=bytes;return true;
        }
    }
}
