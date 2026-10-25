"""Reproducible, local-only synthetic tag fixtures; requires FFmpeg and Pillow.
No source music is read. Run from any directory; outputs only beside this script.
"""
from pathlib import Path
import hashlib,json,struct,subprocess
from PIL import Image
ROOT=Path(__file__).resolve().parent
LYRICS='[offset:125]\n[00:00.123]Synthetic café Ελληνικά 𠮷😀\n[00:00.123]合成中文译文 العربية\n[00:00.247]\n'
NEW='[offset:125]\n[00:00.123]New synthetic café Ελληνικά 𠮷😀\n[00:00.123]新的合成中文译文 العربية\n[00:00.247]\n'
for name,color in [('front.jpg',(0,120,120)),('back.jpg',(100,20,140))]:Image.new('RGB',(12,12),color).save(ROOT/name,quality=90)
def ffmpeg(name,codec,rate=16000):
 signal=f'sine=frequency=440:sample_rate={rate}' if codec=='wavpack' else f'anullsrc=r={rate}:cl=mono'
 cmd=['ffmpeg','-v','error','-y','-f','lavfi','-i',signal,'-t','0.3','-c:a',codec,'-metadata','title=Synthetic 中文 café 𠮷😀','-metadata','artist=Artist 一','-metadata','album=Album العربية','-metadata','date=2001','-metadata','comment=Private note café','-metadata','lyrics='+LYRICS,str(ROOT/name)]
 subprocess.run(cmd,check=True)
for name,codec,rate in [('native.flac','flac',16000),('native.ogg','libvorbis',16000),('native.opus','libopus',48000),('native.m4a','aac',16000),('native.wav','pcm_s16le',16000),('native.aiff','pcm_s16be',16000),('native.wma','wmav2',44100),('native.wv','wavpack',16000)]:ffmpeg(name,codec,rate)
# Opaque records exercise preservation independently of either platform's tag model.
PRIVATE=b'JEEK_OPAQUE\x00\xff\x83\x00_keep_private_2001'
b=(ROOT/'native.flac').read_bytes();p=4;blocks=[]
while True:
 last=b[p]&128;n=int.from_bytes(b[p+1:p+4],'big');t=b[p]&127;body=b[p+4:p+4+n];p+=4+n
 if t==4:
  q=4+struct.unpack_from('<I',body)[0];count=struct.unpack_from('<I',body,q)[0];extra=['ARTIST=Artist 二','JEEK_PRIVATE=保留私有 café 𠮷😀']
  body=body[:q]+struct.pack('<I',count+len(extra))+body[q+4:]+b''.join(struct.pack('<I',len(s.encode()))+s.encode() for s in extra)
 blocks.append((t,body))
 if last:break
blocks.append((2,b'JEEK'+PRIVATE));(ROOT/'native.flac').write_bytes(b'fLaC'+b''.join(bytes([t|(128 if i==len(blocks)-1 else 0)])+len(body).to_bytes(3,'big')+body for i,(t,body) in enumerate(blocks))+b[p:])
def box(t,body):return struct.pack('>I',8+len(body))+t+body
def mp4_private(b):
 out=b'';p=0
 while p<len(b):
  n=struct.unpack_from('>I',b,p)[0];t=b[p+4:p+8];body=b[p+8:p+n]
  if t in [b'moov',b'udta']:body=mp4_private(body)
  elif t==b'meta':body=body[:4]+mp4_private(body[4:])
  elif t==b'ilst':body+=box(b'zzzz',box(b'data',struct.pack('>II',0,0)+PRIVATE))
  out+=box(t,body);p+=n
 return out
(ROOT/'native.m4a').write_bytes(mp4_private((ROOT/'native.m4a').read_bytes()))
def remove_udta(b):
 out=b'';p=0
 while p<len(b):
  n=struct.unpack_from('>I',b,p)[0];t=b[p+4:p+8];body=b[p+8:p+n]
  if t==b'moov':body=remove_udta(body)
  if t!=b'udta':out+=box(t,body)
  p+=n
 return out
(ROOT/'untagged.m4a').write_bytes(remove_udta((ROOT/'native.m4a').read_bytes()))
subprocess.run(['ffmpeg','-v','error','-y','-i',str(ROOT/'native.m4a'),'-map_metadata','0','-c','copy','-movflags','+faststart',str(ROOT/'faststart.m4a')],check=True)
for name,big in [('native.wav',False),('native.aiff',True)]:
 b=(ROOT/name).read_bytes();chunk=b'JEEK'+struct.pack('>I' if big else '<I',len(PRIVATE))+PRIVATE+(b'\0' if len(PRIVATE)%2 else b'')
 b+=chunk;b=b[:4]+struct.pack('>I' if big else '<I',len(b)-8)+b[8:];(ROOT/name).write_bytes(b)
