#!/usr/bin/env python3
"""Check first-party source for common publication privacy mistakes.

Run from any directory. Findings print paths and categories, never secret values.
Generated files, third-party dependencies, and Git history require separate review.
"""

import json
import os
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {'.git', 'node_modules', 'bin', 'obj', 'dist', 'wwwroot', 'logs',
            'TestResults', '__pycache__'}
SECRET_KEYS = {'clientsecret', 'clientid', 'sourceapitoken', 'targetapitoken',
               'accesstoken', 'refreshtoken', 'password', 'apikey'}
ALLOWED_EMAIL_DOMAINS = {'example.com', 'example.org', 'example.net', 'example.invalid'}


def findings(path, text):
    result = set()
    for domain in re.findall(r'[\w.+-]+@([\w.-]+\.[a-z]{2,})', text, re.I):
        if domain.lower() not in ALLOWED_EMAIL_DOMAINS:
            result.add('non-example email address')
    if re.search(r'(?:/Users/|/home/|[A-Za-z]:[/\\][Uu]sers[/\\])[A-Za-z0-9_.-]+[/\\]', text):
        result.add('personal filesystem path')
    if re.search(r'(?<![\w.-])[\w-]+\.atlassian\.net\b', text, re.I):
        result.add('tenant hostname')
    if re.search(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|'
                 r'\b(?:ATATT3x|ghp_|github_pat_|AKIA)[A-Za-z0-9_/-]{16,}|'
                 r'\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}', text):
        result.add('possible credential')
    if path.name.startswith('appsettings') and path.suffix == '.json':
        try:
            config = json.loads(text)
        except ValueError:
            result.add('invalid configuration JSON')
        else:
            def walk(value):
                if isinstance(value, dict):
                    for key, item in value.items():
                        if key.lower() in SECRET_KEYS and item:
                            result.add('nonempty credential configuration')
                        walk(item)
                elif isinstance(value, list):
                    for item in value:
                        walk(item)
            walk(config)
    return sorted(result)


def main():
    count = 0
    failures = []
    for base, directories, filenames in os.walk(ROOT):
        directories[:] = [name for name in directories if name not in EXCLUDED
                          and not (Path(base) == ROOT and name.startswith('publish'))]
        for name in filenames:
            path = Path(base) / name
            relative = path.relative_to(ROOT)
            if path.is_symlink():
                failures.append((relative, 'symlink requires review'))
                continue
            if path.suffix in {'.zip', '.log', '.trx', '.pyc', '.tsbuildinfo'}:
                continue
            raw = path.read_bytes()
            try:
                encoding = 'utf-16' if raw[:2] in (b'\xff\xfe', b'\xfe\xff') else 'utf-8-sig'
                text = raw.decode(encoding)
            except UnicodeError:
                failures.append((relative, 'binary requires review'))
                continue
            count += 1
            failures.extend((relative, category) for category in findings(path, text))
    for path, category in failures:
        print(f'{path}: {category}')
    print(f'Checked {count} first-party files; {len(failures)} finding(s). Git history and generated output excluded.')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
