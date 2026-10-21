"""生成 shared/testcases/sync-merge.json（多设备同步合并的共用用例，desktop/docs/design.md §11.2）。

用脚本生成是为了 legacy uid 的 SHA-1 算得准；改用例时改这里再运行：python shared/testcases/build-sync-merge.py
"""
import hashlib
import json
from pathlib import Path

DAY = 86_400_000
NOW = 1_800_000_000_000


def T(n):
    """用分钟区分先后：T(1) < T(2) < … < NOW。"""
    return NOW - (100 - n) * 60_000


def legacy(name):
    return "legacy-" + hashlib.sha1(name.encode("utf-8")).hexdigest()


def rules(seps=None, exc=None, infer=True, fmt="ARTIST_TITLE", charset=None):
    return {"artist_separators": seps or [";", "/", "、", "feat.", "ft.", "featuring"],
            "artist_exceptions": exc if exc is not None else ["AC/DC"],
            "infer_from_file_name": infer, "file_name_format": fmt, "legacy_charset": charset}


def state(playlists=(), deleted=(), overrides=None, offsets=None, rules_=None, nav=None):
    s = {"playlists": [dict(uid=u, name=n, updated_at=t, items=list(i)) for u, n, t, i in playlists],
         "deleted_playlists": [dict(uid=u, deleted_at=t) for u, t in deleted],
         "tag_overrides": {r: {d: {"value": v, "updated_at": t} for d, (v, t) in dims.items()} for r, dims in (overrides or {}).items()},
         "lyrics_offsets": {r: {"offset_ms": o, "updated_at": t} for r, (o, t) in (offsets or {}).items()}}
    if rules_:
        s["rules"] = {"updated_at": rules_[1], "value": rules_[0]}
    if nav:
        s["nav"] = {"updated_at": nav[1], "items": [{"type": a, "ref": b} for a, b in nav[0]]}
    return s


def file(device, written, st, name=None):
    f = {"format": 2,
         "device": {"id": device, "name": name or device, "platform": "windows" if device.startswith("pc") else "android", "app_version": "1.0.0"},
         "written_at": written}
    f.update(st)
    return f


cases = []


def case(desc, local, remotes, expected, changes=(), replaced=(), self_id="phone", now=NOW):
    cases.append({"description": desc, "self": self_id, "now": now, "local": local, "remotes": remotes,
                  "expected": {"state": expected, "changes": sorted(changes), "replaced": sorted(replaced)}})


A = ["周杰伦/叶惠美/03. 晴天.flac", "Live/2004.flac#05"]
B = ["散曲/周深 - 大鱼.mp3"]

case("别的设备新建的播放列表：本机新建",
     state(), [file("pc", T(5), state(playlists=[("p1", "跑步", T(3), A)]))],
     state(playlists=[("p1", "跑步", T(3), A)]), changes=["playlist:p1@pc"])

case("别的设备改得更晚：覆盖本机的，本机的旧版本存进历史",
     state(playlists=[("p1", "跑步", T(2), A)]), [file("pc", T(5), state(playlists=[("p1", "跑步（新）", T(4), A + B)]))],
     state(playlists=[("p1", "跑步（新）", T(4), A + B)]), changes=["playlist:p1@pc"], replaced=["p1"])

case("本机改得更晚：旧的不覆盖新的",
     state(playlists=[("p1", "跑步", T(6), B)]), [file("pc", T(7), state(playlists=[("p1", "跑步", T(4), A)]))],
     state(playlists=[("p1", "跑步", T(6), B)]))

case("修改时间相同：保留本机的",
     state(playlists=[("p1", "本机", T(4), B)]), [file("pc", T(7), state(playlists=[("p1", "电脑", T(4), A)]))],
     state(playlists=[("p1", "本机", T(4), B)]))

case("只有时间不同、内容一样：更新时间但不算改动",
     state(playlists=[("p1", "跑步", T(2), A)]), [file("pc", T(7), state(playlists=[("p1", "跑步", T(4), A)]))],
     state(playlists=[("p1", "跑步", T(4), A)]))

case("别的设备删掉了（删除晚于本机的修改）：本机也删掉，旧版本存进历史",
     state(playlists=[("p1", "跑步", T(3), A)]), [file("pc", T(7), state(deleted=[("p1", T(5))]))],
     state(deleted=[("p1", T(5))]), changes=["playlist:p1@pc"], replaced=["deleted:p1"])

case("删除早于本机的修改：保留本机的，删除记录作废",
     state(playlists=[("p1", "跑步", T(6), A)]), [file("pc", T(7), state(deleted=[("p1", T(5))]))],
     state(playlists=[("p1", "跑步", T(6), A)]))