import uuid
b=(ROOT/'native.wma').read_bytes();end=struct.unpack_from('<Q',b,16)[0];private=uuid.UUID('FEA70611-426D-4B81-A143-FA12167E0010').bytes_le+struct.pack('<Q',24+len(PRIVATE))+PRIVATE
b=b[:end]+private+b[end:];h=bytearray(b);struct.pack_into('<Q',h,16,end+len(private));struct.pack_into('<I',h,24,struct.unpack_from('<I',h,24)[0]+1);p=30
while p<end:
 n=struct.unpack_from('<Q',h,p+16)[0]
 if uuid.UUID(bytes_le=bytes(h[p:p+16]))==uuid.UUID('8CABDCA1-A947-11CF-8EE4-00C00C205365'):struct.pack_into('<Q',h,p+40,len(h))
 p+=n
(ROOT/'native.wma').write_bytes(h)
ffmpeg('raw.mp3','libmp3lame')
raw=(ROOT/'raw.mp3').read_bytes()
if raw[:3]==b'ID3':
 n=sum(v<<(21-7*i) for i,v in enumerate(raw[6:10]));raw=raw[10+n:]
(ROOT/'untagged.mp3').write_bytes(raw)
def sync(n):return bytes([n>>21&127,n>>14&127,n>>7&127,n&127])
def frame(code,body,ver):return code.encode()+ (sync(len(body)) if ver==4 else struct.pack('>I',len(body)))+b'\0\0'+body
for ver in [3,4]:
 def text(s):return (b'\x03'+s.encode()) if ver==4 else b'\x01\xff\xfe'+s.encode('utf-16-le')
 def uslt(s):return (b'\x03eng\0'+s.encode()) if ver==4 else b'\x01eng\xff\xfe\0\0\xff\xfe'+s.encode('utf-16-le')
 frames=b''.join(frame(k,text(v),ver) for k,v in [('TIT2','Synthetic 中文 café 𠮷😀'),('TPE1','Artist 一\0Artist 二' if ver==4 else 'Artist 一/Artist 二'),('TALB','Album العربية'),('TDRC' if ver==4 else 'TYER','2001')])
 frames+=frame('PRIV',b'org.jeekmusic.fixture\0\x00\xff\x00\x83\x01private',ver)+frame('TXXX',text('CUSTOM\0保持原始字段'),ver)+frame('USLT',uslt(LYRICS),ver)
 frames+=frame('APIC',bytes([3 if ver==4 else 1])+b'image/jpeg\0\x04'+text('背面 café 𠮷😀\0')[1:]+(ROOT/'back.jpg').read_bytes(),ver)
 v1=b'TAG'+bytes(range(125));(ROOT/f'id3v2{ver}.mp3').write_bytes(b'ID3'+bytes([ver,0,0])+sync(len(frames))+frames+raw+v1)
 # Byte-identical source except ambiguous lyric slots; cover edit may preserve, lyrics must refuse.
 named=frame('USLT',b'\x03deualternate\0'+LYRICS.encode(),4) if ver==4 else b''
 if named:(ROOT/'multiple-lyrics.mp3').write_bytes(b'ID3\4\0\0'+sync(len(frames+named))+frames+named+raw+v1)
(ROOT/'lyrics-before.lrc').write_text(LYRICS,encoding='utf-8',newline='')
(ROOT/'lyrics-after.lrc').write_text(NEW,encoding='utf-8',newline='')
(ROOT/'opaque-marker.bin').write_bytes(PRIVATE)
files=[]
for p in sorted(ROOT.iterdir()):
 if p.suffix not in ['.mp3','.flac','.ogg','.opus','.m4a','.wav','.aiff','.wma','.wv','.jpg','.lrc','.bin']:continue
 files.append(dict(id=p.stem,path=p.name,sha256=hashlib.sha256(p.read_bytes()).hexdigest(),bytes=p.stat().st_size,synthetic=True,expectedLyricsEdit='refuse' if p.name=='multiple-lyrics.mp3' else 'write-if-profile-supported'))
(ROOT/'manifest.json').write_text(json.dumps(dict(policyVersion=1,source='FFmpeg lavfi silence / 440Hz WavPack tone + procedural JPEG + explicitly framed synthetic metadata',ffmpegVersion=subprocess.run(['ffmpeg','-version'],capture_output=True,text=True).stdout.splitlines()[0],operations=['cover','lyrics','combined'],files=files),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Generated',len(files),'synthetic assets; never read source music')
