"""Export explicit song IDs for local review via the production read-only MCP service.
No audio is read, no automatic/manual tag is silently promoted to a human label.
Keep exports outside the repository. Canonicalize recording/album/artist IDs before independent evaluation.
"""
import argparse
import hashlib
import json
from pathlib import Path
from mcp_client import McpClient


def unwrap(result):
    if result.get("isError"):
        raise RuntimeError(result)
    return json.loads(result["content"][0]["text"])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--track-ids", required=True, help="1–50 comma-separated local track IDs")
    p.add_argument("--identity-salt", required=True, help="Stable local pseudonym namespace; use same value between exports")
    p.add_argument("--output", type=Path, required=True)
    args = p.parse_args()
    ids = list(dict.fromkeys(int(x) for x in args.track_ids.split(",")))
    if not 1 <= len(ids) <= 50:
        p.error("Select 1–50 explicit track IDs")
    def identity(kind, value):
        return hashlib.sha256((args.identity_salt + kind + str(value)).encode()).hexdigest() if value else None
    records = []
    with McpClient() as client:
        for tid in ids:
            track = unwrap(client.call("get_track", {"trackId": tid}))
            review = unwrap(client.call("explain_analysis", {"trackId": tid, "includeReviews": True}))
            records.append(dict(song_id=identity("song", track["trackKey"]),
                album_id=identity("album", track.get("albumKey")),
                artist_ids=[identity("artist", a) for a in track.get("artistKeys", [])],
                labels={d: "?" for d in ("MOOD", "VOICE", "GENRE", "CALMNESS")}, automatic=review["automatic"],
                provenance=dict(labels="unreviewed", scores="desktop_production_v5_onnx" if review["complete"] else "legacy_or_incomplete", independent=False),
                review=review))
    # Exclusive creation avoids clobbering an already reviewed export.
    with args.output.open("x", encoding="utf-8") as output:
        for record in records:
            output.write(json.dumps(record, ensure_ascii=False) + "\n")
    print(f"Exported {len(records)} unlabelled review records to {args.output}")

if __name__ == "__main__": main()
