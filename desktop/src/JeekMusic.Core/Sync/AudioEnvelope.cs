using System.Buffers.Binary;
using System.Security.Cryptography;

namespace JeekMusic.Core.Sync;

/// <summary>Conservative contiguous media ranges. Unsupported layouts use complete transfer.</summary>
public sealed record AudioEnvelope(string Format,long Start,long Length)
{
    public long End=>checked(Start+Length);
    public const long MaximumMetadata=32L*1024*1024;
    public static bool Supports(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".flac" or ".wav" or ".m4a" or ".mp4";
    public static AudioEnvelope? Parse(Stream input,string path)
    {
        if(!Supports(path) || !input.CanSeek || input.Length<12)return null;
        try {
            byte[] Read(long p,int n) { if(p<0 || p>input.Length-n)throw new InvalidDataException();input.Position=p;var b=new byte[n];input.ReadExactly(b);return b; }
            long Le(byte[] b,int p)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p,4));
            long Be(byte[] b,int p)=>BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p,4));
            var head=Read(0,12);var size=input.Length;var extension=Path.GetExtension(path).ToLowerInvariant();
            AudioEnvelope? Result(string format,long start,long length)=>length>0 && start>=0 && start<=size-length && size-length<=MaximumMetadata?new(format,start,length):null;
            if(extension==".mp3") {
                long start=0,end=size;
                if(head.AsSpan(0,3).SequenceEqual("ID3"u8)) {
                    if(head[3] is <2 or >4 || head[4]!=0 || head.AsSpan(6,4).ContainsAnyInRange((byte)128,byte.MaxValue))return null;
                    start=10L+(head[6]<<21|head[7]<<14|head[8]<<7|head[9]);
                    if(head[3]==4 && (head[5]&16)!=0) { if(!Read(start,3).AsSpan().SequenceEqual("3DI"u8))return null;start+=10; }
                    if(start>MaximumMetadata)return null;
                }
                if(end>=128 && Read(end-128,3).AsSpan().SequenceEqual("TAG"u8))end-=128;
                if(end>=32 && Read(end-32,8).AsSpan().SequenceEqual("APETAGEX"u8))return null;
                var audio=Read(start,2);if(audio[0]!=255 || (audio[1]&0xe0)!=0xe0)return null;
                return Result("mp3",start,end-start);
            }
            if(extension==".flac" && head.AsSpan(0,4).SequenceEqual("fLaC"u8)) {
                long p=4;var count=0;
                while(++count<=4096) {
                    var block=Read(p,4);var length=block[1]<<16|block[2]<<8|block[3];
                    if(count==1 && ((block[0]&127)!=0 || length!=34) || (block[0]&127)==127)return null;
                    p=checked(p+4+length);if(p>size || p>MaximumMetadata)return null;
                    if((block[0]&128)!=0)return Result("flac",p,size-p);
                }
                return null;
            }
            if(extension==".wav" && head.AsSpan(0,4).SequenceEqual("RIFF"u8) && head.AsSpan(8,4).SequenceEqual("WAVE"u8)) {
                if(Le(head,4)+8!=size)return null;
                long p=12;AudioEnvelope? media=null;var count=0;var format=false;
                while(p<size && ++count<=100000) {
                    var chunk=Read(p,8);var length=Le(chunk,4);var end=checked(p+8+length);if(end>size)return null;
                    if(chunk.AsSpan(0,4).SequenceEqual("fmt "u8))format=true;
                    if(chunk.AsSpan(0,4).SequenceEqual("data"u8)) { if(media!=null)return null;media=Result("wav",p+8,length); }
                    p=checked(end+(length&1));
                }
                return p==size && format?media:null;
            }
            if(extension is ".m4a" or ".mp4" && head.AsSpan(4,4).SequenceEqual("ftyp"u8)) {
                long p=0;AudioEnvelope? media=null;var count=0;
                while(p<size && ++count<=100000) {
                    var box=Read(p,8);var length=Be(box,0);var header=8;
                    if(length==1) { var large=BinaryPrimitives.ReadUInt64BigEndian(Read(p+8,8));if(large>long.MaxValue)return null;length=(long)large;header=16; }
                    else if(length==0)length=size-p;
                    if(length<header || length>size-p)return null;
                    if(box.AsSpan(4,4).SequenceEqual("mdat"u8)) { if(media!=null)return null;media=Result("mp4",p+header,length-header); }
                    p=checked(p+length);
                }
                return p==size?media:null;
            }
            return null;
        }catch(Exception e) when(e is InvalidDataException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException) { return null; }
    }
    public static async Task<string> Digest(Stream input,long start,long length,CancellationToken ct,Action<int>? observed=null)
    {
        if(start<0 || length<0 || start>input.Length-length)throw new InvalidDataException("Invalid media range");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[131072];input.Position=start;
        while(length>0) { ct.ThrowIfCancellationRequested();var n=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(length,buffer.Length)),ct);if(n==0)throw new EndOfStreamException();hash.AppendData(buffer,0,n);observed?.Invoke(n);length-=n; }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