case("删除和修改时间相同：删除优先",
     state(playlists=[("p1", "跑步", T(5), A)]), [file("pc", T(7), state(deleted=[("p1", T(5))]))],
     state(deleted=[("p1", T(5))]), changes=["playlist:p1@pc"], replaced=["deleted:p1"])

case("本机删掉了：别的设备更旧的同一播放列表不会把它加回来",
     state(deleted=[("p1", T(5))]), [file("pc", T(7), state(playlists=[("p1", "跑步", T(3), A)]))],
     state(deleted=[("p1", T(5))]))

case("本机删掉之后别的设备又改了它：以后改的为准，播放列表回来",
     state(deleted=[("p1", T(5))]), [file("pc", T(7), state(playlists=[("p1", "跑步", T(6), A)]))],
     state(playlists=[("p1", "跑步", T(6), A)]), changes=["playlist:p1@pc"])

case("删除记录超过 180 天：清掉",
     state(deleted=[("p1", NOW - 181 * DAY), ("p2", NOW - 10 * DAY)]), [],
     state(deleted=[("p2", NOW - 10 * DAY)]))

case("对不上本机歌曲的条目（只在别的设备上有的歌）：原样保留",
     state(playlists=[("p1", "跑步", T(2), A)]),
     [file("pc", T(7), state(playlists=[("p1", "跑步", T(4), A + ["只在电脑上/某首歌.ape", "整轨/专辑.ape#07"])]))],
     state(playlists=[("p1", "跑步", T(4), A + ["只在电脑上/某首歌.ape", "整轨/专辑.ape#07"])]), changes=["playlist:p1@pc"], replaced=["p1"])

case("没有出现不等于删除：别的设备的文件里没有本机的播放列表，本机的不受影响",
     state(playlists=[("p1", "跑步", T(2), A)], overrides={"a.mp3": {"MOOD": ("SPORT", T(2))}}, offsets={"a.mp3": (-500, T(2))}),
     [file("pc", T(7), state(playlists=[("p2", "睡前", T(3), B)]))],
     state(playlists=[("p1", "跑步", T(2), A), ("p2", "睡前", T(3), B)], overrides={"a.mp3": {"MOOD": ("SPORT", T(2))}}, offsets={"a.mp3": (-500, T(2))}),
     changes=["playlist:p2@pc"])

case("手动修正：别的设备改回自动（value 为 null）且更晚，本机也改回自动",
     state(overrides={"a.mp3": {"MOOD": ("SPORT", T(2)), "VOICE": ("VOCAL", T(2))}}),
     [file("pc", T(7), state(overrides={"a.mp3": {"MOOD": (None, T(4))}}))],
     state(overrides={"a.mp3": {"MOOD": (None, T(4)), "VOICE": ("VOCAL", T(2))}}), changes=["tag_override:a.mp3|MOOD@pc"])

case("手动修正：别的设备更早的改回自动，不影响本机更晚的修正",
     state(overrides={"a.mp3": {"GENRE": ("CLASSICAL", T(5))}}),
     [file("pc", T(7), state(overrides={"a.mp3": {"GENRE": (None, T(4))}}))],
     state(overrides={"a.mp3": {"GENRE": ("CLASSICAL", T(5))}}))

case("手动修正：本机没有的修正直接采用；别的设备的改回自动对本机没有可见变化，不算改动",
     state(), [file("pc", T(7), state(overrides={"a.mp3": {"MOOD": ("NON_SPORT", T(3))}, "b.mp3": {"VOICE": (None, T(3))}}))],
     state(overrides={"a.mp3": {"MOOD": ("NON_SPORT", T(3))}, "b.mp3": {"VOICE": (None, T(3))}}), changes=["tag_override:a.mp3|MOOD@pc"])

case("改回自动超过 180 天的记录、为 0 的歌词偏移超过 180 天：清掉",
     state(overrides={"a.mp3": {"MOOD": (None, NOW - 200 * DAY), "VOICE": ("VOCAL", NOW - 200 * DAY)}},
           offsets={"a.mp3": (0, NOW - 200 * DAY), "b.mp3": (500, NOW - 200 * DAY)}),
     [], state(overrides={"a.mp3": {"VOICE": ("VOCAL", NOW - 200 * DAY)}}, offsets={"b.mp3": (500, NOW - 200 * DAY)}))

