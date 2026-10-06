import sys
import os

# Ensure src/ai-core is on sys.path for test discovery
AI_CORE_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if AI_CORE_DIR not in sys.path:
    sys.path.insert(0, AI_CORE_DIR)
