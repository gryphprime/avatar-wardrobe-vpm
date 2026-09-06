"""Classification signals for Avatar Wardrobe.
Extracted move-only from wardrobe_index.py; behavior unchanged. Only stdlib.
"""
import re
from wardrobe_text import path_contains_token, title, tokenize

UV_RE = re.compile(r"(?i)^uv\s*(\d+)\s*[_:\-\s]+(.*?)\s*$")

NUMSUFFIX_RE = re.compile(r"\.\d+$")

KIND_AVATAR, KIND_OUTFIT, KIND_CANDIDATE, KIND_IGNORED = 0, 1, 2, 3

COLOR_TOKENS = frozenset(
    "beige black blue brown cyan gray grey green ivory lime navy orange pink "
    "purple red silver violet white yellow".split()
)

def uv_groups(renderer_names):
    """Group mesh-object names by UV island: ['UV1: Vest, Jacket', ...]."""
    groups, order, other = {}, [], []
    for raw in renderer_names or []:
        name = (raw or "").strip()
        if not name:
            continue
        m = UV_RE.match(name)
        if m:
            part = NUMSUFFIX_RE.sub("", m.group(2).strip())
            if not part:
                part = "Part " + m.group(1)
            key = "UV" + str(int(m.group(1)))
            if key not in groups:
                groups[key] = []
                order.append(key)
            if part not in groups[key] and len(groups[key]) < 12:
                groups[key].append(part)
        elif name not in other and len(other) < 12:
            other.append(NUMSUFFIX_RE.sub("", name))
    out = [key + ": " + ", ".join(groups[key]) for key in order]
    if other:
        out.append("Other: " + ", ".join(other))
    return out

def colorway_of(material_names):
    for mat in sorted(material_names or []):
        for tok in tokenize(mat):
            if tok in COLOR_TOKENS:
                return title([tok])
    return ""

HAIR_TOKENS = frozenset(
    "hair ahoge bangs bang twintail twin ponytail braid braided mitsuami "
    "bun hime odango forelock sidelock tied".split()
)

OUTFIT_TOKENS = frozenset(
    "skirt skirts dress dresses jacket jackets shirt shirts blouse pants jeans "
    "trousers leggings bikini bra bras panty panties underwear underwears shoes "
    "boots loafer loafers sneaker sneakers heel heels suit suits coat coats "
    "sweater knit hoodie parka uniform sailor swimsuit swimsuits leotard corset "
    "cardigan blazer garter apron kimono yukata maid bunny onepiece overalls vest "
    "gown robe polo tee sleeve sleeves bottoms tights pantyhose stocking "
    "stockings socks bodysuit outer "
    # Japanese part names (decoded from \\uXXXX escapes); each verified:
    # 0 hits in hair/outfit geometry, only in unknowns (at2am etc).
    "キャミソール 靴下 パンツ ドルフィンパンツ サンダル ジャケット スカート "
    "インナースカート アウタースカート ガントレット チョーカー ヘルメット "
    "ベルト ズボン".split()
)

GIMMICK_TOKENS = frozenset(
    "gun pistol watergun cigarette lighter bullet タバコ ライター".split()
)

TRAIL_DIGIT_RE = re.compile(r"[0-9]+$")

ESCAPE_RE = re.compile(r"\\[uU]([0-9a-fA-F]{4})")

def decode_prefab_name(name):
    # Some exporters write object names with literal \uXXXX escapes
    # (at2am's '"\\u30AD..."' parts). Decode for scoring only;
    # display text stays raw.
    if name and ("\\u" in name or "\\U" in name):
        try:
            return ESCAPE_RE.sub(
                lambda m: chr(int(m.group(1), 16)), name)
        except (ValueError, TypeError):
            return name
    return name

