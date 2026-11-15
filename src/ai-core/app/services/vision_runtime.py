"""Initialize OpenCV's process-wide thread budget once, under Python's import lock."""
import cv2

cv2.setNumThreads(1)
