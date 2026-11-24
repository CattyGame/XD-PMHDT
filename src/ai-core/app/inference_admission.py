"""FIFO handoff for the single research inference slot, with bounded HTTP waiters."""
from collections import deque
from threading import Event, Lock


class FairInferenceSlot:
    def __init__(self):
        self._lock=Lock()
        self._occupied=False
        self._waiters=deque()

    def acquire(self, blocking=True, timeout=None, *, max_waiters=None):
        if timeout is not None and (timeout<0 or not blocking):raise ValueError('Invalid admission timeout')
        if max_waiters is not None and max_waiters<0:raise ValueError('Invalid waiter limit')
        with self._lock:
            if not self._occupied:
                self._occupied=True
                return True
            if not blocking or (max_waiters is not None and len(self._waiters)>=max_waiters):return False
            waiter=Event()
            self._waiters.append(waiter)
        try:
            if waiter.wait(timeout):return True
        except BaseException:
            with self._lock:
                if waiter.is_set():
                    if self._waiters:self._waiters.popleft().set()
                    else:self._occupied=False
                else:self._waiters.remove(waiter)
            raise
        with self._lock:
            # A release racing with timeout has already handed ownership to this waiter.
            if waiter.is_set():return True
            self._waiters.remove(waiter)
            return False

    def release(self):
        with self._lock:
            if not self._occupied:raise ValueError('Inference slot released too many times')
            if self._waiters:
                self._waiters.popleft().set()
            else:
                self._occupied=False

    def __enter__(self):
        self.acquire()
        return self

    def __exit__(self, *_args):self.release()
