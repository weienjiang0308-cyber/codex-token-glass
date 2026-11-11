"""Protocol-only relay validation. No account, network or inference requests."""
import json, os, subprocess, sys, tempfile
from pathlib import Path

relay = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="codex-relay-test-") as folder:
    root = Path(folder)
    server = root / "mock.py"
    server.write_text('''import sys,json,time
def send(value): print(json.dumps(value,ensure_ascii=False),flush=True)
for line in sys.stdin:
 try: r=json.loads(line)
 except: print(line,end='',flush=True); continue
 send({"id":r.get("id"),"result":{"echo":r}})
 if r.get("method")=="thread/start":
  send({"method":"item/agentMessage/delta","params":{"threadId":"a","turnId":"t","delta":"PRIVATE_BODY_NEVER_LOG"}})
  time.sleep(.05)
  send({"method":"rawResponse/completed","params":{"threadId":"a","turnId":"t","responseId":"r1","usage":{"outputTokens":100},"usageMetadata":{"secret":"SECRET_NEVER_LOG"}}})
  send({"method":"item/agentMessage/delta","params":{"threadId":"a","turnId":"t","delta":"second"}})
  send({"method":"rawResponse/completed","params":{"threadId":"a","turnId":"t","responseId":"r1","usage":{"outputTokens":100}}})
  send({"method":"turn/completed","params":{"threadId":"a","turn":{"id":"old-turn"}}})
  time.sleep(.01)
  send({"method":"rawResponse/completed","params":{"threadId":"a","turnId":"t","responseId":"r2","usage":{"outputTokens":50}}})
  send({"method":"rawResponseItem/completed","params":{"item":{"text":"PRIVATE_BODY_NEVER_LOG"}}})
  send({"method":"rawResponse/completed","params":{"threadId":"b","turnId":"u","responseId":"tool-only","usage":{"outputTokens":20}}})
  send({"id":77,"method":"item/commandExecution/requestApproval","params":{"command":"echo hello"}})
''', encoding="utf-8")
    requests = [
        {"id":1,"method":"initialize","params":{"capabilities":{"optOutNotificationMethods":["rawResponse/completed","rawResponseItem/completed","turn/diff/updated"]}}},
        {"id":2,"method":"thread/start","params":{"experimentalRawEvents":False,"model":"unchanged","approvalPolicy":"never","unicodeProbe":"中文路径🙂\\\""*2000}},
        {"id":3,"method":"thread/resume","params":{"threadId":"existing"}},
        {"id":77,"result":{"decision":"accept"}},
    ]
    env = dict(os.environ, CODEX_RAW_REAL_EXE=sys.executable, CODEX_RAW_TRACE_DIR=str(root / "traces"), PYTHONIOENCODING="utf-8")
    result = subprocess.run([str(relay),str(server),"app-server"],input="\n".join(json.dumps(r,ensure_ascii=False) for r in requests)+"\n",
        capture_output=True,text=True,encoding="utf-8",env=env,timeout=15)
    assert result.returncode == 0, result.stderr
    received = [json.loads(line) for line in result.stdout.splitlines()]
    echoes = [r['result']['echo'] for r in received if 'result' in r and 'echo' in r['result']]
    assert echoes[0]['params']['capabilities']['optOutNotificationMethods'] == ['turn/diff/updated']
    assert echoes[0]['params']['capabilities']['experimentalApi'] is True
    assert echoes[1]['params'] == {**requests[1]['params'],'experimentalRawEvents':True}
    assert echoes[2:] == requests[2:]
    assert all(not r.get('method','').startswith('rawResponse') for r in received)
    assert any(r.get('method')=='item/agentMessage/delta' for r in received)
    assert any(r.get('id')==77 and 'method' in r for r in received)
    raw = next((root/'traces').glob('*.jsonl')).read_text()
    assert 'PRIVATE_BODY_NEVER_LOG' not in raw and 'SECRET_NEVER_LOG' not in raw
    records = [json.loads(line) for line in raw.splitlines()]
    responses = [r for r in records if r['kind']=='response']
    assert responses[0]['responseId']=='r1' and responses[0]['outputTokens']==100 and responses[0]['durationMs']>0
    assert responses[1]['responseId']=='r2' and responses[1]['durationMs']>0 and len(responses)==3
    assert responses[2]['responseId']=='tool-only' and responses[2]['firstExposedTicks'] is None and responses[2]['durationMs'] is None
    assert records[-1]['kind']=='end' and records[-1]['droppedRecords']==0
    blocked=root/'not-a-directory';blocked.write_text('file')
    failed_trace=subprocess.run([str(relay),str(server),'app-server'],input=json.dumps(requests[0])+'\n',
        capture_output=True,text=True,encoding='utf-8',env={**env,'CODEX_RAW_TRACE_DIR':str(blocked)},timeout=10)
    assert failed_trace.returncode==0 and json.loads(failed_trace.stdout)['id']==1
    echo=root/'echo.py';echo.write_text('import sys;sys.stdout.buffer.write(sys.stdin.buffer.read())')
    data=b'\x00\xffUTF8:'+ '中文🙂'.encode()+b'\n'
    passthrough=subprocess.run([str(relay),str(echo)],input=data,capture_output=True,env=env,timeout=5)
    assert passthrough.returncode==0 and passthrough.stdout==data
    print('PASS relay: Unicode, duplicate completion, isolated turns, trace failure, passthrough EOF; mock transport only')