case("歌词偏移：以后改的为准；本机没有的偏移等于 0",
     state(offsets={"a.mp3": (-500, T(2)), "b.mp3": (1000, T(6))}),
     [file("pc", T(7), state(offsets={"a.mp3": (1500, T(4)), "b.mp3": (0, T(5)), "c.mp3": (0, T(5))}))],
     state(offsets={"a.mp3": (1500, T(4)), "b.mp3": (1000, T(6)), "c.mp3": (0, T(5))}), changes=["lyrics_offset:a.mp3@pc"])

case("曲库规则：整组比较，别的设备的更晚就整组采用",
     state(rules_=(rules(), T(2))), [file("pc", T(7), state(rules_=(rules(seps=[";", "/", "&"], exc=[]), T(4))))],
     state(rules_=(rules(seps=[";", "/", "&"], exc=[]), T(4))), changes=["rules:@pc"])

case("曲库规则：本机更晚时保留本机的整组（不会只采用其中一项）",
     state(rules_=(rules(infer=False), T(5))), [file("pc", T(7), state(rules_=(rules(seps=[";"], charset="GBK"), T(4))))],
     state(rules_=(rules(infer=False), T(5))))

case("导航顺序：整组比较；本机从没改过时（updated_at 为 0）采用别的设备的",
     state(nav=([("FOLDERS", None), ("ARTISTS", None), ("PLAYLISTS", None)], 0)),
     [file("pc", T(7), state(nav=([("FOLDERS", None), ("ARTIST", "周深"), ("ARTISTS", None), ("PLAYLISTS", None)], T(4))))],
     state(nav=([("FOLDERS", None), ("ARTIST", "周深"), ("ARTISTS", None), ("PLAYLISTS", None)], T(4))), changes=["nav:@pc"])

case("同一台设备在几个音乐文件夹里都有文件：只用 written_at 最新的那份",
     state(), [file("pc", T(5), state(playlists=[("p1", "旧", T(3), A)])), file("pc", T(8), state(playlists=[("p1", "新", T(2), B)]))],
     state(playlists=[("p1", "新", T(2), B)]), changes=["playlist:p1@pc"])

case("本机自己的文件不参与合并",
     state(playlists=[("p1", "跑步", T(2), A)]), [file("phone", T(9), state(playlists=[("p1", "别的内容", T(8), B)]))],
     state(playlists=[("p1", "跑步", T(2), A)]))

case("多台设备：每一项取所有设备里最新的",
     state(playlists=[("p1", "本机", T(1), A)]),
     [file("pc", T(9), state(playlists=[("p1", "电脑", T(5), B)])),
      file("pad", T(9), state(playlists=[("p1", "平板", T(6), A + B)]), name="iPad"),
      file("pc2", T(9), state(playlists=[("p1", "另一台电脑", T(3), [])]))],
     state(playlists=[("p1", "平板", T(6), A + B)]), changes=["playlist:p1@pad"], replaced=["p1"])

case("旧版备份（legacy uid）：两端从同一份旧 backup.json 读入的播放列表 uid 相同，不会重复",
     state(playlists=[(legacy("跑步"), "跑步", 1, A)]),
     [file("pc", T(7), state(playlists=[(legacy("跑步"), "跑步", 1, A), (legacy("睡前"), "睡前", 1, B)]))],
     state(playlists=[(legacy("跑步"), "跑步", 1, A), (legacy("睡前"), "睡前", 1, B)]), changes=[f"playlist:{legacy('睡前')}@pc"])

doc = {
    "description": "多设备同步的合并（desktop/docs/design.md §11.2，需求 SY-3 ~ SY-6、B-7）。"
                   "self 是本机的设备 ID，now 是当前时间（UTC 毫秒）；local 是本机的状态（同步文件去掉 format / device / written_at 的部分），"
                   "remotes 是读到的其他设备的同步文件；expected.state 是合并后本机的状态（播放列表、删除记录按 uid 比较，不比较顺序），"
                   "expected.changes 是本机发生可见变化的项（\"类别:键@来自的设备\"，排序后比较；手动修正的键是 \"引用|维度\"，整组的键为空），"
                   "expected.replaced 是被覆盖（或带 deleted: 前缀表示被删除）的本机播放列表的 uid，它们要存进同步历史。"
                   "legacy_uids 是旧版备份里播放列表名字对应的 uid（\"legacy-\" + 名字 UTF-8 的 SHA-1 十六进制小写）。"
                   "本文件由 build-sync-merge.py 生成。",
    "legacy_uids": [{"name": n, "uid": legacy(n)} for n in ["跑步", "睡前", "Running", "周杰伦 精选"]],
    "cases": cases,
}
out = Path(__file__).with_name("sync-merge.json")
out.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"{len(cases)} cases -> {out}")
