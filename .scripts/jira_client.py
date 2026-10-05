import os
import sys
import json
import base64
import urllib.request
import urllib.error

# Ensure UTF-8 output on Windows terminals
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Resolve paths
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)
ENV_PATH = os.path.join(PROJECT_ROOT, ".env")

def load_env():
    env = {}
    if os.path.exists(ENV_PATH):
        with open(ENV_PATH, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#") and "=" in line:
                    k, v = line.split("=", 1)
                    env[k.strip()] = v.strip()
    return env

CONFIG = load_env()
BASE_URL = CONFIG.get("JIRA_BASE_URL", "https://nguyentruonghau2202.atlassian.net").rstrip("/")
EMAIL = CONFIG.get("JIRA_USER_EMAIL", "quandd2937@ut.edu.vn")
TOKEN = CONFIG.get("JIRA_API_TOKEN", "")
PROJECT_KEY = CONFIG.get("JIRA_PROJECT_KEY", "SCRUM")

def get_headers():
    auth_str = f"{EMAIL}:{TOKEN}"
    encoded = base64.b64encode(auth_str.encode("utf-8")).decode("utf-8")
    return {
        "Authorization": f"Basic {encoded}",
        "Content-Type": "application/json",
        "Accept": "application/json",
        "User-Agent": "AntigravityJiraClient/1.0"
    }

import ssl

def get_ssl_context():
    try:
        import certifi
        return ssl.create_default_context(cafile=certifi.where())
    except Exception:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        return ctx

def request(endpoint, method="GET", data=None):
    url = f"{BASE_URL}{endpoint}"
    headers = get_headers()
    body = json.dumps(data).encode("utf-8") if data is not None else None
    req = urllib.request.Request(url, data=body, headers=headers, method=method)
    ctx = get_ssl_context()
    try:
        with urllib.request.urlopen(req, context=ctx) as resp:
            content = resp.read().decode("utf-8")
            if content:
                return json.loads(content)
            return None
    except urllib.error.HTTPError as e:
        err_msg = e.read().decode("utf-8")
        print(f"HTTP Error {e.code} for {url}: {err_msg}", file=sys.stderr)
        raise e

def extract_adf_text(node):
    if not isinstance(node, dict):
        return ""
    text = ""
    node_type = node.get("type", "")
    if node_type == "text":
        text += node.get("text", "")
    elif node_type == "hardBreak":
        text += "\n"
    
    for child in node.get("content", []):
        text += extract_adf_text(child)
        if node_type in ["paragraph", "heading", "bulletList", "orderedList", "listItem"]:
            if child == node.get("content", [])[-1]:
                text += "\n"
    return text

def list_issues(jql=None, max_results=50):
    if not jql:
        jql = f"project = {PROJECT_KEY} ORDER BY key ASC"
    endpoint = f"/rest/api/3/search/jql?jql={urllib.parse.quote(jql)}"
    res = request(endpoint)
    issue_refs = res.get("issues", [])
    print(f"\n--- Found {len(issue_refs)} issues (JQL: {jql}) ---")
    for item in issue_refs:
        issue_id = item["id"]
        detail = request(f"/rest/api/3/issue/{issue_id}")
        fields = detail.get("fields", {})
        key = detail.get("key")
        summary = fields.get("summary")
        status = fields.get("status", {}).get("name")
        assignee = fields.get("assignee")
        assignee_name = assignee.get("displayName") if assignee else "Unassigned"
        print(f"[{key:10}] [{status:12}] {summary} (Assignee: {assignee_name})")

def my_issues():
    jql = f"project = {PROJECT_KEY} AND assignee = currentUser() ORDER BY key ASC"
    list_issues(jql)

def get_issue(key):
    detail = request(f"/rest/api/3/issue/{key}")
    fields = detail.get("fields", {})
    print(f"\n==================== {detail.get('key')} ====================")
    print(f"Summary:     {fields.get('summary')}")
    print(f"Status:      {fields.get('status', {}).get('name')}")
    assignee = fields.get("assignee")
    print(f"Assignee:    {assignee.get('displayName') if assignee else 'Unassigned'}")
    print(f"Type:        {fields.get('issuetype', {}).get('name')}")
    print("-" * 50)
    print("Description:")
    desc = fields.get("description")
    if desc:
        print(extract_adf_text(desc).strip())
    else:
        print("(No description)")
    print("-" * 50)
    
    # Comments
    comments = fields.get("comment", {}).get("comments", [])
    print(f"Comments ({len(comments)}):")
    for c in comments:
        author = c.get("author", {}).get("displayName")
        created = c.get("created")
        body = extract_adf_text(c.get("body", {}))
        print(f"[{author} @ {created}]: {body.strip()}")
    print("===================================================\n")

def transition_issue(key, target_name):
    trans_data = request(f"/rest/api/3/issue/{key}/transitions")
    transitions = trans_data.get("transitions", [])
    target = None
    for t in transitions:
        if t.get("name", "").lower() == target_name.lower():
            target = t
            break
    if not target:
        available = ", ".join([f"'{t['name']}'" for t in transitions])
        print(f"Error: Transition '{target_name}' not available for {key}. Available: {available}", file=sys.stderr)
        return False

    body = {"transition": {"id": target["id"]}}
    request(f"/rest/api/3/issue/{key}/transitions", method="POST", data=body)
    print(f"Successfully transitioned {key} to '{target['name']}'.")
    return True

def add_comment(key, text):
    body = {
        "body": {
            "type": "doc",
            "version": 1,
            "content": [
                {
                    "type": "paragraph",
                    "content": [
                        {"type": "text", "text": text}
                    ]
                }
            ]
        }
    }
    request(f"/rest/api/3/issue/{key}/comment", method="POST", data=body)
    print(f"Successfully added comment to {key}.")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage:")
        print("  python .scripts/jira_client.py my-issues")
        print("  python .scripts/jira_client.py list [optional_jql]")
        print("  python .scripts/jira_client.py get <KEY>")
        print("  python .scripts/jira_client.py transition <KEY> <StatusName>")
        print("  python .scripts/jira_client.py comment <KEY> <Text>")
        sys.exit(1)

    cmd = sys.argv[1].lower()
    if cmd == "my-issues":
        my_issues()
    elif cmd == "list":
        jql = sys.argv[2] if len(sys.argv) > 2 else None
        list_issues(jql)
    elif cmd == "get" and len(sys.argv) > 2:
        get_issue(sys.argv[2])
    elif cmd == "transition" and len(sys.argv) > 3:
        transition_issue(sys.argv[2], sys.argv[3])
    elif cmd == "comment" and len(sys.argv) > 3:
        add_comment(sys.argv[2], " ".join(sys.argv[3:]))
    else:
        print("Invalid arguments.")
