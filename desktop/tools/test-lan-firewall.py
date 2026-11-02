"""Opt-in exact real Windows rule lifecycle. No real library, public mapping or broad port rule."""
import argparse, json, os, shutil, subprocess, tempfile
from pathlib import Path
from mcp_client import McpClient


def main():
    p=argparse.ArgumentParser();p.add_argument('--exe',type=Path,required=True);p.add_argument('--bind',required=True);p.add_argument('--evidence',type=Path,required=True);a=p.parse_args()
    assert a.bind not in ('0.0.0.0','127.0.0.1'), 'Test binds one explicitly supplied local IPv4 only'
    reports=[]
    with tempfile.TemporaryDirectory(prefix='jeek-firewall-') as temp:
        root=Path(temp);data=root/'data';data.mkdir(); upgraded=root/'upgraded';shutil.copytree(a.exe.resolve().parent,upgraded)
        (data/'settings.json').write_text(json.dumps(dict(autoCheckUpdate=False,analysisMode='Manual',closeAction='Exit')))
        (data/'lan-sync.json').write_text(json.dumps(dict(enabled=False,bindAddress=a.bind,roots={},devices={})))
        def call(c,action,**args):
            r=c.call('lan_sync_control',dict(action=action,**args));assert not r.get('isError'),r
            return r['structuredContent']
        def checked(c,action,**args):
            call(c,action,**args);status=call(c,'firewall_repair');fw=status['firewall']
            needed=status['enabled'] and status['bindAddress']!='127.0.0.1'
            assert fw['state']==('configured' if needed else 'not_required'),fw
            assert fw['satisfied'] and fw['ownedRuleCount']==int(needed),fw
            assert fw['protocol']=='TCP' and fw['profiles']==['Domain','Private','Public'] and fw['remoteAddress']=='Any',fw
            reports.append(dict(action=action,enabled=status['enabled'],port=status['port'],firewall=fw))
            a.evidence.parent.mkdir(parents=True,exist_ok=True);a.evidence.write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
            print('PASS',action,status['bindAddress'],status['port'],fw['state'],flush=True);return status
        pid=None
        try:
            with McpClient(a.exe,data=data,timeout=180) as c:
                pid=c.call('get_diagnostics')['structuredContent']['processId']
                first=checked(c,'start',port=0);port=first['port'];assert port>0 and first['firewall']['localAddress']==a.bind
                again=checked(c,'firewall_repair');assert again['firewall']['ruleName']==first['firewall']['ruleName']
                checked(c,'stop')
                call(c,'configure',bindAddress=a.bind,port=port+1 if port<65535 else port-1)
                second=checked(c,'start');assert second['port']!=port and second['firewall']['ruleName']!=first['firewall']['ruleName']
            # Simulate a real update/crash: stop the old process without orderly service disposal.
            subprocess.run(['taskkill','/pid',str(pid),'/f'],check=True,capture_output=True);old_pid=pid;pid=None
            subprocess.run(['powershell.exe','-NoProfile','-Command',f'Get-Process -Id {int(old_pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10'],capture_output=True)
            with McpClient(upgraded/'JeekMusic.exe',data=data,timeout=180) as c:
                d=c.call('get_diagnostics')['structuredContent'];pid=d['processId'];assert Path(d['processPath']).resolve()==(upgraded/'JeekMusic.exe').resolve()
                migrated=checked(c,'firewall_repair');assert migrated['enabled'] and migrated['firewall']['program']==str(upgraded/'JeekMusic.exe')
                assert migrated['firewall']['ruleName']!=second['firewall']['ruleName'] and migrated['firewall']['ownedRuleCount']==1
                checked(c,'stop');call(c,'configure',bindAddress='127.0.0.1',port=39843);loopback=checked(c,'start');assert loopback['firewall']['ownedRuleCount']==0
                checked(c,'stop')
        finally:
            if pid:
                try:
                    with McpClient(upgraded/'JeekMusic.exe',data=data,timeout=180) as c: checked(c,'stop')
                finally:
                    subprocess.run(['taskkill','/pid',str(pid),'/f'],capture_output=True)
                    subprocess.run(['powershell.exe','-NoProfile','-Command',f'Get-Process -Id {int(pid)} -ErrorAction SilentlyContinue | Wait-Process -Timeout 10'],capture_output=True)
        a.evidence.parent.mkdir(parents=True,exist_ok=True);a.evidence.write_text(json.dumps(reports,ensure_ascii=False,indent=2),encoding='utf-8')
    print('PASS real exact Windows rule create/query/idempotence/port update/upgrade path cleanup/stop/loopback cleanup; no broad ports or routing changes')

if __name__=='__main__':main()
