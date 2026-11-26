"""Verifies a ReleaseOS webhook delivery (docs/WEBHOOKS.md). Python 3.8+, standard library only:  python3 docs/samples/webhooks/verify.py --selftest
In a real receiver, call verify() with the RAW request body (bytes exactly as received, not json.dumps of a parsed object) and the three headers."""
import hashlib, hmac, sys, time

TOLERANCE_SECONDS = 300


def verify(secret: str, timestamp: str, delivery_id: str, raw_body: bytes, signature: str, now: int | None = None) -> bool:
    """secret: the whsec_ secret shown once. timestamp, delivery_id, signature: the X-ReleaseOS-Timestamp, -Delivery and -Signature headers. now: your clock in Unix seconds."""
    try:
        ts = int(timestamp)
    except (TypeError, ValueError):
        return False
    now = int(time.time()) if now is None else now
    if not signature or abs(now - ts) > TOLERANCE_SECONDS:        # replay window
        return False
    mac = hmac.new(secret.encode(), f"{ts}.{delivery_id}.".encode() + raw_body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(f"v1={mac}", signature)             # constant time
# After verify(): remember delivery_id (for a day or more) and answer 200 without acting when you have seen it: delivery is at least once.


if __name__ == "__main__" and "--selftest" in sys.argv:
    secret, delivery, body, ts = "whsec_test_secret", "7d1c0a52-8d3e-4f5e-9d3b-0c1b6f6e2a11", b'{"id":"evt_1","type":"gate.failed"}', 1792504800
    sig = "v1=ac276cd9bebb669b711a37e14b70c1398c4d7b8f6a6ed3f563b8813fb4f43dff"
    ok = (verify(secret, str(ts), delivery, body, sig, ts)
          and not verify(secret, str(ts), delivery, body + b" ", sig, ts)        # body changed
          and not verify("whsec_other", str(ts), delivery, body, sig, ts)        # wrong secret
          and not verify(secret, str(ts), delivery, body, sig, ts + 301)         # outside the replay window
          and verify(secret, str(ts), delivery, body, sig, ts + 300))            # inside it
    print("ok" if ok else "FAILED")
    sys.exit(0 if ok else 1)
