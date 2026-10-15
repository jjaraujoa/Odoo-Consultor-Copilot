#!/usr/bin/env python3
"""
Auditor de Pureza Open Source para Odoo-Consultor-Copilot.
Verifica que el repositorio no contenga términos corporativos, marcas de cliente
ni archivos confidenciales antes de realizar publicaciones o promociones.
"""

import os
import re
import sys

EXCLUDE_DIRS = {'.git', 'bin', 'obj', '.system_generated', '__pycache__'}
TARGET_EXTENSIONS = ('.cs', '.md', '.json', '.ps1', '.py', '.txt', '.xml', '.yml', '.yaml')

# Patrones prohibidos
SUSPICIOUS_PATTERNS = [
    re.compile(r'\bpragmatic\b', re.IGNORECASE),
    re.compile(r'\bprg\b', re.IGNORECASE),
    re.compile(r'\bvivaldi\b', re.IGNORECASE),
    re.compile(r'\brodatech\b', re.IGNORECASE),
    re.compile(r'\b4singenieria\b', re.IGNORECASE),
    re.compile(r'PRG-INT-FOR', re.IGNORECASE),
    re.compile(r'Cliente-PRG', re.IGNORECASE),
    re.compile(r'1773', re.IGNORECASE),
]

# Archivos permitidos para mencionar reglas (como este mismo script)
WHITELISTED_FILES = {
    'Auditar-OpenSource.py',
    'pre-commit',
    'pre-push',
}

def audit_repository(repo_root='.'):
    leaks = []
    total_files = 0

    for root, dirs, files in os.walk(repo_root):
        dirs[:] = [d for d in dirs if d not in EXCLUDE_DIRS]
        for f in files:
            if f in WHITELISTED_FILES:
                continue

            filepath = os.path.join(root, f)
            total_files += 1

            # 1. Comprobar nombre de archivo
            for p in SUSPICIOUS_PATTERNS:
                if p.search(f):
                    leaks.append(('NOMBRE_ARCHIVO', filepath, f, p.pattern))

            # 2. Comprobar contenido en archivos de texto
            if f.endswith(TARGET_EXTENSIONS):
                try:
                    with open(filepath, 'r', encoding='utf-8', errors='ignore') as fp:
                        for line_num, line in enumerate(fp, 1):
                            for p in SUSPICIOUS_PATTERNS:
                                if p.search(line):
                                    snippet = line.strip()[:100]
                                    leaks.append(('CONTENIDO', filepath, f"L{line_num}: {snippet}", p.pattern))
                except Exception as e:
                    print(f"Advertencia: no se pudo leer {filepath}: {e}")

    return total_files, leaks

def main():
    if hasattr(sys.stdout, 'reconfigure'):
        try:
            sys.stdout.reconfigure(encoding='utf-8', errors='replace')
            sys.stderr.reconfigure(encoding='utf-8', errors='replace')
        except Exception:
            pass

    print("Iniciando auditoria de pureza Open Source en Odoo-Consultor-Copilot...")
    total_files, leaks = audit_repository('.')

    print(f"Archivos analizados: {total_files}")

    if leaks:
        print(f"\n[ERROR] SE ENCONTRARON {len(leaks)} POSIBLES FUGAS O TERMINOS PROHIBIDOS:")
        for item_type, path, detail, pattern in leaks:
            print(f"  [{item_type}] {path} (Patron: {pattern}) -> {detail}")
        sys.exit(1)
    else:
        print("[OK] EXITO: Repositorio 100% limpio. Cero terminos corporativos detectados.")
        sys.exit(0)

if __name__ == '__main__':
    main()
