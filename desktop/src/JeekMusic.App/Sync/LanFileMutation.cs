using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using JeekMusic.Core.Sync;
using Microsoft.Win32.SafeHandles;

namespace JeekMusic.Sync;

/// <summary>Windows directory/leaf handles pin the verified objects through conditional rename.
/// No path-based overwrite or unlink is used. Backups stay beside the target for recovery.</summary>
internal sealed class LanFileMutation : IDisposable
{
    readonly List<SafeFileHandle> directories=[];
    readonly string root,relative;
    readonly bool parentExists;
    FileStream? original,stage;
    string? stagePath;
    bool installed;
    public string FullPath { get; }
    public string? BackupRelative { get; private set; }
    public bool Changed=>installed || BackupRelative!=null;
    public LanFileMutation(string root,string relative,bool createParents)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Conditional file writes require Windows handles");
        LanFiles.Relative(relative); if(relative.Split('/').Length>64) throw new IOException("Path depth budget exceeded");
        this.root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); this.relative=relative;
        FullPath=Path.Combine(this.root,relative.Replace('/',Path.DirectorySeparatorChar));
        try
        {
            var ancestors=new Stack<string>();
            for(var d=new DirectoryInfo(this.root);d!=null;d=d.Parent) ancestors.Push(d.FullName);
            while(ancestors.TryPop(out var directory)) PinDirectory(directory);
            var parent=this.root;
            foreach(var part in relative.Split('/').SkipLast(1))
            {
                parent=Path.Combine(parent,part);
                try { PinDirectory(parent); }
                catch(FileNotFoundException) {
                    if(!createParents) { parentExists=false; return; }
                    Directory.CreateDirectory(parent);PinDirectory(parent);
                }
            }
            parentExists=true;
        }
        catch { Dispose(); throw; }
    }
    void PinDirectory(string path)
    {
        var h=Open(path,0x80,3,3,0x02200000); // READ_ATTRIBUTES, share read/write but never delete, OPEN_REPARSE_POINT.
        try { Verify(h,path,true); directories.Add(h); } catch { h.Dispose(); throw; }
    }
    static string NativePath(string path)=>path.StartsWith(@"\\?")?path:path.StartsWith(@"\\")?@"\\?\UNC\"+path[2..]:@"\\?\"+path;
    static SafeFileHandle Open(string path,uint access,uint share,uint creation,uint flags)
    {
        var h=CreateFileW(NativePath(Path.GetFullPath(path)),access,share,0,creation,flags,0);
        if(h.IsInvalid) { var error=Marshal.GetLastPInvokeError(); h.Dispose(); if(error is 2 or 3) throw new FileNotFoundException("File unavailable"); throw new IOException("File handle unavailable",new Win32Exception(error)); }
        return h;
    }
    static void Verify(SafeFileHandle handle,string expected,bool directory)
    {
        if(!GetFileInformationByHandleEx(handle,9,out AttributeTag info,8) || (info.Attributes&0x400)!=0 || ((info.Attributes&0x10)!=0)!=directory) throw new IOException("Linked or invalid file object");
        var buffer=new StringBuilder(32768);var n=GetFinalPathNameByHandleW(handle,buffer,(uint)buffer.Capacity,0);
        var path=buffer.ToString();if(path.StartsWith(@"\\?\UNC\")) path=@"\\"+path[8..];else if(path.StartsWith(@"\\?\")) path=path[4..];
        if(n==0 || n>=buffer.Capacity || !Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),StringComparison.OrdinalIgnoreCase)) throw new IOException("File handle boundary changed");
    }
    internal static FileStream OpenPrivate(string path,bool create=true)
    {
        var h=Open(path,0xC0010000,1,create?4u:3u,0x40200000);
        try { Verify(h,path,false);return new FileStream(h,FileAccess.ReadWrite,128*1024,isAsync:true); }catch { h.Dispose();throw; }
    }
    internal static string Identity(FileStream file)
    {
        if(!GetFileInformationByHandleEx(file.SafeFileHandle,18,out FileIdentity info,24))throw new IOException("File identity unavailable");
        return $"{info.Volume:x16}{info.Low:x16}{info.High:x16}";
    }
    internal static void DeleteOwned(FileStream file,string identity)
    {
        if(Identity(file)!=identity || !SetFileInformationByHandle(file.SafeFileHandle,4,[1],1))throw new IOException("Owned staging cleanup deferred");
    }
    public async Task<string?> Observe(long maxBytes,CancellationToken ct,bool forMutation=false)
    {
        if(!parentExists) return null;
        SafeFileHandle handle;
        try { handle=Open(FullPath,forMutation?0x80010000u:0x80000000u,1,3,0x40200000); }
        catch(FileNotFoundException) { return null; }
        FileStream? stream=null;
        try
        {
            Verify(handle,FullPath,false);
            stream=new FileStream(handle,FileAccess.Read,128*1024,isAsync:true);
            if(stream.Length>maxBytes) throw new IOException("File byte budget exceeded");
            var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));
            ct.ThrowIfCancellationRequested();
            if(forMutation) { original=stream; stream=null; }
            return hash;
        }
        finally { if(stream!=null) stream.Dispose(); else if(original is null) handle.Dispose(); }
    }
    public async Task Stage(string source,string expected,long size,string operation,CancellationToken ct)
    {
        if(!parentExists || stage!=null || !Guid.TryParseExact(operation,"N",out _)) throw new IOException("Invalid staging state");
        stagePath=Path.Combine(Path.GetDirectoryName(FullPath)!,".jeek-sync-"+operation+".new");
        await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read,128*1024,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(input.Length!=size) throw new IOException("Upload size changed");
        SafeFileHandle h; bool resumed=false;
        try { h=Open(stagePath,0xC0010000,0,1,0x40200000); }
        catch(IOException e) when(e.InnerException is Win32Exception w && w.NativeErrorCode is 80 or 183) { h=Open(stagePath,0xC0010000,0,3,0x40200000); resumed=true; }
        FileStream? candidate=null;
        try {
            Verify(h,stagePath,false); candidate=new FileStream(h,FileAccess.ReadWrite,128*1024,isAsync:true);
            if(resumed) {
                if(candidate.Length>size) throw new IOException("Staging conflict; retained");
                var left=new byte[128*1024];var right=new byte[left.Length];long remaining=candidate.Length;
                while(remaining>0) { var n=(int)Math.Min(left.Length,remaining);await candidate.ReadExactlyAsync(left.AsMemory(0,n),ct);await input.ReadExactlyAsync(right.AsMemory(0,n),ct);if(!left.AsSpan(0,n).SequenceEqual(right.AsSpan(0,n))) throw new IOException("Staging conflict; retained");remaining-=n; }
                candidate.Position=0;input.Position=0;
            }
            stage=candidate; candidate=null;
        }
        catch { candidate?.Dispose(); if(candidate is null && stage is null) h.Dispose();throw; }
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[128*1024];long copied=0;int read;
        while((read=await input.ReadAsync(buffer,ct))>0) { copied=checked(copied+read);if(copied>size) throw new IOException("Upload size changed");digest.AppendData(buffer,0,read);await stage.WriteAsync(buffer.AsMemory(0,read),ct); }
        if(copied!=size || Convert.ToHexStringLower(digest.GetHashAndReset())!=expected) throw new IOException("Upload hash changed");
        await stage.FlushAsync(ct);stage.Flush(true);
    }
    public async Task<long?> VerifiedBackup(string operation,string expected,long maxBytes,CancellationToken ct)
    {
        if(!Guid.TryParseExact(operation,"N",out _))throw new IOException("Invalid operation identity");
        var path=Path.Combine(Path.GetDirectoryName(FullPath)!,".jeek-sync-"+operation+".bak");
        SafeFileHandle handle;try { handle=Open(path,0x80000000,1,3,0x40200000); }catch(FileNotFoundException) { return null; }
        try {
            Verify(handle,path,false);using var input=new FileStream(handle,FileAccess.Read,128*1024,true);
            if(input.Length>maxBytes)return null;
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))==expected?input.Length:null;
        } finally { handle.Dispose(); }
    }
    public void SetModified(long nanoseconds)
    {
        if(nanoseconds<=0 || nanoseconds>DateTime.MaxValue.Subtract(DateTime.UnixEpoch).Ticks*100d)return;
        var time=DateTime.UnixEpoch.AddTicks(nanoseconds/100).ToFileTimeUtc();
        if(stage is null || !SetFileTime(stage.SafeFileHandle,0,0,ref time))throw new IOException("File timestamp unavailable");
    }
    public async Task PurgeBackup(string operation,string expected,long maxBytes,CancellationToken ct,Action<Action> commit)
    {
        if(!Guid.TryParseExact(operation,"N",out _))throw new IOException("Invalid backup identity");
        var path=Path.Combine(Path.GetDirectoryName(FullPath)!,".jeek-sync-"+operation+".bak");
        using var input=OpenPrivate(path,false);
        if(input.Length>maxBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))!=expected)throw new IOException("Backup changed; retained");
        commit(()=>DeleteOwned(input,Identity(input)));
    }
    public void Commit(string operation,bool delete,Action<Action> authorizedCommit)
    {
        if(!Guid.TryParseExact(operation,"N",out _) || delete && original is null || !delete && stage is null) throw new IOException("Invalid commit state");
        var backup=Path.Combine(Path.GetDirectoryName(FullPath)!,".jeek-sync-"+operation+".bak");
        authorizedCommit(()=> {
            if(original!=null)
            {
                Rename(original.SafeFileHandle,backup);
                BackupRelative=Path.GetRelativePath(root,backup).Replace('\\','/');
            }
            if(!delete)
            {
                try { Rename(stage!.SafeFileHandle,FullPath); installed=true; }
                catch { if(original!=null) try { Rename(original.SafeFileHandle,FullPath);BackupRelative=null; } catch(IOException) { /* Never overwrite a newly appeared local file; keep backup. */ } throw; }
            }
        });
    }
    static void Rename(SafeFileHandle handle,string path)
    {
        var bytes=Encoding.Unicode.GetBytes(NativePath(Path.GetFullPath(path)));var offset=IntPtr.Size==8?20:12;
        var buffer=new byte[offset+bytes.Length]; // ReplaceIfExists=false, RootDirectory=NULL.
        BitConverter.GetBytes(bytes.Length).CopyTo(buffer,offset-4);bytes.CopyTo(buffer,offset);
        if(!SetFileInformationByHandle(handle,3,buffer,(uint)buffer.Length)) throw new IOException("Conditional rename deferred",new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    public void Dispose()
    {
        if(stage!=null && !installed) SetFileInformationByHandle(stage.SafeFileHandle,4,[1],1); // Delete only this still-open task-owned stage.
        stage?.Dispose();stage=null;original?.Dispose();original=null;
        foreach(var h in directories.AsEnumerable().Reverse()) h.Dispose();directories.Clear();
    }
    [StructLayout(LayoutKind.Sequential)] struct AttributeTag { public uint Attributes,Tag; }
    [StructLayout(LayoutKind.Sequential)] struct FileIdentity { public ulong Volume,Low,High; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string path,uint access,uint share,nint security,uint creation,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out AttributeTag info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileIdentity info,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,StringBuilder name,uint length,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,byte[] info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetFileTime(SafeFileHandle handle,nint creation,nint access,ref long write);
}
