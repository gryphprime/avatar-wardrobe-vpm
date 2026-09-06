"""Pure text and naming helpers for Avatar Wardrobe.
Extracted move-only from wardrobe_index.py; behavior unchanged. Only stdlib.
"""
import hashlib
import os

GENERIC_TOKENS = frozenset(
    "asset assets avatar avatars base fbx model models modular outfit "
    "prefab prefabs prefeb ver version variant".split()
)

VARIANT_TOKENS = frozenset(
    "beige black blue brown cyan gray grey green ivory lime navy orange pink "
    "purple red silver violet white yellow short long lite dark light with "
    "without on off default".split()
)

WEAK_NAME_TOKENS = frozenset(
    "color colors variant variants outfit outfits model models style styles "
    "item items part parts set sets costume costumes clothes clothing wear".split()
)

def is_weak_name(name):
    words = [t for t in tokenize(name or "") if not is_int_token(t)]
    return bool(words) and all(w in WEAK_NAME_TOKENS for w in words)

def tokenize(value):
    words, buf = [], []
    for i, ch in enumerate(value or ""):
        if i > 0 and ch.isupper() and value[i - 1].islower() and buf:
            words.append("".join(buf).lower())
            buf = []
        if ch.isalnum():
            buf.append(ch)
            continue
        if not buf:
            continue
        words.append("".join(buf).lower())
        buf = []
    if buf:
        words.append("".join(buf).lower())
    return words

def normalize(value):
    return "".join(tokenize(value))

def title(words):
    return " ".join(w if len(w) <= 1 else w[:1].upper() + w[1:] for w in words)

def humanize(value):
    return title(tokenize(value))

def is_int_token(word):
    try:
        int(word)
        return True
    except (ValueError, TypeError):
        return False

def is_generic_asset_name(name):
    words = tokenize(name)
    return not words or all(
        w in GENERIC_TOKENS or w in VARIANT_TOKENS or is_int_token(w) for w in words
    )

def is_preview_utility(asset_path):
    # Preview/test rigs can carry a descriptor without being playable avatars.
    return "preview" in os.path.splitext(os.path.basename(asset_path))[0].lower()

def display_name(asset_path):
    name = os.path.splitext(os.path.basename(asset_path))[0]
    if not is_generic_asset_name(name):
        return humanize(name)
    folder = os.path.dirname(asset_path)
    while folder:
        candidate = os.path.basename(folder)
        if not is_generic_asset_name(candidate):
            return humanize(candidate)
        parent = os.path.dirname(folder)
        if parent == folder:
            break
        folder = parent
    return humanize(name)

def variant_name(asset_name):
    words = tokenize(asset_name)
    for w in words:
        if w in VARIANT_TOKENS:
            return title([w])
    for w in words:
        if is_int_token(w):
            return "Variant " + w
    return "Default"

def family_name(filename_noext, avatar_tokens):
    words = [
        t
        for t in tokenize(filename_noext)
        if t not in VARIANT_TOKENS
        and t not in avatar_tokens
        and t not in GENERIC_TOKENS
        and not is_int_token(t)
    ]
    if not words:
        return None
    return title(words)

def md5_hex(text):
    return hashlib.md5(text.encode("utf-8")).hexdigest()

def family_id(mesh_ids, skinned_count, bone_names, asset_path, avatar_tokens, base):
    # Exact geometry still wins across folders. Otherwise group by
    # (folder, base avatar, cleaned name): per-color model duplicates unite
    # (180 Shinano .. 180 Shinano 11) while different garments (1 Main vs
    # 10 Purple Shinano) and other avatars' versions stay split.
    if mesh_ids:
        structure = (
            "|".join(sorted(mesh_ids))
            + "|"
            + str(skinned_count)
            + "|"
            + "|".join(sorted(bone_names))
        )
        return "mesh:" + md5_hex(structure)
    parent = os.path.dirname(asset_path)
    cleaned = " ".join(
        t
        for t in tokenize(os.path.splitext(os.path.basename(asset_path))[0])
        if t not in VARIANT_TOKENS and t not in avatar_tokens
    )
    return "name:" + md5_hex(parent + "|" + (base or "") + "|" + cleaned)

def avatar_tokens_for(display_name, asset_path):
    toks = set()
    for source in (display_name, os.path.splitext(os.path.basename(asset_path))[0]):
        for t in tokenize(source):
            if len(t) >= 4 and t not in GENERIC_TOKENS:
                toks.add(t)
    return toks

def path_contains_token(path, token):
    return token in set(tokenize(path))