def cat_tokens(names):
    """Distinct digit-stripped tokens (Ahoge01 and Ahoge02 count once)."""
    out = set()
    for name in names or []:
        for tok in tokenize(decode_prefab_name(name)):
            tok = TRAIL_DIGIT_RE.sub("", tok)
            if tok:
                out.add(tok)
    return out

def bone_stems(bones):
    """Distinct bone stems: hairlong1l/hairlong2r collapse to hairlong."""
    out = set()
    for bone in bones or []:
        stem = re.sub(r"[0-9]+[lr]?$", "", bone)
        if len(stem) > 3 and stem[-1] in "lr":
            stem = stem[:-1]
        if stem:
            out.add(stem)
    return out

def category_scores(renderer_names, bone_names, asset_path,
                     family_name, display_name, material_names):
    """Weighted (hair, outfit, gimmick) evidence. Counting, not boolean:
    one hair accessory among eleven garments must not flip an outfit."""
    rn = cat_tokens(renderer_names)
    bn = bone_stems(bone_names)
    path = cat_tokens([asset_path])
    fam = cat_tokens([family_name, display_name])
    mat = cat_tokens(material_names)
    hair = (
        3 * len(rn & HAIR_TOKENS)
        + 2 * len(bn & HAIR_TOKENS)
        + 2 * len(path & HAIR_TOKENS)
        + 1.5 * len(fam & HAIR_TOKENS)
        + len(mat & HAIR_TOKENS)
    )
    outfit = (
        3 * len(rn & OUTFIT_TOKENS)
        + 2 * len(bn & OUTFIT_TOKENS)
        + 2 * len(path & OUTFIT_TOKENS)
        + 1.5 * len(fam & OUTFIT_TOKENS)
        + len(mat & OUTFIT_TOKENS)
    )
    gimmick = (
        3 * len(rn & GIMMICK_TOKENS)
        + 2 * len(bn & GIMMICK_TOKENS)
        + 2 * len(path & GIMMICK_TOKENS)
        + 1.5 * len(fam & GIMMICK_TOKENS)
        + len(mat & GIMMICK_TOKENS)
    )
    return hair, outfit, gimmick

def classify_category(hair, outfit, gimmick=0.0):
    """(label, confidence): gimmick wins outright past half the evidence;
    otherwise hair/outfit needs a 60% share, else unknown."""
    total = hair + outfit + gimmick
    if total <= 0:
        return "unknown", 0.0
    if gimmick > hair and gimmick > outfit and gimmick / total >= 0.5:
        return "gimmick", round(gimmick / total, 2)
    if hair > outfit and hair / total >= 0.6:
        return "hair", round(hair / total, 2)
    if outfit > hair and outfit / total >= 0.6:
        return "outfit", round(outfit / total, 2)
    return "unknown", round(max(hair, outfit, gimmick) / total, 2)

def classify(flags, bone_count, asset_path, avatar_tokens):
    if flags["avatar"]:
        return KIND_AVATAR, 1.0
    evidence = 0
    # mod_mats/mod_bones mirror base content invisible to text (model instances):
    # overriding renderer materials/bones proves base renderers exist.
    if flags["skinned"] > 0 or flags["mod_bones"]:
        evidence += 2
    if flags["renderers"] > 0 or flags["mod_mats"] or flags["mod_bones"]:
        evidence += 1
    if bone_count >= 8:
        evidence += 1
    if flags["humanoid"]:
        evidence += 1
    if flags["merge"] or flags["outfit_root"]:
        evidence += 4
    elif flags["toggle"] or flags["menu"]:
        evidence += 1
    if any(path_contains_token(asset_path, t) for t in avatar_tokens):
        evidence += 3
    # Model instances with local modifications and no visible geometry are
    # outfits in practice (avatar-rooted instances hit the descriptor rule).
    if flags["instance_mods"]:
        evidence += 1
    kind = KIND_OUTFIT if evidence >= 5 else KIND_CANDIDATE
    return kind, max(0.0, min(1.0, evidence / 8.0))
