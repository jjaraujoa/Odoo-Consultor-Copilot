# Contributing to Odoo Consultor Copilot

Thank you for your interest in contributing to **Odoo Consultor Copilot**! We welcome contributions from Odoo consultants, ERP architects, and .NET developers.

---

## Code of Conduct

This project is an open source community endeavor. Please interact respectfully, provide constructive feedback, and maintain professional collaboration at all times.

---

## How Can I Contribute?

- **Report Bugs**: Open a GitHub issue describing the unexpected behavior, Odoo version, hosting type (SaaS, Odoo.sh, On-Premise), and relevant log messages.
- **Suggest Features**: Submit an issue with the tag `enhancement` detailing the functional or technical consulting use case.
- **Submit Pull Requests**: Fix a bug, improve documentation, or extend MCP capabilities.

---

## Development Environment & Toolchain

### Prerequisites
- **Operating System**: Windows 10/11 or Windows Server (required for DPAPI and Windows Credential Manager).
- **.NET Framework**: 4.8 Runtime / Developer Pack or .NET SDK supporting MSBuild.
- **PowerShell**: Windows PowerShell 5.1 or PowerShell Core 7+.
- **Odoo Instance**: Any Odoo instance (v14 to v19, Community or Enterprise) for live integration testing.

### Building from Source

Using the modern SDK-style project:
```powershell
dotnet build consultant-host/OdooConsultor.csproj -c Release
```

Or using the automated build script:
```powershell
powershell -ExecutionPolicy RemoteSigned -File .\scripts\Build-OdooConsultor.ps1 -OutputName OdooConsultor.dll
```

### Running Regression Tests

Before submitting any code changes, you must ensure that all self-tests pass:
```powershell
powershell -ExecutionPolicy RemoteSigned -File .\scripts\Test-OdooConsultor.ps1
```
All **199 internal self-tests** and MCP contract validations must complete with 0 errors.

---

## Coding Standards & Inviolable Rules

1. **Closed MCP Contract Principle**:
   - The LLM must never execute arbitrary or unverified methods (`execute_kw` is blocked).
   - All tool arguments and schemas must remain strongly-typed in `McpServer.cs`.

2. **Zero Plaintext Credentials**:
   - Never write passwords, tokens, or API keys to log files, repository trees, or tool response payloads.

3. **7-Bit Anti-Mojibake Rule**:
   - Non-ASCII characters in XML views (`ir.ui.view` / Studio) must be written as 7-bit standard numeric XML entities (`&#225;` for á, `&#241;` for ñ, etc.) to prevent encoding corruption in Odoo web client.

4. **Pure Open Source Neutrality**:
   - Code, tests, and documentation must remain strictly generic, without proprietary customer branding or private institutional formats. Run the leak scanner before submitting:
     ```bash
     python scripts/Auditar-OpenSource.py
     ```

---

## Pull Request Guidelines

1. Fork the repository and create your branch from `main`:
   ```bash
   git checkout -b feature/my-new-feature
   ```
2. Commit your changes using [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat: add new capability...`
   - `fix: resolve issue in...`
   - `docs: update documentation...`
3. Run the full test suite and audit script.
4. Push to your fork and submit a Pull Request to `jjaraujoa/Odoo-Consultor-Copilot` (`main` branch).
