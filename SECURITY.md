# Security Policy

## Supported Versions

Only the latest stable release receives security updates.

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

---

## Security & Architecture Principles

**Odoo Consultor Copilot** is designed from the ground up for strict enterprise security and data privacy:

1. **Zero External Middlewares / Cloud Brokers**:
   - The MCP host executes 100% locally on your machine over `stdio`.
   - All network calls originate directly from your local workstation to your designated Odoo instances (via HTTPS JSON-2 or XML-RPC).
   - No prompts, business records, or metadata are ever transmitted to third-party tracking or proxy services.

2. **Windows DPAPI Credential Protection**:
   - API keys and passwords are encrypted using the Windows Data Protection API (`DPAPI`) under the `CurrentUser` scope and stored in the Windows Credential Manager (`advapi32.dll`).
   - Secrets are never saved in plaintext configuration files, environment variables, git history, or MCP server response payloads.

3. **Production Read-Only Safeguard**:
   - By default, all operations against production environments are strictly read-only.
   - Declarative package promotions require verified pre-conditions (100% staging certification, 0 collision conflicts, mandatory backup snapshot reference, and explicit human confirmation tokens).

4. **Non-Destructive Operations**:
   - Demo and test compensation mechanisms archive or deactivate records (`active=False`); `unlink` (permanent database deletion) is architecturally prohibited to protect referential and accounting integrity.

---

## Reporting a Vulnerability

If you discover a potential security vulnerability in this project, please report it responsibly:

1. **Do not create public GitHub issues** for suspected security vulnerabilities.
2. Email your findings directly to **Jorge Araujo** at `jjaraujoa@eafit.edu.com` with:
   - A description of the issue and potential impact.
   - Step-by-step instructions or proof-of-concept to reproduce the behavior.
   - The version of Odoo Consultor Copilot, Windows OS, and Odoo server tested.
3. You will receive an acknowledgment within 48 hours. Validated vulnerabilities will be addressed promptly in a point release.
