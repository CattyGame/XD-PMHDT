import threading
import time
import unittest
from unittest.mock import patch

from app.inference_admission import FairInferenceSlot


class InferenceAdmissionTests(unittest.TestCase):
    def wait_for_queue(self, slot, count):
        deadline=time.monotonic()+2
        while time.monotonic()<deadline:
            with slot._lock:
                if len(slot._waiters)==count:return
            time.sleep(.001)
        self.fail('Admission waiter did not reach the queue')

    def test_fifo_handoff_prevents_a_new_job_from_barging(self):
        slot=FairInferenceSlot();self.assertTrue(slot.acquire(False))
        acquired=[threading.Event(),threading.Event()];release=[threading.Event(),threading.Event()];order=[]
        def worker(index):
            if slot.acquire(timeout=2,max_waiters=2):
                order.append(index);acquired[index].set();release[index].wait(2);slot.release()
        threads=[]
        try:
            for index in range(2):
                thread=threading.Thread(target=worker,args=(index,));thread.start();threads.append(thread)
                self.wait_for_queue(slot,index+1)
            slot.release()
            self.assertFalse(slot.acquire(False))
            self.assertTrue(acquired[0].wait(2));self.assertFalse(acquired[1].is_set())
            release[0].set();self.assertTrue(acquired[1].wait(2));self.assertEqual([0,1],order)
        finally:
            for event in release:event.set()
            for thread in threads:thread.join(3)
        self.assertTrue(slot.acquire(False));slot.release()

    def test_full_queue_is_rejected_and_timed_out_waiter_does_not_leak_the_slot(self):
        slot=FairInferenceSlot();self.assertTrue(slot.acquire(False));outcome=[]
        thread=threading.Thread(target=lambda:outcome.append(slot.acquire(timeout=1,max_waiters=1)))
        thread.start();self.wait_for_queue(slot,1)
        self.assertFalse(slot.acquire(timeout=0,max_waiters=1));thread.join(2)
        self.assertEqual([False],outcome);self.wait_for_queue(slot,0)
        slot.release();self.assertTrue(slot.acquire(False));slot.release()
        with self.assertRaises(ValueError):slot.release()

    def test_interrupted_waiter_cannot_leave_a_queued_or_handed_off_permit(self):
        for handed_off in [False,True]:
            slot=FairInferenceSlot();slot.acquire(False);event=threading.Event()
            def interrupt(_timeout):
                if handed_off:slot.release()
                raise KeyboardInterrupt()
            with patch('app.inference_admission.Event',return_value=event),patch.object(event,'wait',side_effect=interrupt):
                with self.assertRaises(KeyboardInterrupt):slot.acquire(timeout=1,max_waiters=1)
            if not handed_off:slot.release()
            self.assertTrue(slot.acquire(False));slot.release()


if __name__=='__main__':unittest.main()
