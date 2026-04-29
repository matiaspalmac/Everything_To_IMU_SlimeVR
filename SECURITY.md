# Security Policy

This is a personal hobby fork. There is no paid bug bounty and no formal SLA — but security reports are taken seriously and fixes are prioritised over feature work.

## Supported versions

Only the latest release on the `main` branch receives fixes. Older releases are archival and will not be patched.

| Version | Supported |
|---------|-----------|
| Latest tagged release on `main` | ✅ |
| Anything older | ❌ |

## How to report a vulnerability

Please do **not** open a public issue or PR for a security problem. Use one of the private channels below:

1. **GitHub private vulnerability report** — preferred. From the repo's `Security` tab → `Report a vulnerability`. This routes the report through GitHub's encrypted disclosure flow.
2. **Email** — `matiaspalma2594@gmail.com` with `[SECURITY]` in the subject. Plaintext is fine; if you'd like to encrypt, mention it in the first message and a key will be sent back.

Please include:
- A short description of the issue and which release / commit it reproduces on.
- Steps to reproduce (a minimal repro is more useful than a long writeup).
- The impact you believe the issue has — what an attacker can actually achieve.
- Any patch you'd like considered (optional but appreciated).

## Disclosure timeline

- **Within 7 days** — acknowledgement of the report and a first triage.
- **Within 30 days** — a fix in `main` for confirmed issues, or a written explanation of why it cannot be addressed.
- **Public disclosure** — coordinated. The default is "fix lands, then disclose"; if a fix is not feasible, the issue will be disclosed with mitigations.

## Threat model — what is and is not in scope

This bridge runs locally and talks to:
- A SlimeVR Server, expected to be on the same machine or a trusted LAN.
- Game controllers via USB / Bluetooth / BLE.
- An optional companion app (3DS / Wii) over UDP on the local network.
- An auto-updater that pulls a signed-in-spirit (SHA-256) zip over HTTPS.

**In scope**
- Code execution, privilege escalation, or arbitrary file write triggered by:
  - A malicious controller / fake controller that returns crafted payloads.
  - A malformed `config.json`, `update.xml`, or update zip.
  - A network peer reaching the bridge over UDP (haptics / OSC / SlimeVR protocol).
- Information disclosure (e.g. logging that leaks Bluetooth MACs, paths, or other sensitive identifiers).
- DLL hijacking, supply-chain or update-channel tampering.

**Out of scope**
- Physical-access attacks (you already own the PC).
- Generic DoS where the attacker is already on the same LAN as a trusted SlimeVR Server. The threat model assumes that LAN is trusted; if you do not trust your LAN, run the bridge on a network you do trust.
- Issues only reproducible against forks or modified builds.
- Vulnerabilities in third-party dependencies that have not been backported upstream — please report those to the upstream project first.
