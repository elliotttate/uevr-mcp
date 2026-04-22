#!/usr/bin/env bash
# End-to-end test of the new pipeline against RoboQuest.
# Run in one shot because RoboQuest crashes ~60s post-UEVR injection.
set -e

EXE="E:/SteamLibrary/steamapps/common/RoboQuest/RoboQuest/Binaries/Win64/RoboQuest-Win64-Shipping.exe"
OUT=C:/temp/rq_e2e
MCP="E:/Github/uevr-mcp/mcp-server/bin/Release/net9.0/UevrMcpServer.exe"
mkdir -p "$OUT"

echo "==== 1. plugin_info ===="
"$MCP" plugin-info "$EXE" | tee "$OUT/01_plugin_info.json" | python -c "import sys,json; d=json.loads(sys.stdin.read()); print(' built mtime:',d['data']['built']['mtime']); print(' installed mtime:',d['data']['installed']['mtime']); print(' needsInstall:',d['data']['needsInstall'])"

# Setup + injection happens via MCP tool, but exe doesn't expose that verb.
# Call directly through HTTP bridge via the existing `setup-game` flow.
echo ""
echo "==== 2. probe_status (expect UE4 + UFunction::Script resolved) ===="
curl -s http://127.0.0.1:8899/api/dump/probe_status > "$OUT/02_probe.json" || echo "plugin not reachable (game not running)"
python -c "
import json
try:
    d=json.load(open('$OUT/02_probe.json'))
    print(' engineVersionHint:',d.get('engineVersionHint'))
    for p in d.get('probes',[]):
        print(f'  {p[\"field\"]:50s} offset={p.get(\"offset\")} status={p[\"status\"]} hits={p[\"hits\"]}')
except Exception as e:
    print(' probe read failed:',e)
"

echo ""
echo "==== 3. small live reflection batch with methods (to see scriptBytes) ===="
curl -s 'http://127.0.0.1:8899/api/dump/reflection_batch?offset=0&limit=200&filter=RoboQuest&methods=true&enums=true' > "$OUT/03_reflection.json" || echo "reflection fetch failed"
python -c "
import json
try:
    d=json.load(open('$OUT/03_reflection.json'))
    classes=d.get('classes',[])
    bp_funcs=0
    native_funcs=0
    samples=[]
    for c in classes:
        for m in c.get('methods',[]):
            if 'scriptBytes' in m:
                bp_funcs+=1
                if len(samples)<6:
                    samples.append((c['name'],m['name'],m['scriptBytes'],m.get('scriptOps',[])[:5]))
            else:
                native_funcs+=1
    print(f' classes={len(classes)} bp_funcs={bp_funcs} native_funcs={native_funcs}')
    for s in samples:
        print(f'   {s[0]}.{s[1]} {s[2]}B ops={s[3]}')
except Exception as e:
    print(' parse failed:',e)
"

echo ""
echo "==== 4. emit UE project from live ===="
rm -rf "$OUT/MirrorLive"
# MCP exe doesn't expose dump_ue_project CLI — use curl through the plugin
# for a small filtered emit to avoid the 60s crash window.
# The plugin doesn't have a direct dump_ue_project endpoint either; the emit
# happens server-side in UhtSdkTools. We invoke it via the MCP server CLI if
# possible, or fall back to live reflection → manual emit.
# For this test: use uevr_dump_sdk_cpp which is a direct plugin endpoint
curl -s -X POST 'http://127.0.0.1:8899/api/dump/uht_sdk' \
  -H 'Content-Type: application/json' \
  -d "{\"outDir\":\"$OUT/MirrorLive\",\"filter\":\"RoboQuest\",\"includeMethods\":true,\"includeEnums\":true}" > "$OUT/04_emit.json" || echo "emit fetch failed"
python -c "
import json,os,glob
try:
    d=json.load(open('$OUT/04_emit.json'))
    print(' emit result:',json.dumps(d,indent=2)[:500])
except Exception as e:
    print(' emit parse failed:',e)
hdrs = glob.glob('$OUT/MirrorLive/**/*.h', recursive=True)
print(f' headers emitted: {len(hdrs)}')
# Find one with @kismet in it
kmhit=0
for h in hdrs[:500]:
    with open(h,encoding='utf-8',errors='ignore') as f:
        if '@kismet' in f.read():
            kmhit+=1
print(f' headers with @kismet comments: {kmhit}')
"
