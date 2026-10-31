using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JeekMusic.Preferences;

/// <summary>Kill-on-close Windows job includes decoder children; enforce a 2 GiB aggregate commit limit.</summary>
internal sealed class WorkerBudget : IDisposable
{
    readonly SafeFileHandle job;
    public WorkerBudget(Process process)
    {
        job=CreateJobObject(0,null);
        if(job.IsInvalid) throw new Win32Exception();
        var limit=new ExtendedLimit { Basic=new BasicLimit { Flags=0x2000|0x200|0x100 },JobMemory=2UL*1024*1024*1024,ProcessMemory=2UL*1024*1024*1024 };
        if(!SetInformationJobObject(job,9,ref limit,Marshal.SizeOf<ExtendedLimit>()) || !AssignProcessToJobObject(job,process.Handle)) { job.Dispose(); throw new Win32Exception(); }
        // Restrict all compute libraries to the first two CPUs already allowed for this process.
        var allowed=(ulong)process.ProcessorAffinity.ToInt64(); ulong selected=0; int count=0;
        for(int i=0;i<64 && count<2;i++) if((allowed&(1UL<<i))!=0) { selected|=1UL<<i; count++; }
        if(selected!=0) process.ProcessorAffinity=(nint)selected;
    }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimit { public long ProcessTime,JobTime; public uint Flags; public nuint MinWorking,MaxWorking; public uint ActiveProcesses; public nuint Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit { public BasicLimit Basic; public Io Counters; public ulong ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job,int info,ref ExtendedLimit limit,int size);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    public void Dispose()=>job.Dispose();
}
