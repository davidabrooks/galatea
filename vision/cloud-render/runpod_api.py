#!/usr/bin/env python3
"""Tiny Runpod API helper for the vision test (key read from /home/box/.secrets/runpod_api_key, never printed).
usage: runpod_api.py balance | create | run <endpoint> <input.json> <out.json> | status <endpoint> <job> | scale0 <endpoint> | delete <endpoint> <template>
"""
import json, sys, time, urllib.error, urllib.request

KEY = open("/home/box/.secrets/runpod_api_key").read().strip()
GQL = "https://api.runpod.io/graphql"

def req(url, data=None, method=None):
    r = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None, method=method,
                               headers={"Content-Type": "application/json", "User-Agent": "galatea-vision-test/0.1", "Authorization": "Bearer " + KEY})
    try:
        return json.load(urllib.request.urlopen(r, timeout=120))
    except urllib.error.HTTPError as e:
        sys.exit(f"HTTP {e.code}: {e.read().decode(errors='replace')[:800]}")

def gql(q):
    d = req(GQL, {"query": q})
    if d.get("errors"): sys.exit("graphql error: " + json.dumps(d["errors"])[:500])
    return d["data"]

def balance():
    return gql("query { myself { clientBalance currentSpendPerHr } }")["myself"]

REF = "vision/runpod-test-render"
START = ("bash -c \"apt-get update -qq && apt-get install -y -qq curl ca-certificates >/dev/null && "
         f"curl -sfL https://raw.githubusercontent.com/davidabrooks/galatea/{REF}/vision/cloud-render/bootstrap.sh | bash\"")

cmd = sys.argv[1]
if cmd == "balance":
    print(json.dumps(balance()))
elif cmd == "create":
    t = gql('mutation { saveTemplate(input: {name: "gt-vision-blender-test", imageName: "nvidia/cuda:12.4.1-base-ubuntu22.04", '
            f'dockerArgs: {json.dumps(START)}, containerDiskInGb: 15, volumeInGb: 0, isServerless: true, '
            'env: [{key: "NVIDIA_DRIVER_CAPABILITIES", value: "all"}, {key: "GT_REPO_REF", value: "' + REF + '"}]}) { id } }')["saveTemplate"]
    print(json.dumps({"template": t["id"]}), "-> now: runpod_api.py endpoint", t["id"])
elif cmd == "run":
    ep, inp, out = sys.argv[2:5]
    j = req(f"https://api.runpod.ai/v2/{ep}/run", {"input": json.load(open(inp))})
    print("job", j.get("id"), j.get("status"), flush=True)
    t0 = time.time()
    while True:
        time.sleep(5)
        s = req(f"https://api.runpod.ai/v2/{ep}/status/{j['id']}")
        if s.get("status") in ("COMPLETED", "FAILED", "CANCELLED", "TIMED_OUT"):
            json.dump(s, open(out, "w")); print("done", s.get("status"), "delayTime", s.get("delayTime"), "executionTime", s.get("executionTime"),
                                                "wall", round(time.time() - t0)); break
        if time.time() - t0 > 900:
            req(f"https://api.runpod.ai/v2/{ep}/cancel/{j['id']}", {}); print("cancelled after 900 s wall"); break
        print(" ", s.get("status"), round(time.time() - t0), flush=True)
elif cmd == "templates":
    print(json.dumps(gql("query { myself { podTemplates { id name isServerless } } }")))
elif cmd == "endpoint":
    t = sys.argv[2]
    e = gql('mutation { saveEndpoint(input: {name: "gt-vision-test", templateId: "' + t + '", gpuIds: "AMPERE_16", '
            'workersMin: 0, workersMax: 1, idleTimeout: 5, scalerType: "QUEUE_DELAY", scalerValue: 4, '
            'executionTimeoutMs: 300000}) { id gpuIds workersMin workersMax idleTimeout executionTimeoutMs } }')["saveEndpoint"]
    print(json.dumps({"template": t, "endpoint": e}))
elif cmd == "health":
    print(json.dumps(req(f"https://api.runpod.ai/v2/{sys.argv[2]}/health")))
elif cmd == "scale0":
    ep = sys.argv[2]
    q = gql('query { myself { endpoints { id templateId name gpuIds idleTimeout executionTimeoutMs scalerType scalerValue } } }')
    e = [x for x in q["myself"]["endpoints"] if x["id"] == ep][0]
    r = gql('mutation { saveEndpoint(input: {id: "' + ep + '", name: "' + e["name"] + '", templateId: "' + e["templateId"] + '", gpuIds: "' + e["gpuIds"] +
            '", workersMin: 0, workersMax: 0, idleTimeout: 5, scalerType: "QUEUE_DELAY", scalerValue: 4, executionTimeoutMs: 300000}) { id workersMin workersMax } }')
    print(json.dumps(r))
