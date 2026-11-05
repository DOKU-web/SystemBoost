"""SystemBoost license keys (ECDSA P-256, matches app/.../Services/License.cs).

  python3 license_tool.py genkey  <private.pem>             -> prints the public key for License.PublicKeyB64
  python3 license_tool.py sign    <private.pem> <email> [--days N] [--plan premium]
                                                            -> prints a license key (no --days = no expiry)

Key format: base64url(payload JSON) "." base64url(r||s)   (IEEE P1363 signature,
what .NET ECDsa.VerifyData expects by default). Keep private.pem secret: anyone
with it can make keys. Never put it in the repository or the app.
"""
import base64, json, sys, datetime
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature


def b64url(b: bytes) -> str:
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def genkey(path):
    key = ec.generate_private_key(ec.SECP256R1())
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                            serialization.NoEncryption())
    with open(path, "wb") as f:
        f.write(pem)
    spki = key.public_key().public_bytes(serialization.Encoding.DER,
                                         serialization.PublicFormat.SubjectPublicKeyInfo)
    print(base64.b64encode(spki).decode())


def sign(path, email, days=None, plan="premium"):
    with open(path, "rb") as f:
        key = serialization.load_pem_private_key(f.read(), password=None)
    payload = {"plan": plan, "email": email,
               "issued": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
    if days:
        exp = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=days)
        payload["expires"] = exp.strftime("%Y-%m-%dT%H:%M:%SZ")
    data = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
    r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    sig = r.to_bytes(32, "big") + s.to_bytes(32, "big")
    print(b64url(data) + "." + b64url(sig))


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) >= 2 and a[0] == "genkey":
        genkey(a[1])
    elif len(a) >= 3 and a[0] == "sign":
        days = int(a[a.index("--days") + 1]) if "--days" in a else None
        plan = a[a.index("--plan") + 1] if "--plan" in a else "premium"
        sign(a[1], a[2], days, plan)
    else:
        print(__doc__)
        sys.exit(1)
