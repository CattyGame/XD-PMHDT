import sys
import os
import urllib.parse

# Ensure UTF-8 output
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import jira_client as jc

def find_m4():
    # 1. Search by summary ~ M4
    jql = 'project = SCRUM AND (summary ~ "M4" OR description ~ "M4" OR text ~ "M4")'
    endpoint = f"/rest/api/3/search/jql?jql={urllib.parse.quote(jql)}&maxResults=100"
    res = jc.request(endpoint)
    issue_refs = res.get("issues", [])
    print(f"Total M4 issues found: {len(issue_refs)}")
    
    issues = []
    for item in issue_refs:
        detail = jc.request(f"/rest/api/3/issue/{item['id']}")
        f = detail.get("fields", {})
        assignee = f.get("assignee")
        assignee_name = assignee.get("displayName") if assignee else "Unassigned"
        desc = f.get("description")
        desc_text = jc.extract_adf_text(desc) if desc else ""
        issues.append({
            "key": detail.get("key"),
            "summary": f.get("summary"),
            "status": f.get("status", {}).get("name"),
            "assignee": assignee_name,
            "description": desc_text
        })
    
    # Sort issues by numeric key
    def get_num(key):
        try:
            return int(key.split("-")[1])
        except:
            return 99999
            
    issues.sort(key=lambda x: get_num(x["key"]))
    
    print("\n--- Checking all issues in project SCRUM ---")
    res_all = jc.request("/rest/api/3/search/jql?jql=project%20%3D%20SCRUM&maxResults=500")
    all_refs = res_all.get("issues", [])
    print(f"Total issues in project: {len(all_refs)}")
    
    # Check if there are other weeks or modules
    modules = {}
    for item in all_refs:
        detail = jc.request(f"/rest/api/3/issue/{item['id']}")
        summary = detail.get("fields", {}).get("summary", "")
        key = detail.get("key")
        assignee = detail.get("fields", {}).get("assignee")
        assignee_name = assignee.get("displayName") if assignee else "Unassigned"
        
        # Extract tag like [W1][M4]
        tag = summary[:15] if summary.startswith("[") else "No tag"
        if tag not in modules:
            modules[tag] = []
        modules[tag].append((key, summary, assignee_name))
        
    for tag, items in sorted(modules.items()):
        print(f"\n{tag} ({len(items)} issues):")
        for k, s, a in items[:3]:
            print(f"  [{k}] {s} (Assignee: {a})")
        if len(items) > 3:
            print(f"  ... and {len(items)-3} more issues")

if __name__ == "__main__":
    find_m4()
