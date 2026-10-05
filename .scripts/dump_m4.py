import sys
import os

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jira_client as jc

m4_keys = ['SCRUM-59', 'SCRUM-60', 'SCRUM-61', 'SCRUM-62', 'SCRUM-63', 'SCRUM-64', 'SCRUM-290', 'SCRUM-291', 'SCRUM-292', 'SCRUM-293']

for key in m4_keys:
    detail = jc.request(f"/rest/api/3/issue/{key}")
    f = detail.get("fields", {})
    desc = jc.extract_adf_text(f.get("description")) if f.get("description") else "(Không có mô tả)"
    print(f"=== [{key}] {f.get('summary')} ===")
    print(desc.strip())
    print()
