#!/usr/bin/env python3
"""Mock Avatar Wardrobe server for the browser checks in this folder.

Serves the real files from Web/ and fakes the API. Requests that Unity would answer on the
editor main thread are serialized through one lock with timings measured on a real project,
so redundant requests queue up the same way they do in the editor.

  python3 -I mock_server.py --web ../../Web [--img DIR] [--port 8791] [--latency 1.0] [--write-ms 400]

Queued writes behave like Unity's: 202 + a receipt id, applied one at a time on a worker
(--write-ms each), polled through /api/write_result. Test hooks:
  POST /mock/reload       new session (writes signed with the old one get the 409 Unity sends)
  POST /mock/busy?ms=N    writes and main-thread reads stall for N ms (Unity importing, compiling or applying)
  POST /mock/forget_look?id=X  drops a look and what it wears (test cleanup)
"""
import argparse, hashlib, json, threading, time, urllib.parse
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler
from pathlib import Path

ap = argparse.ArgumentParser()
ap.add_argument('--web', required=True)
ap.add_argument('--img', default='')
ap.add_argument('--port', type=int, default=8791)
ap.add_argument('--latency', type=float, default=1.0, help='scale for simulated main-thread time (0 disables)')
ap.add_argument('--write-ms', type=int, default=400, help='time Unity spends applying each queued write')
args = ap.parse_args()
WEB = Path(args.web).resolve()
IMG = Path(args.img) if args.img else None

# Main-thread cost per endpoint (seconds), measured against a real Unity project (454 families, 36 looks).
COST = {'/api/state': .045, '/api/families': .08, '/api/installed': .165, '/api/family': .06, '/api/prefab_presets': .03,
        '/api/menu_groups': .03, '/api/item_parts': .08, '/api/batch_state': .05, '/api/shops': .02, '/api/organization': .02}
MAIN = threading.Lock()
def main_thread(path):
    cost = COST.get(path, .015 if path.startswith('/api/') and path not in ('/api/thumb', '/api/active') else 0) * args.latency
    if cost:
        with MAIN:
            # Like Unity: while a change is being applied, main-thread reads wait for it.
            while time.time() < STATE['busyUntil']: time.sleep(.05)
            time.sleep(cost)

def gid(name): return hashlib.md5(name.encode()).hexdigest()
IMGS = sorted(p.stem for p in IMG.glob('*.webp') if not p.name.startswith('._')) if IMG and IMG.is_dir() else []
STEMS = IMGS or ['p%02d' % i for i in range(24)]
SHOPS = ['CYCR', 'Moonlit', 'Atelier Nemu', 'KAMI Works', 'Rosemary', 'EXTENSION CLOTHING · 黒装束 official store']
FAMILIES = []
for i in range(96):
    stem = STEMS[i % len(STEMS)]
    name = stem.replace('_', ' ').title() + ('' if i < len(STEMS) else ' %d' % (i // len(STEMS) + 1))
    nv = 1 + (i % 4)
    if i == 1: name, nv = 'Daily Dolphin', 24                       # many variants: stresses the variant strip
    if i == 2: name = 'Advanced Hand Gesture Visualizer For Every Base Avatar'   # long name: stresses truncation
    variants = [{'guid': gid(name + str(k)), 'img': STEMS[(i + k) % len(STEMS)], 'variant': 'Variant %02d' % (k + 1) if nv > 1 else 'Default'} for k in range(nv)]
    FAMILIES.append({'id': gid('fam' + name), 'name': name, 'shop': SHOPS[i % len(SHOPS)], 'compat': 2 if i % 9 == 8 else (1 if i % 3 else 0),
                     'variants': variants, 'category': 'hair' if i % 5 == 1 else 'outfit', 'tags': ['t1'] if i % 5 == 0 else []})
THUMBS = {v['guid']: v['img'] for f in FAMILIES for v in f['variants']}
VARIANT = {v['guid']: (f, v) for f in FAMILIES for v in f['variants']}
LOOK_NAMES = ['First', 'Frost Maid', 'Urban Dolly', 'Nascent', "Lily's Whisper", 'Kiramei', 'Bikini', 'Breakpoint', 'UrbanDrop', 'midnight', 'sg',
              'tech mesh', 'yakimochi', 'rider', 'hansori', 'useable', 'arowen', 'Combo1', 'Sleep', 'Nova', 'stella', 'dolphin', 'dive vibe',
              'black cat', 'everafter', 'Combo2', 'astraea', 'umbra', 'lounge', 'ace bunny', 'street chic', 'salt line', 'amakoi', 'qingtian',
              'marquise', 'cyborg']
LOOKS = [(gid(n)[:6], n) for n in LOOK_NAMES]
STATE = {'mode': 'multi-avatar', 'selected': LOOKS[0][0], 'installed': [], 'job': None, 'rev': 0, 'session': 's1', 'busyUntil': 0, 'nextId': 1000}

def wear(fi, k, target):
    f = FAMILIES[fi]; v = f['variants'][k]; STATE['nextId'] += 1
    STATE['installed'].append({'instanceId': STATE['nextId'], 'setupWarning': '', 'path': 'Outfits/%s/%s' % (target, f['name']),
                               'guid': v['guid'], 'family': f['name'], 'variant': v['variant'], 'familyId': f['id'], 'target': target, 'targetName': ''})
    STATE['rev'] += 1
for idx, (lid, _) in enumerate(LOOKS):
    for j in range(1 + (idx * 3) % 5):
        fi = (idx * 7 + j * 3 + 3) % len(FAMILIES); wear(fi, j % len(FAMILIES[fi]['variants']), lid)
wear(1, 5, LOOKS[1][0])
for j in range(3): wear(40 + j, 0, 'common')

# Unity's queued writes: receipt saved on acceptance, applied in order on one worker.
QUEUED = {'/api/install', '/api/preset_remove_item', '/api/workflow', '/api/item_settings', '/api/preset_save', '/api/preset_delete',
          '/api/menu_groups', '/api/item_parts', '/api/preset_show', '/api/preset_include', '/api/batch_preset_config', '/api/looks_copy'}
# Per-look upload settings that writes change (include, platforms, avatar ID), like Unity's project data.
CONF = {}; GROUPS = {}
RECEIPTS = {}; WRITES = []; WRITE_CV = threading.Condition()
def write_worker():
    while True:
        with WRITE_CV:
            while not WRITES: WRITE_CV.wait()
            wid, p, q = WRITES.pop(0)
        while time.time() < STATE['busyUntil']: time.sleep(.05)
        RECEIPTS[wid]['state'] = 'running'
        time.sleep(args.write_ms / 1000)
        with MAIN:
            try: RECEIPTS[wid].update(state='completed', result=apply_write(p, q))
            except Exception as e: RECEIPTS[wid].update(state='failed', error=str(e))
threading.Thread(target=write_worker, daemon=True).start()

def apply_write(p, q):
    target = q.get('target') or 'common'
    if p == '/api/install':
        f, v = VARIANT[q['guid']]
        if q.get('switch') == '1':
            worn = [x for x in STATE['installed'] if x['target'] == target]
            old = next((x for x in worn if str(x['instanceId']) == q.get('replaceId')), None) or next((x for x in worn if x['guid'] == q.get('replaceGuid')), None)
            if old: STATE['installed'].remove(old)
        wear(FAMILIES.index(f), f['variants'].index(v), target)
        return {'ok': 1, 'message': 'Wearing ' + f['name']}
    if p == '/api/preset_remove_item':
        worn = [x for x in STATE['installed'] if x['guid'] == q.get('guid') and x['target'] == target]
        it = next((x for x in worn if str(x['instanceId']) == q.get('instanceId')), None) or (worn[-1] if worn and q.get('latest') == '1' else None)
        if not it: return {'ok': 0, 'message': 'This prefab is not installed in the selected preset.'}
        STATE['installed'].remove(it); STATE['rev'] += 1
        return {'ok': 1, 'message': 'Removed'}
    if p == '/api/workflow':
        if 'selected' in q: STATE['selected'] = q['selected']
        if 'mode' in q: STATE['mode'] = q['mode']
        return {'ok': 1}
    if p == '/api/preset_save' and q.get('op') != 'save':
        if q.get('id'):
            for i, (a, b) in enumerate(LOOKS):
                if a == q['id']: LOOKS[i] = (a, q.get('name', b))
            return {'ok': 1}
        nid = gid(q.get('name', '') + str(time.time()))[:6]; LOOKS.append((nid, q.get('name', 'Look')))
        if q.get('select') != '0': STATE['selected'] = nid  # like Unity in multi-avatar mode
        return {'ok': 1, 'id': nid}
    if p == '/api/preset_delete':
        LOOKS[:] = [x for x in LOOKS if x[0] != q.get('id')]; return {'ok': 1, 'message': 'Deleted'}
    if p == '/api/looks_copy':
        r = copy_review(q); items = sum(len(l['items']) for l in r['looks']); missing = sum(1 for l in r['looks'] for i in l['items'] if i['status'] == 'missing')
        return {'ok': 1, 'target': r['target'], 'targetId': 456, 'looks': len(r['looks']), 'items': items - missing, 'missing': missing,
                'variants': sum(1 for l in r['looks'] for i in l['items'] if i['status'] == 'variant'), 'settingsSkipped': 0}
    if p == '/api/preset_include':
        for i in (q['ids'].split('\n') if q.get('ids') else [q.get('id', '')]): CONF.setdefault(i, {})['include'] = 1 if q.get('include') == '1' else 0
        return {'ok': 1}
    if p == '/api/batch_preset_config':
        c = CONF.setdefault(q.get('id', ''), {})
        for k in ('win', 'and', 'ios'):
            if k in q: c[k] = 1 if q[k] == '1' else 0
        if 'blueprint' in q: c['blueprintId'] = q['blueprint']
        return {'ok': 1}
    if p == '/api/menu_groups' and q.get('op'):
        gs = GROUPS.setdefault(q.get('id') or 'common', [{'id': 'g1', 'name': 'Outfits', 'paths': [x['path'] for x in installed_in(q.get('id') or 'common')]}, {'id': 'g2', 'name': 'Accessories', 'paths': []}])
        op, g = q.get('op'), next((x for x in gs if x['id'] == q.get('group')), None)
        if op == 'save':
            if not g: g = {'id': 'g%d' % int(time.time() * 1000), 'name': '', 'paths': []}; gs.append(g)
            g['name'] = q.get('name', '')
        elif op == 'delete' and g: gs.remove(g)
        elif op == 'assign':
            for x in gs: x['paths'] = [y for y in x['paths'] if y != q.get('item')]
            if g: g['paths'].append(q.get('item'))
        return {'ok': 1, 'id': g['id'] if g else '', 'groups': gs}
    return {'ok': 1, 'message': 'ok'}

# Look copy review: every third item needs another variant, every seventh has none for the target.
def copy_review(q):
    names = dict(LOOKS); looks = []
    for lid in [x for x in q.get('ids', '').split('\n') if x in names]:
        items = []
        for n, x in enumerate(installed_in(lid)):
            st = 'missing' if n % 7 == 6 else 'variant' if n % 3 == 2 else 'same'
            items.append({'name': x['family'], 'variant': 'Summer' if st == 'variant' else x['variant'], 'status': st, 'guid': x['guid']})
        looks.append({'id': lid, 'name': names[lid], 'exists': 1 if names[lid] in ('First',) else 0, 'items': items})
    return {'ok': 1, 'message': '', 'target': 'Mayo Summer', 'looks': looks}

def installed_in(target): return [x for x in STATE['installed'] if (x['target'] or 'common') == target]
def fam_row(f, target):
    worn = {x['guid'] for x in installed_in(target)}
    return {'id': f['id'], 'name': f['name'], 'variants': len(f['variants']), 'thumb': f['variants'][0]['guid'], 'shop': f['shop'], 'product': f['name'],
            'compat': f['compat'], 'compatText': ['✓ Compatible', 'Likely compatible', 'Not compatible'][f['compat']], 'compatibleOverride': False,
            'installed': 1 if any(v['guid'] in worn for v in f['variants']) else 0, 'variantGuids': [v['guid'] for v in f['variants']],
            'category': f['category'], 'tags': f['tags']}

def placeholder(guid):
    hue = int(guid[:2], 16) * 360 // 256
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256"><rect width="256" height="256" fill="#000"/>'
            '<rect x="98" y="40" width="60" height="60" rx="30" fill="hsl(%d 30%% 60%%)"/><path d="M70 230 98 110h60l28 120z" fill="hsl(%d 25%% 45%%)"/></svg>' % (hue, hue)).encode()

# Per-Look thumbnail plans (Upload/OutfitThumbnails.cs). The first look uses a cropped
# image, the second a scene camera kept on re-upload; the rest follow the default.
THUMB = {'default': {'source': 'auto', 'update': 'keep', 'camera': '', 'original': '', 'cropX': .5, 'cropY': .5, 'cropZoom': 1}}
CAMERAS = [{'id': 'cam1', 'reference': 'iid:1', 'name': 'Thumbnail Camera', 'path': 'MAYO_Kaihen (2)/Thumbnail Camera', 'underAvatar': 1},
           {'id': '', 'reference': 'iid:2', 'name': 'Main Camera', 'path': 'Main Camera', 'underAvatar': 0}]
def thumb_plan(look_id):
    own = THUMB.get(look_id)
    if look_id != 'default' and (not own or not own.get('source')): return dict(THUMB['default'], inherited=1)
    return dict(own, inherited=0)
def wide_image(w, h, label):
    return ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d"><defs><linearGradient id="g" x1="0" x2="1">'
            '<stop offset="0" stop-color="#3b2a5c"/><stop offset="1" stop-color="#c46a8a"/></linearGradient></defs><rect width="%d" height="%d" fill="url(#g)"/>'
            '<circle cx="%d" cy="%d" r="%d" fill="#f2d43d"/><text x="40" y="%d" font-size="64" fill="#fff" font-family="sans-serif">%s</text></svg>'
            % (w, h, w, h, w, h, w // 2, h // 2, h // 5, h - 50, label)).encode()

class H(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def send(self, code, body, mime='application/json'):
        if not isinstance(body, (bytes, bytearray)): body = json.dumps(body).encode(); mime = 'application/json'
        self.send_response(code); self.send_header('Content-Type', mime); self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store'); self.end_headers(); self.wfile.write(body)
    def do_POST(self):
        n = int(self.headers.get('Content-Length') or 0)
        if n: self.rfile.read(n)
        u = urllib.parse.urlsplit(self.path); q = {k: v[-1] for k, v in urllib.parse.parse_qs(u.query, keep_blank_values=True).items()}; p = u.path
        if p == '/mock/reload': STATE['session'] = 's%d' % (int(STATE['session'][1:]) + 1); return self.send(200, {'ok': 1, 'session': STATE['session']})
        if p == '/mock/forget_look':
            LOOKS[:] = [x for x in LOOKS if x[0] != q.get('id')]
            STATE['installed'] = [x for x in STATE['installed'] if x['target'] != q.get('id')]; STATE['rev'] += 1
            return self.send(200, {'ok': 1})
        if p == '/mock/busy': STATE['busyUntil'] = time.time() + int(q.get('ms', '3000')) / 1000; return self.send(200, {'ok': 1})
        session = self.headers.get('X-Wardrobe-Session')
        if p.startswith('/api/') and session and session != STATE['session'] and p != '/api/active':
            return self.send(409, {'code': 'session-changed', 'message': 'The Unity session changed. Refresh the wardrobe before editing.', 'session': STATE['session'], 'avatarInstanceId': 123})
        wid = self.headers.get('X-Wardrobe-Write-Id')
        if p in QUEUED and wid and self.headers.get('X-Wardrobe-Queue') == '1' and not (p == '/api/menu_groups' and not q.get('op')):
            with WRITE_CV:
                if wid not in RECEIPTS:
                    RECEIPTS[wid] = {'state': 'queued', 'result': None, 'error': ''}; WRITES.append((wid, p, q)); WRITE_CV.notify()
            return self.send(202, {'writeJob': wid, 'state': 'queued'})
        self.do_GET()
    def do_GET(self):
        u = urllib.parse.urlsplit(self.path); q = {k: v[-1] for k, v in urllib.parse.parse_qs(u.query, keep_blank_values=True).items()}; p = u.path
        static = {'/': 'wardrobe.html', '/v2': 'wardrobe-v2.html', '/v2/': 'wardrobe-v2.html', '/lang.json': 'lang.json'}
        if p in static or (p.endswith(('.js', '.css')) and '..' not in p and (WEB / p[1:]).is_file()):
            f = WEB / (static.get(p) or p[1:])
            return self.send(200, f.read_bytes(), {'.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css', '.json': 'application/json'}[f.suffix])
        if p == '/api/write_result':
            r = RECEIPTS.get(q.get('id', ''))
            if not r: return self.send(409, b'Write receipt unavailable after a restart or expiry.', 'text/plain')
            return self.send(200, {'state': r['state'], 'result': r['result'], 'error': r['error']})
        main_thread(p)
        target = q.get('target') or 'common'
        if p == '/api/state':
            return self.send(200, {'ok': 1, 'wardrobeMode': STATE['mode'], 'selectedPreset': STATE['selected'], 'workflowPresets': [{'id': a, 'name': b} for a, b in LOOKS],
                'sdkLoggedIn': True, 'sdkUploadReady': True, 'wardrobeVersion': '1.0.29', 'avatarInstanceId': 123, 'projectPath': '/x/Mayo', 'scenePath': 'Assets/mayo.unity',
                'sceneTargets': [{'id': 123, 'name': 'MAYO_Kaihen (2)', 'scene': 'Assets/mayo.unity'}, {'id': 456, 'name': 'Mayo Summer', 'scene': 'Assets/summer.unity'}],
                'avatarName': 'MAYO_Kaihen (2)', 'avatarLabel': 'MAYO_Kaihen (2)', 'avatarGuid': 'g1', 'session': STATE['session'], 'epoch': 'e1', 'previewEpoch': 1,
                'installedKey': 'i%d' % STATE['rev'], 'outfits': 7059, 'families': len(FAMILIES), 'indexing': 0, 'dirty': 0, 'hiBaked': 6936, 'hiTotal': 7059,
                'baseAvatars': [{'guid': 'b1', 'name': 'Mayo', 'path': 'Assets/Mayo.prefab'}], 'avatarBaseEditable': True, 'ai': 0})
        if p == '/api/looks_copy_review': return self.send(200, copy_review(q))
        if p == '/api/installed': return self.send(200, {'items': STATE['installed'], 'projectId': 'p1', 'avatarId': 'a1', 'avatarName': 'Mayo'})
        if p == '/api/families':
            rows = FAMILIES
            if q.get('filter') == 'compatible': rows = [f for f in rows if f['compat'] < 2]
            if q.get('filter') == 'installed':
                worn = {x['guid'] for x in installed_in(target)}; rows = [f for f in rows if any(v['guid'] in worn for v in f['variants'])]
            if q.get('category'): rows = [f for f in rows if f['category'] == q['category']]
            if q.get('search'): rows = [f for f in rows if q['search'].lower() in f['name'].lower()]
            page, size = int(q.get('page', 0)), int(q.get('pageSize', 60)); total = len(rows)
            return self.send(200, {'total': total, 'page': page, 'pageSize': size, 'pageCount': max(1, -(-total // size)), 'items': [fam_row(f, target) for f in rows[page * size:(page + 1) * size]]})
        if p == '/api/family':
            f = next((x for x in FAMILIES if x['id'] == q.get('id')), None)
            if not f: return self.send(404, {'ok': 0, 'message': 'Not found'})
            worn = {x['guid'] for x in installed_in(target)}
            return self.send(200, {'id': f['id'], 'name': f['name'], 'shop': f['shop'], 'tags': f['tags'], 'hidden': 0, 'folder': 'Assets/' + f['shop'],
                'variants': [{'guid': v['guid'], 'variant': v['variant'], 'colorway': '', 'shop': f['shop'], 'product': f['name'], 'source': 'Assets/%s/%s.prefab' % (f['shop'], f['name']),
                              'compat': f['compat'], 'compatText': ['✓ Compatible', 'Likely compatible', 'Not compatible'][f['compat']], 'installed': 1 if v['guid'] in worn else 0,
                              'setup': 1, 'confidence': 'high', 'mats': ['Body', 'Cloth'], 'parts': ['Jacket', 'Skirt'], 'assetVersion': 'v1'} for v in f['variants']]})
        if p == '/api/prefab_presets':
            out = {}
            for x in STATE['installed']:
                if x['guid'] != q.get('guid'): continue
                t = x['target'] or 'common'; e = out.setdefault(t, {'id': t, 'name': t, 'paths': [], 'instanceIds': [], 'partToggles': 1, 'partTogglesMixed': 0})
                e['paths'].append(x['path']); e['instanceIds'].append(x['instanceId'])
            return self.send(200, {'presets': list(out.values())})
        if p == '/api/menu_groups': return self.send(200, {'ok': 1, 'groups': GROUPS.get(q.get('id') or 'common') or [{'id': 'g1', 'name': 'Outfits', 'paths': [x['path'] for x in installed_in(q.get('id') or 'common')]}, {'id': 'g2', 'name': 'Accessories', 'paths': []}], 'id': 'g3'})
        if p == '/api/item_parts':
            if q.get('op') == 'save': return self.send(200, {'ok': 1, 'message': 'Applied to the avatar.'})
            return self.send(200, {'ok': 1, 'parts': [{'key': '', 'name': 'Base', 'worn': 1, 'shapes': [{'mesh': 'Body', 'shape': 'Shrink_Chest', 'value': 100, 'delete': 0}]},
                {'key': 'dress', 'name': 'Dress', 'worn': 1, 'toggle': 1, 'shapes': [{'mesh': 'Body', 'shape': 'Hide_Belly', 'value': 0, 'delete': 1}]},
                {'key': 'shoes', 'name': 'Shoes', 'worn': 1, 'toggle': 1, 'shapes': []}, {'key': 'gloves', 'name': 'Gloves', 'worn': 0, 'toggle': 1, 'shapes': []}],
                'sliders': [], 'meshes': [{'path': 'Body', 'name': 'Body', 'shapes': ['Shrink_Chest', 'Hide_Belly', 'Foot_Heel']}], 'itemMeshes': [], 'sync': 1, 'syncCandidates': 12, 'copies': 1})
        if p == '/api/workflow': return self.send(200, apply_write(p, q))
        if p == '/api/shops': return self.send(200, {'items': [{'name': s, 'count': 12} for s in SHOPS]})
        if p == '/api/organization': return self.send(200, {'ok': 1, 'tags': [{'id': 't1', 'name': 'Gothic', 'color': '#a487de', 'count': 4}], 'hiddenPaths': [], 'hiddenCount': 0})
        if p == '/api/thumb':
            g = q.get('guid', ''); img = THUMBS.get(g)
            if img and IMGS: return self.send(200, (IMG / (img + '.webp')).read_bytes(), 'image/webp')
            return self.send(200, placeholder(g), 'image/svg+xml') if img else self.send(404, {'ok': 0})
        if p in ('/api/install', '/api/preset_remove_item', '/api/preset_save', '/api/preset_delete') and self.command == 'POST':
            return self.send(200, apply_write(p, q))
        if p == '/api/look_thumb':
            plan = thumb_plan(q.get('id', ''))
            return self.send(200, dict(plan, ok=1, id=q.get('id', ''), name=dict(LOOKS).get(q.get('id'), ''), isNew=0,
                defSource=THUMB['default']['source'], defUpdate=THUMB['default']['update'], cameras=CAMERAS))
        if p == '/api/look_thumb_set':
            for look_id in (q.get('id') or '').split('\n'):
                if not look_id: continue
                cur = THUMB.setdefault(look_id, dict(THUMB['default'], source=''))
                if not cur.get('source') and look_id != 'default': cur.update({k: v for k, v in THUMB['default'].items()})
                if 'source' in q: cur['source'] = q['source']
                if 'camera' in q: cur['camera'] = next((c['id'] or 'cam2' for c in CAMERAS if c['reference'] == q['camera']), q['camera']); cur['source'] = q.get('source', 'camera')
                for k in ('update',): 
                    if k in q: cur[k] = q[k]
                for k in ('cropX', 'cropY', 'cropZoom'):
                    if k in q: cur[k] = float(q[k])
            return self.send(200, {'ok': 1})
        if p == '/api/look_thumb_original': return self.send(200, wide_image(1600, 900, 'VRChat photo'), 'image/svg+xml')
        if p == '/api/look_thumb_upload':
            cur = THUMB.setdefault(q.get('id', ''), {}); cur.update(source='image', original='up%d' % int(time.time()), cropX=.5, cropY=.5, cropZoom=1)
            cur.setdefault('update', 'keep'); cur.setdefault('camera', '')
            return self.send(200, {'ok': 1})
        if p == '/api/look_thumb_preview': return self.send(200, {'ok': 1, 'token': 't' + q.get('id', ''), 'used': thumb_plan(q.get('id', ''))['source'], 'message': ''})
        if p == '/api/batch_thumb_img': return self.send(200, wide_image(1200, 900, 'Render'), 'image/svg+xml')
        if p == '/api/look_thumb_camera':
            CAMERAS.insert(0, {'id': 'cam%d' % (len(CAMERAS) + 1), 'reference': 'iid:%d' % (len(CAMERAS) + 1), 'name': 'Thumbnail Camera', 'path': 'MAYO_Kaihen (2)/Thumbnail Camera %d' % len(CAMERAS), 'underAvatar': 1})
            cur = THUMB.setdefault(q.get('id', ''), dict(THUMB['default'])); cur.update(source='camera', camera=CAMERAS[0]['id'])
            return self.send(200, {'ok': 1, 'message': CAMERAS[0]['id']})
        if p == '/api/batch_state':
            def row(i, a, b):
                plan = thumb_plan(a)
                return {**{'id': a, 'name': b, 'blueprintId': '' if i % 3 == 0 else 'avtr_12345678-1234-1234-1234-1234567890ab', 'win': 1, 'and': 1 if i % 2 else 0, 'ios': 0, 'include': 1, 'members': [], 'lastUpload': '' if i % 3 == 0 else '2026-11-2%d 18:%02d' % (i % 4, i * 7 % 60)}, **CONF.get(a, {}),
                        'thumbSource': plan['source'], 'thumbUpdate': plan['update'], 'thumbInherited': plan['inherited'], 'thumbOriginal': plan['original'] if plan['source'] == 'image' else '',
                        'thumbCropX': plan['cropX'], 'thumbCropY': plan['cropY'], 'thumbCropZoom': plan['cropZoom']}
            return self.send(200, {'ok': 1, 'presets': [row(i, a, b) for i, (a, b) in enumerate(LOOKS)],
                'failed': [], 'defaults': {'release': 'private'}, 'batchActive': 1 if STATE['job'] and not STATE['job']['done'] else 0})
        if p == '/api/batch_upload_presets':
            STATE['job'] = {'ids': q['ids'].split('\n'), 'start': time.time(), 'done': 0}
            return self.send(200, {'ok': 1, 'job': 'j1'})
        if p == '/api/batch_job':
            j = STATE['job']
            if not j: return self.send(200, {'done': 1, 'job': ''})
            t = time.time() - j['start']; idx = int(t // 6); total = len(j['ids'])
            if idx >= total:
                # A multi-look upload fails its last look once (like a VRChat storage hiccup); the retry succeeds.
                j['done'] = 1; names = dict(LOOKS)
                failed = [j['ids'][-1]] if total > 1 and not STATE.get('uploadFailedOnce') else []
                if failed: STATE['uploadFailedOnce'] = 1
                uploaded = [i for i in j['ids'] if i not in failed]
                message = 'Uploaded %d preset%s: %s.' % (len(uploaded), '' if len(uploaded) == 1 else 's', ', '.join(names.get(i, i) for i in uploaded))
                if failed: message += '\nCould not upload:\n' + '\n'.join('%s: New-avatar upload failed: Failed to upload file' % names.get(i, i) for i in failed)
                return self.send(200, {'job': 'j1', 'done': 1, 'ok': 0 if failed else 1, 'index': total, 'total': total, 'message': message,
                                       'perLook': 1, 'uploadedIds': uploaded, 'failedIds': failed})
            return self.send(200, {'job': 'j1', 'done': 0, 'index': idx, 'total': total, 'current': j['ids'][idx], 'stage': 'Uploading', 'uploadProgress': (t % 6) / 6})
        if p == '/api/batch_cancel': STATE['job'] = None; return self.send(200, {'ok': 1, 'message': 'Cancelled'})
        if p.startswith('/api/'): return self.send(200, {'ok': 1, 'message': 'ok'})
        return self.send(404, {'ok': 0})

THUMB[LOOKS[0][0]] = {'source': 'image', 'update': 'always', 'camera': '', 'original': 'seed', 'cropX': .42, 'cropY': .5, 'cropZoom': 1.3}
THUMB[LOOKS[1][0]] = {'source': 'camera', 'update': 'keep', 'camera': 'cam1', 'original': '', 'cropX': .5, 'cropY': .5, 'cropZoom': 1}
ThreadingHTTPServer.daemon_threads = True
ThreadingHTTPServer(('127.0.0.1', args.port), H).serve_forever()
